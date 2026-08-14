#pragma once

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>

namespace progpu::native::semantic {

struct semantic_layer_mask final {
    std::uint32_t kind = 0U;
    progpu_native_scene_layer_mask analytic{};
    progpu_native_scene_layer_coverage_mask coverage{};
    progpu_native_scene_layer_mask_chain chain{};
};

bool is_valid_semantic_layer_mask(
    const progpu_native_scene_layer_mask& mask) noexcept;

bool is_valid_semantic_layer_coverage_mask(
    const progpu_native_scene_layer_coverage_mask& mask,
    std::uint64_t auxiliary_size) noexcept;

bool is_valid_semantic_layer_mask_chain(
    const progpu_native_scene_layer_mask_chain& chain) noexcept;

bool validate_layer_mask_resource(
    const std::byte* bytes,
    const progpu_native_scene_resource& resource,
    std::uint32_t& error_offset,
    semantic_layer_mask* parsed = nullptr) noexcept;

} // namespace progpu::native::semantic
