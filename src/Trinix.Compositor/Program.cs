using System.Diagnostics;
using System.Runtime.InteropServices;
using Trinix.Interop;

namespace Trinix.Compositor;

/// <summary>
///     The compositor process: wire the native library to the window manager, then
///     hand the thread to the Wayland event loop.
/// </summary>
static unsafe class Program {
    // The window manager reached from the unmanaged callbacks. There is one
    // compositor per process and one event-loop thread, so a static is the
    // honest way to say so — the alternative, a GCHandle threaded through
    // every callback as a userdata pointer, would add ceremony to express a
    // singleton that C already assumes.
    static WindowManager? s_manager;
    static IntPtr s_server;

    static int Main(string[] args) {
        string? startup = null;
        string? socketName = null;
        var logLevel = Wlroots.LogLevel.Info;

        for (var i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--startup" when i + 1 < args.Length:
                    startup = args[++i];
                    break;

                case "--socket" when i + 1 < args.Length:
                    socketName = args[++i];
                    break;

                case "--log-level" when i + 1 < args.Length:
                    if (!Enum.TryParse(args[++i], true, out logLevel)) {
                        Log.Error($"unknown log level '{args[i]}'");
                        return 2;
                    }

                    break;

                case "--help":
                    Console.Out.WriteLine(
                        "usage: trinix-compositor [--socket <name>] [--startup <command>] "
                        + "[--log-level silent|error|info|debug]"
                    );
                    return 0;

                default:
                    Log.Error($"unexpected argument '{args[i]}'");
                    return 2;
            }
        }

        var callbacks = new Wlroots.Callbacks {
            OutputAdded = &OnOutputAdded,
            ToplevelMapped = &OnToplevelMapped,
            ToplevelUnmapped = &OnToplevelUnmapped,
            ToplevelRemoved = &OnToplevelRemoved,
            ToplevelRequestMove = &OnToplevelRequestMove,
            ToplevelRequestResize = &OnToplevelRequestResize,
            Key = &OnKey,
            PointerMotion = &OnPointerMotion,
            PointerButton = &OnPointerButton,
            InputAdded = &OnInputAdded,
            ToplevelDecorated = &OnToplevelDecorated,
            ToplevelControl = &OnToplevelControl,
            MenuBegin = &OnMenuBegin,
            MenuItem = &OnMenuItem,
            MenuEnd = &OnMenuEnd,
            MenuRemoved = &OnMenuRemoved
        };

        s_server = Wlroots.Create(in callbacks, logLevel);
        if (s_server == IntPtr.Zero) {
            // Nearly always one of three things, and worth saying so: there is
            // no DRM device, seatd is not running, or this user is not in the
            // group that may talk to it.
            Log.Error("could not create a compositor — no DRM device, or no seat");
            return 1;
        }

        var manager = new WindowManager(s_server);
        manager.ExitRequested += () => Wlroots.Terminate(s_server);
        s_manager = manager;

