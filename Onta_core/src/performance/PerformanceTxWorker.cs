using System.Numerics;
using Onta.Core;
using Onta.View.Core;

namespace Onta.Performance;

/// <summary>
/// 性能測定の送信（トーン／スイープ／ホワイトノイズ／OFDM）をバックグラウンドで実行します。
/// </summary>
internal sealed class PerformanceTxWorker : IDisposable
{
    private readonly object _sync = new();
    private readonly CoreExecutionStatusBoard _vizStatus = new();
    private readonly double[] _pcmLeft = new double[PerformanceConstants.ScopeCaptureSamples];
    private readonly double[] _pcmRight = new double[PerformanceConstants.ScopeCaptureSamples];
    private readonly Complex[] _iqScratch = new Complex[128];
    private readonly byte[] _iqGroups = new byte[128];
    private readonly double[] _iqPcmScratch = new double[PerformanceIqExtractor.CaptureSamples];
    private readonly Complex[] _iqTimeScratch = new Complex[PerformanceIqExtractor.FftSize];
    private readonly Complex[] _iqFftScratch = new Complex[PerformanceIqExtractor.FftSize];
    private readonly Random _noiseRng = new();
    private Complex[]? _fftExact;
    private Task? _worker;
    private CancellationTokenSource? _cts;
    private long _pcmWriteTotal;
    private long _lastFftPublishMs = -1;
    private bool _pcmStereo;
    private bool _disposed;

    /// <summary>音声出力で繰り返す変調信号の最低長（秒）。</summary>
    private const double ModulatedLoopSeconds = 4.0;

    private PerformanceSignalMode _liveMode;
    private double _liveToneHz = 315.0;
    private double _liveAmplitude = 0.8;
    private int _liveSubcarriers = 16;
    private ModulationScheme _liveModulation = ModulationScheme.Bpsk;
    private int _fftSize = PerformanceFftAnalyzer.DefaultSize;
    private PerformanceFftWindowKind _fftWindowKind = PerformanceFftWindowKind.Hanning;
    private bool _flushPlayback;
    private RealtimePcmPlayer? _activePlayer;
    private WhiteNoiseBandFilter _noiseFilterLeft = WhiteNoiseBandFilter.Create(PerformanceSignalGenerator.SampleRate);
    private WhiteNoiseBandFilter _noiseFilterRight = WhiteNoiseBandFilter.Create(PerformanceSignalGenerator.SampleRate);

    /// <summary>
    /// 送信中の FFT 可視化用共有状態です。
    /// </summary>
    public CoreExecutionStatusBoard SharedVizStatus => _vizStatus;

    /// <summary>
    /// 送信中かどうかです。
    /// </summary>
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
    /// 完了結果を取り出します（1 回だけ）。
    /// </summary>
    /// <param name="success">成功なら true。</param>
    /// <param name="message">完了／エラーメッセージ。</param>
    /// <returns>結果があれば true。</returns>
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

    private (bool Success, string Message)? _completion;

    /// <summary>
    /// 送信を開始します。
    /// </summary>
    /// <param name="settings">送信設定。</param>
    /// <returns>開始できたとき true。</returns>
    public bool TryStart(PerformanceTxSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!settings.WriteWav && !settings.PlayAudio)
        {
            return false;
        }

