#include "progpu_native_semantic_layer_mask_tests.hpp"

#include "progpu_native.h"
#include "progpu_native_semantic_layer_mask.hpp"

#include <array>
#include <cstddef>
#include <cstring>
#include <limits>

namespace progpu::native::tests {

bool semantic_layer_coverage_mask_is_exact_and_bounded() {
    static_assert(sizeof(progpu_native_scene_layer_coverage_mask) == 80U);
    constexpr std::size_t coverage_size = 16U;
    std::array<std::byte,
        sizeof(progpu_native_scene_layer_coverage_mask) + coverage_size>
        bytes{};
    progpu_native_scene_layer_coverage_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP;
    mask.width = 4U;
    mask.height = 4U;
    mask.row_bytes = 4U;
    mask.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
    mask.bounds = {0.0F, 0.0F, 4.0F, 4.0F};
    mask.transform = {1.0F, 0.25F, -0.125F, 1.0F, 3.0F, 5.0F};
    mask.opacity = 0.75F;
    std::memcpy(bytes.data(), &mask, sizeof(mask));
    progpu_native_scene_resource resource{};
    resource.payload_size = sizeof(mask);
    resource.auxiliary_offset = sizeof(mask);
    resource.auxiliary_size = coverage_size;
    std::uint32_t error_offset = 0U;
    semantic::semantic_layer_mask parsed{};
    if (!semantic::validate_layer_mask_resource(
            bytes.data(), resource, error_offset, &parsed) ||
        parsed.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_COVERAGE_BITMAP ||
        parsed.coverage.opacity != mask.opacity) {
        return false;
    }

    resource.auxiliary_size = coverage_size - 1U;
    if (semantic::validate_layer_mask_resource(
            bytes.data(), resource, error_offset)) {
        return false;
    }
    resource.auxiliary_size = coverage_size;
    mask.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_CUBIC;
    std::memcpy(bytes.data(), &mask, sizeof(mask));
    if (semantic::validate_layer_mask_resource(
            bytes.data(), resource, error_offset)) {
        return false;
    }
    mask.sampling = PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST;
    mask.transform.m11 = std::numeric_limits<float>::quiet_NaN();
    std::memcpy(bytes.data(), &mask, sizeof(mask));
    return !semantic::validate_layer_mask_resource(
        bytes.data(), resource, error_offset);
}

} // namespace progpu::native::tests
