using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Settings;

namespace ZuloOne.ControlPlane.Billing;

/// <summary>
/// Billing policy as it is RIGHT NOW — the operator's overrides on top of <c>cp.env</c>.
/// </summary>
/// <remarks>
/// The same split <see cref="Provisioning.Demo.DemoConfig"/> makes: policy comes
/// from the settings store so the panel can change it without a container
/// recreate, while Stripe credentials stay bound at startup.
/// </remarks>
public sealed class BillingConfig
{
    private readonly BillingSettings _bound;
    private readonly SettingsStore _store;

    public BillingConfig(IOptions<BillingSettings> bound, SettingsStore store)
    {
        _bound = bound.Value;
        _store = store;
    }

    // ---- wiring: cp.env only --------------------------------------------

    public string? StripeSecretKey => _bound.StripeSecretKey;

    public string? StripeWebhookSecret => _bound.StripeWebhookSecret;

    // ---- policy: overridable in the panel --------------------------------

    public bool Enabled => _store.Bool("Billing:Enabled");

    /// <summary>Slug of the commercial stand that holds the books.</summary>
    public string TenantSlug => _store.Text("Billing:TenantSlug");

    /// <summary>Bank transfer instructions shown to the customer.</summary>
    public string BankDetails => _store.Text("Billing:BankDetails");

    public int SweepIntervalSeconds => _store.Int("Billing:SweepIntervalSeconds");
}
