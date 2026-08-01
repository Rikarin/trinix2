/*
 * trinix-wlr — see trinix-wlr.h for what this is and why it exists.
 *
 * The shape of this file follows wlroots' tinywl, which is the reference
 * minimal compositor, with one deliberate difference: every point where tinywl
 * makes a decision, this calls out to C# instead. Reading them side by side is
 * the fastest way to see where the policy boundary was drawn.
 */
#define _POSIX_C_SOURCE 200809L

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include <wayland-server-core.h>
#include <wlr/backend.h>
#include <wlr/render/allocator.h>
#include <wlr/render/wlr_renderer.h>
#include <wlr/types/wlr_compositor.h>
#include <wlr/types/wlr_cursor.h>
#include <wlr/types/wlr_data_device.h>
#include <wlr/types/wlr_input_device.h>
#include <wlr/types/wlr_keyboard.h>
#include <wlr/types/wlr_output.h>
#include <wlr/types/wlr_output_layout.h>
#include <wlr/types/wlr_pointer.h>
#include <wlr/types/wlr_scene.h>
#include <wlr/types/wlr_seat.h>
#include <wlr/types/wlr_subcompositor.h>
#include <wlr/types/wlr_xdg_shell.h>
#include <wlr/util/log.h>
#include <xkbcommon/xkbcommon.h>

#include "trinix-wlr.h"

/*
 * The desktop background, and the cursor.
 *
 * A compositor with no wallpaper still has to put *something* on the screen,
 * or the first frame is whatever was in the framebuffer. The cursor is drawn
 * from rectangles rather than loaded from an Xcursor theme: a theme is several
 * megabytes of PNG-in-a-custom-container for something the compositor is about
 * to replace with its own rendering anyway, and an image that fails to load
 * produces an invisible pointer, which is a bad way to find out.
 */
static const float BACKGROUND_COLOR[4] = { 0.12f, 0.13f, 0.16f, 1.0f };
static const float CURSOR_COLOR[4] = { 0.95f, 0.95f, 0.97f, 1.0f };
static const float CURSOR_EDGE_COLOR[4] = { 0.08f, 0.08f, 0.10f, 1.0f };
#define CURSOR_HEIGHT 18

/* Large enough to cover any output arrangement a VM will produce, and cheap:
 * the scene graph only ever rasterises the damaged part of it. */
#define BACKGROUND_SPAN 16384

struct tx_server {
    struct wl_display *display;
    struct wlr_backend *backend;
    struct wlr_renderer *renderer;
    struct wlr_allocator *allocator;
    struct wlr_scene *scene;
    struct wlr_scene_output_layout *scene_layout;
    struct wlr_output_layout *output_layout;
    struct wlr_xdg_shell *xdg_shell;
    struct wlr_cursor *cursor;
    struct wlr_seat *seat;

    struct wlr_scene_tree *cursor_tree;

    struct wl_listener new_output;
    struct wl_listener new_input;
    struct wl_listener new_xdg_toplevel;
    struct wl_listener new_xdg_popup;
    struct wl_listener cursor_motion;
    struct wl_listener cursor_motion_absolute;
    struct wl_listener cursor_button;
    struct wl_listener cursor_axis;
    struct wl_listener cursor_frame;
    struct wl_listener request_cursor;
    struct wl_listener request_set_selection;

    struct wl_list outputs;
    struct wl_list keyboards;

    struct trinix_wlr_callbacks cb;
    /* Owned, not borrowed: the caller may be a garbage-collected runtime that
     * marshalled the name into a buffer valid only for the duration of the
     * call, and trinix_wlr_socket() hands this back long afterwards. */
    char socket[64];
};

struct tx_output {
    struct wl_list link;
    struct tx_server *server;
    struct wlr_output *wlr_output;
    struct wl_listener frame;
    struct wl_listener request_state;
    struct wl_listener destroy;
};

struct tx_toplevel {
    struct tx_server *server;
    struct wlr_xdg_toplevel *xdg_toplevel;
    struct wlr_scene_tree *scene_tree;
    struct wl_listener map;
    struct wl_listener unmap;
    struct wl_listener commit;
    struct wl_listener destroy;
    struct wl_listener request_move;
    struct wl_listener request_resize;
    struct wl_listener request_maximize;
    struct wl_listener request_fullscreen;
};

struct tx_popup {
    struct wlr_xdg_popup *xdg_popup;
    struct wl_listener commit;
    struct wl_listener destroy;
};

struct tx_keyboard {
    struct wl_list link;
    struct tx_server *server;
    struct wlr_keyboard *wlr_keyboard;
    struct wl_listener modifiers;
    struct wl_listener key;
    struct wl_listener destroy;
};

