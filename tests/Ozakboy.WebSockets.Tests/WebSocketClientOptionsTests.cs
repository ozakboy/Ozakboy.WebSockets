namespace Ozakboy.WebSockets.Tests;

[TestClass]
public sealed class WebSocketClientOptionsTests
{
    [TestMethod]
    public void 預設值_符合長時間執行的取向()
    {
        var options = new WebSocketClientOptions();

        Assert.IsNull(options.MaxReconnectAttempts, "預設無限重連");
        Assert.AreEqual(TimeSpan.FromSeconds(60), options.IdleTimeout);
        Assert.AreEqual(BackpressureStrategy.DropOldest, options.BackpressureStrategy);
        Assert.AreEqual(1024, options.QueueCapacity);
        Assert.AreEqual(TimeSpan.Zero, options.ApplicationPingInterval, "應用層 ping 預設關閉,因為不能假設對方支援");
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.TransportKeepAliveInterval);
        Assert.IsTrue(options.ReconnectPolicy.JitterRatio > 0d, "重連必須有抖動");
    }

    [TestMethod]
    public void Validate_未設定Uri_失敗()
    {
        var result = new WebSocketClientOptions().Validate();

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(WebSocketErrorCodes.OptionsInvalid, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Validation, result.Error.Category);
    }

    [TestMethod]
    public void Validate_相對位址_失敗()
    {
        var options = new WebSocketClientOptions { Uri = new Uri("/stream", UriKind.Relative) };

        Assert.IsTrue(options.Validate().IsFailure);
    }

    [TestMethod]
    public void Validate_配置不是ws或wss_失敗()
    {
        var options = new WebSocketClientOptions { Uri = new Uri("http://example.invalid/") };

        Assert.IsTrue(options.Validate().IsFailure);
    }

    [TestMethod]
    public void Validate_ws與wss都接受()
    {
        Assert.IsTrue(new WebSocketClientOptions { Uri = new Uri("ws://example.invalid/") }.Validate().IsSuccess);
        Assert.IsTrue(new WebSocketClientOptions { Uri = new Uri("WSS://example.invalid/") }.Validate().IsSuccess);
    }

    [TestMethod]
    public void Validate_設了ping間隔卻沒給內容工廠_失敗()
    {
        var options = new WebSocketClientOptions
        {
            Uri = new Uri("wss://example.invalid/"),
            ApplicationPingInterval = TimeSpan.FromSeconds(20),
        };

        var result = options.Validate();

        Assert.IsTrue(result.IsFailure);
        Assert.IsTrue(result.Error!.Message.Contains("ApplicationPingPayloadFactory", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_訊息上限小於接收緩衝區_失敗()
    {
        var options = new WebSocketClientOptions
        {
            Uri = new Uri("wss://example.invalid/"),
            ReceiveBufferSize = 8192,
            MaxMessageSize = 4096,
        };

        Assert.IsTrue(options.Validate().IsFailure);
    }

    [TestMethod]
    public void 屬性驗證_不合法的值當場擲出()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new WebSocketClientOptions { MaxReconnectAttempts = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new WebSocketClientOptions { IdleTimeout = TimeSpan.FromSeconds(-1) });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new WebSocketClientOptions { IdleCheckInterval = TimeSpan.Zero });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new WebSocketClientOptions { ApplicationPingInterval = TimeSpan.FromSeconds(-1) });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new WebSocketClientOptions { ConnectTimeout = TimeSpan.Zero });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new WebSocketClientOptions { CloseTimeout = TimeSpan.Zero });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new WebSocketClientOptions { TransportKeepAliveInterval = TimeSpan.FromSeconds(-1) });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new WebSocketClientOptions { QueueCapacity = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new WebSocketClientOptions { ReceiveBufferSize = 512 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new WebSocketClientOptions { MaxMessageSize = 0 });
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            _ = new WebSocketClientOptions { ReconnectPolicy = null! });
    }

    [TestMethod]
    public void 標頭與子協定_可以設定()
    {
        var options = new WebSocketClientOptions { Uri = new Uri("wss://example.invalid/") };

        options.RequestHeaders["X-Api-Key"] = "placeholder";
        options.SubProtocols.Add("json");

        Assert.AreEqual("placeholder", options.RequestHeaders["x-api-key"], "標頭比對不分大小寫");
        var expected = new[] { "json" };
        CollectionAssert.AreEqual(expected, options.SubProtocols.ToArray());
    }
}
