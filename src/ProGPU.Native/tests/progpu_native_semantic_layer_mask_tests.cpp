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
    static_assert(sizeof(progpu_native_scene_layer_mask_chain) == 432U);
    static_assert(sizeof(progpu_native_scene_layer_vector_mask) == 32U);
    static_assert(sizeof(progpu_native_scene_clip_path) == 72U);
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
    if (semantic::validate_layer_mask_resource(
            bytes.data(), resource, error_offset)) {
        return false;
    }

    progpu_native_scene_layer_mask_chain chain{};
    chain.struct_size = sizeof(chain);
    chain.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN;
    chain.mask_count = 2U;
    for (std::uint32_t index = 0U; index < chain.mask_count; ++index) {
        auto& analytic = chain.masks[index];
        analytic.struct_size = sizeof(analytic);
        analytic.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_ROUNDED_RECTANGLE;
        analytic.bounds = {
            static_cast<float>(index),
            static_cast<float>(index),
            8.0F,
            8.0F};
        analytic.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
        analytic.opacity = 1.0F;
    }
    std::array<std::byte, sizeof(chain)> chain_bytes{};
    std::memcpy(chain_bytes.data(), &chain, sizeof(chain));
    resource.payload_size = sizeof(chain);
    resource.auxiliary_offset = 0U;
    resource.auxiliary_size = 0U;
    if (!semantic::validate_layer_mask_resource(
            chain_bytes.data(), resource, error_offset, &parsed) ||
        parsed.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_ANALYTIC_CHAIN ||
        parsed.chain.mask_count != 2U) {
        return false;
    }
    chain.mask_count = 1U;
    std::memcpy(chain_bytes.data(), &chain, sizeof(chain));
    if (semantic::validate_layer_mask_resource(
            chain_bytes.data(), resource, error_offset)) {
        return false;
    }
    chain.mask_count = 2U;
    chain.masks[3] = chain.masks[0];
    std::memcpy(chain_bytes.data(), &chain, sizeof(chain));
    if (semantic::validate_layer_mask_resource(
            chain_bytes.data(), resource, error_offset)) {
        return false;
    }

    constexpr std::size_t vector_auxiliary_size =
        sizeof(progpu_native_scene_clip_path) +
        sizeof(progpu_native_path_segment);
    std::array<std::byte,
        sizeof(progpu_native_scene_layer_vector_mask) +
            vector_auxiliary_size> vector_bytes{};
    progpu_native_scene_layer_vector_mask vector_mask{};
    vector_mask.struct_size = sizeof(vector_mask);
    vector_mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN;
    vector_mask.path_count = 1U;
    vector_mask.segment_count = 1U;
    vector_mask.opacity = 0.8F;
    progpu_native_scene_clip_path vector_path{};
    vector_path.segment_count = 1U;
    vector_path.min_x = 0.0F;
    vector_path.min_y = 0.0F;
    vector_path.max_x = 12.0F;
    vector_path.max_y = 8.0F;
    vector_path.transform = {1.0F, 0.2F, -0.1F, 1.0F, 3.0F, 4.0F};
    vector_path.fill_rule = PROGPU_NATIVE_FILL_RULE_EVEN_ODD;
    vector_path.sample_grid = 8U;
    vector_path.operation = PROGPU_NATIVE_CLIP_INTERSECT;
    progpu_native_path_segment vector_segment{};
    vector_segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
    vector_segment.p0 = {0.0F, 0.0F};
    vector_segment.p1 = {12.0F, 8.0F};
    std::memcpy(vector_bytes.data(), &vector_mask, sizeof(vector_mask));
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask),
        &vector_path,
        sizeof(vector_path));
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask) + sizeof(vector_path),
        &vector_segment,
        sizeof(vector_segment));
    resource.payload_offset = 0U;
    resource.payload_size = sizeof(vector_mask);
    resource.auxiliary_offset = sizeof(vector_mask);
    resource.auxiliary_size = vector_auxiliary_size;
    if (!semantic::validate_layer_mask_resource(
            vector_bytes.data(), resource, error_offset, &parsed) ||
        parsed.kind != PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
        parsed.vector.path_count != 1U ||
        parsed.vector_paths[0].sample_grid != 8U ||
        parsed.vector_segments[0].kind !=
            PROGPU_NATIVE_PATH_SEGMENT_LINE) {
        return false;
    }
    vector_path.reserved = 1U;
    std::memcpy(
        vector_bytes.data() + sizeof(vector_mask),
        &vector_path,
        sizeof(vector_path));
    return !semantic::validate_layer_mask_resource(
        vector_bytes.data(), resource, error_offset);
}

} // namespace progpu::native::tests
