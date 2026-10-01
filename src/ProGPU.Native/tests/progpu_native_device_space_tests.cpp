#include "../src/Text/Shaping/progpu_native_space_fallback_internal.hpp"
#include "../src/Text/Shaping/progpu_native_open_type_gpos_internal.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <limits>
#include <span>
#include <stdexcept>
#include <utility>
#include <vector>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

// Original ProGPU-authored raw SFNT and independently specified metric
// controls. No interpreter, source host, renderer or Display admission runs.
namespace {
using namespace progpu::native::text;
namespace device_space = progpu::native::text::detail;
using vector = device_space::gpos_metric_vector;
using path = device_space::gpos_arithmetic_path;
using mapping = std::pair<std::uint32_t, std::uint32_t>;

void require(bool condition) {
    if (!condition) throw std::runtime_error("device special-space control failed");
}

void put_u16(std::span<std::byte> bytes, std::size_t at, std::uint16_t value) {
    bytes[at] = static_cast<std::byte>(value >> 8U);
    bytes[at + 1U] = static_cast<std::byte>(value);
}

void put_u32(std::span<std::byte> bytes, std::size_t at, std::uint32_t value) {
    put_u16(bytes, at, static_cast<std::uint16_t>(value >> 16U));
    put_u16(bytes, at + 2U, static_cast<std::uint16_t>(value));
}

std::vector<std::byte> font_bytes(std::span<const mapping> mappings,
    bool include_head = true, bool include_cmap = true) {
    struct table final { open_type_tag tag; std::vector<std::byte> bytes; };
    std::vector<table> tables{};
    if (include_head) {
        table head{open_type_tag::from_chars('h', 'e', 'a', 'd'),
            std::vector<std::byte>(54U)};
        put_u16(head.bytes, 18U, 1001U);
        tables.push_back(std::move(head));
    }
    table hhea{open_type_tag::from_chars('h', 'h', 'e', 'a'),
        std::vector<std::byte>(36U)};
    put_u16(hhea.bytes, 34U, 16U);
    tables.push_back(std::move(hhea));
    table hmtx{open_type_tag::from_chars('h', 'm', 't', 'x'),
        std::vector<std::byte>(64U)};
    for (std::uint16_t index = 0U; index < 16U; ++index)
        put_u16(hmtx.bytes, static_cast<std::size_t>(index) * 4U,
            static_cast<std::uint16_t>(600U + index));
    tables.push_back(std::move(hmtx));
    table maxp{open_type_tag::from_chars('m', 'a', 'x', 'p'),
        std::vector<std::byte>(6U)};
    put_u16(maxp.bytes, 4U, 16U);
    tables.push_back(std::move(maxp));
    if (include_cmap) {
        table cmap{open_type_tag::from_chars('c', 'm', 'a', 'p'),
            std::vector<std::byte>(28U + mappings.size() * 12U)};
        put_u16(cmap.bytes, 2U, 1U);
        put_u16(cmap.bytes, 4U, 3U);
        put_u16(cmap.bytes, 6U, 10U);
        put_u32(cmap.bytes, 8U, 12U);
        put_u16(cmap.bytes, 12U, 12U);
        put_u32(cmap.bytes, 16U, static_cast<std::uint32_t>(cmap.bytes.size() - 12U));
        put_u32(cmap.bytes, 24U, static_cast<std::uint32_t>(mappings.size()));
        for (std::size_t index = 0U; index < mappings.size(); ++index) {
            const auto at = 28U + index * 12U;
            put_u32(cmap.bytes, at, mappings[index].first);
            put_u32(cmap.bytes, at + 4U, mappings[index].first);
            put_u32(cmap.bytes, at + 8U, mappings[index].second);
        }
        tables.push_back(std::move(cmap));
    }
    std::size_t length = 12U + tables.size() * 16U;
    for (const auto& table : tables) length += table.bytes.size();
    std::vector<std::byte> bytes(length);
    put_u32(bytes, 0U, 0x00010000U);
    put_u16(bytes, 4U, static_cast<std::uint16_t>(tables.size()));
    auto offset = 12U + tables.size() * 16U;
    for (std::size_t index = 0U; index < tables.size(); ++index) {
        const auto record = 12U + index * 16U;
        put_u32(bytes, record, tables[index].tag.value);
        put_u32(bytes, record + 8U, static_cast<std::uint32_t>(offset));
        put_u32(bytes, record + 12U, static_cast<std::uint32_t>(tables[index].bytes.size()));
        std::copy(tables[index].bytes.begin(), tables[index].bytes.end(),
            bytes.begin() + static_cast<std::ptrdiff_t>(offset));
        offset += tables[index].bytes.size();
    }
    return bytes;
}

constexpr std::array mappings{mapping{0x20U, 1U}, mapping{0x2CU, 3U},
    mapping{0x2EU, 2U}, mapping{0x30U, 4U}, mapping{0x31U, 5U},
    mapping{0x32U, 6U}, mapping{0x33U, 7U}, mapping{0x34U, 8U},
    mapping{0x35U, 9U}, mapping{0x36U, 10U}, mapping{0x37U, 11U},
    mapping{0x38U, 12U}, mapping{0x39U, 13U}};

enum class projection_result { scaled, minimum, maximum, failure };
struct projection_control final {
    mutable std::size_t calls = 0U;
    mutable std::array<vector, 2> input{};
    projection_result result = projection_result::scaled;
};

bool begin_projection(const projection_control& control,
    const std::array<vector, 2>& input, std::array<vector, 2>& output,
    bool& finished) noexcept {
    ++control.calls;
    control.input = input;
    finished = control.result != projection_result::scaled;
    if (finished) {
        const auto value = control.result == projection_result::minimum
            ? std::numeric_limits<std::int32_t>::min()
            : std::numeric_limits<std::int32_t>::max();
        output = {{{value, value}, {701, 703}}};
    }
    return control.result != projection_result::failure;
}

bool project_reference(const void* owner, const std::array<vector, 2>& input,
    std::array<vector, 2>& output) noexcept {
    const auto& control = *static_cast<const projection_control*>(owner);
    bool finished = false;
    if (!begin_projection(control, input, output, finished)) return false;
    if (finished) return true;
    std::array<vector, 2> candidate{};
    for (std::size_t index = 0U; index < input.size(); ++index) {
        const auto x = static_cast<std::int64_t>(input[index].x) * 3;
        const auto y = static_cast<std::int64_t>(input[index].y) * 5;
        if (x < std::numeric_limits<std::int32_t>::min() ||
            x > std::numeric_limits<std::int32_t>::max() ||
            y < std::numeric_limits<std::int32_t>::min() ||
            y > std::numeric_limits<std::int32_t>::max()) return false;
        candidate[index] = {static_cast<std::int32_t>(x), static_cast<std::int32_t>(y)};
    }
    output = candidate;
    return true;
}

#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
bool project_intrinsic(const void* owner, const std::array<vector, 2>& input,
    std::array<vector, 2>& output) noexcept {
    const auto& control = *static_cast<const projection_control*>(owner);
    bool finished = false;
    if (!begin_projection(control, input, output, finished)) return false;
    if (finished) return true;
    const std::array<std::int32_t, 4> packed{
        input[0].x, input[0].y, input[1].x, input[1].y};
    for (const auto value : packed)
        if (value < -65535 || value > 65535) return false;
    std::array<std::int32_t, 4> scaled{};
#if defined(__aarch64__) || defined(_M_ARM64)
    const std::array<std::int32_t, 4> factors{3, 5, 3, 5};
    vst1q_s32(scaled.data(), vmulq_s32(vld1q_s32(packed.data()),
        vld1q_s32(factors.data())));
#else
    const auto lanes = _mm_loadu_si128(reinterpret_cast<const __m128i*>(packed.data()));
    const auto triple = _mm_add_epi32(lanes, _mm_slli_epi32(lanes, 1));
    const auto quintuple = _mm_add_epi32(lanes, _mm_slli_epi32(lanes, 2));
    const auto x_mask = _mm_set_epi32(0, -1, 0, -1);
    _mm_storeu_si128(reinterpret_cast<__m128i*>(scaled.data()),
        _mm_or_si128(_mm_and_si128(x_mask, triple),
            _mm_andnot_si128(x_mask, quintuple)));
#endif
    output = {{{scaled[0], scaled[1]}, {scaled[2], scaled[3]}}};
    return true;
}
#endif

device_space::gpos_device_frame frame_for(const sfnt_font_view& font,
    projection_control& control, path selected) {
    device_space::gpos_device_frame frame{};
    frame.font = &font;
    frame.owner = &control;
    frame.arithmetic_path = selected;
    frame.project_design = &project_reference;
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
    if (selected == path::intrinsic_simd) frame.project_design = &project_intrinsic;
#endif
    return frame;
}

struct advance_control final {
    mutable std::size_t calls = 0U, count = 0U;
    mutable device_space::device_space_advance_kind kind = device_space::device_space_advance_kind::figure;
    mutable std::array<std::uint16_t, 10> glyphs{};
    std::array<vector, 16> metrics{};
    vector punctuation_bias{};
    bool fail = false;
    advance_control() {
        // Deliberately different from design widths and projected design widths.
        for (std::size_t index = 0U; index < metrics.size(); ++index)
            metrics[index] = {static_cast<std::int32_t>(70001U + index * 7U),
                static_cast<std::int32_t>(90003U + index * 11U)};
    }
};

bool get_advances(const void* owner, device_space::device_space_advance_kind kind,
    std::span<const std::uint16_t> glyphs,
    std::span<vector> output) noexcept {
    const auto& control = *static_cast<const advance_control*>(owner);
    ++control.calls;
    control.kind = kind;
    control.count = glyphs.size();
    if (glyphs.size() > control.glyphs.size() || output.size() != glyphs.size()) return false;
    std::copy(glyphs.begin(), glyphs.end(), control.glyphs.begin());
    for (std::size_t index = 0U; index < glyphs.size(); ++index) {
        if (glyphs[index] >= control.metrics.size()) return false;
        output[index] = control.metrics[glyphs[index]];
        if (kind == device_space::device_space_advance_kind::punctuation) {
            output[index].x += control.punctuation_bias.x;
            output[index].y += control.punctuation_bias.y;
        }
    }
    return !control.fail;
}

shaping_glyph initial(std::uint32_t code_point) {
    return {1U, code_point, 17, shaping_glyph_flags::unsafe_to_concat,
        100001, -120003, 71007, -81009};
}

bool same(const shaping_glyph& left, const shaping_glyph& right) {
    return std::memcmp(&left, &right, sizeof(left)) == 0;
}

bool apply(const sfnt_font_view& font, shaping_glyph& glyph,
    const device_space::gpos_device_frame& frame, advance_control& advances,
    shaping_direction direction = shaping_direction::left_to_right,
    font_error* error = nullptr) {
    return device_space::try_apply_device_space_fallback(font, direction, {},
        glyph, frame, {&advances, &get_advances}, error);
}

void verify_families(path selected) {
    const auto bytes = font_bytes(mappings);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(bytes, 0U, font));
    constexpr std::array points{0x00A0U, 0x2000U, 0x2001U, 0x2002U, 0x2003U,
        0x2004U, 0x2005U, 0x2006U, 0x2007U, 0x2008U, 0x2009U, 0x200AU,
        0x202FU, 0x205FU, 0x3000U};
    // UPM=1001 distinguishes integer rounding from device-em division.
    constexpr std::array design{0, 501, 1001, 501, 1001, 334, 250, 167,
        0, 0, 200, 63, 0, 222, 1001};
    for (const auto direction : {shaping_direction::left_to_right,
        shaping_direction::right_to_left, shaping_direction::top_to_bottom,
        shaping_direction::bottom_to_top}) {
        const bool vertical = direction == shaping_direction::top_to_bottom ||
            direction == shaping_direction::bottom_to_top;
        for (std::size_t index = 0U; index < points.size(); ++index) {
            projection_control projection{};
            advance_control advances{};
            const auto frame = frame_for(font, projection, selected);
            std::array glyphs{initial(points[index]), initial('A'), initial('Z')};
            const auto before = glyphs;
            auto expected = before[0];
            auto width = vertical ? expected.advance_y : expected.advance_x;
            if (design[index] != 0) width = design[index] * (vertical ? -5 : 3);
            else if (points[index] == 0x2007U)
                width = vertical ? -advances.metrics[4].y : advances.metrics[4].x;
            else if (points[index] == 0x2008U)
                width = vertical ? -advances.metrics[2].y : advances.metrics[2].x;
            else if (points[index] == 0x202FU) width /= 2;
            if (vertical) expected.advance_y = width;
            else expected.advance_x = width;
            font_error error = font_error::verification_failed;
            require(apply(font, glyphs[0], frame, advances, direction, &error));
            require(error == font_error::none && same(glyphs[0], expected) &&
                same(glyphs[1], before[1]) && same(glyphs[2], before[2]));
            require(projection.calls == (design[index] != 0 ? 1U : 0U));
            if (design[index] != 0) {
                require(projection.input[0].x == (vertical ? 0 : design[index]) &&
                    projection.input[0].y == (vertical ? -design[index] : 0) &&
                    projection.input[1].x == 0 && projection.input[1].y == 0);
                auto legacy = before[0];
                require(device_space::try_apply_space_fallback(font, direction, {}, nullptr,
                    legacy, &error));
                require((vertical ? legacy.advance_y : legacy.advance_x) ==
                    (vertical ? -design[index] : design[index]));
            }
            if (points[index] == 0x2007U) {
                require(advances.calls == 1U && advances.count == 1U &&
                    advances.glyphs[0] == 4U);
                require(advances.kind == device_space::device_space_advance_kind::figure);
            } else if (points[index] == 0x2008U) {
                require(advances.calls == 1U && advances.count == 1U &&
                    advances.glyphs[0] == 2U);
                require(advances.kind == device_space::device_space_advance_kind::punctuation);
            } else require(advances.calls == 0U);
        }
    }
    // The design helper retains its original signed16 clamp and width policy.
    auto legacy = initial(0x00A0U);
    require(device_space::try_apply_space_fallback(font, shaping_direction::left_to_right,
        {}, nullptr, legacy, nullptr));
    require(legacy.advance_x == std::numeric_limits<std::int16_t>::max());
    legacy = initial(0x2007U);
    require(device_space::try_apply_space_fallback(font, shaping_direction::left_to_right,
        {}, nullptr, legacy, nullptr) && legacy.advance_x == 604);
}

