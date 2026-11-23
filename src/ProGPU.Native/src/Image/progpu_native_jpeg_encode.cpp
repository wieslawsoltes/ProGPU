// The JPEG writer: baseline, eight bits, YCbCr with the standard Huffman tables of
// Annex K and the IJG scaling of its quantization tables.

#include "progpu_native_raster_internal.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <new>
#include <numbers>
#include <vector>

namespace progpu::native::image {

namespace {

constexpr std::array<std::uint8_t, 64U> zigzag{
    0U,  1U,  8U,  16U, 9U,  2U,  3U,  10U,
    17U, 24U, 32U, 25U, 18U, 11U, 4U,  5U,
    12U, 19U, 26U, 33U, 40U, 48U, 41U, 34U,
    27U, 20U, 13U, 6U,  7U,  14U, 21U, 28U,
    35U, 42U, 49U, 56U, 57U, 50U, 43U, 36U,
    29U, 22U, 15U, 23U, 30U, 37U, 44U, 51U,
    58U, 59U, 52U, 45U, 38U, 31U, 39U, 46U,
    53U, 60U, 61U, 54U, 47U, 55U, 62U, 63U};

constexpr std::array<std::uint8_t, 64U> base_luma{
    16U, 11U, 10U, 16U, 24U, 40U, 51U, 61U,
    12U, 12U, 14U, 19U, 26U, 58U, 60U, 55U,
    14U, 13U, 16U, 24U, 40U, 57U, 69U, 56U,
    14U, 17U, 22U, 29U, 51U, 87U, 80U, 62U,
    18U, 22U, 37U, 56U, 68U, 109U, 103U, 77U,
    24U, 35U, 55U, 64U, 81U, 104U, 113U, 92U,
    49U, 64U, 78U, 87U, 103U, 121U, 120U, 101U,
    72U, 92U, 95U, 98U, 112U, 100U, 103U, 99U};

constexpr std::array<std::uint8_t, 64U> base_chroma{
    17U, 18U, 24U, 47U, 99U, 99U, 99U, 99U,
    18U, 21U, 26U, 66U, 99U, 99U, 99U, 99U,
    24U, 26U, 56U, 99U, 99U, 99U, 99U, 99U,
    47U, 66U, 99U, 99U, 99U, 99U, 99U, 99U,
    99U, 99U, 99U, 99U, 99U, 99U, 99U, 99U,
    99U, 99U, 99U, 99U, 99U, 99U, 99U, 99U,
    99U, 99U, 99U, 99U, 99U, 99U, 99U, 99U,
    99U, 99U, 99U, 99U, 99U, 99U, 99U, 99U};

constexpr std::array<std::uint8_t, 16U> dc_luma_counts{
    0U, 1U, 5U, 1U, 1U, 1U, 1U, 1U, 1U, 0U, 0U, 0U, 0U, 0U, 0U, 0U};
constexpr std::array<std::uint8_t, 16U> dc_chroma_counts{
    0U, 3U, 1U, 1U, 1U, 1U, 1U, 1U, 1U, 1U, 1U, 0U, 0U, 0U, 0U, 0U};
constexpr std::array<std::uint8_t, 12U> dc_values{
    0U, 1U, 2U, 3U, 4U, 5U, 6U, 7U, 8U, 9U, 10U, 11U};
constexpr std::array<std::uint8_t, 16U> ac_luma_counts{
    0U, 2U, 1U, 3U, 3U, 2U, 4U, 3U, 5U, 5U, 4U, 4U, 0U, 0U, 1U, 0x7DU};
constexpr std::array<std::uint8_t, 162U> ac_luma_values{
    0x01U, 0x02U, 0x03U, 0x00U, 0x04U, 0x11U, 0x05U, 0x12U, 0x21U, 0x31U, 0x41U, 0x06U,
    0x13U, 0x51U, 0x61U, 0x07U, 0x22U, 0x71U, 0x14U, 0x32U, 0x81U, 0x91U, 0xA1U, 0x08U,
    0x23U, 0x42U, 0xB1U, 0xC1U, 0x15U, 0x52U, 0xD1U, 0xF0U, 0x24U, 0x33U, 0x62U, 0x72U,
    0x82U, 0x09U, 0x0AU, 0x16U, 0x17U, 0x18U, 0x19U, 0x1AU, 0x25U, 0x26U, 0x27U, 0x28U,
    0x29U, 0x2AU, 0x34U, 0x35U, 0x36U, 0x37U, 0x38U, 0x39U, 0x3AU, 0x43U, 0x44U, 0x45U,
    0x46U, 0x47U, 0x48U, 0x49U, 0x4AU, 0x53U, 0x54U, 0x55U, 0x56U, 0x57U, 0x58U, 0x59U,
    0x5AU, 0x63U, 0x64U, 0x65U, 0x66U, 0x67U, 0x68U, 0x69U, 0x6AU, 0x73U, 0x74U, 0x75U,
    0x76U, 0x77U, 0x78U, 0x79U, 0x7AU, 0x83U, 0x84U, 0x85U, 0x86U, 0x87U, 0x88U, 0x89U,
    0x8AU, 0x92U, 0x93U, 0x94U, 0x95U, 0x96U, 0x97U, 0x98U, 0x99U, 0x9AU, 0xA2U, 0xA3U,
    0xA4U, 0xA5U, 0xA6U, 0xA7U, 0xA8U, 0xA9U, 0xAAU, 0xB2U, 0xB3U, 0xB4U, 0xB5U, 0xB6U,
    0xB7U, 0xB8U, 0xB9U, 0xBAU, 0xC2U, 0xC3U, 0xC4U, 0xC5U, 0xC6U, 0xC7U, 0xC8U, 0xC9U,
    0xCAU, 0xD2U, 0xD3U, 0xD4U, 0xD5U, 0xD6U, 0xD7U, 0xD8U, 0xD9U, 0xDAU, 0xE1U, 0xE2U,
    0xE3U, 0xE4U, 0xE5U, 0xE6U, 0xE7U, 0xE8U, 0xE9U, 0xEAU, 0xF1U, 0xF2U, 0xF3U, 0xF4U,
    0xF5U, 0xF6U, 0xF7U, 0xF8U, 0xF9U, 0xFAU};
constexpr std::array<std::uint8_t, 16U> ac_chroma_counts{
    0U, 2U, 1U, 2U, 4U, 4U, 3U, 4U, 7U, 5U, 4U, 4U, 0U, 1U, 2U, 0x77U};
constexpr std::array<std::uint8_t, 162U> ac_chroma_values{
    0x00U, 0x01U, 0x02U, 0x03U, 0x11U, 0x04U, 0x05U, 0x21U, 0x31U, 0x06U, 0x12U, 0x41U,
    0x51U, 0x07U, 0x61U, 0x71U, 0x13U, 0x22U, 0x32U, 0x81U, 0x08U, 0x14U, 0x42U, 0x91U,
    0xA1U, 0xB1U, 0xC1U, 0x09U, 0x23U, 0x33U, 0x52U, 0xF0U, 0x15U, 0x62U, 0x72U, 0xD1U,
    0x0AU, 0x16U, 0x24U, 0x34U, 0xE1U, 0x25U, 0xF1U, 0x17U, 0x18U, 0x19U, 0x1AU, 0x26U,
    0x27U, 0x28U, 0x29U, 0x2AU, 0x35U, 0x36U, 0x37U, 0x38U, 0x39U, 0x3AU, 0x43U, 0x44U,
    0x45U, 0x46U, 0x47U, 0x48U, 0x49U, 0x4AU, 0x53U, 0x54U, 0x55U, 0x56U, 0x57U, 0x58U,
    0x59U, 0x5AU, 0x63U, 0x64U, 0x65U, 0x66U, 0x67U, 0x68U, 0x69U, 0x6AU, 0x73U, 0x74U,
    0x75U, 0x76U, 0x77U, 0x78U, 0x79U, 0x7AU, 0x82U, 0x83U, 0x84U, 0x85U, 0x86U, 0x87U,
    0x88U, 0x89U, 0x8AU, 0x92U, 0x93U, 0x94U, 0x95U, 0x96U, 0x97U, 0x98U, 0x99U, 0x9AU,
    0xA2U, 0xA3U, 0xA4U, 0xA5U, 0xA6U, 0xA7U, 0xA8U, 0xA9U, 0xAAU, 0xB2U, 0xB3U, 0xB4U,
    0xB5U, 0xB6U, 0xB7U, 0xB8U, 0xB9U, 0xBAU, 0xC2U, 0xC3U, 0xC4U, 0xC5U, 0xC6U, 0xC7U,
    0xC8U, 0xC9U, 0xCAU, 0xD2U, 0xD3U, 0xD4U, 0xD5U, 0xD6U, 0xD7U, 0xD8U, 0xD9U, 0xDAU,
    0xE2U, 0xE3U, 0xE4U, 0xE5U, 0xE6U, 0xE7U, 0xE8U, 0xE9U, 0xEAU, 0xF2U, 0xF3U, 0xF4U,
    0xF5U, 0xF6U, 0xF7U, 0xF8U, 0xF9U, 0xFAU};

struct huffman_code final {
    std::array<std::uint16_t, 256U> code{};
    std::array<std::uint8_t, 256U> size{};
};

huffman_code build_code(
    std::span<const std::uint8_t, 16U> counts,
    std::span<const std::uint8_t> values) noexcept {
    huffman_code table{};
    std::uint32_t code = 0U;
    std::size_t index = 0U;
    for (std::uint32_t length = 1U; length <= 16U; ++length) {
        for (std::uint32_t entry = 0U; entry < counts[length - 1U]; ++entry) {
            const std::uint8_t symbol = values[index++];
            table.code[symbol] = static_cast<std::uint16_t>(code++);
            table.size[symbol] = static_cast<std::uint8_t>(length);
        }
        code <<= 1U;
    }
    return table;
}

class entropy_writer final {
public:
    explicit entropy_writer(std::vector<std::byte>& output) noexcept : output_(output) {}

