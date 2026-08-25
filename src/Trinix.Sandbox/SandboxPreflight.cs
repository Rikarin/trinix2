using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Trinix.Bundle;

namespace Trinix.Sandbox;

/// <summary>Who owes a missing prerequisite.</summary>
/// <remarks>
///     Recorded per prerequisite rather than left to the message text, because the
///     three answers lead to genuinely different actions: one of them the launcher can
///     just do, one is a change to the system image, and one is a change to what runs
///     at boot. A refusal that says "create this" without saying who is a refusal the
///     reader has to research.
/// </remarks>
public enum SandboxPrerequisiteOwner {
    /// <summary>
    ///     The launcher itself, on the spot. A per-application directory under the
    ///     user's own home is the launcher's to create — nobody else is better placed
    ///     and there is nothing to decide.
    /// </summary>
    Launcher,

    /// <summary>
    ///     <c>base/</c> and <c>image/</c>: it belongs in the signed system image, and
    ///     nothing at launch time may conjure it.
    /// </summary>
    /// <remarks>
    ///     ⚠ The reason this is not simply "create it if missing" is that the composed
    ///     root is the thing the container is <i>made of</i>. A launcher that created it
    ///     would be a launcher that decides, at runtime, as root, what an application's
    ///     filesystem consists of — which is the one decision doc 04 puts inside the
    ///     image's integrity rather than outside it.
    /// </remarks>
    SystemImage,

    /// <summary>
    ///     Something that runs once at boot, before the first application that needs it.
    /// </summary>
    Boot,

    /// <summary>
    ///     ⚠ Nobody yet. Doc 04's own prerequisite table: "A privileged path to the
    ///     system manager (polkit, or a <c>trinixd</c> verb)", 0.5 EM, unbudgeted and
    ///     unwritten.
    /// </summary>
    Unplanned
}

/// <summary>Something that has to exist before a unit can be started, and does not.</summary>
public sealed record SandboxPrerequisite {
    /// <summary>The path, or the capability, that is missing.</summary>
    public required string Subject { get; init; }

    /// <summary>Who would have to provide it.</summary>
    public required SandboxPrerequisiteOwner Owner { get; init; }

    /// <summary>One sentence naming what is missing and what it is for.</summary>
    public required string Problem { get; init; }
}

/// <summary>
///     What has to be true of the machine before a <see cref="SandboxUnit" /> can be
///     started on it.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="SandboxUnitBuilder" /> is a pure function and deliberately checks
///         nothing about the world — a unit may legitimately be constructed on a machine
///         that is not the one it will run on. This is the other half: the checks that
///         only make sense on the target, kept out of the builder so that the builder
///         stays assertable and kept out of the launcher so that these stay assertable
///         too.
///     </para>
///     <para>
///         ⚠ <b>Its whole purpose is to fail early and by name.</b> Three of the things
///         a sandboxed launch needs do not exist anywhere in this repository yet: the
///         composed root at <c>/usr/share/trinix/sandbox/root</c>, the shared network
///         namespace at <c>/run/netns/trinix-apps</c>, and a privileged path to the
///         system manager. Without this type, a launch missing any of them produces a
///         <c>systemd-run</c> failure about a mount or a namespace, several layers below
///         the thing that is actually wrong, and the person reading it has no reason to
///         suspect that the answer is "nothing has ever created that directory". With
///         it, the message names the path and says who owes it.
///     </para>
/// </remarks>
public static partial class SandboxPreflight {
    /// <summary>A container is one application's private state, and 0700 says so.</summary>
    const UnixFileMode ContainerMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    ///     Everything missing, or an empty list.
    /// </summary>
    /// <param name="layout">The resolved paths the unit will use.</param>
    /// <param name="permissions">
    ///     What the bundle declared. Only the network permissions change the answer:
    ///     an application with no network never touches the shared namespace, so its
    ///     absence must not stop that application launching.
    /// </param>
    /// <param name="exists">
    ///     How to ask whether a path is there. ⚠ Defaults to <see cref="Path.Exists" />
    ///     rather than <see cref="Directory.Exists" />, and the difference is load
    ///     bearing exactly once: an entry in <c>/run/netns</c> is a <i>file</i> that a
    ///     network namespace is bind-mounted onto, not a directory, so a directory check
    ///     would report the shared namespace missing on a machine where it is present
    ///     and working.
    /// </param>
    /// <param name="privileged">
    ///     Whether this process can talk to the system manager. Defaults to asking the
    ///     runtime.
    /// </param>
    /// <remarks>
    ///     Both machine-facing inputs are parameters with defaults, which is what lets
    ///     every branch here be tested on a laptop with no systemd, no root and no
    ///     <c>/usr/share/trinix</c>.
    /// </remarks>
    public static IReadOnlyList<SandboxPrerequisite> Check(
        SandboxLayout layout,
        PermissionSet permissions,
        Func<string, bool>? exists = null,
        bool? privileged = null
    ) {
        ArgumentNullException.ThrowIfNull(layout);

        exists ??= Path.Exists;
        List<SandboxPrerequisite> missing = [];

        if (!exists(SystemdProbe.SystemdRun)) {
            missing.Add(new SandboxPrerequisite {
                Subject = SystemdProbe.SystemdRun,
                Owner = SandboxPrerequisiteOwner.SystemImage,
                Problem = "there is no systemd-run here, so no transient unit can be created at all"
            });
        }

        // ⚠ Doc 04, verbatim: "a RootDirectory= unit needs the *system* manager, so the
        // launcher needs a privileged path to it that is currently unplanned." The user
        // manager would take most of these properties and cannot take RootDirectory=,
        // so falling back to it would produce an application with a sandbox-shaped unit
        // and no container — the exact failure this whole slice is written to avoid.
        if (!(privileged ?? Environment.IsPrivilegedProcess)) {
            missing.Add(new SandboxPrerequisite {
                Subject = "privilege",
                Owner = SandboxPrerequisiteOwner.Unplanned,
                Problem = "a RootDirectory= unit has to be created on the system manager, and this "
                    + "process is not privileged. Doc 04 budgets 0.5 EM for the privileged path "
                    + "(polkit, or a trinixd verb) and nothing implements it yet"
            });
        }

        if (!exists(layout.ComposedRoot)) {
            missing.Add(new SandboxPrerequisite {
                Subject = layout.ComposedRoot,
                Owner = SandboxPrerequisiteOwner.SystemImage,
                Problem = "the composed root does not exist. It is the read-only skeleton every "
                    + "container is built on — the mount points and nothing else — and nothing in "
                    + "base/ or image/ creates it yet (doc 04's prerequisite table, 0.3 EM)"
            });
        }

        if (!exists(layout.ContainerData)) {
            missing.Add(new SandboxPrerequisite {
                Subject = layout.ContainerData,
                Owner = SandboxPrerequisiteOwner.Launcher,
                Problem = "the application's container does not exist. It becomes the application's "
                    + "$HOME, so without it the bind mount has no source"
            });
        }

        if (permissions.HasAny(Permissions.NetworkClient | Permissions.NetworkServer)
            && !exists(layout.NetworkNamespace)) {
            missing.Add(new SandboxPrerequisite {
                Subject = layout.NetworkNamespace,
                Owner = SandboxPrerequisiteOwner.Boot,
                Problem = "this application declared a network permission and the shared application "
                    + "network namespace does not exist. Nothing creates it before the first "
                    + "networked application (doc 04's prerequisite table, 0.3 EM)"
            });
        }

        return missing;
    }

