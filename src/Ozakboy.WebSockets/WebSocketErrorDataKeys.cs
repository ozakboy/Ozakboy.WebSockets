namespace Ozakboy.WebSockets;

/// <summary>
/// 本套件放進 <see cref="Core.Abstractions.Error.Data"/> 的資料鍵。與 <see cref="WebSocketErrorCodes"/> 一樣
/// 是契約的一部分,請比對這些常數,不要在呼叫端硬寫字串字面值。
/// The keys this package puts into <see cref="Core.Abstractions.Error.Data"/>. Like
/// <see cref="WebSocketErrorCodes"/> they are part of the contract: use these constants rather than writing the
/// string literals out at the call site.
/// </summary>
/// <remarks>
/// <para>
/// 代碼是公開常數而資料鍵不是,這件事本身就不一致:讀 <see cref="Core.Abstractions.Error.Data"/> 的呼叫端
/// 只能硬寫 <c>"attempts"</c>,而那種字串打錯不會有任何徵兆 —— 編譯得過、執行不擲例外,
/// <c>TryGetXxx</c> 只是安靜地回傳 <see langword="false"/>,診斷欄位就這樣永遠是空的。
/// Exposing the codes but not the keys is an inconsistency with teeth: a caller reading
/// <see cref="Core.Abstractions.Error.Data"/> has to write <c>"attempts"</c> by hand, and a typo there shows no
/// symptom at all — it compiles, it does not throw, <c>TryGetXxx</c> merely returns <see langword="false"/>, and the
/// diagnostic field is quietly empty forever.
/// </para>
/// <para>
/// 值一律以字串存放,依型別用 <see cref="Core.Abstractions.Error.TryGetInt64(string, out long)"/> 或
/// <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/> 讀回來;各鍵的型別與單位見下方說明。
/// Values are always stored as strings; read them back with
/// <see cref="Core.Abstractions.Error.TryGetInt64(string, out long)"/> or
/// <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/> according to the type documented on each key
/// below.
/// </para>
/// </remarks>
public static class WebSocketErrorDataKeys
{
    /// <summary>
    /// 放棄前實際嘗試了幾次重連。整數,用
    /// <see cref="Core.Abstractions.Error.TryGetInt64(string, out long)"/> 讀。
    /// 出現於 <see cref="WebSocketErrorCodes.ReconnectExhausted"/> 與
    /// <see cref="WebSocketErrorCodes.Unrecoverable"/>;後者算的是同一串連續失敗的次數,設定在執行期被改壞
    /// 的典型情況下是 1 —— 失敗當下就放棄,沒有第二次。
    /// How many connection attempts had failed when the client gave up. An integer; read it with
    /// <see cref="Core.Abstractions.Error.TryGetInt64(string, out long)"/>. Present on
    /// <see cref="WebSocketErrorCodes.ReconnectExhausted"/> and <see cref="WebSocketErrorCodes.Unrecoverable"/>; on
    /// the latter it counts the same run of consecutive failures and is 1 in the typical mutated-configuration case,
    /// because the client gives up on that very failure rather than trying again.
    /// </summary>
    public const string Attempts = "attempts";

    /// <summary>
    /// 相關逾時的<b>毫秒</b>數。整數,用
    /// <see cref="Core.Abstractions.Error.TryGetInt64(string, out long)"/> 讀。
    /// 出現於 <see cref="WebSocketErrorCodes.ConnectTimeout"/> 與
    /// <see cref="WebSocketErrorCodes.IdleTimeout"/>。
    /// The relevant timeout in <b>milliseconds</b>. An integer; read it with
    /// <see cref="Core.Abstractions.Error.TryGetInt64(string, out long)"/>. Present on
    /// <see cref="WebSocketErrorCodes.ConnectTimeout"/> and <see cref="WebSocketErrorCodes.IdleTimeout"/>.
    /// </summary>
    public const string TimeoutMs = "timeoutMs";

    /// <summary>
    /// 訊息大小上限,單位為<b>位元組</b>。整數,用
    /// <see cref="Core.Abstractions.Error.TryGetInt64(string, out long)"/> 讀。
    /// 出現於 <see cref="WebSocketErrorCodes.MessageTooLarge"/>。
    /// The message size limit in <b>bytes</b>. An integer; read it with
    /// <see cref="Core.Abstractions.Error.TryGetInt64(string, out long)"/>. Present on
    /// <see cref="WebSocketErrorCodes.MessageTooLarge"/>.
    /// </summary>
    public const string LimitBytes = "limitBytes";

