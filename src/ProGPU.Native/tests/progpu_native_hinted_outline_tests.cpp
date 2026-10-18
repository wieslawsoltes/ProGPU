#include "progpu_native_hinted_outline.hpp"
#include "progpu_native_text_hinting.h"
#include "progpu_native_binary_glyph_contract.hpp"

#include <algorithm>
#include <array>
#include <cstring>
#include <iostream>
#include <limits>
#include <source_location>
#include <stdexcept>
#include <string>
#include <vector>

#if defined(__has_feature)
#if __has_feature(address_sanitizer)
#define PROGPU_OUTLINE_TEST_ASAN 1
#endif
#endif
#if defined(__SANITIZE_ADDRESS__)
#define PROGPU_OUTLINE_TEST_ASAN 1
#endif
#if defined(PROGPU_OUTLINE_TEST_ASAN)
#include <sanitizer/asan_interface.h>
#endif

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
        hinted_projection_policy policy = hinted_projection_policy::scalar_reference,
        hinted_outline_coverage coverage = hinted_outline_coverage::strict) {
        return write_hinted_run_outlines(run, {topology, physical}, outlines, segments,
            source, positioned, written, policy, coverage);
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

// Only fixture painting/snapshotting may inspect initialized spare capacity.
// Restore its original sanitizer poison before any production operation, so
// even a one-byte read/write through an incorrectly admitted alias still fails.
class fixture_capacity_access final {
public:
    fixture_capacity_access(std::byte* start, std::size_t count) : start_(start), count_(count) {
#if defined(PROGPU_OUTLINE_TEST_ASAN)
        poisoned_ = count_ != 0U && __asan_address_is_poisoned(start_) != 0;
        if (poisoned_) __asan_unpoison_memory_region(start_, count_);
#endif
    }
    ~fixture_capacity_access() {
#if defined(PROGPU_OUTLINE_TEST_ASAN)
        if (poisoned_) __asan_poison_memory_region(start_, count_);
#endif
    }
    fixture_capacity_access(const fixture_capacity_access&) = delete;
    fixture_capacity_access& operator=(const fixture_capacity_access&) = delete;
private:
    [[maybe_unused]] std::byte* start_;
    [[maybe_unused]] std::size_t count_;
    [[maybe_unused]] bool poisoned_ = false;
};

void shaping_input_aliases() {
    const auto retained = batch();
    auto run = run_for(retained);
    run.shaping_input.reserve(512U);
    for (std::uint32_t index = 0U; index < 8U; ++index)
        run.shaping_input.push_back({0x41U + index, index, 1U, 0U, 0U,
            open_type_tag::from_chars('D', 'F', 'L', 'T')});
    for (std::size_t index = 0U; index < run.glyphs.size(); ++index) {
        const auto source = static_cast<std::uint32_t>(run.glyphs.size() - 1U - index);
        run.glyphs[index].code_point = run.shaping_input[source].code_point;
        run.glyphs[index].cluster = static_cast<std::int32_t>(source);
    }
    // Paint and snapshot the complete retained allocation, not only live
    // scalars: an output aimed solely at spare capacity must not mutate it.
    const auto used_bytes = run.shaping_input.size() * sizeof(unicode_scalar);
    const auto capacity_bytes = run.shaping_input.capacity() * sizeof(unicode_scalar);
    auto* const input_bytes = reinterpret_cast<std::byte*>(run.shaping_input.data());
    std::vector<std::byte> input_before(capacity_bytes);
    {
        fixture_capacity_access inspect(input_bytes + used_bytes, capacity_bytes - used_bytes);
        std::memset(input_bytes + used_bytes, 0xA6, capacity_bytes - used_bytes);
        std::memcpy(input_before.data(), input_bytes, capacity_bytes);
    }
    const auto unchanged_input = [&] {
        fixture_capacity_access inspect(input_bytes + used_bytes, capacity_bytes - used_bytes);
        return std::memcmp(input_before.data(), input_bytes, capacity_bytes) == 0;
    };
    hinted_outline_requirements admitted_requirements{};
    require(get_hinted_outline_requirements(run, admitted_requirements,
        hinted_projection_policy::scalar_reference) == hinted_outline_error::none &&
        admitted_requirements == hinted_outline_requirements{7U, 8U, 4U, 18U, 8U});
    buffers admitted;
    require(admitted.write(run) == hinted_outline_error::none &&
        admitted.written == admitted_requirements &&
        unchanged_input());
    buffers output;
    const auto output_before = bytes(output);
    constexpr auto alias_alignment = std::max({alignof(hinted_outline_requirements),
        alignof(sfnt_outline_point), alignof(progpu_native_point),
        alignof(progpu_native_glyph_outline), alignof(progpu_native_path_segment),
        alignof(std::uint32_t)});
    for (const auto offset : {std::size_t{0U}, used_bytes}) {
        const auto address = reinterpret_cast<std::uintptr_t>(input_bytes + offset);
        const auto aligned = (address + alias_alignment - 1U) & ~(alias_alignment - 1U);
        auto* const storage = reinterpret_cast<std::byte*>(aligned);
        const auto remaining = capacity_bytes - static_cast<std::size_t>(storage - input_bytes);
        require(remaining >= std::max({sizeof(output.topology), sizeof(output.physical),
            sizeof(output.outlines), sizeof(output.segments), sizeof(output.source),
            sizeof(output.positioned), sizeof(output.written)}));
        auto& requirements = *reinterpret_cast<hinted_outline_requirements*>(storage);
        require(get_hinted_outline_requirements(run, requirements,
            hinted_projection_policy::scalar_reference) == hinted_outline_error::invalid_argument);
        require(bytes(output) == output_before &&
            unchanged_input());
        // Independently exercise every borrowed conversion destination/scratch
        // role with its full original capacity in both used and spare storage.
        for (unsigned int target = 0U; target < 7U; ++target) {
            std::span<sfnt_outline_point> topology = output.topology;
            std::span<progpu_native_point> physical = output.physical;
            std::span<progpu_native_glyph_outline> outlines = output.outlines;
            std::span<progpu_native_path_segment> segments = output.segments;
            std::span<std::uint32_t> source = output.source;
            std::span<std::uint32_t> positioned = output.positioned;
            auto* written = &output.written;
            if (target == 0U) topology = {reinterpret_cast<sfnt_outline_point*>(storage), topology.size()};
            if (target == 1U) physical = {reinterpret_cast<progpu_native_point*>(storage), physical.size()};
            if (target == 2U) outlines = {reinterpret_cast<progpu_native_glyph_outline*>(storage), outlines.size()};
            if (target == 3U) segments = {reinterpret_cast<progpu_native_path_segment*>(storage), segments.size()};
            if (target == 4U) source = {reinterpret_cast<std::uint32_t*>(storage), source.size()};
            if (target == 5U) positioned = {reinterpret_cast<std::uint32_t*>(storage), positioned.size()};
            if (target == 6U) written = reinterpret_cast<hinted_outline_requirements*>(storage);
            require(write_hinted_run_outlines(run, {topology, physical}, outlines, segments,
                source, positioned, *written, hinted_projection_policy::scalar_reference) ==
                hinted_outline_error::invalid_argument);
            require(bytes(output) == output_before &&
                unchanged_input());
        }
    }
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

void explicit_nonzero_vector_coverage() {
    // The strict authored geometry has independent literal segment/bounds/map
    // controls above. Add only public raster metadata to separately owned input:
    // outputs must remain that exact geometry, not a metadata-stripped run.
    const auto authored = batch();
    const auto strict_run = run_for(authored);
    buffers strict;
    require(strict.write(strict_run) == hinted_outline_error::none);
    for (const unsigned flags : {0U, 0x08U, 0x100U, 0x108U}) {
        const auto observed = batch();
        for (std::size_t descriptor = 0U; descriptor < 7U; ++descriptor) {
            auto& source = observed->glyphs[descriptor];
            source.outline_flags |= static_cast<int>(flags);
            std::size_t start = 0U;
            for (const auto end : source.contour_ends) {
                source.tags[start] |= 0x04U; // contour-start SCANTYPE mode zero
                start = static_cast<std::size_t>(end) + 1U;
            }
        }
        // The actual hosted fixture reports ON|TOUCH_Y|HAS_SCANMODE = 0x15.
        observed->glyphs[3].tags[0] = 0x15U;
        const auto saved_descriptors = observed->glyphs;
        const auto vector_run = run_for(observed);
        hinted_outline_requirements required{};
        require(get_hinted_outline_requirements(vector_run, required,
            hinted_projection_policy::scalar_reference, hinted_outline_coverage::nonzero_vector) ==
            hinted_outline_error::none && required == strict.written);
        buffers output;
        const auto before = output;
        require(output.write(vector_run, hinted_projection_policy::scalar_reference,
            hinted_outline_coverage::nonzero_vector) == hinted_outline_error::none);
        require(output.written == strict.written && bytes(output.outlines) == bytes(strict.outlines) &&
            bytes(output.segments) == bytes(strict.segments) && bytes(output.physical) == bytes(strict.physical) &&
            output.source == strict.source && output.positioned == strict.positioned);
        for (std::size_t index = 0U; index < output.topology.size(); ++index)
            require(output.topology[index].x == strict.topology[index].x &&
                output.topology[index].y == strict.topology[index].y &&
                output.topology[index].flags == strict.topology[index].flags);
        require(std::memcmp(output.outlines.data() + 4U, before.outlines.data() + 4U,
            sizeof(output.outlines) - 4U * sizeof(progpu_native_glyph_outline)) == 0);
        require(std::memcmp(output.segments.data() + 18U, before.segments.data() + 18U,
            sizeof(output.segments) - 18U * sizeof(progpu_native_path_segment)) == 0);
        require(std::memcmp(output.topology.data() + 8U, before.topology.data() + 8U,
            8U * sizeof(sfnt_outline_point)) == 0);
        require(std::memcmp(output.physical.data() + 8U, before.physical.data() + 8U,
            8U * sizeof(progpu_native_point)) == 0);
        require(std::equal(output.source.begin() + 7U, output.source.end(), before.source.begin() + 7U) &&
            std::equal(output.positioned.begin() + 8U, output.positioned.end(), before.positioned.begin() + 8U));
        require(observed->glyphs == saved_descriptors && vector_run.descriptor_indices == strict_run.descriptor_indices);
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
        buffers intrinsic;
        require(intrinsic.write(vector_run, hinted_projection_policy::intrinsic_simd,
            hinted_outline_coverage::nonzero_vector) == hinted_outline_error::none);
        require(bytes(intrinsic.outlines) == bytes(output.outlines) && bytes(intrinsic.segments) == bytes(output.segments) &&
            bytes(intrinsic.physical) == bytes(output.physical) && intrinsic.source == output.source &&
            intrinsic.positioned == output.positioned && intrinsic.written == output.written);
        require(observed->glyphs == saved_descriptors);
#endif
        buffers denied;
        const auto denied_before = bytes(denied);
        require(denied.write(vector_run) == hinted_outline_error::unsupported_flags && bytes(denied) == denied_before);
        hinted_outline_requirements denied_counts{1U, 2U, 3U, 4U, 5U};
        const auto denied_counts_before = denied_counts;
        require(get_hinted_outline_requirements(vector_run, denied_counts,
            hinted_projection_policy::scalar_reference) == hinted_outline_error::unsupported_flags &&
            denied_counts == denied_counts_before && observed->glyphs == saved_descriptors);
    }

    const auto observed = batch();
    for (std::size_t descriptor = 0U; descriptor < 7U; ++descriptor)
        observed->glyphs[descriptor].outline_flags |= 0x108;
    observed->glyphs[4].tags[4] = 0x15U; // later valid contour-start marker
    auto run = run_for(observed);
    buffers output;
    const auto before = bytes(output);
    const auto fail = [&](const hinted_shaped_run& invalid, hinted_outline_error expected,
        hinted_outline_coverage coverage = hinted_outline_coverage::nonzero_vector) {
        const auto raw_before = invalid.batch->glyphs;
        require(output.write(invalid, hinted_projection_policy::scalar_reference, coverage) == expected &&
            bytes(output) == before && invalid.batch->glyphs == raw_before);
        hinted_outline_requirements required{1U, 2U, 3U, 4U, 5U};
        const auto saved = required;
        require(get_hinted_outline_requirements(invalid, required, hinted_projection_policy::scalar_reference,
            coverage) == expected && required == saved && invalid.batch->glyphs == raw_before);
    };
    for (const int flags : {-1, 0x02, 0x10, 0x20, 0x40, 0x200, 0x400}) {
        auto invalid = std::make_shared<hinted_glyph_batch>(*observed);
        invalid->glyphs[6].outline_flags = flags;
        fail(run_for(invalid), hinted_outline_error::unsupported_flags);
    }
    for (const std::uint8_t tag : {std::uint8_t{0x25}, std::uint8_t{0xE5}, std::uint8_t{0x21}}) {
        auto invalid = std::make_shared<hinted_glyph_batch>(*observed);
        invalid->glyphs[4].tags[4] = tag;
        fail(run_for(invalid), hinted_outline_error::unsupported_flags);
    }
    auto misplaced = std::make_shared<hinted_glyph_batch>(*observed);
    misplaced->glyphs[4].tags[5] = 0x15U; // not the contour start
    fail(run_for(misplaced), hinted_outline_error::unsupported_flags);
    auto malformed = std::make_shared<hinted_glyph_batch>(*observed);
    malformed->glyphs[6].contour_ends = {-1};
    fail(run_for(malformed), hinted_outline_error::invalid_topology);
    fail(run, hinted_outline_error::unsupported_policy, static_cast<hinted_outline_coverage>(713U));
    for (const auto policy : {hinted_projection_policy::native_compute, hinted_projection_policy::gpu_shader,
            static_cast<hinted_projection_policy>(713U)}) {
        require(output.write(run, policy, hinted_outline_coverage::nonzero_vector) ==
            hinted_outline_error::unsupported_policy && bytes(output) == before);
    }
    for (unsigned int missing = 0U; missing < 6U; ++missing) {
        require(write_hinted_run_outlines(run,
            {std::span(output.topology).first(missing == 0U ? 7U : 16U), std::span(output.physical).first(missing == 1U ? 7U : 16U)},
            std::span(output.outlines).first(missing == 2U ? 3U : 12U), std::span(output.segments).first(missing == 3U ? 17U : 64U),
            std::span(output.source).first(missing == 4U ? 6U : 16U), std::span(output.positioned).first(missing == 5U ? 7U : 16U),
            output.written, hinted_projection_policy::scalar_reference, hinted_outline_coverage::nonzero_vector) ==
            hinted_outline_error::insufficient_capacity && bytes(output) == before);
    }
    const auto saved_descriptors = observed->glyphs;
    const auto saved_indices = run.descriptor_indices;
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        run.descriptor_indices, output.positioned, output.written, hinted_projection_policy::scalar_reference,
        hinted_outline_coverage::nonzero_vector) == hinted_outline_error::invalid_argument && bytes(output) == before);
    run.descriptor_indices.reserve(32U);
    auto* spare = run.descriptor_indices.data() + run.descriptor_indices.size();
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        std::span{spare, 16U}, output.positioned, output.written, hinted_projection_policy::scalar_reference,
        hinted_outline_coverage::nonzero_vector) == hinted_outline_error::invalid_argument && bytes(output) == before);
    auto* tail = reinterpret_cast<std::uint32_t*>(output.physical.data() + 12U);
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        std::span{tail, 8U}, output.positioned, output.written, hinted_projection_policy::scalar_reference,
        hinted_outline_coverage::nonzero_vector) == hinted_outline_error::invalid_argument && bytes(output) == before);
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        output.source, output.source, output.written, hinted_projection_policy::scalar_reference,
        hinted_outline_coverage::nonzero_vector) == hinted_outline_error::invalid_argument && bytes(output) == before);
    require(observed->glyphs == saved_descriptors && run.descriptor_indices == saved_indices);
}
void explicit_antialiased_vector_coverage() {
    const auto authored = batch();
    buffers reference;
    require(reference.write(run_for(authored)) == hinted_outline_error::none);
    // Independent declared metadata matrix, not a whitelist of one hosted
    // profile: every dropout/high-precision combination and all SCANTYPE values
    // 0..7 (including the documented fast-mode aliases 3/6/7) retain geometry.
    for (unsigned int profile = 0U; profile < 16U; ++profile) {
        const auto flags = ((profile & 1U) != 0U ? 0x08U : 0U) |
            ((profile & 2U) != 0U ? 0x10U : 0U) | ((profile & 4U) != 0U ? 0x20U : 0U) |
            ((profile & 8U) != 0U ? 0x100U : 0U);
        for (unsigned int mode = 0U; mode < 8U; ++mode) {
            const auto observed = batch();
            for (std::size_t descriptor = 0U; descriptor < 7U; ++descriptor) {
                auto& source = observed->glyphs[descriptor];
                source.outline_flags |= static_cast<int>(flags);
                std::size_t start = 0U;
                for (const auto end : source.contour_ends) {
                    source.tags[start] |= static_cast<std::uint8_t>(0x04U | (mode << 5U));
                    start = static_cast<std::size_t>(end) + 1U;
                }
            }
            const auto raw_before = observed->glyphs;
            const auto run = run_for(observed);
            hinted_outline_requirements counts{71U, 73U, 79U, 83U, 89U};
            require(get_hinted_outline_requirements(run, counts, hinted_projection_policy::scalar_reference,
                hinted_outline_coverage::antialiased_vector) == hinted_outline_error::none && counts == reference.written);
            buffers output;
            require(output.write(run, hinted_projection_policy::scalar_reference,
                hinted_outline_coverage::antialiased_vector) == hinted_outline_error::none);
            require(output.written == reference.written && bytes(output.outlines) == bytes(reference.outlines) &&
                bytes(output.segments) == bytes(reference.segments) && bytes(output.physical) == bytes(reference.physical) &&
                output.source == reference.source && output.positioned == reference.positioned);
            for (std::size_t index = 0U; index < output.topology.size(); ++index)
                require(output.topology[index].x == reference.topology[index].x &&
                    output.topology[index].y == reference.topology[index].y &&
                    output.topology[index].flags == reference.topology[index].flags);
            require(observed->glyphs == raw_before);
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
            buffers intrinsic;
            require(intrinsic.write(run, hinted_projection_policy::intrinsic_simd,
                hinted_outline_coverage::antialiased_vector) == hinted_outline_error::none);
            require(bytes(intrinsic.outlines) == bytes(output.outlines) && bytes(intrinsic.segments) == bytes(output.segments) &&
                bytes(intrinsic.physical) == bytes(output.physical) && intrinsic.source == output.source &&
                intrinsic.positioned == output.positioned && intrinsic.written == output.written);
            for (std::size_t index = 0U; index < intrinsic.topology.size(); ++index)
                require(intrinsic.topology[index].x == output.topology[index].x &&
                    intrinsic.topology[index].y == output.topology[index].y &&
                    intrinsic.topology[index].flags == output.topology[index].flags);
            require(observed->glyphs == raw_before);
#endif
            buffers legacy;
            const auto legacy_before = bytes(legacy);
            require(legacy.write(run) == hinted_outline_error::unsupported_flags && bytes(legacy) == legacy_before);
            const bool legacy_vector_admitted = (flags & 0x30U) == 0U && mode == 0U;
            const auto legacy_result = legacy.write(run, hinted_projection_policy::scalar_reference,
                hinted_outline_coverage::nonzero_vector);
            require(legacy_result == (legacy_vector_admitted ? hinted_outline_error::none : hinted_outline_error::unsupported_flags));
            if (!legacy_vector_admitted) require(bytes(legacy) == legacy_before);
            require(observed->glyphs == raw_before);
        }
    }

    const auto observed = batch();
    for (std::size_t descriptor = 0U; descriptor < 7U; ++descriptor)
        observed->glyphs[descriptor].outline_flags |= 0x130;
    observed->glyphs[4].tags[4] = 0x95U; // ON|TOUCH_Y|HAS_SCANMODE, declared mode 4.
    auto run = run_for(observed);
    buffers output;
    const auto before = bytes(output);
    const auto fail = [&](const hinted_shaped_run& invalid, hinted_outline_error expected) {
        const auto raw_before = invalid.batch->glyphs;
        require(output.write(invalid, hinted_projection_policy::scalar_reference,
            hinted_outline_coverage::antialiased_vector) == expected && bytes(output) == before);
        hinted_outline_requirements counts{1U, 2U, 3U, 4U, 5U};
        const auto counts_before = counts;
        require(get_hinted_outline_requirements(invalid, counts, hinted_projection_policy::scalar_reference,
            hinted_outline_coverage::antialiased_vector) == expected && counts == counts_before &&
            invalid.batch->glyphs == raw_before);
    };
    // Flag validation still precedes no-ink/topology decisions, including a
    // late singleton descriptor and an earlier truly empty descriptor.
    for (const int flags : {-1, 0x02, 0x40, 0x200, 0x400, 0x132, 0x170, 0x330, 0x530}) {
        for (const auto descriptor : {1U, 6U}) {
            auto invalid = std::make_shared<hinted_glyph_batch>(*observed);
            invalid->glyphs[descriptor].outline_flags = flags;
            fail(run_for(invalid), hinted_outline_error::unsupported_flags);
        }
    }
    for (const std::uint8_t tag : {std::uint8_t{0x21}, std::uint8_t{0xE1}}) {
        auto invalid = std::make_shared<hinted_glyph_batch>(*observed);
        invalid->glyphs[4].tags[4] = tag; // Upper mode bits have no marker.
        fail(run_for(invalid), hinted_outline_error::unsupported_flags);
    }
    for (const std::uint8_t tag : {std::uint8_t{0x15}, std::uint8_t{0x25}, std::uint8_t{0xF5}}) {
        auto invalid = std::make_shared<hinted_glyph_batch>(*observed);
        invalid->glyphs[4].tags[5] = tag; // Marker/mode away from contour start.
        fail(run_for(invalid), hinted_outline_error::unsupported_flags);
    }
    auto invalid_kind = std::make_shared<hinted_glyph_batch>(*observed);
    invalid_kind->glyphs[6].tags[0] = 0xE7U;
    fail(run_for(invalid_kind), hinted_outline_error::invalid_topology);
    auto invalid_end = std::make_shared<hinted_glyph_batch>(*observed);
    invalid_end->glyphs[6].contour_ends = {-1};
    fail(run_for(invalid_end), hinted_outline_error::invalid_topology);
    for (unsigned int missing = 0U; missing < 6U; ++missing) {
        require(write_hinted_run_outlines(run,
            {std::span(output.topology).first(missing == 0U ? 7U : 16U), std::span(output.physical).first(missing == 1U ? 7U : 16U)},
            std::span(output.outlines).first(missing == 2U ? 3U : 12U), std::span(output.segments).first(missing == 3U ? 17U : 64U),
            std::span(output.source).first(missing == 4U ? 6U : 16U), std::span(output.positioned).first(missing == 5U ? 7U : 16U),
            output.written, hinted_projection_policy::scalar_reference, hinted_outline_coverage::antialiased_vector) ==
            hinted_outline_error::insufficient_capacity && bytes(output) == before);
    }
    const auto raw_before = observed->glyphs;
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        run.descriptor_indices, output.positioned, output.written, hinted_projection_policy::scalar_reference,
        hinted_outline_coverage::antialiased_vector) == hinted_outline_error::invalid_argument && bytes(output) == before);
    run.descriptor_indices.reserve(32U);
    auto* spare = run.descriptor_indices.data() + run.descriptor_indices.size();
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        std::span{spare, 16U}, output.positioned, output.written, hinted_projection_policy::scalar_reference,
        hinted_outline_coverage::antialiased_vector) == hinted_outline_error::invalid_argument && bytes(output) == before);
    require(write_hinted_run_outlines(run, {output.topology, output.physical}, output.outlines, output.segments,
        output.source, output.source, output.written, hinted_projection_policy::scalar_reference,
        hinted_outline_coverage::antialiased_vector) == hinted_outline_error::invalid_argument && bytes(output) == before);
    require(observed->glyphs == raw_before);
}
void binary_contour_metadata() {
    // Authored source contours, not a raster reference. Exercise every raw mode
    // and inheritance across a zero-segment contour without rewriting aliases.
    for (std::uint32_t mode = 0; mode < 8U; ++mode) {
        auto retained = batch();
        retained->glyphs = {
            glyph(41U, {{0, 0}, {128, 0}, {128, 128}, {0, 128},
                         {256, 0}, {384, 0}, {384, 128}, {256, 128},
                         {512, 0}},
                {static_cast<std::uint8_t>(5U | (mode << 5U)), 1, 1, 1,
                 1, 1, 1, 1, 1}, {3, 7, 8}, 0x109),
            glyph(41U, {}, {}, {}),
            glyph(42U, {{0, 0}, {128, 256}, {256, 0}}, {1, 0, 1}, {2}, 0x131)
        };
        const auto original = retained->glyphs;
        buffers antialiased;
        require(write_hinted_batch_outlines(*retained, {antialiased.topology, antialiased.physical},
            antialiased.outlines, antialiased.segments, antialiased.source, antialiased.written,
            hinted_projection_policy::scalar_reference, hinted_outline_coverage::antialiased_vector) ==
            hinted_outline_error::none);
        buffers scalar;
        for (const auto policy : {hinted_projection_policy::scalar_reference, hinted_projection_policy::intrinsic_simd}) {
            buffers output;
            const auto before = output;
            hinted_outline_requirements required{};
            require(get_hinted_batch_outline_requirements(*retained, required, policy,
                hinted_outline_coverage::monochrome) == hinted_outline_error::none);
            require(write_hinted_batch_outlines(*retained, {output.topology, output.physical}, output.outlines,
                output.segments, output.source, output.written, policy, hinted_outline_coverage::monochrome) ==
                hinted_outline_error::none && output.written == required);
            require(output.source[1] == hinted_no_outline && output.source == antialiased.source &&
                bytes(output.outlines) == bytes(antialiased.outlines) && retained->glyphs == original);
            for (std::size_t i = 0; i < required.outlines; ++i) {
                const auto outline = output.outlines[i];
                const auto selected = std::span<const progpu_native_path_segment>{output.segments}.subspan(
                    outline.segment_offset, outline.segment_count);
                require(valid_glyph_scan_outline(outline, selected));
                auto scaled = outline;
                scaled.raster_scale = 2.0F;
                require(!valid_glyph_scan_outline(scaled, selected));
                scaled = outline; scaled.subpixel_x = 0.25F;
                require(!valid_glyph_scan_outline(scaled, selected));
                for (unsigned fault = 0; fault < 5U; ++fault) {
                    auto changed = output.segments;
                    auto& first = changed[outline.segment_offset];
                    if (fault == 0U) first.pad0 |= 8U;
                    if (fault == 1U) first.pad1 += 1U;
                    if (fault == 2U) first.pad2 = 0U;
                    if (fault == 3U) first.pad2 = static_cast<std::uint32_t>(outline.segment_count + 1U);
                    if (fault == 4U) first.p0.x += 0.25F;
                    require(!valid_glyph_scan_outline(outline,
                        std::span<const progpu_native_path_segment>{changed}.subspan(
                            outline.segment_offset, outline.segment_count)));
                }
            }
            for (std::size_t i = 0; i < required.segments; ++i) {
                const auto& segment = output.segments[i];
                const auto expected_start = i < 8U ? (i < 4U ? 0U : 4U) : 0U;
                const auto expected_count = i < 8U ? 4U : 2U;
                const auto flags = i < 8U ? 0x109U : 0x131U;
                const auto override = i < 4U ? PROGPU_NATIVE_FONT_SCAN_CONTOUR_OVERRIDE : 0U;
                require(segment.pad0 == (PROGPU_NATIVE_FONT_SCAN_SEGMENT_MARKER | (i < 8U ? mode : 4U) |
                    (flags << 16U) | override) &&
                    segment.pad1 == expected_start && segment.pad2 == expected_count);
                auto geometry_only = segment;
                geometry_only.pad0 = geometry_only.pad1 = geometry_only.pad2 = 0U;
                require(bytes(geometry_only) == bytes(antialiased.segments[i]));
            }
            for (std::size_t i = required.segments; i < output.segments.size(); ++i)
                require(bytes(output.segments[i]) == bytes(before.segments[i]));
            if (policy == hinted_projection_policy::scalar_reference) scalar = output;
            else require(bytes(output) == bytes(scalar));
        }
        const auto reject = [&](const hinted_glyph_batch& invalid, hinted_outline_error expected) {
            buffers output;
            const auto before = bytes(output);
            hinted_outline_requirements required{9, 8, 7, 6, 5};
            const auto original_required = required;
            require(get_hinted_batch_outline_requirements(invalid, required,
                hinted_projection_policy::scalar_reference, hinted_outline_coverage::monochrome) == expected &&
                required == original_required);
            require(write_hinted_batch_outlines(invalid, {output.topology, output.physical}, output.outlines,
                output.segments, output.source, output.written, hinted_projection_policy::scalar_reference,
                hinted_outline_coverage::monochrome) == expected && bytes(output) == before);
        };
        auto invalid = *retained;
        invalid.glyphs[0].tags[1] = 5U;
        reject(invalid, hinted_outline_error::unsupported_flags);
        invalid = *retained;
        invalid.glyphs[0].tags[1] = 2U;
        reject(invalid, hinted_outline_error::unsupported_policy);
        invalid = *retained;
        invalid.glyphs[0].outline_flags |= 0x200;
        reject(invalid, hinted_outline_error::unsupported_flags);
        buffers short_output;
        const auto before = bytes(short_output);
        require(write_hinted_batch_outlines(*retained, {short_output.topology, short_output.physical},
            short_output.outlines, std::span(short_output.segments).first(9), short_output.source,
            short_output.written, hinted_projection_policy::scalar_reference, hinted_outline_coverage::monochrome) ==
            hinted_outline_error::insufficient_capacity && bytes(short_output) == before);
    }
}

