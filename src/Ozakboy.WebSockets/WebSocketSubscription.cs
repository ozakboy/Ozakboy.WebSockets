namespace Ozakboy.WebSockets;

/// <summary>
/// 一筆訂閱的描述:識別碼,以及訂閱與取消訂閱時要送出的原始訊息。
/// A subscription: its identifier plus the raw payloads to send when subscribing and unsubscribing.
/// </summary>
/// <remarks>
/// <para>
/// 本套件不理解任何交易所的訂閱協定,因此 <see cref="SubscribePayload"/> 就是要原封不動送出去的字串,
/// 由上層自行組裝。客戶端只負責記住它,並在每次連線建立之後重送一次。
/// This package understands no exchange's subscription protocol, so <see cref="SubscribePayload"/> is sent verbatim
/// and the caller composes it. The client's only job is to remember it and resend it after every connection.
/// </para>
/// <para>
/// <b>重連後重放訂閱是這個套件存在的主要理由之一。</b>漏掉重放的症狀極難察覺:連線是活的、狀態顯示正常、
/// 沒有任何錯誤,但資料永遠不會再進來。見
/// <see cref="IWebSocketClient.SubscribeAsync(WebSocketSubscription, CancellationToken)"/>。
/// <b>Replaying subscriptions after a reconnect is one of the main reasons this package exists.</b> Forgetting it
/// produces a failure that is nearly impossible to spot: the connection is alive, the status looks healthy, no error
/// is raised, and data simply never arrives again. See
/// <see cref="IWebSocketClient.SubscribeAsync(WebSocketSubscription, CancellationToken)"/>.
/// </para>
/// </remarks>
public sealed record WebSocketSubscription
{
    /// <summary>
    /// 建立訂閱描述。
    /// Creates a subscription.
    /// </summary>
    /// <param name="id">
    /// 訂閱識別碼,用於取消訂閱與去重。同一個識別碼重複訂閱會覆蓋先前的設定。不可為空白。
    /// The identifier used for unsubscribing and de-duplication. Subscribing the same id twice replaces the earlier
    /// entry. Must not be blank.
    /// </param>
    /// <param name="subscribePayload">
    /// 訂閱時要送出的原始文字訊息。不可為空白。
    /// The raw text payload sent when subscribing. Must not be blank.
    /// </param>
    /// <param name="unsubscribePayload">
    /// 取消訂閱時要送出的原始文字訊息;為 <see langword="null"/> 表示這個協定沒有取消訂閱訊息,
    /// 取消時只會從重放清單中移除。
    /// The raw text payload sent when unsubscribing, or <see langword="null"/> when the protocol has no such
    /// message — unsubscribing then only removes the entry from the replay list.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> 或 <paramref name="subscribePayload"/> 為 <see langword="null"/>、空字串或僅含空白時擲出。
    /// Thrown when <paramref name="id"/> or <paramref name="subscribePayload"/> is <see langword="null"/>, empty, or
    /// whitespace.
    /// </exception>
    public WebSocketSubscription(string id, string subscribePayload, string? unsubscribePayload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscribePayload);

        Id = id;
        SubscribePayload = subscribePayload;
        UnsubscribePayload = unsubscribePayload;
    }

    /// <summary>
    /// 訂閱識別碼。
    /// The subscription identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// 訂閱時送出的原始文字訊息。每次連線建立後都會重送一次。
    /// The raw text payload sent when subscribing; resent after every connection is established.
    /// </summary>
    public string SubscribePayload { get; }

    /// <summary>
    /// 取消訂閱時送出的原始文字訊息,可為 <see langword="null"/>。
    /// The raw text payload sent when unsubscribing; may be <see langword="null"/>.
    /// </summary>
    public string? UnsubscribePayload { get; }

    /// <summary>
    /// 回傳可讀敘述,只含識別碼。刻意不輸出內容,避免把含憑證的訂閱訊息印進日誌。
    /// Returns a readable description containing only the identifier; payloads are left out so that a subscription
    /// carrying credentials does not end up in logs.
    /// </summary>
    /// <returns>可讀敘述。A readable description.</returns>
    public override string ToString() => $"Subscription({Id})";
}