/* --- cursor ------------------------------------------------------------- */

/*
 * A left-pointing arrow, built as a staircase of one-pixel-tall rectangles
 * with a darker rectangle behind each for the outline. Seventeen scene nodes
 * for a pointer is not elegant, and it is a great deal less machinery than an
 * Xcursor parser plus a theme to feed it.
 */
static void cursor_build(struct tx_server *server) {
    server->cursor_tree = wlr_scene_tree_create(&server->scene->tree);

    for (int y = 0; y < CURSOR_HEIGHT; y++) {
        /* Narrows towards the tail, so the shape reads as an arrow rather
         * than a triangle. */
        int width = (y < CURSOR_HEIGHT * 2 / 3) ? y + 2 : CURSOR_HEIGHT - y + 3;
        if (width < 2) {
            width = 2;
        }

        struct wlr_scene_rect *edge =
            wlr_scene_rect_create(server->cursor_tree, width + 2, 1, CURSOR_EDGE_COLOR);
        wlr_scene_node_set_position(&edge->node, 0, y);

        struct wlr_scene_rect *fill =
            wlr_scene_rect_create(server->cursor_tree, width, 1, CURSOR_COLOR);
        wlr_scene_node_set_position(&fill->node, 1, y);
    }
}

static void cursor_follow(struct tx_server *server) {
    wlr_scene_node_set_position(&server->cursor_tree->node,
                                (int)server->cursor->x, (int)server->cursor->y);
    /* Nothing is allowed above the pointer. Raising it here rather than on
     * every window change means there is one place this can be got wrong. */
    wlr_scene_node_raise_to_top(&server->cursor_tree->node);
}

/* --- keyboard ----------------------------------------------------------- */

static void keyboard_handle_modifiers(struct wl_listener *listener, void *data) {
    struct tx_keyboard *keyboard = wl_container_of(listener, keyboard, modifiers);
    (void)data;

    wlr_seat_set_keyboard(keyboard->server->seat, keyboard->wlr_keyboard);
    wlr_seat_keyboard_notify_modifiers(keyboard->server->seat,
                                       &keyboard->wlr_keyboard->modifiers);
}

static void keyboard_handle_key(struct wl_listener *listener, void *data) {
    struct tx_keyboard *keyboard = wl_container_of(listener, keyboard, key);
    struct tx_server *server = keyboard->server;
    struct wlr_keyboard_key_event *event = data;

    /* libinput numbers keys from 0; X11 and therefore xkbcommon number them
     * from 8, and every compositor carries this line. */
    uint32_t keycode = event->keycode + 8;
    const xkb_keysym_t *syms;
    int nsyms = xkb_state_key_get_syms(keyboard->wlr_keyboard->xkb_state, keycode, &syms);
    uint32_t modifiers = wlr_keyboard_get_modifiers(keyboard->wlr_keyboard);
    bool pressed = event->state == WL_KEYBOARD_KEY_STATE_PRESSED;

    bool handled = false;
    if (server->cb.key != NULL) {
        for (int i = 0; i < nsyms; i++) {
            if (server->cb.key(syms[i], modifiers, pressed)) {
                handled = true;
            }
        }
    }

    if (!handled) {
        wlr_seat_set_keyboard(server->seat, keyboard->wlr_keyboard);
        wlr_seat_keyboard_notify_key(server->seat, event->time_msec,
                                     event->keycode, event->state);
    }
}

static void keyboard_handle_destroy(struct wl_listener *listener, void *data) {
    struct tx_keyboard *keyboard = wl_container_of(listener, keyboard, destroy);
    (void)data;

    wl_list_remove(&keyboard->modifiers.link);
    wl_list_remove(&keyboard->key.link);
    wl_list_remove(&keyboard->destroy.link);
    wl_list_remove(&keyboard->link);
    free(keyboard);
}

