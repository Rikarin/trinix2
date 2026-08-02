/*
 * trinix-shell.c — the two Trinix protocol extensions, server side.
 *
 * trinix-shell-v1 lets a client that draws its own decorations tell the
 * compositor where it put them; trinix-menu-v1 lets it describe its menus so
 * the shell can draw them at the top of the screen. Both protocols are defined
 * in base/recipes/trinix-protocols/protocol/, and the reasoning behind them is
 * in docs/vixen-platform-contract.md.
 *
 * As everywhere else in this library, nothing here decides anything. It
 * receives what clients declare, keeps it, answers questions about it, and
 * reports changes upwards. Which control a click on a title bar hits is a
 * lookup; what to do about it is C#.
 */
#define _POSIX_C_SOURCE 200809L

#include <stdlib.h>
#include <string.h>

#include <wlr/types/wlr_compositor.h>
#include <wlr/util/log.h>

#include "trinix-internal.h"
#include "trinix-menu-v1-protocol.h"
#include "trinix-shell-v1-protocol.h"

/* What this compositor will actually do. Software rendering, so no blur —
 * saying so is cheaper for everyone than letting clients discover it. */
#define TX_SHELL_CAPABILITIES \
    (TRINIX_SHELL_V1_CAPABILITY_SHADOW | TRINIX_SHELL_V1_CAPABILITY_ROUNDED_CORNERS)

/* The margins a shadow occupies outside the window, reported back so a client
 * can reason about its own footprint. Indexed by shadow_style. */
static const int tx_shadow_margin[] = { 0, 8, 24, 48 };

static struct tx_toplevel *toplevel_from_shell_surface(struct wl_resource *resource);
static struct tx_toplevel *toplevel_from_menu(struct wl_resource *resource);

/* --- trinix_shell_surface_v1 -------------------------------------------- */

static void shell_surface_handle_destroy(struct wl_client *client,
                                         struct wl_resource *resource) {
    (void)client;
    wl_resource_destroy(resource);
}

static void shell_surface_notify_decorated(struct tx_toplevel *toplevel) {
    if (toplevel->server->cb.toplevel_decorated != NULL) {
        toplevel->server->cb.toplevel_decorated(toplevel, toplevel->shadow_style,
                                                toplevel->corner_radius);
    }
}

static void shell_surface_send_shadow(struct tx_toplevel *toplevel) {
    if (toplevel->shell_surface == NULL) {
        return;
    }

    size_t index = toplevel->shadow_style;
    if (index >= sizeof(tx_shadow_margin) / sizeof(tx_shadow_margin[0])) {
        index = 0;
    }
    int margin = tx_shadow_margin[index];

    /* Deeper at the bottom than the top, because that is what a light source
     * above the screen produces and what every other desktop draws. */
    trinix_shell_surface_v1_send_shadow_applied(toplevel->shell_surface, margin,
                                                margin / 2, margin, margin);
}

static void shell_surface_handle_set_shadow(struct wl_client *client,
                                            struct wl_resource *resource, uint32_t style) {
    (void)client;
    struct tx_toplevel *toplevel = toplevel_from_shell_surface(resource);
    if (toplevel == NULL) {
        return;
    }

    toplevel->shadow_style = style;
    shell_surface_send_shadow(toplevel);
    shell_surface_notify_decorated(toplevel);
}

static void shell_surface_handle_set_corner_radius(struct wl_client *client,
                                                   struct wl_resource *resource,
                                                   int32_t radius) {
    (void)client;
    struct tx_toplevel *toplevel = toplevel_from_shell_surface(resource);
    if (toplevel == NULL) {
        return;
    }

    toplevel->corner_radius = radius < 0 ? 0 : radius;
    shell_surface_notify_decorated(toplevel);
}

static void shell_surface_handle_set_drag_region(struct wl_client *client,
                                                 struct wl_resource *resource,
                                                 struct wl_resource *region_resource) {
    (void)client;
    struct tx_toplevel *toplevel = toplevel_from_shell_surface(resource);
    if (toplevel == NULL) {
        return;
    }

    /* Copied rather than referenced: a wl_region is the client's to destroy the
     * moment this request returns, and half of them do exactly that. */
    pixman_region32_clear(&toplevel->drag_region);
    if (region_resource == NULL) {
        toplevel->drag_region_set = false;
        return;
    }

    const pixman_region32_t *region = wlr_region_from_resource(region_resource);
    if (region != NULL) {
        pixman_region32_copy(&toplevel->drag_region, (pixman_region32_t *)region);
        toplevel->drag_region_set = true;
    }
}

