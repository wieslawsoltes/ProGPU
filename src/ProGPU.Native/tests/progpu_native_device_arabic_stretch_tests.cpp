#include "../src/Text/Shaping/progpu_native_arabic_stretch_internal.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <source_location>
#include <span>
#include <vector>

// Original raw metric/descriptor controls; no interpreter, font dependency,
// renderer or source Display admission is involved.
namespace {
using namespace progpu::native::text;
namespace stretch = progpu::native::text::detail;

void require(bool value, const std::source_location at = std::source_location::current()) {
    if (!value) {
        std::fprintf(stderr, "device Arabic stretch control failed at %s:%u\n", at.file_name(), at.line());
        std::abort();
    }
}

constexpr shaping_glyph_flags action_flags(open_type_arabic_action action) {
    return static_cast<shaping_glyph_flags>(static_cast<std::uint32_t>(action) << 28U);
}

constexpr auto unsafe = static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
    static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);

struct captured_metric final { std::uint32_t glyph_id = 5U; std::int32_t advance = 0; };
struct metric_control final {
    std::span<const std::uint32_t> mapping;
    std::span<const captured_metric> captured;
    mutable std::size_t calls = 0U;
    std::size_t reject_call = std::numeric_limits<std::size_t>::max();
    mutable std::array<std::size_t, 8U> indices{};
};

bool get_advance(const void* owner, std::size_t index, std::uint32_t glyph_id,
    std::int32_t& output) noexcept {
    const auto& control = *static_cast<const metric_control*>(owner);
    const auto call = control.calls++;
    if (call < control.indices.size()) control.indices[call] = index;
    if (call == control.reject_call) { output = 1234567; return false; }
    if (index >= control.mapping.size()) return false;
    const auto descriptor = control.mapping[index];
    if (descriptor >= control.captured.size() || control.captured[descriptor].glyph_id != glyph_id) return false;
    output = control.captured[descriptor].advance;
    return true;
}

template<class T, std::size_t N>
bool same(const std::array<T, N>& left, const std::array<T, N>& right) {
    return std::memcmp(left.data(), right.data(), sizeof(left)) == 0;
}

