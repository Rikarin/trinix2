using System.Runtime.InteropServices;

namespace Trinix.Gatekeeper;

/// <summary>
/// Replaces this process with the application's.
/// </summary>
/// <remarks>
/// <para>
/// <c>execve</c>, not <c>Process.Start</c>. Starting a child would leave a .NET
/// runtime sitting between the session and every running application: it would
/// own the controlling terminal, it would have to forward every signal, and its
/// exit code would have to be made to mean the child's. Replacing the process
/// image makes all of those questions disappear — after this call there is no
/// launcher, only the application, and everything that would have had to be
/// forwarded is simply already correct.
/// </para>
/// <para>
/// The environment is passed explicitly rather than inherited, because
/// <c>Environment.SetEnvironmentVariable</c> on .NET for Unix modifies a
/// managed copy and never calls <c>setenv</c> — so a variable set the ordinary
/// way would not survive the exec. Building <c>envp</c> by hand is what makes
/// <c>TRINIX_BUNDLE</c> reach the application.
/// </para>
/// </remarks>
internal static partial class Launcher
{
    [LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static partial int Execve(nint path, nint[] argv, nint[] envp);

    /// <summary>
    /// Execute <paramref name="program"/>. Returns only if the exec failed.
    /// </summary>
    /// <param name="program">Absolute path to the executable.</param>
    /// <param name="arguments">Arguments after argv[0].</param>
    /// <param name="environment">The complete environment, as <c>NAME=value</c>.</param>
    /// <returns>The errno the kernel reported.</returns>
    internal static int Exec(string program, IReadOnlyList<string> arguments, IReadOnlyList<string> environment)
    {
        // argv[0] is the program's own path. Some runtimes — including .NET's
        // apphost — use it to locate their assembly directory, so passing the
        // bare file name here would break an application that ran perfectly
        // when started any other way.
        nint[] argv = new nint[arguments.Count + 2];
        argv[0] = Marshal.StringToCoTaskMemUTF8(program);
        for (int i = 0; i < arguments.Count; i++)
        {
            argv[i + 1] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
        }
        argv[^1] = nint.Zero;

        nint[] envp = new nint[environment.Count + 1];
        for (int i = 0; i < environment.Count; i++)
        {
            envp[i] = Marshal.StringToCoTaskMemUTF8(environment[i]);
        }
        envp[^1] = nint.Zero;

        nint path = Marshal.StringToCoTaskMemUTF8(program);

        // No frees on the success path: there is no success path in this
        // process. On failure the process is about to exit with a diagnostic,
        // so the allocations go with it.
        Execve(path, argv, envp);
        return Marshal.GetLastPInvokeError();
    }
}