void verify_mapping_and_noops(path selected) {
    for (const auto code_point : {0x20U, 0x2007U, 0x2008U, 0x2004U, 0x202FU, 0x41U}) {
        for (const bool original_present : {false, true}) {
            const std::array original{mapping{0x20U, 1U}, mapping{code_point, 14U}};
            const std::array no_space{mapping{0x41U, 14U}};
            // Avoid duplicate cmap keys for ordinary U+0020.
            const auto bytes = font_bytes(original_present ?
                (code_point == 0x20U ? std::span<const mapping>(mappings).first(1U) :
                    std::span<const mapping>(original)) : std::span<const mapping>(no_space));
            sfnt_font_view font{};
            require(sfnt_font_view::try_create(bytes, 0U, font));
            projection_control projection{};
            advance_control advances{};
            auto glyph = initial(code_point);
            const auto before = glyph;
            require(apply(font, glyph, frame_for(font, projection, selected), advances) &&
                same(glyph, before) && projection.calls == 0U && advances.calls == 0U);
        }
    }
    for (const auto point : {0x2007U, 0x2008U}) {
        const std::array space_only{mapping{0x20U, 1U}};
        const std::array sparse{mapping{0x20U, 1U}, mapping{0x2CU, 3U},
            mapping{0x33U, 7U}, mapping{0x39U, 13U}};
        for (const bool candidates_present : {false, true}) {
            const auto bytes = font_bytes(candidates_present
                ? std::span<const mapping>(sparse) : std::span<const mapping>(space_only));
            sfnt_font_view font{};
            require(sfnt_font_view::try_create(bytes, 0U, font));
            projection_control projection{};
            advance_control advances{};
            auto glyph = initial(point);
            auto expected = glyph;
            if (candidates_present) expected.advance_x = advances.metrics[point == 0x2007U ? 7U : 3U].x;
            require(apply(font, glyph, frame_for(font, projection, selected), advances) &&
                same(glyph, expected) && projection.calls == 0U);
            require(advances.calls == (candidates_present ? 1U : 0U));
            if (candidates_present) require(advances.count == 1U &&
                advances.glyphs[0] == (point == 0x2007U ? 7U : 3U));
        }
    }
}