void mixed_runs_preserve_original_descriptors(bool rtl) {
    const std::array<shaping_glyph, 6U> original{
        shaping_glyph{3U, 0x0628U, 0, shaping_glyph_flags::safe_to_insert_tatweel, 1000, 11, 7, 13},
        shaping_glyph{5U, 0x0640U, 1, action_flags(open_type_arabic_action::stretch_fixed), 123456, 17, 19, 23},
        shaping_glyph{5U, 0x0640U, 2, action_flags(open_type_arabic_action::stretch_repeating), 654321, 29, 31, 37},
        shaping_glyph{4U, 0x0020U, 3, shaping_glyph_flags::none, 41, 43, 47, 53},
        shaping_glyph{3U, 0x0628U, 4, shaping_glyph_flags::none, 700, 59, 61, 67},
        shaping_glyph{5U, 0x0640U, 5, action_flags(open_type_arabic_action::stretch_repeating), 99999, 71, 73, 79}};
    const shaping_glyph sentinel{101U, 103U, 107, shaping_glyph_flags::none, 109, 113, 127, 131};
    std::array<shaping_glyph, 18U> glyphs{};
    glyphs.fill(sentinel);
    std::copy(original.begin(), original.end(), glyphs.begin());
    std::array<std::uint32_t, 18U> mapping{};
    mapping.fill(0xA5A5A5A5U);
    constexpr std::array<std::uint32_t, 6U> source_mapping{2U, 1U, 3U, 4U, 5U, 6U};
    std::copy(source_mapping.begin(), source_mapping.end(), mapping.begin());
    if (!rtl) {
        std::reverse(glyphs.begin(), glyphs.begin() + 6U);
        std::reverse(mapping.begin(), mapping.begin() + 6U);
    }
    std::array<captured_metric, 8U> captured{};
    captured[1U] = {5U, 100}; captured[3U] = {5U, 200}; captured[6U] = {5U, 300};
    metric_control control{mapping, captured};
    std::array<std::int32_t, 18U> widths{};
    widths.fill(-777);
    std::array<arabic_stretch_run, 3U> runs{};
    runs[2U] = {11U, 13U, 17U, 19, 23};
    std::uint32_t count = 6U;
    font_error error = font_error::none;
    require(stretch::try_apply_device_arabic_stretch_from_glyph_actions(
        sfnt_font_view{}, glyphs, count, rtl, {}, runs, {&control, &get_advance}, mapping, widths, &error));
    require(error == font_error::none && count == 12U && control.calls == 3U);
    require(control.indices[0U] == (rtl ? 1U : 0U) && control.indices[1U] == (rtl ? 2U : 3U) &&
        control.indices[2U] == (rtl ? 5U : 4U));
    require(runs[0U].start == 5U && runs[0U].end == 6U && runs[0U].copy_count == 2U &&
        runs[0U].remaining_width == 0 && runs[0U].extra_repeat_overlap == 100);
    require(runs[1U].start == 1U && runs[1U].end == 3U && runs[1U].copy_count == 4U &&
        runs[1U].remaining_width == 0 && runs[1U].extra_repeat_overlap == 25);
    std::array<shaping_glyph, 12U> expected{original[0U], original[1U], original[2U], original[2U],
        original[2U], original[2U], original[2U], original[3U], original[4U], original[5U], original[5U], original[5U]};
    std::array<std::uint32_t, 12U> expected_mapping{2U, 1U, 3U, 3U, 3U, 3U, 3U, 4U, 5U, 6U, 6U, 6U};
    constexpr std::array<std::int32_t, 5U> rtl_first{-900, -725, -550, -375, -200};
    constexpr std::array<std::int32_t, 5U> ltr_first{725, 550, 375, 200, 0};
    constexpr std::array<std::int32_t, 3U> rtl_last{-700, -500, -300};
    constexpr std::array<std::int32_t, 3U> ltr_last{500, 300, 0};
    for (std::size_t index = 0U; index < expected.size(); ++index) {
        if (index != 7U) expected[index].flags = static_cast<shaping_glyph_flags>(
            static_cast<std::uint32_t>(expected[index].flags) | unsafe);
        if (index == 1U || (index >= 2U && index <= 6U) || index >= 9U) expected[index].advance_x = 0;
    }
    expected[1U].offset_x = rtl ? -1000 : 900;
    for (std::size_t index = 0U; index < 5U; ++index) expected[index + 2U].offset_x = (rtl ? rtl_first : ltr_first)[index];
    for (std::size_t index = 0U; index < 3U; ++index) expected[index + 9U].offset_x = (rtl ? rtl_last : ltr_last)[index];
    if (!rtl) {
        std::reverse(expected.begin(), expected.end());
        std::reverse(expected_mapping.begin(), expected_mapping.end());
    }
    require(std::memcmp(glyphs.data(), expected.data(), sizeof(expected)) == 0);
    require(std::equal(expected_mapping.begin(), expected_mapping.end(), mapping.begin()));
    for (std::size_t index = 12U; index < glyphs.size(); ++index) {
        require(std::memcmp(&glyphs[index], &sentinel, sizeof(sentinel)) == 0 && mapping[index] == 0xA5A5A5A5U);
    }
    for (std::size_t index = 0U; index < 6U; ++index) {
        const auto descriptor = source_mapping[rtl ? index : 5U - index];
        const std::int32_t width = descriptor == 1U ? 100 : descriptor == 3U ? 200 : descriptor == 6U ? 300 : -777;
        require(widths[index] == width);
    }
    for (std::size_t index = 6U; index < widths.size(); ++index) require(widths[index] == -777);
    require(runs[2U].start == 11U && runs[2U].end == 13U && runs[2U].copy_count == 17U &&
        runs[2U].remaining_width == 19 && runs[2U].extra_repeat_overlap == 23);
}

void original_copy_limit_and_device_offsets(bool rtl) {
    std::array<shaping_glyph, 258U> glyphs{};
    glyphs[0U] = {3U, 0x0628U, 0, shaping_glyph_flags::none, std::numeric_limits<std::int32_t>::max()};
    glyphs[1U] = {5U, 0x0640U, 1, action_flags(open_type_arabic_action::stretch_repeating), 99999};
    std::array<std::uint32_t, 258U> mapping{};
    mapping.fill(0xA5A5A5A5U); mapping[0U] = 0U; mapping[1U] = 1U;
    if (!rtl) { std::swap(glyphs[0U], glyphs[1U]); std::swap(mapping[0U], mapping[1U]); }
    constexpr std::array captured{captured_metric{3U, 999}, captured_metric{5U, 1}};
    metric_control control{mapping, captured};
    std::array<std::int32_t, 3U> widths{-777, -777, -777};
    std::array<arabic_stretch_run, 1U> runs{};
    std::uint32_t count = 2U;
    font_error error = font_error::none;
    require(stretch::try_apply_device_arabic_stretch_from_glyph_actions(sfnt_font_view{}, glyphs,
        count, rtl, {}, runs, {&control, &get_advance}, mapping, widths, &error));
    require(count == 257U && runs[0U].copy_count == 255U && control.calls == 1U && widths[2U] == -777);
    for (std::size_t index = 0U; index < 256U; ++index) {
        const auto slot = rtl ? index + 1U : index;
        const std::int32_t offset = (rtl ? 1073741567 : 1073741823) + static_cast<std::int32_t>(index);
        require(glyphs[slot].glyph_id == 5U && glyphs[slot].code_point == 0x0640U && glyphs[slot].cluster == 1 &&
            glyphs[slot].advance_x == 0 && glyphs[slot].offset_x == offset && mapping[slot] == 1U);
    }
    const auto context = rtl ? 0U : 256U;
    require(mapping[context] == 0U && glyphs[context].advance_x == std::numeric_limits<std::int32_t>::max());
    require(mapping[257U] == 0xA5A5A5A5U && glyphs[257U].glyph_id == 0U);
}

