using System.Numerics;
using NAudio.Wave;
using Onta.Core;

namespace Onta.View.Core;

/// <summary>
/// 音声入力デバイスから PCM を小チャンクで取り込み、複素サンプルとして通知します。
/// WAV 化や大規模バッファへの蓄積は行いません。
/// </summary>
internal sealed class RealtimePcmCapture : IDisposable
{
    private WaveInEvent? _waveIn;
    private bool _disposed;

    /// <summary>
    /// 1チャンク分の L/R 複素サンプルを受け取ります。
    /// </summary>
    public event Action<Complex[], Complex[]>? SamplesAvailable;

    /// <summary>
    /// キャプチャエラーを通知します。
    /// </summary>
    public event Action<string>? CaptureFailed;

    /// <summary>
    /// キャプチャ中かどうかです。
    /// </summary>
    public bool IsRunning => _waveIn is not null;

    /// <summary>
    /// 指定デバイスから 44.1kHz / 16bit で取り込みを開始します。
    /// </summary>
    /// <param name="deviceNumber">WaveIn デバイス番号（-1 は既定）。</param>
    /// <param name="channelMode">モノラル / ステレオ。</param>
    /// <param name="sampleRate">サンプルレート。</param>
    public void Start(int deviceNumber, Onta.Core.ChannelMode channelMode, int sampleRate = 44100)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        var channels = channelMode == Onta.Core.ChannelMode.Stereo ? 2 : 1;
        var waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(Math.Max(1, sampleRate), 16, channels),
            // 低遅延・少量チャンク（ファイル化や大量蓄積を避ける）
            BufferMilliseconds = 50
        };
        waveIn.DataAvailable += OnDataAvailable;
        waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn = waveIn;
        waveIn.StartRecording();
    }

    /// <summary>
    /// 取り込みを停止します。
    /// </summary>
    public void Stop()
    {
        var waveIn = _waveIn;
        _waveIn = null;
        if (waveIn is null)
        {
            return;
        }

        try
        {
            waveIn.StopRecording();
        }
        catch
        {
            // 既に停止済み等は無視
        }

        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.RecordingStopped -= OnRecordingStopped;
        waveIn.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _waveIn is null)
        {
            return;
        }

        try
        {
            var format = _waveIn.WaveFormat;
            var bytesPerFrame = format.Channels * (format.BitsPerSample / 8);
            if (bytesPerFrame <= 0)
            {
                return;
            }

            var frames = e.BytesRecorded / bytesPerFrame;
            if (frames <= 0)
            {
                return;
            }

            var left = new Complex[frames];
            var right = format.Channels >= 2 ? new Complex[frames] : Array.Empty<Complex>();
            var src = e.Buffer;
            var offset = 0;
            for (var i = 0; i < frames; i++)
            {
                var l = BitConverter.ToInt16(src, offset) / 32768.0;
                offset += 2;
                left[i] = new Complex(l, 0.0);
                if (format.Channels >= 2)
                {
                    var r = BitConverter.ToInt16(src, offset) / 32768.0;
                    offset += 2;
                    right[i] = new Complex(r, 0.0);
                }
            }

            SamplesAvailable?.Invoke(left, right);
        }
        catch (Exception ex)
        {
            CaptureFailed?.Invoke(ex.Message);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            CaptureFailed?.Invoke(e.Exception.Message);
        }
    }
}
