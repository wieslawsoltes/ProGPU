// The JPEG reader: baseline, extended sequential and progressive Huffman at eight
// bits a sample. Every scan decodes into coefficient blocks, so the sequential and
// the progressive paths share the inverse transform, the upsampling and the colour
// conversion. Entropy data that ends early reads as zero bits: a truncated file
// shows what it holds.

#include "progpu_native_raster_internal.hpp"

#include <algorithm>
#include <array>
#include <cstring>
#include <memory>
#include <vector>

namespace progpu::native::image::detail {

namespace {

constexpr std::uint8_t marker_sof0 = 0xC0U;
constexpr std::uint8_t marker_sof1 = 0xC1U;
constexpr std::uint8_t marker_sof2 = 0xC2U;
constexpr std::uint8_t marker_dht = 0xC4U;
constexpr std::uint8_t marker_soi = 0xD8U;
constexpr std::uint8_t marker_eoi = 0xD9U;
constexpr std::uint8_t marker_sos = 0xDAU;
constexpr std::uint8_t marker_dqt = 0xDBU;
constexpr std::uint8_t marker_dri = 0xDDU;
constexpr std::uint8_t marker_app14 = 0xEEU;
constexpr std::size_t block_size = 64U;
constexpr std::uint32_t fast_bits = 9U;

constexpr std::array<std::uint8_t, 64U> zigzag{
    0U,  1U,  8U,  16U, 9U,  2U,  3U,  10U,
    17U, 24U, 32U, 25U, 18U, 11U, 4U,  5U,
    12U, 19U, 26U, 33U, 40U, 48U, 41U, 34U,
    27U, 20U, 13U, 6U,  7U,  14U, 21U, 28U,
    35U, 42U, 49U, 56U, 57U, 50U, 43U, 36U,
    29U, 22U, 15U, 23U, 30U, 37U, 44U, 51U,
    58U, 59U, 52U, 45U, 38U, 31U, 39U, 46U,
    53U, 60U, 61U, 54U, 47U, 55U, 62U, 63U};

struct huffman_table final {
    bool defined = false;
    std::array<std::int16_t, 1U << fast_bits> fast{};
    std::array<std::int32_t, 18U> max_code{};
    std::array<std::int32_t, 17U> value_offset{};
    std::array<std::uint8_t, 256U> values{};
};

bool build_huffman(
    huffman_table& table,
    std::span<const std::uint8_t, 16U> counts,
    std::span<const std::byte> values) noexcept {
    table = {};
    table.fast.fill(-1);
    std::size_t total = 0U;
    for (const std::uint8_t count : counts) {
        total += count;
    }
    if (total > table.values.size() || total > values.size()) {
        return false;
    }
    for (std::size_t index = 0U; index < total; ++index) {
        table.values[index] = static_cast<std::uint8_t>(values[index]);
    }
    std::int32_t code = 0;
    std::int32_t offset = 0;
    for (std::uint32_t length = 1U; length <= 16U; ++length) {
        const std::int32_t count = counts[length - 1U];
        table.value_offset[length] = offset - code;
        if (count > 0) {
            if (code + count > (1 << length)) {
                return false;
            }
            if (length <= fast_bits) {
                for (std::int32_t entry = 0; entry < count; ++entry) {
                    const std::uint32_t shift = fast_bits - length;
                    const auto first =
                        static_cast<std::uint32_t>(code + entry) << shift;
                    const std::uint32_t span_of = 1U << shift;
                    for (std::uint32_t fill = 0U; fill < span_of; ++fill) {
                        const auto symbol = static_cast<std::uint32_t>(offset + entry);
                        table.fast[first + fill] =
                            static_cast<std::int16_t>((length << 8U) | table.values[symbol]);
                    }
                }
            }
            code += count;
            offset += count;
            table.max_code[length] = code - 1;
        } else {
            table.max_code[length] = -1;
        }
        code <<= 1;
    }
    table.max_code[17] = 0x7FFFFFFF;
    table.defined = true;
    return true;
}

// The entropy-coded bits of a scan. Stops at a marker and reads zeros from there.
class bit_reader final {
public:
    bit_reader(std::span<const std::byte> input, std::size_t at) noexcept
        : input_(input), at_(at) {}

    std::size_t position() const noexcept {
        return at_;
    }

    void reset() noexcept {
        bits_ = 0U;
        count_ = 0U;
    }

    void fill() noexcept {
        while (count_ <= 24U) {
            std::uint32_t next = 0U;
            if (at_ < input_.size()) {
                const std::uint8_t value = byte_at(input_, at_);
                if (value != 0xFFU) {
                    next = value;
                    ++at_;
                } else if (at_ + 1U < input_.size() && byte_at(input_, at_ + 1U) == 0U) {
                    next = 0xFFU;
                    at_ += 2U;
                }
                // Any other 0xFF is a marker: it stays, and zero bits follow.
            }
            bits_ |= next << (24U - count_);
            count_ += 8U;
        }
    }

    std::uint32_t peek(std::uint32_t count) noexcept {
        if (count_ < count) {
            fill();
        }
        return bits_ >> (32U - count);
    }

    void drop(std::uint32_t count) noexcept {
        bits_ <<= count;
        count_ -= count;
    }

    std::uint32_t read(std::uint32_t count) noexcept {
        if (count == 0U) {
            return 0U;
        }
        const std::uint32_t value = peek(count);
        drop(count);
        return value;
    }

    std::uint32_t read_bit() noexcept {
        return read(1U);
    }

