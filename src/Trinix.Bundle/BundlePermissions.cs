namespace Trinix.Bundle;

/// <summary>
///     One of the fourteen things an application may ask to be allowed to do.
/// </summary>
/// <remarks>
///     <para>
///         The vocabulary is decided by one sentence in
///         <c>docs/plan/04-sandbox-and-permissions.md</c>: an application's authority is
///         the set of things it can do <i>that it could not do by being a process</i>.
///         <see cref="NetworkClient" /> is a permission because the default is no
///         network. "Read its own files" is not, because that is what a process is.
///         Everything a mediating service already scopes to the caller's identity — a
///         font, the clipboard while focused, its own settings, an ordinary
///         notification, the time — is unpermissioned, because a checkbox for it would
///         describe a capability the application never lacked.
///     </para>
///     <para>
///         ⚠ <b>Flags, and the bit values are not a wire format.</b> These integers
///         never leave the process: <c>Info.json</c> carries the strings in
///         <see cref="BundlePermissions" />, which is what the signature covers.
///         Reordering the members would be a source-compatible change and a signature
///         non-event; renaming a string constant would invalidate every bundle in the
///         field.
///     </para>
///     <para>
///         The doc calls these "fourteen permissions, in four groups", and the count is
///         exact. ⚠ The prefixes give five — <c>network</c>, <c>files</c>,
///         <c>devices</c>, <c>system</c>, and <see cref="Display" /> standing alone. The
///         reconciliation the table implies is that <see cref="Display" /> is not a
///         group but the ungrouped one: it is the only permission the doc marks "never
///         shown", because an app with no window is not an app.
///         <see cref="PermissionGroup" /> models the five prefixes rather than the four
///         groups, because the prefix is the thing that is actually in the file.
///     </para>
/// </remarks>
[Flags]
public enum Permissions {
    /// <summary>Nothing. The set an application that declares no permissions has.</summary>
    None = 0,

    /// <summary>
    ///     A Wayland connection and a window. Never shown to the user, per doc 04 —
    ///     the consent dialog for "may this application have a window" is a dialog
    ///     every user answers the same way.
    /// </summary>
    Display = 1 << 0,

    /// <summary>Outbound sockets and DNS. Shown as "Connect to the internet".</summary>
    NetworkClient = 1 << 1,

    /// <summary>Listening sockets. Shown as "Accept incoming connections".</summary>
    NetworkServer = 1 << 2,

    /// <summary>
    ///     Broker-mediated reach into the user's documents. Shown as "Access your
    ///     files".
    /// </summary>
    /// <remarks>
    ///     ⚠ This is <b>not</b> a mount. Doc 04 is explicit that the grant is the user
    ///     picking a file: the broker draws the chooser out of the application's
    ///     address space and hands back an fd. Declaring it buys the right to ask,
    ///     which is why the sandbox emits no property for it at all.
    /// </remarks>
    FilesHome = 1 << 3,

    /// <summary>The same, for <c>/Volumes</c>. Shown as "Access removable drives".</summary>
    FilesRemovable = 1 << 4,

    /// <summary>A camera fd. Shown as "Use the camera".</summary>
    DevicesCamera = 1 << 5,

    /// <summary>An audio input node. Shown as "Use the microphone".</summary>
    DevicesMicrophone = 1 << 6,

    /// <summary>Position. Shown as "Know your location".</summary>
    DevicesLocation = 1 << 7,

    /// <summary>
    ///     A raw USB device, chosen by the user each time. Shown as "Talk to USB
    ///     devices".
    /// </summary>
    DevicesUsb = 1 << 8,

    /// <summary>Notifications that pierce Focus. Shown as "Send urgent alerts".</summary>
    SystemNotificationsCritical = 1 << 9,

    /// <summary>
    ///     Invoke other applications' verbs. Shown as "Control other applications".
    /// </summary>
    SystemAutomation = 1 << 10,

    /// <summary>
    ///     Keep running with no window, and be started at login. Shown as "Run in the
    ///     background".
    /// </summary>
    SystemBackground = 1 << 11,

    /// <summary>
    ///     Hold a screen-capture stream after the picker. Shown as "Record the
    ///     screen".
    /// </summary>
    SystemCapture = 1 << 12,

    /// <summary>
    ///     ⚠ Reserved and <b>never granted by the Store</b>: accessibility tools and
    ///     remote-control tools only, by explicit user grant in Settings. Shown as
    ///     "Control your computer".
    /// </summary>
    /// <remarks>
    ///     It is in the vocabulary rather than absent from it because the alternative
    ///     is an accessibility tool that has to ask for something else and get more.
    ///     A permission that exists and is refused by policy is a smaller hole than a
    ///     permission that does not exist and is worked around.
    /// </remarks>
    SystemInput = 1 << 13
}

