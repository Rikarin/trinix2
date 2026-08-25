using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Trinix.Sandbox;

/// <summary>
///     The user an application is launched as, asked of the system rather than
///     assumed.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="SandboxLayout" /> takes a name, a uid and a home directory and
///         does no discovery of its own, which is what makes the unit builder a pure
///         function. This type is the other half of that split: the one place allowed
///         to ask the machine who is running, so that everything downstream can be
///         handed the answer instead of guessing at it.
///     </para>
///     <para>
///         ⚠ <b><c>getpwuid_r</c> and not <c>$HOME</c>.</b> The container is bound over
///         the application's home, so the home this resolves decides where every file
///         the application writes actually lands. An environment variable is settable
///         by whoever started the launcher; the passwd entry is the same fact the
///         kernel and every other program will agree on, and it is what
///         <see cref="SandboxLayout.HomeInside" /> is chosen to match — a process that
///         resolves its own home through <c>getpwuid</c> rather than the environment,
///         and some do, has to land in the same place.
///     </para>
///     <para>
///         ⚠ <b><c>getuid</c>, not <c>geteuid</c>.</b> <c>open</c> is not setuid and
///         must never become so, so today the two agree. If one of them is ever wrong
///         it is the effective uid: the application belongs to the person who asked
///         for it, not to whatever the launcher was made to run as.
///     </para>
/// </remarks>
public sealed partial record SandboxUser {
    /// <summary>Where the buffer <c>getpwuid_r</c> fills starts.</summary>
    /// <remarks>
    ///     glibc's <c>sysconf(_SC_GETPW_R_SIZE_MAX)</c> suggests 1024, and an entry
    ///     that does not fit is reported as <c>ERANGE</c> rather than truncated — so
    ///     the first size is a guess with a documented recovery, not a limit.
    /// </remarks>
    const int InitialBufferSize = 1024;

    /// <summary>
    ///     Where growing the buffer stops.
    /// </summary>
    /// <remarks>
    ///     ⚠ A cap rather than an unbounded loop. <c>ERANGE</c> forever is what a
    ///     misdeclared <c>struct passwd</c> would look like — the call would write
    ///     past a field this side got wrong and keep asking for more room — and an
    ///     allocation loop in the launch path is a worse failure than a refusal that
    ///     names the uid.
    /// </remarks>
    const int MaximumBufferSize = 256 * 1024;

    const int Erange = 34;

    /// <summary>Their login name, which the unit's <c>User=</c> carries.</summary>
    public required string Name { get; init; }

    /// <summary>Their uid, which names <c>/run/user/&lt;uid&gt;</c>.</summary>
    public required uint UserId { get; init; }

    /// <summary>Their primary group's gid.</summary>
    public required uint GroupId { get; init; }

    /// <summary>
    ///     That group's name, or the gid written out when the group has no entry.
    /// </summary>
    /// <remarks>
    ///     ⚠ Falling back to the number is deliberate and it is not a degradation:
    ///     systemd's <c>Group=</c> takes a numeric gid as readily as a name. A group
    ///     that resolves to nothing is ordinary on a machine whose users come from a
    ///     directory service, and refusing to launch over the *label* of a gid we
    ///     already know would be refusing over cosmetics.
    /// </remarks>
    public required string GroupName { get; init; }

    /// <summary>Their real home directory, which holds <c>Library/Containers</c>.</summary>
    /// <remarks>
    ///     ⚠ The host's home, never the container's. The application will see
    ///     <see cref="SandboxLayout.HomeInside" /> as <c>$HOME</c>; this is the
    ///     directory the container is carved out of, and the two must not be confused
    ///     — binding the first over the second is the mistake that would hand an
    ///     application the whole of the user's files under the name of containing it.
    /// </remarks>
    public required string Home { get; init; }

    /// <summary>
    ///     Whoever this process is running as.
    /// </summary>
    /// <param name="user">Them, when they could be resolved.</param>
    /// <param name="problem">One line saying why not, otherwise.</param>
    /// <returns><see langword="true" /> when <paramref name="user" /> is set.</returns>
    public static bool TryCurrent([NotNullWhen(true)] out SandboxUser? user, out string? problem) {
        if (!IsSupported) {
            user = null;
            problem = "the passwd database is a Unix thing and this is not a Unix";
            return false;
        }

        return TryResolve(GetUid(), out user, out problem);
    }

    /// <summary>
    ///     Whoever this process is running as, or refuse.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     There is no passwd entry for this process's uid. ⚠ A refusal rather than a
    ///     default, because every plausible default is wrong in the expensive
    ///     direction: a guessed home is a container in the wrong place, and a guessed
    ///     uid is a <c>XDG_RUNTIME_DIR</c> belonging to somebody else.
    /// </exception>
    public static SandboxUser Current() =>
        TryCurrent(out var user, out var problem)
            ? user
            : throw new InvalidOperationException("cannot resolve the current user: " + problem);

    /// <summary>
    ///     Look one up by uid.
    /// </summary>
    /// <param name="userId">The uid to resolve.</param>
    /// <param name="user">Them, when there is an entry.</param>
    /// <param name="problem">One line saying why not, otherwise.</param>
    /// <returns><see langword="true" /> when <paramref name="user" /> is set.</returns>
    public static bool TryResolve(uint userId, [NotNullWhen(true)] out SandboxUser? user, out string? problem) {
        user = null;

        if (!IsSupported) {
            problem = "the passwd database is a Unix thing and this is not a Unix";
            return false;
        }

        if (!TryPasswd(userId, out var name, out var groupId, out var home, out problem)) {
            return false;
        }

        user = new SandboxUser {
            Name = name,
            UserId = userId,
            GroupId = groupId,
            GroupName = TryGroupName(groupId, out var groupName)
                ? groupName
                : groupId.ToString(CultureInfo.InvariantCulture),
            Home = home
        };

        problem = null;
        return true;
    }

