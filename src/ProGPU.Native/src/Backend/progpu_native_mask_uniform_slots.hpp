#pragma once

// Uniform slots for the analytic masks of clip spans.
//
// A span that draws under an analytic mask (a rounded rectangle or an axis
// clip area) needs the mask's uniforms in a bind group. Making a buffer, a
// queue write and a bind group for every such span of every compile costs far
// more than the 96 bytes they carry: a scene with some hundred masked spans
// that is compiled each frame pays some hundred buffers, staging copies and
// bind groups each frame, and their release on top.
//
// So the uniforms live in pages. A page is one uniform buffer of
// slots_per_page slots, each slot_stride bytes apart, and every slot has one
// bind group that binds the page at the slot's offset and is made once. A
// span takes a slot, the bytes are staged here, and all staged bytes of a
// page reach the buffer in one write before the next submission.
//
// A slot that was given back is not handed out again before recycle() is
// called, which the renderer does when a frame begins: commands that were
// encoded with the slot's bind group but not submitted yet must still find
// the bytes they were encoded for.
//
// Nothing here knows the graphics interface; the table only keeps the books
// and the staged bytes, which keeps its rules testable without a device.

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <new>
#include <stdexcept>
#include <vector>

namespace progpu::native::mask_slots {

inline constexpr std::uint32_t no_slot =
    std::numeric_limits<std::uint32_t>::max();
// The largest minimum offset alignment of uniform bindings a device may ask
// for, so an offset that is a multiple of it is valid on every device.
inline constexpr std::uint32_t slot_stride = 256U;
inline constexpr std::uint32_t slots_per_page = 256U;
inline constexpr std::uint32_t page_bytes = slot_stride * slots_per_page;

inline constexpr std::uint32_t page_of(std::uint32_t slot) noexcept {
    return slot / slots_per_page;
}

inline constexpr std::uint32_t offset_of(std::uint32_t slot) noexcept {
    return (slot % slots_per_page) * slot_stride;
}

class slot_table {
public:
    // Takes a slot and stages size bytes for it; the rest of the slot is
    // zero. no_slot when the bytes do not fit a slot or memory ran out. A new
    // page begins when no slot is free: the caller sees it by page_count().
    std::uint32_t take(const void* bytes, std::size_t size) noexcept {
        if (bytes == nullptr || size == 0U || size > slot_stride) {
            return no_slot;
        }
        if (free_.empty() && !add_page()) {
            return no_slot;
        }
        const std::uint32_t slot = free_.back();
        free_.pop_back();
        state_[slot] = state::taken;
        ++taken_;
        auto* staged = staged_.data() +
            static_cast<std::size_t>(slot) * slot_stride;
        std::memcpy(staged, bytes, size);
        std::memset(staged + size, 0, slot_stride - size);
        auto& range = dirty_[page_of(slot)];
        const std::uint32_t local = slot % slots_per_page;
        range.first = std::min(range.first, local);
        range.end = std::max(range.end, local + 1U);
        return slot;
    }

    // Gives a slot back. It stays out of use until recycle(). A slot that is
    // not taken is left alone, so a second give does no harm.
    void give(std::uint32_t slot) noexcept {
        if (slot >= state_.size() || state_[slot] != state::taken) {
            return;
        }
        state_[slot] = state::given;
        --taken_;
        // Room for every slot was reserved when its page was added.
        given_.push_back(slot);
    }

    // Makes the slots given back so far free again. The lowest slot is
    // handed out first, so the slots in use stay close together and a page
    // is written as one short range.
    void recycle() noexcept {
        if (given_.empty()) {
            return;
        }
        for (const std::uint32_t slot : given_) {
            state_[slot] = state::free;
            free_.push_back(slot);
        }
        given_.clear();
        std::sort(
            free_.begin(), free_.end(),
            [](std::uint32_t first, std::uint32_t second) noexcept {
                return first > second;
            });
    }

    // Hands every page that has staged bytes to sink(page, offset, bytes,
    // size), offset and size in bytes of the page, and forgets the ranges.
    template<typename Sink>
    void flush(Sink&& sink) {
        if (!pending()) {
            return;
        }
        for (std::size_t page = 0U; page < dirty_.size(); ++page) {
            auto& range = dirty_[page];
            if (range.end <= range.first) {
                continue;
            }
            const std::size_t offset =
                static_cast<std::size_t>(range.first) * slot_stride;
            const std::size_t size =
                static_cast<std::size_t>(range.end - range.first) *
                slot_stride;
            sink(
                static_cast<std::uint32_t>(page),
                static_cast<std::uint32_t>(offset),
                staged_.data() + page * page_bytes + offset,
                static_cast<std::uint32_t>(size));
            range = {};
        }
    }

    bool pending() const noexcept {
        for (const auto& range : dirty_) {
            if (range.end > range.first) {
                return true;
            }
        }
        return false;
    }

    std::uint32_t page_count() const noexcept {
        return static_cast<std::uint32_t>(dirty_.size());
    }

    std::uint32_t slot_count() const noexcept {
        return static_cast<std::uint32_t>(state_.size());
    }

    std::uint32_t taken() const noexcept {
        return taken_;
    }

    // Forgets every slot and page. Only for a table none of whose slots is
    // held any more.
    void clear() noexcept {
        staged_.clear();
        state_.clear();
        free_.clear();
        given_.clear();
        dirty_.clear();
        taken_ = 0U;
    }

private:
    enum class state : std::uint8_t {
        free,
        taken,
        given,
    };

    struct dirty_range {
        std::uint32_t first = slots_per_page;
        std::uint32_t end = 0U;
    };

    bool add_page() noexcept {
        const std::size_t slots = state_.size();
        if (slots > no_slot - slots_per_page) {
            return false;
        }
        const std::size_t grown = slots + slots_per_page;
        try {
            // Everything is reserved before anything grows, so the table is
            // either one page larger or as it was.
            staged_.reserve(grown * slot_stride);
            state_.reserve(grown);
            free_.reserve(grown);
            given_.reserve(grown);
            dirty_.reserve(dirty_.size() + 1U);
        } catch (const std::bad_alloc&) {
            return false;
        } catch (const std::length_error&) {
            return false;
        }
        staged_.resize(grown * slot_stride);
        state_.resize(grown, state::free);
        dirty_.emplace_back();
        for (std::size_t slot = grown; slot > slots; --slot) {
            free_.push_back(static_cast<std::uint32_t>(slot - 1U));
        }
        return true;
    }

    std::vector<std::byte> staged_;
    std::vector<state> state_;
    std::vector<std::uint32_t> free_;
    std::vector<std::uint32_t> given_;
    std::vector<dirty_range> dirty_;
    std::uint32_t taken_ = 0U;
};

} // namespace progpu::native::mask_slots