    void put(std::uint32_t value, std::uint32_t count) {
        bits_ = (bits_ << count) | (value & ((std::uint64_t{1U} << count) - 1U));
        count_ += count;
        while (count_ >= 8U) {
            const auto next = static_cast<std::uint8_t>((bits_ >> (count_ - 8U)) & 0xFFU);
            output_.push_back(static_cast<std::byte>(next));
            if (next == 0xFFU) {
                output_.push_back(std::byte{0U});
            }
            count_ -= 8U;
        }
    }

    void finish() {
        if (count_ > 0U) {
            put((1U << (8U - count_)) - 1U, 8U - count_);
        }
    }

private:
    std::vector<std::byte>& output_;
    std::uint64_t bits_ = 0U;
    std::uint32_t count_ = 0U;
};

void put_u8(std::vector<std::byte>& out, std::uint32_t value) {
    out.push_back(static_cast<std::byte>(value & 0xFFU));
}

void put_u16(std::vector<std::byte>& out, std::uint32_t value) {
    put_u8(out, value >> 8U);
    put_u8(out, value);
}

void put_marker(std::vector<std::byte>& out, std::uint32_t marker, std::size_t payload) {
    put_u8(out, 0xFFU);
    put_u8(out, marker);
    put_u16(out, static_cast<std::uint32_t>(payload + 2U));
}

void put_huffman(
    std::vector<std::byte>& out,
    std::uint32_t slot,
    std::span<const std::uint8_t, 16U> counts,
    std::span<const std::uint8_t> values) {
    put_marker(out, 0xC4U, 17U + values.size());
    put_u8(out, slot);
    for (const std::uint8_t count : counts) {
        put_u8(out, count);
    }
    for (const std::uint8_t value : values) {
        put_u8(out, value);
    }
}

std::array<std::uint8_t, 64U> scaled_table(
    const std::array<std::uint8_t, 64U>& base,
    std::uint32_t quality) noexcept {
    const std::uint32_t scale = quality < 50U ? 5000U / quality : 200U - quality * 2U;
    std::array<std::uint8_t, 64U> table{};
    for (std::size_t index = 0U; index < table.size(); ++index) {
        const std::uint32_t value = (base[index] * scale + 50U) / 100U;
        table[index] = static_cast<std::uint8_t>(std::clamp<std::uint32_t>(value, 1U, 255U));
    }
    return table;
}

struct dct_basis final {
    std::array<std::array<float, 8U>, 8U> cosine{};

