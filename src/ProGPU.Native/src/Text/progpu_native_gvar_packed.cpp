#include "progpu_native_text.hpp"

#include "progpu_native_font_bytes.hpp"

#include <cstddef>
#include <limits>

// Direct native port provenance: ProGPU-owned
// OpenTypeVariationData.ReadPackedPoints/ReadPackedDeltas at checkpoint
// 26da237d. Count and write passes are caller-owned and transactional.

namespace progpu::native::text {
namespace {

using detail::can_read;
using detail::read_i16;
using detail::read_u16;

void set_error(font_error* destination, font_error value) noexcept {
    if (destination != nullptr) {
        *destination = value;
    }
}

} // namespace

bool sfnt_packed_variation_data::try_get_point_requirements(
    std::span<const std::byte> data,
    sfnt_packed_point_requirements& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    if (data.empty()) {
        set_error(error, font_error::invalid_glyph);
        return false;
    }
    std::size_t offset = 1U;
    const auto first = std::to_integer<std::uint8_t>(data[0U]);
    if (first == 0U) {
        result.bytes_consumed = 1U;
        result.all_points = true;
        return true;
    }
    std::uint32_t count = first;
    if ((first & 0x80U) != 0U) {
        if (!can_read(data, offset, 1U)) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        count = ((first & 0x7FU) << 8U) |
            std::to_integer<std::uint8_t>(data[offset++]);
    }
    std::uint32_t written = 0U;
    std::uint32_t current = 0U;
    while (written < count) {
        if (!can_read(data, offset, 1U)) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        const auto control =
            std::to_integer<std::uint8_t>(data[offset++]);
        const auto run_count = static_cast<std::uint32_t>(
            (control & 0x7FU) + 1U);
        const bool words = (control & 0x80U) != 0U;
        if (run_count > count - written ||
            !can_read(data, offset, run_count * (words ? 2U : 1U))) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        for (std::uint32_t index = 0U; index < run_count; ++index) {
            const std::uint32_t delta = words
                ? read_u16(data, offset + index * 2U)
                : std::to_integer<std::uint8_t>(data[offset + index]);
            if (delta > std::numeric_limits<std::uint32_t>::max() - current) {
                set_error(error, font_error::invalid_glyph);
                return false;
            }
            current += delta;
        }
        offset += run_count * (words ? 2U : 1U);
        written += run_count;
    }
    result.point_count = count;
    result.bytes_consumed = offset;
    return true;
}

bool sfnt_packed_variation_data::try_decode_points(
    std::span<const std::byte> data,
    std::span<std::uint32_t> points,
    std::uint32_t& written,
    std::size_t& bytes_consumed,
    font_error* error) noexcept {
    written = 0U;
    bytes_consumed = 0U;
    sfnt_packed_point_requirements requirements{};
    if (!try_get_point_requirements(data, requirements, error)) {
        return false;
    }
    if (points.size() < requirements.point_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    bytes_consumed = requirements.bytes_consumed;
    if (requirements.all_points) {
        return true;
    }
    std::size_t offset = (std::to_integer<std::uint8_t>(data[0U]) & 0x80U)
        != 0U ? 2U : 1U;
    std::uint32_t current = 0U;
    while (written < requirements.point_count) {
        const auto control =
            std::to_integer<std::uint8_t>(data[offset++]);
        const auto run_count = static_cast<std::uint32_t>(
            (control & 0x7FU) + 1U);
        const bool words = (control & 0x80U) != 0U;
        for (std::uint32_t index = 0U; index < run_count; ++index) {
            current += words
                ? read_u16(data, offset + index * 2U)
                : std::to_integer<std::uint8_t>(data[offset + index]);
            points[written++] = current;
        }
        offset += run_count * (words ? 2U : 1U);
    }
    set_error(error, font_error::none);
    return true;
}

bool sfnt_packed_variation_data::try_get_delta_requirements(
    std::span<const std::byte> data,
    std::uint32_t delta_count,
    sfnt_packed_delta_requirements& result,
    font_error* error) noexcept {
    result = {};
    set_error(error, font_error::none);
    std::size_t offset = 0U;
    std::uint32_t written = 0U;
    while (written < delta_count) {
        if (!can_read(data, offset, 1U)) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        const auto control =
            std::to_integer<std::uint8_t>(data[offset++]);
        const auto run_count = static_cast<std::uint32_t>(
            (control & 0x3FU) + 1U);
        if (run_count > delta_count - written) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        const bool zeros = (control & 0x80U) != 0U;
        const bool words = (control & 0x40U) != 0U;
        const std::size_t value_size = zeros ? 0U : words ? 2U : 1U;
        if (!can_read(data, offset, run_count * value_size)) {
            set_error(error, font_error::invalid_glyph);
            return false;
        }
        offset += run_count * value_size;
        written += run_count;
    }
    result.delta_count = delta_count;
    result.bytes_consumed = offset;
    return true;
}

bool sfnt_packed_variation_data::try_decode_deltas(
    std::span<const std::byte> data,
    std::span<std::int16_t> deltas,
    std::uint32_t delta_count,
    std::uint32_t& written,
    std::size_t& bytes_consumed,
    font_error* error) noexcept {
    written = 0U;
    bytes_consumed = 0U;
    sfnt_packed_delta_requirements requirements{};
    if (!try_get_delta_requirements(
            data,
            delta_count,
            requirements,
            error)) {
        return false;
    }
    if (deltas.size() < delta_count) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }
    std::size_t offset = 0U;
    while (written < delta_count) {
        const auto control =
            std::to_integer<std::uint8_t>(data[offset++]);
        const auto run_count = static_cast<std::uint32_t>(
            (control & 0x3FU) + 1U);
        const bool zeros = (control & 0x80U) != 0U;
        const bool words = (control & 0x40U) != 0U;
        for (std::uint32_t index = 0U; index < run_count; ++index) {
            deltas[written++] = zeros
                ? 0
                : words
                    ? read_i16(data, offset + index * 2U)
                    : static_cast<std::int8_t>(
                        std::to_integer<std::uint8_t>(
                            data[offset + index]));
        }
        if (!zeros) {
            offset += run_count * (words ? 2U : 1U);
        }
    }
    bytes_consumed = requirements.bytes_consumed;
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
