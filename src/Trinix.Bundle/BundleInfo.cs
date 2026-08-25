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
    ///     Why the application asks for each permission, keyed by the permission
    ///     string — the sentence the consent dialog shows.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Doc 04 § Consent: "the dialog names the application, the thing, and —
    ///         where it can — the why the app declared. <c>Info.json</c> may carry a
    ///         <c>usageDescription</c> per permission; <c>trinix doctor</c> warns when
    ///         it is missing, the Store requires it." This is that field, spelled as a
    ///         map because "per permission" is what it is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Optional, and deliberately not validated by
    ///         <see cref="Validate" />.</b> A missing description, or one for a
    ///         permission that was never declared, is a conformance finding rather than
    ///         a reason to refuse a launch — the permission set is what the sandbox is
    ///         built from, and this is the prose next to it. Making it a refusal here
    ///         would mean a bundle already in the field stops running because its
    ///         author left out a sentence. <c>Trinix.Conformance</c> is where every rule
    ///         about it lives, at the severity each one has earned.
    ///     </para>
    ///     <para>
    ///         ⚠ Additive to a signed format, which is only safe because the signature
    ///         covers <i>file bytes</i>: a bundle sealed before this field existed has
    ///         an <c>Info.json</c> that simply lacks it, verifies unchanged, and arrives
    ///         here as an empty map. Coalesced in the getter for the reason
    ///         <see cref="Permissions" /> gives.
    ///     </para>
    /// </remarks>
    [JsonPropertyName("usageDescriptions")]
    public IReadOnlyDictionary<string, string> UsageDescriptions {
        get => field ?? EmptyDescriptions;
        init;
    } = EmptyDescriptions;

    static readonly IReadOnlyDictionary<string, string> EmptyDescriptions =
        new Dictionary<string, string>(StringComparer.Ordinal);

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

        if (!IsReverseDnsIdentifier(Identifier)) {
            problems.Add($"identifier '{Identifier}' is not a reverse-DNS name (e.g. io.trinix.hello)");
        }

        if (string.IsNullOrWhiteSpace(Name)) {
            problems.Add("name is empty");
        }

        if (!IsOrderedVersion(Version)) {
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

    /// <summary>
    ///     Is <paramref name="value" /> shaped like an application identity?
    /// </summary>
    /// <param name="value">The candidate, e.g. <c>io.trinix.hello</c>.</param>
    /// <remarks>
    ///     ⚠ Public so that <c>trinix doctor</c> can ask the same question this type
    ///     answers, rather than carrying a second predicate that agrees with this one
    ///     until somebody changes one of them. The tool that <i>reports</i> a malformed
    ///     identity and the sealer that <i>refuses</i> one disagreeing about what
    ///     malformed means is the worst outcome available: a bundle that passes
    ///     conformance and fails packaging.
    ///     <para>
    ///         Deliberately permissive about things a reverse-DNS name would not
    ///         actually allow — an underscore, a leading digit, an uppercase letter —
    ///         because tightening it here would refuse bundles that already exist.
    ///         Doctor warns about each of those separately.
    ///     </para>
    /// </remarks>
    public static bool IsReverseDnsIdentifier(string value) {
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

    /// <summary>
    ///     Is <paramref name="value" /> a version the package manager can order?
    /// </summary>
    /// <param name="value">The candidate, e.g. <c>1.2.0</c>.</param>
    /// <remarks>
    ///     Dotted numeric and nothing else. Public for the reason
    ///     <see cref="IsReverseDnsIdentifier" /> gives.
    /// </remarks>
    public static bool IsOrderedVersion(string value) {
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