/// <summary>
///     The prefix a permission's name is built from.
/// </summary>
/// <remarks>
///     Useful to a consent UI and a Privacy pane, which group by this, and to nothing
///     else. It is derived from the name rather than being a second fact that could
///     disagree with it.
/// </remarks>
public enum PermissionGroup {
    /// <summary>Windows and the compositor. Exactly one member.</summary>
    Display,

    /// <summary>Sockets.</summary>
    Network,

    /// <summary>The user's data, reached through the broker.</summary>
    Files,

    /// <summary>Hardware, reached as an fd from <c>trinixd</c>.</summary>
    Devices,

    /// <summary>Authority over the session or over other applications.</summary>
    System
}

/// <summary>
///     The strings <c>Info.json</c> uses, and the translation between them and
///     <see cref="Permissions" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every constant here is inside a signature.</b> The permission array is
///         covered by the bundle's Merkle manifest, which is the entire reason the
///         field was defined before anything enforced it. Renaming one is not a
///         refactor; it is a format change that every already-signed bundle predates.
///     </para>
///     <para>
///         The vocabulary changed once, when doc 04 replaced the placeholder set this
///         file shipped with — <c>files.all</c>, <c>audio.input</c>,
///         <c>audio.output</c>, <c>device.input</c>, <c>system.services</c> — with the
///         fourteen. That was affordable precisely once: Trinix has three applications
///         and they declare <c>display</c> and <c>files.home</c>, both of which
///         survived. The five that vanished have no replacement because they were not
///         permissions under doc 04's test — playing audio is something a process can
///         do, and talking to Trinix's own services is scoped by the service to the
///         caller's identity rather than by a mount.
///     </para>
/// </remarks>
public static class BundlePermissions {
    /// <summary>A Wayland connection and a window.</summary>
    public const string Display = "display";

    /// <summary>Outbound sockets, DNS.</summary>
    public const string NetworkClient = "network.client";

    /// <summary>Listening sockets.</summary>
    public const string NetworkServer = "network.server";

    /// <summary>Broker-mediated reach into the user's documents.</summary>
    public const string FilesHome = "files.home";

    /// <summary>Broker-mediated reach into <c>/Volumes</c>.</summary>
    public const string FilesRemovable = "files.removable";

    /// <summary>A camera fd.</summary>
    public const string DevicesCamera = "devices.camera";

    /// <summary>An audio input node.</summary>
    public const string DevicesMicrophone = "devices.microphone";

    /// <summary>Position.</summary>
    public const string DevicesLocation = "devices.location";

    /// <summary>A raw USB device, chosen by the user each time.</summary>
    public const string DevicesUsb = "devices.usb";

    /// <summary>Notifications that pierce Focus.</summary>
    public const string SystemNotificationsCritical = "system.notifications.critical";

    /// <summary>Invoke other applications' verbs.</summary>
    public const string SystemAutomation = "system.automation";

    /// <summary>Keep running with no window; be started at login.</summary>
    public const string SystemBackground = "system.background";

    /// <summary>Hold a screen-capture stream after the picker.</summary>
    public const string SystemCapture = "system.capture";

    /// <summary>⚠ Reserved; never granted by the Store.</summary>
    public const string SystemInput = "system.input";

    /// <summary>
    ///     The name/value table, in the order doc 04's vocabulary table lists them.
    /// </summary>
    /// <remarks>
    ///     ⚠ One array rather than a name-to-value map and a value-to-name map, so
    ///     that a permission added to one direction and forgotten in the other is not
    ///     a state this type can be in. Fourteen entries scanned linearly is faster
    ///     than hashing the string would be.
    /// </remarks>
    static readonly (string Name, Permissions Value)[] Vocabulary = [
        (Display, Permissions.Display),
        (NetworkClient, Permissions.NetworkClient),
        (NetworkServer, Permissions.NetworkServer),
        (FilesHome, Permissions.FilesHome),
        (FilesRemovable, Permissions.FilesRemovable),
        (DevicesCamera, Permissions.DevicesCamera),
        (DevicesMicrophone, Permissions.DevicesMicrophone),
        (DevicesLocation, Permissions.DevicesLocation),
        (DevicesUsb, Permissions.DevicesUsb),
        (SystemNotificationsCritical, Permissions.SystemNotificationsCritical),
        (SystemAutomation, Permissions.SystemAutomation),
        (SystemBackground, Permissions.SystemBackground),
        (SystemCapture, Permissions.SystemCapture),
        (SystemInput, Permissions.SystemInput)
    ];

