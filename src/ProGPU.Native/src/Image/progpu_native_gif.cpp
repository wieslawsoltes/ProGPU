// The GIF reader: GIF87a and GIF89a, the first frame on a transparent canvas of
// the logical screen, with its transparent colour and interlacing.

#include "progpu_native_raster_internal.hpp"

#include <algorithm>
#include <array>
#include <vector>

namespace progpu::native::image::detail {

namespace {

constexpr std::size_t screen_descriptor_end = 13U;
constexpr std::uint32_t maximum_codes = 4096U;

struct gif_screen final {
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    std::size_t global_palette = 0U;
    std::uint32_t global_colors = 0U;
    std::size_t blocks = 0U;
};

bool parse_screen(
    std::span<const std::byte> input,
    gif_screen& screen,
    image_error& error) noexcept {
    if (input.size() < screen_descriptor_end) {
        error = image_error::invalid_chunk;
        return false;
    }
    screen.width = read_u16_le(input, 6U);
    screen.height = read_u16_le(input, 8U);
    const std::uint32_t flags = byte_at(input, 10U);
    screen.blocks = screen_descriptor_end;
    if ((flags & 0x80U) != 0U) {
        screen.global_colors = 2U << (flags & 7U);
        screen.global_palette = screen_descriptor_end;
        const std::size_t bytes = std::size_t{screen.global_colors} * 3U;
        if (bytes > input.size() - screen_descriptor_end) {
            error = image_error::invalid_chunk;
            return false;
        }
        screen.blocks += bytes;
    }
    return true;
}

// Moves past a chain of data sub-blocks.
bool skip_sub_blocks(std::span<const std::byte> input, std::size_t& at) noexcept {
    while (at < input.size()) {
        const std::size_t length = byte_at(input, at);
        ++at;
        if (length == 0U) {
            return true;
        }
        if (length > input.size() - at) {
            return false;
        }
        at += length;
    }
    return false;
}

// The bits of the sub-blocks of the image data, least significant first.
class code_reader final {
public:
    code_reader(std::span<const std::byte> input, std::size_t at) noexcept
        : input_(input), at_(at) {}