        lock (_sync)
        {
            if (_worker is { IsCompleted: false })
            {
                return false;
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _completion = null;
            _pcmWriteTotal = 0;
            _lastFftPublishMs = -1;
            _pcmStereo = settings.ChannelMode == ChannelMode.Stereo;
            _liveMode = settings.SignalMode;
            _liveToneHz = settings.ToneHz > 0 ? settings.ToneHz : 315.0;
            _liveAmplitude = Math.Clamp(settings.SignalAmplitude, 0.10, 1.0);
            _liveSubcarriers = PerformanceSignalGenerator.ClampSubcarriers(settings.ActiveSubcarriers);
            _liveModulation = PerformanceSignalGenerator.ClampModulation(settings.ModulationScheme);
            _flushPlayback = false;
            _noiseFilterLeft = WhiteNoiseBandFilter.Create(PerformanceSignalGenerator.SampleRate);
            _noiseFilterRight = WhiteNoiseBandFilter.Create(PerformanceSignalGenerator.SampleRate);
            Array.Clear(_pcmLeft);
            Array.Clear(_pcmRight);
            _vizStatus.BeginRun("性能測定送信");
            _worker = Task.Factory.StartNew(
                () => Run(settings, token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return true;
        }
    }

    /// <summary>
    /// 再生中の FFT 解析サイズ／窓関数を更新します。
    /// </summary>
    /// <param name="fftSize">FFT 長（1024/2048/4096/8192）。</param>
    /// <param name="windowKind">窓関数。</param>
    public void UpdateFftAnalysis(int fftSize, PerformanceFftWindowKind windowKind)
    {
        lock (_sync)
        {
            _fftSize = PerformanceFftAnalyzer.ClampSize(fftSize);
            _fftWindowKind = PerformanceFftAnalyzer.ClampWindow(windowKind);
        }
    }

    /// <summary>
    /// 再生中の出力音量を変更します。未再生分は捨て、次のチャンクから新しい音量で出します。
    /// </summary>
    /// <param name="volume">出力音量（0〜1）。</param>
    public void SetOutputVolume(double volume)
    {
        RealtimePcmPlayer? player;
        lock (_sync)
        {
            player = _activePlayer;
        }

        if (player is null)
        {
            return;
        }

        player.SetOutputVolume(volume);
        player.ClearQueuedSamples();
    }

    /// <summary>
    /// 再生中の信号（基準信号／変調、周波数・レベル、変調時のサブキャリア数・変調方式）を更新します（UI スレッドから呼び出し可）。
    /// 値が変わった場合は再生キューを破棄して即反映します。
    /// </summary>
    /// <param name="mode">トーン／スイープ／ホワイトノイズ／変調。</param>
    /// <param name="toneHz">トーン周波数（Hz）。トーン以外では無視。</param>
    /// <param name="amplitude">信号振幅（0.1〜1）。</param>
    /// <param name="activeSubcarriers">変調時のサブキャリア数。</param>
    /// <param name="modulation">変調時の変調方式。</param>
    public void UpdateLiveSignal(
        PerformanceSignalMode mode,
        double toneHz,
        double amplitude,
        int activeSubcarriers,
        ModulationScheme modulation)
    {
        RealtimePcmPlayer? playerToFlush = null;
        lock (_sync)
        {
            var nextAmp = Math.Clamp(amplitude, 0.10, 1.0);
            var nextHz = toneHz > 1.0 ? toneHz : _liveToneHz;
            var nextSc = PerformanceSignalGenerator.ClampSubcarriers(activeSubcarriers);
            var nextMod = PerformanceSignalGenerator.ClampModulation(modulation);
            var changed = mode != _liveMode
                || Math.Abs(nextAmp - _liveAmplitude) > 1e-6
                || (mode == PerformanceSignalMode.Tone && Math.Abs(nextHz - _liveToneHz) > 1e-6)
                || (mode == PerformanceSignalMode.Modulated && (nextSc != _liveSubcarriers || nextMod != _liveModulation));

            _liveMode = mode;
            if (toneHz > 1.0)
            {
                _liveToneHz = toneHz;
            }

            _liveAmplitude = nextAmp;
            _liveSubcarriers = nextSc;
            _liveModulation = nextMod;
            if (changed)
            {
                _vizStatus.BeginIqCapture(nextSc, nextMod);
                _flushPlayback = true;
                Array.Clear(_pcmLeft);
                Array.Clear(_pcmRight);
                _pcmWriteTotal = 0;
                _lastFftPublishMs = -1;
                _noiseFilterLeft = WhiteNoiseBandFilter.Create(PerformanceSignalGenerator.SampleRate);
                _noiseFilterRight = WhiteNoiseBandFilter.Create(PerformanceSignalGenerator.SampleRate);
                playerToFlush = _activePlayer;
            }
        }

        // UI スレッドから即クリア（ワーカーが AddSamples 待ち中でも未再生分を捨てる）
        playerToFlush?.ClearQueuedSamples();
    }

    /// <summary>
    /// パラメータ変更フラグを消費します（可視化リングは UpdateLiveSignal 側で消去済み）。
    /// </summary>
    /// <param name="player">再生プレイヤー。</param>
    /// <returns>フラッシュを実行したら true。</returns>
    private bool ConsumeFlushRequest(RealtimePcmPlayer player)
    {
        lock (_sync)
        {
            if (!_flushPlayback)
            {
                return false;
            }

            _flushPlayback = false;
        }

        player.ClearQueuedSamples();
        return true;
    }

    /// <summary>
    /// 送信を中断します。
    /// </summary>
    public void RequestStop()
    {
        lock (_sync)
        {
            _cts?.Cancel();
        }
    }

    /// <summary>
    /// 送信ワーカーを破棄し、実行中の送信を停止して CancellationTokenSource を解放します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RequestStop();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore
        }

        _cts?.Dispose();
    }

