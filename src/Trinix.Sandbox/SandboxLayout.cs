using Trinix.Bundle;

namespace Trinix.Sandbox;

/// <summary>
///     Every path the unit builder needs, resolved before it runs.
/// </summary>
/// <remarks>
///     <para>
///         The builder takes paths rather than discovering them, and that is the
///         reason this type exists. Discovery means <c>getpwuid</c>, an environment
///         variable, a check that a directory exists — all of which are things that
///         can fail, differ between the machine the unit is built on and the machine
///         it runs on, and cannot be exercised from a unit test. Handing the builder a
///         resolved layout makes it a pure function of two documents, which is what
///         lets the interesting assertions be made without a systemd.
///     </para>
///     <para>
///         ⚠ Every path here is a path <b>on the host</b> except
///         <see cref="HomeInside" />, which is the one path that only exists inside
///         the container. That asymmetry is doc 04's largest single simplification:
///         the application's <c>$HOME</c> <i>is</i> its container, so every library
///         that writes to <c>$HOME/.config</c> — and every one of them does — writes
///         into <c>~/Library/Containers/&lt;id&gt;/Data</c> without being patched, and
///         the application never learns that its home is somewhere else.
///     </para>
/// </remarks>
public sealed record SandboxLayout {
    /// <summary>Where the composed root lives, when nobody says otherwise.</summary>
    /// <remarks>
    ///     A directory in the signed system image holding an empty skeleton — the
    ///     mount points and nothing else. It is shared by every application and is
    ///     mounted read-only, so it carries no per-application state and there is
    ///     nothing in it to tamper with that is not already covered by the image's own
    ///     integrity.
    /// </remarks>
    public const string DefaultComposedRoot = "/usr/share/trinix/sandbox/root";

    /// <summary>Where an application's container lives, relative to the user's home.</summary>
    public const string ContainersDirectory = "Library/Containers";

    /// <summary>The subdirectory of a container that becomes the application's home.</summary>
    /// <remarks>
    ///     Named <c>Data</c> rather than being the container root so that the
    ///     container can grow siblings — a caches directory the system may delete
    ///     under pressure, a bookmarks store the broker owns — without any of them
    ///     landing in the application's <c>$HOME</c>.
    /// </remarks>
    public const string ContainerDataDirectory = "Data";

    /// <summary>The network namespace applications with a network permission join.</summary>
    /// <remarks>
    ///     ⚠ One shared namespace, not one per application. Doc 04 says "a shared
    ///     namespace joined only when <c>network.client</c> is present", and the reason
    ///     to share is that a namespace per application means a veth pair, an address,
    ///     and a NAT rule per application — a router, written by us, in the launch
    ///     path. What sharing costs is that two applications that both have network can
    ///     see each other's listening sockets; what it buys is that applications
    ///     without the permission see no network stack at all, which is the property
    ///     the permission is about.
    /// </remarks>
    public const string DefaultNetworkNamespace = "/run/netns/trinix-apps";

    /// <summary>The Wayland socket name a compositor publishes by default.</summary>
    public const string DefaultWaylandDisplay = "wayland-0";

