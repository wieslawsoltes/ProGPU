#include "progpu_native_hinted_gpos.hpp"

#include <array>
#include <cstring>
#include <limits>
#include <type_traits>

namespace progpu::native::text {
namespace {
template<hinted_projection_policy Policy>
bool project(const void* owner, const std::array<detail::gpos_metric_vector, 2>& input,
    std::array<detail::gpos_metric_vector, 2>& output) noexcept
{
    static_assert(sizeof(hinted_design_vector) == sizeof(detail::gpos_metric_vector));
    static_assert(std::is_trivially_copyable_v<hinted_design_vector> &&
        std::is_trivially_copyable_v<detail::gpos_metric_vector>);
    std::array<hinted_design_vector, 2> design{};
    std::memcpy(design.data(), input.data(), sizeof(design));
    std::array<hinted_outline_point, 2> device{};
    const auto& batch = *static_cast<const hinted_glyph_batch*>(owner);
    if (project_hinted_design_vectors(batch, design, device, Policy).error != hinted_projection_error::none) return false;
    // Projection has already proven every rounded coordinate's signed32 domain.
    const std::array<detail::gpos_metric_vector, 2> candidate{{
        {static_cast<std::int32_t>(device[0].x_26_6), static_cast<std::int32_t>(device[0].y_26_6)},
        {static_cast<std::int32_t>(device[1].x_26_6), static_cast<std::int32_t>(device[1].y_26_6)}}};
    output = candidate;
    return true;
}

bool contour(const void* owner, std::size_t descriptor, std::uint32_t glyph_id,
    std::uint16_t contour_index, detail::gpos_metric_vector& output) noexcept
{
    const auto& batch = *static_cast<const hinted_glyph_batch*>(owner);
    if (descriptor >= batch.glyphs.size() || batch.glyphs[descriptor].glyph_index != glyph_id) return false;
    const auto result = get_hinted_anchor_point(batch, descriptor, contour_index);
    if (result.error != hinted_projection_error::none) return false;
    output = {static_cast<std::int32_t>(result.point.x_26_6), static_cast<std::int32_t>(result.point.y_26_6)};
    return true;
}
} // namespace

hinted_gpos_frame_result bind_hinted_gpos_frame(const hinted_glyph_batch& batch,
    const sfnt_font_view& font, hinted_projection_policy policy,
    std::span<const std::int16_t> normalized_coordinates) noexcept
{
    if (batch.identity == nullptr || batch.identity->source == nullptr ||
        font.data().data() != batch.identity->source->bytes.data() ||
        font.data().size() != batch.identity->source->bytes.size() ||
        font.face_index() != batch.identity->source->face_index)
        return {hinted_projection_error::invalid_argument, {}};
    const auto& metrics = batch.identity->device_frame;
    sfnt_header_metrics header{};
    if (!font.try_get_header_metrics(header) || header.units_per_em != metrics.units_per_em ||
        metrics.x_pixels_per_em > std::numeric_limits<std::uint16_t>::max() ||
        metrics.y_pixels_per_em > std::numeric_limits<std::uint16_t>::max())
        return {hinted_projection_error::unsupported_frame, {}};
    std::uint16_t axis_count = 0U;
    if (!font.try_get_variation_axis_count(axis_count) ||
        batch.identity->variation_coordinates_16_16.size() != axis_count ||
        normalized_coordinates.size() != axis_count)
        return {hinted_projection_error::unsupported_frame, {}};
    const auto normalized_address = reinterpret_cast<std::uintptr_t>(normalized_coordinates.data());
    if (!normalized_coordinates.empty() && (normalized_coordinates.data() == nullptr ||
        normalized_address % alignof(std::int16_t) != 0U ||
        normalized_coordinates.size_bytes() > std::numeric_limits<std::uintptr_t>::max() - normalized_address))
        return {hinted_projection_error::invalid_argument, {}};
    // Original native fvar/avar normalization, with raw fixed-point identity.
    // Never infer default axes or round-trip them through managed floats.
    for (std::uint16_t index = 0U; index < axis_count; ++index) {
        std::int16_t normalized = 0;
        if (!font.try_normalize_variation_coordinate(index,
            batch.identity->variation_coordinates_16_16[index], normalized) ||
            normalized != normalized_coordinates[index])
            return {hinted_projection_error::unsupported_frame, {}};
    }
    // Reuse actual projection admission/selection, even for an empty request.
    const auto selection = project_hinted_design_vectors(batch, {}, {}, policy);
    if (selection.error != hinted_projection_error::none) return {selection.error, {}};
    detail::gpos_device_frame frame{};
    frame.font = &font;
    frame.owner = &batch;
    frame.pixels_per_em_x = static_cast<std::uint16_t>(metrics.x_pixels_per_em);
    frame.pixels_per_em_y = static_cast<std::uint16_t>(metrics.y_pixels_per_em);
    frame.normalized_coordinates = normalized_coordinates;
    frame.arithmetic_path = selection.path == hinted_projection_path::scalar_reference ?
        detail::gpos_arithmetic_path::scalar_reference : detail::gpos_arithmetic_path::intrinsic_simd;
    frame.project_design = selection.path == hinted_projection_path::scalar_reference ?
        &project<hinted_projection_policy::scalar_reference> : &project<hinted_projection_policy::intrinsic_simd>;
    frame.contour_point = &contour;
    return {hinted_projection_error::none, frame};
}
} // namespace progpu::native::text
