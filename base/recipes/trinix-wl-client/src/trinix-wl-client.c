/*
 * trinix-wl-client — see trinix-wl-client.h for what this is and why.
 *
 * Every listener here does the same two things: keep enough state that a
 * question can be answered without a round trip, and hand the event to the
 * managed side. Nothing in this file decides anything.
 */
#include "trinix-wl-client.h"

#include <errno.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <poll.h>
#include <unistd.h>

#include <wayland-client.h>
#include <xkbcommon/xkbcommon.h>

#include "xdg-shell-client-protocol.h"
#include "trinix-shell-v1-client-protocol.h"
#include "trinix-menu-v1-client-protocol.h"

#define MAX_OUTPUTS 8

/*
 * The modifier mask is trinix-menu-v1's — which is xkb's, and already the
 * number an accelerator is registered with. One convention shared by the
 * protocol, this library and the managed side beats three that agree today.
 */
#define TRINIX_WL_MOD_SHIFT 1u
#define TRINIX_WL_MOD_CTRL  4u
#define TRINIX_WL_MOD_ALT   8u
#define TRINIX_WL_MOD_LOGO  64u

struct trinix_wl_output {
    struct trinix_wl_client *client;
    struct wl_output *proxy;
    uint32_t name;
    int32_t width, height, refresh_mhz, scale, x, y;
    char description[128];
    bool used;
};

/*
 * One exported menu bar, and what it is scoped to.
 *
 * `window` is NULL for the client's own bar, which is the ordinary kind: the
 * menus are the application's and every window it opens is under them. Only a
 * window whose menus genuinely differ gets one of its own, and then `window`
 * names it.
 *
 * The indirection exists so that one listener serves both scopes and so that
 * everything above this library holds a menu rather than a window — which is
 * what lets an application with nothing open still have a menu bar.
 */
struct trinix_wl_menu {
    struct trinix_wl_client *client;
    struct trinix_wl_window *window;
    struct trinix_menu_v1 *proxy;
};

struct trinix_wl_client {
    struct wl_display *display;
    struct wl_registry *registry;
    struct wl_compositor *compositor;
    struct xdg_wm_base *wm_base;
    struct wl_seat *seat;
    struct wl_keyboard *keyboard;
    struct wl_pointer *pointer;
    struct trinix_shell_v1 *shell;
    struct trinix_menu_manager_v1 *menu_manager;
    struct trinix_wl_menu *menu;
    struct wl_data_device_manager *data_device_manager;

    struct trinix_wl_output outputs[MAX_OUTPUTS];

    struct xkb_context *xkb;
    struct xkb_keymap *keymap;
    struct xkb_state *xkb_state;

    struct trinix_wl_callbacks callbacks;
    void *user_data;

    uint32_t globals;
    uint32_t shell_capabilities;

    /* Which window has each input focus, so that an event can name a window.
     * They differ: the pointer can be over one window while another has the
     * keyboard, which is not a state a click has to resolve. */
    struct trinix_wl_window *keyboard_focus;
    struct trinix_wl_window *pointer_focus;

    /*
     * The serial of the last input event. Wayland authorises an interactive
     * move or resize by the serial of the click that began it, which is what
     * makes trinix_wl_window_begin_move work at all — and why it only works
     * from inside a button handler.
     */
    uint32_t last_input_serial;

    double pointer_x, pointer_y;
    bool connected;
};

struct trinix_wl_window {
    struct trinix_wl_client *client;

    struct wl_surface *surface;
    struct xdg_surface *xdg_surface;
    struct xdg_toplevel *toplevel;
    struct trinix_shell_surface_v1 *shell_surface;

    /* An override, and null on almost every window: the menus are the
     * client's unless this one said otherwise. */
    struct trinix_wl_menu *menu;
    struct wl_region *drag_region;

    /* The size the compositor last asked for, and the one to use when it asks
     * for nothing — which is what it does when it has no opinion and means
     * "you choose". */
    int32_t width, height;
    int32_t fallback_width, fallback_height;
    int32_t scale;

    uint32_t pending_mode;
    uint32_t mode;
    bool configured;
};

/* ======================================================================== */
/* Outputs                                                                  */
/* ======================================================================== */

static void output_geometry(void *data, struct wl_output *proxy, int32_t x, int32_t y,
                            int32_t physical_width, int32_t physical_height, int32_t subpixel,
                            const char *make, const char *model, int32_t transform) {
    struct trinix_wl_output *output = data;
    (void) proxy; (void) physical_width; (void) physical_height; (void) subpixel; (void) transform;

    output->x = x;
    output->y = y;

    /* wl_output.name arrives only from version 4. make/model is what every
     * compositor has always sent and is enough to tell two displays apart. */
    if (output->description[0] == '\0') {
        snprintf(output->description, sizeof output->description, "%s %s",
                 make != NULL ? make : "", model != NULL ? model : "");
    }
}

static void output_mode(void *data, struct wl_output *proxy, uint32_t flags,
                        int32_t width, int32_t height, int32_t refresh) {
    struct trinix_wl_output *output = data;
    (void) proxy;

    /* The current mode only. The rest of the list is what the display could be
     * set to, which is a compositor's business rather than a client's. */
    if ((flags & WL_OUTPUT_MODE_CURRENT) == 0) {
        return;
    }

    output->width = width;
    output->height = height;
    output->refresh_mhz = refresh;
}

static void output_scale(void *data, struct wl_output *proxy, int32_t factor) {
    struct trinix_wl_output *output = data;
    (void) proxy;
    output->scale = factor > 0 ? factor : 1;
}

static void output_name(void *data, struct wl_output *proxy, const char *name) {
    struct trinix_wl_output *output = data;
    (void) proxy;
    snprintf(output->description, sizeof output->description, "%s", name != NULL ? name : "");
}

