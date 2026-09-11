# Changelog

All notable changes to this project are documented in this file.
本檔記錄本專案所有值得注意的變更。

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-09-11

First release. A WebSocket client meant to stay up for weeks, built on the BCL `ClientWebSocket` and nothing else.
首次發佈。建立在 BCL `ClientWebSocket` 之上、能連續執行數週的 WebSocket 客戶端。

### Added / 新增功能

- `IWebSocketClient` / `WebSocketClient` — connection lifecycle, automatic reconnect, subscription replay, idle-timeout
  liveness detection, bounded-queue backpressure, and a message stream. Every fallible operation returns a `Result`.
  連線生命週期、自動重連、訂閱重放、閒置逾時存活偵測、有界佇列背壓與訊息串流。所有會失敗的操作一律回傳 `Result`。
- `IWebSocketConnection` / `IWebSocketConnectionFactory` and their `ClientWebSocket` implementations. The seam exists
  for testability: everything worth testing sits above the connection, so a fake plus a `TimeProvider` covers it with
  no network involved.
  連線抽象與其 `ClientWebSocket` 實作。抽這層是為了可測性 —— 值得測的東西全在連線之上,假連線加上 `TimeProvider`
  就能完全脫離網路測完。
- `WebSocketClientOptions` — endpoint, reconnect policy, reconnect ceiling (`null` means unlimited), idle timeout,
  connect and close timeouts, queue capacity and backpressure strategy, message size limits, request headers, and
  sub-protocols. Values are validated on assignment; cross-property consistency is checked by `Validate()`.
  端點、重連策略、重連上限(`null` 為無限)、閒置逾時、連線與關閉逾時、佇列容量與背壓策略、訊息大小上限、
  請求標頭與子協定。屬性在設定時就驗證,跨屬性的一致性由 `Validate()` 負責。
- `WebSocketSubscription` — a registered subscription, replayed on every connection.
  一筆登記中的訂閱,每次連線都會重放。
- `BackpressureStrategy` — `DropOldest` (default), `DropNewest`, `Wait`. Dropped messages are never silent: they
  surface through the `MessageDropped` event and the `MessagesDropped` counter.
  三種背壓策略。被丟棄的訊息絕不靜默消失,會經由 `MessageDropped` 事件與 `MessagesDropped` 計數呈現。
- `WebSocketClientStatistics` — a consistent snapshot covering messages, bytes, drops, reconnects, replays, queue
  depth, and timestamps.
  一致的即時快照:則數、位元組、丟棄、重連、重放、佇列長度與各項時間戳。
- `WebSocketClientState` and `WebSocketCloseReason` — the reason separates a caller-initiated shutdown from an
  exhausted reconnect loop. Both end at `Closed`; only one deserves an alert.
  生命週期狀態與關閉理由。理由用來區分呼叫端主動關閉與重連用盡:兩者都停在 `Closed`,但只有一個需要告警。
- `WebSocketErrorCodes` — stable error codes to branch on, so callers never have to parse message text.
  穩定的錯誤代碼,讓呼叫端不必去剖析訊息字串。

### Notes on the design / 設計說明

Three decisions came from measurement rather than documentation, and the XML comments carry the reasoning so that
nobody later "fixes" them back:
以下三個決定來自實測而不是文件,XML 註解裡留了理由,免得後人把它們「修」回去:

- **No ping/pong handling.** `ClientWebSocket` answers the peer's pings at the protocol layer, invisibly and
  inaccessibly. Liveness is detected with an idle timeout instead.
  **完全不處理 ping/pong。** `ClientWebSocket` 在協定層自動回覆,應用層看不到也介入不了;存活偵測改用閒置逾時。
- **The receive loop never takes a cancellation token.** Cancelling an in-flight `ReceiveAsync` aborts the connection
  rather than stopping the read, which makes a graceful close impossible afterwards. Shutdown sends the close frame
  and lets the loop end on the peer's reply; the token is reserved for the hard timeout.
  **接收迴圈永遠不吃取消權杖。** 取消進行中的 `ReceiveAsync` 是中止連線而不是停止接收,之後就無法優雅關閉。
  關閉的做法是送出關閉 frame,讓迴圈看到對方的回覆而自行結束;取消權杖只留給硬逾時。
- **Being connected is not the same as receiving.** A handshake can succeed, the socket can stay `Open`, and no frame
  can arrive for minutes with no error at all. Only an idle timeout catches that.
  **「連得上」不等於「收得到」。** 握手可以成功、socket 可以維持 `Open`,而好幾分鐘沒有任何 frame 也沒有任何錯誤。
  只有閒置逾時抓得到。

### Known limitation / 已知限制

`ws.reconnect_exhausted` is categorised `ErrorCategory.Unavailable`, which counts as transient in
`Ozakboy.Core.Abstractions`. No existing category expresses "this instance is finished; retrying means constructing a
new client", so terminality must be told from the error code. The stream ends by itself after that element, so nothing
hangs either way.
`ws.reconnect_exhausted` 的分類是 `ErrorCategory.Unavailable`,而該分類在 `Ozakboy.Core.Abstractions` 裡算暫時性。
現有分類沒有一個能表達「這個物件已經結束了,重試要換一個新的客戶端」,所以終局與否要看錯誤代碼。
該元素之後串流會自行結束,因此不論有沒有分清楚都不會卡住。

[Unreleased]: https://github.com/ozakboy/Ozakboy.WebSockets/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ozakboy/Ozakboy.WebSockets/releases/tag/v0.1.0
