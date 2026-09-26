namespace ZuloOne.ControlPlane.Settings;

/// <summary>How a value is entered, validated and rendered.</summary>
public enum SettingKind
{
    /// <summary>Whole number, bounded by <see cref="SettingDef.Min"/>/<see cref="SettingDef.Max"/>.</summary>
    Int,

    /// <summary>Whole number of BYTES. Shown as MB/GB; stored as bytes.</summary>
    Bytes,

    /// <summary>Whole number of SECONDS. Shown as a duration; stored as seconds.</summary>
    Seconds,

    /// <summary>Whole number of HOURS.</summary>
    Hours,

    /// <summary>Whole number of DAYS.</summary>
    Days,

    Bool,

    Text,

    /// <summary>Comma-separated. Stored and returned as typed; split on read.</summary>
    TextList,

    /// <summary>A container image reference. Rendered against what the registry holds.</summary>
    Image,
}

/// <summary>Where the value in force came from.</summary>
public enum SettingSource
{
    /// <summary>The compiled-in default. Nothing has ever been said about this key.</summary>
    Default,

    /// <summary>Configuration — appsettings or, in the fleet, cp.env.</summary>
    Config,

    /// <summary>Set here, in the panel. Overrides configuration.</summary>
    Database,
}

/// <summary>
/// One setting, declared once. The screen renders itself from these, so adding a
/// knob is an entry here plus a read through <see cref="SettingsStore"/> — never
/// a change to the UI.
/// </summary>
/// <param name="Key">The configuration key, colon-separated, exactly as the config layer spells it.</param>
/// <param name="Group">Heading on the screen.</param>
/// <param name="Label">Short name for a human.</param>
/// <param name="Description">Why it exists and what changing it costs. Shown, not hidden in a tooltip.</param>
/// <param name="Kind">Decides validation and how the value is rendered.</param>
/// <param name="Default">The compiled-in default, as a string, matching the options class.</param>
/// <param name="RuntimeEditable">
/// False means the process reads this once at startup, so a change here does
/// nothing until the container is recreated. Saying so on the screen is the whole
/// point of the flag: a setting that silently does not apply is worse than one
/// that cannot be edited.
/// </param>
public sealed record SettingDef(
    string Key,
    string Group,
    string Label,
    string Description,
    SettingKind Kind,
    string Default,
    bool RuntimeEditable = true,
    long? Min = null,
    long? Max = null);

/// <summary>
/// Everything the panel will let an operator change, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The exclusions are deliberate and worth keeping. Out of this catalogue, and
/// staying in <c>cp.env</c>:
/// </para>
/// <list type="bullet">
/// <item><c>Access:*</c> and <c>Operator:*</c> — editing the way in, from inside.</item>
/// <item>
/// <c>TenantDatabase:AdminUser/AdminPassword</c>, <c>Patroni:Username/Password</c>,
/// <c>ConnectionStrings:*</c> — secrets, and simultaneously the panel's only route
/// to Postgres. A typo here severs the panel from the cluster entirely.
/// </item>
/// <item>
/// <c>Snapshots:Path</c> — a volume mount point. Changing it at run time points
/// the panel at a directory the container does not have.
/// </item>
/// <item>
/// <c>Demo:RequestToken</c> — the shared secret that opens the only anonymous
/// write path into the panel. Same reasoning as <c>Access:*</c>: a credential
/// that admits the public must not be editable from a screen that credential
/// could one day reach.
/// </item>
/// <item>
/// <c>Billing:StripeSecretKey</c> / <c>Billing:StripeWebhookSecret</c> — Stripe
/// credentials that open the card till. Same reasoning as <c>Demo:RequestToken</c>.
/// </item>
/// <item>
/// <c>Mail:Password</c> — the panel has no data-protection key ring, so an
/// encrypted secret would mean new infrastructure with a new way to fail (lose
/// the ring, lose the value), and a plaintext one would ride into every dump and
/// backup of the registry.
/// </item>
/// </list>
/// </remarks>
public static class SettingsCatalog
{
    public const string Backups = "Backups and retention";
    public const string Fleet = "Fleet and provisioning";
    public const string Monitoring = "Monitoring thresholds";
    public const string Mail = "Mail";
    public const string Demo = "Demo workspaces";
    public const string Billing = "Billing";

