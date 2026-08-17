#include "progpu_native.h"
#include "progpu_native_text.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <new>
#include <span>
#include <type_traits>
#include <vector>

// Stable C ABI adapter for the ProGPU-owned native text stack. The shaping
// algorithms remain in Text/Shaping; this file only validates fixed-layout
// wire records, partitions caller-owned scratch, and performs bulk copies at
// the language boundary. No supplied pointer is retained.

struct progpu_native_text_context final {
    std::vector<std::byte> font_bytes{};
    std::vector<std::byte> normalization_bytes{};
    progpu::native::text::sfnt_font_view font{};
    progpu::native::text::unicode_normalization_data normalization{};
    bool has_normalization = false;
    std::vector<std::uint16_t> gsub_lookups{};
    std::vector<std::uint16_t> gpos_lookups{};
    std::vector<progpu::native::text::open_type_lookup_accelerator>
        gsub_accelerators{};
    std::vector<progpu::native::text::open_type_lookup_accelerator>
        gpos_accelerators{};
    std::vector<progpu::native::text::open_type_context_subtable_requirement>
        gsub_context_subtables{};
    std::vector<progpu::native::text::open_type_context_coverage_requirement>
        gsub_context_coverages{};
    std::vector<progpu::native::text::open_type_context_subtable_requirement>
        gpos_context_subtables{};
    std::vector<progpu::native::text::open_type_context_coverage_requirement>
        gpos_context_coverages{};
    progpu::native::text::open_type_shape_plan plan{};
    bool has_plan = false;

    bool try_get_plan(
        const progpu::native::text::open_type_shape_run_options& options,
        progpu::native::text::font_error& error) {
        using namespace progpu::native::text;
        if (has_plan && plan.matches(font, options)) return true;
        open_type_shape_plan_requirements requirements{};
        if (!try_get_open_type_shape_plan_requirements(
                font, options, requirements, &error)) {
            has_plan = false;
            return false;
        }
        gsub_lookups.resize(requirements.gsub_lookup_capacity);
        gpos_lookups.resize(requirements.gpos_lookup_capacity);
        gsub_accelerators.resize(requirements.gsub_accelerator_capacity);
        gpos_accelerators.resize(requirements.gpos_accelerator_capacity);
        gsub_context_subtables.resize(
            requirements.gsub_context_subtable_capacity);
        gsub_context_coverages.resize(
            requirements.gsub_context_coverage_capacity);
        gpos_context_subtables.resize(
            requirements.gpos_context_subtable_capacity);
        gpos_context_coverages.resize(
            requirements.gpos_context_coverage_capacity);
        if (!try_build_open_type_shape_plan(
                font,
                options,
                gsub_lookups,
                gpos_lookups,
                gsub_accelerators,
                gpos_accelerators,
                gsub_context_subtables,
                gsub_context_coverages,
                gpos_context_subtables,
                gpos_context_coverages,
                plan,
                &error)) {
            has_plan = false;
            return false;
        }
        has_plan = true;
        return true;
    }
};

namespace {

using namespace progpu::native::text;

constexpr auto default_script =
    open_type_tag::from_chars('D', 'F', 'L', 'T');
constexpr auto gsub_tag =
    open_type_tag::from_chars('G', 'S', 'U', 'B');
constexpr auto gpos_tag =
    open_type_tag::from_chars('G', 'P', 'O', 'S');
constexpr std::uint32_t default_feature_capacity = 26U;
constexpr std::uint32_t policy_feature_capacity = 32U;
constexpr std::uint32_t allowed_shape_flags =
    PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES;

struct shape_capacities final {
    std::uint32_t glyphs = 0U;
    std::uint32_t graphemes = 0U;
    std::uint32_t gsub_lookups = 0U;
    std::uint32_t gpos_lookups = 0U;
    std::uint32_t script_actions = 0U;
    std::uint32_t complex_values = 0U;
    std::uint32_t complex_indices = 0U;
    std::uint32_t verification_glyphs = 0U;
    std::uint32_t base_features = 0U;
    std::uint32_t explicit_features = 0U;
    std::uint32_t requested_features = 0U;
    std::uint32_t feature_settings = 0U;
    std::size_t scratch_bytes = 0U;
};

class scratch_size_builder final {
public:
    template <typename T>
    bool add(std::size_t count) noexcept {
        static_assert(std::is_trivially_destructible_v<T>);
        const std::size_t alignment = alignof(T);
        const std::size_t padding =
            (alignment - (size_ % alignment)) % alignment;
        if (padding > std::numeric_limits<std::size_t>::max() - size_) {
            return false;
        }
        size_ += padding;
        if (count != 0U &&
            count > (std::numeric_limits<std::size_t>::max() - size_) /
                sizeof(T)) {
            return false;
        }
        size_ += count * sizeof(T);
        return true;
    }

