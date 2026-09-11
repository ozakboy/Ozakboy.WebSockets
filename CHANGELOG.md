# Changelog

All notable changes to this project are documented in this file.
本檔記錄本專案所有值得注意的變更。

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-09-11

Upgrades to `Ozakboy.Core.Abstractions` 0.3.0 and uses its new `ErrorCategory.Exhausted` to close the one known
limitation of 0.1.0 — then puts that same `IsTransient` contract to work in the reconnect loop, where it fixes a
failure that never stops and never recovers.
升級到 `Ozakboy.Core.Abstractions` 0.3.0,用它新增的 `ErrorCategory.Exhausted` 解除 0.1.0 唯一的已知限制,
並把同一套 `IsTransient` 契約用回重連迴圈,修掉一個不會停止也不會恢復的失敗。

### Changed / 功能優化

- **`ws.reconnect_exhausted` is now categorised `ErrorCategory.Exhausted` instead of `ErrorCategory.Unavailable`.**
  `Unavailable` counts as transient, so `IsTransient` used to report `true` for a client that had stopped for good,
  and callers were told to branch on the error code instead. `Exhausted` is not transient, so **`IsTransient` now
  gives the right answer for this error and nothing has to look at the code to tell a gap from the end of the
  stream.** This is observable behaviour and the reason for the minor bump, even though the package has never been
  published.
  **`ws.reconnect_exhausted` 的錯誤分類從 `ErrorCategory.Unavailable` 改為 `ErrorCategory.Exhausted`。**
  `Unavailable` 算暫時性,所以過去一個已經永久停止的客戶端,`IsTransient` 會回報 `true`,呼叫端只好改用錯誤
  代碼判斷終局與否。`Exhausted` 不是暫時性分類,因此**`IsTransient` 現在對這個錯誤給出正確答案,要區分
  「缺口」與「串流結束」不必再去看錯誤代碼。** 這是消費端觀察得到的行為變更,雖然套件尚未發佈,仍以 Minor
  反映它的份量。
- **`ws.not_connected` is categorised by state for the same reason.** It stays the transient `Unavailable` while the
  client is connecting or reconnecting — there will be a connection shortly — and becomes `Exhausted` once the client
  has closed, because that instance will never connect again. Without the split, a caller retrying a send on
  `IsTransient` would retry a dead object forever.
  **`ws.not_connected` 基於同樣的理由改成依狀態分類。** 連線中或重連中仍是暫時性的 `Unavailable`(稍後就會有
  連線);客戶端已關閉時改為 `Exhausted`,因為那個物件永遠不會再連上。少了這個區分,照著 `IsTransient` 重試
  傳送的呼叫端會對著一個死掉的物件永遠重試下去。
- `IWebSocketClient.Messages`, `WebSocketErrorCodes`, and both READMEs no longer tell callers to branch on the error
  code; they point at `IsTransient` now.
  `IWebSocketClient.Messages`、`WebSocketErrorCodes` 與兩份 README 不再要求呼叫端比對錯誤代碼,改為指向
  `IsTransient`。

### Fixed / 問題修正

