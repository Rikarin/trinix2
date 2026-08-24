using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;
using Vixen.Platform;

namespace Trinix.Platform;

/// <summary>
///     The services Trinix does not implement yet, implemented honestly.
/// </summary>
/// <remarks>
///     <para>
///         Every one of these could be a <see cref="NotSupportedException" /> and none
///         of them is. Vixen's rule is that a capability is a runtime question with a
///         runtime answer — <see cref="IPlatform.Capabilities" /> is what says a
///         service is absent, and the service itself then answers "no" rather than
///         throwing, so an application that checks first and an application that does
///         not both keep running.
///     </para>
///     <para>
///         What is missing here is a list of the next increments, and each is missing
///         for a reason rather than by omission — the reasons are on the types.
///     </para>
/// </remarks>
static class TrinixServices {
    /// <summary>Where the application keeps its files.</summary>
    /// <remarks>
    ///     Vixen's own <see cref="StandardFileSystemHost" />, unmodified. Trinix is a
    ///     desktop with XDG conventions, which is exactly what that class implements,
    ///     and a Trinix-specific copy would differ only by being newer.
    /// </remarks>
    public static IFileSystemHost FileSystem(string organisation, string application) =>
        new StandardFileSystemHost(organisation, application);
}

/// <summary>
///     The clipboard Trinix does not have yet.
/// </summary>
/// <remarks>
///     ⚠ <b>Not a stub for its own sake.</b> The clipboard on Wayland is
///     <c>wl_data_device_manager</c>, and it is not a store: the application that
///     copied *is* the clipboard, and has to stay alive to answer a paste. That is a
///     real piece of work — offer objects, MIME negotiation, a pipe per read — and
///     doing it badly is worse than not doing it, because a half-implemented
///     clipboard silently loses what the user copied.
///     <see cref="PlatformCapabilities.Clipboard" /> is absent until it is done.
/// </remarks>
sealed class TrinixClipboard : IClipboard {
    /// <inheritdoc />
    public bool HasText => false;

    /// <inheritdoc />
    public bool HasImage => false;

    /// <inheritdoc />
    public bool TryGetText([NotNullWhen(true)] out string? text) {
        text = null;
        return false;
    }

    /// <inheritdoc />
    public bool SetText(string text) => false;

    /// <inheritdoc />
    public bool TryGetImage(out ClipboardImage image) {
        image = default;
        return false;
    }

    /// <inheritdoc />
    public bool SetImage(in ClipboardImage image) => false;

    /// <inheritdoc />
    public bool TryGetData(string format, out ReadOnlyMemory<byte> data) {
        data = default;
        return false;
    }

    /// <inheritdoc />
    public bool SetData(string format, ReadOnlySpan<byte> data) => false;

    /// <inheritdoc />
    public void Clear() { }
}

/// <summary>
///     The pickers Trinix does not have yet.
/// </summary>
/// <remarks>
///     A file picker is an application, and Trinix's is Phase 5's Files. Until there
///     is one there is nothing to launch — no zenity, no kdialog, no portal — so this
///     answers "the user chose nothing", which is what a cancelled dialog answers and
///     what every caller already handles.
/// </remarks>
sealed class TrinixDialogs : INativeDialogs {
    /// <inheritdoc />
    public ValueTask<string?> OpenFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> OpenFilesAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public ValueTask<string?> SaveFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask<string?> OpenFolderAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Returns <see cref="MessageBoxResult.None" /> rather than blocking. A
    ///     fatal-error path calls this before there is a renderer and must not hang
    ///     waiting for an answer nothing can give.
    /// </remarks>
    public ValueTask<MessageBoxResult> ShowMessageAsync(
        MessageBoxOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(MessageBoxResult.None);
}

/// <summary>
///     Battery and thermal state, which a virtual machine does not have.
/// </summary>
/// <remarks>
///     Reported as absent rather than guessed. Trinix's first hardware target is Phase
///     8, and until then every honest answer here is "there is no battery" — which
///     <see cref="PowerSource.Unknown" /> says and a fabricated 100% would not.
/// </remarks>
sealed class TrinixPowerInfo : IPowerInfo {
    /// <inheritdoc />
    public PowerSource Source => PowerSource.Unknown;

    /// <inheritdoc />
    public float? BatteryLevel => null;

    /// <inheritdoc />
    public TimeSpan? EstimatedTimeRemaining => null;

    /// <inheritdoc />
    public ThermalState Thermal => ThermalState.Nominal;

    /// <inheritdoc />
    public bool IsLowPowerMode => false;
}

/// <summary>
///     How many processors there are.
/// </summary>
/// <remarks>
///     Counts, but no pinning. <c>sched_setaffinity</c> exists on Trinix and would
///     work; what does not exist yet is a reason — nothing here schedules its own
///     threads across cores, and an affinity API that is used by nothing is an API
///     whose first user finds out it was never tested.
/// </remarks>
sealed class TrinixProcessors : IProcessorTopology {
    /// <inheritdoc />
    public int AvailableProcessors => Environment.ProcessorCount;