static void shell_surface_handle_set_resize_inset(struct wl_client *client,
                                                  struct wl_resource *resource,
                                                  int32_t inset) {
    (void)client;
    struct tx_toplevel *toplevel = toplevel_from_shell_surface(resource);
    if (toplevel != NULL) {
        toplevel->resize_inset = inset < 0 ? 0 : inset;
    }
}

static void shell_surface_handle_set_control(struct wl_client *client,
                                             struct wl_resource *resource, uint32_t control,
                                             int32_t x, int32_t y, int32_t width,
                                             int32_t height) {
    (void)client;
    struct tx_toplevel *toplevel = toplevel_from_shell_surface(resource);
    if (toplevel == NULL) {
        return;
    }
    if (control >= TX_CONTROL_COUNT || width <= 0 || height <= 0) {
        wl_resource_post_error(resource, TRINIX_SHELL_V1_ERROR_INVALID_CONTROL,
                               "control %u with a %dx%d zone", control, width, height);
        return;
    }

    toplevel->controls[control] = (struct tx_control_zone){
        .set = true, .x = x, .y = y, .width = width, .height = height,
    };

    if (toplevel->server->cb.toplevel_control != NULL) {
        toplevel->server->cb.toplevel_control(toplevel, control, x, y, width, height);
    }
}

static void shell_surface_handle_unset_control(struct wl_client *client,
                                               struct wl_resource *resource,
                                               uint32_t control) {
    (void)client;
    struct tx_toplevel *toplevel = toplevel_from_shell_surface(resource);
    if (toplevel == NULL || control >= TX_CONTROL_COUNT) {
        return;
    }

    toplevel->controls[control].set = false;
    if (toplevel->server->cb.toplevel_control != NULL) {
        toplevel->server->cb.toplevel_control(toplevel, control, 0, 0, 0, 0);
    }
}

static const struct trinix_shell_surface_v1_interface shell_surface_impl = {
    .destroy = shell_surface_handle_destroy,
    .set_shadow = shell_surface_handle_set_shadow,
    .set_corner_radius = shell_surface_handle_set_corner_radius,
    .set_drag_region = shell_surface_handle_set_drag_region,
    .set_resize_inset = shell_surface_handle_set_resize_inset,
    .set_control = shell_surface_handle_set_control,
    .unset_control = shell_surface_handle_unset_control,
};

static struct tx_toplevel *toplevel_from_shell_surface(struct wl_resource *resource) {
    return wl_resource_get_user_data(resource);
}

static void shell_surface_handle_resource_destroy(struct wl_resource *resource) {
    struct tx_toplevel *toplevel = toplevel_from_shell_surface(resource);
    if (toplevel != NULL) {
        toplevel->shell_surface = NULL;
    }
}

/* --- trinix_shell_v1 ---------------------------------------------------- */

static void shell_handle_destroy(struct wl_client *client, struct wl_resource *resource) {
    (void)client;
    wl_resource_destroy(resource);
}

static void shell_handle_get_shell_surface(struct wl_client *client,
                                           struct wl_resource *resource, uint32_t id,
                                           struct wl_resource *toplevel_resource) {
    struct wlr_xdg_toplevel *xdg_toplevel = wlr_xdg_toplevel_from_resource(toplevel_resource);
    /* base->data is the scene tree, and the tree's node data is ours — the same
     * two-step the core uses for hit testing. */
    struct wlr_scene_tree *tree = xdg_toplevel == NULL ? NULL : xdg_toplevel->base->data;
    struct tx_toplevel *toplevel = tree == NULL ? NULL : tree->node.data;

    if (toplevel == NULL) {
        wl_resource_post_error(resource, TRINIX_SHELL_V1_ERROR_ALREADY_EXTENDED,
                               "the toplevel is not one this compositor manages");
        return;
    }
    if (toplevel->shell_surface != NULL) {
        wl_resource_post_error(resource, TRINIX_SHELL_V1_ERROR_ALREADY_EXTENDED,
                               "the toplevel already has a shell surface");
        return;
    }

    struct wl_resource *created = wl_resource_create(
        client, &trinix_shell_surface_v1_interface, wl_resource_get_version(resource), id);
    if (created == NULL) {
        wl_client_post_no_memory(client);
        return;
    }
    wl_resource_set_implementation(created, &shell_surface_impl, toplevel,
                                   shell_surface_handle_resource_destroy);
    toplevel->shell_surface = created;
    shell_surface_send_shadow(toplevel);
}

static const struct trinix_shell_v1_interface shell_impl = {
    .destroy = shell_handle_destroy,
    .get_shell_surface = shell_handle_get_shell_surface,
};

