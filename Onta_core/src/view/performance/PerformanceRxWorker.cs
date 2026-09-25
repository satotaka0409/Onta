using System.Numerics;
using Onta.Core;
using Onta.View.Core;

namespace Onta.View.Performance;

/// <summary>
/// 性能測定の受信（キャプチャ / WAV → FFT / IQ / ワウ）を実行します。
/// </summary>
internal sealed class PerformanceRxWorker : IDisposable
{
    private const int RingCapacity = PerformanceConstants.ScopeCaptureSamples;

    private readonly object _sync = new();
    private readonly CoreExecutionStatusBoard _status = new();
    private readonly double[] _leftRing = new double[RingCapacity];
    private readonly double[] _rightRing = new double[RingCapacity];
    private Complex[] _fftExact = new Complex[PerformanceFftAnalyzer.DefaultSize];
    private readonly Complex[] _iqScratch = new Complex[128];
    private readonly byte[] _iqGroups = new byte[128];
    private readonly double[] _iqPcmScratch = new double[PerformanceIqExtractor.CaptureSamples];
    private readonly Complex[] _iqTimeScratch = new Complex[PerformanceIqExtractor.FftSize];
    private readonly Complex[] _iqFftScratch = new Complex[PerformanceIqExtractor.FftSize];

    private RealtimePcmCapture? _capture;
    private CancellationTokenSource? _wavCts;
    private Task? _wavTask;
    private PerformanceRxSettings _settings;
    private long _writeTotal;
    private long _lastPublishMs = -1;
    private double _wowLeftEma;
    private double _wowRightEma;
    private double _wowLeftRefHz;
    private double _wowRightRefHz;
    private int _fftSize = PerformanceFftAnalyzer.DefaultSize;
    private PerformanceFftWindowKind _fftWindowKind = PerformanceFftWindowKind.Hanning;
    private bool _disposed;
    private bool _running;

    /// <summary>
    /// 受信可視化の共有状態です。
    /// </summary>
    public CoreExecutionStatusBoard SharedStatus => _status;

    /// <summary>
    /// 受信中かどうかです。
    /// </summary>
    public bool IsBusy
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    /// <summary>
    /// 受信中の FFT 解析サイズ／窓関数を更新します。
    /// </summary>
    /// <param name="fftSize">FFT 長（1024/2048/4096/8192）。</param>
    /// <param name="windowKind">窓関数。</param>
    public void UpdateFftAnalysis(int fftSize, PerformanceFftWindowKind windowKind)
    {
        lock (_sync)
        {
            _fftSize = PerformanceFftAnalyzer.ClampSize(fftSize);
            _fftWindowKind = PerformanceFftAnalyzer.ClampWindow(windowKind);
            if (_fftExact.Length != _fftSize)
            {
                _fftExact = new Complex[_fftSize];
            }
        }
    }

    /// <summary>
    /// 受信を開始します。
    /// </summary>
    /// <param name="settings">受信設定。</param>
    /// <returns>開始できたとき true。</returns>
    public bool TryStart(PerformanceRxSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_running)
            {
                return false;
            }

            StopLocked();
            _settings = settings;
            _writeTotal = 0;
            _lastPublishMs = -1;
            _wowLeftEma = 0;
            _wowRightEma = 0;
            _wowLeftRefHz = 0;
            _wowRightRefHz = 0;
            Array.Clear(_leftRing);
            Array.Clear(_rightRing);
            _status.BeginRun("性能測定受信");
            _status.SetAnalyzing(false);
            _status.SetWowFlutterPercent(0, 0);

            if (settings.UseWavInput)
            {
                if (string.IsNullOrWhiteSpace(settings.WavPath) || !File.Exists(settings.WavPath))
                {
                    _status.Complete(faulted: true, "WAV ファイルを指定してください。");
                    return false;
                }

                var cts = new CancellationTokenSource();
                _wavCts = cts;
                _running = true;
                _wavTask = Task.Run(() => RunWavAnalysis(settings.WavPath, cts.Token));
                return true;
            }

            var capture = new RealtimePcmCapture();
            capture.SamplesAvailable += OnSamplesAvailable;
            capture.CaptureFailed += OnCaptureFailed;
            try
            {
                capture.Start(
                    settings.InputDeviceNumber,
                    settings.ChannelMode,
                    PerformanceSignalGenerator.SampleRate,
                    settings.InputGain);
            }
            catch (Exception ex)
            {
                capture.Dispose();
                _status.Complete(faulted: true, ex.Message);
                return false;
            }

