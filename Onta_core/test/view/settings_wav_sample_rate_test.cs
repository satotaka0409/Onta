using Onta.Core;
using Onta.Performance;
using Onta.Stream;
using Onta.View.Core;
using Onta.View.Stream;
using Xunit;

namespace Onta.Core.Tests.View;

/// <summary>
/// Onta_setting.bin への WAV サンプリング周波数の保存です。
/// </summary>
public sealed class SettingsWavSampleRateTest
{
    [Fact]
    public void Save_WritesWavSampleRate_AndLoadReadsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"onta-setting-{Guid.NewGuid():N}.bin");
        try
        {
            var settings = new MainWindowSettings(
                Send: new SendSettingsSnapshot(
                    ChannelMode: ChannelMode.Mono,
                    ActiveSubcarriers: 16,
                    ModulationScheme: ModulationScheme.Bpsk,
                    BlockInterleaveFactor: 1,
                    InputFilePath: "in.bin",
                    WriteWav: true,
                    WavOutputPath: "out.wav",
                    PlayAudio: false,
                    AudioDeviceNumber: -1,
                    AudioDeviceName: string.Empty,
                    AudioVolume: 0.8,
                    WavSampleRate: 96000),
                Receive: new ReceivePanel.ReceiveSettingsSnapshot(
                    UseWavInput: true,
                    WavInputPath: string.Empty,
                    OutputDirectory: string.Empty,
                    AudioDeviceNumber: -1,
                    AudioVolume: 0.8),
                Performance: PerformanceUiSettingsSnapshot.CreateDefault() with
                {
                    WavSampleRate = 48000,
                    ActiveSubcarriers = 32,
                    ModulationScheme = ModulationScheme.Qpsk,
                    RxActiveSubcarriers = 56,
                    RxModulationScheme = ModulationScheme.Psk8,
                    RxModulated = true
                },
                Stream: null);

            MainWindowSettingsStore.Save(path, settings);
            Assert.True(MainWindowSettingsStore.TryLoad(path, out var loaded));
            Assert.Equal(96000, loaded.Send.WavSampleRate);
            Assert.Equal(48000, loaded.Performance.WavSampleRate);
            Assert.Equal("out.wav", loaded.Send.WavOutputPath);
            Assert.Equal(32, loaded.Performance.ActiveSubcarriers);
            Assert.Equal(ModulationScheme.Qpsk, loaded.Performance.ModulationScheme);
            Assert.Equal(56, loaded.Performance.RxActiveSubcarriers);
            Assert.Equal(ModulationScheme.Psk8, loaded.Performance.RxModulationScheme);
            Assert.True(loaded.Performance.RxModulated);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_WithoutRxModulation_FallsBackToSendSelection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"onta-setting-{Guid.NewGuid():N}.bin");
        try
        {
            var settings = new MainWindowSettings(
                Send: new SendSettingsSnapshot(
                    ChannelMode: ChannelMode.Mono,
                    ActiveSubcarriers: 16,
                    ModulationScheme: ModulationScheme.Bpsk,
                    BlockInterleaveFactor: 1,
                    InputFilePath: string.Empty,
                    WriteWav: true,
                    WavOutputPath: string.Empty,
                    PlayAudio: false,
                    AudioDeviceNumber: -1,
                    AudioDeviceName: string.Empty,
                    AudioVolume: 0.8),
                Receive: new ReceivePanel.ReceiveSettingsSnapshot(
                    UseWavInput: true,
                    WavInputPath: string.Empty,
                    OutputDirectory: string.Empty,
                    AudioDeviceNumber: -1,
                    AudioVolume: 0.8),
                Performance: PerformanceUiSettingsSnapshot.CreateDefault() with
                {
                    SignalMode = PerformanceSignalMode.Modulated,
                    ActiveSubcarriers = 48,
                    ModulationScheme = ModulationScheme.Qam16
                },
                Stream: null);

            MainWindowSettingsStore.Save(path, settings);
            // 受信側 SC／変調／「変調」チェック（int + byte + bool）が無い、以前の形式の設定ファイルを再現する。
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..^(sizeof(int) + sizeof(byte) + sizeof(bool))]);

            Assert.True(MainWindowSettingsStore.TryLoad(path, out var loaded));
            Assert.Equal(48, loaded.Performance.RxActiveSubcarriers);
            Assert.Equal(ModulationScheme.Qam16, loaded.Performance.RxModulationScheme);
            Assert.True(loaded.Performance.RxModulated);
            Assert.Null(loaded.Stream);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// ストリーム設定（送信の速度・入出力・デバイス・曲情報、受信の入力デバイス）を末尾ブロックとして保存し、そのまま読み戻せること。
    /// </summary>
    [Fact]
    public void Save_WritesStreamSettings_AndLoadReadsThem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"onta-setting-{Guid.NewGuid():N}.bin");
        try
        {
            var stream = SampleStreamSettings();
            MainWindowSettingsStore.Save(path, CreateSettings(stream));
            Assert.True(MainWindowSettingsStore.TryLoad(path, out var loaded));
            Assert.Equal(stream, loaded.Stream);
            Assert.True(loaded.Performance.RxModulated);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 受信の入力デバイスが無い形式 1 のストリームブロックも読め、受信側は null（画面の既定値のまま）になること。
    /// </summary>
    [Fact]
    public void Load_StreamBlockVersion1_LeavesRxInputUnset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"onta-setting-{Guid.NewGuid():N}.bin");
        try
        {
            var stream = SampleStreamSettings();
            MainWindowSettingsStore.Save(path, CreateSettings(stream));
            // 形式 2 で足した受信の入力デバイス（int32）と音量（double）を落とし、長さと版を 1 に書き換える
            var bytes = File.ReadAllBytes(path);
            const int RxBytes = sizeof(int) + sizeof(double);
            var trimmed = bytes[..^RxBytes];
            var blockStart = FindStreamBlockStart(trimmed, RxBytes);
            BitConverter.GetBytes(BitConverter.ToInt32(trimmed, blockStart) - RxBytes).CopyTo(trimmed, blockStart);
            trimmed[blockStart + sizeof(int)] = 1;
            File.WriteAllBytes(path, trimmed);

            Assert.True(MainWindowSettingsStore.TryLoad(path, out var loaded));
            Assert.Equal(stream with { RxInputDeviceNumber = null, RxInputVolume = null }, loaded.Stream);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 末尾にあるストリームブロック（int32 長さ）の開始位置を、長さフィールドがファイル末尾と一致する位置から探します。
    /// </summary>
    /// <param name="bytes">設定ファイルのバイト列（受信フィールドを落とした後）。</param>
    /// <param name="removed">落としたバイト数（長さフィールドはまだ元の値）。</param>
    /// <returns>長さフィールドの位置。</returns>
    private static int FindStreamBlockStart(byte[] bytes, int removed)
    {
        for (var pos = bytes.Length - sizeof(int); pos >= 0; pos--)
        {
            var length = BitConverter.ToInt32(bytes, pos);
            if (length > 0 && pos + sizeof(int) + length - removed == bytes.Length && bytes[pos + sizeof(int)] == 2)
            {
                return pos;
            }
        }

        throw new InvalidOperationException("stream block not found");
    }

    /// <summary>
    /// テスト用のストリーム設定です。
    /// </summary>
    private static StreamUiSettings SampleStreamSettings() =>
        new(
            ModeId: StreamModeId.Rate28k,
            UseWavInput: false,
            WavPath: @"C:\music\曲.flac",
            InputDeviceNumber: 2,
            InputVolume: 0.65,
            OutputDeviceNumber: 1,
            OutputVolume: 0.4,
            Title: "タイトル",
            Artist: "アーティスト",
            CoverPath: @"C:\music\cover.png",
            CoverFormat: StreamCoverFormat.Gray48,
            RxInputDeviceNumber: 3,
            RxInputVolume: 0.55);

    /// <summary>
    /// ストリーム設定を含むメイン設定を作ります。
    /// </summary>
    /// <param name="stream">ストリーム設定。</param>
    private static MainWindowSettings CreateSettings(StreamUiSettings stream)
    {
        return new MainWindowSettings(
            Send: new SendSettingsSnapshot(
                ChannelMode: ChannelMode.Mono,
                ActiveSubcarriers: 16,
                ModulationScheme: ModulationScheme.Bpsk,
                BlockInterleaveFactor: 1,
                InputFilePath: string.Empty,
                WriteWav: true,
                WavOutputPath: string.Empty,
                PlayAudio: false,
                AudioDeviceNumber: -1,
                AudioDeviceName: string.Empty,
                AudioVolume: 0.8),
            Receive: new ReceivePanel.ReceiveSettingsSnapshot(
                UseWavInput: true,
                WavInputPath: string.Empty,
                OutputDirectory: string.Empty,
                AudioDeviceNumber: -1,
                AudioVolume: 0.8),
            Performance: PerformanceUiSettingsSnapshot.CreateDefault() with { RxModulated = true },
            Stream: stream);
    }
}
