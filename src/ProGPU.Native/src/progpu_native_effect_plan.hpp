#pragma once

#include "progpu_native.h"

#include <array>
#include <cstdint>

namespace progpu::native::effects {

// One bounded ping-pong schedule entry. A negative source selects the isolated
// layer texture; non-negative values select one of three effect intermediates.
struct chain_plan_entry {
    std::int32_t source = -1;
    std::uint32_t horizontal = 0U;
    std::uint32_t vertical = 0U;
    std::uint32_t output = 0U;
};

// Builds an O(E) schedule with O(1) storage per node for at most eight nodes.
// Gaussian nodes need two intermediates; drop shadows need a third distinct
// input while composing the unblurred source over the shadow result.
std::array<chain_plan_entry, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
create_chain_plan(
    const progpu_native_group_effect* effects,
    std::uint32_t effect_count) noexcept;

} // namespace progpu::native::effects
