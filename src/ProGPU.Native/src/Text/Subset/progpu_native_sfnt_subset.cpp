#include "progpu_native_sfnt_subset_internal.hpp"

#include <algorithm>
#include <vector>

// Direct native port provenance: ProGPU-owned
// ProGPU.Text.SfntFontSubsetter at checkpoint edf3ea85. The public two-pass
// surface preserves its glyph-ID, composite-closure, table, checksum, and
// fail-closed contracts without exposing STL ownership across an ABI.
namespace progpu::native::text {
namespace {

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

bool build(
    std::span<const std::byte> font_data,
    std::size_t directory_offset,
    std::span<const std::uint16_t> glyphs,
    std::vector<std::byte>& subset,
    font_error* error) noexcept {
    subset.clear();
    set_error(error, font_error::none);
    try {
        subset = sfnt_subset_detail::build_glyph_id_preserving_subset(
            font_data, directory_offset, glyphs);
        return true;
    } catch (...) {
        subset.clear();
        set_error(error, font_error::invalid_face);
        return false;
    }
}

} // namespace

bool try_get_glyph_id_preserving_sfnt_subset_requirements(
    std::span<const std::byte> font_data,
    std::size_t directory_offset,
    std::span<const std::uint16_t> glyphs,
    sfnt_subset_requirements& result,
    font_error* error) noexcept {
    result = {};
    std::vector<std::byte> subset;
    if (!build(font_data, directory_offset, glyphs, subset, error)) {
        return false;
    }
    result.font_bytes = subset.size();
    return true;
}

bool try_create_glyph_id_preserving_sfnt_subset(
    std::span<const std::byte> font_data,
    std::size_t directory_offset,
    std::span<const std::uint16_t> glyphs,
    std::span<std::byte> output,
    sfnt_subset_requirements& result,
    font_error* error) noexcept {
    result = {};
    std::vector<std::byte> subset;
    if (!build(font_data, directory_offset, glyphs, subset, error)) {
        return false;
    }
    result.font_bytes = subset.size();
    if (output.size() < subset.size()) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    std::copy(subset.begin(), subset.end(), output.begin());
    return true;
}

} // namespace progpu::native::text
