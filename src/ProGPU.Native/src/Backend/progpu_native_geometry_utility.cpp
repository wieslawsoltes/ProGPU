#include "progpu_native.h"
#include "../Direct2D/progpu_native_direct2d_path.hpp"

#include <cmath>
#include <cstring>
#include <limits>
#include <memory>
#include <type_traits>
#include <vector>

struct progpu_native_geometry_outline final {
    std::unique_ptr<progpu_native_point[]> points;
    std::vector<std::uint32_t> offsets;
};

extern "C" PROGPU_NATIVE_API progpu_native_status progpu_native_geometry_combine(
    const progpu_native_path_segment* first, std::uint32_t first_count, std::uint32_t first_fill,
    const progpu_native_path_segment* second, std::uint32_t second_count, std::uint32_t second_fill,
    std::uint32_t mode, float tolerance, progpu_native_geometry_outline** result,
    const progpu_native_point** points, std::uint32_t* point_count,
    const std::uint32_t** contour_offsets, std::uint32_t* contour_count)
{
    if (result == nullptr || points == nullptr || point_count == nullptr ||
        contour_offsets == nullptr || contour_count == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *result = nullptr;
    *points = nullptr;
    *point_count = 0U;
    *contour_offsets = nullptr;
    *contour_count = 0U;
    if ((first_count != 0U && first == nullptr) || (second_count != 0U && second == nullptr) ||
        first_count > (1U << 20U) || second_count > (1U << 20U) ||
        first_fill > PROGPU_NATIVE_FILL_RULE_EVEN_ODD || second_fill > PROGPU_NATIVE_FILL_RULE_EVEN_ODD ||
        mode > 3U || !std::isfinite(tolerance) || tolerance <= 0.0F) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;

    namespace d2d = progpu::native::direct2d::compat;
    namespace com = progpu::native::com;
    static_assert(static_cast<std::uint32_t>(d2d::combine_mode::union_value) == 0U);
    static_assert(static_cast<std::uint32_t>(d2d::combine_mode::intersect) == 1U);
    static_assert(static_cast<std::uint32_t>(d2d::combine_mode::xor_value) == 2U);
    static_assert(static_cast<std::uint32_t>(d2d::combine_mode::exclude) == 3U);
    try {
        std::vector<std::vector<d2d::point_2f>> contours;
        const auto fill = [](std::uint32_t value) {
            return value == PROGPU_NATIVE_FILL_RULE_EVEN_ODD ? d2d::fill_mode::alternate : d2d::fill_mode::winding;
        };
        const com::result status = d2d::detail::combine_native_fill_contours(
            {first, first_count}, fill(first_fill), {second, second_count}, fill(second_fill),
            static_cast<d2d::combine_mode>(mode), tolerance, contours);
        if (com::failed(status)) {
            if (status == com::out_of_memory) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
            if (status == com::invalid_argument || status == com::pointer_error) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            if (status == d2d::not_implemented) return PROGPU_NATIVE_STATUS_UNSUPPORTED;
            return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
        }

        // One bounded ownership copy of the existing core's result. Bulk copies
        // use compiler/runtime intrinsic memcpy, not a new scalar point kernel.
        constexpr auto maximum = (std::numeric_limits<std::uint32_t>::max)();
        if (contours.size() >= maximum) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
        auto snapshot = std::make_unique<progpu_native_geometry_outline>();
        snapshot->offsets.reserve(contours.size() + 1U);
        snapshot->offsets.push_back(0U);
        std::uint32_t total = 0U;
        for (const auto& contour : contours) {
            if (contour.size() > maximum - total) return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
            total += static_cast<std::uint32_t>(contour.size());
            snapshot->offsets.push_back(total);
        }
        snapshot->points = std::make_unique_for_overwrite<progpu_native_point[]>(total);
        static_assert(sizeof(d2d::point_2f) == sizeof(progpu_native_point));
        static_assert(offsetof(d2d::point_2f, x) == offsetof(progpu_native_point, x));
        static_assert(offsetof(d2d::point_2f, y) == offsetof(progpu_native_point, y));
        static_assert(std::is_trivially_copyable_v<d2d::point_2f>);
        static_assert(std::is_trivially_copyable_v<progpu_native_point>);
        for (std::size_t index = 0U; index < contours.size(); ++index) {
            const auto& contour = contours[index];
            if (!contour.empty()) std::memcpy(snapshot->points.get() + snapshot->offsets[index],
                contour.data(), contour.size() * sizeof(progpu_native_point));
        }
        *points = snapshot->points.get();
        *point_count = total;
        *contour_offsets = snapshot->offsets.data();
        *contour_count = static_cast<std::uint32_t>(contours.size());
        *result = snapshot.release();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

extern "C" PROGPU_NATIVE_API void progpu_native_geometry_outline_destroy(
    progpu_native_geometry_outline* result)
{
    delete result;
}
