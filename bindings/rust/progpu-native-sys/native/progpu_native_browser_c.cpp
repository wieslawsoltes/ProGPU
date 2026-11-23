#include "progpu_native_browser_c.h"

#include "progpu_native.h"

#include <webgpu/webgpu.h>

#include <new>

struct progpu_native_browser_canvas {
    WGPUInstance instance = nullptr;
    WGPUSurface surface = nullptr;
    WGPUDevice device = nullptr;
    WGPUTextureFormat format = WGPUTextureFormat_Undefined;
    uint32_t width = 0U;
    uint32_t height = 0U;
    WGPUTexture texture = nullptr;
    WGPUTextureView view = nullptr;
};

namespace {

void release_frame(progpu_native_browser_canvas& canvas) noexcept {
    if (canvas.view != nullptr) {
        wgpuTextureViewRelease(canvas.view);
        canvas.view = nullptr;
    }
    if (canvas.texture != nullptr) {
        wgpuTextureRelease(canvas.texture);
        canvas.texture = nullptr;
    }
}

} // namespace

uintptr_t progpu_native_browser_imported_device(void) {
    return reinterpret_cast<uintptr_t>(emscripten_webgpu_get_device());
}

uintptr_t progpu_native_browser_device_queue(uintptr_t device) {
    if (device == 0U) {
        return 0U;
    }
    return reinterpret_cast<uintptr_t>(wgpuDeviceGetQueue(reinterpret_cast<WGPUDevice>(device)));
}

int progpu_native_browser_canvas_create(
    uintptr_t device,
    const char* selector,
    uint32_t native_format,
    progpu_native_browser_canvas** canvas) {
    if (canvas == nullptr) {
        return 0;
    }
    *canvas = nullptr;
    if (device == 0U || selector == nullptr || selector[0] == '\0') {
        return 0;
    }
    WGPUTextureFormat format = WGPUTextureFormat_Undefined;
    if (native_format == PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM) {
        format = WGPUTextureFormat_RGBA8Unorm;
    } else if (native_format == PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM) {
        format = WGPUTextureFormat_BGRA8Unorm;
    } else {
        return 0;
    }
    auto* created = new (std::nothrow) progpu_native_browser_canvas{};
    if (created == nullptr) {
        return 0;
    }
    WGPUInstanceDescriptor instance_descriptor = WGPU_INSTANCE_DESCRIPTOR_INIT;
    created->instance = wgpuCreateInstance(&instance_descriptor);
    if (created->instance != nullptr) {
        WGPUEmscriptenSurfaceSourceCanvasHTMLSelector source =
            WGPU_EMSCRIPTEN_SURFACE_SOURCE_CANVAS_HTML_SELECTOR_INIT;
        source.selector = {selector, WGPU_STRLEN};
        WGPUSurfaceDescriptor descriptor = WGPU_SURFACE_DESCRIPTOR_INIT;
        descriptor.nextInChain = &source.chain;
        created->surface = wgpuInstanceCreateSurface(created->instance, &descriptor);
    }
    if (created->surface == nullptr) {
        progpu_native_browser_canvas_destroy(created);
        return 0;
    }
    created->device = reinterpret_cast<WGPUDevice>(device);
    wgpuDeviceAddRef(created->device);
    created->format = format;
    *canvas = created;
    return 1;
}

void progpu_native_browser_canvas_destroy(progpu_native_browser_canvas* canvas) {
    if (canvas == nullptr) {
        return;
    }
    release_frame(*canvas);
    if (canvas->surface != nullptr) {
        if (canvas->width != 0U) {
            wgpuSurfaceUnconfigure(canvas->surface);
        }
        wgpuSurfaceRelease(canvas->surface);
    }
    if (canvas->device != nullptr) {
        wgpuDeviceRelease(canvas->device);
    }
    if (canvas->instance != nullptr) {
        wgpuInstanceRelease(canvas->instance);
    }
    delete canvas;
}

int progpu_native_browser_canvas_begin_frame(
    progpu_native_browser_canvas* canvas,
    uint32_t width,
    uint32_t height,
    uintptr_t* view) {
    if (view != nullptr) {
        *view = 0U;
    }
    if (canvas == nullptr || view == nullptr || width == 0U || height == 0U ||
        canvas->texture != nullptr) {
        return 0;
    }
    if (canvas->width != width || canvas->height != height) {
        WGPUSurfaceConfiguration configuration = WGPU_SURFACE_CONFIGURATION_INIT;
        configuration.device = canvas->device;
        configuration.format = canvas->format;
        configuration.usage = WGPUTextureUsage_RenderAttachment;
        configuration.width = width;
        configuration.height = height;
        configuration.alphaMode = WGPUCompositeAlphaMode_Premultiplied;
        configuration.presentMode = WGPUPresentMode_Fifo;
        wgpuSurfaceConfigure(canvas->surface, &configuration);
        canvas->width = width;
        canvas->height = height;
    }
    WGPUSurfaceTexture surface_texture = WGPU_SURFACE_TEXTURE_INIT;
    wgpuSurfaceGetCurrentTexture(canvas->surface, &surface_texture);
    if ((surface_texture.status != WGPUSurfaceGetCurrentTextureStatus_SuccessOptimal &&
            surface_texture.status != WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal) ||
        surface_texture.texture == nullptr) {
        if (surface_texture.texture != nullptr) {
            wgpuTextureRelease(surface_texture.texture);
        }
        return 0;
    }
    canvas->texture = surface_texture.texture;
    canvas->view = wgpuTextureCreateView(canvas->texture, nullptr);
    if (canvas->view == nullptr) {
        release_frame(*canvas);
        return 0;
    }
    *view = reinterpret_cast<uintptr_t>(canvas->view);
    return 1;
}

void progpu_native_browser_canvas_end_frame(progpu_native_browser_canvas* canvas) {
    if (canvas != nullptr) {
        // The port shows the current texture when the task returns: wgpuSurfacePresent is not
        // supported there.
        release_frame(*canvas);
    }
}