static void output_description(void *data, struct wl_output *proxy, const char *description) {
    (void) data; (void) proxy; (void) description;
}

/* An output's properties arrive as several events and are only whole at
 * `done`, which is therefore the only place it is worth reporting one. */
static void output_done(void *data, struct wl_output *proxy) {
    struct trinix_wl_output *output = data;
    struct trinix_wl_client *client = output->client;
    (void) proxy;

    if (client->callbacks.output_changed != NULL) {
        client->callbacks.output_changed(output, output->width, output->height,
                                         output->refresh_mhz, output->scale,
                                         output->x, output->y, output->description);
    }
}

static const struct wl_output_listener output_listener = {
    .geometry = output_geometry,
    .mode = output_mode,
    .done = output_done,
    .scale = output_scale,
    .name = output_name,
    .description = output_description,
};

/* ======================================================================== */
/* Keyboard                                                                 */
/* ======================================================================== */

static uint32_t modifier_mask(struct trinix_wl_client *client) {
    if (client->xkb_state == NULL) {
        return 0;
    }

    uint32_t mask = 0;
    if (xkb_state_mod_name_is_active(client->xkb_state, XKB_MOD_NAME_SHIFT, XKB_STATE_MODS_EFFECTIVE) > 0) {
        mask |= TRINIX_WL_MOD_SHIFT;
    }
    if (xkb_state_mod_name_is_active(client->xkb_state, XKB_MOD_NAME_CTRL, XKB_STATE_MODS_EFFECTIVE) > 0) {
        mask |= TRINIX_WL_MOD_CTRL;
    }
    if (xkb_state_mod_name_is_active(client->xkb_state, XKB_MOD_NAME_ALT, XKB_STATE_MODS_EFFECTIVE) > 0) {
        mask |= TRINIX_WL_MOD_ALT;
    }
    if (xkb_state_mod_name_is_active(client->xkb_state, XKB_MOD_NAME_LOGO, XKB_STATE_MODS_EFFECTIVE) > 0) {
        mask |= TRINIX_WL_MOD_LOGO;
    }
    return mask;
}

static void keyboard_keymap(void *data, struct wl_keyboard *keyboard, uint32_t format,
                            int32_t fd, uint32_t size) {
    struct trinix_wl_client *client = data;
    (void) keyboard;

    if (format != WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1) {
        close(fd);
        return;
    }

    char *text = mmap(NULL, size, PROT_READ, MAP_PRIVATE, fd, 0);
    if (text == MAP_FAILED) {
        close(fd);
        return;
    }

    struct xkb_keymap *keymap = xkb_keymap_new_from_string(
        client->xkb, text, XKB_KEYMAP_FORMAT_TEXT_V1, XKB_KEYMAP_COMPILE_NO_FLAGS);
    munmap(text, size);
    close(fd);

    if (keymap == NULL) {
        return;
    }

    struct xkb_state *state = xkb_state_new(keymap);
    if (state == NULL) {
        xkb_keymap_unref(keymap);
        return;
    }

    xkb_state_unref(client->xkb_state);
    xkb_keymap_unref(client->keymap);
    client->keymap = keymap;
    client->xkb_state = state;
}

static void keyboard_enter(void *data, struct wl_keyboard *keyboard, uint32_t serial,
                           struct wl_surface *surface, struct wl_array *keys) {
    struct trinix_wl_client *client = data;
    (void) keyboard; (void) keys;

    client->last_input_serial = serial;
    client->keyboard_focus = surface != NULL ? wl_surface_get_user_data(surface) : NULL;

    /* The keys already held are deliberately not replayed. They were pressed
     * somewhere else, and an application that received them would act on input
     * meant for another window. */
    if (client->keyboard_focus != NULL && client->callbacks.window_focus_changed != NULL) {
        client->callbacks.window_focus_changed(client->keyboard_focus, true);
    }
}

static void keyboard_leave(void *data, struct wl_keyboard *keyboard, uint32_t serial,
                           struct wl_surface *surface) {
    struct trinix_wl_client *client = data;
    (void) keyboard; (void) serial;

    struct trinix_wl_window *window = surface != NULL ? wl_surface_get_user_data(surface) : NULL;
    if (window != NULL && client->callbacks.window_focus_changed != NULL) {
        client->callbacks.window_focus_changed(window, false);
    }
    if (client->keyboard_focus == window) {
        client->keyboard_focus = NULL;
    }
}

static void keyboard_key(void *data, struct wl_keyboard *keyboard, uint32_t serial,
                         uint32_t time, uint32_t key, uint32_t state) {
    struct trinix_wl_client *client = data;
    (void) keyboard;

    client->last_input_serial = serial;
    if (client->keyboard_focus == NULL) {
        return;
    }

    const bool pressed = state == WL_KEYBOARD_KEY_STATE_PRESSED;

    /*
     * ⚠ The wire carries an evdev code and xkbcommon wants an X11 keycode,
     * which is the same number plus eight. Getting this wrong does not fail:
     * every key produces the keysym of a key eight positions away.
     */
    const uint32_t xkb_code = key + 8;

    if (client->callbacks.key != NULL) {
        client->callbacks.key(client->keyboard_focus, key, pressed, modifier_mask(client), time);
    }

    /* Text is the other half and only on press: a key release types nothing,
     * and a keysym with no character — every arrow key — types nothing either,
     * which xkb_state_key_get_utf8 reports as an empty string. */
    if (!pressed || client->xkb_state == NULL || client->callbacks.text == NULL) {
        return;
    }

    char text[64];
    const int length = xkb_state_key_get_utf8(client->xkb_state, xkb_code, text, sizeof text);
    if (length > 0 && text[0] != '\0') {
        client->callbacks.text(client->keyboard_focus, text);
    }
}