static void server_new_keyboard(struct tx_server *server, struct wlr_input_device *device) {
    struct wlr_keyboard *wlr_keyboard = wlr_keyboard_from_input_device(device);

    struct tx_keyboard *keyboard = calloc(1, sizeof(*keyboard));
    if (keyboard == NULL) {
        return;
    }
    keyboard->server = server;
    keyboard->wlr_keyboard = wlr_keyboard;

    /*
     * Compiled from xkeyboard-config's database with the defaults the
     * libxkbcommon recipe baked in. XKB_DEFAULT_LAYOUT and friends override
     * it, which is how a layout will eventually be configurable without this
     * function knowing anything about settings.
     */
    struct xkb_context *context = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
    struct xkb_keymap *keymap =
        xkb_keymap_new_from_names(context, NULL, XKB_KEYMAP_COMPILE_NO_FLAGS);
    if (keymap != NULL) {
        wlr_keyboard_set_keymap(wlr_keyboard, keymap);
        xkb_keymap_unref(keymap);
    } else {
        wlr_log(WLR_ERROR, "no keymap could be compiled — is xkeyboard-config installed?");
    }
    xkb_context_unref(context);
    wlr_keyboard_set_repeat_info(wlr_keyboard, 25, 600);

    keyboard->modifiers.notify = keyboard_handle_modifiers;
    wl_signal_add(&wlr_keyboard->events.modifiers, &keyboard->modifiers);
    keyboard->key.notify = keyboard_handle_key;
    wl_signal_add(&wlr_keyboard->events.key, &keyboard->key);
    keyboard->destroy.notify = keyboard_handle_destroy;
    wl_signal_add(&device->events.destroy, &keyboard->destroy);

    wlr_seat_set_keyboard(server->seat, keyboard->wlr_keyboard);
    wl_list_insert(&server->keyboards, &keyboard->link);
}

static void server_new_input(struct wl_listener *listener, void *data) {
    struct tx_server *server = wl_container_of(listener, server, new_input);
    struct wlr_input_device *device = data;

    switch (device->type) {
    case WLR_INPUT_DEVICE_KEYBOARD:
        server_new_keyboard(server, device);
        break;
    case WLR_INPUT_DEVICE_POINTER:
        /* Pointer configuration is libinput's job and wlr_cursor aggregates
         * every device into one logical pointer. */
        wlr_cursor_attach_input_device(server->cursor, device);
        break;
    default:
        break;
    }

    /* The cursor is drawn whether or not a mouse is plugged in, so the seat
     * always claims the pointer capability. */
    uint32_t caps = WL_SEAT_CAPABILITY_POINTER;
    if (!wl_list_empty(&server->keyboards)) {
        caps |= WL_SEAT_CAPABILITY_KEYBOARD;
    }
    wlr_seat_set_capabilities(server->seat, caps);

    if (server->cb.input_added != NULL) {
        server->cb.input_added((int32_t)device->type, device->name);
    }
}

/* --- pointer ------------------------------------------------------------ */

static struct tx_toplevel *toplevel_at(struct tx_server *server, double lx, double ly,
                                       struct wlr_surface **surface, double *sx, double *sy) {
    struct wlr_scene_node *node = wlr_scene_node_at(&server->scene->tree.node, lx, ly, sx, sy);
    if (node == NULL || node->type != WLR_SCENE_NODE_BUFFER) {
        return NULL;
    }

    struct wlr_scene_buffer *scene_buffer = wlr_scene_buffer_from_node(node);
    struct wlr_scene_surface *scene_surface = wlr_scene_surface_try_from_buffer(scene_buffer);
    if (scene_surface == NULL) {
        return NULL;
    }

    if (surface != NULL) {
        *surface = scene_surface->surface;
    }

    /* Walk up to the tree whose data field was set when the toplevel was
     * created: subsurfaces and popups hang below it. */
    struct wlr_scene_tree *tree = node->parent;
    while (tree != NULL && tree->node.data == NULL) {
        tree = tree->node.parent;
    }
    return tree == NULL ? NULL : tree->node.data;
}

static void server_cursor_motion(struct wl_listener *listener, void *data) {
    struct tx_server *server = wl_container_of(listener, server, cursor_motion);
    struct wlr_pointer_motion_event *event = data;

    wlr_cursor_move(server->cursor, &event->pointer->base, event->delta_x, event->delta_y);
    cursor_follow(server);
    if (server->cb.pointer_motion != NULL) {
        server->cb.pointer_motion(server->cursor->x, server->cursor->y, event->time_msec);
    }
}

static void server_cursor_motion_absolute(struct wl_listener *listener, void *data) {
    struct tx_server *server = wl_container_of(listener, server, cursor_motion_absolute);
    struct wlr_pointer_motion_absolute_event *event = data;

    wlr_cursor_warp_absolute(server->cursor, &event->pointer->base, event->x, event->y);
    cursor_follow(server);
    if (server->cb.pointer_motion != NULL) {
        server->cb.pointer_motion(server->cursor->x, server->cursor->y, event->time_msec);
    }
}

static void server_cursor_button(struct wl_listener *listener, void *data) {
    struct tx_server *server = wl_container_of(listener, server, cursor_button);
    struct wlr_pointer_button_event *event = data;

    /* Forwarded before the callback runs, because the client with pointer
     * focus is entitled to the event regardless of what the compositor then
     * decides to do about focus. */
    wlr_seat_pointer_notify_button(server->seat, event->time_msec, event->button, event->state);

    if (server->cb.pointer_button != NULL) {
        server->cb.pointer_button(event->button,
                                  event->state == WL_POINTER_BUTTON_STATE_PRESSED,
                                  event->time_msec);
    }
}

