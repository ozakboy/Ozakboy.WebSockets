# Ozakboy.WebSockets

A WebSocket client built to stay up for weeks, on top of the BCL `ClientWebSocket` and nothing else.

English | [繁體中文](README_zh-TW.md)

```
dotnet add package Ozakboy.WebSockets
```

Requires .NET 10. Depends only on `Ozakboy.Core.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` — no third-party libraries anywhere in the graph.

---

## Why this exists

`ClientWebSocket` gives you a connection. It does not give you a client that survives a Tuesday.

A market data stream runs for days. In that time the network drops, the peer restarts for maintenance, and the consumer occasionally falls behind. None of that is exceptional; it is the normal operating condition. This package handles it:

| Problem | What happens here |
| --- | --- |
| Connection drops | Reconnects automatically with backoff and jitter from a `RetryPolicy` |
| Reconnect succeeds but nothing arrives | Subscriptions are replayed on every connection |
| Peer stops sending without disconnecting | An idle timeout declares the connection dead and replaces it |
| Consumer falls behind | A bounded queue with a configurable strategy, and every dropped message is observable |
| Something fails | A `Result`, not an exception |

It knows nothing about any exchange. No endpoints, no message formats, no subscription protocols, and no deserialisation — payloads come out as the raw text or bytes that arrived.

---

## Three things we measured

These came out of a 200-second run against a real exchange, and each one shaped the design.

### 1. Your application never sees a ping

`ClientWebSocket` answers the peer's pings with a pong by itself, at the protocol layer. There is no API to observe those frames, intercept them, or send one yourself. Tutorials telling you to reply to a ping manually are about other libraries; here the code would compile and send nothing.

So the heartbeat in this package is not about answering the peer. It is about detecting whether the peer is still there:

```csharp
options.IdleTimeout = TimeSpan.FromSeconds(60);
```

Nothing received in 60 seconds means the connection is dead — abort it, reconnect, replay the subscriptions. Set it comfortably above the longest normal silence in your stream.

### 2. Cancelling a receive aborts the connection

This one is easy to get wrong because the code looks right. Passing a `CancellationToken` to `ReceiveAsync` and cancelling it does not mean "stop receiving". It means "abort the connection": the socket moves to `WebSocketState.Aborted`, and from there a graceful close is impossible — the close frame fails to send and the peer sees a connection that was yanked away.

The receive loop here always passes `CancellationToken.None`. Stopping works the other way round: send the close frame, wait for the peer to echo it, let the loop end when it sees `WebSocketMessageType.Close`. The socket finishes at `Closed`. Cancellation is reserved for the hard timeout when the peer never answers, and only then is `Abort` used.

### 3. Connected is not the same as receiving

We watched a handshake succeed, the socket sit at `WebSocketState.Open`, and 200 seconds pass without a single frame. No exception. No disconnect. Nothing in the state to look at. Something in the network path was letting the handshake through and swallowing the data stream.

Checking `WebSocketState` cannot detect that. Only "how long since the last message" can — which is the idle timeout from point 1, and the reason it is on by default.

---

## Using it

```csharp
var options = new WebSocketClientOptions
{
    Uri = new Uri("wss://stream.example.com/ws"),
    IdleTimeout = TimeSpan.FromSeconds(60),
    MaxReconnectAttempts = null,            // unlimited; the default
    QueueCapacity = 4096,
    BackpressureStrategy = BackpressureStrategy.DropOldest,
};

await using var client = new WebSocketClient(options, logger);

client.MessageDropped += (_, e) =>
    logger.LogWarning("Dropped a message; {Total} lost so far", e.TotalDropped);

await client.SubscribeAsync(new WebSocketSubscription(
    id: "btcusdt-trades",
    subscribePayload: """{"method":"SUBSCRIBE","params":["btcusdt@trade"],"id":1}""",
    unsubscribePayload: """{"method":"UNSUBSCRIBE","params":["btcusdt@trade"],"id":2}"""));

var connect = await client.ConnectAsync();
if (connect.IsFailure)
{
    logger.LogError("Could not connect: {Error}", connect.Error);
    return;
}

await foreach (var item in client.Messages(cancellationToken))
{
    if (item.TryGetValue(out var message))
    {
        Handle(message.Text!);
        continue;
    }

    // A failure means the connection dropped here and data may be missing.
    // IsTransient says which kind it is: true is a gap and the stream carries on,
    // false is terminal — the client has stopped and this is the last element.
    if (item.Error.IsTransient)
    {
        logger.LogWarning("Gap in the stream: {Error}", item.Error);
        continue;
    }

    item.Error.TryGetInt64(WebSocketErrorDataKeys.Attempts, out var attempts);
    logger.LogError("The client has stopped after {Attempts} attempts: {Error}", attempts, item.Error);
}
```

Nothing here asks you to branch on an error code. `IsTransient` is the single source of truth for "is this worth
retrying", and every error this package produces is categorised so that it answers correctly.

Subscriptions registered before connecting go out with the first connection. Subscriptions registered while disconnected are kept and go out with the next one. Every connection replays the full list.

### Starting when the peer might not be up yet

`ConnectAsync` makes one attempt and tells you whether it worked, which is usually what you want at start-up. For a process that runs around the clock and should simply wait for the peer to appear:

```csharp
client.Start();     // returns immediately; retries with backoff until connected
```

---

## Backpressure

The queue is bounded, always. An unbounded queue in a process that runs for weeks is a memory leak with extra steps.

