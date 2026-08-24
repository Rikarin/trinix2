using System.Text.Json.Serialization;

namespace Trinix.Bundle;

/// <summary>
///     <c>Contents/Info.json</c> — what an application says it is.
/// </summary>
/// <remarks>
///     Deliberately small. Everything here is either something the launcher needs
///     in order to start the app, or something the system needs in order to decide
///     whether it should. Presentation metadata (icons, document types, URL
///     schemes) belongs to the shell and is not invented before there is a shell to
///     consume it.
/// </remarks>
public sealed class BundleInfo {
    /// <summary>Format marker, so a future revision can be told apart from a corrupt file.</summary>
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = BundleSchema.Info;

    /// <summary>Reverse-DNS identity, e.g. <c>io.trinix.hello</c>. Unique per application.</summary>
    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    /// <summary>The name shown to a person.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Ordered version, compared by the package manager. Dotted numeric.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>The version shown to a person, when that differs from the ordered one.</summary>
    [JsonPropertyName("shortVersion")]
    public string? ShortVersion { get; init; }

    /// <summary>
    ///     The executable to run, as a bundle-relative path — normally under
    ///     <c>Contents/Bin</c>, but named rather than assumed so that a bundle
    ///     carrying several executables can say which one is the application.
    /// </summary>
    [JsonPropertyName("entryPoint")]
    public required string EntryPoint { get; init; }

    /// <summary>The oldest Trinix that can run this, matched against <c>VERSION_ID</c>.</summary>
    [JsonPropertyName("minimumSystemVersion")]
    public string? MinimumSystemVersion { get; init; }

    /// <summary>
    ///     What the application asks to be allowed to do.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Declared, recorded, and not yet enforced.</b> There is no sandbox in
    ///         Trinix, so nothing stops an application from opening a socket it never
    ///         declared. This exists now because the permission set has to be part of
    ///         the *signed* manifest to be worth anything later: a permission added
    ///         after the fact by whoever is running the app is not a permission, and
    ///         retrofitting the signature to cover it would invalidate every bundle
    ///         already signed.
    ///     </para>
    ///     <para>
    ///         See <see cref="BundlePermissions" /> for the vocabulary.
    ///     </para>
    /// </remarks>
    [JsonPropertyName("permissions")]
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>Free-form categories for a future app catalogue.</summary>
    [JsonPropertyName("categories")]
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    ///     Everything wrong with this file, or an empty list.
    /// </summary>
    /// <remarks>
    ///     Returns all the problems rather than the first, because this is what a
    ///     developer sees when packaging fails and one round trip per mistake is a
    ///     bad way to spend an afternoon.
    /// </remarks>
    public IReadOnlyList<string> Validate() {
        List<string> problems = [];

        if (Schema != BundleSchema.Info) {
            problems.Add($"schema is '{Schema}', expected '{BundleSchema.Info}'");
        }

        if (!IsReverseDns(Identifier)) {
            problems.Add($"identifier '{Identifier}' is not a reverse-DNS name (e.g. io.trinix.hello)");
        }

        if (string.IsNullOrWhiteSpace(Name)) {
            problems.Add("name is empty");
        }

        if (!IsDottedNumeric(Version)) {
            problems.Add($"version '{Version}' is not dotted-numeric");
        }

        if (!BundleLayout.IsSafeRelativePath(EntryPoint)) {
            problems.Add($"entryPoint '{EntryPoint}' is not a safe bundle-relative path");
        } else if (!EntryPoint.StartsWith(BundleLayout.Contents + "/", StringComparison.Ordinal)) {
            problems.Add($"entryPoint '{EntryPoint}' is outside {BundleLayout.Contents}/");
        }

        foreach (var permission in Permissions) {
            if (!BundlePermissions.IsKnown(permission)) {
                problems.Add($"permission '{permission}' is not one Trinix defines");
            }
        }

        return problems;
    }

    static bool IsReverseDns(string value) {
        if (string.IsNullOrEmpty(value)) {
            return false;
        }

        var labels = value.Split('.');
        if (labels.Length < 2) {
            return false;
        }

        foreach (var label in labels) {
            if (label.Length == 0) {
                return false;
            }

            foreach (var c in label) {
                var ok = char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_';
                if (!ok) {
                    return false;
                }
            }
        }

        return true;
    }

    static bool IsDottedNumeric(string value) {
        if (string.IsNullOrEmpty(value)) {
            return false;
        }

        foreach (var part in value.Split('.')) {
            if (part.Length == 0) {
                return false;
            }

            foreach (var c in part) {
                if (!char.IsAsciiDigit(c)) {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>
///     The permissions an application may declare.
/// </summary>
/// <remarks>
///     Coarse on purpose. A permission model that is finer than the enforcement
///     behind it produces a long list nobody reads and no additional safety; these
///     are the divisions that a future sandbox can actually implement with
///     namespaces, seccomp and the Wayland protocol surface Trinix already
///     controls.
/// </remarks>
public static class BundlePermissions {
    /// <summary>Open outbound network connections.</summary>
    public const string NetworkClient = "network.client";

    /// <summary>Listen for inbound connections.</summary>
    public const string NetworkServer = "network.server";

    /// <summary>Read and write the user's home directory.</summary>
    public const string FilesHome = "files.home";

    /// <summary>Read and write anywhere the invoking user can.</summary>
    public const string FilesAll = "files.all";

    /// <summary>Connect to the Wayland display and put windows on the screen.</summary>
    public const string Display = "display";

    /// <summary>Capture audio.</summary>
    public const string AudioInput = "audio.input";

    /// <summary>Play audio.</summary>
    public const string AudioOutput = "audio.output";

    /// <summary>Read input devices directly, rather than through the compositor.</summary>
    public const string DeviceInput = "device.input";

    /// <summary>Talk to Trinix's own system services over D-Bus.</summary>
    public const string SystemServices = "system.services";

    static readonly string[] All = [
        NetworkClient, NetworkServer, FilesHome, FilesAll,
        Display, AudioInput, AudioOutput, DeviceInput, SystemServices
    ];

    /// <summary>Every permission Trinix defines.</summary>
    public static IReadOnlyList<string> Known => All;

    /// <summary>Is this one of them?</summary>
    public static bool IsKnown(string permission) => Array.IndexOf(All, permission) >= 0;
}
