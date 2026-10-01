#include "progpu_native_hinted_outline.hpp"

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <limits>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

// Original ProGPU TrueType contour walker/implicit-point policy in
// progpu_native_true_type_path.cpp, and cubic record policy in
// progpu_native_cff_outline.cpp. No foreign contour implementation is used.
namespace progpu::native::text {
namespace {
constexpr std::uint8_t conic = 0U, on_curve = 1U, cubic = 2U;
constexpr float physical_scale = 1.0F / 64.0F;
// Retained native ownership and reverse-winding metadata do not change the
// shared nonzero coverage predicate. Strict rejects even-odd and scan/raster
// policies rather than implicitly choosing a vector contract for those flags.
constexpr unsigned supported_outline_flags = 0x01U | 0x04U;
// Curve-kind bits plus FreeType's retained internal touch/reserved bits 3/4.
// First-contour-point bit 2 enables bits 5..7 as a SCANTYPE dropout override
// for the B/W rasterizer. Strict rejects that override and unknown tag-policy
// bits rather than claiming grayscale/B/W parity.
// Internal metadata remains exact in the immutable original batch.
constexpr std::uint8_t supported_tag_bits = 0x03U | 0x18U;
// Public FT outline metadata: these switches do not select a new contour or
// winding rule. Explicit vector coverage keeps the original metadata but uses
// only our existing nonzero geometry/coverage contract, never B/W scan behavior.
constexpr unsigned vector_outline_flags = 0x08U | 0x100U;
constexpr std::uint8_t contour_scan_marker = 0x04U;
// SMART_DROPOUTS/INCLUDE_STUBS and contour SCANTYPE belong to B/W scan
// conversion. The explicit antialiased contract retains those original bits
// while using the shared nonzero vector coverage, not a FreeType rasterizer.
constexpr unsigned antialiased_dropout_flags = 0x10U | 0x20U;
constexpr std::uint8_t contour_scan_mode_bits = 0xE0U;

struct memory_range final { std::uintptr_t start = 0U, end = 0U; };

template<class T>
bool range(std::span<T> values, memory_range& output) noexcept {
    constexpr auto maximum = std::numeric_limits<std::uintptr_t>::max();
    const auto start = reinterpret_cast<std::uintptr_t>(values.data());
    if (values.size() > maximum / sizeof(T) ||
        (!values.empty() && (values.data() == nullptr || start % alignof(T) != 0U)) ||
        values.size_bytes() > maximum - start) return false;
    output = {start, start + values.size_bytes()};
    return true;
}

bool overlaps(memory_range left, memory_range right) noexcept {
    return left.start != left.end && right.start != right.end &&
        left.start < right.end && right.start < left.end;
}

template<std::size_t Count, class T>
bool aliases(const std::array<memory_range, Count>& outputs,
    const T* data, std::size_t count) noexcept {
    memory_range input{};
    if (!range(std::span<const T>{data, count}, input)) return true;
    for (const auto output : outputs) if (overlaps(output, input)) return true;
    return false;
}

// Original retained-batch range ownership preflight from hinted_transport.
template<std::size_t Count>
bool aliases_run(const std::array<memory_range, Count>& outputs,
    const hinted_shaped_run& run) noexcept {
    if (aliases(outputs, &run, 1U) ||
        aliases(outputs, run.glyphs.data(), run.glyphs.capacity()) ||
        aliases(outputs, run.descriptor_indices.data(), run.descriptor_indices.capacity()) ||
        aliases(outputs, run.normalized_coordinates.data(), run.normalized_coordinates.capacity()) ||
        aliases(outputs, run.shaping_input.data(), run.shaping_input.capacity()))
        return true;
    if (run.batch == nullptr) return false;
    const auto& batch = *run.batch;
    if (aliases(outputs, &batch, 1U) ||
        aliases(outputs, batch.glyphs.data(), batch.glyphs.capacity())) return true;
    if (batch.identity != nullptr &&
        (aliases(outputs, batch.identity.get(), 1U) ||
         aliases(outputs, batch.identity->variation_coordinates_16_16.data(),
             batch.identity->variation_coordinates_16_16.capacity()))) return true;
    if (batch.identity != nullptr && batch.identity->source != nullptr &&
        (aliases(outputs, batch.identity->source.get(), 1U) ||
         aliases(outputs, batch.identity->source->bytes.data(),
             batch.identity->source->bytes.capacity()))) return true;
    for (const auto& glyph : batch.glyphs)
        if (aliases(outputs, glyph.points.data(), glyph.points.capacity()) ||
            aliases(outputs, glyph.tags.data(), glyph.tags.capacity()) ||
            aliases(outputs, glyph.contour_ends.data(), glyph.contour_ends.capacity())) return true;
    return false;
}

hinted_outline_error select_path(hinted_projection_policy policy,
    hinted_projection_path& path) noexcept {
    if (policy == hinted_projection_policy::scalar_reference) {
        path = hinted_projection_path::scalar_reference;
        return hinted_outline_error::none;
    }
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
    if (policy == hinted_projection_policy::automatic ||
        policy == hinted_projection_policy::intrinsic_simd) {
        path = hinted_projection_path::intrinsic_simd;
        return hinted_outline_error::none;
    }
#endif
    // These caller-visible segments precede GPU submission. Forced GPU paths
    // would require a new readback contract, so neither is a CPU fallback.
    return hinted_outline_error::unsupported_policy;
}

std::uint8_t kind(std::uint8_t tag) noexcept { return tag & 3U; }

progpu_native_point physical(hinted_outline_point point) noexcept {
    return {static_cast<float>(point.x_26_6) * physical_scale,
        static_cast<float>(point.y_26_6) * physical_scale};
}

bool exact_point(hinted_outline_point point) noexcept {
    constexpr auto minimum = std::numeric_limits<std::int32_t>::min();
    constexpr auto maximum = std::numeric_limits<std::int32_t>::max();
    if (point.x_26_6 < minimum || point.x_26_6 > maximum ||
        point.y_26_6 < minimum || point.y_26_6 > maximum) return false;
    const auto converted = physical(point);
    return static_cast<double>(converted.x) == static_cast<double>(point.x_26_6) / 64.0 &&
        static_cast<double>(converted.y) == static_cast<double>(point.y_26_6) / 64.0;
}

bool exact_midpoint(hinted_outline_point left, hinted_outline_point right) noexcept {
    const auto a = physical(left), b = physical(right);
    // This is the original TrueType writer's float addition/multiply policy.
    // Both integers have already passed signed32/exact physical admission.
    return static_cast<double>((a.x + b.x) * 0.5F) ==
            (static_cast<double>(left.x_26_6) + right.x_26_6) / 128.0 &&
        static_cast<double>((a.y + b.y) * 0.5F) ==
            (static_cast<double>(left.y_26_6) + right.y_26_6) / 128.0;
}

struct glyph_info final {
    std::size_t segments = 0U;
    float min_x = 0, min_y = 0, max_x = 0, max_y = 0;
    bool renderable = false;
};

hinted_outline_error inspect_glyph(const hinted_glyph& glyph, glyph_info& info,
    hinted_outline_coverage coverage) noexcept {
    const bool antialiased = coverage == hinted_outline_coverage::antialiased_vector;
    const bool vector = coverage == hinted_outline_coverage::nonzero_vector || antialiased;
    const auto outline_flags = supported_outline_flags | (vector ? vector_outline_flags : 0U) |
        (antialiased ? antialiased_dropout_flags : 0U);
    const auto tag_bits = static_cast<std::uint8_t>(supported_tag_bits |
        (vector ? contour_scan_marker : 0U) | (antialiased ? contour_scan_mode_bits : 0U));
    if (glyph.outline_flags < 0 ||
        (static_cast<unsigned>(glyph.outline_flags) & ~outline_flags) != 0U)
        return hinted_outline_error::unsupported_flags;
    if (glyph.points.size() != glyph.tags.size() || glyph.points.size() > 32768U ||
        (glyph.points.empty() != glyph.contour_ends.empty()))
        return hinted_outline_error::invalid_topology;
    for (std::size_t index = 0U; index < glyph.points.size(); ++index) {
        if ((glyph.tags[index] & static_cast<std::uint8_t>(~tag_bits)) != 0U)
            return hinted_outline_error::unsupported_flags;
        if (antialiased && (glyph.tags[index] & contour_scan_mode_bits) != 0U &&
            (glyph.tags[index] & contour_scan_marker) == 0U)
            return hinted_outline_error::unsupported_flags;
        if (kind(glyph.tags[index]) == 3U) return hinted_outline_error::invalid_topology;
        if (!exact_point(glyph.points[index])) return hinted_outline_error::unsupported_coordinates;
        const auto point = physical(glyph.points[index]);
        if (index == 0U) {
            info.min_x = info.max_x = point.x;
            info.min_y = info.max_y = point.y;
        } else {
            info.min_x = std::min(info.min_x, point.x); info.max_x = std::max(info.max_x, point.x);
            info.min_y = std::min(info.min_y, point.y); info.max_y = std::max(info.max_y, point.y);
        }
    }
    std::size_t start = 0U;
    for (const auto raw_end : glyph.contour_ends) {
        if (raw_end < 0 || static_cast<std::size_t>(raw_end) < start ||
            static_cast<std::size_t>(raw_end) >= glyph.points.size())
            return hinted_outline_error::invalid_topology;
        const auto count = static_cast<std::size_t>(raw_end) + 1U - start;
        const auto points = std::span{glyph.points}.subspan(start, count);
        const auto tags = std::span{glyph.tags}.subspan(start, count);
        start += count;
        // Bit 2 is a per-contour-start marker, never another curve/touch bit.
        // NonzeroVector keeps upper SCANTYPE bits outside tag_bits. The explicit
        // antialiased policy admits the whole documented 3-bit field, including
        // mode aliases 3/6/7, but only with a contour-start marker.
        if (vector && std::any_of(tags.begin() + 1U, tags.end(), [](std::uint8_t tag) {
            return (tag & contour_scan_marker) != 0U;
        })) return hinted_outline_error::unsupported_flags;
        if (kind(tags.front()) == cubic ||
            (kind(tags.front()) == conic && kind(tags.back()) == cubic))
            return hinted_outline_error::invalid_topology;
        if (count < 2U) continue; // Original singleton contour has no segments.
        if (kind(tags.front()) == conic && kind(tags.back()) == conic &&
            !exact_midpoint(points.front(), points.back()))
            return hinted_outline_error::unsupported_coordinates;
        std::size_t index = kind(tags.front()) == on_curve ? 1U : 0U;
        std::size_t processed = 0U;
        while (processed < count) {
            const auto current = index % count;
            const auto current_kind = kind(tags[current]);
            if (current_kind == on_curve) { ++index; ++processed; }
            else if (current_kind == conic) {
                const auto next = (index + 1U) % count;
                if (kind(tags[next]) == cubic) return hinted_outline_error::invalid_topology;
                if (kind(tags[next]) == on_curve) { index += 2U; processed += 2U; }
                else {
                    if (!exact_midpoint(points[current], points[next]))
                        return hinted_outline_error::unsupported_coordinates;
                    ++index; ++processed;
                }
            } else {
                if (kind(tags[(index + 1U) % count]) != cubic ||
                    kind(tags[(index + 2U) % count]) != on_curve)
                    return hinted_outline_error::invalid_topology;
                index += 3U; processed += 3U;
            }
            ++info.segments;
        }
    }
    if (start != glyph.points.size()) return hinted_outline_error::invalid_topology;
    info.renderable = info.segments != 0U && info.max_x > info.min_x && info.max_y > info.min_y;
    return hinted_outline_error::none;
}

hinted_outline_error inspect_run(const hinted_shaped_run& run,
    hinted_outline_requirements& requirements, hinted_outline_coverage coverage) noexcept {
    if (run.batch == nullptr || run.batch->identity == nullptr ||
        run.batch->identity->source == nullptr ||
        run.batch->identity->x_phase_26_6 >= 64U || run.batch->identity->y_phase_26_6 >= 64U ||
        run.source_descriptor_count > run.batch->glyphs.size() ||
        run.source_descriptor_count > UINT32_MAX ||
        run.descriptor_indices.size() != run.glyphs.size()) return hinted_outline_error::invalid_run;
    for (std::size_t index = 0U; index < run.glyphs.size(); ++index) {
        const auto descriptor = run.descriptor_indices[index];
        if (descriptor >= run.source_descriptor_count ||
            run.batch->glyphs[descriptor].glyph_index != run.glyphs[index].glyph_id)
            return hinted_outline_error::invalid_run;
    }
    requirements.source_slots = run.source_descriptor_count;
    requirements.positioned_slots = run.glyphs.size();
    for (std::size_t index = 0U; index < run.source_descriptor_count; ++index) {
        const auto& glyph = run.batch->glyphs[index];
        glyph_info info{};
        const auto error = inspect_glyph(glyph, info, coverage);
        if (error != hinted_outline_error::none) return error;
        requirements.scratch_points = std::max(requirements.scratch_points, glyph.points.size());
        if (info.renderable) {
            if (info.segments > UINT32_MAX - requirements.segments)
                return hinted_outline_error::insufficient_capacity;
            ++requirements.outlines;
            requirements.segments += info.segments;
        }
    }
    return hinted_outline_error::none;
}

void convert_points(std::span<const hinted_outline_point> input,
    std::span<progpu_native_point> output, hinted_projection_path path) noexcept {
    static_assert(sizeof(progpu_native_point) == 2U * sizeof(float));
    static_assert(sizeof(hinted_outline_point) == 2U * sizeof(long));
    std::size_t index = 0U;
    if (path == hinted_projection_path::intrinsic_simd) {
        for (; input.size() - index >= 2U; index += 2U) {
#if defined(__aarch64__) || defined(_M_ARM64)
            int32x4_t values{};
            if constexpr (sizeof(long) == sizeof(std::int64_t)) {
                int64x2_t first{}, second{};
                std::memcpy(&first, input.data() + index, sizeof(first));
                std::memcpy(&second, input.data() + index + 1U, sizeof(second));
                values = vcombine_s32(vmovn_s64(first), vmovn_s64(second));
            } else std::memcpy(&values, input.data() + index, sizeof(values));
            const auto converted = vmulq_n_f32(vcvtq_f32_s32(values), physical_scale);
            std::memcpy(output.data() + index, &converted, sizeof(converted));
#elif defined(__SSE2__) || defined(_M_X64)
            __m128i values{};
            if constexpr (sizeof(long) == sizeof(std::int64_t)) {
                const auto first = _mm_loadu_si128(reinterpret_cast<const __m128i*>(input.data() + index));
                const auto second = _mm_loadu_si128(reinterpret_cast<const __m128i*>(input.data() + index + 1U));
                values = _mm_unpacklo_epi64(_mm_shuffle_epi32(first, _MM_SHUFFLE(2, 0, 2, 0)),
                    _mm_shuffle_epi32(second, _MM_SHUFFLE(2, 0, 2, 0)));
            } else values = _mm_loadu_si128(reinterpret_cast<const __m128i*>(input.data() + index));
            const auto converted = _mm_mul_ps(_mm_cvtepi32_ps(values), _mm_set1_ps(physical_scale));
            std::memcpy(output.data() + index, &converted, sizeof(converted));
#endif
        }
    }
    for (; index < input.size(); ++index) output[index] = physical(input[index]);
}

progpu_native_point midpoint(progpu_native_point left, progpu_native_point right) noexcept {
    return {(left.x + right.x) * 0.5F, (left.y + right.y) * 0.5F};
}

// Extension of the same cyclic TrueType walker for two explicit cubic controls.
// Every cubic uses the original CFF writer's p0/p1/p2/p3/kind/reserved contract.
std::size_t write_cubic_contour(std::span<const std::uint8_t> tags,
    std::span<const progpu_native_point> points,
    std::span<progpu_native_path_segment> segments) noexcept {
    const auto count = points.size();
    std::size_t index = 0U, processed = 0U, written = 0U;
    progpu_native_point current{};
    if (kind(tags.front()) == on_curve) { current = points.front(); index = 1U; }
    else if (kind(tags.back()) == on_curve) current = points.back();
    else current = midpoint(points.front(), points.back());
    while (processed < count) {
        const auto selected = index % count;
        const auto selected_kind = kind(tags[selected]);
        if (selected_kind == on_curve) {
            const auto end = points[selected];
            segments[written++] = {current, end, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U};
            current = end; ++index; ++processed;
        } else if (selected_kind == conic) {
            const auto next = (index + 1U) % count;
            const auto control = points[selected];
            progpu_native_point end{};
            if (kind(tags[next]) == on_curve) { end = points[next]; index += 2U; processed += 2U; }
            else { end = midpoint(control, points[next]); ++index; ++processed; }
            segments[written++] = {current, control, end, {}, PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC, 0U, 0U, 0U};
            current = end;
        } else {
            const auto first = points[selected], second = points[(index + 1U) % count];
            const auto end = points[(index + 2U) % count];
            segments[written++] = {current, first, second, end, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0U, 0U, 0U};
            current = end; index += 3U; processed += 3U;
        }
    }
    return written;
}

std::size_t write_glyph(const hinted_glyph& glyph, hinted_outline_scratch scratch,
    std::span<progpu_native_path_segment> segments, hinted_projection_path path) noexcept {
    convert_points(glyph.points, scratch.physical_points, path);
    for (std::size_t index = 0U; index < glyph.tags.size(); ++index)
        scratch.topology[index] = {0, 0, static_cast<std::uint8_t>(kind(glyph.tags[index]) == on_curve)};
    std::size_t start = 0U, written = 0U;
    for (const auto raw_end : glyph.contour_ends) {
        const auto count = static_cast<std::size_t>(raw_end) + 1U - start;
        const auto tags = std::span{glyph.tags}.subspan(start, count);
        const auto physical_points = scratch.physical_points.subspan(start, count);
        const auto topology = scratch.topology.subspan(start, count);
        start += count;
        if (count < 2U) continue;
        if (std::any_of(tags.begin(), tags.end(), [](std::uint8_t tag) { return kind(tag) == cubic; })) {
            written += write_cubic_contour(tags, physical_points, segments.subspan(written));
        } else {
            const std::uint16_t end = static_cast<std::uint16_t>(count - 1U);
            std::uint32_t contour_written = 0U;
            // Original code performs its same bounded count/write walk; every
            // topology and capacity prerequisite was established before output.
            (void)sfnt_simple_glyph_path::try_write_varied_segments(std::span{&end, 1U},
                topology, physical_points, segments.subspan(written), contour_written, nullptr);
            written += contour_written;
        }
    }
    return written;
}
} // namespace

hinted_outline_error get_hinted_outline_requirements(const hinted_shaped_run& run,
    hinted_outline_requirements& requirements, hinted_projection_policy policy,
    hinted_outline_coverage coverage) noexcept {
    hinted_projection_path path{};
    auto error = select_path(policy, path);
    if (error != hinted_outline_error::none) return error;
    if (coverage != hinted_outline_coverage::strict && coverage != hinted_outline_coverage::nonzero_vector &&
        coverage != hinted_outline_coverage::antialiased_vector)
        return hinted_outline_error::unsupported_policy;
    std::array<memory_range, 1> output{};
    if (!range(std::span{&requirements, 1U}, output[0]) || aliases_run(output, run))
        return hinted_outline_error::invalid_argument;
    hinted_outline_requirements candidate{};
    error = inspect_run(run, candidate, coverage);
    if (error == hinted_outline_error::none) requirements = candidate;
    return error;
}

hinted_outline_error write_hinted_run_outlines(const hinted_shaped_run& run,
    hinted_outline_scratch scratch, std::span<progpu_native_glyph_outline> outlines,
    std::span<progpu_native_path_segment> segments,
    std::span<std::uint32_t> source_outline_indices,
    std::span<std::uint32_t> positioned_outline_indices,
    hinted_outline_requirements& written, hinted_projection_policy policy,
    hinted_outline_coverage coverage) noexcept {
    hinted_projection_path path{};
    auto error = select_path(policy, path);
    if (error != hinted_outline_error::none) return error;
    if (coverage != hinted_outline_coverage::strict && coverage != hinted_outline_coverage::nonzero_vector &&
        coverage != hinted_outline_coverage::antialiased_vector)
        return hinted_outline_error::unsupported_policy;
    std::array<memory_range, 7> outputs{};
    if (!range(scratch.topology, outputs[0]) || !range(scratch.physical_points, outputs[1]) ||
        !range(outlines, outputs[2]) || !range(segments, outputs[3]) ||
        !range(source_outline_indices, outputs[4]) || !range(positioned_outline_indices, outputs[5]) ||
        !range(std::span{&written, 1U}, outputs[6]) || aliases_run(outputs, run))
        return hinted_outline_error::invalid_argument;
    for (std::size_t first = 0U; first < outputs.size(); ++first)
        for (std::size_t second = first + 1U; second < outputs.size(); ++second)
            if (overlaps(outputs[first], outputs[second])) return hinted_outline_error::invalid_argument;
    hinted_outline_requirements required{};
    error = inspect_run(run, required, coverage);
    if (error != hinted_outline_error::none) return error;
    if (scratch.topology.size() < required.scratch_points || scratch.physical_points.size() < required.scratch_points ||
        outlines.size() < required.outlines || segments.size() < required.segments ||
        source_outline_indices.size() < required.source_slots || positioned_outline_indices.size() < required.positioned_slots)
        return hinted_outline_error::insufficient_capacity;
    // The immutable source and completed preflight guarantee this second walk
    // cannot fail. No callbacks, allocations or resource acquisition occur.
    std::size_t outline_index = 0U, segment_offset = 0U;
    for (std::size_t index = 0U; index < required.source_slots; ++index) {
        const auto& glyph = run.batch->glyphs[index];
        glyph_info info{};
        (void)inspect_glyph(glyph, info, coverage);
        if (!info.renderable) { source_outline_indices[index] = hinted_no_outline; continue; }
        (void)write_glyph(glyph, scratch, segments.subspan(segment_offset, info.segments), path);
        outlines[outline_index] = {segment_offset, info.segments,
            info.min_x, info.min_y, info.max_x, info.max_y, 1.0F, 0.0F};
        source_outline_indices[index] = static_cast<std::uint32_t>(outline_index++);
        segment_offset += info.segments;
    }
    for (std::size_t index = 0U; index < required.positioned_slots; ++index)
        positioned_outline_indices[index] = source_outline_indices[run.descriptor_indices[index]];
    written = required;
    return hinted_outline_error::none;
}
} // namespace progpu::native::text
