#include "progpu_native_deflate_internal.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

// Algorithm: RFC 1951 block decoding with canonical Huffman tables and a
// caller-owned 32 KiB-compatible history window in the output span.
// Time complexity: O(I + 15S + O), where I is input bytes, S is decoded
// Huffman symbols, and O is output bytes. Space complexity: O(1), bounded by
// 640 code lengths and two fixed 288-symbol tables on the stack.
namespace progpu::native::compression::detail {
namespace {

constexpr std::array<std::uint16_t, 29U> length_bases{
    3U, 4U, 5U, 6U, 7U, 8U, 9U, 10U, 11U, 13U, 15U, 17U,
    19U, 23U, 27U, 31U, 35U, 43U, 51U, 59U, 67U, 83U, 99U,
    115U, 131U, 163U, 195U, 227U, 258U};
constexpr std::array<std::uint8_t, 29U> length_extras{
    0U, 0U, 0U, 0U, 0U, 0U, 0U, 0U, 1U, 1U, 1U, 1U,
    2U, 2U, 2U, 2U, 3U, 3U, 3U, 3U, 4U, 4U, 4U, 4U,
    5U, 5U, 5U, 5U, 0U};
constexpr std::array<std::uint16_t, 30U> distance_bases{
    1U, 2U, 3U, 4U, 5U, 7U, 9U, 13U, 17U, 25U, 33U, 49U,
    65U, 97U, 129U, 193U, 257U, 385U, 513U, 769U, 1025U,
    1537U, 2049U, 3073U, 4097U, 6145U, 8193U, 12289U, 16385U,
    24577U};
constexpr std::array<std::uint8_t, 30U> distance_extras{
    0U, 0U, 0U, 0U, 1U, 1U, 2U, 2U, 3U, 3U, 4U, 4U,
    5U, 5U, 6U, 6U, 7U, 7U, 8U, 8U, 9U, 9U, 10U, 10U,
    11U, 11U, 12U, 12U, 13U, 13U};
constexpr std::array<std::uint8_t, 19U> code_length_order{
    16U, 17U, 18U, 0U, 8U, 7U, 9U, 6U, 10U, 5U,
    11U, 4U, 12U, 3U, 13U, 2U, 14U, 1U, 15U};

bool try_read_extra(
    bit_reader& reader,
    std::uint8_t count,
    std::uint32_t& value) noexcept {
    value = 0U;
    return count == 0U || reader.try_read(count, value);
}

bool try_build_fixed(
    huffman_table& literal,
    huffman_table& distance) noexcept {
    std::array<std::uint8_t, 288U> literal_lengths{};
    std::fill_n(literal_lengths.begin(), 144U, 8U);
    std::fill_n(literal_lengths.begin() + 144U, 112U, 9U);
    std::fill_n(literal_lengths.begin() + 256U, 24U, 7U);
    std::fill_n(literal_lengths.begin() + 280U, 8U, 8U);
    std::array<std::uint8_t, 32U> distance_lengths{};
    distance_lengths.fill(5U);
    return try_build_huffman(literal_lengths, literal) &&
        try_build_huffman(distance_lengths, distance);
}

bool try_build_dynamic(
    bit_reader& reader,
    huffman_table& literal,
    huffman_table& distance) noexcept {
    std::uint32_t hlit_bits = 0U;
    std::uint32_t hdist_bits = 0U;
    std::uint32_t hclen_bits = 0U;
    if (!reader.try_read(5U, hlit_bits) ||
        !reader.try_read(5U, hdist_bits) ||
        !reader.try_read(4U, hclen_bits)) {
        return false;
    }
    const auto literal_count = static_cast<std::size_t>(hlit_bits + 257U);
    const auto distance_count = static_cast<std::size_t>(hdist_bits + 1U);
    const auto code_length_count =
        static_cast<std::size_t>(hclen_bits + 4U);
    if (literal_count > 286U || distance_count > 32U) {
        return false;
    }

    std::array<std::uint8_t, 19U> code_lengths{};
    for (std::size_t index = 0U; index < code_length_count; ++index) {
        std::uint32_t length = 0U;
        if (!reader.try_read(3U, length)) {
            return false;
        }
        code_lengths[code_length_order[index]] =
            static_cast<std::uint8_t>(length);
    }
    huffman_table code_table{};
    if (!try_build_huffman(code_lengths, code_table)) {
        return false;
    }

    std::array<std::uint8_t, 320U> lengths{};
    const auto total = literal_count + distance_count;
    std::size_t written = 0U;
    while (written < total) {
        std::uint16_t symbol = 0U;
        if (!try_decode_symbol(reader, code_table, symbol)) {
            return false;
        }
        if (symbol <= 15U) {
            lengths[written++] = static_cast<std::uint8_t>(symbol);
            continue;
        }
        std::uint32_t extra = 0U;
        std::size_t repeat = 0U;
        std::uint8_t value = 0U;
        if (symbol == 16U) {
            if (written == 0U || !reader.try_read(2U, extra)) {
                return false;
            }
            repeat = static_cast<std::size_t>(extra + 3U);
            value = lengths[written - 1U];
        } else if (symbol == 17U) {
            if (!reader.try_read(3U, extra)) {
                return false;
            }
            repeat = static_cast<std::size_t>(extra + 3U);
        } else if (symbol == 18U) {
            if (!reader.try_read(7U, extra)) {
                return false;
            }
            repeat = static_cast<std::size_t>(extra + 11U);
        } else {
            return false;
        }
        if (repeat > total - written) {
            return false;
        }
        std::fill_n(lengths.begin() + written, repeat, value);
        written += repeat;
    }
    if (lengths[256U] == 0U) {
        return false;
    }
    return try_build_huffman(
               std::span<const std::uint8_t>{lengths}.first(literal_count),
               literal) &&
        try_build_huffman(
            std::span<const std::uint8_t>{lengths}.subspan(
                literal_count, distance_count),
            distance);
}

bool try_inflate_compressed(
    bit_reader& reader,
    const huffman_table& literal,
    const huffman_table& distance,
    std::span<std::byte> output,
    std::size_t& written,
    compression_error& error) noexcept {
    for (;;) {
        std::uint16_t symbol = 0U;
        if (!try_decode_symbol(reader, literal, symbol)) {
            error = compression_error::invalid_stream;
            return false;
        }
        if (symbol < 256U) {
            if (written == output.size()) {
                error = compression_error::insufficient_buffer;
                return false;
            }
            output[written++] = static_cast<std::byte>(symbol);
            continue;
        }
        if (symbol == 256U) {
            return true;
        }
        if (symbol < 257U || symbol > 285U) {
            error = compression_error::invalid_stream;
            return false;
        }
        const auto length_index = static_cast<std::size_t>(symbol - 257U);
        std::uint32_t length_extra = 0U;
        if (!try_read_extra(
                reader, length_extras[length_index], length_extra)) {
            error = compression_error::invalid_stream;
            return false;
        }
        const auto length = static_cast<std::size_t>(
            length_bases[length_index] + length_extra);

        std::uint16_t distance_symbol = 0U;
        if (!try_decode_symbol(reader, distance, distance_symbol) ||
            distance_symbol >= distance_bases.size()) {
            error = compression_error::invalid_stream;
            return false;
        }
        std::uint32_t distance_extra = 0U;
        if (!try_read_extra(
                reader,
                distance_extras[distance_symbol],
                distance_extra)) {
            error = compression_error::invalid_stream;
            return false;
        }
        const auto copy_distance = static_cast<std::size_t>(
            distance_bases[distance_symbol] + distance_extra);
        if (copy_distance == 0U || copy_distance > written ||
            copy_distance > 32768U || length > output.size() - written) {
            error = length > output.size() - written
                ? compression_error::insufficient_buffer
                : compression_error::invalid_stream;
            return false;
        }
        for (std::size_t index = 0U; index < length; ++index) {
            output[written] = output[written - copy_distance];
            ++written;
        }
    }
}

bool try_inflate_stored(
    bit_reader& reader,
    std::span<std::byte> output,
    std::size_t& written,
    compression_error& error) noexcept {
    reader.align_to_byte();
    std::uint32_t low = 0U;
    std::uint32_t high = 0U;
    std::uint32_t inverse_low = 0U;
    std::uint32_t inverse_high = 0U;
    if (!reader.try_read(8U, low) || !reader.try_read(8U, high) ||
        !reader.try_read(8U, inverse_low) ||
        !reader.try_read(8U, inverse_high)) {
        error = compression_error::invalid_stream;
        return false;
    }
    const auto length = low | (high << 8U);
    const auto inverse = inverse_low | (inverse_high << 8U);
    if ((length ^ 0xFFFFU) != inverse) {
        error = compression_error::invalid_stream;
        return false;
    }
    if (length > output.size() - written) {
        error = compression_error::insufficient_buffer;
        return false;
    }
    for (std::uint32_t index = 0U; index < length; ++index) {
        std::uint32_t value = 0U;
        if (!reader.try_read(8U, value)) {
            error = compression_error::invalid_stream;
            return false;
        }
        output[written++] = static_cast<std::byte>(value);
    }
    return true;
}

} // namespace

bool try_inflate_deflate(
    std::span<const std::byte> input,
    std::span<std::byte> output,
    std::size_t& written,
    compression_error& error) noexcept {
    written = 0U;
    error = compression_error::none;
    bit_reader reader{input};
    auto final_block = false;
    while (!final_block) {
        std::uint32_t final = 0U;
        std::uint32_t type = 0U;
        if (!reader.try_read(1U, final) || !reader.try_read(2U, type)) {
            error = compression_error::invalid_stream;
            return false;
        }
        final_block = final != 0U;
        if (type == 0U) {
            if (!try_inflate_stored(reader, output, written, error)) {
                return false;
            }
            continue;
        }
        if (type == 3U) {
            error = compression_error::invalid_stream;
            return false;
        }
        huffman_table literal{};
        huffman_table distance{};
        const auto valid_tables = type == 1U
            ? try_build_fixed(literal, distance)
            : try_build_dynamic(reader, literal, distance);
        if (!valid_tables ||
            !try_inflate_compressed(
                reader, literal, distance, output, written, error)) {
            if (error == compression_error::none) {
                error = compression_error::invalid_stream;
            }
            return false;
        }
    }
    if (!reader.consumed_all_bytes()) {
        error = compression_error::invalid_stream;
        written = 0U;
        return false;
    }
    return true;
}

} // namespace progpu::native::compression::detail