    static readonly string[] AllNames = [.. Vocabulary.Select(entry => entry.Name)];

    /// <summary>Every permission Trinix defines, in the order the design doc lists them.</summary>
    public static IReadOnlyList<string> Known => AllNames;

    /// <summary>Is this one of them?</summary>
    public static bool IsKnown(string permission) => TryParse(permission, out _);

    /// <summary>
    ///     Turn one declared string into its flag.
    /// </summary>
    /// <param name="permission">The string as it appears in <c>Info.json</c>.</param>
    /// <param name="value">The flag, or <see cref="Permissions.None" />.</param>
    /// <returns><see langword="true" /> if Trinix defines this permission.</returns>
    /// <remarks>
    ///     ⚠ Ordinal comparison, deliberately. These are protocol tokens, not text: a
    ///     culture-aware comparison would make <c>files.home</c> mean something
    ///     different under a Turkish locale, and the assembly is built with
    ///     <c>InvariantGlobalization</c> anyway. Nothing is lowercased on the way in
    ///     either — <c>Files.Home</c> is not a permission, it is a typo, and a
    ///     launcher that quietly corrects typos in signed documents is a launcher that
    ///     accepts two spellings of the signature's meaning.
    /// </remarks>
    public static bool TryParse(string permission, out Permissions value) {
        foreach (var (name, flag) in Vocabulary) {
            if (string.Equals(name, permission, StringComparison.Ordinal)) {
                value = flag;
                return true;
            }
        }

        value = Permissions.None;
        return false;
    }

