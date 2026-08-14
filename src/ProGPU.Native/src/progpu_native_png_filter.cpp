#include "progpu_native_png_internal.hpp"

#include <algorithm>

// Direct native port provenance: ProGPU-owned managed PNG codec contracts.
// Each PNG row is reconstructed once in O(W * H) time and O(1) extra storage;
// the caller owns the filtered byte buffer and it is updated in place.
namespace progpu::native::image::detail {
namespace {

std::uint8_t paeth(
    std::uint8_t left,
    std::uint8_t above,
    std::uint8_t upper_left) noexcept {
    const auto a = static_cast<int>(left);
    const auto b = static_cast<int>(above);
    const auto c = static_cast<int>(upper_left);
    const auto prediction = a + b - c;
    const auto distance_a = prediction > a ? prediction - a : a - prediction;
    const auto distance_b = prediction > b ? prediction - b : b - prediction;
    const auto distance_c = prediction > c ? prediction - c : c - prediction;
    if (distance_a <= distance_b && distance_a <= distance_c) {
        return left;
    }
    return distance_b <= distance_c ? above : upper_left;
}

} // namespace

bool try_unfilter_pass(
    std::span<std::byte> filtered,
    std::uint32_t height,
    std::size_t row_bytes,
    std::size_t bytes_per_pixel) noexcept {
    const auto stride = row_bytes + 1U;
    for (std::size_t row = 0U; row < height; ++row) {
        const auto row_offset = row * stride;
        const auto filter =
            std::to_integer<std::uint8_t>(filtered[row_offset]);
        if (filter > 4U) {
            return false;
        }
        for (std::size_t column = 0U; column < row_bytes; ++column) {
            const auto index = row_offset + 1U + column;
            const auto encoded =
                std::to_integer<std::uint8_t>(filtered[index]);
            const auto left = column >= bytes_per_pixel
                ? std::to_integer<std::uint8_t>(
                    filtered[index - bytes_per_pixel])
                : 0U;
            const auto above = row != 0U
                ? std::to_integer<std::uint8_t>(filtered[index - stride])
                : 0U;
            const auto upper_left = row != 0U && column >= bytes_per_pixel
                ? std::to_integer<std::uint8_t>(
                    filtered[index - stride - bytes_per_pixel])
                : 0U;
            std::uint8_t predictor = 0U;
            switch (filter) {
            case 1U:
                predictor = static_cast<std::uint8_t>(left);
                break;
            case 2U:
                predictor = static_cast<std::uint8_t>(above);
                break;
            case 3U:
                predictor = static_cast<std::uint8_t>((left + above) / 2U);
                break;
            case 4U:
                predictor = paeth(
                    static_cast<std::uint8_t>(left),
                    static_cast<std::uint8_t>(above),
                    static_cast<std::uint8_t>(upper_left));
                break;
            default:
                break;
            }
            filtered[index] = static_cast<std::byte>(
                static_cast<std::uint8_t>(encoded + predictor));
        }
    }
    return true;
}

bool try_unfilter_png(
    const png_layout& layout,
    std::span<std::byte> filtered) noexcept {
    if (filtered.size() != layout.filtered_bytes) {
        return false;
    }
    std::size_t offset = 0U;
    for (std::size_t index = 0U; index < layout.pass_count; ++index) {
        const auto& pass = layout.passes[index];
        if (!try_unfilter_pass(
                filtered.subspan(offset, pass.filtered_bytes),
                pass.height,
                pass.row_bytes,
                layout.filter_bytes_per_pixel)) {
            return false;
        }
        offset += pass.filtered_bytes;
    }
    return offset == filtered.size();
}

} // namespace progpu::native::image::detail