    /// <summary>
    ///     The host paths every application needs read-only in order to start at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These are not permissions — they are what makes the process a process.
    ///         An application that could not read the runtime it was compiled against
    ///         would not run, and a checkbox for "may read the .NET runtime" would be
    ///         doc 04's forty-checkbox sheet in miniature.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>/etc/dotnet</c> is in the list and it is not obvious. Trinix
    ///         installs .NET to <c>/usr/lib/dotnet</c> rather than the
    ///         <c>/usr/share/dotnet</c> an apphost looks in, and the thing that
    ///         redirects it is <c>/etc/dotnet/install_location_&lt;arch&gt;</c>. An
    ///         application whose container has the runtime but not that file prints a
    ///         page of advice about installing .NET onto a machine that already has
    ///         it — which is exactly how the compositor's unit failed the first time.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>/usr/lib</c> whole, rather than an enumeration. Trinix is a
    ///         merged-<c>/usr</c> system, so this is libc, the runtime, and every
    ///         shared library in the image — read-only, from a signed image, with no
    ///         <c>/usr/bin</c> alongside it. Naming the individual sonames instead
    ///         would be a list that has to be revised every time a recipe grows a
    ///         dependency, and the first revision anybody forgot would be an
    ///         application that starts on the build machine and not in the image.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> DefaultRuntimePaths { get; } = [
        "/usr/lib",
        "/usr/share/fonts",
        "/etc/dotnet",
        "/etc/ssl/certs",
        "/etc/os-release"
    ];

    /// <summary>The application's identity, which names its container.</summary>
    public required string Identifier { get; init; }

    /// <summary>The verified bundle, on the host. Bound into the container at the same path.</summary>
    /// <remarks>
    ///     ⚠ Identity-mapped rather than relocated to a fixed path such as
    ///     <c>/App</c>. The bundle's own <c>TRINIX_BUNDLE</c>, the entry point the unit
    ///     execs, and whatever the application does with <c>argv[0]</c> then all agree
    ///     without translation — and a crash report, a journal line and an
    ///     <c>ls /Applications</c> name the same string.
    /// </remarks>
    public required string BundlePath { get; init; }

    /// <summary>The read-only skeleton the container is built on.</summary>
    public string ComposedRoot { get; init; } = DefaultComposedRoot;

    /// <summary><c>~/Library/Containers/&lt;identifier&gt;</c>, on the host.</summary>
    public required string ContainerRoot { get; init; }

    /// <summary>The part of the container that becomes the application's <c>$HOME</c>.</summary>
    public required string ContainerData { get; init; }

    /// <summary>The user the application runs as — the logged-in one, not a dynamic user.</summary>
    /// <remarks>
    ///     ⚠ Not <c>DynamicUser=yes</c>, which every other Trinix unit uses. A dynamic
    ///     uid changes between launches, so every file the application wrote last time
    ///     would belong to somebody who no longer exists. The containment here comes
    ///     from the mount namespace, not from the uid.
    /// </remarks>
    public required string UserName { get; init; }

    /// <summary>The group the application runs as.</summary>
    public required string GroupName { get; init; }

    /// <summary>The uid, which is what names the runtime directory.</summary>
    public required uint UserId { get; init; }

    /// <summary>Where the compositor's Wayland socket is, on the host.</summary>
    public required string WaylandSocket { get; init; }

    /// <summary>Host paths bound in read-only so the application can start.</summary>
    public IReadOnlyList<string> RuntimePaths { get; init; } = DefaultRuntimePaths;

    /// <summary>The namespace an application with a network permission joins.</summary>
    public string NetworkNamespace { get; init; } = DefaultNetworkNamespace;

    /// <summary>The application's <c>$HOME</c>, as seen from inside.</summary>
    /// <remarks>
    ///     Derived from <see cref="UserName" /> rather than stored, because it must be
    ///     the home that <c>/etc/passwd</c> names for that user: a process that
    ///     resolves its own home through <c>getpwuid</c> instead of <c>$HOME</c> — and
    ///     some do — has to land in the same place.
    /// </remarks>
    public string HomeInside => "/home/" + UserName;

    /// <summary><c>XDG_RUNTIME_DIR</c>, the same path inside and out.</summary>
    public string RuntimeDirectory => "/run/user/" + UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    ///     The ordinary layout for an installed application and a logged-in user.
    /// </summary>
    /// <param name="info">The bundle's verified <c>Info.json</c>.</param>
    /// <param name="bundlePath">Where the bundle is on the host.</param>
    /// <param name="userName">The user the application runs as.</param>
    /// <param name="userId">Their uid.</param>
    /// <param name="userHome">Their real home directory, which holds the containers.</param>
    /// <param name="groupName">Their group; defaults to a group named after them.</param>
    public static SandboxLayout For(
        BundleInfo info,
        string bundlePath,
        string userName,
        uint userId,
        string userHome,
        string? groupName = null
    ) {
        var containerRoot = Path.Combine(userHome, ContainersDirectory, info.Identifier);

        return new SandboxLayout {
            Identifier = info.Identifier,
            BundlePath = bundlePath,
            ContainerRoot = containerRoot,
            ContainerData = Path.Combine(containerRoot, ContainerDataDirectory),
            UserName = userName,
            GroupName = groupName ?? userName,
            UserId = userId,
            WaylandSocket = $"/run/user/{userId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{DefaultWaylandDisplay}"
        };
    }

    /// <summary>
    ///     The ordinary layout for an installed application and a resolved user.
    /// </summary>
    /// <param name="info">The bundle's verified <c>Info.json</c>.</param>
    /// <param name="bundlePath">Where the bundle is on the host.</param>
    /// <param name="user">Who is launching it, from <see cref="SandboxUser" />.</param>
    public static SandboxLayout For(BundleInfo info, string bundlePath, SandboxUser user) {
        ArgumentNullException.ThrowIfNull(user);

        return For(info, bundlePath, user.Name, user.UserId, user.Home, user.GroupName);
    }

    /// <summary>
    ///     The layout for the user this process is running as.
    /// </summary>
    /// <param name="info">The bundle's verified <c>Info.json</c>.</param>
    /// <param name="bundlePath">Where the bundle is on the host.</param>
    /// <exception cref="InvalidOperationException">
    ///     There is no passwd entry for this process's uid.
    /// </exception>
    /// <remarks>
    ///     ⚠ The one method in this file that touches the machine, and it is separate
    ///     from <see cref="For(BundleInfo, string, string, uint, string, string?)" />
    ///     rather than folded into it for the reason the type's own remarks give:
    ///     discovery can fail, can differ between the machine a unit is built on and
    ///     the one it runs on, and cannot be exercised from a unit test. Keeping it to
    ///     one call means everything downstream of it is still a function of its
    ///     arguments.
    /// </remarks>
    public static SandboxLayout ForCurrentUser(BundleInfo info, string bundlePath) =>
        For(info, bundlePath, SandboxUser.Current());

    /// <summary>
    ///     Everything wrong with this layout, or an empty list.
    /// </summary>
    /// <remarks>
    ///     ⚠ The only rule worth checking here is that every path is absolute, and it
    ///     is worth checking because of where a relative one would surface. systemd
    ///     resolves <c>BindPaths=</c> against the unit's working directory, so a
    ///     relative path does not fail — it silently names something else, in a
    ///     namespace nobody can inspect after the fact. Existence is deliberately
    ///     <i>not</i> checked: the unit may legitimately be constructed on a machine
    ///     that is not the one it will run on, and a check that passes on the builder's
    ///     machine proves nothing about the target's.
    /// </remarks>
    public IReadOnlyList<string> Validate() {
        List<string> problems = [];

        Absolute(nameof(BundlePath), BundlePath);
        Absolute(nameof(ComposedRoot), ComposedRoot);
        Absolute(nameof(ContainerRoot), ContainerRoot);
        Absolute(nameof(ContainerData), ContainerData);
        Absolute(nameof(WaylandSocket), WaylandSocket);
        Absolute(nameof(NetworkNamespace), NetworkNamespace);

        foreach (var path in RuntimePaths) {
            Absolute(nameof(RuntimePaths), path);
        }

        if (string.IsNullOrEmpty(UserName)) {
            problems.Add("userName is empty");
        }

        if (string.IsNullOrEmpty(GroupName)) {
            problems.Add("groupName is empty");
        }

        return problems;

        void Absolute(string field, string value) {
            if (string.IsNullOrEmpty(value)) {
                problems.Add($"{field} is empty");
            } else if (!value.StartsWith('/')) {
                problems.Add($"{field} '{value}' is not an absolute path");
            }
        }
    }
}
