#pragma once

#include "progpu_native_draw_state.hpp"

#include <algorithm>
#include <array>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <span>
#include <unordered_map>
#include <vector>

namespace progpu::native {

// The caller has validated the complete outline and segment arena. Borrow only
// its selected segment bytes during this synchronous raster rebuild. The arena
// offset locates bytes; it is not coverage identity. A borrowed key does not
// survive the call: the atlas cache below owns a copy of the bytes it keeps.
struct glyph_raster_identity {
    std::array<std::uint32_t, 6U> raster_bits{};
    std::span<const std::byte> segments;

    bool operator==(const glyph_raster_identity& other) const noexcept {
        return raster_bits == other.raster_bits &&
            segments.size() == other.segments.size() &&
            (segments.empty() || std::memcmp(segments.data(), other.segments.data(),
                segments.size()) == 0);
    }
};

inline glyph_raster_identity make_glyph_raster_identity(
    const progpu_native_glyph_outline& outline,
    std::span<const progpu_native_path_segment> selected_segments) noexcept {
    return {{std::bit_cast<std::uint32_t>(outline.min_x),
        std::bit_cast<std::uint32_t>(outline.min_y),
        std::bit_cast<std::uint32_t>(outline.max_x),
        std::bit_cast<std::uint32_t>(outline.max_y),
        std::bit_cast<std::uint32_t>(outline.raster_scale),
        std::bit_cast<std::uint32_t>(outline.subpixel_x)},
        std::as_bytes(selected_segments)};
}

struct glyph_raster_identity_hash {
    std::size_t operator()(const glyph_raster_identity& value) const noexcept {
        auto hash = append_content_hash64(14695981039346656037ULL,
            value.raster_bits.data(), sizeof(value.raster_bits));
        const std::uint64_t size = value.segments.size();
        hash = append_content_hash64(hash, &size, sizeof(size));
        return static_cast<std::size_t>(append_content_hash64(
            hash, value.segments.data(), value.segments.size()));
    }
};

// An identity with its hash, so one pass over the bytes serves both the map of
// a raster rebuild and the atlas cache.
struct hashed_glyph_raster_identity {
    glyph_raster_identity identity;
    std::uint64_t hash = 0U;

    bool operator==(const hashed_glyph_raster_identity& other) const noexcept {
        return hash == other.hash && identity == other.identity;
    }
};

inline hashed_glyph_raster_identity make_hashed_glyph_raster_identity(
    const glyph_raster_identity& identity) noexcept {
    return {identity, glyph_raster_identity_hash{}(identity)};
}

struct hashed_glyph_raster_identity_hash {
    std::size_t operator()(
        const hashed_glyph_raster_identity& value) const noexcept {
        return static_cast<std::size_t>(value.hash);
    }
};

// The shelf packing of the coverage atlas: tiles in rows, two texels apart.
struct glyph_atlas_cursor {
    std::uint32_t x = 2U;
    std::uint32_t y = 2U;
    std::uint32_t row_height = 0U;
};

// Places one tile at the cursor. `required_size` is the atlas edge the placed
// tiles need so far; it doubles up to `maximum_size`. False when the tile does
// not fit the atlas at its largest size (the cursor is then unspecified).
inline bool place_glyph_atlas_tile(
    glyph_atlas_cursor& cursor,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t maximum_size,
    std::uint32_t& required_size,
    std::uint32_t& atlas_x,
    std::uint32_t& atlas_y) noexcept {
    while (width + 4U > required_size && required_size < maximum_size) {
        required_size *= 2U;
    }
    if (cursor.x + width + 2U > required_size) {
        cursor.x = 2U;
        cursor.y += cursor.row_height + 2U;
        cursor.row_height = 0U;
    }
    while (cursor.y + height + 2U > required_size &&
           required_size < maximum_size) {
        required_size *= 2U;
    }
    if (cursor.y + height + 2U > required_size) {
        return false;
    }
    atlas_x = cursor.x;
    atlas_y = cursor.y;
    cursor.x += width + 2U;
    cursor.row_height = std::max(cursor.row_height, height);
    return true;
}

// One tile of the atlas: where the coverage of an outline is, and the physical
// origin of its raster frame.
struct glyph_atlas_tile {
    std::uint32_t atlas_x;
    std::uint32_t atlas_y;
    std::uint32_t width;
    std::uint32_t height;
    float x_start;
    float y_start;
};

// The raster identity of an outline registered with the engine: its serial in
// the registry, the raster scale and the subpixel phase. A serial names one
// content for the engine's lifetime, so no outline byte is read to compare.
struct glyph_atlas_key {
    std::uint64_t serial = 0U;
    std::uint32_t raster_scale_bits = 0U;
    std::uint32_t subpixel_bits = 0U;

