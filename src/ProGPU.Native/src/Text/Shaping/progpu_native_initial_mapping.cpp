#include "progpu_native_initial_mapping_internal.hpp"

#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Exact allocation-free port of the initial scalar-to-glyph expansion in the
// ProGPU-owned OpenTypeTextShaper.GlyphSubstitutionBuffer at repository
// checkpoint 0a08efec. The borrowed FormD plan is searched in O(log R), and a
// scalar emits at most one Khmer prefix plus its stored decomposition. No
// foreign shaping implementation or data structure is used.

namespace progpu::native::text::detail {
namespace {

constexpr std::uint32_t non_breaking_hyphen = 0x2011U;
constexpr std::uint32_t hyphen = 0x2010U;
constexpr std::uint32_t khmer_prebase_vowel = 0x17C1U;

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

std::uint32_t read_u32(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return std::to_integer<std::uint32_t>(bytes[offset]) |
        (std::to_integer<std::uint32_t>(bytes[offset + 1U]) << 8U) |
        (std::to_integer<std::uint32_t>(bytes[offset + 2U]) << 16U) |
        (std::to_integer<std::uint32_t>(bytes[offset + 3U]) << 24U);
}

bool is_mark(std::uint32_t code_point) noexcept {
    const auto category = get_unicode_general_category(code_point);
    return category == unicode_general_category::nonspacing_mark ||
        category == unicode_general_category::spacing_combining_mark ||
        category == unicode_general_category::enclosing_mark;
}

bool is_khmer_split_matra(std::uint32_t code_point) noexcept {
    return code_point == 0x17BEU || code_point == 0x17BFU ||
        code_point == 0x17C0U || code_point == 0x17C4U ||
        code_point == 0x17C5U;
}

} // namespace

std::size_t initial_mapping::size() const noexcept {
    const std::size_t body = decomposition.empty()
        ? 1U
        : decomposition.size() / 4U;
    return body + (prefix == 0U ? 0U : 1U);
}

std::uint32_t initial_mapping::code_point_at(std::size_t index) const noexcept {
    if (prefix != 0U) {
        if (index == 0U) return prefix;
        --index;
    }
    return decomposition.empty()
        ? single
        : read_u32(decomposition, index * 4U);
}

bool try_resolve_initial_mapping(
    const sfnt_font_view& font,
    std::uint32_t code_point,
    open_type_complex_script complex_script,
    const unicode_normalization_data* normalization,
    initial_mapping& result,
    font_error* error) noexcept {
    result = {};
    std::uint16_t glyph = 0U;
    if (!font.try_get_glyph_index(code_point, glyph)) {
        set_error(error, font_error::invalid_face);
        return false;
    }

    std::span<const std::byte> decomposition{};
    const bool has_decomposition = normalization != nullptr &&
        normalization->try_get_decomposition(code_point, decomposition) &&
        !decomposition.empty();
    const bool indic_split =
        complex_script == open_type_complex_script::indic &&
        has_decomposition && is_mark(read_u32(decomposition, 0U));

    result.prefix = complex_script == open_type_complex_script::khmer &&
            is_khmer_split_matra(code_point)
        ? khmer_prebase_vowel
        : 0U;
    if (indic_split) {
        result.decomposition = decomposition;
    } else if (glyph == 0U && code_point == non_breaking_hyphen) {
        std::uint16_t hyphen_glyph = 0U;
        if (!font.try_get_glyph_index(hyphen, hyphen_glyph)) {
            set_error(error, font_error::invalid_face);
            return false;
        }
        if (hyphen_glyph != 0U) {
            result.single = hyphen;
        } else if (has_decomposition) {
            result.decomposition = decomposition;
        } else {
            result.single = code_point;
        }
    } else if (glyph == 0U && has_decomposition) {
        result.decomposition = decomposition;
    } else {
        result.single = code_point;
    }
    set_error(error, font_error::none);
    return true;
}

bool try_get_initial_mapping_count(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    open_type_complex_script complex_script,
    const unicode_normalization_data* normalization,
    std::uint32_t& result,
    font_error* error) noexcept {
    std::uint64_t count = 0U;
    for (const auto& scalar : input) {
        initial_mapping mapping{};
        if (!try_resolve_initial_mapping(
                font,
                scalar.code_point,
                complex_script,
                normalization,
                mapping,
                error)) {
            result = 0U;
            return false;
        }
        count += mapping.size();
        if (count > std::numeric_limits<std::uint32_t>::max()) {
            result = 0U;
            set_error(error, font_error::invalid_argument);
            return false;
        }
    }
    result = static_cast<std::uint32_t>(count);
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text::detail
