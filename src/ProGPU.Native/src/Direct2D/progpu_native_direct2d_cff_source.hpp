#pragma once

#include "progpu_native_direct2d_compat.hpp"
#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <vector>
#include <utility>

namespace progpu::native::direct2d::detail {

inline bool validate_cff_source_directory(std::span<const std::byte> bytes,
    std::uint32_t face_index, bool cff2) noexcept
{
    // The raw SFNT accessor deliberately ignores out-of-range table records.
    // A source CFF owner must not turn such a competing outline/variation table
    // into absence and silently choose another outline or fixed metrics.
    const auto fits = [&](std::size_t offset, std::size_t length) {
        return offset <= bytes.size() && length <= bytes.size() - offset;
    };
    const auto u16 = [&](std::size_t offset) {
        return (std::to_integer<std::uint32_t>(bytes[offset]) << 8U) |
            std::to_integer<std::uint32_t>(bytes[offset + 1U]);
    };
    const auto u32 = [&](std::size_t offset) { return (u16(offset) << 16U) | u16(offset + 2U); };
    if (!fits(0U, 12U)) return false;
    std::size_t offset = 0U;
    if (u32(0U) == text::open_type_tag::from_chars('t', 't', 'c', 'f').value) {
        if (face_index >= u32(8U) || !fits(12U, (static_cast<std::size_t>(face_index) + 1U) * 4U)) return false;
        offset = u32(12U + static_cast<std::size_t>(face_index) * 4U);
    } else if (face_index != 0U) return false;
    if (!fits(offset, 12U) || u32(offset) != text::open_type_tag::from_chars('O', 'T', 'T', 'O').value) return false;
    const auto count = u16(offset + 4U);
    offset += 12U;
    if (!fits(offset, static_cast<std::size_t>(count) * 16U)) return false;
    std::uint32_t seen = 0U;
    for (std::uint32_t index = 0U; index < count; ++index, offset += 16U) {
        const auto tag = u32(offset);
        if (!fits(u32(offset + 8U), u32(offset + 12U))) return false;
        if (tag == text::open_type_tag::from_chars('g', 'l', 'y', 'f').value ||
            tag == text::open_type_tag::from_chars('l', 'o', 'c', 'a').value ||
            tag == text::open_type_tag::from_chars('g', 'v', 'a', 'r').value) return false;
        constexpr std::array tags{text::open_type_tag::from_chars('C', 'F', 'F', ' ').value,
            text::open_type_tag::from_chars('C', 'F', 'F', '2').value,
            text::open_type_tag::from_chars('f', 'v', 'a', 'r').value,
            text::open_type_tag::from_chars('a', 'v', 'a', 'r').value,
            text::open_type_tag::from_chars('H', 'V', 'A', 'R').value};
        for (std::size_t selected = 0U; selected < tags.size(); ++selected) {
            if (tag != tags[selected]) continue;
            const auto bit = 1U << selected;
            if ((seen & bit) != 0U) return false;
            seen |= bit;
        }
    }
    return (seen & 3U) == (cff2 ? 2U : 1U);
}

// Source-specific metadata preflight. The general raw CFF reader keeps its
// original contract. In particular, source admission cannot silently discard a
// FontMatrix, stroked PaintType, synthetic font or non-Type2 CharString program.
struct cff_source_dictionary final {
    text::sfnt_cff_outline_transform matrix{};
    bool has_matrix = false;
    bool cid = false;
};

struct cff_source_matrix final {
    text::sfnt_cff_outline_transform original{};
    bool emits_contours = true;
};

inline bool cff_source_translation_is_zero(double value) noexcept
{
    // Original DirectWrite observations distinguish the two floats immediately
    // below 2^-17. The matched signed 16.16 arithmetic rounds the absolute float
    // value after adding one half; the addition itself rounds to float. This is a
    // source numeric conversion, not a geometric epsilon or matrix inversion.
    const float scaled = std::abs(static_cast<float>(value)) * 65536.0F;
    const float rounded = scaled + 0.5F;
    return rounded < 1.0F;
}

inline com::result read_cff_source_dictionary(std::span<const std::byte> bytes,
    cff_source_dictionary& output) noexcept
{
    cff_source_dictionary candidate{};
    std::array<double, 48U> operands{};
    std::size_t count = 0U, cursor = 0U;
    std::uint32_t seen = 0U;
    while (cursor < bytes.size()) {
        const auto value = std::to_integer<std::uint8_t>(bytes[cursor++]);
        if (value >= 28U && value != 31U && value != 255U) {
            double number = 0.0;
            if (count == operands.size() ||
                !text::sfnt_cff_data::try_read_dictionary_number(bytes, cursor, value, number) ||
                !std::isfinite(number)) return com::invalid_argument;
            operands[count++] = number;
            continue;
        }
        std::uint16_t operation = value;
        if (value == 12U) {
            if (cursor == bytes.size()) return com::invalid_argument;
            operation = static_cast<std::uint16_t>(0x0C00U |
                std::to_integer<std::uint8_t>(bytes[cursor++]));
        }
        std::uint32_t bit = 0U;
        if (operation == 0x0C07U) {
            bit = 1U;
            if (count != 6U) return com::invalid_argument;
            candidate.matrix = {operands[0], operands[1], operands[2], operands[3], operands[4], operands[5]};
            candidate.has_matrix = true;
        } else if (operation == 0x0C05U || operation == 0x0C06U || operation == 0x0C21U) {
            bit = operation == 0x0C05U ? 2U : (operation == 0x0C06U ? 4U : 8U);
            if (count != 1U) return com::invalid_argument;
            const auto expected = operation == 0x0C06U ? 2.0 : 0.0;
            if (operands[0] != expected) return compat::not_implemented;
        } else if (operation == 0x0C14U || operation == 0x0C15U || operation == 0x0C17U) {
            return compat::not_implemented; // SyntheticBase, PostScript, BaseFontBlend
        } else if (operation == 0x0C1EU) {
            bit = 16U;
            if (count != 3U) return com::invalid_argument;
            candidate.cid = true;
        } else if (operation == 17U || operation == 18U || operation == 0x0C24U || operation == 0x0C25U) {
            bit = operation == 17U ? 32U : (operation == 18U ? 64U : (operation == 0x0C24U ? 128U : 256U));
            if (count != (operation == 18U ? 2U : 1U)) return com::invalid_argument;
            for (std::size_t index = 0U; index < count; ++index)
                if (operands[index] < 0.0 || operands[index] > std::numeric_limits<std::int32_t>::max() ||
                    std::trunc(operands[index]) != operands[index]) return com::invalid_argument;
        } else if (!(operation <= 5U || operation == 13U || operation == 14U ||
            operation == 15U || operation == 16U ||
            (operation >= 0x0C00U && operation <= 0x0C04U) || operation == 0x0C08U ||
            operation == 0x0C16U || operation == 0x0C1FU || operation == 0x0C20U ||
            operation == 0x0C22U || operation == 0x0C23U || operation == 0x0C26U)) {
            return compat::not_implemented;
        }
        if ((seen & bit) != 0U) return com::invalid_argument;
        seen |= bit;
        count = 0U;
    }
    if (count != 0U) return com::invalid_argument;
    output = candidate;
    return com::ok;
}

inline bool finite_cff_matrix(const text::sfnt_cff_outline_transform& m) noexcept
{
    return std::isfinite(m.m11) && std::isfinite(m.m12) && std::isfinite(m.m21) &&
        std::isfinite(m.m22) && std::isfinite(m.dx) && std::isfinite(m.dy);
}

inline text::sfnt_cff_outline_transform compose_cff_design_matrix(
    const text::sfnt_cff_outline_transform& local,
    const text::sfnt_cff_outline_transform& top, double units_per_em) noexcept
{
    // Row-vector order: selected FD glyph space -> top glyph space -> em ->
    // OpenType design units. No inverse, epsilon or inferred phantom origin.
    return {(local.m11 * top.m11 + local.m12 * top.m21) * units_per_em,
        (local.m11 * top.m12 + local.m12 * top.m22) * units_per_em,
        (local.m21 * top.m11 + local.m22 * top.m21) * units_per_em,
        (local.m21 * top.m12 + local.m22 * top.m22) * units_per_em,
        ((local.dx * top.m11 + local.dy * top.m21) + top.dx) * units_per_em,
        ((local.dx * top.m12 + local.dy * top.m22) + top.dy) * units_per_em};
}

inline com::result prepare_cff_source_matrices(text::sfnt_cff1_font_view font,
    std::uint16_t units_per_em, std::vector<cff_source_matrix>& output)
{
    if (font.bytes.size() < 4U || font.bytes[1] != std::byte{0}) return com::invalid_argument;
    std::size_t cursor = std::to_integer<std::uint8_t>(font.bytes[2]);
    text::sfnt_cff_index_view names{}, tops{};
    std::span<const std::byte> bytes{};
    if (!text::sfnt_cff_data::try_read_index(font.bytes, cursor, names) || names.count != 1U ||
        !text::sfnt_cff_data::try_read_index(font.bytes, cursor, tops) || tops.count != 1U ||
        !text::sfnt_cff_data::try_get_index_item(tops, 0U, bytes)) return com::invalid_argument;
    cff_source_dictionary top{};
    auto status = read_cff_source_dictionary(bytes, top);
    if (com::failed(status)) return status;
    if (top.cid != (font.font_dictionaries.count != 0U)) return com::invalid_argument;
    if (!top.has_matrix) top.matrix = {0.001, 0.0, 0.0, 0.001, 0.0, 0.0};
    const auto count = std::max(1U, font.font_dictionaries.count);
    std::vector<cff_source_matrix> candidate(count);
    for (std::uint32_t index = 0U; index < count; ++index) {
        cff_source_dictionary local{}; // Omitted FD matrix inherits the top frame.
        if (font.font_dictionaries.count != 0U) {
            if (!text::sfnt_cff_data::try_get_index_item(font.font_dictionaries, index, bytes))
                return com::invalid_argument;
            status = read_cff_source_dictionary(bytes, local);
            if (com::failed(status)) return status;
            if (local.cid) return com::invalid_argument;
        }
        auto& selected = candidate[index];
        selected.original = compose_cff_design_matrix(local.matrix, top.matrix, units_per_em);
        if (!finite_cff_matrix(selected.original)) return com::invalid_argument;
        // The original source emits no ink for a translated Top or selected FD
        // matrix, including translations which cancel after composition. The
        // first FD also gates the whole source face, even when another glyph/FD
        // is requested first. Preserve the full matrix and original bytes.
        selected.emits_contours = cff_source_translation_is_zero(top.matrix.dx) &&
            cff_source_translation_is_zero(top.matrix.dy) &&
            cff_source_translation_is_zero(local.matrix.dx) &&
            cff_source_translation_is_zero(local.matrix.dy) &&
            (index == 0U || candidate.front().emits_contours);
    }
    output = std::move(candidate);
    return com::ok;
}

} // namespace progpu::native::direct2d::detail
