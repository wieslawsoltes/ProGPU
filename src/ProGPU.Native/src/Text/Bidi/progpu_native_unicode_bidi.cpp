#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Direct native port of ProGPU-owned Bidi/Uax9Resolver.cs at checkpoint
// d9e89879. Rule labels follow Unicode Standard Annex #9 revision 51.

namespace progpu::native::text {
namespace {

constexpr std::int8_t maximum_explicit_level = 125;
using bidi = unicode_bidi_class;

enum class override_direction : std::uint8_t {
    neutral,
    left_to_right,
    right_to_left
};

struct directional_status final {
    std::int8_t level = 0;
    override_direction direction = override_direction::neutral;
    bool isolate = false;
};

struct opening_bracket final {
    std::uint32_t position = 0U;
    std::uint32_t expected_close = 0U;
};

void set_error(unicode_error* error, unicode_error value) noexcept {
    if (error != nullptr) {
        *error = value;
    }
}

bool is_isolate_initiator(bidi type) noexcept {
    return type == bidi::left_to_right_isolate ||
        type == bidi::right_to_left_isolate ||
        type == bidi::first_strong_isolate;
}

bool is_removed_by_x9(bidi type) noexcept {
    return type == bidi::right_to_left_embedding ||
        type == bidi::left_to_right_embedding ||
        type == bidi::right_to_left_override ||
        type == bidi::left_to_right_override ||
        type == bidi::pop_directional_format ||
        type == bidi::boundary_neutral;
}

bool is_neutral(bidi type) noexcept {
    return type == bidi::paragraph_separator ||
        type == bidi::segment_separator ||
        type == bidi::whitespace ||
        type == bidi::other_neutral ||
        type == bidi::left_to_right_isolate ||
        type == bidi::right_to_left_isolate ||
        type == bidi::first_strong_isolate ||
        type == bidi::pop_directional_isolate;
}

bool is_l1_trailing(bidi type) noexcept {
    return type == bidi::whitespace || is_isolate_initiator(type) ||
        type == bidi::pop_directional_isolate ||
        type == bidi::right_to_left_embedding ||
        type == bidi::left_to_right_embedding ||
        type == bidi::right_to_left_override ||
        type == bidi::left_to_right_override ||
        type == bidi::pop_directional_format ||
        type == bidi::boundary_neutral;
}

bidi direction_of(std::int32_t level) noexcept {
    return (level & 1) == 0 ? bidi::left_to_right : bidi::right_to_left;
}

bidi strong_direction(bidi type) noexcept {
    if (type == bidi::left_to_right) {
        return bidi::left_to_right;
    }
    if (type == bidi::right_to_left ||
        type == bidi::european_number ||
        type == bidi::arabic_number) {
        return bidi::right_to_left;
    }
    return type;
}

std::int8_t odd_greater_than(std::int8_t level) noexcept {
    return static_cast<std::int8_t>((level + 1) | 1);
}

std::int8_t even_greater_than(std::int8_t level) noexcept {
    return static_cast<std::int8_t>((level + 2) & ~1);
}

std::uint32_t normalize_bracket(std::uint32_t code_point) noexcept {
    if (code_point == 0x2329U) {
        return 0x3008U;
    }
    if (code_point == 0x232AU) {
        return 0x3009U;
    }
    return code_point;
}

std::int8_t determine_paragraph_level(
    std::span<const unicode_bidi_unit> units,
    std::size_t start,
    std::size_t end) noexcept {
    std::uint32_t isolate_depth = 0U;
    for (std::size_t index = start; index < end; ++index) {
        const bidi type = units[index].original;
        if (is_isolate_initiator(type)) {
            ++isolate_depth;
            continue;
        }
        if (type == bidi::pop_directional_isolate) {
            if (isolate_depth != 0U) {
                --isolate_depth;
            }
            continue;
        }
        if (isolate_depth != 0U) {
            continue;
        }
        if (type == bidi::left_to_right) {
            return 0;
        }
        if (type == bidi::right_to_left || type == bidi::arabic_letter) {
            return 1;
        }
    }
    return 0;
}

void match_isolates(
    std::span<unicode_bidi_unit> units,
    std::span<std::uint32_t> stack) noexcept {
    std::size_t count = 0U;
    for (std::size_t index = 0U; index < units.size(); ++index) {
        if (is_isolate_initiator(units[index].original)) {
            stack[count++] = static_cast<std::uint32_t>(index);
        } else if (units[index].original == bidi::pop_directional_isolate &&
            count != 0U) {
            const std::size_t initiator = stack[--count];
            units[initiator].matching_isolate =
                static_cast<std::int32_t>(index);
            units[index].matching_isolate =
                static_cast<std::int32_t>(initiator);
        }
    }
}

bool determine_fsi_direction(
    std::span<const unicode_bidi_unit> units,
    std::size_t index) noexcept {
    const std::size_t end = units[index].matching_isolate >= 0
        ? static_cast<std::size_t>(units[index].matching_isolate)
        : units.size();
    return determine_paragraph_level(units, index + 1U, end) == 1;
}

void resolve_explicit_levels(
    std::span<unicode_bidi_unit> units,
    std::int8_t paragraph_level) noexcept {
    std::array<directional_status, 127U> stack{};
    std::size_t stack_count = 1U;
    stack[0U] = directional_status{paragraph_level};
    std::uint32_t overflow_isolates = 0U;
    std::uint32_t overflow_embeddings = 0U;
    std::uint32_t valid_isolates = 0U;

    const auto push_embedding = [&](std::int8_t level,
                                    override_direction direction) {
        if (level <= maximum_explicit_level && overflow_isolates == 0U &&
            overflow_embeddings == 0U) {
            stack[stack_count++] = directional_status{level, direction, false};
        } else if (overflow_isolates == 0U) {
            ++overflow_embeddings;
        }
    };

    for (std::size_t index = 0U; index < units.size(); ++index) {
        directional_status current = stack[stack_count - 1U];
        unicode_bidi_unit& unit = units[index];
        switch (unit.original) {
            case bidi::right_to_left_embedding:
                push_embedding(odd_greater_than(current.level),
                    override_direction::neutral);
                break;
            case bidi::left_to_right_embedding:
                push_embedding(even_greater_than(current.level),
                    override_direction::neutral);
                break;
            case bidi::right_to_left_override:
                push_embedding(odd_greater_than(current.level),
                    override_direction::right_to_left);
                break;
            case bidi::left_to_right_override:
                push_embedding(even_greater_than(current.level),
                    override_direction::left_to_right);
                break;
            case bidi::right_to_left_isolate:
            case bidi::left_to_right_isolate:
            case bidi::first_strong_isolate: {
                const bool rtl = unit.original == bidi::right_to_left_isolate ||
                    (unit.original == bidi::first_strong_isolate &&
                        determine_fsi_direction(units, index));
                unit.level = current.level;
                if (current.direction != override_direction::neutral) {
                    unit.type = current.direction ==
                            override_direction::left_to_right
                        ? bidi::left_to_right
                        : bidi::right_to_left;
                } else if (unit.original == bidi::first_strong_isolate) {
                    unit.type = rtl ? bidi::right_to_left_isolate
                                    : bidi::left_to_right_isolate;
                }
                const std::int8_t level = rtl
                    ? odd_greater_than(current.level)
                    : even_greater_than(current.level);
                if (level <= maximum_explicit_level &&
                    overflow_isolates == 0U && overflow_embeddings == 0U) {
                    ++valid_isolates;
                    stack[stack_count++] = directional_status{
                        level, override_direction::neutral, true};
                } else {
                    ++overflow_isolates;
                }
                break;
            }
            case bidi::pop_directional_isolate:
                if (overflow_isolates != 0U) {
                    --overflow_isolates;
                } else if (valid_isolates != 0U) {
                    overflow_embeddings = 0U;
                    while (stack_count > 1U &&
                        !stack[stack_count - 1U].isolate) {
                        --stack_count;
                    }
                    if (stack_count > 1U) {
                        --stack_count;
                    }
                    --valid_isolates;
                }
                current = stack[stack_count - 1U];
                unit.level = current.level;
                if (current.direction != override_direction::neutral) {
                    unit.type = current.direction ==
                            override_direction::left_to_right
                        ? bidi::left_to_right
                        : bidi::right_to_left;
                }
                break;
            case bidi::pop_directional_format:
                if (overflow_isolates != 0U) {
                    break;
                }
                if (overflow_embeddings != 0U) {
                    --overflow_embeddings;
                } else if (stack_count > 1U &&
                    !stack[stack_count - 1U].isolate) {
                    --stack_count;
                }
                break;
            case bidi::paragraph_separator:
                unit.level = paragraph_level;
                stack_count = 1U;
                overflow_isolates = 0U;
                overflow_embeddings = 0U;
                valid_isolates = 0U;
                break;
            case bidi::boundary_neutral:
                break;
            default:
                unit.level = current.level;
                if (current.direction != override_direction::neutral) {
                    unit.type = current.direction ==
                            override_direction::left_to_right
                        ? bidi::left_to_right
                        : bidi::right_to_left;
                }
                break;
        }
    }
}

void resolve_paired_brackets(
    std::span<unicode_bidi_unit> units,
    std::span<const std::uint32_t> sequence,
    bidi sor,
    std::span<unicode_bidi_bracket_pair> pairs) noexcept {
    std::array<opening_bracket, 63U> openings{};
    std::size_t opening_count = 0U;
    std::size_t pair_count = 0U;
    bool overflowed = false;
    for (std::size_t position = 0U; position < sequence.size(); ++position) {
        const unicode_bidi_unit& unit = units[sequence[position]];
        std::uint32_t paired = 0U;
        unicode_bidi_bracket_kind kind = unicode_bidi_bracket_kind::none;
        if (unit.type != bidi::other_neutral ||
            !try_get_unicode_bidi_bracket(unit.code_point, paired, kind)) {
            continue;
        }
        const std::uint32_t normalized = normalize_bracket(unit.code_point);
        if (kind == unicode_bidi_bracket_kind::open) {
            if (opening_count == openings.size()) {
                overflowed = true;
                break;
            }
            openings[opening_count++] = opening_bracket{
                static_cast<std::uint32_t>(position),
                normalize_bracket(paired)};
            continue;
        }
        for (std::size_t cursor = opening_count; cursor != 0U; --cursor) {
            const std::size_t opening = cursor - 1U;
            if (openings[opening].expected_close != normalized) {
                continue;
            }
            pairs[pair_count++] = unicode_bidi_bracket_pair{
                openings[opening].position,
                static_cast<std::uint32_t>(position)};
            opening_count = opening;
            break;
        }
    }
    if (overflowed) {
        return;
    }
    std::sort(pairs.begin(), pairs.begin() + pair_count,
        [](const auto& left, const auto& right) {
            return left.open_position < right.open_position;
        });
    for (std::size_t pair_index = 0U; pair_index < pair_count; ++pair_index) {
        const auto pair = pairs[pair_index];
        const std::size_t opening_unit = sequence[pair.open_position];
        const bidi embedding = direction_of(units[opening_unit].level);
        const bidi opposite = embedding == bidi::left_to_right
            ? bidi::right_to_left
            : bidi::left_to_right;
        bool contains_embedding = false;
        bool contains_opposite = false;
        for (std::size_t position = pair.open_position + 1U;
             position < pair.close_position;
             ++position) {
            const bidi strong = strong_direction(units[sequence[position]].type);
            if (strong == embedding) {
                contains_embedding = true;
                break;
            }
            contains_opposite |= strong == opposite;
        }
        bidi resolved = bidi::other_neutral;
        bool has_resolution = false;
        if (contains_embedding) {
            resolved = embedding;
            has_resolution = true;
        } else if (contains_opposite) {
            bidi preceding = sor;
            for (std::size_t position = pair.open_position;
                 position != 0U;
                 --position) {
                const bidi candidate = strong_direction(
                    units[sequence[position - 1U]].type);
                if (candidate == bidi::left_to_right ||
                    candidate == bidi::right_to_left) {
                    preceding = candidate;
                    break;
                }
            }
            resolved = preceding == opposite ? opposite : embedding;
            has_resolution = true;
        }
        if (!has_resolution) {
            continue;
        }
        units[opening_unit].type = resolved;
        units[sequence[pair.close_position]].type = resolved;
        for (std::size_t position = pair.close_position + 1U;
             position < sequence.size() &&
             units[sequence[position]].original == bidi::nonspacing_mark;
             ++position) {
            units[sequence[position]].type = resolved;
        }
    }
}

void resolve_sequence(
    std::span<unicode_bidi_unit> units,
    std::span<const std::uint32_t> sequence,
    bidi sor,
    bidi eor,
    std::span<unicode_bidi_bracket_pair> pairs) noexcept {
    bidi previous = sor;
    for (const std::uint32_t index : sequence) {
        if (units[index].type == bidi::nonspacing_mark) {
            units[index].type = is_isolate_initiator(previous) ||
                    previous == bidi::pop_directional_isolate
                ? bidi::other_neutral
                : previous;
        }
        previous = units[index].type;
    }
    bidi last_strong = sor;
    for (const std::uint32_t index : sequence) {
        if (units[index].type == bidi::european_number &&
            last_strong == bidi::arabic_letter) {
            units[index].type = bidi::arabic_number;
        }
        if (units[index].type == bidi::left_to_right ||
            units[index].type == bidi::right_to_left ||
            units[index].type == bidi::arabic_letter) {
            last_strong = units[index].type;
        }
    }
    for (const std::uint32_t index : sequence) {
        if (units[index].type == bidi::arabic_letter) {
            units[index].type = bidi::right_to_left;
        }
    }
    for (std::size_t position = 1U; position + 1U < sequence.size(); ++position) {
        const std::size_t index = sequence[position];
        const bidi type = units[index].type;
        const bidi before = units[sequence[position - 1U]].type;
        const bidi after = units[sequence[position + 1U]].type;
        if (type == bidi::european_separator &&
            before == bidi::european_number && after == bidi::european_number) {
            units[index].type = bidi::european_number;
        } else if (type == bidi::common_separator && before == after &&
            (before == bidi::european_number || before == bidi::arabic_number)) {
            units[index].type = before;
        }
    }
    std::size_t cursor = 0U;
    while (cursor < sequence.size()) {
        if (units[sequence[cursor]].type != bidi::european_terminator) {
            ++cursor;
            continue;
        }
        const std::size_t start = cursor;
        while (cursor < sequence.size() &&
            units[sequence[cursor]].type == bidi::european_terminator) {
            ++cursor;
        }
        const bool adjacent =
            (start != 0U && units[sequence[start - 1U]].type ==
                bidi::european_number) ||
            (cursor < sequence.size() && units[sequence[cursor]].type ==
                bidi::european_number);
        if (adjacent) {
            for (std::size_t position = start; position < cursor; ++position) {
                units[sequence[position]].type = bidi::european_number;
            }
        }
    }
    for (const std::uint32_t index : sequence) {
        if (units[index].type == bidi::european_separator ||
            units[index].type == bidi::european_terminator ||
            units[index].type == bidi::common_separator) {
            units[index].type = bidi::other_neutral;
        }
    }
    last_strong = sor;
    for (const std::uint32_t index : sequence) {
        if (units[index].type == bidi::european_number &&
            last_strong == bidi::left_to_right) {
            units[index].type = bidi::left_to_right;
        }
        if (units[index].type == bidi::left_to_right ||
            units[index].type == bidi::right_to_left) {
            last_strong = units[index].type;
        }
    }
    resolve_paired_brackets(units, sequence, sor, pairs);

    std::size_t position = 0U;
    while (position < sequence.size()) {
        if (!is_neutral(units[sequence[position]].type)) {
            ++position;
            continue;
        }
        const std::size_t start = position;
        while (position < sequence.size() &&
            is_neutral(units[sequence[position]].type)) {
            ++position;
        }
        const bidi before = start == 0U
            ? strong_direction(sor)
            : strong_direction(units[sequence[start - 1U]].type);
        const bidi after = position == sequence.size()
            ? strong_direction(eor)
            : strong_direction(units[sequence[position]].type);
        for (std::size_t neutral = start; neutral < position; ++neutral) {
            const std::size_t index = sequence[neutral];
            units[index].type = before == after
                ? before
                : direction_of(units[index].level);
        }
    }
    for (const std::uint32_t index : sequence) {
        unicode_bidi_unit& unit = units[index];
        if ((unit.level & 1) == 0) {
            if (unit.type == bidi::right_to_left) {
                ++unit.level;
            } else if (unit.type == bidi::european_number ||
                unit.type == bidi::arabic_number) {
                unit.level = static_cast<std::int8_t>(unit.level + 2);
            }
        } else if (unit.type == bidi::left_to_right ||
            unit.type == bidi::european_number ||
            unit.type == bidi::arabic_number) {
            ++unit.level;
        }
    }
}

void resolve_isolating_run_sequences(
    std::span<unicode_bidi_unit> units,
    std::int8_t paragraph_level,
    std::span<std::uint32_t> indices,
    std::span<unicode_bidi_level_run> runs,
    std::span<unicode_bidi_bracket_pair> pairs) noexcept {
    const std::size_t count = units.size();
    auto active = indices.subspan(0U, count);
    auto run_by_unit = indices.subspan(count, count);
    auto active_position = indices.subspan(count * 2U, count);
    auto sequence = indices.subspan(count * 3U, count);
    std::fill(run_by_unit.begin(), run_by_unit.end(),
        std::numeric_limits<std::uint32_t>::max());
    std::fill(active_position.begin(), active_position.end(),
        std::numeric_limits<std::uint32_t>::max());
    std::size_t active_count = 0U;
    for (std::size_t index = 0U; index < count; ++index) {
        if (!is_removed_by_x9(units[index].original)) {
            active[active_count] = static_cast<std::uint32_t>(index);
            active_position[index] = static_cast<std::uint32_t>(active_count++);
        }
    }
    if (active_count == 0U) {
        return;
    }
    std::size_t run_count = 0U;
    std::size_t start = 0U;
    while (start < active_count) {
        const std::int8_t level = units[active[start]].level;
        std::size_t end = start + 1U;
        while (end < active_count && units[active[end]].level == level) {
            ++end;
        }
        runs[run_count] = unicode_bidi_level_run{
            static_cast<std::uint32_t>(start),
            static_cast<std::uint32_t>(end - start),
            -1,
            level};
        for (std::size_t position = start; position < end; ++position) {
            run_by_unit[active[position]] = static_cast<std::uint32_t>(run_count);
        }
        ++run_count;
        start = end;
    }
    for (std::size_t run_index = 0U; run_index < run_count; ++run_index) {
        const unicode_bidi_level_run& run = runs[run_index];
        const std::size_t last = active[
            run.active_start + run.active_count - 1U];
        if (!is_isolate_initiator(units[last].original) ||
            units[last].matching_isolate < 0) {
            continue;
        }
        const std::uint32_t next = run_by_unit[
            static_cast<std::size_t>(units[last].matching_isolate)];
        if (next == std::numeric_limits<std::uint32_t>::max() ||
            next == run_index) {
            continue;
        }
        runs[run_index].next = static_cast<std::int32_t>(next);
        runs[next].has_predecessor = true;
    }
    for (std::size_t run_index = 0U; run_index < run_count; ++run_index) {
        if (runs[run_index].has_predecessor) {
            continue;
        }
        std::size_t sequence_count = 0U;
        std::size_t current = run_index;
        std::size_t last_run = run_index;
        while (current < run_count) {
            const auto& run = runs[current];
            std::copy_n(
                active.begin() + run.active_start,
                run.active_count,
                sequence.begin() + sequence_count);
            sequence_count += run.active_count;
            last_run = current;
            current = run.next >= 0
                ? static_cast<std::size_t>(run.next)
                : run_count;
        }
        const std::size_t first_position = active_position[sequence[0U]];
        const std::size_t last_position =
            active_position[sequence[sequence_count - 1U]];
        const auto& last_unit = units[sequence[sequence_count - 1U]];
        const std::int8_t preceding = first_position != 0U
            ? runs[run_by_unit[active[first_position - 1U]]].explicit_level
            : paragraph_level;
        const bool unmatched_isolate =
            is_isolate_initiator(last_unit.original) &&
            last_unit.matching_isolate < 0;
        const std::int8_t following = !unmatched_isolate &&
                last_position + 1U < active_count
            ? runs[run_by_unit[active[last_position + 1U]]].explicit_level
            : paragraph_level;
        const bidi sor = direction_of(std::max(
            runs[run_index].explicit_level, preceding));
        const bidi eor = direction_of(std::max(
            runs[last_run].explicit_level, following));
        resolve_sequence(
            units,
            sequence.first(sequence_count),
            sor,
            eor,
            pairs);
    }
}

void apply_line_rule_l1(
    std::span<unicode_bidi_unit> units,
    std::int8_t paragraph_level) noexcept {
    for (std::size_t index = 0U; index < units.size(); ++index) {
        if (units[index].original != bidi::paragraph_separator &&
            units[index].original != bidi::segment_separator) {
            continue;
        }
        units[index].level = paragraph_level;
        for (std::size_t preceding = index; preceding != 0U; --preceding) {
            if (!is_l1_trailing(units[preceding - 1U].original)) {
                break;
            }
            units[preceding - 1U].level = paragraph_level;
        }
    }
    for (std::size_t index = units.size(); index != 0U; --index) {
        if (!is_l1_trailing(units[index - 1U].original)) {
            break;
        }
        units[index - 1U].level = paragraph_level;
    }
}

void retain_explicit_levels(
    std::span<unicode_bidi_unit> units,
    std::int8_t paragraph_level) noexcept {
    std::int8_t previous = paragraph_level;
    for (auto& unit : units) {
        if (is_removed_by_x9(unit.original)) {
            unit.level = previous;
        } else {
            previous = unit.level;
        }
    }
}

} // namespace

bool try_get_unicode_bidi_requirements(
    std::span<const unicode_scalar> input,
    unicode_bidi_requirements& result,
    unicode_error* error) noexcept {
    result = {};
    if (input.size() > std::numeric_limits<std::uint32_t>::max() ||
        input.size() > std::numeric_limits<std::uint32_t>::max() / 4U) {
        set_error(error, unicode_error::invalid_argument);
        return false;
    }
    result.unit_count = static_cast<std::uint32_t>(input.size());
    result.index_count = result.unit_count * 4U;
    result.run_count = result.unit_count;
    result.bracket_pair_count = result.unit_count / 2U;
    set_error(error, unicode_error::none);
    return true;
}

bool try_resolve_unicode_bidi(
    std::span<const unicode_scalar> input,
    std::int8_t requested_paragraph_level,
    unicode_bidi_scratch scratch,
    std::span<unicode_bidi_level> output,
    std::int8_t& paragraph_level,
    std::uint32_t& written,
    unicode_error* error) noexcept {
    paragraph_level = requested_paragraph_level == 1 ? 1 : 0;
    written = 0U;
    unicode_bidi_requirements requirements{};
    if (!try_get_unicode_bidi_requirements(input, requirements, error)) {
        return false;
    }
    if (scratch.units.size() < requirements.unit_count ||
        scratch.indices.size() < requirements.index_count ||
        scratch.runs.size() < requirements.run_count ||
        scratch.bracket_pairs.size() < requirements.bracket_pair_count ||
        output.size() < requirements.unit_count) {
        set_error(error, unicode_error::insufficient_buffer);
        return false;
    }
    if (input.empty()) {
        set_error(error, unicode_error::none);
        return true;
    }
    auto units = scratch.units.first(input.size());
    for (std::size_t index = 0U; index < input.size(); ++index) {
        const unicode_scalar& scalar = input[index];
        const bidi type = get_unicode_bidi_class(scalar.code_point);
        units[index] = unicode_bidi_unit{
            scalar.code_point,
            scalar.input_index,
            scalar.input_length,
            type,
            type};
    }
    match_isolates(units, scratch.indices.first(input.size()));
    paragraph_level = requested_paragraph_level == 0 ||
            requested_paragraph_level == 1
        ? requested_paragraph_level
        : determine_paragraph_level(units, 0U, units.size());
    resolve_explicit_levels(units, paragraph_level);
    resolve_isolating_run_sequences(
        units,
        paragraph_level,
        scratch.indices.first(requirements.index_count),
        scratch.runs.first(requirements.run_count),
        scratch.bracket_pairs.first(requirements.bracket_pair_count));
    apply_line_rule_l1(units, paragraph_level);
    retain_explicit_levels(units, paragraph_level);
    for (std::size_t index = 0U; index < units.size(); ++index) {
        output[index] = unicode_bidi_level{
            units[index].input_index,
            units[index].input_length,
            units[index].level};
    }
    written = static_cast<std::uint32_t>(units.size());
    set_error(error, unicode_error::none);
    return true;
}

} // namespace progpu::native::text
