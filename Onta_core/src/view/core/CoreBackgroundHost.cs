using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Onta.Core;

namespace Onta.View.Core;

/// <summary>
/// Core 処理を単一バックグラウンドスレッドで実行するホストです。
/// </summary>
internal static class CoreBackgroundHost
{
    private static readonly object Sync = new();
    private static readonly bool RunInline = ResolveRunInline();

    private static BlockingCollection<WorkItem>? _queue;
    private static CancellationTokenSource? _hostCts;
    private static Thread? _workerThread;
    private static int _warmupQueued;

    /// <summary>
    /// バックグラウンドワーカースレッドを開始します。
    /// </summary>
    public static void Start()
    {
        if (RunInline)
        {
            return;
        }

        lock (Sync)
        {
            if (_workerThread is { IsAlive: true })
            {
                return;
            }

            _hostCts = new CancellationTokenSource();
            _queue = new BlockingCollection<WorkItem>();
            _workerThread = new Thread(() => WorkerLoop(_queue, _hostCts.Token))
            {
                IsBackground = true,
                Name = "Onta.Core.BackgroundHost"
            };
            _workerThread.Start();
        }
    }

    /// <summary>
    /// バックグラウンドワーカースレッドを停止し、未処理キューを破棄します。
    /// </summary>
    public static void Stop()
    {
        if (RunInline)
        {
            return;
        }

        Thread? thread;
        BlockingCollection<WorkItem>? queue;
        CancellationTokenSource? cts;

        lock (Sync)
        {
            thread = _workerThread;
            queue = _queue;
            cts = _hostCts;
            _workerThread = null;
            _queue = null;
            _hostCts = null;
            _warmupQueued = 0;
        }

        if (queue is not null && !queue.IsAddingCompleted)
        {
            queue.CompleteAdding();
        }

        cts?.Cancel();

        if (thread is not null && thread.IsAlive && !ReferenceEquals(thread, Thread.CurrentThread))
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        queue?.Dispose();
        cts?.Dispose();
    }

    /// <summary>
    /// Core 向け処理をキューへ投入して非同期実行します。
    /// </summary>
    /// <param name="action">実行する処理本体。</param>
    /// <param name="cancellationToken">呼び出し側キャンセル用トークン。</param>
    /// <returns>投入処理の完了を表すタスク。</returns>
    public static Task RunAsync(Action<CancellationToken> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (RunInline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            try
            {
                action(cancellationToken);
                return Task.CompletedTask;
            }
            catch (OperationCanceledException ex)
                when (ex.CancellationToken == cancellationToken || cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        Start();

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(action, cancellationToken, tcs);

        BlockingCollection<WorkItem>? queue;
        lock (Sync)
        {
            queue = _queue;
        }

        if (queue is null || queue.IsAddingCompleted)
        {
            tcs.TrySetException(new InvalidOperationException("Core background host is not running."));
            return tcs.Task;
        }

        try
        {
            queue.Add(item);
        }
        catch (InvalidOperationException ex)
        {
            tcs.TrySetException(ex);
        }

        return tcs.Task;
    }

    /// <summary>
    /// 初回遅延を減らすため、主要処理のウォームアップを1回だけキュー投入します。
    /// </summary>
    public static void QueueWarmup()
    {
        if (RunInline)
        {
            return;
        }

        if (Interlocked.Exchange(ref _warmupQueued, 1) == 1)
        {
            return;
        }

        _ = RunAsync(static cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var profile = new FileWavCodecProfile(
                ActiveSubcarriers: 8,
                ModulationScheme: ModulationScheme.Qpsk,
                ChannelMode: ChannelMode.Mono);
            _ = FileWavCodec.EstimateTransmissionDuration(profile, 4096);

            var config = new OfdmConfig(
                fftSize: profile.DataFftSize,
                activeSubcarriers: profile.ActiveSubcarriers,
                cyclicPrefixLength: profile.DataCyclicPrefixLength,
                ofdmSymbolCount: 1,
                modulationScheme: profile.ModulationScheme,
                channelMode: profile.ChannelMode,
                sampleRate: profile.SampleRate);
            var generator = new OfdmGenerator(config);
            _ = generator.GenerateUnmodulated(generator.SamplesPerOfdmSymbol);

            _ = Crc32.Compute(new byte[] { 0x01, 0x23, 0x45, 0x67 });
        });
    }

    /// <summary>
    /// キューから処理を取り出して順次実行するワーカーループです。
    /// </summary>
    /// <param name="queue">実行対象のワークキュー。</param>
    /// <param name="hostToken">ホスト停止用キャンセルトークン。</param>
    private static void WorkerLoop(BlockingCollection<WorkItem> queue, CancellationToken hostToken)
    {
        try
        {
            foreach (var item in queue.GetConsumingEnumerable(hostToken))
            {
                if (item.CancellationToken.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(item.CancellationToken);
                    continue;
                }

                try
                {
                    item.Action(item.CancellationToken);
                    if (item.CancellationToken.IsCancellationRequested)
                    {
                        item.Completion.TrySetCanceled(item.CancellationToken);
                    }
                    else
                    {
                        item.Completion.TrySetResult();
                    }
                }
                catch (OperationCanceledException ex)
                    when (ex.CancellationToken == item.CancellationToken || item.CancellationToken.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(item.CancellationToken);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ホスト停止時の通常終了。
        }
        finally
        {
            while (queue.TryTake(out var pending))
            {
                pending.Completion.TrySetCanceled();
            }
        }
    }

    /// <summary>
    /// キューに保持するワーク項目です。
    /// </summary>
    private readonly record struct WorkItem(
        Action<CancellationToken> Action,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion);

    /// <summary>
    /// インライン実行モードかどうかを判定します。
    /// テスト実行時はデフォルトでインラインに切り替えます。
    /// </summary>
    /// <returns>インライン実行する場合 true。</returns>
    private static bool ResolveRunInline()
    {
        var env = Environment.GetEnvironmentVariable("ONTA_CORE_INLINE");
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (env == "1")
            {
                return true;
            }

            if (env == "0")
            {
                return false;
            }

            if (bool.TryParse(env, out var parsed))
            {
                return parsed;
            }
        }

        var processName = Process.GetCurrentProcess().ProcessName;
        if (processName.Contains("testhost", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("vstest", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("xunit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
        {
            var name = assembly.GetName().Name;
            return !string.IsNullOrWhiteSpace(name)
                && (name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Microsoft.VisualStudio.TestPlatform", StringComparison.OrdinalIgnoreCase));
        });
    }
}




