#pragma once

#include <mutex>

namespace progpu::native::webgpu {

// wgpu-native devices share an internal process-wide resource lock graph.
// Keep complete native renderer operations in the same synchronization domain
// so submission, polling, and resource lifetime cannot form an inter-device
// lock cycle. Dawn and browser providers own independent synchronization.
#if !defined(PROGPU_NATIVE_DAWN_ABI)
inline std::recursive_mutex& process_render_mutex() noexcept {
    static std::recursive_mutex mutex;
    return mutex;
}
#endif

class process_render_scope final {
public:
    process_render_scope() noexcept
#if !defined(PROGPU_NATIVE_DAWN_ABI)
        : lock_(process_render_mutex())
#endif
    {
    }

    process_render_scope(const process_render_scope&) = delete;
    process_render_scope& operator=(const process_render_scope&) = delete;

private:
#if !defined(PROGPU_NATIVE_DAWN_ABI)
    std::unique_lock<std::recursive_mutex> lock_;
#endif
};

} // namespace progpu::native::webgpu