    [[nodiscard]] std::size_t size() const noexcept { return size_; }

private:
    std::size_t size_ = 0U;
};

class scratch_arena final {
public:
    scratch_arena(void* data, std::size_t size) noexcept
        : data_(static_cast<std::byte*>(data)), size_(size) {}

    template <typename T>
    bool take(std::size_t count, std::span<T>& result) noexcept {
        result = {};
        const std::size_t alignment = alignof(T);
        const auto address = reinterpret_cast<std::uintptr_t>(data_ + offset_);
        const std::size_t padding =
            (alignment - (address % alignment)) % alignment;
        if (padding > size_ - std::min(size_, offset_)) return false;
        offset_ += padding;
        if (count > (size_ - std::min(size_, offset_)) / sizeof(T)) {
            return false;
        }
        if (count != 0U) {
            result = std::span<T>{
                reinterpret_cast<T*>(data_ + offset_), count};
        }
        offset_ += count * sizeof(T);
        return true;
    }

    [[nodiscard]] std::size_t used() const noexcept { return offset_; }

private:
    std::byte* data_ = nullptr;
    std::size_t size_ = 0U;
    std::size_t offset_ = 0U;
};

bool has_pointer(const void* pointer, std::uint32_t count) noexcept {
    return count == 0U || pointer != nullptr;
}

template <typename T>
bool has_aligned_pointer(const T* pointer, std::uint32_t count) noexcept {
    return count == 0U ||
        (pointer != nullptr &&
            reinterpret_cast<std::uintptr_t>(pointer) % alignof(T) == 0U);
}

bool valid_scalar(std::uint32_t value) noexcept {
    return value <= 0x10FFFFU && (value < 0xD800U || value > 0xDFFFU);
}

bool valid_tag(std::uint32_t value) noexcept {
    if (value == 0U) return true;
    for (std::uint32_t shift = 0U; shift <= 24U; shift += 8U) {
        const auto character = static_cast<std::uint8_t>(value >> shift);
        if (character < 0x20U || character > 0x7EU) return false;
    }
    return true;
}

bool valid_wire_scalars(
    const progpu_native_text_scalar* values,
    std::uint32_t count) noexcept {
    if (!has_aligned_pointer(values, count)) return false;
    for (std::uint32_t index = 0U; index < count; ++index) {
        if (!valid_scalar(values[index].code_point) ||
            values[index].input_length == 0U ||
            values[index].reserved != 0U) {
            return false;
        }
    }
    return true;
}

bool valid_request(
    const progpu_native_text_shape_request* request,
    bool require_font = true) noexcept {
    if (request == nullptr ||
        request->struct_size < sizeof(progpu_native_text_shape_request) ||
        request->abi_version != PROGPU_NATIVE_ABI_VERSION ||
        (require_font &&
            (request->font_data == nullptr || request->font_size == 0U)) ||
        (!require_font &&
            ((request->font_data == nullptr) != (request->font_size == 0U))) ||
        request->flags & ~allowed_shape_flags ||
        request->reserved0 != 0U || request->reserved1 != 0U ||
        request->direction > PROGPU_NATIVE_TEXT_DIRECTION_BOTTOM_TO_TOP ||
        request->cluster_level > PROGPU_NATIVE_TEXT_CLUSTER_GRAPHEMES ||
        request->buffer_flags > 0xFFU ||
        !valid_tag(request->unicode_script) || !valid_tag(request->language) ||
        !valid_wire_scalars(request->input, request->input_count) ||
        !valid_wire_scalars(
            request->pre_context, request->pre_context_count) ||
        !valid_wire_scalars(
            request->post_context, request->post_context_count) ||
        !has_aligned_pointer(request->features, request->feature_count) ||
        !has_aligned_pointer(
            request->normalized_coordinates,
            request->normalized_coordinate_count) ||
        !has_pointer(
            request->normalization_data,
            static_cast<std::uint32_t>(std::min<std::size_t>(
                request->normalization_data_size,
                std::numeric_limits<std::uint32_t>::max())))) {
        return false;
    }
    if ((request->normalization_data == nullptr) !=
        (request->normalization_data_size == 0U)) {
        return false;
    }
    const std::uint32_t contradictory =
        PROGPU_NATIVE_TEXT_BUFFER_PRESERVE_DEFAULT_IGNORABLES |
        PROGPU_NATIVE_TEXT_BUFFER_REMOVE_DEFAULT_IGNORABLES;
    if ((request->buffer_flags & contradictory) == contradictory) {
        return false;
    }
    for (std::uint32_t index = 0U; index < request->feature_count; ++index) {
        const auto& feature = request->features[index];
        if (!valid_tag(feature.tag) || feature.tag == 0U ||
            feature.start > feature.end) {
            return false;
        }
    }
    return true;
}

open_type_tag infer_script(
    const progpu_native_text_shape_request& request) noexcept {
    if (request.unicode_script != 0U &&
        request.unicode_script != default_script.value) {
        return open_type_tag{request.unicode_script};
    }
    for (std::uint32_t index = 0U; index < request.input_count; ++index) {
        const auto script = get_unicode_script(request.input[index].code_point);
        if (script != default_script) return script;
    }
    return default_script;
}

progpu_native_status status_from_error(font_error error) noexcept {
    switch (error) {
        case font_error::none:
            return PROGPU_NATIVE_STATUS_SUCCESS;
        case font_error::unsupported_container:
            return PROGPU_NATIVE_STATUS_UNSUPPORTED;
        case font_error::invalid_argument:
        case font_error::invalid_collection:
        case font_error::invalid_face:
        case font_error::truncated_directory:
        case font_error::invalid_glyph:
        case font_error::insufficient_buffer:
        case font_error::invalid_container:
        case font_error::invalid_compressed_data:
        case font_error::verification_failed:
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
}

bool try_get_lookup_count(
    const sfnt_font_view& font,
    open_type_tag tag,
    std::uint32_t& result,
    font_error& error) noexcept {
    result = 0U;
    sfnt_table_view table{};
    if (!font.try_get_table(tag, table)) return true;
    open_type_layout_table_view layout{};
    if (!open_type_layout_table_view::try_create(
            table.bytes, layout, &error)) {
        return false;
    }
    result = layout.lookup_count();
    return true;
}

bool try_add_capacity(
    std::uint32_t left,
    std::uint32_t right,
    std::uint32_t& result) noexcept {
    const auto sum = static_cast<std::uint64_t>(left) + right;
    if (sum > std::numeric_limits<std::uint32_t>::max()) return false;
    result = static_cast<std::uint32_t>(sum);
    return true;
}

bool try_build_capacities(
    const progpu_native_text_shape_request& request,
    const sfnt_font_view& font,
    const unicode_normalization_data* normalization,
    shape_capacities& result,
    font_error& error) noexcept {
    result = {};
    open_type_shaping_route route{};
    if (!try_resolve_open_type_shaping_route(
            font,
            infer_script(request),
            static_cast<shaping_direction>(request.direction),
            route,
            &error)) {
        return false;
    }
    if ((route.complex_script == open_type_complex_script::indic ||
            route.complex_script == open_type_complex_script::use) &&
        normalization == nullptr) {
        error = font_error::invalid_argument;
        return false;
    }
    if (request.input_count > std::numeric_limits<std::uint32_t>::max() / 3U) {
        error = font_error::invalid_argument;
        return false;
    }
    std::uint64_t decomposed_count = request.input_count;
    if (normalization != nullptr) {
        decomposed_count = 0U;
        for (std::uint32_t index = 0U; index < request.input_count; ++index) {
            std::span<const std::byte> decomposition{};
            decomposed_count += normalization->try_get_decomposition(
                    request.input[index].code_point, decomposition) &&
                    !decomposition.empty()
                ? decomposition.size() / 4U
                : 1U;
            if (decomposed_count > std::numeric_limits<std::uint32_t>::max()) {
                error = font_error::invalid_argument;
                return false;
            }
        }
    }
    const std::uint64_t expanded_count = decomposed_count + request.input_count;
    const std::uint64_t base_capacity =
        static_cast<std::uint64_t>(request.input_count) * 3U;
    const std::uint64_t glyph_capacity =
        std::max(base_capacity, expanded_count);
    if (glyph_capacity > std::numeric_limits<std::uint32_t>::max()) {
        error = font_error::invalid_argument;
        return false;
    }
    result.glyphs = static_cast<std::uint32_t>(glyph_capacity);
    result.graphemes = request.input_count;
    result.script_actions = request.input_count;
    result.complex_values = route.complex_script == open_type_complex_script::none
        ? 0U
        : result.glyphs;
    result.complex_indices = route.complex_script == open_type_complex_script::use
        ? result.glyphs + 1U
        : 0U;
    const bool verify =
        (request.buffer_flags & PROGPU_NATIVE_TEXT_BUFFER_VERIFY) != 0U &&
        request.cluster_level <=
            PROGPU_NATIVE_TEXT_CLUSTER_MONOTONE_CHARACTERS;
    result.verification_glyphs = verify ? result.glyphs : 0U;
    if (!try_get_lookup_count(font, gsub_tag, result.gsub_lookups, error) ||
        !try_get_lookup_count(font, gpos_tag, result.gpos_lookups, error) ||
        !try_add_capacity(
            default_feature_capacity,
            request.feature_count,
            result.base_features) ||
        !try_add_capacity(
            result.base_features,
            policy_feature_capacity,
            result.requested_features) ||
        !try_add_capacity(
            result.requested_features,
            request.feature_count,
            result.feature_settings)) {
        if (error == font_error::none) error = font_error::invalid_argument;
        return false;
    }
    result.explicit_features = request.feature_count;

    scratch_size_builder size{};
    if (!size.add<unicode_scalar>(request.input_count) ||
        !size.add<unicode_scalar>(request.pre_context_count) ||
        !size.add<unicode_scalar>(request.post_context_count) ||
        !size.add<shaping_feature>(request.feature_count) ||
        !size.add<open_type_feature_setting>(result.base_features) ||
        !size.add<open_type_tag>(result.explicit_features) ||
        !size.add<open_type_tag>(result.requested_features) ||
        !size.add<shaping_feature>(result.feature_settings) ||
        !size.add<shaping_glyph>(result.glyphs) ||
        !size.add<unicode_grapheme_cluster>(result.graphemes) ||
        !size.add<std::uint16_t>(result.gsub_lookups) ||
        !size.add<std::uint16_t>(result.gpos_lookups) ||
        !size.add<shaping_attachment>(result.glyphs) ||
        !size.add<std::uint8_t>(result.glyphs) ||
        !size.add<open_type_arabic_action>(result.script_actions) ||
        !size.add<shaping_glyph_flags>(result.script_actions) ||
        !size.add<std::uint8_t>(result.complex_values) ||
        !size.add<std::uint8_t>(result.complex_values) ||
        !size.add<std::uint32_t>(result.complex_indices) ||
        !size.add<arabic_stretch_run>(result.glyphs) ||
        !size.add<shaping_glyph>(result.verification_glyphs)) {
        error = font_error::invalid_argument;
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        error = font_error::invalid_argument;
        return false;
    }
    result.scratch_bytes = size.size() + alignof(std::max_align_t) - 1U;
    error = font_error::none;
    return true;
}

unicode_scalar convert_scalar(const progpu_native_text_scalar& source) noexcept {
    return unicode_scalar{
        source.code_point,
        source.input_index,
        source.input_length,
        get_unicode_canonical_combining_class(source.code_point),
        0U,
        get_unicode_script(source.code_point)};
}

void copy_scalars(
    const progpu_native_text_scalar* source,
    std::span<unicode_scalar> destination) noexcept {
    for (std::size_t index = 0U; index < destination.size(); ++index) {
        destination[index] = convert_scalar(source[index]);
    }
}

progpu_native_status shape_core(
    const progpu_native_text_shape_request& request,
    const sfnt_font_view& font,
    const unicode_normalization_data* normalization,
    const shape_capacities& capacities,
    progpu_native_text_shaping_glyph* glyphs,
    std::uint32_t glyph_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_shape_result& result,
    progpu_native_text_context* context) {
    if ((capacities.glyphs != 0U && glyphs == nullptr) ||
        glyph_capacity < capacities.glyphs ||
        (capacities.scratch_bytes != 0U && scratch == nullptr) ||
        scratch_size < capacities.scratch_bytes) {
        result.error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    scratch_arena arena{scratch, scratch_size};
    std::span<unicode_scalar> input{};
    std::span<unicode_scalar> pre_context{};
    std::span<unicode_scalar> post_context{};
    std::span<shaping_feature> features{};
    std::span<open_type_feature_setting> base_features{};
    std::span<open_type_tag> explicit_features{};
    std::span<open_type_tag> requested_features{};
    std::span<shaping_feature> feature_settings{};
    std::span<shaping_glyph> native_glyphs{};
    std::span<unicode_grapheme_cluster> graphemes{};
    std::span<std::uint16_t> gsub_lookups{};
    std::span<std::uint16_t> gpos_lookups{};
    std::span<shaping_attachment> attachments{};
    std::span<std::uint8_t> attachment_states{};
    std::span<open_type_arabic_action> arabic_actions{};
    std::span<shaping_glyph_flags> arabic_flags{};
    std::span<std::uint8_t> script_categories{};
    std::span<std::uint8_t> script_syllables{};
    std::span<std::uint32_t> script_indices{};
    std::span<arabic_stretch_run> arabic_stretch_runs{};
    std::span<shaping_glyph> verification_glyphs{};
    if (!arena.take(request.input_count, input) ||
        !arena.take(request.pre_context_count, pre_context) ||
        !arena.take(request.post_context_count, post_context) ||
        !arena.take(request.feature_count, features) ||
        !arena.take(capacities.base_features, base_features) ||
        !arena.take(capacities.explicit_features, explicit_features) ||
        !arena.take(capacities.requested_features, requested_features) ||
        !arena.take(capacities.feature_settings, feature_settings) ||
        !arena.take(capacities.glyphs, native_glyphs) ||
        !arena.take(capacities.graphemes, graphemes) ||
        !arena.take(capacities.gsub_lookups, gsub_lookups) ||
        !arena.take(capacities.gpos_lookups, gpos_lookups) ||
        !arena.take(capacities.glyphs, attachments) ||
        !arena.take(capacities.glyphs, attachment_states) ||
        !arena.take(capacities.script_actions, arabic_actions) ||
        !arena.take(capacities.script_actions, arabic_flags) ||
        !arena.take(capacities.complex_values, script_categories) ||
        !arena.take(capacities.complex_values, script_syllables) ||
        !arena.take(capacities.complex_indices, script_indices) ||
        !arena.take(capacities.glyphs, arabic_stretch_runs) ||
        !arena.take(capacities.verification_glyphs, verification_glyphs)) {
        result.error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    copy_scalars(request.input, input);
    copy_scalars(request.pre_context, pre_context);
    copy_scalars(request.post_context, post_context);
    for (std::size_t index = 0U; index < features.size(); ++index) {
        const auto& source = request.features[index];
        features[index] = shaping_feature{
            open_type_tag{source.tag}, source.value, source.start, source.end};
    }

    const open_type_shape_configuration_request configuration_request{
        open_type_tag{request.unicode_script},
        {},
        static_cast<shaping_direction>(request.direction),
        features,
        std::span<const std::int16_t>{
            request.normalized_coordinates,
            request.normalized_coordinate_count},
        request.alternate_value,
        (request.flags & PROGPU_NATIVE_TEXT_SHAPE_ZERO_MARK_ADVANCES) != 0U,
        static_cast<shaping_cluster_level>(request.cluster_level),
        static_cast<shaping_buffer_flags>(request.buffer_flags),
        normalization,
        pre_context,
        post_context,
        open_type_tag{request.language}};
    open_type_shape_configuration configuration{};
    font_error error = font_error::none;
    if (!try_prepare_open_type_shape_configuration(
            font,
            input,
            configuration_request,
            base_features,
            explicit_features,
            requested_features,
            feature_settings,
            configuration,
            &error)) {
        result.error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    const open_type_shape_plan* plan = nullptr;
    if (context != nullptr) {
        if (!context->try_get_plan(configuration.options, error)) {
            result.error_code = static_cast<std::uint32_t>(error);
            return status_from_error(error);
        }
        plan = &context->plan;
    }

    open_type_shape_verification_scratch verification{verification_glyphs};
    open_type_shape_run_scratch shaping_scratch{
        graphemes,
        gsub_lookups,
        gpos_lookups,
        attachments,
        attachment_states,
        arabic_actions,
        script_categories,
        script_syllables,
        script_indices,
        arabic_stretch_runs,
        nullptr,
        verification_glyphs.empty() ? nullptr : &verification,
        arabic_flags};
    std::uint32_t written = 0U;
    if (!try_shape_open_type_run(
            font,
            input,
            configuration.options,
            native_glyphs,
            shaping_scratch,
            written,
            &error,
            plan)) {
        result.error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    for (std::uint32_t index = 0U; index < written; ++index) {
        const auto& source = native_glyphs[index];
        glyphs[index] = progpu_native_text_shaping_glyph{
            source.glyph_id,
            source.code_point,
            source.cluster,
            static_cast<std::uint32_t>(source.flags),
            source.advance_x,
            source.advance_y,
            source.offset_x,
            source.offset_y};
    }
    result.glyph_count = written;
    result.error_code = static_cast<std::uint32_t>(font_error::none);
    result.scratch_bytes_used = arena.used();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

} // namespace

extern "C" {

progpu_native_status progpu_native_text_get_shape_requirements(
    const progpu_native_text_shape_request* request,
    progpu_native_text_shape_requirements* requirements) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_shape_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (!valid_request(request)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    font_error error = font_error::none;
    const auto font_bytes = std::span<const std::byte>{
        reinterpret_cast<const std::byte*>(request->font_data),
        request->font_size};
    sfnt_font_view font{};
    if (!sfnt_font_view::try_create(
            font_bytes, request->face_index, font, &error)) {
        requirements->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    unicode_normalization_data normalization{};
    const unicode_normalization_data* normalization_pointer = nullptr;
    if (request->normalization_data_size != 0U) {
        unicode_error unicode_result = unicode_error::none;
        if (!unicode_normalization_data::try_create(
                std::span<const std::byte>{
                    reinterpret_cast<const std::byte*>(
                        request->normalization_data),
                    request->normalization_data_size},
                normalization,
                &unicode_result)) {
            requirements->error_code =
                static_cast<std::uint32_t>(font_error::invalid_argument);
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        }
        normalization_pointer = &normalization;
    }
    shape_capacities capacities{};
    if (!try_build_capacities(
            *request, font, normalization_pointer, capacities, error)) {
        requirements->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    requirements->glyph_capacity = capacities.glyphs;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = capacities.scratch_bytes;
    requirements->error_code = static_cast<std::uint32_t>(font_error::none);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_context_create(
    std::uint32_t abi_version,
    const std::uint8_t* font_data,
    std::size_t font_size,
    std::uint32_t face_index,
    const std::uint8_t* normalization_data,
    std::size_t normalization_data_size,
    progpu_native_text_context** context) {
    if (context == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *context = nullptr;
    if (abi_version != PROGPU_NATIVE_ABI_VERSION || font_data == nullptr ||
        font_size == 0U ||
        ((normalization_data == nullptr) !=
            (normalization_data_size == 0U))) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    try {
        auto* result = new progpu_native_text_context{};
        result->font_bytes.assign(
            reinterpret_cast<const std::byte*>(font_data),
            reinterpret_cast<const std::byte*>(font_data) + font_size);
        font_error error = font_error::none;
        if (!sfnt_font_view::try_create(
                result->font_bytes, face_index, result->font, &error)) {
            delete result;
            return status_from_error(error);
        }
        if (normalization_data_size != 0U) {
            result->normalization_bytes.assign(
                reinterpret_cast<const std::byte*>(normalization_data),
                reinterpret_cast<const std::byte*>(normalization_data) +
                    normalization_data_size);
            unicode_error unicode_result = unicode_error::none;
            if (!unicode_normalization_data::try_create(
                    result->normalization_bytes,
                    result->normalization,
                    &unicode_result)) {
                delete result;
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
            result->has_normalization = true;
        }
        *context = result;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

void progpu_native_text_context_destroy(progpu_native_text_context* context) {
    delete context;
}

progpu_native_status progpu_native_text_context_get_shape_requirements(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* request,
    progpu_native_text_shape_requirements* requirements) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_shape_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (context == nullptr || !valid_request(request, false)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    font_error error = font_error::none;
    shape_capacities capacities{};
    if (!try_build_capacities(
            *request,
            context->font,
            context->has_normalization ? &context->normalization : nullptr,
            capacities,
            error)) {
        requirements->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    requirements->glyph_capacity = capacities.glyphs;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = capacities.scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_context_shape(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* request,
    progpu_native_text_shaping_glyph* glyphs,
    std::uint32_t glyph_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_shape_result* result) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_shape_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (context == nullptr || !valid_request(request, false)) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    font_error error = font_error::none;
    shape_capacities capacities{};
    if (!try_build_capacities(
            *request,
            context->font,
            context->has_normalization ? &context->normalization : nullptr,
            capacities,
            error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    try {
        return shape_core(
            *request,
            context->font,
            context->has_normalization ? &context->normalization : nullptr,
            capacities,
            glyphs,
            glyph_capacity,
            scratch,
            scratch_size,
            *result,
            context);
    } catch (const std::bad_alloc&) {
        result->error_code = static_cast<std::uint32_t>(error);
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        result->error_code = static_cast<std::uint32_t>(error);
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

progpu_native_status progpu_native_text_shape(
    const progpu_native_text_shape_request* request,
    progpu_native_text_shaping_glyph* glyphs,
    std::uint32_t glyph_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_shape_result* result) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_shape_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (!valid_request(request)) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    font_error error = font_error::none;
    const auto font_bytes = std::span<const std::byte>{
        reinterpret_cast<const std::byte*>(request->font_data),
        request->font_size};
    sfnt_font_view font{};
    if (!sfnt_font_view::try_create(
            font_bytes, request->face_index, font, &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    unicode_normalization_data normalization{};
    const unicode_normalization_data* normalization_pointer = nullptr;
    if (request->normalization_data_size != 0U) {
        unicode_error unicode_result = unicode_error::none;
        if (!unicode_normalization_data::try_create(
                std::span<const std::byte>{
                    reinterpret_cast<const std::byte*>(
                        request->normalization_data),
                    request->normalization_data_size},
                normalization,
                &unicode_result)) {
            result->error_code =
                static_cast<std::uint32_t>(font_error::invalid_argument);
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        }
        normalization_pointer = &normalization;
    }
    shape_capacities capacities{};
    if (!try_build_capacities(
            *request, font, normalization_pointer, capacities, error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    return shape_core(
        *request,
        font,
        normalization_pointer,
        capacities,
        glyphs,
        glyph_capacity,
        scratch,
        scratch_size,
        *result,
        nullptr);
}

} // extern "C"
