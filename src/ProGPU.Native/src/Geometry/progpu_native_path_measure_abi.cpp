#include "progpu_native.h"
#include "progpu_native_path_measure.hpp"

#include <memory>
#include <new>
#include <vector>

struct progpu_native_path_measure final {
    progpu::native::geometry::path_measure value;
};

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_path_measure_create(
    const progpu_native_geometry_query_figure* figures, std::uint32_t figure_count,
    const progpu_native_path_segment* segments, std::uint32_t segment_count,
    std::uint32_t force_closed, float resolution_scale, progpu_native_path_measure** measure)
{
    if (measure == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *measure = nullptr;
    if ((figure_count != 0U && figures == nullptr) || (segment_count != 0U && segments == nullptr) ||
        force_closed > 1U)
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        auto result = std::make_unique<progpu_native_path_measure>();
        const progpu_native_status status = result->value.build(
            {figures, figure_count}, {segments, segment_count}, force_closed != 0U, resolution_scale);
        if (status != PROGPU_NATIVE_STATUS_SUCCESS) return status;
        *measure = result.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

extern "C" PROGPU_NATIVE_API void progpu_native_path_measure_destroy(progpu_native_path_measure* measure)
{
    delete measure;
}

extern "C" PROGPU_NATIVE_API std::uint32_t progpu_native_path_measure_contour_count(
    const progpu_native_path_measure* measure)
{
    return measure == nullptr ? 0U : measure->value.contour_count();
}

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_path_measure_contour(
    const progpu_native_path_measure* measure, std::uint32_t contour, float* length, std::uint32_t* closed)
{
    if (length != nullptr) *length = 0.0F;
    if (closed != nullptr) *closed = 0U;
    if (measure == nullptr || contour >= measure->value.contour_count()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    if (length != nullptr) *length = measure->value.length(contour);
    if (closed != nullptr) *closed = measure->value.closed(contour) ? 1U : 0U;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_path_measure_position_tangent(
    const progpu_native_path_measure* measure, std::uint32_t contour, float distance,
    progpu_native_point* position, progpu_native_point* tangent)
{
    if (position != nullptr) *position = {};
    if (tangent != nullptr) *tangent = {};
    if (measure == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    return measure->value.position_tangent(contour, distance, position, tangent)
        ? PROGPU_NATIVE_STATUS_SUCCESS
        : PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
}

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_path_measure_segment(
    const progpu_native_path_measure* measure, std::uint32_t contour,
    float start_distance, float stop_distance,
    progpu_native_path_segment* segments, std::uint32_t capacity, std::uint32_t* count)
{
    if (count == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *count = 0U;
    if (measure == nullptr || contour >= measure->value.contour_count() || (capacity != 0U && segments == nullptr))
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    try {
        std::vector<progpu_native_path_segment> pieces;
        if (!measure->value.segment(contour, start_distance, stop_distance, pieces)) return PROGPU_NATIVE_STATUS_SUCCESS;
        *count = static_cast<std::uint32_t>(pieces.size());
        if (segments == nullptr) return PROGPU_NATIVE_STATUS_SUCCESS;
        if (capacity < pieces.size()) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        for (std::size_t index = 0; index < pieces.size(); ++index) segments[index] = pieces[index];
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}
