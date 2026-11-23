#pragma once

// Uploads of a retained page that send only what changed.
//
// A family page is compiled again whenever one of its records changes, and most
// of its bytes are then what the buffer already holds. A mirror keeps those bytes
// on the CPU, compares the new page with them block by block and writes only the
// runs that differ, so the buffer ends up holding exactly the new page.
//
// The comparison knows nothing of the graphics interface: the writer is a callable,
// which keeps the rules testable without a device.

#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <new>
#include <span>
#include <unordered_map>
#include <vector>

namespace progpu::native {

// The unit of comparison. A multiple of four, as offsets and sizes of buffer
// writes must be.
inline constexpr std::size_t range_upload_block = 256U;

// Hands every run of `current` that differs from `previous` to
// write(offset, bytes, size), and everything past the end of `previous`. Runs
// separated by less than one clean block are joined: a separate write costs more
// than the block. Returns the bytes handed out.
template<typename Write>
std::uint64_t write_changed_ranges(
    std::span<const std::byte> previous,
    std::span<const std::byte> current,
    Write&& write) {
    const std::size_t common = std::min(previous.size(), current.size());
    std::uint64_t written = 0U;
    bool open = false;
    std::size_t run_start = 0U;
    std::size_t run_end = 0U;
    const auto flush = [&]() {
        if (open) {
            write(run_start, current.data() + run_start, run_end - run_start);
            written += run_end - run_start;
            open = false;
        }
    };
    for (std::size_t block = 0U; block < common; block += range_upload_block) {
        const std::size_t size = std::min(range_upload_block, common - block);
        if (std::memcmp(previous.data() + block, current.data() + block, size) != 0) {
            if (!open) {
                open = true;
                run_start = block;
            }
            run_end = block + size;
        } else if (open && block >= run_end + range_upload_block) {
            flush();
        }
    }
    if (current.size() > common) {
        if (!open || common >= run_end + range_upload_block) {
            flush();
            open = true;
            run_start = common;
        }
        run_end = current.size();
    }
    flush();
    return written;
}

// What the uploads of some frames cost, and how much of that is content that only
// moved: an insertion or removal earlier in a page shifts everything after it, and
// a page in chunks would not have written those bytes again.
//
// The time of the writes of one upload is shared out by bytes, so the shifted time
// is an estimate. The time spent finding the shifted bytes is kept apart, so that a
// measurement can take it out of the stage it ran in.
struct range_upload_counters {
    std::uint64_t uploaded_bytes = 0U;
    std::uint64_t shifted_bytes = 0U;
    std::uint64_t upload_nanoseconds = 0U;
    std::uint64_t shifted_nanoseconds = 0U;
    std::uint64_t counting_nanoseconds = 0U;
};

// What the words of a page mean to the comparison. Indices of a page move by the
// vertices inserted before them, so they count as moved when their differences match.
enum class range_upload_content : std::uint8_t {
    records,
    indices,
};

// The bytes of the runs `current` writes over `previous` that hold content found
// at another offset of `previous`, in windows of one block. Runs shorter than a
// block are not searched, so the count errs low.
inline std::uint64_t count_shifted_bytes(
    std::span<const std::byte> previous,
    std::span<const std::byte> current,
    range_upload_content content) {
    constexpr std::size_t window = range_upload_block / sizeof(std::uint32_t);
    constexpr std::uint64_t base = 0x100000001B3ULL;
    if (previous.size() < range_upload_block || current.size() < range_upload_block) {
        return 0U;
    }
    const auto words_of = [content](std::span<const std::byte> bytes) {
        std::vector<std::uint32_t> words(bytes.size() / sizeof(std::uint32_t));
        std::memcpy(words.data(), bytes.data(), words.size() * sizeof(std::uint32_t));
        if (content == range_upload_content::indices) {
            for (std::size_t word = words.size() - 1U; word > 0U; --word) {
                words[word] -= words[word - 1U];
            }
            words[0] = 0U;
        }
        return words;
    };
    const std::vector<std::uint32_t> before = words_of(previous);
    const std::vector<std::uint32_t> after = words_of(current);
    const auto hash_of = [](const std::uint32_t* words) {
        std::uint64_t hash = 0U;
        for (std::size_t word = 0U; word < window; ++word) {
            hash = hash * base + words[word];
        }
        return hash;
    };
    std::uint64_t base_of_window = 1U;
    for (std::size_t word = 0U; word < window; ++word) {
        base_of_window *= base;
    }
    std::unordered_map<std::uint64_t, std::size_t> blocks;
    blocks.reserve(before.size() / window);
    for (std::size_t word = 0U; word + window <= before.size(); word += window) {
        blocks.try_emplace(hash_of(before.data() + word), word);
    }
    std::uint64_t shifted = 0U;
    write_changed_ranges(previous, current, [&](std::size_t offset, const std::byte*, std::size_t size) {
        const std::size_t end = (offset + size) / sizeof(std::uint32_t);
        std::size_t word = offset / sizeof(std::uint32_t);
        while (word + window <= end) {
            std::uint64_t hash = hash_of(after.data() + word);
            bool found = false;
            while (true) {
                const auto block = blocks.find(hash);
                if (block != blocks.end() && block->second != word &&
                    std::equal(after.data() + word, after.data() + word + window,
                        before.data() + block->second)) {
                    std::size_t length = window;
                    while (word + length < end && block->second + length < before.size() &&
                        after[word + length] == before[block->second + length]) {
                        ++length;
                    }
                    shifted += length * sizeof(std::uint32_t);
                    word += length;
                    found = true;
                    break;
                }
                if (word + window >= end) {
                    break;
                }
                hash = hash * base + after[word + window] - after[word] * base_of_window;
                ++word;
            }
            if (!found) {
                break;
            }
        }
    });
    return shifted;
}

// What a GPU buffer holds from offset zero.
class range_upload_mirror final {
public:
    // The buffer was replaced or destroyed: the next upload writes everything.
    void invalidate() noexcept {
        valid_ = false;
    }

