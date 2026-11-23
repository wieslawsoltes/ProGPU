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
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <new>
#include <span>
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
    // write(offset, data, size). Returns the bytes written.
    template<typename Write>
    std::uint64_t upload(std::span<const std::byte> bytes, Write&& write) {
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

    // The CPU memory the mirror holds.
    std::size_t capacity_bytes() const noexcept {
        return bytes_.capacity();
    }

private:
    std::vector<std::byte> bytes_;
    bool valid_ = false;
};

} // namespace progpu::native
