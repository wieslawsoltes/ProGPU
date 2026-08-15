#include "progpu_native_deflate_internal.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

// Algorithm: RFC 1951 least-significant-bit reader and canonical Huffman
// construction/decoding with a maximum code length of 15 bits.
// Time complexity: O(N + 15S) for N code lengths and S decoded symbols.
// Space complexity: O(1), one 288-symbol table and 16 count/offset entries.
namespace progpu::native::compression::detail {

bit_reader::bit_reader(std::span<const std::byte> input) noexcept
    : input_(input) {
}

bool bit_reader::try_read(
    std::uint8_t count,
    std::uint32_t& value) noexcept {
    if (count > 24U) {
        value = 0U;
        return false;
    }
    while (bit_count_ < count) {
        if (offset_ == input_.size()) {
            value = 0U;
            return false;
        }
        bits_ |= static_cast<std::uint64_t>(
            std::to_integer<std::uint8_t>(input_[offset_++])) << bit_count_;
        bit_count_ = static_cast<std::uint8_t>(bit_count_ + 8U);
    }
    const auto mask = count == 0U ? 0U : (std::uint64_t{1U} << count) - 1U;
    value = static_cast<std::uint32_t>(bits_ & mask);
    bits_ >>= count;
    bit_count_ = static_cast<std::uint8_t>(bit_count_ - count);
    return true;
}

void bit_reader::align_to_byte() noexcept {
    const auto discard = static_cast<std::uint8_t>(bit_count_ & 7U);
    bits_ >>= discard;
    bit_count_ = static_cast<std::uint8_t>(bit_count_ - discard);
}

bool bit_reader::consumed_all_bytes() const noexcept {
    return offset_ == input_.size();
}

bool try_build_huffman(
    std::span<const std::uint8_t> lengths,
    huffman_table& table) noexcept {
    table = {};
    if (lengths.empty() || lengths.size() > table.symbols.size()) {
        return false;
    }
    for (const auto length : lengths) {
        if (length > 15U) {
            return false;
        }
        ++table.counts[length];
    }
    if (table.counts[0U] == lengths.size()) {
        return false;
    }
    std::int32_t remaining = 1;
    for (std::size_t bits = 1U; bits < table.counts.size(); ++bits) {
        remaining = (remaining << 1U) - table.counts[bits];
        if (remaining < 0) {
            return false;
        }
    }
    std::array<std::uint16_t, 16U> offsets{};
    for (std::size_t bits = 1U; bits + 1U < offsets.size(); ++bits) {
        offsets[bits + 1U] = static_cast<std::uint16_t>(
            offsets[bits] + table.counts[bits]);
    }
    for (std::size_t symbol = 0U; symbol < lengths.size(); ++symbol) {
        const auto length = lengths[symbol];
        if (length != 0U) {
            table.symbols[offsets[length]++] =
                static_cast<std::uint16_t>(symbol);
            ++table.symbol_count;
        }
    }
    return true;
}

bool try_decode_symbol(
    bit_reader& reader,
    const huffman_table& table,
    std::uint16_t& symbol) noexcept {
    std::uint32_t code = 0U;
    std::uint32_t first = 0U;
    std::uint32_t index = 0U;
    for (std::uint32_t bits = 1U; bits <= 15U; ++bits) {
        std::uint32_t bit = 0U;
        if (!reader.try_read(1U, bit)) {
            return false;
        }
        code |= bit;
        const auto count = table.counts[bits];
        if (code >= first && code - first < count) {
            const auto symbol_index = index + code - first;
            if (symbol_index >= table.symbol_count) {
                return false;
            }
            symbol = table.symbols[symbol_index];
            return true;
        }
        index += count;
        first = (first + count) << 1U;
        code <<= 1U;
    }
    return false;
}

} // namespace progpu::native::compression::detail
