#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <algorithm>
#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned OpenTypeVariationData
// ItemVariationStore and DeltaSetIndexMap at checkpoint 38b2b05f. Stores stay
// borrowed; validation and lookup allocate no memory.
namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_i16;
using detail::read_u16;
using detail::read_u32;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool checked_multiply(
    std::size_t left,
    std::size_t right,
    std::size_t& result) noexcept {
    if (left != 0U &&
        right > std::numeric_limits<std::size_t>::max() / left) {
        return false;
    }
    result = left * right;
    return true;
}

float axis_scalar(
    float coordinate,
    float start,
    float peak,
    float end) noexcept {
    if (start > peak || peak > end ||
        (start < 0.0F && end > 0.0F && peak != 0.0F) || peak == 0.0F) {
        return 1.0F;
    }
    if (coordinate < start || coordinate > end) {
        return 0.0F;
    }
    if (coordinate == peak) {
        return 1.0F;
    }
    if (coordinate < peak) {
        return peak == start
            ? 1.0F
            : (coordinate - start) / (peak - start);
    }
    return end == peak ? 1.0F : (end - coordinate) / (end - peak);
}

bool try_get_subtable_offset(
    sfnt_item_variation_store_view store,
    std::uint16_t outer_index,
    std::size_t& result) noexcept {
    if (outer_index >= store.subtable_count) {
        return false;
    }
    const auto relative = read_u32(
        store.bytes,
        store.subtable_offsets_offset +
            static_cast<std::size_t>(outer_index) * 4U);
    if (relative == 0U || relative > store.bytes.size() - store.store_offset) {
        return false;
    }
    result = store.store_offset + relative;
    return true;
}

} // namespace

