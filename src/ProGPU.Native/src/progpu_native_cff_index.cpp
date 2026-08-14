#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned Cff1OutlineSource.CffIndex at
// checkpoint 2f152ddd. Offset lookup stays O(1) with borrowed table bytes and
// performs no offset-array allocation.
namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_u16;
using detail::read_u32;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool try_read_offset(
    sfnt_cff_index_view index,
    std::uint32_t item,
    std::uint32_t& result) noexcept {
    if (item > index.count) {
        return false;
    }
    const auto offset = index.offsets_offset +
        static_cast<std::size_t>(item) * index.offset_size;
    if (!can_read(index.bytes, offset, index.offset_size)) {
        return false;
    }
    std::uint32_t value = 0U;
    for (std::uint8_t component = 0U;
        component < index.offset_size;
        ++component) {
        value = (value << 8U) |
            std::to_integer<std::uint8_t>(index.bytes[offset + component]);
    }
    result = value;
    return true;
}

bool try_read_index_core(
    std::span<const std::byte> bytes,
    std::size_t& cursor,
    std::size_t count_size,
    sfnt_cff_index_view& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    if ((count_size != 2U && count_size != 4U) ||
        !can_read(bytes, cursor, count_size)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto count = count_size == 2U
        ? static_cast<std::uint32_t>(read_u16(bytes, cursor))
        : read_u32(bytes, cursor);
    cursor += count_size;
    if (count == 0U) {
        result = {bytes, cursor, cursor, cursor, 0U, 0U};
        return true;
    }
    if (!can_read(bytes, cursor, 1U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto offset_size =
        std::to_integer<std::uint8_t>(bytes[cursor++]);
    if (offset_size < 1U || offset_size > 4U ||
        count == std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto offset_count = static_cast<std::size_t>(count) + 1U;
    if (offset_count >
            std::numeric_limits<std::size_t>::max() / offset_size ||
        !can_read(bytes, cursor, offset_count * offset_size)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto offsets_offset = cursor;
    const auto data_offset = cursor + offset_count * offset_size;
    sfnt_cff_index_view candidate{
        bytes,
        offsets_offset,
        data_offset,
        data_offset,
        count,
        offset_size};
    std::uint32_t first = 0U;
    std::uint32_t previous = 0U;
    if (!try_read_offset(candidate, 0U, first) || first != 1U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    previous = first;
    for (std::uint32_t item = 1U; item <= count; ++item) {
        std::uint32_t encoded = 0U;
        if (!try_read_offset(candidate, item, encoded) ||
            encoded < previous) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        previous = encoded;
    }
    const auto data_size = static_cast<std::size_t>(previous - 1U);
    if (!can_read(bytes, data_offset, data_size)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    candidate.end_offset = data_offset + data_size;
    result = candidate;
    cursor = candidate.end_offset;
    return true;
}

} // namespace

bool sfnt_cff_data::try_read_index(
    std::span<const std::byte> bytes,
    std::size_t& cursor,
    sfnt_cff_index_view& result,
    font_error* error) noexcept {
    return try_read_index_core(bytes, cursor, 2U, result, error);
}

bool sfnt_cff_data::try_read_cff2_index(
    std::span<const std::byte> bytes,
    std::size_t& cursor,
    sfnt_cff_index_view& result,
    font_error* error) noexcept {
    return try_read_index_core(bytes, cursor, 4U, result, error);
}

bool sfnt_cff_data::try_get_index_item(
    sfnt_cff_index_view index,
    std::uint32_t item,
    std::span<const std::byte>& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    if (item >= index.count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::uint32_t start = 0U;
    std::uint32_t end = 0U;
    if (!try_read_offset(index, item, start) ||
        !try_read_offset(index, item + 1U, end) ||
        start < 1U || end < start) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto item_offset = index.data_offset + start - 1U;
    const auto item_size = static_cast<std::size_t>(end - start);
    if (item_offset > index.end_offset ||
        item_size > index.end_offset - item_offset) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result = index.bytes.subspan(item_offset, item_size);
    return true;
}

} // namespace progpu::native::text
