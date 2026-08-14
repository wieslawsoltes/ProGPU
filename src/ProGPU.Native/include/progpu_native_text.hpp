#ifndef PROGPU_NATIVE_TEXT_HPP
#define PROGPU_NATIVE_TEXT_HPP

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>
#include <span>

namespace progpu::native::text {

enum class font_error : std::uint32_t {
    none = 0U,
    invalid_argument,
    unsupported_container,
    invalid_collection,
    invalid_face,
    truncated_directory,
    invalid_glyph,
    insufficient_buffer
};

struct open_type_tag final {
    std::uint32_t value = 0U;

    static constexpr open_type_tag from_chars(
        char a,
        char b,
        char c,
        char d) noexcept {
        return open_type_tag{
            (static_cast<std::uint32_t>(
                static_cast<unsigned char>(a)) << 24U) |
            (static_cast<std::uint32_t>(
                static_cast<unsigned char>(b)) << 16U) |
            (static_cast<std::uint32_t>(
                static_cast<unsigned char>(c)) << 8U) |
            static_cast<std::uint32_t>(
                static_cast<unsigned char>(d))};
    }

    friend constexpr bool operator==(
        open_type_tag,
        open_type_tag) noexcept = default;
};

struct sfnt_table_view final {
    open_type_tag tag{};
    std::uint32_t checksum = 0U;
    std::span<const std::byte> bytes{};
};

struct sfnt_header_metrics final {
    std::uint16_t units_per_em = 0U;
    std::int16_t x_min = 0;
    std::int16_t y_min = 0;
    std::int16_t x_max = 0;
    std::int16_t y_max = 0;
    std::int16_t index_to_loc_format = 0;
};

struct sfnt_horizontal_header_metrics final {
    std::int16_t ascender = 0;
    std::int16_t descender = 0;
    std::int16_t line_gap = 0;
    std::uint16_t advance_width_max = 0U;
    std::uint16_t number_of_horizontal_metrics = 0U;
};

struct sfnt_horizontal_glyph_metrics final {
    std::uint16_t advance_width = 0U;
    std::int16_t left_side_bearing = 0;
};

struct sfnt_glyph_data_view final {
    std::int16_t contour_count = 0;
    std::int16_t x_min = 0;
    std::int16_t y_min = 0;
    std::int16_t x_max = 0;
    std::int16_t y_max = 0;
    std::span<const std::byte> bytes{};

    bool empty() const noexcept {
        return bytes.empty();
    }
};

enum class sfnt_glyph_kind : std::uint8_t {
    empty = 0U,
    simple,
    composite
};

struct sfnt_glyph_decode_requirements final {
    sfnt_glyph_kind kind = sfnt_glyph_kind::empty;
    std::uint16_t contour_count = 0U;
    std::uint32_t point_count = 0U;
    std::uint32_t path_segment_count = 0U;
    std::uint16_t instruction_bytes = 0U;
};

struct sfnt_outline_point final {
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::uint8_t flags = 0U;

    bool on_curve() const noexcept {
        return (flags & 0x01U) != 0U;
    }
};

struct sfnt_composite_glyph_decode_requirements final {
    std::uint32_t component_count = 0U;
    std::uint16_t instruction_bytes = 0U;
};

struct sfnt_composite_component final {
    std::uint16_t flags = 0U;
    std::uint16_t glyph_index = 0U;
    std::int32_t argument1 = 0;
    std::int32_t argument2 = 0;
    float m00 = 1.0F;
    float m01 = 0.0F;
    float m10 = 0.0F;
    float m11 = 1.0F;
};

struct sfnt_expanded_glyph_requirements final {
    std::uint32_t point_count = 0U;
    std::uint32_t path_segment_count = 0U;
    std::uint32_t simple_point_scratch_count = 0U;
    std::uint16_t simple_contour_scratch_count = 0U;
};

/*
 * One fixed-size axis record borrowed from an OpenType fvar table. Values stay
 * in signed 16.16 form so the native port can normalize and cache instances
 * without a float round trip. Name resolution is a separate provider concern.
 */
struct sfnt_variation_axis final {
    open_type_tag tag{};
    std::int32_t minimum_fixed = 0;
    std::int32_t default_fixed = 0;
    std::int32_t maximum_fixed = 0;
    std::uint16_t flags = 0U;
    std::uint16_t name_id = 0U;

    float minimum() const noexcept;
    float default_value() const noexcept;
    float maximum() const noexcept;
    bool hidden() const noexcept;
};

/*
 * Borrowed metadata for an OpenType gvar table and one glyph's tuple-data
 * slice. Header parsing is O(G + A * T) only when the caller requests a glyph
 * offset or shared tuple, for G glyph offsets, A axes, and T shared tuples;
 * the views themselves retain no storage and never allocate.
 */
struct sfnt_gvar_header final {
    std::uint16_t axis_count = 0U;
    std::uint16_t shared_tuple_count = 0U;
    std::uint16_t glyph_count = 0U;
    bool uses_long_offsets = false;
};

struct sfnt_glyph_variation_data_view final {
    std::span<const std::byte> bytes{};
    std::uint16_t tuple_count = 0U;
    std::uint16_t serialized_data_offset = 0U;
    bool has_shared_point_numbers = false;