static void server_cursor_axis(struct wl_listener *listener, void *data) {
    struct tx_server *server = wl_container_of(listener, server, cursor_axis);
    struct wlr_pointer_axis_event *event = data;

    wlr_seat_pointer_notify_axis(server->seat, event->time_msec, event->orientation,
                                 event->delta, event->delta_discrete, event->source,
                                 event->relative_direction);
}

static void server_cursor_frame(struct wl_listener *listener, void *data) {
    struct tx_server *server = wl_container_of(listener, server, cursor_frame);
    (void)data;

    wlr_seat_pointer_notify_frame(server->seat);
}

/*
 * A client offered its own cursor image. Declined, for now: honouring it means
 * the client's cursor surface and the compositor's drawn one are both visible,
 * and hiding ours raises a policy question — whose cursor wins during a drag? —
 * that belongs with the rest of the shell in Phase 5.
 */
static void seat_request_cursor(struct wl_listener *listener, void *data) {
    (void)listener;
    (void)data;
}

static void seat_request_set_selection(struct wl_listener *listener, void *data) {
    struct tx_server *server = wl_container_of(listener, server, request_set_selection);
    struct wlr_seat_request_set_selection_event *event = data;

    wlr_seat_set_selection(server->seat, event->source, event->serial);
}

/* --- outputs ------------------------------------------------------------ */

static void output_frame(struct wl_listener *listener, void *data) {
    struct tx_output *output = wl_container_of(listener, output, frame);
    (void)data;

    struct wlr_scene_output *scene_output =
        wlr_scene_get_scene_output(output->server->scene, output->wlr_output);
    if (scene_output == NULL) {
        return;
    }

    wlr_scene_output_commit(scene_output, NULL);

    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    wlr_scene_output_send_frame_done(scene_output, &now);
}

static void output_request_state(struct wl_listener *listener, void *data) {
    struct tx_output *output = wl_container_of(listener, output, request_state);
    const struct wlr_output_event_request_state *event = data;

    wlr_output_commit_state(output->wlr_output, event->state);
}

static void output_destroy(struct wl_listener *listener, void *data) {
    struct tx_output *output = wl_container_of(listener, output, destroy);
    (void)data;

    if (output->server->cb.output_removed != NULL) {
        output->server->cb.output_removed(output);
    }

    wl_list_remove(&output->frame.link);
    wl_list_remove(&output->request_state.link);
    wl_list_remove(&output->destroy.link);
    wl_list_remove(&output->link);
    free(output);
}

static void server_new_output(struct wl_listener *listener, void *data) {
    struct tx_server *server = wl_container_of(listener, server, new_output);
    struct wlr_output *wlr_output = data;

    wlr_output_init_render(wlr_output, server->allocator, server->renderer);

    struct wlr_output_state state;
    wlr_output_state_init(&state);
    wlr_output_state_set_enabled(&state, true);

    /* DRM outputs arrive with a mode list and no mode selected. The preferred
     * mode is what the display says it wants; choosing between them is a
     * settings question nobody has asked yet. */
    struct wlr_output_mode *mode = wlr_output_preferred_mode(wlr_output);
    if (mode != NULL) {
        wlr_output_state_set_mode(&state, mode);
    }
    bool committed = wlr_output_commit_state(wlr_output, &state);
    wlr_output_state_finish(&state);
    if (!committed) {
        wlr_log(WLR_ERROR, "output %s refused its preferred mode", wlr_output->name);
        return;
    }

    struct tx_output *output = calloc(1, sizeof(*output));
    if (output == NULL) {
        return;
    }
    output->wlr_output = wlr_output;
    output->server = server;

    output->frame.notify = output_frame;
    wl_signal_add(&wlr_output->events.frame, &output->frame);
    output->request_state.notify = output_request_state;
    wl_signal_add(&wlr_output->events.request_state, &output->request_state);
    output->destroy.notify = output_destroy;
    wl_signal_add(&wlr_output->events.destroy, &output->destroy);

    wl_list_insert(&server->outputs, &output->link);

    struct wlr_output_layout_output *layout_output =
        wlr_output_layout_add_auto(server->output_layout, wlr_output);
    struct wlr_scene_output *scene_output = wlr_scene_output_create(server->scene, wlr_output);
    wlr_scene_output_layout_add_output(server->scene_layout, layout_output, scene_output);

    /* Put the pointer somewhere visible. Without this it sits at (0,0) until
     * the first mouse movement, which on a machine with no mouse is never. */
    wlr_cursor_warp(server->cursor, NULL, wlr_output->width / 2.0, wlr_output->height / 2.0);
    cursor_follow(server);

    if (server->cb.output_added != NULL) {
        server->cb.output_added(output, wlr_output->width, wlr_output->height,
                                wlr_output->refresh, wlr_output->name);
    }
}

