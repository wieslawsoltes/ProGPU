#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned OpenTypeVariationData.ParseMvar
// and GetMetricDelta at checkpoint df58d65c. Records and store bytes stay
// borrowed; duplicate tags preserve the managed last-record-wins contract.
namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_u16;

constexpr auto mvar_tag = open_type_tag::from_chars('M', 'V', 'A', 'R');

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

} // namespace

bool sfnt_font_view::try_get_metric_variation(
    open_type_tag metric_tag,
    std::span<const std::int16_t> normalized_coordinates,
    float& result,
    bool& has_metric_record,
    font_error* error) const noexcept {
    result = 0.0F;
    has_metric_record = false;
    set_error(error, font_error::none);
    sfnt_table_view mvar{};
    if (!try_get_table(mvar_tag, mvar)) {
        return true;
    }
    if (!can_read(mvar.bytes, 0U, 12U)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto record_size = read_u16(mvar.bytes, 6U);
    const auto record_count = read_u16(mvar.bytes, 8U);
    const auto store_relative = read_u16(mvar.bytes, 10U);
    if (record_size < 8U || store_relative == 0U) {
        return true;
    }
    if (record_count != 0U &&
        record_size >
            std::numeric_limits<std::size_t>::max() / record_count) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    const auto records_size =
        static_cast<std::size_t>(record_count) * record_size;
    if (!can_read(mvar.bytes, 12U, records_size)) {
        set_error(error, font_error::invalid_face);
        return false;
    }
    std::uint16_t outer_index = 0U;
    std::uint16_t inner_index = 0U;
    for (std::uint16_t index = 0U; index < record_count; ++index) {
        const auto offset = 12U + static_cast<std::size_t>(index) * record_size;
        const open_type_tag tag{
            static_cast<std::uint32_t>(
                std::to_integer<std::uint8_t>(mvar.bytes[offset])) << 24U |
            static_cast<std::uint32_t>(
                std::to_integer<std::uint8_t>(mvar.bytes[offset + 1U])) << 16U |
            static_cast<std::uint32_t>(
                std::to_integer<std::uint8_t>(mvar.bytes[offset + 2U])) << 8U |
            std::to_integer<std::uint8_t>(mvar.bytes[offset + 3U])};
        if (tag != metric_tag) {
            continue;
        }
        has_metric_record = true;
        outer_index = read_u16(mvar.bytes, offset + 4U);
        inner_index = read_u16(mvar.bytes, offset + 6U);
    }
    if (!has_metric_record) {
        return true;
    }
    std::uint16_t axis_count = 0U;
    if (!try_get_variation_axis_count(axis_count, error)) {
        return false;
    }
    sfnt_item_variation_store_view store{};
    if (!sfnt_item_variation_data::try_get_store(
            mvar.bytes, store_relative, axis_count, store, error)) {
        return false;
    }
    return sfnt_item_variation_data::try_get_delta(
        store,
        normalized_coordinates,
        outer_index,
        inner_index,
        result,
        error);
}

} // namespace progpu::native::text
