#pragma once

#include "progpu_native_hinted_font.hpp"

#include <array>
#include <mutex>

namespace progpu::native::text {

struct hinted_font_cache_statistics final {
    std::uint64_t face_creations = 0U;
    std::uint64_t glyph_captures = 0U;
    std::uint64_t batch_hits = 0U;
    std::uint32_t live_entries = 0U;
};

// Context-scoped, bounded FIFO replacement. One latest original-order batch per
// exact source/configuration. A returned generation owns everything it needs;
// replacement/disposal never invalidates a source layout's retained snapshot.
class hinted_font_cache final {
public:
    static constexpr std::size_t capacity = 16U;
    hinted_font_cache() = default;
    hinted_font_cache(const hinted_font_cache&) = delete;
    hinted_font_cache& operator=(const hinted_font_cache&) = delete;

    bool try_capture(std::shared_ptr<const owned_font_source> source,
        const hinted_font_configuration& configuration,
        std::span<const std::uint32_t> glyph_indices,
        std::shared_ptr<const hinted_glyph_batch>& result,
        hinted_font_error& error) noexcept;

    hinted_font_cache_statistics statistics();

private:
    struct entry final {
        std::unique_ptr<hinted_font> font{};
        std::shared_ptr<const hinted_glyph_batch> batch{};
        std::vector<std::uint32_t> glyph_indices{};
    };
    std::mutex mutex_{};
    std::array<entry, capacity> entries_{};
    std::size_t cursor_ = 0U;
    hinted_font_cache_statistics statistics_{};
};

} // namespace progpu::native::text