void signed32_endpoints_are_exact(bool rtl) {
    const auto minimum = std::numeric_limits<std::int32_t>::min();
    const auto maximum = std::numeric_limits<std::int32_t>::max();
    constexpr auto fixed = action_flags(open_type_arabic_action::stretch_fixed);
    std::array<shaping_glyph, 3U> glyphs{shaping_glyph{5U, 0x0640U, 0, fixed},
        shaping_glyph{5U, 0x0640U, 1, fixed}, shaping_glyph{5U, 0x0640U, 2, fixed}};
    std::array<std::uint32_t, 3U> mapping{0U, 1U, 2U};
    const std::array captured{captured_metric{5U, rtl ? minimum : maximum}, captured_metric{5U, 1},
        captured_metric{5U, rtl ? maximum : minimum}};
    if (!rtl) { std::reverse(glyphs.begin(), glyphs.end()); std::reverse(mapping.begin(), mapping.end()); }
    metric_control control{mapping, captured};
    std::array<std::int32_t, 3U> widths{};
    std::array<arabic_stretch_run, 1U> runs{};
    font_error error = font_error::none;
    std::uint32_t count = 3U;
    require(stretch::try_apply_device_arabic_stretch_from_glyph_actions(sfnt_font_view{}, glyphs,
        count, rtl, {}, runs, {&control, &get_advance}, mapping, widths, &error));
    require(count == 3U && control.calls == 3U && glyphs[0U].offset_x == 0 &&
        glyphs[1U].offset_x == minimum && glyphs[2U].offset_x == -maximum);
    std::array<shaping_glyph, 2U> pair{shaping_glyph{5U, 0x0640U, 0, fixed}, shaping_glyph{5U, 0x0640U, 1, fixed}};
    std::array<std::uint32_t, 2U> pair_mapping{0U, 1U};
    const std::array pair_capture{captured_metric{5U, rtl ? maximum : -maximum},
        captured_metric{5U, rtl ? -maximum : maximum}};
    if (!rtl) { std::swap(pair[0U], pair[1U]); std::swap(pair_mapping[0U], pair_mapping[1U]); }
    metric_control pair_control{pair_mapping, pair_capture};
    count = 2U;
    require(stretch::try_apply_device_arabic_stretch_from_glyph_actions(sfnt_font_view{}, pair,
        count, rtl, {}, runs, {&pair_control, &get_advance}, pair_mapping, widths, &error));
    require(count == 2U && pair_control.calls == 2U && pair[0U].offset_x == 0 && pair[1U].offset_x == maximum);
}

