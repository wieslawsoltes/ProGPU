#include "progpu_native_hinted_outline.hpp"

#include <algorithm>
#include <array>
#include <cstring>
#include <iostream>
#include <limits>
#include <source_location>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
using namespace progpu::native::text;

void require(bool condition, const std::source_location where = std::source_location::current()) {
    if (!condition) throw std::runtime_error("hinted outline control at line " + std::to_string(where.line()));
}

hinted_glyph glyph(std::uint32_t id, std::initializer_list<hinted_outline_point> points,
    std::initializer_list<std::uint8_t> tags, std::initializer_list<std::int16_t> ends, int flags = 0) {
    hinted_glyph result;
    result.glyph_index = id; result.points = points; result.tags = tags;
    result.contour_ends = ends; result.outline_flags = flags;
    return result;
}

std::shared_ptr<hinted_glyph_batch> batch() {
    auto result = std::make_shared<hinted_glyph_batch>();
    auto identity = std::make_shared<hinted_font_identity>();
    const std::array<std::byte, 64> source{};
    identity->source = std::make_shared<const owned_font_source>(source, 2U);
    identity->x_phase_26_6 = 13U; identity->y_phase_26_6 = 19U;
    identity->variation_coordinates_16_16 = {-65536, 1, 65536};
    result->identity = identity;
    // Coordinates already contain the retained phase. Equal glyph IDs have
    // deliberately distinct actual source-slot geometry, not an ID oracle.
    result->glyphs = {
        glyph(45U, {{13, 19}, {141, 19}, {141, 147}, {13, 147}}, {0x18, 0x08, 0x10, 0x18}, {3}),
        glyph(70U, {}, {}, {}),
        glyph(51U, {{13, 19}, {77, 147}, {141, 147}, {205, 19}}, {0x19, 0x0A, 0x12, 0x19}, {3}),
        glyph(45U, {{269, -109}, {397, -109}, {397, 19}, {269, 19}}, {1, 1, 1, 1}, {3}),
        glyph(63U, {{13, 19}, {269, 19}, {269, 275}, {13, 275},
                    {77, 83}, {77, 211}, {205, 211}, {205, 83}}, {1, 1, 1, 1, 1, 1, 1, 1}, {3, 7}, 5),
        glyph(80U, {{13, 19}, {141, 19}}, {1, 1}, {1}), // real zero-area source descriptor
        glyph(81U, {{13, 19}}, {0}, {0}), // original singleton policy: no segments
        glyph(45U, {{999, 1001}}, {}, {-1}, -1), // auxiliary width probe is NOT drawn or topology-admitted
    };
    return result;
}

hinted_shaped_run run_for(const std::shared_ptr<hinted_glyph_batch>& retained) {
    hinted_shaped_run run;
    run.batch = retained; run.source_descriptor_count = 7U;
    run.figure_descriptor_start = 7U; run.figure_descriptor_count = 1U;
    run.direction = shaping_direction::right_to_left;
    run.descriptor_indices = {3U, 1U, 2U, 0U, 3U, 4U, 5U, 6U};
    for (const auto descriptor : run.descriptor_indices) {
        shaping_glyph positioned;
        positioned.glyph_id = retained->glyphs[descriptor].glyph_index;
        positioned.offset_x = 901; positioned.offset_y = -707; // placement remains a draw role
        run.glyphs.push_back(positioned);
    }
    return run;
}

struct buffers final {
    std::array<sfnt_outline_point, 16> topology{};
    std::array<progpu_native_point, 16> physical{};
    std::array<progpu_native_glyph_outline, 12> outlines{};
    std::array<progpu_native_path_segment, 64> segments{};
    std::array<std::uint32_t, 16> source{}, positioned{};
    hinted_outline_requirements written{71U, 73U, 79U, 83U, 89U};

