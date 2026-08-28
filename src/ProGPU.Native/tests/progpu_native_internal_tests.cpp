#include "progpu_native_draw_state.hpp"
#include "progpu_native_buffer_capacity.hpp"
#include "progpu_native_effect_plan.hpp"
#include "progpu_native_geometry_analytic.hpp"
#include "progpu_native_geometry_dash.hpp"
#include "progpu_native_geometry_spline.hpp"
#include "progpu_native_geometry_stroke.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_semantic_budget.hpp"
#include "progpu_native_semantic_brush.hpp"
#include "progpu_native_semantic_brush_tests.hpp"
#include "progpu_native_semantic_color_glyph.hpp"
#include "progpu_native_semantic_effect_cache.hpp"
#include "progpu_native_semantic_image_tests.hpp"
#include "progpu_native_semantic_layer_mask_tests.hpp"
#include "progpu_native_semantic_draw_merge.hpp"
#include "progpu_native_scene_builder_tests.hpp"
#include "progpu_native_semantic_state.hpp"
#include "progpu_native_semantic_text_style.hpp"
#include "progpu_native_semantic_validation.hpp"
#include "progpu_native_webgpu_synchronization.hpp"

#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <future>
#include <limits>
#include <thread>

namespace {

void require(bool condition) {
    if (!condition) {
        std::abort();
    }
}

void clipped_miter_join_uses_the_wpf_three_triangle_wedge() {
    std::array<progpu::native::stroke_triangle, 8U> triangles{};
    const std::size_t count = progpu::native::create_join_triangles(
        triangles,
        PROGPU_NATIVE_STROKE_JOIN_MITER,
        8.0F,
        1.0F,
        {25.0F, 15.0F},
        {30.0F, 7.5F},
        {7.5F, 15.0F},
        true);
    require(count == 3U);
    require(progpu::native::is_finite(triangles[0U].p2));
    require(progpu::native::is_finite(triangles[1U].p1));
    require(progpu::native::is_finite(triangles[1U].p2));
    require(progpu::native::is_finite(triangles[2U].p1));

    const std::size_t standard_count =
        progpu::native::create_join_triangles(
            triangles,
            PROGPU_NATIVE_STROKE_JOIN_MITER,
            8.0F,
            1.0F,
            {25.0F, 15.0F},
            {30.0F, 7.5F},
            {7.5F, 15.0F});
    require(standard_count == 1U);
}

void reversal_joins_match_wpf_collapsed_contours() {
    std::array<progpu::native::stroke_triangle, 8U> triangles{};
    const std::size_t square_count =
        progpu::native::create_join_triangles(
            triangles,
            PROGPU_NATIVE_STROKE_JOIN_BEVEL,
            2.0F,
            1.0F,
            {0.0F, 0.0F},
            {0.0F, -1.0F},
            {0.0F, 1.0F},
            true);
    require(square_count == 3U);
    require(triangles[0U].p1.x == 1.0F);
    require(triangles[0U].p2.x == 1.0F);
    require(triangles[0U].p2.y == -1.0F);
    require(triangles[1U].p2.x == -1.0F);
    require(triangles[1U].p2.y == -1.0F);
    require(triangles[2U].p2.x == -1.0F);

    const std::size_t round_count =
        progpu::native::create_join_triangles(
            triangles,
            PROGPU_NATIVE_STROKE_JOIN_ROUND,
            2.0F,
            1.0F,
            {0.0F, 0.0F},
            {0.0F, -1.0F},
            {0.0F, 1.0F},
            true);
    require(round_count == 8U);
    require(triangles[0U].p1.x == 1.0F);
    require(std::abs(triangles[3U].p2.x) < 0.000001F);
    require(std::abs(triangles[3U].p2.y + 1.0F) < 0.000001F);
    require(std::abs(triangles[7U].p2.x + 1.0F) < 0.000001F);
}

void native_webgpu_scopes_share_one_process_lock() {
    using namespace std::chrono_literals;
    using progpu::native::webgpu::process_render_scope;

    // Native renderer operations nest helpers under one outer dispatch scope.
    // The synchronization primitive must therefore be recursive.
    {
        process_render_scope outer;
        process_render_scope nested;
    }

    std::promise<void> first_entered;
    std::promise<void> release_first;
    std::promise<void> second_entered;
    auto first_entered_future = first_entered.get_future();
    auto release_first_future = release_first.get_future();
    auto second_entered_future = second_entered.get_future();

    std::thread first([&] {
        process_render_scope scope;
        first_entered.set_value();
        release_first_future.wait();
    });
    first_entered_future.wait();

    std::thread second([&] {
        process_render_scope scope;
        second_entered.set_value();
    });

    require(second_entered_future.wait_for(50ms) ==
        std::future_status::timeout);
    release_first.set_value();
    require(second_entered_future.wait_for(1s) ==
        std::future_status::ready);
    first.join();
    second.join();
}

void native_submission_retirement_is_periodic_and_bounded() {
    using progpu::native::webgpu::submission_retirement_action;
    using progpu::native::webgpu::submission_retirement_tracker;

    submission_retirement_tracker tracker;
    for (std::uint64_t submission = 1U; submission < 64U; ++submission) {
        const auto action = tracker.on_submission(submission);
        require(action == (submission % 8U == 0U
            ? submission_retirement_action::poll
            : submission_retirement_action::none));
    }
    require(tracker.on_submission(64U) ==
        submission_retirement_action::wait);
    require(tracker.polled_count() == 64U);
    require(tracker.retired_count() == 0U);

    tracker.observe_latest_completion(64U);
    require(tracker.retired_count() == 64U);
    for (std::uint64_t submission = 65U; submission < 72U; ++submission) {
        require(tracker.on_submission(submission) ==
            submission_retirement_action::none);
    }
    require(tracker.on_submission(72U) ==
        submission_retirement_action::poll);
    tracker.observe_latest_completion(72U);
    require(tracker.retired_count() == 72U);
}

void native_buffer_growth_respects_the_portable_device_limit() {
    using progpu::native::try_calculate_buffer_capacity;
    constexpr std::uint64_t maximum = 256U;
    std::uint64_t capacity = 0U;

    require(try_calculate_buffer_capacity(64U, 129U, 16U, maximum, capacity));
    require(capacity == 256U);
    require(try_calculate_buffer_capacity(0U, 1U, 0U, maximum, capacity));
    require(capacity == 1U);
    require(!try_calculate_buffer_capacity(
        64U, maximum + 1U, 16U, maximum, capacity));
    require(!try_calculate_buffer_capacity(
        maximum + 1U, maximum, 16U, maximum, capacity));
}

void semantic_contiguous_draws_merge_without_reordering() {
    const auto vertex_stride =
        sizeof(progpu::native::vector_vertex);
    const auto index_stride = sizeof(std::uint32_t);
    semantic_analytic_draw analytic{
        0U,
        0U,
        4U,
        6U};
    require(try_merge_semantic_analytic_draw(
        analytic,
        semantic_analytic_draw{
            4U * vertex_stride,
            6U * index_stride,
            8U,
            12U}));
    require(analytic.vertex_count == 12U && analytic.index_count == 18U);
    const auto retained_analytic = analytic;
    require(!try_merge_semantic_analytic_draw(
        analytic,
        semantic_analytic_draw{
            13U * vertex_stride,
            18U * index_stride,
            4U,
            6U}));
    require(analytic.vertex_count == retained_analytic.vertex_count &&
        analytic.index_count == retained_analytic.index_count);

    semantic_path_draw path{4U, 6U};
    require(try_merge_semantic_path_draw(path, {10U, 9U}));
    require(path.first_index == 4U && path.index_count == 15U);
    require(!try_merge_semantic_path_draw(path, {20U, 3U}));

    semantic_glyph_draw glyph{7U, 3U};
    require(try_merge_semantic_glyph_draw(glyph, {10U, 5U}));
    require(glyph.first_instance == 7U && glyph.instance_count == 8U);
    require(!try_merge_semantic_glyph_draw(glyph, {16U, 1U}));

    semantic_glyph_draw overflow{
        0U,
        std::numeric_limits<std::uint32_t>::max()};
    require(!try_merge_semantic_glyph_draw(overflow, {0U, 1U}));
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

void semantic_cache_budget_is_owner_keyed_and_bounded() {
    using progpu::native::semantic::cache_budget;
    using progpu::native::semantic::scissor;
    cache_budget budget{};
    require(budget.add(71U, scissor{0U, 0U, 40U, 32U, true}, false));
    require(budget.add(72U, scissor{4U, 4U, 24U, 16U, true}, true));
    require(!budget.add(71U, scissor{0U, 0U, 8U, 8U, true}, false));
    require(!budget.add(0U, scissor{0U, 0U, 8U, 8U, true}, false));
    require(budget.count == 2U);
    require(budget.pooled_bytes() ==
        (40U * 32U + 24U * 16U) * 4U);
    require(budget.pooled_effect_bytes() == 24U * 16U * 12U);
    require(budget.maximum_width() == 40U);
    require(budget.maximum_height() == 32U);
}

void semantic_brush_page_is_bounded_deduplicated_and_retained() {
    std::array<std::byte, 2048U> storage{};
    progpu_native_scene_header header{};
    header.resource_offset = 80U;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.resource_count = 2U;
    header.command_offset = 176U;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.command_count = 2U;

    progpu_native_scene_resource brush_resource{};
    brush_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE;
    brush_resource.payload_offset = 304U;
    brush_resource.payload_size = 2U *
        sizeof(progpu_native_scene_brush);
    brush_resource.auxiliary_offset =
        brush_resource.payload_offset + brush_resource.payload_size;
    brush_resource.auxiliary_size = 2U *
        sizeof(progpu_native_scene_gradient_stop);
    std::memcpy(
        storage.data() + header.resource_offset,
        &brush_resource,
        sizeof(brush_resource));

    progpu_native_scene_resource state_resource{};
    state_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_STATE;
    state_resource.payload_offset =
        brush_resource.auxiliary_offset + brush_resource.auxiliary_size;
    state_resource.payload_size = sizeof(progpu_native_scene_state);
    std::memcpy(
        storage.data() + header.resource_offset +
            sizeof(progpu_native_scene_resource),
        &state_resource,
        sizeof(state_resource));

    progpu_native_scene_brush solid{};
    solid.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
    solid.opacity = 1.0F;
    solid.colors[0] = {0.25F, 0.5F, 0.75F, 1.0F};
    solid.coordinate_transform0[0] = 1.0F;
    solid.coordinate_transform1[1] = 1.0F;
    std::memcpy(
        storage.data() + brush_resource.payload_offset,
        &solid,
        sizeof(solid));
    progpu_native_scene_brush gradient{};
    gradient.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
    gradient.opacity = 0.8F;
    gradient.end_point = {64.0F, 0.0F};
    gradient.stop_count = 2U;
    gradient.coordinate_transform0[0] = 1.0F;
    gradient.coordinate_transform1[1] = 1.0F;
    std::memcpy(
        storage.data() + brush_resource.payload_offset + sizeof(solid),
        &gradient,
        sizeof(gradient));
    const std::array stops{
        progpu_native_scene_gradient_stop{
            {1.0F, 0.0F, 0.0F, 1.0F}, 0.0F, 0U, 0U, 0U},
        progpu_native_scene_gradient_stop{
            {0.0F, 0.0F, 1.0F, 1.0F}, 1.0F, 0U, 0U, 0U}};
    std::memcpy(
        storage.data() + brush_resource.auxiliary_offset,
        stops.data(),
        sizeof(stops));
    auto state =
        progpu::native::semantic::semantic_identity_state();
    state.opacity = 0.5F;
    std::memcpy(
        storage.data() + state_resource.payload_offset,
        &state,
        sizeof(state));

    const auto write_draw = [&](
        std::uint32_t command_index,
        std::uint32_t kind,
        std::uint32_t payload_offset,
        const std::uint32_t* indices,
        std::uint32_t count) {
        progpu_native_scene_command command{};
        command.kind = kind;
        command.state_index = 1U;
        command.payload_offset = payload_offset;
        command.payload_size = sizeof(progpu_native_scene_draw_brushes) +
            count * sizeof(std::uint32_t);
        std::memcpy(
            storage.data() + header.command_offset +
                command_index * sizeof(command),
            &command,
            sizeof(command));
        const progpu_native_scene_draw_brushes draw{
            sizeof(progpu_native_scene_draw_brushes),
            0U,
            count,
            0U};
        std::memcpy(storage.data() + payload_offset, &draw, sizeof(draw));
        std::memcpy(
            storage.data() + payload_offset + sizeof(draw),
            indices,
            count * sizeof(std::uint32_t));
    };
    const std::array analytic_indices{0U, 1U};
    const std::array path_indices{1U};
    write_draw(
        0U,
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC,
        944U,
        analytic_indices.data(),
        static_cast<std::uint32_t>(analytic_indices.size()));
    write_draw(
        1U,
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH,
        968U,
        path_indices.data(),
        static_cast<std::uint32_t>(path_indices.size()));

    std::uint32_t error_offset = 0U;
    require(progpu::native::semantic::validate_brush_table(
        storage.data(), brush_resource, error_offset));
    progpu_native_scene_command analytic_command{};
    std::memcpy(
        &analytic_command,
        storage.data() + header.command_offset,
        sizeof(analytic_command));
    require(progpu::native::semantic::validate_draw_brushes(
        storage.data(), header, analytic_command, 2U, error_offset));

    progpu::native::semantic::semantic_brush_page page{};
    require(progpu::native::semantic::compile_brush_page(
        storage.data(), header, 123U, page));
    require(page.cache_valid);
    require(page.scene_hash == 123U);
    require(page.brushes.size() == 3U);
    require(page.gradient_stops.size() == 3U);
    require(page.remapped_indices.size() == 3U);
    require(page.remapped_indices[0] == 1U);
    require(page.remapped_indices[1] == 2U);
    require(page.remapped_indices[2] == 2U);
    require(page.brushes[1].opacity == 0.5F);
    require(page.brushes[1].colors[0].b == 0.75F);
    require(page.brushes[2].opacity == 0.4F);
    require(page.brushes[2].stop_offset == 1U);
    std::uint32_t packed_index = 0U;
    require(progpu::native::semantic::try_get_draw_brush_index(
        page, 1U, 0U, packed_index));
    require(packed_index == 2U);
    require(!progpu::native::semantic::try_get_draw_brush_index(
        page, 1U, 1U, packed_index));

    auto invalid_stops = stops;
    invalid_stops[1].offset = -1.0F;
    std::memcpy(
        storage.data() + brush_resource.auxiliary_offset,
        invalid_stops.data(),
        sizeof(invalid_stops));
    require(!progpu::native::semantic::validate_brush_table(
        storage.data(), brush_resource, error_offset));
}

void semantic_mesh_brush_preserves_source_opacity_before_color_blend() {
    std::array<std::byte, 1024U> storage{};
    progpu_native_scene_header header{};
    header.resource_offset = 64U;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.resource_count = 2U;
    header.command_offset = 192U;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.command_count = 1U;

    progpu_native_scene_resource brush_resource{};
    brush_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE;
    brush_resource.payload_offset = 320U;
    brush_resource.payload_size = sizeof(progpu_native_scene_brush);
    std::memcpy(storage.data() + header.resource_offset, &brush_resource,
        sizeof(brush_resource));
    progpu_native_scene_resource state_resource{};
    state_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_STATE;
    state_resource.payload_offset = 576U;
    state_resource.payload_size = sizeof(progpu_native_scene_state);
    std::memcpy(storage.data() + header.resource_offset +
            sizeof(progpu_native_scene_resource),
        &state_resource, sizeof(state_resource));

    progpu_native_scene_brush brush{};
    brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
    brush.opacity = 0.8F;
    brush.colors[0] = {0.25F, 0.5F, 0.75F, 1.0F};
    brush.coordinate_transform0[0] = 1.0F;
    brush.coordinate_transform1[1] = 1.0F;
    std::memcpy(storage.data() + brush_resource.payload_offset, &brush,
        sizeof(brush));
    auto state = progpu::native::semantic::semantic_identity_state();
    state.opacity = 0.5F;
    std::memcpy(storage.data() + state_resource.payload_offset, &state,
        sizeof(state));

    progpu_native_scene_command command{};
    command.kind = PROGPU_NATIVE_SCENE_COMMAND_DRAW_VERTEX_MESH;
    command.state_index = 1U;
    command.payload_offset = 672U;
    command.payload_size = sizeof(progpu_native_scene_draw_brushes) +
        sizeof(std::uint32_t);
    std::memcpy(storage.data() + header.command_offset, &command,
        sizeof(command));
    const progpu_native_scene_draw_brushes draw{
        sizeof(progpu_native_scene_draw_brushes), 0U, 1U, 0U};
    const std::uint32_t brush_index = 0U;
    std::memcpy(storage.data() + command.payload_offset, &draw, sizeof(draw));
    std::memcpy(storage.data() + command.payload_offset + sizeof(draw),
        &brush_index, sizeof(brush_index));

    progpu::native::semantic::semantic_brush_page page{};
    require(progpu::native::semantic::compile_brush_page(
        storage.data(), header, 456U, page));
    require(page.brushes.size() == 2U);
    require(page.remapped_indices.size() == 1U);
    require(page.brushes[1].opacity == 0.8F);
}

void semantic_text_style_page_is_validated_deduplicated_and_retained() {
    std::array<std::byte, 1536U> storage{};
    progpu_native_scene_header header{};
    header.resource_offset = 80U;
    header.resource_stride = sizeof(progpu_native_scene_resource);
    header.resource_count = 2U;
    header.command_offset = 176U;
    header.command_stride = sizeof(progpu_native_scene_command);
    header.command_count = 3U;

    progpu_native_scene_resource style_resource{};
    style_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_TEXT_STYLE_TABLE;
    style_resource.payload_offset = 384U;
    style_resource.payload_size = sizeof(progpu_native_scene_text_style);
    std::memcpy(
        storage.data() + header.resource_offset,
        &style_resource,
        sizeof(style_resource));

    progpu_native_scene_resource state_resource{};
    state_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_STATE;
    state_resource.payload_offset = 416U;
    state_resource.payload_size = sizeof(progpu_native_scene_state);
    std::memcpy(
        storage.data() + header.resource_offset +
            sizeof(progpu_native_scene_resource),
        &state_resource,
        sizeof(state_resource));

    const progpu_native_scene_text_style style{
        {0.25F, 0.5F, 0.75F, 0.8F},
        PROGPU_NATIVE_SCENE_TEXT_GRAYSCALE,
        0U,
        0U,
        0U};
    std::memcpy(
        storage.data() + style_resource.payload_offset,
        &style,
        sizeof(style));
    auto state = progpu::native::semantic::semantic_identity_state();
    state.opacity = 0.5F;
    std::memcpy(
        storage.data() + state_resource.payload_offset,
        &state,
        sizeof(state));

    const auto write_draw = [&storage, &header](
        std::uint32_t command_index,
        std::uint32_t payload_offset,
        std::uint32_t state_index) {
        progpu_native_scene_command command{};
        command.kind = PROGPU_NATIVE_SCENE_COMMAND_DRAW_GLYPH_RUN;
        command.flags = PROGPU_NATIVE_SCENE_GLYPH_STYLED;
        command.state_index = state_index;
        command.payload_offset = payload_offset;
        command.payload_size = sizeof(progpu_native_scene_glyph_draw) +
            sizeof(progpu_native_positioned_glyph);
        std::memcpy(
            storage.data() + header.command_offset +
                command_index * sizeof(command),
            &command,
            sizeof(command));
        const progpu_native_scene_glyph_draw draw{
            sizeof(progpu_native_scene_glyph_draw), 0U, 0U, 1U, 0U, 0U};
        std::memcpy(storage.data() + payload_offset, &draw, sizeof(draw));
        progpu_native_positioned_glyph glyph{};
        glyph.basis_x = {1.0F, 0.0F};
        glyph.basis_y = {0.0F, 1.0F};
        glyph.color = {1.0F, 0.0F, 1.0F, 0.25F};
        glyph.atlas_to_logical_scale = 1.0F;
        std::memcpy(
            storage.data() + payload_offset + sizeof(draw),
            &glyph,
            sizeof(glyph));
    };
    write_draw(0U, 512U, 1U);
    write_draw(1U, 608U, 1U);
    write_draw(2U, 704U, PROGPU_NATIVE_SCENE_NO_INDEX);

    std::uint32_t error_offset = 0U;
    require(progpu::native::semantic::validate_text_style_table(
        storage.data(), style_resource, error_offset));
    progpu_native_scene_command command{};
    std::memcpy(
        &command,
        storage.data() + header.command_offset,
        sizeof(command));
    require(progpu::native::semantic::validate_styled_glyph_draw(
        storage.data(), header, command, error_offset));

    progpu::native::semantic::semantic_text_style_page page{};
    require(progpu::native::semantic::compile_text_style_page(
        storage.data(), header, 987U, page));
    require(page.cache_valid && page.scene_hash == 987U);
    require(page.styles.size() == 3U);
    require(page.command_style_indices.size() == 3U);
    require(page.command_style_indices[0] == 1U);
    require(page.command_style_indices[1] == 1U);
    require(page.command_style_indices[2] == 2U);
    require(page.styles[1].color.r == 0.25F);
    require(page.styles[1].color.b == 0.75F);
    require(page.styles[1].color.a == 0.4F);
    require(page.styles[2].color.a == 0.8F);
    std::uint32_t packed_index = 0U;
    require(progpu::native::semantic::try_get_command_text_style_index(
        page, 1U, packed_index));
    require(packed_index == 1U);
    require(!progpu::native::semantic::try_get_command_text_style_index(
        page, 3U, packed_index));

    auto invalid_style = style;
    invalid_style.text_rendering_mode = 3U;
    std::memcpy(
        storage.data() + style_resource.payload_offset,
        &invalid_style,
        sizeof(invalid_style));
    require(!progpu::native::semantic::validate_text_style_table(
        storage.data(), style_resource, error_offset));
}

void semantic_color_glyph_resource_is_strictly_validated() {
    std::array<std::byte, 256U> storage{};
    progpu_native_scene_resource resource{};
    resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_GLYPH_RUN;
    resource.flags = PROGPU_NATIVE_SCENE_COLOR_GLYPH_BITMAPS;
    resource.payload_offset = 64U;
    resource.payload_size =
        sizeof(progpu_native_scene_color_glyph_bitmap);
    resource.auxiliary_offset = 128U;
    resource.auxiliary_size = 16U;
    const progpu_native_scene_color_glyph_bitmap bitmap{
        0U, 2U, 2U, 8U, 0U,
        1.0F, -2.0F, 12.0F, 14.0F, 0U, 0U};
    std::memcpy(
        storage.data() + resource.payload_offset,
        &bitmap,
        sizeof(bitmap));
    std::uint32_t error_offset = 0U;
    require(progpu::native::semantic::validate_color_glyph_resource(
        storage.data(), resource, error_offset));

    auto invalid = bitmap;
    invalid.row_bytes = 7U;
    std::memcpy(
        storage.data() + resource.payload_offset,
        &invalid,
        sizeof(invalid));
    require(!progpu::native::semantic::validate_color_glyph_resource(
        storage.data(), resource, error_offset));
    require(error_offset == resource.payload_offset);
}

void semantic_effect_output_cache_requires_exact_retained_identity() {
    using namespace progpu::native::effects;
    semantic_output_cache cache{};
    const semantic_output_cache_key key{
        17U, 29U, 3U, 960U, 540U};
    require(!semantic_output_cache_hit(cache, key));
    commit_semantic_output_cache(cache, key);
    require(semantic_output_cache_hit(cache, key));
    require(!semantic_output_cache_hit(
        cache,
        semantic_output_cache_key{18U, 29U, 3U, 960U, 540U}));
    require(!semantic_output_cache_hit(
        cache,
        semantic_output_cache_key{17U, 30U, 3U, 960U, 540U}));
    require(!semantic_output_cache_hit(
        cache,
        semantic_output_cache_key{17U, 29U, 4U, 960U, 540U}));
    require(!semantic_output_cache_hit(
        cache,
        semantic_output_cache_key{17U, 29U, 3U, 961U, 540U}));
    invalidate_semantic_output_cache(cache);
    require(!semantic_output_cache_hit(cache, key));
    commit_semantic_output_cache(cache, {});
    require(!semantic_output_cache_hit(cache, {}));
}

void gpu_records_preserve_alignment_phase_and_cache_identity() {
    using namespace progpu::native;
    static_assert(sizeof(gpu_uniforms) == 224U);
    static_assert(sizeof(gpu_path_record) == 32U);
    static_assert(sizeof(gpu_glyph_instance) == 96U);
    require(align_up(1U, 256U) == 256U);
    require(align_up(256U, 256U) == 256U);
    require(quantize_subpixel_phase(0.0F) == 0.0F);
    require(quantize_subpixel_phase(1.0F) == 0.0F);
    require(quantize_subpixel_phase(0.5F) == 0.5F);

    const native_path_cache_key first{
        1U, 2U, 3U, 4U, 5U, 6U, 7U, 8U, 9U, 10U, 11U, 12U, 13U};
    const native_path_cache_key same = first;
    auto different = first;
    different.subpixel_x = 12U;
    native_path_cache_key_hash hash{};
    require(first == same);
    require(hash(first) == hash(same));
    require(!(first == different));
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

void semantic_static_guidelines_adjust_state_at_target_dpi() {
    std::array<std::byte, 512U> storage{};
    progpu_native_scene_header header{};
    header.resource_offset = 64U;
    header.resource_count = 2U;
    header.resource_stride = sizeof(progpu_native_scene_resource);

    progpu_native_scene_resource guideline_resource{};
    guideline_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_GUIDELINE_SET;
    guideline_resource.payload_offset = 256U;
    guideline_resource.payload_size =
        sizeof(progpu_native_scene_guideline_set) + 2U * sizeof(double);
    std::memcpy(storage.data() + header.resource_offset,
        &guideline_resource, sizeof(guideline_resource));
    progpu_native_scene_guideline_set guidelines{};
    guidelines.struct_size = sizeof(guidelines);
    guidelines.guideline_x_count = 1U;
    guidelines.guideline_y_count = 1U;
    std::memcpy(storage.data() + guideline_resource.payload_offset,
        &guidelines, sizeof(guidelines));
    constexpr double guideline_x = 12.25;
    constexpr double guideline_y = 23.5;
    std::memcpy(
        storage.data() + guideline_resource.payload_offset +
            sizeof(guidelines),
        &guideline_x,
        sizeof(guideline_x));
    std::memcpy(
        storage.data() + guideline_resource.payload_offset +
            sizeof(guidelines) + sizeof(guideline_x),
        &guideline_y,
        sizeof(guideline_y));

    progpu_native_scene_resource state_resource{};
    state_resource.kind = PROGPU_NATIVE_SCENE_RESOURCE_STATE;
    state_resource.payload_offset = 320U;
    std::memcpy(
        storage.data() + header.resource_offset +
            sizeof(progpu_native_scene_resource),
        &state_resource,
        sizeof(state_resource));
    auto state = progpu::native::semantic::semantic_identity_state();
    state.flags = PROGPU_NATIVE_SCENE_STATE_GUIDELINE_SET;
    state.guideline_resource_index = 0U;
    state.transform.m31 = 10.0F;
    state.transform.m32 = 20.0F;
    std::memcpy(storage.data() + state_resource.payload_offset,
        &state, sizeof(state));

    progpu::native::semantic::semantic_state_cursor cursor(
        storage.data(), header, 1.0F);
    progpu_native_scene_command save{};
    save.kind = PROGPU_NATIVE_SCENE_COMMAND_SAVE;
    save.state_index = 1U;
    const auto snapped = cursor.advance(save);
    require(snapped.transform.m31 == 9.75F);
    require(snapped.transform.m32 == 20.5F);

    guidelines.flags = PROGPU_NATIVE_SCENE_GUIDELINE_EXPLICIT_OFFSETS;
    guideline_resource.payload_size =
        sizeof(progpu_native_scene_guideline_set) + 4U * sizeof(double);
    std::memcpy(storage.data() + header.resource_offset,
        &guideline_resource, sizeof(guideline_resource));
    std::memcpy(storage.data() + guideline_resource.payload_offset,
        &guidelines, sizeof(guidelines));
    constexpr std::array<double, 2U> explicit_offsets{0.125, -0.25};
    std::memcpy(
        storage.data() + guideline_resource.payload_offset +
            sizeof(guidelines) + 2U * sizeof(double),
        explicit_offsets.data(),
        sizeof(explicit_offsets));
    progpu::native::semantic::semantic_state_cursor explicit_cursor(
        storage.data(), header, 2.0F);
    const auto explicitly_snapped = explicit_cursor.advance(save);
    require(explicitly_snapped.transform.m31 == 10.0625F);
    require(explicitly_snapped.transform.m32 == 19.875F);

    guidelines.flags = PROGPU_NATIVE_SCENE_GUIDELINE_COMPOSITE_ONLY;
    guidelines.guideline_x_count = 2U;
    guidelines.guideline_y_count = 0U;
    guideline_resource.payload_size =
        sizeof(progpu_native_scene_guideline_set) + 2U * sizeof(double);
    std::memcpy(storage.data() + header.resource_offset,
        &guideline_resource, sizeof(guideline_resource));
    std::memcpy(storage.data() + guideline_resource.payload_offset,
        &guidelines, sizeof(guidelines));
    constexpr std::array<double, 2U> composite_guidelines{10.5, 12.0};
    std::memcpy(
        storage.data() + guideline_resource.payload_offset +
            sizeof(guidelines),
        composite_guidelines.data(),
        sizeof(composite_guidelines));
    progpu::native::semantic::semantic_state_cursor composite_cursor(
        storage.data(), header, 1.0F);
    const auto composite_state = composite_cursor.read_composite_state(1U);
    float midpoint_x = 11.25F;
    float midpoint_y = 4.0F;
    composite_cursor.snap_composite_point(
        composite_state, midpoint_x, midpoint_y);
    require(midpoint_x == 11.75F);
    require(midpoint_y == 4.0F);
    float upper_x = 11.26F;
    composite_cursor.snap_composite_point(
        composite_state, upper_x, midpoint_y);
    require(upper_x == 11.26F);

    guidelines.flags = PROGPU_NATIVE_SCENE_GUIDELINE_PER_POINT;
    std::memcpy(storage.data() + guideline_resource.payload_offset,
        &guidelines, sizeof(guidelines));
    constexpr std::array<double, 2U> per_point_guidelines{10.25, 12.75};
    std::memcpy(
        storage.data() + guideline_resource.payload_offset +
            sizeof(guidelines),
        per_point_guidelines.data(),
        sizeof(per_point_guidelines));
    progpu::native::semantic::semantic_state_cursor per_point_cursor(
        storage.data(), header, 1.0F);
    const auto per_point_state = per_point_cursor.resolve_state(1U);
    require(per_point_state.transform.m31 == 10.0F);
    require(per_point_state.transform.m32 == 20.0F);
    float lower_tie_x = 11.5F;
    float per_point_y = 4.0F;
    per_point_cursor.snap_draw_point(
        per_point_state, lower_tie_x, per_point_y);
    require(lower_tie_x == 11.25F);
    float upper_nearest_x = 11.51F;
    per_point_cursor.snap_draw_point(
        per_point_state, upper_nearest_x, per_point_y);
    require(std::abs(upper_nearest_x - 11.76F) < 0.0001F);
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
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_SNAP_TO_PIXELS;
    require(progpu::native::semantic::is_valid_semantic_image(image, 16U));
    image.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED;
    require(progpu::native::semantic::is_valid_semantic_image(image, 16U));

    float snapped_x = 1.26F;
    float snapped_y = -2.24F;
    progpu::native::semantic::snap_semantic_image_point(
        snapped_x, snapped_y, 2.0F);
    require(snapped_x == 1.5F);
    require(snapped_y == -2.0F);

    snapped_x = 1.25F;
    snapped_y = -1.25F;
    progpu::native::semantic::snap_semantic_image_point(
        snapped_x, snapped_y, 2.0F);
    require(snapped_x == 1.0F);
    require(snapped_y == -1.0F);
    snapped_x = 1.75F;
    snapped_y = -1.75F;
    progpu::native::semantic::snap_semantic_image_point(
        snapped_x, snapped_y, 2.0F);
    require(snapped_x == 2.0F);
    require(snapped_y == -2.0F);
}

void draw_state_resolution_is_cpu_only_and_bounded() {
    resolved_draw_state resolved{};
    require(resolve_draw_state(nullptr, 0U, 32U, 24U, 2.0F, resolved));
    require(resolved.opacity == 1.0F);
    require(resolved.group_opacity == 1.0F);
    require(!resolved.has_clip);

    progpu_native_group_mask mask{};
    mask.struct_size = sizeof(mask);
    mask.kind = PROGPU_NATIVE_GROUP_MASK_ROUNDED_RECTANGLE;
    mask.bounds = {0.0F, 0.0F, 10.0F, 8.0F};
    mask.transform = {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    mask.corner_radii_x[0] = 10.0F;
    mask.corner_radii_x[1] = 10.0F;
    mask.corner_radii_x[2] = 10.0F;
    mask.corner_radii_x[3] = 10.0F;
    mask.corner_radii_y[0] = 8.0F;
    mask.corner_radii_y[1] = 8.0F;
    mask.corner_radii_y[2] = 8.0F;
    mask.corner_radii_y[3] = 8.0F;
    mask.opacity = 0.75F;

    const std::array effects{
        effect(PROGPU_NATIVE_GROUP_EFFECT_GAUSSIAN_BLUR),
        effect(PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW)};
    const progpu_native_group_effect_chain effect_chain{
        sizeof(progpu_native_group_effect_chain),
        static_cast<std::uint32_t>(effects.size()),
        12U,
        0U,
        effects.data()};
    progpu_native_draw_state state{};
    state.struct_size = sizeof(state);
    state.flags = PROGPU_NATIVE_DRAW_STATE_CLIP_RECT;
    state.opacity = 0.8F;
    state.clip_rect = {-0.2F, 0.2F, 10.4F, 8.6F};
    state.group_opacity = 0.5F;
    state.group_revision = 7U;
    state.group_mask = &mask;
    state.group_effect_chain = &effect_chain;
    state.group_blend_mode = PROGPU_NATIVE_BLEND_MULTIPLY;

    require(resolve_draw_state(&state, 0U, 32U, 24U, 2.0F, resolved));
    require(resolved.opacity == 0.8F);
    require(resolved.group_opacity == 0.5F);
    require(resolved.group_revision == 7U);
    require(resolved.group_blend_mode == PROGPU_NATIVE_BLEND_MULTIPLY);
    require(resolved.has_clip && resolved.has_drawable_clip);
    require(resolved.clip_x == 0U && resolved.clip_y == 0U);
    require(resolved.clip_width == 21U && resolved.clip_height == 18U);
    require(resolved.has_group_mask);
    require(resolved.group_mask.corner_radii_x[0] == 5.0F);
    require(resolved.group_mask.corner_radii_y[0] == 4.0F);
    require(resolved.has_group_effect && resolved.effect_count == 2U);
    require(resolved.effect_chain_revision == 12U);

    state.reserved2 = 1U;
    require(!resolve_draw_state(&state, 0U, 32U, 24U, 2.0F, resolved));

    constexpr std::uint64_t seed = 1469598103934665603ULL;
    const std::array<std::uint32_t, 3U> words{1U, 2U, 3U};
    const auto first = append_fnv1a64(seed, words.data(), sizeof(words));
    const auto second = append_fnv1a64(seed, words.data(), sizeof(words));
    require(first == second);
    require(first != append_fnv1a64(seed, words.data(), sizeof(words[0])));
}

} // namespace

int main() {
    clipped_miter_join_uses_the_wpf_three_triangle_wedge();
    reversal_joins_match_wpf_collapsed_contours();
    native_webgpu_scopes_share_one_process_lock();
    native_submission_retirement_is_periodic_and_bounded();
    native_buffer_growth_respects_the_portable_device_limit();
    semantic_contiguous_draws_merge_without_reordering();
    effect_plan_uses_three_bounded_intermediates();
    semantic_budget_counts_effected_depth_once();
    semantic_compilation_budget_is_checked();
    semantic_cache_budget_is_owner_keyed_and_bounded();
    semantic_brush_page_is_bounded_deduplicated_and_retained();
    semantic_mesh_brush_preserves_source_opacity_before_color_blend();
    require(progpu::native::tests::
        semantic_perlin_brush_table_is_exact_and_bounded());
    require(progpu::native::tests::
        semantic_image_sampling_payload_is_exact_and_bounded());
    require(progpu::native::tests::
        semantic_layer_coverage_mask_is_exact_and_bounded());
    require(progpu::native::tests::
        semantic_scene_builder_is_deterministic_and_valid());
    require(progpu::native::tests::
        semantic_scene_builder_bounds_composite_only_guidelines());
    require(progpu::native::tests::
        semantic_scene_builder_records_final_composite_clip());
    require(progpu::native::tests::
        semantic_scene_builder_preserves_shared_path_segments());
    require(progpu::native::tests::
        semantic_scene_builder_records_general_brushes());
    require(progpu::native::tests::
        semantic_scene_builder_records_native_svg_layers());
    require(progpu::native::tests::
        semantic_scene_builder_rejects_invalid_state());
    require(progpu::native::tests::
        semantic_scene_builder_reuses_retained_images());
    require(progpu::native::tests::
        semantic_scene_builder_records_image_patch_batches());
    require(progpu::native::tests::
        semantic_scene_builder_batches_compatible_image_draws());
    require(progpu::native::tests::
        semantic_scene_builder_serializes_external_images_pointer_free());
    require(progpu::native::tests::
        semantic_scene_builder_updates_retained_images_transactionally());
    require(progpu::native::tests::
        semantic_scene_builder_records_styled_glyph_runs());
    require(progpu::native::tests::
        semantic_scene_content_hashes_normalize_resource_ordinals());
    require(progpu::native::tests::
        semantic_scene_builder_shares_glyph_segments_across_raster_sizes());
    require(progpu::native::tests::
        semantic_scene_builder_records_native_shaped_runs());
    require(progpu::native::tests::
        semantic_scene_builder_records_color_bitmap_glyphs());
    require(progpu::native::tests::
        semantic_scene_builder_records_layers_masks_and_effects());
    require(progpu::native::tests::
        semantic_scene_builder_preserves_stable_resource_identities());
    require(progpu::native::tests::
        semantic_scene_builder_records_retained_3d_families());
    require(progpu::native::tests::
        semantic_scene_builder_records_retained_hit_test_index());
    require(progpu::native::tests::
        semantic_scene_content_hashes_isolate_image_updates());
    semantic_text_style_page_is_validated_deduplicated_and_retained();
    semantic_color_glyph_resource_is_strictly_validated();
    semantic_effect_output_cache_requires_exact_retained_identity();
    gpu_records_preserve_alignment_phase_and_cache_identity();
    semantic_state_is_cpu_only_and_target_relative();
    semantic_state_and_layer_cursors_restore_scopes();
    semantic_static_guidelines_adjust_state_at_target_dpi();
    semantic_payload_validation_is_bounded_and_cpu_only();
    draw_state_resolution_is_cpu_only_and_bounded();
    return 0;
}