static void keyboard_modifiers(void *data, struct wl_keyboard *keyboard, uint32_t serial,
                               uint32_t depressed, uint32_t latched, uint32_t locked,
                               uint32_t group) {
    struct trinix_wl_client *client = data;
    (void) keyboard; (void) serial;

    if (client->xkb_state != NULL) {
        xkb_state_update_mask(client->xkb_state, depressed, latched, locked, 0, 0, group);
    }
}

static void keyboard_repeat_info(void *data, struct wl_keyboard *keyboard, int32_t rate,
                                 int32_t delay) {
    /* The compositor's repeat rate, which a client is expected to implement
     * itself. Not yet: Vixen's text input does its own repeat, and a second
     * one here would double every held key. */
    (void) data; (void) keyboard; (void) rate; (void) delay;
}

static const struct wl_keyboard_listener keyboard_listener = {
    .keymap = keyboard_keymap,
    .enter = keyboard_enter,
    .leave = keyboard_leave,
    .key = keyboard_key,
    .modifiers = keyboard_modifiers,
    .repeat_info = keyboard_repeat_info,
};

/* ======================================================================== */
/* Pointer                                                                  */
/* ======================================================================== */

static void pointer_enter(void *data, struct wl_pointer *pointer, uint32_t serial,
                          struct wl_surface *surface, wl_fixed_t x, wl_fixed_t y) {
    struct trinix_wl_client *client = data;
    (void) pointer;

    client->last_input_serial = serial;
    client->pointer_focus = surface != NULL ? wl_surface_get_user_data(surface) : NULL;
    client->pointer_x = wl_fixed_to_double(x);
    client->pointer_y = wl_fixed_to_double(y);

    if (client->pointer_focus != NULL && client->callbacks.pointer_motion != NULL) {
        client->callbacks.pointer_motion(client->pointer_focus, client->pointer_x,
                                         client->pointer_y, 0);
    }
}

static void pointer_leave(void *data, struct wl_pointer *pointer, uint32_t serial,
                          struct wl_surface *surface) {
    struct trinix_wl_client *client = data;
    (void) pointer; (void) serial;

    struct trinix_wl_window *window = surface != NULL ? wl_surface_get_user_data(surface) : NULL;
    if (window != NULL && client->callbacks.pointer_left != NULL) {
        client->callbacks.pointer_left(window);
    }
    if (client->pointer_focus == window) {
        client->pointer_focus = NULL;
    }
}

static void pointer_motion(void *data, struct wl_pointer *pointer, uint32_t time,
                           wl_fixed_t x, wl_fixed_t y) {
    struct trinix_wl_client *client = data;
    (void) pointer;

    client->pointer_x = wl_fixed_to_double(x);
    client->pointer_y = wl_fixed_to_double(y);

    if (client->pointer_focus != NULL && client->callbacks.pointer_motion != NULL) {
        client->callbacks.pointer_motion(client->pointer_focus, client->pointer_x,
                                         client->pointer_y, time);
    }
}

static void pointer_button(void *data, struct wl_pointer *pointer, uint32_t serial,
                           uint32_t time, uint32_t button, uint32_t state) {
    struct trinix_wl_client *client = data;
    (void) pointer;

    client->last_input_serial = serial;

    if (client->pointer_focus != NULL && client->callbacks.pointer_button != NULL) {
        client->callbacks.pointer_button(client->pointer_focus, button,
                                         state == WL_POINTER_BUTTON_STATE_PRESSED, time);
    }
}

static void pointer_axis(void *data, struct wl_pointer *pointer, uint32_t time,
                         uint32_t axis, wl_fixed_t value) {
    struct trinix_wl_client *client = data;
    (void) pointer;

    if (client->pointer_focus == NULL || client->callbacks.pointer_scroll == NULL) {
        return;
    }

    /* Wayland's axis is positive-down and positive-right; the sign is left
     * exactly as it arrived, because inverting it is a decision about what
     * scrolling means and that belongs to the side that has a preference. */
    const double amount = wl_fixed_to_double(value);
    if (axis == WL_POINTER_AXIS_VERTICAL_SCROLL) {
        client->callbacks.pointer_scroll(client->pointer_focus, 0.0, amount, time);
    } else {
        client->callbacks.pointer_scroll(client->pointer_focus, amount, 0.0, time);
    }
}

static void pointer_frame(void *data, struct wl_pointer *pointer) {
    (void) data; (void) pointer;
}
static void pointer_axis_source(void *data, struct wl_pointer *pointer, uint32_t source) {
    (void) data; (void) pointer; (void) source;
}
static void pointer_axis_stop(void *data, struct wl_pointer *pointer, uint32_t time,
                              uint32_t axis) {
    (void) data; (void) pointer; (void) time; (void) axis;
}
static void pointer_axis_discrete(void *data, struct wl_pointer *pointer, uint32_t axis,
                                  int32_t discrete) {
    (void) data; (void) pointer; (void) axis; (void) discrete;
}
static void pointer_axis_value120(void *data, struct wl_pointer *pointer, uint32_t axis,
                                  int32_t value120) {
    (void) data; (void) pointer; (void) axis; (void) value120;
}
static void pointer_axis_relative_direction(void *data, struct wl_pointer *pointer, uint32_t axis,
                                            uint32_t direction) {
    (void) data; (void) pointer; (void) axis; (void) direction;
}

static const struct wl_pointer_listener pointer_listener = {
    .enter = pointer_enter,
    .leave = pointer_leave,
    .motion = pointer_motion,
    .button = pointer_button,
    .axis = pointer_axis,
    .frame = pointer_frame,
    .axis_source = pointer_axis_source,
    .axis_stop = pointer_axis_stop,
    .axis_discrete = pointer_axis_discrete,
    .axis_value120 = pointer_axis_value120,
    .axis_relative_direction = pointer_axis_relative_direction,
};