    bool valid() const noexcept {
        return valid_;
    }

    // Makes the buffer hold `bytes` from offset zero through
    // write(offset, data, size). Returns the bytes written. With counters it also
    // finds what of those bytes only moved, which costs more than the upload.
    template<typename Write>
    std::uint64_t upload(
        std::span<const std::byte> bytes,
        Write&& write,
        range_upload_counters* counters = nullptr,
        range_upload_content content = range_upload_content::records) {
        if (counters == nullptr) {
            return write_page(bytes, write);
        }
        using clock = std::chrono::steady_clock;
        const auto nanoseconds = [](clock::duration time) {
            return static_cast<std::uint64_t>(
                std::chrono::duration_cast<std::chrono::nanoseconds>(time).count());
        };
        const auto counting = clock::now();
        const std::uint64_t moved = valid_
            ? count_shifted_bytes(std::span<const std::byte>(bytes_), bytes, content)
            : 0U;
        const auto writing = clock::now();
        const std::uint64_t written = write_page(bytes, write);
        const std::uint64_t write_time = nanoseconds(clock::now() - writing);
        counters->counting_nanoseconds += nanoseconds(writing - counting);
        counters->uploaded_bytes += written;
        counters->upload_nanoseconds += write_time;
        if (written != 0U) {
            const std::uint64_t shifted = std::min(moved, written);
            counters->shifted_bytes += shifted;
            counters->shifted_nanoseconds += static_cast<std::uint64_t>(
                static_cast<double>(write_time) * static_cast<double>(shifted) /
                static_cast<double>(written));
        }
        return written;
    }

    // The CPU memory the mirror holds.
    std::size_t capacity_bytes() const noexcept {
        return bytes_.capacity();
    }

private:
    template<typename Write>
    std::uint64_t write_page(std::span<const std::byte> bytes, Write& write) {
        const std::size_t common = valid_ ? std::min(bytes_.size(), bytes.size()) : 0U;
        try {
            bytes_.resize(bytes.size());
        } catch (const std::bad_alloc&) {
            valid_ = false;
            if (!bytes.empty()) {
                write(std::size_t{0U}, bytes.data(), bytes.size());
            }
            return bytes.size();
        }
        valid_ = true;
        return write_changed_ranges(
            std::span<const std::byte>(bytes_.data(), common),
            bytes,
            [&](std::size_t offset, const std::byte* data, std::size_t size) {
                write(offset, data, size);
                std::memcpy(bytes_.data() + offset, data, size);
            });
    }

    std::vector<std::byte> bytes_;
    bool valid_ = false;
};

} // namespace progpu::native
