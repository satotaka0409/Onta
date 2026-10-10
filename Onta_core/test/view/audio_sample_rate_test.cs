using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Onta.View.Core;
using Xunit;

namespace Onta.Core.Tests.View;

/// <summary>
/// デバイス境界のサンプリング周波数変換です。
/// </summary>
public sealed class AudioSampleRateTest
{
    [Fact]
    public void Resample_44100To48000_KeepsDurationAndLevel()
    {
        const int inputRate = 44100;
        const int outputRate = 48000;
        var input = new float[inputRate];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = 0.5f;
        }

        var resampler = new StreamingPcmResampler(inputRate, outputRate, 1);
        var output = new List<float>();
        var offset = 0;
        while (offset < input.Length)
        {
            var count = Math.Min(512, input.Length - offset);
            resampler.Process(input.AsSpan(offset, count), output);
            offset += count;
        }

        Assert.InRange(output.Count, outputRate - 200, outputRate + 200);
        var tail = output.Skip(200).ToArray();
        var mean = tail.Average();
        Assert.InRange(mean, 0.49, 0.51);
    }

    [Fact]
    public void Resample_Stereo48000To44100_KeepsChannels()
    {
        const int frames = 4800;
        var input = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            input[i * 2] = 0.25f;
            input[(i * 2) + 1] = -0.25f;
        }

        var resampler = new StreamingPcmResampler(48000, 44100, 2);
        var output = new List<float>();
        resampler.Process(input, output);

        Assert.Equal(0, output.Count % 2);
        var outFrames = output.Count / 2;
        Assert.InRange(outFrames, 4300, 4500);
        var left = output.Where((_, index) => index % 2 == 0).Skip(40).Average();
        var right = output.Where((_, index) => index % 2 == 1).Skip(40).Average();
        Assert.InRange(left, 0.2, 0.3);
        Assert.InRange(right, -0.3, -0.2);
    }

    [Fact]
    public void Resolve_DefaultDevices_ReturnsPositiveRate()
    {
        var capture = AudioDeviceSampleRate.ResolveCapture(-1, 44100);
        var render = AudioDeviceSampleRate.ResolveRender(-1, 44100);
        Assert.InRange(capture, 8000, 384000);
        Assert.InRange(render, 8000, 384000);
    }

    [Fact]
    public void ResolveRender_SpecifiedDevice_UsesThatDeviceMixRate()
    {
        Assert.True(WaveOut.DeviceCount > 0);
        for (var i = 0; i < WaveOut.DeviceCount; i++)
        {
            var expected = MixRateOfEndpoint(QueryRenderEndpointId(i));
            var actual = AudioDeviceSampleRate.ResolveRender(i, 44100);
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// 指定エンドポイント ID のミックス形式サンプリング周波数を返します。
    /// </summary>
    /// <param name="endpointId">WASAPI エンドポイント ID。</param>
    /// <returns>サンプリング周波数（Hz）。</returns>
    private static int MixRateOfEndpoint(string endpointId)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (string.Equals(endpoint.ID, endpointId, StringComparison.OrdinalIgnoreCase))
            {
                return endpoint.AudioClient.MixFormat.SampleRate;
            }
        }

        throw new InvalidOperationException($"endpoint not found: {endpointId}");
    }

    /// <summary>
    /// WaveOut デバイス番号の WASAPI エンドポイント ID を返します。
    /// </summary>
    /// <param name="deviceNumber">WaveOut デバイス番号。</param>
    /// <returns>エンドポイント ID。</returns>
    private static string QueryRenderEndpointId(int deviceNumber)
    {
        var rc = WaveOutMessageSize((IntPtr)deviceNumber, 0x812, out var size, IntPtr.Zero);
        Assert.Equal(0, rc);
        var id = new StringBuilder(Math.Max(size / 2, 1));
        rc = WaveOutMessageId((IntPtr)deviceNumber, 0x811, id, (IntPtr)size);
        Assert.Equal(0, rc);
        return id.ToString().Trim().TrimEnd('\0');
    }

    /// <summary>
    /// waveOutMessage でバッファサイズを問い合わせます。
    /// </summary>
    [DllImport("winmm.dll", EntryPoint = "waveOutMessage")]
    private static extern int WaveOutMessageSize(IntPtr device, uint message, out int size, IntPtr unused);

    /// <summary>
    /// waveOutMessage でエンドポイント ID 文字列を取得します。
    /// </summary>
    [DllImport("winmm.dll", EntryPoint = "waveOutMessage", CharSet = CharSet.Unicode)]
    private static extern int WaveOutMessageId(IntPtr device, uint message, StringBuilder id, IntPtr sizeBytes);
}
