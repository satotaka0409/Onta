using Onta.Core;
using Onta.Performance;
using Onta.View.Core;
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
                });

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
                });

            MainWindowSettingsStore.Save(path, settings);
            // 受信側 SC／変調／「変調」チェック（int + byte + bool）が無い、以前の形式の設定ファイルを再現する。
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..^(sizeof(int) + sizeof(byte) + sizeof(bool))]);

            Assert.True(MainWindowSettingsStore.TryLoad(path, out var loaded));
            Assert.Equal(48, loaded.Performance.RxActiveSubcarriers);
            Assert.Equal(ModulationScheme.Qam16, loaded.Performance.RxModulationScheme);
            Assert.True(loaded.Performance.RxModulated);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
