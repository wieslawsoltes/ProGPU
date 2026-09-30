#include "progpu_native.h"
#include "../src/Text/Interop/progpu_native_text_font_source.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <source_location>
#include <vector>

namespace {
using bytes = std::vector<std::uint8_t>;
template <typename T, std::size_t Count>
constexpr std::uint32_t wire_count(const std::array<T, Count>&) noexcept {
    static_assert(Count <= std::numeric_limits<std::uint32_t>::max());
    return static_cast<std::uint32_t>(Count);
}
unsigned checks = 0U;
void require(bool condition, std::source_location location = std::source_location::current()) {
    ++checks;
    if (!condition) {
        std::fprintf(stderr, "device advance check failed at line %u\n", location.line());
        std::abort();
    }
}
void put16(bytes& data, std::size_t offset, std::uint16_t value) {
    data[offset] = static_cast<std::uint8_t>(value >> 8U);
    data[offset + 1U] = static_cast<std::uint8_t>(value);
}
void put32(bytes& data, std::size_t offset, std::uint32_t value) {
    put16(data, offset, static_cast<std::uint16_t>(value >> 16U));
    put16(data, offset + 2U, static_cast<std::uint16_t>(value));
}
bytes font_bytes(bool with_device_table = true, std::uint8_t alternate_width = 255U) {
    // Independently specified maxp + hdmx: three glyphs, nonlinear device
    // records at 12 and 16 ppem, zero advances and explicit four-byte padding.
    bytes data(with_device_table ? 100U : 60U);
    put32(data, 0U, 0x00010000U);
    put16(data, 4U, with_device_table ? 2U : 1U);
    put32(data, 12U, 0x6D617870U);
    put32(data, 20U, with_device_table ? 44U : 28U);
    put32(data, 24U, 32U);
    const auto maxp = with_device_table ? 44U : 28U;
    put32(data, maxp, 0x00010000U);
    put16(data, maxp + 4U, 3U);
    if (with_device_table) {
        put32(data, 28U, 0x68646D78U);
        put32(data, 36U, 76U);
        put32(data, 40U, 24U);
        put16(data, 78U, 2U);
        put32(data, 80U, 8U);
        data[84U] = 12U; data[85U] = alternate_width;
        data[86U] = 1U; data[87U] = 0U; data[88U] = alternate_width;
        data[92U] = 16U; data[93U] = 4U;
        data[94U] = 2U; data[95U] = 3U; data[96U] = 4U;
    }
    return data;
}
struct context_owner final {
    progpu_native_text_context* value = nullptr;
    explicit context_owner(const bytes& data, std::uint32_t face_index = 0U) {
        require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
            data.data(), data.size(), face_index, nullptr, 0U, &value) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(value != nullptr);
    }
    ~context_owner() { progpu_native_text_context_destroy(value); }
    context_owner(const context_owner&) = delete;
    context_owner& operator=(const context_owner&) = delete;
};

