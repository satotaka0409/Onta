using System.Numerics;
using NAudio.Wave;
using Onta.Core;

namespace Onta.View;

/// <summary>
/// 符号化中の PCM を NAudio 経由でリアルタイム再生します。
/// エンコードが再生より速い場合はバッファ空きを待って分割投入します。
/// </summary>
internal sealed class RealtimePcmPlayer : IDisposable
{
    /// <summary>1回の AddSamples 上限（約 100ms @ 44.1kHz）。大きな BD チャンクの Buffer full を防ぐ。</summary>
    private const int MaxSliceSamples = 4410;

    private readonly object _sync = new();
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _buffer;
    private byte[]? _convertScratch;
    private int _channels;
    private int _sampleRate = 44100;
    private double _scale = 1.0;
    private bool _disposed;

    public bool IsDisposed => _disposed;

    /// <summary>再生を開始します。</summary>
    /// <param name="deviceNumber">NAudio デバイス番号（-1=既定）。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <param name="channelMode">モノラル / ステレオ。</param>
    /// <param name="samplePeak">PCM 量子化時のピーク目標（既存 WAV 出力と揃える）。</param>
    public void Start(int deviceNumber, int sampleRate, Onta.Core.ChannelMode channelMode, double samplePeak)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopInternal();

        _channels = channelMode == Onta.Core.ChannelMode.Stereo ? 2 : 1;
        _sampleRate = Math.Max(1, sampleRate);
        // リアルタイムでは全体ピークが未知のため、SamplePeak をそのまま振幅スケールに使う。
        _scale = Math.Clamp(samplePeak <= 0.0 ? 0.8 : samplePeak, 0.05, 1.0);
        var format = new WaveFormat(_sampleRate, 16, _channels);
        _buffer = new BufferedWaveProvider(format)
        {
            // 長尺ブロックでも空き待ちできるよう余裕を持たせる。
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

    /// <summary>符号化で生成された Complex PCM チャンクを再生キューへ投入します。</summary>
    public void AddSamples(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right)
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

            if (waveOut.PlaybackState != PlaybackState.Playing)
            {
                waveOut.Play();
            }
        }
    }

    /// <summary>符号化完了後、残バッファの再生終了を待ちます。</summary>
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

    /// <summary>再生待ちバッファに残っているサンプルフレーム数（モノラル換算の時間軸）。</summary>
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
