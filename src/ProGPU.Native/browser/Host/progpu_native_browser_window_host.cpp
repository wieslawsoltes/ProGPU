#include "progpu_native_browser_window_host.hpp"

#include "progpu_native.h"

#include <emscripten.h>

#include <algorithm>
#include <cmath>

namespace progpu::native::browser {

browser_window_host::~browser_window_host() {
    if (surface_ != nullptr) {
        wgpuSurfaceUnconfigure(surface_);
        wgpuSurfaceRelease(surface_);
    }
    if (instance_ != nullptr) {
        wgpuInstanceRelease(instance_);
    }
}

bool browser_window_host::initialize(
    WGPUDevice device,
    const char* selector) noexcept {
    if (device == nullptr || selector == nullptr || selector[0] == '\0') {
        return false;
    }
    WGPUInstanceDescriptor instance_descriptor = WGPU_INSTANCE_DESCRIPTOR_INIT;
    instance_ = wgpuCreateInstance(&instance_descriptor);
    if (instance_ == nullptr) {
        return false;
    }
    WGPUEmscriptenSurfaceSourceCanvasHTMLSelector source =
        WGPU_EMSCRIPTEN_SURFACE_SOURCE_CANVAS_HTML_SELECTOR_INIT;
    source.selector = {selector, WGPU_STRLEN};
    WGPUSurfaceDescriptor surface_descriptor = WGPU_SURFACE_DESCRIPTOR_INIT;
    surface_descriptor.nextInChain = &source.chain;
    surface_ = wgpuInstanceCreateSurface(instance_, &surface_descriptor);
    if (surface_ == nullptr) {
        return false;
    }
    device_ = device;
    const std::uint32_t preferred = static_cast<std::uint32_t>(EM_ASM_INT({
        return Module.progpuBrowserCanvasFormat === 'rgba8unorm' ? 1 : 2;
    }));
    if (preferred == 1U) {
        format_ = WGPUTextureFormat_RGBA8Unorm;
        native_format_ = PROGPU_NATIVE_TEXTURE_FORMAT_RGBA8_UNORM;
    } else {
        format_ = WGPUTextureFormat_BGRA8Unorm;
        native_format_ = PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    }
    return configure_if_needed();
}

bool browser_window_host::begin_frame(browser_frame& frame) noexcept {
    frame = {};
    if (!configure_if_needed()) {
        return false;
    }
    WGPUSurfaceTexture surface_texture = WGPU_SURFACE_TEXTURE_INIT;
    wgpuSurfaceGetCurrentTexture(surface_, &surface_texture);
    if ((surface_texture.status !=
            WGPUSurfaceGetCurrentTextureStatus_SuccessOptimal &&
         surface_texture.status !=
            WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal) ||
        surface_texture.texture == nullptr) {
        if (surface_texture.texture != nullptr) {
            wgpuTextureRelease(surface_texture.texture);
        }
        return false;
    }
    WGPUTextureViewDescriptor view_descriptor =
        WGPU_TEXTURE_VIEW_DESCRIPTOR_INIT;
    view_descriptor.format = format_;
    view_descriptor.dimension = WGPUTextureViewDimension_2D;
    view_descriptor.mipLevelCount = 1U;
    view_descriptor.arrayLayerCount = 1U;
    view_descriptor.aspect = WGPUTextureAspect_All;
    frame.view = wgpuTextureCreateView(
        surface_texture.texture,
        &view_descriptor);
    if (frame.view == nullptr) {
        wgpuTextureRelease(surface_texture.texture);
        return false;
    }
    frame.texture = surface_texture.texture;
    frame.width = width_;
    frame.height = height_;
    frame.dpi_scale = dpi_scale_;
    frame.logical_width = logical_width_;
    frame.logical_height = logical_height_;
    return true;
}

void browser_window_host::end_frame(browser_frame& frame) noexcept {
    if (frame.view != nullptr) {
        wgpuTextureViewRelease(frame.view);
    }
    if (frame.texture != nullptr) {
        wgpuTextureRelease(frame.texture);
    }
    frame = {};
    // Emdawnwebgpu presents the current canvas texture when the active
    // requestAnimationFrame callback returns. wgpuSurfacePresent intentionally
    // is not called because that function is unsupported by this browser port.
}

WGPUTextureFormat browser_window_host::format() const noexcept {
    return format_;
}

std::uint32_t browser_window_host::native_format() const noexcept {
    return native_format_;
}

bool browser_window_host::configure_if_needed() noexcept {
    if (surface_ == nullptr || device_ == nullptr) {
        return false;
    }
    const double raw_scale = EM_ASM_DOUBLE({
        return Number(Module.progpuBrowserMetrics?.scale || 1);
    });
    const auto width = static_cast<std::uint32_t>(std::max(
        1,
        EM_ASM_INT({ return Module.progpuBrowserMetrics?.width || 1; })));
    const auto height = static_cast<std::uint32_t>(std::max(
        1,
        EM_ASM_INT({ return Module.progpuBrowserMetrics?.height || 1; })));
    logical_width_ = static_cast<float>(EM_ASM_DOUBLE({
        return Number(Module.progpuBrowserMetrics?.logicalWidth || 1);
    }));
    logical_height_ = static_cast<float>(EM_ASM_DOUBLE({
        return Number(Module.progpuBrowserMetrics?.logicalHeight || 1);
    }));
    dpi_scale_ = static_cast<float>(std::clamp(raw_scale, 1.0, 4.0));
    if (width == width_ && height == height_) {
        return true;
    }
    width_ = width;
    height_ = height;
    WGPUSurfaceConfiguration configuration =
        WGPU_SURFACE_CONFIGURATION_INIT;
    configuration.device = device_;
    configuration.format = format_;
    configuration.usage = WGPUTextureUsage_RenderAttachment;
    configuration.width = width_;
    configuration.height = height_;
    configuration.alphaMode = WGPUCompositeAlphaMode_Premultiplied;
    configuration.presentMode = WGPUPresentMode_Fifo;
    wgpuSurfaceConfigure(surface_, &configuration);
    return true;
}

} // namespace progpu::native::browser
