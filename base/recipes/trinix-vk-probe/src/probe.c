// trinix-vk-probe — is there a working Vulkan on this machine, and can it put
// a picture on the screen?
//
// The chain a Vixen application walks on its first frame, in the same order,
// with a line of output at every link so that a failure names itself:
//
//     loader  ->  ICD  ->  physical device  ->  logical device
//                                 |
//     wl_display  ->  wl_surface  +->  VkSurfaceKHR  ->  swapchain  ->  present
//
// Every one of those can fail in a way that reaches the application as "no
// Vulkan device", which is why this exists as a separate program: the answer
// to "is the runtime broken or is the toolkit" should not require a toolkit.
//
// Nothing here is a rendering demonstration. The frame is a clear to a solid
// colour, submitted with vkCmdClearColorImage rather than through a pipeline,
// because a pipeline would be testing shader compilation of *this program's*
// shaders. lavapipe JITs its own clear path through LLVM either way, so the
// interesting dependency is exercised without a single line of GLSL here.

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <wayland-client.h>

// Before vulkan.h, and load-bearing: without it the Wayland entry points are
// not declared at all, and the failure is a compile error that reads as though
// the loader were missing rather than a macro.
#define VK_USE_PLATFORM_WAYLAND_KHR
#include <vulkan/vulkan.h>

#include "xdg-shell-client-protocol.h"

#define WIDTH  640
#define HEIGHT 480

// A failure here is a failed check, not an exception: print what broke, at the
// step it broke on, and leave with a status the caller can read.
#define FAIL(...) do { fprintf(stderr, "trinix-vk-probe: " __VA_ARGS__); fputc('\n', stderr); return 1; } while (0)
#define VK_TRY(call, what) do { \
        VkResult _r = (call); \
        if (_r != VK_SUCCESS) FAIL("%s failed (VkResult %d)", (what), (int) _r); \
    } while (0)

struct wayland {
    struct wl_display    *display;
    struct wl_registry   *registry;
    struct wl_compositor *compositor;
    struct xdg_wm_base   *wm_base;
    struct wl_surface    *surface;
    struct xdg_surface   *xdg_surface;
    struct xdg_toplevel  *toplevel;
    bool                  configured;
};

static void wm_base_ping(void *data, struct xdg_wm_base *base, uint32_t serial) {
    (void) data;
    xdg_wm_base_pong(base, serial);
}
static const struct xdg_wm_base_listener wm_base_listener = { .ping = wm_base_ping };

static void registry_global(void *data, struct wl_registry *registry, uint32_t name,
                            const char *interface, uint32_t version) {
    struct wayland *wl = data;
    (void) version;

    if (strcmp(interface, wl_compositor_interface.name) == 0) {
        wl->compositor = wl_registry_bind(registry, name, &wl_compositor_interface, 4);
    } else if (strcmp(interface, xdg_wm_base_interface.name) == 0) {
        wl->wm_base = wl_registry_bind(registry, name, &xdg_wm_base_interface, 1);
        xdg_wm_base_add_listener(wl->wm_base, &wm_base_listener, NULL);
    }
}
static void registry_global_remove(void *data, struct wl_registry *r, uint32_t name) {
    (void) data; (void) r; (void) name;
}
static const struct wl_registry_listener registry_listener = {
    .global = registry_global, .global_remove = registry_global_remove,
};

static void xdg_surface_configure(void *data, struct xdg_surface *surface, uint32_t serial) {
    struct wayland *wl = data;
    xdg_surface_ack_configure(surface, serial);
    wl->configured = true;
}
static const struct xdg_surface_listener xdg_surface_listener = { .configure = xdg_surface_configure };

static const char *device_type(VkPhysicalDeviceType type) {
    switch (type) {
        case VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU: return "integrated GPU";
        case VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU:   return "discrete GPU";
        case VK_PHYSICAL_DEVICE_TYPE_VIRTUAL_GPU:    return "virtual GPU";
        case VK_PHYSICAL_DEVICE_TYPE_CPU:            return "CPU";
        default:                                     return "other";
    }
}

