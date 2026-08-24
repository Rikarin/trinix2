/*
 * trinix-wl-client — the C side of Trinix's Vixen platform.
 *
 * The compositor's C half (base/recipes/trinix-wlr) exists because wlroots'
 * objects and signals cannot be expressed in C#. This is its mirror image on
 * the client side, and it exists for a sharper reason: libwayland's client API
 * is *not* a wire protocol that C# could speak instead.
 *
 * Every request goes through wl_proxy_marshal_flags, which is variadic and
 * takes a `struct wl_interface *` whose wl_message tables encode each request's
 * argument signature. Rebuilding those tables in managed memory is possible and
 * is a transcription error waiting to happen — a wrong signature string does
 * not fail, it marshals the next argument as the wrong type and the compositor
 * disconnects the client with a protocol error naming an opcode.
 *
 * There is a harder constraint underneath that one. Vulkan's Wayland WSI takes
 * a `struct wl_display *` and calls libwayland on it — wl_display_create_queue,
 * wl_proxy_marshal, the lot. So the display object has to be libwayland's, not
 * a managed reimplementation of one, whatever else is decided. Given that, the
 * only question left was where the boundary falls, and it falls here.
 *
 * The split is the same as the compositor's: mechanism here, policy there.
 * Nothing in this file decides what a key means, when a window should close, or
 * what the pointer is doing. It reports that something happened and offers the
 * operations to act on it.
 *
 * Handles are opaque pointers. C# holds them as IntPtr and never dereferences
 * one, which is what keeps this interface stable across a libwayland or
 * protocol upgrade.
 */
#ifndef TRINIX_WL_CLIENT_H
#define TRINIX_WL_CLIENT_H

#include <stdbool.h>
#include <stdint.h>

struct trinix_wl_client;
struct trinix_wl_window;

/* Which optional globals the compositor offered. A missing one is a capability
 * the platform does not report, not an error: docs/vixen-platform-contract.md
 * says a window without trinix_shell_v1 is undecorated and works. */
enum trinix_wl_global {
    TRINIX_WL_GLOBAL_SHELL = 1 << 0,
    TRINIX_WL_GLOBAL_MENU = 1 << 1,
    TRINIX_WL_GLOBAL_DATA_DEVICE = 1 << 2,
};

/* Matches Vixen's WindowMode, restated so the managed side has a documented
 * contract rather than a copy of an enum from the other side of the wire. */
enum trinix_wl_window_mode {
    TRINIX_WL_WINDOW_WINDOWED = 0,
    TRINIX_WL_WINDOW_MINIMISED = 1,
    TRINIX_WL_WINDOW_MAXIMISED = 2,
    TRINIX_WL_WINDOW_FULLSCREEN = 3,
};

/*
 * Everything the client is told about. Called only from inside
 * trinix_wl_client_pump, on the thread that called it, never re-entrantly —
 * which is what lets the managed side turn each one into a PlatformEvent
 * without a lock.
 *
 * A null entry is "not interested".
 */
struct trinix_wl_callbacks {
    /*
     * The compositor has decided how big the window is. Both sizes, because
     * they are different numbers: a window is laid out in logical points and
     * its swapchain is built in device pixels, and confusing them renders a
     * quarter of the window or four times too much of it.
     *
     * ⚠ Arrives before the first frame and after every resize, and the surface
     * is *not* committed here. Once a Vulkan swapchain exists, Mesa's WSI owns
     * wl_surface.commit for this surface; a commit from this side would attach
     * a buffer the driver did not put there.
     */
    void (*window_configured)(struct trinix_wl_window *window,
                              int32_t width, int32_t height,
                              int32_t pixel_width, int32_t pixel_height,
                              int32_t scale, uint32_t mode);

    /* The user asked to close it. Whether that closes anything is policy. */
    void (*window_close_requested)(struct trinix_wl_window *window);

    void (*window_focus_changed)(struct trinix_wl_window *window, bool focused);

    /*
     * Keyboard. `code` is the evdev keycode as it came off the wire, not a
     * keysym: Vixen's Key is a physical position — WASD must be the same shape
     * under the left hand on AZERTY — so the managed side maps this to a HID
     * usage and never consults the layout for it.
     *
     * `text` is the other half and arrives separately, already composed by
     * xkbcommon, because a typed character may need a dead key or several
     * keystrokes and is not a function of one keycode.
     */
    void (*key)(struct trinix_wl_window *window, uint32_t code, bool pressed,
                uint32_t modifiers, uint32_t time_msec);
    void (*text)(struct trinix_wl_window *window, const char *utf8);