/* ======================================================================== */
/* Seat                                                                     */
/* ======================================================================== */

static void seat_capabilities(void *data, struct wl_seat *seat, uint32_t capabilities) {
    struct trinix_wl_client *client = data;

    const bool has_keyboard = (capabilities & WL_SEAT_CAPABILITY_KEYBOARD) != 0;
    const bool has_pointer = (capabilities & WL_SEAT_CAPABILITY_POINTER) != 0;

    /* Both directions: a seat can lose a device as well as gain one, and a
     * proxy kept after the capability went is a proxy the compositor has
     * stopped sending to. */
    if (has_keyboard && client->keyboard == NULL) {
        client->keyboard = wl_seat_get_keyboard(seat);
        wl_keyboard_add_listener(client->keyboard, &keyboard_listener, client);
    } else if (!has_keyboard && client->keyboard != NULL) {
        wl_keyboard_release(client->keyboard);
        client->keyboard = NULL;
    }

    if (has_pointer && client->pointer == NULL) {
        client->pointer = wl_seat_get_pointer(seat);
        wl_pointer_add_listener(client->pointer, &pointer_listener, client);
    } else if (!has_pointer && client->pointer != NULL) {
        wl_pointer_release(client->pointer);
        client->pointer = NULL;
    }
}

static void seat_name(void *data, struct wl_seat *seat, const char *name) {
    (void) data; (void) seat; (void) name;
}

static const struct wl_seat_listener seat_listener = {
    .capabilities = seat_capabilities,
    .name = seat_name,
};

/* ======================================================================== */
/* xdg-shell                                                                */
/* ======================================================================== */

static void wm_base_ping(void *data, struct xdg_wm_base *wm_base, uint32_t serial) {
    (void) data;
    xdg_wm_base_pong(wm_base, serial);
}

static const struct xdg_wm_base_listener wm_base_listener = { .ping = wm_base_ping };

static void surface_enter(void *data, struct wl_surface *surface, struct wl_output *proxy) {
    struct trinix_wl_window *window = data;
    (void) surface;

    /*
     * The scale a window is drawn at is the scale of the output it is on, and
     * this is the only event that says which output that is. A window spanning
     * two takes the first one's, which is wrong on exactly one frame of a drag
     * between displays of different densities.
     */
    for (int i = 0; i < MAX_OUTPUTS; i++) {
        struct trinix_wl_output *output = &window->client->outputs[i];
        if (output->used && output->proxy == proxy) {
            window->scale = output->scale > 0 ? output->scale : 1;
            wl_surface_set_buffer_scale(window->surface, window->scale);
            return;
        }
    }
}

static void surface_leave(void *data, struct wl_surface *surface, struct wl_output *proxy) {
    (void) data; (void) surface; (void) proxy;
}

static void surface_preferred_buffer_scale(void *data, struct wl_surface *surface, int32_t factor) {
    (void) data; (void) surface; (void) factor;
}

static void surface_preferred_buffer_transform(void *data, struct wl_surface *surface,
                                               uint32_t transform) {
    (void) data; (void) surface; (void) transform;
}

static const struct wl_surface_listener surface_listener = {
    .enter = surface_enter,
    .leave = surface_leave,
    .preferred_buffer_scale = surface_preferred_buffer_scale,
    .preferred_buffer_transform = surface_preferred_buffer_transform,
};

static void toplevel_configure(void *data, struct xdg_toplevel *toplevel,
                               int32_t width, int32_t height, struct wl_array *states) {
    struct trinix_wl_window *window = data;
    (void) toplevel;

    /* Zero means the compositor has no opinion, which is a request to keep
     * whatever size the window last chose rather than to become zero wide. */
    if (width > 0 && height > 0) {
        window->width = width;
        window->height = height;
    } else if (!window->configured) {
        window->width = window->fallback_width;
        window->height = window->fallback_height;
    }

    uint32_t mode = TRINIX_WL_WINDOW_WINDOWED;
    uint32_t *state;
    wl_array_for_each(state, states) {
        switch (*state) {
            case XDG_TOPLEVEL_STATE_FULLSCREEN: mode = TRINIX_WL_WINDOW_FULLSCREEN; break;
            case XDG_TOPLEVEL_STATE_MAXIMIZED:
                if (mode != TRINIX_WL_WINDOW_FULLSCREEN) { mode = TRINIX_WL_WINDOW_MAXIMISED; }
                break;
            default: break;
        }
    }
    window->pending_mode = mode;
}

static void toplevel_close(void *data, struct xdg_toplevel *toplevel) {
    struct trinix_wl_window *window = data;
    (void) toplevel;

    if (window->client->callbacks.window_close_requested != NULL) {
        window->client->callbacks.window_close_requested(window);
    }
}

static void toplevel_configure_bounds(void *data, struct xdg_toplevel *toplevel,
                                      int32_t width, int32_t height) {
    (void) data; (void) toplevel; (void) width; (void) height;
}

static void toplevel_wm_capabilities(void *data, struct xdg_toplevel *toplevel,
                                     struct wl_array *capabilities) {
    (void) data; (void) toplevel; (void) capabilities;
}

static const struct xdg_toplevel_listener toplevel_listener = {
    .configure = toplevel_configure,
    .close = toplevel_close,
    .configure_bounds = toplevel_configure_bounds,
    .wm_capabilities = toplevel_wm_capabilities,
};

/*
 * xdg_surface.configure is the end of a configure sequence: the toplevel's
 * own configure arrived first and only described part of the new state. The
 * ack goes here, and so does telling anyone, because this is the first moment
 * the state is whole.
 */