    buffers() {
        const auto paint = [](auto& values) {
            const auto data = std::as_writable_bytes(std::span{values});
            std::fill(data.begin(), data.end(), std::byte{0x5A});
        };
        paint(topology); paint(physical); paint(outlines); paint(segments);
        source.fill(0x5A5A5A5AU); positioned.fill(0x5A5A5A5AU);
    }
    hinted_outline_error write(const hinted_shaped_run& run,
        hinted_projection_policy policy = hinted_projection_policy::scalar_reference) {
        return write_hinted_run_outlines(run, {topology, physical}, outlines, segments,
            source, positioned, written, policy);
    }
};

template<class T> auto bytes(const T& value) {
    std::array<std::byte, sizeof(T)> result{};
    std::memcpy(result.data(), &value, sizeof(value));
    return result;
}

progpu_native_path_segment line(progpu_native_point a, progpu_native_point b) {
    return {a, b, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U};
}
progpu_native_path_segment quadratic(progpu_native_point a, progpu_native_point b, progpu_native_point c) {
    return {a, b, c, {}, PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC, 0U, 0U, 0U};
}
progpu_native_path_segment cubic(progpu_native_point a, progpu_native_point b,
    progpu_native_point c, progpu_native_point d) {
    return {a, b, c, d, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0U, 0U, 0U};
}

void source_geometry_and_phase() {
    const auto retained = batch();
    const auto run = run_for(retained);
    const auto retained_descriptors = retained->glyphs;
    buffers scalar;
    const auto before = scalar;
    hinted_outline_requirements requirements;
    require(get_hinted_outline_requirements(run, requirements, hinted_projection_policy::scalar_reference) ==
        hinted_outline_error::none);
    require(requirements == hinted_outline_requirements{7U, 8U, 4U, 18U, 8U});
    require(scalar.write(run) == hinted_outline_error::none && scalar.written == requirements);
    const std::array<std::uint32_t, 7> source_expected{0U, hinted_no_outline, 1U, 2U, 3U, hinted_no_outline, hinted_no_outline};
    const std::array<std::uint32_t, 8> positioned_expected{2U, hinted_no_outline, 1U, 0U, 2U, 3U, hinted_no_outline, hinted_no_outline};
    require(std::equal(source_expected.begin(), source_expected.end(), scalar.source.begin()));
    require(std::equal(positioned_expected.begin(), positioned_expected.end(), scalar.positioned.begin()));
    constexpr float x = 13.0F / 64.0F, y = 19.0F / 64.0F;
    const std::array<progpu_native_path_segment, 18> expected{
        // Four independent implied-midpoint quadratics; Y remains Y-up.
        quadratic({x, y + 1}, {x, y}, {x + 1, y}),
        quadratic({x + 1, y}, {x + 2, y}, {x + 2, y + 1}),
        quadratic({x + 2, y + 1}, {x + 2, y + 2}, {x + 1, y + 2}),
        quadratic({x + 1, y + 2}, {x, y + 2}, {x, y + 1}),
        // Exact cubic record followed by original explicit closure.
        cubic({x, y}, {x + 1, y + 2}, {x + 2, y + 2}, {x + 3, y}),
        line({x + 3, y}, {x, y}),
        line({x + 4, y - 2}, {x + 6, y - 2}), line({x + 6, y - 2}, {x + 6, y}),
        line({x + 6, y}, {x + 4, y}), line({x + 4, y}, {x + 4, y - 2}),
        // Outer/inner contours retain order and opposite winding; flags 5
        // retain raw ownership/reverse-fill provenance in the original batch.
        line({x, y}, {x + 4, y}), line({x + 4, y}, {x + 4, y + 4}),
        line({x + 4, y + 4}, {x, y + 4}), line({x, y + 4}, {x, y}),
        line({x + 1, y + 1}, {x + 1, y + 3}), line({x + 1, y + 3}, {x + 3, y + 3}),
        line({x + 3, y + 3}, {x + 3, y + 1}), line({x + 3, y + 1}, {x + 1, y + 1}),
    };
    require(std::memcmp(scalar.segments.data(), expected.data(), sizeof(expected)) == 0);
    const std::array<progpu_native_glyph_outline, 4> outline_expected{
        progpu_native_glyph_outline{0U, 4U, x, y, x + 2, y + 2, 1.0F, 0.0F},
        progpu_native_glyph_outline{4U, 2U, x, y, x + 3, y + 2, 1.0F, 0.0F},
        progpu_native_glyph_outline{6U, 4U, x + 4, y - 2, x + 6, y, 1.0F, 0.0F},
        progpu_native_glyph_outline{10U, 8U, x, y, x + 4, y + 4, 1.0F, 0.0F},
    };
    require(std::memcmp(scalar.outlines.data(), outline_expected.data(), sizeof(outline_expected)) == 0);
    require(std::memcmp(scalar.outlines.data() + 4U, before.outlines.data() + 4U, sizeof(scalar.outlines) - sizeof(outline_expected)) == 0);
    require(std::memcmp(scalar.segments.data() + 18U, before.segments.data() + 18U, sizeof(scalar.segments) - sizeof(expected)) == 0);
    require(std::equal(scalar.source.begin() + 7, scalar.source.end(), before.source.begin() + 7));
    require(std::equal(scalar.positioned.begin() + 8, scalar.positioned.end(), before.positioned.begin() + 8));
    require(std::memcmp(scalar.topology.data() + 8U, before.topology.data() + 8U, 8U * sizeof(sfnt_outline_point)) == 0);
    require(std::memcmp(scalar.physical.data() + 8U, before.physical.data() + 8U, 8U * sizeof(progpu_native_point)) == 0);
    require(retained->glyphs == retained_descriptors); // raw hint tag bits, flags and auxiliaries unchanged
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
    buffers intrinsic;
    require(intrinsic.write(run, hinted_projection_policy::intrinsic_simd) == hinted_outline_error::none);
    require(bytes(intrinsic.outlines) == bytes(scalar.outlines) && bytes(intrinsic.segments) == bytes(scalar.segments) &&
        intrinsic.source == scalar.source && intrinsic.positioned == scalar.positioned && intrinsic.written == scalar.written &&
        bytes(intrinsic.physical) == bytes(scalar.physical));
    for (std::size_t index = 0U; index < 8U; ++index)
        require(intrinsic.topology[index].flags == scalar.topology[index].flags &&
            intrinsic.topology[index].x == scalar.topology[index].x && intrinsic.topology[index].y == scalar.topology[index].y);
#endif
}

