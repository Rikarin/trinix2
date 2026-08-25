namespace Trinix.Settings;

/// <summary>Light or dark, per doc 08 § Appearance.</summary>
public enum ColorScheme {
    /// <summary>Light.</summary>
    Light = 0,

    /// <summary>Dark.</summary>
    Dark = 1,
}

/// <summary>
///     The desktop's appearance: doc 02's own example of a setting an application follows.
/// </summary>
/// <remarks>
///     <para>
///         Doc 02 § Settings storage uses exactly one setting to explain what change events
///         are for — "an application that wants to follow the accent colour subscribes" — and
///         doc 08 makes every Settings control a binding to a schema'd key. This is that key,
///         and the three next to it.
///     </para>
///     <para>
///         ⚠ It lives in the store's own assembly rather than in an application, and there
///         are two reasons. The first is ownership: appearance belongs to the desktop, not to
///         any application, and doc 08 is explicit that third-party Settings panes are not a
///         thing — so until the Settings application exists there is nowhere better. The
///         second is that it puts the generator's output inside an assembly that
///         <c>scripts/check-api.ps1</c> gates. What a consumer of this generator actually
///         depends on is the <i>shape</i> of what it emits, and that shape is only watched
///         where it lands; without one real declaration in a gated assembly, a change to the
///         emitter would move nothing in any baseline. Trinix.Services.Contracts carries the
///         same argument for the service generator.
///     </para>
///     <para>
///         ⚠ <see cref="ReduceMotion" /> and <see cref="IncreaseContrast" /> are two booleans
///         rather than one accessibility flags enum, and that is the shape the generator
///         insists on — see its refusal of <c>[Flags]</c>. They are separate preferences to
///         the person setting them, they get separate switches in a pane, and a user who
///         turns one on has not asked for the other.
///     </para>
/// </remarks>
[SettingsSchema("com.trinix.desktop.appearance")]
public interface IAppearanceSettings {
    /// <summary>Light or dark.</summary>
    [Setting(Default = ColorScheme.Light, Summary = "Whether the desktop uses the light or the dark appearance.")]
    ColorScheme Scheme { get; }

    /// <summary>The accent colour, as <c>#rrggbb</c>.</summary>
    /// <remarks>
    ///     ⚠ Text rather than an integer, because a settings database is looked at with the
    ///     <c>sqlite3</c> CLI on machines that have a serial console and nothing else, and
    ///     <c>#0a84ff</c> is an answer where <c>690943</c> is a question. The store does not
    ///     validate the format; doc 08's pane is a colour well, so the only way to write a
    ///     malformed one is to write it by hand.
    /// </remarks>
    [Setting(Default = "#0a84ff", Summary = "The accent colour used for selection and controls, as #rrggbb.")]
    string AccentColor { get; }

    /// <summary>Whether to prefer crossfades over movement.</summary>
    [Setting(Default = false, Summary = "Replace animations that move things with animations that fade them.")]
    bool ReduceMotion { get; }

    /// <summary>Whether to draw stronger borders and darker text.</summary>
    [Setting(Default = false, Summary = "Draw stronger borders and higher-contrast text throughout the desktop.")]
    bool IncreaseContrast { get; }
}
