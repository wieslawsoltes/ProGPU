#include "../src/Text/Shaping/progpu_native_fallback_marks_internal.hpp"
#include "../src/Text/Shaping/progpu_native_open_type_gpos_internal.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <limits>
#include <source_location>
#include <span>
#include <stdexcept>
#include <string>
#include <vector>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

// ProGPU-authored SFNT bytes and captured-metric controls. These mocked device
// frames do not execute a font interpreter, renderer, GPU or source application.
namespace {
using namespace progpu::native::text;
namespace marks = progpu::native::text::detail;
constexpr auto maximum = std::numeric_limits<std::int32_t>::max();
constexpr auto minimum = std::numeric_limits<std::int32_t>::min();

void require(bool value, std::source_location where = std::source_location::current()) {
    if (!value) throw std::runtime_error("device mark control failed at line " +
        std::to_string(where.line()));
}

void put16(std::span<std::byte> bytes, std::size_t at, std::uint16_t value) {
    bytes[at] = static_cast<std::byte>(value >> 8U);
    bytes[at + 1U] = static_cast<std::byte>(value);
}

void put32(std::span<std::byte> bytes, std::size_t at, std::uint32_t value) {
    put16(bytes, at, static_cast<std::uint16_t>(value >> 16U));
    put16(bytes, at + 2U, static_cast<std::uint16_t>(value));
}

std::vector<std::byte> font_bytes(bool header_only = false, std::uint16_t upem = 1000U) {
    struct table { open_type_tag tag; std::vector<std::byte> bytes; };
    std::vector<table> tables{{open_type_tag::from_chars('h', 'e', 'a', 'd'),
        std::vector<std::byte>(54U)}};
    put16(tables[0].bytes, 18U, upem);
    put16(tables[0].bytes, 50U, 1U);
    if (!header_only) {
        tables.push_back({open_type_tag::from_chars('h', 'h', 'e', 'a'),
            std::vector<std::byte>(36U)});
        put16(tables.back().bytes, 34U, 2U);
        tables.push_back({open_type_tag::from_chars('h', 'm', 't', 'x'),
            std::vector<std::byte>(8U)});
        put16(tables.back().bytes, 0U, 500U);
        put16(tables.back().bytes, 4U, 600U);
        tables.push_back({open_type_tag::from_chars('m', 'a', 'x', 'p'),
            std::vector<std::byte>(6U)});
        put16(tables.back().bytes, 4U, 2U);
        tables.push_back({open_type_tag::from_chars('l', 'o', 'c', 'a'),
            std::vector<std::byte>(12U)});
        put32(tables.back().bytes, 8U, 22U);
        tables.push_back({open_type_tag::from_chars('g', 'l', 'y', 'f'),
            std::vector<std::byte>(22U)});
        auto& glyph = tables.back().bytes;
        put16(glyph, 0U, 1U);
        put16(glyph, 2U, 10U);
        put16(glyph, 6U, 30U);
        put16(glyph, 8U, 40U);
        put16(glyph, 10U, 2U);
        glyph[14U] = std::byte{0x33};
        glyph[15U] = std::byte{0x37};
        glyph[16U] = std::byte{0x26};
        glyph[17U] = std::byte{10};
        glyph[18U] = std::byte{20};
        glyph[19U] = std::byte{5};
        glyph[20U] = std::byte{30};
        glyph[21U] = std::byte{10};
    }
    auto size = 12U + tables.size() * 16U;
    for (const auto& table : tables) size += table.bytes.size();
    std::vector<std::byte> bytes(size);
    put32(bytes, 0U, 0x00010000U);
    put16(bytes, 4U, static_cast<std::uint16_t>(tables.size()));
    auto at = 12U + tables.size() * 16U;
    for (std::size_t index = 0U; index < tables.size(); ++index) {
        put32(bytes, 12U + index * 16U, tables[index].tag.value);
        put32(bytes, 20U + index * 16U, static_cast<std::uint32_t>(at));
        put32(bytes, 24U + index * 16U,
            static_cast<std::uint32_t>(tables[index].bytes.size()));
        std::copy(tables[index].bytes.begin(), tables[index].bytes.end(),
            bytes.begin() + static_cast<std::ptrdiff_t>(at));
        at += tables[index].bytes.size();
    }
    return bytes;
}

struct projection_control final {
    mutable std::size_t calls = 0U;
    mutable std::array<marks::gpos_metric_vector, 2> input{};
    bool reject = false;
    bool cross_axis = false;
    std::int32_t y_factor = 5;
};

bool project_reference(const void* owner,
    const std::array<marks::gpos_metric_vector, 2>& input,
    std::array<marks::gpos_metric_vector, 2>& output) noexcept {
    const auto& control = *static_cast<const projection_control*>(owner);
    ++control.calls;
    control.input = input;
    if (control.reject) return false;
    std::array<marks::gpos_metric_vector, 2> candidate{};
    for (std::size_t index = 0U; index < input.size(); ++index) {
        const auto x = static_cast<std::int64_t>(input[index].x) * 3;
        const auto y = static_cast<std::int64_t>(input[index].y) * control.y_factor;
        if (x < minimum || x > maximum || y < minimum || y > maximum) return false;
        candidate[index] = {static_cast<std::int32_t>(x), static_cast<std::int32_t>(y)};
    }
    if (control.cross_axis) candidate[0].x = 1;
    output = candidate;
    return true;
}

#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
bool project_intrinsic(const void* owner,
    const std::array<marks::gpos_metric_vector, 2>& input,
    std::array<marks::gpos_metric_vector, 2>& output) noexcept {
    const auto& control = *static_cast<const projection_control*>(owner);
    ++control.calls;
    control.input = input;
    if (control.reject) return false;
    // The fixture bounds make overflow impossible for these four independent
    // lanes. Actual native projection has its separate checked-frame controls.
    if (control.y_factor < -5 || control.y_factor > 5) return false;
    const std::array<std::int32_t, 4> lanes{
        input[0].x, input[0].y, input[1].x, input[1].y};
    for (const auto value : lanes)
        if (value < -4096 || value > 4096) return false;
    std::array<std::int32_t, 4> result{};
#if defined(__aarch64__) || defined(_M_ARM64)
    const std::array<std::int32_t, 4> factors{3, control.y_factor, 3, control.y_factor};
    vst1q_s32(result.data(), vmulq_s32(vld1q_s32(lanes.data()),
        vld1q_s32(factors.data())));
#else
    const auto values = _mm_loadu_si128(reinterpret_cast<const __m128i*>(lanes.data()));
    const auto triple = _mm_add_epi32(values, _mm_slli_epi32(values, 1));
    auto y_values = _mm_setzero_si128();
    for (std::int32_t index = 0; index < std::abs(control.y_factor); ++index)
        y_values = _mm_add_epi32(y_values, values);
    if (control.y_factor < 0) y_values = _mm_sub_epi32(_mm_setzero_si128(), y_values);
    const auto x_mask = _mm_set_epi32(0, -1, 0, -1);
    _mm_storeu_si128(reinterpret_cast<__m128i*>(result.data()),
        _mm_or_si128(_mm_and_si128(x_mask, triple),
            _mm_andnot_si128(x_mask, y_values)));
#endif
    output = {{{result[0], result[1]}, {result[2], result[3]}}};
    if (control.cross_axis) output[0].x = 1;
    return true;
}
#endif

struct captured_control final {
    std::array<marks::device_mark_bounds, 4> bounds{{
        {27, 90000, 11000, -70000, 120000},
        {10000, 9000, 20000, -8000, 42000},
        {12000, 12000, 26000, -12000, 55000},
        {100, 100, 200, -50, 77}}};
    std::array<std::uint32_t, 4> glyph_ids{1U, 1U, 1U, 1U};
    mutable std::array<std::size_t, 4> indices{};
    mutable std::size_t calls = 0U;
    std::size_t reject_index = 99U;
    std::size_t absent_index = 99U;
};

bool captured_extents(const void* owner, std::size_t index, std::uint32_t glyph,
    marks::device_mark_bounds& output, bool& found) noexcept {
    const auto& control = *static_cast<const captured_control*>(owner);
    if (control.calls < control.indices.size()) control.indices[control.calls] = index;
    ++control.calls;
    if (index >= control.bounds.size() || glyph != control.glyph_ids[index] ||
        index == control.reject_index) return false;
    found = index != control.absent_index;
    if (found) output = control.bounds[index];
    return true;
}

marks::gpos_device_frame frame_for(const sfnt_font_view& font,
    projection_control& projection, marks::gpos_arithmetic_path path) {
    marks::gpos_device_frame frame{};
    frame.font = &font;
    frame.owner = &projection;
    frame.arithmetic_path = path;
    frame.project_design = &project_reference;
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
    if (path == marks::gpos_arithmetic_path::intrinsic_simd)
        frame.project_design = &project_intrinsic;
#endif
    // Fractional retained scales may have rounded integer ppem zero. This
    // private path needs captured extents, not contour-point or Device tables.
    return frame;
}

std::array<shaping_glyph, 3> initial() {
    return {{{1U, 'A', 0, {}, 130000, 130, 300, 500},
        {1U, 0x0301U, 1, {}, 211, 223, 701, 703},
        {1U, 0x0301U, 2, {}, 227, 229, 709, 719}}};
}

void use_small_metrics(captured_control& control) {
    control.bounds[0] = {500, 600, 400, -500, 1000};
    control.bounds[1] = {100, 100, 200, -50, 77};
    control.bounds[2] = control.bounds[1];
}

void use_small_positions(std::array<shaping_glyph, 3>& glyphs) {
    glyphs[0].advance_x = 1000;
    glyphs[0].advance_y = glyphs[0].offset_x = glyphs[0].offset_y = 0;
}

template<std::size_t Count>
bool same(const std::array<shaping_glyph, Count>& left,
    const std::array<shaping_glyph, Count>& right) {
    return std::memcmp(left.data(), right.data(), sizeof(left)) == 0;
}

bool same_metrics(const shaping_glyph& left, const shaping_glyph& right) {
    return left.advance_x == right.advance_x && left.advance_y == right.advance_y &&
        left.offset_x == right.offset_x && left.offset_y == right.offset_y;
}

bool dependent(const shaping_glyph& glyph) {
    constexpr auto bits = static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    return (static_cast<std::uint32_t>(glyph.flags) & bits) == bits;
}

bool apply(const sfnt_font_view& font, std::span<shaping_glyph> glyphs,
    std::span<const shaping_attachment> metadata, captured_control& captured,
    projection_control& projection, marks::gpos_arithmetic_path path,
    shaping_direction direction = shaping_direction::left_to_right,
    font_error* error = nullptr) {
    return marks::try_apply_device_fallback_mark_positioning_from_attachments(
        font, glyphs, direction, metadata, {}, nullptr,
        frame_for(font, projection, path), {&captured, &captured_extents}, error);
}

void verify_geometry(const sfnt_font_view& font, marks::gpos_arithmetic_path path) {
    for (const auto direction : {shaping_direction::left_to_right,
        shaping_direction::right_to_left, shaping_direction::top_to_bottom,
        shaping_direction::bottom_to_top}) {
        captured_control captured{};
        projection_control projection{};
        auto glyphs = initial();
        const auto before = glyphs;
        const std::array<shaping_attachment, 3> metadata{};
        font_error error = font_error::invalid_face;
        require(apply(font, glyphs, metadata, captured, projection, path, direction, &error));
        const bool forward = direction == shaping_direction::left_to_right ||
            direction == shaping_direction::top_to_bottom;
        require(error == font_error::none && same_metrics(glyphs[0], before[0]));
        require(glyphs[1].offset_x == (forward ? -90000 : 40000));
        require(glyphs[2].offset_x == (forward ? -95000 : 35000));
        require(glyphs[1].offset_y == (forward ? 89680 : 89810));
        require(glyphs[2].offset_y == (forward ? 98990 : 99120));
        require(glyphs[1].advance_x == 0 && glyphs[1].advance_y == 0 &&
            glyphs[2].advance_x == 0 && glyphs[2].advance_y == 0);
        require(dependent(glyphs[1]) && dependent(glyphs[2]));
        require(captured.calls == 3U && captured.indices[0] == 0U &&
            captured.indices[1] == 1U && captured.indices[2] == 2U);
        require(projection.calls == 1U && projection.input[0].x == 0 &&
            projection.input[0].y == 62 && projection.input[1].x == 0 &&
            projection.input[1].y == 0);
    }
    struct known_mark { std::uint32_t code; std::int32_t x, y; };
    // Independently hand-computed original recategorization and geometry roles.
    constexpr std::array known{
        known_mark{0x0301U, -700, 860}, known_mark{0x0316U, -700, -310},
        known_mark{0x0315U, -300, 860}, known_mark{0x0321U, -700, 0},
        known_mark{0x031BU, -300, 550}, known_mark{0x035CU, -200, -310},
        known_mark{0x035DU, -200, 860}, known_mark{0x0345U, -700, 0},
        known_mark{0x0E31U, -300, 860}, known_mark{0x05B9U, -1100, 860},
        known_mark{0x05BFU, -700, 550}, known_mark{0x05B1U, -700, -310}};
    for (const auto mark : known) {
        captured_control captured{};
        projection_control projection{};
        use_small_metrics(captured);
        auto glyphs = initial();
        use_small_positions(glyphs);
        glyphs[1].code_point = mark.code;
        const std::array<shaping_attachment, 2> metadata{};
        require(apply(font, std::span{glyphs}.first(2), metadata, captured,
            projection, path));
        require(glyphs[1].offset_x == mark.x && glyphs[1].offset_y == mark.y);
    }
    for (const auto factor : {0, -5}) {
        captured_control captured{};
        projection_control projection{};
        projection.y_factor = factor;
        use_small_metrics(captured);
        auto glyphs = initial();
        use_small_positions(glyphs);
        const std::array<shaping_attachment, 2> metadata{};
        require(apply(font, std::span{glyphs}.first(2), metadata, captured,
            projection, path));
        require(glyphs[1].offset_y == (factor == 0 ? 275 : 120));
    }
}

void verify_walk(const sfnt_font_view& font, marks::gpos_arithmetic_path path) {
    for (const auto direction : {shaping_direction::left_to_right, shaping_direction::right_to_left}) {
        captured_control captured{};
        projection_control projection{};
        use_small_metrics(captured);
        auto glyphs = initial();
        use_small_positions(glyphs);
        std::array<shaping_attachment, 3> metadata{};
        metadata[0].reserved0 = 2U;
        metadata[1].reserved1 = 0U;
        metadata[2].reserved1 = 0xFFU;
        require(apply(font, glyphs, metadata, captured, projection, path, direction));
        require(glyphs[1].offset_x == (direction == shaping_direction::left_to_right ? -950 : 550));
        require(glyphs[2].offset_x == (direction == shaping_direction::left_to_right ? -450 : 50));
        require(glyphs[1].offset_y == 860 && glyphs[2].offset_y == 860);
    }
    {
        captured_control captured{};
        projection_control projection{};
        use_small_metrics(captured);
        auto glyphs = initial();
        use_small_positions(glyphs);
        glyphs[1].code_point = 0x0316U;
        const std::array<shaping_attachment, 3> metadata{};
        require(apply(font, glyphs, metadata, captured, projection, path));
        require(glyphs[1].offset_y == -310 && glyphs[2].offset_y == 860);
    }
    {
        captured_control captured{};
        projection_control projection{};
        use_small_metrics(captured);
        auto glyphs = initial();
        use_small_positions(glyphs);
        glyphs[1].code_point = 0x200DU;
        glyphs[1].advance_x = 120;
        glyphs[1].advance_y = -20;
        const auto before = glyphs;
        const std::array<shaping_attachment, 3> metadata{};
        require(apply(font, glyphs, metadata, captured, projection, path));
        require(same_metrics(glyphs[1], before[1]) && glyphs[2].offset_x == -820 &&
            glyphs[2].offset_y == 880 && captured.calls == 2U && captured.indices[1] == 2U);
    }
    {
        captured_control captured{};
        projection_control projection{};
        auto glyphs = initial();
        const auto before = glyphs;
        std::array<shaping_attachment, 3> metadata{};
        metadata[1].reserved2 = 7U;
        require(apply(font, glyphs, metadata, captured, projection, path));
        require(same_metrics(glyphs[1], before[1]) && glyphs[2].offset_y == 90680 &&
            captured.calls == 2U && captured.indices[1] == 2U);
    }
    for (const auto absent : {0U, 1U}) {
        captured_control captured{};
        projection_control projection{};
        captured.absent_index = absent;
        auto glyphs = initial();
        const auto before = glyphs;
        const std::array<shaping_attachment, 3> metadata{};
        require(apply(font, glyphs, metadata, captured, projection, path));
        if (absent == 0U) require(same(glyphs, before) && captured.calls == 1U);
        else require(glyphs[1].offset_x == -129299 && glyphs[1].offset_y == 573 &&
            glyphs[1].advance_x == 0 && glyphs[2].offset_y == 90680);
    }
    {
        // Leading marks do not acquire a fabricated base. The actual base and
        // mark retain source descriptor indices 2 and 3 despite repeated IDs.
        captured_control captured{};
        projection_control projection{};
        captured.bounds[2] = {500, 600, 400, -500, 1000};
        std::array<shaping_glyph, 4> glyphs{{
            {1U, 0x0301U, 0, {}, 1, 2, 3, 4}, {1U, 0x0301U, 1, {}, 5, 6, 7, 8},
            {1U, 'A', 2, {}, 1000, 0, 0, 0}, {1U, 0x0301U, 3, {}, 9, 10, 11, 12}}};
        const auto before = glyphs;
        const std::array<shaping_attachment, 4> metadata{};
        require(apply(font, glyphs, metadata, captured, projection, path));
        require(same_metrics(glyphs[0], before[0]) && same_metrics(glyphs[1], before[1]));
        require(captured.calls == 2U && captured.indices[0] == 2U &&
            captured.indices[1] == 3U && glyphs[3].offset_x == -700 && glyphs[3].offset_y == 860);
    }
}

void verify_checked_publication(const sfnt_font_view& font, marks::gpos_arithmetic_path path) {
    const std::array<shaping_attachment, 3> metadata{};
    {
        captured_control captured{};
        projection_control projection{};
        use_small_metrics(captured);
        auto glyphs = initial();
        use_small_positions(glyphs);
        glyphs[0].advance_x = -1000;
        captured.bounds[2].x_bearing = minimum;
        const auto before = glyphs;
        font_error error = font_error::none;
        require(!apply(font, glyphs, metadata, captured, projection, path,
            shaping_direction::left_to_right, &error));
        require(error == font_error::invalid_face && glyphs[1].offset_x == 1300 &&
            glyphs[1].offset_y == 860 && glyphs[1].advance_x == 0);
        require(same_metrics(glyphs[2], before[2]) && dependent(glyphs[2]));
    }
    for (const auto failure : {0U, 1U, 2U, 3U, 4U}) {
        captured_control captured{};
        projection_control projection{};
        use_small_metrics(captured);
        auto glyphs = initial();
        use_small_positions(glyphs);
        if (failure == 0U) { glyphs[0].advance_x = maximum; captured.bounds[1].x_bearing = maximum; }
        if (failure == 1U) captured.bounds[0].y_bearing = maximum;
        if (failure == 2U) { captured.bounds[0].y_bearing = maximum; glyphs[0].offset_y = 1; }
        if (failure == 3U) captured.bounds[1].width = -1;
        if (failure == 4U) captured.bounds[1].negative_height = 1;
        const auto before = glyphs;
        require(!apply(font, glyphs, metadata, captured, projection, path));
        require(same_metrics(glyphs[1], before[1]) && same_metrics(glyphs[2], before[2]));
    }
    {
        captured_control captured{};
        projection_control projection{};
        use_small_metrics(captured);
        captured.reject_index = 2U;
        auto glyphs = initial();
        use_small_positions(glyphs);
        const auto before = glyphs;
        require(!apply(font, glyphs, metadata, captured, projection, path));
        require(glyphs[1].offset_x == -700 && glyphs[1].offset_y == 860 &&
            same_metrics(glyphs[2], before[2]));
    }
    {
        // An intermediate X beyond signed32 cancels with the device pen. Only
        // the final metric publication is signed32, never int16-clamped.
        captured_control captured{};
        projection_control projection{};
        captured.bounds[0] = {0, 0, 0, 0, 0};
        captured.bounds[1] = {minimum, 0, 0, 0, 0};
        auto glyphs = initial();
        use_small_positions(glyphs);
        glyphs[0].advance_x = maximum;
        glyphs[1].code_point = 0x0345U;
        require(apply(font, std::span{glyphs}.first(2), std::span{metadata}.first(2),
            captured, projection, path));
        require(glyphs[1].offset_x == 1 && glyphs[1].offset_y == 0);
    }
    {
        // The original component product is widened in device mode. Its
        // mathematical partition retains truncation toward zero and ordering.
        captured_control captured{};
        projection_control projection{};
        captured.bounds[0] = {0, 0, 1, 0, maximum};
        captured.bounds[1] = {0, 0, 1, 0, 0};
        auto glyphs = initial();
        use_small_positions(glyphs);
        glyphs[0].advance_x = maximum;
        glyphs[1].code_point = 0x0345U;
        std::array<shaping_attachment, 2> components{};
        components[0].reserved0 = 255U;
        components[1].reserved1 = 254U;
        require(apply(font, std::span{glyphs}.first(2), components,
            captured, projection, path));
        const auto origin = static_cast<std::int64_t>(254) * maximum / 255;
        const auto width = maximum / 255;
        require(glyphs[1].offset_x == origin + (width - 1) / 2 - maximum);
    }
}

void verify_admission(const sfnt_font_view& font, marks::gpos_arithmetic_path path) {
    for (const auto failure : {0U, 1U, 2U, 3U, 4U, 5U, 6U, 7U, 8U, 9U, 10U, 11U}) {
        captured_control captured{};
        projection_control projection{};
        auto frame = frame_for(font, projection, path);
        marks::device_mark_extents provider{&captured, &captured_extents};
        auto glyphs = initial();
        std::array<shaping_attachment, 3> metadata{};
        std::span<const shaping_attachment> selected = metadata;
        auto direction = shaping_direction::left_to_right;
        const std::array<std::int16_t, 1> coordinates{77};
        const std::array<std::int16_t, 1> other_coordinates{78};
        std::span<const std::int16_t> normalized{};
        sfnt_font_view different_font = font;
        if (failure == 0U) frame.font = &different_font;
        if (failure == 1U) frame.owner = nullptr;
        if (failure == 2U) frame.project_design = nullptr;
        if (failure == 3U) provider.owner = nullptr;
        if (failure == 4U) provider.try_get_extents = nullptr;
        if (failure == 5U) selected = selected.first(2);
        if (failure == 6U) direction = shaping_direction::unspecified;
        if (failure == 7U) direction = static_cast<shaping_direction>(99U);
        if (failure == 8U) frame.arithmetic_path = static_cast<marks::gpos_arithmetic_path>(99U);
        if (failure == 9U) normalized = coordinates;
        if (failure == 10U) { normalized = coordinates; frame.normalized_coordinates = other_coordinates; }
        if (failure == 11U) glyphs[2].glyph_id = 0x10000U;
        const auto before = glyphs;
        font_error error = font_error::none;
        require(!marks::try_apply_device_fallback_mark_positioning_from_attachments(
            font, glyphs, direction, selected, normalized, nullptr, frame, provider, &error));
        require(error == (failure == 11U ? font_error::invalid_glyph : font_error::invalid_argument));
        require(same(glyphs, before) && captured.calls == 0U && projection.calls == 0U);
    }
    for (const auto cross_axis : {false, true}) {
        captured_control captured{};
        projection_control projection{};
        projection.reject = !cross_axis;
        projection.cross_axis = cross_axis;
        auto glyphs = initial();
        const auto before = glyphs;
        const std::array<shaping_attachment, 3> metadata{};
        require(!apply(font, glyphs, metadata, captured, projection, path));
        require(same(glyphs, before) && projection.calls == 1U && captured.calls == 0U);
    }
    {
        captured_control captured{};
        projection_control projection{};
        auto frame = frame_for(font, projection, path);
        alignas(std::int16_t) std::array<std::byte, 4> unaligned{};
        const std::span<const std::int16_t> coordinates{
            reinterpret_cast<const std::int16_t*>(unaligned.data() + 1U), 1U};
        frame.normalized_coordinates = coordinates;
        auto glyphs = initial();
        const auto before = glyphs;
        const std::array<shaping_attachment, 3> metadata{};
        require(!marks::try_apply_device_fallback_mark_positioning_from_attachments(
            font, glyphs, shaping_direction::left_to_right, metadata, coordinates,
            nullptr, frame, {&captured, &captured_extents}, nullptr));
        require(same(glyphs, before) && captured.calls == 0U && projection.calls == 0U);
    }
    {
        captured_control captured{};
        projection_control projection{};
        captured.glyph_ids[1] = 2U;
        auto glyphs = initial();
        const auto before = glyphs;
        const std::array<shaping_attachment, 3> metadata{};
        require(!apply(font, glyphs, metadata, captured, projection, path));
        require(same_metrics(glyphs[1], before[1]) &&
            same_metrics(glyphs[2], before[2]) && captured.calls == 2U);
    }
    {
        // Equal original normalized coordinates may occupy different borrowed
        // spans, and the provider wrapper owner may differ from the frame owner.
        captured_control captured{};
        projection_control projection{};
        auto frame = frame_for(font, projection, path);
        const std::array<std::int16_t, 2> coordinates{77, -91};
        const auto same_coordinates = coordinates;
        frame.normalized_coordinates = same_coordinates;
        auto glyphs = initial();
        const std::array<shaping_attachment, 3> metadata{};
        fallback_mark_positioning_scratch scratch{};
        require(marks::try_apply_device_fallback_mark_positioning_from_attachments(
            font, glyphs, shaping_direction::left_to_right, metadata, coordinates,
            &scratch, frame, {&captured, &captured_extents}, nullptr));
        require(glyphs[1].offset_x == -90000 && glyphs[1].offset_y == 89680);
    }
}

void verify_original_design(const sfnt_font_view& font) {
    std::array<shaping_glyph, 3> glyphs{{
        {1U, 'A', 0, {}, 600, 0, 0, 0},
        {1U, 0x0301U, 1, {}, 600, 0, 0, 0},
        {1U, 0x0301U, 2, {}, 600, 0, 0, 0}}};
    auto retained = glyphs;
    const std::array<shaping_attachment, 3> attachments{};
    require(try_apply_fallback_mark_positioning(font, glyphs,
        shaping_direction::left_to_right, {}, {}, nullptr));
    require(marks::try_apply_fallback_mark_positioning_from_attachments(font,
        retained, shaping_direction::left_to_right, attachments, {}, nullptr, nullptr));
    require(same(glyphs, retained) && glyphs[1].offset_x == -320 &&
        glyphs[1].offset_y == 102 && glyphs[2].offset_y == 204);
    glyphs[0].advance_x = maximum;
    require(try_apply_fallback_mark_positioning(font, glyphs,
        shaping_direction::left_to_right, {}, {}, nullptr));
    require(glyphs[1].offset_x == -32768 && glyphs[2].offset_x == -32768);
}
} // namespace