    bool read(std::uint32_t width, std::uint32_t& code) noexcept {
        while (count_ < width) {
            if (left_ == 0U) {
                if (at_ >= input_.size()) {
                    return false;
                }
                left_ = byte_at(input_, at_);
                ++at_;
                if (left_ == 0U) {
                    ended_ = true;
                    return false;
                }
            }
            if (ended_ || at_ >= input_.size()) {
                return false;
            }
            bits_ |= static_cast<std::uint32_t>(byte_at(input_, at_)) << count_;
            ++at_;
            --left_;
            count_ += 8U;
        }
        code = bits_ & ((1U << width) - 1U);
        bits_ >>= width;
        count_ -= width;
        return true;
    }

private:
    std::span<const std::byte> input_;
    std::size_t at_;
    std::uint32_t bits_ = 0U;
    std::uint32_t count_ = 0U;
    std::uint32_t left_ = 0U;
    bool ended_ = false;
};

// Expands the LZW stream into `indices`. A stream that ends early leaves the
// rest of the frame at the index it started with.
void decode_lzw(
    std::span<const std::byte> input,
    std::size_t at,
    std::uint32_t minimum_code_size,
    std::span<std::uint8_t> indices) {
    const std::uint32_t clear = 1U << minimum_code_size;
    const std::uint32_t end = clear + 1U;
    std::vector<std::uint16_t> prefix(maximum_codes);
    std::vector<std::uint8_t> suffix(maximum_codes);
    std::vector<std::uint8_t> stack(maximum_codes + 1U);
    for (std::uint32_t code = 0U; code < clear; ++code) {
        suffix[code] = static_cast<std::uint8_t>(code);
    }
    code_reader reader(input, at);
    std::uint32_t width = minimum_code_size + 1U;
    std::uint32_t next = end + 1U;
    std::uint32_t previous = maximum_codes;
    std::uint8_t first = 0U;
    std::size_t written = 0U;
    std::uint32_t code = 0U;
    while (written < indices.size() && reader.read(width, code)) {
        if (code == clear) {
            width = minimum_code_size + 1U;
            next = end + 1U;
            previous = maximum_codes;
            continue;
        }
        if (code == end) {
            return;
        }
        if (previous == maximum_codes) {
            if (code >= clear) {
                return;
            }
            first = static_cast<std::uint8_t>(code);
            indices[written++] = first;
            previous = code;
            continue;
        }
        std::size_t depth = 0U;
        std::uint32_t current = code;
        if (code >= next) {
            if (code != next) {
                return;
            }
            stack[depth++] = first;
            current = previous;
        }
        while (current >= clear) {
            if (depth >= maximum_codes) {
                return;
            }
            stack[depth++] = suffix[current];
            current = prefix[current];
        }
        first = suffix[current];
        stack[depth++] = first;
        while (depth > 0U && written < indices.size()) {
            indices[written++] = stack[--depth];
        }
        if (next < maximum_codes) {
            prefix[next] = static_cast<std::uint16_t>(previous);
            suffix[next] = first;
            ++next;
            if (next == (1U << width) && width < 12U) {
                ++width;
            }
        }
        previous = code;
    }
}

} // namespace

bool read_gif_size(
    std::span<const std::byte> input,
    raster_image_size& result,
    image_error& error) noexcept {
    gif_screen screen{};
    if (!parse_screen(input, screen, error)) {
        return false;
    }
    if (!raster_bytes(screen.width, screen.height, result.rgba_bytes)) {
        error = image_error::unsupported_format;
        return false;
    }
    result.width = screen.width;
    result.height = screen.height;
    return true;
}

bool decode_gif(
    std::span<const std::byte> input,
    std::span<std::byte> rgba,
    image_error& error) {
    gif_screen screen{};
    if (!parse_screen(input, screen, error)) {
        return false;
    }
    std::fill(rgba.begin(), rgba.end(), std::byte{0});
    std::size_t at = screen.blocks;
    bool has_transparent = false;
    std::uint32_t transparent = 0U;
    error = image_error::invalid_chunk;
    while (at < input.size()) {
        const std::uint32_t introducer = byte_at(input, at);
        ++at;
        if (introducer == 0x3BU) {
            // A file without a frame shows the empty canvas.
            return true;
        }
        if (introducer == 0x21U) {
            if (at >= input.size()) {
                return false;
            }
            const std::uint32_t label = byte_at(input, at);
            ++at;
            if (label == 0xF9U && at + 5U < input.size() && byte_at(input, at) == 4U) {
                has_transparent = (byte_at(input, at + 1U) & 1U) != 0U;
                transparent = byte_at(input, at + 4U);
            }
            if (!skip_sub_blocks(input, at)) {
                return false;
            }
            continue;
        }
        if (introducer != 0x2CU) {
            return false;
        }
        if (input.size() - at < 9U) {
            return false;
        }
        const std::uint32_t left = read_u16_le(input, at);
        const std::uint32_t top = read_u16_le(input, at + 2U);
        const std::uint32_t width = read_u16_le(input, at + 4U);
        const std::uint32_t height = read_u16_le(input, at + 6U);
        const std::uint32_t flags = byte_at(input, at + 8U);
        at += 9U;
        std::size_t palette = screen.global_palette;
        std::uint32_t colors = screen.global_colors;
        if ((flags & 0x80U) != 0U) {
            colors = 2U << (flags & 7U);
            palette = at;
            const std::size_t bytes = std::size_t{colors} * 3U;
            if (bytes > input.size() - at) {
                return false;
            }
            at += bytes;
        }
        if (colors == 0U || at >= input.size() || width == 0U || height == 0U) {
            return false;
        }
        const std::uint32_t minimum_code_size = byte_at(input, at);
        ++at;
        if (minimum_code_size < 1U || minimum_code_size > 11U) {
            error = image_error::invalid_compressed_data;
            return false;
        }
        const std::size_t pixels = std::size_t{width} * height;
        // What a stream that ends early leaves is transparent.
        const auto unset = static_cast<std::uint8_t>(has_transparent ? transparent : 0U);
        std::vector<std::uint8_t> indices(pixels, unset);
        decode_lzw(input, at, minimum_code_size, indices);

        const bool interlaced = (flags & 0x40U) != 0U;
        static constexpr std::array<std::uint32_t, 4U> pass_start{0U, 4U, 2U, 1U};
        static constexpr std::array<std::uint32_t, 4U> pass_step{8U, 8U, 4U, 2U};
        std::size_t source = 0U;
        const auto draw_row = [&](std::uint32_t row) noexcept {
            const std::uint32_t y = top + row;
            for (std::uint32_t column = 0U; column < width; ++column) {
                const std::uint32_t index = indices[source + column];
                const std::uint32_t x = left + column;
                if (x >= screen.width || y >= screen.height ||
                    (has_transparent && index == transparent) || index >= colors) {
                    continue;
                }
                const std::size_t color = palette + std::size_t{index} * 3U;
                write_rgba(
                    rgba,
                    std::size_t{y} * screen.width + x,
                    byte_at(input, color),
                    byte_at(input, color + 1U),
                    byte_at(input, color + 2U),
                    255U);
            }
            source += width;
        };
        if (interlaced) {
            for (std::size_t pass = 0U; pass < pass_start.size(); ++pass) {
                for (std::uint32_t row = pass_start[pass]; row < height; row += pass_step[pass]) {
                    draw_row(row);
                }
            }
        } else {
            for (std::uint32_t row = 0U; row < height; ++row) {
                draw_row(row);
            }
        }
        return true;
    }
    return false;
}

} // namespace progpu::native::image::detail
