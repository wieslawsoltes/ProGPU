#include "progpu_native_hinted_font_cache.hpp"

#include <cstring>
#include <limits>
#include <new>
#include <stdexcept>
#include <type_traits>
#include <utility>

namespace progpu::native::text {
namespace {
void increment(std::uint64_t& value) noexcept
{
    if (value != std::numeric_limits<std::uint64_t>::max()) ++value;
}

template<class T>
bool equal_values(std::span<const T> a, std::span<const T> b) noexcept
{
    // Original owned uint32/int32 cache keys use exact bulk equality, including
    // source order and repetitions. No hash-only or per-glyph managed crossing.
    return a.size() == b.size() && (a.empty() || std::memcmp(a.data(), b.data(), a.size_bytes()) == 0);
}

bool matches(const hinted_font_identity& identity,
    const std::shared_ptr<const owned_font_source>& source,
    const hinted_font_configuration& configuration) noexcept
{
    // The shared source has const bytes and original face index. Pointer equality
    // is owned immutable identity, never a borrowed mutable-font/cache address.
    return identity.source == source &&
        identity.x_pixels_per_em_26_6 == configuration.x_pixels_per_em_26_6 &&
        identity.y_pixels_per_em_26_6 == configuration.y_pixels_per_em_26_6 &&
        identity.policy == configuration.policy &&
        identity.x_phase_26_6 == configuration.x_phase_26_6 &&
        identity.y_phase_26_6 == configuration.y_phase_26_6 &&
        equal_values<std::int32_t>(identity.variation_coordinates_16_16, configuration.variation_coordinates_16_16);
}
} // namespace

bool hinted_font_cache::try_capture(std::shared_ptr<const owned_font_source> source,
    const hinted_font_configuration& configuration, std::span<const std::uint32_t> glyph_indices,
    std::shared_ptr<const hinted_glyph_batch>& result, hinted_font_error& error) noexcept
{
    try {
        const std::lock_guard lock(mutex_);
        for (auto& cached : entries_) {
            if (cached.batch == nullptr || !matches(*cached.batch->identity, source, configuration)) continue;
            if (equal_values<std::uint32_t>(cached.glyph_indices, glyph_indices)) {
                result = cached.batch;
                increment(statistics_.batch_hits);
                error = hinted_font_error::none;
                return true;
            }
            // Allocate/capture before replacing either retained key or output.
            std::vector<std::uint32_t> owned_indices(glyph_indices.begin(), glyph_indices.end());
            std::shared_ptr<const hinted_glyph_batch> candidate;
            increment(statistics_.glyph_captures);
            if (!cached.font->try_capture(glyph_indices, candidate, error)) return false;
            cached.glyph_indices.swap(owned_indices);
            cached.batch = std::move(candidate);
            result = cached.batch;
            error = hinted_font_error::none;
            return true;
        }
        entry candidate;
        static_assert(std::is_nothrow_move_assignable_v<entry>);
        if (!hinted_font::try_create(std::move(source), configuration, candidate.font, error)) return false;
        increment(statistics_.face_creations);
        candidate.glyph_indices.assign(glyph_indices.begin(), glyph_indices.end());
        increment(statistics_.glyph_captures);
        if (!candidate.font->try_capture(glyph_indices, candidate.batch, error)) return false;
        // The old slot and caller's previous exact generation survive every
        // earlier creation/allocation/glyph failure. Publication below is no-throw.
        auto& destination = entries_[cursor_];
        if (destination.font == nullptr) ++statistics_.live_entries;
        destination = std::move(candidate);
        cursor_ = (cursor_ + 1U) % capacity;
        result = destination.batch;
        error = hinted_font_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        error = hinted_font_error::resource_exhausted;
    } catch (const std::length_error&) {
        error = hinted_font_error::resource_exhausted;
    } catch (...) {
        error = hinted_font_error::hinting_failed;
    }
    return false;
}

hinted_font_cache_statistics hinted_font_cache::statistics()
{
    const std::lock_guard lock(mutex_);
    return statistics_;
}

} // namespace progpu::native::text