static void xdg_surface_configure(void *data, struct xdg_surface *xdg_surface, uint32_t serial) {
    struct trinix_wl_window *window = data;

    xdg_surface_ack_configure(xdg_surface, serial);

    window->mode = window->pending_mode;
    window->configured = true;

    if (window->client->callbacks.window_configured != NULL) {
        const int32_t scale = window->scale > 0 ? window->scale : 1;
        window->client->callbacks.window_configured(
            window, window->width, window->height,
            window->width * scale, window->height * scale, scale, window->mode);
    }

    /*
     * ⚠ No wl_surface_commit here, and its absence is the whole reason this
     * comment exists. Before there is a swapchain there is no buffer to
     * commit; after there is one, Mesa's WSI owns this surface's commits, and
     * a commit from here attaches whatever was last attached — which is a
     * frame of the wrong contents, or a protocol error about a buffer size
     * that no longer matches.
     */
}

static const struct xdg_surface_listener xdg_surface_listener = {
    .configure = xdg_surface_configure,
};

/* ======================================================================== */
/* trinix-shell-v1 and trinix-menu-v1                                       */
/* ======================================================================== */

static void shell_capabilities(void *data, struct trinix_shell_v1 *shell, uint32_t capabilities) {
    struct trinix_wl_client *client = data;
    (void) shell;
    client->shell_capabilities = capabilities;
}

static const struct trinix_shell_v1_listener shell_listener = {
    .capabilities = shell_capabilities,
};

static void shell_surface_control_activated(void *data, struct trinix_shell_surface_v1 *proxy,
                                            uint32_t control) {
    struct trinix_wl_window *window = data;
    (void) proxy;

    if (window->client->callbacks.control_activated != NULL) {
        window->client->callbacks.control_activated(window, control);
    }
}

static void shell_surface_control_hover(void *data, struct trinix_shell_surface_v1 *proxy,
                                        uint32_t control, uint32_t state) {
    struct trinix_wl_window *window = data;
    (void) proxy;

    if (window->client->callbacks.control_hover != NULL) {
        window->client->callbacks.control_hover(window, control, state);
    }
}

static void shell_surface_shadow_applied(void *data, struct trinix_shell_surface_v1 *proxy,
                                         int32_t left, int32_t top, int32_t right, int32_t bottom) {
    struct trinix_wl_window *window = data;
    (void) proxy;

    if (window->client->callbacks.shadow_applied != NULL) {
        window->client->callbacks.shadow_applied(window, left, top, right, bottom);
    }
}

static const struct trinix_shell_surface_v1_listener shell_surface_listener = {
    .control_activated = shell_surface_control_activated,
    .control_hover = shell_surface_control_hover,
    .shadow_applied = shell_surface_shadow_applied,
};

static void menu_activated(void *data, struct trinix_menu_v1 *proxy, uint32_t id) {
    struct trinix_wl_menu *menu = data;
    (void) proxy;

    if (menu->client->callbacks.menu_activated != NULL) {
        menu->client->callbacks.menu_activated(menu, id);
    }
}

static void menu_about_to_show(void *data, struct trinix_menu_v1 *proxy, uint32_t id) {
    struct trinix_wl_menu *menu = data;
    (void) proxy;

    if (menu->client->callbacks.menu_about_to_show != NULL) {
        menu->client->callbacks.menu_about_to_show(menu, id);
    }
}

static void menu_closed(void *data, struct trinix_menu_v1 *proxy) {
    struct trinix_wl_menu *menu = data;
    (void) proxy;

    if (menu->client->callbacks.menu_closed != NULL) {
        menu->client->callbacks.menu_closed(menu);
    }
}

static const struct trinix_menu_v1_listener menu_listener = {
    .activated = menu_activated,
    .about_to_show = menu_about_to_show,
    .closed = menu_closed,
};

/* ======================================================================== */
/* Registry                                                                 */
/* ======================================================================== */

/* The version actually bound, which is the lower of what the compositor
 * offers and what this code was written against. Binding a higher version than
 * the listener struct knows about is how a client crashes on an event it never
 * heard of. */
static uint32_t bind_version(uint32_t offered, uint32_t supported) {
    return offered < supported ? offered : supported;
}

static void registry_global(void *data, struct wl_registry *registry, uint32_t name,
                            const char *interface, uint32_t version) {
    struct trinix_wl_client *client = data;

    if (strcmp(interface, wl_compositor_interface.name) == 0) {
        /* Version 4 for damage_buffer, which docs/vixen-platform-contract.md
         * names as the minimum; 6 adds preferred_buffer_scale. */
        client->compositor = wl_registry_bind(registry, name, &wl_compositor_interface,
                                              bind_version(version, 6));
    } else if (strcmp(interface, xdg_wm_base_interface.name) == 0) {
        client->wm_base = wl_registry_bind(registry, name, &xdg_wm_base_interface,
                                           bind_version(version, 3));
        xdg_wm_base_add_listener(client->wm_base, &wm_base_listener, client);
    } else if (strcmp(interface, wl_seat_interface.name) == 0) {
        client->seat = wl_registry_bind(registry, name, &wl_seat_interface,
                                        bind_version(version, 7));
        wl_seat_add_listener(client->seat, &seat_listener, client);
    } else if (strcmp(interface, wl_output_interface.name) == 0) {
        for (int i = 0; i < MAX_OUTPUTS; i++) {
            struct trinix_wl_output *output = &client->outputs[i];
            if (output->used) {
                continue;
            }
            output->used = true;
            output->client = client;
            output->name = name;
            output->scale = 1;
            output->proxy = wl_registry_bind(registry, name, &wl_output_interface,
                                             bind_version(version, 4));
            wl_output_add_listener(output->proxy, &output_listener, output);
            break;
        }
    } else if (strcmp(interface, wl_data_device_manager_interface.name) == 0) {
        client->data_device_manager = wl_registry_bind(registry, name,
                                                       &wl_data_device_manager_interface,
                                                       bind_version(version, 3));
        client->globals |= TRINIX_WL_GLOBAL_DATA_DEVICE;
    } else if (strcmp(interface, trinix_shell_v1_interface.name) == 0) {
        client->shell = wl_registry_bind(registry, name, &trinix_shell_v1_interface, 1);
        trinix_shell_v1_add_listener(client->shell, &shell_listener, client);
        client->globals |= TRINIX_WL_GLOBAL_SHELL;
    } else if (strcmp(interface, trinix_menu_manager_v1_interface.name) == 0) {
        client->menu_manager = wl_registry_bind(registry, name,
                                                &trinix_menu_manager_v1_interface, 1);
        client->globals |= TRINIX_WL_GLOBAL_MENU;
    }
}

