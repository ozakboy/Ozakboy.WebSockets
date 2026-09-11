using System.Net.WebSockets;

namespace Ozakboy.WebSockets.Tests;

[TestClass]
public sealed class WebSocketClientSubscriptionTests
{
    [TestMethod]
    public async Task SubscribeAsync_已連線_立刻送出訂閱訊息()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();

        var result = await harness.Client.SubscribeAsync(new WebSocketSubscription("btc", "sub-btc"));

        Assert.IsTrue(result.IsSuccess, result.ToString());
        var expected = new[] { "sub-btc" };
        CollectionAssert.AreEqual(expected, harness.Factory[0].SentTexts.ToArray());
        Assert.AreEqual(1, harness.Client.Subscriptions.Count);
    }

    [TestMethod]
    public async Task SubscribeAsync_送出失敗_訂閱仍然留在重放清單裡()
    {
        await using var harness = new ClientHarness();
        harness.Factory.Configure = (connection, ordinal) =>
        {
            if (ordinal == 1)
            {
                connection.SendException = new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "送不出去");
            }
        };

        await harness.Client.ConnectAsync();
        var result = await harness.Client.SubscribeAsync(new WebSocketSubscription("btc", "sub-btc"));

        Assert.IsTrue(result.IsFailure, "送不出去要如實回報");
        Assert.AreEqual(1, harness.Client.Subscriptions.Count, "登記與送出是兩件事,送失敗不該讓登記消失");
    }

    [TestMethod]
    public async Task SubscribeAsync_相同識別碼_只保留一筆且用新的內容()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();

        await harness.Client.SubscribeAsync(new WebSocketSubscription("btc", "sub-btc-v1"));
        await harness.Client.SubscribeAsync(new WebSocketSubscription("btc", "sub-btc-v2"));

        Assert.AreEqual(1, harness.Client.Subscriptions.Count);
        Assert.AreEqual("sub-btc-v2", harness.Client.Subscriptions[0].SubscribePayload);

        harness.Factory[0].PushFault();
        await harness.WaitForConnectionsAsync(2);
        await harness.WaitForStateAsync(WebSocketClientState.Connected);

        var expected2 = new[] { "sub-btc-v2" };
        CollectionAssert.AreEqual(expected2, harness.Factory[1].SentTexts.ToArray());
    }

    [TestMethod]
    public async Task UnsubscribeAsync_送出取消訊息並移出重放清單()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();
        await harness.Client.SubscribeAsync(new WebSocketSubscription("btc", "sub-btc", "unsub-btc"));

        var result = await harness.Client.UnsubscribeAsync("btc");

        Assert.IsTrue(result.IsSuccess, result.ToString());
        var expected3 = new[] { "sub-btc", "unsub-btc" };
        CollectionAssert.AreEqual(expected3, harness.Factory[0].SentTexts.ToArray());
        Assert.AreEqual(0, harness.Client.Subscriptions.Count);

        harness.Factory[0].PushFault();
        await harness.WaitForConnectionsAsync(2);
        await harness.WaitForStateAsync(WebSocketClientState.Connected);

        Assert.AreEqual(0, harness.Factory[1].SentTexts.Count, "取消掉的訂閱不該再被重放");
    }

    [TestMethod]
    public async Task UnsubscribeAsync_沒有取消訊息_僅移出清單()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();
        await harness.Client.SubscribeAsync(new WebSocketSubscription("btc", "sub-btc"));

        var result = await harness.Client.UnsubscribeAsync("btc");

        Assert.IsTrue(result.IsSuccess, result.ToString());
        var expected4 = new[] { "sub-btc" };
        CollectionAssert.AreEqual(expected4, harness.Factory[0].SentTexts.ToArray());
        Assert.AreEqual(0, harness.Client.Subscriptions.Count);
    }

    [TestMethod]
    public async Task UnsubscribeAsync_未連線_仍可移出清單()
    {
        await using var harness = new ClientHarness();
        await harness.Client.SubscribeAsync(new WebSocketSubscription("btc", "sub-btc", "unsub-btc"));

        var result = await harness.Client.UnsubscribeAsync("btc");

        Assert.IsTrue(result.IsSuccess, result.ToString());
        Assert.AreEqual(0, harness.Client.Subscriptions.Count);
    }

    [TestMethod]
    public async Task UnsubscribeAsync_識別碼不存在_回傳找不到()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();

        var result = await harness.Client.UnsubscribeAsync("nope");

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(WebSocketErrorCodes.SubscriptionNotFound, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.NotFound, result.Error.Category);
    }

    [TestMethod]
    public async Task UnsubscribeAsync_識別碼空白_擲出ArgumentException()
    {
        await using var harness = new ClientHarness();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => harness.Client.UnsubscribeAsync("   "));
    }

    [TestMethod]
    public void WebSocketSubscription_識別碼或內容空白_擲出ArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _ = new WebSocketSubscription("  ", "payload"));
        Assert.ThrowsExactly<ArgumentException>(() => _ = new WebSocketSubscription("id", "  "));
    }

    [TestMethod]
    public void WebSocketSubscription_敘述只含識別碼()
    {
        var subscription = new WebSocketSubscription("btc", """{"token":"secret"}""");

        Assert.AreEqual("Subscription(btc)", subscription.ToString());
        Assert.IsNull(subscription.UnsubscribePayload);
    }

    [TestMethod]
    public void WebSocketSubscription_相等性依內容比較()
    {
        var left = new WebSocketSubscription("btc", "sub", "unsub");
        var right = new WebSocketSubscription("btc", "sub", "unsub");

        Assert.AreEqual(left, right);
        Assert.AreEqual(left.GetHashCode(), right.GetHashCode());
    }
}
