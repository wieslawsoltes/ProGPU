#include "progpu_native_direct2d.h"

#include <cstddef>
#include <cstdint>
#include <type_traits>

static_assert(std::is_standard_layout_v<progpu_native_direct2d_guid>);
static_assert(sizeof(progpu_native_direct2d_guid) == 16U);
static_assert(sizeof(progpu_native_direct2d_point_2f) == 8U);
static_assert(sizeof(progpu_native_direct2d_rect_f) == 16U);
static_assert(sizeof(progpu_native_direct2d_matrix_3x2_f) == 24U);
static_assert(sizeof(progpu_native_direct2d_color_f) == 16U);
static_assert(sizeof(progpu_native_direct2d_triangle) == 24U);
static_assert(sizeof(progpu_native_direct2d_command_stream_summary) == 64U);
static_assert(sizeof(progpu_native_direct2d_scene_stream_result) == 80U);

#if defined(_WIN32)
static_assert(PROGPU_NATIVE_DIRECT2D_HAS_WINDOWS_PROVIDER == 1);
#else
static_assert(PROGPU_NATIVE_DIRECT2D_HAS_WINDOWS_PROVIDER == 0);
#endif

int main()
{
    constexpr progpu_native_direct2d_guid identity{
        0x19967CEEU,
        0xEA52U,
        0x45DDU,
        {0x9FU, 0xDAU, 0xD9U, 0x70U, 0x3AU, 0x9FU, 0xD1U, 0x50U}};
    constexpr progpu_native_direct2d_matrix_3x2_f matrix{
        1.0F, 0.0F, 0.0F, 1.0F, 12.0F, -4.0F};
    constexpr progpu_native_direct2d_rect_f rectangle{
        1.0F, 2.0F, 31.0F, 42.0F};

    if (identity.data1 != 0x19967CEEU ||
        identity.data4[7] != 0x50U ||
        matrix.m11 != 1.0F ||
        matrix.m31 != 12.0F ||
        rectangle.width != 31.0F ||
        PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED != 13) {
        return 1;
    }
    return 0;
}
