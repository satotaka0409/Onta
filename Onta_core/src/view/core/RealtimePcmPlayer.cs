using System.Numerics;
using NAudio.Wave;
using Onta.Core;

namespace Onta.View.Core;

/// <summary>
/// PCM チャンクを逐次キューイングしてリアルタイム再生するプレイヤーです。
/// </summary>
internal sealed class RealtimePcmPlayer : IDisposable
{
    /// <summary>1回に変換・投入する最大サンプル数です。</summary>
    private const int MaxSliceSamples = 4410;

    private readonly object _sync = new();
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _buffer;
    private byte[]? _convertScratch;
    private int _channels;
    private int _sampleRate = 44100;
    private double _scale = 1.0;
    private bool _disposed;

    /// <summary>
    /// 破棄済みかどうかを返します。
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 再生デバイスとフォーマットを初期化して再生を開始します。
    /// </summary>
    /// <param name="deviceNumber">出力デバイス番号。</param>
    /// <param name="sampleRate">サンプルレート。</param>
    /// <param name="channelMode">モノラル/ステレオ。</param>
    /// <param name="samplePeak">出力振幅スケール。</param>
    public void Start(int deviceNumber, int sampleRate, Onta.Core.ChannelMode channelMode, double samplePeak)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopInternal();

        _channels = channelMode == Onta.Core.ChannelMode.Stereo ? 2 : 1;
        _sampleRate = Math.Max(1, sampleRate);
        // クリップを避けるため再生振幅を安全域に制限する。
        _scale = Math.Clamp(samplePeak <= 0.0 ? 0.8 : samplePeak, 0.05, 1.0);
        var format = new WaveFormat(_sampleRate, 16, _channels);
        _buffer = new BufferedWaveProvider(format)
        {
            // 長時間再生に備えてバッファを十分確保する。
            BufferDuration = TimeSpan.FromSeconds(60),
            DiscardOnBufferOverflow = false
        };
        _waveOut = new WaveOutEvent
        {
            DeviceNumber = deviceNumber,
            DesiredLatency = 100
        };
        _waveOut.Init(_buffer);
        _waveOut.Play();
    }

    /// <summary>
    /// 複素PCMチャンクを16bit PCMへ変換して再生キューへ追加します。
    /// </summary>
    /// <param name="left">左チャネルサンプル。</param>
    /// <param name="right">右チャネルサンプル。</param>
    /// <param name="onSamplesQueued">投入フレーム数通知コールバック。</param>
    public void AddSamples(
        ReadOnlySpan<Complex> left,
        ReadOnlySpan<Complex> right,
        Action<int>? onSamplesQueued = null)
    {
        if (_disposed)
        {
            throw new OperationCanceledException("Realtime playback was stopped.");
        }

        BufferedWaveProvider buffer;
        WaveOutEvent waveOut;
        int channels;
        double scale;
        lock (_sync)
        {
            if (_disposed || _buffer is null || _waveOut is null)
            {
                throw new OperationCanceledException("Realtime playback was stopped.");
            }

            buffer = _buffer;
            waveOut = _waveOut;
            channels = _channels;
            scale = _scale;
        }

        if (left.Length == 0)
        {
            return;
        }

        if (channels == 2 && right.Length != left.Length)
        {
            throw new ArgumentException("Stereo playback requires equal L/R chunk lengths.");
        }

        var bytesPerFrame = channels * sizeof(short);
        var offset = 0;
        while (offset < left.Length && !_disposed)
        {
            var slice = Math.Min(MaxSliceSamples, left.Length - offset);
            var byteCount = slice * bytesPerFrame;
            WaitForBufferSpace(buffer, waveOut, byteCount);
            if (_disposed)
            {
                throw new OperationCanceledException("Realtime playback was stopped.");
            }

            EnsureScratch(byteCount);
            var scratch = _convertScratch!;
            var write = 0;
            for (var i = 0; i < slice; i++)
            {
                WritePcm16(scratch, ref write, left[offset + i].Real, scale);
                if (channels == 2)
                {
                    WritePcm16(scratch, ref write, right[offset + i].Real, scale);
                }
            }

            buffer.AddSamples(scratch, 0, byteCount);
            offset += slice;
            onSamplesQueued?.Invoke(slice);

            if (waveOut.PlaybackState != PlaybackState.Playing)
            {
                waveOut.Play();
            }
        }
    }

    /// <summary>
    /// 指定時間内で再生バッファが空になるまで待機します。
    /// </summary>
    /// <param name="timeout">待機上限時間。</param>
    public void FinishAndWait(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!_disposed && DateTime.UtcNow < deadline)
        {
            BufferedWaveProvider? buffer;
            lock (_sync)
            {
                buffer = _buffer;
            }

            if (buffer is null)
            {
                return;
            }

            if (buffer.BufferedBytes <= 0)
            {
                Thread.Sleep(80);
                return;
            }

            Thread.Sleep(30);
        }
    }

    /// <summary>
    /// 現在バッファに残っているフレーム数です。
    /// </summary>
    public int BufferedSampleFrames
    {
        get
        {
            lock (_sync)
            {
                if (_buffer is null || _channels <= 0)
                {
                    return 0;
                }

                var bytesPerFrame = _channels * sizeof(short);
                return bytesPerFrame <= 0 ? 0 : _buffer.BufferedBytes / bytesPerFrame;
            }
        }
    }

    /// <summary>
    /// 再生リソースを解放します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopInternal();
    }

    private void WaitForBufferSpace(BufferedWaveProvider buffer, WaveOutEvent waveOut, int requiredBytes)
    {
        while (!_disposed)
        {
            var free = buffer.BufferLength - buffer.BufferedBytes;
            if (free >= requiredBytes)
            {
                return;
            }

            if (waveOut.PlaybackState != PlaybackState.Playing)
            {
                waveOut.Play();
            }

            Thread.Sleep(20);
        }
    }

    private void StopInternal()
    {
        lock (_sync)
        {
            if (_waveOut is not null)
            {
                try
                {
                    _waveOut.Stop();
                }
                catch
                {
                    // ignore
                }

                _waveOut.Dispose();
                _waveOut = null;
            }

            _buffer = null;
        }
    }

    private void EnsureScratch(int byteCount)
    {
        if (_convertScratch is null || _convertScratch.Length < byteCount)
        {
            _convertScratch = new byte[Math.Max(byteCount, 4096)];
        }
    }

    private static void WritePcm16(byte[] dest, ref int offset, double value, double scale)
    {
        var sample = (short)Math.Round(Math.Clamp(value * scale, -1.0, 1.0) * short.MaxValue);
        dest[offset++] = (byte)(sample & 0xFF);
        dest[offset++] = (byte)((sample >> 8) & 0xFF);
    }
}




