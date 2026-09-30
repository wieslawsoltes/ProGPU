#include "../src/Text/Shaping/progpu_native_legacy_kern_internal.hpp"
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
#include <vector>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

// Original ProGPU-authored SFNT/kern bytes and projection controls. These
// controls execute neither a font interpreter nor a renderer/source host.
namespace {
using namespace progpu::native::text;
namespace device_kern = progpu::native::text::detail;

void require(bool condition) {
    if (!condition) throw std::runtime_error("device legacy kern control failed");
}

void put_u16(std::span<std::byte> bytes, std::size_t at, std::uint16_t value) {
    bytes[at] = static_cast<std::byte>(value >> 8U);
    bytes[at + 1U] = static_cast<std::byte>(value);
}

void put_u32(std::span<std::byte> bytes, std::size_t at, std::uint32_t value) {
    put_u16(bytes, at, static_cast<std::uint16_t>(value >> 16U));
    put_u16(bytes, at + 2U, static_cast<std::uint16_t>(value));
}

struct pair_record final {
    std::uint16_t left, right;
    std::int16_t value;
};

std::vector<std::byte> pair_subtable(bool apple, bool cross_stream,
    std::span<const pair_record> pairs, std::uint8_t extra_coverage = 0U) {
    const std::size_t header = apple ? 8U : 6U;
    std::vector<std::byte> bytes(header + 8U + pairs.size() * 6U);
    const auto coverage = static_cast<std::uint8_t>(extra_coverage |
        (apple ? (cross_stream ? 0x40U : 0U) :
            (cross_stream ? 0x05U : 0x01U)));
    if (apple) {
        put_u32(bytes, 0U, static_cast<std::uint32_t>(bytes.size()));
        bytes[4U] = static_cast<std::byte>(coverage);
    } else {
        put_u16(bytes, 2U, static_cast<std::uint16_t>(bytes.size()));
        bytes[5U] = static_cast<std::byte>(coverage);
    }
    put_u16(bytes, header, static_cast<std::uint16_t>(pairs.size()));
    for (std::size_t index = 0U; index < pairs.size(); ++index) {
        const auto at = header + 8U + index * 6U;
        put_u16(bytes, at, pairs[index].left);
        put_u16(bytes, at + 2U, pairs[index].right);
        put_u16(bytes, at + 4U, static_cast<std::uint16_t>(pairs[index].value));
    }
    return bytes;
}

std::vector<std::byte> class_subtable(std::int16_t value) {
    std::vector<std::byte> bytes(36U);
    put_u16(bytes, 2U, 36U);
    put_u16(bytes, 4U, 0x0201U);
    put_u16(bytes, 6U, 4U); // Row width.
    put_u16(bytes, 8U, 14U);
    put_u16(bytes, 10U, 22U);
    put_u16(bytes, 12U, 30U);
    put_u16(bytes, 14U, 1U);
    put_u16(bytes, 16U, 1U);
    put_u16(bytes, 18U, 30U);
    put_u16(bytes, 22U, 2U);
    put_u16(bytes, 24U, 1U);
    put_u16(bytes, 26U, 2U);
    put_u16(bytes, 32U, static_cast<std::uint16_t>(value));
    return bytes;
}

std::vector<std::byte> kern_table(bool apple,
    std::span<const std::vector<std::byte>> subtables) {
    const std::size_t header = apple ? 8U : 4U;
    std::size_t length = header;
    for (const auto& subtable : subtables) length += subtable.size();
    std::vector<std::byte> bytes(length);
    if (apple) {
        put_u32(bytes, 0U, 0x00010000U);
        put_u32(bytes, 4U, static_cast<std::uint32_t>(subtables.size()));
    } else {
        put_u16(bytes, 2U, static_cast<std::uint16_t>(subtables.size()));
    }
    auto at = header;
    for (const auto& subtable : subtables) {
        std::copy(subtable.begin(), subtable.end(), bytes.begin() +
            static_cast<std::ptrdiff_t>(at));
        at += subtable.size();
    }
    return bytes;
}

std::vector<std::byte> font_bytes(std::span<const std::byte> kern) {
    const std::size_t header = kern.empty() ? 12U : 28U;
    std::vector<std::byte> bytes(header + kern.size());
    put_u32(bytes, 0U, 0x00010000U);
    if (!kern.empty()) {
        put_u16(bytes, 4U, 1U);
        put_u32(bytes, 12U, open_type_tag::from_chars('k', 'e', 'r', 'n').value);
        put_u32(bytes, 20U, static_cast<std::uint32_t>(header));
        put_u32(bytes, 24U, static_cast<std::uint32_t>(kern.size()));
        std::copy(kern.begin(), kern.end(), bytes.begin() +
            static_cast<std::ptrdiff_t>(header));
    }
    return bytes;
}

std::vector<std::byte> pair_font(bool apple, bool cross_stream,
    std::span<const pair_record> pairs) {
    const std::array subtables{pair_subtable(apple, cross_stream, pairs)};
    return font_bytes(kern_table(apple, subtables));
}

enum class projection_result { scaled, zero, minimum, maximum };
struct projection_control final {
    mutable std::size_t calls = 0U;
    std::size_t reject_call = std::numeric_limits<std::size_t>::max();
    projection_result result = projection_result::scaled;
    mutable std::array<std::array<device_kern::gpos_metric_vector, 2>, 8> input{};
};

bool begin_projection(const projection_control& control,
    const std::array<device_kern::gpos_metric_vector, 2>& input,
    std::array<device_kern::gpos_metric_vector, 2>& output,
    bool& finished) noexcept {
    const auto call = control.calls++;
    if (call < control.input.size()) control.input[call] = input;
    if (call == control.reject_call) {
        output = {{{701, 703}, {707, 709}}};
        return false;
    }
    finished = control.result != projection_result::scaled;
    if (finished) {
        const auto value = control.result == projection_result::minimum
            ? std::numeric_limits<std::int32_t>::min()
            : control.result == projection_result::maximum
                ? std::numeric_limits<std::int32_t>::max() : 0;
        output = {{{value, value}, {0, 0}}};
    }
    return true;
}

bool project_reference(const void* owner,
    const std::array<device_kern::gpos_metric_vector, 2>& input,
    std::array<device_kern::gpos_metric_vector, 2>& output) noexcept {
    const auto& control = *static_cast<const projection_control*>(owner);
    bool finished = false;
    if (!begin_projection(control, input, output, finished)) return false;
    if (finished) return true;
    std::array<device_kern::gpos_metric_vector, 2> candidate{};
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
bool project_intrinsic(const void* owner,
    const std::array<device_kern::gpos_metric_vector, 2>& input,
    std::array<device_kern::gpos_metric_vector, 2>& output) noexcept {
    const auto& control = *static_cast<const projection_control*>(owner);
    bool finished = false;
    if (!begin_projection(control, input, output, finished)) return false;
    if (finished) return true;
    const std::array<std::int32_t, 4> packed{
        input[0].x, input[0].y, input[1].x, input[1].y};
    for (const auto value : packed) {
        if (value < std::numeric_limits<std::int16_t>::min() ||
            value > std::numeric_limits<std::int16_t>::max()) return false;
    }
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

device_kern::gpos_device_frame frame_for(const sfnt_font_view& font,
    projection_control& control, device_kern::gpos_arithmetic_path path) {
    device_kern::gpos_device_frame frame{};
    frame.font = &font;
    frame.owner = &control;
    frame.arithmetic_path = path;
    frame.project_design = &project_reference;
#if defined(__aarch64__) || defined(_M_ARM64) || defined(__SSE2__) || defined(_M_X64)
    if (path == device_kern::gpos_arithmetic_path::intrinsic_simd)
        frame.project_design = &project_intrinsic;
#endif
    // Rounded integer ppem0 is valid for a retained fractional device scale.
    // Legacy kern needs no contour-point callback or Device-table ppem lookup.
    return frame;
}

template<std::size_t Count>
bool same(const std::array<shaping_glyph, Count>& left,
    const std::array<shaping_glyph, Count>& right) {
    return std::memcmp(left.data(), right.data(), sizeof(left)) == 0;
}

std::array<shaping_glyph, 2> initial_pair() {
    return {{{1U, 'A', 4, shaping_glyph_flags::none, 50000, 101, 103, 107},
        {2U, 'V', 8, shaping_glyph_flags::none, 60000, 109, 70000, -80000}}};
}

bool dependent(const shaping_glyph& glyph) {
    constexpr auto mask = static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
        static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat);
    return (static_cast<std::uint32_t>(glyph.flags) & mask) == mask;
}

void verify_success(device_kern::gpos_arithmetic_path path) {
    for (const auto apple : {false, true}) {
        for (const auto cross_stream : {false, true}) {
            const std::array pairs{pair_record{1U, 2U, -3}};
            const auto bytes = pair_font(apple, cross_stream, pairs);
            sfnt_font_view font{};
            require(sfnt_font_view::try_create(bytes, 0U, font));
            projection_control control{};
            const auto frame = frame_for(font, control, path);
            auto glyphs = initial_pair();
            auto expected = glyphs;
            if (cross_stream) expected[1].offset_y -= 15;
            else {
                expected[0].advance_x -= 5;
                expected[1].advance_x -= 4;
                expected[1].offset_x -= 4;
            }
            expected[1].flags = static_cast<shaping_glyph_flags>(
                static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_break) |
                static_cast<std::uint32_t>(shaping_glyph_flags::unsafe_to_concat));
            font_error error = font_error::invalid_face;
            require(device_kern::try_apply_device_legacy_kern(font, glyphs, nullptr, frame, &error));
            require(error == font_error::none && same(glyphs, expected) && control.calls == 1U);
            require(control.input[0][0].x == (cross_stream ? 0 : -3) &&
                control.input[0][0].y == (cross_stream ? -3 : 0) &&
                control.input[0][1].x == 0 && control.input[0][1].y == 0);
            auto design = initial_pair();
            device_kern::apply_legacy_kern(font, design, nullptr);
            require(design[0].advance_x == (cross_stream ? 50000 : 32767) &&
                design[1].advance_x == (cross_stream ? 60000 : 32767) &&
                design[1].offset_x == (cross_stream ? 70000 : 32767) &&
                design[1].offset_y == (cross_stream ? -32768 : -80000));
        }
    }
    const std::array class_tables{class_subtable(7)};
    const auto bytes = font_bytes(kern_table(false, class_tables));
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(bytes, 0U, font));
    projection_control control{};
    auto glyphs = initial_pair();
    require(device_kern::try_apply_device_legacy_kern(font, glyphs, nullptr,
        frame_for(font, control, path)));
    require(glyphs[0].advance_x == 50010 && glyphs[1].advance_x == 60011 &&
        glyphs[1].offset_x == 70011 && dependent(glyphs[1]));
}

