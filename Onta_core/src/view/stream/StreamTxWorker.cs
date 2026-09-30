using System.Numerics;
using Onta.Core;
using Onta.Performance;
using Onta.Stream;
using Onta.View.Core;
using Onta.View.Language;

namespace Onta.View.Stream;

/// <summary>
/// ストリーム録音送信ワーカーです。
/// </summary>
internal sealed class StreamTxWorker : IDisposable
{
    private const int PcmCap = PerformanceConstants.ScopeCaptureSamples;
    private const int FftSize = 2048;
    private const int MinFftPublishIntervalMs = 80;

    private readonly object _sync = new();
    private readonly CoreExecutionStatusBoard _status = new();
    private readonly double[] _pcmLeft = new double[PcmCap];
    private readonly double[] _pcmRight = new double[PcmCap];
    private readonly Complex[] _iqScratch = new Complex[128];
    private readonly byte[] _iqGroups = new byte[128];
    private readonly double[] _iqPcmScratch = new double[PerformanceIqExtractor.CaptureSamples];
    private readonly Complex[] _iqTimeScratch = new Complex[PerformanceIqExtractor.FftSize];
    private readonly Complex[] _iqFftScratch = new Complex[PerformanceIqExtractor.FftSize];

    private readonly Complex[] _fftLeft = new Complex[FftSize];
    private readonly Complex[] _fftRight = new Complex[FftSize];
    private int _vizSampleRate = StreamConstants.DefaultSampleRate;
    private long _pcmWriteTotal;
    private long _lastFftPublishMs = -1;
    private Task? _worker;
    private CancellationTokenSource? _cts;
    private (bool Success, string Message)? _completion;
    private bool _disposed;

    /// <summary>共有状態（進捗・FFT・I-Q）。</summary>
    public CoreExecutionStatusBoard SharedStatus => _status;

    /// <summary>実行中か。</summary>
    public bool IsBusy
    {
        get
        {
            lock (_sync)
            {
                return _worker is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// 完了結果を 1 回だけ取り出します。
    /// </summary>
    /// <param name="success">成功なら true。</param>
    /// <param name="message">完了メッセージ。</param>
    /// <returns>完了が保留中で取り出せた場合 true。</returns>
    public bool TryConsumeCompletion(out bool success, out string message)
    {
        lock (_sync)
        {
            if (_completion is null)
            {
                success = false;
                message = string.Empty;
                return false;
            }

            (success, message) = _completion.Value;
            _completion = null;
            return true;
        }
    }

    /// <summary>
    /// 送信を開始します。
    /// </summary>
    /// <param name="settings">送信設定。</param>
    public void Start(StreamTxSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_worker is { IsCompleted: false })
            {
                throw new InvalidOperationException("Stream TX is already running.");
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _completion = null;
            _pcmWriteTotal = 0;
            _lastFftPublishMs = -1;
            _worker = Task.Run(() => Run(settings, token), token);
        }
    }

    /// <summary>
    /// 送信を停止します。
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _cts;
        }

        cts?.Cancel();
    }

