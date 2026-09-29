using System.Text;
using Onta.Core;
using Onta.Performance;

namespace Onta.View.Core;

/// <summary>
/// メインウィンドウ全体の永続化設定です。
/// </summary>
internal readonly record struct MainWindowSettings(
    SendSettingsSnapshot Send,
    ReceivePanel.ReceiveSettingsSnapshot Receive,
    PerformanceUiSettingsSnapshot Performance);

/// <summary>
/// <c>Onta_setting.bin</c> の読み書きです（現行フォーマットのみ）。
/// </summary>
internal static class MainWindowSettingsStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ONTASET1");
    private const ushort Version = 3;

    /// <summary>
    /// 設定ファイルを読み込みます。無い・破損・Version 不一致のときは false です。
    /// </summary>
    /// <param name="filePath">設定ファイルパス。</param>
    /// <param name="settings">読み込んだ設定。</param>
    /// <returns>成功したとき true。</returns>
    public static bool TryLoad(string filePath, out MainWindowSettings settings)
    {
        settings = default;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var magic = reader.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !magic.SequenceEqual(Magic))
        {
            return false;
        }

        if (reader.ReadUInt16() != Version)
        {
            return false;
        }

        var sendChannel = (ChannelMode)reader.ReadByte();
        var sendSubcarriers = reader.ReadInt32();
        var sendModulation = (ModulationScheme)reader.ReadByte();
        var sendInterleave = reader.ReadInt32();
        var sendInputPath = reader.ReadString();
        var sendWriteWav = reader.ReadBoolean();
        var sendWavPath = reader.ReadString();
        var sendAudioDeviceNumber = reader.ReadInt32();
        var sendAudioVolume = reader.ReadDouble();

        var receiveUseWav = reader.ReadBoolean();
        var receiveWavPath = reader.ReadString();
        var receiveOutputDir = reader.ReadString();
        var receiveAudioDeviceNumber = reader.ReadInt32();
        var receiveAudioVolume = reader.ReadDouble();

        settings = new MainWindowSettings(
            Send: new SendSettingsSnapshot(
                ChannelMode: Enum.IsDefined(typeof(ChannelMode), sendChannel) ? sendChannel : ChannelMode.Mono,
                ActiveSubcarriers: OfdmConfig.IsSupportedActiveSubcarriers(sendSubcarriers) ? sendSubcarriers : 16,
                ModulationScheme: Enum.IsDefined(typeof(ModulationScheme), sendModulation) ? sendModulation : ModulationScheme.Bpsk,
                BlockInterleaveFactor: sendInterleave,
                InputFilePath: sendInputPath,
                WriteWav: sendWriteWav,
                WavOutputPath: sendWavPath,
                PlayAudio: !sendWriteWav,
                AudioDeviceNumber: sendAudioDeviceNumber,
                AudioDeviceName: string.Empty,
                AudioVolume: sendAudioVolume,
                WavSampleRate: 44100),
            Receive: new ReceivePanel.ReceiveSettingsSnapshot(
                UseWavInput: receiveUseWav,
                WavInputPath: receiveWavPath,
                OutputDirectory: receiveOutputDir,
                AudioDeviceNumber: receiveAudioDeviceNumber,
                AudioVolume: receiveAudioVolume),
            Performance: ReadPerformance(reader));
        var sendWavSampleRate = ReadOptionalWavSampleRate(reader);
        var performanceWavSampleRate = ReadOptionalWavSampleRate(reader);
        var (rxSubcarriers, rxModulation) = ReadOptionalPerformanceRxModulation(reader, settings.Performance);
        var rxModulated = ReadOptionalPerformanceRxModulated(reader, settings.Performance);
        settings = settings with
        {
            Send = settings.Send with
            {
                WavSampleRate = sendWavSampleRate
            },
            Performance = settings.Performance with
            {
                WavSampleRate = performanceWavSampleRate,
                RxActiveSubcarriers = rxSubcarriers,
                RxModulationScheme = rxModulation,
                RxModulated = rxModulated
            }
        };
        return true;
    }

    /// <summary>
    /// ファイル末尾にあれば性能測定の受信側「変調」チェックを読みます。無い古い設定は送信側が変調タブかどうかで決めます。
    /// </summary>
    /// <param name="reader">設定バイナリの読み取り位置。</param>
    /// <param name="performance">読み込み済みの性能測定設定（送信側の選択）。</param>
    /// <returns>変調波として受信するとき true。</returns>
    private static bool ReadOptionalPerformanceRxModulated(BinaryReader reader, PerformanceUiSettingsSnapshot performance)
    {
        if (reader.BaseStream.Position + sizeof(bool) > reader.BaseStream.Length)
        {
            return performance.SignalMode == PerformanceSignalMode.Modulated;
        }

        return reader.ReadBoolean();
    }

    /// <summary>
    /// ファイル末尾にあれば性能測定の受信側 SC／変調を読みます。無い古い設定は送信側の選択を使います。
    /// </summary>
    /// <param name="reader">設定バイナリの読み取り位置。</param>
    /// <param name="performance">読み込み済みの性能測定設定（送信側の選択）。</param>
    /// <returns>受信側のサブキャリア数と変調方式。</returns>
    private static (int Subcarriers, ModulationScheme Modulation) ReadOptionalPerformanceRxModulation(
        BinaryReader reader,
        PerformanceUiSettingsSnapshot performance)
    {
        if (reader.BaseStream.Position + sizeof(int) + sizeof(byte) > reader.BaseStream.Length)
        {
            return (performance.ActiveSubcarriers, performance.ModulationScheme);
        }

        var subcarriers = PerformanceSignalGenerator.ClampSubcarriers(reader.ReadInt32());
        var modulationByte = reader.ReadByte();
        var modulation = Enum.IsDefined(typeof(ModulationScheme), modulationByte)
            ? (ModulationScheme)modulationByte
            : performance.ModulationScheme;
        return (subcarriers, modulation);
    }

    /// <summary>
    /// ファイル末尾にあれば WAV サンプリング周波数を読みます。無い古い設定は 44100 です。
    /// </summary>
    /// <param name="reader">設定バイナリの読み取り位置。</param>
    /// <returns>44100、48000、96000 のいずれか。</returns>
    private static int ReadOptionalWavSampleRate(BinaryReader reader)
    {
        if (reader.BaseStream.Position + sizeof(int) > reader.BaseStream.Length)
        {
            return 44100;
        }

        return SendSettingsSnapshot.NormalizeWavSampleRate(reader.ReadInt32());
    }

    /// <summary>
    /// 設定ファイルへ書き込みます。
    /// </summary>
    /// <param name="filePath">設定ファイルパス。</param>
    /// <param name="settings">保存する設定。</param>
    public static void Save(string filePath, MainWindowSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(Version);

        writer.Write((byte)settings.Send.ChannelMode);
        writer.Write(settings.Send.ActiveSubcarriers);
        writer.Write((byte)settings.Send.ModulationScheme);
        writer.Write(settings.Send.BlockInterleaveFactor);
        writer.Write(settings.Send.InputFilePath ?? string.Empty);
        writer.Write(settings.Send.WriteWav);
        writer.Write(settings.Send.WavOutputPath ?? string.Empty);
        writer.Write(settings.Send.AudioDeviceNumber);
        writer.Write(settings.Send.AudioVolume);

        writer.Write(settings.Receive.UseWavInput);
        writer.Write(settings.Receive.WavInputPath ?? string.Empty);
        writer.Write(settings.Receive.OutputDirectory ?? string.Empty);
        writer.Write(settings.Receive.AudioDeviceNumber);
        writer.Write(settings.Receive.AudioVolume);

        WritePerformance(writer, settings.Performance);
        writer.Write(SendSettingsSnapshot.NormalizeWavSampleRate(settings.Send.WavSampleRate));
        writer.Write(PerformanceConstants.NormalizeWavSampleRate(settings.Performance.WavSampleRate));
        writer.Write(PerformanceSignalGenerator.ClampSubcarriers(settings.Performance.RxActiveSubcarriers));
        writer.Write((byte)settings.Performance.RxModulationScheme);
        writer.Write(settings.Performance.RxModulated);
    }

    /// <summary>
    /// 性能測定設定を読み込みます。
    /// </summary>
    /// <param name="reader">設定バイナリの読み取り位置。</param>
    /// <returns>妥当性チェック済みの性能測定 UI 設定。</returns>
    private static PerformanceUiSettingsSnapshot ReadPerformance(BinaryReader reader)
    {
        var defaults = PerformanceUiSettingsSnapshot.CreateDefault();
        var modeByte = reader.ReadByte();
        var mode = Enum.IsDefined(typeof(PerformanceSignalMode), modeByte)
            ? (PerformanceSignalMode)modeByte
            : defaults.SignalMode;
        var toneHz = reader.ReadDouble();
        var subcarriers = reader.ReadInt32();
        var modulationByte = reader.ReadByte();
        var modulation = Enum.IsDefined(typeof(ModulationScheme), modulationByte)
            ? (ModulationScheme)modulationByte
            : defaults.ModulationScheme;
        var duration = reader.ReadDouble();
        var writeWav = reader.ReadBoolean();
        var wavPath = reader.ReadString();
        var outputDevice = reader.ReadInt32();
        var signalAmplitude = reader.ReadDouble();
        var outputVolume = reader.ReadDouble();
        var rxUseWav = reader.ReadBoolean();
        var rxWavPath = reader.ReadString();
        var inputDevice = reader.ReadInt32();
        var inputGain = reader.ReadDouble();
        var fftSize = PerformanceFftAnalyzer.ClampSize(reader.ReadInt32());
        var windowByte = reader.ReadByte();
        var fftWindow = PerformanceFftAnalyzer.ClampWindow(
            Enum.IsDefined(typeof(PerformanceFftWindowKind), windowByte)
                ? (PerformanceFftWindowKind)windowByte
                : defaults.FftWindowKind);

        return new PerformanceUiSettingsSnapshot(
            SignalMode: mode,
            ToneHz: toneHz > 1.0 ? toneHz : defaults.ToneHz,
            ActiveSubcarriers: PerformanceSignalGenerator.ClampSubcarriers(subcarriers),
            ModulationScheme: modulation,
            DurationSeconds: duration is 30.0 or 60.0 or 120.0 ? duration : defaults.DurationSeconds,
            WriteWav: writeWav,
            WavPath: wavPath ?? string.Empty,
            OutputDeviceNumber: outputDevice,
            SignalAmplitude: Math.Clamp(signalAmplitude, 0.10, 1.0),
            OutputVolume: Math.Clamp(outputVolume, 0.0, 1.0),
            RxUseWavInput: rxUseWav,
            RxWavPath: rxWavPath ?? string.Empty,
            InputDeviceNumber: inputDevice,
            InputGain: Math.Clamp(inputGain, 0.0, 1.0),
            FftSize: fftSize,
            FftWindowKind: fftWindow);
    }

    /// <summary>
    /// 性能測定設定を書き込みます。
    /// </summary>
    /// <param name="writer">設定バイナリの書き込み先。</param>
    /// <param name="settings">保存する性能測定 UI 設定。</param>
    private static void WritePerformance(BinaryWriter writer, PerformanceUiSettingsSnapshot settings)
    {
        writer.Write((byte)settings.SignalMode);
        writer.Write(settings.ToneHz);
        writer.Write(settings.ActiveSubcarriers);
        writer.Write((byte)settings.ModulationScheme);
        writer.Write(settings.DurationSeconds);
        writer.Write(settings.WriteWav);
        writer.Write(settings.WavPath ?? string.Empty);
        writer.Write(settings.OutputDeviceNumber);
        writer.Write(settings.SignalAmplitude);
        writer.Write(settings.OutputVolume);
        writer.Write(settings.RxUseWavInput);
        writer.Write(settings.RxWavPath ?? string.Empty);
        writer.Write(settings.InputDeviceNumber);
        writer.Write(settings.InputGain);
        writer.Write(PerformanceFftAnalyzer.ClampSize(settings.FftSize));
        writer.Write((byte)PerformanceFftAnalyzer.ClampWindow(settings.FftWindowKind));
    }
}