    /// <summary>
    ///     Create the one prerequisite the launcher owns.
    /// </summary>
    /// <param name="layout">The resolved paths.</param>
    /// <param name="groupId">
    ///     The gid the container should belong to. ⚠ Not on
    ///     <see cref="SandboxLayout" />, which carries the uid because
    ///     <c>/run/user/&lt;uid&gt;</c> is spelled with it and has never needed the
    ///     group for anything else.
    /// </param>
    /// <param name="problem">One line saying why not, when it failed.</param>
    /// <returns><see langword="true" /> when the container is there afterwards.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Created and then <c>chown</c>ed, and skipping the second half would
    ///         be worse than not creating it.</b> The launch path is privileged — it has
    ///         to be, see <see cref="SandboxPrerequisiteOwner.Unplanned" /> — so a
    ///         directory created here belongs to root by default. The application runs
    ///         as the user, and its <c>$HOME</c> <i>is</i> this directory: a root-owned
    ///         one produces an application that starts, finds its home unwritable, and
    ///         fails in whatever way its first attempt to save something fails. That is
    ///         a bug in the launcher wearing the costume of a bug in the application.
    ///     </para>
    ///     <para>
    ///         ⚠ Mode <c>0700</c>. A container is one application's private state, and
    ///         the other applications on the machine are precisely the parties it is
    ///         private from.
    ///     </para>
    /// </remarks>
    public static bool TryPrepareContainer(SandboxLayout layout, uint groupId, [NotNullWhen(false)] out string? problem) {
        ArgumentNullException.ThrowIfNull(layout);

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) {
            problem = "containers are a Unix thing and this is not a Unix";
            return false;
        }

        try {
            // Each level in turn rather than one CreateDirectory of the leaf, because
            // the ownership has to be fixed on every level this call brought into
            // existence — a root-owned ~/Library with a user-owned Data inside it is
            // still a home the application cannot reach.
            foreach (var directory in Ancestry(layout)) {
                if (Directory.Exists(directory)) {
                    continue;
                }

                _ = Directory.CreateDirectory(directory, ContainerMode);

                if (Chown(directory, layout.UserId, groupId) != 0) {
                    var errno = Marshal.GetLastPInvokeError();
                    problem = $"created {directory} but could not give it to uid {layout.UserId}: "
                        + new System.ComponentModel.Win32Exception(errno).Message;

                    return false;
                }
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
            problem = "could not create the container at " + layout.ContainerData + ": " + e.Message;
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    ///     <c>~/Library</c>, <c>~/Library/Containers</c>, the container and its
    ///     <c>Data</c>, outermost first.
    /// </summary>
    /// <remarks>
    ///     Derived from <see cref="SandboxLayout.ContainerData" /> by walking up to the
    ///     user's home rather than by re-joining the constants, so that a layout whose
    ///     container was placed somewhere unusual — a test, a future per-volume
    ///     container store — is still created correctly rather than being created in the
    ///     place the constants describe.
    /// </remarks>
    static List<string> Ancestry(SandboxLayout layout) {
        List<string> directories = [];

        for (var path = layout.ContainerData; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path)) {
            if (Directory.Exists(path)) {
                break;
            }

            directories.Add(path);
        }

        directories.Reverse();
        return directories;
    }

    [LibraryImport("libc", EntryPoint = "chown", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Chown(string path, uint owner, uint group);
}