void original_quadratic_differential() {
    // Exhaust every original on/conic start/end/adjacency pattern, including
    // odd SIMD tails. This original-writer control supplements explicit records.
    for (std::size_t length = 2U; length <= 8U; ++length) {
        for (unsigned int pattern = 0U; pattern < (1U << length); ++pattern) {
            const auto retained = batch();
            retained->glyphs.resize(1U);
            auto& source = retained->glyphs.front();
            source.points.clear(); source.tags.clear(); source.contour_ends = {static_cast<std::int16_t>(length - 1U)};
            std::array<sfnt_outline_point, 8> original{};
            std::array<progpu_native_point, 8> physical{};
            for (std::size_t index = 0U; index < length; ++index) {
                const auto horizontal = static_cast<long>(index) * 128L - 193L;
                const auto vertical = static_cast<long>((index * index + 3U * index) % 17U) * 64L - 77L;
                const auto tag = static_cast<std::uint8_t>((pattern >> index) & 1U);
                source.points.push_back({horizontal, vertical}); source.tags.push_back(tag);
                original[index] = {0, 0, tag};
                physical[index] = {static_cast<float>(horizontal) / 64.0F, static_cast<float>(vertical) / 64.0F};
            }
            hinted_shaped_run run;
            run.batch = retained; run.source_descriptor_count = 1U; run.descriptor_indices = {0U};
            shaping_glyph positioned; positioned.glyph_id = source.glyph_index; run.glyphs = {positioned};
            const std::uint16_t end = static_cast<std::uint16_t>(length - 1U);
            std::array<progpu_native_path_segment, 16> reference{};
            std::uint32_t count = 0U;
            require(sfnt_simple_glyph_path::try_write_varied_segments(std::span{&end, 1U},
                std::span(original).first(length), std::span(physical).first(length), reference, count));
            buffers scalar;
            require(scalar.write(run) == hinted_outline_error::none && scalar.written.segments == count);
            require(std::memcmp(scalar.segments.data(), reference.data(), count * sizeof(progpu_native_path_segment)) == 0);
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
            buffers intrinsic;
            require(intrinsic.write(run, hinted_projection_policy::intrinsic_simd) == hinted_outline_error::none);
            require(bytes(intrinsic.segments) == bytes(scalar.segments) && bytes(intrinsic.physical) == bytes(scalar.physical));
#endif
        }
    }
}

