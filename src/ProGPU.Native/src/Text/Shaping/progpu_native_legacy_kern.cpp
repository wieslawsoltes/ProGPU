#include "progpu_native_legacy_kern_internal.hpp"
#include "progpu_native_open_type_gpos_internal.hpp"
#include "../progpu_native_font_bytes.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>
#if defined(__aarch64__) || defined(_M_ARM64)
#include <arm_neon.h>
#elif defined(__SSE2__) || defined(_M_X64)
#include <emmintrin.h>
#endif

// Direct native port provenance: ProGPU-owned
// OpenTypeTextShaper.GlyphPositionBuffer.ApplyLegacyKern at repository
// checkpoint 34b76eeb. Parsing is bounded and all glyph storage is borrowed.

namespace progpu::native::text::detail {
namespace {

constexpr auto kern_tag = open_type_tag::from_chars('k', 'e', 'r', 'n');

std::int32_t add_i16_clamped(
    std::int32_t value,
    std::int32_t adjustment) noexcept {
    return static_cast<std::int32_t>(std::clamp<std::int64_t>(
        static_cast<std::int64_t>(value) + adjustment,
        std::numeric_limits<std::int16_t>::min(),
        std::numeric_limits<std::int16_t>::max()));
}

std::int32_t floor_half(std::int32_t value) noexcept {
    return value >= 0 ? value / 2 : -((-value + 1) / 2);
}

std::size_t next_kern_glyph(
    std::span<const shaping_glyph> glyphs,
    std::size_t index,
    const open_type_gdef_view* gdef) noexcept {
    if (gdef == nullptr) return index;
    while (index < glyphs.size() &&
        gdef->glyph_class(static_cast<std::uint16_t>(
            glyphs[index].glyph_id)) == open_type_glyph_class::mark) {
        ++index;
    }
    return index;
}

void apply_adjustment(
    std::span<shaping_glyph> glyphs,
    std::size_t left_index,
    std::size_t right_index,
    std::int32_t kerning,
    bool cross_stream) noexcept {
    if (kerning == 0) return;
    mark_gpos_dependency(glyphs, left_index, right_index);
    if (cross_stream) {
        glyphs[right_index].offset_y = add_i16_clamped(
            glyphs[right_index].offset_y, kerning);
        return;
    }
    const auto first = floor_half(kerning);
    const auto second = kerning - first;
    glyphs[left_index].advance_x = add_i16_clamped(
        glyphs[left_index].advance_x, first);
    glyphs[right_index].advance_x = add_i16_clamped(
        glyphs[right_index].advance_x, second);
    glyphs[right_index].offset_x = add_i16_clamped(
        glyphs[right_index].offset_x, second);
}

// The four metric lanes are independent, while pairs/subtables are ordered
// dependencies. Checked SIMD arithmetic follows the ProGPU-owned GPOS helper
// in progpu_native_open_type_gpos.cpp; scalar arithmetic is an explicit oracle.
bool add_device_kern_values(
    const std::array<std::int32_t, 4>& values,
    const std::array<std::int32_t, 4>& deltas,
    gpos_arithmetic_path path,
    std::array<std::int32_t, 4>& output) noexcept {
    if (path == gpos_arithmetic_path::scalar_reference) {
        std::array<std::int32_t, 4> candidate{};
        for (std::size_t index = 0U; index < candidate.size(); ++index) {
            const auto sum = static_cast<std::int64_t>(values[index]) +
                deltas[index];
            if (sum < std::numeric_limits<std::int32_t>::min() ||
                sum > std::numeric_limits<std::int32_t>::max()) return false;
            candidate[index] = static_cast<std::int32_t>(sum);
        }
        output = candidate;
        return true;
    }
#if defined(__aarch64__) || defined(_M_ARM64)
    const auto values_vector = vld1q_s32(values.data());
    const auto deltas_vector = vld1q_s32(deltas.data());
    const auto sum = vaddq_s32(values_vector, deltas_vector);
    const auto overflow = vbicq_s32(veorq_s32(sum, values_vector),
        veorq_s32(values_vector, deltas_vector));
    if (vminvq_s32(overflow) < 0) return false;
    vst1q_s32(output.data(), sum);
    return true;
#elif defined(__SSE2__) || defined(_M_X64)
    const auto values_vector = _mm_loadu_si128(
        reinterpret_cast<const __m128i*>(values.data()));
    const auto deltas_vector = _mm_loadu_si128(
        reinterpret_cast<const __m128i*>(deltas.data()));
    const auto sum = _mm_add_epi32(values_vector, deltas_vector);
    const auto overflow = _mm_andnot_si128(
        _mm_xor_si128(values_vector, deltas_vector),
        _mm_xor_si128(sum, values_vector));
    if (_mm_movemask_ps(_mm_castsi128_ps(overflow)) != 0) return false;
    _mm_storeu_si128(reinterpret_cast<__m128i*>(output.data()), sum);
    return true;
#else
    return false;
#endif
}

bool apply_device_adjustment(
    std::span<shaping_glyph> glyphs,
    std::size_t left_index,
    std::size_t right_index,
    std::int32_t kerning,
    bool cross_stream,
    const gpos_device_frame& frame) noexcept {
    if (kerning == 0) return true;
    const std::array<gpos_metric_vector, 2> design{{
        cross_stream ? gpos_metric_vector{0, kerning} :
            gpos_metric_vector{kerning, 0}, {}}};
    std::array<gpos_metric_vector, 2> device{};
    if (!frame.project_design(frame.owner, design, device)) return false;
    const auto projected = cross_stream ? device[0].y : device[0].x;
    // Division truncates toward zero; subtracting one for negative odd values
    // is floor-half without negating INT32_MIN.
    const auto first = projected / 2 -
        (projected < 0 && projected % 2 != 0 ? 1 : 0);
    const auto second = projected - first;
    const std::array<std::int32_t, 4> values{
        glyphs[left_index].advance_x, glyphs[right_index].advance_x,
        glyphs[right_index].offset_x, glyphs[right_index].offset_y};
    const std::array<std::int32_t, 4> deltas = cross_stream
        ? std::array<std::int32_t, 4>{0, 0, 0, projected}
        : std::array<std::int32_t, 4>{first, second, second, 0};
    std::array<std::int32_t, 4> adjusted{};
    if (!add_device_kern_values(values, deltas, frame.arithmetic_path,
        adjusted)) return false;
    glyphs[left_index].advance_x = adjusted[0];
    glyphs[right_index].advance_x = adjusted[1];
    glyphs[right_index].offset_x = adjusted[2];
    glyphs[right_index].offset_y = adjusted[3];
    // Keep the original raw-pair dependency even when projection rounds to zero.
    mark_gpos_dependency(glyphs, left_index, right_index);
    return true;
}

std::int32_t find_pair(
    std::span<const std::byte> data,
    std::size_t records,
    std::uint16_t pair_count,
    std::uint16_t left,
    std::uint16_t right) noexcept {
    const auto key = (static_cast<std::uint32_t>(left) << 16U) | right;
    std::uint16_t low = 0U;
    std::uint16_t high = pair_count;
    while (low < high) {
        const auto middle = static_cast<std::uint16_t>(
            low + static_cast<std::uint16_t>((high - low) / 2U));
        const auto record = records + static_cast<std::size_t>(middle) * 6U;
        const auto candidate = read_u32(data, record);
        if (key < candidate) {
            high = middle;
        } else if (key > candidate) {
            low = static_cast<std::uint16_t>(middle + 1U);
        } else {
            return read_i16(data, record + 4U);
        }
    }
    return 0;
}

std::uint16_t get_class(
    std::span<const std::byte> data,
    std::size_t subtable,
    std::size_t length,
    std::uint16_t relative_offset,
    std::uint16_t glyph) noexcept {
    if (relative_offset > length || length - relative_offset < 4U) return 0U;
    const auto table = subtable + relative_offset;
    const auto first_glyph = read_u16(data, table);
    const auto glyph_count = read_u16(data, table + 2U);
    if (glyph < first_glyph) return 0U;
    const auto index = static_cast<std::uint32_t>(glyph - first_glyph);
    const auto value = table + 4U + static_cast<std::size_t>(index) * 2U;
    return index < glyph_count && can_read(data, value, 2U)
        ? read_u16(data, value)
        : 0U;
}

template<class ApplyAdjustment>
bool apply_format_zero(
    std::span<const std::byte> data,
    std::size_t subtable,
    std::size_t header_size,
    std::size_t length,
    bool cross_stream,
    std::span<shaping_glyph> glyphs,
    const open_type_gdef_view* gdef,
    const ApplyAdjustment& apply) noexcept {
    const auto body = subtable + header_size;
    if (!can_read(data, body, 8U)) return true;
    const auto pair_count = read_u16(data, body);
    const auto records = body + 8U;
    if (records > subtable + length ||
        static_cast<std::size_t>(pair_count) >
            (subtable + length - records) / 6U) {
        return true;
    }
    for (std::size_t left = 0U; left + 1U < glyphs.size(); ++left) {
        const auto right = next_kern_glyph(glyphs, left + 1U, gdef);
        if (right >= glyphs.size()) break;
        const auto kerning = find_pair(
            data,
            records,
            pair_count,
            static_cast<std::uint16_t>(glyphs[left].glyph_id),
            static_cast<std::uint16_t>(glyphs[right].glyph_id));
        if (!apply(glyphs, left, right, kerning, cross_stream)) return false;
    }
    return true;
}

template<class ApplyAdjustment>
bool apply_format_two(
    std::span<const std::byte> data,
    std::size_t subtable,
    std::size_t header_size,
    std::size_t length,
    bool cross_stream,
    std::span<shaping_glyph> glyphs,
    const open_type_gdef_view* gdef,
    const ApplyAdjustment& apply) noexcept {
    const auto body = subtable + header_size;
    if (!can_read(data, body, 8U)) return true;
    const auto left_table = read_u16(data, body + 2U);
    const auto right_table = read_u16(data, body + 4U);
    const auto array = read_u16(data, body + 6U);
    for (std::size_t left = 0U; left + 1U < glyphs.size(); ++left) {
        const auto right = next_kern_glyph(glyphs, left + 1U, gdef);
        if (right >= glyphs.size()) break;
        const auto left_offset = get_class(
            data, subtable, length, left_table,
            static_cast<std::uint16_t>(glyphs[left].glyph_id));
        const auto right_offset = get_class(
            data, subtable, length, right_table,
            static_cast<std::uint16_t>(glyphs[right].glyph_id));
        const auto value_offset =
            static_cast<std::size_t>(left_offset) + right_offset;
        const auto kerning = value_offset < array ||
            value_offset > length || length - value_offset < 2U
            ? 0
            : read_i16(data, subtable + value_offset);
        if (!apply(glyphs, left, right, kerning, cross_stream)) return false;
    }
    return true;
}

template<class ApplyAdjustment>
bool walk_legacy_kern(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    const open_type_gdef_view* gdef,
    const ApplyAdjustment& apply) noexcept {
    sfnt_table_view table{};
    if (!font.try_get_table(kern_tag, table)) return true;
    const auto data = table.bytes;
    const bool apple = can_read(data, 0U, 8U) &&
        read_u32(data, 0U) == 0x00010000U;
    std::uint32_t subtable_count = 0U;
    std::size_t subtable = 0U;
    if (apple) {
        subtable_count = read_u32(data, 4U);
        subtable = 8U;
    } else {
        if (!can_read(data, 0U, 4U) || read_u16(data, 0U) != 0U) return true;
        subtable_count = read_u16(data, 2U);
        subtable = 4U;
    }
    for (std::uint32_t index = 0U; index < subtable_count; ++index) {
        const std::size_t header_size = apple ? 8U : 6U;
        if (!can_read(data, subtable, header_size)) break;
        const auto raw_length = apple
            ? static_cast<std::uint64_t>(read_u32(data, subtable))
            : static_cast<std::uint64_t>(read_u16(data, subtable + 2U));
        if (raw_length < header_size ||
            raw_length > std::numeric_limits<std::size_t>::max() ||
            !can_read(data, subtable, static_cast<std::size_t>(raw_length))) {
            break;
        }
        const auto length = static_cast<std::size_t>(raw_length);
        const auto format = std::to_integer<std::uint8_t>(
            data[subtable + (apple ? 5U : 4U)]);
        const auto coverage = std::to_integer<std::uint8_t>(
            data[subtable + (apple ? 4U : 5U)]);
        const bool horizontal = apple
            ? (coverage & 0x80U) == 0U
            : (coverage & 0x01U) != 0U;
        const bool cross_stream = apple
            ? (coverage & 0x40U) != 0U
            : (coverage & 0x04U) != 0U;
        if (horizontal && format == 0U) {
            if (!apply_format_zero(
                data, subtable, header_size, length, cross_stream, glyphs,
                gdef, apply)) return false;
        } else if (horizontal && format == 2U) {
            if (!apply_format_two(
                data, subtable, header_size, length, cross_stream, glyphs,
                gdef, apply)) return false;
        }
        subtable += length;
    }
    return true;
}

} // namespace

void apply_legacy_kern(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    const open_type_gdef_view* gdef) noexcept {
    static_cast<void>(walk_legacy_kern(font, glyphs, gdef,
        [](std::span<shaping_glyph> buffer, std::size_t left,
            std::size_t right, std::int32_t kerning, bool cross_stream) noexcept {
            apply_adjustment(buffer, left, right, kerning, cross_stream);
            return true;
        }));
}

bool try_apply_device_legacy_kern(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    const open_type_gdef_view* gdef,
    const gpos_device_frame& frame,
    font_error* error) noexcept {
    const auto fail = [error](font_error value) noexcept {
        if (error != nullptr) *error = value;
        return false;
    };
    if (frame.font != &font || font.data().empty() ||
        frame.owner == nullptr || frame.project_design == nullptr ||
        (frame.arithmetic_path != gpos_arithmetic_path::intrinsic_simd &&
         frame.arithmetic_path != gpos_arithmetic_path::scalar_reference)) {
        return fail(font_error::invalid_argument);
    }
#if !defined(__aarch64__) && !defined(_M_ARM64) && !defined(__SSE2__) && !defined(_M_X64)
    if (frame.arithmetic_path == gpos_arithmetic_path::intrinsic_simd)
        return fail(font_error::invalid_argument);
#endif
    if (!walk_legacy_kern(font, glyphs, gdef,
        [&frame](std::span<shaping_glyph> buffer, std::size_t left,
            std::size_t right, std::int32_t kerning, bool cross_stream) noexcept {
            return apply_device_adjustment(buffer, left, right, kerning,
                cross_stream, frame);
        })) return fail(font_error::invalid_argument);
    if (error != nullptr) *error = font_error::none;
    return true;
}

} // namespace progpu::native::text::detail