void verify_order_and_marks(device_kern::gpos_arithmetic_path path) {
    const std::array negative{pair_record{1U, 2U, -3}};
    const std::array positive{pair_record{1U, 2U, 5}};
    // The authoritative legacy walker accumulates these coverage-bit inputs
    // in subtable order. Keep its existing minimum/override-bit behavior.
    const std::array subtables{pair_subtable(false, false, negative, 0x02U),
        pair_subtable(false, false, positive, 0x08U)};
    const auto bytes = font_bytes(kern_table(false, subtables));
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(bytes, 0U, font));
    projection_control control{};
    auto glyphs = initial_pair();
    require(device_kern::try_apply_device_legacy_kern(font, glyphs, nullptr,
        frame_for(font, control, path)));
    require(glyphs[0].advance_x == 50002 && glyphs[1].advance_x == 60004 &&
        glyphs[1].offset_x == 70004 && control.calls == 2U);
    auto design = initial_pair();
    design[0].advance_x = design[1].advance_x = design[1].offset_x = 100;
    device_kern::apply_legacy_kern(font, design, nullptr);
    require(design[0].advance_x == 100 && design[1].advance_x == 102 &&
        design[1].offset_x == 102);

    std::array<std::byte, 20> gdef_bytes{};
    put_u16(gdef_bytes, 0U, 1U);
    put_u16(gdef_bytes, 4U, 12U);
    put_u16(gdef_bytes, 12U, 1U);
    put_u16(gdef_bytes, 14U, 9U);
    put_u16(gdef_bytes, 16U, 1U);
    put_u16(gdef_bytes, 18U, 3U);
    open_type_gdef_view gdef{};
    require(open_type_gdef_view::try_create(gdef_bytes, gdef));
    const auto marked_bytes = pair_font(false, false, positive);
    require(sfnt_font_view::try_create(marked_bytes, 0U, font));
    const auto pair = initial_pair();
    std::array<shaping_glyph, 3> marked{pair[0],
        shaping_glyph{9U, 0x0301U, 2, shaping_glyph_flags::none, 11, 13, 17, 19}, pair[1]};
    const auto mark_before = marked[1];
    control = {};
    require(device_kern::try_apply_device_legacy_kern(font, marked, &gdef,
        frame_for(font, control, path)));
    require(marked[0].advance_x == 50007 && marked[2].advance_x == 60008 &&
        marked[2].offset_x == 70008 && control.calls == 1U &&
        std::memcmp(&marked[1], &mark_before, sizeof(mark_before)) == 0 &&
        marked[0].cluster == 4 && marked[2].cluster == 8 &&
        dependent(marked[0]) && dependent(marked[2]));
}

