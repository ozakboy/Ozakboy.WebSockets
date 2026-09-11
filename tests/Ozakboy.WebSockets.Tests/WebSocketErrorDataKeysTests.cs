namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 資料鍵是公開契約,值不能被改掉。消費端讀 <c>Error.Data</c> 用的就是這些字串,改一個字
/// 對方不會收到編譯錯誤、也不會擲例外,只會安靜地讀不到值。
/// The data keys are public contract and their values must not drift. Consumers read <c>Error.Data</c> with exactly
/// these strings; changing one gives them no compile error and no exception, just a value that silently never comes
/// back.
/// </summary>
[TestClass]
public sealed class WebSocketErrorDataKeysTests
{
    [TestMethod]
    public void 資料鍵常數_值就是契約上的字串()
    {
        // 兩邊都放進陣列再比,是為了避開「兩個常數比大小,結果編譯期就知道」的分析器警告;
        // 這裡真正要鎖的是值,不是表達式。
        // Both sides go through arrays to avoid the analyzer warning about comparing two compile-time constants;
        // what is being pinned here is the values, not the expression.
        string[] actual =
        [
            WebSocketErrorDataKeys.Attempts,
            WebSocketErrorDataKeys.TimeoutMs,
            WebSocketErrorDataKeys.LimitBytes,
            WebSocketErrorDataKeys.SubscriptionId,
            WebSocketErrorDataKeys.State,
            WebSocketErrorDataKeys.Operation,
            WebSocketErrorDataKeys.InnerCode,
            WebSocketErrorDataKeys.InnerCategory,
        ];

        string[] expected =
        [
            "attempts",
            "timeoutMs",
            "limitBytes",
            "subscriptionId",
            "state",
            "operation",
            "innerCode",
            "innerCategory",
        ];

        CollectionAssert.AreEqual(expected, actual);
    }

    /// <summary>
    /// 常數不是擺著好看的:每一個鍵都要真的能從對應錯誤的 <c>Error.Data</c> 讀出值來。
    /// The constants are not decoration: each one has to actually read a value back out of the matching error's
    /// <c>Error.Data</c>.
    /// </summary>
    [TestMethod]
    public async Task 用常數讀_讀得到實際錯誤帶的值()
    {
        await using var harness = new ClientHarness();

        var notFound = await harness.Client.UnsubscribeAsync("沒這個訂閱");

        Assert.IsTrue(notFound.IsFailure);
        Assert.IsTrue(
            notFound.Error.TryGetData(WebSocketErrorDataKeys.SubscriptionId, out var subscriptionId),
            "訂閱相關的錯誤要能用常數讀出識別碼");
        Assert.AreEqual("沒這個訂閱", subscriptionId);

        var notConnected = await harness.Client.SendAsync("哈囉");

        Assert.IsTrue(notConnected.IsFailure);
        Assert.IsTrue(
            notConnected.Error.TryGetData(WebSocketErrorDataKeys.State, out var state),
            "沒有連線的錯誤要能用常數讀出當下狀態");
        Assert.AreEqual(nameof(WebSocketClientState.Disconnected), state);
    }
}