static void registry_global_remove(void *data, struct wl_registry *registry, uint32_t name) {
    struct trinix_wl_client *client = data;
    (void) registry;

    for (int i = 0; i < MAX_OUTPUTS; i++) {
        struct trinix_wl_output *output = &client->outputs[i];
        if (!output->used || output->name != name) {
            continue;
        }

        if (client->callbacks.output_removed != NULL) {
            client->callbacks.output_removed(output);
        }
        wl_output_destroy(output->proxy);
        memset(output, 0, sizeof *output);
        return;
    }
}

static const struct wl_registry_listener registry_listener = {
    .global = registry_global,
    .global_remove = registry_global_remove,
};

/* ======================================================================== */
/* Lifecycle                                                                */
/* ======================================================================== */

struct trinix_wl_client *trinix_wl_client_connect(const struct trinix_wl_callbacks *callbacks,
                                                  void *user_data) {
    if (callbacks == NULL) {
        return NULL;
    }

    struct trinix_wl_client *client = calloc(1, sizeof *client);
    if (client == NULL) {
        return NULL;
    }

    client->callbacks = *callbacks;
    client->user_data = user_data;

    client->display = wl_display_connect(NULL);
    if (client->display == NULL) {
        free(client);
        return NULL;
    }

    client->xkb = xkb_context_new(XKB_CONTEXT_NO_FLAGS);

    client->registry = wl_display_get_registry(client->display);
    wl_registry_add_listener(client->registry, &registry_listener, client);

    /*
     * Two round trips, and the second is not optional. The first delivers the
     * globals; the second delivers the events those globals send on binding —
     * the seat's capabilities, each output's mode, the shell's capabilities.
     * With one, a window can be created before it is known whether there is a
     * keyboard.
     */
    wl_display_roundtrip(client->display);
    wl_display_roundtrip(client->display);

    if (client->compositor == NULL || client->wm_base == NULL) {
        trinix_wl_client_destroy(client);
        return NULL;
    }

    client->connected = true;
    return client;
}

void trinix_wl_client_destroy(struct trinix_wl_client *client) {
    if (client == NULL) {
        return;
    }

    for (int i = 0; i < MAX_OUTPUTS; i++) {
        if (client->outputs[i].used) {
            wl_output_destroy(client->outputs[i].proxy);
        }
    }

    if (client->keyboard != NULL) { wl_keyboard_release(client->keyboard); }
    if (client->pointer != NULL) { wl_pointer_release(client->pointer); }
    if (client->seat != NULL) { wl_seat_destroy(client->seat); }
    /* The bar before the manager that handed it out: the protocol says menus
     * survive their manager, not the other way round. */
    if (client->menu != NULL) { trinix_wl_menu_destroy(client->menu); }
    if (client->menu_manager != NULL) { trinix_menu_manager_v1_destroy(client->menu_manager); }
    if (client->shell != NULL) { trinix_shell_v1_destroy(client->shell); }
    if (client->data_device_manager != NULL) {
        wl_data_device_manager_destroy(client->data_device_manager);
    }
    if (client->wm_base != NULL) { xdg_wm_base_destroy(client->wm_base); }
    if (client->compositor != NULL) { wl_compositor_destroy(client->compositor); }
    if (client->registry != NULL) { wl_registry_destroy(client->registry); }

    xkb_state_unref(client->xkb_state);
    xkb_keymap_unref(client->keymap);
    xkb_context_unref(client->xkb);

    if (client->display != NULL) { wl_display_disconnect(client->display); }
    free(client);
}

uint32_t trinix_wl_client_globals(struct trinix_wl_client *client) {
    return client != NULL ? client->globals : 0;
}

void *trinix_wl_client_display(struct trinix_wl_client *client) {
    return client != NULL ? client->display : NULL;
}

void *trinix_wl_window_surface(struct trinix_wl_window *window) {
    return window != NULL ? window->surface : NULL;
}

bool trinix_wl_client_pump(struct trinix_wl_client *client) {
    if (client == NULL || !client->connected) {
        return false;
    }

    /*
     * The prepare/read dance rather than wl_display_dispatch, which blocks.
     * prepare_read fails while this thread still has events queued, and the
     * only correct response is to dispatch them and ask again — a plain `if`
     * here is a client that stops responding under load.
     */
    while (wl_display_prepare_read(client->display) != 0) {
        if (wl_display_dispatch_pending(client->display) < 0) {
            client->connected = false;
            return false;
        }
    }

    if (wl_display_flush(client->display) < 0 && errno != EAGAIN) {
        wl_display_cancel_read(client->display);
        client->connected = false;
        return false;
    }

    struct pollfd fd = {
        .fd = wl_display_get_fd(client->display),
        .events = POLLIN,
    };

    /* Zero timeout: this is called once a frame from a loop that has other
     * things to do, and a platform that blocked here would be a frame rate
     * decided by how often the compositor says something. */
    if (poll(&fd, 1, 0) > 0 && (fd.revents & POLLIN) != 0) {
        if (wl_display_read_events(client->display) < 0) {
            client->connected = false;
            return false;
        }
    } else {
        wl_display_cancel_read(client->display);
    }

    if (wl_display_dispatch_pending(client->display) < 0) {
        client->connected = false;
        return false;
    }

    return true;
}

