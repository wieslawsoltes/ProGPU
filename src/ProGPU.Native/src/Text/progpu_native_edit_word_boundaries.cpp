#include "progpu_native_edit_word_boundaries.hpp"
#include "progpu_native_text.hpp"
#include "progpu_native_unicode_line_break_internal.hpp"
#include "progpu_native_edit_item_policy.hpp"

#include <algorithm>
#include <limits>
#include <new>
#include <utility>

#if defined(PROGPU_NATIVE_EDIT_WORD_ICU)
#include <unicode/ubrk.h>
#include <unicode/udata.h>
#include <unicode/uscript.h>
#include <unicode/uversion.h>
#include <array>
#include <memory>
#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#else
#include <dlfcn.h>
#endif
extern "C" const std::uint32_t progpu_edit_icu_data[];
#endif

namespace progpu::native::text {
namespace {
constexpr auto thai = open_type_tag::from_chars('t', 'h', 'a', 'i');
constexpr auto lao = open_type_tag::from_chars('l', 'a', 'o', 'o');
constexpr auto lao_layout = open_type_tag::from_chars('l', 'a', 'o', ' ');
constexpr auto khmer = open_type_tag::from_chars('k', 'h', 'm', 'r');
constexpr auto myanmar = open_type_tag::from_chars('m', 'y', 'm', 'r');
using lb = unicode_line_break_class;

bool fail(edit_word_boundary_error value, edit_word_boundary_error& error) noexcept
{
    error = value;
    return false;
}

bool is_edit_white_space(std::uint32_t code_point) noexcept
{
    // SCRIPT_LOGATTR's whitespace is NOT Unicode White_Space: the independent
    // receipt excludes tab/CR/LF, NBSP and ideographic space, but includes NNBSP.
    return get_unicode_general_category(code_point) ==
        unicode_general_category::space_separator &&
        code_point != 0x00A0U && code_point != 0x3000U;
}

struct edit_item final {
    std::size_t scalar_start = 0U;
    std::size_t scalar_count = 0U;
    detail::edit_item_properties properties{};
    std::int8_t level = 0;
};

bool validate_owned_script_context(
    std::span<const unicode_scalar> marks,
    open_type_tag script,
    edit_word_boundary_error& error) noexcept;

bool assemble_edit_item_breaks(
    std::span<const unicode_scalar> scalars,
    std::span<detail::edit_item_properties> properties,
    std::int8_t requested_level,
    std::span<text_line_break_kind> breaks,
    bool& needs_thai_dictionary,
    edit_word_boundary_error& error)
{
    // Reuse the actual complete-source native bidi worker; no independent
    // prefix resolution, inferred direction from script or rewritten input.
    unicode_bidi_requirements requirements{};
    if (!try_get_unicode_bidi_requirements(scalars, requirements))
        return fail(edit_word_boundary_error::input_too_large, error);
    std::vector<unicode_bidi_unit> units(requirements.unit_count);
    std::vector<std::uint32_t> indices(requirements.index_count);
    std::vector<unicode_bidi_level_run> runs(requirements.run_count);
    std::vector<unicode_bidi_bracket_pair> brackets(requirements.bracket_pair_count);
    std::vector<unicode_bidi_level> levels(scalars.size());
    std::int8_t resolved_level = 0;
    std::uint32_t written = 0U;
    if (!try_resolve_unicode_bidi(scalars, requested_level,
            {units, indices, runs, brackets}, levels, resolved_level, written) ||
        written != scalars.size() || resolved_level != requested_level)
        return fail(edit_word_boundary_error::invalid_encoding, error);
    const auto leading_mark_count = detail::get_edit_initial_mark_count(
        scalars, properties, levels, requested_level);
    detail::edit_leading_item_context leading_context{};
    if (leading_mark_count != 0U) {
        std::uint32_t script_run_count = 0U;
        if (!try_get_unicode_script_run_count(scalars, script_run_count))
            return fail(edit_word_boundary_error::invalid_encoding, error);
        std::vector<unicode_script_run> source_runs(script_run_count);
        if (!try_itemize_unicode_scripts(scalars, source_runs, written) || written != source_runs.size())
            return fail(edit_word_boundary_error::invalid_encoding, error);
        if (!detail::try_get_edit_leading_item_context(scalars, properties, levels, source_runs,
                leading_mark_count, requested_level, leading_context))
            return fail(edit_word_boundary_error::unqualified_script_item_transition_policy, error);
        if (leading_context.requires_script_extensions) {
            if (!validate_owned_script_context(scalars.first(leading_mark_count), leading_context.script, error))
                return false;
            for (std::size_t index = 0U; index < leading_mark_count; ++index) {
                // Only the scratch-owned policy family acquires context. Keep
                // the original inherited role and false attachment-owner bit.
                properties[index].profile = leading_context.profile;
                properties[index].flags = detail::get_edit_profile_flags(leading_context.profile);
            }
        }
    }
    for (std::size_t index = 0U; index < scalars.size(); ++index) {
        const auto* previous_scalar = index == 0U ? nullptr : &scalars[index - 1U];
        const auto* previous_properties = index == 0U ? nullptr : &properties[index - 1U];
        if (index >= leading_mark_count &&
            !detail::try_attach_edit_item_properties(scalars[index], previous_scalar,
                previous_properties, properties[index]))
            return fail(edit_word_boundary_error::unqualified_script_item_transition_policy, error);
        if (index != 0U && is_edit_white_space(scalars[index].code_point)) {
            // Paragraph direction can attach a space to the following RTL
            // item. Match its real resolved level, not array order alone.
            if (levels[index].level == levels[index - 1U].level)
                properties[index].profile = properties[index - 1U].profile;
            else {
                auto following = index + 1U;
                while (following < scalars.size() &&
                    is_edit_white_space(scalars[following].code_point)) ++following;
                if (following < scalars.size() && levels[following].level == levels[index].level)
                    properties[index].profile = properties[following].profile;
            }
            properties[index].flags = detail::get_edit_profile_flags(properties[index].profile);
        }
    }
    std::vector<edit_item> items;
    for (std::size_t index = 0U; index < scalars.size(); ++index) {
        // Item context is not raw scalar bidi. The verified leading marks use
        // their real following owner's item level, while the original complete-
        // source levels remain immutable for all other source policies.
        const auto item_level = index < leading_context.mark_count && leading_context.requires_script_extensions
            ? leading_context.owner_level : levels[index].level;
        if (items.empty() || items.back().properties.profile != properties[index].profile ||
            items.back().level != item_level)
            items.push_back({index, 1U, properties[index], item_level});
        else ++items.back().scalar_count;
    }
    std::vector<lb> item_classes(scalars.size());
    std::vector<text_line_break_kind> item_breaks(scalars.size());
    std::vector<std::uint8_t> syllable_categories;
    std::vector<std::uint8_t> syllables;
    for (const auto& item : items) {
        const auto start = item.scalar_start;
        const auto count = item.scalar_count;
        if (item.properties.flags == 0U) continue; // original paragraph bridge
        // The independent direct ScriptBreak requests distinguish an actual
        // one-UTF16-unit Thai/Lao item (no entry soft break) from longer items.
        // This property generalization never examines an observed scalar/word
        // identity or the preceding item. Retain the full source line context.
        const bool singleton_dictionary_item = count == 1U && scalars[start].input_length == 1U &&
            (item.properties.profile == detail::edit_item_profile::thai_nominal ||
                item.properties.profile == detail::edit_item_profile::lao_nominal);
        if (item.properties.profile == detail::edit_item_profile::thai_nominal && !singleton_dictionary_item)
            needs_thai_dictionary = true;
        // Resolve each whole original typed item. A local terminal sentinel
        // is NOT a boundary at the next item: only its interior is copied.
        if (!detail::try_resolve_edit_selection_line_breaks(scalars.subspan(start, count),
                std::span(item_classes).subspan(start, count),
                std::span(item_breaks).subspan(start, count)))
            return fail(edit_word_boundary_error::invalid_encoding, error);
        for (std::size_t offset = 0U; offset + 1U < count; ++offset)
            breaks[start + offset] = item_breaks[start + offset];
        if (detail::is_edit_symbol_item(item.properties.profile)) {
            // The original direct symbol item has no interior soft breaks,
            // including its attached VS, and no soft exit into Latin or CJK.
            // An AL line-class substitution alone would retain the wrong CJK
            // seams. Keep hard controls and the separate whitespace policy.
            for (std::size_t offset = 1U; offset < count; ++offset)
                if (!is_edit_white_space(scalars[start + offset].code_point) &&
                    breaks[start + offset - 1U] != text_line_break_kind::mandatory)
                    breaks[start + offset - 1U] = text_line_break_kind::prohibited;
            const auto end = start + count;
            if (end < scalars.size() && !is_edit_white_space(scalars[end].code_point) &&
                properties[end].role != detail::edit_item_source_role::hard_control &&
                breaks[end - 1U] != text_line_break_kind::mandatory)
                breaks[end - 1U] = text_line_break_kind::prohibited;
        } else if (item.properties.profile == detail::edit_item_profile::myanmar_syllabic) {
            // Reuse the original property lookup and machine over the WHOLE
            // unchanged item, including broken syllables and joiners. This is
            // source metadata only: no glyph shaping/reordering or clustering.
            syllable_categories.resize(count);
            syllables.resize(count);
            for (std::size_t offset = 0U; offset < count; ++offset)
                syllable_categories[offset] = get_unicode_indic_shaping_properties(
                    scalars[start + offset].code_point).category;
            if (!try_assign_unicode_syllables(unicode_syllable_machine::myanmar,
                    syllable_categories, {}, syllables))
                return fail(edit_word_boundary_error::unqualified_complex_script_policy, error);
            for (std::size_t offset = 1U; offset < count; ++offset) {
                // Whitespace keeps the existing original line/whitespace
                // policy; a non-Myanmar space token is not a syllable seam.
                if (is_edit_white_space(scalars[start + offset].code_point)) continue;
                breaks[start + offset - 1U] = syllables[offset] != syllables[offset - 1U]
                    ? text_line_break_kind::opportunity : text_line_break_kind::prohibited;
            }
        }
        if (start != 0U && !is_edit_white_space(scalars[start].code_point) &&
            breaks[start - 1U] != text_line_break_kind::mandatory) {
            if ((item.properties.flags & detail::edit_item_soft_entry) != 0U)
                breaks[start - 1U] = singleton_dictionary_item
                    ? text_line_break_kind::prohibited : text_line_break_kind::opportunity;
            else if ((item.properties.flags & detail::edit_item_suppressed_entry) != 0U)
                breaks[start - 1U] = text_line_break_kind::prohibited;
        }
    }
    return true;
}

#if defined(PROGPU_NATIVE_EDIT_WORD_ICU)
const void* module_of(const void* address) noexcept
{
#if defined(_WIN32)
    HMODULE module = nullptr;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
        GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(address), &module) != 0 ? module : nullptr;
#else
    Dl_info information{};
    return dladdr(address, &information) != 0 ? information.dli_fbase : nullptr;
#endif
}

edit_word_boundary_error initialize_owned_icu_once() noexcept
{
    const auto owner = module_of(reinterpret_cast<const void*>(&initialize_owned_icu_once));
    const std::array<const void*, 11> functions{
        reinterpret_cast<const void*>(&u_getVersion),
        reinterpret_cast<const void*>(&udata_setCommonData),
        reinterpret_cast<const void*>(&udata_setFileAccess),
        reinterpret_cast<const void*>(&ubrk_open),
        reinterpret_cast<const void*>(&ubrk_first),
        reinterpret_cast<const void*>(&ubrk_next),
        reinterpret_cast<const void*>(&ubrk_close),
        reinterpret_cast<const void*>(&ubrk_getRuleStatus),
        reinterpret_cast<const void*>(&uscript_getCode),
        reinterpret_cast<const void*>(&uscript_getScript),
        reinterpret_cast<const void*>(&uscript_hasScript)};
    if (owner == nullptr) return edit_word_boundary_error::dependency_unavailable;
    for (const auto address : functions)
        if (module_of(address) != owner)
            return edit_word_boundary_error::dependency_unavailable;
    UVersionInfo version{};
    u_getVersion(version);
    if (version[0] != 78U || version[1] != 3U || version[2] != 0U || version[3] != 0U)
        return edit_word_boundary_error::dependency_unavailable;
    UErrorCode status = U_ZERO_ERROR;
    // Function-local initialization serializes the only private ICU data setup.
    // Versioned AND purpose-suffixed static symbols never share another ICU's
    // globals; no environment path or system data can replace the original data.
    udata_setFileAccess(UDATA_NO_FILES, &status);
    udata_setCommonData(progpu_edit_icu_data, &status);
    return U_SUCCESS(status) ? edit_word_boundary_error::none :
        edit_word_boundary_error::dependency_failure;
}

edit_word_boundary_error initialize_owned_icu() noexcept
{
    // One initialization shared by dictionary AND script-context clients. Never
    // install data again after either client has started using the private ICU.
    static const auto initialized = initialize_owned_icu_once();
    return initialized;
}

bool add_thai_dictionary_boundaries(
    std::span<const std::uint16_t> source,
    std::span<const unicode_scalar> scalars,
    std::span<const detail::edit_item_properties> properties,
    std::vector<std::uint8_t>& boundaries,
    edit_word_boundary_error& error)
{
    const auto initialized = initialize_owned_icu();
    if (initialized != edit_word_boundary_error::none) return fail(initialized, error);
    static_assert(sizeof(UChar) == sizeof(std::uint16_t));
    // Own the API's exact character type: no aliasing cast and no normalized,
    // isolated word, substring, grapheme or reshaped replacement input.
    const std::vector<UChar> characters(source.begin(), source.end());
    UErrorCode status = U_ZERO_ERROR;
    std::unique_ptr<UBreakIterator, decltype(&ubrk_close)> iterator(
        ubrk_open(UBRK_LINE, "en_US", characters.data(),
            static_cast<std::int32_t>(characters.size()), &status), &ubrk_close);
    if (U_FAILURE(status) || iterator == nullptr)
        return fail(edit_word_boundary_error::dependency_failure, error);
    std::size_t scalar = 0U;
    for (std::int32_t index = ubrk_first(iterator.get()); index != UBRK_DONE;
         index = ubrk_next(iterator.get())) {
        if (index < 0 || static_cast<std::size_t>(index) > source.size())
            return fail(edit_word_boundary_error::dependency_failure, error);
        while (scalar < scalars.size() && scalars[scalar].input_index <
            static_cast<std::uint32_t>(index)) ++scalar;
        // Admit ONLY an interior nominal Thai-to-Thai original scalar seam.
        // Newly admitted numeric items are not dictionary text. Lao and
        // Khmer dictionary results (known to contradict EDIT) are not used;
        // whitespace/script transitions come solely from the explicit profile.
        if (scalar != 0U && scalar < scalars.size() &&
            scalars[scalar].input_index == static_cast<std::uint32_t>(index) &&
            scalars[scalar - 1U].script == thai && scalars[scalar].script == thai &&
            properties[scalar - 1U].profile == detail::edit_item_profile::thai_nominal &&
            properties[scalar].profile == detail::edit_item_profile::thai_nominal)
            boundaries[static_cast<std::size_t>(index)] = 1U;
    }
    return true;
}
#endif

bool validate_owned_script_context(
    std::span<const unicode_scalar> marks,
    open_type_tag script,
    edit_word_boundary_error& error) noexcept
{
#if defined(PROGPU_NATIVE_EDIT_WORD_ICU)
    const auto initialized = initialize_owned_icu();
    if (initialized != edit_word_boundary_error::none) return fail(initialized, error);
    const std::array<char, 5> name{
        static_cast<char>((script.value >> 24U) & 0xFFU), static_cast<char>((script.value >> 16U) & 0xFFU),
        static_cast<char>((script.value >> 8U) & 0xFFU), static_cast<char>(script.value & 0xFFU), '\0'};
    UScriptCode code = USCRIPT_INVALID_CODE;
    UErrorCode status = U_ZERO_ERROR;
    if (uscript_getCode(name.data(), &code, 1, &status) != 1 || U_FAILURE(status) ||
        code == USCRIPT_INVALID_CODE || code == USCRIPT_COMMON || code == USCRIPT_INHERITED || code == USCRIPT_UNKNOWN)
        return fail(edit_word_boundary_error::dependency_failure, error);
    for (const auto& scalar : marks) {
        const auto cp = static_cast<UChar32>(scalar.code_point);
        const auto source_script = uscript_getScript(cp, &status);
        if (U_FAILURE(status)) return fail(edit_word_boundary_error::dependency_failure, error);
        // Real Unicode metadata only, not an observed mark/word/CCC allowlist.
        if (source_script != USCRIPT_INHERITED || !uscript_hasScript(cp, code))
            return fail(edit_word_boundary_error::unqualified_script_item_transition_policy, error);
    }
    return true;
#else
    static_cast<void>(marks);
    static_cast<void>(script);
    return fail(edit_word_boundary_error::dependency_unavailable, error);
#endif
}
} // namespace