static void shell_bind(struct wl_client *client, void *data, uint32_t version, uint32_t id) {
    (void)data;

    struct wl_resource *resource =
        wl_resource_create(client, &trinix_shell_v1_interface, (int)version, id);
    if (resource == NULL) {
        wl_client_post_no_memory(client);
        return;
    }
    wl_resource_set_implementation(resource, &shell_impl, NULL, NULL);
    trinix_shell_v1_send_capabilities(resource, TX_SHELL_CAPABILITIES);
}

/* --- trinix_menu_v1 ----------------------------------------------------- */

static struct tx_toplevel *toplevel_from_menu(struct wl_resource *resource) {
    return wl_resource_get_user_data(resource);
}

static struct tx_menu_item *menu_find(struct tx_toplevel *toplevel, uint32_t id) {
    struct tx_menu_item *item;
    wl_list_for_each(item, &toplevel->menu_items, link) {
        if (item->id == id) {
            return item;
        }
    }
    return NULL;
}

static void menu_item_destroy(struct tx_menu_item *item) {
    wl_list_remove(&item->link);
    free(item->label);
    free(item);
}

/* Removing an item takes its children with it, which is the only sane reading
 * of "remove a submenu". */
static void menu_remove_recursive(struct tx_toplevel *toplevel, uint32_t id) {
    struct tx_menu_item *item, *next;
    wl_list_for_each_safe(item, next, &toplevel->menu_items, link) {
        if (item->parent == id) {
            menu_remove_recursive(toplevel, item->id);
        }
    }

    item = menu_find(toplevel, id);
    if (item != NULL) {
        menu_item_destroy(item);
    }
}

static void menu_clear(struct tx_toplevel *toplevel) {
    struct tx_menu_item *item, *next;
    wl_list_for_each_safe(item, next, &toplevel->menu_items, link) {
        menu_item_destroy(item);
    }
}

static void menu_handle_destroy(struct wl_client *client, struct wl_resource *resource) {
    (void)client;
    wl_resource_destroy(resource);
}

static void menu_handle_insert(struct wl_client *client, struct wl_resource *resource,
                               uint32_t id, uint32_t parent, int32_t index, uint32_t kind,
                               const char *label) {
    (void)client;
    struct tx_toplevel *toplevel = toplevel_from_menu(resource);
    if (toplevel == NULL) {
        return;
    }

    if (id == 0 || menu_find(toplevel, id) != NULL) {
        wl_resource_post_error(resource, TRINIX_MENU_MANAGER_V1_ERROR_BAD_ITEM,
                               "item id %u is zero or already in use", id);
        return;
    }
    if (parent != 0) {
        struct tx_menu_item *container = menu_find(toplevel, parent);
        if (container == NULL || container->kind != TRINIX_MENU_V1_KIND_SUBMENU) {
            wl_resource_post_error(resource, TRINIX_MENU_MANAGER_V1_ERROR_BAD_PARENT,
                                   "item %u is not a submenu", parent);
            return;
        }
    }

    struct tx_menu_item *item = calloc(1, sizeof(*item));
    if (item == NULL) {
        wl_client_post_no_memory(client);
        return;
    }
    item->id = id;
    item->parent = parent;
    item->kind = kind;
    item->state = TRINIX_MENU_V1_STATE_ENABLED;
    item->label = strdup(label == NULL ? "" : label);

    /* The list is flat and in tree order only by construction, so an insert
     * with an index has to find the sibling it goes before. A negative index,
     * or one past the end, appends — which is what almost every caller wants
     * and what saves them counting. */
    struct tx_menu_item *before = NULL;
    if (index >= 0) {
        int32_t seen = 0;
        struct tx_menu_item *sibling;
        wl_list_for_each(sibling, &toplevel->menu_items, link) {
            if (sibling->parent != parent) {
                continue;
            }
            if (seen++ == index) {
                before = sibling;
                break;
            }
        }
    }

    if (before != NULL) {
        wl_list_insert(before->link.prev, &item->link);
    } else {
        wl_list_insert(toplevel->menu_items.prev, &item->link);
    }
}

static void menu_handle_update(struct wl_client *client, struct wl_resource *resource,
                               uint32_t id, const char *label, uint32_t state) {
    (void)client;
    struct tx_toplevel *toplevel = toplevel_from_menu(resource);
    if (toplevel == NULL) {
        return;
    }

    struct tx_menu_item *item = menu_find(toplevel, id);
    if (item == NULL) {
        wl_resource_post_error(resource, TRINIX_MENU_MANAGER_V1_ERROR_BAD_ITEM,
                               "no item %u", id);
        return;
    }

    free(item->label);
    item->label = strdup(label == NULL ? "" : label);
    item->state = state;
}