bool trinix_wl_client_roundtrip(struct trinix_wl_client *client) {
    if (client == NULL || !client->connected) {
        return false;
    }

    if (wl_display_roundtrip(client->display) < 0) {
        client->connected = false;
        return false;
    }

    return true;
}

/* ======================================================================== */
/* Windows                                                                  */
/* ======================================================================== */

struct trinix_wl_window *trinix_wl_window_create(struct trinix_wl_client *client,
                                                 const char *title, const char *app_id,
                                                 int32_t width, int32_t height,
                                                 bool resizable) {
    if (client == NULL || !client->connected) {
        return NULL;
    }

    struct trinix_wl_window *window = calloc(1, sizeof *window);
    if (window == NULL) {
        return NULL;
    }

    window->client = client;
    window->fallback_width = width > 0 ? width : 1;
    window->fallback_height = height > 0 ? height : 1;
    window->width = window->fallback_width;
    window->height = window->fallback_height;
    window->scale = 1;

    window->surface = wl_compositor_create_surface(client->compositor);
    wl_surface_add_listener(window->surface, &surface_listener, window);

    /* The surface's user data is how an input event, which names a surface,
     * finds the window it belongs to. */
    wl_surface_set_user_data(window->surface, window);

    window->xdg_surface = xdg_wm_base_get_xdg_surface(client->wm_base, window->surface);
    xdg_surface_add_listener(window->xdg_surface, &xdg_surface_listener, window);

    window->toplevel = xdg_surface_get_toplevel(window->xdg_surface);
    xdg_toplevel_add_listener(window->toplevel, &toplevel_listener, window);

    if (title != NULL) { xdg_toplevel_set_title(window->toplevel, title); }
    if (app_id != NULL) { xdg_toplevel_set_app_id(window->toplevel, app_id); }

    if (!resizable) {
        xdg_toplevel_set_min_size(window->toplevel, width, height);
        xdg_toplevel_set_max_size(window->toplevel, width, height);
    }

    if (client->shell != NULL) {
        window->shell_surface = trinix_shell_v1_get_shell_surface(client->shell, window->toplevel);
        trinix_shell_surface_v1_add_listener(window->shell_surface, &shell_surface_listener, window);
    }

    /* No menu is created here. A menu belongs to the application, not to each
     * of its windows, so creating one per window would export the same tree N
     * times and leave the application applying every state change N times.
     * trinix_wl_client_menu_create is where a menu bar comes from; a window
     * that genuinely needs a different one asks for it by name. */

    /* A commit with no buffer, which the protocol requires before the first
     * configure: it is what says "the role is set, tell me how big to be".
     *
     * ⚠ And no round trip here — see the header. The caller does it, once it
     * has somewhere to put the configure that comes back.
     */
    wl_surface_commit(window->surface);

    return window;
}

void trinix_wl_window_destroy(struct trinix_wl_window *window) {
    if (window == NULL) {
        return;
    }

    struct trinix_wl_client *client = window->client;
    if (client->keyboard_focus == window) { client->keyboard_focus = NULL; }
    if (client->pointer_focus == window) { client->pointer_focus = NULL; }

    if (window->drag_region != NULL) { wl_region_destroy(window->drag_region); }
    /* The override goes with the window it overrode. The client's own bar is
     * not this window's to take down, and does not move. */
    if (window->menu != NULL) { trinix_wl_menu_destroy(window->menu); }
    if (window->shell_surface != NULL) { trinix_shell_surface_v1_destroy(window->shell_surface); }
    if (window->toplevel != NULL) { xdg_toplevel_destroy(window->toplevel); }
    if (window->xdg_surface != NULL) { xdg_surface_destroy(window->xdg_surface); }
    if (window->surface != NULL) { wl_surface_destroy(window->surface); }

    free(window);
}

void trinix_wl_window_set_title(struct trinix_wl_window *window, const char *title) {
    if (window != NULL && title != NULL) {
        xdg_toplevel_set_title(window->toplevel, title);
    }
}

void trinix_wl_window_set_mode(struct trinix_wl_window *window, uint32_t mode) {
    if (window == NULL) {
        return;
    }

    /*
     * Every one of these is a *request*. The compositor decides, and says so
     * with a configure — which is why nothing here updates window->mode.
     */
    switch (mode) {
        case TRINIX_WL_WINDOW_MINIMISED:
            xdg_toplevel_set_minimized(window->toplevel);
            break;
        case TRINIX_WL_WINDOW_MAXIMISED:
            xdg_toplevel_set_maximized(window->toplevel);
            break;
        case TRINIX_WL_WINDOW_FULLSCREEN:
            xdg_toplevel_set_fullscreen(window->toplevel, NULL);
            break;
        default:
            xdg_toplevel_unset_fullscreen(window->toplevel);
            xdg_toplevel_unset_maximized(window->toplevel);
            break;
    }
}

void trinix_wl_window_set_min_size(struct trinix_wl_window *window, int32_t width, int32_t height) {
    if (window != NULL) { xdg_toplevel_set_min_size(window->toplevel, width, height); }
}

void trinix_wl_window_set_max_size(struct trinix_wl_window *window, int32_t width, int32_t height) {
    if (window != NULL) { xdg_toplevel_set_max_size(window->toplevel, width, height); }
}

void trinix_wl_window_begin_move(struct trinix_wl_window *window) {
    if (window != NULL && window->client->seat != NULL) {
        xdg_toplevel_move(window->toplevel, window->client->seat, window->client->last_input_serial);
    }
}

