namespace PayGate.Kashier;

/// <summary>
/// Kashier-only choices for one payment, passed as <see cref="CreatePaymentRequest.ProviderOptions"/>. Anything left unset
/// falls back to the account's <see cref="KashierAccountOptions"/>, then to Kashier's own default.
/// </summary>
public sealed record KashierPaymentOptions : ProviderPaymentOptions
{
    /// <inheritdoc/>
    public override PaymentProviderId Provider => PaymentProviderId.Kashier;

    /// <summary>The payment page language, <c>en</c> or <c>ar</c>. Overrides <see cref="CreatePaymentRequest.Locale"/>.</summary>
    public string? Display { get; init; }

    /// <summary>How many failed tries the customer gets on the page. Each try is a new Kashier order under the same reference.</summary>
    public int? MaxFailureAttempts { get; init; }

    /// <summary>
    /// Whether a failed try sends the customer back to the return URL at once (<see langword="true"/>), or keeps them on the
    /// payment page to try again (<see langword="false"/>, Kashier's default).
    /// </summary>
    public bool? FailureRedirect { get; init; }

    /// <summary>Whether to run 3-D Secure on cards. Kashier's default applies when unset.</summary>
    public bool? Enable3DS { get; init; }
}
