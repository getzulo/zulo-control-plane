namespace ZuloOne.ControlPlane.Billing;

/// <summary>
/// The Stripe credentials that are NOT in the settings catalogue.
/// </summary>
/// <remarks>
/// Same split as <c>Demo:RequestToken</c> and <c>Mail:Password</c>: a secret that
/// opens a payment path must live in <c>cp.env</c>, never on a screen that
/// credential could one day reach. Empty means card pay is unavailable; bank
/// details still work.
/// </remarks>
public sealed class BillingSettings
{
    public string? StripeSecretKey { get; set; }

    public string? StripeWebhookSecret { get; set; }
}