    bool empty() const noexcept {
        return bytes.empty();
    }
};

struct sfnt_packed_point_requirements final {
    std::uint32_t point_count = 0U;
    std::size_t bytes_consumed = 0U;
    bool all_points = false;
};

struct sfnt_packed_delta_requirements final {
    std::uint32_t delta_count = 0U;
    std::size_t bytes_consumed = 0U;
};

struct sfnt_gvar_tuple_requirements final {
    std::uint16_t tuple_count = 0U;
    std::uint32_t region_coordinate_count = 0U;
};

/*
 * Each header indexes a caller-owned coordinate block laid out as contiguous
 * start[A], peak[A], end[A] F2Dot14 arrays for A variation axes.
 */
struct sfnt_gvar_tuple_header final {
    std::uint32_t region_coordinate_offset = 0U;
    std::uint16_t serialized_data_size = 0U;
    std::uint16_t flags = 0U;

    bool has_private_point_numbers() const noexcept {
        return (flags & 0x2000U) != 0U;
    }
};

class sfnt_gvar_tuple_data final {
public:
    static float calculate_scalar(
        std::span<const std::int16_t> normalized_coordinates,
        std::span<const std::int16_t> region_coordinates) noexcept;
};

/*
 * Allocation-free TrueType IUP interpolation for one tuple's sparse deltas.
 * Validation is transactional; interpolation is O(P) time and O(1) internal
 * storage for P contour points.
 */
class sfnt_gvar_deltas final {
public:
    static bool try_infer_untouched(
        std::span<const progpu_native_point> original_points,
        std::span<const std::uint16_t> contour_end_points,
        std::span<float> x_deltas,
        std::span<float> y_deltas,
        std::span<const std::uint8_t> touched,
        font_error* error = nullptr) noexcept;
};

struct sfnt_simple_glyph_variation_requirements final {
    std::uint16_t tuple_header_count = 0U;
    std::uint32_t region_coordinate_count = 0U;
    std::uint32_t point_number_count = 0U;
    std::uint32_t delta_count = 0U;
    std::uint32_t tuple_point_count = 0U;
};

struct sfnt_simple_glyph_variation_scratch final {
    std::span<sfnt_gvar_tuple_header> tuple_headers{};
    std::span<std::int16_t> region_coordinates{};
    std::span<std::uint32_t> shared_point_numbers{};
    std::span<std::uint32_t> private_point_numbers{};
    std::span<std::int16_t> x_deltas{};
    std::span<std::int16_t> y_deltas{};
    std::span<float> tuple_x{};
    std::span<float> tuple_y{};
    std::span<std::uint8_t> touched{};
};

struct sfnt_composite_glyph_variation_requirements final {
    std::uint16_t tuple_header_count = 0U;
    std::uint32_t region_coordinate_count = 0U;
    std::uint32_t point_number_count = 0U;
    std::uint32_t delta_count = 0U;
};

struct sfnt_composite_glyph_variation_scratch final {
    std::span<sfnt_gvar_tuple_header> tuple_headers{};
    std::span<std::int16_t> region_coordinates{};
    std::span<std::uint32_t> shared_point_numbers{};
    std::span<std::uint32_t> private_point_numbers{};
    std::span<std::int16_t> x_deltas{};
    std::span<std::int16_t> y_deltas{};
};

struct sfnt_glyph_phantom_variation_requirements final {
    std::uint16_t tuple_header_count = 0U;
    std::uint32_t region_coordinate_count = 0U;
    std::uint32_t point_number_count = 0U;
    std::uint32_t delta_count = 0U;
};

struct sfnt_glyph_phantom_variation_scratch final {
    std::span<sfnt_gvar_tuple_header> tuple_headers{};
    std::span<std::int16_t> region_coordinates{};
    std::span<std::uint32_t> shared_point_numbers{};
    std::span<std::uint32_t> private_point_numbers{};
    std::span<std::int16_t> x_deltas{};
    std::span<std::int16_t> y_deltas{};
};

/*
 * Exact maximum caller storage for recursive variable TrueType expansion.
 * Measurement is O(G + C) for G reachable glyphs and C components; decoding
 * is O(G + C + T * (A + P + D)) and performs no internal heap allocation.
 * Component offsets reserve only the maximum active recursion path, not the
 * full expanded tree.
 */
struct sfnt_varied_glyph_requirements final {
    sfnt_expanded_glyph_requirements outline{};
    sfnt_simple_glyph_variation_requirements simple_variation{};
    sfnt_composite_glyph_variation_requirements composite_variation{};
    std::uint32_t varied_simple_point_count = 0U;
    std::uint32_t component_offset_count = 0U;
};

struct sfnt_varied_glyph_scratch final {
    std::span<std::uint16_t> simple_contour_end_points{};
    std::span<sfnt_outline_point> simple_points{};
    std::span<progpu_native_point> varied_simple_points{};
    std::span<progpu_native_point> component_offsets{};
    sfnt_simple_glyph_variation_scratch simple_variation{};
    sfnt_composite_glyph_variation_scratch composite_variation{};
};

/*
 * Transactional two-pass decoders for gvar packed point and delta streams.
 * Each pass is O(N) time with O(1) internal storage for N encoded values. The
 * caller owns every output span; insufficient or malformed input writes no
 * partial output.
 */
class sfnt_packed_variation_data final {
public:
    static bool try_get_point_requirements(
        std::span<const std::byte> data,
        sfnt_packed_point_requirements& result,
        font_error* error = nullptr) noexcept;
    static bool try_decode_points(
        std::span<const std::byte> data,
        std::span<std::uint32_t> points,
        std::uint32_t& written,
        std::size_t& bytes_consumed,
        font_error* error = nullptr) noexcept;
    static bool try_get_delta_requirements(
        std::span<const std::byte> data,
        std::uint32_t delta_count,
        sfnt_packed_delta_requirements& result,
        font_error* error = nullptr) noexcept;
    static bool try_decode_deltas(
        std::span<const std::byte> data,
        std::span<std::int16_t> deltas,
        std::uint32_t delta_count,
        std::uint32_t& written,
        std::size_t& bytes_consumed,
        font_error* error = nullptr) noexcept;
};

/*
 * Allocation-free lowering of decoded TrueType contours to the renderer's
 * canonical line/quadratic path ABI. The count pass and write pass are both
 * O(C + P) for C contours and P decoded points with O(1) internal storage.
 */
class sfnt_simple_glyph_path final {
public:
    static bool try_get_segment_count(
        std::span<const std::uint16_t> contour_end_points,
        std::span<const sfnt_outline_point> points,
        std::uint32_t& result,
        font_error* error = nullptr) noexcept;
    static bool try_write_segments(
        std::span<const std::uint16_t> contour_end_points,
        std::span<const sfnt_outline_point> points,
        std::span<progpu_native_path_segment> segments,
        std::uint32_t& written,
        font_error* error = nullptr) noexcept;
    static bool try_write_varied_segments(
        std::span<const std::uint16_t> contour_end_points,
        std::span<const sfnt_outline_point> original_points,
        std::span<const progpu_native_point> varied_points,
        std::span<progpu_native_path_segment> segments,
        std::uint32_t& written,
        font_error* error = nullptr) noexcept;
};

/*
 * Allocation-free borrowed view over one SFNT or TrueType Collection face.
 * The caller owns the byte span and must keep it alive for the view lifetime.
 * Construction and table lookup are O(T) for T directory records with O(1)
 * storage. Character lookup is O(log G) for format 12/13 groups and O(S) for
 * format 4 segments. Simple-glyph decoding is two-pass O(C + P + B) for C
 * contours, P points, and B encoded flag/coordinate bytes: the first call
 * reports exact caller-buffer requirements and the second writes directly to
 * those spans. Composite expansion is O(G + K + P + S) normally and
 * O(D * (G + K + P + S)) worst-case when nested point attachments require
 * bounded child preflight, for visited glyphs G, components K, points P,
 * segments S, and D <= 33. Scratch/output spans are caller-owned; no operation
 * allocates or initializes WebGPU.
 */
class sfnt_font_view final {
public:
    static bool try_create(
        std::span<const std::byte> data,
        std::uint32_t face_index,
        sfnt_font_view& result,
        font_error* error = nullptr) noexcept;