void original_collection_identity_survives_context()
{
    // Original fixture provenance: font_bytes above and TTC wire assembly in
    // progpu_native_text_tests.cpp:collection_and_failure_paths_are_bounded.
    // Two distinct hdmx faces prove collection index is not the palette index.
    const std::array<bytes, 2> faces{font_bytes(true, 231U), font_bytes(true, 7U)};
    bytes collection(20U + faces[0].size() + faces[1].size());
    put32(collection, 0U, 0x74746366U);
    put32(collection, 4U, 0x00010000U);
    put32(collection, 8U, 2U);
    std::uint32_t offset = 20U;
    for (std::size_t index = 0U; index < faces.size(); ++index) {
        put32(collection, 12U + index * 4U, offset);
        std::copy(faces[index].begin(), faces[index].end(), collection.begin() + offset);
        // Table locations in this original two-table fixture are fixed.
        put32(collection, offset + 20U, offset + 44U);
        put32(collection, offset + 36U, offset + 76U);
        offset += static_cast<std::uint32_t>(faces[index].size());
    }
    using namespace progpu::native::text;
    std::shared_ptr<const owned_font_source> retained_primary, retained_fallback;
    {
        context_owner context(collection, 1U);
        retained_primary = select_context_font_source(context.value, 0U);
        require(retained_primary != nullptr && retained_primary->face_index == 1U);
        require(select_context_font_source(context.value, 0U) == retained_primary);
        std::uint32_t index = 99U;
        require(progpu_native_text_context_add_fallback_font(context.value,
            collection.data(), collection.size(), 0U, 901U, &index) == PROGPU_NATIVE_STATUS_SUCCESS && index == 1U);
        retained_fallback = select_context_font_source(context.value, 1U);
        require(retained_fallback != nullptr && retained_fallback != retained_primary && retained_fallback->face_index == 0U);
        require(retained_fallback->bytes == retained_primary->bytes);
        for (std::uint32_t next = 2U; next <= 20U; ++next) {
            const auto data = font_bytes(false);
            require(progpu_native_text_context_add_fallback_font(context.value, data.data(), data.size(),
                0U, 901U + next, &index) == PROGPU_NATIVE_STATUS_SUCCESS && index == next);
        }
        require(select_context_font_source(context.value, 1U) == retained_fallback);
        require(select_context_font_source(context.value, 21U) == nullptr && select_context_font_source(nullptr, 0U) == nullptr);
        std::fill(collection.begin(), collection.end(), std::uint8_t{0});
        const std::uint32_t glyph = 2U;
        for (const std::uint32_t palette : {0U, 1U}) {
            std::array<float, 3> output{-91.0F, -91.0F, -91.0F};
            std::uint32_t available = 0U;
            require(progpu_native_text_context_get_device_advances(context.value, palette, 12U,
                &glyph, 1U, output.data(), wire_count(output), &available) == PROGPU_NATIVE_STATUS_SUCCESS);
            require(available == 1U && output[0] == (palette == 0U ? 7.0F : 231.0F) &&
                output[1] == -91.0F && output[2] == -91.0F);
        }
    }
    require(retained_primary->face_index == 1U && retained_fallback->face_index == 0U &&
        retained_primary->bytes == retained_fallback->bytes && retained_primary->bytes[0] == std::byte{0x74});
}
}

