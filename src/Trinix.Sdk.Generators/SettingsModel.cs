using System.Collections.Generic;

namespace Trinix.Sdk.Generators;

/// <summary>One declared key, resolved to everything the emitter needs to spell it.</summary>
/// <remarks>
///     ⚠ <see cref="Kind" /> is a string rather than an enum, and deliberately so: it is the
///     name of a <c>Trinix.Settings.SettingKind</c> member, and this assembly targets
///     netstandard2.0 and cannot reference <c>Trinix.Settings</c> — the dependency runs the
///     other way. The generator tests compile what comes out, which is what makes a typo
///     here a failing test rather than a broken consumer.
/// </remarks>
sealed class SettingsMember {
    public SettingsMember(
        string name,
        string kind,
        string propertyType,
        string defaultValueExpression,
        string defaultLiteral,
        string summary,
        IReadOnlyList<string>? enumMembers
    ) {
        Name = name;
        Kind = kind;
        PropertyType = propertyType;
        DefaultValueExpression = defaultValueExpression;
        DefaultLiteral = defaultLiteral;
        Summary = summary;
        EnumMembers = enumMembers;
    }

    /// <summary>The key's name, which is the C# property's name.</summary>
    public string Name { get; }

    /// <summary>The <c>SettingKind</c> member name: <c>Bool</c>, <c>Integer</c>, <c>Real</c>, <c>Text</c> or <c>Enum</c>.</summary>
    public string Kind { get; }

    /// <summary>The fully-qualified C# type the accessor exposes.</summary>
    public string PropertyType { get; }

    /// <summary>A C# expression constructing the default as a <c>SettingValue</c>.</summary>
    public string DefaultValueExpression { get; }

    /// <summary>The default as a literal of <see cref="PropertyType" />, for the enum fallback.</summary>
    public string DefaultLiteral { get; }

    /// <summary>The mandatory one-line description.</summary>
    public string Summary { get; }

    /// <summary>Every member name of the enum, for <c>Enum</c> keys; otherwise null.</summary>
    public IReadOnlyList<string>? EnumMembers { get; }
}

/// <summary>One <c>[SettingsSchema]</c> interface, resolved.</summary>
sealed class SettingsContract {
    public SettingsContract(
        string id,
        string containingNamespace,
        string interfaceName,
        string accessorName,
        string accessibility,
        IReadOnlyList<SettingsMember> members
    ) {
        Id = id;
        Namespace = containingNamespace;
        InterfaceName = interfaceName;
        AccessorName = accessorName;
        Accessibility = accessibility;
        Members = members;
    }

    /// <summary>The schema identifier.</summary>
    public string Id { get; }

    /// <summary>The declaring namespace, or empty for the global one.</summary>
    public string Namespace { get; }

    /// <summary>The interface's own name, for the generated documentation.</summary>
    public string InterfaceName { get; }

    /// <summary>The generated class's name: the interface's name without its leading <c>I</c>.</summary>
    public string AccessorName { get; }

    /// <summary>
    ///     <c>public</c> or <c>internal</c>, taken from the declaring interface.
    /// </summary>
    /// <remarks>
    ///     ⚠ Inherited rather than always <c>public</c>. An internal schema that produced a
    ///     public accessor fails to compile the moment one of its keys is an internal enum —
    ///     CS0053, in generated source, naming a member nobody wrote. Inheriting also states
    ///     the obvious right thing: an application's own preferences are the application's.
    /// </remarks>
    public string Accessibility { get; }

    /// <summary>The keys, in declaration order.</summary>
    public IReadOnlyList<SettingsMember> Members { get; }
}