    dct_basis() noexcept {
        for (std::size_t u = 0U; u < 8U; ++u) {
            const double scale = u == 0U ? std::sqrt(1.0 / 8.0) : 0.5;
            for (std::size_t x = 0U; x < 8U; ++x) {
                cosine[u][x] = static_cast<float>(scale * std::cos(
                    (2.0 * static_cast<double>(x) + 1.0) * static_cast<double>(u) *
                    std::numbers::pi / 16.0));
            }
        }
    }
};

void forward_dct(
    const std::array<float, 64U>& samples,
    const dct_basis& basis,
    std::array<float, 64U>& output) noexcept {
    std::array<float, 64U> rows{};
    for (std::size_t y = 0U; y < 8U; ++y) {
        for (std::size_t u = 0U; u < 8U; ++u) {
            float sum = 0.0F;
            for (std::size_t x = 0U; x < 8U; ++x) {
                sum += samples[y * 8U + x] * basis.cosine[u][x];
            }
            rows[y * 8U + u] = sum;
        }
    }
    for (std::size_t u = 0U; u < 8U; ++u) {
        for (std::size_t v = 0U; v < 8U; ++v) {
            float sum = 0.0F;
            for (std::size_t y = 0U; y < 8U; ++y) {
                sum += rows[y * 8U + u] * basis.cosine[v][y];
            }
            output[v * 8U + u] = sum;
        }
    }
}

std::uint32_t magnitude_bits(std::int32_t value) noexcept {
    auto magnitude = static_cast<std::uint32_t>(value < 0 ? -value : value);
    std::uint32_t bits = 0U;
    while (magnitude != 0U) {
        ++bits;
        magnitude >>= 1U;
    }
    return bits;
}

void put_value(entropy_writer& writer, std::int32_t value, std::uint32_t bits) {
    if (bits != 0U) {
        const std::int32_t coded = value < 0 ? value + (1 << bits) - 1 : value;
        writer.put(static_cast<std::uint32_t>(coded), bits);
    }
}

void encode_block(
    entropy_writer& writer,
    const std::array<float, 64U>& samples,
    const std::array<std::uint8_t, 64U>& quantization,
    const dct_basis& basis,
    const huffman_code& dc,
    const huffman_code& ac,
    std::int32_t& predictor) {
    std::array<float, 64U> frequency{};
    forward_dct(samples, basis, frequency);
    std::array<std::int32_t, 64U> quantized{};
    for (std::size_t index = 0U; index < 64U; ++index) {
        const std::size_t at = zigzag[index];
        quantized[index] = static_cast<std::int32_t>(
            std::lround(frequency[at] / static_cast<float>(quantization[at])));
    }
    const std::int32_t difference = quantized[0] - predictor;
    predictor = quantized[0];
    const std::uint32_t dc_bits = magnitude_bits(difference);
    writer.put(dc.code[dc_bits], dc.size[dc_bits]);
    put_value(writer, difference, dc_bits);
    std::uint32_t run = 0U;
    for (std::size_t index = 1U; index < 64U; ++index) {
        const std::int32_t value = quantized[index];
        if (value == 0) {
            ++run;
            continue;
        }
        while (run >= 16U) {
            writer.put(ac.code[0xF0U], ac.size[0xF0U]);
            run -= 16U;
        }
        const std::uint32_t bits = magnitude_bits(value);
        const std::uint32_t symbol = (run << 4U) | bits;
        writer.put(ac.code[symbol], ac.size[symbol]);
        put_value(writer, value, bits);
        run = 0U;
    }
    if (run != 0U) {
        writer.put(ac.code[0U], ac.size[0U]);
    }
}

} // namespace

bool try_encode_jpeg_rgba(
    std::span<const std::byte> rgba,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t quality,
    std::vector<std::byte>& output,
    image_error* error) noexcept {
    const auto fail = [&](image_error value) noexcept {
        if (error != nullptr) {
            *error = value;
        }
        return false;
    };
    std::size_t bytes = 0U;
    if (!detail::raster_bytes(width, height, bytes) || rgba.size() < bytes ||
        width > 65535U || height > 65535U) {
        return fail(image_error::invalid_argument);
    }
    const std::uint32_t level = std::clamp<std::uint32_t>(quality, 1U, 100U);
    const bool subsampled = level < 90U;
    try {
        output.clear();
        put_u8(output, 0xFFU);
        put_u8(output, 0xD8U);
        put_marker(output, 0xE0U, 14U);
        for (const char letter : {'J', 'F', 'I', 'F', '\0'}) {
            put_u8(output, static_cast<std::uint8_t>(letter));
        }
        put_u8(output, 1U);
        put_u8(output, 1U);
        put_u8(output, 0U);
        put_u16(output, 1U);
        put_u16(output, 1U);
        put_u8(output, 0U);
        put_u8(output, 0U);

        const auto luma_table = scaled_table(base_luma, level);
        const auto chroma_table = scaled_table(base_chroma, level);
        for (std::uint32_t slot = 0U; slot < 2U; ++slot) {
            const auto& table = slot == 0U ? luma_table : chroma_table;
            put_marker(output, 0xDBU, 65U);
            put_u8(output, slot);
            for (const std::uint8_t position : zigzag) {
                put_u8(output, table[position]);
            }
        }
        put_marker(output, 0xC0U, 15U);
        put_u8(output, 8U);
        put_u16(output, height);
        put_u16(output, width);
        put_u8(output, 3U);
        put_u8(output, 1U);
        put_u8(output, subsampled ? 0x22U : 0x11U);
        put_u8(output, 0U);
        for (std::uint32_t id = 2U; id <= 3U; ++id) {
            put_u8(output, id);
            put_u8(output, 0x11U);
            put_u8(output, 1U);
        }
        put_huffman(output, 0x00U, dc_luma_counts, dc_values);
        put_huffman(output, 0x10U, ac_luma_counts, ac_luma_values);
        put_huffman(output, 0x01U, dc_chroma_counts, dc_values);
        put_huffman(output, 0x11U, ac_chroma_counts, ac_chroma_values);
        put_marker(output, 0xDAU, 10U);
        put_u8(output, 3U);
        put_u8(output, 1U);
        put_u8(output, 0x00U);
        put_u8(output, 2U);
        put_u8(output, 0x11U);
        put_u8(output, 3U);
        put_u8(output, 0x11U);
        put_u8(output, 0U);
        put_u8(output, 63U);
        put_u8(output, 0U);

        const auto dc_luma = build_code(dc_luma_counts, dc_values);
        const auto ac_luma = build_code(ac_luma_counts, ac_luma_values);
        const auto dc_chroma = build_code(dc_chroma_counts, dc_values);
        const auto ac_chroma = build_code(ac_chroma_counts, ac_chroma_values);
        const dct_basis basis;

        // The planes at full resolution, level shifted, edges repeated by the sampling below.
        const std::size_t pixels = std::size_t{width} * height;
        std::vector<float> luma(pixels);
        std::vector<float> blue(pixels);
        std::vector<float> red(pixels);
        for (std::size_t pixel = 0U; pixel < pixels; ++pixel) {
            const float r = static_cast<float>(detail::byte_at(rgba, pixel * 4U));
            const float g = static_cast<float>(detail::byte_at(rgba, pixel * 4U + 1U));
            const float b = static_cast<float>(detail::byte_at(rgba, pixel * 4U + 2U));
            luma[pixel] = 0.299F * r + 0.587F * g + 0.114F * b - 128.0F;
            blue[pixel] = -0.168736F * r - 0.331264F * g + 0.5F * b;
            red[pixel] = 0.5F * r - 0.418688F * g - 0.081312F * b;
        }
        const auto at = [&](const std::vector<float>& plane, std::uint32_t x, std::uint32_t y) noexcept {
            return plane[std::size_t{std::min(y, height - 1U)} * width + std::min(x, width - 1U)];
        };
        entropy_writer writer(output);
        std::int32_t luma_dc = 0;
        std::int32_t blue_dc = 0;
        std::int32_t red_dc = 0;
        const std::uint32_t step = subsampled ? 16U : 8U;
        std::array<float, 64U> block{};
        const auto luma_block = [&](std::uint32_t left, std::uint32_t top) {
            for (std::uint32_t y = 0U; y < 8U; ++y) {
                for (std::uint32_t x = 0U; x < 8U; ++x) {
                    block[y * 8U + x] = at(luma, left + x, top + y);
                }
            }
            encode_block(writer, block, luma_table, basis, dc_luma, ac_luma, luma_dc);
        };
        const auto chroma_block = [&](const std::vector<float>& plane, std::uint32_t left,
                                      std::uint32_t top, std::int32_t& predictor) {
            for (std::uint32_t y = 0U; y < 8U; ++y) {
                for (std::uint32_t x = 0U; x < 8U; ++x) {
                    if (subsampled) {
                        const std::uint32_t sx = left + x * 2U;
                        const std::uint32_t sy = top + y * 2U;
                        block[y * 8U + x] = (at(plane, sx, sy) + at(plane, sx + 1U, sy) +
                            at(plane, sx, sy + 1U) + at(plane, sx + 1U, sy + 1U)) * 0.25F;
                    } else {
                        block[y * 8U + x] = at(plane, left + x, top + y);
                    }
                }
            }
            encode_block(writer, block, chroma_table, basis, dc_chroma, ac_chroma, predictor);
        };
        for (std::uint32_t top = 0U; top < height; top += step) {
            for (std::uint32_t left = 0U; left < width; left += step) {
                if (subsampled) {
                    luma_block(left, top);
                    luma_block(left + 8U, top);
                    luma_block(left, top + 8U);
                    luma_block(left + 8U, top + 8U);
                } else {
                    luma_block(left, top);
                }
                chroma_block(blue, left, top, blue_dc);
                chroma_block(red, left, top, red_dc);
            }
        }
        writer.finish();
        put_u8(output, 0xFFU);
        put_u8(output, 0xD9U);
    } catch (const std::bad_alloc&) {
        return fail(image_error::out_of_memory);
    } catch (...) {
        return fail(image_error::out_of_memory);
    }
    if (error != nullptr) {
        *error = image_error::none;
    }
    return true;
}

} // namespace progpu::native::image