    /// <summary>
    /// ファイルまたは音声入力をストリーム変調して再生し、FFT と I-Q を公開します。
    /// </summary>
    /// <param name="settings">送信設定。</param>
    /// <param name="token">停止用のキャンセルトークン。</param>
    private void Run(StreamTxSettings settings, CancellationToken token)
    {
        _status.BeginRun(CoreViewText.StreamTxRunningTitle);
        _status.SetAnalyzing(false);
        _status.SetFftStereoMode(true);
        RealtimePcmPlayer? player = null;
        RealtimePcmCapture? capture = null;
        StreamTxPipeline? pipeline = null;
        try
        {
            byte[]? cover = null;
            if (!string.IsNullOrWhiteSpace(settings.CoverPath) && File.Exists(settings.CoverPath))
            {
                cover = StreamCoverImage.EncodeFile(settings.CoverPath, settings.CoverFormat, out _);
            }

            var mode = StreamMode.Resolve(settings.ModeId);

            if (settings.UseWavInput)
            {
                if (string.IsNullOrWhiteSpace(settings.WavPath) || !File.Exists(settings.WavPath))
                {
                    throw new FileNotFoundException(CoreViewText.InputFileNotFound, settings.WavPath);
                }

                // 受信側と OFDM シンボル長を揃えるため、ファイルのレートによらず 44.1 kHz で変調する。
                using var reader = new StreamAudioFilePcmReader(settings.WavPath, StreamConstants.DefaultSampleRate);
                var streamSampleRate = reader.SampleRate;
                _vizSampleRate = streamSampleRate;
                pipeline = new StreamTxPipeline(settings.ModeId, settings.Title, settings.Artist, cover, streamSampleRate);

                player = new RealtimePcmPlayer();
                player.Start(settings.OutputDevice, streamSampleRate, ChannelMode.Stereo, settings.OutputVolume);

                var chunk = Math.Max(1, streamSampleRate / 10);
                while (!token.IsCancellationRequested && reader.TryRead(chunk, out var leftC, out var rightC))
                {
                    var left = ToDouble(leftC);
                    var right = ToDouble(rightC.Length > 0 ? rightC : leftC);
                    Scale(left, settings.InputVolume);
                    Scale(right, settings.InputVolume);
                    foreach (var (l, r) in pipeline.PushPcm(left, right))
                    {
                        token.ThrowIfCancellationRequested();
                        if (ReferenceEquals(l, r))
                        {
                            throw new InvalidOperationException("Stream TX returned identical L/R buffers.");
                        }

                        PublishPcmForViz(l, r, mode);
                        player.AddSamples(l, r);
                    }

                    _status.SetProgress(new CoreProgressInfo(
                        CurrentFrame: CoreFrameKind.Bd,
                        CurrentBlockIndex: 0,
                        PassIndex: 0,
                        AcceptedBlockCount: 0,
                        TotalBlockCount: 0,
                        ProgressPercent: reader.Progress * 100.0));
                }

                if (!token.IsCancellationRequested)
                {
                    foreach (var (l, r) in pipeline.Flush())
                    {
                        PublishPcmForViz(l, r, mode);
                        player.AddSamples(l, r);
                    }
                }
            }
            else
            {
                // デバイスは固有レートで開き、入出力の境界で 44.1 kHz と相互変換する。
                var streamSampleRate = StreamConstants.DefaultSampleRate;
                _vizSampleRate = streamSampleRate;
                pipeline = new StreamTxPipeline(settings.ModeId, settings.Title, settings.Artist, cover, streamSampleRate);

                player = new RealtimePcmPlayer();
                player.Start(settings.OutputDevice, streamSampleRate, ChannelMode.Stereo, settings.OutputVolume);

                var queue = new Queue<(Complex[] L, Complex[] R)>();
                var gate = new object();
                capture = new RealtimePcmCapture();
                capture.SamplesAvailable += (l, r) =>
                {
                    lock (gate)
                    {
                        queue.Enqueue((l, r));
                    }
                };
                capture.CaptureFailed += msg => throw new InvalidOperationException(msg);
                capture.Start(settings.InputDevice, ChannelMode.Stereo, streamSampleRate, settings.InputVolume);

                while (!token.IsCancellationRequested)
                {
                    (Complex[] L, Complex[] R)? item = null;
                    lock (gate)
                    {
                        if (queue.Count > 0)
                        {
                            item = queue.Dequeue();
                        }
                    }

                    if (item is null)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    var left = ToDouble(item.Value.L);
                    var right = ToDouble(item.Value.R);
                    foreach (var (l, r) in pipeline.PushPcm(left, right))
                    {
                        token.ThrowIfCancellationRequested();
                        if (ReferenceEquals(l, r))
                        {
                            throw new InvalidOperationException("Stream TX returned identical L/R buffers.");
                        }

                        PublishPcmForViz(l, r, mode);
                        player.AddSamples(l, r);
                    }

                    _status.SetProgress(new CoreProgressInfo(
                        CurrentFrame: CoreFrameKind.Bd,
                        CurrentBlockIndex: 0,
                        PassIndex: 0,
                        AcceptedBlockCount: 0,
                        TotalBlockCount: 0,
                        ProgressPercent: 50.0));
                }
            }

            _status.Complete(faulted: false);
            Complete(true, CoreViewText.StreamTxCompleted);
        }
        catch (OperationCanceledException)
        {
            _status.Complete(faulted: true, CoreViewText.Cancelled);
            Complete(false, CoreViewText.Cancelled);
        }
        catch (Exception ex)
        {
            _status.Complete(faulted: true, ex.Message);
            Complete(false, ex.Message);
        }
        finally
        {
            capture?.Dispose();
            player?.Dispose();
            pipeline?.Dispose();
        }
    }

    /// <summary>
    /// 送信 PCM をリングへ載せ、FFT と I-Q を共有ボードへ公開します。
    /// </summary>
    /// <param name="left">送信した L チャネル PCM。</param>
    /// <param name="right">送信した R チャネル PCM。</param>
    /// <param name="mode">ストリーム変調モード。</param>
    private void PublishPcmForViz(Complex[] left, Complex[] right, StreamModeInfo mode)
    {
        var len = left.Length;
        if (right.Length != len)
        {
            throw new InvalidOperationException(
                $"Stream TX L/R length mismatch: L={left.Length}, R={right.Length}.");
        }

        if (len <= 0)
        {
            return;
        }

        lock (_sync)
        {
            for (var i = 0; i < len; i++)
            {
                var idx = (int)(_pcmWriteTotal % PcmCap);
                _pcmLeft[idx] = left[i].Real;
                _pcmRight[idx] = right[i].Real;
                _pcmWriteTotal++;
            }
        }

        PublishFft();
        PublishIq(mode);
    }

