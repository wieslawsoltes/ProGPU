#pragma once

#include <cstdint>
#include <type_traits>

/* Neutral vocabulary of the native Geometry module (progpu_native_geometry):
 * points, matrices, segments, stroke properties, path enums and a status code.
 * The device-independent path, Boolean and stroke algorithms of the engine are
 * written against these types; Direct2D, MIL and the C ABI are clients.
 *
 * Type strategy. The Direct2D C structs (progpu_native_direct2d_point_2f,
 * progpu_native_direct2d_matrix_3x2_f, progpu_native_direct2d_triangle) remain
 * the public C ABI and the types of the Direct2D C++ layer. The types here have
 * the same layout; the Direct2D layer converts at its boundary with zero-cost
 * std::bit_cast helpers (progpu_native_direct2d_core.hpp, which also holds the
 * layout static_asserts, because this header includes no Direct2D or COM
 * header). Types without point members (rectangle edges, size, the enums and
 * the stroke style properties) are shared directly: the Direct2D core and
 * compatibility layers alias them, so their code is unchanged.
 *
 * Status strategy. status is a 32-bit integer whose named values equal the COM
 * result codes the Direct2D layer reports for the same conditions, so the
 * Direct2D layer converts by value and the C ABI maps them to
 * progpu_native_status exactly as before. */

namespace progpu::native::geometry {

using status = std::int32_t;

namespace status_code {

inline constexpr status ok = 0;
inline constexpr status invalid_argument = -2147024809;    // 0x80070057
inline constexpr status out_of_memory = -2147024882;       // 0x8007000E
inline constexpr status pointer_error = -2147467261;       // 0x80004003
inline constexpr status not_implemented = -2147467263;     // 0x80004001
inline constexpr status failure = -2147467259;             // 0x80004005
inline constexpr status wrong_state = -2003238911;         // 0x88990001
inline constexpr status wrong_factory = -2003238894;       // 0x88990012

} // namespace status_code

[[nodiscard]] constexpr bool succeeded(status value) noexcept
{
    return value >= 0;
}

[[nodiscard]] constexpr bool failed(status value) noexcept
{
    return value < 0;
}

inline constexpr float default_flattening_tolerance = 0.25F;

struct point_2f final {
    float x;
    float y;
};

/* Row-vector affine matrix: x' = x * m11 + y * m21 + m31. */
struct matrix_3x2_f final {
    float m11;
    float m12;
    float m21;
    float m22;
    float m31;
    float m32;
};

/* Rectangles use edge coordinates rather than x/y/width/height, which keeps
 * valid spans whose mathematical width is larger than FLT_MAX. */
struct rectangle_edges_f final {
    float left;
    float top;
    float right;
    float bottom;
};

struct size_f final {
    float width;
    float height;
};

enum class arc_sweep_direction : std::uint32_t {
    counter_clockwise = 0U,
    clockwise = 1U
};

enum class arc_size_kind : std::uint32_t {
    small_value = 0U,
    large_value = 1U
};

struct arc_segment_f final {
    point_2f point;
    size_f size;
    float rotation_angle;
    arc_sweep_direction sweep;
    arc_size_kind size_kind;
};

struct cubic_bezier_segment_f final {
    point_2f point1;
    point_2f point2;
    point_2f point3;
};

struct quadratic_bezier_segment_f final {
    point_2f point1;
    point_2f point2;
};

struct ellipse_f final {
    point_2f point;
    float radius_x;
    float radius_y;
};

struct rounded_rectangle_f final {
    rectangle_edges_f rectangle;
    float radius_x;
    float radius_y;
};

struct triangle_f final {
    point_2f point1;
    point_2f point2;
    point_2f point3;
};

enum class cap_style : std::uint32_t {
    flat = 0U,
    square = 1U,
    round = 2U,
    triangle = 3U
};

enum class line_join : std::uint32_t {
    miter = 0U,
    bevel = 1U,
    round = 2U,
    miter_or_bevel = 3U
};

enum class dash_style : std::uint32_t {
    solid = 0U,
    dash = 1U,
    dot = 2U,
    dash_dot = 3U,
    dash_dot_dot = 4U,
    custom = 5U
};

struct stroke_style_properties_f final {
    cap_style start_cap;
    cap_style end_cap;
    cap_style dash_cap;
    line_join join;
    float miter_limit;
    dash_style dash;
    float dash_offset;
};

enum class fill_mode : std::uint32_t {
    alternate = 0U,
    winding = 1U
};

/* Segment flags; values combine as a bit set. */
enum class path_segment : std::uint32_t {
    none = 0U,
    force_unstroked = 1U,
    force_round_line_join = 2U
};

enum class figure_begin : std::uint32_t {
    filled = 0U,
    hollow = 1U
};

enum class figure_end : std::uint32_t {
    open = 0U,
    closed = 1U
};

enum class geometry_relation : std::uint32_t {
    unknown = 0U,
    disjoint = 1U,
    is_contained = 2U,
    contains = 3U,
    overlap = 4U
};

enum class combine_mode : std::uint32_t {
    union_value = 0U,
    intersect = 1U,
    xor_value = 2U,
    exclude = 3U
};

/* A stroke style by value: the properties plus a borrowed dash array.
 * dash_count is the style's dash count. dashes points to dash_count readable
 * values, or is null when they were not read (the algorithms read them only for
 * dash_style::custom with 1 to 2^20 dashes, and reject other counts first). */
struct stroke_style final {
    stroke_style_properties_f properties{};
    const float* dashes = nullptr;
    std::uint32_t dash_count = 0U;

    /* Copies min(dash_count, count) values: the contract of GetDashes. */
    void copy_dashes(float* destination, std::uint32_t count) const noexcept
    {
        if (destination == nullptr || count == 0U || dashes == nullptr) {
            return;
        }
        const std::uint32_t available = dash_count < count ? dash_count : count;
        for (std::uint32_t index = 0U; index < available; ++index) {
            destination[index] = dashes[index];
        }
    }
};

static_assert(std::is_trivially_copyable_v<point_2f>);
static_assert(std::is_trivially_copyable_v<matrix_3x2_f>);
static_assert(std::is_trivially_copyable_v<arc_segment_f>);
static_assert(std::is_trivially_copyable_v<cubic_bezier_segment_f>);
static_assert(std::is_trivially_copyable_v<quadratic_bezier_segment_f>);
static_assert(std::is_trivially_copyable_v<ellipse_f>);
static_assert(std::is_trivially_copyable_v<triangle_f>);
static_assert(sizeof(point_2f) == 2U * sizeof(float));
static_assert(sizeof(matrix_3x2_f) == 6U * sizeof(float));
static_assert(sizeof(cubic_bezier_segment_f) == 3U * sizeof(point_2f));
static_assert(sizeof(quadratic_bezier_segment_f) == 2U * sizeof(point_2f));
static_assert(sizeof(triangle_f) == 3U * sizeof(point_2f));

} // namespace progpu::native::geometry
