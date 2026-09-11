namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 等待背景工作抵達某個狀態的輔助方法。
/// Helpers that wait for background work to reach a given state.
/// </summary>
/// <remarks>
/// 這裡的短暫等待是<b>執行緒同步</b>,不是在測時間相關的邏輯。逾時、退避、心跳一律用
/// <see cref="TestTimeProvider"/> 的假時鐘推進,絕不靠真的等待。
/// The short waits here are <b>thread synchronisation</b>, not tests of time-dependent logic. Timeouts, backoff, and
/// heartbeats are always driven by advancing <see cref="TestTimeProvider"/>, never by waiting for real time.
/// </remarks>
internal static class Wait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);

    public static async Task UntilAsync(Func<bool> condition, string description)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTime.UtcNow + DefaultTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }

        Assert.Fail($"等待條件逾時:{description}。Timed out waiting for: {description}.");
    }
}
