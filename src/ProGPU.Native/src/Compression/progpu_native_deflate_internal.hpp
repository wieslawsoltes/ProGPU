#ifndef PROGPU_NATIVE_DEFLATE_INTERNAL_HPP
#define PROGPU_NATIVE_DEFLATE_INTERNAL_HPP

#include "progpu_native_compression.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::compression::detail {

class bit_reader final {
public:
    explicit bit_reader(std::span<const std::byte> input) noexcept;

    bool try_read(std::uint8_t count, std::uint32_t& value) noexcept;
    void align_to_byte() noexcept;
    bool consumed_all_bytes() const noexcept;

private:
    std::span<const std::byte> input_{};
    std::size_t offset_ = 0U;
    std::uint64_t bits_ = 0U;
    std::uint8_t bit_count_ = 0U;
};

struct huffman_table final {
    std::array<std::uint16_t, 16U> counts{};
    std::array<std::uint16_t, 288U> symbols{};
    std::uint16_t symbol_count = 0U;
};

bool try_build_huffman(
    std::span<const std::uint8_t> lengths,
    huffman_table& table) noexcept;

bool try_decode_symbol(
    bit_reader& reader,
    const huffman_table& table,
    std::uint16_t& symbol) noexcept;

bool try_inflate_deflate(
    std::span<const std::byte> input,
    std::span<std::byte> output,
    std::size_t& written,
    compression_error& error) noexcept;

} // namespace progpu::native::compression::detail

#endif
