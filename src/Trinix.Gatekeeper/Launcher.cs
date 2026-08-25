using System.Runtime.InteropServices;

namespace Trinix.Gatekeeper;

/// <summary>
///     Replaces this process with the application's.
/// </summary>
/// <remarks>
///     <para>
///         <c>execve</c>, not <c>Process.Start</c>. Starting a child would leave a .NET
///         runtime sitting between the session and every running application: it would
///         own the controlling terminal, it would have to forward every signal, and its
///         exit code would have to be made to mean the child's. Replacing the process
///         image makes all of those questions disappear — after this call there is no
///         launcher, only the application, and everything that would have had to be
///         forwarded is simply already correct.
///     </para>
///     <para>
///         The environment is passed explicitly rather than inherited, because
///         <c>Environment.SetEnvironmentVariable</c> on .NET for Unix modifies a
///         managed copy and never calls <c>setenv</c> — so a variable set the ordinary
///         way would not survive the exec. Building <c>envp</c> by hand is what makes
///         <c>TRINIX_BUNDLE</c> reach the application.
///     </para>
/// </remarks>
static partial class Launcher {
    /// <summary>Ask the kernel to put the new process in a session of its own.</summary>
    /// <remarks>
    ///     <c>POSIX_SPAWN_SETSID</c>, which is what makes a launched application
    ///     outlive the terminal that launched it: without it the child stays in the
    ///     shell's session and a hangup on that terminal closes the window.
    /// </remarks>
    const short SpawnSetsid = 0x80;

    /// <summary>
    ///     Room for a <c>posix_spawnattr_t</c>.
    /// </summary>
    /// <remarks>
    ///     It is opaque, and glibc's is about 340 bytes — two signal sets at 128 each
    ///     and change. Half a kilobyte on the stack costs nothing and cannot be the
    ///     thing that is too small.
    /// </remarks>
    const int SpawnAttributesSize = 512;

    [LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static partial int Execve(nint path, nint[] argv, nint[] envp);

    [LibraryImport("libc", EntryPoint = "posix_spawn")]
    private static partial int PosixSpawn(
        out int pid,
        nint path,
        nint fileActions,
        nint attributes,
        nint[] argv,
        nint[] envp
    );

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_init")]
    private static partial int SpawnAttributesInit(nint attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    private static partial int SpawnAttributesDestroy(nint attributes);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    private static partial int SpawnAttributesSetFlags(nint attributes, short flags);

    /// <summary>
    ///     Execute <paramref name="program" />. Returns only if the exec failed.
    /// </summary>
    /// <param name="program">Absolute path to the executable.</param>
    /// <param name="arguments">Arguments after argv[0].</param>
    /// <param name="environment">The complete environment, as <c>NAME=value</c>.</param>
    /// <returns>The errno the kernel reported.</returns>
    internal static int Exec(string program, IReadOnlyList<string> arguments, IReadOnlyList<string> environment) {
        // argv[0] is the program's own path. Some runtimes — including .NET's
        // apphost — use it to locate their assembly directory, so passing the
        // bare file name here would break an application that ran perfectly
        // when started any other way.
        var argv = new nint[arguments.Count + 2];
        argv[0] = Marshal.StringToCoTaskMemUTF8(program);
        for (var i = 0; i < arguments.Count; i++) {
            argv[i + 1] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
        }

        argv[^1] = nint.Zero;

        var envp = new nint[environment.Count + 1];
        for (var i = 0; i < environment.Count; i++) {
            envp[i] = Marshal.StringToCoTaskMemUTF8(environment[i]);
        }

        envp[^1] = nint.Zero;

        var path = Marshal.StringToCoTaskMemUTF8(program);

        // No frees on the success path: there is no success path in this
        // process. On failure the process is about to exit with a diagnostic,
        // so the allocations go with it.
        Execve(path, argv, envp);
        return Marshal.GetLastPInvokeError();
    }

    /// <summary>
    ///     Start <paramref name="program" /> in a session of its own and return.
    /// </summary>
    /// <param name="program">Absolute path to the executable.</param>
    /// <param name="arguments">Arguments after argv[0].</param>
    /// <param name="environment">The complete environment, as <c>NAME=value</c>.</param>
    /// <param name="pid">The new process, when there is one.</param>
    /// <returns>Zero, or the errno the kernel reported.</returns>
    /// <remarks>
    ///     <para>
    ///         <c>posix_spawn</c> rather than fork and exec, and the reason is that
    ///         this is a .NET process. A forked child of a multithreaded runtime may
    ///         only reach <c>execve</c> through async-signal-safe calls, and there is
    ///         no way to promise that of a managed frame — one allocation between the
    ///         two, one lazy P/Invoke stub, and the child deadlocks holding a lock its
    ///         thread does not exist to release. <c>posix_spawn</c> does the whole
    ///         thing inside libc, from one call, on this thread.
    ///     </para>
    ///     <para>
    ///         <c>POSIX_SPAWN_SETSID</c> is what makes it a launch rather than a child
    ///         process: the application gets its own session, so closing the terminal
    ///         it was typed into does not close the window.
    ///     </para>
    /// </remarks>
    internal static unsafe int Spawn(
        string program,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> environment,
        out int pid
    ) {
        var argv = new nint[arguments.Count + 2];
        argv[0] = Marshal.StringToCoTaskMemUTF8(program);
        for (var i = 0; i < arguments.Count; i++) {
            argv[i + 1] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
        }

        argv[^1] = nint.Zero;

        var envp = new nint[environment.Count + 1];
        for (var i = 0; i < environment.Count; i++) {
            envp[i] = Marshal.StringToCoTaskMemUTF8(environment[i]);
        }

        envp[^1] = nint.Zero;

        var path = Marshal.StringToCoTaskMemUTF8(program);

        var attributes = stackalloc byte[SpawnAttributesSize];
        var attributesPointer = (nint)attributes;

        var error = SpawnAttributesInit(attributesPointer);
        if (error != 0) {
            pid = 0;
            return error;
        }

        try {
            error = SpawnAttributesSetFlags(attributesPointer, SpawnSetsid);
            if (error != 0) {
                pid = 0;
                return error;
            }

            // ⚠ posix_spawn returns the error rather than setting errno, which is
            // the one way its contract differs from everything else in this file.
            return PosixSpawn(out pid, path, nint.Zero, attributesPointer, argv, envp);
        } finally {
            // Discarded deliberately: destroy fails only on an attribute set that
            // was never initialised, and this one was — the init above returned
            // before reaching here otherwise.
            _ = SpawnAttributesDestroy(attributesPointer);

            for (var i = 0; i < argv.Length - 1; i++) { Marshal.FreeCoTaskMem(argv[i]); }
            for (var i = 0; i < envp.Length - 1; i++) { Marshal.FreeCoTaskMem(envp[i]); }
            Marshal.FreeCoTaskMem(path);
        }
    }
}
