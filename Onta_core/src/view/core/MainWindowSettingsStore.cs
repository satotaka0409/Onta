using System.Text;
using Onta.Core;
using Onta.View.Performance;

namespace Onta.View.Core;

/// <summary>
/// メインウィンドウ全体の永続化設定です。
/// </summary>
internal readonly record struct MainWindowSettings(
    SendSettingsSnapshot Send,
    ReceivePanel.ReceiveSettingsSnapshot Receive,
    PerformanceUiSettingsSnapshot Performance);

/// <summary>
/// <c>Onta_setting.bin</c> の読み書きです。
/// </summary>
internal static class MainWindowSettingsStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ONTASET1");
    private const ushort Version = 2;
    private const ushort MinSupportedVersion = 1;

    /// <summary>
    /// 設定ファイルを読み込みます。無い・破損・未対応 Version のときは false です。
    /// Version 1 は性能測定欄を既定値で補います。
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

        var version = reader.ReadUInt16();
        if (version < MinSupportedVersion || version > Version)
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

        var performance = version >= 2
            ? ReadPerformance(reader)
            : PerformanceUiSettingsSnapshot.CreateDefault();

        settings = new MainWindowSettings(
            Send: new SendSettingsSnapshot(
                ChannelMode: Enum.IsDefined(typeof(ChannelMode), sendChannel) ? sendChannel : ChannelMode.Mono,
                ActiveSubcarriers: sendSubcarriers,
                ModulationScheme: Enum.IsDefined(typeof(ModulationScheme), sendModulation) ? sendModulation : ModulationScheme.Bpsk,
                BlockInterleaveFactor: sendInterleave,
                InputFilePath: sendInputPath,
                WriteWav: sendWriteWav,
                WavOutputPath: sendWavPath,
                PlayAudio: !sendWriteWav,
                AudioDeviceNumber: sendAudioDeviceNumber,
                AudioDeviceName: string.Empty,
                AudioVolume: sendAudioVolume),
            Receive: new ReceivePanel.ReceiveSettingsSnapshot(
                UseWavInput: receiveUseWav,
                WavInputPath: receiveWavPath,
                OutputDirectory: receiveOutputDir,
                AudioDeviceNumber: receiveAudioDeviceNumber,
                AudioVolume: receiveAudioVolume),
            Performance: performance);
        return true;
    }

    /// <summary>
    /// 設定ファイルへ書き込みます（現行 Version=2）。
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
    }

    /// <summary>
    /// 性能測定設定を読み込みます。
    /// </summary>
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
            InputGain: Math.Clamp(inputGain, 0.0, 1.0));
    }

    /// <summary>
    /// 性能測定設定を書き込みます。
    /// </summary>
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
    }
}
