using System.Numerics;
using Onta.Core;
using Onta.View.Core;

namespace Onta.View.Performance;

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
    private Task? _worker;
    private CancellationTokenSource? _cts;
    private long _pcmWriteTotal;
    private bool _pcmStereo;
    private bool _disposed;

    private PerformanceSignalMode _liveMode;
    private double _liveToneHz = 315.0;
    private double _liveAmplitude = 0.8;
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
    /// 完了結果待ちです。
    /// </summary>
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
            _pcmStereo = settings.ChannelMode == ChannelMode.Stereo;
            _liveMode = settings.SignalMode;
            _liveToneHz = settings.ToneHz > 0 ? settings.ToneHz : 315.0;
            _liveAmplitude = Math.Clamp(settings.SignalAmplitude, 0.10, 1.0);
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
    /// 再生中の周波数・レベル・基準／スイープを更新します（UI スレッドから呼び出し可）。
    /// 値が変わった場合は再生キューを破棄して即反映します。
    /// </summary>
    /// <param name="mode">トーン／スイープ／変調。</param>
    /// <param name="toneHz">トーン周波数（Hz）。スイープ時は無視。</param>
    /// <param name="amplitude">正弦波振幅（0.1〜1）。</param>
    public void UpdateLiveSignal(PerformanceSignalMode mode, double toneHz, double amplitude)
    {
        RealtimePcmPlayer? playerToFlush = null;
        lock (_sync)
        {
            var nextAmp = Math.Clamp(amplitude, 0.10, 1.0);
            var nextHz = toneHz > 1.0 ? toneHz : _liveToneHz;
            var changed = mode != _liveMode
                || Math.Abs(nextAmp - _liveAmplitude) > 1e-6
                || (mode == PerformanceSignalMode.Tone && Math.Abs(nextHz - _liveToneHz) > 1e-6);

            _liveMode = mode;
            if (toneHz > 1.0)
            {
                _liveToneHz = toneHz;
            }

            _liveAmplitude = nextAmp;
            if (changed)
            {
                _flushPlayback = true;
                Array.Clear(_pcmLeft);
                Array.Clear(_pcmRight);
                _pcmWriteTotal = 0;
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

    /// <inheritdoc />
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

    private void Run(PerformanceTxSettings settings, CancellationToken token)
    {
        RealtimePcmPlayer? player = null;
        try
        {
            token.ThrowIfCancellationRequested();
            Complex[]? left = null;
            Complex[]? right = null;
            var needsBuffer = settings.WriteWav
                || (settings.PlayAudio && settings.SignalMode == PerformanceSignalMode.Modulated);
            if (needsBuffer)
            {
                (left, right) = Generate(settings);
                token.ThrowIfCancellationRequested();
            }

            if (settings.WriteWav)
            {
                var path = string.IsNullOrWhiteSpace(settings.WavPath)
                    ? Path.Combine(AppPaths.OutputDir, $"perf_{DateTime.Now:yyyyMMdd_HHmmss}.wav")
                    : settings.WavPath;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                WavWriter.WritePcm16(
                    path,
                    PerformanceSignalGenerator.SampleRate,
                    left!,
                    right ?? Array.Empty<Complex>(),
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

                if (settings.SignalMode == PerformanceSignalMode.Modulated && left is not null)
                {
                    PlayBuffered(settings, left, right ?? Array.Empty<Complex>(), player, token);
                }
                else
                {
                    PlayLiveReference(settings, player, token);
                }

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
    /// トーン／スイープ／ホワイトノイズをチャンク生成しながら再生します（周波数・レベルをライブ反映）。
    /// </summary>
    private void PlayLiveReference(
        PerformanceTxSettings settings,
        RealtimePcmPlayer player,
        CancellationToken token)
    {
        const int chunk = 4096;
        var total = Math.Max(1, (int)Math.Round(settings.DurationSeconds * PerformanceSignalGenerator.SampleRate));
        var fftSize = PerformanceConstants.VizFftSize;
        var pcmCap = PerformanceConstants.ScopeCaptureSamples;
        var fftWork = new Complex[fftSize];
        var window = new Complex[fftSize];
        var leftChunk = new Complex[chunk];
        var rightChunk = settings.ChannelMode == ChannelMode.Stereo ? new Complex[chunk] : Array.Empty<Complex>();
        var phase = 0.0;
        var sampleIndex = 0;

        while (sampleIndex < total)
        {
            token.ThrowIfCancellationRequested();
            PerformanceSignalMode mode;
            double toneHz;
            double amp;
            lock (_sync)
            {
                mode = _liveMode;
                toneHz = _liveToneHz;
                amp = _liveAmplitude;
            }

            ConsumeFlushRequest(player);

            var len = Math.Min(chunk, total - sampleIndex);
            var dest = leftChunk.AsSpan(0, len);
            if (mode == PerformanceSignalMode.Sweep)
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
                // 変調ラジオは送信中ロック。トーン扱いに落とす。
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
            PublishPcmAndFft(settings, leftSlice, rightSlice, fftWork, window, fftSize, pcmCap);
            sampleIndex += len;
        }
    }

    /// <summary>
    /// 事前生成バッファをチャンク再生します（変調）。
    /// </summary>
    private void PlayBuffered(
        PerformanceTxSettings settings,
        Complex[] left,
        Complex[] right,
        RealtimePcmPlayer player,
        CancellationToken token)
    {
        const int chunk = 4096;
        var fftSize = PerformanceConstants.VizFftSize;
        var pcmCap = PerformanceConstants.ScopeCaptureSamples;
        var fftWork = new Complex[fftSize];
        var window = new Complex[fftSize];
        var scaleBuf = new Complex[chunk];
        var scaleRight = settings.ChannelMode == ChannelMode.Stereo ? new Complex[chunk] : Array.Empty<Complex>();
        var baseAmp = Math.Max(1e-6, Math.Clamp(settings.SignalAmplitude, 0.05, 1.0));

        for (var offset = 0; offset < left.Length; offset += chunk)
        {
            token.ThrowIfCancellationRequested();
            ConsumeFlushRequest(player);
            var len = Math.Min(chunk, left.Length - offset);
            double liveAmp;
            lock (_sync)
            {
                liveAmp = _liveAmplitude;
            }

            var gain = liveAmp / baseAmp;
            for (var i = 0; i < len; i++)
            {
                scaleBuf[i] = new Complex(left[offset + i].Real * gain, 0.0);
            }

            ReadOnlySpan<Complex> rightSlice;
            if (settings.ChannelMode == ChannelMode.Stereo)
            {
                for (var i = 0; i < len; i++)
                {
                    scaleRight[i] = new Complex(right[offset + i].Real * gain, 0.0);
                }

                rightSlice = scaleRight.AsSpan(0, len);
            }
            else
            {
                rightSlice = ReadOnlySpan<Complex>.Empty;
            }

            var leftSlice = scaleBuf.AsSpan(0, len);
            player.AddSamples(leftSlice, rightSlice);
            PublishPcmAndFft(settings, leftSlice, rightSlice, fftWork, window, fftSize, pcmCap);
        }
    }

    /// <summary>
    /// 再生チャンクをリング／FFT 可視化へ載せます。
    /// </summary>
    private void PublishPcmAndFft(
        PerformanceTxSettings settings,
        ReadOnlySpan<Complex> leftSlice,
        ReadOnlySpan<Complex> rightSlice,
        Complex[] fftWork,
        Complex[] window,
        int fftSize,
        int pcmCap)
    {
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

            if (_pcmWriteTotal >= fftSize)
            {
                FillPcmWindow(_pcmLeft, window, fftSize, pcmCap);
            }
        }

        if (_pcmWriteTotal < fftSize)
        {
            return;
        }

        _vizStatus.SetFftStereoMode(settings.ChannelMode == ChannelMode.Stereo);
        OfdmGenerator.ComputeForwardSpectrumFromRealPcm(window, fftWork);
        _vizStatus.SetFftFrame(fftWork, isRightChannel: false, PerformanceSignalGenerator.SampleRate);

        if (settings.ChannelMode == ChannelMode.Stereo)
        {
            lock (_sync)
            {
                FillPcmWindow(_pcmRight, window, fftSize, pcmCap);
            }

            OfdmGenerator.ComputeForwardSpectrumFromRealPcm(window, fftWork);
            _vizStatus.SetFftFrame(fftWork, isRightChannel: true, PerformanceSignalGenerator.SampleRate);
        }

        // 変調送信時は I-Q も更新（トーン／スイープは OFDM キャリアが無いので省略）
        if (settings.SignalMode == PerformanceSignalMode.Modulated)
        {
            PublishIqFromPcm(settings);
        }
    }

    /// <summary>
    /// 送信 PCM リングから等化 I-Q を抽出し可視化ボードへ載せます。
    /// </summary>
    private void PublishIqFromPcm(PerformanceTxSettings settings)
    {
        var sc = PerformanceSignalGenerator.ClampSubcarriers(settings.ActiveSubcarriers);
        var mod = PerformanceSignalGenerator.ClampModulation(settings.ModulationScheme);

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

        if (settings.ChannelMode == ChannelMode.Stereo)
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
    private bool TryCopyPcmTailUnlocked(double[] ring, double[] dest, out int count)
    {
        count = 0;
        var n = Math.Min(dest.Length, (int)Math.Min(_pcmWriteTotal, ring.Length));
        if (n <= 0)
        {
            return false;
        }

        var start = _pcmWriteTotal - n;
        for (var i = 0; i < n; i++)
        {
            var idx = (int)((start + i) % ring.Length);
            if (idx < 0)
            {
                idx += ring.Length;
            }

            dest[i] = ring[idx];
        }

        count = n;
        return true;
    }

    private static (Complex[] Left, Complex[] Right) Generate(PerformanceTxSettings settings)
    {
        var samples = Math.Max(1, (int)Math.Round(settings.DurationSeconds * PerformanceSignalGenerator.SampleRate));
        var amp = Math.Clamp(settings.SignalAmplitude, 0.05, 1.0);
        return settings.SignalMode switch
        {
            PerformanceSignalMode.Tone => PerformanceSignalGenerator.GenerateTone(
                settings.ToneHz,
                samples,
                settings.ChannelMode,
                amp),
            PerformanceSignalMode.Sweep => PerformanceSignalGenerator.GenerateLogSweep(
                20.0,
                20000.0,
                samples,
                settings.ChannelMode,
                amp),
            PerformanceSignalMode.WhiteNoise => PerformanceSignalGenerator.GenerateWhiteNoise(
                samples,
                settings.ChannelMode,
                amp),
            _ => PerformanceSignalGenerator.GenerateModulated(
                settings.ActiveSubcarriers,
                settings.ModulationScheme,
                settings.ChannelMode,
                settings.DurationSeconds,
                amp)
        };
    }

    /// <summary>
    /// 直近の PCM 窓をオシロスコープ用にコピーします。
    /// </summary>
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
            var start = _pcmWriteTotal - n;
            for (var i = 0; i < n; i++)
            {
                var idx = (int)((start + i) % cap);
                if (idx < 0)
                {
                    idx += cap;
                }

                left[i] = _pcmLeft[idx];
                right[i] = _pcmStereo ? _pcmRight[idx] : _pcmLeft[idx];
            }

            count = n;
            return true;
        }
    }

    /// <summary>
    /// リング末尾から FFT 窓を埋めます。
    /// </summary>
    private void FillPcmWindow(double[] ring, Complex[] destination, int fftSize, int capacity)
    {
        var start = _pcmWriteTotal - fftSize;
        for (var i = 0; i < fftSize; i++)
        {
            var idx = (int)((start + i) % capacity);
            if (idx < 0)
            {
                idx += capacity;
            }

            destination[i] = new Complex(ring[idx], 0.0);
        }
    }
}