bool sfnt_item_variation_data::try_get_store(
    std::span<const std::byte> bytes,
    std::size_t store_offset,
    std::uint16_t expected_axis_count,
    sfnt_item_variation_store_view& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    if (!can_read(bytes, store_offset, 8U) ||
        read_u16(bytes, store_offset) != 1U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto region_relative = read_u32(bytes, store_offset + 2U);
    const auto subtable_count = read_u16(bytes, store_offset + 6U);
    std::size_t subtable_offset_bytes = 0U;
    if (!checked_multiply(subtable_count, 4U, subtable_offset_bytes) ||
        !can_read(bytes, store_offset + 8U, subtable_offset_bytes) ||
        region_relative > bytes.size() - store_offset) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto region_offset = store_offset + region_relative;
    if (!can_read(bytes, region_offset, 4U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto axis_count = read_u16(bytes, region_offset);
    const auto region_count = read_u16(bytes, region_offset + 2U);
    std::size_t region_axis_count = 0U;
    std::size_t region_bytes = 0U;
    if (axis_count != expected_axis_count ||
        !checked_multiply(region_count, axis_count, region_axis_count) ||
        !checked_multiply(region_axis_count, 6U, region_bytes) ||
        !can_read(bytes, region_offset + 4U, region_bytes)) {
        set_error(error, font_error::invalid_face);
        return false;
    }

    for (std::uint16_t table = 0U; table < subtable_count; ++table) {
        const auto relative = read_u32(
            bytes, store_offset + 8U + static_cast<std::size_t>(table) * 4U);
        if (relative == 0U) {
            continue;
        }
        if (relative > bytes.size() - store_offset) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto table_offset = store_offset + relative;
        if (!can_read(bytes, table_offset, 6U)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto item_count = read_u16(bytes, table_offset);
        const auto packed_word_count = read_u16(bytes, table_offset + 2U);
        const auto region_index_count = read_u16(bytes, table_offset + 4U);
        const auto word_count = packed_word_count & 0x7FFFU;
        const bool long_words = (packed_word_count & 0x8000U) != 0U;
        if (word_count > region_index_count) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        std::size_t index_bytes = 0U;
        if (!checked_multiply(region_index_count, 2U, index_bytes) ||
            !can_read(bytes, table_offset + 6U, index_bytes)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        const auto word_bytes = long_words ? 4U : 2U;
        const auto short_bytes = long_words ? 2U : 1U;
        const auto bytes_per_row =
            static_cast<std::size_t>(word_count) * word_bytes +
            static_cast<std::size_t>(region_index_count - word_count) *
                short_bytes;
        std::size_t delta_bytes = 0U;
        if (!checked_multiply(item_count, bytes_per_row, delta_bytes) ||
            !can_read(
                bytes, table_offset + 6U + index_bytes, delta_bytes)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
    }

    result = {
        bytes,
        store_offset,
        region_offset,
        store_offset + 8U,
        axis_count,
        region_count,
        subtable_count};
    return true;
}

bool sfnt_item_variation_data::try_get_delta(
    sfnt_item_variation_store_view store,
    std::span<const std::int16_t> normalized_coordinates,
    std::uint16_t outer_index,
    std::uint16_t inner_index,
    float& result,
    font_error* error) noexcept {
    result = 0.0F;
    set_error(error, font_error::none);
    if (normalized_coordinates.size() < store.axis_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    std::size_t table_offset = 0U;
    if (!try_get_subtable_offset(store, outer_index, table_offset)) {
        return true;
    }
    const auto item_count = read_u16(store.bytes, table_offset);
    if (inner_index >= item_count) {
        return true;
    }
    const auto packed_word_count = read_u16(store.bytes, table_offset + 2U);
    const auto region_index_count = read_u16(store.bytes, table_offset + 4U);
    const auto word_count = packed_word_count & 0x7FFFU;
    const bool long_words = (packed_word_count & 0x8000U) != 0U;
    const auto index_offset = table_offset + 6U;
    const auto word_bytes = long_words ? 4U : 2U;
    const auto short_bytes = long_words ? 2U : 1U;
    const auto bytes_per_row =
        static_cast<std::size_t>(word_count) * word_bytes +
        static_cast<std::size_t>(region_index_count - word_count) *
            short_bytes;
    auto delta_offset = index_offset +
        static_cast<std::size_t>(region_index_count) * 2U +
        static_cast<std::size_t>(inner_index) * bytes_per_row;
    float delta_sum = 0.0F;
    constexpr float coordinate_scale = 1.0F / 16384.0F;
    for (std::uint16_t region = 0U;
        region < region_index_count;
        ++region) {
        std::int32_t delta = 0;
        if (region < word_count) {
            if (long_words) {
                delta = static_cast<std::int32_t>(
                    read_u32(store.bytes, delta_offset));
                delta_offset += 4U;
            } else {
                delta = read_i16(store.bytes, delta_offset);
                delta_offset += 2U;
            }
        } else if (long_words) {
            delta = read_i16(store.bytes, delta_offset);
            delta_offset += 2U;
        } else {
            delta = static_cast<std::int8_t>(
                std::to_integer<std::uint8_t>(store.bytes[delta_offset++]));
        }
        const auto region_index = read_u16(
            store.bytes,
            index_offset + static_cast<std::size_t>(region) * 2U);
        if (region_index >= store.region_count) {
            continue;
        }
        const auto axes_offset = store.region_list_offset + 4U +
            static_cast<std::size_t>(region_index) * store.axis_count * 6U;
        float scalar = 1.0F;
        for (std::uint16_t axis = 0U; axis < store.axis_count; ++axis) {
            const auto offset = axes_offset + static_cast<std::size_t>(axis) * 6U;
            scalar *= axis_scalar(
                normalized_coordinates[axis] * coordinate_scale,
                read_i16(store.bytes, offset) * coordinate_scale,
                read_i16(store.bytes, offset + 2U) * coordinate_scale,
                read_i16(store.bytes, offset + 4U) * coordinate_scale);
            if (scalar == 0.0F) {
                break;
            }
        }
        delta_sum += delta * scalar;
    }
    result = delta_sum;
    return true;
}

bool sfnt_item_variation_data::try_get_region_scalar_count(
    sfnt_item_variation_store_view store,
    std::uint16_t outer_index,
    std::uint16_t& result,
    font_error* error) noexcept {
    result = 0U;
    set_error(error, font_error::none);
    std::size_t table_offset = 0U;
    if (!try_get_subtable_offset(store, outer_index, table_offset)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto item_count = read_u16(store.bytes, table_offset);
    const auto packed_word_count = read_u16(store.bytes, table_offset + 2U);
    const auto region_index_count = read_u16(store.bytes, table_offset + 4U);
    if (item_count != 0U || packed_word_count != 0U) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto indices_offset = table_offset + 6U;
    for (std::uint16_t region = 0U;
        region < region_index_count;
        ++region) {
        if (read_u16(
                store.bytes,
                indices_offset + static_cast<std::size_t>(region) * 2U) >=
            store.region_count) {
            set_error(error, font_error::invalid_face);
            return false;
        }
    }
    result = region_index_count;
    return true;
}

bool sfnt_item_variation_data::try_get_region_scalar(
    sfnt_item_variation_store_view store,
    std::span<const std::int16_t> normalized_coordinates,
    std::uint16_t outer_index,
    std::uint16_t region_position,
    float& result,
    font_error* error) noexcept {
    result = 0.0F;
    set_error(error, font_error::none);
    if (normalized_coordinates.size() < store.axis_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    std::uint16_t region_count = 0U;
    if (!try_get_region_scalar_count(
            store, outer_index, region_count, error)) {
        return false;
    }
    if (region_position >= region_count) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::size_t table_offset = 0U;
    if (!try_get_subtable_offset(store, outer_index, table_offset)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto region_index = read_u16(
        store.bytes,
        table_offset + 6U +
            static_cast<std::size_t>(region_position) * 2U);
    const auto axes_offset = store.region_list_offset + 4U +
        static_cast<std::size_t>(region_index) * store.axis_count * 6U;
    constexpr float coordinate_scale = 1.0F / 16384.0F;
    float scalar = 1.0F;
    for (std::uint16_t axis = 0U; axis < store.axis_count; ++axis) {
        const auto offset = axes_offset + static_cast<std::size_t>(axis) * 6U;
        scalar *= axis_scalar(
            normalized_coordinates[axis] * coordinate_scale,
            read_i16(store.bytes, offset) * coordinate_scale,
            read_i16(store.bytes, offset + 2U) * coordinate_scale,
            read_i16(store.bytes, offset + 4U) * coordinate_scale);
        if (scalar == 0.0F) {
            break;
        }
    }
    result = scalar;
    return true;
}

bool sfnt_item_variation_data::try_get_delta_set_index_map(
    std::span<const std::byte> bytes,
    std::size_t map_offset,
    sfnt_delta_set_index_map_view& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    if (!can_read(bytes, map_offset, 4U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto format = std::to_integer<std::uint8_t>(bytes[map_offset]);
    const auto entry_format =
        std::to_integer<std::uint8_t>(bytes[map_offset + 1U]);
    std::uint32_t count = 0U;
    std::size_t cursor = 0U;
    if (format == 0U) {
        count = read_u16(bytes, map_offset + 2U);
        cursor = map_offset + 4U;
    } else if (format == 1U && can_read(bytes, map_offset, 6U)) {
        count = read_u32(bytes, map_offset + 2U);
        cursor = map_offset + 6U;
    } else {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto entry_size = static_cast<std::uint8_t>(
        ((entry_format & 0x30U) >> 4U) + 1U);
    const auto inner_bits = static_cast<std::uint8_t>(
        (entry_format & 0x0FU) + 1U);
    std::size_t entry_bytes = 0U;
    if (!checked_multiply(count, entry_size, entry_bytes) ||
        !can_read(bytes, cursor, entry_bytes)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    result = {bytes, cursor, count, entry_size, inner_bits};
    return true;
}

void sfnt_item_variation_data::get_delta_set_index(
    sfnt_delta_set_index_map_view map,
    std::uint32_t item_index,
    std::uint16_t& outer_index,
    std::uint16_t& inner_index) noexcept {
    if (map.entry_count == 0U) {
        outer_index = 0U;
        inner_index = static_cast<std::uint16_t>(std::min<std::uint32_t>(
            item_index, std::numeric_limits<std::uint16_t>::max()));
        return;
    }
    const auto entry = std::min(item_index, map.entry_count - 1U);
    const auto offset = map.entries_offset +
        static_cast<std::size_t>(entry) * map.entry_size;
    std::uint32_t value = 0U;
    for (std::uint8_t part = 0U; part < map.entry_size; ++part) {
        value = (value << 8U) |
            std::to_integer<std::uint8_t>(map.bytes[offset + part]);
    }
    const auto mask = map.inner_index_bits == 32U
        ? std::numeric_limits<std::uint32_t>::max()
        : (std::uint32_t{1U} << map.inner_index_bits) - 1U;
    outer_index = static_cast<std::uint16_t>(
        value >> map.inner_index_bits);
    inner_index = static_cast<std::uint16_t>(value & mask);
}

} // namespace progpu::native::text