/* --- toplevels ---------------------------------------------------------- */

static void xdg_toplevel_map(struct wl_listener *listener, void *data) {
    struct tx_toplevel *toplevel = wl_container_of(listener, toplevel, map);
    (void)data;

    if (toplevel->server->cb.toplevel_mapped != NULL) {
        toplevel->server->cb.toplevel_mapped(toplevel);
    }
}

static void xdg_toplevel_unmap(struct wl_listener *listener, void *data) {
    struct tx_toplevel *toplevel = wl_container_of(listener, toplevel, unmap);
    (void)data;

    if (toplevel->server->cb.toplevel_unmapped != NULL) {
        toplevel->server->cb.toplevel_unmapped(toplevel);
    }
}

static void xdg_toplevel_commit(struct wl_listener *listener, void *data) {
    struct tx_toplevel *toplevel = wl_container_of(listener, toplevel, commit);
    (void)data;

    /* xdg-shell requires a configure in reply to the first commit before the
     * client may attach a buffer. A zero size means "you choose", which is the
     * right answer until there is a tiling policy that says otherwise. */
    if (toplevel->xdg_toplevel->base->initial_commit) {
        wlr_xdg_toplevel_set_size(toplevel->xdg_toplevel, 0, 0);
    }
}

static void xdg_toplevel_destroy(struct wl_listener *listener, void *data) {
    struct tx_toplevel *toplevel = wl_container_of(listener, toplevel, destroy);
    (void)data;

    if (toplevel->server->cb.toplevel_removed != NULL) {
        toplevel->server->cb.toplevel_removed(toplevel);
    }

    wl_list_remove(&toplevel->map.link);
    wl_list_remove(&toplevel->unmap.link);
    wl_list_remove(&toplevel->commit.link);
    wl_list_remove(&toplevel->destroy.link);
    wl_list_remove(&toplevel->request_move.link);
    wl_list_remove(&toplevel->request_resize.link);
    wl_list_remove(&toplevel->request_maximize.link);
    wl_list_remove(&toplevel->request_fullscreen.link);
    free(toplevel);
}

static void xdg_toplevel_request_move(struct wl_listener *listener, void *data) {
    struct tx_toplevel *toplevel = wl_container_of(listener, toplevel, request_move);
    (void)data;

    if (toplevel->server->cb.toplevel_request_move != NULL) {
        toplevel->server->cb.toplevel_request_move(toplevel);
    }
}

static void xdg_toplevel_request_resize(struct wl_listener *listener, void *data) {
    struct tx_toplevel *toplevel = wl_container_of(listener, toplevel, request_resize);
    struct wlr_xdg_toplevel_resize_event *event = data;

    if (toplevel->server->cb.toplevel_request_resize != NULL) {
        toplevel->server->cb.toplevel_request_resize(toplevel, event->edges);
    }
}

/*
 * Maximise and fullscreen are answered with an empty configure. The protocol
 * requires *a* reply; it does not require the compositor to agree, and a
 * window manager that has no opinion about screen-filling windows yet should
 * say so rather than pretend.
 */
static void xdg_toplevel_request_maximize(struct wl_listener *listener, void *data) {
    struct tx_toplevel *toplevel = wl_container_of(listener, toplevel, request_maximize);
    (void)data;

    if (toplevel->xdg_toplevel->base->initialized) {
        wlr_xdg_surface_schedule_configure(toplevel->xdg_toplevel->base);
    }
}

static void xdg_toplevel_request_fullscreen(struct wl_listener *listener, void *data) {
    struct tx_toplevel *toplevel = wl_container_of(listener, toplevel, request_fullscreen);
    (void)data;

    if (toplevel->xdg_toplevel->base->initialized) {
        wlr_xdg_surface_schedule_configure(toplevel->xdg_toplevel->base);
    }
}

