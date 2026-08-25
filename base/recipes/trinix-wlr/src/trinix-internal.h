/*
 * Shared between trinix-wlr.c and trinix-shell.c — the objects both halves of
 * the compositor's C side need to see.
 *
 * Not installed, and not part of the interface: trinix-wlr.h is what the
 * managed side compiles against, and nothing in this file appears there. The
 * split exists so that the two Trinix protocol extensions live next to each
 * other rather than in the middle of the core event plumbing.
 */
#ifndef TRINIX_INTERNAL_H
#define TRINIX_INTERNAL_H

#include <pixman.h>
#include <stdbool.h>
#include <stdint.h>

#include <wayland-server-core.h>
#include <wlr/types/wlr_scene.h>
#include <wlr/types/wlr_xdg_shell.h>

#include "trinix-wlr.h"

/* Close, minimise, zoom — trinix_shell_surface_v1's control enum. */
#define TX_CONTROL_COUNT 3

struct tx_control_zone {
    bool set;
    int32_t x, y, width, height;
};

/* One node of a client's menu. Flat, linked by parent, because that is how a
 * tree survives being mutated one item at a time. */
struct tx_menu_item {
    struct wl_list link;
    uint32_t id;
    uint32_t parent;
    uint32_t kind;
    uint32_t state;
    uint32_t keysym;
    uint32_t modifiers;
    char *label;
};

/*
 * One exported menu bar, and the thing it is scoped to.
 *
 * `client` is always set, because a menu is an application's; `toplevel` is
 * set only on an override, which is trinix-menu-v1's second and rarer scope.
 * Neither lives on tx_toplevel any more: a client's bar outlives every window
 * that client ever opens, which is the whole reason it is a client's.
 *
 * ⚠ `toplevel` is cleared when the window it overrides is destroyed, and the
 * record stays alive until the client destroys the resource. A detached
 * override still accepts requests — a client that is slow to notice is not an
 * error — but nothing resolves to it, so nothing it says is ever shown.
 */
struct tx_menu {
    struct wl_list link;
    struct tx_server *server;
    struct wl_client *client;
    struct tx_toplevel *toplevel;
    struct wl_resource *resource;
    struct wl_list items;
};

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

    /* The two Trinix extensions. */
    struct wl_global *shell_global;
    struct wl_global *menu_global;

    /* trinix-menu-v1's two scopes, kept in two lists rather than one list with
     * a discriminator: every lookup asks exactly one of the two questions, and
     * a list that answers only that question cannot answer it wrongly. */
    struct wl_list client_menus;
    struct wl_list toplevel_menus;

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
    char socket[64];
};

struct tx_toplevel {
    struct tx_server *server;
    struct wlr_xdg_toplevel *xdg_toplevel;

    /* The connection this window came in on, cached at creation because it is
     * what a menu is scoped to and it is asked for on every focus change. It
     * cannot change, and a window outlives its client by nothing. */
    struct wl_client *client;

    struct wlr_scene_tree *scene_tree;
    struct wl_listener map;
    struct wl_listener unmap;
    struct wl_listener commit;
    struct wl_listener destroy;
    struct wl_listener request_move;
    struct wl_listener request_resize;
    struct wl_listener request_maximize;
    struct wl_listener request_fullscreen;

    /* trinix-shell-v1. Null resource means the client never bound it, which is
     * legal: the window is then undecorated and everything else still works. */
    struct wl_resource *shell_surface;
    struct tx_control_zone controls[TX_CONTROL_COUNT];
    pixman_region32_t drag_region;
    bool drag_region_set;
    int32_t resize_inset;
    uint32_t shadow_style;
    int32_t corner_radius;

    /* trinix-menu-v1 keeps nothing here. A window has no menu of its own; it
     * has, at most, an override, and that is looked up on the server. */
};

/* Called from the core: set up and tear down the per-toplevel extension state,
 * and create the globals. */
void tx_shell_init(struct tx_server *server);
void tx_toplevel_extensions_init(struct tx_toplevel *toplevel);
void tx_toplevel_extensions_finish(struct tx_toplevel *toplevel);

#endif /* TRINIX_INTERNAL_H */