    /* Pointer, in surface-local logical coordinates. */
    void (*pointer_motion)(struct trinix_wl_window *window, double x, double y, uint32_t time_msec);
    void (*pointer_button)(struct trinix_wl_window *window, uint32_t button, bool pressed,
                           uint32_t time_msec);
    void (*pointer_scroll)(struct trinix_wl_window *window, double dx, double dy, uint32_t time_msec);
    void (*pointer_left)(struct trinix_wl_window *window);

    /*
     * An output appeared, changed or went away. Reported rather than
     * accumulated: which display a window is "on" is a question the managed
     * side answers, because it is the side that knows where the window is.
     */
    void (*output_changed)(void *output, int32_t width, int32_t height,
                           int32_t refresh_mhz, int32_t scale,
                           int32_t x, int32_t y, const char *name);
    void (*output_removed)(void *output);

    /* --- trinix-shell-v1 -------------------------------------------------
     *
     * The user operated one of the controls the window declared — the traffic
     * lights. The compositor does not act on it: closing a window is the
     * application's decision, because it may have something to ask about
     * first, so this arrives exactly as xdg_toplevel.close does.
     */
    void (*control_activated)(struct trinix_wl_window *window, uint32_t control);

    /* Hover is reported for the *group*: the glyphs appear when the pointer is
     * anywhere over them, and `control` names which one it is over. */
    void (*control_hover)(struct trinix_wl_window *window, uint32_t control, uint32_t state);

    /* What the shadow actually occupies outside the window geometry, which the
     * client cannot work out for itself — the compositor draws it. */
    void (*shadow_applied)(struct trinix_wl_window *window,
                           int32_t left, int32_t top, int32_t right, int32_t bottom);

    /* --- trinix-menu-v1 --------------------------------------------------
     *
     * `activated` is leaf items only, and deliberately does not toggle
     * anything: whether a click on a checkbox changes its state is the
     * application's business, and it says so with trinix_wl_window_menu_update.
     */
    void (*menu_activated)(struct trinix_wl_window *window, uint32_t id);
    void (*menu_about_to_show)(struct trinix_wl_window *window, uint32_t id);
    void (*menu_closed)(struct trinix_wl_window *window);
};

/* --- lifecycle ---------------------------------------------------------- */

/*
 * Connects to $WAYLAND_DISPLAY and binds the globals. Returns NULL if there is
 * no compositor to talk to, which is the ordinary case for a process started
 * outside a session and must not be fatal here — the managed side reports it
 * as a platform that cannot do windowing.
 */
struct trinix_wl_client *trinix_wl_client_connect(const struct trinix_wl_callbacks *callbacks,
                                                  void *user_data);

void trinix_wl_client_destroy(struct trinix_wl_client *client);

/* Which optional globals were found, as a mask of enum trinix_wl_global. */
uint32_t trinix_wl_client_globals(struct trinix_wl_client *client);

/*
 * Reads whatever the compositor has sent and calls back for each of it, then
 * flushes what this side has queued. Never blocks.
 *
 * ⚠ Dispatches the *default* queue only. Mesa's WSI runs the swapchain on a
 * queue of its own and dispatches it inside vkAcquireNextImageKHR and
 * vkQueuePresentKHR, so the two do not race and neither may drain the other's.
 *
 * Returns false once the connection is gone, which is how a compositor exiting
 * reaches the application.
 */
bool trinix_wl_client_pump(struct trinix_wl_client *client);

/*
 * Sends everything queued and blocks until the compositor has answered all of
 * it. Used once per window, immediately after trinix_wl_window_create, for the
 * reason on that function.
 *
 * ⚠ Blocking, unlike the pump. Anywhere else in a frame this would be a stall
 * on another process's scheduling.
 */
bool trinix_wl_client_roundtrip(struct trinix_wl_client *client);

/* The objects Vulkan's VkWaylandSurfaceCreateInfoKHR wants. */
void *trinix_wl_client_display(struct trinix_wl_client *client);
void *trinix_wl_window_surface(struct trinix_wl_window *window);

/* --- windows ------------------------------------------------------------ */