static void server_new_xdg_toplevel(struct wl_listener *listener, void *data) {
    struct tx_server *server = wl_container_of(listener, server, new_xdg_toplevel);
    struct wlr_xdg_toplevel *xdg_toplevel = data;

    struct tx_toplevel *toplevel = calloc(1, sizeof(*toplevel));
    if (toplevel == NULL) {
        return;
    }
    toplevel->server = server;
    toplevel->xdg_toplevel = xdg_toplevel;
    toplevel->scene_tree = wlr_scene_xdg_surface_create(&server->scene->tree, xdg_toplevel->base);
    /* Both directions: the scene tree finds the toplevel during hit testing,
     * and popups find the tree to parent themselves under. */
    toplevel->scene_tree->node.data = toplevel;
    xdg_toplevel->base->data = toplevel->scene_tree;

    toplevel->map.notify = xdg_toplevel_map;
    wl_signal_add(&xdg_toplevel->base->surface->events.map, &toplevel->map);
    toplevel->unmap.notify = xdg_toplevel_unmap;
    wl_signal_add(&xdg_toplevel->base->surface->events.unmap, &toplevel->unmap);
    toplevel->commit.notify = xdg_toplevel_commit;
    wl_signal_add(&xdg_toplevel->base->surface->events.commit, &toplevel->commit);
    toplevel->destroy.notify = xdg_toplevel_destroy;
    wl_signal_add(&xdg_toplevel->events.destroy, &toplevel->destroy);
    toplevel->request_move.notify = xdg_toplevel_request_move;
    wl_signal_add(&xdg_toplevel->events.request_move, &toplevel->request_move);
    toplevel->request_resize.notify = xdg_toplevel_request_resize;
    wl_signal_add(&xdg_toplevel->events.request_resize, &toplevel->request_resize);
    toplevel->request_maximize.notify = xdg_toplevel_request_maximize;
    wl_signal_add(&xdg_toplevel->events.request_maximize, &toplevel->request_maximize);
    toplevel->request_fullscreen.notify = xdg_toplevel_request_fullscreen;
    wl_signal_add(&xdg_toplevel->events.request_fullscreen, &toplevel->request_fullscreen);

    if (server->cb.toplevel_added != NULL) {
        server->cb.toplevel_added(toplevel);
    }
}

static void xdg_popup_commit(struct wl_listener *listener, void *data) {
    struct tx_popup *popup = wl_container_of(listener, popup, commit);
    (void)data;

    if (popup->xdg_popup->base->initial_commit) {
        wlr_xdg_surface_schedule_configure(popup->xdg_popup->base);
    }
}

static void xdg_popup_destroy(struct wl_listener *listener, void *data) {
    struct tx_popup *popup = wl_container_of(listener, popup, destroy);
    (void)data;

    wl_list_remove(&popup->commit.link);
    wl_list_remove(&popup->destroy.link);
    free(popup);
}

/*
 * Popups are handled entirely here. A menu appears where its parent said, it
 * is stacked above that parent, and it goes away when dismissed — there is no
 * decision in any of that, and the managed side is not told about them.
 */
static void server_new_xdg_popup(struct wl_listener *listener, void *data) {
    struct wlr_xdg_popup *xdg_popup = data;
    (void)listener;

    struct tx_popup *popup = calloc(1, sizeof(*popup));
    if (popup == NULL) {
        return;
    }
    popup->xdg_popup = xdg_popup;

    struct wlr_xdg_surface *parent = wlr_xdg_surface_try_from_wlr_surface(xdg_popup->parent);
    if (parent == NULL) {
        free(popup);
        return;
    }
    struct wlr_scene_tree *parent_tree = parent->data;
    xdg_popup->base->data = wlr_scene_xdg_surface_create(parent_tree, xdg_popup->base);

    popup->commit.notify = xdg_popup_commit;
    wl_signal_add(&xdg_popup->base->surface->events.commit, &popup->commit);
    popup->destroy.notify = xdg_popup_destroy;
    wl_signal_add(&xdg_popup->events.destroy, &popup->destroy);
}

/* --- public interface --------------------------------------------------- */

