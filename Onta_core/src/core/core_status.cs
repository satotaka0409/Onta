using System.Numerics;

namespace Onta.Core;

/// <summary>
/// 処理中フレーム種別を表します。
/// </summary>
public enum CoreFrameKind : byte
{
    Fh = 0,
    Bh = 1,
    Bd = 2
}

/// <summary>
/// 進捗表示に必要な最小情報を保持します。
/// </summary>
public readonly record struct CoreProgressInfo(
    CoreFrameKind CurrentFrame,
    int CurrentBlockIndex,
    int PassIndex,
    int AcceptedBlockCount,
    int TotalBlockCount,
    /// <summary>0 から 100 の全体進捗率です。</summary>
    double ProgressPercent,
    /// <summary>現在ブロック内の局所進捗（0..100）。BH/BD メーター用。</summary>
    double CurrentBlockProgressPercent = 0)
{
    /// <summary>
    /// 待機状態を表す初期値です。
    /// </summary>
    public static CoreProgressInfo Idle { get; } = new(
        CurrentFrame: CoreFrameKind.Fh,
        CurrentBlockIndex: -1,
        PassIndex: 0,
        AcceptedBlockCount: 0,
        TotalBlockCount: 0,
        ProgressPercent: 0,
        CurrentBlockProgressPercent: 0);
}

/// <summary>
/// エラー率グラフ上の誤り訂正段階です。
/// </summary>
public enum CoreEccDecoderKind : byte
{
    /// <summary>畳み込み（ビタビ / BCJR）。</summary>
    Viterbi = 0,
    /// <summary>ターボ符号（データ部）。</summary>
    Turbo = 1,
    /// <summary>Reed-Solomon（ヘッダー部）。グラフ上はターボと同色の外符号系列。</summary>
    ReedSolomon = 2
}

/// <summary>
/// 最新エラー率と対象フレーム種別を保持します。
/// </summary>
public readonly record struct CoreErrorRateInfo(
    double LatestPercent,
    CoreFrameKind FrameKind,
    CoreEccDecoderKind DecoderKind,
    int Sequence,
    double? RightPercent = null)
{
    /// <summary>
    /// 初期エラー率情報です。
    /// </summary>
    public static CoreErrorRateInfo Idle { get; } = new(0, CoreFrameKind.Fh, CoreEccDecoderKind.Viterbi, 0);
}

/// <summary>
/// I/Q 平面上の1サンプルです。
/// </summary>
public readonly record struct CoreIqSample(double I, double Q, byte Group = 0);

/// <summary>
/// IQグラフ描画に必要な系列情報です。
/// </summary>
public readonly record struct CoreIqGraphInfo(
    IReadOnlyList<CoreIqSample> Points,
    int ActiveSubcarrierCount,
    ModulationScheme ModulationScheme)
{
    /// <summary>
    /// 空状態のIQグラフ情報です。
    /// </summary>
    public static CoreIqGraphInfo Empty { get; } = new(Array.Empty<CoreIqSample>(), 0, ModulationScheme.Bpsk);
}

/// <summary>
/// FFTグラフ描画用の1ビンサンプルです。
/// </summary>
/// <param name="FrequencyHz">ビン中心周波数（Hz）。整数丸めせず連続値で保持する。</param>
/// <param name="MagnitudeDb">振幅（dBFS、フルスケール正弦波≈0 dB）。</param>
public readonly record struct CoreFftSample(double FrequencyHz, double MagnitudeDb);

/// <summary>
/// 左右FFTの描画データとモード情報を保持します。
/// </summary>
public readonly record struct CoreFftGraphInfo(
    IReadOnlyList<CoreFftSample> LeftPoints,
    IReadOnlyList<CoreFftSample> RightPoints,
    bool IsStereo,
    int FftSize)
{
    /// <summary>
    /// 空状態のFFTグラフ情報です。
    /// </summary>
    public static CoreFftGraphInfo Empty { get; } = new(
        Array.Empty<CoreFftSample>(),
        Array.Empty<CoreFftSample>(),
        false,
        0);
}

