namespace Trinix.Sandbox;

/// <summary>
///     The cgroup limits the unit is given.
/// </summary>
/// <remarks>
///     <para>
///         Doc 04's last table row names three properties — <c>MemoryMax=</c>,
///         <c>CPUWeight=</c>, <c>TasksMax=</c> — and no values, and it names them for
///         two reasons at once. The first is containment: a runaway application should
///         hit a limit rather than the machine's. The second is the one that will be
///         noticed, which is that a cgroup per application is what makes doc 11's
///         System Monitor able to say something true about "how much memory is Mail
///         using" instead of summing processes by name and hoping.
///     </para>
///     <para>
///         ⚠ <b>The numbers below are Trinix's first guess and nothing more.</b> They
///         are not in any design document and they are not derived from measurement;
///         they are here because the properties have to have values and a value chosen
///         in the launcher's source is worse than one chosen in a type called
///         <c>SandboxResourceBounds</c>. <c>Info.json</c> carries no per-application
///         bounds, and that is probably right — a developer who declares their own
///         memory limit declares the largest number they can — so the eventual answer
///         is a default here and an override in Settings, not a signed field.
///     </para>
/// </remarks>
public sealed record SandboxResourceBounds {
    /// <summary>What an ordinary foreground application gets.</summary>
    public static SandboxResourceBounds Default { get; } = new();

    /// <summary>
    ///     <c>MemoryMax=</c>: the hard ceiling, beyond which the cgroup OOM killer
    ///     acts.
    /// </summary>
    /// <remarks>
    ///     ⚠ A percentage of physical memory rather than an absolute size, because the
    ///     same image runs on a VM with 2 GiB and a workstation with 64, and a constant
    ///     that is generous on one is an instant kill on the other. 75% leaves room for
    ///     the compositor, <c>trinixd</c> and the shell — the trusted computing base,
    ///     which is precisely what must not be the thing that dies when an application
    ///     misbehaves.
    /// </remarks>
    public string MemoryMax { get; init; } = "75%";

    /// <summary>
    ///     <c>CPUWeight=</c>: relative share under contention, not a cap.
    /// </summary>
    /// <remarks>
    ///     100 is systemd's own default, and it is stated rather than omitted so that
    ///     the value is visible in <c>systemctl show</c> and in a test. The interesting
    ///     number is not this one — it is the lower weight a future <c>system.background</c>
    ///     application should get, which is a policy decision doc 04 does not make.
    /// </remarks>
    public uint CpuWeight { get; init; } = 100;

    /// <summary>
    ///     <c>TasksMax=</c>: threads plus processes.
    /// </summary>
    /// <remarks>
    ///     512 is comfortable for a .NET application — the thread pool, the GC's
    ///     threads, and a few dozen of the application's own — and low enough that a
    ///     fork bomb inside a container stops at the container. ⚠ It is the one bound
    ///     here whose failure mode is silent: a process that cannot spawn a thread gets
    ///     an exception from somewhere unrelated, so raising it for an application that
    ///     genuinely needs more is a legitimate thing to do rather than a defeat.
    /// </remarks>
    public uint TasksMax { get; init; } = 512;
}