void verify_limits_and_failures(path selected) {
    const auto bytes = font_bytes(mappings);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(bytes, 0U, font));
    for (const auto result : {projection_result::minimum, projection_result::maximum,
        projection_result::failure}) {
        projection_control projection{};
        projection.result = result;
        advance_control advances{};
        auto glyph = initial(0x2001U);
        const auto before = glyph;
        font_error error = font_error::none;
        const auto success = apply(font, glyph, frame_for(font, projection, selected), advances,
            shaping_direction::left_to_right, &error);
        if (result == projection_result::failure)
            require(!success && error == font_error::invalid_argument && same(glyph, before));
        else require(success && glyph.advance_x == (result == projection_result::minimum
            ? std::numeric_limits<std::int32_t>::min() : std::numeric_limits<std::int32_t>::max()));
    }
    for (const auto point : {0x2007U, 0x2008U}) {
        projection_control projection{};
        advance_control advances{};
        auto frame = frame_for(font, projection, selected);
        const auto candidate = point == 0x2007U ? 4U : 2U;
        auto glyph = initial(point);
        const auto before = glyph;
        advances.fail = true; // Callback writes its entire private span before failing.
        require(!apply(font, glyph, frame, advances) && same(glyph, before));
        advances.fail = false;
        advances.metrics[candidate].y = std::numeric_limits<std::int32_t>::min();
        require(!apply(font, glyph, frame, advances, shaping_direction::top_to_bottom) && same(glyph, before));
        advances.metrics[candidate].y = std::numeric_limits<std::int32_t>::max();
        require(apply(font, glyph, frame, advances, shaping_direction::bottom_to_top) &&
            glyph.advance_y == -std::numeric_limits<std::int32_t>::max());
        glyph = before;
        advances.metrics[candidate].x = std::numeric_limits<std::int32_t>::min();
        require(apply(font, glyph, frame, advances) &&
            glyph.advance_x == std::numeric_limits<std::int32_t>::min() && projection.calls == 0U);
        glyph = before;
        require(!device_space::try_apply_device_space_fallback(font,
            shaping_direction::left_to_right, {}, glyph, frame, {}, nullptr) && same(glyph, before));
    }
    for (const auto value : {std::numeric_limits<std::int32_t>::min(),
        std::numeric_limits<std::int32_t>::max(), -100001}) {
        projection_control projection{};
        advance_control advances{};
        auto glyph = initial(0x202FU);
        glyph.advance_x = value;
        require(apply(font, glyph, frame_for(font, projection, selected), advances) &&
            glyph.advance_x == value / 2 && projection.calls == 0U && advances.calls == 0U);
    }
    projection_control projection{};
    advance_control advances{};
    auto glyph = initial(0x2004U);
    const auto before = glyph;
    auto frame = frame_for(font, projection, selected);
    sfnt_font_view other{};
    require(sfnt_font_view::try_create(bytes, 0U, other));
    frame.font = &other;
    require(!apply(font, glyph, frame, advances) && same(glyph, before));
    frame = frame_for(font, projection, selected);
    frame.owner = nullptr;
    require(!apply(font, glyph, frame, advances) && same(glyph, before));
    frame = frame_for(font, projection, selected);
    frame.project_design = nullptr;
    require(!apply(font, glyph, frame, advances) && same(glyph, before));
    frame = frame_for(font, projection, selected);
    frame.arithmetic_path = static_cast<path>(99U);
    require(!apply(font, glyph, frame, advances) && same(glyph, before));
    frame = frame_for(font, projection, selected);
    for (const auto direction : {shaping_direction::unspecified, static_cast<shaping_direction>(99U)})
        require(!apply(font, glyph, frame, advances, direction) && same(glyph, before));
    const std::array<std::int16_t, 1> coordinates{7};
    const std::array<std::int16_t, 1> changed{8};
    frame.normalized_coordinates = coordinates;
    require(!apply(font, glyph, frame, advances) && same(glyph, before));
    require(!device_space::try_apply_device_space_fallback(font, shaping_direction::left_to_right,
        changed, glyph, frame, {&advances, &get_advances}, nullptr) && same(glyph, before));
    require(device_space::try_apply_device_space_fallback(font, shaping_direction::left_to_right,
        coordinates, glyph, frame, {&advances, &get_advances}, nullptr) && glyph.advance_x == 1002);
    for (const bool include_cmap : {false, true}) {
        const auto broken = font_bytes(mappings, false, include_cmap);
        sfnt_font_view broken_font{};
        require(sfnt_font_view::try_create(broken, 0U, broken_font));
        glyph = before;
        font_error error = font_error::none;
        require(!apply(broken_font, glyph, frame_for(broken_font, projection, selected), advances,
            shaping_direction::left_to_right, &error) && error == font_error::invalid_face && same(glyph, before));
    }
}

