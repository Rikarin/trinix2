namespace Trinix.Settings;

/// <summary>
///     Declares that an interface is a settings schema: a set of keys an application owns,
///     each typed, each with a default.
/// </summary>
/// <remarks>
///     <para>
///         The declaration is an <i>interface</i> rather than a class, and it is never
///         implemented by anything. That is deliberate: what the generator emits is a
///         reader over <see cref="SettingsStore" />, and an interface that could also be
///         implemented by hand would invite exactly the second implementation — a settings
///         object backed by a field, agreeing with the store until it does not.
///     </para>
///     <para>
///         ⚠ The identifier is reverse-DNS and lowercase, and it is the *namespace* half of
///         every key in the schema. Doc 02 asks for per-user, per-app namespacing; this is
///         the per-app half, and it is stated rather than derived from the CLR namespace
///         because a rename of a C# namespace must not silently orphan a user's
///         preferences.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class SettingsSchemaAttribute : Attribute {
    /// <summary>Declare a schema with this identifier.</summary>
    /// <param name="id">
    ///     Reverse-DNS, lowercase, at least two dot-separated segments — for example
    ///     <c>com.trinix.desktop.appearance</c>.
    /// </param>
    public SettingsSchemaAttribute(string id) => Id = id;

    /// <summary>The schema identifier, which namespaces every key in it.</summary>
    public string Id { get; }
}

/// <summary>
///     Declares one key: its default, and the sentence that keeps the store from becoming a
///     registry.
/// </summary>
/// <remarks>
///     <para>
///         Both <see cref="Default" /> and <see cref="Summary" /> are required, and the
///         generator refuses a property that omits either. <see cref="Default" /> is
///         obvious — a preference the user has never touched still has to answer, and doc
///         02 wants "reset to default" to mean something. <see cref="Summary" /> is the
///         interesting one.
///     </para>
///     <para>
///         ⚠ <b>The mandatory summary is how doc 02's "this is not a registry" is
///         enforced.</b> The document's own test for the boundary is that if you cannot
///         describe a thing in a settings schema, it is application data — and the part of
///         a schema that is hard to fake is the description. A window position, a cursor
///         into a document, a cached token: none of them has a one-line summary a person
///         would recognise, and writing a dishonest one is a visible act rather than an
///         omission. Doc 08 then spends the same sentence twice over, because the Settings
///         application's search index is generated from these summaries rather than
///         hand-listed.
///     </para>
///     <para>
///         They are named arguments rather than positional on purpose: two positional
///         arguments where one is a string default and the other is a string summary is a
///         pair somebody transposes, and the compiler cannot see it.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class SettingAttribute : Attribute {
    /// <summary>
    ///     The value the key has when no layer has set it. Must be a constant of the
    ///     property's own type.
    /// </summary>
    public object? Default { get; set; }

    /// <summary>
    ///     One sentence, in the user's language, describing what this key does. See the
    ///     type's remarks: this is a constraint, not documentation.
    /// </summary>
    public string? Summary { get; set; }
}
