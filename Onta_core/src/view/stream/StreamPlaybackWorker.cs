using System.Numerics;
using Onta.Stream;
using Onta.View.Core;

namespace Onta.View.Stream;

/// <summary>
/// ストリーム再生の Opus 復号と再生キューへの投入を、復調とは別のスレッドで行います。
/// </summary>
/// <remarks>
/// 復調スレッドは受理したパケットの Opus フレーム（未復号）を <see cref="Enqueue"/> で渡すだけにし、
/// 復号・PLC・充填量の微調整はすべてこのスレッドの <see cref="StreamPlayoutRegulator"/> が行う（Opus デコーダーの状態を 1 スレッドに閉じる）。
/// 復調が遅れてフレームが届かない間も一定間隔でバッファを見て、減りすぎたら PLC で埋める。
/// </remarks>
internal sealed class StreamPlaybackWorker : IDisposable
{
    /// <summary>フレームが届いていないときにバッファを見直す間隔（ミリ秒）。</summary>
    private const int PollIntervalMs = 10;

    /// <summary>停止時にスレッドの終了を待つ時間（ミリ秒）。</summary>
    private const int StopTimeoutMs = 2000;

    /// <summary>
    /// 再生バッファがほぼ空のとき、入力からこれより遅れて復調されたパケットは再生せず捨てる（秒）。
    /// 受信開始直後は速度の総当たりで復調が遅れ、追いついた時点で溜まった分がまとめて届く。無音の先詰めに積み増すと、
    /// 充填量の微調整（約 20% 速）で溜まりを解消するまで早回しが続くため。
    /// </summary>
    private const double MaxStartLagSeconds = 1.0;

    /// <summary>続けて捨てる音声の上限（秒）。復調が実時間に追いつかないままでも、これを超えたら再生する。</summary>
    private const double MaxStartSkipSeconds = 8.0;

    /// <summary>Opus 1 フレームの長さ（秒）。</summary>
    private const double OpusFrameSeconds =
        Onta.Stream.Opus.OpusEncoder.FrameSamplesPerChannel / (double)Onta.Stream.Opus.OpusEncoder.OpusSampleRate;

    private readonly object _gate = new();
    private readonly Queue<StreamRxAudioPacket> _queue = new();
    private readonly RealtimePcmPlayer _player;
    private readonly StreamPlayoutRegulator _regulator;
    private readonly int _sampleRate;
    private readonly int _maxBufferedFrames;
    private readonly int _stallThresholdFrames;
    private readonly Thread _thread;
    private volatile bool _stop;
    private Exception? _fault;
    private bool _disposed;

    /// <summary>
    /// 再生先と充填量の目標を指定して構築します。
    /// </summary>
    /// <param name="player">再生先（開始済み）。</param>
    /// <param name="sampleRate">復号音声のサンプリング周波数。</param>
    /// <param name="targetFrames">再生バッファの目標充填量（片チャネルのサンプル数）。</param>
    /// <param name="toleranceFrames">目標の不感帯（片チャネルのサンプル数）。</param>
    /// <param name="maxBufferedFrames">これを超えて溜まったら届いた分を捨てる上限（片チャネルのサンプル数）。</param>
    public StreamPlaybackWorker(RealtimePcmPlayer player, int sampleRate, int targetFrames, int toleranceFrames, int maxBufferedFrames)
    {
        _player = player;
        _sampleRate = Math.Max(1, sampleRate);
        _maxBufferedFrames = maxBufferedFrames;
        // 復号時の無音先詰め（目標の 1/4 未満）より上で PLC を始め、先詰めと取り合わないようにする
        _stallThresholdFrames = targetFrames / 3;
        _regulator = new StreamPlayoutRegulator(sampleRate)
        {
            BufferedFrames = () => player.BufferedSampleFrames,
            TargetFrames = targetFrames,
            ToleranceFrames = toleranceFrames,
        };
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Onta stream playback",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    /// <summary>再生スレッドで起きた例外（無ければ null）。</summary>
    public Exception? Fault
    {
        get { lock (_gate) { return _fault; } }
    }

    /// <summary>
    /// 再生スレッドを開始します。
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _thread.Start();
    }

    /// <summary>
    /// 受理したパケットの Opus フレームを再生スレッドへ渡します。
    /// </summary>
    /// <param name="packets">復調スレッドが取り出したパケット。</param>
    public void Enqueue(IReadOnlyList<StreamRxAudioPacket> packets)
    {
        if (packets.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var packet in packets)
            {
                _queue.Enqueue(packet);
            }
        }
    }

    /// <summary>
    /// 届いたフレームを復号して再生キューへ積み、届かない間はバッファが減りすぎたら PLC で埋めます。
    /// </summary>
    private void Run()
    {
        var pending = new List<StreamRxAudioPacket>();
        var output = new List<(double[] Left, double[] Right)>();
        var skippedSeconds = 0.0;
        try
        {
            while (!_stop)
            {
                lock (_gate)
                {
                    pending.AddRange(_queue);
                    _queue.Clear();
                }

                output.Clear();
                if (pending.Count > 0)
                {
                    foreach (var packet in pending)
                    {
                        if (packet.LagSeconds > MaxStartLagSeconds
                            && skippedSeconds < MaxStartSkipSeconds
                            && _regulator.IsStarved(output))
                        {
                            skippedSeconds += packet.Frames.Count * OpusFrameSeconds;
                            continue;
                        }

                        skippedSeconds = 0.0;
                        _regulator.PendingFrames = (int)Math.Round(packet.LagSeconds * _sampleRate);
                        _regulator.ConcealLost(packet.LostFrames, output);
                        _regulator.Decode(packet.Frames, output);
                    }

                    pending.Clear();
                }
                else if (_player.BufferedSampleFrames < _stallThresholdFrames)
                {
                    _regulator.ConcealStall(output);
                }

                if (output.Count > 0)
                {
                    Play(output);
                }
                else
                {
                    Thread.Sleep(PollIntervalMs);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止で再生先が閉じられた
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _fault = ex;
            }
        }
    }

    /// <summary>
    /// 復号した PCM を再生キューへ積みます。微調整が追いつかないほど溜まったときだけ、届いた分を捨てます。
    /// </summary>
    /// <param name="frames">復号した PCM。</param>
    private void Play(List<(double[] Left, double[] Right)> frames)
    {
        // キューを丸ごと消すと上限分が一度に飛ぶので、今回届いた分だけを捨てる
        if (_player.BufferedSampleFrames > _maxBufferedFrames)
        {
            return;
        }

        var total = 0;
        foreach (var (l, _) in frames)
        {
            total += l.Length;
        }

        if (total == 0)
        {
            return;
        }

        var left = new Complex[total];
        var right = new Complex[total];
        var offset = 0;
        foreach (var (l, r) in frames)
        {
            var n = Math.Min(l.Length, r.Length);
            for (var i = 0; i < n; i++)
            {
                left[offset + i] = new Complex(l[i], 0.0);
                right[offset + i] = new Complex(r[i], 0.0);
            }

            offset += l.Length;
        }

        _player.AddSamples(left, right);
    }

    /// <summary>
    /// 再生スレッドを止め、Opus デコーダーを解放します。再生先の破棄は呼び出し側が行います。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop = true;
        if (_thread.IsAlive && !_thread.Join(StopTimeoutMs))
        {
            // 再生キューの空き待ちで止まっている。再生先を閉じれば抜けるので、デコーダーは解放しない
            return;
        }

        _regulator.Dispose();
    }
}
