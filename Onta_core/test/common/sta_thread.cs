using System.Runtime.ExceptionServices;

namespace Onta.Core.Tests;

/// <summary>
/// WPF オブジェクト（FlowDocument・BitmapSource など）を扱うテスト処理を STA スレッドで実行します。
/// </summary>
internal static class StaThread
{
    /// <summary>
    /// 処理を STA スレッドで実行し、完了まで待ちます。例外は呼び出し元へ再スローします。
    /// </summary>
    /// <param name="action">実行する処理。</param>
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    /// <summary>
    /// 値を返す処理を STA スレッドで実行します。
    /// </summary>
    /// <param name="func">実行する処理。</param>
    /// <returns>処理の戻り値。</returns>
    public static T Run<T>(Func<T> func)
    {
        T result = default!;
        Run(() => { result = func(); });
        return result;
    }
}
