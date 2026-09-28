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
    private float[] _floatScratch = [];
    private readonly List<float> _resampled = new();
    private StreamingPcmResampler? _resampler;
    private int _channels;
    private int _appSampleRate = 44100;
    private int _deviceSampleRate = 44100;
    private double _scale = 1.0;
    private bool _disposed;

    /// <summary>
    /// 破棄済みかどうかを返します。
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 再生デバイスとフォーマットを初期化して再生を開始します。
    /// 指定デバイスのミックス形式で開きます。入力が 44100 Hz のときはその周波数へ変換してから出力します。
    /// </summary>
    /// <param name="deviceNumber">出力デバイス番号。</param>
    /// <param name="sampleRate">入力サンプルのサンプリング周波数（変調・WAV 側は 44100）。</param>
    /// <param name="channelMode">モノラル/ステレオ。</param>
    /// <param name="samplePeak">出力振幅スケール。</param>
    public void Start(int deviceNumber, int sampleRate, Onta.Core.ChannelMode channelMode, double samplePeak)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopInternal();

        _channels = channelMode == Onta.Core.ChannelMode.Stereo ? 2 : 1;
        _appSampleRate = Math.Max(1, sampleRate);
        // 音量バー 0% は無音、それ以外は 0.05〜1.0 に制限する。
        _scale = NormalizeOutputScale(samplePeak);
        var deviceRate = AudioDeviceSampleRate.ResolveRender(deviceNumber, _appSampleRate);
        try
        {
            Open(deviceNumber, deviceRate);
        }
        catch when (deviceRate != _appSampleRate)
        {
            StopInternal();
            deviceRate = _appSampleRate;
            Open(deviceNumber, deviceRate);
        }

        _resampler = deviceRate == _appSampleRate
            ? null
            : new StreamingPcmResampler(_appSampleRate, deviceRate, _channels);
    }

    /// <summary>
    /// 再生中の出力音量を変更します。以降に投入するサンプルから反映されます。
    /// </summary>
    /// <param name="samplePeak">出力振幅スケール（0 は無音、それ以外は 0.05〜1）。</param>
    public void SetOutputVolume(double samplePeak)
    {
        lock (_sync)
        {
            _scale = NormalizeOutputScale(samplePeak);
        }
    }

    /// <summary>
    /// 出力音量を再生用の範囲へ収めます。
    /// </summary>
    /// <param name="samplePeak">指定された振幅スケール。</param>
    /// <returns>0 はそのまま無音、それ以外は 0.05〜1。</returns>
    private static double NormalizeOutputScale(double samplePeak) =>
        samplePeak <= 0.0 ? 0.0 : Math.Clamp(samplePeak, 0.05, 1.0);

    /// <summary>
    /// WaveOut を指定周波数で開いて再生を開始します。
    /// </summary>
    /// <param name="deviceNumber">出力デバイス番号。</param>
    /// <param name="sampleRate">デバイス側サンプリング周波数。</param>
    private void Open(int deviceNumber, int sampleRate)
    {
        _deviceSampleRate = Math.Max(1, sampleRate);
        var format = new WaveFormat(_deviceSampleRate, 16, _channels);
        _buffer = new BufferedWaveProvider(format)
        {
            // 長時間先読みを避け、FFT/進捗と耳の聴感を揃える（エンコードはバッファ満杯で待機）。
            BufferDuration = TimeSpan.FromSeconds(3),
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
    /// <param name="onBufferWait">バッファ待ち中の心拍（再生ヘッド追従用）。</param>
    public void AddSamples(
        ReadOnlySpan<Complex> left,
        ReadOnlySpan<Complex> right,
        Action<int>? onSamplesQueued = null,
        Action? onBufferWait = null)
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
            int byteCount;
            EnsureScratch(slice * bytesPerFrame);
            var scratch = _convertScratch!;
            if (_resampler is null)
            {
                byteCount = slice * bytesPerFrame;
                var write = 0;
                for (var i = 0; i < slice; i++)
                {
                    WritePcm16(scratch, ref write, left[offset + i].Real, scale);
                    if (channels == 2)
                    {
                        WritePcm16(scratch, ref write, right[offset + i].Real, scale);
                    }
                }
            }
            else
            {
                var floatCount = slice * channels;
                if (_floatScratch.Length < floatCount)
                {
                    _floatScratch = new float[floatCount];
                }

                var sampleIndex = 0;
                for (var i = 0; i < slice; i++)
                {
                    _floatScratch[sampleIndex++] = (float)left[offset + i].Real;
                    if (channels == 2)
                    {
                        _floatScratch[sampleIndex++] = (float)right[offset + i].Real;
                    }
                }

                _resampled.Clear();
                _resampler.Process(_floatScratch.AsSpan(0, floatCount), _resampled);
                var outFrames = _resampled.Count / channels;
                byteCount = outFrames * bytesPerFrame;
                if (byteCount > 0)
                {
                    EnsureScratch(byteCount);
                    scratch = _convertScratch!;
                    var write = 0;
                    sampleIndex = 0;
                    for (var i = 0; i < outFrames; i++)
                    {
                        WritePcm16(scratch, ref write, _resampled[sampleIndex++], scale);
                        if (channels == 2)
                        {
                            WritePcm16(scratch, ref write, _resampled[sampleIndex++], scale);
                        }
                    }
                }
            }

            if (byteCount <= 0)
            {
                offset += slice;
                onSamplesQueued?.Invoke(slice);
                continue;
            }

            WaitForBufferSpace(buffer, waveOut, byteCount, onBufferWait);
            if (_disposed)
            {
                throw new OperationCanceledException("Realtime playback was stopped.");
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
    /// 未再生のキューを破棄し、次の AddSamples から即反映できるようにします。
    /// </summary>
    public void ClearQueuedSamples()
    {
        lock (_sync)
        {
            if (_disposed || _buffer is null)
            {
                return;
            }

            _buffer.ClearBuffer();
            if (_waveOut is not null && _waveOut.PlaybackState != PlaybackState.Playing)
            {
                try
                {
                    _waveOut.Play();
                }
                catch
                {
                    // ignore
                }
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
                if (bytesPerFrame <= 0)
                {
                    return 0;
                }

                var deviceFrames = _buffer.BufferedBytes / bytesPerFrame;
                if (_deviceSampleRate <= 0 || _deviceSampleRate == _appSampleRate)
                {
                    return deviceFrames;
                }

                return (int)Math.Round(deviceFrames * (_appSampleRate / (double)_deviceSampleRate));
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

    /// <summary>
    /// 再生バッファに必要な空きができるまで待機し、停止中なら再生を再開します。
    /// </summary>
    /// <param name="buffer">書き込み先バッファ。</param>
    /// <param name="waveOut">再生デバイス。</param>
    /// <param name="requiredBytes">必要な空きバイト数。</param>
    /// <param name="onBufferWait">待ち中の心拍コールバック。</param>
    private void WaitForBufferSpace(
        BufferedWaveProvider buffer,
        WaveOutEvent waveOut,
        int requiredBytes,
        Action? onBufferWait)
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

            onBufferWait?.Invoke();
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// WaveOut を停止・破棄し、バッファ参照をクリアします。
    /// </summary>
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
            _resampler = null;
            _resampled.Clear();
        }
    }

    /// <summary>
    /// PCM 変換用スクラッチバッファを必要サイズまで確保します。
    /// </summary>
    /// <param name="byteCount">必要なバイト数。</param>
    private void EnsureScratch(int byteCount)
    {
        if (_convertScratch is null || _convertScratch.Length < byteCount)
        {
            _convertScratch = new byte[Math.Max(byteCount, 4096)];
        }
    }

    /// <summary>
    /// 正規化サンプルを 16bit little-endian PCM として書き込みます。
    /// </summary>
    /// <param name="dest">書き込み先バッファ。</param>
    /// <param name="offset">書き込み位置（書き込み後に進む）。</param>
    /// <param name="value">正規化振幅（±1 想定）。</param>
    /// <param name="scale">出力スケール。</param>
    private static void WritePcm16(byte[] dest, ref int offset, double value, double scale)
    {
        var sample = (short)Math.Round(Math.Clamp(value * scale, -1.0, 1.0) * short.MaxValue);
        dest[offset++] = (byte)(sample & 0xFF);
        dest[offset++] = (byte)((sample >> 8) & 0xFF);
    }
}