bool try_create_edit_word_boundary_snapshot(
    std::span<const std::uint16_t> source,
    edit_word_boundary_snapshot& result,
    edit_word_boundary_error& error,
    std::int8_t paragraph_level) noexcept
{
    if (paragraph_level != 0 && paragraph_level != 1)
        return fail(edit_word_boundary_error::invalid_paragraph_level, error);
    if (source.size() > static_cast<std::size_t>(std::numeric_limits<std::int32_t>::max()))
        return fail(edit_word_boundary_error::input_too_large, error);
    unicode_decode_requirements requirements{};
    if (!try_get_utf16_decode_requirements(source, requirements))
        return fail(edit_word_boundary_error::invalid_encoding, error);
    try {
        std::vector<unicode_scalar> scalars(requirements.scalar_count);
        std::uint32_t written = 0U;
        if (!try_decode_utf16(source, scalars, written) || written != scalars.size())
            return fail(edit_word_boundary_error::invalid_encoding, error);
        bool needs_thai_dictionary = false;
        bool has_hangul_symbols = false;
        bool has_other_hangul_source = false;
        std::vector<detail::edit_item_properties> item_properties(scalars.size());
        for (std::size_t index = 0U; index < scalars.size(); ++index) {
            const auto& scalar = scalars[index];
            const auto raw = get_unicode_line_break_class(scalar.code_point);
            if (scalar.code_point <= 0xFFFFU && raw == lb::ideographic &&
                get_unicode_general_category(scalar.code_point) ==
                    unicode_general_category::other_symbol &&
                detail::get_edit_symbol_line_break_class(scalar.code_point) == lb::unknown &&
                !detail::is_edit_symbol_item(detail::get_edit_symbol_item_profile(scalar)))
                return fail(edit_word_boundary_error::unqualified_bmp_symbol_policy, error);
            if (raw == lb::complex_context && scalar.script != thai &&
                scalar.script != lao && scalar.script != lao_layout && scalar.script != khmer && scalar.script != myanmar &&
                scalar.script != open_type_tag::from_chars('D', 'F', 'L', 'T'))
                return fail(edit_word_boundary_error::unqualified_complex_script_policy, error);
            // Typed original Unicode properties select a portable policy
            // family, NEVER an inferred Windows eScript identity. The observed
            // scalar table is regression evidence only, not a runtime allowlist.
            if (!detail::try_classify_edit_item_properties(scalar, item_properties[index]))
                return fail(edit_word_boundary_error::unqualified_script_item_transition_policy, error);
            if (scalar.script == open_type_tag::from_chars('h', 'a', 'n', 'g')) {
                if (item_properties[index].profile == detail::edit_item_profile::hangul_symbols)
                    has_hangul_symbols = true;
                else has_other_hangul_source = true;
            }
        }
        // The independent contexts do not establish co-itemization with
        // ordinary Hangul source. Keep the entire original request atomic and
        // unsupported instead of inventing a split inside native engine 19.
        if (has_hangul_symbols && has_other_hangul_source)
            return fail(edit_word_boundary_error::unqualified_script_item_transition_policy, error);
        std::vector<lb> classes(scalars.size());
        std::vector<text_line_break_kind> breaks(scalars.size());
        if (!detail::try_resolve_edit_selection_line_breaks(scalars, classes, breaks))
            return fail(edit_word_boundary_error::invalid_encoding, error);
        if (!assemble_edit_item_breaks(scalars, item_properties, paragraph_level, breaks,
                needs_thai_dictionary, error)) return false;
        std::vector<std::uint8_t> boundaries(source.size() + 1U, 0U);
        boundaries.front() = boundaries.back() = 1U;
        for (std::size_t index = 1U; index < scalars.size(); ++index) {
            const auto& previous = scalars[index - 1U];
            const auto& current = scalars[index];
            const bool previous_white = is_edit_white_space(previous.code_point);
            const bool current_white = is_edit_white_space(current.code_point);
            if (breaks[index - 1U] != text_line_break_kind::prohibited ||
                (previous_white && !current_white) ||
                ((item_properties[index - 1U].flags & detail::edit_item_white_exit) != 0U && current_white))
                boundaries[current.input_index] = 1U;
        }
        // Keep original CRLF / CRCRLF units intact, but retain the EDIT word
        // boundary before the original hard-break group. LF alone stays left.
        for (std::size_t index = 0U; index < scalars.size(); ++index) {
            if (scalars[index].code_point != 0x0DU) continue;
            std::size_t group_count = 1U;
            if (index + 1U < scalars.size() && scalars[index + 1U].code_point == 0x0AU)
                group_count = 2U;
            else if (index + 2U < scalars.size() && scalars[index + 1U].code_point == 0x0DU &&
                scalars[index + 2U].code_point == 0x0AU) group_count = 3U;
            boundaries[scalars[index].input_index] = 1U;
            for (std::size_t inside = 1U; inside < group_count; ++inside)
                boundaries[scalars[index + inside].input_index] = 0U;
            index += group_count - 1U;
        }
        if (needs_thai_dictionary) {
#if defined(PROGPU_NATIVE_EDIT_WORD_ICU)
            if (!add_thai_dictionary_boundaries(source, scalars, item_properties, boundaries, error)) return false;
#else
            return fail(edit_word_boundary_error::dependency_unavailable, error);
#endif
        }
        edit_word_boundary_snapshot candidate;
        for (std::size_t index = 0U; index < boundaries.size(); ++index)
            if (boundaries[index] != 0U) candidate.positions.push_back(static_cast<std::uint32_t>(index));
        for (const auto& scalar : scalars) {
            if (!is_edit_white_space(scalar.code_point)) break;
            candidate.leading_content_start = scalar.input_index + scalar.input_length;
        }
        result.positions.swap(candidate.positions);
        result.leading_content_start = candidate.leading_content_start;
        error = edit_word_boundary_error::none;
        return true;
    } catch (const std::bad_alloc&) {
        return fail(edit_word_boundary_error::allocation_failure, error);
    } catch (...) {
        return fail(edit_word_boundary_error::dependency_failure, error);
    }
}
} // namespace progpu::native::text
