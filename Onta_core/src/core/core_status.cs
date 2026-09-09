using System.Numerics;

namespace Onta.Core;

/// <summary>
/// 送受信コアが現在処理中のフレーム種別です（画面仕様の FH/BH/BD）。
/// </summary>
public enum CoreFrameKind : byte
{
    Fh = 0,
    Bh = 1,
    Bd = 2
}

/// <summary>
/// 1. 現在受信中の FH/BLOCK と進捗率です。
/// </summary>
public readonly record struct CoreProgressInfo(
    CoreFrameKind CurrentFrame,
    /// <summary>FH のときは -1。BH/BD はブロック番号。</summary>
    int CurrentBlockIndex,
    int PassIndex,
    int AcceptedBlockCount,
    int TotalBlockCount,
    /// <summary>0〜100。</summary>
    double ProgressPercent)
{
    /// <summary>
    /// 未開始／待機時の既定進捗です。
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
/// 2. 直近のエラー率（0〜100%）です。
/// </summary>
public readonly record struct CoreErrorRateInfo(
    double LatestPercent,
    CoreFrameKind FrameKind)
{
    /// <summary>
    /// 未開始／待機時の既定エラー率です。
    /// </summary>
    public static CoreErrorRateInfo Idle { get; } = new(0, CoreFrameKind.Fh);
}

/// <summary>
/// I-Q 平面上の1点です（等化後データキャリア）。
/// </summary>
public readonly record struct CoreIqSample(double I, double Q);

/// <summary>
/// 3. I-Q グラフ用の直近サンプル列です。
/// </summary>
public readonly record struct CoreIqGraphInfo(
    IReadOnlyList<CoreIqSample> Points,
    int ActiveSubcarrierCount,
    ModulationScheme ModulationScheme)
{
    /// <summary>
    /// サンプル未取得時の空グラフです。
    /// </summary>
    public static CoreIqGraphInfo Empty { get; } = new(Array.Empty<CoreIqSample>(), 0, ModulationScheme.Bpsk);
}

/// <summary>
/// FFT グラフの 1 ビン分サンプルです。
/// </summary>
public readonly record struct CoreFftSample(int Bin, double MagnitudeDb);

/// <summary>
/// FFT グラフ用の直近フレームです（正周波数側のみ）。
/// </summary>
public readonly record struct CoreFftGraphInfo(
    IReadOnlyList<CoreFftSample> LeftPoints,
    IReadOnlyList<CoreFftSample> RightPoints,
    bool IsStereo,
    int FftSize)
{
    /// <summary>
    /// サンプル未取得時の空 FFT グラフです。
    /// </summary>
    public static CoreFftGraphInfo Empty { get; } = new(
        Array.Empty<CoreFftSample>(),
        Array.Empty<CoreFftSample>(),
        false,
        0);
}

/// <summary>
/// 画面がコアスレッドへ問い合わせる実行状況のスナップショットです。
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
    /// 未開始／待機時の既定実行状況です。
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
/// コアスレッドが保持し、画面からの問い合わせでスナップショットを返す実行状況バッファです。
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
    /// 現在保持しているファイル名表示文字列です。
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
    /// I-Q リングバッファ容量を指定して実行状況ボードを初期化します。
    /// </summary>
    /// <param name="iqCapacity">I-Q サンプルの保持上限。</param>
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
    /// 実行状況・グラフバッファを待機状態へ戻します。
    /// </summary>
    /// <param name="fileName">表示用ファイル名。</param>
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
    /// 実行開始状態へ遷移し、グラフバッファをクリアします。
    /// </summary>
    /// <param name="fileName">表示用ファイル名。</param>
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
    /// ファイル名・サイズ・ブロック数の表示文字列を更新します。
    /// </summary>
    /// <param name="fileName">表示用ファイル名。</param>
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
    /// FH/BLOCK 進捗情報を更新します。
    /// </summary>
    /// <param name="progress">更新する進捗スナップショット。</param>
    public void SetProgress(CoreProgressInfo progress)
    {
        lock (_sync)
        {
            _status = _status with { Progress = progress };
        }
    }

    /// <summary>
    /// 直近エラー率（%）と対象フレーム種別を更新します。
    /// </summary>
    /// <param name="percent">エラー率（0〜100）。</param>
    /// <param name="frameKind">対象フレーム種別（FH/BH/BD）。</param>
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
    /// エラー率グラフ用のフレーム種別だけを差し替えます。
    /// </summary>
    /// <param name="frameKind">差し替えるフレーム種別。</param>
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
    /// ワウ・フラッター推定値（L/R、%）を更新します。
    /// </summary>
    /// <param name="leftPercent">L 側推定値（%）。</param>
    /// <param name="rightPercent">R 側推定値（%）。</param>
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
    /// 等化後シンボル 1 点を I-Q リングへ追加します。
    /// </summary>
    /// <param name="equalizedSymbol">等化後の複素シンボル。</param>
    public void PushIq(Complex equalizedSymbol)
    {
        PushIq(equalizedSymbol.Real, equalizedSymbol.Imaginary);
    }

    /// <summary>
    /// I/Q 座標 1 点をリングバッファへ追加します。
    /// </summary>
    /// <param name="i">I 成分。</param>
    /// <param name="q">Q 成分。</param>
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
    /// 等化後シンボル列をまとめて I-Q リングへ追加します。
    /// </summary>
    /// <param name="equalizedSymbols">追加する等化後シンボル列。</param>
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
    /// I-Q 表示を指定シンボル列で置き換え、変調方式も更新します。
    /// </summary>
    /// <param name="equalizedSymbols">表示する等化後シンボル列。</param>
    /// <param name="modulationScheme">表示用の変調方式。</param>
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
    /// FFT 周波数ビンから正周波数側の dB 振幅を左右どちらかへ格納します。
    /// </summary>
    /// <param name="freqBins">FFT 周波数ビン列。</param>
    /// <param name="isRightChannel">R チャンネルへ格納するか。</param>
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
    /// FFT グラフのステレオ表示可否を設定します。
    /// </summary>
    /// <param name="isStereo">ステレオ表示にするか。</param>
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
    /// 実行完了（正常／異常）を記録し、実行中フラグを下ろします。
    /// </summary>
    /// <param name="faulted">異常完了なら <see langword="true"/>。</param>
    /// <param name="lastError">直近エラーメッセージ。省略可。</param>
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
    /// 直近エラーメッセージを更新します。
    /// </summary>
    /// <param name="lastError">直近エラーメッセージ。無い場合は null。</param>
    public void SetLastError(string? lastError)
    {
        lock (_sync)
        {
            _status = _status with { LastError = lastError };
        }
    }

    /// <summary>
    /// 画面スレッドからの問い合わせ用スナップショットを返します。
    /// </summary>
    /// <returns>進捗・エラー率・I-Q/FFT を含む実行状況。</returns>
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
    /// I-Q リング容量が不足していれば拡張します（呼び出し側でロック済み想定）。
    /// </summary>
    /// <param name="required">必要な最小容量。</param>
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
    /// FFT ビン配列の容量が不足していれば左右どちらかを拡張します。
    /// </summary>
    /// <param name="required">必要な最小ビン数。</param>
    /// <param name="isRightChannel">R 側を拡張するか。</param>
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
    /// リング上の I-Q 点を時系列順の配列へコピーします。
    /// </summary>
    /// <returns>時系列順の I-Q 点列（空なら空配列）。</returns>
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
    /// 左右いずれかの FFT 点列をコピーして返します。
    /// </summary>
    /// <param name="isRightChannel">R 側をコピーするか。</param>
    /// <returns>コピーした FFT 点列（空なら空配列）。</returns>
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