void verify_repeated_role_identity(path selected) {
    const std::array repeated{mapping{0x20U, 1U}, mapping{0x2CU, 2U},
        mapping{0x2EU, 2U}, mapping{0x30U, 2U}, mapping{0x31U, 2U}};
    const auto bytes = font_bytes(repeated);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(bytes, 0U, font));
    projection_control projection{};
    advance_control advances{};
    advances.punctuation_bias = {4096, 8192};
    const auto frame = frame_for(font, projection, selected);
    for (const auto direction : {shaping_direction::left_to_right,
        shaping_direction::top_to_bottom}) {
        auto figure = initial(0x2007U);
        auto punctuation = initial(0x2008U);
        require(apply(font, figure, frame, advances, direction));
        require(advances.kind == device_space::device_space_advance_kind::figure &&
            advances.count == 1U && advances.glyphs[0] == 2U);
        require(apply(font, punctuation, frame, advances, direction));
        require(advances.kind == device_space::device_space_advance_kind::punctuation &&
            advances.count == 1U && advances.glyphs[0] == 2U);
        if (direction == shaping_direction::left_to_right)
            require(figure.advance_x == advances.metrics[2].x &&
                punctuation.advance_x == advances.metrics[2].x + advances.punctuation_bias.x);
        else require(figure.advance_y == -advances.metrics[2].y &&
            punctuation.advance_y == -advances.metrics[2].y - advances.punctuation_bias.y);
    }
    require(advances.calls == 4U && projection.calls == 0U);
}