void mixed_and_closed_cubic_records() {
    const auto retained = batch();
    retained->glyphs = {
        glyph(91U, {{0, 0}, {64, 128}, {128, 0}, {192, -128}, {256, -128}, {320, 0}}, {1, 0, 1, 2, 2, 1}, {5}),
        glyph(92U, {{0, 0}, {64, 128}, {128, 64}}, {1, 2, 2}, {2}),
    };
    hinted_shaped_run run;
    run.batch = retained; run.source_descriptor_count = 2U; run.descriptor_indices = {1U, 0U};
    for (const auto descriptor : run.descriptor_indices) {
        shaping_glyph positioned; positioned.glyph_id = retained->glyphs[descriptor].glyph_index;
        run.glyphs.push_back(positioned);
    }
    buffers output;
    require(output.write(run) == hinted_outline_error::none && output.written.segments == 4U);
    const std::array<progpu_native_path_segment, 4> expected{
        quadratic({0, 0}, {1, 2}, {2, 0}), cubic({2, 0}, {3, -2}, {4, -2}, {5, 0}), line({5, 0}, {0, 0}),
        cubic({0, 0}, {1, 2}, {2, 1}, {0, 0}),
    };
    require(std::memcmp(output.segments.data(), expected.data(), sizeof(expected)) == 0);
    require(output.positioned[0] == 1U && output.positioned[1] == 0U);
}