- **A non-transient failure no longer sends the reconnect loop spinning forever.** The options object is mutable and
  held by reference, and the loop re-validates it on every attempt, so anything that breaks the configuration after
  the client has started — setting `Uri` to `null`, say — used to produce the same non-transient `ws.options_invalid`
  on every attempt, and the loop backed off and tried again regardless. With `MaxReconnectAttempts` defaulting to
  unlimited, which is exactly what a round-the-clock process is told to use, that loop never ended. **The symptom to
  recognise is not a crash: the process stays up, the reconnect log keeps scrolling, the state keeps flipping between
  Reconnecting and Connecting, and it looks like work — but no data will ever arrive again.** The loop now checks
  `error.IsTransient` before retrying and ends for good when it is `false`, which is what that property is for.
  Transient failures — `Network`, `Timeout`, `Unavailable`, `RateLimited` — still reconnect without limit, and the
  decision reads `IsTransient` rather than singling out a code, so any non-transient error added later is covered.
  **非暫時性失敗不會再讓重連迴圈永遠空轉。** 設定物件是可變的、由參照持有,而重連迴圈每次嘗試都重跑
  `Validate()`,所以客戶端跑起來之後被改壞的設定(例如把 `Uri` 設成 `null`)會讓每一次重連都收到同一個
  非暫時性的 `ws.options_invalid`,迴圈卻照樣退避重試。而 `MaxReconnectAttempts` 預設無限 —— 那正是
  24 小時執行的建議設定 —— 於是這個迴圈永遠不會結束。**要認出這個問題,別找當機:行程還活著、重連日誌
  一直在刷、狀態在重連中與連線中之間來回跳,看起來像在工作,但資料再也不會進來。** 現在迴圈會在重試前
  先看 `error.IsTransient`,為 `false` 就終局結束 —— 那本來就是這個屬性存在的意義。暫時性失敗
  (`Network`、`Timeout`、`Unavailable`、`RateLimited`)照舊無限重連;判斷依 `IsTransient` 而不是針對
  特定代碼特判,日後新增的非暫時性錯誤自動適用。

### Added / 新增功能

- `WebSocketCloseReason.UnrecoverableError` — the ending for the case above. Reusing
  `ReconnectAttemptsExhausted` would have been a lie: that one means "tried many times and failed" and sends whoever
  is on call to check the peer, while this one means "must not try even once" and has its cause on this side. The
  terminal error published to the message stream is the new `ws.unrecoverable`, categorised `Exhausted` so
  `IsTransient` is `false`, and its `Error.Data` carries `innerCode` and `innerCategory` — the failure that made the
  client give up — plus `attempts`. It is logged at `Error` under its own event id (1014), separate from the
  reconnect-exhausted one.
  `WebSocketCloseReason.UnrecoverableError` —— 上面那個情況的結局。沿用 `ReconnectAttemptsExhausted` 是說謊:
  那代表「試過很多次都失敗」,會把值班的人帶去查對方;而這裡是「一次都不該試」,原因在自己這邊。送進訊息
  串流的終局錯誤是新的 `ws.unrecoverable`,分類為 `Exhausted`(`IsTransient` 為 `false`),`Error.Data` 帶
  `innerCode` 與 `innerCategory`(害它放棄的那個錯誤)以及 `attempts`。日誌以獨立的事件代碼 1014 記在
  `Error` 等級,與重連用盡分得開。
- `WebSocketErrorDataKeys` — the `Error.Data` keys are now public constants. Exposing the codes but not the keys was
  an inconsistency with teeth: reading `Error.Data` meant writing `"attempts"` by hand, and a typo there has no
  symptom at all — it compiles, it does not throw, `TryGetXxx` just returns `false` and the field is quietly empty
  forever. Each key documents which codes carry it, the value's type, and its unit.
  `WebSocketErrorDataKeys` —— `Error.Data` 的資料鍵改為公開常數。代碼公開而資料鍵不公開是會咬人的不一致:
  讀 `Error.Data` 只能硬寫 `"attempts"`,而那種字串打錯完全沒有徵兆 —— 編譯得過、不擲例外,`TryGetXxx`
  只是安靜地回傳 `false`,欄位就永遠是空的。每個鍵都註明了哪些錯誤代碼會帶它、值的型別與單位。
