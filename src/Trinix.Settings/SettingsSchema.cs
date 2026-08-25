using System.Collections.Generic;

namespace Trinix.Settings;

/// <summary>
///     One key, as declared: what it is called, what it holds, what it is when nobody has
///     said otherwise, and what it means.
/// </summary>
/// <remarks>
///     The generator emits these; nothing stops a caller from writing one by hand, and a
///     tool that imports a schema from somewhere else has to. What no caller can do is skip
///     one — <see cref="SettingsStore.Get" /> refuses a key it has no descriptor for, which
///     is doc 02's "a key with no schema is a bug" enforced a second time at runtime for
///     the paths the compiler never saw.
/// </remarks>
public sealed class SettingDescriptor {
    /// <summary>Declare a key.</summary>
    /// <param name="name">The key's name within its schema.</param>
    /// <param name="default">The value when no layer has set it. Its kind decides the key's kind.</param>
    /// <param name="summary">One sentence a user would recognise. See <see cref="SettingAttribute" />.</param>
    /// <exception cref="ArgumentException">Any argument is empty, or the default has no kind.</exception>
    public SettingDescriptor(string name, SettingValue @default, string summary) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("A setting needs a name.", nameof(name));
        }

        if (@default.Kind == SettingKind.None) {
            throw new ArgumentException(
                $"'{name}' has no default. Doc 02: a key with no schema is a bug, and a schema "
                + "without a default cannot answer a read or implement 'reset'.",
                nameof(@default)
            );
        }

        if (string.IsNullOrWhiteSpace(summary)) {
            throw new ArgumentException(
                $"'{name}' has no summary. That is not a documentation lapse: doc 02 draws the "
                + "line between a preference and application data at whether it can be described, "
                + "and doc 08 generates the Settings search index from these sentences.",
                nameof(summary)
            );
        }

        Name = name;
        Default = @default;
        Summary = summary;
    }

    /// <summary>The key's name within its schema.</summary>
    public string Name { get; }

    /// <summary>What this key holds, taken from <see cref="Default" />.</summary>
    public SettingKind Kind => Default.Kind;

    /// <summary>The value when no layer has set it.</summary>
    public SettingValue Default { get; }

    /// <summary>One sentence describing the key, for the user and for doc 08's search index.</summary>
    public string Summary { get; }
}

/// <summary>
///     Everything one application declares about its preferences, in one place.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ The identifier is validated here as well as in the generator, and the
///         duplication is intentional. The generator catches the case the compiler can see;
///         this catches a schema built at runtime by a tool importing one — the settings
///         importer doc 08 will eventually want, or a test. A rule enforced in only the
///         place where it is convenient is a rule with a documented hole.
///     </para>
/// </remarks>
public sealed class SettingsSchema {
    readonly Dictionary<string, SettingDescriptor> _byName;

    /// <summary>Declare a schema.</summary>
    /// <param name="id">Reverse-DNS, lowercase, at least two segments.</param>
    /// <param name="settings">Its keys. Must not be empty, and names must be unique.</param>
    /// <exception cref="ArgumentException">The identifier is malformed, or the keys are not.</exception>
    public SettingsSchema(string id, IReadOnlyList<SettingDescriptor> settings) {
        ArgumentNullException.ThrowIfNull(settings);

        if (!IsWellFormedId(id)) {
            throw new ArgumentException(
                $"'{id}' is not a schema identifier. Reverse-DNS, lowercase, at least two "
                + "dot-separated segments of [a-z0-9-] — for example com.trinix.desktop.appearance.",
                nameof(id)
            );
        }

        if (settings.Count == 0) {
            throw new ArgumentException($"'{id}' declares no keys.", nameof(settings));
        }

        _byName = new Dictionary<string, SettingDescriptor>(settings.Count, StringComparer.Ordinal);
        foreach (var setting in settings) {
            ArgumentNullException.ThrowIfNull(setting);
            if (!_byName.TryAdd(setting.Name, setting)) {
                throw new ArgumentException($"'{id}' declares '{setting.Name}' twice.", nameof(settings));
            }
        }

        Id = id;
        Settings = settings;
    }

    /// <summary>The schema identifier, which namespaces every key in it.</summary>
    public string Id { get; }

    /// <summary>The keys, in declaration order.</summary>
    public IReadOnlyList<SettingDescriptor> Settings { get; }

    /// <summary>The descriptor for a key name, or <see langword="null" />.</summary>
    /// <param name="name">The key's name within this schema.</param>
    public SettingDescriptor? Find(string name) =>
        name is not null && _byName.TryGetValue(name, out var setting) ? setting : null;

    /// <summary>The full key for a name in this schema.</summary>
    /// <param name="name">The key's name within this schema.</param>
    public SettingsKey Key(string name) => new(Id, name);

    /// <summary>
    ///     Whether two schemas declare exactly the same thing.
    /// </summary>
    /// <remarks>
    ///     Used by <see cref="SettingsStore.Register" /> to tell an idempotent second
    ///     registration from two components that disagree about what a schema is — which is
    ///     a real possibility once a schema identifier is a string two assemblies can both
    ///     spell.
    /// </remarks>
    /// <param name="other">The schema to compare against.</param>
    public bool Declares(SettingsSchema other) {
        if (other is null || !string.Equals(Id, other.Id, StringComparison.Ordinal)) {
            return false;
        }

        if (Settings.Count != other.Settings.Count) {
            return false;
        }

        foreach (var mine in Settings) {
            if (other.Find(mine.Name) is not { } theirs
                || mine.Kind != theirs.Kind
                || mine.Default != theirs.Default
                || !string.Equals(mine.Summary, theirs.Summary, StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Whether a string is a schema identifier.
    /// </summary>
    /// <remarks>
    ///     ⚠ Hand-rolled rather than a regular expression, and not for speed: this assembly
    ///     builds with the trim and AOT analysers on, and <c>System.Text.RegularExpressions</c>
    ///     with a non-source-generated pattern is exactly the shape those exist to complain
    ///     about. Six lines of char comparison is also six lines somebody can read at 3 a.m.
    /// </remarks>
    /// <param name="id">The candidate identifier.</param>
    public static bool IsWellFormedId(string? id) {
        if (string.IsNullOrEmpty(id) || id[0] == '.' || id[id.Length - 1] == '.') {
            return false;
        }

        var segments = 1;
        var previousWasDot = false;
        foreach (var character in id) {
            if (character == '.') {
                if (previousWasDot) {
                    return false;
                }

                segments++;
                previousWasDot = true;
                continue;
            }

            if (!(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) {
                return false;
            }

            previousWasDot = false;
        }

        return segments >= 2;
    }
}