    /// <summary>
    /// 送信 PCM 末尾を L/R 別バッファへ切り出し、Hanning 窓 FFT して共有ボードへ載せます。
    /// </summary>
    private void PublishFft()
    {
        lock (_sync)
        {
            if (_pcmWriteTotal < FftSize)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (_lastFftPublishMs >= 0 && now - _lastFftPublishMs < MinFftPublishIntervalMs)
            {
                return;
            }

            _lastFftPublishMs = now;
            PerformanceRingCopy.FillComplexWindow(_pcmLeft, _pcmWriteTotal, _fftLeft, FftSize);
            PerformanceRingCopy.FillComplexWindow(_pcmRight, _pcmWriteTotal, _fftRight, FftSize);
        }

        PerformanceFftAnalyzer.ComputeSpectrumInPlace(_fftLeft, PerformanceFftWindowKind.Hanning);
        PerformanceFftAnalyzer.ComputeSpectrumInPlace(_fftRight, PerformanceFftWindowKind.Hanning);
        _status.SetFftStereoFrames(_fftLeft, _fftRight, _vizSampleRate);
    }

    /// <summary>
    /// 送信 PCM 末尾から等化 I-Q を抽出し共有ボードへ載せます。
    /// </summary>
    /// <param name="mode">ストリーム変調モード。</param>
    private void PublishIq(StreamModeInfo mode)
    {
        var sc = PerformanceSignalGenerator.ClampSubcarriers(mode.Subcarriers);
        var mod = mode.Modulation;

        int leftPcmCount;
        lock (_sync)
        {
            leftPcmCount = Math.Min(_iqPcmScratch.Length, (int)Math.Min(_pcmWriteTotal, PcmCap));
            if (leftPcmCount < PerformanceIqExtractor.FftSize)
            {
                return;
            }

            PerformanceRingCopy.CopyTail(_pcmLeft, _pcmWriteTotal, _iqPcmScratch.AsSpan(0, leftPcmCount), leftPcmCount);
        }

        var leftCount = PerformanceIqExtractor.ExtractEqualized(
            _iqPcmScratch.AsSpan(0, leftPcmCount),
            sc,
            useRightCarriers: false,
            _iqTimeScratch,
            _iqFftScratch,
            _iqScratch.AsSpan(),
            _iqGroups.AsSpan());
        var count = leftCount;

        int rightPcmCount;
        lock (_sync)
        {
            rightPcmCount = Math.Min(_iqPcmScratch.Length, (int)Math.Min(_pcmWriteTotal, PcmCap));
            if (rightPcmCount >= PerformanceIqExtractor.FftSize)
            {
                PerformanceRingCopy.CopyTail(_pcmRight, _pcmWriteTotal, _iqPcmScratch.AsSpan(0, rightPcmCount), rightPcmCount);
            }
            else
            {
                rightPcmCount = 0;
            }
        }

        if (rightPcmCount >= PerformanceIqExtractor.FftSize)
        {
            var rightCount = PerformanceIqExtractor.ExtractEqualized(
                _iqPcmScratch.AsSpan(0, rightPcmCount),
                sc,
                useRightCarriers: true,
                _iqTimeScratch,
                _iqFftScratch,
                _iqScratch.AsSpan(leftCount),
                _iqGroups.AsSpan(leftCount));
            count = leftCount + rightCount;
        }

        if (count <= 0)
        {
            return;
        }

        _status.BeginIqCapture(sc, mod);
        _status.AppendIqFrame(_iqScratch.AsSpan(0, count), _iqGroups.AsSpan(0, count));
        _status.SetIqLeftPointCount(leftCount);
    }

    /// <summary>
    /// 完了結果を、あとから一度だけ取り出せるよう格納します。
    /// </summary>
    /// <param name="success">成功なら true。</param>
    /// <param name="message">完了メッセージ。</param>
    private void Complete(bool success, string message)
    {
        lock (_sync)
        {
            _completion = (success, message);
        }
    }

    /// <summary>
    /// 複素サンプルの実部を double 配列にします。
    /// </summary>
    /// <param name="samples">複素 PCM。</param>
    /// <returns>実部の配列。</returns>
    private static double[] ToDouble(Complex[] samples)
    {
        var d = new double[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            d[i] = samples[i].Real;
        }

        return d;
    }

    /// <summary>
    /// サンプルへ音量ゲインを掛けます。
    /// </summary>
    /// <param name="samples">スケールする PCM。</param>
    /// <param name="gain">音量ゲイン。0〜1.5 に制限します。</param>
    private static void Scale(double[] samples, double gain)
    {
        var g = Math.Clamp(gain, 0.0, 1.5);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= g;
        }
    }

    /// <summary>
    /// 送信を停止し、ワーカーを破棄します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        try
        {
            _worker?.Wait(2000);
        }
        catch
        {
            // ignore
        }

        _cts?.Dispose();
    }
}
