namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// What a container is allowed to consume. Null anywhere means "whatever the
/// fleet settings say", which is what every non-demo caller wants.
/// </summary>
/// <param name="MemoryBytes">Hard memory ceiling. 0 means unbounded.</param>
/// <param name="Cpu">Cores, as a fraction. 0 means unbounded.</param>
/// <param name="PidsLimit">
/// Maximum processes. Caps the fork bomb that memory and CPU limits do not stop;
/// null leaves it unset, which is the Docker default of unlimited.
/// </param>
public sealed record ContainerLimits(long MemoryBytes, decimal Cpu, long? PidsLimit = null);