int main(int argc, char **argv) {
    int frames = 0;
    bool headless = false;

    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--frames") == 0 && i + 1 < argc) {
            frames = atoi(argv[++i]);
        } else if (strcmp(argv[i], "--no-window") == 0) {
            headless = true;
        } else {
            FAIL("usage: %s [--frames N] [--no-window]", argv[0]);
        }
    }

    // --- The loader and the ICD -------------------------------------------
    //
    // vkCreateInstance is where a missing ICD manifest shows up, and it does
    // not fail: the loader returns an instance with no drivers behind it, and
    // the enumeration below is what notices.
    struct wayland wl = {0};
    const char *extensions[] = { VK_KHR_SURFACE_EXTENSION_NAME, VK_KHR_WAYLAND_SURFACE_EXTENSION_NAME };

    VkApplicationInfo app = {
        .sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
        .pApplicationName = "trinix-vk-probe",
        .apiVersion = VK_API_VERSION_1_1,
    };
    VkInstanceCreateInfo instance_info = {
        .sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
        .pApplicationInfo = &app,
        .enabledExtensionCount = headless ? 0 : 2,
        .ppEnabledExtensionNames = headless ? NULL : extensions,
    };

    VkInstance instance;
    VK_TRY(vkCreateInstance(&instance_info, NULL, &instance), "vkCreateInstance");
    printf("instance created\n");

    uint32_t count = 0;
    VK_TRY(vkEnumeratePhysicalDevices(instance, &count, NULL), "vkEnumeratePhysicalDevices");
    if (count == 0) FAIL("no physical devices — the loader found no ICD");

    VkPhysicalDevice *devices = calloc(count, sizeof *devices);
    if (!devices) FAIL("out of memory");
    VK_TRY(vkEnumeratePhysicalDevices(instance, &count, devices), "vkEnumeratePhysicalDevices");

    VkPhysicalDevice gpu = devices[0];
    VkPhysicalDeviceProperties props;
    vkGetPhysicalDeviceProperties(gpu, &props);
    printf("device: %s (%s, Vulkan %u.%u.%u)\n", props.deviceName, device_type(props.deviceType),
           VK_VERSION_MAJOR(props.apiVersion), VK_VERSION_MINOR(props.apiVersion),
           VK_VERSION_PATCH(props.apiVersion));
    free(devices);

    // --- A window, if one was asked for -----------------------------------
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    if (!headless) {
        wl.display = wl_display_connect(NULL);
        if (!wl.display) FAIL("cannot connect to a Wayland display — is WAYLAND_DISPLAY set?");

        wl.registry = wl_display_get_registry(wl.display);
        wl_registry_add_listener(wl.registry, &registry_listener, &wl);
        wl_display_roundtrip(wl.display);

        if (!wl.compositor) FAIL("the compositor advertises no wl_compositor");
        if (!wl.wm_base)    FAIL("the compositor advertises no xdg_wm_base");

        wl.surface     = wl_compositor_create_surface(wl.compositor);
        wl.xdg_surface = xdg_wm_base_get_xdg_surface(wl.wm_base, wl.surface);
        xdg_surface_add_listener(wl.xdg_surface, &xdg_surface_listener, &wl);
        wl.toplevel = xdg_surface_get_toplevel(wl.xdg_surface);
        xdg_toplevel_set_title(wl.toplevel, "Trinix Vulkan probe");
        wl_surface_commit(wl.surface);

        while (!wl.configured && wl_display_dispatch(wl.display) != -1) { }
        printf("surface configured\n");

        VkWaylandSurfaceCreateInfoKHR surface_info = {
            .sType = VK_STRUCTURE_TYPE_WAYLAND_SURFACE_CREATE_INFO_KHR,
            .display = wl.display,
            .surface = wl.surface,
        };
        VK_TRY(vkCreateWaylandSurfaceKHR(instance, &surface_info, NULL, &surface),
               "vkCreateWaylandSurfaceKHR");
        printf("VkSurfaceKHR created\n");
    }

    // --- A queue that can do both ------------------------------------------
    //
    // Graphics and presentation are separate capabilities and a real driver
    // can put them on different families. lavapipe has one queue, so this is
    // written the simple way and says so if that assumption ever breaks.
    uint32_t family_count = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(gpu, &family_count, NULL);
    VkQueueFamilyProperties *families = calloc(family_count, sizeof *families);
    if (!families) FAIL("out of memory");
    vkGetPhysicalDeviceQueueFamilyProperties(gpu, &family_count, families);

    uint32_t family = UINT32_MAX;
    for (uint32_t i = 0; i < family_count; i++) {
        if (!(families[i].queueFlags & VK_QUEUE_GRAPHICS_BIT)) continue;
        if (surface != VK_NULL_HANDLE) {
            VkBool32 supported = VK_FALSE;
            VK_TRY(vkGetPhysicalDeviceSurfaceSupportKHR(gpu, i, surface, &supported),
                   "vkGetPhysicalDeviceSurfaceSupportKHR");
            if (!supported) continue;
        }
        family = i;
        break;
    }
    free(families);
    if (family == UINT32_MAX) FAIL("no queue family can both draw and present");
    printf("queue family %u can draw%s\n", family, surface != VK_NULL_HANDLE ? " and present" : "");

    float priority = 1.0f;
    VkDeviceQueueCreateInfo queue_info = {
        .sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
        .queueFamilyIndex = family, .queueCount = 1, .pQueuePriorities = &priority,
    };
    const char *device_extensions[] = { VK_KHR_SWAPCHAIN_EXTENSION_NAME };
    VkDeviceCreateInfo device_info = {
        .sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
        .queueCreateInfoCount = 1, .pQueueCreateInfos = &queue_info,
        .enabledExtensionCount = surface != VK_NULL_HANDLE ? 1 : 0,
        .ppEnabledExtensionNames = surface != VK_NULL_HANDLE ? device_extensions : NULL,
    };

    VkDevice device;
    VK_TRY(vkCreateDevice(gpu, &device_info, NULL, &device), "vkCreateDevice");
    VkQueue queue;
    vkGetDeviceQueue(device, family, 0, &queue);
    printf("logical device created\n");

    if (surface == VK_NULL_HANDLE || frames <= 0) {
        // Reported as zero rather than as the number asked for: with no
        // window there is nothing to present to, and a check reading this line
        // should see what happened rather than what was requested.
        printf("frames=0%s\n", headless && frames > 0 ? " (no window was asked for)" : "");
        vkDestroyDevice(device, NULL);
        vkDestroyInstance(instance, NULL);
        return 0;
    }

    // --- A swapchain -------------------------------------------------------
    //
    // On lavapipe this is Mesa's software path: no dmabuf, no render node, and
    // the images are wl_shm buffers. Which is the interesting fact about the
    // whole exercise — see docs/vixen-platform-contract.md.
    VkSurfaceCapabilitiesKHR caps;
    VK_TRY(vkGetPhysicalDeviceSurfaceCapabilitiesKHR(gpu, surface, &caps),
           "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

    uint32_t format_count = 0;
    VK_TRY(vkGetPhysicalDeviceSurfaceFormatsKHR(gpu, surface, &format_count, NULL),
           "vkGetPhysicalDeviceSurfaceFormatsKHR");
    if (format_count == 0) FAIL("the surface supports no formats");
    VkSurfaceFormatKHR *formats = calloc(format_count, sizeof *formats);
    if (!formats) FAIL("out of memory");
    VK_TRY(vkGetPhysicalDeviceSurfaceFormatsKHR(gpu, surface, &format_count, formats),
           "vkGetPhysicalDeviceSurfaceFormatsKHR");
    VkSurfaceFormatKHR format = formats[0];
    free(formats);

    VkExtent2D extent = caps.currentExtent;
    if (extent.width == UINT32_MAX) { extent.width = WIDTH; extent.height = HEIGHT; }

    uint32_t image_count = caps.minImageCount + 1;
    if (caps.maxImageCount > 0 && image_count > caps.maxImageCount) image_count = caps.maxImageCount;

    VkSwapchainCreateInfoKHR swapchain_info = {
        .sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR,
        .surface = surface,
        .minImageCount = image_count,
        .imageFormat = format.format,
        .imageColorSpace = format.colorSpace,
        .imageExtent = extent,
        .imageArrayLayers = 1,
        // TRANSFER_DST rather than COLOR_ATTACHMENT: the frame is a clear, and
        // a clear is a transfer. See the header.
        .imageUsage = VK_IMAGE_USAGE_TRANSFER_DST_BIT,
        .imageSharingMode = VK_SHARING_MODE_EXCLUSIVE,
        .preTransform = caps.currentTransform,
        .compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR,
        .presentMode = VK_PRESENT_MODE_FIFO_KHR,
        .clipped = VK_TRUE,
    };
    VkSwapchainKHR swapchain;
    VK_TRY(vkCreateSwapchainKHR(device, &swapchain_info, NULL, &swapchain), "vkCreateSwapchainKHR");

    uint32_t got = 0;
    VK_TRY(vkGetSwapchainImagesKHR(device, swapchain, &got, NULL), "vkGetSwapchainImagesKHR");
    VkImage *images = calloc(got, sizeof *images);
    if (!images) FAIL("out of memory");
    VK_TRY(vkGetSwapchainImagesKHR(device, swapchain, &got, images), "vkGetSwapchainImagesKHR");
    printf("swapchain: %ux%u, %u image(s), format %d\n", extent.width, extent.height, got,
           (int) format.format);

    VkCommandPoolCreateInfo pool_info = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
        .flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
        .queueFamilyIndex = family,
    };
    VkCommandPool pool;
    VK_TRY(vkCreateCommandPool(device, &pool_info, NULL, &pool), "vkCreateCommandPool");

    VkCommandBufferAllocateInfo alloc = {
        .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
        .commandPool = pool, .level = VK_COMMAND_BUFFER_LEVEL_PRIMARY, .commandBufferCount = 1,
    };
    VkCommandBuffer cmd;
    VK_TRY(vkAllocateCommandBuffers(device, &alloc, &cmd), "vkAllocateCommandBuffers");

    VkSemaphoreCreateInfo sem_info = { .sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
    VkSemaphore acquired, rendered;
    VK_TRY(vkCreateSemaphore(device, &sem_info, NULL, &acquired), "vkCreateSemaphore");
    VK_TRY(vkCreateSemaphore(device, &sem_info, NULL, &rendered), "vkCreateSemaphore");

    for (int frame = 0; frame < frames; frame++) {
        uint32_t index = 0;
        VK_TRY(vkAcquireNextImageKHR(device, swapchain, UINT64_MAX, acquired, VK_NULL_HANDLE, &index),
               "vkAcquireNextImageKHR");

        VkCommandBufferBeginInfo begin = {
            .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
            .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
        };
        VK_TRY(vkBeginCommandBuffer(cmd, &begin), "vkBeginCommandBuffer");

        VkImageSubresourceRange range = {
            .aspectMask = VK_IMAGE_ASPECT_COLOR_BIT, .levelCount = 1, .layerCount = 1,
        };
        VkImageMemoryBarrier to_dst = {
            .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            .oldLayout = VK_IMAGE_LAYOUT_UNDEFINED,
            .newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            .image = images[index], .subresourceRange = range,
            .dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT,
        };
        vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
                             0, 0, NULL, 0, NULL, 1, &to_dst);

        // Something visibly not black, and different every frame, so that a
        // human watching the VM can tell a live window from a stuck one.
        float t = (float) frame / (float) frames;
        VkClearColorValue colour = { .float32 = { 0.15f, 0.35f + 0.5f * t, 0.65f, 1.0f } };
        vkCmdClearColorImage(cmd, images[index], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                             &colour, 1, &range);

        VkImageMemoryBarrier to_present = to_dst;
        to_present.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        to_present.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
        to_present.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        to_present.dstAccessMask = 0;
        vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,
                             0, 0, NULL, 0, NULL, 1, &to_present);

        VK_TRY(vkEndCommandBuffer(cmd), "vkEndCommandBuffer");

        VkPipelineStageFlags wait_stage = VK_PIPELINE_STAGE_TRANSFER_BIT;
        VkSubmitInfo submit = {
            .sType = VK_STRUCTURE_TYPE_SUBMIT_INFO,
            .waitSemaphoreCount = 1, .pWaitSemaphores = &acquired, .pWaitDstStageMask = &wait_stage,
            .commandBufferCount = 1, .pCommandBuffers = &cmd,
            .signalSemaphoreCount = 1, .pSignalSemaphores = &rendered,
        };
        VK_TRY(vkQueueSubmit(queue, 1, &submit, VK_NULL_HANDLE), "vkQueueSubmit");

        VkPresentInfoKHR present = {
            .sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR,
            .waitSemaphoreCount = 1, .pWaitSemaphores = &rendered,
            .swapchainCount = 1, .pSwapchains = &swapchain, .pImageIndices = &index,
        };
        VK_TRY(vkQueuePresentKHR(queue, &present), "vkQueuePresentKHR");

        // Draining the queue every frame rather than pipelining. Two reasons,
        // and the second is the one that matters: a check that overlaps frames
        // can pass while the previous one was still wrong, and the two
        // semaphores are reused every iteration — which is only safe once
        // nothing is still waiting on them.
        VK_TRY(vkQueueWaitIdle(queue), "vkQueueWaitIdle");

        wl_display_roundtrip(wl.display);
    }

    printf("frames=%d\n", frames);

    vkDeviceWaitIdle(device);
    vkDestroySemaphore(device, rendered, NULL);
    vkDestroySemaphore(device, acquired, NULL);
    vkDestroyCommandPool(device, pool, NULL);
    free(images);
    vkDestroySwapchainKHR(device, swapchain, NULL);
    vkDestroyDevice(device, NULL);
    vkDestroySurfaceKHR(instance, surface, NULL);
    vkDestroyInstance(instance, NULL);

    if (wl.toplevel)    xdg_toplevel_destroy(wl.toplevel);
    if (wl.xdg_surface) xdg_surface_destroy(wl.xdg_surface);
    if (wl.surface)     wl_surface_destroy(wl.surface);
    if (wl.display)     wl_display_disconnect(wl.display);
    return 0;
}