struct trinix_wlr_server *trinix_wlr_create(const struct trinix_wlr_callbacks *callbacks,
                                            int log_level) {
    wlr_log_init((enum wlr_log_importance)log_level, NULL);

    struct tx_server *server = calloc(1, sizeof(*server));
    if (server == NULL) {
        return NULL;
    }
    if (callbacks != NULL) {
        server->cb = *callbacks;
    }

    server->display = wl_display_create();
    if (server->display == NULL) {
        free(server);
        return NULL;
    }

    server->backend = wlr_backend_autocreate(wl_display_get_event_loop(server->display), NULL);
    if (server->backend == NULL) {
        wlr_log(WLR_ERROR, "no backend: no DRM device, or no seat to open it with");
        goto fail;
    }

    /* Pixman unless WLR_RENDERER says otherwise — this build has no other
     * renderer compiled in. See base/recipes/wlroots/recipe.sh. */
    server->renderer = wlr_renderer_autocreate(server->backend);
    if (server->renderer == NULL) {
        wlr_log(WLR_ERROR, "no renderer");
        goto fail;
    }
    wlr_renderer_init_wl_display(server->renderer, server->display);

    server->allocator = wlr_allocator_autocreate(server->backend, server->renderer);
    if (server->allocator == NULL) {
        wlr_log(WLR_ERROR, "no allocator");
        goto fail;
    }

    wlr_compositor_create(server->display, 5, server->renderer);
    wlr_subcompositor_create(server->display);
    wlr_data_device_manager_create(server->display);

    server->output_layout = wlr_output_layout_create(server->display);
    server->scene = wlr_scene_create();
    server->scene_layout = wlr_scene_attach_output_layout(server->scene, server->output_layout);

    /* First node in the scene, so it stays at the bottom of the stack. */
    struct wlr_scene_rect *background = wlr_scene_rect_create(
        &server->scene->tree, BACKGROUND_SPAN, BACKGROUND_SPAN, BACKGROUND_COLOR);
    wlr_scene_node_set_position(&background->node, 0, 0);

    wl_list_init(&server->outputs);
    wl_list_init(&server->keyboards);

    server->new_output.notify = server_new_output;
    wl_signal_add(&server->backend->events.new_output, &server->new_output);
    server->new_input.notify = server_new_input;
    wl_signal_add(&server->backend->events.new_input, &server->new_input);

    server->xdg_shell = wlr_xdg_shell_create(server->display, 3);
    server->new_xdg_toplevel.notify = server_new_xdg_toplevel;
    wl_signal_add(&server->xdg_shell->events.new_toplevel, &server->new_xdg_toplevel);
    server->new_xdg_popup.notify = server_new_xdg_popup;
    wl_signal_add(&server->xdg_shell->events.new_popup, &server->new_xdg_popup);

    server->cursor = wlr_cursor_create();
    wlr_cursor_attach_output_layout(server->cursor, server->output_layout);
    cursor_build(server);

    server->cursor_motion.notify = server_cursor_motion;
    wl_signal_add(&server->cursor->events.motion, &server->cursor_motion);
    server->cursor_motion_absolute.notify = server_cursor_motion_absolute;
    wl_signal_add(&server->cursor->events.motion_absolute, &server->cursor_motion_absolute);
    server->cursor_button.notify = server_cursor_button;
    wl_signal_add(&server->cursor->events.button, &server->cursor_button);
    server->cursor_axis.notify = server_cursor_axis;
    wl_signal_add(&server->cursor->events.axis, &server->cursor_axis);
    server->cursor_frame.notify = server_cursor_frame;
    wl_signal_add(&server->cursor->events.frame, &server->cursor_frame);

    server->seat = wlr_seat_create(server->display, "seat0");
    server->request_cursor.notify = seat_request_cursor;
    wl_signal_add(&server->seat->events.request_set_cursor, &server->request_cursor);
    server->request_set_selection.notify = seat_request_set_selection;
    wl_signal_add(&server->seat->events.request_set_selection, &server->request_set_selection);

    return (struct trinix_wlr_server *)server;

fail:
    if (server->backend != NULL) {
        wlr_backend_destroy(server->backend);
    }
    wl_display_destroy(server->display);
    free(server);
    return NULL;
}

bool trinix_wlr_start(struct trinix_wlr_server *handle, const char *socket_name) {
    struct tx_server *server = (struct tx_server *)handle;

    const char *socket;
    if (socket_name != NULL && socket_name[0] != '\0') {
        if (wl_display_add_socket(server->display, socket_name) != 0) {
            wlr_log(WLR_ERROR, "could not listen on '%s' — is one already running?",
                    socket_name);
            return false;
        }
        socket = socket_name;
    } else {
        socket = wl_display_add_socket_auto(server->display);
    }
    if (socket == NULL) {
        wlr_log(WLR_ERROR, "could not create a Wayland socket in XDG_RUNTIME_DIR");
        return false;
    }
    snprintf(server->socket, sizeof(server->socket), "%s", socket);

    if (!wlr_backend_start(server->backend)) {
        wlr_log(WLR_ERROR, "backend refused to start");
        return false;
    }
    return true;
}

const char *trinix_wlr_socket(struct trinix_wlr_server *handle) {
    return ((struct tx_server *)handle)->socket;
}

void trinix_wlr_run(struct trinix_wlr_server *handle) {
    wl_display_run(((struct tx_server *)handle)->display);
}

void trinix_wlr_terminate(struct trinix_wlr_server *handle) {
    wl_display_terminate(((struct tx_server *)handle)->display);
}

