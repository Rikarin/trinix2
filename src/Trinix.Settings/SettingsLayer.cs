namespace Trinix.Settings;

/// <summary>
///     Doc 02's four layers, in the order they override one another.
/// </summary>
/// <remarks>
///     <para>
///         The numbers are the precedence: a value in a higher layer wins. Below all four
///         sits a fifth answer that is not a layer at all — the schema's compiled-in
///         default, which is what <see cref="SettingsResolution.Layer" /> being
///         <see langword="null" /> means. That distinction earns its keep: an image can ship
///         a <see cref="SystemDefault" /> database that changes a default without anybody
///         rebuilding the application that declared it, and "the user has never touched
///         this" stays a separate fact from "nobody has ever touched this".
///     </para>
///     <para>
///         ⚠ <see cref="ManagedProfile" /> is above <see cref="User" />, which is the whole
///         point of it and reads backwards until you say why: a managed machine's policy has
///         to survive the user opening Settings and changing it. Doc 08's panes are expected
///         to render a key resolved from this layer as disabled with an explanation, which
///         is why <see cref="SettingsResolution" /> hands back the layer and not only the
///         value. <see cref="Administrator" /> sits below the user because it is a *default*
///         an administrator sets for a machine, not a policy — a distinction that has to be
///         two layers because collapsing them means every site-wide default is unchangeable.
///     </para>
/// </remarks>
public enum SettingsLayer {
    /// <summary>Shipped with the image, in <c>/usr/share</c>. Read-only in practice and by filesystem.</summary>
    SystemDefault = 0,

    /// <summary>Set for this machine by whoever administers it, in <c>/etc</c>. A default, not a policy.</summary>
    Administrator = 1,

    /// <summary>The user's own, in their home directory. The layer everything writes by default.</summary>
    User = 2,

    /// <summary>Delivered by management and not the user's to change. Overrides everything.</summary>
    ManagedProfile = 3,
}
