namespace ZuloOne.ControlPlane.Provisioning.Demo;

/// <summary>Wakes the pool reconciler without turning wake-ups into queued work.</summary>
public sealed class DemoNudge
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        _signal.WaitAsync(timeout, ct);
}