int main() {
    original_collection_identity_survives_context();
    auto original = font_bytes();
    context_owner context(original);
    std::fill(original.begin(), original.end(), std::uint8_t{0}); // Context owns its bytes.
    const std::array<std::uint32_t, 9> indices{2U, 0U, 1U, 2U, 1U, 0U, 2U, 2U, 0U};
    const std::array<float, 3> expected{1.0F, 0.0F, 255.0F};
    std::array<float, 12> output{};
    std::uint32_t available = 99U;
    for (std::uint32_t count = 0U; count <= wire_count(indices); ++count) {
        for (unsigned repeat = 0U; repeat < 2U; ++repeat) {
            output.fill(-777.0F);
            require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
                indices.data(), count, output.data(), wire_count(output), &available) == PROGPU_NATIVE_STATUS_SUCCESS);
            require(available == 1U);
            for (std::size_t i = 0U; i < wire_count(output); ++i)
                require(output[i] == (i < count ? expected[indices[i]] : -777.0F));
        }
    }
    // More than the bounded cache's capacity, including absent ppem records.
    for (std::uint32_t ppem = 1U; ppem <= 40U; ++ppem) {
        output.fill(-777.0F);
        require(progpu_native_text_context_get_device_advances(context.value, 0U, ppem,
            indices.data(), wire_count(indices), output.data(), wire_count(output), &available) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(available == ((ppem == 12U || ppem == 16U) ? 1U : 0U));
        for (std::size_t i = 0U; i < wire_count(output); ++i) {
            const float value = i >= wire_count(indices) || available == 0U ? -777.0F :
                ppem == 12U ? expected[indices[i]] : static_cast<float>(indices[i] + 2U);
            require(output[i] == value);
        }
    }
    // Cache entries keep face identity and survive fallback-vector growth.
    for (std::uint32_t face = 1U; face <= 20U; ++face) {
        auto fallback = font_bytes(true, static_cast<std::uint8_t>(face + 4U));
        std::uint32_t index = 0U;
        require(progpu_native_text_context_add_fallback_font(context.value,
            fallback.data(), fallback.size(), 0U, face, &index) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(index == face);
        std::fill(fallback.begin(), fallback.end(), std::uint8_t{0});
        require(progpu_native_text_context_get_device_advances(context.value, face, 12U,
            indices.data(), wire_count(indices), output.data(), wire_count(output), &available) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(available == 1U && output[0U] == static_cast<float>(face + 4U));
    }
    for (const auto face : {0U, 1U, 20U}) {
        require(progpu_native_text_context_get_device_advances(context.value, face, 12U,
            indices.data(), wire_count(indices), output.data(), wire_count(output), &available) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(available == 1U && output[0U] == (face == 0U ? 255.0F : static_cast<float>(face + 4U)));
    }
    context_owner absent(font_bytes(false));
    auto damaged_bytes = font_bytes();
    damaged_bytes.back() = 1U; // Later malformed record invalidates the first too.
    context_owner damaged(damaged_bytes);
    output.fill(-777.0F);
    const auto sentinel = output;
    for (const auto ppem : {12U, 13U, 256U, 65535U}) {
        require(progpu_native_text_context_get_device_advances(absent.value, 0U, ppem,
            indices.data(), wire_count(indices), output.data(), wire_count(output), &available) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(available == 0U && output == sentinel);
    }
    require(progpu_native_text_context_get_device_advances(damaged.value, 0U, 12U,
        indices.data(), wire_count(indices), output.data(), wire_count(output), &available) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(available == 0U && output == sentinel);

    const auto rejects = [&](progpu_native_text_context* owner, std::uint32_t face,
        std::uint32_t ppem, const std::uint32_t* glyphs, std::uint32_t count,
        float* destination, std::uint32_t capacity,
        std::source_location location = std::source_location::current()) {
        available = 99U;
        require(progpu_native_text_context_get_device_advances(owner, face, ppem,
            glyphs, count, destination, capacity, &available) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, location);
        require(available == 0U && output == sentinel, location);
    };
    rejects(nullptr, 0U, 12U, indices.data(), wire_count(indices), output.data(), wire_count(output));
    rejects(context.value, 21U, 12U, indices.data(), wire_count(indices), output.data(), wire_count(output));
    for (const auto ppem : {0U, 65536U, std::numeric_limits<std::uint32_t>::max()})
        rejects(context.value, 0U, ppem, indices.data(), wire_count(indices), output.data(), wire_count(output));
    rejects(context.value, 0U, 12U, nullptr, 1U, output.data(), wire_count(output));
    rejects(context.value, 0U, 12U, indices.data(), wire_count(indices), nullptr, wire_count(indices));
    rejects(context.value, 0U, 12U, indices.data(), wire_count(indices), output.data(), wire_count(indices) - 1U);
    // The entire declared span must fit even for the intentionally misaligned
    // destination. Otherwise its overrun can alias availability on a different
    // compiler's stack, correctly selecting the all-storage-preserved contract.
    alignas(std::uint32_t) std::array<std::byte, sizeof(output) + alignof(float)> unaligned{};
    unaligned.fill(std::byte{0x5A});
    const auto unaligned_sentinel = unaligned;
    rejects(context.value, 0U, 12U, reinterpret_cast<const std::uint32_t*>(unaligned.data() + 1U), 1U,
        output.data(), wire_count(output));
    rejects(context.value, 0U, 12U, indices.data(), wire_count(indices),
        reinterpret_cast<float*>(unaligned.data() + 1U), wire_count(indices));
    require(unaligned == unaligned_sentinel);
    rejects(context.value, 0U, 12U,
        reinterpret_cast<const std::uint32_t*>(std::numeric_limits<std::uintptr_t>::max() - 3U),
        2U, output.data(), wire_count(output));
    for (const auto bad : {3U, 65535U, 0x80000000U, 0xFFFFFFFFU}) {
        for (std::size_t slot = 0U; slot < wire_count(indices); ++slot) {
            auto invalid = indices;
            invalid[slot] = bad;
            for (const auto ppem : {12U, 13U})
                rejects(context.value, 0U, ppem, invalid.data(), wire_count(invalid), output.data(), wire_count(output));
        }
    }
    require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
        indices.data(), wire_count(indices), output.data(), wire_count(output), nullptr) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(output == sentinel);
    auto aliased = indices;
    const auto saved = aliased;
    require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
        aliased.data(), wire_count(aliased), output.data(), wire_count(output), aliased.data()) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(aliased == saved && output == sentinel);
    require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
        indices.data(), wire_count(indices), output.data(), wire_count(output),
        reinterpret_cast<std::uint32_t*>(output.data() + 11U)) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(output == sentinel);
    available = 99U;
    require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
        aliased.data(), wire_count(aliased), reinterpret_cast<float*>(aliased.data()),
        wire_count(aliased), &available) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(aliased == saved && available == 99U);
    require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
        nullptr, 0U, nullptr, 0U, &available) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(available == 1U);
    std::printf("native device advances: %u checks passed\n", checks);
}