| Strategy | Drops | Use it for |
| --- | --- | --- |
| `DropOldest` (default) | The oldest queued message | Market data, where the newest value is the useful one |
| `DropNewest` | The message that just arrived | Ordered feeds where older entries cannot be skipped |
| `Wait` | Nothing — stalls the receive loop | When nothing may be lost, and the consumer is only briefly slow |

`Wait` has a cost worth understanding: once the receive loop stalls, the peer's send buffer and the TCP window fill up and the peer usually drops the connection, and the idle detector will eventually conclude the connection is dead because nothing new is arriving. It trades losing messages for losing the connection.

Whichever you pick, drops are never silent. They surface through the `MessageDropped` event and the `MessagesDropped` counter. A trading system that quietly misses a candle will trade on wrong data, and nobody will know why.

---

## Watching it

```csharp
var stats = client.Statistics;
```

The two numbers that matter most:

- `MessagesDropped` — anything above zero means data has already gone unprocessed.
- `ReconnectCount` vs `SubscriptionReplayCount` — these should grow together. Reconnects climbing while replays stay put is what "connected but never resubscribed" looks like.

`State` and `CloseReason` tell a normal shutdown apart from one that needs an alert. Everything ends at `Closed`, so the reason is what separates `CallerRequested` from `ReconnectAttemptsExhausted` (the attempts ran out) and `UnrecoverableError` (a failure retrying could not fix — look on your own side, not at the peer).

---

## Error codes

Branch on `WebSocketErrorCodes`, not on message text. Messages are for people and get rewritten; codes are part of the contract.

| Code | Category | Meaning |
| --- | --- | --- |
| `ws.options_invalid` | Validation | The configuration is unusable |
| `ws.connect_failed` | Network | Handshake failed or refused |
| `ws.connect_timeout` | Timeout | Handshake did not finish in time |
| `ws.connection_lost` | Network | The connection dropped; a gap in the data |
| `ws.idle_timeout` | Timeout | Nothing arrived within the idle window |
| `ws.reconnect_exhausted` | **Exhausted** (not transient) | Gave up after the attempts ran out; the client has stopped — **the last element in the stream** |
| `ws.unrecoverable` | **Exhausted** (not transient) | Gave up on a failure retrying cannot fix, without retrying once — **the last element in the stream** |
| `ws.not_connected` | Unavailable, or **Exhausted** once the client is closed | Nothing to send on right now |
| `ws.send_failed` | Network | The send failed |
| `ws.subscription_replay_failed` | Network | A replay failed; the connection was abandoned and retried |
| `ws.subscription_not_found` | NotFound | No subscription with that id |
| `ws.message_too_large` | Network | One message exceeded `MaxMessageSize` |
| `ws.invalid_state` | Conflict | The lifecycle state does not allow this |
| `ws.cancelled` | Cancelled | The caller cancelled |

The categories are chosen so that `IsTransient` tells the truth, which means you never have to read this table to
decide whether to retry. `ws.reconnect_exhausted` is the case worth spelling out: the peer genuinely is unavailable,
but this client instance is finished and retrying it can never work — retrying means constructing a new client — so it
is `Exhausted`, not the transient `Unavailable`. `ws.not_connected` follows the same rule: transient while the client
is connecting or reconnecting, `Exhausted` once it has closed for good.

The loop applies that same rule to itself. A failure whose `IsTransient` is `false` is never retried — retrying could
not change the outcome — so the client ends with `ws.unrecoverable` and `CloseReason.UnrecoverableError` instead of
backing off forever. Transient failures still reconnect without limit.

The numbers in an error message are also in `Error.Data`, so you never have to parse the text. The keys are public
constants on `WebSocketErrorDataKeys` — read them from there rather than writing the literal, because a typo in a
key has no symptom at all: it compiles, it does not throw, and `TryGetXxx` just returns `false`.

| Code | Data keys (`WebSocketErrorDataKeys`) |
| --- | --- |
| `ws.reconnect_exhausted` | `Attempts` |
| `ws.unrecoverable` | `InnerCode`, `InnerCategory`, `Attempts` |
| `ws.connect_timeout`, `ws.idle_timeout` | `TimeoutMs` |
| `ws.message_too_large` | `LimitBytes` |
| `ws.subscription_not_found` | `SubscriptionId` |
| `ws.subscription_replay_failed` | `SubscriptionId`, `InnerCode` |
| `ws.not_connected` | `State` |
| `ws.invalid_state` | `State`, `Operation` |
| `ws.cancelled` | `Operation` |

`Attempts` is a count, `TimeoutMs` is milliseconds and `LimitBytes` is bytes — all three read with `TryGetInt64`.
The rest are strings read with `TryGetData`; `State` holds a `WebSocketClientState` name, `InnerCode` a
`WebSocketErrorCodes` value, and `InnerCategory` an `ErrorCategory` name.

```csharp
error.TryGetInt64(WebSocketErrorDataKeys.Attempts, out var attempts);
error.TryGetData(WebSocketErrorDataKeys.InnerCode, out var innerCode);
```

---

## Testing against it

`IWebSocketConnection` and `IWebSocketConnectionFactory` are public so you can substitute the transport. Combined with a `TimeProvider`, that lets you drive reconnects, idle timeouts, and backoff on a fake clock with no network and no waiting:

```csharp
var client = new WebSocketClient(options, myFakeFactory, logger, myFakeClock);
```

That is exactly how this package's own tests work — 105 of them, none of which open a socket.

---

## Licence

MIT. See [LICENSE](LICENSE).
