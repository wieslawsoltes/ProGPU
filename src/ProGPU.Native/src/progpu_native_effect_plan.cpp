#include "progpu_native_effect_plan.hpp"

namespace progpu::native::effects {

std::array<chain_plan_entry, PROGPU_NATIVE_MAX_GROUP_EFFECTS>
create_chain_plan(
    const progpu_native_group_effect* effects,
    std::uint32_t effect_count) noexcept {
    std::array<chain_plan_entry, PROGPU_NATIVE_MAX_GROUP_EFFECTS> plan{};
    std::int32_t source = -1;
    for (std::uint32_t index = 0U; index < effect_count; ++index) {
        auto& entry = plan[index];
        entry.source = source;
        for (std::uint32_t texture = 0U; texture < 3U; ++texture) {
            if (static_cast<std::int32_t>(texture) != source) {
                entry.horizontal = texture;
                break;
            }
        }
        if (effects[index].kind ==
            PROGPU_NATIVE_GROUP_EFFECT_DROP_SHADOW) {
            for (std::uint32_t texture = 0U; texture < 3U; ++texture) {
                if (texture != entry.horizontal &&
                    static_cast<std::int32_t>(texture) != source) {
                    entry.vertical = texture;
                    break;
                }
            }
            entry.output = entry.horizontal;
        } else {
            entry.vertical = source >= 0
                ? static_cast<std::uint32_t>(source)
                : (entry.horizontal == 0U ? 1U : 0U);
            entry.output = entry.vertical;
        }
        source = static_cast<std::int32_t>(entry.output);
    }
    return plan;
}

} // namespace progpu::native::effects
