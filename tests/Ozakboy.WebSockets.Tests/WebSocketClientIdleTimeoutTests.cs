using System.Net.WebSockets;

namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 閒置逾時是這個套件唯一能抓到「握手成功、socket 維持 Open、卻一個 frame 都收不到」那種故障的機制。
/// 這些測試因此刻意讓假連線保持 <see cref="WebSocketState.Open"/> 卻不送任何資料 —— 只檢查連線狀態的實作
/// 會在這裡全數通過而完全沒發現問題。
/// The idle timeout is the only thing here that can catch a handshake that succeeded, a socket that stayed Open, and
/// a stream that delivers nothing. These tests therefore keep the fake at <see cref="WebSocketState.Open"/> while
/// sending nothing: an implementation that only inspects the socket state would sail through and notice nothing.
/// </summary>
[TestClass]
public sealed class WebSocketClientIdleTimeoutTests
{
    [TestMethod]
    public async Task 連線正常但一直收不到訊息_觸發重連()
    {
        await using var harness = new ClientHarness(options =>
        {
            options.IdleTimeout = TimeSpan.FromSeconds(30);
            options.IdleCheckInterval = TimeSpan.FromSeconds(1);
        });
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        // 連線從頭到尾都是 Open,沒有例外、沒有斷線,就只是不送資料。
        // The connection is Open throughout: no exception, no disconnect, it simply sends nothing.
        Assert.AreEqual(WebSocketState.Open, harness.Factory[0].State);

        harness.Time.Advance(TimeSpan.FromSeconds(31));

        await harness.WaitForConnectionsAsync(2);
        await harness.WaitForStateAsync(WebSocketClientState.Connected);

        Assert.IsTrue(harness.Factory[0].WasAborted, "判定已死的連線應該被中止");
        await Wait.UntilAsync(
            () => harness.FailuresReceived.Any(error => error.Code == WebSocketErrorCodes.IdleTimeout),
            "串流中出現閒置逾時的失敗");
    }

    [TestMethod]
    public async Task 持續有訊息進來_不會誤判為已死()
    {
        await using var harness = new ClientHarness(options =>
        {
            options.IdleTimeout = TimeSpan.FromSeconds(30);
            options.IdleCheckInterval = TimeSpan.FromSeconds(1);
        });
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        for (var round = 1; round <= 5; round++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(20));
            harness.Factory[0].PushText($"tick-{round}");
            await harness.WaitForMessagesAsync(round);
        }

        Assert.AreEqual(1, harness.Factory.CreateCount, "有訊息就不該重連");
        Assert.AreEqual(WebSocketClientState.Connected, harness.Client.State);
    }

    [TestMethod]
    public async Task 閒置逾時停用_不會因為沒訊息而重連()
    {
        await using var harness = new ClientHarness(options => options.IdleTimeout = TimeSpan.Zero);
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        harness.Time.Advance(TimeSpan.FromHours(6));
        await Task.Delay(20);

        Assert.AreEqual(1, harness.Factory.CreateCount);
        Assert.AreEqual(WebSocketClientState.Connected, harness.Client.State);
    }

    [TestMethod]
    public async Task 閒置逾時_檢查間隔未設定時取四分之一()
    {
        await using var harness = new ClientHarness(options =>
        {
            options.IdleTimeout = TimeSpan.FromSeconds(40);
            options.IdleCheckInterval = null;
        });
        harness.StartConsuming();
        await harness.Client.ConnectAsync();

        // 檢查間隔為 10 秒,所以最壞情況會在逾時後 10 秒內發現。
        // The check runs every 10 s, so the worst case is noticed within 10 s of the timeout.
        harness.Time.Advance(TimeSpan.FromSeconds(50));

        await harness.WaitForConnectionsAsync(2);
    }

    [TestMethod]
    public void 閒置檢查間隔_未設定時為逾時的四分之一且有下限()
    {
        var options = new WebSocketClientOptions { IdleTimeout = TimeSpan.FromSeconds(60) };
        Assert.AreEqual(TimeSpan.FromSeconds(15), options.ResolveIdleCheckInterval());

        options.IdleTimeout = TimeSpan.FromMilliseconds(40);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), options.ResolveIdleCheckInterval());

        options.IdleCheckInterval = TimeSpan.FromSeconds(3);
        Assert.AreEqual(TimeSpan.FromSeconds(3), options.ResolveIdleCheckInterval());
    }
}
