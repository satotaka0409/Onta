using System.Numerics;
using Onta.Core;
using Onta.Performance;
using Onta.Stream;
using Onta.View.Core;
using Onta.View.Language;

namespace Onta.View.Stream;

/// <summary>
/// ストリーム再生受信ワーカーです。
/// </summary>
/// <remarks>
/// 受信 PCM の FFT、復調したパケットの I-Q とビタビ中間訂正率を共有ボード（<see cref="SharedStatus"/>）へ書き込みます。
/// 復調はこのワーカーのスレッド、Opus 復号と再生は <see cref="StreamPlaybackWorker"/> のスレッドで行い、復調の遅れが音切れに直結しないようにします。
/// </remarks>
internal sealed class StreamRxWorker : IDisposable
{
    private const int FftSize = 2048;
    private const int MinFftPublishIntervalMs = 80;
    private const int PlaybackSampleRate = Onta.Stream.Opus.OpusEncoder.OpusSampleRate;
    /// <summary>再生バッファの目標充填量（1.6 秒。パケット到着の揺らぎや受信エラーで欠けた分を吸収する）。</summary>
    private const int PlaybackTargetFrames = PlaybackSampleRate * 8 / 5;
    /// <summary>目標充填量の不感帯（± 0.2 秒）。この範囲を外れたら PLC 差し込み／フレーム結合で 20 ms ずつ戻す。</summary>
    private const int PlaybackToleranceFrames = PlaybackSampleRate / 5;
    /// <summary>この量（6 秒）を超えて溜まったら届いた音声を捨てる（微調整が追いつかないほど溜まったときの保険）。</summary>
    private const int PlaybackMaxBufferedFrames = PlaybackSampleRate * 6;
    /// <summary>再生キューの容量。上限まで溜まった状態で無音の先積みと 1 回分の復号音声を足しても、再生スレッドが空き待ちで止まらない大きさ。</summary>
    private static readonly TimeSpan PlaybackQueueDuration = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private readonly CoreExecutionStatusBoard _status = new();
    private readonly double[] _pcmLeft = new double[FftSize];
    private readonly double[] _pcmRight = new double[FftSize];
    private readonly Complex[] _fftLeft = new Complex[FftSize];
    private readonly Complex[] _fftRight = new Complex[FftSize];
    private int _captureSampleRate = StreamConstants.DefaultSampleRate;
    private long _pcmWriteTotal;
    private long _lastFftPublishMs = -1;
    private int _packetsReceived;
    private int _packetErrors;
    private double _speedDeviationPercent;
    private Task? _worker;
    private CancellationTokenSource? _cts;
    private (bool Success, string Message)? _completion;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private byte[] _cover = Array.Empty<byte>();
    private int _coverReceivedBlocks;
    private int _coverTotalBlocks;
    private int _displayKbps;
    private RealtimePcmPlayer? _player;
    private bool _disposed;

    /// <summary>共有状態。</summary>
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

    /// <summary>表示用タイトル。</summary>
    public string Title
    {
        get { lock (_sync) { return _title; } }
    }

    /// <summary>表示用アーティスト。</summary>
    public string Artist
    {
        get { lock (_sync) { return _artist; } }
    }

    /// <summary>ジャケ写バイト。</summary>
    public byte[] CoverBytes
    {
        get { lock (_sync) { return _cover; } }
    }

    /// <summary>取得済みのジャケ写ブロック数。</summary>
    public int CoverReceivedBlocks
    {
        get { lock (_sync) { return _coverReceivedBlocks; } }
    }

    /// <summary>ジャケ写の総ブロック数（未受信なら 0）。</summary>
    public int CoverTotalBlocks
    {
        get { lock (_sync) { return _coverTotalBlocks; } }
    }

    /// <summary>検出速度 kbps。</summary>
    public int DisplayKbps
    {
        get { lock (_sync) { return _displayKbps; } }
    }

    /// <summary>受理したパケット数。</summary>
    public int PacketsReceived
    {
        get { lock (_sync) { return _packetsReceived; } }
    }

    /// <summary>ヘッダーは読めたが受理できなかったパケット数。</summary>
    public int PacketErrors
    {
        get { lock (_sync) { return _packetErrors; } }
    }

    /// <summary>推定した再生速度の偏差（%。正なら録音時より速い）。</summary>
    public double SpeedDeviationPercent
    {
        get { lock (_sync) { return _speedDeviationPercent; } }
    }

    /// <summary>
    /// 完了結果を取り出します。
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
    /// 受信中の復号音声の再生音量を変更します。
    /// </summary>
    /// <param name="volume">再生音量 0〜1。</param>
    public void SetOutputVolume(double volume)
    {
        lock (_sync)
        {
            _player?.SetOutputVolume(volume);
        }
    }