/// <summary>
/// UI表示向けに集約した実行状態です。
/// </summary>
public readonly record struct CoreExecutionStatus(
    bool IsRunning,
    bool IsCompleted,
    bool IsFaulted,
    /// <summary>FH ワウ推定など、ヘッダー確定前の解析中は true。</summary>
    bool IsAnalyzing,
    CoreProgressInfo Progress,
    CoreErrorRateInfo ErrorRate,
    /// <summary>前回 Read 以降にコアが書き込んだエラー率サンプル（ビタビ/RS/ターボ）。</summary>
    IReadOnlyList<CoreErrorRateInfo> ErrorRateSamples,
    CoreIqGraphInfo IqGraph,
    CoreFftGraphInfo FftGraph,
    double WowLeftPercent,
    double WowRightPercent,
    /// <summary>推定ワウモデルが有効なら true（UI が瞬間速度を連続評価する）。</summary>
    bool WowTrackingActive,
    double WowAmount,
    double WowPhase,
    double FlutterPhase,
    int WowSampleRate,
    long WowSampleIndex,
    string FileName,
    string FileSizeText,
    string BlockCountText,
    string? LastError)
{
    /// <summary>
    /// 待機状態の初期実行ステータスです。
    /// </summary>
    public static CoreExecutionStatus Idle { get; } = new(
        IsRunning: false,
        IsCompleted: false,
        IsFaulted: false,
        IsAnalyzing: false,
        Progress: CoreProgressInfo.Idle,
        ErrorRate: CoreErrorRateInfo.Idle,
        ErrorRateSamples: Array.Empty<CoreErrorRateInfo>(),
        IqGraph: CoreIqGraphInfo.Empty,
        FftGraph: CoreFftGraphInfo.Empty,
        WowLeftPercent: 0,
        WowRightPercent: 0,
        WowTrackingActive: false,
        WowAmount: 0,
        WowPhase: 0,
        FlutterPhase: 0,
        WowSampleRate: 44100,
        WowSampleIndex: 0,
        FileName: "(未受信)",
        FileSizeText: "-",
        BlockCountText: "-",
        LastError: null);
}

/// <summary>
/// コアと画面の共有実行状態メモリです。
/// コアが進捗・グラフ用データを書き込み、画面は定期的に <see cref="Read"/> で読み取ります（画面→コア問い合わせは行いません）。
/// </summary>
public sealed class CoreExecutionStatusBoard
{
    public const int DefaultIqCapacity = 4096;
    public const int DefaultFftCapacity = 256;

    private readonly object _sync = new();
    /// <summary>エラー率の SPSC リング（コア=producer、画面 Read=consumer）。</summary>
    private readonly CoreErrorRateInfo[] _errorRateRing = new CoreErrorRateInfo[MaxPendingErrorRates];
    private long _errorRateWriteSeq;
    private long _errorRateReadSeq;
    private const int MaxPendingErrorRates = 256;
    private CoreIqSample[] _iqRing;
    private int _iqCount;
    private int _iqWrite;
    private ModulationScheme _iqModulationScheme = ModulationScheme.Bpsk;
    private int _iqActiveSubcarrierCount;
    private CoreFftSample[] _fftLeftBins;
    private CoreFftSample[] _fftRightBins;
    private int _fftLeftCount;
    private int _fftRightCount;
    private bool _fftIsStereo;
    private int _fftSize;
    private CoreExecutionStatus _status = CoreExecutionStatus.Idle;

    /// <summary>
    /// 現在の対象ファイル名を返します。
    /// </summary>
    public string FileName
    {
        get
        {
            lock (_sync)
            {
                return _status.FileName;
            }
        }
    }

