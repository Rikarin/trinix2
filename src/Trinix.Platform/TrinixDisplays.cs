using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;
using Vixen.Platform;

namespace Trinix.Platform;

/// <summary>
///     The displays the compositor told this client about.
/// </summary>
/// <remarks>
///     <para>
///         Every number here arrived through <c>wl_output</c>, which is the only way a
///         Wayland client learns anything about a screen. There is no enumeration API
///         to call and no display to query: outputs are announced, described over
///         several events, and finished with a <c>done</c>.
///     </para>
///     <para>
///         <b>What a client is not told is where its window is.</b> So
///         <see cref="TryGetForWindow" /> answers with the primary display rather than
///         the true one, which is right on the single-output system Trinix runs on
///         today and a guess on any other. It is documented as a guess rather than
///         silently correct-looking, because the alternative — returning nothing —
///         breaks DPI selection on the machine that does work.
///     </para>
/// </remarks>
sealed class TrinixDisplays : IDisplayInfo {
    readonly Dictionary<IntPtr, DisplayInfo> displays = [];
    readonly List<DisplayInfo> ordered = [];

    /// <inheritdoc />
    public IReadOnlyList<DisplayInfo> Displays => ordered;

    /// <inheritdoc />
    /// <remarks>
    ///     The first one announced. Wayland has no notion of a primary output — that
    ///     is a desktop-shell policy — and until Trinix's shell has an opinion, the
    ///     first is as good an answer as exists.
    /// </remarks>
    public DisplayInfo? Primary => ordered.Count > 0 ? ordered[0] : null;

    /// <inheritdoc />
    public bool TryGetForWindow(IWindow window, [NotNullWhen(true)] out DisplayInfo? display) {
        display = Primary;
        return display is not null;
    }

    /// <inheritdoc />
    public bool TryGetForPoint(Int2 point, [NotNullWhen(true)] out DisplayInfo? display) {
        foreach (var candidate in ordered) {
            var bounds = candidate.Bounds;
            if (point.X >= bounds.X && point.X < bounds.X + bounds.Width
                && point.Y >= bounds.Y && point.Y < bounds.Y + bounds.Height) {
                display = candidate;
                return true;
            }
        }

        display = null;
        return false;
    }

    internal void Changed(IntPtr output, int width, int height, int refreshMilliHertz, int scale,
                          int x, int y, string name) {
        // Refresh arrives in millihertz, which is the only unit wl_output speaks and
        // never the one anything else wants.
        var mode = new DisplayMode(new Int2(width, height), refreshMilliHertz / 1000f, IsHdr: false);
        var bounds = new Rectangle(x, y, width, height);

        var info = new DisplayInfo(
            Index: displays.Count,
            Name: string.IsNullOrWhiteSpace(name) ? $"Display {displays.Count + 1}" : name,
            Bounds: bounds,
            // The work area is the whole display: subtracting a menu bar and a dock
            // is the shell's job, and Trinix's shell does not exist yet.
            WorkArea: bounds,
            DpiScale: scale > 0 ? scale : 1,
            CurrentMode: mode,
            Modes: [mode],
            IsPrimary: displays.Count == 0
        );

        displays[output] = info;
        Rebuild();
    }

    internal void Removed(IntPtr output) {
        if (displays.Remove(output)) {
            Rebuild();
        }
    }

    void Rebuild() {
        ordered.Clear();
        ordered.AddRange(displays.Values);
    }
}