    // Reads `count` bits and extends them to a signed difference (T.81, F.2.2.1).
    std::int32_t receive_extend(std::uint32_t count) noexcept {
        if (count == 0U) {
            return 0;
        }
        const auto value = static_cast<std::int32_t>(read(count));
        return value < (1 << (count - 1U)) ? value - (1 << count) + 1 : value;
    }

    bool decode(const huffman_table& table, std::uint32_t& symbol) noexcept {
        const std::uint32_t look = peek(fast_bits);
        const std::int32_t fast = table.fast[look];
        if (fast >= 0) {
            drop(static_cast<std::uint32_t>(fast) >> 8U);
            symbol = static_cast<std::uint32_t>(fast) & 0xFFU;
            return true;
        }
        const auto wide = static_cast<std::int32_t>(peek(16U));
        for (std::uint32_t length = 1U; length <= 16U; ++length) {
            const std::int32_t code = wide >> (16U - length);
            if (table.max_code[length] >= 0 && code <= table.max_code[length]) {
                const std::int32_t index = code + table.value_offset[length];
                if (index < 0 || index >= 256) {
                    return false;
                }
                drop(length);
                symbol = table.values[static_cast<std::size_t>(index)];
                return true;
            }
        }
        return false;
    }

private:
    std::span<const std::byte> input_;
    std::size_t at_;
    std::uint32_t bits_ = 0U;
    std::uint32_t count_ = 0U;
};

struct component final {
    std::uint8_t id = 0U;
    std::uint32_t horizontal = 1U;
    std::uint32_t vertical = 1U;
    std::uint32_t quantization = 0U;
    std::uint32_t dc_table = 0U;
    std::uint32_t ac_table = 0U;
    // Blocks stored, and those a scan of this component alone walks.
    std::uint32_t blocks_wide = 0U;
    std::uint32_t blocks_high = 0U;
    std::uint32_t scan_blocks_wide = 0U;
    std::uint32_t scan_blocks_high = 0U;
    // The samples that belong to the image.
    std::uint32_t sample_width = 0U;
    std::uint32_t sample_height = 0U;
    std::int32_t dc_predictor = 0;
    std::vector<std::int16_t> coefficients{};
    std::vector<std::uint8_t> samples{};