void failures_do_not_publish(bool rtl) {
    const auto maximum = std::numeric_limits<std::int32_t>::max();
    constexpr auto fixed = action_flags(open_type_arabic_action::stretch_fixed);
    std::array<shaping_glyph, 4U> glyphs{shaping_glyph{5U, 0x0640U, 0, fixed, 111},
        shaping_glyph{5U, 0x0640U, 1, fixed, 113}, shaping_glyph{5U, 0x0640U, 2, fixed, 127}};
    std::array<std::uint32_t, 4U> mapping{0U, 1U, 2U, 0xA5A5A5A5U};
    if (!rtl) {
        std::reverse(glyphs.begin(), glyphs.begin() + 3U);
        std::reverse(mapping.begin(), mapping.begin() + 3U);
    }
    const auto original = glyphs;
    const auto original_mapping = mapping;
    std::array captured{captured_metric{5U, -maximum}, captured_metric{5U, -maximum}, captured_metric{5U, maximum}};
    metric_control control{mapping, captured};
    std::array<std::int32_t, 4U> widths{-777, -777, -777, -777};
    std::array<arabic_stretch_run, 1U> runs{};
    font_error error = font_error::none;
    const auto reject = [&](std::span<shaping_glyph> storage, std::span<std::uint32_t> descriptors,
        std::span<std::int32_t> cache, std::span<arabic_stretch_run> run_cache, font_error expected) {
        std::uint32_t count = 3U;
        require(!stretch::try_apply_device_arabic_stretch_from_glyph_actions(sfnt_font_view{}, storage,
            count, rtl, {}, run_cache, {&control, &get_advance}, descriptors, cache, &error));
        require(error == expected && count == 3U && same(glyphs, original) && same(mapping, original_mapping));
        require(widths[3U] == -777);
    };
    reject(glyphs, mapping, widths, runs, font_error::invalid_argument); // Later generated offset exceeds signed32.
    require(control.calls == 3U);
    control.calls = 0U; control.reject_call = 1U;
    reject(glyphs, mapping, widths, runs, font_error::invalid_argument);
    require(control.calls == 2U);
    control.calls = 0U; control.reject_call = std::numeric_limits<std::size_t>::max();
    reject(std::span(glyphs).first(2U), mapping, widths, runs, font_error::invalid_argument);
    reject(glyphs, std::span(mapping).first(2U), widths, runs, font_error::insufficient_buffer);
    reject(glyphs, mapping, std::span(widths).first(2U), runs, font_error::insufficient_buffer);
    require(control.calls == 0U);
    captured = {captured_metric{5U, 100}, captured_metric{5U, 200}, captured_metric{5U, 300}};
    reject(glyphs, mapping, widths, {}, font_error::insufficient_buffer);
    require(control.calls == 3U);
    glyphs[0U].glyph_id = 0x10000U;
    const auto invalid = glyphs;
    std::uint32_t count = 3U;
    require(!stretch::try_apply_device_arabic_stretch_from_glyph_actions(sfnt_font_view{}, glyphs,
        count, rtl, {}, runs, {&control, &get_advance}, mapping, widths, &error));
    require(error == font_error::invalid_glyph && count == 3U && same(glyphs, invalid) && same(mapping, original_mapping));
}

void admission_and_expanded_mapping_capacity() {
    std::array<shaping_glyph, 12U> glyphs{shaping_glyph{3U, 0x0628U, 0, shaping_glyph_flags::none, 1000},
        shaping_glyph{5U, 0x0640U, 1, action_flags(open_type_arabic_action::stretch_repeating), 99999}};
    std::array<std::uint32_t, 12U> mapping{0U, 1U};
    constexpr std::array captured{captured_metric{3U, 123}, captured_metric{5U, 100}};
    metric_control control{mapping, captured};
    std::array<std::int32_t, 2U> widths{};
    std::array<arabic_stretch_run, 1U> runs{};
    const auto original = glyphs;
    const auto original_mapping = mapping;
    std::uint32_t count = 2U;
    font_error error = font_error::none;
    require(!stretch::try_apply_device_arabic_stretch_from_glyph_actions(sfnt_font_view{}, glyphs,
        count, true, {}, runs, {&control, &get_advance}, std::span(mapping).first(2U), widths, &error));
    require(error == font_error::insufficient_buffer && count == 2U && same(glyphs, original) && same(mapping, original_mapping));
    require(!stretch::try_apply_device_arabic_stretch_from_glyph_actions(sfnt_font_view{}, std::span(glyphs).first(2U),
        count, true, {}, runs, {&control, &get_advance}, mapping, widths, &error));
    require(error == font_error::insufficient_buffer && count == 2U && same(glyphs, original) && same(mapping, original_mapping));
    count = 0U;
    require(!stretch::try_apply_device_arabic_stretch_from_glyph_actions(sfnt_font_view{}, {}, count, true,
        {}, {}, {}, {}, {}, &error) && error == font_error::invalid_argument);
    glyphs[1U].flags = shaping_glyph_flags::none;
    const auto plain = glyphs;
    const auto calls = control.calls;
    count = 2U;
    require(stretch::try_apply_device_arabic_stretch_from_glyph_actions(sfnt_font_view{}, glyphs,
        count, true, {}, {}, {&control, &get_advance}, mapping, widths, &error));
    require(error == font_error::none && count == 2U && control.calls == calls && same(glyphs, plain) && same(mapping, original_mapping));
}