void trinix_wl_window_begin_resize(struct trinix_wl_window *window, uint32_t edges) {
    if (window != NULL && window->client->seat != NULL) {
        xdg_toplevel_resize(window->toplevel, window->client->seat,
                            window->client->last_input_serial, edges);
    }
}

/* ======================================================================== */
/* trinix-shell-v1                                                          */
/* ======================================================================== */

void trinix_wl_window_set_shadow(struct trinix_wl_window *window, uint32_t style) {
    if (window != NULL && window->shell_surface != NULL) {
        trinix_shell_surface_v1_set_shadow(window->shell_surface, style);
    }
}

void trinix_wl_window_set_corner_radius(struct trinix_wl_window *window, int32_t radius) {
    if (window != NULL && window->shell_surface != NULL) {
        trinix_shell_surface_v1_set_corner_radius(window->shell_surface, radius);
    }
}

void trinix_wl_window_set_drag_region(struct trinix_wl_window *window,
                                      int32_t x, int32_t y, int32_t width, int32_t height) {
    if (window == NULL || window->shell_surface == NULL) {
        return;
    }

    if (window->drag_region != NULL) {
        wl_region_destroy(window->drag_region);
        window->drag_region = NULL;
    }

    /* An empty rectangle is the protocol's null region, which means the whole
     * window — right for a panel and wrong for almost anything else, so it is
     * spelled as a deliberate clear rather than reached by passing zeroes. */
    if (width > 0 && height > 0) {
        window->drag_region = wl_compositor_create_region(window->client->compositor);
        wl_region_add(window->drag_region, x, y, width, height);
    }

    trinix_shell_surface_v1_set_drag_region(window->shell_surface, window->drag_region);
}

void trinix_wl_window_set_resize_inset(struct trinix_wl_window *window, int32_t inset) {
    if (window != NULL && window->shell_surface != NULL) {
        trinix_shell_surface_v1_set_resize_inset(window->shell_surface, inset);
    }
}

void trinix_wl_window_set_control(struct trinix_wl_window *window, uint32_t control,
                                  int32_t x, int32_t y, int32_t width, int32_t height) {
    if (window != NULL && window->shell_surface != NULL) {
        trinix_shell_surface_v1_set_control(window->shell_surface, control, x, y, width, height);
    }
}

void trinix_wl_window_unset_control(struct trinix_wl_window *window, uint32_t control) {
    if (window != NULL && window->shell_surface != NULL) {
        trinix_shell_surface_v1_unset_control(window->shell_surface, control);
    }
}

/* ======================================================================== */
/* trinix-menu-v1                                                           */
/* ======================================================================== */

/* The half both scopes share: wrap a freshly created trinix_menu_v1 in the
 * record the rest of this library and everything above it hold. */
static struct trinix_wl_menu *menu_wrap(struct trinix_wl_client *client,
                                        struct trinix_wl_window *window,
                                        struct trinix_menu_v1 *proxy) {
    struct trinix_wl_menu *menu = calloc(1, sizeof *menu);
    if (menu == NULL) {
        trinix_menu_v1_destroy(proxy);
        return NULL;
    }

    menu->client = client;
    menu->window = window;
    menu->proxy = proxy;
    trinix_menu_v1_add_listener(proxy, &menu_listener, menu);
    return menu;
}

struct trinix_wl_menu *trinix_wl_client_menu_create(struct trinix_wl_client *client) {
    if (client == NULL || client->menu_manager == NULL) {
        return NULL;
    }

    /* Idempotent, because asking twice is a protocol error and the caller
     * above has no cheaper way to know it has already asked. */
    if (client->menu != NULL) {
        return client->menu;
    }

    client->menu = menu_wrap(client, NULL,
                             trinix_menu_manager_v1_get_menu_bar(client->menu_manager));
    return client->menu;
}

struct trinix_wl_menu *trinix_wl_window_menu_create(struct trinix_wl_window *window) {
    if (window == NULL || window->client->menu_manager == NULL) {
        return NULL;
    }
    if (window->menu != NULL) {
        return window->menu;
    }

    window->menu = menu_wrap(
        window->client, window,
        trinix_menu_manager_v1_get_toplevel_menu_bar(window->client->menu_manager,
                                                     window->toplevel));
    return window->menu;
}

void trinix_wl_menu_destroy(struct trinix_wl_menu *menu) {
    if (menu == NULL) {
        return;
    }

    if (menu->window != NULL) {
        menu->window->menu = NULL;
    } else if (menu->client != NULL) {
        menu->client->menu = NULL;
    }

    trinix_menu_v1_destroy(menu->proxy);
    free(menu);
}

void trinix_wl_menu_insert(struct trinix_wl_menu *menu, uint32_t id, uint32_t parent,
                           int32_t index, uint32_t kind, const char *label) {
    if (menu != NULL) {
        trinix_menu_v1_insert(menu->proxy, id, parent, index, kind, label != NULL ? label : "");
    }
}

void trinix_wl_menu_update(struct trinix_wl_menu *menu, uint32_t id,
                           const char *label, uint32_t state) {
    if (menu != NULL) {
        trinix_menu_v1_update(menu->proxy, id, label != NULL ? label : "", state);
    }
}

void trinix_wl_menu_accelerator(struct trinix_wl_menu *menu, uint32_t id,
                                uint32_t keysym, uint32_t modifiers) {
    if (menu != NULL) {
        trinix_menu_v1_set_accelerator(menu->proxy, id, keysym, modifiers);
    }
}

void trinix_wl_menu_remove(struct trinix_wl_menu *menu, uint32_t id) {
    if (menu != NULL) {
        trinix_menu_v1_remove(menu->proxy, id);
    }
}

void trinix_wl_menu_commit(struct trinix_wl_menu *menu) {
    if (menu != NULL) {
        trinix_menu_v1_commit(menu->proxy);
    }
}
