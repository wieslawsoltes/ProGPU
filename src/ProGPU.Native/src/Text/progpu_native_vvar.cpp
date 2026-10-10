#include "progpu_native_text.hpp"
#include "progpu_native_font_bytes.hpp"

#include <cmath>
#include <initializer_list>
#include <limits>

// Independently authored VVAR preflight from the public OpenType VVAR and
// variation-common formats. Scalar/delta evaluation reuses ProGPU's owned
// ItemVariationStore walker; its older HVAR acceptance policy is unchanged.
namespace progpu::native::text {
namespace {
using detail::can_read;
using detail::read_i16;
using detail::read_u16;
using detail::read_u32;

struct memory_range final { const void* data; std::size_t size; };
template<class T> memory_range memory(const T& value) noexcept {
    return {&value, sizeof(value)};
}
template<class T, std::size_t N> memory_range memory(std::span<T, N> value) noexcept {
    return {value.data(), value.size_bytes()};
}
bool overlaps(memory_range a, memory_range b) noexcept {
    if (a.size == 0U || b.size == 0U) return false;
    const auto x = reinterpret_cast<std::uintptr_t>(a.data);
    const auto y = reinterpret_cast<std::uintptr_t>(b.data);
    return x <= y ? y - x < a.size : x - y < b.size;
}
bool fail(font_error* error, font_error value = font_error::invalid_face) noexcept {
    if (error != nullptr) *error = value;
    return false;
}
bool disjoint_outputs(font_error* error,
    std::initializer_list<memory_range> outputs,
    std::initializer_list<memory_range> inputs) noexcept {
    // Do not clobber a protected input/output to report an alias error.
    if (error != nullptr) {
        for (const auto item : inputs) if (overlaps(memory(*error), item)) return false;
        for (const auto item : outputs) if (overlaps(memory(*error), item)) return false;
    }
    for (auto a = outputs.begin(); a != outputs.end(); ++a) {
        for (auto b = a + 1; b != outputs.end(); ++b) {
            if (overlaps(*a, *b)) return fail(error, font_error::invalid_argument);
        }
        for (const auto input : inputs) {
            if (overlaps(*a, input)) return fail(error, font_error::invalid_argument);
        }
    }
    return true;
}

struct parsed_vvar final {
    sfnt_item_variation_store_view store{};
    sfnt_delta_set_index_map_view maps[4]{};
    std::uint16_t glyph_count = 0U;
    bool uses_vvar = false;
    bool has_maps[4]{};
};

bool strict_vvar_table(const sfnt_font_view& font, sfnt_table_view& result,
    bool& present) noexcept {
    const auto bytes = font.data();
    const auto face = static_cast<std::size_t>(font.face_offset());
    if (!can_read(bytes, face, 12U)) return false;
    const auto directory = face + 12U;
    if (!can_read(bytes, directory, static_cast<std::size_t>(font.table_count()) * 16U)) return false;
    constexpr auto tag = open_type_tag::from_chars('V', 'V', 'A', 'R');
    // The legacy table lookup skips invalid ranges and selects one duplicate.
    // This optional strict boundary must distinguish absence from either fault.
    for (std::size_t index = 0U; index < font.table_count(); ++index) {
        const auto record = directory + index * 16U;
        if (read_u32(bytes, record) != tag.value) continue;
        if (present) return false;
        present = true;
        const auto offset = read_u32(bytes, record + 8U);
        const auto length = read_u32(bytes, record + 12U);
        if (!can_read(bytes, offset, length)) return false;
        result = {tag, read_u32(bytes, record + 4U), bytes.subspan(offset, length)};
    }
    return true;
}

bool valid_reference(sfnt_item_variation_store_view store,
    std::uint16_t outer, std::uint16_t inner) noexcept {
    if (outer == 0xFFFFU && inner == 0xFFFFU) return true;
    if (outer >= store.subtable_count) return false;
    const auto relative = read_u32(store.bytes,
        store.subtable_offsets_offset + static_cast<std::size_t>(outer) * 4U);
    // OpenType explicitly assigns zero variation to a null subtable, whatever
    // the inner index. Non-null subtables must contain the referenced row.
    return relative == 0U || inner < read_u16(store.bytes, store.store_offset + relative);
}

bool strict_store(sfnt_item_variation_store_view store) noexcept {
    if (store.region_count >= 0x8000U ||
        read_u32(store.bytes, store.store_offset + 2U) == 0U) return false;
    for (std::size_t region = 0U; region < store.region_count; ++region) {
        for (std::size_t axis = 0U; axis < store.axis_count; ++axis) {
            const auto offset = store.region_list_offset + 4U +
                (region * store.axis_count + axis) * 6U;
            const auto start = read_i16(store.bytes, offset);
            const auto peak = read_i16(store.bytes, offset + 2U);
            const auto end = read_i16(store.bytes, offset + 4U);
            if (start < -16384 || end > 16384 || start > peak || peak > end ||
                (start < 0 && end > 0 && peak != 0)) return false;
        }
    }
    for (std::size_t outer = 0U; outer < store.subtable_count; ++outer) {
        const auto relative = read_u32(store.bytes, store.subtable_offsets_offset + outer * 4U);
        if (relative == 0U) continue;
        const auto offset = store.store_offset + relative;
        const auto count = read_u16(store.bytes, offset + 4U);
        for (std::size_t region = 0U; region < count; ++region) {
            if (read_u16(store.bytes, offset + 6U + region * 2U) >= store.region_count)
                return false;
        }
    }
    return true;
}

bool strict_map(std::span<const std::byte> bytes, std::size_t offset,
    sfnt_item_variation_store_view store, sfnt_delta_set_index_map_view& result) noexcept {
    if (offset < 24U || !can_read(bytes, offset, 4U) || bytes[offset] != std::byte{0} ||
        (std::to_integer<unsigned>(bytes[offset + 1U]) & 0xC0U) != 0U ||
        !sfnt_item_variation_data::try_get_delta_set_index_map(bytes, offset, result) ||
        result.entry_count == 0U) return false;
    for (std::size_t entry = 0U; entry < result.entry_count; ++entry) {
        std::uint32_t packed = 0U;
        for (std::size_t part = 0U; part < result.entry_size; ++part) {
            packed = (packed << 8U) | std::to_integer<std::uint8_t>(
                bytes[result.entries_offset + entry * result.entry_size + part]);
        }
        const auto outer = packed >> result.inner_index_bits;
        const auto inner = packed & ((std::uint32_t{1U} << result.inner_index_bits) - 1U);
        if (outer > 0xFFFFU || !valid_reference(store,
            static_cast<std::uint16_t>(outer), static_cast<std::uint16_t>(inner))) return false;
    }
    return true;
}

bool preflight(const sfnt_font_view& font, std::span<const std::int16_t> coordinates,
    parsed_vvar& result, font_error* error) noexcept {
    std::uint16_t axes = 0U;
    if (!font.try_get_glyph_count(result.glyph_count) ||
        !font.try_get_variation_axis_count(axes)) return fail(error);
    if (coordinates.size() != axes) return fail(error, font_error::invalid_argument);
    for (const auto coordinate : coordinates) {
        if (coordinate < -16384 || coordinate > 16384)
            return fail(error, font_error::invalid_argument);
    }
    sfnt_table_view table{};
    bool present = false;
    if (!strict_vvar_table(font, table, present)) return fail(error);
    if (!present) return true;
    sfnt_table_view fvar{};
    if (axes == 0U || !font.try_get_table(open_type_tag::from_chars('f', 'v', 'a', 'r'), fvar) ||
        !can_read(fvar.bytes, 0U, 16U) || read_u32(fvar.bytes, 0U) != 0x00010000U ||
        read_u16(fvar.bytes, 4U) < 16U || read_u16(fvar.bytes, 10U) < 20U ||
        !can_read(table.bytes, 0U, 24U) || read_u32(table.bytes, 0U) != 0x00010000U)
        return fail(error);
    const auto store_offset = read_u32(table.bytes, 4U);
    if (store_offset < 24U || !sfnt_item_variation_data::try_get_store(
        table.bytes, store_offset, axes, result.store) || !strict_store(result.store)) return fail(error);
    for (std::size_t kind = 0U; kind < 4U; ++kind) {
        const auto offset = read_u32(table.bytes, 8U + kind * 4U);
        if (offset == 0U) continue;
        if (!strict_map(table.bytes, offset, result.store, result.maps[kind])) return fail(error);
        result.has_maps[kind] = true;
    }
    if (!result.has_maps[0]) {
        for (std::uint32_t glyph = 0U; glyph < result.glyph_count; ++glyph) {
            if (!valid_reference(result.store, 0U, static_cast<std::uint16_t>(glyph))) return fail(error);
        }
    }
    result.uses_vvar = true;
    return true;
}

bool read_deltas(std::uint16_t glyph, sfnt_item_variation_store_view store,
    const sfnt_delta_set_index_map_view (&maps)[4], const bool (&has_maps)[4],
    std::span<const float> scalars, bool uses_vvar, sfnt_vertical_metrics_variation& result) noexcept {
    result = {};
    if (!uses_vvar) return true;
    float values[4]{};
    for (std::size_t kind = 0U; kind < 4U; ++kind) {
        if (kind != 0U && !has_maps[kind]) continue;
        std::uint16_t outer = 0U;
        std::uint16_t inner = glyph;
        if (has_maps[kind]) sfnt_item_variation_data::get_delta_set_index(maps[kind], glyph, outer, inner);
        if (!sfnt_item_variation_data::try_get_delta(store, scalars, outer, inner, values[kind]) ||
            !std::isfinite(values[kind])) return false;
    }
    result = {values[0], values[1], values[2], values[3], true,
        has_maps[1], has_maps[2], has_maps[3]};
    return true;
}
} // namespace

bool sfnt_font_view::try_get_vertical_metrics_variation_region_count(
    std::span<const std::int16_t> coordinates, std::uint16_t& result,
    bool& uses_vvar, font_error* error) const noexcept {
    if (!disjoint_outputs(error, {memory(result), memory(uses_vvar)},
        {memory(data_), memory(coordinates), memory(*this)})) return false;
    parsed_vvar parsed{};
    if (!preflight(*this, coordinates, parsed, error)) return false;
    result = parsed.store.region_count;
    uses_vvar = parsed.uses_vvar;
    if (error != nullptr) *error = font_error::none;
    return true;
}

bool sfnt_font_view::try_prepare_vertical_metrics_variation(
    std::span<const std::int16_t> coordinates, std::span<float> region_scalars,
    sfnt_vertical_metrics_variation_instance& result, font_error* error) const noexcept {
    if (!disjoint_outputs(error, {memory(result), memory(region_scalars)},
        {memory(data_), memory(coordinates), memory(*this)})) return false;
    parsed_vvar parsed{};
    if (!preflight(*this, coordinates, parsed, error)) return false;
    if (region_scalars.size() < parsed.store.region_count)
        return fail(error, font_error::insufficient_buffer);
    // First calculate without publication. Once the complete table and every
    // scalar are proven, the identical second walk cannot fail under the
    // immutable/disjoint input contract. No heap scratch or partial writes.
    for (std::uint16_t region = 0U; region < parsed.store.region_count; ++region) {
        float scalar = 0.0F;
        if (!sfnt_item_variation_data::try_get_region_scalar(parsed.store, coordinates, region, scalar) ||
            !std::isfinite(scalar) || scalar < 0.0F || scalar > 1.0F) return fail(error);
    }
    sfnt_vertical_metrics_variation_instance candidate{};
    candidate.font_bytes_ = data_;
    candidate.face_index_ = face_index_;
    candidate.face_offset_ = face_offset_;
    candidate.glyph_count_ = parsed.glyph_count;
    candidate.prepared_ = true;
    candidate.uses_vvar_ = parsed.uses_vvar;
    candidate.store_ = parsed.store;
    for (std::size_t kind = 0U; kind < 4U; ++kind) {
        candidate.maps_[kind] = parsed.maps[kind];
        candidate.has_maps_[kind] = parsed.has_maps[kind];
    }
    candidate.region_scalars_ = region_scalars.first(parsed.store.region_count);
    for (std::uint16_t region = 0U; region < parsed.store.region_count; ++region) {
        float scalar = 0.0F;
        (void)sfnt_item_variation_data::try_get_region_scalar(parsed.store, coordinates, region, scalar);
        region_scalars[region] = scalar;
    }
    result = candidate;
    if (error != nullptr) *error = font_error::none;
    return true;
}

bool sfnt_font_view::try_get_vertical_metrics_variation(std::uint16_t glyph,
    const sfnt_vertical_metrics_variation_instance& variation,
    sfnt_vertical_metrics_variation& result, font_error* error) const noexcept {
    return try_get_vertical_metrics_variation(std::span<const std::uint16_t>(&glyph, 1U),
        variation, std::span<sfnt_vertical_metrics_variation>(&result, 1U), error);
}

bool sfnt_font_view::try_get_vertical_metrics_variation(std::span<const std::uint16_t> glyphs,
    const sfnt_vertical_metrics_variation_instance& variation,
    std::span<sfnt_vertical_metrics_variation> results, font_error* error) const noexcept {
    if (!disjoint_outputs(error, {memory(results)}, {memory(data_), memory(*this),
        memory(glyphs), memory(variation), memory(variation.region_scalars_),
        memory(variation.font_bytes_)})) return false;
    if (!variation.prepared_ || variation.font_bytes_.data() != data_.data() ||
        variation.font_bytes_.size() != data_.size() || variation.face_index_ != face_index_ ||
        variation.face_offset_ != face_offset_)
        return fail(error, font_error::invalid_argument);
    if (results.size() < glyphs.size()) return fail(error, font_error::insufficient_buffer);
    for (const auto scalar : variation.region_scalars_) {
        if (!std::isfinite(scalar) || scalar < 0.0F || scalar > 1.0F)
            return fail(error, font_error::invalid_argument);
    }
    for (const auto glyph : glyphs) {
        if (glyph >= variation.glyph_count_) return fail(error, font_error::invalid_argument);
        sfnt_vertical_metrics_variation candidate{};
        if (!read_deltas(glyph, variation.store_, variation.maps_, variation.has_maps_,
            variation.region_scalars_, variation.uses_vvar_, candidate)) return fail(error);
    }
    for (std::size_t index = 0U; index < glyphs.size(); ++index) {
        sfnt_vertical_metrics_variation candidate{};
        (void)read_deltas(glyphs[index], variation.store_, variation.maps_, variation.has_maps_,
            variation.region_scalars_, variation.uses_vvar_, candidate);
        results[index] = candidate;
    }
    if (error != nullptr) *error = font_error::none;
    return true;
}
} // namespace progpu::native::text