    /// <inheritdoc />
    /// <remarks>
    ///     The same number as <see cref="AvailableProcessors" />: telling cores from
    ///     threads means reading <c>/sys/devices/system/cpu</c>'s topology, and
    ///     reporting a guess would be worse than reporting a count that is true of
    ///     every machine without SMT and conservative on the rest.
    /// </remarks>
    public int PhysicalCores => Environment.ProcessorCount;

    /// <inheritdoc />
    public int PerformanceCores => Environment.ProcessorCount;

    /// <inheritdoc />
    public bool SupportsAffinity => false;

    /// <inheritdoc />
    public ProcessorClass ClassOf(int processor) => ProcessorClass.Performance;

    /// <inheritdoc />
    public bool TrySetAffinity(int processor) => false;

    /// <inheritdoc />
    public void ClearAffinity() { }
}

/// <summary>
///     Text entry, which on Wayland is the keyboard until there is an IME.
/// </summary>
/// <remarks>
///     Composed text already arrives: xkbcommon resolves dead keys and the compositor
///     sends the result, and that is what <see cref="PlatformEventKind.TextInput" />
///     carries. What is missing is <c>text-input-v3</c> — the protocol an input method
///     talks to — which is what a language needing candidate selection requires and
///     what <see cref="SetCandidateArea" /> would position.
/// </remarks>
sealed class TrinixTextInput : ITextInput {
    /// <inheritdoc />
    public bool IsActive { get; private set; }

    /// <inheritdoc />
    public bool HasOnScreenKeyboard => false;

    /// <inheritdoc />
    public bool IsOnScreenKeyboardVisible => false;

    /// <inheritdoc />
    public Rectangle OnScreenKeyboardArea => default;

    /// <inheritdoc />
    public void Activate(IWindow window) => IsActive = true;

    /// <inheritdoc />
    public void Deactivate() => IsActive = false;

    /// <inheritdoc />
    public void SetCandidateArea(IWindow window, Rectangle area) { }
}

/// <summary>
///     Suspend, resume and quit.
/// </summary>
/// <remarks>
///     A desktop process is never suspended by the system the way a mobile one is, so
///     the state is <see cref="ApplicationState.Running" /> from start to finish and
///     the interesting half is the quit protocol — which is a request, a chance to
///     refuse, and nothing else.
/// </remarks>
sealed class TrinixLifecycle : ILifecycle {
    /// <inheritdoc />
    public ApplicationState State => ApplicationState.Running;

    /// <inheritdoc />
    public MemoryPressure MemoryPressure => MemoryPressure.Normal;

    /// <inheritdoc />
    public bool IsQuitRequested { get; private set; }

    /// <inheritdoc />
    public void RequestQuit() => IsQuitRequested = true;

    /// <inheritdoc />
    public void CancelQuit() => IsQuitRequested = false;
}

/// <summary>
///     What the input devices are doing right now, as opposed to what they just did.
/// </summary>
/// <remarks>
///     The event stream is the truth and this is a running total of it, kept because
///     "is this key down" is a question a frame asks and an event is a thing that
///     happened. Both come from the same callbacks.
/// </remarks>
sealed class TrinixInput : IInputSource {
    readonly HashSet<Key> keysDown = [];
    readonly HashSet<MouseButton> buttonsDown = [];

    /// <inheritdoc />
    /// <remarks>
    ///     Empty. Gamepads on Linux are evdev joystick devices, which the compositor
    ///     does not forward — a client reads them itself, and Trinix has no
    ///     permission model for that yet.
    /// </remarks>
    public IReadOnlyList<IGamepad> Gamepads => [];

    /// <inheritdoc />
    public KeyModifiers Modifiers { get; internal set; }

    /// <inheritdoc />
    public Vector2 PointerPosition { get; internal set; }

    /// <inheritdoc />
    public bool TryGetGamepad(int deviceId, [NotNullWhen(true)] out IGamepad? gamepad) {
        gamepad = null;
        return false;
    }

    /// <inheritdoc />
    public bool IsKeyDown(Key key) => keysDown.Contains(key);

    /// <inheritdoc />
    public bool IsMouseButtonDown(MouseButton button) => buttonsDown.Contains(button);

    internal void KeyChanged(Key key, bool pressed) {
        if (pressed) {
            keysDown.Add(key);
        } else {
            keysDown.Remove(key);
        }
    }

    internal void ButtonChanged(MouseButton button, bool pressed) {
        if (pressed) {
            buttonsDown.Add(button);
        } else {
            buttonsDown.Remove(button);
        }
    }

    /// <summary>
    ///     Forgets everything, for a window that lost focus.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not tidiness. Keys held when focus leaves are never released as far as
    ///     this side is concerned, because the release goes to whoever has focus now —
    ///     so without this, Alt stays down forever and every subsequent click is an
    ///     Alt-click.
    /// </remarks>
    internal void ReleaseAll() {
        keysDown.Clear();
        buttonsDown.Clear();
        Modifiers = KeyModifiers.None;
    }
}