void verify_limits_and_failure(device_kern::gpos_arithmetic_path path) {
    for (const auto value : {std::int16_t{-3}, std::int16_t{3}}) {
        for (std::size_t lane = 0U; lane < 4U; ++lane) {
            const std::array pairs{pair_record{1U, 2U, value}};
            const auto bytes = pair_font(false, lane == 3U, pairs);
            sfnt_font_view font{};
            require(sfnt_font_view::try_create(bytes, 0U, font));
            projection_control control{};
            auto glyphs = initial_pair();
            auto& target = lane == 0U ? glyphs[0].advance_x :
                lane == 1U ? glyphs[1].advance_x :
                lane == 2U ? glyphs[1].offset_x : glyphs[1].offset_y;
            target = value < 0 ? std::numeric_limits<std::int32_t>::min() :
                std::numeric_limits<std::int32_t>::max();
            const auto before = glyphs;
            font_error error = font_error::none;
            require(!device_kern::try_apply_device_legacy_kern(font, glyphs, nullptr,
                frame_for(font, control, path), &error));
            require(error == font_error::invalid_argument && same(glyphs, before));
        }
    }
    const std::array pairs{pair_record{1U, 2U, 3}, pair_record{2U, 3U, 3}};
    const auto bytes = pair_font(false, false, pairs);
    sfnt_font_view font{};
    require(sfnt_font_view::try_create(bytes, 0U, font));
    projection_control control{};
    auto glyphs = initial_pair();
    const auto before = glyphs;
    control.reject_call = 0U;
    require(!device_kern::try_apply_device_legacy_kern(font, glyphs, nullptr,
        frame_for(font, control, path)) && same(glyphs, before));
    control = {};
    control.reject_call = 1U;
    std::array<shaping_glyph, 3> run{before[0], before[1],
        shaping_glyph{3U, 'W', 12, shaping_glyph_flags::none, 90000, 23, 29, 31}};
    const auto tail = run[2];
    require(!device_kern::try_apply_device_legacy_kern(font, run, nullptr,
        frame_for(font, control, path)));
    require(run[0].advance_x == 50004 && run[1].advance_x == 60005 &&
        run[1].offset_x == 70005 && dependent(run[1]) && control.calls == 2U &&
        std::memcmp(&run[2], &tail, sizeof(tail)) == 0);

    for (const auto result : {projection_result::minimum, projection_result::maximum,
        projection_result::zero}) {
        control = {};
        control.result = result;
        glyphs = initial_pair();
        glyphs[0].advance_x = glyphs[1].advance_x = glyphs[1].offset_x = 0;
        require(device_kern::try_apply_device_legacy_kern(font, glyphs, nullptr,
            frame_for(font, control, path)));
        const auto first = result == projection_result::minimum ? -1073741824 :
            result == projection_result::maximum ? 1073741823 : 0;
        const auto second = result == projection_result::minimum ? -1073741824 :
            result == projection_result::maximum ? 1073741824 : 0;
        require(glyphs[0].advance_x == first && glyphs[1].advance_x == second &&
            glyphs[1].offset_x == second && dependent(glyphs[1]));

        const auto cross_bytes = pair_font(true, true, std::span(pairs).first(1U));
        sfnt_font_view cross_font{};
        require(sfnt_font_view::try_create(cross_bytes, 0U, cross_font));
        control = {};
        control.result = result;
        glyphs = initial_pair();
        glyphs[1].offset_y = 0;
        require(device_kern::try_apply_device_legacy_kern(cross_font, glyphs, nullptr,
            frame_for(cross_font, control, path)));
        const auto cross_value = result == projection_result::minimum
            ? std::numeric_limits<std::int32_t>::min()
            : result == projection_result::maximum
                ? std::numeric_limits<std::int32_t>::max() : 0;
        require(glyphs[0].advance_x == 50000 && glyphs[1].advance_x == 60000 &&
            glyphs[1].offset_x == 70000 && glyphs[1].offset_y == cross_value &&
            dependent(glyphs[1]));
    }
}

