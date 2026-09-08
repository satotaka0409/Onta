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
    public static CoreErrorRateInfo Idle { get; } = new(0, CoreFrameKind.Fh);
}

/// <summary>
/// I-Q 平面上の1点です（等化後データキャリア）。
/// </summary>
public readonly record struct CoreIqSample(double I, double Q);

/// <summary>
/// 3. I-Q グラフ用の直近サンプル列です。
/// </summary>
public readonly record struct CoreIqGraphInfo(IReadOnlyList<CoreIqSample> Points)
{
    public static CoreIqGraphInfo Empty { get; } = new(Array.Empty<CoreIqSample>());
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
    double WowLeftPercent,
    double WowRightPercent,
    string FileName,
    string FileSizeText,
    string BlockCountText,
    string? LastError)
{
    public static CoreExecutionStatus Idle { get; } = new(
        IsRunning: false,
        IsCompleted: false,
        IsFaulted: false,
        Progress: CoreProgressInfo.Idle,
        ErrorRate: CoreErrorRateInfo.Idle,
        IqGraph: CoreIqGraphInfo.Empty,
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

    private readonly object _sync = new();
    private readonly CoreIqSample[] _iqRing;
    private int _iqCount;
    private int _iqWrite;
    private CoreExecutionStatus _status = CoreExecutionStatus.Idle;

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

    public CoreExecutionStatusBoard(int iqCapacity = DefaultIqCapacity)
    {
        if (iqCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iqCapacity));
        }

        _iqRing = new CoreIqSample[iqCapacity];
    }

    public void Reset(string fileName = "(未受信)")
    {
        lock (_sync)
        {
            _iqCount = 0;
            _iqWrite = 0;
            _status = CoreExecutionStatus.Idle with { FileName = fileName };
        }
    }

    public void BeginRun(string fileName, string fileSizeText = "-", string blockCountText = "-")
    {
        lock (_sync)
        {
            _iqCount = 0;
            _iqWrite = 0;
            _status = new CoreExecutionStatus(
                IsRunning: true,
                IsCompleted: false,
                IsFaulted: false,
                Progress: CoreProgressInfo.Idle,
                ErrorRate: CoreErrorRateInfo.Idle,
                IqGraph: CoreIqGraphInfo.Empty,
                WowLeftPercent: 0,
                WowRightPercent: 0,
                FileName: fileName,
                FileSizeText: fileSizeText,
                BlockCountText: blockCountText,
                LastError: null);
        }
    }

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

    public void SetProgress(CoreProgressInfo progress)
    {
        lock (_sync)
        {
            _status = _status with { Progress = progress };
        }
    }

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

    public void PushIq(Complex equalizedSymbol)
    {
        PushIq(equalizedSymbol.Real, equalizedSymbol.Imaginary);
    }

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
    public CoreExecutionStatus Query()
    {
        lock (_sync)
        {
            return _status with { IqGraph = new CoreIqGraphInfo(CopyIqPointsUnlocked()) };
        }
    }

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
}