    /// <summary>
    /// 相關訂閱的識別碼,即 <see cref="WebSocketSubscription.Id"/>。字串,用
    /// <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/> 讀。
    /// 出現於 <see cref="WebSocketErrorCodes.SubscriptionNotFound"/> 與
    /// <see cref="WebSocketErrorCodes.SubscriptionReplayFailed"/>。
    /// The identifier of the subscription involved, i.e. <see cref="WebSocketSubscription.Id"/>. A string; read it
    /// with <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/>. Present on
    /// <see cref="WebSocketErrorCodes.SubscriptionNotFound"/> and
    /// <see cref="WebSocketErrorCodes.SubscriptionReplayFailed"/>.
    /// </summary>
    public const string SubscriptionId = "subscriptionId";

    /// <summary>
    /// 當下的生命週期狀態,內容是 <see cref="WebSocketClientState"/> 的名稱(例如 <c>Reconnecting</c>)。
    /// 字串,用 <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/> 讀;
    /// 需要列舉值時用 <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> 轉換。
    /// 出現於 <see cref="WebSocketErrorCodes.NotConnected"/> 與 <see cref="WebSocketErrorCodes.InvalidState"/>。
    /// The lifecycle state at the time, as the name of a <see cref="WebSocketClientState"/> such as
    /// <c>Reconnecting</c>. A string; read it with
    /// <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/> and convert with
    /// <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> when the enum value is wanted. Present on
    /// <see cref="WebSocketErrorCodes.NotConnected"/> and <see cref="WebSocketErrorCodes.InvalidState"/>.
    /// </summary>
    public const string State = "state";

    /// <summary>
    /// 被拒絕或被取消的操作名稱(例如 <c>SendAsync</c>)。字串,用
    /// <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/> 讀。
    /// 出現於 <see cref="WebSocketErrorCodes.InvalidState"/> 與 <see cref="WebSocketErrorCodes.Cancelled"/>。
    /// The name of the operation that was refused or cancelled, such as <c>SendAsync</c>. A string; read it with
    /// <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/>. Present on
    /// <see cref="WebSocketErrorCodes.InvalidState"/> and <see cref="WebSocketErrorCodes.Cancelled"/>.
    /// </summary>
    public const string Operation = "operation";

    /// <summary>
    /// 內層失敗的錯誤代碼,值必定是 <see cref="WebSocketErrorCodes"/> 的其中一個常數。字串,用
    /// <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/> 讀。
    /// 出現於 <see cref="WebSocketErrorCodes.SubscriptionReplayFailed"/>(重放時的傳送失敗)與
    /// <see cref="WebSocketErrorCodes.Unrecoverable"/>(害客戶端放棄的那個非暫時性失敗)。
    /// The error code of the underlying failure; the value is always one of the
    /// <see cref="WebSocketErrorCodes"/> constants. A string; read it with
    /// <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/>. Present on
    /// <see cref="WebSocketErrorCodes.SubscriptionReplayFailed"/>, where it is the send failure behind the replay,
    /// and on <see cref="WebSocketErrorCodes.Unrecoverable"/>, where it is the non-transient failure that made the
    /// client give up.
    /// </summary>
    public const string InnerCode = "innerCode";

    /// <summary>
    /// 內層失敗的錯誤分類名稱,即 <see cref="Core.Abstractions.ErrorCategory"/> 的名稱(例如
    /// <c>Validation</c>)。字串,用 <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/> 讀。
    /// 只出現於 <see cref="WebSocketErrorCodes.Unrecoverable"/>,用來說明「為什麼判定不可恢復」。
    /// The category name of the underlying failure, i.e. the name of a
    /// <see cref="Core.Abstractions.ErrorCategory"/> such as <c>Validation</c>. A string; read it with
    /// <see cref="Core.Abstractions.Error.TryGetData(string, out string)"/>. Present only on
    /// <see cref="WebSocketErrorCodes.Unrecoverable"/>, where it explains why the failure was judged unrecoverable.
    /// </summary>
    public const string InnerCategory = "innerCategory";
}