void put_u16(std::span<std::byte> bytes, std::size_t at, std::uint16_t value) {
    bytes[at] = static_cast<std::byte>(value >> 8U); bytes[at + 1U] = static_cast<std::byte>(value);
}
void put_u32(std::span<std::byte> bytes, std::size_t at, std::uint32_t value) {
    put_u16(bytes, at, static_cast<std::uint16_t>(value >> 16U));
    put_u16(bytes, at + 2U, static_cast<std::uint16_t>(value));
}

void design_unit_paths_remain_paired(bool rtl) {
    std::vector<std::byte> bytes(208U);
    put_u32(bytes, 0U, 0x00010000U); put_u16(bytes, 4U, 4U);
    struct table final { std::uint32_t tag, offset, length; };
    constexpr std::array tables{table{0x68656164U, 76U, 54U}, table{0x68686561U, 132U, 36U},
        table{0x6D617870U, 168U, 6U}, table{0x686D7478U, 176U, 32U}};
    for (std::size_t index = 0U; index < tables.size(); ++index) {
        put_u32(bytes, 12U + index * 16U, tables[index].tag);
        put_u32(bytes, 20U + index * 16U, tables[index].offset);
        put_u32(bytes, 24U + index * 16U, tables[index].length);
    }
    put_u16(bytes, 76U + 18U, 1000U); put_u16(bytes, 132U + 34U, 8U);
    put_u32(bytes, 168U, 0x00010000U); put_u16(bytes, 168U + 4U, 8U);
    for (std::size_t index = 0U; index < 8U; ++index) put_u16(bytes, 176U + index * 4U, 600U);
    sfnt_font_view font{};
    font_error error = font_error::none;
    require(sfnt_font_view::try_create(bytes, 0U, font, &error));
    std::array<shaping_glyph, 7U> source{
        shaping_glyph{4U, 0x0628U, 0, shaping_glyph_flags::none, 600},
        shaping_glyph{4U, 0x0628U, 1, shaping_glyph_flags::none, 600},
        shaping_glyph{4U, 0x0628U, 2, shaping_glyph_flags::none, 600},
        shaping_glyph{4U, 0x0640U, 3, action_flags(open_type_arabic_action::stretch_repeating), 600}};
    std::array actions{open_type_arabic_action::none, open_type_arabic_action::none,
        open_type_arabic_action::none, open_type_arabic_action::stretch_repeating};
    std::array<std::uint32_t, 7U> mapping{0U, 1U, 2U, 3U};
    if (!rtl) {
        std::reverse(source.begin(), source.begin() + 4U);
        std::reverse(actions.begin(), actions.end());
        std::reverse(mapping.begin(), mapping.begin() + 4U);
    }
    auto public_glyphs = source; auto private_glyphs = source; auto device_glyphs = source;
    std::uint32_t public_count = 4U, private_count = 4U, device_count = 4U;
    std::array<arabic_stretch_run, 1U> runs{};
    require(try_apply_arabic_stretch(font, public_glyphs, public_count, actions, rtl, {}, runs, &error));
    require(stretch::try_apply_arabic_stretch_from_glyph_actions(font, private_glyphs, private_count, rtl, {}, runs, &error));
    constexpr std::array captured{captured_metric{4U, 600}, captured_metric{4U, 600},
        captured_metric{4U, 600}, captured_metric{4U, 600}};
    metric_control control{mapping, captured};
    std::array<std::int32_t, 5U> widths{-777, -777, -777, -777, -777};
    require(stretch::try_apply_device_arabic_stretch_from_glyph_actions(font, device_glyphs, device_count,
        rtl, {}, runs, {&control, &get_advance}, mapping, widths, &error));
    require(public_count == 6U && private_count == public_count && device_count == public_count &&
        same(public_glyphs, private_glyphs) && same(public_glyphs, device_glyphs));
    require(control.calls == 1U && widths[4U] == -777);
    for (std::size_t index = 0U; index < 6U; ++index) {
        const auto descriptor = rtl ? (index < 3U ? static_cast<std::uint32_t>(index) : 3U)
            : (index < 3U ? 3U : static_cast<std::uint32_t>(5U - index));
        require(mapping[index] == descriptor);
    }
}
} // namespace

int main() {
    static_assert(sizeof(shaping_glyph) == 32U);
    for (const bool rtl : {false, true}) {
        mixed_runs_preserve_original_descriptors(rtl);
        original_copy_limit_and_device_offsets(rtl);
        signed32_endpoints_are_exact(rtl);
        failures_do_not_publish(rtl);
        design_unit_paths_remain_paired(rtl);
    }
    admission_and_expanded_mapping_capacity();
    std::puts("device Arabic stretch controls passed");
}
