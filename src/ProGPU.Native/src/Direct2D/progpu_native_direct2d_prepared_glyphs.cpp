#include "progpu_native_direct2d_prepared_glyphs.hpp"
#include "progpu_native_direct2d_cff_source.hpp"
#include "progpu_native_direct2d_vertical_metrics.hpp"
#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <mutex>
#include <unordered_map>

#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#elif defined(__wasm_simd128__)
#include <wasm_simd128.h>
#endif

namespace progpu::native::direct2d {
namespace {
constexpr std::size_t maximum_segments = 1U << 20U;
constexpr std::size_t maximum_points = 1U << 20U;

enum class original_outline_family { true_type, cff1, cff2 };

struct decoded_original_glyph final {
    std::vector<progpu_native_path_segment> segments;
    float horizontal_origin = 0.0F;
    float horizontal_advance = 0.0F;
};

// Each scratch array is bounded by the existing point-domain budget, including
// tuple metadata. Shared decoders borrow these spans only during preparation.
// Simple and composite recursion have distinct tuple buffers; phantom extraction
// may reuse the composite buffers after contour decoding has completed.
struct varied_outline_storage final {
    std::vector<std::uint16_t> contours;
    std::vector<text::sfnt_outline_point> original_points;
    std::vector<progpu_native_point> varied_points, component_offsets, points;
    std::vector<text::sfnt_gvar_tuple_header> simple_headers, composite_headers;
    std::vector<std::int16_t> simple_regions, composite_regions, simple_x, simple_y, composite_x, composite_y;
    std::vector<std::uint32_t> simple_shared, simple_private, composite_shared, composite_private;
    std::vector<float> tuple_x, tuple_y;
    std::vector<std::uint8_t> touched;

    [[nodiscard]] bool resize(const text::sfnt_varied_glyph_requirements& requirements)
    {
        const auto& simple = requirements.simple_variation;
        const auto& composite = requirements.composite_variation;
        const std::array<std::size_t, 13U> counts{requirements.outline.point_count,
            requirements.outline.simple_contour_scratch_count, requirements.outline.simple_point_scratch_count,
            requirements.varied_simple_point_count, requirements.component_offset_count,
            simple.tuple_header_count, simple.region_coordinate_count, simple.point_number_count,
            simple.delta_count, simple.tuple_point_count, composite.tuple_header_count,
            composite.region_coordinate_count, std::max(composite.point_number_count, composite.delta_count)};
        if (std::any_of(counts.begin(), counts.end(), [](auto count) { return count > maximum_points; })) return false;
        contours.resize(requirements.outline.simple_contour_scratch_count);
        original_points.resize(requirements.outline.simple_point_scratch_count);
        varied_points.resize(requirements.varied_simple_point_count);
        component_offsets.resize(requirements.component_offset_count);
        points.resize(requirements.outline.point_count);
        simple_headers.resize(simple.tuple_header_count); simple_regions.resize(simple.region_coordinate_count);
        simple_shared.resize(simple.point_number_count); simple_private.resize(simple.point_number_count);
        simple_x.resize(simple.delta_count); simple_y.resize(simple.delta_count);
        tuple_x.resize(simple.tuple_point_count); tuple_y.resize(simple.tuple_point_count); touched.resize(simple.tuple_point_count);
        composite_headers.resize(composite.tuple_header_count); composite_regions.resize(composite.region_coordinate_count);
        composite_shared.resize(composite.point_number_count); composite_private.resize(composite.point_number_count);
        composite_x.resize(composite.delta_count); composite_y.resize(composite.delta_count);
        return true;
    }

    [[nodiscard]] text::sfnt_varied_glyph_scratch borrow() noexcept
    {
        return {contours, original_points, varied_points, component_offsets,
            {simple_headers, simple_regions, simple_shared, simple_private, simple_x, simple_y, tuple_x, tuple_y, touched},
            {composite_headers, composite_regions, composite_shared, composite_private, composite_x, composite_y}};
    }

    [[nodiscard]] bool resize_phantoms(const text::sfnt_glyph_phantom_variation_requirements& requirements)
    {
        if (requirements.region_coordinate_count > maximum_points || requirements.point_number_count > maximum_points ||
            requirements.delta_count > maximum_points) return false;
        composite_headers.resize(requirements.tuple_header_count); composite_regions.resize(requirements.region_coordinate_count);
        composite_shared.resize(requirements.point_number_count); composite_private.resize(requirements.point_number_count);
        composite_x.resize(requirements.delta_count); composite_y.resize(requirements.delta_count);
        return true;
    }

