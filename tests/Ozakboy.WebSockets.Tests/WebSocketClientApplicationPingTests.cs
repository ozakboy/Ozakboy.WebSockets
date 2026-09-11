namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 應用層 ping 是選用功能。<see cref="System.Net.WebSockets.ClientWebSocket"/> 的協定層 ping/pong 由它自己
/// 處理、應用層完全看不到,所以這裡送的是「某些交易所自訂的、長得像普通訊息的 ping」,預設關閉,
/// 因為不能假設對方認得。
/// The application-level ping is optional. <see cref="System.Net.WebSockets.ClientWebSocket"/> handles
/// protocol-level ping/pong internally and invisibly, so what is sent here is the kind of custom ping some exchanges
/// define as an ordinary message. It is off by default because the peer cannot be assumed to understand it.
/// </summary>
[TestClass]
public sealed class WebSocketClientApplicationPingTests
{
    [TestMethod]
    public async Task 設定了間隔_會依間隔送出()
    {
        var counter = 0;

        await using var harness = new ClientHarness(options =>
        {
            options.ApplicationPingInterval = TimeSpan.FromSeconds(20);
            options.ApplicationPingPayloadFactory = () =>
                $$"""{"op":"ping","id":{{Interlocked.Increment(ref counter)}}}""";
        });

        await harness.Client.ConnectAsync();

        harness.Time.Advance(TimeSpan.FromSeconds(20));
        await Wait.UntilAsync(() => harness.Factory[0].SentTexts.Count >= 1, "送出第一個 ping");

        harness.Time.Advance(TimeSpan.FromSeconds(20));
        await Wait.UntilAsync(() => harness.Factory[0].SentTexts.Count >= 2, "送出第二個 ping");

        var expected = new[] { """{"op":"ping","id":1}""", """{"op":"ping","id":2}""" };
        CollectionAssert.AreEqual(expected, harness.Factory[0].SentTexts.Take(2).ToArray());
    }

    [TestMethod]
    public async Task 未設定_完全不送任何東西()
    {
        await using var harness = new ClientHarness();
        await harness.Client.ConnectAsync();

        harness.Time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(20);

        Assert.AreEqual(0, harness.Factory[0].SentTexts.Count);
    }

    [TestMethod]
    public async Task 內容工廠擲出例外_不會讓客戶端停擺()
    {
        await using var harness = new ClientHarness(options =>
        {
            options.ApplicationPingInterval = TimeSpan.FromSeconds(10);
            options.ApplicationPingPayloadFactory = () => throw new InvalidOperationException("故意爆炸");
        });

        await harness.Client.ConnectAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(20);

        Assert.AreEqual(WebSocketClientState.Connected, harness.Client.State);
        Assert.AreEqual(1, harness.Factory.CreateCount);
    }

    [TestMethod]
    public async Task 斷線期間_ping送不出去也不會讓客戶端停擺()
    {
        await using var harness = new ClientHarness(options =>
        {
            options.ApplicationPingInterval = TimeSpan.FromSeconds(10);
            options.ApplicationPingPayloadFactory = () => "ping";
        });
        harness.StartConsuming();
        harness.Factory.Configure = (connection, ordinal) =>
        {
            if (ordinal == 1)
            {
                connection.SendException = new System.Net.WebSockets.WebSocketException("送不出去");
            }
        };

        await harness.Client.ConnectAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        await Task.Delay(20);

        Assert.AreEqual(WebSocketClientState.Connected, harness.Client.State);
    }
}