void failures_precede_all_publication() {
    const auto retained = batch();
    auto run = run_for(retained);
    buffers output;
    const auto saved = bytes(output);
    const auto unchanged = [&] { require(bytes(output) == saved); };
    const auto fail_run = [&](const hinted_shaped_run& invalid, hinted_outline_error error) {
        require(output.write(invalid) == error); unchanged();
        hinted_outline_requirements counts{1U, 2U, 3U, 4U, 5U};
        const auto original = counts;
        require(get_hinted_outline_requirements(invalid, counts, hinted_projection_policy::scalar_reference) == error && counts == original);
    };
    // Last source descriptor faults after all preceding descriptors are valid.
    for (unsigned int invalid = 0U; invalid < 12U; ++invalid) {
        auto bad_batch = batch();
        auto& last = bad_batch->glyphs[6];
        if (invalid == 0U) last.tags.clear();
        if (invalid == 1U) last.contour_ends = {-1};
        if (invalid == 2U) last.contour_ends.clear();
        if (invalid == 3U) last.contour_ends = {1};
        if (invalid == 4U) last.tags = {3};
        if (invalid == 5U) last.tags = {2};
        if (invalid == 6U) last = glyph(81U, {{0, 0}, {64, 64}, {128, 0}}, {1, 2, 1}, {2});
        if (invalid == 7U) last = glyph(81U, {{0, 0}, {64, 64}, {128, 0}}, {0, 1, 2}, {2});
        if (invalid == 8U) last = glyph(81U, {{0, 0}, {64, 64}}, {0, 2}, {1});
        if (invalid == 9U) last = glyph(81U, {{0, 0}, {64, 64}}, {1, 1}, {0, 0});
        if (invalid == 10U) last = glyph(81U, {{0, 0}, {64, 64}}, {1, 1}, {0});
        if (invalid == 11U) last = glyph(81U, {}, {1}, {});
        auto invalid_run = run_for(bad_batch);
        fail_run(invalid_run, hinted_outline_error::invalid_topology);
    }
    for (const int flags : {-1, 2, 8, 0x100, 0x400}) {
        auto bad_batch = batch(); bad_batch->glyphs[6].outline_flags = flags;
        fail_run(run_for(bad_batch), hinted_outline_error::unsupported_flags);
    }
    // A B/W per-contour SCANTYPE override appears at a late contour start;
    // neither earlier glyphs nor earlier contours may be published first.
    for (const std::uint8_t tag : {std::uint8_t{0x05}, std::uint8_t{0xE5}, std::uint8_t{0x21}}) {
        auto bad_batch = batch(); bad_batch->glyphs[4].tags[4] = tag;
        fail_run(run_for(bad_batch), hinted_outline_error::unsupported_flags);
    }
    for (unsigned int invalid = 0U; invalid < 3U; ++invalid) {
        auto bad_batch = batch();
        auto& last = bad_batch->glyphs[6];
        if (invalid == 0U) last.points[0].x_26_6 = 16777217L; // point itself not exactly representable
        if (invalid == 1U) last = glyph(81U, {{16777216L, 0}, {16777218L, 64}}, {0, 0}, {1}); // exact inputs, inexact implied midpoint
        if (invalid == 2U) last.points[0].y_26_6 = std::numeric_limits<long>::max(); // signed32 or float precision failure
        fail_run(run_for(bad_batch), hinted_outline_error::unsupported_coordinates);
    }
    auto invalid = run; invalid.descriptor_indices.back() = 7U; fail_run(invalid, hinted_outline_error::invalid_run);
    invalid = run; invalid.glyphs.back().glyph_id = 999U; fail_run(invalid, hinted_outline_error::invalid_run);
    invalid = run; invalid.descriptor_indices.pop_back(); fail_run(invalid, hinted_outline_error::invalid_run);
    invalid = run; invalid.source_descriptor_count = 9U; fail_run(invalid, hinted_outline_error::invalid_run);
    invalid = run; invalid.batch.reset(); fail_run(invalid, hinted_outline_error::invalid_run);
    auto invalid_identity = batch();
    invalid_identity->identity.reset(); fail_run(run_for(invalid_identity), hinted_outline_error::invalid_run);
    auto invalid_phase = batch();
    auto phase = std::make_shared<hinted_font_identity>(*invalid_phase->identity); phase->y_phase_26_6 = 64U;
    invalid_phase->identity = phase; fail_run(run_for(invalid_phase), hinted_outline_error::invalid_run);
    for (unsigned int missing = 0U; missing < 6U; ++missing) {
        require(write_hinted_run_outlines(run,
            {std::span(output.topology).first(missing == 0U ? 7U : 16U), std::span(output.physical).first(missing == 1U ? 7U : 16U)},
            std::span(output.outlines).first(missing == 2U ? 3U : 12U), std::span(output.segments).first(missing == 3U ? 17U : 64U),
            std::span(output.source).first(missing == 4U ? 6U : 16U), std::span(output.positioned).first(missing == 5U ? 7U : 16U),
            output.written, hinted_projection_policy::scalar_reference) == hinted_outline_error::insufficient_capacity);
        unchanged();
    }
    for (const auto policy : {hinted_projection_policy::native_compute, hinted_projection_policy::gpu_shader,
            static_cast<hinted_projection_policy>(713U)}) {
        require(output.write(run, policy) == hinted_outline_error::unsupported_policy); unchanged();
    }
    // Capacity tails are borrowed too: aliasing only an otherwise-unused tail
    // still fails before any scratch/output publication.
    auto* physical_tail = reinterpret_cast<std::uint32_t*>(output.physical.data() + 12U);
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        std::span{physical_tail, 8U}, output.positioned, output.written,
        hinted_projection_policy::scalar_reference) == hinted_outline_error::invalid_argument); unchanged();
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        output.source, output.source, output.written, hinted_projection_policy::scalar_reference) ==
        hinted_outline_error::invalid_argument); unchanged();
    const auto retained_indices = run.descriptor_indices;
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        run.descriptor_indices, output.positioned, output.written, hinted_projection_policy::scalar_reference) ==
        hinted_outline_error::invalid_argument); unchanged(); require(run.descriptor_indices == retained_indices);
    run.descriptor_indices.reserve(32U);
    auto* index_spare = run.descriptor_indices.data() + run.descriptor_indices.size();
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        std::span{index_spare, 16U}, output.positioned, output.written, hinted_projection_policy::scalar_reference) ==
        hinted_outline_error::invalid_argument); unchanged(); require(run.descriptor_indices == retained_indices);
    retained->glyphs[6].points.reserve(32U);
    auto* point_spare = reinterpret_cast<progpu_native_point*>(retained->glyphs[6].points.data() + retained->glyphs[6].points.size());
    require(write_hinted_run_outlines(run, {output.topology, std::span{point_spare, 16U}}, output.outlines, output.segments,
        output.source, output.positioned, output.written, hinted_projection_policy::scalar_reference) ==
        hinted_outline_error::invalid_argument); unchanged();
    require(retained->glyphs[6].points == std::vector<hinted_outline_point>{{13, 19}});
    // Auxiliary storage is not drawn, but still belongs to the immutable input.
    auto* auxiliary = reinterpret_cast<progpu_native_point*>(retained->glyphs.back().points.data());
    require(write_hinted_run_outlines(run, {output.topology, std::span{auxiliary, 1U}}, output.outlines, output.segments,
        output.source, output.positioned, output.written, hinted_projection_policy::scalar_reference) ==
        hinted_outline_error::invalid_argument); unchanged();
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        output.source, output.positioned, *reinterpret_cast<hinted_outline_requirements*>(output.source.data()),
        hinted_projection_policy::scalar_reference) == hinted_outline_error::invalid_argument); unchanged();
    alignas(progpu_native_point) std::array<std::byte, 256> unaligned{};
    auto* unaligned_points = reinterpret_cast<progpu_native_point*>(unaligned.data() + 1U);
    require(write_hinted_run_outlines(run, {output.topology, std::span{unaligned_points, 16U}}, output.outlines, output.segments,
        output.source, output.positioned, output.written, hinted_projection_policy::scalar_reference) ==
        hinted_outline_error::invalid_argument); unchanged();
    auto empty_batch = batch(); empty_batch->glyphs.clear();
    hinted_shaped_run empty; empty.batch = empty_batch;
    require(output.write(empty) == hinted_outline_error::none && output.written == hinted_outline_requirements{});
    require(bytes(output.topology) == bytes(buffers{}.topology) && bytes(output.physical) == bytes(buffers{}.physical) &&
        bytes(output.outlines) == bytes(buffers{}.outlines) && bytes(output.segments) == bytes(buffers{}.segments) &&
        output.source == buffers{}.source && output.positioned == buffers{}.positioned);
}
} // namespace

int main() {
    try {
        source_geometry_and_phase();
        original_quadratic_differential();
        mixed_and_closed_cubic_records();
        failures_precede_all_publication();
        std::cout << "{\"sourceIndexedGeometry\":true,\"originalQuadraticWriter\":true,\"exactCubicRecords\":true,\"wholeRunAtomicPreflight\":true}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n'; return 1;
    }
}
