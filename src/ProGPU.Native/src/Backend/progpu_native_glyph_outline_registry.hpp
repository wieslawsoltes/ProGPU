#pragma once

#include "progpu_native.h"
#include "../Text/Font/progpu_native_binary_glyph_contract.hpp"

#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <span>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace progpu::native {

// The glyph outlines an engine knows by key. A frame names an outline with a
// few words; the bytes are here, validated once at registration.
//
// A key resolves to a serial: a number counted from one that is never given a
// second time. A face number that was released is refused afterwards, so a key
// names the same content for as long as the registry lives. Whatever keeps
// work by serial (the glyph atlas, a compiled glyph page) therefore never sees
// other content behind a serial it holds; at worst the serial is gone.
class glyph_outline_registry {
public:
    struct entry {
        float min_x = 0.0F;
        float min_y = 0.0F;
        float max_x = 0.0F;
        float max_y = 0.0F;
        std::vector<progpu_native_path_segment> segments;
    };

    enum class result {
        registered,
        invalid_outline,
        released_face,
        other_content,
    };

    // The serial of a key, or zero when the registry does not hold it.
    std::uint64_t find(const progpu_native_glyph_outline_key& key) const noexcept {
        if (key.face != last_face_ || last_glyphs_ == nullptr) {
            const auto face = faces_.find(key.face);
            if (face == faces_.end()) {
                return 0U;
            }
            last_face_ = key.face;
            last_glyphs_ = &face->second;
        }
        const auto glyph = last_glyphs_->find(glyph_word(key));
        return glyph == last_glyphs_->end() ? 0U : glyph->second;
    }

    // The outline of a serial, or null after its face was released.
    const entry* get(std::uint64_t serial) const noexcept {
        return serial == 0U || serial > entries_.size()
            ? nullptr
            : entries_[static_cast<std::size_t>(serial - 1U)].get();
    }

    // Registers all outlines of the call or none. May throw std::bad_alloc;
    // the registry is unchanged then.
    result add(
        std::span<const progpu_native_registered_glyph_outline> outlines,
        std::span<const progpu_native_path_segment> segments) {
        // First pass: every outline is valid and none contradicts the registry
        // or an earlier outline of this call.
        std::vector<std::unique_ptr<entry>> created;
        std::vector<std::size_t> created_sources;
        std::unordered_map<std::uint64_t, std::unordered_map<std::uint64_t, std::size_t>> pending;
        created.reserve(outlines.size());
        created_sources.reserve(outlines.size());
        for (std::size_t index = 0U; index < outlines.size(); ++index) {
            const auto& outline = outlines[index];
            if (outline.segment_count == 0U ||
                outline.segment_offset > segments.size() ||
                outline.segment_count > segments.size() - outline.segment_offset ||
                !std::isfinite(outline.min_x) || !std::isfinite(outline.min_y) ||
                !std::isfinite(outline.max_x) || !std::isfinite(outline.max_y) ||
                outline.max_x <= outline.min_x || outline.max_y <= outline.min_y) {
                return result::invalid_outline;
            }
            const auto selected = segments.subspan(
                static_cast<std::size_t>(outline.segment_offset),
                static_cast<std::size_t>(outline.segment_count));
            for (const auto& segment : selected) {
                if (segment.pad0 != 0U || !text::valid_glyph_scan_segment(segment)) {
                    return result::invalid_outline;
                }
            }
            if (released_faces_.contains(outline.key.face)) {
                return result::released_face;
            }
            const entry* existing = get(find(outline.key));
            if (existing == nullptr) {
                const auto face = pending.find(outline.key.face);
                if (face != pending.end()) {
                    const auto glyph = face->second.find(glyph_word(outline.key));
                    if (glyph != face->second.end()) {
                        existing = created[glyph->second].get();
                    }
                }
            }
            if (existing != nullptr) {
                if (!same_content(*existing, outline, selected)) {
                    return result::other_content;
                }
                continue;
            }
            auto value = std::make_unique<entry>();
            value->min_x = outline.min_x;
            value->min_y = outline.min_y;
            value->max_x = outline.max_x;
            value->max_y = outline.max_y;
            value->segments.assign(selected.begin(), selected.end());
            pending[outline.key.face].emplace(glyph_word(outline.key), created.size());
            created.push_back(std::move(value));
            created_sources.push_back(index);
        }
        // Second pass: everything that can throw first, undone if it does;
        // the entries move in last, without allocation.
        entries_.reserve(entries_.size() + created.size());
        const auto first_serial = static_cast<std::uint64_t>(entries_.size()) + 1U;
        std::size_t named = 0U;
        try {
            for (; named < created.size(); ++named) {
                const auto& key = outlines[created_sources[named]].key;
                faces_[key.face].emplace(glyph_word(key), first_serial + named);
            }
        } catch (...) {
            for (std::size_t index = 0U; index < named; ++index) {
                const auto& key = outlines[created_sources[index]].key;
                faces_.find(key.face)->second.erase(glyph_word(key));
            }
            last_face_ = 0U;
            last_glyphs_ = nullptr;
            throw;
        }
        for (auto& value : created) {
            entries_.push_back(std::move(value));
        }
        return result::registered;
    }

    // Forgets the outlines of one face; its number is refused afterwards.
    // May throw std::bad_alloc; the registry is unchanged then.
    void release_face(std::uint64_t face) {
        released_faces_.insert(face);
        const auto found = faces_.find(face);
        if (found == faces_.end()) {
            return;
        }
        for (const auto& [glyph, serial] : found->second) {
            entries_[static_cast<std::size_t>(serial - 1U)].reset();
        }
        faces_.erase(found);
        last_face_ = 0U;
        last_glyphs_ = nullptr;
    }

    // The outlines held now.
    std::size_t size() const noexcept {
        std::size_t count = 0U;
        for (const auto& [face, glyphs] : faces_) {
            count += glyphs.size();
        }
        return count;
    }

private:
    using glyph_map = std::unordered_map<std::uint64_t, std::uint64_t>;

    static std::uint64_t glyph_word(
        const progpu_native_glyph_outline_key& key) noexcept {
        return (static_cast<std::uint64_t>(key.variant) << 32U) | key.glyph;
    }

    static bool same_content(
        const entry& existing,
        const progpu_native_registered_glyph_outline& outline,
        std::span<const progpu_native_path_segment> selected) noexcept {
        const auto bits = [](float value) { return std::bit_cast<std::uint32_t>(value); };
        return bits(existing.min_x) == bits(outline.min_x) &&
            bits(existing.min_y) == bits(outline.min_y) &&
            bits(existing.max_x) == bits(outline.max_x) &&
            bits(existing.max_y) == bits(outline.max_y) &&
            existing.segments.size() == selected.size() &&
            std::memcmp(existing.segments.data(), selected.data(),
                selected.size_bytes()) == 0;
    }

    // Serial minus one indexes this; a released entry is null. Entries do not
    // move: a pointer from get stays valid until its face is released.
    std::vector<std::unique_ptr<entry>> entries_;
    std::unordered_map<std::uint64_t, glyph_map> faces_;
    std::unordered_set<std::uint64_t> released_faces_;
    // Runs of one face follow each other: remember the last face looked up.
    mutable std::uint64_t last_face_ = 0U;
    mutable const glyph_map* last_glyphs_ = nullptr;
};

} // namespace progpu::native
