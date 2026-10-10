using System.Numerics;
using Onta.Core;
using Onta.View.Core;

namespace Onta.Performance;

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
    private readonly HeldIqPoints _iqHeldLeft = new(128);
    private readonly HeldIqPoints _iqHeldRight = new(128);
    private readonly double[] _iqPcmScratch = new double[PerformanceIqExtractor.SymbolLength * 8];
    private readonly double[] _iqDeviceScratch = new double[8192];
    private readonly Complex[] _iqTimeScratch = new Complex[PerformanceIqExtractor.FftSize];
    private readonly Complex[] _iqFftScratch = new Complex[PerformanceIqExtractor.FftSize];
    private readonly double[] _wowPcm = new double[PerformanceFftAnalyzer.MaxSize];
    private readonly double[] _wowWindowed = new double[PerformanceFftAnalyzer.MaxSize];

    private RealtimePcmCapture? _capture;
    private int _sampleRate = PerformanceConstants.SampleRate;
    private CancellationTokenSource? _wavCts;
    private Task? _wavTask;
    private PerformanceRxSettings _settings;
    private long _writeTotal;
    private long _lastPublishMs = -1;
    private readonly WowChannelState _wowLeft = new();
    private readonly WowChannelState _wowRight = new();
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
    /// 直近の解析のワウ表示状態（測定できたか・ロック中の基準周波数）を L/R 別に返します。
    /// </summary>
    /// <param name="left">L の状態。</param>
    /// <param name="right">R の状態（モノラルは L と同じ）。</param>
    public void GetWowView(out PerformanceWowChannelView left, out PerformanceWowChannelView right)
    {
        lock (_sync)
        {
            left = _wowLeft.ToView(_running);
            right = _wowRight.ToView(_running);
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
            _wowLeft.Reset();
            _wowRight.Reset();
            _iqHeldLeft.Clear();
            _iqHeldRight.Clear();
            Array.Clear(_leftRing);
            Array.Clear(_rightRing);
            _status.BeginRun(CoreText.PerformanceRxRun);
            _status.SetAnalyzing(false);
            _status.SetWowFlutterPercent(0, 0);

            if (settings.UseWavInput)
            {
                if (string.IsNullOrWhiteSpace(settings.WavPath) || !File.Exists(settings.WavPath))
                {
                    _status.Complete(faulted: true, CoreText.SpecifyWavFile);
                    return false;
                }

                var cts = new CancellationTokenSource();
                _wavCts = cts;
                _running = true;
                // WAV の長さだけ実時間ペースで待つループなので専用スレッドで回す（スレッドプールが埋まっていると開始が数十秒遅れる）
                _wavTask = Task.Factory.StartNew(
                    () => RunWavAnalysis(settings.WavPath, cts.Token),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                return true;
            }

            var capture = new RealtimePcmCapture();
            capture.SamplesAvailable += OnSamplesAvailable;
            capture.CaptureFailed += OnCaptureFailed;
            var deviceRate = AudioDeviceSampleRate.ResolveCapture(
                settings.InputDeviceNumber,
                PerformanceConstants.SampleRate);
            try
            {
                capture.Start(
                    settings.InputDeviceNumber,
                    settings.ChannelMode,
                    deviceRate,
                    settings.InputGain);
                _sampleRate = deviceRate;
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
    /// 受信中の入力ゲインを変更します。音声入力の次のバッファから反映されます。
    /// </summary>
    /// <param name="inputGain">入力ゲイン（0〜1）。</param>
    public void SetInputGain(double inputGain)
    {
        var gain = Math.Clamp(inputGain, 0.0, 1.0);
        lock (_sync)
        {
            _settings = _settings with { InputGain = gain };
            _capture?.SetInputGain(gain);
        }
    }

    /// <summary>
    /// 受信中の復調設定を変更します。次の解析から反映し、I-Q とワウはいったんリセットします。
    /// </summary>
    /// <param name="captureConstellation">変調波として I-Q を出すか。</param>
    /// <param name="signalMode">ワウ基準の選び方。</param>
    /// <param name="activeSubcarriers">サブキャリア数。</param>
    /// <param name="modulationScheme">変調方式。</param>
    public void UpdateDemodulation(
        bool captureConstellation,
        PerformanceSignalMode signalMode,
        int activeSubcarriers,
        ModulationScheme modulationScheme)
    {
        lock (_sync)
        {
            _settings = _settings with
            {
                CaptureConstellation = captureConstellation,
                SignalMode = signalMode,
                ActiveSubcarriers = activeSubcarriers,
                ModulationScheme = modulationScheme
            };
            if (!_running)
            {
                return;
            }

            // 前の設定の点・ロック先が残らないようにする。
            _wowLeft.Reset();
            _wowRight.Reset();
            _iqHeldLeft.Clear();
            _iqHeldRight.Clear();
            _status.SetWowFlutterPercent(0, 0);
            _status.BeginIqCapture(
                PerformanceSignalGenerator.ClampSubcarriers(activeSubcarriers),
                PerformanceSignalGenerator.ClampModulation(modulationScheme));
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
            _sampleRate = Math.Max(1, reader.SampleRate);
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
        var sampleRate = Math.Max(1, _sampleRate);
        _status.SetFftFrame(_fftExact, isRightChannel: false, sampleRate);

        UpdateWowChannel(_wowLeft, _leftRing, _fftExact, sampleRate, useRightCarriers: false);

        FillWindow(_rightRing, _fftExact, fftSize);
        PerformanceFftAnalyzer.ComputeSpectrumInPlace(_fftExact, windowKind);
        if (_settings.ChannelMode == ChannelMode.Stereo)
        {
            _status.SetFftFrame(_fftExact, isRightChannel: true, sampleRate);
            UpdateWowChannel(_wowRight, _rightRing, _fftExact, sampleRate, useRightCarriers: true);
        }
        else
        {
            _wowRight.CopyFrom(_wowLeft);
        }

        _status.SetWowFlutterPercent(_wowLeft.Ema, _wowRight.Ema);

        if (_settings.CaptureConstellation)
        {
            PublishIqFromCarriersUnlocked();
        }
    }

    /// <summary>
    /// リング末尾から等化 I-Q を抽出し共有ボードへ載せます。
    /// 信号が区間全体に続いていないチャネルは直前の点を載せ直し、L/R とも無ければボードを更新しません（表示が止まる）。
    /// </summary>
    private void PublishIqFromCarriersUnlocked()
    {
        // 音声入力は変調クロック（PerformanceSignalGenerator.SampleRate）で生成した信号をデバイスレートで録る。WAV はそのファイルのレートが変調クロック。
        var alignToSynthesis = _capture is not null
            && _sampleRate > 0
            && _sampleRate != PerformanceSignalGenerator.SampleRate;
        var modulationClock = alignToSynthesis ? PerformanceSignalGenerator.SampleRate : _sampleRate;
        var sc = PerformanceSignalGenerator.ClampSubcarriers(_settings.ActiveSubcarriers);
        var mod = PerformanceSignalGenerator.ClampModulation(_settings.ModulationScheme);

        var leftCount = FillIqChannelUnlocked(
            _leftRing, alignToSynthesis, sc, useRightCarriers: false, modulationClock, offset: 0, _iqHeldLeft, out var leftFresh);
        var count = leftCount;
        var rightFresh = false;
        if (_settings.ChannelMode == ChannelMode.Stereo)
        {
            count += FillIqChannelUnlocked(
                _rightRing, alignToSynthesis, sc, useRightCarriers: true, modulationClock, offset: leftCount, _iqHeldRight, out rightFresh);
        }

        if (count <= 0 || (!leftFresh && !rightFresh))
        {
            return;
        }

        _status.BeginIqCapture(sc, mod);
        _status.AppendIqFrame(_iqScratch.AsSpan(0, count), _iqGroups.AsSpan(0, count));
        _status.SetIqLeftPointCount(leftCount);
    }

    /// <summary>
    /// 1 チャネル分の等化 I-Q を <see cref="_iqScratch"/> の指定位置へ書きます。
    /// 区間全体に信号が続いていれば抽出して保持し直し、そうでなければ（低レベル・信号の出始めや止まり際）直前に保持した点を書きます。
    /// 低レベルの雑音も等化で振幅がそろえられ、点がグラフ全体に散らばるため。
    /// </summary>
    /// <param name="ring">そのチャネルの PCM リング。</param>
    /// <param name="alignToSynthesis">変調クロックへ戻すか。</param>
    /// <param name="sc">サブキャリア数。</param>
    /// <param name="useRightCarriers">R 搬送波を使うか。</param>
    /// <param name="modulationClock">変調クロック（Hz）。</param>
    /// <param name="offset"><see cref="_iqScratch"/> の書き込み開始位置。</param>
    /// <param name="held">そのチャネルの保持点。</param>
    /// <param name="fresh">今回の PCM から抽出したとき true。</param>
    /// <returns>書いた点数（保持点も無ければ 0）。</returns>
    private int FillIqChannelUnlocked(
        double[] ring,
        bool alignToSynthesis,
        int sc,
        bool useRightCarriers,
        int modulationClock,
        int offset,
        HeldIqPoints held,
        out bool fresh)
    {
        fresh = false;
        var dest = _iqScratch.AsSpan(offset);
        var groups = _iqGroups.AsSpan(offset);
        if (TryReadIqPcmUnlocked(ring, alignToSynthesis, out var pcm)
            && PerformanceSignalLevel.IsSteady(pcm.Span))
        {
            var count = PerformanceIqExtractor.ExtractEqualized(
                pcm.Span,
                sc,
                useRightCarriers,
                _iqTimeScratch,
                _iqFftScratch,
                dest,
                groups,
                sampleRate: modulationClock);
            if (count > 0)
            {
                held.Store(dest[..count], groups[..count]);
                fresh = true;
                return count;
            }
        }

        return held.CopyTo(dest, groups);
    }

    /// <summary>
    /// リング末尾を I-Q 用 PCM として取り出します。音声入力で <see cref="PerformanceSignalGenerator.SampleRate"/> 以外なら変調クロックへ戻します。
    /// </summary>
    /// <param name="ring">PCM リング。</param>
    /// <param name="alignToSynthesis"><see cref="PerformanceSignalGenerator.SampleRate"/> へ戻すか。</param>
    /// <param name="pcm">変調クロックの PCM。</param>
    /// <returns>1 シンボル以上取り出せたとき true。</returns>
    private bool TryReadIqPcmUnlocked(double[] ring, bool alignToSynthesis, out ReadOnlyMemory<double> pcm)
    {
        pcm = ReadOnlyMemory<double>.Empty;
        var raw = alignToSynthesis ? _iqDeviceScratch : _iqPcmScratch;
        if (!CopyRingTail(ring, raw, out var count) || count < PerformanceIqExtractor.FftSize)
        {
            return false;
        }

        if (!alignToSynthesis)
        {
            pcm = raw.AsMemory(0, count);
            return true;
        }

        var aligned = PerformanceIqExtractor.AlignToSynthesisRate(raw.AsSpan(0, count), _sampleRate);
        pcm = aligned;
        return aligned.Length >= PerformanceIqExtractor.FftSize;
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
        sampleRate = Math.Max(1, _sampleRate);
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
    /// 1 チャネルのワウを更新します。FFT と同じ区間の全体に信号が続いていなければ（無音・信号の始まりや終わり）値を据え置き、ロックを外します。
    /// </summary>
    /// <param name="state">そのチャネルのワウ状態。</param>
    /// <param name="ring">FFT と同じチャネルの PCM リング。</param>
    /// <param name="bins">そのチャネルの FFT 結果。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <param name="useRightCarriers">変調の基準に R のキャリアを使うか。</param>
    private void UpdateWowChannel(
        WowChannelState state,
        double[] ring,
        Complex[] bins,
        int sampleRate,
        bool useRightCarriers)
    {
        var n = bins.Length;
        var pcm = _wowPcm.AsSpan(0, n);
        PerformanceRingCopy.CopyTail(ring, _writeTotal, pcm, n);
        if (!PerformanceSignalLevel.IsSteady(pcm))
        {
            state.RefHz = 0;
            state.Active = false;
            return;
        }

        var kind = ResolveWowReferenceKind(bins, sampleRate);
        if (kind != state.Kind)
        {
            state.Kind = kind;
            state.RefHz = 0;
        }

        var candidates = kind switch
        {
            PerformanceWowReferenceKind.Tone => PerformanceWowReference.ToneFrequenciesHz,
            PerformanceWowReferenceKind.Carrier => PerformanceWowReference.ResolveCandidates(
                PerformanceSignalMode.Modulated,
                _settings.ActiveSubcarriers,
                useRightCarriers),
            _ => []
        };
        var measuredHz = MeasureWowPeakHz(pcm, bins, sampleRate, candidates.Length > 0);
        state.Ema = UpdateWowEma(state.Ema, measuredHz, candidates, ref state.RefHz);
        state.Active = true;
    }

    /// <summary>
    /// ワウの基準を選びます。受信パネルの「変調」が ON でも、受信信号が単一トーンならトーン一覧を基準にします。
    /// </summary>
    /// <param name="bins">そのチャネルの FFT 結果。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <returns>基準の種類。</returns>
    private PerformanceWowReferenceKind ResolveWowReferenceKind(Complex[] bins, int sampleRate) =>
        _settings.SignalMode switch
        {
            PerformanceSignalMode.Tone => PerformanceWowReferenceKind.Tone,
            PerformanceSignalMode.Modulated => PerformanceWowReference.IsSingleTone(bins, sampleRate)
                ? PerformanceWowReferenceKind.Tone
                : PerformanceWowReferenceKind.Carrier,
            _ => PerformanceWowReferenceKind.None
        };

    /// <summary>
    /// ワウ算出用のピーク周波数を求めます。FFT のピークを粗い値とし、同じ区間の PCM で周波数を精密化します。
    /// </summary>
    /// <param name="pcm">FFT と同じ区間の PCM。</param>
    /// <param name="bins">そのチャネルの FFT 結果。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <param name="refine">精密化するか（ワウの基準周波数が無いスイープ等では省く）。</param>
    /// <returns>ピーク周波数（Hz）。無効時は 0。</returns>
    private double MeasureWowPeakHz(ReadOnlySpan<double> pcm, Complex[] bins, int sampleRate, bool refine)
    {
        var coarseHz = FindPeakFrequencyHz(bins, sampleRate);
        if (!refine || coarseHz <= 0)
        {
            return coarseHz;
        }

        return PerformanceWowReference.RefinePeakFrequencyHz(
            pcm, sampleRate, coarseHz, sampleRate / (double)bins.Length, _wowWindowed);
    }

    /// <summary>
    /// 送信側周波数へロックしたワウ（%）を指数平均します。ロックし直した直後は測定値から始めます。
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
        var wasLocked = lockedRefHz > 1.0;
        if (!PerformanceWowReference.TryLock(measuredHz, candidates, ref lockedRefHz))
        {
            return current;
        }

        var percent = PerformanceWowReference.ToWowPercent(measuredHz, lockedRefHz);
        if (!wasLocked)
        {
            return percent;
        }

        const double alpha = 0.25;
        return (current * (1.0 - alpha)) + (percent * alpha);
    }

    /// <summary>
    /// 1 チャネル分のワウ測定状態です（_sync の内側でだけ触る）。
    /// </summary>
    private sealed class WowChannelState
    {
        /// <summary>ワウ EMA（%）。</summary>
        public double Ema;

        /// <summary>ロック中の基準周波数（Hz。未ロックは 0）。</summary>
        public double RefHz;

        /// <summary>直近の解析で測定したか（信号が続いていたか）。</summary>
        public bool Active;

        /// <summary>直近の基準の種類。</summary>
        public PerformanceWowReferenceKind Kind;

        /// <summary>
        /// 受信開始・設定変更時の初期状態へ戻します。
        /// </summary>
        public void Reset()
        {
            Ema = 0;
            RefHz = 0;
            Active = false;
            Kind = PerformanceWowReferenceKind.None;
        }

        /// <summary>
        /// 別チャネルの状態を写します（モノラル時に R を L と同じにする）。
        /// </summary>
        /// <param name="other">写し元。</param>
        public void CopyFrom(WowChannelState other)
        {
            Ema = other.Ema;
            RefHz = other.RefHz;
            Active = other.Active;
            Kind = other.Kind;
        }

        /// <summary>
        /// 画面表示用の状態を返します。
        /// </summary>
        /// <param name="running">受信中か。</param>
        /// <returns>表示用の状態。</returns>
        public PerformanceWowChannelView ToView(bool running) =>
            new(running && Active, RefHz > 1.0 ? RefHz : 0, Kind);
    }

    /// <summary>
    /// 1 チャネル分の直近に表示した I-Q 点です（_sync の内側でだけ触る）。
    /// </summary>
    private sealed class HeldIqPoints
    {
        private readonly Complex[] _points;
        private readonly byte[] _groups;
        private int _count;

        /// <summary>
        /// 指定点数まで保持できる領域を作ります。
        /// </summary>
        /// <param name="capacity">保持できる最大点数。</param>
        public HeldIqPoints(int capacity)
        {
            _points = new Complex[capacity];
            _groups = new byte[capacity];
        }

        /// <summary>
        /// 保持点を捨てます（受信開始・設定変更時）。
        /// </summary>
        public void Clear()
        {
            _count = 0;
        }

        /// <summary>
        /// 点とグループを保持し直します。
        /// </summary>
        /// <param name="points">等化後の点。</param>
        /// <param name="groups">各点のサブキャリアグループ。</param>
        public void Store(ReadOnlySpan<Complex> points, ReadOnlySpan<byte> groups)
        {
            _count = Math.Min(points.Length, _points.Length);
            points[.._count].CopyTo(_points);
            groups[.._count].CopyTo(_groups);
        }

        /// <summary>
        /// 保持点を書き出します。
        /// </summary>
        /// <param name="points">点の書き込み先。</param>
        /// <param name="groups">グループの書き込み先。</param>
        /// <returns>書いた点数。</returns>
        public int CopyTo(Span<Complex> points, Span<byte> groups)
        {
            var n = Math.Min(_count, Math.Min(points.Length, groups.Length));
            _points.AsSpan(0, n).CopyTo(points);
            _groups.AsSpan(0, n).CopyTo(groups);
            return n;
        }
    }
}

/// <summary>
/// 性能測定ワウの基準の種類です。
/// </summary>
internal enum PerformanceWowReferenceKind : byte
{
    /// <summary>基準なし（スイープ・ホワイトノイズ）。</summary>
    None = 0,

    /// <summary>無変調トーン一覧（315Hz〜20kHz）。</summary>
    Tone = 1,

    /// <summary>選択中サブキャリアの OFDM キャリア。</summary>
    Carrier = 2
}

/// <summary>
/// 性能測定ワウの 1 チャネル分の表示状態です。
/// </summary>
/// <param name="Active">直近の解析で測定したか（無音・信号の切れ目なら false）。</param>
/// <param name="ReferenceHz">ロック中の基準周波数（Hz。未ロックは 0）。</param>
/// <param name="Kind">基準の種類。</param>
internal readonly record struct PerformanceWowChannelView(
    bool Active,
    double ReferenceHz,
    PerformanceWowReferenceKind Kind);