    /// <summary>
    /// 受信を開始します。
    /// </summary>
    /// <param name="settings">受信設定。</param>
    public void Start(StreamRxSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_worker is { IsCompleted: false })
            {
                throw new InvalidOperationException("Stream RX is already running.");
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _completion = null;
            _title = string.Empty;
            _artist = string.Empty;
            _cover = Array.Empty<byte>();
            _coverReceivedBlocks = 0;
            _coverTotalBlocks = 0;
            _displayKbps = 0;
            _packetsReceived = 0;
            _packetErrors = 0;
            _speedDeviationPercent = 0;
            _pcmWriteTotal = 0;
            _lastFftPublishMs = -1;
            // 送信側と OFDM シンボル長を揃えるため 44.1 kHz で復調する（デバイスレートからは取り込み時に変換）。
            _captureSampleRate = StreamConstants.DefaultSampleRate;
            // 画面が次に読む前に前回の FFT / I-Q / エラー率を消す。
            _status.BeginRun(CoreViewText.StreamRxRunningTitle);
            _status.SetAnalyzing(false);
            _status.SetFftStereoMode(true);
            _worker = Task.Run(() => Run(settings, token), token);
        }
    }

    /// <summary>
    /// 受信を停止します。
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
    /// 音声入力を受信し、FFT・パケット・曲情報を共有ボードへ公開します。
    /// </summary>
    /// <param name="settings">受信設定。</param>
    /// <param name="token">停止用のキャンセルトークン。</param>
    private void Run(StreamRxSettings settings, CancellationToken token)
    {
        _status.BeginRun(CoreViewText.StreamRxRunningTitle);
        _status.SetAnalyzing(false);
        _status.SetFftStereoMode(true);
        RealtimePcmCapture? capture = null;
        RealtimePcmPlayer? player = null;
        StreamPlaybackWorker? playback = null;
        StreamRxPipeline? pipeline = null;
        try
        {
            pipeline = new StreamRxPipeline(_captureSampleRate, PlaybackSampleRate) { PacketReported = PublishPacket };

            player = new RealtimePcmPlayer();
            player.Start(settings.OutputDevice, PlaybackSampleRate, ChannelMode.Stereo, settings.OutputVolume, PlaybackQueueDuration);
            playback = new StreamPlaybackWorker(player, PlaybackSampleRate, PlaybackTargetFrames, PlaybackToleranceFrames, PlaybackMaxBufferedFrames);
            playback.Start();
            lock (_sync)
            {
                _player = player;
            }

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
            capture.Start(settings.InputDevice, ChannelMode.Stereo, _captureSampleRate, settings.InputVolume);

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

                PublishFft(item.Value.L, item.Value.R);
                pipeline.PushCapture(item.Value.L, item.Value.R);
                playback.Enqueue(pipeline.PumpPackets(out var statusMsg));
                if (playback.Fault is { } fault)
                {
                    throw new InvalidOperationException(fault.Message, fault);
                }

                lock (_sync)
                {
                    _title = pipeline.Meta.GetTitleText();
                    _artist = pipeline.Meta.GetArtistText();
                    _cover = pipeline.Meta.GetCoverBytes();
                    _coverReceivedBlocks = pipeline.Meta.CoverReceivedBlocks;
                    _coverTotalBlocks = pipeline.Meta.CoverTotalBlocks;
                    _packetsReceived = pipeline.PacketsReceived;
                    _packetErrors = pipeline.PacketErrors;
                    _speedDeviationPercent = pipeline.SpeedDeviation * 100.0;
                    if (pipeline.DetectedModeId is { } mode)
                    {
                        _displayKbps = StreamMode.Resolve(mode).DisplayKbps;
                    }
                }

                _status.SetProgress(new CoreProgressInfo(
                    CurrentFrame: CoreFrameKind.Bd,
                    CurrentBlockIndex: 0,
                    PassIndex: 0,
                    AcceptedBlockCount: 0,
                    TotalBlockCount: 0,
                    ProgressPercent: 50.0));
                _ = statusMsg;
            }

            _status.Complete(faulted: false);
            Complete(true, CoreViewText.StreamRxStopped);
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
            lock (_sync)
            {
                _player = null;
            }

            capture?.Dispose();
            playback?.Dispose();
            player?.Dispose();
            pipeline?.Dispose();
        }
    }

    /// <summary>
    /// 受信 PCM をリングへ載せ、スロットル付きで Hanning 窓 FFT を共有ボードへ公開します（送信側と同じ表示）。
    /// </summary>
    /// <param name="left">キャプチャした L PCM。</param>
    /// <param name="right">キャプチャした R PCM。</param>
    private void PublishFft(Complex[] left, Complex[] right)
    {
        var len = Math.Min(left.Length, right.Length);
        for (var i = 0; i < len; i++)
        {
            var idx = (int)(_pcmWriteTotal % FftSize);
            _pcmLeft[idx] = left[i].Real;
            _pcmRight[idx] = right[i].Real;
            _pcmWriteTotal++;
        }

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
        PerformanceFftAnalyzer.ComputeSpectrumInPlace(_fftLeft, PerformanceFftWindowKind.Hanning);
        PerformanceFftAnalyzer.ComputeSpectrumInPlace(_fftRight, PerformanceFftWindowKind.Hanning);
        _status.SetFftStereoFrames(_fftLeft, _fftRight, _captureSampleRate);
    }

    /// <summary>
    /// 1 パケット分の I-Q（曲情報／データ部の等化後シンボル）とビタビ中間訂正率（L/R）を共有ボードへ公開します。
    /// </summary>
    /// <param name="report">パイプラインからの受信結果。</param>
    private void PublishPacket(StreamRxPacketReport report)
    {
        if (report.IqPoints.Length > 0)
        {
            _status.BeginIqCapture(report.Mode.Subcarriers, report.Mode.Modulation);
            _status.AppendIqFrame(report.IqPoints, report.IqGroups);
        }

        if (report.Body is { } body)
        {
            // グラフはデコーダ種別ごとに 1 系列なので、パケット全体（L+R）の訂正率を単一系列で載せる
            _status.SetErrorRate(
                body.CorrectionRate * 100.0,
                CoreFrameKind.Bd,
                CoreEccDecoderKind.Viterbi);
        }
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
    /// 受信を停止し、ワーカーを破棄します。
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