void verify_unused_candidates_are_not_requested(path selected) {
    // Glyph 17 is outside both this font's glyph count and the callback's
    // metric generation. Requesting the later digit or comma would fail.
    const std::array unused_invalid{mapping{0x20U, 1U}, mapping{0x2CU, 17U},
        mapping{0x2EU, 2U}, mapping{0x30U, 4U}, mapping{0x31U, 17U},
        mapping{0x39U, 17U}};
    const auto bytes = font_bytes(unused_invalid);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(bytes, 0U, font));
    for (const auto direction : {shaping_direction::left_to_right,
        shaping_direction::right_to_left, shaping_direction::top_to_bottom,
        shaping_direction::bottom_to_top}) {
        const bool vertical = direction == shaping_direction::top_to_bottom ||
            direction == shaping_direction::bottom_to_top;
        for (const auto point : {0x2007U, 0x2008U}) {
            projection_control projection{};
            advance_control advances{};
            advances.glyphs.fill(std::numeric_limits<std::uint16_t>::max());
            const auto frame = frame_for(font, projection, selected);
            std::array glyphs{initial(point), initial('A'), initial('Z')};
            const auto before = glyphs;
            auto expected = before[0];
            const auto candidate = point == 0x2007U ? 4U : 2U;
            if (vertical) expected.advance_y = -advances.metrics[candidate].y;
            else expected.advance_x = advances.metrics[candidate].x;
            font_error error = font_error::verification_failed;
            require(apply(font, glyphs[0], frame, advances, direction, &error) &&
                error == font_error::none && same(glyphs[0], expected) &&
                same(glyphs[1], before[1]) && same(glyphs[2], before[2]));
            require(projection.calls == 0U && advances.calls == 1U &&
                advances.count == 1U && advances.glyphs[0] == candidate &&
                advances.kind == (point == 0x2007U ? device_space::device_space_advance_kind::figure :
                    device_space::device_space_advance_kind::punctuation));
            for (std::size_t index = 1U; index < advances.glyphs.size(); ++index)
                require(advances.glyphs[index] == std::numeric_limits<std::uint16_t>::max());
        }
    }
}
} // namespace

int main() {
    try {
        for (const auto selected : {path::scalar_reference, path::intrinsic_simd}) {
#if !defined(__aarch64__) && !defined(_M_ARM64) && !defined(__SSE2__) && !defined(_M_X64)
            if (selected == path::intrinsic_simd) {
                const auto bytes = font_bytes(mappings);
                sfnt_font_view font{};
                require(sfnt_font_view::try_create(bytes, 0U, font));
                projection_control projection{};
                advance_control advances{};
                auto glyph = initial(0x2001U);
                const auto before = glyph;
                require(!apply(font, glyph, frame_for(font, projection, selected), advances) && same(glyph, before));
                continue;
            }
#endif
            verify_families(selected);
            verify_mapping_and_noops(selected);
            verify_limits_and_failures(selected);
            verify_repeated_role_identity(selected);
            verify_unused_candidates_are_not_requested(selected);
        }
        std::cout << "native device special-space raw controls passed\n";
        return 0;
    } catch (const std::exception& failure) {
        std::cerr << failure.what() << '\n';
        return 1;
    }
}
