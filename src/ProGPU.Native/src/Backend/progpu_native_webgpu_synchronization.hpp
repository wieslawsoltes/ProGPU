#pragma once

#include <cstdint>
#include <mutex>

namespace progpu::native::webgpu {

enum class submission_retirement_action : std::uint8_t {
    none,
    poll,
    wait
};

// Keep native queue retirement aligned with the managed backend: poll exact
// submission tokens periodically, but force a bounded drain when the producer
// outruns the GPU. Exact-token completion retires every earlier queue item.
class submission_retirement_tracker final {
public:
    static constexpr std::uint64_t poll_interval = 8U;
    static constexpr std::uint64_t maximum_deferred_submissions = 64U;

    [[nodiscard]] submission_retirement_action on_submission(
        std::uint64_t submitted_count) noexcept {
        if (submitted_count - retired_count_ >=
            maximum_deferred_submissions) {
            polled_count_ = submitted_count;
            return submission_retirement_action::wait;
        }
        if (submitted_count - polled_count_ >= poll_interval) {
            polled_count_ = submitted_count;
            return submission_retirement_action::poll;
        }
        return submission_retirement_action::none;
    }

    void observe_latest_completion(
        std::uint64_t submitted_count) noexcept {
        retired_count_ = submitted_count;
        polled_count_ = submitted_count;
    }

    [[nodiscard]] std::uint64_t retired_count() const noexcept {
        return retired_count_;
    }

    [[nodiscard]] std::uint64_t polled_count() const noexcept {
        return polled_count_;
    }

private:
    std::uint64_t retired_count_ = 0U;
    std::uint64_t polled_count_ = 0U;
};

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
