#include "progpu_native_compression.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <span>
#include <string_view>
#include <vector>

namespace {

using progpu::native::compression::compression_error;
using progpu::native::compression::try_get_gzip_uncompressed_size;
using progpu::native::compression::try_inflate_gzip;
using progpu::native::compression::try_inflate_zlib;

void require(bool condition) {
    if (!condition) {
        std::abort();
    }
}

template<std::size_t Size>
std::array<std::byte, Size> bytes(
    const std::array<unsigned char, Size>& source) {
    std::array<std::byte, Size> result{};
    for (std::size_t index = 0U; index < Size; ++index) {
        result[index] = static_cast<std::byte>(source[index]);
    }
    return result;
}

void fixed_huffman_and_overlapping_history_decode() {
    constexpr std::array<unsigned char, 26U> encoded_values{
        0x78U, 0x01U, 0x4BU, 0xCBU, 0xACU, 0x48U, 0x4DU, 0xD1U,
        0xCDU, 0x28U, 0x4DU, 0x4BU, 0xCBU, 0x4DU, 0xCCU, 0xD3U,
        0x4DU, 0x1BU, 0xE5U, 0x41U, 0x79U, 0x00U, 0x62U, 0xC2U,
        0x6AU, 0x2DU};
    const auto encoded = bytes(encoded_values);
    std::array<std::byte, 280U> output{};
    std::size_t written = 0U;
    compression_error error = compression_error::invalid_stream;
    require(try_inflate_zlib(encoded, output, written, &error));
    require(error == compression_error::none && written == output.size());
    constexpr std::string_view pattern = "fixed-huffman-";
    for (std::size_t index = 0U; index < output.size(); ++index) {
        require(std::to_integer<unsigned char>(output[index]) ==
            static_cast<unsigned char>(pattern[index % pattern.size()]));
    }
}

void dynamic_huffman_decodes_exact_payload() {
    constexpr std::array<unsigned char, 81U> encoded_values{
        0x78U, 0xDAU, 0x05U, 0xC1U, 0x07U, 0x0EU, 0x80U, 0x20U,
        0x0CU, 0x00U, 0xC0U, 0xAFU, 0xF8U, 0x04U, 0x19U, 0x96U,
        0xF0U, 0x9CU, 0x42U, 0x9BU, 0xC8U, 0x10U, 0x19U, 0x71U,
        0xF1U, 0x7AU, 0xEFU, 0xD4U, 0x8CU, 0x54U, 0xC2U, 0xCAU,
        0xB9U, 0x43U, 0xA1U, 0x39U, 0x08U, 0x16U, 0x01U, 0xFCU,
        0xC1U, 0x9DU, 0x31U, 0x0EU, 0xB0U, 0x59U, 0x93U, 0xA8U,
        0xF8U, 0x06U, 0x79U, 0xE3U, 0xA1U, 0x3DU, 0x6BU, 0xDFU,
        0xBCU, 0x15U, 0x09U, 0xB9U, 0x18U, 0x57U, 0x9FU, 0x2AU,
        0x4FU, 0xDBU, 0x7BU, 0xEAU, 0x7BU, 0x23U, 0xEFU, 0xC6U,
        0x95U, 0x71U, 0x6EU, 0x46U, 0xFDU, 0x7DU, 0x58U, 0x1CU,
        0xA7U};
    constexpr std::string_view expected =
        "3zjdni0elr6ndzsd6 16ey6vlajs69l4d1paxi2vam4ce4cqc91kaen7bpwp2o9rrkrhqdcb"
        "sulaz573";
    static_assert(expected.size() == 80U);
    const auto encoded = bytes(encoded_values);
    std::array<std::byte, expected.size()> output{};
    std::size_t written = 0U;
    require(try_inflate_zlib(encoded, output, written));
    require(written == expected.size());
    for (std::size_t index = 0U; index < expected.size(); ++index) {
        require(std::to_integer<unsigned char>(output[index]) ==
            static_cast<unsigned char>(expected[index]));
    }
}

void stored_blocks_and_failures_are_bounded() {
    std::array<unsigned char, 75U> encoded_values{
        0x78U, 0x01U, 0x01U, 0x40U, 0x00U, 0xBFU, 0xFFU};
    for (std::size_t index = 0U; index < 64U; ++index) {
        encoded_values[7U + index] = static_cast<unsigned char>(index);
    }
    encoded_values[71U] = 0xAAU;
    encoded_values[72U] = 0xE0U;
    encoded_values[73U] = 0x07U;
    encoded_values[74U] = 0xE1U;
    auto encoded = bytes(encoded_values);
    std::array<std::byte, 64U> output{};
    std::size_t written = 0U;
    compression_error error = compression_error::none;
    require(try_inflate_zlib(encoded, output, written, &error));
    for (std::size_t index = 0U; index < output.size(); ++index) {
        require(std::to_integer<unsigned char>(output[index]) == index);
    }

    std::array<std::byte, 63U> short_output{};
    short_output.fill(std::byte{0xA5U});
    written = 99U;
    require(!try_inflate_zlib(encoded, short_output, written, &error));
    require(error == compression_error::insufficient_buffer && written == 0U);

    std::array<std::byte, 76U> trailing{};
    std::copy_n(encoded.begin(), 71U, trailing.begin());
    std::copy_n(encoded.begin() + 71U, 4U, trailing.begin() + 72U);
    require(!try_inflate_zlib(trailing, output, written, &error));
    require(error == compression_error::invalid_stream && written == 0U);

    encoded[0U] = std::byte{0U};
    require(!try_inflate_zlib(encoded, output, written, &error));
    require(error == compression_error::invalid_header && written == 0U);
    encoded[0U] = std::byte{0x78U};
    encoded.back() ^= std::byte{1U};
    require(!try_inflate_zlib(encoded, output, written, &error));
    require(error == compression_error::checksum_mismatch && written == 0U);
}

std::uint32_t crc32(std::span<const std::byte> bytes) {
    auto crc = 0xFFFFFFFFU;
    for (const auto value : bytes) {
        crc ^= std::to_integer<std::uint8_t>(value);
        for (std::uint32_t bit = 0U; bit < 8U; ++bit) {
            const auto mask = 0U - (crc & 1U);
            crc = (crc >> 1U) ^ (0xEDB88320U & mask);
        }
    }
    return crc ^ 0xFFFFFFFFU;
}

void append_u16_le(std::vector<std::byte>& bytes, std::uint16_t value) {
    bytes.push_back(static_cast<std::byte>(value));
    bytes.push_back(static_cast<std::byte>(value >> 8U));
}

void append_u32_le(std::vector<std::byte>& bytes, std::uint32_t value) {
    append_u16_le(bytes, static_cast<std::uint16_t>(value));
    append_u16_le(bytes, static_cast<std::uint16_t>(value >> 16U));
}

std::vector<std::byte> make_gzip(std::span<const std::byte> payload) {
    require(payload.size() <= 65535U);
    std::vector<std::byte> result{
        std::byte{0x1FU}, std::byte{0x8BU}, std::byte{8U},
        std::byte{0x1EU}, std::byte{0U}, std::byte{0U}, std::byte{0U},
        std::byte{0U}, std::byte{0U}, std::byte{255U}};
    append_u16_le(result, 2U);
    result.push_back(std::byte{0xCAU});
    result.push_back(std::byte{0xFEU});
    constexpr std::string_view name = "glyph.svg";
    for (const auto value : name) {
        result.push_back(static_cast<std::byte>(value));
    }
    result.push_back(std::byte{0U});
    constexpr std::string_view comment = "ProGPU";
    for (const auto value : comment) {
        result.push_back(static_cast<std::byte>(value));
    }
    result.push_back(std::byte{0U});
    append_u16_le(result,
        static_cast<std::uint16_t>(crc32(result)));
    result.push_back(std::byte{1U});
    const auto length = static_cast<std::uint16_t>(payload.size());
    append_u16_le(result, length);
    append_u16_le(result, static_cast<std::uint16_t>(~length));
    result.insert(result.end(), payload.begin(), payload.end());
    append_u32_le(result, crc32(payload));
    append_u32_le(result, static_cast<std::uint32_t>(payload.size()));
    return result;
}

void gzip_optional_fields_and_failures_are_bounded() {
    constexpr std::string_view expected = "svg-gzip";
    std::array<std::byte, expected.size()> payload{};
    for (std::size_t index = 0U; index < expected.size(); ++index) {
        payload[index] = static_cast<std::byte>(expected[index]);
    }
    auto encoded = make_gzip(payload);
    std::array<std::byte, expected.size()> output{};
    std::size_t written = 0U;
    compression_error error = compression_error::invalid_stream;
    std::size_t expected_size = 0U;
    require(try_get_gzip_uncompressed_size(
        encoded, expected_size, &error));
    require(error == compression_error::none &&
        expected_size == expected.size());
    require(try_inflate_gzip(encoded, output, written, &error));
    require(error == compression_error::none && written == output.size());
    require(output == payload);

    std::array<std::byte, expected.size() - 1U> short_output{};
    require(!try_inflate_gzip(
        encoded, short_output, written, &error));
    require(error == compression_error::insufficient_buffer && written == 0U);

    encoded[31U] ^= std::byte{1U};
    require(!try_inflate_gzip(encoded, output, written, &error));
    require(error == compression_error::checksum_mismatch && written == 0U);
}

} // namespace

int main() {
    fixed_huffman_and_overlapping_history_decode();
    dynamic_huffman_decodes_exact_payload();
    stored_blocks_and_failures_are_bounded();
    gzip_optional_fields_and_failures_are_bounded();
    return 0;
}
