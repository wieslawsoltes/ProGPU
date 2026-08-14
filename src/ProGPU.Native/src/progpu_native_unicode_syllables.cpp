#include "progpu_native_text.hpp"

#include "progpu_native_unicode_data.generated.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <span>

// Allocation-free execution of the exact ProGPU-owned managed Indic, USE,
// Myanmar, and Khmer syllable machines. The caller may provide a filtered
// machine-index span (including its end sentinel) so USE can omit CGJ and
// contextually ignored ZWNJ without copying glyph records.

namespace progpu::native::text {
namespace {

std::uint8_t from_state_action(
    unicode_syllable_machine machine,
    std::uint16_t state) noexcept {
    switch (machine) {
        case unicode_syllable_machine::indic:
            return state < detail::unicode_indic_from_state_actions.size()
                ? detail::unicode_indic_from_state_actions[state] : 0U;
        case unicode_syllable_machine::use:
            return state < detail::unicode_use_from_state_actions.size()
                ? detail::unicode_use_from_state_actions[state] : 0U;
        case unicode_syllable_machine::myanmar:
            return state < detail::unicode_myanmar_from_state_actions.size()
                ? detail::unicode_myanmar_from_state_actions[state] : 0U;
        case unicode_syllable_machine::khmer:
            return state < detail::unicode_khmer_from_state_actions.size()
                ? detail::unicode_khmer_from_state_actions[state] : 0U;
    }
    return 0U;
}

std::uint8_t to_state_action(
    unicode_syllable_machine machine,
    std::uint16_t state) noexcept {
    switch (machine) {
        case unicode_syllable_machine::indic:
            return state < detail::unicode_indic_to_state_actions.size()
                ? detail::unicode_indic_to_state_actions[state] : 0U;
        case unicode_syllable_machine::use:
            return state < detail::unicode_use_to_state_actions.size()
                ? detail::unicode_use_to_state_actions[state] : 0U;
        case unicode_syllable_machine::myanmar:
            return state < detail::unicode_myanmar_to_state_actions.size()
                ? detail::unicode_myanmar_to_state_actions[state] : 0U;
        case unicode_syllable_machine::khmer:
            return state < detail::unicode_khmer_to_state_actions.size()
                ? detail::unicode_khmer_to_state_actions[state] : 0U;
    }
    return 0U;
}

std::uint8_t from_state_token_action(
    unicode_syllable_machine machine) noexcept {
    switch (machine) {
        case unicode_syllable_machine::indic: return 10U;
        case unicode_syllable_machine::use: return 3U;
        case unicode_syllable_machine::myanmar: return 2U;
        case unicode_syllable_machine::khmer: return 7U;
    }
    return 0U;
}

std::uint8_t to_state_token_action(
    unicode_syllable_machine machine) noexcept {
    switch (machine) {
        case unicode_syllable_machine::indic: return 9U;
        case unicode_syllable_machine::use: return 2U;
        case unicode_syllable_machine::myanmar: return 1U;
        case unicode_syllable_machine::khmer: return 6U;
    }
    return 0U;
}

std::size_t mapped_index(
    std::span<const std::uint32_t> machine_indices,
    std::size_t position) noexcept {
    return machine_indices.empty()
        ? position
        : static_cast<std::size_t>(machine_indices[position]);
}

void assign_syllable(
    std::span<const std::uint32_t> machine_indices,
    std::span<std::uint8_t> syllables,
    std::ptrdiff_t token_start,
    std::ptrdiff_t token_end,
    std::uint8_t type,
    std::uint8_t& serial) noexcept {
    if (token_start < 0 || token_end < token_start) {
        return;
    }
    const auto start = mapped_index(
        machine_indices, static_cast<std::size_t>(token_start));
    const auto end = mapped_index(
        machine_indices, static_cast<std::size_t>(token_end));
    std::fill(
        syllables.begin() + static_cast<std::ptrdiff_t>(start),
        syllables.begin() + static_cast<std::ptrdiff_t>(end),
        static_cast<std::uint8_t>((serial << 4U) | type));
    if (++serial == 16U) {
        serial = 1U;
    }
}

void apply_indic_action(
    std::uint8_t action,
    std::ptrdiff_t& position,
    std::ptrdiff_t& token_end,
    std::uint8_t& pending_action,
    std::ptrdiff_t token_start,
    std::span<const std::uint32_t> indices,
    std::span<std::uint8_t> syllables,
    std::uint8_t& serial) noexcept {
    switch (action) {
        case 2U: token_end = position + 1; break;
        case 11U: token_end = position + 1; assign_syllable(indices, syllables, token_start, token_end, 5U, serial); break;
        case 14U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 0U, serial); break;
        case 15U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 1U, serial); break;
        case 18U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 2U, serial); break;
        case 20U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 3U, serial); break;
        case 16U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 4U, serial); break;
        case 17U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 5U, serial); break;
        case 1U: position = token_end - 1; assign_syllable(indices, syllables, token_start, token_end, 0U, serial); break;
        case 3U: position = token_end - 1; assign_syllable(indices, syllables, token_start, token_end, 1U, serial); break;
        case 7U: position = token_end - 1; assign_syllable(indices, syllables, token_start, token_end, 2U, serial); break;
        case 8U: position = token_end - 1; assign_syllable(indices, syllables, token_start, token_end, 3U, serial); break;
        case 4U: position = token_end - 1; assign_syllable(indices, syllables, token_start, token_end, 4U, serial); break;
        case 6U:
            position = token_end - 1;
            assign_syllable(indices, syllables, token_start, token_end,
                pending_action == 1U ? 0U : pending_action == 6U ? 4U : 5U,
                serial);
            break;
        case 19U: token_end = position + 1; pending_action = 1U; break;
        case 13U: token_end = position + 1; pending_action = 5U; break;
        case 5U: token_end = position + 1; pending_action = 6U; break;
        case 12U: token_end = position + 1; pending_action = 7U; break;
        default: break;
    }
}

