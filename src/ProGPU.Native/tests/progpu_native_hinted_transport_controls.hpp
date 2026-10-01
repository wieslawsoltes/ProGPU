#pragma once

#include "progpu_native_hinted_transport.hpp"

#include <array>
#include <cstring>
#include <source_location>
#include <stdexcept>
#include <string>
#include <vector>

namespace progpu::native::text::tests {
inline void transport_require(bool condition,
    std::source_location location = std::source_location::current())
{
    if (!condition) throw std::runtime_error("Hinted transport failed at line " + std::to_string(location.line()));
}

inline void verify_hinted_transport(const hinted_glyph_batch& batch)
{
    progpu_native_hinted_batch_counts counts{};
    transport_require(get_hinted_batch_counts(batch, counts) == hinted_transport_error::none);
    for (const auto policy : {hinted_transport_policy::automatic, hinted_transport_policy::intrinsic_simd,
            hinted_transport_policy::scalar_reference}) {
        std::vector<progpu_native_hinted_glyph> glyphs(static_cast<std::size_t>(counts.glyphs) + 3U);
        std::vector<progpu_native_hinted_point> points(static_cast<std::size_t>(counts.points) + 3U);
        std::vector<std::uint8_t> tags(static_cast<std::size_t>(counts.points) + 3U, 0xA5U);
        std::vector<std::int32_t> contours(static_cast<std::size_t>(counts.contours) + 3U, -713);
        std::memset(glyphs.data(), 0xA5, glyphs.size() * sizeof(glyphs[0]));
        std::memset(points.data(), 0xA5, points.size() * sizeof(points[0]));
        const auto glyph_tail = glyphs.back();
        const auto point_tail = points.back();
        const auto result = copy_hinted_batch(batch, glyphs, points, tags, contours, policy);
#if !defined(__aarch64__) && !defined(_M_ARM64) && !defined(__SSE2__) && !defined(_M_X64)
        if (policy == hinted_transport_policy::intrinsic_simd) {
            transport_require(result.error == hinted_transport_error::unsupported_policy);
            continue;
        }
#endif
        transport_require(static_cast<bool>(result));
        if (policy == hinted_transport_policy::scalar_reference)
            transport_require(result.path == hinted_transport_path::scalar_reference);
        if (policy == hinted_transport_policy::intrinsic_simd)
            transport_require(result.path == hinted_transport_path::intrinsic_simd);
        if (policy == hinted_transport_policy::automatic && sizeof(long) == 8U)
            transport_require(result.path == hinted_transport_path::bulk_copy);
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
        if (policy == hinted_transport_policy::automatic && sizeof(long) == 4U)
            transport_require(result.path == hinted_transport_path::intrinsic_simd);
#endif
        std::uint32_t point_offset = 0U, contour_offset = 0U;
        for (std::size_t index = 0U; index < batch.glyphs.size(); ++index) {
            const auto& source = batch.glyphs[index];
            const auto& destination = glyphs[index];
            transport_require(destination.glyph_index == source.glyph_index &&
                destination.point_offset == point_offset && destination.point_count == source.points.size() &&
                destination.contour_offset == contour_offset && destination.contour_count == source.contour_ends.size() &&
                destination.outline_flags == static_cast<std::uint32_t>(source.outline_flags));
            const std::array<std::int64_t, 14> expected{source.advance_x_26_6, source.advance_y_26_6,
                source.horizontal_bearing_x_26_6, source.horizontal_bearing_y_26_6, source.width_26_6, source.height_26_6,
                source.horizontal_advance_26_6, source.vertical_bearing_x_26_6, source.vertical_bearing_y_26_6,
                source.vertical_advance_26_6, source.linear_horizontal_advance_16_16,
                source.linear_vertical_advance_16_16, source.left_side_bearing_delta_26_6, source.right_side_bearing_delta_26_6};
            const std::array<std::int64_t, 14> actual{destination.advance_x_26_6, destination.advance_y_26_6,
                destination.horizontal_bearing_x_26_6, destination.horizontal_bearing_y_26_6,
                destination.width_26_6, destination.height_26_6, destination.horizontal_advance_26_6,
                destination.vertical_bearing_x_26_6, destination.vertical_bearing_y_26_6, destination.vertical_advance_26_6,
                destination.linear_horizontal_advance_16_16, destination.linear_vertical_advance_16_16,
                destination.left_side_bearing_delta_26_6, destination.right_side_bearing_delta_26_6};
            transport_require(actual == expected);
            for (std::size_t point = 0U; point < source.points.size(); ++point) {
                transport_require(points[point_offset + point].x_26_6 == source.points[point].x_26_6 &&
                    points[point_offset + point].y_26_6 == source.points[point].y_26_6 &&
                    tags[point_offset + point] == source.tags[point]);
            }
            for (std::size_t contour = 0U; contour < source.contour_ends.size(); ++contour)
                transport_require(contours[contour_offset + contour] == source.contour_ends[contour]);
            point_offset += static_cast<std::uint32_t>(source.points.size());
            contour_offset += static_cast<std::uint32_t>(source.contour_ends.size());
        }
        transport_require(point_offset == counts.points && contour_offset == counts.contours);
        for (std::size_t index = counts.glyphs; index < glyphs.size(); ++index)
            transport_require(std::memcmp(&glyphs[index], &glyph_tail, sizeof(glyph_tail)) == 0);
        for (std::size_t index = counts.points; index < points.size(); ++index)
            transport_require(std::memcmp(&points[index], &point_tail, sizeof(point_tail)) == 0 && tags[index] == 0xA5U);
        for (std::size_t index = counts.contours; index < contours.size(); ++index)
            transport_require(contours[index] == -713);
    }
}
} // namespace progpu::native::text::tests