    bool operator==(const glyph_atlas_key&) const noexcept = default;
};

struct glyph_atlas_key_hash {
    std::size_t operator()(const glyph_atlas_key& value) const noexcept {
        std::uint64_t hash = value.serial * 0x9E3779B97F4A7C15ULL;
        hash ^= ((static_cast<std::uint64_t>(value.raster_scale_bits) << 32U) |
            value.subpixel_bits) * 0xC2B2AE3D27D4EB4FULL;
        return static_cast<std::size_t>(hash ^ (hash >> 29U));
    }
};

// The coverage the atlas keeps across raster rebuilds, by exact raster
// identity: the outline bytes, the bounds, the scale and the subpixel phase,
// or, for an outline registered with the engine, its glyph_atlas_key.
// An outline found here is not rasterized again and keeps its tile, so a new
// outline never moves or re-rasterizes the others. The cache owns its keys.
// It holds what was encoded for the atlas texture: whoever abandons such an
// encoding, or replaces the texture without its content, clears the cache.
class glyph_atlas_cache {
public:
    const glyph_atlas_tile* find(const glyph_atlas_key& key) const noexcept {
        const auto entry = keyed_.find(key);
        return entry == keyed_.end() ? nullptr : &entry->second;
    }

    // May throw std::bad_alloc; the cache is unchanged then.
    void insert(const glyph_atlas_key& key, const glyph_atlas_tile& tile) {
        keyed_.insert_or_assign(key, tile);
    }

    const glyph_atlas_tile* find(
        std::uint64_t hash,
        const glyph_raster_identity& identity) const noexcept {
        const auto range = entries_.equal_range(hash);
        for (auto entry = range.first; entry != range.second; ++entry) {
            const auto& value = entry->second;
            if (value.raster_bits == identity.raster_bits &&
                value.segments.size() == identity.segments.size() &&
                (value.segments.empty() || std::memcmp(
                    value.segments.data(),
                    identity.segments.data(),
                    value.segments.size()) == 0)) {
                return &value.tile;
            }
        }
        return nullptr;
    }

    // May throw std::bad_alloc; the cache is unchanged then.
    void insert(
        std::uint64_t hash,
        const glyph_raster_identity& identity,
        const glyph_atlas_tile& tile) {
        entries_.emplace(hash, entry{
            identity.raster_bits,
            std::vector<std::byte>(
                identity.segments.begin(), identity.segments.end()),
            tile});
    }

    void clear() noexcept {
        entries_.clear();
        keyed_.clear();
        cursor_ = {};
    }

    bool empty() const noexcept { return entries_.empty() && keyed_.empty(); }
    std::size_t size() const noexcept { return entries_.size() + keyed_.size(); }
    const glyph_atlas_cursor& cursor() const noexcept { return cursor_; }
    void set_cursor(const glyph_atlas_cursor& cursor) noexcept {
        cursor_ = cursor;
    }

private:
    struct entry {
        std::array<std::uint32_t, 6U> raster_bits;
        std::vector<std::byte> segments;
        glyph_atlas_tile tile;
    };
    std::unordered_multimap<std::uint64_t, entry> entries_;
    std::unordered_map<glyph_atlas_key, glyph_atlas_tile, glyph_atlas_key_hash> keyed_;
    glyph_atlas_cursor cursor_{};
};

} // namespace progpu::native
