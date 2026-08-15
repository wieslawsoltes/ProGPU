#include "progpu_native_text.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port of ProGPU-owned UnicodeNormalizationPlan.cs and the
// normalization boundary in OpenTypeTextShaper.cs at checkpoint 3d21b111.
// The binary plan remains one shared repository resource and is borrowed.

namespace progpu::native::text {
namespace {

constexpr std::uint32_t normalization_magic = 0x4E554750U;
constexpr std::uint32_t normalization_version = 1U;
constexpr std::size_t header_size = 20U;
constexpr std::size_t record_size = 12U;

void set_error(unicode_error* error, unicode_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool try_add(std::size_t left, std::size_t right, std::size_t& result) noexcept {
    if (right > std::numeric_limits<std::size_t>::max() - left) {
        return false;
    }
    result = left + right;
    return true;
}

bool try_multiply(
    std::size_t left,
    std::size_t right,
    std::size_t& result) noexcept {
    if (left != 0U && right > std::numeric_limits<std::size_t>::max() / left) {
        return false;
    }
    result = left * right;
    return true;
}

std::uint32_t read_u32(
    std::span<const std::byte> bytes,
    std::size_t offset) noexcept {
    return std::to_integer<std::uint32_t>(bytes[offset]) |
        (std::to_integer<std::uint32_t>(bytes[offset + 1U]) << 8U) |
        (std::to_integer<std::uint32_t>(bytes[offset + 2U]) << 16U) |
        (std::to_integer<std::uint32_t>(bytes[offset + 3U]) << 24U);
}

bool is_scalar(std::uint32_t value) noexcept {
    return value <= 0x10FFFFU &&
        (value < 0xD800U || value > 0xDFFFU);
}

unicode_scalar make_component(
    std::uint32_t code_point,
    const unicode_scalar& source) noexcept {
    return unicode_scalar{
        code_point,
        source.input_index,
        source.input_length,
        get_unicode_canonical_combining_class(code_point),
        0U,
        get_unicode_script(code_point)};
}

void merge_source_range(
    unicode_scalar& destination,
    const unicode_scalar& source) noexcept {
    const std::uint64_t destination_end =
        static_cast<std::uint64_t>(destination.input_index) +
        destination.input_length;
    const std::uint64_t source_end =
        static_cast<std::uint64_t>(source.input_index) + source.input_length;
    const std::uint32_t start =
        std::min(destination.input_index, source.input_index);
    const std::uint64_t end = std::max(destination_end, source_end);
    destination.input_index = start;
    destination.input_length = static_cast<std::uint16_t>(std::min<std::uint64_t>(
        end - start,
        std::numeric_limits<std::uint16_t>::max()));
}

} // namespace

bool unicode_normalization_data::try_create(
    std::span<const std::byte> bytes,
    unicode_normalization_data& result,
    unicode_error* error) noexcept {
    result = {};
    if (bytes.size() < header_size || read_u32(bytes, 0U) != normalization_magic ||
        read_u32(bytes, 4U) != normalization_version) {
        set_error(error, unicode_error::invalid_argument);
        return false;
    }
    const std::uint32_t decomposition_count = read_u32(bytes, 8U);
    const std::uint32_t scalar_count = read_u32(bytes, 12U);
    const std::uint32_t composition_count = read_u32(bytes, 16U);
    std::size_t decomposition_bytes = 0U;
    std::size_t scalar_bytes = 0U;
    std::size_t composition_bytes = 0U;
    std::size_t scalar_offset = 0U;
    std::size_t composition_offset = 0U;
    std::size_t expected_size = 0U;
    if (!try_multiply(decomposition_count, record_size, decomposition_bytes) ||
        !try_multiply(scalar_count, 4U, scalar_bytes) ||
        !try_multiply(composition_count, record_size, composition_bytes) ||
        !try_add(header_size, decomposition_bytes, scalar_offset) ||
        !try_add(scalar_offset, scalar_bytes, composition_offset) ||
        !try_add(composition_offset, composition_bytes, expected_size) ||
        expected_size != bytes.size()) {
        set_error(error, unicode_error::invalid_argument);
        return false;
    }

    std::uint32_t previous = 0U;
    for (std::uint32_t index = 0U; index < decomposition_count; ++index) {
        const std::size_t offset = header_size + index * record_size;
        const std::uint32_t code_point = read_u32(bytes, offset);
        const std::uint32_t scalar_index = read_u32(bytes, offset + 4U);
        const std::uint32_t count = read_u32(bytes, offset + 8U);
        if (!is_scalar(code_point) || (index != 0U && code_point <= previous) ||
            scalar_index > scalar_count || count > scalar_count - scalar_index) {
            set_error(error, unicode_error::invalid_argument);
            return false;
        }
        previous = code_point;
        for (std::uint32_t component = 0U; component < count; ++component) {
            if (!is_scalar(read_u32(
                    bytes,
                    scalar_offset +
                        static_cast<std::size_t>(scalar_index + component) * 4U))) {
                set_error(error, unicode_error::invalid_argument);
                return false;
            }
        }
    }

    std::uint64_t previous_pair = 0U;
    for (std::uint32_t index = 0U; index < composition_count; ++index) {
        const std::size_t offset = composition_offset + index * record_size;
        const std::uint32_t first = read_u32(bytes, offset);
        const std::uint32_t second = read_u32(bytes, offset + 4U);
        const std::uint32_t composed = read_u32(bytes, offset + 8U);
        const std::uint64_t pair =
            (static_cast<std::uint64_t>(first) << 32U) | second;
        if (!is_scalar(first) || !is_scalar(second) || !is_scalar(composed) ||
            (index != 0U && pair <= previous_pair)) {
            set_error(error, unicode_error::invalid_argument);
            return false;
        }
        previous_pair = pair;
    }

    result.bytes_ = bytes;
    result.decomposition_offset_ = header_size;
    result.scalar_offset_ = scalar_offset;
    result.composition_offset_ = composition_offset;
    result.decomposition_count_ = decomposition_count;
    result.scalar_count_ = scalar_count;
    result.composition_count_ = composition_count;
    set_error(error, unicode_error::none);
    return true;
}

bool unicode_normalization_data::try_get_decomposition(
    std::uint32_t code_point,
    std::span<const std::byte>& little_endian_scalars) const noexcept {
    little_endian_scalars = {};
    std::uint32_t low = 0U;
    std::uint32_t high = decomposition_count_;
    while (low < high) {
        const std::uint32_t middle = low + (high - low) / 2U;
        const std::size_t offset =
            decomposition_offset_ + static_cast<std::size_t>(middle) * record_size;
        const std::uint32_t current = read_u32(bytes_, offset);
        if (code_point < current) {
            high = middle;
        } else if (code_point > current) {
            low = middle + 1U;
        } else {
            const std::uint32_t scalar_index = read_u32(bytes_, offset + 4U);
            const std::uint32_t count = read_u32(bytes_, offset + 8U);
            little_endian_scalars = bytes_.subspan(
                scalar_offset_ + static_cast<std::size_t>(scalar_index) * 4U,
                static_cast<std::size_t>(count) * 4U);
            return true;
        }
    }
    return false;
}

bool unicode_normalization_data::try_compose(
    std::uint32_t first,
    std::uint32_t second,
    std::uint32_t& composed) const noexcept {
    composed = 0U;
    const std::uint64_t requested =
        (static_cast<std::uint64_t>(first) << 32U) | second;
    std::uint32_t low = 0U;
    std::uint32_t high = composition_count_;
    while (low < high) {
        const std::uint32_t middle = low + (high - low) / 2U;
        const std::size_t offset =
            composition_offset_ + static_cast<std::size_t>(middle) * record_size;
        const std::uint64_t current =
            (static_cast<std::uint64_t>(read_u32(bytes_, offset)) << 32U) |
            read_u32(bytes_, offset + 4U);
        if (requested < current) {
            high = middle;
        } else if (requested > current) {
            low = middle + 1U;
        } else {
            composed = read_u32(bytes_, offset + 8U);
            return true;
        }
    }
    return false;
}

bool try_get_unicode_normalization_requirements(
    std::span<const unicode_scalar> input,
    const unicode_normalization_data& data,
    unicode_normalization_requirements& result,
    unicode_error* error) noexcept {
    result = {};
    std::uint64_t count = 0U;
    for (const auto& scalar : input) {
        if (!is_scalar(scalar.code_point)) {
            set_error(error, unicode_error::invalid_argument);
            return false;
        }
        std::span<const std::byte> decomposition{};
        count += data.try_get_decomposition(scalar.code_point, decomposition)
            ? decomposition.size() / 4U
            : 1U;
        if (count > std::numeric_limits<std::uint32_t>::max()) {
            set_error(error, unicode_error::invalid_argument);
            return false;
        }
    }
    result.scalar_capacity = static_cast<std::uint32_t>(count);
    set_error(error, unicode_error::none);
    return true;
}

bool try_normalize_unicode(
    std::span<const unicode_scalar> input,
    const unicode_normalization_data& data,
    unicode_normalization_form form,
    std::span<unicode_scalar> output,
    std::uint32_t& written,
    unicode_error* error) noexcept {
    written = 0U;
    if (form != unicode_normalization_form::canonical_decomposition &&
        form != unicode_normalization_form::canonical_composition) {
        set_error(error, unicode_error::invalid_argument);
        return false;
    }
    unicode_normalization_requirements requirements{};
    if (!try_get_unicode_normalization_requirements(
            input, data, requirements, error)) {
        return false;
    }
    if (output.size() < requirements.scalar_capacity) {
        set_error(error, unicode_error::insufficient_buffer);
        return false;
    }
    if (!input.empty() && input.data() == output.data()) {
        set_error(error, unicode_error::invalid_argument);
        return false;
    }

    std::uint32_t count = 0U;
    for (const auto& scalar : input) {
        std::span<const std::byte> decomposition{};
        if (!data.try_get_decomposition(scalar.code_point, decomposition)) {
            output[count++] = make_component(scalar.code_point, scalar);
            continue;
        }
        for (std::size_t offset = 0U;
             offset < decomposition.size();
             offset += 4U) {
            output[count++] = make_component(
                read_u32(decomposition, offset), scalar);
        }
    }

    for (std::uint32_t index = 1U; index < count; ++index) {
        const unicode_scalar value = output[index];
        if (value.canonical_combining_class == 0U) {
            continue;
        }
        std::uint32_t destination = index;
        while (destination > 0U) {
            const std::uint8_t previous_class =
                output[destination - 1U].canonical_combining_class;
            if (previous_class == 0U ||
                previous_class <= value.canonical_combining_class) {
                break;
            }
            output[destination] = output[destination - 1U];
            --destination;
        }
        output[destination] = value;
    }

    if (form == unicode_normalization_form::canonical_composition && count > 1U) {
        std::uint32_t destination = 1U;
        std::uint32_t starter = 0U;
        std::uint8_t previous_class = 0U;
        for (std::uint32_t source = 1U; source < count; ++source) {
            const unicode_scalar current = output[source];
            const std::uint8_t current_class = current.canonical_combining_class;
            std::uint32_t composed = 0U;
            const bool blocked = previous_class != 0U &&
                previous_class >= current_class;
            if (!blocked && data.try_compose(
                    output[starter].code_point,
                    current.code_point,
                    composed)) {
                merge_source_range(output[starter], current);
                output[starter].code_point = composed;
                output[starter].canonical_combining_class =
                    get_unicode_canonical_combining_class(composed);
                output[starter].script = get_unicode_script(composed);
                continue;
            }
            output[destination] = current;
            if (current_class == 0U) {
                starter = destination;
            }
            previous_class = current_class;
            ++destination;
        }
        count = destination;
    }

    written = count;
    set_error(error, unicode_error::none);
    return true;
}

} // namespace progpu::native::text
