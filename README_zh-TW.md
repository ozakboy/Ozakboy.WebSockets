# Ozakboy.WebSockets

建立在 BCL `ClientWebSocket` 之上、能連續跑上好幾週的 WebSocket 客戶端。

[English](README.md) | 繁體中文

```
dotnet add package Ozakboy.WebSockets
```

需要 .NET 10。相依只有 `Ozakboy.Core.Abstractions` 與 `Microsoft.Extensions.Logging.Abstractions`,整棵相依樹沒有任何第三方套件。

---

## 為什麼需要這個東西

`ClientWebSocket` 給你的是一條連線,不是一個撐得過禮拜二的客戶端。

行情串流一跑就是好幾天。這段期間網路會斷、對方會因為維護重啟、下游偶爾會處理不過來。這些都不是例外狀況,是日常。這個套件處理的就是這些:

| 遇到的狀況 | 這裡怎麼處理 |
| --- | --- |
| 連線中斷 | 用 `RetryPolicy` 算出的退避間隔(含抖動)自動重連 |
| 重連成功但收不到資料 | 每次連上都重放一次訂閱 |
| 對方不斷線但也不送資料 | 閒置逾時判定連線已死,直接換一條 |
| 下游跟不上 | 有界佇列 + 可設定的策略,而且每一則被丟掉的訊息都看得到 |
| 任何失敗 | 回傳 `Result`,不用例外 |

它完全不認識任何交易所:沒有端點、沒有訊息格式、沒有訂閱協定,也不做反序列化 —— 交出去的就是收到的原始文字或位元組。

---

## 三件實測出來的事

以下是對真實交易所做 200 秒實測的結果,每一件都直接決定了這個套件的設計。

### 一、應用層根本看不到 ping

`ClientWebSocket` 會自己在協定層回覆對方的 ping,沒有任何 API 能觀察那些 frame、攔截它們,或代你送一個出去。網路上叫你「收到 ping 要手動回 pong」的教學是針對別的函式庫寫的,照著做在這裡會編譯得過、但什麼都送不出去。

所以這個套件的心跳不是在回應對方,而是在偵測對方還在不在:

```csharp
options.IdleTimeout = TimeSpan.FromSeconds(60);
```

60 秒沒收到任何東西就當連線已死 —— 中止它、重連、重放訂閱。這個值要設得比你的串流正常會有的最長沉默期還寬一些。

### 二、取消接收等於中止連線

這個坑很容易踩,因為那段程式碼看起來完全正確。把 `CancellationToken` 傳給 `ReceiveAsync` 再取消它,語意不是「停止接收」而是「中止連線」:socket 會變成 `WebSocketState.Aborted`,從那一刻起就不可能優雅關閉了 —— 關閉 frame 送不出去,對方看到的是連線被硬扯斷。

本套件的接收迴圈一律傳 `CancellationToken.None`。停止的方式反過來做:先送出關閉 frame,等對方回覆,讓迴圈自己看到 `WebSocketMessageType.Close` 而結束,socket 停在 `Closed`。取消權杖只留給「對方不回應」時的硬逾時,也只有那時才會用到 `Abort`。

### 三、「連得上」不等於「收得到」

我們看過握手成功、socket 維持在 `WebSocketState.Open`、整整 200 秒沒有任何一個 frame 進來。沒有例外、沒有斷線、狀態上看不出任何異常。原因是網路路徑上有東西放行了握手卻吃掉了資料流。

檢查 `WebSocketState` 永遠發現不了這種故障,只有「多久沒收到訊息」能 —— 那就是第一點的閒置逾時,也是它預設就開著的理由。

---

## 怎麼用

```csharp
var options = new WebSocketClientOptions
{
    Uri = new Uri("wss://stream.example.com/ws"),
    IdleTimeout = TimeSpan.FromSeconds(60),
    MaxReconnectAttempts = null,            // 無限重連,也是預設值
    QueueCapacity = 4096,
    BackpressureStrategy = BackpressureStrategy.DropOldest,
};

await using var client = new WebSocketClient(options, logger);

client.MessageDropped += (_, e) =>
    logger.LogWarning("有訊息被丟棄,累計 {Total} 則", e.TotalDropped);

await client.SubscribeAsync(new WebSocketSubscription(
    id: "btcusdt-trades",
    subscribePayload: """{"method":"SUBSCRIBE","params":["btcusdt@trade"],"id":1}""",
    unsubscribePayload: """{"method":"UNSUBSCRIBE","params":["btcusdt@trade"],"id":2}"""));

var connect = await client.ConnectAsync();
if (connect.IsFailure)
{
    logger.LogError("連不上:{Error}", connect.Error);
    return;
}

await foreach (var item in client.Messages(cancellationToken))
{
    if (item.TryGetValue(out var message))
    {
        Handle(message.Text!);
        continue;
    }

    // 失敗代表這裡斷過線,資料可能有缺口。串流會繼續,
    // 除非代碼是 ws.reconnect_exhausted —— 那一定是最後一個元素。
    logger.LogWarning("串流有缺口:{Error}", item.Error);
}
```