- Errors carry their numbers in `Error.Data`, so downstream code reads them back with `TryGetInt64` / `TryGetData`
  instead of parsing message text: `attempts` on `ws.reconnect_exhausted`, `timeoutMs` on `ws.connect_timeout` and
  `ws.idle_timeout`, `limitBytes` on `ws.message_too_large`, `subscriptionId` on the two subscription errors (plus
  `innerCode` on the replay failure), `state` on `ws.not_connected`, and `state` with `operation` on
  `ws.invalid_state` and `ws.cancelled`.
  錯誤現在把數值放進 `Error.Data`,下游用 `TryGetInt64` / `TryGetData` 讀回來,不必剖析訊息字串:
  `ws.reconnect_exhausted` 帶 `attempts`,`ws.connect_timeout` 與 `ws.idle_timeout` 帶 `timeoutMs`,
  `ws.message_too_large` 帶 `limitBytes`,兩個訂閱相關錯誤帶 `subscriptionId`(重放失敗另帶 `innerCode`),
  `ws.not_connected` 帶 `state`,`ws.invalid_state` 與 `ws.cancelled` 帶 `state` 與 `operation`。
- The `ArgumentException` thrown for invalid options carries the full `Error` as a `ResultException` in its
  `InnerException`. A constructor signature has no room for a `Result`, and the exception type is unchanged, so this
  is purely additive: existing `catch (ArgumentException)` still works, and callers that want the code and category
  can now get them.
  設定不合法時擲出的 `ArgumentException`,其 `InnerException` 現在是攜帶完整 `Error` 的 `ResultException`。
  建構式的簽章塞不進 `Result`,而例外型別維持不變,所以這是純新增:既有的 `catch (ArgumentException)` 照舊,
  想取得代碼與分類的呼叫端現在拿得到。

### Technical / 技術改進

- `Ozakboy.Core.Abstractions` 0.2.1 → 0.3.0. The dependency graph still contains nothing but `Microsoft.*` and
  `System.*`.
  `Ozakboy.Core.Abstractions` 0.2.1 → 0.3.0。相依樹仍然只有 `Microsoft.*` 與 `System.*`。
- Seven tests added, 98 → 105. Two pin the category work: `IsTransient` is `false` on `ws.reconnect_exhausted` and on
  `ws.not_connected` after close. Five cover the reconnect fix and the public keys: a configuration broken at runtime
  ends the client instead of spinning (the fake clock is deliberately never advanced, so a client that did back off
  would fail the test by timing out), transient failures still reconnect without limit, the two ways of giving up
  leave different log events, and the key constants both hold their contract values and read real values back. Still
  no test opens a socket.
  新增七條測試,98 → 105。兩條鎖住分類的成果:`ws.reconnect_exhausted` 與關閉之後的 `ws.not_connected`,
  `IsTransient` 都必須是 `false`。五條涵蓋重連修正與公開資料鍵:設定在執行期被改壞時客戶端終局結束而不是
  空轉(那條刻意完全不推進假時鐘,所以只要客戶端真的去退避重試就會等不到而失敗)、暫時性失敗仍然無限重連、
  兩種放棄留下的是不同的日誌事件,以及資料鍵常數的值與實際讀取。仍然沒有任何一條測試開過 socket。

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

### Known limitation at the time / 當時的已知限制(0.2.0 已解除)

`ws.reconnect_exhausted` was categorised `ErrorCategory.Unavailable`, which counts as transient, because no category
expressed "this instance is finished; retrying means constructing a new client" — so terminality had to be told from
the error code. `Ozakboy.Core.Abstractions` 0.3.0 added `ErrorCategory.Exhausted` in response and 0.2.0 of this
package adopted it. **This is no longer a limitation; see the 0.2.0 entry.**
`ws.reconnect_exhausted` 當時的分類是算暫時性的 `ErrorCategory.Unavailable`,因為沒有任何分類能表達「這個物件
已經結束了,重試要換一個新的客戶端」,終局與否只好看錯誤代碼。`Ozakboy.Core.Abstractions` 0.3.0 為此加入了
`ErrorCategory.Exhausted`,本套件 0.2.0 已改用它。**這一項已不再是限制,見 0.2.0 條目。**

[Unreleased]: https://github.com/ozakboy/Ozakboy.WebSockets/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/ozakboy/Ozakboy.WebSockets/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/ozakboy/Ozakboy.WebSockets/releases/tag/v0.1.0
