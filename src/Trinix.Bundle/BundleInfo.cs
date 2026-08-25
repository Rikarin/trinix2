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
    ///         <b>Declared, signed, and enforced by construction rather than by trust.</b>
    ///         The field was defined before anything read it, because a permission set
    ///         is only worth something inside the signature — a permission added after
    ///         the fact by whoever is running the app is not a permission — and
    ///         retrofitting the signature to cover a new field would invalidate every
    ///         bundle already signed. That bet is what lets
    ///         <c>Trinix.Sandbox</c> derive a transient systemd unit from this array at
    ///         launch without a format change.
    ///     </para>
    ///     <para>
    ///         ⚠ Enforcement is still partial, and the honest boundary is drawn in
    ///         <c>Trinix.Sandbox</c> rather than here: most of the fourteen are
    ///         broker-mediated and have no systemd property at all, so declaring one
    ///         buys the right to <i>ask</i>, and the thing that says no is the broker.
    ///         Until it ships, those permissions are recorded and unenforced.
    ///     </para>
    ///     <para>
    ///         See <see cref="BundlePermissions" /> for the vocabulary and
    ///         <see cref="PermissionSet" /> for the parsed form. Validation refuses a
    ///         string it does not know rather than dropping it, for the reason argued
    ///         on <see cref="PermissionSet" />.
    ///     </para>
    ///     <para>
    ///         ⚠ The getter coalesces rather than trusting the field initialiser, and
    ///         the initialiser on its own was a bug that <c>Trinix.Bundle.Tests</c>
    ///         found on the day it was written. <c>System.Text.Json</c> constructs a
    ///         type with <c>required</c> members without running its field
    ///         initialisers, so an <c>Info.json</c> that simply omits
    ///         <c>"permissions"</c> — which is every application that asks for nothing
    ///         — arrived here as <see langword="null" />, and <see cref="Validate" />
    ///         threw a <see cref="NullReferenceException" /> out of
    ///         <see cref="BundleVerifier" /> rather than returning a refusal. A crash
    ///         in the component that decides whether code may run is strictly worse
    ///         than a "no". The initialiser stays so that code constructing this
    ///         directly still gets the documented default.
    ///     </para>
    /// </remarks>
    [JsonPropertyName("permissions")]
    public IReadOnlyList<string> Permissions { get => field ?? []; init; } = [];

    /// <summary>Free-form categories for a future app catalogue.</summary>
    /// <remarks>Coalesced for the same reason as <see cref="Permissions" />.</remarks>
    [JsonPropertyName("categories")]
    public IReadOnlyList<string> Categories { get => field ?? []; init; } = [];

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