連線前登記的訂閱會隨第一次連線送出;斷線期間登記的訂閱會被保留,下次連上時送出。每一次連線都會重放完整的清單。

### 啟動時對方可能還沒起來

`ConnectAsync` 只嘗試一次並告訴你成不成功,啟動階段通常就是要這樣。如果是 24 小時執行、希望它自己等到對方出現為止:

```csharp
client.Start();     // 立刻返回,依退避間隔一路重試到連上
```

---

## 背壓

佇列一律是有界的。在一個要跑好幾週的程式裡放無界佇列,那只是一個繞了路的記憶體洩漏。

| 策略 | 丟掉什麼 | 適合 |
| --- | --- | --- |
| `DropOldest`(預設) | 佇列中最舊的一則 | 行情這種「最新的最有價值」的串流 |
| `DropNewest` | 剛收到的那一則 | 必須依序處理、舊訊息不能跳過的串流 |
| `Wait` | 什麼都不丟,阻塞接收迴圈 | 一則都不能少,而且下游只是偶爾慢一下 |

`Wait` 的代價要先想清楚:接收迴圈一停,對方的傳送緩衝區與 TCP 視窗會跟著填滿,最後多半是被對方斷線;而閒置逾時偵測也會因為長時間沒有新訊息而判定連線已死。等於是把「丟訊息」換成「丟連線」。

不論選哪一種,丟棄都不會是靜默的:`MessageDropped` 事件與 `MessagesDropped` 計數都會反映。交易系統若因為佇列滿而漏掉一根 K 線又沒人知道,策略就會拿錯誤的資料下單,事後也查不出原因。

---

## 怎麼盯它

```csharp
var stats = client.Statistics;
```

最值得看的兩個數字:

- `MessagesDropped` —— 只要不是零,就代表已經有資料沒被處理到。
- `ReconnectCount` 對照 `SubscriptionReplayCount` —— 這兩個應該一起成長。重連在增加而重放停著不動,就是「連上了但沒重新訂閱」的樣子。

`State` 與 `CloseReason` 用來區分正常關機與重連用盡:兩者的狀態都是 `Closed`,但只有其中一個需要告警。

---

## 錯誤代碼

要分支請比對 `WebSocketErrorCodes`,不要比對訊息字串。訊息是給人看的、隨時會被改寫;代碼是契約的一部分。

| 代碼 | 分類 | 意義 |
| --- | --- | --- |
| `ws.options_invalid` | Validation | 設定不可用 |
| `ws.connect_failed` | Network | 握手失敗或被拒 |
| `ws.connect_timeout` | Timeout | 握手沒能在時限內完成 |
| `ws.connection_lost` | Network | 連線中斷,資料有缺口 |
| `ws.idle_timeout` | Timeout | 閒置時間內什麼都沒收到 |
| `ws.reconnect_exhausted` | Unavailable | 放棄重連,客戶端已停止 —— **串流的最後一個元素** |
| `ws.not_connected` | Unavailable | 目前沒有連線可送 |
| `ws.send_failed` | Network | 送出失敗 |
| `ws.subscription_replay_failed` | Network | 重放失敗,這條連線已作廢重來 |
| `ws.subscription_not_found` | NotFound | 沒有這個識別碼的訂閱 |
| `ws.message_too_large` | Network | 單則訊息超過 `MaxMessageSize` |
| `ws.invalid_state` | Conflict | 目前的生命週期狀態不允許 |
| `ws.cancelled` | Cancelled | 呼叫端取消 |

---

## 怎麼測你自己的程式

`IWebSocketConnection` 與 `IWebSocketConnectionFactory` 是公開的,可以替換掉傳輸層。再搭配 `TimeProvider`,重連、閒置逾時、退避都能在假時鐘上跑完,不碰網路也不用等:

```csharp
var client = new WebSocketClient(options, myFakeFactory, logger, myFakeClock);
```

這個套件自己的測試就是這樣寫的 —— 98 條,沒有任何一條開過 socket。

---

## 授權

MIT,見 [LICENSE](LICENSE)。
