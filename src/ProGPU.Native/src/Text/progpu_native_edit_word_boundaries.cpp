#include "progpu_native_edit_word_boundaries.hpp"
#include "progpu_native_text.hpp"
#include "progpu_native_unicode_line_break_internal.hpp"

#include <algorithm>
#include <limits>
#include <new>
#include <utility>

#if defined(PROGPU_NATIVE_EDIT_WORD_ICU)
#include <unicode/ubrk.h>
#include <unicode/udata.h>
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
constexpr auto arabic = open_type_tag::from_chars('a', 'r', 'a', 'b');
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

edit_word_boundary_error initialize_owned_icu() noexcept
{
    const auto owner = module_of(reinterpret_cast<const void*>(&initialize_owned_icu));
    const std::array<const void*, 8> functions{
        reinterpret_cast<const void*>(&u_getVersion),
        reinterpret_cast<const void*>(&udata_setCommonData),
        reinterpret_cast<const void*>(&udata_setFileAccess),
        reinterpret_cast<const void*>(&ubrk_open),
        reinterpret_cast<const void*>(&ubrk_first),
        reinterpret_cast<const void*>(&ubrk_next),
        reinterpret_cast<const void*>(&ubrk_close),
        reinterpret_cast<const void*>(&ubrk_getRuleStatus)};
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

bool add_thai_dictionary_boundaries(
    std::span<const std::uint16_t> source,
    std::span<const unicode_scalar> scalars,
    std::vector<std::uint8_t>& boundaries,
    edit_word_boundary_error& error)
{
    static const auto initialized = initialize_owned_icu();
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
        // Admit ONLY an interior Thai-to-Thai original scalar seam. Lao and
        // Khmer dictionary results (known to contradict EDIT) are not used;
        // whitespace/script transitions come solely from the explicit profile.
        if (scalar != 0U && scalar < scalars.size() &&
            scalars[scalar].input_index == static_cast<std::uint32_t>(index) &&
            scalars[scalar - 1U].script == thai && scalars[scalar].script == thai)
            boundaries[static_cast<std::size_t>(index)] = 1U;
    }
    return true;
}
#endif
} // namespace

bool try_create_edit_word_boundary_snapshot(
    std::span<const std::uint16_t> source,
    edit_word_boundary_snapshot& result,
    edit_word_boundary_error& error) noexcept
{
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
        for (const auto& scalar : scalars) {
            const auto raw = get_unicode_line_break_class(scalar.code_point);
            if (scalar.code_point <= 0xFFFFU && raw == lb::ideographic &&
                get_unicode_general_category(scalar.code_point) ==
                    unicode_general_category::other_symbol)
                return fail(edit_word_boundary_error::unqualified_bmp_symbol_policy, error);
            if (raw == lb::complex_context && scalar.script != thai &&
                scalar.script != lao && scalar.script != lao_layout && scalar.script != khmer &&
                scalar.script != open_type_tag::from_chars('D', 'F', 'L', 'T'))
                return fail(edit_word_boundary_error::unqualified_complex_script_policy, error);
            needs_thai_dictionary |= scalar.script == thai;
        }
        std::vector<lb> classes(scalars.size());
        std::vector<text_line_break_kind> breaks(scalars.size());
        if (!detail::try_resolve_edit_joiner_line_breaks(scalars, classes, breaks))
            return fail(edit_word_boundary_error::invalid_encoding, error);
        std::vector<std::uint8_t> boundaries(source.size() + 1U, 0U);
        boundaries.front() = boundaries.back() = 1U;
        for (std::size_t index = 1U; index < scalars.size(); ++index) {
            const auto& previous = scalars[index - 1U];
            const auto& current = scalars[index];
            const bool previous_white = is_edit_white_space(previous.code_point);
            const bool current_white = is_edit_white_space(current.code_point);
            if (breaks[index - 1U] != text_line_break_kind::prohibited ||
                (previous_white && !current_white) ||
                (previous.script == khmer && current_white))
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
        // The independent direct ScriptBreak Arabic item starts with a soft
        // opportunity even when ordinary UAX14 has none. Our canonical script
        // itemizer owns Common/Inherited attachment, including interior ZWJ;
        // do not guess a Windows item-boundary rule or publish a known-wrong
        // snapshot for that precise missing transition domain.
        std::uint32_t run_count = 0U;
        if (!try_get_unicode_script_run_count(scalars, run_count))
            return fail(edit_word_boundary_error::invalid_encoding, error);
        std::vector<unicode_script_run> runs(run_count);
        std::uint32_t runs_written = 0U;
        if (!try_itemize_unicode_scripts(scalars, runs, runs_written) ||
            runs_written != run_count)
            return fail(edit_word_boundary_error::invalid_encoding, error);
        for (const auto& run : runs)
            if (run.script == arabic && run.scalar_start != 0U &&
                boundaries[run.input_start] == 0U)
                return fail(edit_word_boundary_error::unqualified_script_item_transition_policy, error);
        if (needs_thai_dictionary) {
#if defined(PROGPU_NATIVE_EDIT_WORD_ICU)
            if (!add_thai_dictionary_boundaries(source, scalars, boundaries, error)) return false;
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