    [[nodiscard]] text::sfnt_glyph_phantom_variation_scratch borrow_phantoms() noexcept
    {
        return {composite_headers, composite_regions, composite_shared, composite_private, composite_x, composite_y};
    }
};

[[nodiscard]] std::uint32_t source_axis_tag(std::uint32_t value) noexcept
{
    // DWRITE_MAKE_FONT_AXIS_TAG is byte-little-endian; OpenType tags are not.
    return (value >> 24U) | ((value >> 8U) & 0x0000FF00U) |
        ((value << 8U) & 0x00FF0000U) | (value << 24U);
}

[[nodiscard]] bool standard_axis(std::uint32_t value) noexcept
{
    return value == text::open_type_tag::from_chars('w', 'g', 'h', 't').value ||
        value == text::open_type_tag::from_chars('w', 'd', 't', 'h').value ||
        value == text::open_type_tag::from_chars('i', 't', 'a', 'l').value ||
        value == text::open_type_tag::from_chars('s', 'l', 'n', 't').value ||
        value == text::open_type_tag::from_chars('o', 'p', 's', 'z').value;
}

[[nodiscard]] com::result prepare_variation(const text::sfnt_font_view& font,
    const original_font_capture& source, std::vector<std::int16_t>& normalized,
    std::vector<float>& region_scalars, text::sfnt_horizontal_metrics_variation_instance& metrics)
{
    text::sfnt_table_view fvar{};
    if (!font.try_get_table(text::open_type_tag::from_chars('f', 'v', 'a', 'r'), fvar))
        return source.has_variations ? com::invalid_argument : com::ok;
    if (!source.axis_values_available) return compat::not_implemented;
    if (!source.has_variations || source.axis_values.size() > original_font_capture::maximum_axes ||
        fvar.bytes.size() < 16U || fvar.bytes[0] != std::byte{0} || fvar.bytes[1] != std::byte{1} ||
        fvar.bytes[2] != std::byte{0} || fvar.bytes[3] != std::byte{0}) return com::invalid_argument;
    std::uint16_t count = 0U;
    if (!font.try_get_variation_axis_count(count) || count == 0U) return com::invalid_argument;
    std::unordered_map<std::uint32_t, float> captured;
    captured.reserve(source.axis_values.size());
    for (const auto& axis : source.axis_values) {
        if (!std::isfinite(axis.value) || !captured.emplace(source_axis_tag(axis.tag), axis.value).second)
            return com::invalid_argument;
    }
    normalized.resize(count);
    // Map the genuine captured user coordinates once, in the font's own fvar
    // order. No default coordinate is supplied for a missing source axis.
    for (std::uint16_t index = 0U; index < count; ++index) {
        text::sfnt_variation_axis axis{};
        if (!font.try_get_variation_axis(index, axis)) return com::invalid_argument;
        const auto found = captured.find(axis.tag.value);
        if (found == captured.end() ||
            !font.try_normalize_variation_design_coordinate(index, found->second, normalized[index]))
            return com::invalid_argument;
        captured.erase(found); // also rejects a repeated fvar tag
    }
    for (const auto& [tag, value] : captured) {
        (void)value;
        if (!standard_axis(tag)) return compat::not_implemented;
    }
    text::sfnt_table_view gvar_table{};
    if (font.try_get_table(text::open_type_tag::from_chars('g', 'v', 'a', 'r'), gvar_table)) {
        if (gvar_table.bytes.size() < 20U || gvar_table.bytes[0] != std::byte{0} ||
            gvar_table.bytes[1] != std::byte{1} || gvar_table.bytes[2] != std::byte{0} ||
            gvar_table.bytes[3] != std::byte{0} || gvar_table.bytes[14] != std::byte{0} ||
            (std::to_integer<unsigned>(gvar_table.bytes[15]) & ~1U) != 0U) return com::invalid_argument;
        text::sfnt_gvar_header gvar{};
        if (!font.try_get_gvar_header(gvar) || gvar.axis_count != count || gvar.glyph_count != source.glyph_count)
            return com::invalid_argument;
    }
    std::uint16_t region_count = 0U;
    bool uses_hvar = false;
    if (!font.try_get_horizontal_advance_variation_region_count(normalized, region_count, uses_hvar))
        return com::invalid_argument;
    region_scalars.resize(region_count);
    if (!font.try_prepare_horizontal_metrics_variation(normalized, region_scalars, metrics))
        return com::invalid_argument;
    return com::ok;
}

[[nodiscard]] com::result decode_varied_glyph(const text::sfnt_font_view& font,
    std::span<const std::int16_t> normalized, const text::sfnt_horizontal_metrics_variation_instance& variation,
    varied_outline_storage& scratch, std::uint16_t glyph, std::size_t remaining,
    std::shared_ptr<const decoded_original_glyph>& output)
{
    text::sfnt_varied_glyph_requirements requirements{};
    if (!font.try_get_varied_glyph_requirements(glyph, requirements)) return com::invalid_argument;
    if (requirements.outline.path_segment_count > remaining || !scratch.resize(requirements)) return com::out_of_memory;
    auto candidate = std::make_shared<decoded_original_glyph>();
    candidate->segments.resize(requirements.outline.path_segment_count);
    std::uint32_t points_written = 0U, segments_written = 0U;
    if (!font.try_decode_varied_glyph_outline(glyph, normalized, scratch.borrow(), scratch.points,
            candidate->segments, points_written, segments_written) ||
        points_written != requirements.outline.point_count || segments_written != requirements.outline.path_segment_count)
        return com::invalid_argument;
    text::sfnt_horizontal_glyph_metrics base{};
    text::sfnt_glyph_data_view original{};
    if (!font.try_get_horizontal_glyph_metrics(glyph, base) || !font.try_get_glyph_data(glyph, original))
        return com::invalid_argument;
    std::uint32_t item_count = 0U;
    text::sfnt_glyph_phantom_variation_requirements phantom_requirements{};
    if (!font.try_get_glyph_variation_item_count(glyph, item_count) ||
        !font.try_get_glyph_phantom_variation_requirements(glyph, item_count, phantom_requirements))
        return com::invalid_argument;
    if (!scratch.resize_phantoms(phantom_requirements)) return com::out_of_memory;
    float left = 0.0F, right = 0.0F;
    if (!font.try_get_glyph_horizontal_phantom_deltas(glyph, normalized, item_count, left, right,
            scratch.borrow_phantoms())) return com::invalid_argument;
    candidate->horizontal_advance = static_cast<float>(base.advance_width) + (right - left);
    // HVAR advance has the existing shared precedence. Its region scalars and
    // optional maps belong to this same immutable axis instance, not a glyph.
    if (variation.advance.uses_hvar && !font.try_get_design_advance_width(glyph, normalized,
            &variation.advance, candidate->horizontal_advance, scratch.borrow_phantoms())) return com::invalid_argument;
    if (!original.empty()) {
        candidate->horizontal_origin = static_cast<float>(static_cast<std::int32_t>(original.x_min) -
            static_cast<std::int32_t>(base.left_side_bearing)) + left;
        float lsb_delta = 0.0F;
        bool has_lsb = false;
        if (!font.try_get_horizontal_left_side_bearing_variation(glyph, variation, lsb_delta, has_lsb))
            return com::invalid_argument;
        if (has_lsb) {
            if (points_written == 0U) return com::invalid_argument;
            float minimum_x = std::numeric_limits<float>::infinity();
            for (const auto& point : scratch.points) {
                if (!std::isfinite(point.x) || !std::isfinite(point.y)) return com::invalid_argument;
                minimum_x = std::min(minimum_x, point.x);
            }
            candidate->horizontal_origin = minimum_x - (static_cast<float>(base.left_side_bearing) + lsb_delta);
        }
    }
    if (!std::isfinite(candidate->horizontal_advance) || !std::isfinite(candidate->horizontal_origin))
        return com::invalid_argument;
    output = std::move(candidate);
    return com::ok;
}

[[nodiscard]] com::result decode_glyph(const text::sfnt_font_view& font, std::uint16_t glyph,
    std::size_t remaining, std::shared_ptr<const decoded_original_glyph>& output)
{
    text::sfnt_expanded_glyph_requirements requirements{};
    if (!font.try_get_expanded_glyph_requirements(glyph, requirements)) return com::invalid_argument;
    if (requirements.path_segment_count > remaining || requirements.point_count > maximum_points ||
        requirements.simple_point_scratch_count > maximum_points) return com::out_of_memory;
    auto candidate = std::make_shared<decoded_original_glyph>();
    text::sfnt_horizontal_glyph_metrics metrics{};
    text::sfnt_glyph_data_view original{};
    if (!font.try_get_horizontal_glyph_metrics(glyph, metrics) || !font.try_get_glyph_data(glyph, original))
        return com::invalid_argument;
    candidate->horizontal_advance = metrics.advance_width;
    // OpenType hmtx defines the unhinted left phantom point as xMin - lsb.
    // Stored contour coordinates are not necessarily relative to that origin.
    // Empty glyphs have no xMin/ink; supplied or nominal advances still participate.
    if (!original.empty()) candidate->horizontal_origin = static_cast<float>(
        static_cast<std::int32_t>(original.x_min) - static_cast<std::int32_t>(metrics.left_side_bearing));
    candidate->segments.resize(requirements.path_segment_count);
    std::vector<std::uint16_t> contours(requirements.simple_contour_scratch_count);
    std::vector<text::sfnt_outline_point> scratch(requirements.simple_point_scratch_count);
    std::vector<progpu_native_point> points(requirements.point_count);
    std::uint32_t points_written = 0U, segments_written = 0U;
    if (!font.try_decode_glyph_outline(glyph, contours, scratch, points, candidate->segments,
        points_written, segments_written) || points_written != requirements.point_count ||
        segments_written != requirements.path_segment_count) return com::invalid_argument;
    output = std::move(candidate);
    return com::ok;
}

[[nodiscard]] com::result decode_cff_glyph(const text::sfnt_font_view& font,
    original_outline_family family, text::sfnt_cff1_font_view cff1, text::sfnt_cff2_font_view cff2,
    std::span<const text::sfnt_cff_outline_transform> matrices,
    std::span<const std::int16_t> normalized,
    const text::sfnt_horizontal_metrics_variation_instance& variation,
    std::uint16_t glyph, std::size_t remaining, std::shared_ptr<const decoded_original_glyph>& output)
{
    std::uint32_t count = 0U, dictionary = 0U;
    if (family == original_outline_family::cff1) {
        if (!cff1.fd_select.bytes.empty() &&
            !text::sfnt_cff_data::try_get_font_dictionary(cff1.fd_select, glyph, dictionary))
            return com::invalid_argument;
        if (dictionary >= matrices.size()) return com::invalid_argument;
        text::sfnt_cff1_outline_requirements requirements{};
        if (!text::sfnt_cff_data::try_get_outline_requirements(cff1, glyph, matrices[dictionary], requirements))
            return com::invalid_argument;
        count = requirements.path_segment_count;
    } else {
        text::sfnt_cff2_outline_requirements requirements{};
        if (!text::sfnt_cff_data::try_get_outline_requirements(cff2, glyph, normalized, requirements))
            return com::invalid_argument;
        count = requirements.path_segment_count;
    }
    if (count > remaining) return com::out_of_memory;
    auto candidate = std::make_shared<decoded_original_glyph>();
    candidate->segments.resize(count);
    std::uint32_t written = 0U;
    const bool decoded = family == original_outline_family::cff1
        ? text::sfnt_cff_data::try_decode_outline(cff1, glyph, matrices[dictionary], candidate->segments, written)
        : text::sfnt_cff_data::try_decode_outline(cff2, glyph, normalized, candidate->segments, written);
    if (!decoded || written != count) return com::invalid_argument;
    text::sfnt_horizontal_glyph_metrics base{};
    if (!font.try_get_horizontal_glyph_metrics(glyph, base)) return com::invalid_argument;
    candidate->horizontal_advance = base.advance_width;
    // CFF has a genuine CharString origin, not a glyf phantom point. Its hmtx
    // bearing is descriptive and must not translate the contour. CFF2 advances
    // vary only through HVAR; absent HVAR means unchanged hmtx, never gvar.
    if (family == original_outline_family::cff2 && variation.advance.uses_hvar &&
        !font.try_get_design_advance_width(glyph, normalized, &variation.advance,
            candidate->horizontal_advance)) return com::invalid_argument;
    if (!std::isfinite(candidate->horizontal_advance)) return com::invalid_argument;
    output = std::move(candidate);
    return com::ok;
}

[[nodiscard]] bool place_point(progpu_native_point& value, float scale, float x, float y,
    float horizontal_origin) noexcept
{
    value = {(value.x - horizontal_origin) * scale + x, -value.y * scale + y};
    return std::isfinite(value.x) && std::isfinite(value.y);
}

// Source coordinate conversion, not raster coverage: two independent XY points
// use intrinsic lanes; the remaining quadratic/cubic controls are fixed tails.
[[nodiscard]] bool place_segment(progpu_native_path_segment& value, float scale, float x, float y,
    float horizontal_origin) noexcept
{
    if (value.kind > PROGPU_NATIVE_PATH_SEGMENT_CUBIC) return false;
    alignas(16) float points[4]{value.p0.x, value.p0.y, value.p1.x, value.p1.y};
    alignas(16) const float scales[4]{scale, -scale, scale, -scale};
    alignas(16) const float origins[4]{x, y, x, y};
    alignas(16) const float design_origins[4]{horizontal_origin, 0, horizontal_origin, 0};
#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
    vst1q_f32(points, vaddq_f32(vmulq_f32(vsubq_f32(vld1q_f32(points), vld1q_f32(design_origins)),
        vld1q_f32(scales)), vld1q_f32(origins)));
#elif defined(__SSE2__) || defined(_M_X64)
    _mm_store_ps(points, _mm_add_ps(_mm_mul_ps(_mm_sub_ps(_mm_load_ps(points), _mm_load_ps(design_origins)),
        _mm_load_ps(scales)), _mm_load_ps(origins)));
#elif defined(__wasm_simd128__)
    wasm_v128_store(points, wasm_f32x4_add(wasm_f32x4_mul(wasm_f32x4_sub(wasm_v128_load(points),
        wasm_v128_load(design_origins)), wasm_v128_load(scales)), wasm_v128_load(origins)));
#else
    for (std::size_t lane = 0U; lane < 4U; ++lane)
        points[lane] = (points[lane] - design_origins[lane]) * scales[lane] + origins[lane];
#endif
    value.p0 = {points[0], points[1]}; value.p1 = {points[2], points[3]};
    return std::isfinite(points[0]) && std::isfinite(points[1]) &&
        std::isfinite(points[2]) && std::isfinite(points[3]) &&
        (value.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE || place_point(value.p2, scale, x, y, horizontal_origin)) &&
        (value.kind != PROGPU_NATIVE_PATH_SEGMENT_CUBIC || place_point(value.p3, scale, x, y, horizontal_origin));
}

// A sideways glyph rotates left in the untransformed screen frame about its
// vertical origin. Exchange the two design lanes before the same subtract /
// multiply / add sequence used above. Do not rotate the run's pen or offsets.
[[nodiscard]] bool place_sideways_point(progpu_native_point& value, float scale, float x, float y,
    float origin_x, float origin_y) noexcept
{
    value = {-(value.y - origin_y) * scale + x, -(value.x - origin_x) * scale + y};
    return std::isfinite(value.x) && std::isfinite(value.y);
}

[[nodiscard]] bool place_sideways_segment(progpu_native_path_segment& value, float scale, float x, float y,
    float origin_x, float origin_y) noexcept
{
    if (value.kind > PROGPU_NATIVE_PATH_SEGMENT_CUBIC) return false;
    alignas(16) float points[4]{value.p0.y, value.p0.x, value.p1.y, value.p1.x};
    alignas(16) const float scales[4]{-scale, -scale, -scale, -scale};
    alignas(16) const float origins[4]{x, y, x, y};
    alignas(16) const float design_origins[4]{origin_y, origin_x, origin_y, origin_x};
#if defined(__ARM_NEON) || defined(__aarch64__) || defined(_M_ARM64)
    vst1q_f32(points, vaddq_f32(vmulq_f32(vsubq_f32(vld1q_f32(points), vld1q_f32(design_origins)),
        vld1q_f32(scales)), vld1q_f32(origins)));
#elif defined(__SSE2__) || defined(_M_X64)
    _mm_store_ps(points, _mm_add_ps(_mm_mul_ps(_mm_sub_ps(_mm_load_ps(points), _mm_load_ps(design_origins)),
        _mm_load_ps(scales)), _mm_load_ps(origins)));
#elif defined(__wasm_simd128__)
    wasm_v128_store(points, wasm_f32x4_add(wasm_f32x4_mul(wasm_f32x4_sub(wasm_v128_load(points),
        wasm_v128_load(design_origins)), wasm_v128_load(scales)), wasm_v128_load(origins)));
#else
    for (std::size_t lane = 0U; lane < 4U; ++lane)
        points[lane] = (points[lane] - design_origins[lane]) * scales[lane] + origins[lane];
#endif
    value.p0 = {points[0], points[1]}; value.p1 = {points[2], points[3]};
    return std::isfinite(points[0]) && std::isfinite(points[1]) &&
        std::isfinite(points[2]) && std::isfinite(points[3]) &&
        (value.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE ||
            place_sideways_point(value.p2, scale, x, y, origin_x, origin_y)) &&
        (value.kind != PROGPU_NATIVE_PATH_SEGMENT_CUBIC ||
            place_sideways_point(value.p3, scale, x, y, origin_x, origin_y));
}
} // namespace

struct prepared_original_font::state final {
    std::shared_ptr<const original_font_capture> source;
    text::sfnt_font_view font;
    original_outline_family family = original_outline_family::true_type;
    text::sfnt_cff1_font_view cff1{};
    text::sfnt_cff2_font_view cff2{};
    std::vector<text::sfnt_cff_outline_transform> cff_matrices;
    std::uint16_t units_per_em = 0U;
    std::vector<std::int16_t> normalized_coordinates;
    std::vector<float> region_scalars;
    text::sfnt_horizontal_metrics_variation_instance variation{};
    std::shared_ptr<const retained_original_vertical_metrics> vertical_metrics;
    varied_outline_storage varied_scratch;
    std::mutex mutex;
    std::unordered_map<std::uint16_t, std::shared_ptr<const decoded_original_glyph>> cache;
    std::size_t cached_segments = 0U;
};

prepared_original_font::prepared_original_font(std::unique_ptr<state> value) noexcept : state_(std::move(value)) {}
prepared_original_font::~prepared_original_font() = default;

const std::shared_ptr<const original_font_capture>& prepared_original_font::source() const noexcept
{
    return state_->source;
}

std::size_t prepared_original_font::cached_glyph_count() const noexcept
{
    const std::lock_guard lock(state_->mutex);
    return state_->cache.size();
}

com::result prepared_original_font::create(std::shared_ptr<const original_font_capture> source,
    std::shared_ptr<prepared_original_font>& output) noexcept
{
    if (!source || !source->face || source->files.empty()) return com::invalid_argument;
    // DWRITE_FONT_FACE_TYPE_CFF / TRUETYPE / OPENTYPE_COLLECTION, no simulations.
    // A multi-file/type1 face needs its actual decoder contract;
    // do not concatenate files or choose default coordinates here.
    if (source->files.size() != 1U || source->simulations != 0U ||
        source->face_type > 2U) return compat::not_implemented;
    try {
        auto candidate = std::make_unique<state>();
        candidate->source = std::move(source);
        if (!text::sfnt_font_view::try_create(candidate->source->files[0],
            candidate->source->face_index, candidate->font)) return com::invalid_argument;
        std::uint16_t glyphs = 0U;
        text::sfnt_header_metrics header{};
        if (!candidate->font.try_get_glyph_count(glyphs) || glyphs != candidate->source->glyph_count ||
            !candidate->font.try_get_header_metrics(header) || header.units_per_em == 0U)
            return com::invalid_argument;
        // The shared raw metric reader permits an absent bearing as zero for
        // legacy callers. This original-source capability must own every real
        // hmtx bearing, including the compact repeated-advance tail.
        text::sfnt_horizontal_header_metrics horizontal{};
        text::sfnt_table_view hmtx{};
        if (!candidate->font.try_get_horizontal_header_metrics(horizontal) ||
            horizontal.number_of_horizontal_metrics == 0U || horizontal.number_of_horizontal_metrics > glyphs ||
            !candidate->font.try_get_table(text::open_type_tag::from_chars('h', 'm', 't', 'x'), hmtx))
            return com::invalid_argument;
        const auto metric_count = static_cast<std::size_t>(horizontal.number_of_horizontal_metrics);
        if (hmtx.bytes.size() < metric_count * 4U + (static_cast<std::size_t>(glyphs) - metric_count) * 2U)
            return com::invalid_argument;
        text::sfnt_table_view glyf{}, loca{}, cff1{}, cff2{}, gvar{}, fvar{};
        const bool has_glyf = candidate->font.try_get_table(text::open_type_tag::from_chars('g', 'l', 'y', 'f'), glyf);
        const bool has_loca = candidate->font.try_get_table(text::open_type_tag::from_chars('l', 'o', 'c', 'a'), loca);
        const bool has_cff1 = candidate->font.try_get_table(text::open_type_tag::from_chars('C', 'F', 'F', ' '), cff1);
        const bool has_cff2 = candidate->font.try_get_table(text::open_type_tag::from_chars('C', 'F', 'F', '2'), cff2);
        const auto family_count = static_cast<unsigned>(has_glyf || has_loca) +
            static_cast<unsigned>(has_cff1) + static_cast<unsigned>(has_cff2);
        if (family_count == 0U) return compat::not_implemented;
        if (family_count != 1U || has_glyf != has_loca ||
            (candidate->source->face_type == 0U && has_glyf) ||
            (candidate->source->face_type == 1U && !has_glyf)) return com::invalid_argument;
        candidate->units_per_em = header.units_per_em;
        if (has_cff1 || has_cff2) {
            text::sfnt_table_view maxp{}, hvar{}, avar{};
            const bool has_fvar = candidate->font.try_get_table(text::open_type_tag::from_chars('f', 'v', 'a', 'r'), fvar);
            const bool has_hvar = candidate->font.try_get_table(text::open_type_tag::from_chars('H', 'V', 'A', 'R'), hvar);
            const bool has_avar = candidate->font.try_get_table(text::open_type_tag::from_chars('a', 'v', 'a', 'r'), avar);
            if ((!has_fvar && (has_hvar || has_avar)) ||
                (has_hvar && (hvar.bytes.size() < 20U ||
                    (hvar.bytes[4] == std::byte{0} && hvar.bytes[5] == std::byte{0} &&
                     hvar.bytes[6] == std::byte{0} && hvar.bytes[7] == std::byte{0}))))
                return com::invalid_argument;
            if (!detail::validate_cff_source_directory(candidate->source->files[0],
                    candidate->source->face_index, has_cff2) ||
                !candidate->font.try_get_table(text::open_type_tag::from_chars('m', 'a', 'x', 'p'), maxp) ||
                maxp.bytes.size() != 6U || maxp.bytes[0] != std::byte{0} || maxp.bytes[1] != std::byte{0} ||
                maxp.bytes[2] != std::byte{0x50} || maxp.bytes[3] != std::byte{0} ||
                candidate->font.try_get_table(text::open_type_tag::from_chars('g', 'v', 'a', 'r'), gvar))
                return com::invalid_argument;
            if (has_cff1) {
                // CFF1's legacy multiple-master/synthetic operators are not the
                // OpenType CFF2 variation contract. Do not reinterpret fvar.
                if (candidate->source->has_variations || has_fvar)
                    return compat::not_implemented;
                candidate->family = original_outline_family::cff1;
                if (!candidate->font.try_get_cff1_font(glyphs, candidate->cff1)) return com::invalid_argument;
                const auto status = detail::prepare_cff_source_matrices(candidate->cff1,
                    candidate->units_per_em, candidate->cff_matrices);
                if (com::failed(status)) return status;
            } else {
                candidate->family = original_outline_family::cff2;
                if (!candidate->font.try_get_cff2_font(glyphs, candidate->cff2)) return com::invalid_argument;
                // Do not inherit the generic raw parser's approximate matrix
                // check as source admission. The CFF2 transform is reciprocal
                // UPM, not a second arbitrary contour transform.
                if (candidate->cff2.top_dictionary.font_matrix_scale !=
                    1.0 / static_cast<double>(candidate->units_per_em)) return compat::not_implemented;
            }
        }
        const auto variation_status = prepare_variation(candidate->font, *candidate->source,
            candidate->normalized_coordinates, candidate->region_scalars, candidate->variation);
        if (com::failed(variation_status)) return variation_status;
        output = std::shared_ptr<prepared_original_font>(new prepared_original_font(std::move(candidate)));
        return com::ok;
    } catch (const std::bad_alloc&) { return com::out_of_memory; }
    catch (...) { return com::invalid_argument; }
}

com::result prepared_original_font::prepare(std::shared_ptr<const original_glyph_request> request,
    std::shared_ptr<const prepared_original_glyph_run>& output) noexcept
{
    if (!request || request->font != state_->source) return com::invalid_argument;
    // Genuine source OUTLINE selects design contours, not any modern raster
    // profile or the internal historical RGB box model. GDI metrics remain a
    // separate original contract. Absent advances use the selected original
    // unhinted horizontal/vertical metrics, never hinted device widths.
    if (!request->rendering.supplied || request->rendering.rendering_mode != 6U ||
        request->measuring != compat::measuring_mode::natural)
        return compat::not_implemented;
    const bool sideways = request->sideways != 0;
    // The combined sideways/RTL source contract and variable vertical origin
    // rounding are separate work; never borrow the horizontal RTL placement or
    // apply unvaried vertical values to a variable outline.
    if (sideways && ((request->bidi_level & 1U) != 0U || !state_->normalized_coordinates.empty()))
        return compat::not_implemented;
    try {
        const std::lock_guard lock(state_->mutex);
        auto candidate = std::make_shared<prepared_original_glyph_run>();
        candidate->request_ = std::move(request);
        const auto& original = *candidate->request_;
        const float scale = original.em_size / static_cast<float>(state_->units_per_em);
        if (!std::isfinite(scale) || scale <= 0.0F) return com::invalid_argument;
        auto vertical_metrics = state_->vertical_metrics;
        if (sideways) {
            if (!vertical_metrics) {
                const auto status = retained_original_vertical_metrics::create(state_->source, vertical_metrics);
                if (com::failed(status)) return status;
            }
            if (!vertical_metrics->has_metrics()) return compat::not_implemented;
        }
        std::unordered_map<std::uint16_t, std::shared_ptr<const decoded_original_glyph>> additions;
        std::vector<std::shared_ptr<const decoded_original_glyph>> occurrences;
        occurrences.reserve(original.glyphs.count());
        std::size_t added_segments = 0U, output_segments = 0U;
        for (std::uint32_t index = 0U; index < original.glyphs.count(); ++index) {
            const auto glyph = original.glyphs.indices()[index];
            if (glyph >= state_->source->glyph_count) return com::invalid_argument;
            std::shared_ptr<const decoded_original_glyph> decoded;
            if (const auto existing = state_->cache.find(glyph); existing != state_->cache.end()) decoded = existing->second;
            else if (const auto added = additions.find(glyph); added != additions.end()) decoded = added->second;
            else {
                const auto remaining = maximum_segments - state_->cached_segments - added_segments;
                const auto status = state_->family != original_outline_family::true_type
                    ? decode_cff_glyph(state_->font, state_->family, state_->cff1, state_->cff2,
                        state_->cff_matrices, state_->normalized_coordinates, state_->variation, glyph, remaining, decoded)
                    : state_->normalized_coordinates.empty()
                    ? decode_glyph(state_->font, glyph, remaining, decoded)
                    : decode_varied_glyph(state_->font, state_->normalized_coordinates, state_->variation,
                        state_->varied_scratch, glyph, remaining, decoded);
                if (com::failed(status)) return status;
                added_segments += decoded->segments.size();
                additions.emplace(glyph, decoded);
            }
            if (decoded->segments.size() > maximum_segments - output_segments) return com::out_of_memory;
            output_segments += decoded->segments.size();
            occurrences.push_back(std::move(decoded));
        }
        candidate->segments_.reserve(output_segments);
        const bool right_to_left = (original.bidi_level & 1U) != 0U;
        float pen = 0.0F;
        for (std::uint32_t index = 0U; index < original.glyphs.count(); ++index) {
            // Keep logical source order and captured null-pointer identity.
            // Explicit advances, including zero/negative values, always win.
            original_vertical_glyph_metrics vertical{};
            if (sideways) {
                const auto status = vertical_metrics->read_base(original.glyphs.indices()[index], vertical);
                if (com::failed(status)) return status;
                if (!occurrences[index]->segments.empty() && !vertical.has_origin)
                    return compat::not_implemented;
            }
            const float design_advance = sideways ? static_cast<float>(vertical.advance_height)
                : occurrences[index]->horizontal_advance;
            const float advance = original.glyphs.advances() != nullptr
                ? original.glyphs.advances()[index]
                : design_advance * scale;
            const float next_pen = pen + advance;
            if (!std::isfinite(next_pen)) return com::invalid_argument;
            const auto offset = original.glyphs.offsets() == nullptr ? compat::glyph_offset{} : original.glyphs.offsets()[index];
            // An RTL pen starts at the right edge of the glyph's advance box.
            // Move its left-oriented outline origin, never reflect contours or
            // reverse occurrences. Advance offsets follow the run direction;
            // ascender offsets retain their original screen-up direction.
            const float glyph_pen = right_to_left ? -next_pen : pen;
            const float advance_offset = right_to_left ? -offset.advance_offset : offset.advance_offset;
            const float x = original.target.baseline.x + glyph_pen + advance_offset;
            const float y = original.target.baseline.y - offset.ascender_offset;
            if (!std::isfinite(x) || !std::isfinite(y)) return com::invalid_argument;
            const float vertical_origin_x = occurrences[index]->horizontal_origin +
                occurrences[index]->horizontal_advance * 0.5F;
            for (auto segment : occurrences[index]->segments) {
                const bool placed = sideways
                    ? place_sideways_segment(segment, scale, x, y, vertical_origin_x,
                        static_cast<float>(vertical.top_origin))
                    : place_segment(segment, scale, x, y, occurrences[index]->horizontal_origin);
                if (!placed)
                    return com::invalid_argument;
                candidate->segments_.push_back(segment);
            }
            pen = next_pen;
        }
        // Complete run preflight precedes both cache and output publication.
        // reserve may allocate but does not remove earlier cache entries. merge
        // transfers already-owned nodes without per-glyph allocations/redecodes.
        state_->cache.reserve(state_->cache.size() + additions.size());
        state_->cache.merge(additions);
        state_->cached_segments += added_segments;
        if (sideways) state_->vertical_metrics = std::move(vertical_metrics);
        output = std::move(candidate);
        return com::ok;
    } catch (const std::bad_alloc&) { return com::out_of_memory; }
    catch (...) { return com::invalid_argument; }
}

} // namespace progpu::native::direct2d
