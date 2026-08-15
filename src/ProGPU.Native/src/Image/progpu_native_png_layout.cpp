#include "progpu_native_png_internal.hpp"

#include <array>
#include <limits>

// Direct native port provenance: ProGPU-owned PNG decode contracts at
// checkpoint 93259231, constrained by PNG Third Edition section 6.5. Layout
// construction is fixed O(1) time and storage for at most seven Adam7 passes.
namespace progpu::native::image::detail {
namespace {

struct adam7_pass final {
    std::uint32_t start_x;
    std::uint32_t start_y;
    std::uint32_t step_x;
    std::uint32_t step_y;
};

constexpr std::array<adam7_pass, 7U> adam7{{
    {0U, 0U, 8U, 8U},
    {4U, 0U, 8U, 8U},
    {0U, 4U, 4U, 8U},
    {2U, 0U, 4U, 4U},
    {0U, 2U, 2U, 4U},
    {1U, 0U, 2U, 2U},
    {0U, 1U, 1U, 2U}}};

bool checked_multiply(
    std::size_t left,
    std::size_t right,
    std::size_t& result) noexcept {
    if (left != 0U &&
        right > std::numeric_limits<std::size_t>::max() / left) {
        return false;
    }
    result = left * right;
    return true;
}

bool checked_add(
    std::size_t left,
    std::size_t right,
    std::size_t& result) noexcept {
    if (right > std::numeric_limits<std::size_t>::max() - left) {
        return false;
    }
    result = left + right;
    return true;
}

std::uint32_t pass_extent(
    std::uint32_t extent,
    std::uint32_t start,
    std::uint32_t step) noexcept {
    return extent <= start ? 0U : 1U + (extent - 1U - start) / step;
}

bool append_pass(
    png_layout& layout,
    std::uint32_t image_width,
    std::uint32_t image_height,
    std::size_t bits_per_pixel,
    const adam7_pass& descriptor) noexcept {
    const auto width =
        pass_extent(image_width, descriptor.start_x, descriptor.step_x);
    const auto height =
        pass_extent(image_height, descriptor.start_y, descriptor.step_y);
    if (width == 0U || height == 0U) {
        return true;
    }
    std::size_t row_bits = 0U;
    if (!checked_multiply(width, bits_per_pixel, row_bits) ||
        row_bits > std::numeric_limits<std::size_t>::max() - 7U) {
        return false;
    }
    const auto row_bytes = (row_bits + 7U) / 8U;
    std::size_t stride = 0U;
    std::size_t filtered_bytes = 0U;
    std::size_t next_total = 0U;
    if (!checked_add(row_bytes, 1U, stride) ||
        !checked_multiply(height, stride, filtered_bytes) ||
        !checked_add(layout.filtered_bytes, filtered_bytes, next_total)) {
        return false;
    }
    layout.passes[layout.pass_count++] = {
        descriptor.start_x,
        descriptor.start_y,
        descriptor.step_x,
        descriptor.step_y,
        width,
        height,
        row_bytes,
        filtered_bytes};
    layout.filtered_bytes = next_total;
    return true;
}

} // namespace

bool try_build_png_layout(
    const png_decode_requirements& requirements,
    png_layout& layout) noexcept {
    layout = {};
    std::size_t bits_per_pixel = 0U;
    if (!checked_multiply(
            requirements.channel_count,
            requirements.bit_depth,
            bits_per_pixel) ||
        bits_per_pixel == 0U) {
        return false;
    }
    layout.filter_bytes_per_pixel =
        bits_per_pixel < 8U ? 1U : (bits_per_pixel + 7U) / 8U;
    if (requirements.interlace_method == 0U) {
        return append_pass(layout,
            requirements.width,
            requirements.height,
            bits_per_pixel,
            {0U, 0U, 1U, 1U});
    }
    if (requirements.interlace_method != 1U) {
        return false;
    }
    for (const auto& descriptor : adam7) {
        if (!append_pass(layout,
                requirements.width,
                requirements.height,
                bits_per_pixel,
                descriptor)) {
            return false;
        }
    }
    return layout.pass_count != 0U;
}

} // namespace progpu::native::image::detail
