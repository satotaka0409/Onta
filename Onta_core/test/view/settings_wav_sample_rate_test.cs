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
                    WavSampleRate = 48000
                });

            MainWindowSettingsStore.Save(path, settings);
            Assert.True(MainWindowSettingsStore.TryLoad(path, out var loaded));
            Assert.Equal(96000, loaded.Send.WavSampleRate);
            Assert.Equal(48000, loaded.Performance.WavSampleRate);
            Assert.Equal("out.wav", loaded.Send.WavOutputPath);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
