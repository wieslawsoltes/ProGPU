#pragma once

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace progpu::native::diagnostics {

// Internal, header-compatible diagnostic value; no public C ABI or GPU ownership.
// The immutable opt-in is read once per provider, not per dispatch/frame.
inline bool compute_trace_requested() noexcept {
    static const bool enabled = [] {
#if defined(_WIN32)
        char* value = nullptr;
        std::size_t length = 0U;
        if (_dupenv_s(&value, &length, "PROGPU_NATIVE_TRACE_COMPUTE") != 0) {
            return false;
        }
#else
        const char* value = std::getenv("PROGPU_NATIVE_TRACE_COMPUTE");
#endif
        const bool result = value != nullptr && std::strcmp(value, "1") == 0;
#if defined(_WIN32)
        std::free(value);
#endif
        return result;
    }();
    return enabled;
}

class compute_trace final {
public:
    static constexpr std::uint32_t event_limit = 4096U;

    explicit compute_trace(bool enabled = compute_trace_requested(),
                           std::FILE* output = stderr) noexcept
        : enabled_(enabled), output_(output) {}

    bool enabled() const noexcept { return enabled_; }

    void record(const void* engine, std::uint64_t scene,
                std::uint64_t generation, std::uint64_t submissions,
                const char* event, const char* pipeline, std::uint32_t slot,
                std::uint32_t x, std::uint32_t y, std::uint32_t z) noexcept {
        if (!enabled_ || output_ == nullptr || events_ > event_limit) return;
        if (events_++ == event_limit) {
            std::fprintf(output_,
                "ProGPU native compute trace: engine=%p, truncated=1, limit=%u\n",
                engine, event_limit);
            return;
        }
        std::fprintf(output_,
            "ProGPU native compute trace: engine=%p, scene=%llu, generation=%llu, "
            "submissions=%llu, event=%s, pipeline=%s, slot=%u, groups=%u/%u/%u\n",
            engine, static_cast<unsigned long long>(scene),
            static_cast<unsigned long long>(generation),
            static_cast<unsigned long long>(submissions), event, pipeline,
            slot, x, y, z);
    }

private:
    const bool enabled_;
    std::FILE* const output_; // Borrowed diagnostic sink; never owned or closed.
    std::uint32_t events_ = 0U;
};

} // namespace progpu::native::diagnostics