void original_batch_geometry() {
    const auto retained = batch();
    buffers rejected;
    const auto untouched = bytes(rejected);
    // The run excludes its invalid auxiliary probe. A raw batch must instead
    // validate every descriptor; it cannot guess which glyphs a caller will use.
    require(write_hinted_batch_outlines(*retained, {rejected.topology, rejected.physical},
        rejected.outlines, rejected.segments, rejected.source, rejected.written) ==
        hinted_outline_error::unsupported_flags && bytes(rejected) == untouched);
    retained->glyphs.pop_back();
    const auto original = retained->glyphs;
    auto run = run_for(retained);
    run.figure_descriptor_count = 0U;
    for (const auto coverage : {hinted_outline_coverage::strict,
            hinted_outline_coverage::nonzero_vector, hinted_outline_coverage::antialiased_vector}) {
        buffers reference;
        require(reference.write(run, hinted_projection_policy::scalar_reference, coverage) == hinted_outline_error::none);
        for (const auto policy : {hinted_projection_policy::scalar_reference, hinted_projection_policy::intrinsic_simd}) {
            buffers output;
            const auto before = output;
            hinted_outline_requirements required{9U, 8U, 7U, 6U, 5U};
            require(get_hinted_batch_outline_requirements(*retained, required, policy, coverage) ==
                hinted_outline_error::none);
            require(required == hinted_outline_requirements{7U, 0U, 4U, 18U, 8U});
            require(write_hinted_batch_outlines(*retained, {output.topology, output.physical},
                output.outlines, output.segments, output.source, output.written, policy, coverage) ==
                hinted_outline_error::none && output.written == required);
            require(bytes(output.outlines) == bytes(reference.outlines) &&
                bytes(output.segments) == bytes(reference.segments) && output.source == reference.source &&
                output.positioned == before.positioned && retained->glyphs == original);
            require(bytes(output.topology) == bytes(reference.topology) && bytes(output.physical) == bytes(reference.physical));
        }
    }
    buffers output;
    const auto before = bytes(output);
    for (unsigned missing = 0U; missing < 5U; ++missing) {
        require(write_hinted_batch_outlines(*retained,
            {std::span(output.topology).first(missing == 0U ? 7U : 16U), std::span(output.physical).first(missing == 1U ? 7U : 16U)},
            std::span(output.outlines).first(missing == 2U ? 3U : 12U),
            std::span(output.segments).first(missing == 3U ? 17U : 64U),
            std::span(output.source).first(missing == 4U ? 6U : 16U), output.written) ==
            hinted_outline_error::insufficient_capacity && bytes(output) == before);
    }
    const auto fail = [&](const hinted_glyph_batch& invalid, hinted_outline_error expected,
            hinted_projection_policy policy = hinted_projection_policy::automatic,
            hinted_outline_coverage coverage = hinted_outline_coverage::strict) {
        hinted_outline_requirements counts{1U, 2U, 3U, 4U, 5U};
        const auto saved = counts;
        require(get_hinted_batch_outline_requirements(invalid, counts, policy, coverage) == expected && counts == saved);
        require(write_hinted_batch_outlines(invalid, {output.topology, output.physical},
            output.outlines, output.segments, output.source, output.written, policy, coverage) == expected && bytes(output) == before);
    };
    fail(*retained, hinted_outline_error::unsupported_policy, static_cast<hinted_projection_policy>(99U));
    fail(*retained, hinted_outline_error::unsupported_policy, hinted_projection_policy::automatic,
        static_cast<hinted_outline_coverage>(99U));
    for (unsigned fault = 0U; fault < 4U; ++fault) {
        auto invalid = *retained;
        if (fault == 0U) invalid.identity.reset();
        if (fault == 1U) invalid.glyphs.back().tags = {3U};
        if (fault == 2U) invalid.glyphs.back().contour_ends = {-1};
        if (fault == 3U) invalid.glyphs.back().outline_flags = 0x200;
        fail(invalid, fault == 0U ? hinted_outline_error::invalid_run :
            fault == 3U ? hinted_outline_error::unsupported_flags : hinted_outline_error::invalid_topology);
    }
    // Output tails cannot overlap retained point capacity or each other, even
    // when the data used for actual writes would be disjoint.
    retained->glyphs[0].points.reserve(32U);
    auto* spare = retained->glyphs[0].points.data() + retained->glyphs[0].points.size();
    require(write_hinted_batch_outlines(*retained, {output.topology, output.physical},
        output.outlines, output.segments, std::span{reinterpret_cast<std::uint32_t*>(spare), 16U}, output.written) ==
        hinted_outline_error::invalid_argument && bytes(output) == before && retained->glyphs == original);
    require(write_hinted_batch_outlines(*retained, {output.topology, output.physical},
        output.outlines, output.segments, std::span{reinterpret_cast<std::uint32_t*>(output.segments.data()), 16U},
        output.written) == hinted_outline_error::invalid_argument && bytes(output) == before);
    auto empty = *retained;
    empty.glyphs.clear();
    require(write_hinted_batch_outlines(empty, {output.topology, output.physical}, output.outlines,
        output.segments, output.source, output.written) == hinted_outline_error::none &&
        output.written == hinted_outline_requirements{});
    output.written = rejected.written;
    require(bytes(output) == before);
}
} // namespace

int main() {
    try {
        binary_contour_metadata();
        original_batch_geometry();
        source_geometry_and_phase();
        original_quadratic_differential();
        mixed_and_closed_cubic_records();
        shaping_input_aliases();
        failures_precede_all_publication();
        explicit_nonzero_vector_coverage();
        explicit_antialiased_vector_coverage();
        std::cout << "{\"sourceIndexedGeometry\":true,\"originalQuadraticWriter\":true,\"exactCubicRecords\":true,\"retainedShapingInputAliases\":true,\"wholeRunAtomicPreflight\":true,\"originalBatchGeometry\":true}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n'; return 1;
    }
}
