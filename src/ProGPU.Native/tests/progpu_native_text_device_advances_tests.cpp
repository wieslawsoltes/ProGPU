#include "progpu_native.h"

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
    explicit context_owner(const bytes& data) {
        require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
            data.data(), data.size(), 0U, nullptr, 0U, &value) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(value != nullptr);
    }
    ~context_owner() { progpu_native_text_context_destroy(value); }
    context_owner(const context_owner&) = delete;
    context_owner& operator=(const context_owner&) = delete;
};
}

int main() {
    auto original = font_bytes();
    context_owner context(original);
    std::fill(original.begin(), original.end(), 0U); // Context owns its bytes.
    const std::array<std::uint32_t, 9> indices{2U, 0U, 1U, 2U, 1U, 0U, 2U, 2U, 0U};
    const std::array<float, 3> expected{1.0F, 0.0F, 255.0F};
    std::array<float, 12> output{};
    std::uint32_t available = 99U;
    for (std::uint32_t count = 0U; count <= indices.size(); ++count) {
        for (unsigned repeat = 0U; repeat < 2U; ++repeat) {
            output.fill(-777.0F);
            require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
                indices.data(), count, output.data(), output.size(), &available) == PROGPU_NATIVE_STATUS_SUCCESS);
            require(available == 1U);
            for (std::size_t i = 0U; i < output.size(); ++i)
                require(output[i] == (i < count ? expected[indices[i]] : -777.0F));
        }
    }
    // More than the bounded cache's capacity, including absent ppem records.
    for (std::uint32_t ppem = 1U; ppem <= 40U; ++ppem) {
        output.fill(-777.0F);
        require(progpu_native_text_context_get_device_advances(context.value, 0U, ppem,
            indices.data(), indices.size(), output.data(), output.size(), &available) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(available == ((ppem == 12U || ppem == 16U) ? 1U : 0U));
        for (std::size_t i = 0U; i < output.size(); ++i) {
            const float value = i >= indices.size() || available == 0U ? -777.0F :
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
        std::fill(fallback.begin(), fallback.end(), 0U);
        require(progpu_native_text_context_get_device_advances(context.value, face, 12U,
            indices.data(), indices.size(), output.data(), output.size(), &available) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(available == 1U && output[0U] == static_cast<float>(face + 4U));
    }
    for (const auto face : {0U, 1U, 20U}) {
        require(progpu_native_text_context_get_device_advances(context.value, face, 12U,
            indices.data(), indices.size(), output.data(), output.size(), &available) == PROGPU_NATIVE_STATUS_SUCCESS);
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
            indices.data(), indices.size(), output.data(), output.size(), &available) == PROGPU_NATIVE_STATUS_SUCCESS);
        require(available == 0U && output == sentinel);
    }
    require(progpu_native_text_context_get_device_advances(damaged.value, 0U, 12U,
        indices.data(), indices.size(), output.data(), output.size(), &available) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
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
    rejects(nullptr, 0U, 12U, indices.data(), indices.size(), output.data(), output.size());
    rejects(context.value, 21U, 12U, indices.data(), indices.size(), output.data(), output.size());
    for (const auto ppem : {0U, 65536U, std::numeric_limits<std::uint32_t>::max()})
        rejects(context.value, 0U, ppem, indices.data(), indices.size(), output.data(), output.size());
    rejects(context.value, 0U, 12U, nullptr, 1U, output.data(), output.size());
    rejects(context.value, 0U, 12U, indices.data(), indices.size(), nullptr, indices.size());
    rejects(context.value, 0U, 12U, indices.data(), indices.size(), output.data(), indices.size() - 1U);
    // The entire declared span must fit even for the intentionally misaligned
    // destination. Otherwise its overrun can alias availability on a different
    // compiler's stack, correctly selecting the all-storage-preserved contract.
    alignas(std::uint32_t) std::array<std::byte, sizeof(output) + alignof(float)> unaligned{};
    unaligned.fill(std::byte{0x5A});
    const auto unaligned_sentinel = unaligned;
    rejects(context.value, 0U, 12U, reinterpret_cast<const std::uint32_t*>(unaligned.data() + 1U), 1U,
        output.data(), output.size());
    rejects(context.value, 0U, 12U, indices.data(), indices.size(),
        reinterpret_cast<float*>(unaligned.data() + 1U), indices.size());
    require(unaligned == unaligned_sentinel);
    rejects(context.value, 0U, 12U,
        reinterpret_cast<const std::uint32_t*>(std::numeric_limits<std::uintptr_t>::max() - 3U),
        2U, output.data(), output.size());
    for (const auto bad : {3U, 65535U, 0x80000000U, 0xFFFFFFFFU}) {
        for (std::size_t slot = 0U; slot < indices.size(); ++slot) {
            auto invalid = indices;
            invalid[slot] = bad;
            for (const auto ppem : {12U, 13U})
                rejects(context.value, 0U, ppem, invalid.data(), invalid.size(), output.data(), output.size());
        }
    }
    require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
        indices.data(), indices.size(), output.data(), output.size(), nullptr) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(output == sentinel);
    auto aliased = indices;
    const auto saved = aliased;
    require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
        aliased.data(), aliased.size(), output.data(), output.size(), aliased.data()) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(aliased == saved && output == sentinel);
    require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
        indices.data(), indices.size(), output.data(), output.size(),
        reinterpret_cast<std::uint32_t*>(output.data() + 11U)) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(output == sentinel);
    available = 99U;
    require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
        aliased.data(), aliased.size(), reinterpret_cast<float*>(aliased.data()),
        aliased.size(), &available) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(aliased == saved && available == 99U);
    require(progpu_native_text_context_get_device_advances(context.value, 0U, 12U,
        nullptr, 0U, nullptr, 0U, &available) == PROGPU_NATIVE_STATUS_SUCCESS);
    require(available == 1U);
    std::printf("native device advances: %u checks passed\n", checks);
}
