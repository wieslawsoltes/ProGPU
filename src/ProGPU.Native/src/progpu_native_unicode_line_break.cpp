#include "progpu_native_text.hpp"

#include "progpu_native_unicode_data.generated.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <span>

// Direct implementation of Unicode 17 UAX #14 revision 55 over the packed
// ProGPU-owned generated property tables. Classes are resolved once into
// caller scratch; no regex engine, heap graph, or per-boundary interop is used.

namespace progpu::native::text {
namespace {

using value = unicode_line_break_class;

void set_error(unicode_error* error, unicode_error result) noexcept {
    if (error != nullptr) {
        *error = result;
    }
}

std::uint32_t find_range_value(
    std::uint32_t code_point,
    std::span<const std::uint32_t> ranges,
    std::uint32_t fallback) noexcept {
    std::size_t low = 0U;
    std::size_t high = ranges.size() / 3U;
    while (low < high) {
        const std::size_t middle = low + (high - low) / 2U;
        const std::size_t offset = middle * 3U;
        if (code_point < ranges[offset]) {
            high = middle;
        } else if (code_point > ranges[offset + 1U]) {
            low = middle + 1U;
        } else {
            return ranges[offset + 2U];
        }
    }
    return fallback;
}

bool is_hard(value item) noexcept {
    return item == value::mandatory || item == value::carriage_return ||
        item == value::line_feed || item == value::next_line;
}

bool is_alphabetic(value item) noexcept {
    return item == value::alphabetic || item == value::hebrew_letter;
}

bool is_hangul(value item) noexcept {
    return item == value::hangul_l || item == value::hangul_v ||
        item == value::hangul_t || item == value::hangul_lv ||
        item == value::hangul_lvt;
}

bool is_ideographic(value item) noexcept {
    return item == value::ideographic || item == value::emoji_base ||
        item == value::emoji_modifier;
}

bool is_east_asian(std::uint32_t code_point) noexcept {
    return find_range_value(
        code_point,
        std::span<const std::uint32_t>{
            detail::unicode_line_break_east_asian_ranges},
        0U) != 0U;
}

std::uint32_t quotation_category(std::uint32_t code_point) noexcept {
    return find_range_value(
        code_point,
        std::span<const std::uint32_t>{
            detail::unicode_line_break_quotation_categories},
        0U);
}

bool is_mark(std::uint32_t code_point) noexcept {
    return find_range_value(
        code_point,
        std::span<const std::uint32_t>{
            detail::unicode_line_break_mark_ranges},
        0U) != 0U;
}

bool is_unassigned(std::uint32_t code_point) noexcept {
    return find_range_value(
        code_point,
        std::span<const std::uint32_t>{
            detail::unicode_line_break_unassigned_ranges},
        0U) != 0U;
}

value resolve_class(std::uint32_t code_point) noexcept {
    const value raw = get_unicode_line_break_class(code_point);
    switch (raw) {
        case value::ambiguous:
        case value::surrogate:
        case value::unknown:
            return value::alphabetic;
        case value::conditional_japanese:
            return value::nonstarter;
        case value::complex_context:
            return is_mark(code_point)
                ? value::combining_mark
                : value::alphabetic;
        default:
            return raw;
    }
}

std::size_t previous_non_space(
    std::span<const value> classes,
    std::size_t before) noexcept {
    while (before != 0U) {
        --before;
        if (classes[before] != value::space) {
            return before;
        }
    }
    return classes.size();
}

std::size_t source_base_index(
    std::span<const unicode_scalar> input,
    std::size_t index) noexcept {
    while (index != 0U) {
        const value raw = get_unicode_line_break_class(
            input[index].code_point);
        if (raw != value::combining_mark &&
            raw != value::zero_width_joiner &&
            !(raw == value::complex_context &&
                is_mark(input[index].code_point))) {
            break;
        }
        --index;
    }
    return index;
}

std::size_t next_source_base_index(
    std::span<const unicode_scalar> input,
    std::size_t index) noexcept {
    while (index < input.size()) {
        const value raw = get_unicode_line_break_class(
            input[index].code_point);
        if (raw != value::combining_mark &&
            raw != value::zero_width_joiner &&
            !(raw == value::complex_context &&
                is_mark(input[index].code_point))) {
            break;
        }
        ++index;
    }
    return index;
}

bool numeric_left_context(
    std::span<const value> classes,
    std::size_t left) noexcept {
    while (classes[left] == value::break_symbol ||
           classes[left] == value::infix_numeric) {
        if (left == 0U) {
            return false;
        }
        --left;
    }
    return classes[left] == value::numeric;
}

bool numeric_right_context(
    std::span<const value> classes,
    std::size_t right) noexcept {
    if (right >= classes.size()) {
        return false;
    }
    if (classes[right] == value::open_punctuation) {
        ++right;
    }
    if (right < classes.size() &&
        classes[right] == value::infix_numeric) {
        ++right;
    }
    return right < classes.size() && classes[right] == value::numeric;
}

text_line_break_kind boundary(
    std::span<const unicode_scalar> input,
    std::span<const value> classes,
    std::size_t right) noexcept {
    const std::size_t left_index = right - 1U;
    const value left = classes[left_index];
    const value next = classes[right];
    const value raw_left = get_unicode_line_break_class(
        input[left_index].code_point);
    const value raw_next = get_unicode_line_break_class(
        input[right].code_point);
    const std::size_t left_base = source_base_index(input, left_index);

    // LB5-LB12a: non-tailorable breaks, spaces, combining sequences, joiners.
    if (raw_left == value::carriage_return &&
        raw_next == value::line_feed) {
        return text_line_break_kind::prohibited;
    }
    if (is_hard(raw_left)) {
        return text_line_break_kind::mandatory;
    }
    if (is_hard(raw_next) || raw_next == value::space ||
        raw_next == value::zero_width_space) {
        return text_line_break_kind::prohibited;
    }
    const bool next_combining = raw_next == value::combining_mark ||
        raw_next == value::zero_width_joiner ||
        (raw_next == value::complex_context &&
            is_mark(input[right].code_point));
    if (next_combining && left != value::space && !is_hard(left) &&
        left != value::zero_width_space) {
        return text_line_break_kind::prohibited;
    }
    const std::size_t prior = previous_non_space(classes, right);
    if (prior != classes.size() &&
        get_unicode_line_break_class(input[prior].code_point) ==
            value::zero_width_space) {
        return text_line_break_kind::opportunity;
    }
    if (raw_left == value::zero_width_joiner ||
        left == value::word_joiner || next == value::word_joiner ||
        left == value::glue) {
        return text_line_break_kind::prohibited;
    }
    if (next == value::glue && left != value::space &&
        left != value::break_after && left != value::hyphen &&
        left != value::unambiguous_hyphen) {
        return text_line_break_kind::prohibited;
    }

    // LB13-LB18: punctuation with space-sensitive context.
    if (next == value::close_punctuation ||
        next == value::close_parenthesis || next == value::exclamation ||
        next == value::break_symbol) {
        return text_line_break_kind::prohibited;
    }
    if (prior != classes.size() && classes[prior] == value::open_punctuation) {
        return text_line_break_kind::prohibited;
    }
    if (prior != classes.size() && classes[prior] == value::quotation) {
        const std::size_t quote_base = source_base_index(input, prior);
        if (quotation_category(input[quote_base].code_point) == 1U) {
            if (quote_base == 0U ||
                is_hard(classes[quote_base - 1U]) ||
                classes[quote_base - 1U] == value::open_punctuation ||
                classes[quote_base - 1U] == value::quotation ||
                classes[quote_base - 1U] == value::glue ||
                classes[quote_base - 1U] == value::space ||
                classes[quote_base - 1U] == value::zero_width_space) {
                return text_line_break_kind::prohibited;
            }
        }
    }
    if (next == value::quotation &&
        quotation_category(input[right].code_point) == 2U &&
        (right + 1U == classes.size() ||
            classes[right + 1U] == value::space ||
            classes[right + 1U] == value::glue ||
            classes[right + 1U] == value::word_joiner ||
            classes[right + 1U] == value::close_punctuation ||
            classes[right + 1U] == value::quotation ||
            classes[right + 1U] == value::close_parenthesis ||
            classes[right + 1U] == value::exclamation ||
            classes[right + 1U] == value::infix_numeric ||
            classes[right + 1U] == value::break_symbol ||
            is_hard(classes[right + 1U]) ||
            classes[right + 1U] == value::zero_width_space)) {
        return text_line_break_kind::prohibited;
    }
    if (left == value::space && next == value::infix_numeric &&
        right + 1U < classes.size() &&
        classes[right + 1U] == value::numeric) {
        return text_line_break_kind::opportunity;
    }
    if (next == value::infix_numeric) {
        return text_line_break_kind::prohibited;
    }
    if (prior != classes.size() &&
        (classes[prior] == value::close_punctuation ||
            classes[prior] == value::close_parenthesis) &&
        next == value::nonstarter) {
        return text_line_break_kind::prohibited;
    }
    if (prior != classes.size() && classes[prior] == value::break_both &&
        next == value::break_both) {
        return text_line_break_kind::prohibited;
    }
    if (left == value::space) {
        return text_line_break_kind::opportunity;
    }

    // LB19/LB19a: Pi may open and Pf may close only when the unresolved quote
    // is surrounded by East-Asian characters. All other quote sides stay
    // attached. LB15a/LB15b above take precedence across spaces.
    if (left == value::quotation) {
        const std::uint32_t category = quotation_category(
            input[left_base].code_point);
        const std::size_t before_quote = left_base == 0U
            ? input.size()
            : source_base_index(input, left_base - 1U);
        if (category != 2U ||
            !is_east_asian(input[right].code_point) ||
            before_quote == input.size() ||
            !is_east_asian(input[before_quote].code_point)) {
            return text_line_break_kind::prohibited;
        }
    }
    if (next == value::quotation) {
        const std::uint32_t category = quotation_category(
            input[right].code_point);
        const std::size_t after_quote = next_source_base_index(
            input, right + 1U);
        if (category != 1U ||
            !is_east_asian(input[left_base].code_point) ||
            after_quote == input.size() ||
            !is_east_asian(input[after_quote].code_point)) {
            return text_line_break_kind::prohibited;
        }
    }

    // LB20-LB24.
    if (left == value::contingent || next == value::contingent) {
        return text_line_break_kind::opportunity;
    }
    if ((left == value::hyphen || left == value::unambiguous_hyphen) &&
        is_alphabetic(next)) {
        const bool word_initial = left_base == 0U ||
            is_hard(classes[left_base - 1U]) ||
            classes[left_base - 1U] == value::space ||
            classes[left_base - 1U] == value::zero_width_space ||
            classes[left_base - 1U] == value::contingent ||
            classes[left_base - 1U] == value::glue;
        if (word_initial) {
            return text_line_break_kind::prohibited;
        }
    }
    if (next == value::break_after ||
        next == value::unambiguous_hyphen || next == value::hyphen ||
        next == value::nonstarter || left == value::break_before) {
        return text_line_break_kind::prohibited;
    }
    if (left_base != 0U &&
        classes[left_base - 1U] == value::hebrew_letter &&
        (left == value::hyphen || left == value::unambiguous_hyphen) &&
        next != value::hebrew_letter) {
        return text_line_break_kind::prohibited;
    }
    if (left == value::break_symbol && next == value::hebrew_letter) {
        return text_line_break_kind::prohibited;
    }
    if (next == value::inseparable ||
        (is_alphabetic(left) && next == value::numeric) ||
        (left == value::numeric && is_alphabetic(next)) ||
        (left == value::prefix_numeric && is_ideographic(next)) ||
        (is_ideographic(left) && next == value::postfix_numeric) ||
        ((left == value::prefix_numeric || left == value::postfix_numeric) &&
            is_alphabetic(next)) ||
        (is_alphabetic(left) &&
            (next == value::prefix_numeric ||
                next == value::postfix_numeric))) {
        return text_line_break_kind::prohibited;
    }

    // LB25: numeric expressions.
    if ((next == value::postfix_numeric ||
            next == value::prefix_numeric || next == value::numeric) &&
        numeric_left_context(classes, left_index)) {
        return text_line_break_kind::prohibited;
    }
    if ((next == value::postfix_numeric ||
            next == value::prefix_numeric) &&
        (left == value::close_punctuation ||
            left == value::close_parenthesis) &&
        left_index != 0U &&
        numeric_left_context(classes, left_index - 1U)) {
        return text_line_break_kind::prohibited;
    }
    if ((left == value::postfix_numeric || left == value::prefix_numeric) &&
        numeric_right_context(classes, right)) {
        return text_line_break_kind::prohibited;
    }
    if ((left == value::hyphen || left == value::infix_numeric) &&
        next == value::numeric) {
        return text_line_break_kind::prohibited;
    }

    // LB26-LB30b: Hangul, words, Brahmic syllables, delimiters, flags, emoji.
    if ((left == value::hangul_l &&
            (next == value::hangul_l || next == value::hangul_v ||
                next == value::hangul_lv || next == value::hangul_lvt)) ||
        ((left == value::hangul_v || left == value::hangul_lv) &&
            (next == value::hangul_v || next == value::hangul_t)) ||
        ((left == value::hangul_t || left == value::hangul_lvt) &&
            next == value::hangul_t) ||
        (is_hangul(left) && next == value::postfix_numeric) ||
        (left == value::prefix_numeric && is_hangul(next)) ||
        (is_alphabetic(left) && is_alphabetic(next))) {
        return text_line_break_kind::prohibited;
    }
    const bool left_aksara = left == value::aksara ||
        left == value::aksara_start || input[left_base].code_point == 0x25CCU;
    const bool next_aksara = next == value::aksara ||
        next == value::aksara_start || input[right].code_point == 0x25CCU;
    if ((left == value::aksara_prebase && next_aksara) ||
        (left_aksara &&
            (next == value::virama_final || next == value::virama)) ||
        (left == value::virama && next_aksara && left_base != 0U &&
            (classes[source_base_index(input, left_base - 1U)] ==
                    value::aksara ||
                classes[source_base_index(input, left_base - 1U)] ==
                    value::aksara_start ||
                input[source_base_index(input, left_base - 1U)].code_point ==
                    0x25CCU)) ||
        (left_aksara && next_aksara && right + 1U < classes.size() &&
            classes[right + 1U] == value::virama_final) ||
        (left == value::infix_numeric && is_alphabetic(next))) {
        return text_line_break_kind::prohibited;
    }
    if ((is_alphabetic(left) || left == value::numeric) &&
        next == value::open_punctuation &&
        !is_east_asian(input[right].code_point)) {
        return text_line_break_kind::prohibited;
    }
    if (left == value::close_parenthesis &&
        !is_east_asian(input[left_base].code_point) &&
        (is_alphabetic(next) || next == value::numeric)) {
        return text_line_break_kind::prohibited;
    }
    if (left == value::regional_indicator &&
        next == value::regional_indicator) {
        std::size_t count = 0U;
        for (std::size_t index = left_index + 1U; index != 0U;) {
            --index;
            const value raw = get_unicode_line_break_class(
                input[index].code_point);
            if (raw == value::combining_mark ||
                raw == value::zero_width_joiner ||
                (raw == value::complex_context &&
                    is_mark(input[index].code_point))) {
                continue;
            }
            if (raw != value::regional_indicator) {
                break;
            }
            ++count;
        }
        if ((count & 1U) != 0U) {
            return text_line_break_kind::prohibited;
        }
    }
    if ((left == value::emoji_base ||
            (is_unassigned(input[left_base].code_point) &&
                is_unicode_extended_pictographic(
                    input[left_base].code_point))) &&
        next == value::emoji_modifier) {
        return text_line_break_kind::prohibited;
    }
    return text_line_break_kind::opportunity;
}

} // namespace

unicode_line_break_class get_unicode_line_break_class(
    std::uint32_t code_point) noexcept {
    if (code_point > 0x10FFFFU ||
        (code_point >= 0xD800U && code_point <= 0xDFFFU)) {
        return value::unknown;
    }
    return static_cast<value>(find_range_value(
        code_point,
        std::span<const std::uint32_t>{
            detail::unicode_line_break_ranges},
        static_cast<std::uint32_t>(value::unknown)));
}

bool try_resolve_unicode_line_breaks(
    std::span<const unicode_scalar> input,
    std::span<unicode_line_break_class> class_scratch,
    std::span<text_line_break_kind> breaks_after,
    unicode_error* error) noexcept {
    if (class_scratch.size() < input.size() ||
        breaks_after.size() < input.size()) {
        set_error(error, unicode_error::insufficient_buffer);
        return false;
    }
    for (std::size_t index = 0U; index < input.size(); ++index) {
        if (input[index].code_point > 0x10FFFFU ||
            (input[index].code_point >= 0xD800U &&
                input[index].code_point <= 0xDFFFU)) {
            set_error(error, unicode_error::invalid_argument);
            return false;
        }
        value resolved = resolve_class(input[index].code_point);
        if (resolved == value::combining_mark ||
            resolved == value::zero_width_joiner) {
            if (index != 0U && !is_hard(class_scratch[index - 1U]) &&
                class_scratch[index - 1U] != value::space &&
                class_scratch[index - 1U] != value::zero_width_space) {
                resolved = class_scratch[index - 1U];
            } else {
                resolved = value::alphabetic;
            }
        }
        class_scratch[index] = resolved;
    }
    if (input.empty()) {
        set_error(error, unicode_error::none);
        return true;
    }
    for (std::size_t right = 1U; right < input.size(); ++right) {
        breaks_after[right - 1U] = boundary(
            input,
            class_scratch.first(input.size()),
            right);
    }
    breaks_after[input.size() - 1U] = text_line_break_kind::mandatory;
    set_error(error, unicode_error::none);
    return true;
}

} // namespace progpu::native::text