    static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    // --- the passwd database -------------------------------------------------

    static bool TryPasswd(
        uint userId,
        out string name,
        out uint groupId,
        out string home,
        out string? problem
    ) {
        name = string.Empty;
        groupId = 0;
        home = string.Empty;

        var entrySize = OperatingSystem.IsMacOS() ? Marshal.SizeOf<DarwinPasswd>() : Marshal.SizeOf<LinuxPasswd>();

        for (var bufferSize = InitialBufferSize; ; bufferSize *= 2) {
            var entry = Marshal.AllocHGlobal(entrySize);
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try {
                // ⚠ getpwuid_r *returns* the errno rather than setting it, the way
                // posix_spawn does and almost nothing else in libc does. Reading
                // Marshal.GetLastPInvokeError() here would report whatever call came
                // before this one.
                var error = GetPasswordEntry(userId, entry, buffer, (nuint)bufferSize, out var found);

                if (error == Erange) {
                    if (bufferSize >= MaximumBufferSize) {
                        problem = $"the passwd entry for uid {userId} does not fit in {bufferSize} bytes";
                        return false;
                    }

                    continue;
                }

                if (error != 0) {
                    problem = $"could not read the passwd entry for uid {userId}: {new Win32Exception(error).Message}";
                    return false;
                }

                // ⚠ Success with a null result is "no such user", not an error, and
                // it is the case that actually happens: a uid from a container with
                // no /etc/passwd, or a directory service that is not answering. It
                // must not be read as an entry full of zeroes.
                if (found == nint.Zero) {
                    problem = $"there is no passwd entry for uid {userId}";
                    return false;
                }

                nint namePointer;
                nint homePointer;
                if (OperatingSystem.IsMacOS()) {
                    var passwd = Marshal.PtrToStructure<DarwinPasswd>(entry);
                    namePointer = passwd.Name;
                    homePointer = passwd.Directory;
                    groupId = passwd.GroupId;
                } else {
                    var passwd = Marshal.PtrToStructure<LinuxPasswd>(entry);
                    namePointer = passwd.Name;
                    homePointer = passwd.Directory;
                    groupId = passwd.GroupId;
                }

                name = Marshal.PtrToStringUTF8(namePointer) ?? string.Empty;
                home = Marshal.PtrToStringUTF8(homePointer) ?? string.Empty;

                if (name.Length == 0 || home.Length == 0) {
                    problem = $"the passwd entry for uid {userId} has no {(name.Length == 0 ? "name" : "home directory")}";
                    return false;
                }

                problem = null;
                return true;
            } finally {
                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(entry);
            }
        }
    }

    static bool TryGroupName(uint groupId, [NotNullWhen(true)] out string? name) {
        name = null;
        var entrySize = Marshal.SizeOf<Group>();

        for (var bufferSize = InitialBufferSize; ; bufferSize *= 2) {
            var entry = Marshal.AllocHGlobal(entrySize);
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try {
                var error = GetGroupEntry(groupId, entry, buffer, (nuint)bufferSize, out var found);

                if (error == Erange && bufferSize < MaximumBufferSize) {
                    // A group with a great many members is the ordinary reason to be
                    // here — gr_mem is in the same buffer as the name.
                    continue;
                }

                if (error != 0 || found == nint.Zero) {
                    return false;
                }

                name = Marshal.PtrToStringUTF8(Marshal.PtrToStructure<Group>(entry).Name);
                return !string.IsNullOrEmpty(name);
            } finally {
                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(entry);
            }
        }
    }

    // --- libc ----------------------------------------------------------------

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();

    [LibraryImport("libc", EntryPoint = "getpwuid_r")]
    private static partial int GetPasswordEntry(uint uid, nint entry, nint buffer, nuint bufferSize, out nint found);

    [LibraryImport("libc", EntryPoint = "getgrgid_r")]
    private static partial int GetGroupEntry(uint gid, nint entry, nint buffer, nuint bufferSize, out nint found);

    /// <summary>
    ///     glibc's <c>struct passwd</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the layout Trinix runs on and it is not the layout this file
    ///     is compiled on.</b> Darwin's <c>struct passwd</c> has <c>pw_change</c> and
    ///     <c>pw_class</c> between the gid and <c>pw_gecos</c> — see
    ///     <see cref="DarwinPasswd" /> — so a single declaration would read a home
    ///     directory out of the wrong field on one of the two platforms, and it would
    ///     not crash: it would hand back a plausible string. Both are declared, and
    ///     the choice is made at runtime, so that the resolution can be exercised on a
    ///     developer's Mac rather than only in a VM.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    struct LinuxPasswd {
        public nint Name;
        public nint Password;
        public uint UserId;
        public uint GroupId;
        public nint Gecos;
        public nint Directory;
        public nint Shell;
    }

    /// <summary>Darwin's <c>struct passwd</c>. See <see cref="LinuxPasswd" />.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct DarwinPasswd {
        public nint Name;
        public nint Password;
        public uint UserId;
        public uint GroupId;
        public long Change;
        public nint Class;
        public nint Gecos;
        public nint Directory;
        public nint Shell;
        public long Expire;
    }

    /// <summary><c>struct group</c>, which both platforms happen to spell the same.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Group {
        public nint Name;
        public nint Password;
        public uint GroupId;
        public nint Members;
    }
}