    /// <summary>
    ///     The <c>Info.json</c> spelling of a single flag.
    /// </summary>
    /// <param name="permission">Exactly one flag; not a combination.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="permission" /> is <see cref="Permissions.None" /> or names
    ///     more than one bit.
    /// </exception>
    public static string NameOf(Permissions permission) {
        foreach (var (name, flag) in Vocabulary) {
            if (flag == permission) {
                return name;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(permission),
            permission,
            "Not a single Trinix permission."
        );
    }

    /// <summary>
    ///     Which group a permission belongs to, for a UI that shows them grouped.
    /// </summary>
    /// <param name="permission">Exactly one flag; not a combination.</param>
    /// <exception cref="ArgumentOutOfRangeException">Not a single permission.</exception>
    public static PermissionGroup GroupOf(Permissions permission) =>
        permission switch {
            Permissions.Display => PermissionGroup.Display,
            Permissions.NetworkClient or Permissions.NetworkServer => PermissionGroup.Network,
            Permissions.FilesHome or Permissions.FilesRemovable => PermissionGroup.Files,
            Permissions.DevicesCamera or Permissions.DevicesMicrophone
                or Permissions.DevicesLocation or Permissions.DevicesUsb => PermissionGroup.Devices,
            Permissions.SystemNotificationsCritical or Permissions.SystemAutomation
                or Permissions.SystemBackground or Permissions.SystemCapture
                or Permissions.SystemInput => PermissionGroup.System,
            _ => throw new ArgumentOutOfRangeException(
                nameof(permission),
                permission,
                "Not a single Trinix permission."
            )
        };

    /// <summary>
    ///     Is this one of the permissions a repository will never grant, whatever the
    ///     bundle asks for?
    /// </summary>
    /// <param name="permission">Exactly one flag; not a combination.</param>
    /// <exception cref="ArgumentOutOfRangeException">Not a single permission.</exception>
    /// <remarks>
    ///     <para>
    ///         There is exactly one today — <see cref="Permissions.SystemInput" />, which
    ///         doc 04 marks "reserved and never granted by the Store: accessibility tools
    ///         and remote-control tools only, by explicit user grant in Settings".
    ///     </para>
    ///     <para>
    ///         ⚠ A <c>switch</c> over every member rather than a set containing one, and
    ///         that is the point of writing it this way: a fifteenth permission does not
    ///         compile until somebody has answered this question about it. The fact was
    ///         previously only in an XML comment, which meant <c>trinix doctor</c> could
    ///         not tell a developer before submission what the repository would tell them
    ///         after it — and doc 09 § Submission makes exactly this a gate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not a refusal anywhere in this assembly.</b> Sideloading and
    ///         developer mode still work, and the user can grant it in Settings; what
    ///         this predicate describes is a distribution policy, not containment. The
    ///         thing that enforces it is a person reviewing a submission, which doc 09
    ///         says out loud rather than pretending otherwise.
    ///     </para>
    /// </remarks>
    public static bool NeverGrantedByTheStore(Permissions permission) =>
        permission switch {
            Permissions.SystemInput => true,
            Permissions.Display or Permissions.NetworkClient or Permissions.NetworkServer
                or Permissions.FilesHome or Permissions.FilesRemovable
                or Permissions.DevicesCamera or Permissions.DevicesMicrophone
                or Permissions.DevicesLocation or Permissions.DevicesUsb
                or Permissions.SystemNotificationsCritical or Permissions.SystemAutomation
                or Permissions.SystemBackground or Permissions.SystemCapture => false,
            _ => throw new ArgumentOutOfRangeException(
                nameof(permission),
                permission,
                "Not a single Trinix permission."
            )
        };

    /// <summary>
    ///     What the consent dialog calls this, or <see langword="null" /> when it is
    ///     never shown.
    /// </summary>
    /// <param name="permission">Exactly one flag; not a combination.</param>
    /// <remarks>
    ///     Column three of doc 04's vocabulary table, kept next to columns one and two
    ///     rather than in the shell. The dialog is the security model's user
    ///     interface, and the phrasing is part of the model: "Access your files" is a
    ///     promise about what the broker will do, and it should be reviewed wherever
    ///     the permission is defined.
    ///     <para>
    ///         ⚠ Not localised. When Trinix grows a translation layer these become
    ///         message ids; until then an English string in the wrong place is easier
    ///         to find than an empty abstraction.
    ///     </para>
    /// </remarks>
    public static string? UserVisibleName(Permissions permission) =>
        permission switch {
            // Never shown. An app with no window is not an app.
            Permissions.Display => null,
            Permissions.NetworkClient => "Connect to the internet",
            Permissions.NetworkServer => "Accept incoming connections",
            Permissions.FilesHome => "Access your files",
            Permissions.FilesRemovable => "Access removable drives",
            Permissions.DevicesCamera => "Use the camera",
            Permissions.DevicesMicrophone => "Use the microphone",
            Permissions.DevicesLocation => "Know your location",
            Permissions.DevicesUsb => "Talk to USB devices",
            Permissions.SystemNotificationsCritical => "Send urgent alerts",
            Permissions.SystemAutomation => "Control other applications",
            Permissions.SystemBackground => "Run in the background",
            Permissions.SystemCapture => "Record the screen",
            Permissions.SystemInput => "Control your computer",
            _ => throw new ArgumentOutOfRangeException(
                nameof(permission),
                permission,
                "Not a single Trinix permission."
            )
        };
}

/// <summary>
///     What one application declared, parsed.
/// </summary>
/// <remarks>
///     <para>
///         The type exists so that the code deciding what an application may do is not
///         doing string comparisons. <c>Info.json</c> holds strings because it is a
///         signed document read by PowerShell; everything downstream of the parse
///         should be holding a value that cannot contain a permission Trinix has never
///         heard of.
///     </para>
///     <para>
///         ⚠ <b>An unknown permission is a refusal, not a shrug.</b> A bundle signed by
///         a future Trinix that asks for something this system does not understand is
///         exactly the case where guessing is wrong in both directions: ignoring the
///         string runs the application with less authority than its developer designed
///         for and than the user was shown, and inferring an approximation grants
///         authority nobody wrote down. <see cref="Parse(IReadOnlyList{string})" /> throws, and the launcher's
///         answer is "this needs a newer Trinix".
///     </para>
/// </remarks>
public readonly struct PermissionSet : IEquatable<PermissionSet> {
    /// <summary>Wrap a flag combination directly. Mostly for tests and for the builder.</summary>
    /// <param name="flags">The permissions in the set.</param>
    public PermissionSet(Permissions flags) {
        Flags = flags;
    }

    /// <summary>The permissions, as flags.</summary>
    public Permissions Flags { get; }

    /// <summary>An application that asked for nothing.</summary>
    public static PermissionSet Empty => default;

    /// <summary>Did the application declare all of <paramref name="permission" />?</summary>
    /// <param name="permission">One flag, or several combined with <c>|</c>.</param>
    public bool Has(Permissions permission) => (Flags & permission) == permission;

    /// <summary>Did it declare any of <paramref name="permission" />?</summary>
    /// <param name="permission">One flag, or several combined with <c>|</c>.</param>
    public bool HasAny(Permissions permission) => (Flags & permission) != Permissions.None;

    /// <summary>
    ///     The permissions in the set, one at a time, in vocabulary order.
    /// </summary>
    /// <remarks>
    ///     A method rather than an <see cref="System.Collections.Generic.IEnumerable{T}" />
    ///     implementation on the type itself: a set of flags is a value, and making it
    ///     a collection would invite it to be treated as one.
    /// </remarks>
    public IReadOnlyList<Permissions> Enumerate() {
        List<Permissions> present = [];
        foreach (var name in BundlePermissions.Known) {
            if (BundlePermissions.TryParse(name, out var flag) && Has(flag)) {
                present.Add(flag);
            }
        }

        return present;
    }

    /// <summary>
    ///     Parse what the bundle declared, or say which strings were not understood.
    /// </summary>
    /// <param name="declared">The <c>permissions</c> array from <c>Info.json</c>.</param>
    /// <param name="parsed">Everything that was understood.</param>
    /// <param name="unknown">Every string that was not, in the order it appeared.</param>
    /// <returns><see langword="true" /> when <paramref name="unknown" /> is empty.</returns>
    /// <remarks>
    ///     ⚠ <paramref name="parsed" /> is populated even when this returns
    ///     <see langword="false" />, because the caller that wants to <i>report</i> the
    ///     problem — <c>trinix doctor</c>, the packaging tool — wants both halves. The
    ///     caller that wants to <i>launch</i> should use <see cref="Parse(IReadOnlyList{string})" /> and let
    ///     it throw, so that "I forgot to check the return value" is not a way to run
    ///     an application whose manifest was not understood.
    /// </remarks>
    public static bool TryParse(
        IReadOnlyList<string> declared,
        out PermissionSet parsed,
        out IReadOnlyList<string> unknown
    ) {
        var flags = Permissions.None;
        List<string> rejected = [];

        foreach (var name in declared) {
            if (BundlePermissions.TryParse(name, out var flag)) {
                flags |= flag;
            } else {
                rejected.Add(name);
            }
        }

        parsed = new PermissionSet(flags);
        unknown = rejected;
        return rejected.Count == 0;
    }

    /// <summary>
    ///     Parse what the bundle declared, or refuse.
    /// </summary>
    /// <param name="declared">The <c>permissions</c> array from <c>Info.json</c>.</param>
    /// <exception cref="BundleException">
    ///     One or more declared strings are not permissions Trinix defines. The failure
    ///     is <see cref="BundleFailure.UnknownPermission" />, which the launcher
    ///     explains as "this application needs a newer Trinix" rather than as tampering
    ///     — the signature was fine, the vocabulary was not.
    /// </exception>
    public static PermissionSet Parse(IReadOnlyList<string> declared) {
        if (TryParse(declared, out var parsed, out var unknown)) {
            return parsed;
        }

        throw new BundleException(
            BundleFailure.UnknownPermission,
            $"declares {(unknown.Count == 1 ? "a permission" : "permissions")} this system does not "
            + $"define: {string.Join(", ", unknown.Select(name => $"'{name}'"))}"
        );
    }

    /// <summary>Parse a verified bundle's manifest.</summary>
    /// <param name="info">The bundle's <c>Info.json</c>.</param>
    /// <exception cref="BundleException">A declared permission is not one Trinix defines.</exception>
    public static PermissionSet Parse(BundleInfo info) => Parse(info.Permissions);

    /// <summary>The <c>Info.json</c> spellings, in vocabulary order.</summary>
    /// <remarks>
    ///     ⚠ Vocabulary order, not the order the bundle listed them. This is a set, so
    ///     the declaration order is not information; rendering it in a stable order is
    ///     what makes two bundles with the same authority produce the same string in a
    ///     log and in a diff.
    /// </remarks>
    public IReadOnlyList<string> ToNames() => [.. Enumerate().Select(BundlePermissions.NameOf)];

    /// <inheritdoc />
    public override string ToString() =>
        Flags == Permissions.None ? "(none)" : string.Join(" ", ToNames());

    /// <inheritdoc />
    public bool Equals(PermissionSet other) => Flags == other.Flags;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PermissionSet other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (int)Flags;

    /// <summary>Do two sets grant the same authority?</summary>
    /// <param name="left">One set.</param>
    /// <param name="right">The other.</param>
    public static bool operator ==(PermissionSet left, PermissionSet right) => left.Equals(right);

    /// <summary>Do two sets grant different authority?</summary>
    /// <param name="left">One set.</param>
    /// <param name="right">The other.</param>
    public static bool operator !=(PermissionSet left, PermissionSet right) => !left.Equals(right);
}