void verify_admission_and_noops(device_kern::gpos_arithmetic_path path) {
    const std::array pairs{pair_record{1U, 2U, 3}};
    const auto bytes = pair_font(false, false, pairs);
    sfnt_font_view font{}, other{};
    require(sfnt_font_view::try_create(bytes, 0U, font) &&
        sfnt_font_view::try_create(bytes, 0U, other));
    projection_control control{};
    const auto frame = frame_for(font, control, path);
    auto glyphs = initial_pair();
    const auto before = glyphs;
    for (std::size_t fault = 0U; fault < 5U; ++fault) {
        auto rejected = frame;
        if (fault == 0U) rejected.font = nullptr;
        if (fault == 1U) rejected.font = &other;
        if (fault == 2U) rejected.owner = nullptr;
        if (fault == 3U) rejected.project_design = nullptr;
        if (fault == 4U) rejected.arithmetic_path =
            static_cast<device_kern::gpos_arithmetic_path>(0xFFFFFFFFU);
        font_error error = font_error::none;
        require(!device_kern::try_apply_device_legacy_kern(font, glyphs, nullptr,
            rejected, &error) && error == font_error::invalid_argument &&
            same(glyphs, before) && control.calls == 0U);
    }
    sfnt_font_view empty{};
    require(!device_kern::try_apply_device_legacy_kern(empty, glyphs, nullptr,
        frame_for(empty, control, path)) && same(glyphs, before));
    const auto no_kern = font_bytes({});
    require(sfnt_font_view::try_create(no_kern, 0U, other));
    font_error error = font_error::invalid_face;
    require(device_kern::try_apply_device_legacy_kern(other, glyphs, nullptr,
        frame_for(other, control, path), &error) && error == font_error::none &&
        same(glyphs, before) && control.calls == 0U);
    const std::array zeros{pair_record{1U, 2U, 0}};
    const auto zero_bytes = pair_font(false, false, zeros);
    require(sfnt_font_view::try_create(zero_bytes, 0U, other));
    require(device_kern::try_apply_device_legacy_kern(other, glyphs, nullptr,
        frame_for(other, control, path)) && same(glyphs, before) && control.calls == 0U);
    auto truncated = pair_subtable(false, false, pairs);
    put_u16(truncated, 6U, 2U);
    const std::array subtables{truncated};
    const auto truncated_bytes = font_bytes(kern_table(false, subtables));
    require(sfnt_font_view::try_create(truncated_bytes, 0U, other));
    require(device_kern::try_apply_device_legacy_kern(other, glyphs, nullptr,
        frame_for(other, control, path)) && same(glyphs, before) && control.calls == 0U);
    device_kern::apply_legacy_kern(other, glyphs, nullptr);
    require(same(glyphs, before));
}
} // namespace

int main() {
    try {
        for (const auto path : {device_kern::gpos_arithmetic_path::scalar_reference,
            device_kern::gpos_arithmetic_path::intrinsic_simd}) {
#if !defined(__aarch64__) && !defined(_M_ARM64) && !defined(__SSE2__) && !defined(_M_X64)
            if (path == device_kern::gpos_arithmetic_path::intrinsic_simd) {
                const std::array pairs{pair_record{1U, 2U, 3}};
                const auto bytes = pair_font(false, false, pairs);
                sfnt_font_view font{};
                require(sfnt_font_view::try_create(bytes, 0U, font));
                projection_control control{};
                auto glyphs = initial_pair();
                const auto before = glyphs;
                require(!device_kern::try_apply_device_legacy_kern(font, glyphs, nullptr,
                    frame_for(font, control, path)) && same(glyphs, before));
                continue;
            }
#endif
            verify_success(path);
            verify_order_and_marks(path);
            verify_limits_and_failure(path);
            verify_admission_and_noops(path);
        }
        std::cout << "native device legacy kern controls passed\n";
        return 0;
    } catch (const std::exception& failure) {
        std::cerr << failure.what() << '\n';
        return 1;
    }
}
