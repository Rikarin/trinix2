/*
 * trinix-wlr — the C side of Trinix's compositor.
 *
 * Trinix's compositor is written in C#. This library is the part that cannot
 * be: it owns the wlroots objects, the wl_listener structs that wlroots'
 * signals require, and the struct field accesses that would otherwise force
 * the managed side to hard-code an ABI layout that changes every wlroots
 * release.
 *
 * The split is mechanism here, policy there. Nothing in this file decides
 * where a window goes, what has focus, or what a key combination means; it
 * reports that those things happened and offers the operations to act on
 * them. Every function below is either "tell me what happened" (the callback
 * table) or "make this happen" (everything else).
 *
 * Handles are opaque pointers. C# holds them as IntPtr and never dereferences
 * one, which is what keeps this interface stable across a wlroots upgrade:
 * when a struct grows a field, this file is recompiled and the managed side
 * does not change.
 */
#ifndef TRINIX_WLR_H
#define TRINIX_WLR_H

#include <stdbool.h>
#include <stdint.h>

struct trinix_wlr_server;

/* Matches enum wlr_input_device_type, restated so the managed side has a
 * documented contract rather than a copy of someone else's enum. */
enum trinix_wlr_input_type {
    TRINIX_WLR_INPUT_KEYBOARD = 0,
    TRINIX_WLR_INPUT_POINTER = 1,
    TRINIX_WLR_INPUT_TOUCH = 2,
    TRINIX_WLR_INPUT_TABLET = 3,
    TRINIX_WLR_INPUT_OTHER = 4,
};

/*
 * Everything the compositor is told about. Called from the Wayland event
 * loop, on its thread, never re-entrantly.
 *
 * A null entry is treated as "not interested" — except `key`, where null
 * means every key goes to the focused client.
 */
struct trinix_wlr_callbacks {
    void (*output_added)(void *output, int32_t width, int32_t height,
                         int32_t refresh_mhz, const char *name);
    void (*output_removed)(void *output);

    void (*toplevel_added)(void *toplevel);
    void (*toplevel_mapped)(void *toplevel);
    void (*toplevel_unmapped)(void *toplevel);
    void (*toplevel_removed)(void *toplevel);

    /* The client asked to be dragged or resized — usually because the user
     * grabbed its own decorations. Whether that is honoured is policy. */
    void (*toplevel_request_move)(void *toplevel);
    void (*toplevel_request_resize)(void *toplevel, uint32_t edges);

    /*
     * Return true to keep the key. This is how the managed side owns
     * keybindings: what it does not claim is forwarded to the focused client,
     * and the decision is made per keypress rather than by registering a
     * table here.
     */
    bool (*key)(uint32_t keysym, uint32_t modifiers, bool pressed);

    /* The cursor has already moved; these report where it ended up. What to
     * do about it — drag a window, or pass the motion to whatever is under
     * the pointer — is the caller's decision. */
    void (*pointer_motion)(double lx, double ly, uint32_t time_msec);
    void (*pointer_button)(uint32_t button, bool pressed, uint32_t time_msec);

    void (*input_added)(int32_t type, const char *name);
};

/* --- lifecycle ---------------------------------------------------------- */

/* log_level maps to enum wlr_log_importance: 0 silent, 1 error, 2 info, 3 debug. */
struct trinix_wlr_server *trinix_wlr_create(const struct trinix_wlr_callbacks *callbacks,
                                            int log_level);

/*
 * Creates the Unix socket and starts the backend: this is where DRM master is
 * taken and the outputs appear. Returns false if there is nothing to run on.
 *
 * socket_name is the name under XDG_RUNTIME_DIR, or NULL to take the first
 * free one. A fixed name is what lets a unit file name the display in advance;
 * it fails rather than silently picking another if that name is in use, which
 * is the useful behaviour when something is already running.
 */
bool trinix_wlr_start(struct trinix_wlr_server *server, const char *socket_name);

/* The WAYLAND_DISPLAY value clients need. Valid until destroy. */
const char *trinix_wlr_socket(struct trinix_wlr_server *server);

/* Runs the event loop. Returns when terminate is called or the display dies. */
void trinix_wlr_run(struct trinix_wlr_server *server);
void trinix_wlr_terminate(struct trinix_wlr_server *server);
void trinix_wlr_destroy(struct trinix_wlr_server *server);

/* --- outputs ------------------------------------------------------------ */

void trinix_wlr_output_size(void *output, int32_t *width, int32_t *height);

/* --- toplevels ---------------------------------------------------------- */

/* Both may be null: a client is not obliged to set either, and several
 * toolkits set the title only after the first frame. */
const char *trinix_wlr_toplevel_title(void *toplevel);
const char *trinix_wlr_toplevel_app_id(void *toplevel);

void trinix_wlr_toplevel_set_position(void *toplevel, int32_t x, int32_t y);
void trinix_wlr_toplevel_set_size(void *toplevel, int32_t width, int32_t height);

/* Position in layout coordinates, size from the client's declared geometry —
 * which is the window as the user sees it, excluding whatever invisible
 * margin the toolkit uses for its shadows. */
void trinix_wlr_toplevel_get_box(void *toplevel, int32_t *x, int32_t *y,
                                 int32_t *width, int32_t *height);

/* Raise, activate, and give keyboard focus. One call because a window that is
 * raised without being activated, or activated without being raised, is a bug
 * every compositor writes once. */
void trinix_wlr_toplevel_focus(void *toplevel);
void trinix_wlr_toplevel_close(void *toplevel);

/* --- pointer ------------------------------------------------------------ */

void trinix_wlr_cursor_position(struct trinix_wlr_server *server, double *lx, double *ly);

/* Topmost toplevel at these layout coordinates, or null. */
void *trinix_wlr_toplevel_at(struct trinix_wlr_server *server, double lx, double ly);

/* Send enter/motion to whatever is under the cursor, or clear pointer focus if
 * that is nothing. The normal thing to do when not dragging a window. */
void trinix_wlr_pointer_passthrough(struct trinix_wlr_server *server, uint32_t time_msec);

#endif /* TRINIX_WLR_H */
