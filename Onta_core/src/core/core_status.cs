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
    /// <summary>0 から 100 の進捗率です。</summary>
    double ProgressPercent)
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
        ProgressPercent: 0);
}

/// <summary>
/// 最新エラー率と対象フレーム種別を保持します。
/// </summary>
public readonly record struct CoreErrorRateInfo(
    double LatestPercent,
    CoreFrameKind FrameKind)
{
    /// <summary>
    /// 初期エラー率情報です。
    /// </summary>
    public static CoreErrorRateInfo Idle { get; } = new(0, CoreFrameKind.Fh);
}

/// <summary>
/// I/Q 平面上の1サンプルです。
/// </summary>
public readonly record struct CoreIqSample(double I, double Q);

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
public readonly record struct CoreFftSample(int Bin, double MagnitudeDb);

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
    CoreProgressInfo Progress,
    CoreErrorRateInfo ErrorRate,
    CoreIqGraphInfo IqGraph,
    CoreFftGraphInfo FftGraph,
    double WowLeftPercent,
    double WowRightPercent,
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
        Progress: CoreProgressInfo.Idle,
        ErrorRate: CoreErrorRateInfo.Idle,
        IqGraph: CoreIqGraphInfo.Empty,
        FftGraph: CoreFftGraphInfo.Empty,
        WowLeftPercent: 0,
        WowRightPercent: 0,
        FileName: "(未受信)",
        FileSizeText: "-",
        BlockCountText: "-",
        LastError: null);
}

/// <summary>
/// 実行状態をスレッド安全に更新・参照する状態ボードです。
/// </summary>
public sealed class CoreExecutionStatusBoard
{
    public const int DefaultIqCapacity = 256;
    public const int DefaultFftCapacity = 128;

    private readonly object _sync = new();
    private CoreIqSample[] _iqRing;
    private int _iqCount;
    private int _iqWrite;
    private ModulationScheme _iqModulationScheme = ModulationScheme.Bpsk;
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
            _fftLeftCount = 0;
            _fftRightCount = 0;
            _fftIsStereo = false;
            _fftSize = 0;
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
            _fftLeftCount = 0;
            _fftRightCount = 0;
            _fftIsStereo = false;
            _fftSize = 0;
            _status = new CoreExecutionStatus(
                IsRunning: true,
                IsCompleted: false,
                IsFaulted: false,
                Progress: CoreProgressInfo.Idle,
                ErrorRate: CoreErrorRateInfo.Idle,
                IqGraph: CoreIqGraphInfo.Empty,
                FftGraph: CoreFftGraphInfo.Empty,
                WowLeftPercent: 0,
                WowRightPercent: 0,
                FileName: fileName,
                FileSizeText: fileSizeText,
                BlockCountText: blockCountText,
                LastError: null);
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
    /// <param name="percent">エラー率（0 から 100）。</param>
    /// <param name="frameKind">エラー率の対象フレーム種別。</param>
    public void SetErrorRate(double percent, CoreFrameKind frameKind)
    {
        lock (_sync)
        {
            _status = _status with
            {
                ErrorRate = new CoreErrorRateInfo(Math.Clamp(percent, 0.0, 100.0), frameKind)
            };
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
                WowRightPercent = rightPercent
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
    public void PushIq(double i, double q)
    {
        lock (_sync)
        {
            _iqRing[_iqWrite] = new CoreIqSample(i, q);
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
    public void PushIqMany(ReadOnlySpan<Complex> equalizedSymbols)
    {
        lock (_sync)
        {
            for (var n = 0; n < equalizedSymbols.Length; n++)
            {
                var s = equalizedSymbols[n];
                _iqRing[_iqWrite] = new CoreIqSample(s.Real, s.Imaginary);
                _iqWrite = (_iqWrite + 1) % _iqRing.Length;
                if (_iqCount < _iqRing.Length)
                {
                    _iqCount++;
                }
            }
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
            EnsureIqCapacityUnlocked(equalizedSymbols.Length);
            _iqCount = 0;
            _iqWrite = 0;
            for (var n = 0; n < equalizedSymbols.Length; n++)
            {
                var s = equalizedSymbols[n];
                _iqRing[_iqWrite] = new CoreIqSample(s.Real, s.Imaginary);
                _iqWrite++;
                _iqCount++;
            }

            if (_iqWrite >= _iqRing.Length)
            {
                _iqWrite = 0;
            }

            _iqModulationScheme = modulationScheme;
        }
    }

    /// <summary>
    /// FFT表示用フレームを更新します。
    /// </summary>
    /// <param name="freqBins">周波数ビン列。</param>
    /// <param name="isRightChannel">右チャネル更新時は true。</param>
    public void SetFftFrame(ReadOnlySpan<Complex> freqBins, bool isRightChannel)
    {
        lock (_sync)
        {
            _fftSize = freqBins.Length;
            var positiveCount = Math.Max(0, (freqBins.Length / 2) - 1);
            EnsureFftCapacityUnlocked(positiveCount, isRightChannel);

            if (isRightChannel)
            {
                _fftRightCount = positiveCount;
                _fftIsStereo = true;
            }
            else
            {
                _fftLeftCount = positiveCount;
                if (!_fftIsStereo)
                {
                    _fftRightCount = 0;
                }
            }

            for (var bin = 1; bin <= positiveCount; bin++)
            {
                var c = freqBins[bin];
                var magnitude = Math.Sqrt((c.Real * c.Real) + (c.Imaginary * c.Imaginary));
                var magnitudeDb = 20.0 * Math.Log10(magnitude + 1e-12);
                if (isRightChannel)
                {
                    _fftRightBins[bin - 1] = new CoreFftSample(bin, magnitudeDb);
                }
                else
                {
                    _fftLeftBins[bin - 1] = new CoreFftSample(bin, magnitudeDb);
                }
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
    /// 現在状態を描画用データ付きで取得します。
    /// </summary>
    /// <returns>現在の実行状態。</returns>
    public CoreExecutionStatus Query()
    {
        lock (_sync)
        {
            return _status with
            {
                IqGraph = new CoreIqGraphInfo(
                    CopyIqPointsUnlocked(),
                    _iqCount,
                    _iqModulationScheme),
                FftGraph = new CoreFftGraphInfo(
                    CopyFftPointsUnlocked(isRightChannel: false),
                    CopyFftPointsUnlocked(isRightChannel: true),
                    _fftIsStereo,
                    _fftSize)
            };
        }
    }

    /// <summary>
    /// EnsureIqCapacityUnlocked を実行します。
    /// </summary>
    /// <param name="required">required を指定します。</param>
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
    /// EnsureFftCapacityUnlocked を実行します。
    /// </summary>
    /// <param name="required">required を指定します。</param>
    /// <param name="isRightChannel">isRightChannel を指定します。true で有効です。</param>
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
    /// CopyIqPointsUnlocked を実行します。
    /// </summary>
    /// <returns>処理結果。</returns>
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
    /// CopyFftPointsUnlocked を実行します。
    /// </summary>
    /// <param name="isRightChannel">isRightChannel を指定します。true で有効です。</param>
    /// <returns>処理結果。</returns>
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