void trinix_wlr_destroy(struct trinix_wlr_server *handle) {
    struct tx_server *server = (struct tx_server *)handle;

    wl_display_destroy_clients(server->display);
    wlr_scene_node_destroy(&server->scene->tree.node);
    wlr_cursor_destroy(server->cursor);
    wlr_allocator_destroy(server->allocator);
    wlr_renderer_destroy(server->renderer);
    wlr_backend_destroy(server->backend);
    wl_display_destroy(server->display);
    free(server);
}

void trinix_wlr_output_size(void *handle, int32_t *width, int32_t *height) {
    struct tx_output *output = handle;

    if (width != NULL) {
        *width = output->wlr_output->width;
    }
    if (height != NULL) {
        *height = output->wlr_output->height;
    }
}

const char *trinix_wlr_toplevel_title(void *handle) {
    return ((struct tx_toplevel *)handle)->xdg_toplevel->title;
}

const char *trinix_wlr_toplevel_app_id(void *handle) {
    return ((struct tx_toplevel *)handle)->xdg_toplevel->app_id;
}

void trinix_wlr_toplevel_set_position(void *handle, int32_t x, int32_t y) {
    struct tx_toplevel *toplevel = handle;

    wlr_scene_node_set_position(&toplevel->scene_tree->node, x, y);
}

void trinix_wlr_toplevel_set_size(void *handle, int32_t width, int32_t height) {
    struct tx_toplevel *toplevel = handle;

    wlr_xdg_toplevel_set_size(toplevel->xdg_toplevel, width, height);
}

void trinix_wlr_toplevel_get_box(void *handle, int32_t *x, int32_t *y,
                                 int32_t *width, int32_t *height) {
    struct tx_toplevel *toplevel = handle;
    struct wlr_box *geometry = &toplevel->xdg_toplevel->base->geometry;

    if (x != NULL) {
        *x = toplevel->scene_tree->node.x;
    }
    if (y != NULL) {
        *y = toplevel->scene_tree->node.y;
    }
    if (width != NULL) {
        *width = geometry->width;
    }
    if (height != NULL) {
        *height = geometry->height;
    }
}

void trinix_wlr_toplevel_focus(void *handle) {
    struct tx_toplevel *toplevel = handle;
    struct tx_server *server = toplevel->server;
    struct wlr_seat *seat = server->seat;
    struct wlr_surface *surface = toplevel->xdg_toplevel->base->surface;

    if (seat->keyboard_state.focused_surface == surface) {
        return;
    }

    /* Tell the outgoing window it lost focus, so it stops drawing a caret and
     * dims its title bar. */
    if (seat->keyboard_state.focused_surface != NULL) {
        struct wlr_xdg_toplevel *previous =
            wlr_xdg_toplevel_try_from_wlr_surface(seat->keyboard_state.focused_surface);
        if (previous != NULL) {
            wlr_xdg_toplevel_set_activated(previous, false);
        }
    }

    wlr_scene_node_raise_to_top(&toplevel->scene_tree->node);
    cursor_follow(server);
    wlr_xdg_toplevel_set_activated(toplevel->xdg_toplevel, true);

    struct wlr_keyboard *keyboard = wlr_seat_get_keyboard(seat);
    if (keyboard != NULL) {
        wlr_seat_keyboard_notify_enter(seat, surface, keyboard->keycodes,
                                       keyboard->num_keycodes, &keyboard->modifiers);
    }
}

void trinix_wlr_toplevel_close(void *handle) {
    struct tx_toplevel *toplevel = handle;

    wlr_xdg_toplevel_send_close(toplevel->xdg_toplevel);
}

void trinix_wlr_cursor_position(struct trinix_wlr_server *handle, double *lx, double *ly) {
    struct tx_server *server = (struct tx_server *)handle;

    if (lx != NULL) {
        *lx = server->cursor->x;
    }
    if (ly != NULL) {
        *ly = server->cursor->y;
    }
}

void *trinix_wlr_toplevel_at(struct trinix_wlr_server *handle, double lx, double ly) {
    double sx, sy;

    return toplevel_at((struct tx_server *)handle, lx, ly, NULL, &sx, &sy);
}

void trinix_wlr_pointer_passthrough(struct trinix_wlr_server *handle, uint32_t time_msec) {
    struct tx_server *server = (struct tx_server *)handle;
    struct wlr_surface *surface = NULL;
    double sx, sy;

    toplevel_at(server, server->cursor->x, server->cursor->y, &surface, &sx, &sy);

    if (surface != NULL) {
        /* wlroots suppresses duplicate enters and motions, so this is cheap to
         * call on every pointer event. */
        wlr_seat_pointer_notify_enter(server->seat, surface, sx, sy);
        wlr_seat_pointer_notify_motion(server->seat, time_msec, sx, sy);
    } else {
        wlr_seat_pointer_clear_focus(server->seat);
    }
}
