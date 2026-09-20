namespace ZuloOne.ControlPlane.Provisioning.Demo;

/// <summary>What the panel decided to do with a demo request.</summary>
public enum DemoVerdict
{
    /// <summary>A pooled workspace is free; claim it now.</summary>
    Ready,

    /// <summary>Nothing free, but there is room to wait.</summary>
    Queue,

    /// <summary>This address already has a live demo. Point them at it again.</summary>
    Duplicate,

    TooManyForEmail,

    TooManyForIp,

    /// <summary>At the ceiling AND the queue is full. Say so plainly.</summary>
    Full,

    /// <summary>The feature is switched off.</summary>
    Disabled,
}

/// <summary>
/// The entire admission policy for demo requests, as a function of counts.
/// </summary>
/// <remarks>
/// <para>
/// No database, no clock, no settings object it does not own. That is the point:
/// this is where the bugs will be — an off-by-one at the ceiling hands out a
/// workspace the host cannot carry, and a mis-ordered check tells an honest
/// visitor they are rate-limited when the fleet is simply full. Factored this
/// way, every branch is reachable from a test.
/// </para>
/// <para>
/// Order matters and is deliberate. Disabled first, because nothing else is
/// meaningful when the feature is off. Then the caller's own quotas, so somebody
/// asking for a second demo is told that rather than being told the fleet is
/// full. Then capacity.
/// </para>
/// </remarks>
public static class DemoQuota
{
    public static DemoVerdict Decide(
        bool enabled,
        int pooledAvailable,
        int liveDemos,
        int queueDepth,
        int perEmailToday,
        int perIpToday,
        bool emailHasLiveDemo,
        int maxConcurrent,
        int maxQueue,
        int maxPerEmailPerDay,
        int maxPerIpPerDay)
    {
        if (!enabled)
        {
            return DemoVerdict.Disabled;
        }

        // Before any quota: if this address is already sitting in a workspace,
        // the honest answer is "here it is again", not "you have had your one".
        if (emailHasLiveDemo)
        {
            return DemoVerdict.Duplicate;
        }

        if (perEmailToday >= maxPerEmailPerDay)
        {
            return DemoVerdict.TooManyForEmail;
        }

        if (perIpToday >= maxPerIpPerDay)
        {
            return DemoVerdict.TooManyForIp;
        }

        // A pooled workspace can be handed over even at the ceiling: it is already
        // built and already counted against it. Refusing here would mean holding
        // capacity we have deliberately paid for and refusing to use it.
        if (pooledAvailable > 0)
        {
            return DemoVerdict.Ready;
        }

        if (liveDemos >= maxConcurrent && queueDepth >= maxQueue)
        {
            return DemoVerdict.Full;
        }

        return DemoVerdict.Queue;
    }
}
