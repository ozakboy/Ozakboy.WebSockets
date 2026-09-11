namespace Ozakboy.WebSockets.Tests;

/// <summary>
/// 產生 <see cref="FakeWebSocketConnection"/> 的工廠。每次重連都會拿到一條新的假連線,
/// 測試可以透過 <see cref="Configure"/> 針對「第幾次連線」指定不同的行為。
/// Produces <see cref="FakeWebSocketConnection"/> instances. Each reconnect gets a fresh fake, and
/// <see cref="Configure"/> lets a test give the nth connection different behaviour.
/// </summary>
internal sealed class FakeWebSocketConnectionFactory : IWebSocketConnectionFactory
{
    private readonly Lock _gate = new();
    private readonly List<FakeWebSocketConnection> _created = [];
    private readonly List<DateTimeOffset> _createdAt = [];
    private readonly TimeProvider _timeProvider;

    public FakeWebSocketConnectionFactory(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 每條連線被建立時的假時鐘讀值。重連退避的間隔就是拿相鄰兩個時間相減來驗證的,
    /// 完全不依賴真實時間,也不必猜計時器有沒有排好。
    /// The fake-clock reading when each connection was created. Backoff intervals are verified by subtracting
    /// adjacent entries, with no dependence on real time and no guessing about whether a timer is armed yet.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> CreatedAt
    {
        get
        {
            lock (_gate)
            {
                return [.. _createdAt];
            }
        }
    }

    /// <summary>
    /// 每次建立連線時呼叫,參數為新連線與它是第幾條(從 1 起算)。
    /// Invoked on each creation with the new connection and its one-based ordinal.
    /// </summary>
    public Action<FakeWebSocketConnection, int>? Configure { get; set; }

    public IReadOnlyList<FakeWebSocketConnection> Created
    {
        get
        {
            lock (_gate)
            {
                return [.. _created];
            }
        }
    }

    public int CreateCount
    {
        get
        {
            lock (_gate)
            {
                return _created.Count;
            }
        }
    }

    public FakeWebSocketConnection this[int index]
    {
        get
        {
            lock (_gate)
            {
                return _created[index];
            }
        }
    }

    public IWebSocketConnection Create()
    {
        FakeWebSocketConnection connection;
        int ordinal;

        lock (_gate)
        {
            connection = new FakeWebSocketConnection();
            _created.Add(connection);
            _createdAt.Add(_timeProvider.GetUtcNow());
            ordinal = _created.Count;
        }

        Configure?.Invoke(connection, ordinal);
        return connection;
    }
}