    static bool try_get_face_count(
        std::span<const std::byte> data,
        std::uint32_t& face_count,
        font_error* error = nullptr) noexcept;

    bool try_get_table(
        open_type_tag tag,
        sfnt_table_view& result) const noexcept;
    bool try_get_header_metrics(
        sfnt_header_metrics& result) const noexcept;
    bool try_get_horizontal_header_metrics(
        sfnt_horizontal_header_metrics& result) const noexcept;
    bool try_get_horizontal_glyph_metrics(
        std::uint16_t glyph_index,
        sfnt_horizontal_glyph_metrics& result) const noexcept;
    bool try_get_glyph_count(std::uint16_t& result) const noexcept;
    bool try_get_glyph_data(
        std::uint16_t glyph_index,
        sfnt_glyph_data_view& result) const noexcept;
    bool try_get_glyph_decode_requirements(
        std::uint16_t glyph_index,
        sfnt_glyph_decode_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_simple_glyph(
        std::uint16_t glyph_index,
        std::span<std::uint16_t> contour_end_points,
        std::span<sfnt_outline_point> points,
        font_error* error = nullptr) const noexcept;
    bool try_get_composite_glyph_decode_requirements(
        std::uint16_t glyph_index,
        sfnt_composite_glyph_decode_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_composite_glyph(
        std::uint16_t glyph_index,
        std::span<sfnt_composite_component> components,
        font_error* error = nullptr) const noexcept;
    bool try_get_expanded_glyph_requirements(
        std::uint16_t glyph_index,
        sfnt_expanded_glyph_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_glyph_outline(
        std::uint16_t glyph_index,
        std::span<std::uint16_t> simple_contour_scratch,
        std::span<sfnt_outline_point> simple_point_scratch,
        std::span<progpu_native_point> points,
        std::span<progpu_native_path_segment> segments,
        std::uint32_t& points_written,
        std::uint32_t& segments_written,
        font_error* error = nullptr) const noexcept;
    bool try_get_varied_glyph_requirements(
        std::uint16_t glyph_index,
        sfnt_varied_glyph_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_varied_glyph_outline(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        sfnt_varied_glyph_scratch scratch,
        std::span<progpu_native_point> points,
        std::span<progpu_native_path_segment> segments,
        std::uint32_t& points_written,
        std::uint32_t& segments_written,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_index(
        std::uint32_t code_point,
        std::uint16_t& result) const noexcept;
    bool try_get_variation_axis_count(
        std::uint16_t& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_variation_axes(
        std::span<sfnt_variation_axis> axes,
        std::uint16_t& written,
        font_error* error = nullptr) const noexcept;
    /*
     * Normalize one signed 16.16 user coordinate to F2Dot14 and apply its
     * optional avar segment map. Work is O(A + M) over A axis maps and M map
     * pairs with O(1) storage; no variation instance is retained.
     */
    bool try_normalize_variation_coordinate(
        std::uint16_t axis_index,
        std::int32_t user_fixed,
        std::int16_t& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_gvar_header(
        sfnt_gvar_header& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_variation_data(
        std::uint16_t glyph_index,
        sfnt_glyph_variation_data_view& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_gvar_shared_tuple(
        std::uint16_t tuple_index,
        std::span<std::int16_t> coordinates,
        std::uint16_t& written,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_variation_tuple_requirements(
        std::uint16_t glyph_index,
        sfnt_gvar_tuple_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_glyph_variation_tuple_headers(
        std::uint16_t glyph_index,
        std::span<sfnt_gvar_tuple_header> headers,
        std::span<std::int16_t> region_coordinates,
        std::uint16_t& headers_written,
        std::uint32_t& coordinates_written,
        font_error* error = nullptr) const noexcept;
    bool try_get_simple_glyph_variation_requirements(
        std::uint16_t glyph_index,
        std::uint32_t point_count,
        sfnt_simple_glyph_variation_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_apply_simple_glyph_variations(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        std::span<const std::uint16_t> contour_end_points,
        std::span<const sfnt_outline_point> original_points,
        std::span<progpu_native_point> varied_points,
        sfnt_simple_glyph_variation_scratch scratch,
        font_error* error = nullptr) const noexcept;
    bool try_get_composite_glyph_variation_requirements(
        std::uint16_t glyph_index,
        std::uint32_t component_count,
        sfnt_composite_glyph_variation_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_composite_glyph_variation_offsets(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        std::uint32_t component_count,
        std::span<progpu_native_point> offsets,
        sfnt_composite_glyph_variation_scratch scratch,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_phantom_variation_requirements(
        std::uint16_t glyph_index,
        std::uint32_t item_count,
        sfnt_glyph_phantom_variation_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_phantom_advance_delta(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        std::uint32_t item_count,
        float& result,
        sfnt_glyph_phantom_variation_scratch scratch,
        font_error* error = nullptr) const noexcept;

    std::span<const std::byte> data() const noexcept;
    std::uint32_t face_index() const noexcept;
    std::uint32_t face_offset() const noexcept;
    std::uint16_t table_count() const noexcept;
    bool uses_symbol_character_map() const noexcept;

private:
    std::span<const std::byte> data_{};
    std::span<const std::byte> cmap_format4_{};
    std::span<const std::byte> cmap_format12_{};
    std::span<const std::byte> cmap_format13_{};
    std::uint32_t face_index_ = 0U;
    std::uint32_t face_offset_ = 0U;
    std::size_t directory_offset_ = 0U;
    std::uint16_t table_count_ = 0U;
    bool uses_symbol_character_map_ = false;
};

} // namespace progpu::native::text

#endif