int main() {
    try {
        const auto bytes = font_bytes();
        sfnt_font_view font{};
        require(sfnt_font_view::try_create(bytes, 0U, font));
        verify_original_design(font);
        // A header-only font proves the device route obtains extents and base
        // advance from the captured generation, not design hmtx/glyf lookups.
        const auto device_bytes = font_bytes(true);
        sfnt_font_view device_font{};
        require(sfnt_font_view::try_create(device_bytes, 0U, device_font));
        for (const auto path : {marks::gpos_arithmetic_path::scalar_reference,
            marks::gpos_arithmetic_path::intrinsic_simd}) {
#if !defined(__aarch64__) && !defined(_M_ARM64) && !defined(__SSE2__) && !defined(_M_X64)
            if (path == marks::gpos_arithmetic_path::intrinsic_simd) continue;
#endif
            verify_geometry(device_font, path);
            verify_walk(device_font, path);
            verify_checked_publication(device_font, path);
            verify_admission(device_font, path);
            const auto zero_upem_bytes = font_bytes(true, 0U);
            sfnt_font_view zero_upem_font{};
            require(sfnt_font_view::try_create(zero_upem_bytes, 0U, zero_upem_font));
            captured_control captured{};
            projection_control projection{};
            auto glyphs = initial();
            const auto before = glyphs;
            const std::array<shaping_attachment, 3> attachments{};
            font_error error = font_error::none;
            require(!apply(zero_upem_font, glyphs, attachments, captured,
                projection, path, shaping_direction::left_to_right, &error));
            require(error == font_error::invalid_face && same(glyphs, before) &&
                captured.calls == 0U && projection.calls == 0U);
        }
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
        auto scalar = initial();
        auto intrinsic = scalar;
        captured_control scalar_extents{}, intrinsic_extents{};
        projection_control scalar_projection{}, intrinsic_projection{};
        const std::array<shaping_attachment, 3> metadata{};
        require(apply(device_font, scalar, metadata, scalar_extents, scalar_projection,
            marks::gpos_arithmetic_path::scalar_reference));
        require(apply(device_font, intrinsic, metadata, intrinsic_extents, intrinsic_projection,
            marks::gpos_arithmetic_path::intrinsic_simd));
        require(same(scalar, intrinsic));
#endif
        std::cout << "Device-frame fallback mark mocked geometry controls passed\n";
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
    return 0;
}