    std::int16_t* block(std::uint32_t x, std::uint32_t y) noexcept {
        const std::size_t index = std::size_t{y} * blocks_wide + x;
        return coefficients.data() + index * block_size;
    }
};

struct frame final {
    bool defined = false;
    bool progressive = false;
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    std::uint32_t max_horizontal = 1U;
    std::uint32_t max_vertical = 1U;
    std::uint32_t mcus_wide = 0U;
    std::uint32_t mcus_high = 0U;
    std::array<component, 4U> components{};
    std::uint32_t component_count = 0U;
};

std::uint32_t divide_up(std::uint32_t value, std::uint32_t by) noexcept {
    return static_cast<std::uint32_t>(
        (static_cast<std::uint64_t>(value) + by - 1U) / by);
}

// Parses a frame header (the payload of a SOF marker).
bool parse_frame(
    std::span<const std::byte> payload,
    std::uint8_t marker,
    frame& result,
    image_error& error) noexcept {
    error = image_error::invalid_chunk;
    if (payload.size() < 6U) {
        return false;
    }
    if (byte_at(payload, 0U) != 8U) {
        error = image_error::unsupported_format;
        return false;
    }
    result.progressive = marker == marker_sof2;
    result.height = read_u16_be(payload, 1U);
    result.width = read_u16_be(payload, 3U);
    result.component_count = byte_at(payload, 5U);
    if (result.width == 0U || result.height == 0U) {
        // A height given later by a DNL marker is not read.
        error = image_error::unsupported_format;
        return false;
    }
    const std::uint32_t count = result.component_count;
    if ((count != 1U && count != 3U && count != 4U) ||
        payload.size() < 6U + std::size_t{count} * 3U) {
        return false;
    }
    result.max_horizontal = 1U;
    result.max_vertical = 1U;
    for (std::uint32_t index = 0U; index < count; ++index) {
        auto& part = result.components[index];
        const std::size_t at = 6U + std::size_t{index} * 3U;
        part.id = byte_at(payload, at);
        const std::uint32_t sampling = byte_at(payload, at + 1U);
        part.horizontal = sampling >> 4U;
        part.vertical = sampling & 0x0FU;
        part.quantization = byte_at(payload, at + 2U);
        if (part.horizontal < 1U || part.horizontal > 4U || part.vertical < 1U ||
            part.vertical > 4U || part.quantization > 3U) {
            return false;
        }
        result.max_horizontal = std::max(result.max_horizontal, part.horizontal);
        result.max_vertical = std::max(result.max_vertical, part.vertical);
    }
    result.mcus_wide = divide_up(result.width, 8U * result.max_horizontal);
    result.mcus_high = divide_up(result.height, 8U * result.max_vertical);
    for (std::uint32_t index = 0U; index < count; ++index) {
        auto& part = result.components[index];
        part.blocks_wide = result.mcus_wide * part.horizontal;
        part.blocks_high = result.mcus_high * part.vertical;
        part.sample_width = divide_up(result.width * part.horizontal, result.max_horizontal);
        part.sample_height = divide_up(result.height * part.vertical, result.max_vertical);
        part.scan_blocks_wide = divide_up(part.sample_width, 8U);
        part.scan_blocks_high = divide_up(part.sample_height, 8U);
    }
    result.defined = true;
    return true;
}

// Finds the next marker from `at`. Leaves `at` on the byte after it.
bool next_marker(
    std::span<const std::byte> input,
    std::size_t& at,
    std::uint8_t& marker) noexcept {
    while (at + 1U < input.size()) {
        if (byte_at(input, at) != 0xFFU) {
            ++at;
            continue;
        }
        const std::uint8_t value = byte_at(input, at + 1U);
        if (value == 0U) {
            at += 2U;
        } else if (value == 0xFFU) {
            ++at;
        } else {
            marker = value;
            at += 2U;
            return true;
        }
    }
    return false;
}

bool is_sof(std::uint8_t marker) noexcept {
    return marker >= 0xC0U && marker <= 0xCFU && marker != marker_dht &&
        marker != 0xC8U && marker != 0xCCU;
}

bool has_length(std::uint8_t marker) noexcept {
    return !(marker == marker_soi || marker == marker_eoi || marker == 0x01U ||
        (marker >= 0xD0U && marker <= 0xD7U));
}

// One scan: its components, spectral selection and successive approximation.
struct scan_state final {
    std::array<component*, 4U> parts{};
    std::uint32_t count = 0U;
    std::uint32_t spectral_start = 0U;
    std::uint32_t spectral_end = 63U;
    std::uint32_t approx_high = 0U;
    std::uint32_t approx_low = 0U;
    std::uint32_t end_of_band = 0U;
};

using huffman_set = std::array<huffman_table, 4U>;

// The DC predictor of a damaged file may run past its range.
std::int32_t add_wrapping(std::int32_t value, std::int32_t difference) noexcept {
    return static_cast<std::int32_t>(
        static_cast<std::uint32_t>(value) + static_cast<std::uint32_t>(difference));
}

bool decode_block_baseline(
    bit_reader& bits,
    component& part,
    std::int16_t* block,
    const huffman_set& dc,
    const huffman_set& ac) noexcept {
    std::uint32_t symbol = 0U;
    if (!bits.decode(dc[part.dc_table], symbol) || symbol > 15U) {
        return false;
    }
    part.dc_predictor = add_wrapping(part.dc_predictor, bits.receive_extend(symbol));
    block[0] = static_cast<std::int16_t>(part.dc_predictor);
    for (std::uint32_t k = 1U; k < 64U;) {
        if (!bits.decode(ac[part.ac_table], symbol)) {
            return false;
        }
        const std::uint32_t run = symbol >> 4U;
        const std::uint32_t size = symbol & 0x0FU;
        if (size == 0U) {
            if (run != 15U) {
                break;
            }
            k += 16U;
            continue;
        }
        k += run;
        if (k >= 64U) {
            return false;
        }
        block[zigzag[k]] = static_cast<std::int16_t>(bits.receive_extend(size));
        ++k;
    }
    return true;
}

bool decode_block_dc_progressive(
    bit_reader& bits,
    component& part,
    std::int16_t* block,
    const huffman_set& dc,
    const scan_state& scan) noexcept {
    if (scan.approx_high == 0U) {
        std::uint32_t symbol = 0U;
        if (!bits.decode(dc[part.dc_table], symbol) || symbol > 15U) {
            return false;
        }
        part.dc_predictor = add_wrapping(part.dc_predictor, bits.receive_extend(symbol));
        block[0] = static_cast<std::int16_t>(
            static_cast<std::uint32_t>(part.dc_predictor) << scan.approx_low);
    } else if (bits.read_bit() != 0U) {
        block[0] = static_cast<std::int16_t>(block[0] | (1 << scan.approx_low));
    }
    return true;
}

bool decode_block_ac_first(
    bit_reader& bits,
    component& part,
    std::int16_t* block,
    const huffman_set& ac,
    scan_state& scan) noexcept {
    if (scan.end_of_band > 0U) {
        --scan.end_of_band;
        return true;
    }
    for (std::uint32_t k = scan.spectral_start; k <= scan.spectral_end;) {
        std::uint32_t symbol = 0U;
        if (!bits.decode(ac[part.ac_table], symbol)) {
            return false;
        }
        const std::uint32_t run = symbol >> 4U;
        const std::uint32_t size = symbol & 0x0FU;
        if (size == 0U) {
            if (run < 15U) {
                scan.end_of_band = (1U << run) - 1U + bits.read(run);
                break;
            }
            k += 16U;
            continue;
        }
        k += run;
        if (k > scan.spectral_end) {
            return false;
        }
        const std::int32_t value = bits.receive_extend(size);
        block[zigzag[k]] = static_cast<std::int16_t>(value * (1 << scan.approx_low));
        ++k;
    }
    return true;
}

bool decode_block_ac_refine(
    bit_reader& bits,
    component& part,
    std::int16_t* block,
    const huffman_set& ac,
    scan_state& scan) noexcept {
    const auto positive = static_cast<std::int16_t>(1 << scan.approx_low);
    const auto negative = static_cast<std::int16_t>(-(1 << scan.approx_low));
    const auto refine = [&](std::int16_t& value) noexcept {
        if (bits.read_bit() != 0U && (value & positive) == 0) {
            value = static_cast<std::int16_t>(value + (value >= 0 ? positive : negative));
        }
    };
    std::uint32_t k = scan.spectral_start;
    if (scan.end_of_band == 0U) {
        while (k <= scan.spectral_end) {
            std::uint32_t symbol = 0U;
            if (!bits.decode(ac[part.ac_table], symbol)) {
                return false;
            }
            std::uint32_t run = symbol >> 4U;
            const std::uint32_t size = symbol & 0x0FU;
            std::int16_t placed = 0;
            if (size == 0U) {
                if (run < 15U) {
                    scan.end_of_band = (1U << run) + bits.read(run);
                    break;
                }
            } else {
                if (size != 1U) {
                    return false;
                }
                placed = bits.read_bit() != 0U ? positive : negative;
            }
            // Skips `run` zero coefficients, refining the nonzero ones on the way.
            while (k <= scan.spectral_end) {
                std::int16_t& value = block[zigzag[k]];
                ++k;
                if (value != 0) {
                    refine(value);
                } else {
                    if (run == 0U) {
                        if (placed != 0) {
                            value = placed;
                        }
                        break;
                    }
                    --run;
                }
            }
        }
    }
    if (scan.end_of_band > 0U) {
        for (; k <= scan.spectral_end; ++k) {
            std::int16_t& value = block[zigzag[k]];
            if (value != 0) {
                refine(value);
            }
        }
        --scan.end_of_band;
    }
    return true;
}

bool decode_block(
    bit_reader& bits,
    const frame& image,
    component& part,
    std::int16_t* block,
    const huffman_set& dc,
    const huffman_set& ac,
    scan_state& scan) noexcept {
    if (!image.progressive) {
        return decode_block_baseline(bits, part, block, dc, ac);
    }
    if (scan.spectral_start == 0U) {
        return decode_block_dc_progressive(bits, part, block, dc, scan);
    }
    return scan.approx_high == 0U
        ? decode_block_ac_first(bits, part, block, ac, scan)
        : decode_block_ac_refine(bits, part, block, ac, scan);
}

// Moves the reader past the restart marker that ends an interval. `false` when
// the next marker is not a restart marker: the scan ends there.
bool next_restart(
    std::span<const std::byte> input,
    bit_reader& bits,
    std::size_t& resume) noexcept {
    std::size_t at = bits.position();
    std::uint8_t marker = 0U;
    if (!next_marker(input, at, marker)) {
        resume = input.size();
        return false;
    }
    if (marker < 0xD0U || marker > 0xD7U) {
        resume = at - 2U;
        return false;
    }
    resume = at;
    return true;
}

bool decode_scan(
    std::span<const std::byte> input,
    std::size_t& at,
    frame& image,
    scan_state& scan,
    const huffman_set& dc,
    const huffman_set& ac,
    std::uint32_t restart_interval,
    image_error& error) noexcept {
    error = image_error::invalid_compressed_data;
    std::size_t start = at;
    bit_reader bits(input, start);
    for (std::uint32_t index = 0U; index < scan.count; ++index) {
        scan.parts[index]->dc_predictor = 0;
    }
    scan.end_of_band = 0U;

    const bool single = scan.count == 1U;
    const std::uint32_t units_wide = single ? scan.parts[0]->scan_blocks_wide : image.mcus_wide;
    const std::uint32_t units_high = single ? scan.parts[0]->scan_blocks_high : image.mcus_high;
    std::uint32_t until_restart = restart_interval;

    for (std::uint32_t y = 0U; y < units_high; ++y) {
        for (std::uint32_t x = 0U; x < units_wide; ++x) {
            if (restart_interval != 0U && until_restart == 0U) {
                std::size_t resume = 0U;
                if (!next_restart(input, bits, resume)) {
                    at = resume;
                    return true;
                }
                bits = bit_reader(input, resume);
                for (std::uint32_t index = 0U; index < scan.count; ++index) {
                    scan.parts[index]->dc_predictor = 0;
                }
                scan.end_of_band = 0U;
                until_restart = restart_interval;
            }
            if (single) {
                component& part = *scan.parts[0];
                if (!decode_block(bits, image, part, part.block(x, y), dc, ac, scan)) {
                    at = bits.position();
                    return true;
                }
            } else {
                for (std::uint32_t index = 0U; index < scan.count; ++index) {
                    component& part = *scan.parts[index];
                    for (std::uint32_t v = 0U; v < part.vertical; ++v) {
                        for (std::uint32_t h = 0U; h < part.horizontal; ++h) {
                            std::int16_t* block = part.block(
                                x * part.horizontal + h,
                                y * part.vertical + v);
                            if (!decode_block(bits, image, part, block, dc, ac, scan)) {
                                at = bits.position();
                                return true;
                            }
                        }
                    }
                }
            }
            if (restart_interval != 0U) {
                --until_restart;
            }
        }
    }
    at = bits.position();
    return true;
}

std::uint8_t clamp_sample(std::int32_t value) noexcept {
    return static_cast<std::uint8_t>(std::clamp(value, 0, 255));
}

// The inverse DCT of one dequantized block, in 13-bit fixed point (the even and
// odd parts of the Loeffler, Ligtenberg and Moschytz factorization).
// In 64 bits: the coefficients of a damaged file can be as large as their type.
void inverse_dct(
    const std::int16_t* coefficients,
    const std::array<std::uint16_t, 64U>& quantization,
    std::uint8_t* output,
    std::size_t stride) noexcept {
    constexpr std::int64_t fix_0_298 = 2446;
    constexpr std::int64_t fix_0_390 = 3196;
    constexpr std::int64_t fix_0_541 = 4433;
    constexpr std::int64_t fix_0_765 = 6270;
    constexpr std::int64_t fix_0_899 = 7373;
    constexpr std::int64_t fix_1_175 = 9633;
    constexpr std::int64_t fix_1_501 = 12299;
    constexpr std::int64_t fix_1_847 = 15137;
    constexpr std::int64_t fix_1_961 = 16069;
    constexpr std::int64_t fix_2_053 = 16819;
    constexpr std::int64_t fix_2_562 = 20995;
    constexpr std::int64_t fix_3_072 = 25172;
    constexpr int const_bits = 13;
    constexpr int pass1_bits = 2;

    std::array<std::int64_t, 64U> work{};
    for (std::size_t column = 0U; column < 8U; ++column) {
        const auto in = [&](std::size_t row) noexcept {
            const std::size_t at = row * 8U + column;
            return static_cast<std::int64_t>(coefficients[at]) *
                static_cast<std::int64_t>(quantization[at]);
        };
        const std::int64_t z2 = in(2U);
        const std::int64_t z3 = in(6U);
        const std::int64_t z1 = (z2 + z3) * fix_0_541;
        const std::int64_t even2 = z1 + z3 * -fix_1_847;
        const std::int64_t even3 = z1 + z2 * fix_0_765;
        const std::int64_t even0 = (in(0U) + in(4U)) * (1 << const_bits);
        const std::int64_t even1 = (in(0U) - in(4U)) * (1 << const_bits);
        const std::int64_t t10 = even0 + even3;
        const std::int64_t t13 = even0 - even3;
        const std::int64_t t11 = even1 + even2;
        const std::int64_t t12 = even1 - even2;

        std::int64_t o0 = in(7U);
        std::int64_t o1 = in(5U);
        std::int64_t o2 = in(3U);
        std::int64_t o3 = in(1U);
        const std::int64_t p1 = o0 + o3;
        const std::int64_t p2 = o1 + o2;
        std::int64_t p3 = o0 + o2;
        std::int64_t p4 = o1 + o3;
        const std::int64_t p5 = (p3 + p4) * fix_1_175;
        o0 *= fix_0_298;
        o1 *= fix_2_053;
        o2 *= fix_3_072;
        o3 *= fix_1_501;
        const std::int64_t q1 = p1 * -fix_0_899;
        const std::int64_t q2 = p2 * -fix_2_562;
        p3 = p3 * -fix_1_961 + p5;
        p4 = p4 * -fix_0_390 + p5;
        o0 += q1 + p3;
        o1 += q2 + p4;
        o2 += q2 + p3;
        o3 += q1 + p4;

        const int shift = const_bits - pass1_bits;
        const std::int64_t round = 1 << (shift - 1);
        work[column + 0U * 8U] = (t10 + o3 + round) >> shift;
        work[column + 7U * 8U] = (t10 - o3 + round) >> shift;
        work[column + 1U * 8U] = (t11 + o2 + round) >> shift;
        work[column + 6U * 8U] = (t11 - o2 + round) >> shift;
        work[column + 2U * 8U] = (t12 + o1 + round) >> shift;
        work[column + 5U * 8U] = (t12 - o1 + round) >> shift;
        work[column + 3U * 8U] = (t13 + o0 + round) >> shift;
        work[column + 4U * 8U] = (t13 - o0 + round) >> shift;
    }
    for (std::size_t row = 0U; row < 8U; ++row) {
        const std::int64_t* in = work.data() + row * 8U;
        const std::int64_t z2 = in[2];
        const std::int64_t z3 = in[6];
        const std::int64_t z1 = (z2 + z3) * fix_0_541;
        const std::int64_t even2 = z1 + z3 * -fix_1_847;
        const std::int64_t even3 = z1 + z2 * fix_0_765;
        const std::int64_t even0 = (in[0] + in[4]) * (1 << const_bits);
        const std::int64_t even1 = (in[0] - in[4]) * (1 << const_bits);
        const std::int64_t t10 = even0 + even3;
        const std::int64_t t13 = even0 - even3;
        const std::int64_t t11 = even1 + even2;
        const std::int64_t t12 = even1 - even2;

        std::int64_t o0 = in[7];
        std::int64_t o1 = in[5];
        std::int64_t o2 = in[3];
        std::int64_t o3 = in[1];
        const std::int64_t p1 = o0 + o3;
        const std::int64_t p2 = o1 + o2;
        std::int64_t p3 = o0 + o2;
        std::int64_t p4 = o1 + o3;
        const std::int64_t p5 = (p3 + p4) * fix_1_175;
        o0 *= fix_0_298;
        o1 *= fix_2_053;
        o2 *= fix_3_072;
        o3 *= fix_1_501;
        const std::int64_t q1 = p1 * -fix_0_899;
        const std::int64_t q2 = p2 * -fix_2_562;
        p3 = p3 * -fix_1_961 + p5;
        p4 = p4 * -fix_0_390 + p5;
        o0 += q1 + p3;
        o1 += q2 + p4;
        o2 += q2 + p3;
        o3 += q1 + p4;

        const int shift = const_bits + pass1_bits + 3;
        const std::int64_t round = 1 << (shift - 1);
        const auto put = [&](std::size_t column, std::int64_t value) noexcept {
            const std::int64_t sample = ((value + round) >> shift) + 128;
            output[row * stride + column] =
                static_cast<std::uint8_t>(std::clamp<std::int64_t>(sample, 0, 255));
        };
        put(0U, t10 + o3);
        put(7U, t10 - o3);
        put(1U, t11 + o2);
        put(6U, t11 - o2);
        put(2U, t12 + o1);
        put(5U, t12 - o1);
        put(3U, t13 + o0);
        put(4U, t13 - o0);
    }
}

// One row of a component at the full width of the image. A factor of two uses
// the triangular filter of centred samples; any other ratio repeats samples.
void upsample_row(
    const component& part,
    const frame& image,
    std::uint32_t y,
    std::span<std::uint8_t> output,
    std::vector<std::int32_t>& wide) {
    const std::uint32_t scale_x = image.max_horizontal / part.horizontal;
    const std::uint32_t scale_y = image.max_vertical / part.vertical;
    const bool exact_x = scale_x * part.horizontal == image.max_horizontal;
    const bool exact_y = scale_y * part.vertical == image.max_vertical;
    const std::size_t stride = std::size_t{part.blocks_wide} * 8U;
    const std::uint32_t width = part.sample_width;
    const std::uint32_t height = part.sample_height;
    const std::uint32_t full = image.width;

    const auto sample_row = [&](std::uint32_t row) noexcept {
        return part.samples.data() + std::size_t{std::min(row, height - 1U)} * stride;
    };

    if (exact_x && exact_y && scale_x == 1U && scale_y == 1U) {
        std::memcpy(output.data(), sample_row(y), full);
        return;
    }
    if (exact_x && exact_y && scale_x == 2U && scale_y <= 2U) {
        // The vertical part first: three of the near row and one of the far row.
        wide.resize(width);
        std::int32_t weight = 1;
        if (scale_y == 2U) {
            const std::uint32_t row = y / 2U;
            const std::uint32_t other = (y & 1U) == 0U ? (row == 0U ? 0U : row - 1U) : row + 1U;
            const std::uint8_t* near_row = sample_row(row);
            const std::uint8_t* far_row = sample_row(other);
            for (std::uint32_t x = 0U; x < width; ++x) {
                wide[x] = 3 * near_row[x] + far_row[x];
            }
            weight = 4;
        } else {
            const std::uint8_t* row = sample_row(y);
            for (std::uint32_t x = 0U; x < width; ++x) {
                wide[x] = row[x];
            }
        }
        const std::int32_t divisor = weight * 4;
        for (std::uint32_t x = 0U; x < full; ++x) {
            const std::uint32_t source = x / 2U;
            const std::uint32_t neighbour = (x & 1U) == 0U
                ? (source == 0U ? 0U : source - 1U)
                : std::min(source + 1U, width - 1U);
            const std::int32_t rounding = (x & 1U) == 0U ? divisor / 2 - 1 : divisor / 2;
            output[x] = clamp_sample(
                (3 * wide[source] + wide[neighbour] + rounding) / divisor);
        }
        return;
    }
    if (exact_x && exact_y && scale_x == 1U && scale_y == 2U) {
        const std::uint32_t row = y / 2U;
        const std::uint32_t other = (y & 1U) == 0U ? (row == 0U ? 0U : row - 1U) : row + 1U;
        const std::uint8_t* near_row = sample_row(row);
        const std::uint8_t* far_row = sample_row(other);
        const std::int32_t rounding = (y & 1U) == 0U ? 1 : 2;
        for (std::uint32_t x = 0U; x < full; ++x) {
            output[x] = static_cast<std::uint8_t>((3 * near_row[x] + far_row[x] + rounding) >> 2);
        }
        return;
    }
    const std::uint32_t source_row = static_cast<std::uint32_t>(
        std::uint64_t{y} * part.vertical / image.max_vertical);
    const std::uint8_t* row = sample_row(source_row);
    for (std::uint32_t x = 0U; x < full; ++x) {
        const auto source = static_cast<std::uint32_t>(
            std::uint64_t{x} * part.horizontal / image.max_horizontal);
        output[x] = row[std::min(source, width - 1U)];
    }
}

void write_ycc(
    std::span<std::byte> rgba,
    std::size_t pixel,
    std::int32_t luma,
    std::int32_t blue_chroma,
    std::int32_t red_chroma,
    std::uint8_t alpha) noexcept {
    const std::int32_t cb = blue_chroma - 128;
    const std::int32_t cr = red_chroma - 128;
    const std::int32_t scaled = luma * 65536 + 32768;
    write_rgba(
        rgba,
        pixel,
        clamp_sample((scaled + 91881 * cr) >> 16),
        clamp_sample((scaled - 22554 * cb - 46802 * cr) >> 16),
        clamp_sample((scaled + 116130 * cb) >> 16),
        alpha);
}

std::uint8_t scale_by(std::uint32_t value, std::uint32_t by) noexcept {
    const std::uint32_t product = value * by + 128U;
    return static_cast<std::uint8_t>((product + (product >> 8U)) >> 8U);
}

void convert_colors(
    frame& image,
    bool has_adobe,
    std::uint32_t adobe_transform,
    bool has_jfif,
    std::span<std::byte> rgba) {
    const std::uint32_t count = image.component_count;
    std::array<std::vector<std::uint8_t>, 4U> rows{};
    for (std::uint32_t index = 0U; index < count; ++index) {
        rows[index].resize(image.width);
    }
    std::vector<std::int32_t> wide;
    const bool named_rgb = count == 3U && image.components[0].id == 'R' &&
        image.components[1].id == 'G' && image.components[2].id == 'B';
    const bool ycc = count == 3U &&
        (has_adobe ? adobe_transform != 0U : (has_jfif || !named_rgb));
    const bool ycck = count == 4U && has_adobe && adobe_transform == 2U;
    for (std::uint32_t y = 0U; y < image.height; ++y) {
        for (std::uint32_t index = 0U; index < count; ++index) {
            upsample_row(image.components[index], image, y, rows[index], wide);
        }
        const std::size_t first = std::size_t{y} * image.width;
        for (std::uint32_t x = 0U; x < image.width; ++x) {
            const std::size_t pixel = first + x;
            if (count == 1U) {
                const std::uint8_t value = rows[0][x];
                write_rgba(rgba, pixel, value, value, value, 255U);
            } else if (count == 3U) {
                if (ycc) {
                    write_ycc(rgba, pixel, rows[0][x], rows[1][x], rows[2][x], 255U);
                } else {
                    write_rgba(rgba, pixel, rows[0][x], rows[1][x], rows[2][x], 255U);
                }
            } else {
                std::uint32_t c = rows[0][x];
                std::uint32_t m = rows[1][x];
                std::uint32_t yellow = rows[2][x];
                std::uint32_t k = rows[3][x];
                if (ycck) {
                    // The three first channels are YCC of the inverted CMY.
                    write_ycc(rgba, pixel, rows[0][x], rows[1][x], rows[2][x], 255U);
                    const std::size_t at = pixel * 4U;
                    c = static_cast<std::uint32_t>(rgba[at]);
                    m = static_cast<std::uint32_t>(rgba[at + 1U]);
                    yellow = static_cast<std::uint32_t>(rgba[at + 2U]);
                } else if (!has_adobe) {
                    // Without the Adobe marker the ink values are as they are.
                    c = 255U - c;
                    m = 255U - m;
                    yellow = 255U - yellow;
                    k = 255U - k;
                }
                write_rgba(rgba, pixel, scale_by(c, k), scale_by(m, k), scale_by(yellow, k), 255U);
            }
        }
    }
}

} // namespace

bool read_jpeg_size(
    std::span<const std::byte> input,
    raster_image_size& result,
    image_error& error) noexcept {
    error = image_error::invalid_chunk;
    std::size_t at = 2U;
    std::uint8_t marker = 0U;
    while (next_marker(input, at, marker)) {
        if (marker == marker_eoi || marker == marker_sos) {
            return false;
        }
        if (!has_length(marker)) {
            continue;
        }
        if (at + 2U > input.size()) {
            return false;
        }
        const std::size_t length = read_u16_be(input, at);
        if (length < 2U || length > input.size() - at) {
            return false;
        }
        if (is_sof(marker)) {
            if (marker != marker_sof0 && marker != marker_sof1 && marker != marker_sof2) {
                error = image_error::unsupported_format;
                return false;
            }
            auto image = std::make_unique<frame>();
            if (!parse_frame(input.subspan(at + 2U, length - 2U), marker, *image, error)) {
                return false;
            }
            if (!raster_bytes(image->width, image->height, result.rgba_bytes)) {
                error = image_error::unsupported_format;
                return false;
            }
            result.width = image->width;
            result.height = image->height;
            return true;
        }
        at += length;
    }
    return false;
}

bool decode_jpeg(
    std::span<const std::byte> input,
    std::span<std::byte> rgba,
    image_error& error) {
    auto image_owner = std::make_unique<frame>();
    frame& image = *image_owner;
    auto dc_owner = std::make_unique<huffman_set>();
    auto ac_owner = std::make_unique<huffman_set>();
    huffman_set& dc = *dc_owner;
    huffman_set& ac = *ac_owner;
    std::array<std::array<std::uint16_t, 64U>, 4U> quantization{};
    std::array<bool, 4U> has_quantization{};
    std::uint32_t restart_interval = 0U;
    bool has_adobe = false;
    bool has_jfif = false;
    std::uint32_t adobe_transform = 0U;
    bool decoded_scan = false;

    std::size_t at = 2U;
    std::uint8_t marker = 0U;
    error = image_error::invalid_chunk;
    while (next_marker(input, at, marker)) {
        if (marker == marker_eoi) {
            break;
        }
        if (!has_length(marker)) {
            continue;
        }
        if (at + 2U > input.size()) {
            break;
        }
        const std::size_t length = read_u16_be(input, at);
        if (length < 2U || length > input.size() - at) {
            break;
        }
        const auto payload = input.subspan(at + 2U, length - 2U);
        if (marker == marker_dqt) {
            std::size_t cursor = 0U;
            while (cursor < payload.size()) {
                const std::uint32_t spec = byte_at(payload, cursor);
                ++cursor;
                const std::uint32_t precision = spec >> 4U;
                const std::uint32_t slot = spec & 0x0FU;
                const std::size_t entry = precision == 0U ? 1U : 2U;
                if (precision > 1U || slot > 3U || payload.size() - cursor < 64U * entry) {
                    return false;
                }
                for (std::size_t index = 0U; index < 64U; ++index) {
                    quantization[slot][zigzag[index]] = precision == 0U
                        ? byte_at(payload, cursor + index)
                        : read_u16_be(payload, cursor + index * 2U);
                }
                has_quantization[slot] = true;
                cursor += 64U * entry;
            }
        } else if (marker == marker_dht) {
            std::size_t cursor = 0U;
            while (cursor + 17U <= payload.size()) {
                const std::uint32_t spec = byte_at(payload, cursor);
                const std::uint32_t kind = spec >> 4U;
                const std::uint32_t slot = spec & 0x0FU;
                if (kind > 1U || slot > 3U) {
                    return false;
                }
                std::array<std::uint8_t, 16U> counts{};
                std::size_t total = 0U;
                for (std::size_t index = 0U; index < 16U; ++index) {
                    counts[index] = byte_at(payload, cursor + 1U + index);
                    total += counts[index];
                }
                cursor += 17U;
                if (total > payload.size() - cursor ||
                    !build_huffman(
                        kind == 0U ? dc[slot] : ac[slot],
                        std::span<const std::uint8_t, 16U>(counts),
                        payload.subspan(cursor, total))) {
                    return false;
                }
                cursor += total;
            }
        } else if (is_sof(marker)) {
            if (image.defined) {
                error = image_error::unsupported_format;
                return false;
            }
            if (marker != marker_sof0 && marker != marker_sof1 && marker != marker_sof2) {
                error = image_error::unsupported_format;
                return false;
            }
            if (!parse_frame(payload, marker, image, error)) {
                return false;
            }
            error = image_error::invalid_chunk;
            for (std::uint32_t index = 0U; index < image.component_count; ++index) {
                auto& part = image.components[index];
                const std::size_t blocks = std::size_t{part.blocks_wide} * part.blocks_high;
                part.coefficients.assign(blocks * block_size, 0);
            }
        } else if (marker == marker_dri) {
            if (payload.size() < 2U) {
                return false;
            }
            restart_interval = read_u16_be(payload, 0U);
        } else if (marker == 0xE0U) {
            has_jfif = has_jfif || (payload.size() >= 5U &&
                std::memcmp(payload.data(), "JFIF", 5U) == 0);
        } else if (marker == marker_app14) {
            if (payload.size() >= 12U && std::memcmp(payload.data(), "Adobe", 5U) == 0) {
                has_adobe = true;
                adobe_transform = byte_at(payload, 11U);
            }
        } else if (marker == marker_sos) {
            if (!image.defined || payload.empty()) {
                return false;
            }
            scan_state scan{};
            scan.count = byte_at(payload, 0U);
            if (scan.count < 1U || scan.count > image.component_count ||
                payload.size() < 1U + std::size_t{scan.count} * 2U + 3U) {
                return false;
            }
            for (std::uint32_t index = 0U; index < scan.count; ++index) {
                const std::size_t entry = 1U + std::size_t{index} * 2U;
                const std::uint8_t id = byte_at(payload, entry);
                const std::uint32_t tables = byte_at(payload, entry + 1U);
                component* found = nullptr;
                for (std::uint32_t part = 0U; part < image.component_count; ++part) {
                    if (image.components[part].id == id) {
                        found = &image.components[part];
                        break;
                    }
                }
                if (found == nullptr || (tables >> 4U) > 3U || (tables & 0x0FU) > 3U) {
                    return false;
                }
                found->dc_table = tables >> 4U;
                found->ac_table = tables & 0x0FU;
                scan.parts[index] = found;
            }
            const std::size_t tail = 1U + std::size_t{scan.count} * 2U;
            scan.spectral_start = byte_at(payload, tail);
            scan.spectral_end = byte_at(payload, tail + 1U);
            const std::uint32_t approximation = byte_at(payload, tail + 2U);
            scan.approx_high = approximation >> 4U;
            scan.approx_low = approximation & 0x0FU;
            if (image.progressive) {
                if (scan.spectral_end > 63U || scan.spectral_start > scan.spectral_end ||
                    scan.approx_low > 13U ||
                    (scan.spectral_start == 0U && scan.spectral_end != 0U) ||
                    (scan.spectral_start != 0U && scan.count != 1U)) {
                    return false;
                }
            } else {
                scan.spectral_start = 0U;
                scan.spectral_end = 63U;
                scan.approx_high = 0U;
                scan.approx_low = 0U;
            }
            const bool needs_dc = !image.progressive ||
                (scan.spectral_start == 0U && scan.approx_high == 0U);
            const bool needs_ac = !image.progressive || scan.spectral_start != 0U;
            for (std::uint32_t index = 0U; index < scan.count; ++index) {
                const component& part = *scan.parts[index];
                if ((needs_dc && !dc[part.dc_table].defined) ||
                    (needs_ac && !ac[part.ac_table].defined)) {
                    return false;
                }
            }
            at += length;
            if (!decode_scan(input, at, image, scan, dc, ac, restart_interval, error)) {
                return false;
            }
            error = image_error::invalid_chunk;
            decoded_scan = true;
            continue;
        }
        at += length;
    }
    if (!image.defined || !decoded_scan) {
        error = image_error::invalid_compressed_data;
        return false;
    }
    for (std::uint32_t index = 0U; index < image.component_count; ++index) {
        auto& part = image.components[index];
        if (!has_quantization[part.quantization]) {
            return false;
        }
        const std::size_t stride = std::size_t{part.blocks_wide} * 8U;
        part.samples.resize(stride * (std::size_t{part.blocks_high} * 8U));
        for (std::uint32_t y = 0U; y < part.blocks_high; ++y) {
            for (std::uint32_t x = 0U; x < part.blocks_wide; ++x) {
                inverse_dct(
                    part.block(x, y),
                    quantization[part.quantization],
                    part.samples.data() + (std::size_t{y} * 8U) * stride + std::size_t{x} * 8U,
                    stride);
            }
        }
        part.coefficients = {};
    }
    convert_colors(image, has_adobe, adobe_transform, has_jfif, rgba);
    return true;
}

} // namespace progpu::native::image::detail
