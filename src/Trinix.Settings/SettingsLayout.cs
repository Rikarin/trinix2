namespace Trinix.Settings;

/// <summary>
///     Where each layer's database lives.
/// </summary>
/// <remarks>
///     <para>
///         Doc 02 says "one SQLite database per user", and that is the
///         <see cref="SettingsLayer.User" /> row: the file the user owns, the only one a
///         desktop session writes, and the one that gets backed up. The other three are
///         machine-wide, belong to root, and are usually absent — an unmanaged personal
///         machine has exactly one settings database, which is the shape doc 02 describes.
///     </para>
///     <para>
///         The user's path follows doc 01 § Paths: the home directory is mac-shaped, so
///         preferences are in <c>~/Library/Preferences</c> and not in <c>~/.config</c>.
///         ⚠ Nothing here creates <c>XDG_CONFIG_HOME</c>-shaped fallbacks; doc 01 points
///         <c>XDG_*</c> inside <c>~/Library</c> precisely so that this decision is made once.
///     </para>
///     <para>
///         ⚠ The system layers are read-only by <i>filesystem permission</i>, not by anything
///         this library does. That is on purpose. SQLite opened with read/write falls back to
///         read-only when the file denies the write, so a root-owned <c>0644</c> file in a
///         read-only image is already unwritable by a session, and adding a second,
///         library-level notion of read-only would only create somewhere for the two to
///         disagree. What the library *does* enforce is that a store writes exactly one
///         layer — the one it was opened for.
///     </para>
/// </remarks>
/// <param name="UserPath">The per-user database. Created on open if it is not there.</param>
public sealed record SettingsLayout(string UserPath) {
    /// <summary>The image's defaults, under <c>/usr/share</c> and therefore read-only.</summary>
    public const string DefaultSystemDefaultPath = "/usr/share/trinix/settings/defaults.db";

    /// <summary>A machine administrator's defaults, under <c>/etc</c>.</summary>
    public const string DefaultAdministratorPath = "/etc/trinix/settings/administrator.db";

    /// <summary>Management's policy, under <c>/etc</c> and above the user.</summary>
    public const string DefaultManagedProfilePath = "/etc/trinix/settings/managed.db";

    /// <summary>The user database's name inside <c>~/Library/Preferences</c>.</summary>
    public const string UserFileName = "Settings.db";

    /// <summary>The image's defaults, or <see langword="null" /> for none.</summary>
    public string? SystemDefaultPath { get; init; }

    /// <summary>The administrator's defaults, or <see langword="null" /> for none.</summary>
    public string? AdministratorPath { get; init; }

    /// <summary>The managed profile, or <see langword="null" /> for none.</summary>
    public string? ManagedProfilePath { get; init; }

    /// <summary>
    ///     The layout a Trinix session uses: the user's database under their home, and the
    ///     three system paths.
    /// </summary>
    /// <param name="home">The user's home directory, mac-shaped per doc 01 § Paths.</param>
    public static SettingsLayout ForHome(string home) {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        return new SettingsLayout(Path.Combine(home, "Library", "Preferences", UserFileName)) {
            SystemDefaultPath = DefaultSystemDefaultPath,
            AdministratorPath = DefaultAdministratorPath,
            ManagedProfilePath = DefaultManagedProfilePath,
        };
    }

    /// <summary>
    ///     All four layers side by side in one directory.
    /// </summary>
    /// <remarks>
    ///     For tests, and for the administrative tooling that has to build an
    ///     <see cref="SettingsLayer.Administrator" /> or
    ///     <see cref="SettingsLayer.ManagedProfile" /> database somewhere before installing
    ///     it. ⚠ Not a shape a session should ever be given: four settings databases in one
    ///     writable directory is four files one process can write, which is the arrangement
    ///     the layering exists to avoid.
    /// </remarks>
    /// <param name="directory">The directory to put all four files in.</param>
    public static SettingsLayout ForDirectory(string directory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return new SettingsLayout(Path.Combine(directory, "user.db")) {
            SystemDefaultPath = Path.Combine(directory, "defaults.db"),
            AdministratorPath = Path.Combine(directory, "administrator.db"),
            ManagedProfilePath = Path.Combine(directory, "managed.db"),
        };
    }

    /// <summary>The path for one layer, or <see langword="null" /> if this layout has none.</summary>
    /// <param name="layer">The layer to look up.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layer" /> is not one of the four.</exception>
    public string? PathFor(SettingsLayer layer) => layer switch {
        SettingsLayer.SystemDefault => SystemDefaultPath,
        SettingsLayer.Administrator => AdministratorPath,
        SettingsLayer.User => UserPath,
        SettingsLayer.ManagedProfile => ManagedProfilePath,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Not a settings layer."),
    };
}