void apply_use_action(
    std::uint8_t action,
    std::ptrdiff_t& position,
    std::ptrdiff_t& token_end,
    std::uint8_t& pending_action,
    std::ptrdiff_t token_start,
    std::span<const std::uint32_t> indices,
    std::span<std::uint8_t> syllables,
    std::uint8_t& serial) noexcept {
    if (action == 7U) {
        token_end = position + 1;
        return;
    }
    constexpr std::uint8_t direct_types[26U]{
        0U, 5U, 0U, 0U, 8U, 7U, 0U, 0U, 0U, 5U, 5U, 2U, 2U,
        1U, 1U, 0U, 0U, 4U, 4U, 3U, 3U, 7U, 0U, 8U, 6U, 6U};
    switch (action) {
        case 16U: case 14U: case 12U: case 20U: case 18U:
        case 10U: case 25U: case 5U: case 4U:
            token_end = position + 1;
            assign_syllable(indices, syllables, token_start, token_end,
                direct_types[action], serial);
            break;
        case 15U: case 13U: case 11U: case 19U: case 17U:
        case 9U: case 24U: case 21U: case 23U:
            token_end = position;
            --position;
            assign_syllable(indices, syllables, token_start, token_end,
                direct_types[action], serial);
            break;
        case 1U:
            position = token_end - 1;
            assign_syllable(indices, syllables, token_start, token_end, 5U, serial);
            break;
        case 22U:
            position = token_end - 1;
            assign_syllable(indices, syllables, token_start, token_end,
                pending_action == 9U ? 7U : 8U, serial);
            break;
        case 6U: token_end = position + 1; pending_action = 8U; break;
        case 8U: token_end = position + 1; pending_action = 9U; break;
        default: break;
    }
}

void apply_myanmar_action(
    std::uint8_t action,
    std::ptrdiff_t& position,
    std::ptrdiff_t& token_end,
    std::uint8_t& pending_action,
    std::ptrdiff_t token_start,
    std::span<const std::uint32_t> indices,
    std::span<std::uint8_t> syllables,
    std::uint8_t& serial) noexcept {
    switch (action) {
        case 8U: token_end = position + 1; assign_syllable(indices, syllables, token_start, token_end, 0U, serial); break;
        case 4U: case 3U: token_end = position + 1; assign_syllable(indices, syllables, token_start, token_end, 2U, serial); break;
        case 10U: token_end = position + 1; assign_syllable(indices, syllables, token_start, token_end, 1U, serial); break;
        case 7U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 0U, serial); break;
        case 9U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 1U, serial); break;
        case 12U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 2U, serial); break;
        case 11U:
            position = token_end - 1;
            assign_syllable(indices, syllables, token_start, token_end,
                pending_action == 2U ? 2U : 1U, serial);
            break;
        case 6U: token_end = position + 1; pending_action = 2U; break;
        case 5U: token_end = position + 1; pending_action = 3U; break;
        default: break;
    }
}