        try {
            if (!Wlroots.Start(s_server, socketName)) {
                Log.Error("could not start the backend");
                return 1;
            }

            var socket = Wlroots.SocketName(s_server);
            Log.Line($"listening on WAYLAND_DISPLAY={socket}");

            // Ready means "a client could connect now", which is exactly what
            // the socket existing and the backend running amounts to.
            Systemd.NotifyReady();
            Systemd.NotifyStatus($"running on {socket}");

            if (startup is not null) {
                StartClient(startup, socket);
            }

            Wlroots.Run(s_server);
            Log.Line("event loop returned, shutting down");
            return 0;
        } finally {
            s_manager = null;
            Wlroots.Destroy(s_server);
            s_server = IntPtr.Zero;
        }
    }

    /// <summary>
    ///     Launches a client into this compositor's session.
    /// </summary>
    /// <remarks>
    ///     Through <c>/bin/sh</c> so the argument can be a command line rather than
    ///     a program path, which is what makes it useful from a unit file and from
    ///     a boot check. The child is not waited on: a compositor outlives the
    ///     programs it starts, and reaping is systemd's problem once this process
    ///     is a service.
    /// </remarks>
    static void StartClient(string command, string socket) {
        var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        startInfo.Environment["WAYLAND_DISPLAY"] = socket;

        try {
            using var child = Process.Start(startInfo);
            Log.Line($"started '{command}' as pid {child?.Id.ToString() ?? "?"}");
        } catch (SystemException error) {
            Log.Error($"could not start '{command}': {error.Message}");
        }
    }

    // --- unmanaged entry points ------------------------------------------
    //
    // Called from the C event loop. They do nothing but translate and
    // delegate, because an exception thrown across the boundary into C is
    // undefined behaviour rather than a stack trace.

    [UnmanagedCallersOnly]
    static void OnOutputAdded(IntPtr output, int width, int height, int refresh, byte* name) =>
        WindowManager.OutputAdded(width, height, refresh, Wlroots.ReadString(name));

    [UnmanagedCallersOnly]
    static void OnToplevelMapped(IntPtr toplevel) => s_manager?.WindowMapped(toplevel);

    [UnmanagedCallersOnly]
    static void OnToplevelUnmapped(IntPtr toplevel) => s_manager?.WindowUnmapped(toplevel);

    [UnmanagedCallersOnly]
    static void OnToplevelRemoved(IntPtr toplevel) => s_manager?.WindowRemoved(toplevel);

    [UnmanagedCallersOnly]
    static void OnToplevelRequestMove(IntPtr toplevel) => s_manager?.RequestMove(toplevel);

    [UnmanagedCallersOnly]
    static void OnToplevelRequestResize(IntPtr toplevel, uint edges) => s_manager?.RequestResize(toplevel, edges);

    [UnmanagedCallersOnly]
    static byte OnKey(uint keysym, uint modifiers, byte pressed) =>
        s_manager?.Key(keysym, (Wlroots.Modifiers)modifiers, pressed != 0) == true ? (byte)1 : (byte)0;

    [UnmanagedCallersOnly]
    static void OnPointerMotion(double x, double y, uint timeMsec) => s_manager?.PointerMotion(x, y, timeMsec);

    [UnmanagedCallersOnly]
    static void OnPointerButton(uint button, byte pressed, uint timeMsec) =>
        s_manager?.PointerButton(button, pressed != 0);

    [UnmanagedCallersOnly]
    static void OnInputAdded(int type, byte* name) =>
        WindowManager.InputAdded((Wlroots.InputDeviceType)type, Wlroots.ReadString(name));

    [UnmanagedCallersOnly]
    static void OnToplevelDecorated(IntPtr toplevel, uint shadowStyle, int cornerRadius) =>
        WindowManager.WindowDecorated(toplevel, shadowStyle, cornerRadius);

    [UnmanagedCallersOnly]
    static void OnToplevelControl(
        IntPtr toplevel,
        uint control,
        int x,
        int y,
        int width,
        int height
    ) =>
        WindowManager.WindowControlZone(toplevel, control, x, y, width, height);

    // The handle these four carry is a menu, not a window: a menu bar belongs
    // to the client, so the same one covers every window that client owns.
    [UnmanagedCallersOnly]
    static void OnMenuBegin(IntPtr menu) => s_manager?.MenuBegin(menu);

    [UnmanagedCallersOnly]
    static void OnMenuItem(
        IntPtr menu,
        uint id,
        uint parent,
        uint kind,
        uint state,
        uint keysym,
        uint modifiers,
        byte* label
    ) =>
        s_manager?.MenuItem(
            menu,
            new(
                id,
                parent,
                (Wlroots.MenuItemKind)kind,
                (Wlroots.MenuItemState)state,
                keysym,
                (Wlroots.Modifiers)modifiers,
                Wlroots.ReadString(label) ?? string.Empty
            )
        );

    [UnmanagedCallersOnly]
    static void OnMenuEnd(IntPtr menu, uint itemCount) {
        _ = itemCount;
        s_manager?.MenuEnd(menu);
    }

    [UnmanagedCallersOnly]
    static void OnMenuRemoved(IntPtr menu) => s_manager?.MenuRemoved(menu);
}
