#ifndef PROGPU_NATIVE_TEXT_HPP
#define PROGPU_NATIVE_TEXT_HPP

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>
#include <span>
#include <string_view>

namespace progpu::native::text {

class sfnt_font_view;

enum class font_error : std::uint32_t {
    none = 0U,
    invalid_argument,
    unsupported_container,
    invalid_collection,
    invalid_face,
    truncated_directory,
    invalid_glyph,
    insufficient_buffer,
    invalid_container,
    invalid_compressed_data,
    verification_failed
};

struct sfnt_container_requirements final {
    std::size_t normalized_bytes = 0U;
    std::size_t table_scratch_bytes = 0U;
    std::uint16_t table_count = 0U;
    bool requires_normalization = false;
};

struct sfnt_subset_requirements final {
    std::size_t font_bytes = 0U;
    std::size_t glyph_map_count = 0U;
};

struct sfnt_glyph_remap final {
    std::uint16_t source_glyph_id = 0U;
    std::uint16_t subset_glyph_id = 0U;

    friend constexpr bool operator==(
        sfnt_glyph_remap,
        sfnt_glyph_remap) noexcept = default;
};

/*
 * Direct native port of ProGPU.Text.SfntFontSubsetter's glyph-ID-preserving
 * TrueType subset. Glyph zero and transitive composite dependencies are
 * retained, omitted glyph IDs remain empty, DSIG is removed, loca is emitted
 * in long form, and the SFNT checksum adjustment is rebuilt. This is a cold
 * font-preparation API: work is O(T + G + B) with bounded traversal for T
 * tables, G glyphs, and B copied bytes. Output ownership is caller supplied.
 */
bool try_get_glyph_id_preserving_sfnt_subset_requirements(
    std::span<const std::byte> font_data,
    std::size_t directory_offset,
    std::span<const std::uint16_t> glyphs,
    sfnt_subset_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_create_glyph_id_preserving_sfnt_subset(
    std::span<const std::byte> font_data,
    std::size_t directory_offset,
    std::span<const std::uint16_t> glyphs,
    std::span<std::byte> output,
    sfnt_subset_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_get_compact_sfnt_subset_requirements(
    std::span<const std::byte> font_data,
    std::size_t directory_offset,
    std::span<const std::uint16_t> glyphs,
    sfnt_subset_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_create_compact_sfnt_subset(
    std::span<const std::byte> font_data,
    std::size_t directory_offset,
    std::span<const std::uint16_t> glyphs,
    std::span<std::byte> output,
    std::span<sfnt_glyph_remap> glyph_map,
    sfnt_subset_requirements& result,
    font_error* error = nullptr) noexcept;

/*
 * Dependency-free WOFF1-to-SFNT normalization. The requirements pass is O(T)
 * for T tables and reports exact caller-owned output plus maximum-table scratch
 * storage. Normalization preflights every compressed table before touching the
 * destination, then performs O(I + O) bounded decode/copy work. WOFF2 remains
 * an explicit unsupported container until its Brotli transform slice lands.
 */
bool try_get_sfnt_container_requirements(
    std::span<const std::byte> input,
    sfnt_container_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_normalize_sfnt_container(
    std::span<const std::byte> input,
    std::span<std::byte> table_scratch,
    std::span<std::byte> output,
    sfnt_container_requirements& result,
    font_error* error = nullptr) noexcept;

struct open_type_tag final {
    std::uint32_t value = 0U;

    static constexpr open_type_tag from_chars(
        char a,
        char b,
        char c,
        char d) noexcept {
        return open_type_tag{
            (static_cast<std::uint32_t>(
                static_cast<unsigned char>(a)) << 24U) |
            (static_cast<std::uint32_t>(
                static_cast<unsigned char>(b)) << 16U) |
            (static_cast<std::uint32_t>(
                static_cast<unsigned char>(c)) << 8U) |
            static_cast<std::uint32_t>(
                static_cast<unsigned char>(d))};
    }

    friend constexpr bool operator==(
        open_type_tag,
        open_type_tag) noexcept = default;
};

enum class unicode_error : std::uint32_t {
    none = 0U,
    invalid_argument,
    invalid_encoding,
    insufficient_buffer
};

enum class unicode_bidi_class : std::uint8_t {
    left_to_right = 0U,
    right_to_left,
    arabic_letter,
    european_number,
    european_separator,
    european_terminator,
    arabic_number,
    common_separator,
    nonspacing_mark,
    boundary_neutral,
    paragraph_separator,
    segment_separator,
    whitespace,
    other_neutral,
    left_to_right_embedding,
    left_to_right_override,
    right_to_left_embedding,
    right_to_left_override,
    pop_directional_format,
    left_to_right_isolate,
    right_to_left_isolate,
    first_strong_isolate,
    pop_directional_isolate
};

enum class unicode_bidi_bracket_kind : std::uint8_t {
    none = 0U,
    open = 1U,
    close = 2U
};

enum class shaping_direction : std::uint8_t {
    unspecified = 0U,
    left_to_right = 1U,
    right_to_left = 2U,
    top_to_bottom = 3U,
    bottom_to_top = 4U
};

enum class shaping_cluster_level : std::uint8_t {
    monotone_graphemes = 0U,
    monotone_characters = 1U,
    characters = 2U,
    graphemes = 3U
};

enum class shaping_buffer_flags : std::uint8_t {
    none = 0U,
    beginning_of_text = 0x01U,
    end_of_text = 0x02U,
    preserve_default_ignorables = 0x04U,
    remove_default_ignorables = 0x08U,
    do_not_insert_dotted_circle = 0x10U,
    verify = 0x20U,
    produce_unsafe_to_concat = 0x40U,
    produce_safe_to_insert_tatweel = 0x80U
};

enum class shaping_glyph_flags : std::uint32_t {
    none = 0U,
    unsafe_to_break = 0x01U,
    unsafe_to_concat = 0x02U,
    safe_to_insert_tatweel = 0x04U
};

struct shaping_feature final {
    open_type_tag tag{};
    std::uint32_t value = 1U;
    std::uint32_t start = 0U;
    std::uint32_t end = 0xFFFFFFFFU;

    bool applies_to(std::uint32_t input_index) const noexcept {
        return input_index >= start && input_index < end;
    }

    friend constexpr bool operator==(
        shaping_feature,
        shaping_feature) noexcept = default;
};

struct open_type_feature_setting final {
    open_type_tag tag{};
    std::uint32_t value = 1U;

    friend constexpr bool operator==(
        open_type_feature_setting,
        open_type_feature_setting) noexcept = default;
};

struct open_type_feature_tag_requirements final {
    std::uint32_t tag_capacity = 0U;
};

bool try_get_open_type_feature_tag_requirements(
    const sfnt_font_view& font,
    open_type_feature_tag_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_decode_open_type_feature_tags(
    const sfnt_font_view& font,
    std::span<open_type_tag> output,
    std::uint32_t& written,
    font_error* error = nullptr) noexcept;

/*
 * The native equivalent of ProGPU.Text.Shaping.ShapingGlyph. Its fixed
 * value-only layout is suitable for bulk managed/native transfer and direct
 * GPU-plan upload; no per-glyph interop call is required.
 */
struct shaping_glyph final {
    std::uint32_t glyph_id = 0U;
    std::uint32_t code_point = 0U;
    std::int32_t cluster = 0;
    shaping_glyph_flags flags = shaping_glyph_flags::none;
    std::int32_t advance_x = 0;
    std::int32_t advance_y = 0;
    std::int32_t offset_x = 0;
    std::int32_t offset_y = 0;
};

struct unicode_decode_requirements final {
    std::uint32_t scalar_count = 0U;
};

/* Numeric values intentionally match System.Globalization.UnicodeCategory,
 * the authoritative category contract used by the managed ProGPU shaper. */
enum class unicode_general_category : std::uint8_t {
    uppercase_letter = 0U,
    lowercase_letter = 1U,
    titlecase_letter = 2U,
    modifier_letter = 3U,
    other_letter = 4U,
    nonspacing_mark = 5U,
    spacing_combining_mark = 6U,
    enclosing_mark = 7U,
    decimal_digit_number = 8U,
    letter_number = 9U,
    other_number = 10U,
    space_separator = 11U,
    line_separator = 12U,
    paragraph_separator = 13U,
    control = 14U,
    format = 15U,
    surrogate = 16U,
    private_use = 17U,
    connector_punctuation = 18U,
    dash_punctuation = 19U,
    open_punctuation = 20U,
    close_punctuation = 21U,
    initial_quote_punctuation = 22U,
    final_quote_punctuation = 23U,
    other_punctuation = 24U,
    math_symbol = 25U,
    currency_symbol = 26U,
    modifier_symbol = 27U,
    other_symbol = 28U,
    other_not_assigned = 29U
};

unicode_general_category get_unicode_general_category(
    std::uint32_t code_point) noexcept;

/*
 * One decoded scalar retains the original input-unit range. Script is the
 * Unicode 17 Script property translated to ProGPU's OpenType tag convention;
 * Common, Inherited, and Unknown use DFLT. The canonical combining class is
 * shared with the managed shaper's generated table.
 */
struct unicode_scalar final {
    std::uint32_t code_point = 0U;
    std::uint32_t input_index = 0U;
    std::uint16_t input_length = 0U;
    std::uint8_t canonical_combining_class = 0U;
    std::uint8_t reserved = 0U;
    open_type_tag script{};
};

open_type_tag get_unicode_script(std::uint32_t code_point) noexcept;
std::uint8_t get_unicode_canonical_combining_class(
    std::uint32_t code_point) noexcept;
unicode_bidi_class get_unicode_bidi_class(std::uint32_t code_point) noexcept;
std::uint32_t get_unicode_mirrored_code_point(
    std::uint32_t code_point) noexcept;
std::uint32_t get_unicode_vertical_code_point(
    std::uint32_t code_point) noexcept;
bool try_get_unicode_bidi_bracket(
    std::uint32_t code_point,
    std::uint32_t& paired_code_point,
    unicode_bidi_bracket_kind& kind) noexcept;

struct unicode_bidi_level final {
    std::uint32_t input_index = 0U;
    std::uint16_t input_length = 0U;
    std::int8_t level = 0;
    std::uint8_t reserved = 0U;
};

struct unicode_bidi_unit final {
    std::uint32_t code_point = 0U;
    std::uint32_t input_index = 0U;
    std::uint16_t input_length = 0U;
    unicode_bidi_class original = unicode_bidi_class::left_to_right;
    unicode_bidi_class type = unicode_bidi_class::left_to_right;
    std::int8_t level = 0;
    std::uint8_t reserved = 0U;
    std::int32_t matching_isolate = -1;
};

struct unicode_bidi_level_run final {
    std::uint32_t active_start = 0U;
    std::uint32_t active_count = 0U;
    std::int32_t next = -1;
    std::int8_t explicit_level = 0;
    bool has_predecessor = false;
    std::uint8_t reserved0 = 0U;
    std::uint8_t reserved1 = 0U;
};

struct unicode_bidi_bracket_pair final {
    std::uint32_t open_position = 0U;
    std::uint32_t close_position = 0U;
};

struct unicode_bidi_requirements final {
    std::uint32_t unit_count = 0U;
    std::uint32_t index_count = 0U;
    std::uint32_t run_count = 0U;
    std::uint32_t bracket_pair_count = 0U;
};

struct unicode_bidi_scratch final {
    std::span<unicode_bidi_unit> units{};
    std::span<std::uint32_t> indices{};
    std::span<unicode_bidi_level_run> runs{};
    std::span<unicode_bidi_bracket_pair> bracket_pairs{};
};

bool try_get_unicode_bidi_requirements(
    std::span<const unicode_scalar> input,
    unicode_bidi_requirements& result,
    unicode_error* error = nullptr) noexcept;

bool try_resolve_unicode_bidi(
    std::span<const unicode_scalar> input,
    std::int8_t requested_paragraph_level,
    unicode_bidi_scratch scratch,
    std::span<unicode_bidi_level> output,
    std::int8_t& paragraph_level,
    std::uint32_t& written,
    unicode_error* error = nullptr) noexcept;

enum class unicode_grapheme_break_class : std::uint8_t {
    other = 0U,
    carriage_return,
    line_feed,
    control,
    extend,
    zero_width_joiner,
    regional_indicator,
    prepend,
    spacing_mark,
    hangul_l,
    hangul_v,
    hangul_t,
    hangul_lv,
    hangul_lvt
};

enum class unicode_indic_conjunct_class : std::uint8_t {
    none = 0U,
    consonant,
    extend,
    linker
};

struct unicode_grapheme_cluster final {
    std::uint32_t input_index = 0U;
    std::uint32_t input_length = 0U;
    std::uint32_t scalar_index = 0U;
    std::uint32_t scalar_count = 0U;
};

unicode_grapheme_break_class get_unicode_grapheme_break_class(
    std::uint32_t code_point) noexcept;
unicode_indic_conjunct_class get_unicode_indic_conjunct_class(
    std::uint32_t code_point) noexcept;
bool is_unicode_extended_pictographic(std::uint32_t code_point) noexcept;
bool try_get_unicode_grapheme_cluster_count(
    std::span<const unicode_scalar> input,
    std::uint32_t& result,
    unicode_error* error = nullptr) noexcept;
bool try_segment_unicode_graphemes(
    std::span<const unicode_scalar> input,
    std::span<unicode_grapheme_cluster> output,
    std::uint32_t& written,
    unicode_error* error = nullptr) noexcept;

struct unicode_indic_shaping_properties final {
    std::uint8_t category = 0U;
    std::uint8_t position = 0U;
};

enum class unicode_syllable_machine : std::uint8_t {
    indic = 0U,
    use = 1U,
    myanmar = 2U,
    khmer = 3U
};

struct unicode_syllable_transition final {
    std::uint16_t target = 0U;
    std::uint8_t action = 0U;
    std::uint8_t reserved = 0U;
};

unicode_indic_shaping_properties get_unicode_indic_shaping_properties(
    std::uint32_t code_point) noexcept;
std::uint8_t get_unicode_use_shaping_category(
    std::uint32_t code_point) noexcept;
std::uint16_t get_unicode_syllable_machine_state_count(
    unicode_syllable_machine machine) noexcept;
std::uint16_t get_unicode_syllable_machine_start_state(
    unicode_syllable_machine machine) noexcept;
bool try_get_unicode_syllable_transition(
    unicode_syllable_machine machine,
    std::uint16_t state,
    std::uint8_t category,
    unicode_syllable_transition& result) noexcept;
bool try_get_unicode_syllable_eof_transition(
    unicode_syllable_machine machine,
    std::uint16_t state,
    unicode_syllable_transition& result) noexcept;
bool try_assign_unicode_syllables(
    unicode_syllable_machine machine,
    std::span<const std::uint8_t> categories,
    std::span<const std::uint32_t> machine_indices,
    std::span<std::uint8_t> syllables) noexcept;

enum class unicode_arabic_joining_type : std::uint8_t {
    non_joining = 0U,
    left_joining = 1U,
    right_joining = 2U,
    dual_joining = 3U,
    alaph = 4U,
    dalath_rish = 5U,
    transparent = 6U
};

unicode_arabic_joining_type get_unicode_arabic_joining_type(
    std::uint32_t code_point) noexcept;

enum class open_type_arabic_action : std::uint8_t {
    isolated = 0U,
    final = 1U,
    final2 = 2U,
    final3 = 3U,
    medial = 4U,
    medial2 = 5U,
    initial = 6U,
    none = 7U,
    stretch_fixed = 8U,
    stretch_repeating = 9U
};

bool try_assign_open_type_arabic_actions(
    std::span<const unicode_scalar> input,
    std::span<open_type_arabic_action> output,
    std::uint32_t& written,
    unicode_error* error = nullptr) noexcept;

bool try_assign_open_type_arabic_actions(
    std::span<const unicode_scalar> input,
    std::span<const unicode_scalar> pre_context,
    std::span<const unicode_scalar> post_context,
    std::span<open_type_arabic_action> output,
    std::uint32_t& written,
    unicode_error* error = nullptr) noexcept;

/* Managed-equivalent Arabic joining safety metadata. The action and flag
 * arrays are scalar-indexed and can be propagated together during initial
 * glyph mapping without a glyph-dependent callback or allocation. */
bool try_assign_open_type_arabic_actions_and_flags(
    std::span<const unicode_scalar> input,
    std::span<const unicode_grapheme_cluster> graphemes,
    std::span<const unicode_scalar> pre_context,
    std::span<const unicode_scalar> post_context,
    shaping_buffer_flags buffer_flags,
    std::span<open_type_arabic_action> action_output,
    std::span<shaping_glyph_flags> flag_output,
    std::uint32_t& written,
    unicode_error* error = nullptr) noexcept;

struct arabic_stretch_run final {
    std::uint32_t start = 0U;
    std::uint32_t end = 0U;
    std::uint32_t copy_count = 0U;
    std::int32_t remaining_width = 0;
    std::int32_t extra_repeat_overlap = 0;
};

struct arabic_stretch_requirements final {
    std::uint32_t glyph_capacity = 0U;
    std::uint32_t run_capacity = 0U;
};

bool try_get_arabic_stretch_requirements(
    const sfnt_font_view& font,
    std::span<const shaping_glyph> glyphs,
    std::span<const open_type_arabic_action> actions,
    bool right_to_left,
    std::span<const std::int16_t> normalized_coordinates,
    arabic_stretch_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_apply_arabic_stretch(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    std::span<const open_type_arabic_action> actions,
    bool right_to_left,
    std::span<const std::int16_t> normalized_coordinates,
    std::span<arabic_stretch_run> run_scratch,
    font_error* error = nullptr) noexcept;

/*
 * Strict, transactional UTF decoding. The requirements pass validates the
 * entire input in O(N) time and O(1) storage. Decode repeats validation before
 * writing, requires exact-or-larger caller storage, writes one value record per
 * scalar, and leaves the destination untouched on failure.
 */
bool try_get_utf8_decode_requirements(
    std::span<const std::byte> input,
    unicode_decode_requirements& result,
    unicode_error* error = nullptr) noexcept;

bool try_decode_utf8(
    std::span<const std::byte> input,
    std::span<unicode_scalar> output,
    std::uint32_t& written,
    unicode_error* error = nullptr) noexcept;

bool try_get_utf16_decode_requirements(
    std::span<const std::uint16_t> input,
    unicode_decode_requirements& result,
    unicode_error* error = nullptr) noexcept;

bool try_decode_utf16(
    std::span<const std::uint16_t> input,
    std::span<unicode_scalar> output,
    std::uint32_t& written,
    unicode_error* error = nullptr) noexcept;

struct unicode_script_run final {
    std::uint32_t scalar_start = 0U;
    std::uint32_t scalar_count = 0U;
    std::uint32_t input_start = 0U;
    std::uint32_t input_length = 0U;
    open_type_tag script{};
};

/*
 * Allocation-free initial script itemization. DFLT Common/Inherited scalars
 * attach to the preceding resolved script, or the first following script for
 * leading text, matching ProGPU's first-strong run inference. The caller may
 * subsequently tailor boundaries with Script_Extensions/language policy.
 */
bool try_get_unicode_script_run_count(
    std::span<const unicode_scalar> input,
    std::uint32_t& run_count,
    unicode_error* error = nullptr) noexcept;

bool try_itemize_unicode_scripts(
    std::span<const unicode_scalar> input,
    std::span<unicode_script_run> output,
    std::uint32_t& written,
    unicode_error* error = nullptr) noexcept;

enum class unicode_normalization_form : std::uint8_t {
    canonical_decomposition = 0U,
    canonical_composition = 1U
};

struct unicode_normalization_requirements final {
    std::uint32_t scalar_capacity = 0U;
};

/*
 * Borrowed view over ProGPU.Text/UnicodeNormalizationData.bin. The format is
 * shared with UnicodeNormalizationPlan.cs and contains fully decomposed FormD
 * scalars plus canonical FormC pairs. Construction validates the complete
 * little-endian resource and retains no ownership.
 */
class unicode_normalization_data final {
public:
    static bool try_create(
        std::span<const std::byte> bytes,
        unicode_normalization_data& result,
        unicode_error* error = nullptr) noexcept;

    bool try_get_decomposition(
        std::uint32_t code_point,
        std::span<const std::byte>& little_endian_scalars) const noexcept;

    bool try_compose(
        std::uint32_t first,
        std::uint32_t second,
        std::uint32_t& composed) const noexcept;

private:
    std::span<const std::byte> bytes_{};
    std::size_t decomposition_offset_ = 0U;
    std::size_t scalar_offset_ = 0U;
    std::size_t composition_offset_ = 0U;
    std::uint32_t decomposition_count_ = 0U;
    std::uint32_t scalar_count_ = 0U;
    std::uint32_t composition_count_ = 0U;
};

/*
 * Canonical normalization uses the shared fully decomposed plan. Requirements
 * reports the maximum FormD output. The write pass performs stable canonical
 * ordering in place and optionally compacts canonical compositions. It is
 * transactional for validation/capacity failure and retains source ranges.
 */
bool try_get_unicode_normalization_requirements(
    std::span<const unicode_scalar> input,
    const unicode_normalization_data& data,
    unicode_normalization_requirements& result,
    unicode_error* error = nullptr) noexcept;

bool try_normalize_unicode(
    std::span<const unicode_scalar> input,
    const unicode_normalization_data& data,
    unicode_normalization_form form,
    std::span<unicode_scalar> output,
    std::uint32_t& written,
    unicode_error* error = nullptr) noexcept;

class open_type_coverage_view final {
public:
    static bool try_create(
        std::span<const std::byte> table,
        std::size_t offset,
        open_type_coverage_view& result,
        font_error* error = nullptr) noexcept;

    std::int32_t find(std::uint16_t glyph_id) const noexcept;

private:
    std::span<const std::byte> table_{};
    std::size_t offset_ = 0U;
    std::uint16_t count_ = 0U;
    std::uint16_t format_ = 0U;
};

class open_type_class_definition_view final {
public:
    static bool try_create(
        std::span<const std::byte> table,
        std::size_t offset,
        open_type_class_definition_view& result,
        font_error* error = nullptr) noexcept;

    std::uint16_t get(std::uint16_t glyph_id) const noexcept;

private:
    std::span<const std::byte> table_{};
    std::size_t offset_ = 0U;
    std::uint16_t count_ = 0U;
    std::uint16_t start_glyph_ = 0U;
    std::uint16_t format_ = 0U;
};

struct open_type_lookup_view final {
    std::span<const std::byte> table{};
    std::size_t offset = 0U;
    std::uint16_t type = 0U;
    std::uint16_t flags = 0U;
    std::uint16_t subtable_count = 0U;
    std::uint16_t mark_filtering_set = 0xFFFFU;

    bool try_get_subtable(
        std::uint16_t index,
        std::size_t& subtable_offset,
        font_error* error = nullptr) const noexcept;
};

/*
 * Borrowed common GSUB/GPOS table header and LookupList. Creation validates
 * the version and top-level offset arrays. Individual Lookup records are
 * validated transactionally when requested, keeping startup lazy and bounded.
 */
class open_type_layout_table_view final {
public:
    static bool try_create(
        std::span<const std::byte> table,
        open_type_layout_table_view& result,
        font_error* error = nullptr) noexcept;

    std::uint16_t lookup_count() const noexcept {
        return lookup_count_;
    }

    bool try_get_lookup(
        std::uint16_t index,
        open_type_lookup_view& result,
        font_error* error = nullptr) const noexcept;

    struct lookup_selection_requirements final {
        std::uint32_t lookup_capacity = 0U;
    };

    /*
     * Selects the required and explicitly requested LangSys features for an
     * exact script/language, falling back to DFLT and the default LangSys.
     * Requirements reports a safe caller-buffer upper bound; selection
     * deduplicates lookup indices in feature order without allocation.
     */
    bool try_get_lookup_selection_requirements(
        open_type_tag script,
        open_type_tag language,
        std::span<const open_type_tag> requested_features,
        lookup_selection_requirements& result,
        font_error* error = nullptr) const noexcept;

    bool try_select_lookups(
        open_type_tag script,
        open_type_tag language,
        std::span<const open_type_tag> requested_features,
        std::span<std::uint16_t> output,
        std::uint32_t& written,
        font_error* error = nullptr) const noexcept;

    bool try_select_lookups_excluding(
        open_type_tag script,
        open_type_tag language,
        std::span<const open_type_tag> requested_features,
        std::span<const open_type_tag> excluded_features,
        std::span<std::uint16_t> output,
        std::uint32_t& written,
        font_error* error = nullptr) const noexcept;

    /* Selects only one allowed LangSys feature, excluding the required
     * feature. This supports staged script shapers such as Arabic forms while
     * retaining the same caller-owned lookup buffer. */
    bool try_select_feature_lookups(
        open_type_tag script,
        open_type_tag language,
        open_type_tag feature,
        std::span<std::uint16_t> output,
        std::uint32_t& written,
        font_error* error = nullptr) const noexcept;

    bool try_feature_contains_lookup(
        open_type_tag script,
        open_type_tag language,
        open_type_tag feature,
        std::uint16_t lookup,
        bool& contains,
        font_error* error = nullptr) const noexcept;

    bool try_required_feature_contains_lookup(
        open_type_tag script,
        open_type_tag language,
        std::uint16_t lookup,
        bool& contains,
        font_error* error = nullptr) const noexcept;

    bool try_required_feature_for_lookup(
        open_type_tag script,
        open_type_tag language,
        std::uint16_t lookup,
        open_type_tag& feature,
        bool& contains,
        font_error* error = nullptr) const noexcept;

private:
    std::span<const std::byte> table_{};
    std::size_t script_list_offset_ = 0U;
    std::size_t feature_list_offset_ = 0U;
    std::size_t lookup_list_offset_ = 0U;
    std::size_t feature_variations_offset_ = 0U;
    std::uint16_t lookup_count_ = 0U;
};

enum class open_type_glyph_class : std::uint16_t {
    unclassified = 0U,
    base = 1U,
    ligature = 2U,
    mark = 3U,
    component = 4U
};

/*
 * Borrowed GDEF glyph-class and mark-filtering metadata. Construction validates
 * only the present tables and retains no ownership. Glyph classification is
 * O(log R) for range ClassDefs, while mark-set membership is O(log C) for C
 * coverage records. The fixed compatibility blocklist matches ProGPU's managed
 * OpenTypeGdefPolicy and is O(1) without allocation.
 */
class open_type_gdef_view final {
public:
    static bool try_create(
        std::span<const std::byte> table,
        open_type_gdef_view& result,
        font_error* error = nullptr) noexcept;

    open_type_glyph_class glyph_class(std::uint16_t glyph_id) const noexcept;
    std::uint16_t mark_attachment_class(std::uint16_t glyph_id) const noexcept;
    std::uint16_t mark_set_count() const noexcept {
        return mark_set_count_;
    }
    bool is_in_mark_set(
        std::uint16_t set_index,
        std::uint16_t glyph_id) const noexcept;

private:
    std::span<const std::byte> table_{};
    open_type_class_definition_view glyph_classes_{};
    open_type_class_definition_view mark_attachment_classes_{};
    std::size_t mark_sets_offset_ = 0U;
    std::uint16_t mark_set_count_ = 0U;
    bool has_glyph_classes_ = false;
    bool has_mark_attachment_classes_ = false;
};

bool is_open_type_gdef_blocklisted(
    std::size_t gdef_length,
    std::size_t gsub_length,
    std::size_t gpos_length) noexcept;

struct open_type_gsub_apply_options final {
    const open_type_gdef_view* gdef = nullptr;
    std::uint32_t alternate_value = 1U;
    /* Optional private/script eligibility bits required on the lookup's
     * starting glyph. Zero preserves ordinary GSUB behavior. */
    std::uint32_t required_glyph_flags = 0U;
    bool mark_substituted = false;
    /* Optional top-level contextual match boundary. Context and chaining
     * lookups write the exclusive input end so a caller can avoid feeding a
     * matched input sequence back through the same lookup. */
    std::uint32_t* context_match_end = nullptr;
    /* Enables transient ligature-component metadata for the later managed-
     * parity fallback mark stage. The full-run shaper strips it before return. */
    bool track_fallback_mark_metadata = false;
    /* Marks MultipleSubst outputs with their bounded component index for the
     * Arabic stch stage. This metadata is private to native shaping and must
     * be cleared before publishing the stable shaped-glyph span. */
    bool track_arabic_stretch_metadata = false;
    /* Managed `rand` parity: one caller/run-owned state advances only for a
     * matched AlternateSubst at top-level lookup depth. */
    std::uint32_t* random_state = nullptr;
    bool random_alternate = false;
};

/*
 * Allocation-free GSUB lookup execution over a caller-owned bulk glyph buffer.
 * The initial executor covers Single, Multiple, Alternate, Ligature, and
 * Extension, Reverse Chaining Single, and Context/Chaining Context formats
 * 1–3 with nested-lookup depth bounds and GDEF filtering. Each individual
 * substitution preflights all table and capacity reads before mutation.
 * `glyph_count` must describe the initialized prefix of `glyph_storage`.
 */
bool try_apply_open_type_gsub_lookup(
    const open_type_layout_table_view& gsub,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    const open_type_gsub_apply_options& options,
    bool& applied,
    font_error* error = nullptr) noexcept;

bool try_apply_open_type_gsub_lookup_at(
    const open_type_layout_table_view& gsub,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    std::uint32_t position,
    const open_type_gsub_apply_options& options,
    bool& applied,
    font_error* error = nullptr) noexcept;

enum class shaping_attachment_kind : std::uint8_t {
    none = 0U,
    mark = 1U,
    cursive_horizontal = 2U,
    cursive_vertical = 3U
};

struct shaping_attachment final {
    std::int32_t target = -1;
    shaping_attachment_kind kind = shaping_attachment_kind::none;
    /* During full-run shaping these bytes carry fallback-mark ligature count,
     * component (0xFF means unspecified), and prior-positioned state. */
    std::uint8_t reserved0 = 0U;
    std::uint8_t reserved1 = 0xFFU;
    std::uint8_t reserved2 = 0U;
};

/* Transient metadata used only while reproducing managed fallback mark
 * placement. It is deliberately separate from the stable 32-byte glyph wire
 * record and remains caller-owned for the duration of one shaping run. */
struct fallback_mark_metadata final {
    std::uint8_t ligature_component_count = 0U;
    std::uint8_t ligature_component = 0xFFU;
    bool positioned = false;
};

struct fallback_mark_positioning_scratch;

/*
 * Direct native counterpart of GlyphPositionBuffer fallback mark placement.
 * Glyph positions are updated in place in O(G * T) time for G glyphs and T
 * borrowed table records, with O(1) internal storage. An empty metadata span
 * means no prior GPOS placement or ligature-component annotations.
 */
bool try_apply_fallback_mark_positioning(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    std::span<const fallback_mark_metadata> metadata = {},
    std::span<const std::int16_t> normalized_coordinates = {},
    font_error* error = nullptr) noexcept;
bool try_apply_fallback_mark_positioning(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    std::span<const fallback_mark_metadata> metadata,
    std::span<const std::int16_t> normalized_coordinates,
    fallback_mark_positioning_scratch& scratch,
    font_error* error = nullptr) noexcept;

struct open_type_gpos_apply_options final {
    const open_type_gdef_view* gdef = nullptr;
    shaping_direction direction = shaping_direction::left_to_right;
    std::span<shaping_attachment> attachments{};
    const sfnt_font_view* font = nullptr;
    std::span<const std::int16_t> normalized_coordinates{};
    std::uint16_t pixels_per_em_x = 0U;
    std::uint16_t pixels_per_em_y = 0U;
};

/*
 * Allocation-free GPOS execution over the same caller-owned shaped glyph
 * records. The executor covers Single, Pair, Cursive, Mark-to-Base,
 * Mark-to-Ligature, and Mark-to-Mark positioning plus Extension wrappers and
 * Context/Chaining Context formats 1-3 with bounded nested lookups. Value and
 * anchor Device tables plus VariationIndex records are applied when the caller
 * supplies the font/coordinates and optional target ppem. Anchor relationships
 * use caller-owned attachment records and values remain in font units for later
 * run scaling.
 */
bool try_apply_open_type_gpos_lookup(
    const open_type_layout_table_view& gpos,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> glyphs,
    const open_type_gpos_apply_options& options,
    bool& applied,
    font_error* error = nullptr) noexcept;

bool try_apply_open_type_gpos_lookup_at(
    const open_type_layout_table_view& gpos,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> glyphs,
    std::uint32_t position,
    const open_type_gpos_apply_options& options,
    bool& applied,
    font_error* error = nullptr) noexcept;

bool try_resolve_open_type_attachments(
    std::span<shaping_glyph> glyphs,
    std::span<const shaping_attachment> attachments,
    shaping_direction direction,
    std::span<std::uint8_t> state_scratch,
    font_error* error = nullptr) noexcept;

enum class open_type_complex_script : std::uint8_t {
    none = 0U,
    indic = 1U,
    use = 2U,
    myanmar = 3U,
    khmer = 4U
};

struct open_type_shaping_route final {
    open_type_tag unicode_script{};
    open_type_tag layout_script{};
    shaping_direction direction = shaping_direction::left_to_right;
    open_type_complex_script complex_script = open_type_complex_script::none;
    bool use_shaper = false;
    bool indic_shaper = false;
    bool khmer_shaper = false;
    bool myanmar_shaper = false;
    bool arabic_shaper = false;
    bool compose_hebrew_presentation_forms = true;
};

/* Maps the managed ProGPU BCP-47 language subset to OpenType language tags.
 * Matching is ASCII case-insensitive and treats '_' as '-'; unknown tags map
 * to dflt without allocation. */
open_type_tag resolve_open_type_language_tag(std::string_view language) noexcept;

/* Resolves the same Unicode-script to OpenType-layout route as managed
 * ProGPU, including third-/second-generation Indic tags, USE selection, and
 * default bidi direction. The font and GSUB bytes remain borrowed. Work is
 * bounded O(S) for S ScriptList records with O(1) internal storage. */
bool try_resolve_open_type_shaping_route(
    const sfnt_font_view& font,
    open_type_tag unicode_script,
    shaping_direction requested_direction,
    open_type_shaping_route& result,
    font_error* error = nullptr) noexcept;

struct open_type_feature_plan_requirements final {
    std::uint32_t requested_feature_capacity = 0U;
    std::uint32_t feature_setting_capacity = 0U;
};

/* Returns the authoritative managed ProGPU default feature baseline without
 * ownership transfer. The storage is immutable and process-lifetime. */
std::span<const open_type_feature_setting>
get_default_open_type_feature_settings() noexcept;

struct open_type_requested_feature_requirements final {
    std::uint32_t base_feature_capacity = 0U;
    std::uint32_t explicit_feature_capacity = 0U;
    std::uint32_t ranged_feature_capacity = 0U;
};

/* Ports managed CpuOpenTypeShaper request normalization. Full-run records
 * override the default baseline; all request tags become explicit; partial
 * ranges remain caller-owned for later concatenation with the resolved global
 * value records. Requirements is exact and failure is transactional. */
bool try_get_open_type_requested_feature_requirements(
    std::span<const shaping_feature> requested_features,
    open_type_requested_feature_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_resolve_open_type_requested_features(
    std::span<const shaping_feature> requested_features,
    std::span<open_type_feature_setting> base_features_output,
    std::span<open_type_tag> explicit_features_output,
    std::span<shaping_feature> ranged_features_output,
    std::uint32_t& base_features_written,
    std::uint32_t& explicit_features_written,
    std::uint32_t& ranged_features_written,
    font_error* error = nullptr) noexcept;

/* Resolves the same script/direction feature policy and ordering as managed
 * ProGPU. Requirements reports safe caller capacities. Resolution writes the
 * ordered requested tags and only non-default full-run value records; value
 * one is represented by tag presence so conditional features remain distinct
 * from explicit ranged settings. Explicit tags distinguish caller intent for
 * defaults such as Khmer liga. Work is O(F^2) for the small feature set F with
 * O(1) internal space. */
bool try_get_open_type_feature_plan_requirements(
    const open_type_shaping_route& route,
    std::span<const open_type_feature_setting> base_features,
    open_type_feature_plan_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_resolve_open_type_feature_plan(
    const open_type_shaping_route& route,
    std::span<const open_type_feature_setting> base_features,
    std::span<const open_type_tag> explicit_features,
    std::span<open_type_tag> requested_features_output,
    std::span<shaping_feature> feature_settings_output,
    std::uint32_t& requested_features_written,
    std::uint32_t& feature_settings_written,
    font_error* error = nullptr) noexcept;

struct open_type_shape_run_options final {
    open_type_tag script{};
    open_type_tag language{};
    shaping_direction direction = shaping_direction::left_to_right;
    std::span<const open_type_tag> requested_features{};
    std::span<const std::int16_t> normalized_coordinates{};
    std::uint32_t alternate_value = 1U;
    bool zero_mark_advances = true;
    shaping_cluster_level cluster_level =
        shaping_cluster_level::monotone_graphemes;
    shaping_buffer_flags buffer_flags = shaping_buffer_flags::none;
    bool compose_hebrew_presentation_forms = true;
    open_type_complex_script complex_script = open_type_complex_script::none;
    /* Feature tags explicitly requested by the caller, as distinct from
     * shaper defaults. Conditional defaults such as numr/dnom remain dormant
     * outside their script context unless listed here. */
    std::span<const open_type_tag> explicit_features{};
    /* Exact ProGPU half-open feature-value ranges. The records are borrowed
     * for the run and cross the managed/native boundary as one fixed-layout
     * span; requested_features must contain every referenced tag. When a tag
     * has any records, this span is its resolved value sequence and therefore
     * includes an all-input baseline record before narrower overrides. */
    std::span<const shaping_feature> feature_settings{};
    /* Shared borrowed FormD plan used by managed-compatible initial missing-
     * glyph decomposition and Indic/USE shaping. Required for Indic and USE;
     * retained only for this synchronous call and never copied or owned by the
     * shaping plan. Other scripts opt into canonical missing-glyph expansion
     * by supplying the same process-wide plan. */
    const unicode_normalization_data* normalization_data = nullptr;
    /* Decoded neighboring text used only for boundary-sensitive shaping.
     * At most five scalars on each side are inspected; spans are borrowed for
     * this synchronous call and never retained by a shaping plan. */
    std::span<const unicode_scalar> pre_context{};
    std::span<const unicode_scalar> post_context{};
    /* Original canonical Unicode script. A zero tag preserves compatibility
     * by falling back to script; generation-specific layout tags such as dev2
     * must set this to the base Unicode tag for preprocessing/reordering. */
    open_type_tag unicode_script{};
};

struct open_type_shape_configuration_request final {
    open_type_tag unicode_script{};
    std::string_view language{};
    shaping_direction direction = shaping_direction::unspecified;
    std::span<const shaping_feature> features{};
    std::span<const std::int16_t> normalized_coordinates{};
    std::uint32_t alternate_value = 1U;
    bool zero_mark_advances = true;
    shaping_cluster_level cluster_level =
        shaping_cluster_level::monotone_graphemes;
    shaping_buffer_flags buffer_flags = shaping_buffer_flags::none;
    const unicode_normalization_data* normalization_data = nullptr;
    std::span<const unicode_scalar> pre_context{};
    std::span<const unicode_scalar> post_context{};
};

struct open_type_shape_configuration_requirements final {
    std::uint32_t base_feature_capacity = 0U;
    std::uint32_t explicit_feature_capacity = 0U;
    std::uint32_t requested_feature_capacity = 0U;
    std::uint32_t feature_setting_capacity = 0U;
};

struct open_type_shape_configuration final {
    open_type_shaping_route route{};
    open_type_shape_run_options options{};
    std::uint32_t base_features_written = 0U;
    std::uint32_t requested_features_written = 0U;
    std::uint32_t explicit_features_written = 0U;
    std::uint32_t feature_settings_written = 0U;
};

/* One allocation-free managed-equivalent planning boundary for a decoded
 * uniform run. It infers/canonicalizes the Unicode script, selects the font's
 * layout generation, normalizes caller feature ranges, resolves ordered
 * script/direction features, maps language, and returns run options borrowing
 * only caller-owned buffers. The capacity pass is a safe upper bound. */
bool try_get_open_type_shape_configuration_requirements(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const open_type_shape_configuration_request& request,
    open_type_shape_configuration_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_prepare_open_type_shape_configuration(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const open_type_shape_configuration_request& request,
    std::span<open_type_feature_setting> base_feature_scratch,
    std::span<open_type_tag> explicit_feature_storage,
    std::span<open_type_tag> requested_feature_storage,
    std::span<shaping_feature> feature_setting_storage,
    open_type_shape_configuration& result,
    font_error* error = nullptr) noexcept;

struct open_type_shape_verification_scratch final {
    /* Separate glyph output preserves the completed run while each advertised
     * safe fragment is reshaped. All other shaping scratch is reused. */
    std::span<shaping_glyph> glyphs{};
};

struct open_type_shape_run_scratch final {
    std::span<unicode_grapheme_cluster> grapheme_clusters{};
    std::span<std::uint16_t> gsub_lookups{};
    std::span<std::uint16_t> gpos_lookups{};
    std::span<shaping_attachment> attachments{};
    std::span<std::uint8_t> attachment_states{};
    std::span<open_type_arabic_action> arabic_actions{};
    std::span<std::uint8_t> script_categories{};
    std::span<std::uint8_t> script_syllables{};
    std::span<std::uint32_t> script_indices{};
    std::span<arabic_stretch_run> arabic_stretch_runs{};
    /* Optional reusable full-outline/phantom scratch. Supplying it upgrades
     * ordinary run metrics and fallback marks to active gvar/CFF parity; the
     * pointer is borrowed synchronously and is never retained. */
    fallback_mark_positioning_scratch* fallback_marks = nullptr;
    /* Required only when ShapingBufferFlags.Verify is set. Diagnostic
     * verification is allocation-free and never retained. */
    open_type_shape_verification_scratch* verification = nullptr;
    /* Scalar-indexed joining flags accompany arabic_actions for Arabic-
     * joining scripts and are copied into mapped glyph records in bulk. */
    std::span<shaping_glyph_flags> arabic_flags{};
};

struct open_type_shape_run_requirements final {
    std::uint32_t initial_glyph_count = 0U;
    std::uint32_t glyph_capacity = 0U;
    std::uint32_t grapheme_capacity = 0U;
    std::uint32_t gsub_lookup_capacity = 0U;
    std::uint32_t gpos_lookup_capacity = 0U;
    std::uint32_t script_action_capacity = 0U;
    std::uint32_t complex_script_capacity = 0U;
    std::uint32_t complex_script_index_capacity = 0U;
    std::uint32_t verification_glyph_capacity = 0U;
};

struct open_type_shape_plan_requirements final {
    std::uint32_t gsub_lookup_capacity = 0U;
    std::uint32_t gpos_lookup_capacity = 0U;
};

/* Borrowed reusable shaping plan. The font bytes and caller-owned lookup
 * arrays must outlive the plan; no font data or feature list is copied. */
struct open_type_shape_plan final {
    open_type_layout_table_view gsub{};
    open_type_layout_table_view gpos{};
    open_type_gdef_view gdef{};
    std::span<const std::uint16_t> gsub_lookups{};
    std::span<const std::uint16_t> gpos_lookups{};
    const std::byte* font_data = nullptr;
    std::size_t font_size = 0U;
    std::uint64_t feature_hash = 0U;
    std::uint32_t face_index = 0U;
    open_type_tag script{};
    open_type_tag language{};
    bool has_gdef = false;

    bool matches(
        const sfnt_font_view& font,
        const open_type_shape_run_options& options) const noexcept;
};

bool try_get_open_type_shape_plan_requirements(
    const sfnt_font_view& font,
    const open_type_shape_run_options& options,
    open_type_shape_plan_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_build_open_type_shape_plan(
    const sfnt_font_view& font,
    const open_type_shape_run_options& options,
    std::span<std::uint16_t> gsub_lookup_storage,
    std::span<std::uint16_t> gpos_lookup_storage,
    open_type_shape_plan& result,
    font_error* error = nullptr) noexcept;

/* Applies the ProGPU Hangul composition/decomposition preparation in place.
 * The caller supplies expandable storage; capacity failure is transactional. */
bool try_prepare_open_type_hangul(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    font_error* error = nullptr) noexcept;

/* Applies the ProGPU directional code-point fallback in place. Backward runs
 * use mirrored forms when the font maps them; vertical runs without a selected
 * vert/vrt2 feature then use vertical presentation forms. Work is O(G log P)
 * for G glyphs and P generated mappings, allocation-free, and preserves a
 * glyph when the font does not contain its mapped form. */
bool try_apply_directional_code_point_fallback(
    const sfnt_font_view& font,
    std::span<shaping_glyph> glyphs,
    shaping_direction direction,
    bool has_vertical_substitution,
    font_error* error = nullptr) noexcept;

/* Ports ProGPU's common pre-GSUB glyph preparation: optional start-of-text
 * dotted circle, modified combining-class ordering (including Arabic modifier
 * marks), Hebrew presentation-form composition, Indic vowel-constraint
 * repair, optional USE mark-led FormD expansion, and Thai/Lao Sara Am
 * decomposition/reordering. The caller supplies expandable storage and the
 * operation preflights capacity and font mappings before mutation. */
bool try_preprocess_open_type_glyphs(
    const sfnt_font_view& font,
    open_type_tag script,
    shaping_cluster_level cluster_level,
    shaping_buffer_flags buffer_flags,
    bool compose_hebrew_presentation_forms,
    std::span<shaping_glyph> glyph_storage,
    std::uint32_t& glyph_count,
    font_error* error = nullptr,
    const unicode_normalization_data* use_normalization_data = nullptr,
    bool has_pre_context = false) noexcept;

struct font_fallback_candidate final {
    const sfnt_font_view* font = nullptr;
    std::uint64_t identity = 0U;
};

struct font_fallback_run final {
    std::uint32_t scalar_index = 0U;
    std::uint32_t scalar_count = 0U;
    std::uint32_t input_index = 0U;
    std::uint32_t input_length = 0U;
    std::uint32_t font_index = 0U;
    bool has_missing_glyphs = false;
    std::uint8_t reserved0 = 0U;
    std::uint8_t reserved1 = 0U;
    std::uint8_t reserved2 = 0U;
};

enum class font_provider_slant : std::uint8_t {
    normal = 0U,
    italic = 1U,
    oblique = 2U
};

struct font_style_request final {
    std::int32_t weight = 400;
    std::int32_t width = 5;
    font_provider_slant slant = font_provider_slant::normal;
};

struct font_style_variation_requirements final {
    std::uint16_t setting_count = 0U;
};

struct font_style_variation final {
    open_type_tag tag{};
    std::int32_t user_fixed = 0;
    std::int16_t normalized = 0;
    std::uint16_t axis_index = 0U;
};

/* Maps the managed FontStyleRequest contract onto wght/wdth/ital/slnt axes.
 * Requirements fully validate recognized axes before a caller-owned result is
 * written, so short buffers and malformed variation data are transactional. */
bool try_get_font_style_variation_requirements(
    const sfnt_font_view& font,
    font_style_request request,
    font_style_variation_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_resolve_font_style_variations(
    const sfnt_font_view& font,
    font_style_request request,
    std::span<font_style_variation> output,
    std::uint16_t& written,
    font_style_variation_requirements* requirements = nullptr,
    font_error* error = nullptr) noexcept;

/* Ordered platform family preferences from managed FontManager. Returned
 * string views reference process-lifetime literals and can be mapped to a
 * provider's family identities once per discovery generation. */
bool try_get_font_fallback_family_preference_count(
    std::span<const std::string_view> language_tags,
    std::uint32_t code_point,
    std::uint32_t& result,
    font_error* error = nullptr) noexcept;

bool try_get_font_fallback_family_preferences(
    std::span<const std::string_view> language_tags,
    std::uint32_t code_point,
    std::span<std::string_view> output,
    std::uint32_t& written,
    font_error* error = nullptr) noexcept;

/* Platform adapters expose borrowed face bytes through this neutral record.
 * `family_identity` is a provider-stable normalized family hash. */
struct font_provider_face final {
    std::span<const std::byte> data{};
    std::uint64_t identity = 0U;
    std::uint64_t family_identity = 0U;
    std::uint32_t face_index = 0U;
    std::uint16_t weight = 400U;
    std::uint8_t stretch = 5U;
    font_provider_slant slant = font_provider_slant::normal;
    bool is_fallback = false;
};

using font_provider_count_callback = std::uint32_t(*)(void*) noexcept;
using font_provider_face_callback = bool(*)(
    void*, std::uint32_t, font_provider_face&) noexcept;

struct font_provider_view final {
    void* context = nullptr;
    std::uint64_t generation = 0U;
    font_provider_count_callback get_face_count = nullptr;
    font_provider_face_callback try_get_face = nullptr;
};

struct font_provider_cache_entry final {
    std::uint64_t generation = 0U;
    std::uint64_t family_identity = 0U;
    std::uint32_t code_point = 0U;
    std::uint32_t face_index = 0U;
    std::uint16_t weight = 0U;
    std::uint8_t stretch = 0U;
    font_provider_slant slant = font_provider_slant::normal;
    bool found = false;
    bool occupied = false;
};

struct font_provider_result final {
    font_provider_face face{};
    std::uint32_t provider_index = 0U;
    std::uint16_t glyph_index = 0U;
    bool found = false;
};

/* Resolves one family/style/scalar request, including the already validated
 * glyph index so the caller does not repeat cmap lookup. Cache storage and replacement
 * cursor belong to the caller. Hits are O(C), misses O(F*T) for cache slots C,
 * provider faces F, and bounded SFNT table lookup T; no allocation or I/O is
 * performed by the native resolver. */
bool try_resolve_font_provider_face(
    const font_provider_view& provider,
    std::uint64_t family_identity,
    std::uint16_t weight,
    std::uint8_t stretch,
    font_provider_slant slant,
    std::uint32_t code_point,
    std::span<font_provider_cache_entry> cache,
    std::uint32_t& replacement_cursor,
    font_provider_result& result,
    font_error* error = nullptr) noexcept;

/* Performs the managed FontManager fallback priority in one provider pass:
 * ordered family identities, registered fallback faces, then all remaining
 * faces. The excluded identity is skipped without changing catalog state. */
bool try_resolve_font_provider_fallback_face(
    const font_provider_view& provider,
    std::span<const std::uint64_t> ordered_family_identities,
    std::uint16_t weight,
    std::uint8_t stretch,
    font_provider_slant slant,
    std::uint32_t code_point,
    std::uint64_t excluded_face_identity,
    font_provider_result& result,
    font_error* error = nullptr) noexcept;

bool try_get_font_fallback_run_count(
    std::span<const unicode_scalar> input,
    std::span<const unicode_grapheme_cluster> graphemes,
    std::span<const font_fallback_candidate> candidates,
    std::uint32_t preferred_font_index,
    std::uint32_t& result,
    font_error* error = nullptr) noexcept;

/* Grapheme-preserving fallback over borrowed parsed faces. Platform font
 * discovery stays outside the hot path; this bulk stage performs no callbacks,
 * allocation, file access, or managed/native crossings. */
bool try_itemize_font_fallback(
    std::span<const unicode_scalar> input,
    std::span<const unicode_grapheme_cluster> graphemes,
    std::span<const font_fallback_candidate> candidates,
    std::uint32_t preferred_font_index,
    std::span<font_fallback_run> output,
    std::uint32_t& written,
    font_error* error = nullptr) noexcept;

enum class unicode_line_break_class : std::uint8_t {
    unknown = 0U,
    ambiguous = 1U,
    aksara = 2U,
    alphabetic = 3U,
    aksara_prebase = 4U,
    aksara_start = 5U,
    break_both = 6U,
    break_after = 7U,
    break_before = 8U,
    mandatory = 9U,
    contingent = 10U,
    conditional_japanese = 11U,
    close_punctuation = 12U,
    combining_mark = 13U,
    close_parenthesis = 14U,
    carriage_return = 15U,
    emoji_base = 16U,
    emoji_modifier = 17U,
    exclamation = 18U,
    glue = 19U,
    hangul_lv = 20U,
    hangul_lvt = 21U,
    unambiguous_hyphen = 22U,
    hebrew_letter = 23U,
    hyphen = 24U,
    ideographic = 25U,
    inseparable = 26U,
    infix_numeric = 27U,
    hangul_l = 28U,
    hangul_t = 29U,
    hangul_v = 30U,
    line_feed = 31U,
    next_line = 32U,
    nonstarter = 33U,
    numeric = 34U,
    open_punctuation = 35U,
    postfix_numeric = 36U,
    prefix_numeric = 37U,
    quotation = 38U,
    regional_indicator = 39U,
    complex_context = 40U,
    surrogate = 41U,
    space = 42U,
    break_symbol = 43U,
    virama_final = 44U,
    virama = 45U,
    word_joiner = 46U,
    zero_width_space = 47U,
    zero_width_joiner = 48U
};

enum class text_line_break_kind : std::uint8_t {
    prohibited = 0U,
    opportunity = 1U,
    mandatory = 2U
};

unicode_line_break_class get_unicode_line_break_class(
    std::uint32_t code_point) noexcept;

/* Unicode 17 UAX #14 default line-break resolution over decoded logical
 * scalars. Output is indexed by scalar and describes the boundary after it;
 * the final boundary is mandatory. The caller owns the resolved-class scratch
 * and output spans, keeping the stage allocation-free and bulk-interoperable. */
bool try_resolve_unicode_line_breaks(
    std::span<const unicode_scalar> input,
    std::span<unicode_line_break_class> class_scratch,
    std::span<text_line_break_kind> breaks_after,
    unicode_error* error = nullptr) noexcept;

enum class text_trimming : std::uint8_t {
    none = 0U,
    character_ellipsis = 1U,
    word_ellipsis = 2U
};

struct text_layout_options final {
    float scale = 1.0F;
    float maximum_width = 0.0F;
    float line_height = 0.0F;
    std::uint32_t maximum_lines = 0U;
    shaping_direction direction = shaping_direction::left_to_right;
    text_trimming trimming = text_trimming::none;
    std::uint16_t reserved = 0U;
    std::uint32_t ellipsis_glyph_id = 0U;
    float ellipsis_advance = 0.0F;
};

struct positioned_text_glyph final {
    std::uint32_t glyph_index = 0U;
    std::uint32_t glyph_id = 0U;
    std::int32_t cluster = 0;
    float x = 0.0F;
    float y = 0.0F;
    float advance_x = 0.0F;
    float advance_y = 0.0F;
};

struct positioned_text_line final {
    std::uint32_t glyph_start = 0U;
    std::uint32_t glyph_count = 0U;
    std::int32_t input_start = 0;
    std::int32_t input_end = 0;
    float width = 0.0F;
    float baseline_y = 0.0F;
    float height = 0.0F;
    bool clipped = false;
    std::uint8_t reserved0 = 0U;
    std::uint8_t reserved1 = 0U;
    std::uint8_t reserved2 = 0U;
};

struct text_layout_requirements final {
    std::uint32_t glyph_capacity = 0U;
    std::uint32_t line_capacity = 0U;
};

struct text_visual_cluster_group final {
    std::uint32_t glyph_start = 0U;
    std::uint32_t glyph_count = 0U;
    std::int8_t bidi_level = 0;
    std::uint8_t reserved0 = 0U;
    std::uint8_t reserved1 = 0U;
    std::uint8_t reserved2 = 0U;
};

struct text_visual_order_requirements final {
    std::uint32_t glyph_capacity = 0U;
    std::uint32_t group_capacity = 0U;
};

/* Ports TextLayout.GetVisualLineCandidates: equal-cluster glyph groups keep
 * their internal shaper order while UAX #9 L1/L2 reorders the groups. */
bool try_get_text_visual_order_requirements(
    std::span<const shaping_glyph> logical_glyphs,
    std::span<const std::int8_t> bidi_levels,
    text_visual_order_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_reorder_text_line_visual(
    std::span<const shaping_glyph> logical_glyphs,
    std::span<const std::int8_t> bidi_levels,
    std::int8_t paragraph_level,
    std::span<text_visual_cluster_group> group_scratch,
    std::span<shaping_glyph> visual_glyphs,
    std::uint32_t& written,
    font_error* error = nullptr) noexcept;

bool try_get_text_line_visual_indices(
    std::span<const shaping_glyph> logical_glyphs,
    std::span<const std::int8_t> bidi_levels,
    std::int8_t paragraph_level,
    std::span<text_visual_cluster_group> group_scratch,
    std::span<std::uint32_t> visual_indices,
    std::uint32_t& written,
    font_error* error = nullptr) noexcept;

bool try_get_text_layout_requirements(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    text_layout_requirements& result,
    font_error* error = nullptr) noexcept;

/* Positions an already-shaped visual run in O(G) time with O(1) internal
 * storage. Break opportunities are supplied by the reusable Unicode line-break
 * stage and are ignored inside equal-cluster glyph sequences. */
bool try_layout_shaped_text(
    std::span<const shaping_glyph> glyphs,
    std::span<const text_line_break_kind> breaks_after,
    const text_layout_options& options,
    std::span<positioned_text_glyph> positioned_glyphs,
    std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count,
    std::uint32_t& line_count,
    font_error* error = nullptr) noexcept;

struct text_logical_layout_scratch final {
    std::span<text_visual_cluster_group> visual_groups{};
    std::span<std::uint32_t> visual_indices{};
};

/* Wraps logical shaped glyphs, applies per-line UAX #9 L1/L2 ordering, and
 * publishes positioned glyphs with their original logical input indices. */
bool try_layout_logical_shaped_text(
    std::span<const shaping_glyph> logical_glyphs,
    std::span<const text_line_break_kind> breaks_after,
    std::span<const std::int8_t> bidi_levels,
    std::int8_t paragraph_level,
    const text_layout_options& options,
    text_logical_layout_scratch scratch,
    std::span<positioned_text_glyph> positioned_glyphs,
    std::span<positioned_text_line> lines,
    std::uint32_t& glyph_count,
    std::uint32_t& line_count,
    font_error* error = nullptr) noexcept;

struct text_cluster_box final {
    std::int32_t input_start = 0;
    std::int32_t input_end = 0;
    std::uint32_t line_index = 0U;
    std::int8_t bidi_level = 0;
    std::uint8_t reserved0 = 0U;
    std::uint8_t reserved1 = 0U;
    std::uint8_t reserved2 = 0U;
    float x = 0.0F;
    float y = 0.0F;
    float width = 0.0F;
    float height = 0.0F;
};

struct text_caret_stop final {
    std::int32_t input_position = 0;
    std::uint32_t line_index = 0U;
    float x = 0.0F;
    float y = 0.0F;
    float height = 0.0F;
    std::int8_t bidi_level = 0;
    bool trailing = false;
    std::uint8_t reserved0 = 0U;
    std::uint8_t reserved1 = 0U;
};

struct text_rectangle final {
    float x = 0.0F;
    float y = 0.0F;
    float width = 0.0F;
    float height = 0.0F;
};

struct text_hit_test_result final {
    std::int32_t input_position = 0;
    std::uint32_t line_index = 0U;
    text_rectangle bounds{};
    std::int8_t bidi_level = 0;
    bool trailing = false;
    bool inside = false;
    std::uint8_t reserved0 = 0U;
};

struct text_interaction_requirements final {
    std::uint32_t cluster_box_capacity = 0U;
    std::uint32_t caret_stop_capacity = 0U;
};

/* Converts positioned glyphs to physical-order cluster boxes. Cluster ends and
 * bidi levels are explicit reusable paragraph inputs, avoiding hidden maps. */
bool try_get_text_interaction_requirements(
    std::span<const positioned_text_glyph> glyphs,
    std::span<const positioned_text_line> lines,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels,
    text_interaction_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_build_text_interaction(
    std::span<const positioned_text_glyph> glyphs,
    std::span<const positioned_text_line> lines,
    std::span<const std::int32_t> cluster_ends,
    std::span<const std::int8_t> bidi_levels,
    std::span<text_cluster_box> cluster_boxes,
    std::span<text_caret_stop> caret_stops,
    std::uint32_t& cluster_box_count,
    std::uint32_t& caret_stop_count,
    font_error* error = nullptr) noexcept;

bool try_hit_test_text(
    std::span<const text_cluster_box> cluster_boxes,
    float x,
    float y,
    text_hit_test_result& result,
    font_error* error = nullptr) noexcept;

bool try_get_text_caret_stop(
    std::span<const text_caret_stop> caret_stops,
    std::int32_t input_position,
    bool trailing_affinity,
    text_caret_stop& result,
    font_error* error = nullptr) noexcept;

bool try_move_text_caret_visually(
    std::span<const text_caret_stop> caret_stops,
    std::int32_t input_position,
    bool trailing_affinity,
    std::int32_t direction,
    text_caret_stop& result,
    font_error* error = nullptr) noexcept;

bool try_get_text_selection_rectangles(
    std::span<const text_cluster_box> cluster_boxes,
    std::int32_t input_start,
    std::int32_t input_end,
    std::span<text_rectangle> rectangles,
    std::uint32_t& written,
    font_error* error = nullptr) noexcept;

bool try_get_open_type_shape_run_requirements(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    open_type_shape_run_requirements& result,
    font_error* error = nullptr) noexcept;

/* Option-aware requirements include USE diacritic decomposition and its
 * expanded complex-script metadata. Prefer this overload for full shaping. */
bool try_get_open_type_shape_run_requirements(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    open_type_shape_run_requirements& result,
    font_error* error = nullptr) noexcept;

/* Diagnostic equivalent of managed ShapingBufferFlags.Verify. The completed
 * result is preserved while each advertised safe fragment is reshaped into
 * caller-owned glyph storage. Other shaping scratch is reused synchronously;
 * glyph flags are intentionally excluded from reconstruction comparison. */
bool try_verify_open_type_shape_result(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::span<const shaping_glyph> expected,
    std::span<shaping_glyph> fragment_glyph_storage,
    open_type_shape_run_scratch scratch,
    font_error* error = nullptr,
    const open_type_shape_plan* plan = nullptr) noexcept;

/*
 * Allocation-free uniform-run shaping orchestration. The caller supplies the
 * decoded/normalized scalar run, requested OpenType features, expandable glyph
 * storage, lookup scratch, and attachment scratch. Script itemization, bidi,
 * fallback selection, and paragraph layout remain independent reusable stages.
 */
bool try_shape_open_type_run(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const open_type_shape_run_options& options,
    std::span<shaping_glyph> glyph_storage,
    open_type_shape_run_scratch scratch,
    std::uint32_t& glyph_count,
    font_error* error = nullptr,
    const open_type_shape_plan* plan = nullptr) noexcept;

struct sfnt_table_view final {
    open_type_tag tag{};
    std::uint32_t checksum = 0U;
    std::span<const std::byte> bytes{};
};

struct sfnt_header_metrics final {
    std::uint16_t units_per_em = 0U;
    std::int16_t x_min = 0;
    std::int16_t y_min = 0;
    std::int16_t x_max = 0;
    std::int16_t y_max = 0;
    std::int16_t index_to_loc_format = 0;
};

struct sfnt_horizontal_header_metrics final {
    std::int16_t ascender = 0;
    std::int16_t descender = 0;
    std::int16_t line_gap = 0;
    std::uint16_t advance_width_max = 0U;
    std::uint16_t number_of_horizontal_metrics = 0U;
};

struct sfnt_horizontal_glyph_metrics final {
    std::uint16_t advance_width = 0U;
    std::int16_t left_side_bearing = 0;
};

struct sfnt_vertical_header_metrics final {
    std::int16_t ascender = 0;
    std::int16_t descender = 0;
    std::int16_t line_gap = 0;
    std::uint16_t advance_height_max = 0U;
    std::uint16_t number_of_vertical_metrics = 0U;
};

struct sfnt_vertical_glyph_metrics final {
    std::uint16_t advance_height = 0U;
    std::int16_t top_side_bearing = 0;
    bool has_top_side_bearing = false;
};

struct sfnt_glyph_bounds final {
    std::int16_t x_min = 0;
    std::int16_t y_min = 0;
    std::int16_t x_max = 0;
    std::int16_t y_max = 0;
};

struct sfnt_name_ids final {
    static constexpr std::uint16_t family_name = 1U;
    static constexpr std::uint16_t subfamily_name = 2U;
    static constexpr std::uint16_t unique_font_identifier = 3U;
    static constexpr std::uint16_t full_name = 4U;
    static constexpr std::uint16_t version = 5U;
    static constexpr std::uint16_t post_script_name = 6U;
    static constexpr std::uint16_t preferred_family_name = 16U;
    static constexpr std::uint16_t preferred_subfamily_name = 17U;
};

struct sfnt_name_requirements final {
    std::size_t utf8_bytes = 0U;
    std::int32_t score = 0;
    std::uint16_t platform_id = 0U;
    std::uint16_t encoding_id = 0U;
    std::uint16_t language_id = 0U;
};

struct sfnt_face_style final {
    std::uint16_t weight = 400U;
    std::uint16_t width = 5U;
    bool italic = false;
};

struct sfnt_glyph_resident_requirements final {
    std::size_t sbix_bytes = 0U;
    std::size_t font_bytes = 0U;
    std::uint32_t strike_count = 0U;
};

struct sfnt_standalone_requirements final {
    std::size_t font_bytes = 0U;
    std::uint16_t table_scratch_count = 0U;
};

struct sfnt_directory_record final {
    open_type_tag tag{};
    std::uint32_t checksum = 0U;
    std::uint32_t offset = 0U;
    std::uint32_t length = 0U;
};

struct sfnt_glyph_data_view final {
    std::int16_t contour_count = 0;
    std::int16_t x_min = 0;
    std::int16_t y_min = 0;
    std::int16_t x_max = 0;
    std::int16_t y_max = 0;
    std::span<const std::byte> bytes{};

    bool empty() const noexcept {
        return bytes.empty();
    }
};

enum class sfnt_glyph_kind : std::uint8_t {
    empty = 0U,
    simple,
    composite
};

struct sfnt_glyph_decode_requirements final {
    sfnt_glyph_kind kind = sfnt_glyph_kind::empty;
    std::uint16_t contour_count = 0U;
    std::uint32_t point_count = 0U;
    std::uint32_t path_segment_count = 0U;
    std::uint16_t instruction_bytes = 0U;
};

struct sfnt_outline_point final {
    std::int32_t x = 0;
    std::int32_t y = 0;
    std::uint8_t flags = 0U;

    bool on_curve() const noexcept {
        return (flags & 0x01U) != 0U;
    }
};

struct sfnt_composite_glyph_decode_requirements final {
    std::uint32_t component_count = 0U;
    std::uint16_t instruction_bytes = 0U;
};

struct sfnt_composite_component final {
    std::uint16_t flags = 0U;
    std::uint16_t glyph_index = 0U;
    std::int32_t argument1 = 0;
    std::int32_t argument2 = 0;
    float m00 = 1.0F;
    float m01 = 0.0F;
    float m10 = 0.0F;
    float m11 = 1.0F;
};

struct sfnt_expanded_glyph_requirements final {
    std::uint32_t point_count = 0U;
    std::uint32_t path_segment_count = 0U;
    std::uint32_t simple_point_scratch_count = 0U;
    std::uint16_t simple_contour_scratch_count = 0U;
};

/*
 * One fixed-size axis record borrowed from an OpenType fvar table. Values stay
 * in signed 16.16 form so the native port can normalize and cache instances
 * without a float round trip. Name resolution is a separate provider concern.
 */
struct sfnt_variation_axis final {
    open_type_tag tag{};
    std::int32_t minimum_fixed = 0;
    std::int32_t default_fixed = 0;
    std::int32_t maximum_fixed = 0;
    std::uint16_t flags = 0U;
    std::uint16_t name_id = 0U;

    float minimum() const noexcept;
    float default_value() const noexcept;
    float maximum() const noexcept;
    bool hidden() const noexcept;
};

/*
 * Borrowed metadata for an OpenType gvar table and one glyph's tuple-data
 * slice. Header parsing is O(G + A * T) only when the caller requests a glyph
 * offset or shared tuple, for G glyph offsets, A axes, and T shared tuples;
 * the views themselves retain no storage and never allocate.
 */
struct sfnt_gvar_header final {
    std::uint16_t axis_count = 0U;
    std::uint16_t shared_tuple_count = 0U;
    std::uint16_t glyph_count = 0U;
    bool uses_long_offsets = false;
};

struct sfnt_glyph_variation_data_view final {
    std::span<const std::byte> bytes{};
    std::uint16_t tuple_count = 0U;
    std::uint16_t serialized_data_offset = 0U;
    bool has_shared_point_numbers = false;

    bool empty() const noexcept {
        return bytes.empty();
    }
};

struct sfnt_packed_point_requirements final {
    std::uint32_t point_count = 0U;
    std::size_t bytes_consumed = 0U;
    bool all_points = false;
};

struct sfnt_packed_delta_requirements final {
    std::uint32_t delta_count = 0U;
    std::size_t bytes_consumed = 0U;
};

struct sfnt_gvar_tuple_requirements final {
    std::uint16_t tuple_count = 0U;
    std::uint32_t region_coordinate_count = 0U;
};

/*
 * Each header indexes a caller-owned coordinate block laid out as contiguous
 * start[A], peak[A], end[A] F2Dot14 arrays for A variation axes.
 */
struct sfnt_gvar_tuple_header final {
    std::uint32_t region_coordinate_offset = 0U;
    std::uint16_t serialized_data_size = 0U;
    std::uint16_t flags = 0U;

    bool has_private_point_numbers() const noexcept {
        return (flags & 0x2000U) != 0U;
    }
};

class sfnt_gvar_tuple_data final {
public:
    static float calculate_scalar(
        std::span<const std::int16_t> normalized_coordinates,
        std::span<const std::int16_t> region_coordinates) noexcept;
};

/*
 * Allocation-free TrueType IUP interpolation for one tuple's sparse deltas.
 * Validation is transactional; interpolation is O(P) time and O(1) internal
 * storage for P contour points.
 */
class sfnt_gvar_deltas final {
public:
    static bool try_infer_untouched(
        std::span<const progpu_native_point> original_points,
        std::span<const std::uint16_t> contour_end_points,
        std::span<float> x_deltas,
        std::span<float> y_deltas,
        std::span<const std::uint8_t> touched,
        font_error* error = nullptr) noexcept;
};

struct sfnt_simple_glyph_variation_requirements final {
    std::uint16_t tuple_header_count = 0U;
    std::uint32_t region_coordinate_count = 0U;
    std::uint32_t point_number_count = 0U;
    std::uint32_t delta_count = 0U;
    std::uint32_t tuple_point_count = 0U;
};

struct sfnt_simple_glyph_variation_scratch final {
    std::span<sfnt_gvar_tuple_header> tuple_headers{};
    std::span<std::int16_t> region_coordinates{};
    std::span<std::uint32_t> shared_point_numbers{};
    std::span<std::uint32_t> private_point_numbers{};
    std::span<std::int16_t> x_deltas{};
    std::span<std::int16_t> y_deltas{};
    std::span<float> tuple_x{};
    std::span<float> tuple_y{};
    std::span<std::uint8_t> touched{};
};

struct sfnt_composite_glyph_variation_requirements final {
    std::uint16_t tuple_header_count = 0U;
    std::uint32_t region_coordinate_count = 0U;
    std::uint32_t point_number_count = 0U;
    std::uint32_t delta_count = 0U;
};

struct sfnt_composite_glyph_variation_scratch final {
    std::span<sfnt_gvar_tuple_header> tuple_headers{};
    std::span<std::int16_t> region_coordinates{};
    std::span<std::uint32_t> shared_point_numbers{};
    std::span<std::uint32_t> private_point_numbers{};
    std::span<std::int16_t> x_deltas{};
    std::span<std::int16_t> y_deltas{};
};

struct sfnt_glyph_phantom_variation_requirements final {
    std::uint16_t tuple_header_count = 0U;
    std::uint32_t region_coordinate_count = 0U;
    std::uint32_t point_number_count = 0U;
    std::uint32_t delta_count = 0U;
};

struct sfnt_glyph_phantom_variation_scratch final {
    std::span<sfnt_gvar_tuple_header> tuple_headers{};
    std::span<std::int16_t> region_coordinates{};
    std::span<std::uint32_t> shared_point_numbers{};
    std::span<std::uint32_t> private_point_numbers{};
    std::span<std::int16_t> x_deltas{};
    std::span<std::int16_t> y_deltas{};
};

struct sfnt_design_advance_width_requirements final {
    std::uint32_t glyph_variation_item_count = 0U;
    sfnt_glyph_phantom_variation_requirements phantom{};
};

struct sfnt_item_variation_store_view final {
    std::span<const std::byte> bytes{};
    std::size_t store_offset = 0U;
    std::size_t region_list_offset = 0U;
    std::size_t subtable_offsets_offset = 0U;
    std::uint16_t axis_count = 0U;
    std::uint16_t region_count = 0U;
    std::uint16_t subtable_count = 0U;
};

struct sfnt_delta_set_index_map_view final {
    std::span<const std::byte> bytes{};
    std::size_t entries_offset = 0U;
    std::uint32_t entry_count = 0U;
    std::uint8_t entry_size = 0U;
    std::uint8_t inner_index_bits = 0U;
};

struct sfnt_cff_index_view final {
    std::span<const std::byte> bytes{};
    std::size_t offsets_offset = 0U;
    std::size_t data_offset = 0U;
    std::size_t end_offset = 0U;
    std::uint32_t count = 0U;
    std::uint8_t offset_size = 0U;
};

struct sfnt_cff1_top_dictionary final {
    std::uint32_t char_strings_offset = 0U;
    std::uint32_t private_size = 0U;
    std::uint32_t private_offset = 0U;
    std::uint32_t font_dictionary_offset = 0U;
    std::uint32_t fd_select_offset = 0U;
};

struct sfnt_cff_fd_select_view final {
    std::span<const std::byte> bytes{};
    std::size_t records_offset = 0U;
    std::uint32_t glyph_count = 0U;
    std::uint32_t range_count = 0U;
    std::uint32_t font_dictionary_count = 0U;
    std::uint8_t format = 0U;
};

struct sfnt_cff1_font_view final {
    std::span<const std::byte> bytes{};
    sfnt_cff_index_view char_strings{};
    sfnt_cff_index_view global_subroutines{};
    sfnt_cff_index_view default_local_subroutines{};
    sfnt_cff_index_view font_dictionaries{};
    sfnt_cff_fd_select_view fd_select{};
    sfnt_cff1_top_dictionary top_dictionary{};
};

struct sfnt_cff1_outline_requirements final {
    std::uint32_t path_segment_count = 0U;
};

struct sfnt_cff2_top_dictionary final {
    std::uint32_t char_strings_offset = 0U;
    std::uint32_t font_dictionary_offset = 0U;
    std::uint32_t fd_select_offset = 0U;
    std::uint32_t variation_store_offset = 0U;
    double font_matrix_scale = 0.001;
    bool has_font_matrix = false;
};

/*
 * Borrowed CFF2 container state. INDEX objects retain their encoded uint32
 * count/offset arrays, and the optional variation store is bounded to its
 * declared length. Normalized coordinates use the shared F2Dot14 axis order.
 */
struct sfnt_cff2_font_view final {
    std::span<const std::byte> bytes{};
    sfnt_cff_index_view char_strings{};
    sfnt_cff_index_view global_subroutines{};
    sfnt_cff_index_view font_dictionaries{};
    sfnt_cff_fd_select_view fd_select{};
    sfnt_item_variation_store_view variation_store{};
    sfnt_cff2_top_dictionary top_dictionary{};
    std::uint16_t axis_count = 0U;
};

struct sfnt_cff2_outline_requirements final {
    std::uint32_t path_segment_count = 0U;
};

struct sfnt_bitmap_glyph_data_view final {
    std::span<const std::byte> bytes{};
    open_type_tag graphic_type{};
    std::uint16_t pixels_per_em = 0U;
    std::uint16_t pixels_per_inch = 0U;
    std::int16_t origin_offset_x = 0;
    std::int16_t origin_offset_y = 0;
    bool uses_horizontal_metrics = false;
    std::int16_t bearing_x = 0;
    std::int16_t bearing_y = 0;
};

struct sfnt_svg_glyph_document_view final {
    std::span<const std::byte> bytes{};
    std::uint16_t first_glyph = 0U;
    std::uint16_t last_glyph = 0U;
    bool gzip_compressed = false;
};

/*
 * Allocation-free SVG path-data compilation shared by OpenType-SVG glyph
 * lowering and standalone native text consumers. The requirements pass
 * validates the complete path and reports the exact canonical segment count
 * and managed-compatible control/arc bounds. Decode performs the same bounded
 * pass into caller-owned storage and never partially writes on malformed input
 * or a short destination.
 */
struct svg_path_requirements final {
    std::size_t segment_count = 0U;
    float minimum_x = 0.0F;
    float minimum_y = 0.0F;
    float maximum_x = 0.0F;
    float maximum_y = 0.0F;
    std::uint32_t fill_rule = PROGPU_NATIVE_FILL_RULE_NON_ZERO;
};

bool try_get_svg_path_requirements(
    std::string_view path_data,
    svg_path_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_decode_svg_path(
    std::string_view path_data,
    std::span<progpu_native_path_segment> segments,
    svg_path_requirements& result,
    font_error* error = nullptr) noexcept;

/*
 * Canonical OpenType-SVG glyph output. Layers borrow ranges from the decoded
 * segment and brush arrays. Geometry remains in element-local coordinates and
 * carries the exact inherited SVG transform, allowing the retained scene
 * compiler to apply placement without rewriting segment data. Brush points
 * are already expressed in the SVG glyph coordinate system, matching the
 * managed FontColorLayer contract.
 */
struct svg_glyph_layer final {
    std::size_t segment_offset = 0U;
    std::size_t segment_count = 0U;
    float minimum_x = 0.0F;
    float minimum_y = 0.0F;
    float maximum_x = 0.0F;
    float maximum_y = 0.0F;
    progpu_native_affine_2d transform{1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    std::uint32_t brush_index = 0U;
    std::uint32_t fill_rule = PROGPU_NATIVE_FILL_RULE_NON_ZERO;
};

struct svg_glyph_requirements final {
    std::size_t layer_count = 0U;
    std::size_t segment_count = 0U;
    std::size_t brush_count = 0U;
    std::size_t gradient_stop_count = 0U;
};

using svg_brush_record = progpu_native_scene_brush;
using svg_gradient_stop_record = progpu_native_scene_gradient_stop;

bool try_get_svg_glyph_requirements(
    std::string_view xml,
    std::uint16_t glyph_index,
    std::uint16_t units_per_em,
    svg_glyph_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_decode_svg_glyph(
    std::string_view xml,
    std::uint16_t glyph_index,
    std::uint16_t units_per_em,
    std::span<svg_glyph_layer> layers,
    std::span<progpu_native_path_segment> segments,
    std::span<svg_brush_record> brushes,
    std::span<svg_gradient_stop_record> gradient_stops,
    svg_glyph_requirements& result,
    font_error* error = nullptr) noexcept;

bool try_decode_svg_glyph_document(
    const sfnt_svg_glyph_document_view& document,
    std::span<std::byte> output,
    std::size_t& written,
    font_error* error = nullptr) noexcept;

bool try_get_svg_glyph_document_size(
    const sfnt_svg_glyph_document_view& document,
    std::size_t& result,
    font_error* error = nullptr) noexcept;

struct sfnt_color_rgba8 final {
    std::uint8_t red = 255U;
    std::uint8_t green = 255U;
    std::uint8_t blue = 255U;
    std::uint8_t alpha = 255U;
};

struct sfnt_color_glyph_layer final {
    std::uint16_t glyph_index = 0U;
    std::uint16_t palette_entry_index = 0U;
    sfnt_color_rgba8 color{};
    bool uses_foreground_color = false;
};

class sfnt_cff_data final {
public:
    static bool try_read_index(
        std::span<const std::byte> bytes,
        std::size_t& cursor,
        sfnt_cff_index_view& result,
        font_error* error = nullptr) noexcept;
    static bool try_read_cff2_index(
        std::span<const std::byte> bytes,
        std::size_t& cursor,
        sfnt_cff_index_view& result,
        font_error* error = nullptr) noexcept;
    static bool try_get_index_item(
        sfnt_cff_index_view index,
        std::uint32_t item,
        std::span<const std::byte>& result,
        font_error* error = nullptr) noexcept;
    static bool try_read_dictionary_number(
        std::span<const std::byte> bytes,
        std::size_t& cursor,
        std::uint8_t first,
        double& result) noexcept;
    static bool try_get_top_dictionary(
        std::span<const std::byte> bytes,
        sfnt_cff1_top_dictionary& result,
        font_error* error = nullptr) noexcept;
    static bool try_get_cff2_top_dictionary(
        std::span<const std::byte> bytes,
        sfnt_cff2_top_dictionary& result,
        font_error* error = nullptr) noexcept;
    static bool try_read_local_subroutines(
        std::span<const std::byte> bytes,
        std::uint32_t private_offset,
        std::uint32_t private_size,
        sfnt_cff_index_view& result,
        font_error* error = nullptr) noexcept;
    static bool try_read_fd_select(
        std::span<const std::byte> bytes,
        std::uint32_t offset,
        std::uint32_t glyph_count,
        std::uint32_t font_dictionary_count,
        sfnt_cff_fd_select_view& result,
        font_error* error = nullptr) noexcept;
    static bool try_get_font_dictionary(
        sfnt_cff_fd_select_view fd_select,
        std::uint32_t glyph_index,
        std::uint32_t& result,
        font_error* error = nullptr) noexcept;
    static bool try_get_local_subroutines(
        sfnt_cff1_font_view font,
        std::uint32_t glyph_index,
        sfnt_cff_index_view& result,
        font_error* error = nullptr) noexcept;
    static bool try_get_outline_requirements(
        sfnt_cff1_font_view font,
        std::uint32_t glyph_index,
        sfnt_cff1_outline_requirements& result,
        font_error* error = nullptr) noexcept;
    static bool try_decode_outline(
        sfnt_cff1_font_view font,
        std::uint32_t glyph_index,
        std::span<progpu_native_path_segment> segments,
        std::uint32_t& written,
        font_error* error = nullptr) noexcept;
    static bool try_get_outline_requirements(
        sfnt_cff2_font_view font,
        std::uint32_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        sfnt_cff2_outline_requirements& result,
        font_error* error = nullptr) noexcept;
    static bool try_decode_outline(
        sfnt_cff2_font_view font,
        std::uint32_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        std::span<progpu_native_path_segment> segments,
        std::uint32_t& written,
        font_error* error = nullptr) noexcept;
};

class sfnt_item_variation_data final {
public:
    static bool try_get_store(
        std::span<const std::byte> bytes,
        std::size_t store_offset,
        std::uint16_t expected_axis_count,
        sfnt_item_variation_store_view& result,
        font_error* error = nullptr) noexcept;
    static bool try_get_delta(
        sfnt_item_variation_store_view store,
        std::span<const std::int16_t> normalized_coordinates,
        std::uint16_t outer_index,
        std::uint16_t inner_index,
        float& result,
        font_error* error = nullptr) noexcept;
    static bool try_get_region_scalar_count(
        sfnt_item_variation_store_view store,
        std::uint16_t outer_index,
        std::uint16_t& result,
        font_error* error = nullptr) noexcept;
    static bool try_get_region_scalar(
        sfnt_item_variation_store_view store,
        std::span<const std::int16_t> normalized_coordinates,
        std::uint16_t outer_index,
        std::uint16_t region_position,
        float& result,
        font_error* error = nullptr) noexcept;
    static bool try_get_delta_set_index_map(
        std::span<const std::byte> bytes,
        std::size_t map_offset,
        sfnt_delta_set_index_map_view& result,
        font_error* error = nullptr) noexcept;
    static void get_delta_set_index(
        sfnt_delta_set_index_map_view map,
        std::uint32_t item_index,
        std::uint16_t& outer_index,
        std::uint16_t& inner_index) noexcept;
};

/*
 * Exact maximum caller storage for recursive variable TrueType expansion.
 * Measurement is O(G + C) for G reachable glyphs and C components; decoding
 * is O(G + C + T * (A + P + D)) and performs no internal heap allocation.
 * Component offsets reserve only the maximum active recursion path, not the
 * full expanded tree.
 */
struct sfnt_varied_glyph_requirements final {
    sfnt_expanded_glyph_requirements outline{};
    sfnt_simple_glyph_variation_requirements simple_variation{};
    sfnt_composite_glyph_variation_requirements composite_variation{};
    std::uint32_t varied_simple_point_count = 0U;
    std::uint32_t component_offset_count = 0U;
};

struct sfnt_varied_glyph_scratch final {
    std::span<std::uint16_t> simple_contour_end_points{};
    std::span<sfnt_outline_point> simple_points{};
    std::span<progpu_native_point> varied_simple_points{};
    std::span<progpu_native_point> component_offsets{};
    sfnt_simple_glyph_variation_scratch simple_variation{};
    sfnt_composite_glyph_variation_scratch composite_variation{};
};

enum class sfnt_glyph_outline_source : std::uint8_t {
    none = 0U,
    true_type_static,
    true_type_varied,
    cff1,
    cff2
};

struct sfnt_glyph_outline_bounds_requirements final {
    sfnt_glyph_outline_source source = sfnt_glyph_outline_source::none;
    std::uint32_t point_count = 0U;
    std::uint32_t path_segment_count = 0U;
    sfnt_varied_glyph_requirements varied{};
};

struct sfnt_glyph_outline_bounds_scratch final {
    sfnt_varied_glyph_scratch varied{};
    std::span<progpu_native_point> points{};
    std::span<progpu_native_path_segment> path_segments{};
};

struct fallback_mark_positioning_scratch final {
    sfnt_glyph_outline_bounds_scratch outline_bounds{};
    sfnt_glyph_phantom_variation_scratch advance_width{};
};

/*
 * Transactional two-pass decoders for gvar packed point and delta streams.
 * Each pass is O(N) time with O(1) internal storage for N encoded values. The
 * caller owns every output span; insufficient or malformed input writes no
 * partial output.
 */
class sfnt_packed_variation_data final {
public:
    static bool try_get_point_requirements(
        std::span<const std::byte> data,
        sfnt_packed_point_requirements& result,
        font_error* error = nullptr) noexcept;
    static bool try_decode_points(
        std::span<const std::byte> data,
        std::span<std::uint32_t> points,
        std::uint32_t& written,
        std::size_t& bytes_consumed,
        font_error* error = nullptr) noexcept;
    static bool try_get_delta_requirements(
        std::span<const std::byte> data,
        std::uint32_t delta_count,
        sfnt_packed_delta_requirements& result,
        font_error* error = nullptr) noexcept;
    static bool try_decode_deltas(
        std::span<const std::byte> data,
        std::span<std::int16_t> deltas,
        std::uint32_t delta_count,
        std::uint32_t& written,
        std::size_t& bytes_consumed,
        font_error* error = nullptr) noexcept;
};

/*
 * Allocation-free lowering of decoded TrueType contours to the renderer's
 * canonical line/quadratic path ABI. The count pass and write pass are both
 * O(C + P) for C contours and P decoded points with O(1) internal storage.
 */
class sfnt_simple_glyph_path final {
public:
    static bool try_get_segment_count(
        std::span<const std::uint16_t> contour_end_points,
        std::span<const sfnt_outline_point> points,
        std::uint32_t& result,
        font_error* error = nullptr) noexcept;
    static bool try_write_segments(
        std::span<const std::uint16_t> contour_end_points,
        std::span<const sfnt_outline_point> points,
        std::span<progpu_native_path_segment> segments,
        std::uint32_t& written,
        font_error* error = nullptr) noexcept;
    static bool try_write_varied_segments(
        std::span<const std::uint16_t> contour_end_points,
        std::span<const sfnt_outline_point> original_points,
        std::span<const progpu_native_point> varied_points,
        std::span<progpu_native_path_segment> segments,
        std::uint32_t& written,
        font_error* error = nullptr) noexcept;
};

/*
 * Allocation-free borrowed view over one SFNT or TrueType Collection face.
 * The caller owns the byte span and must keep it alive for the view lifetime.
 * Construction and table lookup are O(T) for T directory records with O(1)
 * storage. Character lookup is O(log G) for format 12/13 groups and O(S) for
 * format 4 segments. Simple-glyph decoding is two-pass O(C + P + B) for C
 * contours, P points, and B encoded flag/coordinate bytes: the first call
 * reports exact caller-buffer requirements and the second writes directly to
 * those spans. Composite expansion is O(G + K + P + S) normally and
 * O(D * (G + K + P + S)) worst-case when nested point attachments require
 * bounded child preflight, for visited glyphs G, components K, points P,
 * segments S, and D <= 33. Scratch/output spans are caller-owned; no operation
 * allocates or initializes WebGPU.
 */
class sfnt_font_view final {
public:
    static bool try_create(
        std::span<const std::byte> data,
        std::uint32_t face_index,
        sfnt_font_view& result,
        font_error* error = nullptr) noexcept;

    static bool try_get_face_count(
        std::span<const std::byte> data,
        std::uint32_t& face_count,
        font_error* error = nullptr) noexcept;

    bool try_get_table(
        open_type_tag tag,
        sfnt_table_view& result) const noexcept;
    bool try_get_header_metrics(
        sfnt_header_metrics& result) const noexcept;
    bool try_get_horizontal_header_metrics(
        sfnt_horizontal_header_metrics& result) const noexcept;
    bool try_get_horizontal_glyph_metrics(
        std::uint16_t glyph_index,
        sfnt_horizontal_glyph_metrics& result) const noexcept;
    /* Returns the managed TtfFont.GetAdvanceWidth base/HVAR value in design
     * units. The scratch overload additionally applies raw gvar phantom-point
     * fallback when HVAR does not own advance variation. */
    bool try_get_design_advance_width(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        float& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_design_advance_width_requirements(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        sfnt_design_advance_width_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_design_advance_width(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        float& result,
        sfnt_glyph_phantom_variation_scratch scratch,
        font_error* error = nullptr) const noexcept;
    bool try_get_vertical_header_metrics(
        sfnt_vertical_header_metrics& result) const noexcept;
    bool try_get_vertical_glyph_metrics(
        std::uint16_t glyph_index,
        sfnt_vertical_glyph_metrics& result) const noexcept;
    bool try_get_glyph_bounds(
        std::uint16_t glyph_index,
        sfnt_glyph_bounds& result) const noexcept;
    bool try_get_outline_bounds_requirements(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        sfnt_glyph_outline_bounds_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_outline_bounds(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        sfnt_glyph_outline_bounds_scratch scratch,
        sfnt_glyph_bounds& result,
        bool& has_bounds,
        font_error* error = nullptr) const noexcept;
    bool try_get_design_advance_height(
        std::uint16_t glyph_index,
        std::int32_t& result) const noexcept;
    bool try_get_design_vertical_origin_y(
        std::uint16_t glyph_index,
        std::int32_t& result) const noexcept;
    bool try_get_design_kerning(
        std::uint32_t left_code_point,
        std::uint32_t right_code_point,
        std::int32_t& result) const noexcept;
    /*
     * Selects and decodes the same canonical SFNT name record as the managed
     * ProGPU metadata reader. The requirements pass reports exact caller-owned
     * UTF-8 storage. No string, locale, codec, or heap ownership crosses this
     * boundary; a short output span is left untouched.
     */
    bool try_get_name_requirements(
        std::uint16_t name_id,
        sfnt_name_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_name(
        std::uint16_t name_id,
        std::span<char> utf8,
        std::size_t& written,
        sfnt_name_requirements* requirements = nullptr,
        font_error* error = nullptr) const noexcept;
    bool try_get_face_style(sfnt_face_style& result) const noexcept;
    bool try_get_embedding_rights(std::uint16_t& result) const noexcept;
    bool try_get_glyph_resident_requirements(
        std::uint16_t glyph_index,
        sfnt_glyph_resident_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_create_glyph_resident_sbix(
        std::uint16_t glyph_index,
        std::span<std::byte> output,
        std::size_t& written,
        sfnt_glyph_resident_requirements* requirements = nullptr,
        font_error* error = nullptr) const noexcept;
    bool try_create_glyph_resident_font(
        std::uint16_t glyph_index,
        std::span<std::byte> output,
        std::size_t& written,
        sfnt_glyph_resident_requirements* requirements = nullptr,
        font_error* error = nullptr) const noexcept;
    bool try_get_standalone_requirements(
        sfnt_standalone_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_create_standalone_font(
        std::span<std::byte> output,
        std::span<sfnt_directory_record> table_scratch,
        std::size_t& written,
        sfnt_standalone_requirements* requirements = nullptr,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_count(std::uint16_t& result) const noexcept;
    bool try_get_glyph_data(
        std::uint16_t glyph_index,
        sfnt_glyph_data_view& result) const noexcept;
    bool try_get_glyph_decode_requirements(
        std::uint16_t glyph_index,
        sfnt_glyph_decode_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_simple_glyph(
        std::uint16_t glyph_index,
        std::span<std::uint16_t> contour_end_points,
        std::span<sfnt_outline_point> points,
        font_error* error = nullptr) const noexcept;
    bool try_get_composite_glyph_decode_requirements(
        std::uint16_t glyph_index,
        sfnt_composite_glyph_decode_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_composite_glyph(
        std::uint16_t glyph_index,
        std::span<sfnt_composite_component> components,
        font_error* error = nullptr) const noexcept;
    bool try_get_expanded_glyph_requirements(
        std::uint16_t glyph_index,
        sfnt_expanded_glyph_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_glyph_outline(
        std::uint16_t glyph_index,
        std::span<std::uint16_t> simple_contour_scratch,
        std::span<sfnt_outline_point> simple_point_scratch,
        std::span<progpu_native_point> points,
        std::span<progpu_native_path_segment> segments,
        std::uint32_t& points_written,
        std::uint32_t& segments_written,
        font_error* error = nullptr) const noexcept;
    bool try_get_varied_glyph_requirements(
        std::uint16_t glyph_index,
        sfnt_varied_glyph_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_varied_glyph_outline(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        sfnt_varied_glyph_scratch scratch,
        std::span<progpu_native_point> points,
        std::span<progpu_native_path_segment> segments,
        std::uint32_t& points_written,
        std::uint32_t& segments_written,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_index(
        std::uint32_t code_point,
        std::uint16_t& result) const noexcept;
    bool try_get_variation_glyph(
        std::uint32_t code_point,
        std::uint32_t variation_selector,
        std::uint16_t& result) const noexcept;
    bool try_get_variation_axis_count(
        std::uint16_t& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_variation_axes(
        std::span<sfnt_variation_axis> axes,
        std::uint16_t& written,
        font_error* error = nullptr) const noexcept;
    bool try_get_variation_axis(
        std::uint16_t axis_index,
        sfnt_variation_axis& result,
        font_error* error = nullptr) const noexcept;
    /*
     * Normalize one signed 16.16 user coordinate to F2Dot14 and apply its
     * optional avar segment map. Work is O(A + M) over A axis maps and M map
     * pairs with O(1) storage; no variation instance is retained.
     */
    bool try_normalize_variation_coordinate(
        std::uint16_t axis_index,
        std::int32_t user_fixed,
        std::int16_t& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_gvar_header(
        sfnt_gvar_header& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_variation_data(
        std::uint16_t glyph_index,
        sfnt_glyph_variation_data_view& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_gvar_shared_tuple(
        std::uint16_t tuple_index,
        std::span<std::int16_t> coordinates,
        std::uint16_t& written,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_variation_tuple_requirements(
        std::uint16_t glyph_index,
        sfnt_gvar_tuple_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_glyph_variation_tuple_headers(
        std::uint16_t glyph_index,
        std::span<sfnt_gvar_tuple_header> headers,
        std::span<std::int16_t> region_coordinates,
        std::uint16_t& headers_written,
        std::uint32_t& coordinates_written,
        font_error* error = nullptr) const noexcept;
    bool try_get_simple_glyph_variation_requirements(
        std::uint16_t glyph_index,
        std::uint32_t point_count,
        sfnt_simple_glyph_variation_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_apply_simple_glyph_variations(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        std::span<const std::uint16_t> contour_end_points,
        std::span<const sfnt_outline_point> original_points,
        std::span<progpu_native_point> varied_points,
        sfnt_simple_glyph_variation_scratch scratch,
        font_error* error = nullptr) const noexcept;
    bool try_get_composite_glyph_variation_requirements(
        std::uint16_t glyph_index,
        std::uint32_t component_count,
        sfnt_composite_glyph_variation_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_composite_glyph_variation_offsets(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        std::uint32_t component_count,
        std::span<progpu_native_point> offsets,
        sfnt_composite_glyph_variation_scratch scratch,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_phantom_variation_requirements(
        std::uint16_t glyph_index,
        std::uint32_t item_count,
        sfnt_glyph_phantom_variation_requirements& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_variation_item_count(
        std::uint16_t glyph_index,
        std::uint32_t& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_glyph_phantom_advance_delta(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        std::uint32_t item_count,
        float& result,
        sfnt_glyph_phantom_variation_scratch scratch,
        font_error* error = nullptr) const noexcept;
    bool try_get_horizontal_advance_variation(
        std::uint16_t glyph_index,
        std::span<const std::int16_t> normalized_coordinates,
        float& result,
        bool& uses_hvar,
        font_error* error = nullptr) const noexcept;
    bool try_get_metric_variation(
        open_type_tag metric_tag,
        std::span<const std::int16_t> normalized_coordinates,
        float& result,
        bool& has_metric_record,
        font_error* error = nullptr) const noexcept;
    bool try_get_layout_variation(
        std::uint16_t outer_index,
        std::uint16_t inner_index,
        std::span<const std::int16_t> normalized_coordinates,
        float& result,
        bool& uses_layout_store,
        font_error* error = nullptr) const noexcept;
    bool try_get_cff1_font(
        std::uint16_t expected_glyph_count,
        sfnt_cff1_font_view& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_cff2_font(
        std::uint16_t expected_glyph_count,
        sfnt_cff2_font_view& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_sbix_glyph(
        std::uint16_t glyph_index,
        float target_pixels_per_em,
        sfnt_bitmap_glyph_data_view& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_cbdt_glyph(
        std::uint16_t glyph_index,
        float target_pixels_per_em,
        sfnt_bitmap_glyph_data_view& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_svg_glyph_document(
        std::uint16_t glyph_index,
        sfnt_svg_glyph_document_view& result,
        font_error* error = nullptr) const noexcept;
    bool try_get_colr_layer_count(
        std::uint16_t glyph_index,
        std::uint16_t& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_colr_layers(
        std::uint16_t glyph_index,
        std::uint16_t palette_index,
        std::span<sfnt_color_glyph_layer> layers,
        std::uint16_t& written,
        font_error* error = nullptr) const noexcept;

    std::span<const std::byte> data() const noexcept;
    std::uint32_t face_index() const noexcept;
    std::uint32_t face_offset() const noexcept;
    std::uint16_t table_count() const noexcept;
    bool uses_symbol_character_map() const noexcept;

private:
    std::span<const std::byte> data_{};
    std::span<const std::byte> cmap_format4_{};
    std::span<const std::byte> cmap_format12_{};
    std::span<const std::byte> cmap_format13_{};
    std::span<const std::byte> cmap_format14_{};
    std::uint32_t face_index_ = 0U;
    std::uint32_t face_offset_ = 0U;
    std::size_t directory_offset_ = 0U;
    std::uint16_t table_count_ = 0U;
    bool uses_symbol_character_map_ = false;
};

} // namespace progpu::native::text

#endif