void apply_khmer_action(
    std::uint8_t action,
    std::ptrdiff_t& position,
    std::ptrdiff_t& token_end,
    std::uint8_t& pending_action,
    std::ptrdiff_t token_start,
    std::span<const std::uint32_t> indices,
    std::span<std::uint8_t> syllables,
    std::uint8_t& serial) noexcept {
    switch (action) {
        case 2U: token_end = position + 1; break;
        case 8U: token_end = position + 1; assign_syllable(indices, syllables, token_start, token_end, 2U, serial); break;
        case 10U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 0U, serial); break;
        case 11U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 1U, serial); break;
        case 12U: token_end = position; --position; assign_syllable(indices, syllables, token_start, token_end, 2U, serial); break;
        case 1U: position = token_end - 1; assign_syllable(indices, syllables, token_start, token_end, 0U, serial); break;
        case 3U: position = token_end - 1; assign_syllable(indices, syllables, token_start, token_end, 1U, serial); break;
        case 5U:
            position = token_end - 1;
            assign_syllable(indices, syllables, token_start, token_end,
                pending_action == 2U ? 1U : 2U, serial);
            break;
        case 4U: token_end = position + 1; pending_action = 2U; break;
        case 9U: token_end = position + 1; pending_action = 3U; break;
        default: break;
    }
}

} // namespace

bool try_assign_unicode_syllables(
    unicode_syllable_machine machine,
    std::span<const std::uint8_t> categories,
    std::span<const std::uint32_t> machine_indices,
    std::span<std::uint8_t> syllables) noexcept {
    if (syllables.size() < categories.size() ||
        static_cast<std::uint8_t>(machine) >
            static_cast<std::uint8_t>(unicode_syllable_machine::khmer)) {
        return false;
    }

    std::size_t machine_count = categories.size();
    if (!machine_indices.empty()) {
        if (machine_indices.size() < 2U ||
            machine_indices.back() != categories.size()) {
            return false;
        }
        for (std::size_t index = 1U; index < machine_indices.size(); ++index) {
            if (machine_indices[index - 1U] >= machine_indices[index]) {
                return false;
            }
        }
        machine_count = machine_indices.size() - 1U;
    }

    std::fill_n(syllables.begin(), categories.size(), std::uint8_t{0U});
    if (machine_count == 0U) {
        return true;
    }

    auto state = get_unicode_syllable_machine_start_state(machine);
    std::ptrdiff_t position = 0;
    std::ptrdiff_t token_start = -1;
    std::ptrdiff_t token_end = -1;
    std::uint8_t pending_action = 0U;
    std::uint8_t serial = 1U;
    while (true) {
        unicode_syllable_transition transition{};
        if (position == static_cast<std::ptrdiff_t>(machine_count)) {
            if (!try_get_unicode_syllable_eof_transition(
                    machine, state, transition)) {
                break;
            }
        } else {
            if (from_state_action(machine, state) ==
                from_state_token_action(machine)) {
                token_start = position;
            }
            const auto glyph_index = mapped_index(
                machine_indices, static_cast<std::size_t>(position));
            if (!try_get_unicode_syllable_transition(
                    machine, state, categories[glyph_index], transition)) {
                return false;
            }
        }

        state = transition.target;
        switch (machine) {
            case unicode_syllable_machine::indic:
                apply_indic_action(transition.action, position, token_end,
                    pending_action, token_start, machine_indices, syllables,
                    serial);
                break;
            case unicode_syllable_machine::use:
                apply_use_action(transition.action, position, token_end,
                    pending_action, token_start, machine_indices, syllables,
                    serial);
                break;
            case unicode_syllable_machine::myanmar:
                apply_myanmar_action(transition.action, position, token_end,
                    pending_action, token_start, machine_indices, syllables,
                    serial);
                break;
            case unicode_syllable_machine::khmer:
                apply_khmer_action(transition.action, position, token_end,
                    pending_action, token_start, machine_indices, syllables,
                    serial);
                break;
        }
        if (to_state_action(machine, state) == to_state_token_action(machine)) {
            token_start = -1;
        }
        ++position;
        if (position < 0 ||
            position > static_cast<std::ptrdiff_t>(machine_count)) {
            break;
        }
    }
    return true;
}

} // namespace progpu::native::text
