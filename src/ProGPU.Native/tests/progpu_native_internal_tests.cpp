#include "progpu_native_effect_plan.hpp"
#include "progpu_native_semantic_budget.hpp"

#include <array>
#include <cstdint>
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

} // namespace

int main() {
    effect_plan_uses_three_bounded_intermediates();
    semantic_budget_counts_effected_depth_once();
    semantic_compilation_budget_is_checked();
    return 0;
}
