#ifndef PROGPU_NATIVE_TEXT_HPP
#define PROGPU_NATIVE_TEXT_HPP

#include "progpu_native.h"

#include <cstddef>
#include <cstdint>
#include <span>

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
    invalid_compressed_data
};

struct sfnt_container_requirements final {
    std::size_t normalized_bytes = 0U;
    std::size_t table_scratch_bytes = 0U;
    std::uint16_t table_count = 0U;
    bool requires_normalization = false;
};

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
};

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

enum class shaping_attachment_kind : std::uint8_t {
    none = 0U,
    mark = 1U,
    cursive_horizontal = 2U,
    cursive_vertical = 3U
};

struct shaping_attachment final {
    std::int32_t target = -1;
    shaping_attachment_kind kind = shaping_attachment_kind::none;
    std::uint8_t reserved0 = 0U;
    std::uint8_t reserved1 = 0U;
    std::uint8_t reserved2 = 0U;
};

struct open_type_gpos_apply_options final {
    const open_type_gdef_view* gdef = nullptr;
    shaping_direction direction = shaping_direction::left_to_right;
    std::span<shaping_attachment> attachments{};
};

/*
 * Allocation-free GPOS execution over the same caller-owned shaped glyph
 * records. The executor covers Single, Pair, Cursive, Mark-to-Base,
 * Mark-to-Ligature, and Mark-to-Mark positioning plus Extension wrappers and
 * Context/Chaining Context formats 1-3 with bounded nested lookups.
 * Anchor relationships use caller-owned attachment records and values remain
 * in font units for later run scaling.
 */
bool try_apply_open_type_gpos_lookup(
    const open_type_layout_table_view& gpos,
    std::uint16_t lookup_index,
    std::span<shaping_glyph> glyphs,
    const open_type_gpos_apply_options& options,
    bool& applied,
    font_error* error = nullptr) noexcept;

bool try_resolve_open_type_attachments(
    std::span<shaping_glyph> glyphs,
    std::span<const shaping_attachment> attachments,
    shaping_direction direction,
    std::span<std::uint8_t> state_scratch,
    font_error* error = nullptr) noexcept;

struct open_type_shape_run_options final {
    open_type_tag script{};
    open_type_tag language{};
    shaping_direction direction = shaping_direction::left_to_right;
    std::span<const open_type_tag> requested_features{};
    std::span<const std::int16_t> normalized_coordinates{};
    std::uint32_t alternate_value = 1U;
    bool zero_mark_advances = true;
};

struct open_type_shape_run_scratch final {
    std::span<unicode_grapheme_cluster> grapheme_clusters{};
    std::span<std::uint16_t> gsub_lookups{};
    std::span<std::uint16_t> gpos_lookups{};
    std::span<shaping_attachment> attachments{};
    std::span<std::uint8_t> attachment_states{};
};

struct open_type_shape_run_requirements final {
    std::uint32_t initial_glyph_count = 0U;
    std::uint32_t grapheme_capacity = 0U;
    std::uint32_t gsub_lookup_capacity = 0U;
    std::uint32_t gpos_lookup_capacity = 0U;
};

bool try_get_open_type_shape_run_requirements(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    open_type_shape_run_requirements& result,
    font_error* error = nullptr) noexcept;

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
    font_error* error = nullptr) noexcept;

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
    bool try_get_variation_axis_count(
        std::uint16_t& result,
        font_error* error = nullptr) const noexcept;
    bool try_decode_variation_axes(
        std::span<sfnt_variation_axis> axes,
        std::uint16_t& written,
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
    std::uint32_t face_index_ = 0U;
    std::uint32_t face_offset_ = 0U;
    std::size_t directory_offset_ = 0U;
    std::uint16_t table_count_ = 0U;
    bool uses_symbol_character_map_ = false;
};

} // namespace progpu::native::text

#endif
