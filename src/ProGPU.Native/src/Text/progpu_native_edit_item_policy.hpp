#pragma once

#include "progpu_native_text.hpp"

namespace progpu::native::text::detail {

inline constexpr std::uint32_t edit_item_soft_entry = 1U;
inline constexpr std::uint32_t edit_item_suppressed_entry = 2U;
inline constexpr std::uint32_t edit_item_white_exit = 4U;

// These are portable policy families, NOT Windows eScript identities. Their
// entry policies were observed independently; extending nominal BMP letters
// through the owned Unicode properties is an explicit profile inference.
enum class edit_item_profile : std::uint8_t {
    paragraph_bridge,
    numeric_bridge,
    arabic_nominal,
    syriac_nominal,
    hebrew_nominal,
    devanagari_nominal,
    thai_nominal,
    lao_nominal,
    khmer_nominal,
    myanmar_syllabic,
    balinese_symbols,
    hangul_symbols
};

enum class edit_item_source_role : std::uint8_t {
    ordinary,
    context_mark,
    inherited_mark,
    joiner,
    shared_cursive_modifier,
    white_space,
    hard_control,
    format_control
};

struct edit_item_properties final {
    edit_item_profile profile = edit_item_profile::paragraph_bridge;
    std::uint32_t flags = 0U;
    edit_item_source_role role = edit_item_source_role::ordinary;
    bool has_attachment_owner = true;
};

constexpr bool is_edit_symbol_item(edit_item_profile profile) noexcept
{
    return profile == edit_item_profile::balinese_symbols ||
        profile == edit_item_profile::hangul_symbols;
}

inline edit_item_profile get_edit_symbol_item_profile(const unicode_scalar& scalar) noexcept
{
    // The complete original three-context sweep contains exactly 19 Balinese
    // and 62 Hangul members of these owned property intersections. These are
    // typed item policies, not an observed-scalar table or Windows engine ID.
    if (scalar.code_point > 0xFFFFU ||
        get_unicode_general_category(scalar.code_point) != unicode_general_category::other_symbol ||
        get_unicode_line_break_class(scalar.code_point) != unicode_line_break_class::ideographic)
        return edit_item_profile::paragraph_bridge;
    const auto bidi = get_unicode_bidi_class(scalar.code_point);
    if (scalar.script == open_type_tag::from_chars('b', 'a', 'l', 'i') &&
        bidi == unicode_bidi_class::left_to_right) return edit_item_profile::balinese_symbols;
    if (scalar.script == open_type_tag::from_chars('h', 'a', 'n', 'g') &&
        (bidi == unicode_bidi_class::left_to_right || bidi == unicode_bidi_class::other_neutral))
        return edit_item_profile::hangul_symbols;
    return edit_item_profile::paragraph_bridge;
}

constexpr edit_item_profile get_edit_nominal_profile(open_type_tag script) noexcept
{
    if (script == open_type_tag::from_chars('a', 'r', 'a', 'b')) return edit_item_profile::arabic_nominal;
    if (script == open_type_tag::from_chars('s', 'y', 'r', 'c')) return edit_item_profile::syriac_nominal;
    if (script == open_type_tag::from_chars('h', 'e', 'b', 'r')) return edit_item_profile::hebrew_nominal;
    if (script == open_type_tag::from_chars('d', 'e', 'v', 'a')) return edit_item_profile::devanagari_nominal;
    if (script == open_type_tag::from_chars('t', 'h', 'a', 'i')) return edit_item_profile::thai_nominal;
    if (script == open_type_tag::from_chars('l', 'a', 'o', ' ') ||
        script == open_type_tag::from_chars('l', 'a', 'o', 'o')) return edit_item_profile::lao_nominal;
    if (script == open_type_tag::from_chars('k', 'h', 'm', 'r')) return edit_item_profile::khmer_nominal;
    if (script == open_type_tag::from_chars('m', 'y', 'm', 'r')) return edit_item_profile::myanmar_syllabic;
    return edit_item_profile::paragraph_bridge;
}

constexpr std::uint32_t get_edit_profile_flags(edit_item_profile profile) noexcept
{
    switch (profile) {
    case edit_item_profile::arabic_nominal:
    case edit_item_profile::syriac_nominal:
    case edit_item_profile::thai_nominal:
    case edit_item_profile::lao_nominal:
    case edit_item_profile::myanmar_syllabic:
    case edit_item_profile::balinese_symbols:
        return edit_item_soft_entry;
    case edit_item_profile::hebrew_nominal:
    case edit_item_profile::devanagari_nominal:
    case edit_item_profile::hangul_symbols:
        return edit_item_suppressed_entry;
    case edit_item_profile::khmer_nominal:
        return edit_item_suppressed_entry | edit_item_white_exit;
    default:
        return 0U;
    }
}

constexpr bool has_edit_nominal_bidi(edit_item_profile profile, unicode_bidi_class bidi) noexcept
{
    if (profile == edit_item_profile::arabic_nominal || profile == edit_item_profile::syriac_nominal)
        return bidi == unicode_bidi_class::arabic_letter;
    if (profile == edit_item_profile::hebrew_nominal)
        return bidi == unicode_bidi_class::right_to_left;
    return profile != edit_item_profile::paragraph_bridge &&
        bidi == unicode_bidi_class::left_to_right;
}

constexpr bool is_edit_letter(unicode_general_category category) noexcept
{
    return category >= unicode_general_category::uppercase_letter &&
        category <= unicode_general_category::other_letter;
}

constexpr bool is_edit_mark(unicode_general_category category) noexcept
{
    return category >= unicode_general_category::nonspacing_mark &&
        category <= unicode_general_category::enclosing_mark;
}

constexpr bool is_arabic_presentation_block(std::uint32_t code_point) noexcept
{
    // Exact named Unicode17 Blocks properties, not nominal Script membership:
    // https://www.unicode.org/Public/17.0.0/ucd/Blocks.txt
    return (code_point >= 0xFB50U && code_point <= 0xFDFFU) ||
        (code_point >= 0xFE70U && code_point <= 0xFEFFU);
}

inline bool try_classify_edit_item_properties(
    const unicode_scalar& scalar,
    edit_item_properties& result) noexcept
{
    using gc = unicode_general_category;
    using bc = unicode_bidi_class;
    using lb = unicode_line_break_class;
    const auto category = get_unicode_general_category(scalar.code_point);
    const auto bidi = get_unicode_bidi_class(scalar.code_point);
    const auto raw = get_unicode_line_break_class(scalar.code_point);
    const auto nominal = get_edit_nominal_profile(scalar.script);
    const auto symbol = get_edit_symbol_item_profile(scalar);
    edit_item_properties candidate{};
    if (is_edit_symbol_item(symbol)) {
        candidate.profile = symbol;
        candidate.flags = get_edit_profile_flags(symbol);
    } else if (nominal == edit_item_profile::myanmar_syllabic) {
        // The original Myanmar machine owns consonants, digits AND broken
        // leading/medial mark groups. Marks must not attach to a foreign item
        // or require an invented preceding base before that machine can run.
        if (scalar.code_point > 0xFFFFU ||
            (bidi != bc::left_to_right && bidi != bc::nonspacing_mark) ||
            (!is_edit_letter(category) && !is_edit_mark(category) && category != gc::decimal_digit_number) ||
            get_unicode_indic_shaping_properties(scalar.code_point).category == 0U) return false;
        if (category == gc::decimal_digit_number) {
            const auto decimal = get_unicode_decimal_digit_value(scalar.code_point);
            if (decimal < 0 || decimal > 9) return false;
        }
        candidate.profile = nominal;
        candidate.flags = get_edit_profile_flags(nominal);
    } else if (raw == lb::zero_width_joiner ||
        (category == gc::format && bidi == bc::boundary_neutral && raw == lb::combining_mark)) {
        candidate.role = edit_item_source_role::joiner;
    } else if (category == gc::control || raw == lb::mandatory ||
        raw == lb::carriage_return || raw == lb::line_feed || raw == lb::next_line) {
        candidate.role = edit_item_source_role::hard_control;
    } else if (category == gc::format) {
        candidate.role = edit_item_source_role::format_control;
    } else if (category == gc::space_separator) {
        candidate.role = edit_item_source_role::white_space;
    } else if (category == gc::decimal_digit_number) {
        // A Script tag alone must not turn numeric source into nominal text.
        // Original Devanagari/Thai/Lao/Khmer Nd/L observations use separate
        // numeric items. Nominal entry policy belongs to the actual next item,
        // not to a digit's Script tag or to a guessed Windows engine identity.
        const auto decimal = get_unicode_decimal_digit_value(scalar.code_point);
        const bool numeric_policy = bidi == bc::european_number || bidi == bc::arabic_number ||
            (bidi == bc::left_to_right && (nominal == edit_item_profile::devanagari_nominal ||
                nominal == edit_item_profile::thai_nominal || nominal == edit_item_profile::lao_nominal ||
                nominal == edit_item_profile::khmer_nominal));
        if (decimal < 0 || decimal > 9 ||
            !numeric_policy) {
            if (nominal != edit_item_profile::paragraph_bridge) return false;
        } else candidate.profile = edit_item_profile::numeric_bridge;
    } else if (is_edit_mark(category)) {
        if (nominal != edit_item_profile::paragraph_bridge) {
            if (scalar.code_point > 0xFFFFU ||
                (bidi != bc::nonspacing_mark && !has_edit_nominal_bidi(nominal, bidi))) return false;
            candidate.profile = nominal;
            candidate.flags = get_edit_profile_flags(nominal);
            candidate.role = edit_item_source_role::context_mark;
        } else candidate.role = edit_item_source_role::inherited_mark;
    } else if (is_edit_letter(category) && nominal != edit_item_profile::paragraph_bridge) {
        // The original Arabic engine also owns supplementary AL letters.
        // Preserve each scalar's original two-unit source range, not a BMP
        // replacement or compatibility decomposition of mathematical letters.
        if ((scalar.code_point > 0xFFFFU && nominal != edit_item_profile::arabic_nominal) ||
            !has_edit_nominal_bidi(nominal, bidi)) return false;
        if (nominal == edit_item_profile::arabic_nominal && is_arabic_presentation_block(scalar.code_point)) {
            // ScriptItemize's original default fCharShape=false keeps Arabic
            // presentation forms distinct from nominal Arabic shaping items.
            // The observed U+FE8F bridge trains this property-based inference.
        } else {
            // Hebrew presentation letters remain in the Hebrew item in the
            // original source; Arabic's fCharShape bridge is not transferable.
            candidate.profile = nominal;
            candidate.flags = get_edit_profile_flags(nominal);
        }
    } else if (category == gc::modifier_letter && bidi == bc::arabic_letter &&
        scalar.script == open_type_tag::from_chars('D', 'F', 'L', 'T')) {
        // Common shared-cursive source cannot select an Arabic/Syriac family
        // by itself. UAX24 context attachment is admitted only below, after an
        // already classified nominal cursive item (never at a standalone edge).
        candidate.role = edit_item_source_role::shared_cursive_modifier;
    } else if (nominal != edit_item_profile::paragraph_bridge) return false;
    candidate.has_attachment_owner = candidate.role == edit_item_source_role::ordinary;
    result = candidate;
    return true;
}

inline bool try_attach_edit_item_properties(
    const unicode_scalar& scalar,
    const unicode_scalar* previous_scalar,
    const edit_item_properties* previous,
    edit_item_properties& properties) noexcept
{
    using role = edit_item_source_role;
    if (properties.role != role::context_mark && properties.role != role::inherited_mark &&
        properties.role != role::joiner && properties.role != role::shared_cursive_modifier) return true;
    using lb = unicode_line_break_class;
    const auto previous_raw = previous_scalar == nullptr ? lb::unknown :
        get_unicode_line_break_class(previous_scalar->code_point);
    const bool has_base = previous != nullptr && previous_scalar != nullptr &&
        previous->has_attachment_owner &&
        previous->role != role::white_space && previous->role != role::hard_control &&
        previous->role != role::format_control && previous_raw != lb::zero_width_space;
    // Standalone marks and marks following a barrier have no qualified item
    // owner. Leading/space-adjacent JOINERS are separately observed and keep
    // the paragraph bridge, but must not admit a later mark through that edge.
    if (!has_base) return properties.role == role::joiner;
    // Only the independently observed variation-selector role is established
    // for these symbol items. Extend through the exact named BMP Variation
    // Selectors block; do not turn arbitrary inherited marks or joiners into
    // newly qualified Balinese/Hangul source roles.
    if (is_edit_symbol_item(previous->profile) &&
        (properties.role != role::inherited_mark ||
            scalar.code_point < 0xFE00U || scalar.code_point > 0xFE0FU)) return false;
    // Only original Myanmar source and the independently observed joiners are
    // admitted to its syllable machine. Generic inherited/foreign marks do not
    // acquire a newly qualified script policy merely by following Myanmar.
    if (previous->profile == edit_item_profile::myanmar_syllabic &&
        properties.role != role::joiner) return false;
    if (properties.role == role::context_mark && properties.profile != previous->profile) return false;
    if (properties.role == role::shared_cursive_modifier &&
        previous->profile != edit_item_profile::arabic_nominal &&
        previous->profile != edit_item_profile::syriac_nominal) return false;
    if (properties.role == role::inherited_mark &&
        scalar.script != open_type_tag::from_chars('D', 'F', 'L', 'T') &&
        get_edit_profile_flags(previous->profile) != 0U) return false;
    // Retain the CURRENT original source role and actual bidi level. Only its
    // policy family is attached; no code point, index or Unicode property changes.
    properties.profile = previous->profile;
    properties.flags = previous->flags;
    properties.has_attachment_owner = true;
    return true;
}

inline std::size_t get_edit_initial_mark_count(
    std::span<const unicode_scalar> scalars,
    std::span<const edit_item_properties> properties,
    std::span<const unicode_bidi_level> levels,
    std::int8_t paragraph_level) noexcept
{
    if (properties.size() != scalars.size() || levels.size() != scalars.size()) return 0U;
    // Inspect original roles and complete-source bidi, never a rewritten
    // scalar, fabricated base or independently resolved prefix/suffix.
    std::size_t count = 0U;
    while (count < scalars.size()) {
        const auto& scalar = scalars[count];
        if (properties[count].role != edit_item_source_role::inherited_mark ||
            properties[count].profile != edit_item_profile::paragraph_bridge ||
            properties[count].flags != 0U || properties[count].has_attachment_owner ||
            scalar.code_point > 0xFFFFU ||
            scalar.script != open_type_tag::from_chars('D', 'F', 'L', 'T') ||
            get_unicode_general_category(scalar.code_point) != unicode_general_category::nonspacing_mark ||
            get_unicode_bidi_class(scalar.code_point) != unicode_bidi_class::nonspacing_mark ||
            get_unicode_line_break_class(scalar.code_point) != unicode_line_break_class::combining_mark ||
            levels[count].level != paragraph_level) break;
        ++count;
    }
    return count;
}

struct edit_leading_item_context final {
    std::size_t mark_count = 0U;
    std::size_t owner_scalar = 0U;
    edit_item_profile profile = edit_item_profile::paragraph_bridge;
    open_type_tag script{};
    std::int8_t owner_level = 0;
    bool requires_script_extensions = false;
};

inline bool try_get_edit_leading_item_context(
    std::span<const unicode_scalar> scalars,
    std::span<const edit_item_properties> properties,
    std::span<const unicode_bidi_level> levels,
    std::span<const unicode_script_run> source_runs,
    std::size_t count,
    std::int8_t paragraph_level,
    edit_leading_item_context& result) noexcept
{
    if (properties.size() != scalars.size() || levels.size() != scalars.size() ||
        count == 0U || count >= scalars.size() || source_runs.empty()) return false;
    const auto& following = scalars[count];
    const auto& run = source_runs.front();
    // The original whole-source itemizer supplies a candidate, not permission
    // to promote DFLT to any next family. Require its actual unchanged source
    // range and owner; an intervening control/space/foreign mark cannot own it.
    if (run.scalar_start != 0U || run.scalar_count <= count ||
        run.input_start != scalars.front().input_index || run.script != following.script ||
        static_cast<std::uint64_t>(run.input_start) + run.input_length <
            static_cast<std::uint64_t>(following.input_index) + following.input_length ||
        !is_edit_letter(get_unicode_general_category(following.code_point)) ||
        properties[count].role != edit_item_source_role::ordinary || !properties[count].has_attachment_owner)
        return false;
    edit_leading_item_context candidate{count, count, properties[count].profile,
        run.script, levels[count].level, false};
    if (following.script == open_type_tag::from_chars('l', 'a', 't', 'n') &&
        candidate.profile == edit_item_profile::paragraph_bridge &&
        get_unicode_bidi_class(following.code_point) == unicode_bidi_class::left_to_right &&
        candidate.owner_level == (paragraph_level == 0 ? 0 : 2)) {
        // Original generic Latin mark bridge needs no nominal owner override.
    } else if (candidate.profile == edit_item_profile::arabic_nominal &&
        following.script == open_type_tag::from_chars('a', 'r', 'a', 'b') &&
        get_unicode_bidi_class(following.code_point) == unicode_bidi_class::arabic_letter &&
        candidate.owner_level == 1) {
        // Pending proof only: the caller MUST validate every original leading
        // mark's true Inherited/Script_Extensions properties before assigning
        // this item context. Raw script and resolved bidi remain untouched.
        candidate.requires_script_extensions = true;
    } else return false;
    result = candidate;
    return true;
}

} // namespace progpu::native::text::detail