    /// <summary>
    /// 送信ワーカー本体（WAV 出力／音声再生）です。
    /// </summary>
    /// <param name="settings">送信設定。</param>
    /// <param name="token">取消トークン。</param>
    private void Run(PerformanceTxSettings settings, CancellationToken token)
    {
        RealtimePcmPlayer? player = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (settings.WriteWav)
            {
                var (left, right) = Generate(settings);
                token.ThrowIfCancellationRequested();
                var path = string.IsNullOrWhiteSpace(settings.WavPath)
                    ? Path.Combine(AppPaths.OutputDir, $"perf_{DateTime.Now:yyyyMMdd_HHmmss}.wav")
                    : settings.WavPath;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                WavWriter.WritePcm16(
                    path,
                    PerformanceConstants.NormalizeWavSampleRate(settings.WavSampleRate),
                    left,
                    right,
                    peakTarget: Math.Clamp(settings.SignalAmplitude, 0.05, 1.0),
                    channelMode: settings.ChannelMode);
            }

            if (settings.PlayAudio)
            {
                player = new RealtimePcmPlayer();
                lock (_sync)
                {
                    _activePlayer = player;
                }

                player.Start(
                    settings.AudioDeviceNumber,
                    PerformanceSignalGenerator.SampleRate,
                    settings.ChannelMode,
                    settings.OutputVolume);

                PlayLive(settings, player, token);
                player.FinishAndWait(TimeSpan.FromSeconds(Math.Max(2.0, settings.DurationSeconds * 0.1)));
            }

            _vizStatus.Complete(faulted: false);
            lock (_sync)
            {
                _completion = (true, "送信完了");
            }
        }
        catch (OperationCanceledException)
        {
            _vizStatus.Complete(faulted: true, "送信を中断しました。");
            lock (_sync)
            {
                _completion = (false, "送信を中断しました。");
            }
        }
        catch (Exception ex)
        {
            _vizStatus.Complete(faulted: true, ex.Message);
            lock (_sync)
            {
                _completion = (false, ex.Message);
            }
        }
        finally
        {
            lock (_sync)
            {
                _activePlayer = null;
            }

            player?.Dispose();
        }
    }

    /// <summary>
    /// トーン／スイープ／ホワイトノイズ／変調をチャンク生成しながら再生します（信号の種類・周波数・レベル・SC・変調方式をライブ反映）。
    /// </summary>
    /// <remarks>変調は約 4 秒の乱数ペイロード信号を繰り返し、SC・変調方式が変わったら作り直す。</remarks>
    /// <param name="settings">送信設定。</param>
    /// <param name="player">再生プレイヤー。</param>
    /// <param name="token">取消トークン。</param>
    private void PlayLive(
        PerformanceTxSettings settings,
        RealtimePcmPlayer player,
        CancellationToken token)
    {
        const int chunk = 4096;
        var total = Math.Max(1, (int)Math.Round(settings.DurationSeconds * PerformanceSignalGenerator.SampleRate));
        var pcmCap = PerformanceConstants.ScopeCaptureSamples;
        var leftChunk = new Complex[chunk];
        var rightChunk = settings.ChannelMode == ChannelMode.Stereo ? new Complex[chunk] : Array.Empty<Complex>();
        var phase = 0.0;
        var sampleIndex = 0;
        Complex[]? loopLeft = null;
        var loopRight = Array.Empty<Complex>();
        var loopSubcarriers = 0;
        var loopModulation = ModulationScheme.Bpsk;
        var loopOffset = 0;

        while (sampleIndex < total)
        {
            token.ThrowIfCancellationRequested();
            PerformanceSignalMode mode;
            double toneHz;
            double amp;
            int subcarriers;
            ModulationScheme modulation;
            lock (_sync)
            {
                mode = _liveMode;
                toneHz = _liveToneHz;
                amp = _liveAmplitude;
                subcarriers = _liveSubcarriers;
                modulation = _liveModulation;
            }

            ConsumeFlushRequest(player);

            var len = Math.Min(chunk, total - sampleIndex);
            var dest = leftChunk.AsSpan(0, len);
            var modulated = mode == PerformanceSignalMode.Modulated;
            if (modulated)
            {
                if (loopLeft is null || loopSubcarriers != subcarriers || loopModulation != modulation)
                {
                    (loopLeft, loopRight) = PerformanceSignalGenerator.GenerateModulatedLoop(
                        subcarriers,
                        modulation,
                        settings.ChannelMode,
                        ModulatedLoopSeconds);
                    loopSubcarriers = subcarriers;
                    loopModulation = modulation;
                    loopOffset = 0;
                }

                CopyLoop(loopLeft, loopOffset, dest, amp);
                if (settings.ChannelMode == ChannelMode.Stereo)
                {
                    CopyLoop(loopRight, loopOffset, rightChunk.AsSpan(0, len), amp);
                }

                loopOffset = (loopOffset + len) % loopLeft.Length;
            }
            else if (mode == PerformanceSignalMode.Sweep)
            {
                PerformanceSignalGenerator.FillLogSweepChunk(
                    dest,
                    20.0,
                    20000.0,
                    total,
                    sampleIndex,
                    amp,
                    ref phase);
                if (settings.ChannelMode == ChannelMode.Stereo)
                {
                    dest.CopyTo(rightChunk.AsSpan(0, len));
                }
            }
            else if (mode == PerformanceSignalMode.WhiteNoise)
            {
                PerformanceSignalGenerator.FillWhiteNoiseChunk(dest, amp, _noiseRng, ref _noiseFilterLeft);
                if (settings.ChannelMode == ChannelMode.Stereo)
                {
                    PerformanceSignalGenerator.FillWhiteNoiseChunk(
                        rightChunk.AsSpan(0, len),
                        amp,
                        _noiseRng,
                        ref _noiseFilterRight);
                }
            }
            else
            {
                PerformanceSignalGenerator.FillToneChunk(dest, toneHz, amp, ref phase);
                if (settings.ChannelMode == ChannelMode.Stereo)
                {
                    dest.CopyTo(rightChunk.AsSpan(0, len));
                }
            }

            var leftSlice = leftChunk.AsSpan(0, len);
            var rightSlice = settings.ChannelMode == ChannelMode.Stereo
                ? rightChunk.AsSpan(0, len)
                : ReadOnlySpan<Complex>.Empty;
            player.AddSamples(leftSlice, rightSlice);
            PublishPcmAndFft(
                settings.ChannelMode,
                leftSlice,
                rightSlice,
                pcmCap,
                modulated ? (subcarriers, modulation) : null);
            sampleIndex += len;
        }
    }

    /// <summary>
    /// 繰り返し信号を読み位置から折り返しながらコピーし、振幅を掛けます。
    /// </summary>
    /// <param name="loop">繰り返す信号。</param>
    /// <param name="offset">読み始め位置。</param>
    /// <param name="destination">出力先。</param>
    /// <param name="gain">振幅。</param>
    private static void CopyLoop(Complex[] loop, int offset, Span<Complex> destination, double gain)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = new Complex(loop[(offset + i) % loop.Length].Real * gain, 0.0);
        }
    }

    /// <summary>
    /// 再生チャンクをリング／FFT 可視化へ載せ、変調時は I-Q も更新します。
    /// </summary>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="leftSlice">L チャンク。</param>
    /// <param name="rightSlice">R チャンク。</param>
    /// <param name="pcmCap">PCM リング容量。</param>
    /// <param name="iq">変調時のサブキャリア数と変調方式（基準信号なら null）。</param>
    private void PublishPcmAndFft(
        ChannelMode channelMode,
        ReadOnlySpan<Complex> leftSlice,
        ReadOnlySpan<Complex> rightSlice,
        int pcmCap,
        (int Subcarriers, ModulationScheme Modulation)? iq)
    {
        int fftSize;
        PerformanceFftWindowKind windowKind;
        lock (_sync)
        {
            fftSize = _fftSize;
            windowKind = _fftWindowKind;
        }

        EnsureFftExactBuffer(fftSize);
        var exact = _fftExact!;

        var len = leftSlice.Length;
        lock (_sync)
        {
            for (var i = 0; i < len; i++)
            {
                var idx = (int)(_pcmWriteTotal % pcmCap);
                _pcmLeft[idx] = leftSlice[i].Real;
                _pcmRight[idx] = rightSlice.Length > i
                    ? rightSlice[i].Real
                    : leftSlice[i].Real;
                _pcmWriteTotal++;
            }

            if (_pcmWriteTotal < fftSize)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (_lastFftPublishMs >= 0 && now - _lastFftPublishMs < 80)
            {
                return;
            }

            FillPcmWindow(_pcmLeft, exact, fftSize, pcmCap);
            _lastFftPublishMs = now;
        }

        _vizStatus.SetFftStereoMode(channelMode == ChannelMode.Stereo);
        PerformanceFftAnalyzer.ComputeSpectrumInPlace(exact, windowKind);
        _vizStatus.SetFftFrame(exact, isRightChannel: false, PerformanceSignalGenerator.SampleRate);

        if (channelMode == ChannelMode.Stereo)
        {
            lock (_sync)
            {
                FillPcmWindow(_pcmRight, exact, fftSize, pcmCap);
            }

            PerformanceFftAnalyzer.ComputeSpectrumInPlace(exact, windowKind);
            _vizStatus.SetFftFrame(exact, isRightChannel: true, PerformanceSignalGenerator.SampleRate);
        }

        // 変調送信時は I-Q も更新（トーン／スイープは OFDM キャリアが無いので省略）
        if (iq is { } target)
        {
            PublishIqFromPcm(channelMode, target.Subcarriers, target.Modulation);
        }
    }

    /// <summary>
    /// 現在の FFT 長に一致する作業バッファを確保します。
    /// </summary>
    /// <param name="fftSize">必要 FFT 長。</param>
    private void EnsureFftExactBuffer(int fftSize)
    {
        if (_fftExact is not null && _fftExact.Length == fftSize)
        {
            return;
        }

        _fftExact = new Complex[fftSize];
    }

    /// <summary>
    /// 送信 PCM リングから等化 I-Q を抽出し可視化ボードへ載せます。
    /// </summary>
    /// <param name="channelMode">モノラル／ステレオ。</param>
    /// <param name="sc">送信中のサブキャリア数。</param>
    /// <param name="mod">送信中の変調方式。</param>
    private void PublishIqFromPcm(ChannelMode channelMode, int sc, ModulationScheme mod)
    {

        int leftPcmCount;
        lock (_sync)
        {
            if (!TryCopyPcmTailUnlocked(_pcmLeft, _iqPcmScratch, out leftPcmCount)
                || leftPcmCount < PerformanceIqExtractor.FftSize)
            {
                return;
            }
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

        if (channelMode == ChannelMode.Stereo)
        {
            int rightPcmCount;
            lock (_sync)
            {
                if (!TryCopyPcmTailUnlocked(_pcmRight, _iqPcmScratch, out rightPcmCount)
                    || rightPcmCount < PerformanceIqExtractor.FftSize)
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
        }

        if (count <= 0)
        {
            return;
        }

        _vizStatus.BeginIqCapture(sc, mod);
        _vizStatus.AppendIqFrame(_iqScratch.AsSpan(0, count), _iqGroups.AsSpan(0, count));
        _vizStatus.SetIqLeftPointCount(leftCount);
    }

    /// <summary>
    /// PCM リング末尾を線形バッファへコピーします（呼び出し元で _sync を保持）。
    /// </summary>
    /// <param name="ring">リング。</param>
    /// <param name="dest">出力。</param>
    /// <param name="count">コピーしたサンプル数。</param>
    /// <returns>コピーできたとき true。</returns>
    private bool TryCopyPcmTailUnlocked(double[] ring, double[] dest, out int count)
    {
        count = 0;
        var n = Math.Min(dest.Length, (int)Math.Min(_pcmWriteTotal, ring.Length));
        if (n <= 0)
        {
            return false;
        }

        PerformanceRingCopy.CopyTail(ring, _pcmWriteTotal, dest.AsSpan(0, n), n);
        count = n;
        return true;
    }

    /// <summary>
    /// 設定に応じた PCM（トーン／スイープ／ノイズ／変調）を生成します。
    /// </summary>
    /// <param name="settings">送信設定。</param>
    /// <returns>L/R PCM。</returns>
    private static (Complex[] Left, Complex[] Right) Generate(PerformanceTxSettings settings)
    {
        var sampleRate = settings.WriteWav
            ? PerformanceConstants.NormalizeWavSampleRate(settings.WavSampleRate)
            : PerformanceSignalGenerator.SampleRate;
        var samples = Math.Max(1, (int)Math.Round(settings.DurationSeconds * sampleRate));
        var amp = Math.Clamp(settings.SignalAmplitude, 0.05, 1.0);
        return settings.SignalMode switch
        {
            PerformanceSignalMode.Tone => PerformanceSignalGenerator.GenerateTone(
                settings.ToneHz,
                samples,
                settings.ChannelMode,
                amp,
                sampleRate),
            PerformanceSignalMode.Sweep => PerformanceSignalGenerator.GenerateLogSweep(
                20.0,
                20000.0,
                samples,
                settings.ChannelMode,
                amp,
                sampleRate),
            PerformanceSignalMode.WhiteNoise => PerformanceSignalGenerator.GenerateWhiteNoise(
                samples,
                settings.ChannelMode,
                amp,
                sampleRate),
            _ => PerformanceSignalGenerator.GenerateModulated(
                settings.ActiveSubcarriers,
                settings.ModulationScheme,
                settings.ChannelMode,
                settings.DurationSeconds,
                amp,
                sampleRate)
        };
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
            if (_pcmWriteTotal < OscilloscopeTrigger.MinDisplaySamples)
            {
                return false;
            }

            var cap = _pcmLeft.Length;
            var n = Math.Min(cap, Math.Min(left.Length, right.Length));
            n = (int)Math.Min(n, _pcmWriteTotal);
            PerformanceRingCopy.CopyStereoTail(
                _pcmLeft,
                _pcmRight,
                _pcmWriteTotal,
                left.AsSpan(0, n),
                right.AsSpan(0, n),
                n,
                copyRightFromLeft: !_pcmStereo);
            count = n;
            return true;
        }
    }

    /// <summary>
    /// リング末尾から FFT 窓を埋めます。
    /// </summary>
    /// <param name="ring">PCM リング。</param>
    /// <param name="destination">FFT 入力。</param>
    /// <param name="fftSize">窓長。</param>
    /// <param name="capacity">リング容量（互換用・未使用）。</param>
    private void FillPcmWindow(double[] ring, Complex[] destination, int fftSize, int capacity)
    {
        _ = capacity;
        PerformanceRingCopy.FillComplexWindow(ring, _pcmWriteTotal, destination, fftSize);
    }
}
