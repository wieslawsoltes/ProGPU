#include "progpu_native_text.hpp"

#include "progpu_native_unicode_data.generated.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

// Typed native access to the exact ProGPU-owned managed Indic/USE property
// tries and syllable state machines. Lookup is fixed-depth O(1), allocation-
// free, and generated-data drift is rejected by the shared stale-data gate.

namespace progpu::native::text {
namespace {

std::uint8_t nibble(
    std::span<const std::uint8_t> values,
    std::uint32_t index) noexcept {
    return static_cast<std::uint8_t>(
        (values[index >> 1U] >> ((index & 1U) << 2U)) & 15U);
}

template<typename Key, typename Span, typename Offset, typename Index,
    typename Target, typename Action>
bool transition(
    std::uint16_t state,
    std::uint8_t category,
    const Key& keys,
    const Span& spans,
    const Offset& offsets,
    const Index& indices,
    const Target& targets,
    const Action& actions,
    unicode_syllable_transition& result) noexcept {
    if (state >= spans.size()) {
        return false;
    }
    const std::size_t key_offset = static_cast<std::size_t>(state) * 2U;
    const std::size_t span = spans[state];
    const std::size_t relative = span != 0U &&
        category >= keys[key_offset] && category <= keys[key_offset + 1U]
        ? category - keys[key_offset]
        : span;
    const std::size_t index = offsets[state] + relative;
    if (index >= indices.size()) {
        return false;
    }
    const std::size_t selected = indices[index];
    if (selected >= targets.size() || selected >= actions.size()) {
        return false;
    }
    result = unicode_syllable_transition{
        targets[selected], actions[selected], 0U};
    return true;
}

template<typename Eof, typename Target, typename Action>
bool eof_transition(
    std::uint16_t state,
    const Eof& eof,
    const Target& targets,
    const Action& actions,
    unicode_syllable_transition& result) noexcept {
    if (state >= eof.size() || eof[state] <= 0) {
        return false;
    }
    const std::size_t selected = static_cast<std::size_t>(eof[state] - 1);
    if (selected >= targets.size() || selected >= actions.size()) {
        return false;
    }
    result = unicode_syllable_transition{
        targets[selected], actions[selected], 0U};
    return true;
}

} // namespace

unicode_indic_shaping_properties get_unicode_indic_shaping_properties(
    std::uint32_t code_point) noexcept {
    std::size_t value_index = 37U;
    if (code_point < 71396U) {
        const auto& data = detail::unicode_indic_shaping_data;
        const std::size_t level0 = nibble(data, code_point >> 9U);
        const std::size_t level1 = data[
            70U + (level0 << 3U) + ((code_point >> 6U) & 7U)];
        const std::size_t level2 = data[
            186U + (level1 << 3U) + ((code_point >> 3U) & 7U)];
        const std::size_t level3 = data[
            488U + (level2 << 2U) + ((code_point >> 1U) & 3U)];
        value_index = data[996U + level3 + (code_point & 1U)];
    }
    const std::uint16_t properties =
        detail::unicode_indic_shaping_values[value_index];
    return unicode_indic_shaping_properties{
        static_cast<std::uint8_t>(properties),
        static_cast<std::uint8_t>(properties >> 8U)};
}

std::uint8_t get_unicode_use_shaping_category(
    std::uint32_t code_point) noexcept {
    if (code_point >= 921600U) {
        return 0U;
    }
    const auto& data8 = detail::unicode_use_shaping_data8;
    const auto& data16 = detail::unicode_use_shaping_data16;
    const std::size_t page = nibble(data8, code_point >> 12U);
    const std::size_t level1 = data8[
        113U + (page << 5U) + ((code_point >> 7U) & 31U)];
    const std::size_t level2 = data16[
        (level1 << 3U) + ((code_point >> 4U) & 7U)];
    const std::size_t level3 = data8[
        625U + level2 + ((code_point >> 1U) & 7U)];
    return data8[2953U + (level3 << 1U) + (code_point & 1U)];
}

bool is_unicode_mark(std::uint32_t code_point) noexcept {
    const auto category = get_unicode_general_category(code_point);
    return category == unicode_general_category::nonspacing_mark ||
        category == unicode_general_category::spacing_combining_mark ||
        category == unicode_general_category::enclosing_mark;
}

std::uint32_t get_unicode_vowel_constraint_count() noexcept {
    return static_cast<std::uint32_t>(
        detail::unicode_vowel_constraints.size() / 4U);
}

bool try_get_unicode_vowel_constraint(
    std::uint32_t index,
    unicode_vowel_constraint& result) noexcept {
    result = {};
    constexpr std::size_t stride = 4U;
    const auto offset = static_cast<std::size_t>(index) * stride;
    if (offset >= detail::unicode_vowel_constraints.size()) {
        return false;
    }
    result = unicode_vowel_constraint{
        open_type_tag{detail::unicode_vowel_constraints[offset]},
        detail::unicode_vowel_constraints[offset + 1U],
        detail::unicode_vowel_constraints[offset + 2U],
        detail::unicode_vowel_constraints[offset + 3U]};
    return true;
}

std::uint16_t get_unicode_syllable_machine_state_count(
    unicode_syllable_machine machine) noexcept {
    switch (machine) {
        case unicode_syllable_machine::indic:
            return detail::unicode_indic_state_count;
        case unicode_syllable_machine::use:
            return detail::unicode_use_state_count;
        case unicode_syllable_machine::myanmar:
            return detail::unicode_myanmar_state_count;
        case unicode_syllable_machine::khmer:
            return detail::unicode_khmer_state_count;
    }
    return 0U;
}

std::uint16_t get_unicode_syllable_machine_start_state(
    unicode_syllable_machine machine) noexcept {
    switch (machine) {
        case unicode_syllable_machine::indic:
            return detail::unicode_indic_start_state;
        case unicode_syllable_machine::use:
            return detail::unicode_use_start_state;
        case unicode_syllable_machine::myanmar:
            return detail::unicode_myanmar_start_state;
        case unicode_syllable_machine::khmer:
            return detail::unicode_khmer_start_state;
    }
    return 0U;
}

static bool try_get_state_action(
    std::uint16_t state,
    std::span<const std::uint8_t> actions,
    std::uint8_t& action) noexcept {
    action = 0U;
    if (state >= actions.size()) {
        return false;
    }
    action = actions[state];
    return true;
}

#define PROGPU_MACHINE_STATE_ACTION(direction, name) \
    return try_get_state_action( \
        state, detail::unicode_##name##_##direction##_state_actions, action)

bool try_get_unicode_syllable_to_state_action(
    unicode_syllable_machine machine,
    std::uint16_t state,
    std::uint8_t& action) noexcept {
    action = 0U;
    switch (machine) {
        case unicode_syllable_machine::indic:
            PROGPU_MACHINE_STATE_ACTION(to, indic);
        case unicode_syllable_machine::use:
            PROGPU_MACHINE_STATE_ACTION(to, use);
        case unicode_syllable_machine::myanmar:
            PROGPU_MACHINE_STATE_ACTION(to, myanmar);
        case unicode_syllable_machine::khmer:
            PROGPU_MACHINE_STATE_ACTION(to, khmer);
    }
    return false;
}

bool try_get_unicode_syllable_from_state_action(
    unicode_syllable_machine machine,
    std::uint16_t state,
    std::uint8_t& action) noexcept {
    action = 0U;
    switch (machine) {
        case unicode_syllable_machine::indic:
            PROGPU_MACHINE_STATE_ACTION(from, indic);
        case unicode_syllable_machine::use:
            PROGPU_MACHINE_STATE_ACTION(from, use);
        case unicode_syllable_machine::myanmar:
            PROGPU_MACHINE_STATE_ACTION(from, myanmar);
        case unicode_syllable_machine::khmer:
            PROGPU_MACHINE_STATE_ACTION(from, khmer);
    }
    return false;
}

#undef PROGPU_MACHINE_STATE_ACTION

#define PROGPU_MACHINE_TRANSITION(name) \
    return transition(state, category, \
        detail::unicode_##name##_trans_keys, \
        detail::unicode_##name##_key_spans, \
        detail::unicode_##name##_index_offsets, \
        detail::unicode_##name##_indices, \
        detail::unicode_##name##_trans_targets, \
        detail::unicode_##name##_trans_actions, result)

bool try_get_unicode_syllable_transition(
    unicode_syllable_machine machine,
    std::uint16_t state,
    std::uint8_t category,
    unicode_syllable_transition& result) noexcept {
    result = {};
    switch (machine) {
        case unicode_syllable_machine::indic:
            PROGPU_MACHINE_TRANSITION(indic);
        case unicode_syllable_machine::use:
            PROGPU_MACHINE_TRANSITION(use);
        case unicode_syllable_machine::myanmar:
            PROGPU_MACHINE_TRANSITION(myanmar);
        case unicode_syllable_machine::khmer:
            PROGPU_MACHINE_TRANSITION(khmer);
    }
    return false;
}

#undef PROGPU_MACHINE_TRANSITION

#define PROGPU_MACHINE_EOF(name) \
    return eof_transition(state, \
        detail::unicode_##name##_eof_transitions, \
        detail::unicode_##name##_trans_targets, \
        detail::unicode_##name##_trans_actions, result)

bool try_get_unicode_syllable_eof_transition(
    unicode_syllable_machine machine,
    std::uint16_t state,
    unicode_syllable_transition& result) noexcept {
    result = {};
    switch (machine) {
        case unicode_syllable_machine::indic:
            PROGPU_MACHINE_EOF(indic);
        case unicode_syllable_machine::use:
            PROGPU_MACHINE_EOF(use);
        case unicode_syllable_machine::myanmar:
            PROGPU_MACHINE_EOF(myanmar);
        case unicode_syllable_machine::khmer:
            PROGPU_MACHINE_EOF(khmer);
    }
    return false;
}

#undef PROGPU_MACHINE_EOF

} // namespace progpu::native::text
