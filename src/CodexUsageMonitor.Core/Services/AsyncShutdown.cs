namespace CodexUsageMonitor.Core.Services;

public static class AsyncShutdown
{
    public static async Task WaitAsync(
        IEnumerable<Task> tasks,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            // 关机路径有明确上限；未完成的外部进程或磁盘写入由进程退出回收。
        }
    }
}
