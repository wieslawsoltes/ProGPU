#pragma once

#include "progpu_native_engine.hpp"

namespace progpu::native {

inline void collect_memory(gpu_memory_inventory& inventory, const path_raster_resources& value) {
    inventory.buffer(value.uniforms);
    for (auto buffer : value.split_leaf_uniforms) inventory.buffer(buffer);
    for (auto buffer : value.split_signed_leaf_uniforms) inventory.buffer(buffer);
    inventory.buffer(value.records);
    inventory.buffer(value.segments);
    inventory.buffer(value.coverage);
    inventory.buffer(value.coverage_combine_uniforms);
    inventory.buffer(value.signed_coverage_combine_uniforms);
}

inline void collect_memory(gpu_memory_inventory& inventory, const semantic_layer_slot& value) {
    inventory.texture(value.texture);
    inventory.texture(value.depth_texture);
    inventory.buffer(value.uniform_buffer);
    for (auto texture : value.effect_textures) inventory.texture(texture);
    // bound_analytic_brush_buffer, bound_analytic_gradient_buffer and
    // bound_text_style_buffer are non-owning binding-cache identity keys.
    // They may be stale after invalidation; never dereference those keys.
}

inline void collect_memory(gpu_memory_inventory& inventory, const semantic_image_draw& value) {
    inventory.texture(value.texture);
    if (value.picture_backing) inventory.texture(value.picture_backing->texture);
    inventory.buffer(value.color_matrix_buffer);
    inventory.buffer(value.effect_uniform_buffer);
    inventory.buffer(value.effect_mask_uniform_buffer);
    inventory.texture(value.blur_intermediate_texture);
    inventory.texture(value.blur_output_texture);
    inventory.buffer(value.blur_horizontal_uniform_buffer);
    inventory.buffer(value.blur_vertical_uniform_buffer);
}

inline progpu_native_gpu_memory_snapshot collect_memory(progpu_native_engine& engine) {
    auto& inventory = engine.memory_inventory;
    inventory.reset();
    // Enumerate the owning graph, not draw counts, nominal atlas dimensions or
    // texture views. Shared picture/clip aliases are deduplicated by live handle.
#define B(field) inventory.buffer(engine.field)
#define T(field) inventory.texture(engine.field)
    B(uniform_buffer);
    B(analytic_uniform_buffer);
    B(analytic_brush_buffer);
    B(analytic_gradient_buffer);
    B(text_style_buffer);
    B(text_vertex_buffer);
    B(vertex_buffer);
    B(index_buffer);
    B(path_vertex_buffer);
    B(path_index_buffer);
    B(image_uniform_buffer);
    B(image_mask_uniform_buffer);
    B(image_vertex_buffer);
    B(image_index_buffer);
    B(layer_mask_uniform_buffer);
    B(group_blend_uniform_buffer);
    B(clip_compose_uniform_buffer);
    B(clip_vertex_buffer);
    B(clip_index_buffer);
    B(effect_blur_horizontal_uniform_buffer);
    B(effect_blur_vertical_uniform_buffer);
    B(effect_drop_shadow_uniform_buffer);
    for (auto buffer : engine.effect_chain_blur_horizontal_uniform_buffers) inventory.buffer(buffer);
    for (auto buffer : engine.effect_chain_blur_vertical_uniform_buffers) inventory.buffer(buffer);
    for (auto buffer : engine.effect_chain_drop_shadow_uniform_buffers) inventory.buffer(buffer);
    B(layer_uniform_buffer);
    B(layer_vertex_buffer);
    B(layer_index_buffer);
    B(semantic_hit_test_candidates);
    B(semantic_hit_test_dispatch_arguments);
    B(semantic_hit_test_query_buffer);
    B(semantic_hit_test_node_buffer);
    B(semantic_hit_test_primitive_index_buffer);
    B(semantic_hit_test_primitive_buffer);
    B(semantic_hit_test_result_buffer);
    B(semantic_hit_test_readback_buffer);
    B(semantic_hit_test_path_segment_buffer);
    B(semantic_layer_vertex_buffer);
    B(semantic_effect_uniform_buffer);
    B(semantic_advanced_blend_uniform_buffer);
    B(semantic_analytic_cache.vertex_buffer);
    B(semantic_analytic_cache.index_buffer);
    B(semantic_image_cache.vertex_buffer);
    B(semantic_3d_cache.camera_buffer);
    B(semantic_3d_cache.line_buffer);
    B(semantic_3d_cache.mesh_buffer);
    B(semantic_3d_cache.vertex_buffer);
    B(semantic_3d_cache.index_buffer);
    B(semantic_3d_cache.edge_buffer);
    B(semantic_3d_cache.light_buffer);
    B(semantic_3d_cache.material_buffer);
    B(semantic_3d_cache.material_gradient_stop_buffer);
    T(analytic_sentinel_texture);
    T(path_atlas_texture);
    T(glyph_atlas_texture);
    T(color_glyph_atlas_texture);
    T(image_texture);
    T(layer_mask_dummy_texture);
    T(group_blend_source_texture);
    T(clip_atlas_texture);
    T(clip_node_texture);
    for (auto texture : engine.clip_accumulation_textures) inventory.texture(texture);
    for (auto texture : engine.effect_textures) inventory.texture(texture);
    for (auto texture : engine.effect_chain_textures) inventory.texture(texture);
    T(layer_texture);
    T(semantic_3d_sentinel_texture);
    T(semantic_hit_test_readback_texture);
#undef B
#undef T
    for (const auto& picture : engine.semantic_picture_cache)
        if (picture) inventory.texture(picture->texture);
    for (const auto& picture : engine.semantic_picture_mask_cache)
        if (picture) inventory.texture(picture->texture);
    for (const auto& draw : engine.semantic_image_cache.draws) collect_memory(inventory, draw);
    for (const auto& slot : engine.semantic_layer_slots) collect_memory(inventory, slot);
    collect_memory(inventory, engine.semantic_root_slot);
    collect_memory(inventory, engine.semantic_advanced_source_slot);
    collect_memory(inventory, engine.semantic_advanced_output_slot);
    for (const auto& span : engine.semantic_render_bundle_spans) {
        inventory.buffer(span.mask_uniform_buffer);
        inventory.buffer(span.mask_chain_uniform_buffer);
        inventory.texture(span.mask_picture_backing
                ? span.mask_picture_backing->texture
                : span.mask_texture);
    }
    engine.retained_raster_resources.visit_resources([&](const auto& resources) {
        collect_memory(inventory, resources);
    });
    // Only external/borrowed bindings enter this distinct view count. Views of
    // owned textures above are aliases, not new allocations or borrowed images.
    inventory.borrowed_view(engine.image_mask_view);
    inventory.borrowed_view(engine.layer_external_mask_view);
    for (const auto& binding : engine.semantic_external_image_bindings)
        inventory.borrowed_view(binding.view);
    auto result = inventory.summarize();
    result.scene_id = engine.semantic_scene_id;
    result.scene_generation = engine.semantic_scene_generation;
    result.submission_index = engine.last_submission_index;
    result.retained_submission_batch_count = engine.retained_raster_resources.size();
    return result;
}

} // namespace progpu::native