/*
 * Creates a surface with an xdg_toplevel role and commits it once, without a
 * buffer, which is what the protocol requires before the first configure.
 *
 * ⚠ Does *not* wait for that configure, and the caller must —
 * trinix_wl_client_roundtrip is how. The order matters in both directions: the
 * configure has to arrive before anything attaches a buffer, or the compositor
 * rejects the surface as never configured; and it has to arrive *after* the
 * caller has a handle to file it under, or the callback names a window nothing
 * has heard of yet and the size it carries is dropped.
 *
 * So the window is not on screen when this returns and has the size that was
 * asked for rather than the one it will have.
 */
struct trinix_wl_window *trinix_wl_window_create(struct trinix_wl_client *client,
                                                 const char *title, const char *app_id,
                                                 int32_t width, int32_t height,
                                                 bool resizable);

void trinix_wl_window_destroy(struct trinix_wl_window *window);

void trinix_wl_window_set_title(struct trinix_wl_window *window, const char *title);
void trinix_wl_window_set_mode(struct trinix_wl_window *window, uint32_t mode);
void trinix_wl_window_set_min_size(struct trinix_wl_window *window, int32_t width, int32_t height);
void trinix_wl_window_set_max_size(struct trinix_wl_window *window, int32_t width, int32_t height);

/*
 * Ask the compositor to move or resize the window, as if the user had grabbed
 * it. There is no other way: a Wayland client cannot position itself, and the
 * serial of the click that started the gesture is what authorises it — which is
 * why this takes no coordinates and only works from inside a button handler.
 */
void trinix_wl_window_begin_move(struct trinix_wl_window *window);
void trinix_wl_window_begin_resize(struct trinix_wl_window *window, uint32_t edges);

/* --- trinix-shell-v1 ----------------------------------------------------
 *
 * Every request here is double-buffered against the xdg surface's configure
 * cycle and takes effect on the next commit — which, once there is a
 * swapchain, is Mesa's commit rather than one this library makes. A window
 * that resizes and moves its close button therefore does both in one frame,
 * and there is nothing here to flush.
 *
 * All of it does nothing when the compositor did not offer trinix_shell_v1,
 * which is a window that is undecorated and works.
 */

/* enum trinix_shell_surface_v1.shadow_style: 0 none, 1 docked, 2 window,
 * 3 floating. Restated rather than included so the managed side has a
 * contract instead of a copy of a generated header. */
void trinix_wl_window_set_shadow(struct trinix_wl_window *window, uint32_t style);

/* Surface-local pixels; zero is square. The compositor clips to the rounded
 * rectangle, so the client need not punch its own corners out. */
void trinix_wl_window_set_corner_radius(struct trinix_wl_window *window, int32_t radius);

/*
 * Where dragging moves the window — the title bar, less the controls. The
 * client never sees motion inside it, which is what keeps dragging smooth
 * while the application's own thread is busy.
 *
 * One rectangle rather than a wl_region, because the protocol takes a region
 * and building one is three calls this side can make on the caller's behalf.
 * A zero width or height clears it, which the protocol spells as a null
 * region and means "the whole window".
 */
void trinix_wl_window_set_drag_region(struct trinix_wl_window *window,
                                      int32_t x, int32_t y, int32_t width, int32_t height);

/* How far inside its own edges a resize grab starts. Zero is a window nobody
 * can resize on a high-DPI display. */
void trinix_wl_window_set_resize_inset(struct trinix_wl_window *window, int32_t inset);

/* One traffic light's hit zone. enum control: 0 close, 1 minimise, 2 zoom.
 * The client draws it; the compositor decides what pointing at it means. */
void trinix_wl_window_set_control(struct trinix_wl_window *window, uint32_t control,
                                  int32_t x, int32_t y, int32_t width, int32_t height);
void trinix_wl_window_unset_control(struct trinix_wl_window *window, uint32_t control);

/* --- trinix-menu-v1 -----------------------------------------------------
 *
 * The shell holds the whole model and renders it. Changes accumulate until
 * commit, so a menu bar is never drawn half-built.
 */
void trinix_wl_window_menu_insert(struct trinix_wl_window *window, uint32_t id, uint32_t parent,
                                  int32_t index, uint32_t kind, const char *label);
void trinix_wl_window_menu_update(struct trinix_wl_window *window, uint32_t id,
                                  const char *label, uint32_t state);
void trinix_wl_window_menu_accelerator(struct trinix_wl_window *window, uint32_t id,
                                       uint32_t keysym, uint32_t modifiers);
void trinix_wl_window_menu_remove(struct trinix_wl_window *window, uint32_t id);
void trinix_wl_window_menu_commit(struct trinix_wl_window *window);

#endif /* TRINIX_WL_CLIENT_H */
