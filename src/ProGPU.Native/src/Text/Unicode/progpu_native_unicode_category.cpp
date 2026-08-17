#include "progpu_native_text.hpp"

#include "progpu_native_unicode_categories.generated.hpp"

#include <cstddef>
#include <cstdint>

// Direct native port provenance: ProGPU-owned
// UnicodeShapingProperties.GetGeneralCategory. The generated ranges preserve
// the exact .NET 10 category values used by the managed implementation.

namespace progpu::native::text {

unicode_general_category get_unicode_general_category(
    std::uint32_t code_point) noexcept {
    if (code_point > 0x10FFFFU ||
        (code_point >= 0xD800U && code_point <= 0xDFFFU)) {
        return unicode_general_category::other_not_assigned;
    }
    const auto& ranges = detail::unicode_general_category_ranges;
    std::size_t low = 0U;
    std::size_t high = ranges.size() / 3U;
    while (low < high) {
        const std::size_t middle = low + (high - low) / 2U;
        const std::size_t offset = middle * 3U;
        if (code_point < ranges[offset]) {
            high = middle;
        } else if (code_point > ranges[offset + 1U]) {
            low = middle + 1U;
        } else {
            return static_cast<unicode_general_category>(
                ranges[offset + 2U]);
        }
    }
    return unicode_general_category::other_not_assigned;
}

} // namespace progpu::native::text
