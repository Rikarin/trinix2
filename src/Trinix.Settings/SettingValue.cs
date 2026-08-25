using System.Globalization;

namespace Trinix.Settings;

/// <summary>
///     One preference's value, tagged with its <see cref="SettingKind" />.
/// </summary>
/// <remarks>
///     <para>
///         A struct with a tag rather than <see cref="object" />, because every read of
///         every setting would otherwise box, and because <c>object</c> makes a kind
///         mismatch a <see cref="InvalidCastException" /> at the call site rather than a
///         <see cref="SettingsTypeMismatchException" /> naming the key.
///     </para>
///     <para>
///         ⚠ Equality is what decides whether a write produced a change, and therefore
///         whether anybody is notified — see <see cref="SettingsStore" />. Two consequences.
///         <see cref="Real" /> compares with <see cref="double.Equals(double)" /> rather
///         than <c>==</c>, which is the one place where IEEE 754's answer is the wrong one:
///         under <c>==</c> a stored NaN never equals itself, so every commit that touched
///         it would report a change and wake every subscriber, forever.
///         <see cref="Text" /> and <see cref="Enum" /> compare ordinally, never by culture:
///         the assembly builds with <c>InvariantGlobalization</c>, and a setting whose
///         identity depended on the user's locale would change meaning at the airport.
///     </para>
/// </remarks>
public readonly struct SettingValue : IEquatable<SettingValue> {
    readonly long _integer;
    readonly double _real;
    readonly string? _text;

    SettingValue(SettingKind kind, long integer, double real, string? text) {
        Kind = kind;
        _integer = integer;
        _real = real;
        _text = text;
    }

    /// <summary>Which of the five this is.</summary>
    public SettingKind Kind { get; }

    /// <summary>A <see cref="SettingKind.Bool" />.</summary>
    public static SettingValue Bool(bool value) => new(SettingKind.Bool, value ? 1 : 0, 0, null);

    /// <summary>A <see cref="SettingKind.Integer" />.</summary>
    public static SettingValue Integer(long value) => new(SettingKind.Integer, value, 0, null);

    /// <summary>A <see cref="SettingKind.Real" />.</summary>
    public static SettingValue Real(double value) => new(SettingKind.Real, 0, value, null);

    /// <summary>A <see cref="SettingKind.Text" />.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    public static SettingValue Text(string value) =>
        new(SettingKind.Text, 0, 0, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>
    ///     A <see cref="SettingKind.Enum" />, named by its CLR member name.
    /// </summary>
    /// <remarks>
    ///     The name is not validated here — this type does not know the enum. The generated
    ///     accessor validates on the way in (it will not compile a member that does not
    ///     exist) and falls back to the schema default on the way out, which is what keeps a
    ///     hand-edited database from throwing at every read.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="member" /> is null.</exception>
    public static SettingValue EnumMember(string member) =>
        new(SettingKind.Enum, 0, 0, member ?? throw new ArgumentNullException(nameof(member)));

    /// <summary>The value, as a <see cref="bool" />.</summary>
    /// <exception cref="SettingsTypeMismatchException">This is not a <see cref="SettingKind.Bool" />.</exception>
    public bool AsBool() => Require(SettingKind.Bool)._integer != 0;

    /// <summary>The value, as a <see cref="long" />.</summary>
    /// <exception cref="SettingsTypeMismatchException">This is not a <see cref="SettingKind.Integer" />.</exception>
    public long AsInteger() => Require(SettingKind.Integer)._integer;

    /// <summary>The value, as a <see cref="double" />.</summary>
    /// <exception cref="SettingsTypeMismatchException">This is not a <see cref="SettingKind.Real" />.</exception>
    public double AsReal() => Require(SettingKind.Real)._real;

    /// <summary>The value, as a <see cref="string" />.</summary>
    /// <exception cref="SettingsTypeMismatchException">This is not a <see cref="SettingKind.Text" />.</exception>
    public string AsText() => Require(SettingKind.Text)._text!;

    /// <summary>The enum member's name.</summary>
    /// <exception cref="SettingsTypeMismatchException">This is not a <see cref="SettingKind.Enum" />.</exception>
    public string AsEnumMember() => Require(SettingKind.Enum)._text!;

    SettingValue Require(SettingKind kind) =>
        Kind == kind ? this : throw new SettingsTypeMismatchException(kind, Kind);

    /// <inheritdoc />
    public bool Equals(SettingValue other) =>
        Kind == other.Kind
        && Kind switch {
            SettingKind.None => true,
            SettingKind.Bool or SettingKind.Integer => _integer == other._integer,
            // An explicit == on doubles, argued for in the remarks above.
            SettingKind.Real => _real.Equals(other._real),
            _ => string.Equals(_text, other._text, StringComparison.Ordinal),
        };

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SettingValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Kind switch {
        SettingKind.None => 0,
        SettingKind.Bool or SettingKind.Integer => HashCode.Combine(Kind, _integer),
        SettingKind.Real => HashCode.Combine(Kind, _real),
        _ => HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(_text ?? "")),
    };

    /// <summary>Equality, per <see cref="Equals(SettingValue)" />.</summary>
    public static bool operator ==(SettingValue left, SettingValue right) => left.Equals(right);

    /// <summary>Inequality, per <see cref="Equals(SettingValue)" />.</summary>
    public static bool operator !=(SettingValue left, SettingValue right) => !left.Equals(right);

    /// <summary>
    ///     A round-trippable rendering, for diagnostics and for the store's own error
    ///     messages.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="CultureInfo.InvariantCulture" />, explicitly. The assembly sets
    ///     <c>InvariantGlobalization</c> so the current culture is already invariant, but
    ///     saying so here means a future component that turns ICU on does not silently start
    ///     writing <c>1,5</c> into a diagnostic somebody is comparing.
    /// </remarks>
    public override string ToString() => Kind switch {
        SettingKind.None => "<unset>",
        SettingKind.Bool => _integer != 0 ? "true" : "false",
        SettingKind.Integer => _integer.ToString(CultureInfo.InvariantCulture),
        SettingKind.Real => _real.ToString("R", CultureInfo.InvariantCulture),
        SettingKind.Text => "\"" + _text + "\"",
        _ => _text!,
    };
}