            _capture = capture;
            _running = true;
            return true;
        }
    }

    /// <summary>
    /// 受信を停止します。
    /// </summary>
    public void RequestStop()
    {
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            StopLocked();
            _status.Complete(faulted: false);
        }
    }

    /// <summary>
    /// 受信ワーカーを破棄し、キャプチャ／WAV 解析を停止します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            StopLocked();
        }
    }

    /// <summary>
    /// キャプチャ／WAV 解析を停止します（呼び出し元が _sync を保持）。
    /// </summary>
    private void StopLocked()
    {
        var capture = _capture;
        _capture = null;
        var cts = _wavCts;
        _wavCts = null;
        var wavTask = _wavTask;
        _wavTask = null;
        _running = false;

        if (capture is not null)
        {
            capture.SamplesAvailable -= OnSamplesAvailable;
            capture.CaptureFailed -= OnCaptureFailed;
            capture.Dispose();
        }

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // ignore
            }

            cts.Dispose();
        }

        // WAV タスクはロック外で待つ（デッドロック回避）
        if (wavTask is not null)
        {
            Monitor.Exit(_sync);
            try
            {
                wavTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore
            }
            finally
            {
                Monitor.Enter(_sync);
            }
        }
    }

    /// <summary>
    /// WAV をチャンク読みして可視化へ流します（ほぼリアルタイム速度）。
    /// </summary>
    /// <param name="wavPath">入力 WAV パス。</param>
    /// <param name="token">取消トークン。</param>
    private void RunWavAnalysis(string wavPath, CancellationToken token)
    {
        try
        {
            using var reader = WavPcmStreamReader.Open(wavPath);
            var chunkFrames = Math.Max(1, reader.SampleRate / 20); // 50ms
            var gain = Math.Clamp(_settings.InputGain, 0.0, 1.0);

            while (!token.IsCancellationRequested && reader.TryRead(chunkFrames, out var left, out var right))
            {
                if (gain is > 0.0 and < 1.0)
                {
                    ScaleInPlace(left, gain);
                    if (right.Length > 0)
                    {
                        ScaleInPlace(right, gain);
                    }
                }

                OnSamplesAvailable(left, right);
                // 可視化が追従できるよう、チャンク長に近い待ちを入れる
                var sleepMs = (int)Math.Round(1000.0 * left.Length / Math.Max(1, reader.SampleRate));
                if (sleepMs > 0)
                {
                    token.WaitHandle.WaitOne(sleepMs);
                }
            }

            lock (_sync)
            {
                if (_running && !token.IsCancellationRequested)
                {
                    // 自タスク待機を避けるため StopLocked は呼ばない
                    _wavCts = null;
                    _wavTask = null;
                    _running = false;
                    _status.Complete(faulted: false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stop
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _wavCts = null;
                _wavTask = null;
                _running = false;
                _status.Complete(faulted: true, ex.Message);
            }
        }
    }

    /// <summary>
    /// PCM 実部にゲインを掛けます。
    /// </summary>
    /// <param name="samples">PCM（破壊的）。</param>
    /// <param name="gain">ゲイン（0〜1）。</param>
    private static void ScaleInPlace(Complex[] samples, double gain)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = new Complex(samples[i].Real * gain, 0.0);
        }
    }

    /// <summary>
    /// キャプチャ失敗時に停止し状態を完了します。
    /// </summary>
    /// <param name="message">エラーメッセージ。</param>
    private void OnCaptureFailed(string message)
    {
        lock (_sync)
        {
            StopLocked();
            _status.Complete(faulted: true, message);
        }
    }

    /// <summary>
    /// 入力 PCM をリングへ書き、間引いて FFT／ワウ／I-Q を更新します。
    /// </summary>
    /// <param name="left">L チャンク。</param>
    /// <param name="right">R チャンク。</param>
    private void OnSamplesAvailable(Complex[] left, Complex[] right)
    {
        if (left.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            for (var i = 0; i < left.Length; i++)
            {
                var idx = (int)(_writeTotal % RingCapacity);
                _leftRing[idx] = left[i].Real;
                if (_settings.ChannelMode == ChannelMode.Stereo && right.Length > i)
                {
                    _rightRing[idx] = right[i].Real;
                }
                else
                {
                    _rightRing[idx] = left[i].Real;
                }

                _writeTotal++;
            }

            var now = Environment.TickCount64;
            if (_lastPublishMs >= 0 && now - _lastPublishMs < 80)
            {
                return;
            }

            if (_writeTotal < _fftSize)
            {
                return;
            }

            PublishAnalysisUnlocked();
            _lastPublishMs = now;
        }
    }

    /// <summary>
    /// FFT／ワウ／I-Q を共有ボードへ載せます（呼び出し元が _sync を保持）。
    /// </summary>
    private void PublishAnalysisUnlocked()
    {
        var fftSize = _fftSize;
        var windowKind = _fftWindowKind;
        if (_fftExact.Length != fftSize)
        {
            _fftExact = new Complex[fftSize];
        }

        FillWindow(_leftRing, _fftExact, fftSize);
        PerformanceFftAnalyzer.ComputeSpectrumInPlace(_fftExact, windowKind);
        _status.SetFftStereoMode(_settings.ChannelMode == ChannelMode.Stereo);
        _status.SetFftFrame(_fftExact, isRightChannel: false, PerformanceSignalGenerator.SampleRate);

        var leftPeakHz = FindPeakFrequencyHz(_fftExact, PerformanceSignalGenerator.SampleRate);
        var leftCandidates = PerformanceWowReference.ResolveCandidates(
            _settings.SignalMode,
            _settings.ActiveSubcarriers,
            useRightCarriers: false);
        _wowLeftEma = UpdateWowEma(_wowLeftEma, leftPeakHz, leftCandidates, ref _wowLeftRefHz);

        FillWindow(_rightRing, _fftExact, fftSize);
        PerformanceFftAnalyzer.ComputeSpectrumInPlace(_fftExact, windowKind);
        if (_settings.ChannelMode == ChannelMode.Stereo)
        {
            _status.SetFftFrame(_fftExact, isRightChannel: true, PerformanceSignalGenerator.SampleRate);
            var rightPeakHz = FindPeakFrequencyHz(_fftExact, PerformanceSignalGenerator.SampleRate);
            var rightCandidates = PerformanceWowReference.ResolveCandidates(
                _settings.SignalMode,
                _settings.ActiveSubcarriers,
                useRightCarriers: true);
            _wowRightEma = UpdateWowEma(_wowRightEma, rightPeakHz, rightCandidates, ref _wowRightRefHz);
        }
        else
        {
            _wowRightEma = _wowLeftEma;
            _wowRightRefHz = _wowLeftRefHz;
        }

        _status.SetWowFlutterPercent(_wowLeftEma, _wowRightEma);

        if (_settings.CaptureConstellation)
        {
            PublishIqFromCarriersUnlocked();
        }
    }

    /// <summary>
    /// リング末尾から等化 I-Q を抽出し共有ボードへ載せます。
    /// </summary>
    private void PublishIqFromCarriersUnlocked()
    {
        var sc = PerformanceSignalGenerator.ClampSubcarriers(_settings.ActiveSubcarriers);
        var mod = PerformanceSignalGenerator.ClampModulation(_settings.ModulationScheme);
        if (!CopyRingTail(_leftRing, _iqPcmScratch, out var pcmCount) || pcmCount < PerformanceIqExtractor.FftSize)
        {
            return;
        }

        var leftCount = PerformanceIqExtractor.ExtractEqualized(
            _iqPcmScratch.AsSpan(0, pcmCount),
            sc,
            useRightCarriers: false,
            _iqTimeScratch,
            _iqFftScratch,
            _iqScratch.AsSpan(),
            _iqGroups.AsSpan());
        var count = leftCount;

        if (_settings.ChannelMode == ChannelMode.Stereo
            && CopyRingTail(_rightRing, _iqPcmScratch, out pcmCount)
            && pcmCount >= PerformanceIqExtractor.FftSize)
        {
            var rightCount = PerformanceIqExtractor.ExtractEqualized(
                _iqPcmScratch.AsSpan(0, pcmCount),
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
    /// リング末尾を線形バッファへコピーします。
    /// </summary>
    /// <param name="ring">リング。</param>
    /// <param name="dest">出力。</param>
    /// <param name="count">コピーしたサンプル数。</param>
    /// <returns>コピーできたとき true。</returns>
    private bool CopyRingTail(double[] ring, double[] dest, out int count)
    {
        count = 0;
        var n = Math.Min(dest.Length, (int)Math.Min(_writeTotal, ring.Length));
        if (n <= 0)
        {
            return false;
        }

        PerformanceRingCopy.CopyTail(ring, _writeTotal, dest.AsSpan(0, n), n);
        count = n;
        return true;
    }

    /// <summary>
    /// リング末尾から FFT 窓を埋めます。
    /// </summary>
    /// <param name="ring">PCM リング。</param>
    /// <param name="destination">FFT 入力。</param>
    /// <param name="fftSize">窓長。</param>
    private void FillWindow(double[] ring, Complex[] destination, int fftSize)
    {
        PerformanceRingCopy.FillComplexWindow(ring, _writeTotal, destination, fftSize);
    }

    /// <summary>
    /// 直近の PCM 窓をオシロスコープ用にコピーします。
    /// </summary>
    /// <param name="left">左チャネル出力。</param>
    /// <param name="right">右チャネル出力。</param>
    /// <param name="count">有効サンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <returns>十分なサンプルがあれば true。</returns>
    public bool TryCopyLatestPcm(double[] left, double[] right, out int count, out int sampleRate)
    {
        count = 0;
        sampleRate = PerformanceSignalGenerator.SampleRate;
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        lock (_sync)
        {
            if (!_running || _writeTotal < OscilloscopeTrigger.MinDisplaySamples)
            {
                return false;
            }

            var n = Math.Min(RingCapacity, Math.Min(left.Length, right.Length));
            n = (int)Math.Min(n, _writeTotal);
            PerformanceRingCopy.CopyStereoTail(
                _leftRing,
                _rightRing,
                _writeTotal,
                left.AsSpan(0, n),
                right.AsSpan(0, n),
                n,
                copyRightFromLeft: false);
            count = n;
            return true;
        }
    }

    /// <summary>
    /// 振幅ピークビンを放物線補間して周波数（Hz）を返します。
    /// </summary>
    /// <param name="bins">片側スペクトル。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <returns>ピーク周波数（Hz）。無効時は 0。</returns>
    private static double FindPeakFrequencyHz(Complex[] bins, int sampleRate)
    {
        var half = bins.Length / 2;
        var bestMagSq = -1.0;
        var bestBin = 1;
        var last = half - 1;
        for (var bin = 1; bin < last; bin++)
        {
            var re = bins[bin].Real;
            var im = bins[bin].Imaginary;
            var magSq = (re * re) + (im * im);
            if (magSq > bestMagSq)
            {
                bestMagSq = magSq;
                bestBin = bin;
            }
        }

        if (bestMagSq < 1e-18)
        {
            return 0;
        }

        var leftMag = bins[bestBin - 1].Magnitude;
        var peakMag = bins[bestBin].Magnitude;
        var rightMag = bins[bestBin + 1].Magnitude;
        var delta = PerformanceWowReference.InterpolatePeakOffset(leftMag, peakMag, rightMag);
        return (bestBin + delta) * (sampleRate / (double)bins.Length);
    }

    /// <summary>
    /// 送信側周波数へロックしたワウ（%）を指数平均します。
    /// </summary>
    /// <param name="current">現在の EMA 値（%）。</param>
    /// <param name="measuredHz">測定ピーク（Hz）。</param>
    /// <param name="candidates">送信側周波数候補。</param>
    /// <param name="lockedRefHz">ロック中の基準周波数（未ロックは 0）。</param>
    /// <returns>更新後のワウ EMA（%）。</returns>
    private static double UpdateWowEma(
        double current,
        double measuredHz,
        ReadOnlySpan<double> candidates,
        ref double lockedRefHz)
    {
        if (!PerformanceWowReference.TryLock(measuredHz, candidates, ref lockedRefHz))
        {
            return current;
        }

        var percent = PerformanceWowReference.ToWowPercent(measuredHz, lockedRefHz);
        const double alpha = 0.25;
        return (current * (1.0 - alpha)) + (percent * alpha);
    }
}