static void menu_handle_set_accelerator(struct wl_client *client, struct wl_resource *resource,
                                        uint32_t id, uint32_t keysym, uint32_t modifiers) {
    (void)client;
    struct tx_toplevel *toplevel = toplevel_from_menu(resource);
    if (toplevel == NULL) {
        return;
    }

    struct tx_menu_item *item = menu_find(toplevel, id);
    if (item == NULL) {
        wl_resource_post_error(resource, TRINIX_MENU_MANAGER_V1_ERROR_BAD_ITEM,
                               "no item %u", id);
        return;
    }

    item->keysym = keysym;
    item->modifiers = modifiers;
}

static void menu_handle_remove(struct wl_client *client, struct wl_resource *resource,
                               uint32_t id) {
    (void)client;
    struct tx_toplevel *toplevel = toplevel_from_menu(resource);
    if (toplevel != NULL) {
        menu_remove_recursive(toplevel, id);
    }
}

/* Emitted parent-first and in sibling order, so the receiver can build its own
 * tree in one pass without looking anything up. */
static uint32_t menu_emit(struct tx_toplevel *toplevel, uint32_t parent) {
    uint32_t emitted = 0;
    struct tx_menu_item *item;

    wl_list_for_each(item, &toplevel->menu_items, link) {
        if (item->parent != parent) {
            continue;
        }
        toplevel->server->cb.menu_item(toplevel, item->id, item->parent, item->kind,
                                       item->state, item->keysym, item->modifiers,
                                       item->label);
        emitted++;
        emitted += menu_emit(toplevel, item->id);
    }
    return emitted;
}

static void menu_handle_commit(struct wl_client *client, struct wl_resource *resource) {
    (void)client;
    struct tx_toplevel *toplevel = toplevel_from_menu(resource);
    if (toplevel == NULL || toplevel->server->cb.menu_item == NULL) {
        return;
    }

    /*
     * Mutations are applied as they arrive and published only here, which is
     * where the protocol's atomicity actually matters: the shell never sees a
     * half-rebuilt menu because the shell only ever reads what commit
     * publishes. Staging the mutations as well would buy nothing — a client
     * cannot observe the compositor's private list.
     */
    if (toplevel->server->cb.menu_begin != NULL) {
        toplevel->server->cb.menu_begin(toplevel);
    }
    uint32_t count = menu_emit(toplevel, 0);
    if (toplevel->server->cb.menu_end != NULL) {
        toplevel->server->cb.menu_end(toplevel, count);
    }
}

static const struct trinix_menu_v1_interface menu_impl = {
    .destroy = menu_handle_destroy,
    .insert = menu_handle_insert,
    .update = menu_handle_update,
    .set_accelerator = menu_handle_set_accelerator,
    .remove = menu_handle_remove,
    .commit = menu_handle_commit,
};

static void menu_handle_resource_destroy(struct wl_resource *resource) {
    struct tx_toplevel *toplevel = toplevel_from_menu(resource);
    if (toplevel == NULL) {
        return;
    }

    menu_clear(toplevel);
    toplevel->menu = NULL;
    if (toplevel->server->cb.menu_removed != NULL) {
        toplevel->server->cb.menu_removed(toplevel);
    }
}

/* --- trinix_menu_manager_v1 --------------------------------------------- */

static void menu_manager_handle_destroy(struct wl_client *client,
                                        struct wl_resource *resource) {
    (void)client;
    wl_resource_destroy(resource);
}

static void menu_manager_handle_get_menu_bar(struct wl_client *client,
                                             struct wl_resource *resource, uint32_t id,
                                             struct wl_resource *toplevel_resource) {
    struct wlr_xdg_toplevel *xdg_toplevel = wlr_xdg_toplevel_from_resource(toplevel_resource);
    struct wlr_scene_tree *tree = xdg_toplevel == NULL ? NULL : xdg_toplevel->base->data;
    struct tx_toplevel *toplevel = tree == NULL ? NULL : tree->node.data;

    if (toplevel == NULL || toplevel->menu != NULL) {
        wl_resource_post_error(resource, TRINIX_MENU_MANAGER_V1_ERROR_ALREADY_HAS_MENU,
                               "the toplevel already exported a menu bar");
        return;
    }

    struct wl_resource *created = wl_resource_create(
        client, &trinix_menu_v1_interface, wl_resource_get_version(resource), id);
    if (created == NULL) {
        wl_client_post_no_memory(client);
        return;
    }
    wl_resource_set_implementation(created, &menu_impl, toplevel,
                                   menu_handle_resource_destroy);
    toplevel->menu = created;
}

