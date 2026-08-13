#include "progpu_native_effect_plan.hpp"
#include "progpu_native_semantic_budget.hpp"
#include "progpu_native_semantic_state.hpp"
#include "progpu_native_semantic_validation.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cstdlib>

namespace {

void require(bool condition) {
    if (!condition) {
        std::abort();
    }
}

progpu_native_group_effect effect(std::uint32_t kind) noexcept {
    progpu_native_group_effect result{};
    result.struct_size = sizeof(result);
    result.kind = kind;
    result.revision = 1U;
    result.sigma_x = 1.0F;
    result.sigma_y = 1.0F;
    return result;
}

void effect_plan_uses_three_bounded_intermediates() {
    const std::array effects{
        effect(PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR),
        effect(PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW),
        effect(PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR)};
    const auto plan = progpu::native::effects::create_chain_plan(
        effects.data(),
        static_cast<std::uint32_t>(effects.size()));

    require(plan[0].source == -1);
    require(plan[0].horizontal == 0U);
    require(plan[0].vertical == 1U);
    require(plan[0].output == 1U);
    require(plan[1].source == 1);
    require(plan[1].horizontal == 0U);
    require(plan[1].vertical == 2U);
    require(plan[1].output == 0U);
    require(plan[2].source == 0);
    require(plan[2].horizontal == 1U);
    require(plan[2].vertical == 0U);
    require(plan[2].output == 0U);

    std::array<progpu_native_group_effect,
        PROGPU_NATIVE_MAX_GROUP_EFFECTS> maximum{};
    for (std::uint32_t index = 0U; index < maximum.size(); ++index) {
        maximum[index] = effect(index % 2U == 0U
            ? PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR
            : PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW);
    }
    const auto maximum_plan =
        progpu::native::effects::create_chain_plan(
            maximum.data(),
            static_cast<std::uint32_t>(maximum.size()));
    for (std::uint32_t index = 0U; index < maximum.size(); ++index) {
        const auto& entry = maximum_plan[index];
        require(entry.source < 3);
        require(entry.horizontal < 3U);
        require(entry.vertical < 3U);
        require(entry.output < 3U);
        require(static_cast<std::int32_t>(entry.horizontal) !=
            entry.source);
        if (maximum[index].kind ==
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
            require(entry.vertical != entry.horizontal);
            require(static_cast<std::int32_t>(entry.vertical) !=
                entry.source);
        }
    }
}

void semantic_budget_counts_effected_depth_once() {
    using progpu::native::semantic::layer_budget;
    using progpu::native::semantic::scissor;
    layer_budget budget{};
    require(budget.push(scissor{0U, 0U, 40U, 32U, true}, true));
    require(budget.push(scissor{4U, 4U, 24U, 16U, true}, true, true));
    budget.pop();
    require(budget.push(scissor{8U, 8U, 32U, 24U, true}, true, true));
    budget.pop();
    budget.pop();

    require(budget.peak_materialized_depth == 2U);
    require(budget.pooled_bytes() ==
        (40U * 32U + 32U * 24U) * 4U);
    require(budget.pooled_effect_bytes() == 32U * 24U * 12U);
    require(budget.maximum_width() == 40U);
    require(budget.maximum_height() == 32U);
}

void semantic_compilation_budget_is_checked() {
    progpu::native::semantic::compilation_budget budget{};
    require(budget.add(16U, 16U, 16U, 16U));
    require(budget.total_bytes() == 64U);
    require(!budget.add(
        progpu::native::semantic::max_vertex_bytes,
        0U,
        0U,
        0U));
}

void semantic_state_is_cpu_only_and_target_relative() {
    auto state = progpu::native::semantic::semantic_identity_state();
    state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
    state.transform.m31 = 12.0F;
    state.transform.m32 = 10.0F;
    state.opacity = 0.5F;
    state.clip_rect = {10.0F, 5.0F, 20.0F, 15.0F};

    const auto clipped =
        progpu::native::semantic::resolve_semantic_scissor(
            state, 100U, 80U, 2.0F);
    require(clipped == progpu::native::semantic::scissor{
        20U, 10U, 40U, 30U, true});

    const progpu::native::semantic::scissor target{
        18U, 8U, 30U, 20U, true};
    const auto target_clip =
        progpu::native::semantic::resolve_semantic_target_scissor(
            state, target, 100U, 80U, 2.0F);
    require(target_clip == progpu::native::semantic::scissor{
        2U, 2U, 28U, 18U, true});

    const auto localized =
        progpu::native::semantic::localize_semantic_state(
            state, target, 2.0F);
    require(localized.transform.m31 == 3.0F);
    require(localized.transform.m32 == 6.0F);

    progpu_native_analytic_primitive primitive{};
    primitive.transform = {1.0F, 0.0F, 0.0F, 1.0F, 2.0F, 3.0F};
    primitive.color.a = 0.75F;
    progpu::native::semantic::apply_semantic_state(primitive, state);
    require(primitive.transform.m31 == 14.0F);
    require(primitive.transform.m32 == 13.0F);
    require(primitive.color.a == 0.375F);
}

void semantic_state_and_layer_cursors_restore_scopes() {
    std::array<std::byte, 512U> storage{};
    progpu_native_scene_header header{};
    header.resource_offset = 64U;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    progpu_native_scene_resource resource{};
    resource.payload_offset = 256U;
    std::memcpy(storage.data() + header.resource_offset,
        &resource, sizeof(resource));
    auto stored_state =
        progpu::native::semantic::semantic_identity_state();
    stored_state.opacity = 0.25F;
    std::memcpy(storage.data() + resource.payload_offset,
        &stored_state, sizeof(stored_state));

    progpu::native::semantic::semantic_state_cursor state_cursor(
        storage.data(), header);
    progpu_native_scene_command save{};
    save.kind = PROGPU_NATIVE_SCENE_COMMAND_SAVE;
    save.state_index = 0U;
    require(state_cursor.advance(save).opacity == 0.25F);
    progpu_native_scene_command restore{};
    restore.kind = PROGPU_NATIVE_SCENE_COMMAND_RESTORE;
    restore.state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    require(state_cursor.advance(restore).opacity == 1.0F);

    std::array<std::byte, sizeof(progpu_native_scene_layer)> layer_bytes{};
    auto layer = progpu::native::semantic::semantic_default_layer();
    layer.flags = PROGPU_NATIVE_SCENE_LAYER_BOUNDS |
        PROGPU_NATIVE_SCENE_LAYER_FORCE_ISOLATION;
    layer.bounds = {4.0F, 5.0F, 20.0F, 10.0F};
    std::memcpy(layer_bytes.data(), &layer, sizeof(layer));
    progpu::native::semantic::semantic_layer_target_cursor layer_cursor(
        layer_bytes.data(), 64U, 48U, 1.0F);
    progpu_native_scene_command push{};
    push.kind = PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER;
    push.payload_size = sizeof(layer);
    require(layer_cursor.advance(push) ==
        progpu::native::semantic::scissor{4U, 5U, 20U, 10U, true});
    progpu_native_scene_command pop{};
    pop.kind = PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER;
    require(layer_cursor.advance(pop) ==
        progpu::native::semantic::scissor{0U, 0U, 64U, 48U, true});
}

void semantic_payload_validation_is_bounded_and_cpu_only() {
    progpu_native_scene_path_fill path{};
    path.segment_count = 1U;
    path.max_x = 10.0F;
    path.max_y = 10.0F;
    path.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    path.sample_grid = 4U;
    std::uint64_t path_coverage = 0U;
    require(progpu::native::semantic::is_valid_semantic_path(
        path, 1U, &path_coverage));
    require(path_coverage == 256U * 18U);
    path.segment_count = 2U;
    require(!progpu::native::semantic::is_valid_semantic_path(path, 1U));

    progpu_native_scene_glyph_outline outline{};
    outline.segment_count = 1U;
    outline.max_x = 10.0F;
    outline.max_y = 10.0F;
    outline.raster_scale = 1.0F;
    std::uint64_t glyph_coverage = 0U;
    require(progpu::native::semantic::is_valid_semantic_glyph_outline(
        outline, 1U, &glyph_coverage));
    require(glyph_coverage == 256U * 18U);
    outline.subpixel_x = 0.125F;
    require(!progpu::native::semantic::is_valid_semantic_glyph_outline(
        outline, 1U));

    progpu_native_positioned_glyph glyph{};
    glyph.atlas_to_logical_scale = 1.0F;
    require(progpu::native::semantic::is_valid_semantic_positioned_glyph(
        glyph, 1U));
    require(!progpu::native::semantic::is_valid_semantic_positioned_glyph(
        glyph, 0U));

    progpu_native_scene_image_draw image{};
    image.struct_size = sizeof(image);
    image.image_width = 2U;
    image.image_height = 2U;
    image.row_bytes = 8U;
    image.source_rect = {0.0F, 0.0F, 2.0F, 2.0F};
    image.destination_rect = {4.0F, 5.0F, 2.0F, 2.0F};
    image.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    image.opacity = 1.0F;
    require(progpu::native::semantic::is_valid_semantic_image(image, 16U));
    require(!progpu::native::semantic::is_valid_semantic_image(image, 15U));
}

} // namespace

int main() {
    effect_plan_uses_three_bounded_intermediates();
    semantic_budget_counts_effected_depth_once();
    semantic_compilation_budget_is_checked();
    semantic_state_is_cpu_only_and_target_relative();
    semantic_state_and_layer_cursors_restore_scopes();
    semantic_payload_validation_is_bounded_and_cpu_only();
    return 0;
}
