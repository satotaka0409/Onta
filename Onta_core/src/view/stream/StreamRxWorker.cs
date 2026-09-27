using Onta.Core;
using Onta.Stream;
using Onta.View.Core;

namespace Onta.View.Stream;

/// <summary>
/// ストリーム再生受信ワーカーです。
/// </summary>
internal sealed class StreamRxWorker : IDisposable
{
    private readonly object _sync = new();
    private readonly CoreExecutionStatusBoard _status = new();
    private Task? _worker;
    private CancellationTokenSource? _cts;
    private (bool Success, string Message)? _completion;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private byte[] _cover = Array.Empty<byte>();
    private int _displayKbps;
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

    /// <summary>検出速度 kbps。</summary>
    public int DisplayKbps
    {
        get { lock (_sync) { return _displayKbps; } }
    }

    /// <summary>
    /// 完了結果を取り出します。
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

    /// <summary>
    /// 受信を開始します。
    /// </summary>
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
            _displayKbps = 0;
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

    private void Run(StreamRxSettings settings, CancellationToken token)
    {
        _status.BeginRun("ストリーム受信");
        _status.SetAnalyzing(false);
        RealtimePcmCapture? capture = null;
        StreamRxPipeline? pipeline = null;
        try
        {
            pipeline = new StreamRxPipeline();

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
            capture.Start(settings.InputDevice, ChannelMode.Stereo, StreamConstants.SampleRate, settings.InputVolume);

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

                pipeline.PushCapture(item.Value.L, item.Value.R);
                // 再生／WAV 出力は UI から削除。復号結果（曲情報・状態）のみ利用する。
                _ = pipeline.Pump(out var statusMsg);

                lock (_sync)
                {
                    _title = pipeline.Meta.GetTitleText();
                    _artist = pipeline.Meta.GetArtistText();
                    _cover = pipeline.Meta.GetCoverBytes();
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
            Complete(true, "ストリーム受信停止");
        }
        catch (OperationCanceledException)
        {
            _status.Complete(faulted: true, "キャンセル");
            Complete(false, "キャンセル");
        }
        catch (Exception ex)
        {
            _status.Complete(faulted: true, ex.Message);
            Complete(false, ex.Message);
        }
        finally
        {
            capture?.Dispose();
            pipeline?.Dispose();
        }
    }

    private void Complete(bool success, string message)
    {
        lock (_sync)
        {
            _completion = (success, message);
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