static const struct trinix_menu_manager_v1_interface menu_manager_impl = {
    .destroy = menu_manager_handle_destroy,
    .get_menu_bar = menu_manager_handle_get_menu_bar,
};

static void menu_manager_bind(struct wl_client *client, void *data, uint32_t version,
                              uint32_t id) {
    (void)data;

    struct wl_resource *resource =
        wl_resource_create(client, &trinix_menu_manager_v1_interface, (int)version, id);
    if (resource == NULL) {
        wl_client_post_no_memory(client);
        return;
    }
    wl_resource_set_implementation(resource, &menu_manager_impl, NULL, NULL);
}

/* --- what the core calls ------------------------------------------------ */

void tx_shell_init(struct tx_server *server) {
    server->shell_global =
        wl_global_create(server->display, &trinix_shell_v1_interface, 1, server, shell_bind);
    server->menu_global = wl_global_create(server->display, &trinix_menu_manager_v1_interface,
                                           1, server, menu_manager_bind);

    if (server->shell_global == NULL || server->menu_global == NULL) {
        wlr_log(WLR_ERROR, "could not advertise the Trinix protocol extensions");
    }
}

void tx_toplevel_extensions_init(struct tx_toplevel *toplevel) {
    pixman_region32_init(&toplevel->drag_region);
    wl_list_init(&toplevel->menu_items);
}

void tx_toplevel_extensions_finish(struct tx_toplevel *toplevel) {
    /* The resources outlive the toplevel if the client is slow to notice, so
     * their user data has to stop pointing at freed memory. */
    if (toplevel->shell_surface != NULL) {
        wl_resource_set_user_data(toplevel->shell_surface, NULL);
    }
    if (toplevel->menu != NULL) {
        wl_resource_set_user_data(toplevel->menu, NULL);
    }

    menu_clear(toplevel);
    pixman_region32_fini(&toplevel->drag_region);
}

/* --- the interface the managed side uses -------------------------------- */

int32_t trinix_wlr_toplevel_control_at(void *handle, int32_t x, int32_t y) {
    struct tx_toplevel *toplevel = handle;

    for (int32_t control = 0; control < TX_CONTROL_COUNT; control++) {
        const struct tx_control_zone *zone = &toplevel->controls[control];
        if (!zone->set) {
            continue;
        }
        if (x >= zone->x && x < zone->x + zone->width &&
            y >= zone->y && y < zone->y + zone->height) {
            return control;
        }
    }
    return -1;
}

void trinix_wlr_toplevel_send_control_hover(void *handle, int32_t control, uint32_t state) {
    struct tx_toplevel *toplevel = handle;

    if (toplevel->shell_surface != NULL) {
        trinix_shell_surface_v1_send_control_hover(toplevel->shell_surface,
                                                   control < 0 ? 0 : (uint32_t)control, state);
    }
}

void trinix_wlr_toplevel_send_control_activated(void *handle, int32_t control) {
    struct tx_toplevel *toplevel = handle;

    if (toplevel->shell_surface != NULL && control >= 0) {
        trinix_shell_surface_v1_send_control_activated(toplevel->shell_surface,
                                                        (uint32_t)control);
    }
}

bool trinix_wlr_toplevel_in_drag_region(void *handle, int32_t x, int32_t y) {
    struct tx_toplevel *toplevel = handle;

    if (toplevel->shell_surface == NULL) {
        return false;
    }
    /* No region set means the whole window, per the protocol — right for a
     * panel, and the client asked for it. */
    if (!toplevel->drag_region_set) {
        return true;
    }
    return pixman_region32_contains_point(&toplevel->drag_region, x, y, NULL);
}

void trinix_wlr_menu_send_activated(void *handle, uint32_t id) {
    struct tx_toplevel *toplevel = handle;

    if (toplevel->menu != NULL) {
        trinix_menu_v1_send_activated(toplevel->menu, id);
    }
}

void trinix_wlr_menu_send_about_to_show(void *handle, uint32_t id) {
    struct tx_toplevel *toplevel = handle;

    if (toplevel->menu != NULL) {
        trinix_menu_v1_send_about_to_show(toplevel->menu, id);
    }
}

void trinix_wlr_menu_send_closed(void *handle) {
    struct tx_toplevel *toplevel = handle;

    if (toplevel->menu != NULL) {
        trinix_menu_v1_send_closed(toplevel->menu);
    }
}