    /// <summary>
    /// 状態ボードを生成します。
    /// </summary>
    /// <param name="iqCapacity">保持するIQサンプルの最大数。</param>
    public CoreExecutionStatusBoard(int iqCapacity = DefaultIqCapacity)
    {
        if (iqCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iqCapacity));
        }

        _iqRing = new CoreIqSample[iqCapacity];
        _fftLeftBins = new CoreFftSample[DefaultFftCapacity];
        _fftRightBins = new CoreFftSample[DefaultFftCapacity];
    }

    /// <summary>
    /// 実行状態を初期化します。
    /// </summary>
    /// <param name="fileName">初期表示するファイル名。</param>
    public void Reset(string fileName = "(未受信)")
    {
        lock (_sync)
        {
            _iqCount = 0;
            _iqWrite = 0;
            _iqModulationScheme = ModulationScheme.Bpsk;
            _iqActiveSubcarrierCount = 0;
            _fftLeftCount = 0;
            _fftRightCount = 0;
            _fftIsStereo = false;
            _fftSize = 0;
            ResetErrorRateRingUnlocked();
            _status = CoreExecutionStatus.Idle with { FileName = fileName };
        }
    }

    /// <summary>
    /// 新しい処理開始時の状態へ遷移します。
    /// </summary>
    /// <param name="fileName">対象ファイル名。</param>
    /// <param name="fileSizeText">表示用ファイルサイズ文字列。</param>
    /// <param name="blockCountText">表示用ブロック数文字列。</param>
    public void BeginRun(string fileName, string fileSizeText = "-", string blockCountText = "-")
    {
        lock (_sync)
        {
            _iqCount = 0;
            _iqWrite = 0;
            _iqModulationScheme = ModulationScheme.Bpsk;
            _iqActiveSubcarrierCount = 0;
            _fftLeftCount = 0;
            _fftRightCount = 0;
            _fftIsStereo = false;
            _fftSize = 0;
            ResetErrorRateRingUnlocked();
            _status = new CoreExecutionStatus(
                IsRunning: true,
                IsCompleted: false,
                IsFaulted: false,
                IsAnalyzing: true,
                Progress: CoreProgressInfo.Idle,
                ErrorRate: CoreErrorRateInfo.Idle,
                ErrorRateSamples: Array.Empty<CoreErrorRateInfo>(),
                IqGraph: CoreIqGraphInfo.Empty,
                FftGraph: CoreFftGraphInfo.Empty,
                WowLeftPercent: 0,
                WowRightPercent: 0,
                WowTrackingActive: false,
                WowAmount: 0,
                WowPhase: 0,
                FlutterPhase: 0,
                WowSampleRate: 44100,
                WowSampleIndex: 0,
                FileName: fileName,
                FileSizeText: fileSizeText,
                BlockCountText: blockCountText,
                LastError: null);
        }
    }

    /// <summary>
    /// FH ワウ推定などの解析フェーズかどうかを設定します。
    /// </summary>
    /// <param name="analyzing">解析中なら true。</param>
    public void SetAnalyzing(bool analyzing)
    {
        lock (_sync)
        {
            _status = _status with { IsAnalyzing = analyzing };
        }
    }

    /// <summary>
    /// ファイル情報表示を更新します。
    /// </summary>
    /// <param name="fileName">対象ファイル名。</param>
    /// <param name="fileSizeText">表示用ファイルサイズ文字列。</param>
    /// <param name="blockCountText">表示用ブロック数文字列。</param>
    public void SetFileInfo(string fileName, string fileSizeText, string blockCountText)
    {
        lock (_sync)
        {
            _status = _status with
            {
                FileName = fileName,
                FileSizeText = fileSizeText,
                BlockCountText = blockCountText
            };
        }
    }

    /// <summary>
    /// 進捗情報を更新します。
    /// </summary>
    /// <param name="progress">更新する進捗情報。</param>
    public void SetProgress(CoreProgressInfo progress)
    {
        lock (_sync)
        {
            _status = _status with { Progress = progress };
        }
    }

    /// <summary>
    /// エラー率と対象フレーム種別を更新します。
    /// </summary>
    /// <param name="percent">誤り訂正段階の推定エラー率（0 から 100）。</param>
    /// <param name="frameKind">エラー率の対象フレーム種別。</param>
    /// <param name="decoderKind">ビタビ / ターボの区別。</param>
    /// <param name="rightPercent">右チャネルの推定エラー率（ステレオ時）。null の場合は単一系列として扱います。</param>
    public void SetErrorRate(
        double percent,
        CoreFrameKind frameKind,
        CoreEccDecoderKind decoderKind = CoreEccDecoderKind.Viterbi,
        double? rightPercent = null)
    {
        lock (_sync)
        {
            var info = new CoreErrorRateInfo(
                Math.Clamp(percent, 0.0, 100.0),
                frameKind,
                decoderKind,
                _status.ErrorRate.Sequence + 1,
                rightPercent is null ? null : Math.Clamp(rightPercent.Value, 0.0, 100.0));
            _status = _status with { ErrorRate = info };
            // コアが共有リングへ追記。画面の Read が追いつかない場合は最古を上書きする。
            var nextWrite = _errorRateWriteSeq + 1;
            if (nextWrite - _errorRateReadSeq > MaxPendingErrorRates)
            {
                _errorRateReadSeq = nextWrite - MaxPendingErrorRates;
            }

            _errorRateRing[(int)(_errorRateWriteSeq % MaxPendingErrorRates)] = info;
            _errorRateWriteSeq = nextWrite;
        }
    }

    /// <summary>
    /// エラー率表示の対象フレーム種別のみ更新します。
    /// </summary>
    /// <param name="frameKind">対象フレーム種別。</param>
    public void SetErrorFrameKind(CoreFrameKind frameKind)
    {
        lock (_sync)
        {
            _status = _status with
            {
                ErrorRate = _status.ErrorRate with { FrameKind = frameKind }
            };
        }
    }

    /// <summary>
    /// WOW/Flutter 推定値を更新します。
    /// </summary>
    /// <param name="leftPercent">左チャネル推定値（%）。</param>
    /// <param name="rightPercent">右チャネル推定値（%）。</param>
    public void SetWowFlutterPercent(double leftPercent, double rightPercent)
    {
        lock (_sync)
        {
            _status = _status with
            {
                WowLeftPercent = leftPercent,
                WowRightPercent = rightPercent,
                WowTrackingActive = false
            };
        }
    }

    /// <summary>
    /// 推定ワウモデルを公開し、瞬間速度偏差（%）も更新します。
    /// UI はこのモデルから連続的にメーターを動かします。
    /// </summary>
    public void SetWowFlutterTracking(
        double amount,
        double wowPhase,
        double flutterPhase,
        int sampleRate,
        long sampleIndex)
    {
        var sr = Math.Max(1, sampleRate);
        var pct = WowFlutterWarp.EvaluateSpeedDeviationPercent(
            sr, amount, wowPhase, flutterPhase, sampleIndex);
        lock (_sync)
        {
            _status = _status with
            {
                WowLeftPercent = pct,
                WowRightPercent = pct,
                WowTrackingActive = true,
                WowAmount = amount,
                WowPhase = wowPhase,
                FlutterPhase = flutterPhase,
                WowSampleRate = sr,
                WowSampleIndex = Math.Max(0L, sampleIndex)
            };
        }
    }

    /// <summary>
    /// 単一IQサンプルを追加します。
    /// </summary>
    /// <param name="equalizedSymbol">等化後の複素シンボル。</param>
    public void PushIq(Complex equalizedSymbol)
    {
        PushIq(equalizedSymbol.Real, equalizedSymbol.Imaginary);
    }

    /// <summary>
    /// I/Q 成分を指定して単一サンプルを追加します。
    /// </summary>
    /// <param name="i">同相成分 I。</param>
    /// <param name="q">直交成分 Q。</param>
    /// <param name="group">サブキャリアグループ（0=A..5=F）。</param>
    public void PushIq(double i, double q, byte group = 0)
    {
        lock (_sync)
        {
            _iqRing[_iqWrite] = new CoreIqSample(i, q, group);
            _iqWrite = (_iqWrite + 1) % _iqRing.Length;
            if (_iqCount < _iqRing.Length)
            {
                _iqCount++;
            }
        }
    }

    /// <summary>
    /// 複数のIQサンプルをまとめて追加します。
    /// </summary>
    /// <param name="equalizedSymbols">等化後シンボル列。</param>
    /// <param name="groups">各シンボルのサブキャリアグループ（0=A..5=F）。null 時は 0。</param>
    public void PushIqMany(ReadOnlySpan<Complex> equalizedSymbols, ReadOnlySpan<byte> groups = default)
    {
        lock (_sync)
        {
            for (var n = 0; n < equalizedSymbols.Length; n++)
            {
                var s = equalizedSymbols[n];
                var group = n < groups.Length ? groups[n] : (byte)0;
                _iqRing[_iqWrite] = new CoreIqSample(s.Real, s.Imaginary, group);
                _iqWrite = (_iqWrite + 1) % _iqRing.Length;
                if (_iqCount < _iqRing.Length)
                {
                    _iqCount++;
                }
            }
        }
    }

    /// <summary>
    /// IQ リングを空にして、新しいブロック／試行の取り込みを開始します。
    /// </summary>
    /// <param name="activeSubcarriers">データ部の有効サブキャリア数。</param>
    /// <param name="modulationScheme">表示する変調方式。</param>
    public void BeginIqCapture(int activeSubcarriers, ModulationScheme modulationScheme)
    {
        lock (_sync)
        {
            _iqCount = 0;
            _iqWrite = 0;
            _iqActiveSubcarrierCount = Math.Max(0, activeSubcarriers);
            _iqModulationScheme = modulationScheme;
        }
    }

    /// <summary>
    /// IQ表示用フレームを丸ごと差し替えます。
    /// </summary>
    /// <param name="equalizedSymbols">等化後シンボル列。</param>
    /// <param name="modulationScheme">表示対象の変調方式。</param>
    public void SetIqFrame(ReadOnlySpan<Complex> equalizedSymbols, ModulationScheme modulationScheme)
    {
        lock (_sync)
        {
            EnsureIqCapacityUnlocked(Math.Max(equalizedSymbols.Length, DefaultIqCapacity));
            _iqCount = 0;
            _iqWrite = 0;
            for (var n = 0; n < equalizedSymbols.Length; n++)
            {
                var s = equalizedSymbols[n];
                _iqRing[_iqWrite] = new CoreIqSample(s.Real, s.Imaginary, 0);
                _iqWrite++;
                _iqCount++;
            }

            if (_iqWrite >= _iqRing.Length)
            {
                _iqWrite = 0;
            }

            _iqModulationScheme = modulationScheme;
            if (_iqActiveSubcarrierCount <= 0)
            {
                _iqActiveSubcarrierCount = equalizedSymbols.Length;
            }
        }
    }

    /// <summary>
    /// 等化後シンボルを IQ リングへ追記します（コンスタレーション蓄積用）。
    /// </summary>
    /// <param name="equalizedSymbols">等化後シンボル列。</param>
    /// <param name="groups">各シンボルのサブキャリアグループ（0=A..5=F）。</param>
    public void AppendIqFrame(ReadOnlySpan<Complex> equalizedSymbols, ReadOnlySpan<byte> groups = default)
    {
        if (equalizedSymbols.IsEmpty)
        {
            return;
        }

        PushIqMany(equalizedSymbols, groups);
    }

    /// <summary>
    /// FFT表示用フレームを更新します。
    /// 横軸は正周波数（Hz）、ビン0（DC）を左端に置きます。
    /// </summary>
    /// <param name="freqBins">周波数ビン列。</param>
    /// <param name="isRightChannel">右チャネル更新時は true。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    public void SetFftFrame(ReadOnlySpan<Complex> freqBins, bool isRightChannel, int sampleRate = 44100)
    {
        lock (_sync)
        {
            var n = freqBins.Length;
            _fftSize = n;
            if (n < 2)
            {
                if (isRightChannel)
                {
                    _fftRightCount = 0;
                    _fftIsStereo = true;
                }
                else
                {
                    _fftLeftCount = 0;
                    if (!_fftIsStereo)
                    {
                        _fftRightCount = 0;
                    }
                }

                return;
            }

            var half = n / 2;
            var sr = Math.Max(1, sampleRate);
            // 正周波数のみ（DC..Nyquist直前）
            var pointCount = half;
            EnsureFftCapacityUnlocked(pointCount, isRightChannel);

            if (isRightChannel)
            {
                _fftRightCount = pointCount;
                _fftIsStereo = true;
            }
            else
            {
                _fftLeftCount = pointCount;
                if (!_fftIsStereo)
                {
                    _fftRightCount = 0;
                }
            }

            var dest = isRightChannel ? _fftRightBins : _fftLeftBins;
            // 実信号 FFT の片側振幅スケール（ピーク≈時間振幅、0 dB≒フルスケール正弦波）
            var scale = 2.0 / n;

            for (var bin = 0; bin < half; bin++)
            {
                var c = freqBins[bin];
                var magnitude = Math.Sqrt((c.Real * c.Real) + (c.Imaginary * c.Imaginary)) * scale;
                if (bin == 0)
                {
                    // DC は片側スケールしない
                    magnitude *= 0.5;
                }

                var magnitudeDb = 20.0 * Math.Log10(magnitude + 1e-12);
                // 整数丸めすると隣接ビンが同じ X になり縦スパイクになるため、Hz は連続値で渡す
                var hz = bin * (double)sr / n;
                dest[bin] = new CoreFftSample(hz, magnitudeDb);
            }
        }
    }

    /// <summary>
    /// FFTのステレオ表示モードを設定します。
    /// </summary>
    /// <param name="isStereo">ステレオ表示にする場合 true。</param>
    public void SetFftStereoMode(bool isStereo)
    {
        lock (_sync)
        {
            _fftIsStereo = isStereo;
            if (!isStereo)
            {
                _fftRightCount = 0;
            }
        }
    }

    /// <summary>
    /// 処理完了状態へ遷移します。
    /// </summary>
    /// <param name="faulted">失敗終了時は true。</param>
    /// <param name="lastError">失敗理由メッセージ。</param>
    public void Complete(bool faulted, string? lastError = null)
    {
        lock (_sync)
        {
            var progress = _status.Progress;
            if (!faulted)
            {
                progress = progress with { ProgressPercent = 100.0 };
            }

            _status = _status with
            {
                IsRunning = false,
                IsCompleted = true,
                IsFaulted = faulted,
                IsAnalyzing = false,
                Progress = progress,
                LastError = lastError
            };
        }
    }

    /// <summary>
    /// 最終エラーメッセージを更新します。
    /// </summary>
    /// <param name="lastError">設定するエラー文字列。</param>
    public void SetLastError(string? lastError)
    {
        lock (_sync)
        {
            _status = _status with { LastError = lastError };
        }
    }

    /// <summary>
    /// 共有状態のスナップショットを読み取ります（画面スレッド用）。
    /// 進捗・IQ・FFT・ワウは最新値のコピー、エラー率サンプルは未読分のみ取り出します。
    /// </summary>
    /// <returns>現在の実行状態。</returns>
    public CoreExecutionStatus Read()
    {
        lock (_sync)
        {
            var samples = ConsumeErrorRateSamplesUnlocked();
            var iqCount = _iqActiveSubcarrierCount > 0 ? _iqActiveSubcarrierCount : _iqCount;
            var iqGraph = new CoreIqGraphInfo(CopyIqPointsUnlocked(), iqCount, _iqModulationScheme);
            var fftGraph = new CoreFftGraphInfo(
                CopyFftPointsUnlocked(false),
                CopyFftPointsUnlocked(true),
                _fftIsStereo,
                _fftSize);

            return _status with
            {
                ErrorRateSamples = samples,
                IqGraph = iqGraph,
                FftGraph = fftGraph
            };
        }
    }

    /// <summary>
    /// <see cref="Read"/> の互換エイリアスです。
    /// </summary>
    public CoreExecutionStatus Query() => Read();

    private void ResetErrorRateRingUnlocked()
    {
        _errorRateWriteSeq = 0;
        _errorRateReadSeq = 0;
        Array.Clear(_errorRateRing);
    }

    private CoreErrorRateInfo[] ConsumeErrorRateSamplesUnlocked()
    {
        var unread = _errorRateWriteSeq - _errorRateReadSeq;
        if (unread <= 0)
        {
            return [];
        }

        if (unread > MaxPendingErrorRates)
        {
            _errorRateReadSeq = _errorRateWriteSeq - MaxPendingErrorRates;
            unread = MaxPendingErrorRates;
        }

        var samples = new CoreErrorRateInfo[unread];
        for (var i = 0; i < unread; i++)
        {
            samples[i] = _errorRateRing[(int)((_errorRateReadSeq + i) % MaxPendingErrorRates)];
        }

        _errorRateReadSeq = _errorRateWriteSeq;
        return samples;
    }

    /// <summary>
    /// IQ リング容量が不足する場合に拡張します。
    /// </summary>
    /// <param name="required">必要容量。</param>
    private void EnsureIqCapacityUnlocked(int required)
    {
        if (required <= _iqRing.Length)
        {
            return;
        }

        _iqRing = new CoreIqSample[required];
        _iqCount = 0;
        _iqWrite = 0;
    }

    /// <summary>
    /// 指定チャネルの FFT バッファ容量が不足する場合に拡張します。
    /// </summary>
    /// <param name="required">必要容量。</param>
    /// <param name="isRightChannel">true のとき右チャネル、false のとき左チャネルを対象にします。</param>
    private void EnsureFftCapacityUnlocked(int required, bool isRightChannel)
    {
        var target = isRightChannel ? _fftRightBins : _fftLeftBins;
        if (required <= target.Length)
        {
            return;
        }

        if (isRightChannel)
        {
            _fftRightBins = new CoreFftSample[required];
            _fftRightCount = 0;
        }
        else
        {
            _fftLeftBins = new CoreFftSample[required];
            _fftLeftCount = 0;
        }
    }

    /// <summary>
    /// IQ リング内容を時系列順の配列として返します。
    /// </summary>
    /// <returns>時系列順の IQ サンプル配列。</returns>
    private CoreIqSample[] CopyIqPointsUnlocked()
    {
        if (_iqCount == 0)
        {
            return [];
        }

        var points = new CoreIqSample[_iqCount];
        var start = _iqCount == _iqRing.Length ? _iqWrite : 0;
        for (var i = 0; i < _iqCount; i++)
        {
            points[i] = _iqRing[(start + i) % _iqRing.Length];
        }

        return points;
    }

    /// <summary>
    /// 指定チャネルの FFT 表示点をコピーして返します。
    /// </summary>
    /// <param name="isRightChannel">true のとき右チャネル、false のとき左チャネル。</param>
    /// <returns>FFT 表示点配列。</returns>
    private CoreFftSample[] CopyFftPointsUnlocked(bool isRightChannel)
    {
        var count = isRightChannel ? _fftRightCount : _fftLeftCount;
        if (count == 0)
        {
            return [];
        }

        var source = isRightChannel ? _fftRightBins : _fftLeftBins;
        var points = new CoreFftSample[count];
        Array.Copy(source, 0, points, 0, count);
        return points;
    }
}

