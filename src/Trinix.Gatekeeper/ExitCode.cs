namespace Trinix.Gatekeeper;

/// <summary>
///     What <c>open</c> exits with.
/// </summary>
/// <remarks>
///     ⚠ A shared class rather than four <c>const</c>s in <c>Program.cs</c>, because the
///     sandboxed branch lives in its own file and has to answer with the same four
///     numbers. Two copies of an exit code is the kind of duplication that stays correct
///     until somebody adds a fifth.
/// </remarks>
static class ExitCode {
    /// <summary>It worked.</summary>
    internal const int Ok = 0;

    /// <summary>The command line was wrong.</summary>
    internal const int Usage = 1;

    /// <summary>
    ///     The bundle was refused: something about it failed verification.
    /// </summary>
    /// <remarks>
    ///     ⚠ Distinct from <see cref="Failed" /> and the distinction is the user-visible
    ///     one. This means "we do not trust this"; <see cref="Failed" /> means "we could
    ///     not do it", which is usually a fact about the machine and never a suspicion
    ///     about the developer.
    /// </remarks>
    internal const int Refused = 2;

    /// <summary>Something about this machine, or this system, prevented the launch.</summary>
    internal const int Failed = 3;
}
