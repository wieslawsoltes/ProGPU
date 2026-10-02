#include "progpu_native_direct2d_prepared_glyphs.hpp"
#include "progpu_native_text.hpp"

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

struct decoded_original_glyph final {
    std::vector<progpu_native_path_segment> segments;
    float horizontal_origin = 0.0F;
    std::uint16_t horizontal_advance = 0U;
};

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

[[nodiscard]] bool place_point(progpu_native_point& value, float scale, float x, float y,
    float horizontal_origin) noexcept
{
    value = {(value.x - horizontal_origin) * scale + x, -value.y * scale + y};
    return std::isfinite(value.x) && std::isfinite(value.y);
}

// Source coordinate conversion, not raster coverage: two independent XY points
// use intrinsic lanes; a quadratic's third point is the fixed scalar tail.
[[nodiscard]] bool place_segment(progpu_native_path_segment& value, float scale, float x, float y,
    float horizontal_origin) noexcept
{
    if (value.kind > PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC) return false;
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
        (value.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE || place_point(value.p2, scale, x, y, horizontal_origin));
}
} // namespace

struct prepared_original_font::state final {
    std::shared_ptr<const original_font_capture> source;
    text::sfnt_font_view font;
    std::uint16_t units_per_em = 0U;
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
    // DWRITE_FONT_FACE_TYPE_TRUETYPE / OPENTYPE_COLLECTION, no simulations.
    // A multi-file/type1/CFF/variable face needs its actual decoder/axis contract;
    // do not concatenate files or choose default coordinates here.
    if (source->files.size() != 1U || source->simulations != 0U ||
        (source->face_type != 1U && source->face_type != 2U)) return compat::not_implemented;
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
        text::sfnt_table_view variation{};
        if (candidate->font.try_get_table(text::open_type_tag::from_chars('f', 'v', 'a', 'r'), variation))
            return compat::not_implemented;
        text::sfnt_table_view glyf{}, loca{};
        if (!candidate->font.try_get_table(text::open_type_tag::from_chars('g', 'l', 'y', 'f'), glyf) ||
            !candidate->font.try_get_table(text::open_type_tag::from_chars('l', 'o', 'c', 'a'), loca))
            return compat::not_implemented;
        candidate->units_per_em = header.units_per_em;
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
    // profile or the internal historical RGB box model. Sideways/RTL and GDI
    // metrics remain separate original placement contracts. Absent advances
    // use the owned unhinted horizontal metrics, never hinted device widths.
    if (!request->rendering.supplied || request->rendering.rendering_mode != 6U ||
        request->sideways != 0 || (request->bidi_level & 1U) != 0U ||
        request->measuring != compat::measuring_mode::natural)
        return compat::not_implemented;
    try {
        const std::lock_guard lock(state_->mutex);
        auto candidate = std::make_shared<prepared_original_glyph_run>();
        candidate->request_ = std::move(request);
        const auto& original = *candidate->request_;
        const float scale = original.em_size / static_cast<float>(state_->units_per_em);
        if (!std::isfinite(scale) || scale <= 0.0F) return com::invalid_argument;
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
                const auto status = decode_glyph(state_->font, glyph,
                    maximum_segments - state_->cached_segments - added_segments, decoded);
                if (com::failed(status)) return status;
                added_segments += decoded->segments.size();
                additions.emplace(glyph, decoded);
            }
            if (decoded->segments.size() > maximum_segments - output_segments) return com::out_of_memory;
            output_segments += decoded->segments.size();
            occurrences.push_back(std::move(decoded));
        }
        candidate->segments_.reserve(output_segments);
        float pen = 0.0F;
        for (std::uint32_t index = 0U; index < original.glyphs.count(); ++index) {
            const auto offset = original.glyphs.offsets() == nullptr ? compat::glyph_offset{} : original.glyphs.offsets()[index];
            const float x = original.target.baseline.x + pen + offset.advance_offset;
            const float y = original.target.baseline.y - offset.ascender_offset;
            if (!std::isfinite(x) || !std::isfinite(y)) return com::invalid_argument;
            for (auto segment : occurrences[index]->segments) {
                if (!place_segment(segment, scale, x, y, occurrences[index]->horizontal_origin))
                    return com::invalid_argument;
                candidate->segments_.push_back(segment);
            }
            // Keep the captured null pointer as original source identity. An
            // explicit advance, including zero/negative values, always wins.
            const float advance = original.glyphs.advances() != nullptr
                ? original.glyphs.advances()[index]
                : static_cast<float>(occurrences[index]->horizontal_advance) * scale;
            pen += advance;
            if (!std::isfinite(pen)) return com::invalid_argument;
        }
        // Complete run preflight precedes both cache and output publication.
        // reserve may allocate but does not remove earlier cache entries. merge
        // transfers already-owned nodes without per-glyph allocations/redecodes.
        state_->cache.reserve(state_->cache.size() + additions.size());
        state_->cache.merge(additions);
        state_->cached_segments += added_segments;
        output = std::move(candidate);
        return com::ok;
    } catch (const std::bad_alloc&) { return com::out_of_memory; }
    catch (...) { return com::invalid_argument; }
}

} // namespace progpu::native::direct2d
