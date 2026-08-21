#pragma once

#include "webgpu.h"

#include <cstdint>

namespace progpu::native::browser {

struct browser_frame final {
    WGPUTexture texture = nullptr;
    WGPUTextureView view = nullptr;
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    float dpi_scale = 1.0F;
    float logical_width = 0.0F;
    float logical_height = 0.0F;
};

/*
 * Native counterpart to ProGPU.Browser.BrowserWindowHost. It consumes the
 * same shared JavaScript canvas/device contract, owns a WebGPU surface, and
 * exposes one physical-pixel swapchain view per requestAnimationFrame.
 */
class browser_window_host final {
public:
    browser_window_host() = default;
    ~browser_window_host();

    browser_window_host(const browser_window_host&) = delete;
    browser_window_host& operator=(const browser_window_host&) = delete;

    bool initialize(WGPUDevice device, const char* selector) noexcept;
    bool begin_frame(browser_frame& frame) noexcept;
    void end_frame(browser_frame& frame) noexcept;

    WGPUTextureFormat format() const noexcept;
    std::uint32_t native_format() const noexcept;

private:
    bool configure_if_needed() noexcept;

    WGPUInstance instance_ = nullptr;
    WGPUSurface surface_ = nullptr;
    WGPUDevice device_ = nullptr;
    WGPUTextureFormat format_ = WGPUTextureFormat_Undefined;
    std::uint32_t native_format_ = 0U;
    std::uint32_t width_ = 0U;
    std::uint32_t height_ = 0U;
    float dpi_scale_ = 1.0F;
    float logical_width_ = 0.0F;
    float logical_height_ = 0.0F;
};

} // namespace progpu::native::browser
