#pragma once

// The draw commands of one clip span, recorded as plain words.
//
// A span of the compiled scene used to be a render bundle. A bundle is paid
// for when it is made (an encoder, a finish that checks every command, a
// release) and again each time it is executed (the pass loses its state and
// the bundle is checked against it), so it only pays off when it holds several
// draws and is executed more than once. A scene that changes every frame, with
// a clip of its own on nearly every element, has one draw in each span and
// uses none of its bundles twice.
//
// So a span keeps the list of its commands. The list is replayed straight
// into the render pass, and only a span that holds enough draws and whose
// list was met before is turned into a bundle. Such a bundle is kept by the
// content of its list: it outlives the scene update that leaves its span as
// it was, and a span whose list changed is the only one that is made again.
//
// Nothing here knows the graphics interface: handles are words, and the
// policies named Ops and Sink do the calls. That keeps the rules testable
// without a device.

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <new>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace progpu::native::span_commands {

enum class handle_kind : std::uint8_t {
    pipeline,
    bind_group,
    buffer,
};

inline constexpr std::uint64_t op_set_pipeline = 1U;
inline constexpr std::uint64_t op_set_bind_group = 2U;
inline constexpr std::uint64_t op_set_vertex_buffer = 3U;
inline constexpr std::uint64_t op_set_index_buffer = 4U;
inline constexpr std::uint64_t op_draw = 5U;
inline constexpr std::uint64_t op_draw_indexed = 6U;

inline constexpr std::uint32_t bind_group_slot_count = 4U;

// A bundle saves the calls of its draws on every later frame and costs one
// execution, after which the pass has to be given its state again. Below this
// many draws a span is replayed directly however often it repeats. The number
// follows from that cost model and has not been tuned by measurement.
inline constexpr std::uint32_t kept_bundle_min_draws = 8U;

struct held_handle {
    handle_kind kind = handle_kind::pipeline;
    std::uintptr_t handle = 0U;
};

// Calls visitor for every command of a list. False when the list is not
// well formed, which no list written by command_store is.
template<typename Visitor>
bool visit(
    const std::uint64_t* words,
    std::size_t count,
    Visitor&& visitor) {
    std::size_t cursor = 0U;
    while (cursor < count) {
        const std::uint64_t head = words[cursor];
        const std::uint64_t op = head & 0xFFU;
        const std::size_t length =
            op == op_set_pipeline || op == op_set_bind_group ? 2U : 3U;
        if (op < op_set_pipeline || op > op_draw_indexed ||
            count - cursor < length) {
            return false;
        }
        const std::uint64_t first = words[cursor + 1U];
        const std::uint64_t second = length == 3U ? words[cursor + 2U] : 0U;
        switch (op) {
            case op_set_pipeline:
                visitor.set_pipeline(static_cast<std::uintptr_t>(first));
                break;
            case op_set_bind_group: {
                const auto slot = static_cast<std::uint32_t>(head >> 8U);
                if (slot >= bind_group_slot_count) {
                    return false;
                }
                visitor.set_bind_group(
                    slot, static_cast<std::uintptr_t>(first));
                break;
            }
            case op_set_vertex_buffer:
                visitor.set_vertex_buffer(
                    static_cast<std::uintptr_t>(first), second);
                break;
            case op_set_index_buffer:
                visitor.set_index_buffer(
                    static_cast<std::uintptr_t>(first), second);
                break;
            case op_draw:
                visitor.draw(
                    static_cast<std::uint32_t>(first),
                    static_cast<std::uint32_t>(first >> 32U),
                    static_cast<std::uint32_t>(second),
                    static_cast<std::uint32_t>(second >> 32U));
                break;
            default:
                visitor.draw_indexed(
                    static_cast<std::uint32_t>(first),
                    static_cast<std::uint32_t>(first >> 32U),
                    static_cast<std::int32_t>(
                        static_cast<std::uint32_t>(second)));
                break;
        }
        cursor += length;
    }
    return true;
}

// What a pass or a bundle encoder was last given. A command that would set
// what is set already is left out, which changes nothing in what is drawn.
struct replay_state {
    std::uintptr_t pipeline = 0U;
    std::array<std::uintptr_t, bind_group_slot_count> bind_groups{};
    std::uintptr_t vertex_buffer = 0U;
    std::uint64_t vertex_size = 0U;
    std::uintptr_t index_buffer = 0U;
    std::uint64_t index_size = 0U;

    void reset() noexcept {
        *this = replay_state{};
    }
};

// Replays a list through Sink, the policy that does the calls on a render
// pass or on a bundle encoder.
template<typename Sink>
bool replay(
    typename Sink::encoder_type encoder,
    const std::uint64_t* words,
    std::size_t count,
    replay_state& state) {
    struct visitor_type {
        typename Sink::encoder_type encoder;
        replay_state& state;

        void set_pipeline(std::uintptr_t handle) {
            if (state.pipeline != handle) {
                state.pipeline = handle;
                Sink::set_pipeline(encoder, handle);
            }
        }
        void set_bind_group(std::uint32_t slot, std::uintptr_t handle) {
            if (state.bind_groups[slot] != handle) {
                state.bind_groups[slot] = handle;
                Sink::set_bind_group(encoder, slot, handle);
            }
        }
        void set_vertex_buffer(std::uintptr_t handle, std::uint64_t size) {
            if (state.vertex_buffer != handle || state.vertex_size != size) {
                state.vertex_buffer = handle;
                state.vertex_size = size;
                Sink::set_vertex_buffer(encoder, handle, size);
            }
        }
        void set_index_buffer(std::uintptr_t handle, std::uint64_t size) {
            if (state.index_buffer != handle || state.index_size != size) {
                state.index_buffer = handle;
                state.index_size = size;
                Sink::set_index_buffer(encoder, handle, size);
            }
        }
        void draw(
            std::uint32_t vertex_count,
            std::uint32_t instance_count,
            std::uint32_t first_vertex,
            std::uint32_t first_instance) {
            Sink::draw(
                encoder,
                vertex_count,
                instance_count,
                first_vertex,
                first_instance);
        }
        void draw_indexed(
            std::uint32_t index_count,
            std::uint32_t first_index,
            std::int32_t base_vertex) {
            Sink::draw_indexed(
                encoder, index_count, first_index, base_vertex);
        }
    };
    return visit(words, count, visitor_type{encoder, state});
}

// Collects every handle a list names, each once.
inline bool collect_handles(
    const std::uint64_t* words,
    std::size_t count,
    std::vector<held_handle>& handles) {
    struct visitor_type {
        std::vector<held_handle>& handles;

        void add(handle_kind kind, std::uintptr_t handle) {
            for (const auto& held : handles) {
                if (held.handle == handle) {
                    return;
                }
            }
            handles.push_back(held_handle{kind, handle});
        }
        void set_pipeline(std::uintptr_t handle) {
            add(handle_kind::pipeline, handle);
        }
        void set_bind_group(std::uint32_t, std::uintptr_t handle) {
            add(handle_kind::bind_group, handle);
        }
        void set_vertex_buffer(std::uintptr_t handle, std::uint64_t) {
            add(handle_kind::buffer, handle);
        }
        void set_index_buffer(std::uintptr_t handle, std::uint64_t) {
            add(handle_kind::buffer, handle);
        }
        void draw(
            std::uint32_t, std::uint32_t, std::uint32_t, std::uint32_t) {
        }
        void draw_indexed(std::uint32_t, std::uint32_t, std::int32_t) {
        }
    };
    return visit(words, count, visitor_type{handles});
}

inline std::uint64_t hash_words(
    std::uint64_t seed,
    const std::uint64_t* words,
    std::size_t count) noexcept {
    std::uint64_t hash = 14695981039346656037ULL ^ seed;
    for (std::size_t index = 0U; index < count; ++index) {
        hash ^= words[index];
        hash *= 1099511628211ULL;
        hash ^= hash >> 29U;
    }
    return hash;
}

// The lists of every span of one compiled scene, and one reference on every
// handle they name. The references keep a list valid for as long as it can be
// replayed, exactly as a bundle keeps what it was encoded with.
//
// Ops: static void retain(handle_kind, std::uintptr_t) noexcept and
// static void release(handle_kind, std::uintptr_t) noexcept.
template<typename Ops>
class command_store final {
public:
    command_store() = default;
    command_store(const command_store&) = delete;
    command_store& operator=(const command_store&) = delete;

    command_store(command_store&& other) noexcept
        : words_(std::move(other.words_)),
          held_(std::move(other.held_)),
          held_set_(std::move(other.held_set_)),
          last_(other.last_),
          failed_(other.failed_) {
        other.forget();
    }

    command_store& operator=(command_store&& other) noexcept {
        if (this != &other) {
            clear();
            words_ = std::move(other.words_);
            held_ = std::move(other.held_);
            held_set_ = std::move(other.held_set_);
            last_ = other.last_;
            failed_ = other.failed_;
            other.forget();
        }
        return *this;
    }

    ~command_store() {
        clear();
    }

    // Releases every reference and forgets every list.
    void clear() noexcept {
        for (const auto& held : held_) {
            Ops::release(held.kind, held.handle);
        }
        forget();
    }

    // True once a list could not be stored. Such a store must not be replayed.
    bool failed() const noexcept {
        return failed_;
    }

    std::size_t size() const noexcept {
        return words_.size();
    }

    const std::uint64_t* data() const noexcept {
        return words_.data();
    }

    std::size_t held_count() const noexcept {
        return held_.size();
    }

    void set_pipeline(std::uintptr_t handle) noexcept {
        hold(handle_kind::pipeline, handle);
        push(op_set_pipeline, handle);
    }

    void set_bind_group(std::uint32_t slot, std::uintptr_t handle) noexcept {
        if (slot >= bind_group_slot_count) {
            failed_ = true;
            return;
        }
        hold(handle_kind::bind_group, handle);
        push(op_set_bind_group | (static_cast<std::uint64_t>(slot) << 8U),
            handle);
    }

    void set_vertex_buffer(
        std::uintptr_t handle, std::uint64_t size) noexcept {
        hold(handle_kind::buffer, handle);
        push(op_set_vertex_buffer, handle, size);
    }

    void set_index_buffer(
        std::uintptr_t handle, std::uint64_t size) noexcept {
        hold(handle_kind::buffer, handle);
        push(op_set_index_buffer, handle, size);
    }

    void draw(
        std::uint32_t vertex_count,
        std::uint32_t instance_count,
        std::uint32_t first_vertex,
        std::uint32_t first_instance) noexcept {
        push(op_draw,
            static_cast<std::uint64_t>(vertex_count) |
                (static_cast<std::uint64_t>(instance_count) << 32U),
            static_cast<std::uint64_t>(first_vertex) |
                (static_cast<std::uint64_t>(first_instance) << 32U));
    }

    void draw_indexed(
        std::uint32_t index_count,
        std::uint32_t first_index,
        std::int32_t base_vertex) noexcept {
        push(op_draw_indexed,
            static_cast<std::uint64_t>(index_count) |
                (static_cast<std::uint64_t>(first_index) << 32U),
            static_cast<std::uint64_t>(
                static_cast<std::uint32_t>(base_vertex)));
    }

private:
    void forget() noexcept {
        words_.clear();
        held_.clear();
        held_set_.clear();
        last_ = {};
        failed_ = false;
    }

    void hold(handle_kind kind, std::uintptr_t handle) noexcept {
        auto& last = last_[static_cast<std::size_t>(kind)];
        if (handle == 0U) {
            failed_ = true;
            return;
        }
        if (last == handle) {
            return;
        }
        try {
            if (held_set_.insert(handle).second) {
                held_.push_back(held_handle{kind, handle});
                Ops::retain(kind, handle);
            }
            last = handle;
        } catch (const std::bad_alloc&) {
            // The set may hold the handle without a reference behind it.
            // Nothing reads the set after a failure: the store is dropped.
            failed_ = true;
        }
    }

    template<typename... Words>
    void push(Words... values) noexcept {
        try {
            (words_.push_back(static_cast<std::uint64_t>(values)), ...);
        } catch (const std::bad_alloc&) {
            failed_ = true;
        }
    }

    std::vector<std::uint64_t> words_;
    std::vector<held_handle> held_;
    std::unordered_set<std::uintptr_t> held_set_;
    std::array<std::uintptr_t, 3U> last_{};
    bool failed_ = false;
};

// The bundles kept from one compile of the scene to the next.
//
// A bundle is found by the words of its list, compared exactly, and by the
// formats it was made for. Every entry holds a reference on each handle its
// list names: while the entry lives no other object can take the address of
// one of them, so equal words always mean the same objects.
//
// A list becomes a bundle the second time it is met in consecutive compiles
// (or at once when the whole scene is drawn again unchanged). An entry no
// compile asked for is released at the end of that compile.
//
// Ops: retain and release as for command_store, and
// static void release_bundle(std::uintptr_t) noexcept.
template<typename Ops>
class kept_bundle_cache final {
public:
    kept_bundle_cache() = default;
    kept_bundle_cache(const kept_bundle_cache&) = delete;
    kept_bundle_cache& operator=(const kept_bundle_cache&) = delete;

    ~kept_bundle_cache() {
        clear();
    }

    void clear() noexcept {
        for (auto& [hash, entry] : entries_) {
            release(entry);
        }
        entries_.clear();
        seen_.clear();
        seen_next_.clear();
    }

    std::size_t size() const noexcept {
        return entries_.size();
    }

    void begin_compile() noexcept {
        ++serial_;
        seen_next_.clear();
    }

    // The kept bundle of this list, or zero. The cache keeps its reference.
    std::uintptr_t find(
        std::uint64_t hash,
        std::uint32_t format,
        const std::uint64_t* words,
        std::size_t count) noexcept {
        const auto range = entries_.equal_range(hash);
        for (auto iterator = range.first; iterator != range.second;
             ++iterator) {
            auto& entry = iterator->second;
            if (entry.format == format && entry.words.size() == count &&
                (count == 0U || std::memcmp(
                    entry.words.data(), words,
                    count * sizeof(std::uint64_t)) == 0)) {
                entry.used = serial_;
                return entry.bundle;
            }
        }
        return 0U;
    }

    // True when the compile before this one met a list with this hash and
    // had no bundle for it. Two different lists with one hash only make a
    // bundle one compile early.
    bool met_before(std::uint64_t hash) const noexcept {
        return seen_.find(hash) != seen_.end();
    }

    // Remembers a list this compile replays directly.
    void note(std::uint64_t hash) noexcept {
        try {
            seen_next_.insert(hash);
        } catch (const std::bad_alloc&) {
            // Forgetting it only delays the bundle.
        }
    }

    // Keeps a bundle made from this list and takes over the reference the
    // caller holds on it. False when it could not be kept: the caller still
    // owns its reference then.
    bool insert(
        std::uint64_t hash,
        std::uint32_t format,
        const std::uint64_t* words,
        std::size_t count,
        std::uintptr_t bundle) noexcept {
        try {
            entry_type entry{};
            entry.format = format;
            entry.words.assign(words, words + count);
            if (!collect_handles(words, count, entry.handles)) {
                return false;
            }
            entry.used = serial_;
            auto iterator = entries_.emplace(hash, std::move(entry));
            for (const auto& held : iterator->second.handles) {
                Ops::retain(held.kind, held.handle);
            }
            iterator->second.bundle = bundle;
            return true;
        } catch (const std::bad_alloc&) {
            return false;
        }
    }

    // Releases what this compile did not ask for.
    void end_compile() noexcept {
        for (auto iterator = entries_.begin(); iterator != entries_.end();) {
            if (iterator->second.used != serial_) {
                release(iterator->second);
                iterator = entries_.erase(iterator);
            } else {
                ++iterator;
            }
        }
        seen_.swap(seen_next_);
        seen_next_.clear();
    }

private:
    struct entry_type {
        std::uint32_t format = 0U;
        std::vector<std::uint64_t> words;
        std::vector<held_handle> handles;
        std::uintptr_t bundle = 0U;
        std::uint64_t used = 0U;
    };

    static void release(entry_type& entry) noexcept {
        if (entry.bundle != 0U) {
            Ops::release_bundle(entry.bundle);
            entry.bundle = 0U;
        }
        for (const auto& held : entry.handles) {
            Ops::release(held.kind, held.handle);
        }
        entry.handles.clear();
    }

    std::unordered_multimap<std::uint64_t, entry_type> entries_;
    std::unordered_set<std::uint64_t> seen_;
    std::unordered_set<std::uint64_t> seen_next_;
    std::uint64_t serial_ = 0U;
};

} // namespace progpu::native::span_commands
