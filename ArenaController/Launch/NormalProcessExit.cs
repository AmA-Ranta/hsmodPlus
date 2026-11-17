using System.Diagnostics;

namespace ArenaController;

/// <summary>Wait for the requested process to exit; timeout never authorizes a kill or launch.</summary>
public static class NormalProcessExit
{
    public static async Task WaitAsync(Func<bool> hasExited, Func<bool> cancelled, TimeSpan timeout,
        Func<TimeSpan>? elapsed = null, Func<Task>? pause = null)
    {
        var timer = Stopwatch.StartNew();
        elapsed ??= () => timer.Elapsed;
        pause ??= () => Task.Delay(250);
        while (true)
        {
            if (cancelled()) throw new OperationCanceledException("Normal game exit wait cancelled");
            if (hasExited()) return;
            if (elapsed() >= timeout)
                throw new TimeoutException("炉石正常退出尚未完成，已保留进程并停止本次重启。");
            await pause().ConfigureAwait(false);
        }
    }
}