    public static readonly IReadOnlyList<SettingDef> All =
    [
        // ------------------------------------------------------------ Backups
        new("Snapshots:KeepPerTenant", Backups,
            "Automatic snapshots kept per tenant",
            "How many pre-upgrade snapshots to keep for each tenant, newest first. "
            + "Manual snapshots are never removed automatically — taking one by hand is how a "
            + "state is pinned forever.",
            SettingKind.Int, "3", Min: 1, Max: 100),

        new("Snapshots:KeepDays", Backups,
            "Keep everything younger than",
            "Age below which an automatic snapshot survives regardless of how many newer ones "
            + "exist. Without this, a run of upgrades in one afternoon would evict yesterday's "
            + "rollback point.",
            SettingKind.Days, "7", Min: 1, Max: 365),

        new("Snapshots:PruneIntervalHours", Backups,
            "Sweep every",
            "How often the panel queues a prune. The sweep runs as a job, in the same single-file "
            + "queue as backups and restores, so it can never delete a file another job is reading.",
            SettingKind.Hours, "6", Min: 1, Max: 168),

        new("Snapshots:OrphanGraceHours", Backups,
            "Leave unknown files alone for",
            "A file in the snapshot directory with no row is only removed once it is older than "
            + "this. The grace exists because a dump in progress looks exactly like an orphan.",
            SettingKind.Hours, "2", Min: 1, Max: 72),

        new("Snapshots:MinFreeBytes", Backups,
            "Refuse to snapshot below",
            "Free space on the snapshot volume under which a snapshot is refused rather than "
            + "attempted. An upgrade refuses too — an upgrade whose rollback could not be written "
            + "is an upgrade with no way back.",
            SettingKind.Bytes, "2147483648", Min: 104857600),

        new("Snapshots:RegistryKeepDays", Backups,
            "Keep registry snapshots for",
            "The panel's own database holds the only record of which container and database belong "
            + "to whom. It is small, so this is generous by default.",
            SettingKind.Days, "30", Min: 1, Max: 365),

        new("Images:KeepRecent", Backups,
            "Recent tenant images kept on the app host",
            "How many of the newest images stay pulled after the sweep, on top of every image a "
            + "tenant is pinned to and the fleet default. This is the rollback window: rolling a "
            + "tenant back moves it to its previous tag, which is instant while that image is still "
            + "here and a pull from the registry once it is not. Counted rather than aged because "
            + "an image reports when it was BUILT, and the rollback target of a tenant that was "
            + "far behind is an old image somebody needs right now.",
            SettingKind.Int, "5", Min: 1, Max: 100),

        new("Images:KeepUnusedReleases", Backups,
            "Unused releases kept in the registry",
            "How many of the newest unused release tags stay after a prune, on top of every "
            + "release a tenant runs and the fleet default. Older unused releases can be deleted "
            + "from the Images screen. Zero means every unused non-default release is removable.",
            SettingKind.Int, "3", Min: 0, Max: 50),

        // -------------------------------------------------------------- Fleet
        new("Fleet:DefaultImage", Fleet,
            "Image new tenants start on",
            "Every tenant provisioned from now on. Existing tenants are pinned individually and "
            + "are not moved by changing this.",
            SettingKind.Image, "zuloone/core:dev"),

        new("Fleet:ReadinessTimeoutSeconds", Fleet,
            "Wait for a tenant to answer",
            "How long provisioning and upgrades wait for a container to report ready. First boot "
            + "runs migrations, a schema sync and a Roslyn compile against a cold database, so this "
            + "is generous on purpose.",
            SettingKind.Seconds, "300", Min: 30, Max: 3600),

        new("Fleet:MemoryLimitBytes", Fleet,
            "Memory limit per tenant",
            "Applied to containers created after the change. Zero means unlimited.",
            SettingKind.Bytes, "0"),

        new("Fleet:AdditionalReservedSlugs", Fleet,
            "Extra reserved subdomains",
            "Names no customer may take, on top of the compiled-in baseline. This list can only "
            + "ADD: emptying it cannot hand anyone 'admin'. Read once at startup, so a change here "
            + "applies to the panel's next run.",
            SettingKind.TextList, "", RuntimeEditable: false),

        new("Fleet:Packages", Fleet,
            "Models installed by default",
            "The list a tenant follows unless it has been pinned to its own on the Models screen. "
            + "Changing it moves every unpinned tenant at their next recreate, and nothing before "
            + "that. Empty installs nothing; '*' installs everything the image has.",
            SettingKind.TextList, "Common,Organization"),

        // --------------------------------------------------------- Monitoring
        new("Infra:ExpectedNodes", Monitoring,
            "Machines that should report",
            "name:role, comma-separated. Roles: postgres, etcd, mongo, app, panel, ci. "
            + "A name that never reports is shown as never heard from — that is how a missing "
            + "Mongo or app host becomes visible. Empty lists only who has spoken or who Patroni sees.",
            SettingKind.TextList,
            "zo-pg-1:postgres,zo-pg-2:postgres,zo-pgw-1:etcd,zo-app-1:app,mongo:mongo,zo-cp-1:panel,zo-ci-1:ci"),

        new("Patroni:ReportStaleAfterMinutes", Monitoring,
            "A node report goes stale after",
            "Beyond this the panel shows a node as unheard-from rather than as whatever it last "
            + "claimed. Three times the reporting timer, so one missed run is not an alarm.",
            SettingKind.Int, "15", Min: 5, Max: 240),

        new("Monitoring:BackupWarnHours", Monitoring,
            "Warn when the newest backup is older than",
            "Turns the cluster backup age amber. Full backups are weekly and differentials daily, "
            + "so this should sit just past a day.",
            SettingKind.Hours, "30", Min: 2, Max: 720),

        new("Monitoring:BackupCriticalHours", Monitoring,
            "Call it critical past",
            "Past this the backup age is shown red. Two missed differentials mean something is "
            + "wrong with archiving, not with the schedule.",
            SettingKind.Hours, "72", Min: 4, Max: 1440),

        new("Monitoring:DiskWarnPercent", Monitoring,
            "Warn on disk use above",
            "Applies to the database nodes and the snapshot volume.",
            SettingKind.Int, "80", Min: 50, Max: 99),

        // --------------------------------------------------------------- Mail
        new("Mail:Enabled", Mail,
            "Send invitation e-mail",
            "When off, a new tenant's administrator password is shown once in the panel and never "
            + "sent. That is a working arrangement, not a degraded one.",
            SettingKind.Bool, "false"),

        // --------------------------------------------------------------- Demo
        new("Demo:Enabled", Demo,
            "Demo workspaces",
            "Master switch. Off means the public endpoint answers 503, the pool service idles, "
            + "and nothing is ever reaped — an unconfigured feature refuses rather than half-runs.",
            SettingKind.Bool, "false"),

        new("Demo:GoldenSlug", Demo,
            "Golden tenant",
            "The hand-curated workspace every demo is cloned from. An ordinary operator tenant "
            + "that happens to be the template's origin; it is never reaped and never handed out.",
            SettingKind.Text, "showcase"),

        new("Demo:TemplateSnapshotId", Demo,
            "Template snapshot",
            "Which snapshot of the golden tenant new demos are built from. Written by the "
            + "template refresh job; set by hand only to pin an older one deliberately.",
            SettingKind.Text, ""),

        new("Demo:PoolTarget", Demo,
            "Keep this many ready",
            "Pre-built demos waiting to be claimed. This is what makes a demo instant: a visitor "
            + "gets a workspace that already exists instead of waiting out a provision.",
            SettingKind.Int, "2", Min: 0, Max: 20),

        new("Demo:MaxConcurrent", Demo,
            "Never more than",
            "Hard ceiling on live demos, pooled and claimed together. Excludes the golden tenant. "
            + "Every demo is a real container and a real database on the app host, so this is the "
            + "actual capacity control — everything else is friction.",
            SettingKind.Int, "6", Min: 0, Max: 50),

        new("Demo:MaxQueue", Demo,
            "Queue at most",
            "Past this many waiting requests the answer is a plain \"full, try later\" rather than "
            + "a longer wait. A queue nobody will reach the end of is a worse answer than no.",
            SettingKind.Int, "20", Min: 0, Max: 200),

        new("Demo:LifetimeHours", Demo,
            "A demo lasts",
            "Measured from the moment it is CLAIMED, not built — the clock is a promise to the "
            + "person who was given it, and time spent sitting in the pool is not theirs.",
            SettingKind.Hours, "24", Min: 1, Max: 168),

        new("Demo:PoolMaxAgeHours", Demo,
            "Rebuild unclaimed after",
            "An unclaimed demo is capacity too, and it drifts from the template as the golden "
            + "tenant moves on. Past this age it is retired and a fresh one takes its place.",
            SettingKind.Hours, "72", Min: 1, Max: 720),

        new("Demo:MaxExtendHours", Demo,
            "Operator may extend by at most",
            "The escape hatch for a live sales call. Bounded so that extending cannot quietly "
            + "turn a throwaway into a tenant nobody is accounting for.",
            SettingKind.Hours, "48", Min: 0, Max: 336),

        new("Demo:ReapIntervalSeconds", Demo,
            "Check for expiry every",
            "How often the pool service reconciles: reap what is out of time, serve the queue, "
            + "top the pool back up.",
            SettingKind.Seconds, "60", Min: 15, Max: 3600),

        new("Demo:MaxPerEmailPerDay", Demo,
            "Per e-mail address, per day",
            "Counted against demo requests, not against tenants — a refused request still counts, "
            + "which is what makes the limit hold.",
            SettingKind.Int, "1", Min: 1, Max: 20),

        new("Demo:MaxPerIpPerDay", Demo,
            "Per source address, per day",
            "The quota that actually matters: edge rate limits are bypassed by anyone who finds "
            + "the origin, and the origin is one firewall rule away from being found.",
            SettingKind.Int, "3", Min: 1, Max: 50),

        new("Demo:PasswordWindowMinutes", Demo,
            "Password readable for",
            "How long a claimed demo's password can still be re-read. Deliberately NOT the "
            + "read-once rule used for an admin password: read-once is right when a human clicks a "
            + "button and sees a modal, and wrong across a network hop, where a dropped response "
            + "would cost the visitor the demo they just asked for with no way to recover.",
            SettingKind.Int, "10", Min: 1, Max: 120),

        new("Demo:MemoryLimitBytes", Demo,
            "Memory per demo",
            "Demo containers get their own limit, unlike tenants, which currently run unbounded. "
            + "A demo hands a stranger a script editor that compiles C# on the app host — the host "
            + "that also runs every paying tenant and the proxy.",
            SettingKind.Bytes, "805306368", Min: 268435456),

        new("Demo:CpuMilli", Demo,
            "CPU per demo",
            "In thousandths of a core: 1000 is one core. Same reasoning as the memory limit.",
            SettingKind.Int, "1000", Min: 100, Max: 8000),

        new("Demo:PidsLimit", Demo,
            "Processes per demo",
            "Caps the fork bomb that memory and CPU limits do not stop.",
            SettingKind.Int, "256", Min: 32, Max: 4096),

        new("Demo:UserName", Demo,
            "Demo account",
            "The account inside the golden snapshot whose password is reset when a demo is built "
            + "and again when it is claimed. Twice, so the value handed over existed nowhere "
            + "before — not in the dump, and not while the workspace sat in the pool.",
            SettingKind.Text, "demo"),

        // ------------------------------------------------------------ Billing
        new("Billing:Enabled", Billing,
            "Billing",
            "Master switch. Off means the sweep idles and the Billing panel refuses rather than "
            + "half-running against an unconfigured commercial stand.",
            SettingKind.Bool, "false"),

        new("Billing:TenantSlug", Billing,
            "Commercial tenant",
            "The fleet stand that holds customers, contracts, invoices and payments for hosting. "
            + "Not a customer workspace — Zulo's own books. Must be Active or every Billing call "
            + "answers that the commercial tenant is down.",
            SettingKind.Text, "hq"),

        new("Billing:BankDetails", Billing,
            "Bank transfer details",
            "Shown to the customer when they pay by bank. Leave empty if only card pay is offered.",
            SettingKind.Text, ""),

        new("Billing:SweepIntervalSeconds", Billing,
            "Check for overdue every",
            "How often the sweep asks the commercial tenant who is overdue and who is settled, "
            + "then Stops or Starts the matching stands.",
            SettingKind.Seconds, "60", Min: 15, Max: 3600),

        new("Mail:Host", Mail, "SMTP host", "Leave empty to disable sending entirely.", SettingKind.Text, ""),
        new("Mail:Port", Mail, "SMTP port", "587 for STARTTLS, 465 for implicit TLS.", SettingKind.Int, "587", Min: 1, Max: 65535),
        new("Mail:UseSsl", Mail, "Implicit TLS", "On for port 465. Off for 587, which upgrades with STARTTLS.", SettingKind.Bool, "false"),
        new("Mail:UserName", Mail, "SMTP user", "The password is NOT here — it lives in cp.env. See the note on this screen.", SettingKind.Text, ""),
        new("Mail:FromAddress", Mail, "From address", "Must be one the SMTP server will accept as a sender.", SettingKind.Text, ""),
        new("Mail:FromName", Mail, "From name", "What a recipient sees instead of the address.", SettingKind.Text, "ZuloOne"),
    ];

    public static SettingDef? Find(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
}
