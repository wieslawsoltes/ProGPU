#include "progpu_native.h"
#include "progpu_native_text.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cmath>
#include <limits>
#include <new>
#include <span>
#include <type_traits>
#include <utility>
#include <vector>

// Stable C ABI adapter for the ProGPU-owned native text stack. The shaping
// algorithms remain in Text/Shaping; this file only validates fixed-layout
// wire records, partitions caller-owned scratch, and performs bulk copies at
// the language boundary. No supplied pointer is retained.

struct progpu_native_text_owned_font final {
    std::vector<std::byte> bytes{};
    progpu::native::text::sfnt_font_view font{};
    std::uint64_t identity = 0U;
};

struct progpu_native_text_plan_entry final {
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
    std::uint64_t last_used = 0U;
    bool valid = false;
};

struct progpu_native_text_context final {
    static constexpr std::size_t plan_capacity = 16U;

    std::vector<std::byte> font_bytes{};
    std::vector<std::byte> normalization_bytes{};
    progpu::native::text::sfnt_font_view font{};
    progpu::native::text::unicode_normalization_data normalization{};
    bool has_normalization = false;
    std::vector<progpu_native_text_owned_font> fallback_fonts{};
    std::vector<progpu_native_text_plan_entry> plans{};
    std::uint64_t plan_clock = 0U;
    std::uint32_t plan_build_count = 0U;

    bool try_get_plan(
        const progpu::native::text::sfnt_font_view& selected_font,
        const progpu::native::text::open_type_shape_run_options& options,
        const progpu::native::text::open_type_shape_plan*& result,
        progpu::native::text::font_error& error) {
        using namespace progpu::native::text;
        result = nullptr;
        ++plan_clock;
        if (plan_clock == 0U) {
            plan_clock = 1U;
            for (auto& entry : plans) entry.last_used = 0U;
        }
        for (auto& entry : plans) {
            if (!entry.valid || !entry.plan.matches(selected_font, options)) {
                continue;
            }
            entry.last_used = plan_clock;
            result = &entry.plan;
            error = font_error::none;
            return true;
        }
        open_type_shape_plan_requirements requirements{};
        if (!try_get_open_type_shape_plan_requirements(
                selected_font, options, requirements, &error)) {
            return false;
        }
        progpu_native_text_plan_entry* entry = nullptr;
        if (plans.size() < plan_capacity) {
            plans.emplace_back();
            entry = &plans.back();
        } else {
            entry = &*std::min_element(
                plans.begin(),
                plans.end(),
                [](const auto& left, const auto& right) noexcept {
                    return left.last_used < right.last_used;
                });
        }
        entry->valid = false;
        entry->gsub_lookups.resize(requirements.gsub_lookup_capacity);
        entry->gpos_lookups.resize(requirements.gpos_lookup_capacity);
        entry->gsub_accelerators.resize(requirements.gsub_accelerator_capacity);
        entry->gpos_accelerators.resize(requirements.gpos_accelerator_capacity);
        entry->gsub_context_subtables.resize(
            requirements.gsub_context_subtable_capacity);
        entry->gsub_context_coverages.resize(
            requirements.gsub_context_coverage_capacity);
        entry->gpos_context_subtables.resize(
            requirements.gpos_context_subtable_capacity);
        entry->gpos_context_coverages.resize(
            requirements.gpos_context_coverage_capacity);
        if (!try_build_open_type_shape_plan(
                selected_font,
                options,
                entry->gsub_lookups,
                entry->gpos_lookups,
                entry->gsub_accelerators,
                entry->gpos_accelerators,
                entry->gsub_context_subtables,
                entry->gsub_context_coverages,
                entry->gpos_context_subtables,
                entry->gpos_context_coverages,
                entry->plan,
                &error)) {
            return false;
        }
        entry->valid = true;
        entry->last_used = plan_clock;
        if (plan_build_count != std::numeric_limits<std::uint32_t>::max()) {
            ++plan_build_count;
        }
        result = &entry->plan;
        return true;
    }

    std::size_t font_count() const noexcept {
        return fallback_fonts.size() + 1U;
    }

    const progpu::native::text::sfnt_font_view* font_at(
        std::size_t index) const noexcept {
        if (index == 0U) return &font;
        const std::size_t fallback = index - 1U;
        return fallback < fallback_fonts.size()
            ? &fallback_fonts[fallback].font
            : nullptr;
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

std::int32_t negate_managed_metric(std::int32_t value) noexcept {
    // C# unchecked integer negation preserves Int32.MinValue. Avoid signed
    // overflow while keeping the stable native boundary bit-identical.
    return value == std::numeric_limits<std::int32_t>::min()
        ? value
        : -value;
}

struct shape_capacities final {
    std::uint32_t glyphs = 0U;
    std::uint32_t graphemes = 0U;
    std::uint32_t gsub_lookups = 0U;
    std::uint32_t gpos_lookups = 0U;
    std::uint32_t script_actions = 0U;
    std::uint32_t complex_values = 0U;
    std::uint32_t complex_indices = 0U;
    std::uint32_t verification_glyphs = 0U;
    std::uint32_t normalization_scalars = 0U;
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

bool try_resolve_normalization_data(
    const progpu_native_text_shape_request& request,
    unicode_normalization_data& storage,
    const unicode_normalization_data*& result) noexcept {
    result = get_default_unicode_normalization_data();
    if (request.normalization_data_size == 0U) {
        return result != nullptr;
    }
    unicode_error error = unicode_error::none;
    if (!unicode_normalization_data::try_create(
            std::span<const std::byte>{
                reinterpret_cast<const std::byte*>(
                    request.normalization_data),
                request.normalization_data_size},
            storage,
            &error)) {
        result = nullptr;
        return false;
    }
    result = &storage;
    return true;
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

bool try_compute_shape_scratch_bytes(
    const progpu_native_text_shape_request& request,
    shape_capacities& result,
    font_error& error) noexcept {
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
        !size.add<shaping_glyph>(result.verification_glyphs) ||
        !size.add<unicode_scalar>(result.normalization_scalars)) {
        error = font_error::invalid_argument;
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        error = font_error::invalid_argument;
        return false;
    }
    result.scratch_bytes = size.size() + alignof(std::max_align_t) - 1U;
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
    result.normalization_scalars = static_cast<std::uint32_t>(decomposed_count);
    result.graphemes = request.input_count;
    result.script_actions = std::max(
        request.input_count, result.normalization_scalars);
    result.complex_values = route.complex_script == open_type_complex_script::none
        ? 0U
        : result.glyphs;
    result.complex_indices =
        (route.complex_script == open_type_complex_script::indic ||
            route.complex_script == open_type_complex_script::use)
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

    if (!try_compute_shape_scratch_bytes(request, result, error)) return false;
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

bool valid_layout_request(
    const progpu_native_text_layout_request* request) noexcept {
    if (request == nullptr ||
        request->struct_size < sizeof(progpu_native_text_layout_request) ||
        request->abi_version != PROGPU_NATIVE_ABI_VERSION ||
        request->glyph_count != request->break_count ||
        !has_aligned_pointer(request->glyphs, request->glyph_count) ||
        !has_pointer(request->breaks_after, request->break_count) ||
        !std::isfinite(request->scale) || request->scale <= 0.0F ||
        !std::isfinite(request->maximum_width) ||
        request->maximum_width < 0.0F ||
        !std::isfinite(request->line_height) || request->line_height < 0.0F ||
        !std::isfinite(request->ellipsis_advance) ||
        request->ellipsis_advance < 0.0F ||
        (request->direction != PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT &&
            request->direction !=
                PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT) ||
        request->trimming > PROGPU_NATIVE_TEXT_TRIMMING_WORD_ELLIPSIS ||
        request->alignment > PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY ||
        request->reserved != 0U) {
        return false;
    }
    for (std::uint32_t index = 0U; index < request->glyph_count; ++index) {
        if (request->glyphs[index].flags > 0x07U ||
            request->breaks_after[index] >
                PROGPU_NATIVE_TEXT_LINE_BREAK_MANDATORY) {
            return false;
        }
    }
    return true;
}

bool valid_vertical_layout_request(
    const progpu_native_text_layout_request* request) noexcept {
    if (request == nullptr ||
        request->struct_size < sizeof(progpu_native_text_layout_request) ||
        request->abi_version != PROGPU_NATIVE_ABI_VERSION ||
        request->glyph_count != request->break_count ||
        !has_aligned_pointer(request->glyphs, request->glyph_count) ||
        !has_pointer(request->breaks_after, request->break_count) ||
        !std::isfinite(request->scale) || request->scale <= 0.0F ||
        !std::isfinite(request->maximum_width) ||
        request->maximum_width < 0.0F ||
        !std::isfinite(request->line_height) || request->line_height < 0.0F ||
        request->direction < PROGPU_NATIVE_TEXT_DIRECTION_TOP_TO_BOTTOM ||
        request->direction > PROGPU_NATIVE_TEXT_DIRECTION_BOTTOM_TO_TOP ||
        request->trimming != PROGPU_NATIVE_TEXT_TRIMMING_NONE ||
        request->alignment > PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY ||
        request->ellipsis_glyph_id != 0U ||
        request->ellipsis_advance != 0.0F || request->reserved != 0U) {
        return false;
    }
    for (std::uint32_t index = 0U; index < request->glyph_count; ++index) {
        if (request->glyphs[index].flags > 0x07U ||
            request->breaks_after[index] >
                PROGPU_NATIVE_TEXT_LINE_BREAK_MANDATORY) {
            return false;
        }
    }
    return true;
}

bool try_get_layout_scratch_bytes(
    std::uint32_t glyph_count,
    std::size_t& result) noexcept {
    if (glyph_count == 0U) {
        result = 0U;
        return true;
    }
    scratch_size_builder size{};
    if (!size.add<shaping_glyph>(glyph_count) ||
        !size.add<text_line_break_kind>(glyph_count) ||
        !size.add<shaping_glyph>(glyph_count) ||
        !size.add<positioned_text_glyph>(glyph_count) ||
        !size.add<positioned_text_line>(glyph_count)) {
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        return false;
    }
    result = size.size() + alignof(std::max_align_t) - 1U;
    return true;
}

bool try_get_vertical_layout_scratch_bytes(
    std::uint32_t glyph_count,
    std::size_t& result) noexcept {
    if (glyph_count == 0U) {
        result = 0U;
        return true;
    }
    scratch_size_builder size{};
    if (!size.add<shaping_glyph>(glyph_count) ||
        !size.add<text_line_break_kind>(glyph_count) ||
        !size.add<positioned_text_glyph>(glyph_count) ||
        !size.add<positioned_text_column>(glyph_count)) {
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        return false;
    }
    result = size.size() + alignof(std::max_align_t) - 1U;
    return true;
}

bool try_get_line_break_scratch_bytes(
    std::uint32_t input_count,
    std::size_t& result) noexcept {
    if (input_count == 0U) {
        result = 0U;
        return true;
    }
    scratch_size_builder size{};
    if (!size.add<unicode_scalar>(input_count) ||
        !size.add<unicode_line_break_class>(input_count) ||
        !size.add<text_line_break_kind>(input_count)) {
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        return false;
    }
    result = size.size() + alignof(std::max_align_t) - 1U;
    return true;
}

bool try_get_bidi_scratch_bytes(
    std::uint32_t input_count,
    std::size_t& result) noexcept {
    if (input_count == 0U) {
        result = 0U;
        return true;
    }
    if (input_count > std::numeric_limits<std::uint32_t>::max() / 4U) {
        return false;
    }
    scratch_size_builder size{};
    if (!size.add<unicode_scalar>(input_count) ||
        !size.add<unicode_bidi_unit>(input_count) ||
        !size.add<std::uint32_t>(static_cast<std::size_t>(input_count) * 4U) ||
        !size.add<unicode_bidi_level_run>(input_count) ||
        !size.add<unicode_bidi_bracket_pair>(input_count / 2U) ||
        !size.add<unicode_bidi_level>(input_count)) {
        return false;
    }
    if (size.size() > std::numeric_limits<std::size_t>::max() -
            (alignof(std::max_align_t) - 1U)) {
        return false;
    }
    result = size.size() + alignof(std::max_align_t) - 1U;
    return true;
}

progpu_native_status status_from_unicode_error(unicode_error error) noexcept {
    switch (error) {
        case unicode_error::none:
            return PROGPU_NATIVE_STATUS_SUCCESS;
        case unicode_error::invalid_argument:
        case unicode_error::invalid_encoding:
        case unicode_error::insufficient_buffer:
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
}

bool valid_paragraph_layout_options(
    const progpu_native_text_layout_options* options) noexcept {
    return options != nullptr &&
        options->struct_size >= sizeof(progpu_native_text_layout_options) &&
        std::isfinite(options->scale) && options->scale > 0.0F &&
        std::isfinite(options->maximum_width) &&
        options->maximum_width >= 0.0F &&
        std::isfinite(options->line_height) &&
        options->line_height >= 0.0F &&
        options->direction <= PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT &&
        options->trimming <= PROGPU_NATIVE_TEXT_TRIMMING_WORD_ELLIPSIS &&
        options->alignment <= PROGPU_NATIVE_TEXT_ALIGNMENT_JUSTIFY &&
        std::isfinite(options->ellipsis_advance) &&
        options->ellipsis_advance >= 0.0F &&
        options->reserved0 == 0U && options->reserved1 == 0U;
}

text_layout_options convert_paragraph_layout_options(
    const progpu_native_text_layout_options& source,
    std::int8_t paragraph_level) noexcept {
    return text_layout_options{
        source.scale,
        source.maximum_width,
        source.line_height,
        source.maximum_lines,
        paragraph_level == 1 ? shaping_direction::right_to_left
                             : shaping_direction::left_to_right,
        static_cast<text_trimming>(source.trimming),
        static_cast<text_alignment>(source.alignment),
        0U,
        source.ellipsis_glyph_id,
        source.ellipsis_advance};
}

struct paragraph_capacities final {
    shape_capacities shaping{};
    std::size_t scratch_bytes = 0U;
};

bool try_build_paragraph_capacities(
    const progpu_native_text_shape_request& request,
    const progpu_native_text_context& context,
    paragraph_capacities& result,
    font_error& error) noexcept {
    result = {};
    if (request.input_count == 0U) {
        error = font_error::none;
        return true;
    }
    if (request.input_count > std::numeric_limits<std::uint32_t>::max() / 4U) {
        error = font_error::invalid_argument;
        return false;
    }
    const auto normalization = context.has_normalization
        ? &context.normalization
        : nullptr;
    auto merge = [](shape_capacities& target,
                     const shape_capacities& source) noexcept {
        target.glyphs = std::max(target.glyphs, source.glyphs);
        target.graphemes = std::max(target.graphemes, source.graphemes);
        target.gsub_lookups = std::max(target.gsub_lookups, source.gsub_lookups);
        target.gpos_lookups = std::max(target.gpos_lookups, source.gpos_lookups);
        target.script_actions =
            std::max(target.script_actions, source.script_actions);
        target.complex_values =
            std::max(target.complex_values, source.complex_values);
        target.complex_indices =
            std::max(target.complex_indices, source.complex_indices);
        target.verification_glyphs =
            std::max(target.verification_glyphs, source.verification_glyphs);
        target.base_features =
            std::max(target.base_features, source.base_features);
        target.explicit_features =
            std::max(target.explicit_features, source.explicit_features);
        target.requested_features =
            std::max(target.requested_features, source.requested_features);
        target.feature_settings =
            std::max(target.feature_settings, source.feature_settings);
    };
    for (std::size_t index = 0U; index < context.font_count(); ++index) {
        const auto* font = context.font_at(index);
        shape_capacities candidate{};
        if (font == nullptr || !try_build_capacities(
                request, *font, normalization, candidate, error)) {
            if (error == font_error::none) error = font_error::invalid_argument;
            return false;
        }
        merge(result.shaping, candidate);
    }
    result.shaping.complex_values = result.shaping.glyphs;
    if (result.shaping.glyphs == std::numeric_limits<std::uint32_t>::max()) {
        error = font_error::invalid_argument;
        return false;
    }
    result.shaping.complex_indices = result.shaping.glyphs + 1U;
    auto scratch_request = request;
    scratch_request.pre_context_count = std::max(
        request.pre_context_count, request.input_count);
    scratch_request.post_context_count = std::max(
        request.post_context_count, request.input_count);
    if (!try_compute_shape_scratch_bytes(
            scratch_request, result.shaping, error)) {
        return false;
    }
    const std::size_t input_count = request.input_count;
    const std::size_t glyph_count = result.shaping.glyphs;
    scratch_size_builder size{};
    if (!size.add<std::byte>(result.shaping.scratch_bytes) ||
        !size.add<progpu_native_text_shaping_glyph>(glyph_count) ||
        !size.add<unicode_scalar>(input_count) ||
        !size.add<unicode_bidi_unit>(input_count) ||
        !size.add<std::uint32_t>(input_count * 4U) ||
        !size.add<unicode_bidi_level_run>(input_count) ||
        !size.add<unicode_bidi_bracket_pair>(input_count / 2U) ||
        !size.add<unicode_bidi_level>(input_count) ||
        !size.add<unicode_script_run>(input_count) ||
        !size.add<unicode_grapheme_cluster>(input_count) ||
        !size.add<font_fallback_candidate>(context.font_count()) ||
        !size.add<font_fallback_run>(input_count) ||
        !size.add<unicode_line_break_class>(input_count) ||
        !size.add<text_line_break_kind>(input_count) ||
        !size.add<shaping_glyph>(glyph_count) ||
        !size.add<std::int8_t>(glyph_count) ||
        !size.add<std::uint32_t>(glyph_count) ||
        !size.add<text_line_break_kind>(glyph_count) ||
        !size.add<text_visual_cluster_group>(glyph_count) ||
        !size.add<std::uint32_t>(glyph_count) ||
        !size.add<positioned_text_glyph>(glyph_count) ||
        !size.add<positioned_text_line>(glyph_count)) {
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

shaping_glyph convert_wire_glyph(
    const progpu_native_text_shaping_glyph& source) noexcept {
    return shaping_glyph{
        source.glyph_id,
        source.code_point,
        source.cluster,
        static_cast<shaping_glyph_flags>(source.flags),
        source.advance_x,
        source.advance_y,
        source.offset_x,
        source.offset_y};
}

bool append_logical_bidi_run(
    std::span<const progpu_native_text_shaping_glyph> run,
    std::int8_t level,
    std::uint32_t font_index,
    std::span<shaping_glyph> output,
    std::span<std::int8_t> levels,
    std::span<std::uint32_t> font_indices,
    std::uint32_t& written) noexcept {
    if (font_indices.size() < output.size() ||
        run.size() > output.size() -
            std::min<std::size_t>(written, output.size())) {
        return false;
    }
    auto append_group = [&](std::size_t start, std::size_t end) noexcept {
        for (std::size_t index = start; index < end; ++index) {
            output[written] = convert_wire_glyph(run[index]);
            levels[written] = level;
            font_indices[written] = font_index;
            ++written;
        }
    };
    if ((level & 1) == 0) {
        append_group(0U, run.size());
        return true;
    }
    std::size_t end = run.size();
    while (end != 0U) {
        const std::int32_t cluster = run[end - 1U].cluster;
        std::size_t start = end - 1U;
        while (start != 0U && run[start - 1U].cluster == cluster) --start;
        append_group(start, end);
        end = start;
    }
    return true;
}

bool try_map_logical_cluster_breaks(
    std::span<const progpu_native_text_scalar> input,
    std::span<const text_line_break_kind> scalar_breaks,
    std::span<const shaping_glyph> glyphs,
    std::span<text_line_break_kind> glyph_breaks) noexcept {
    if (scalar_breaks.size() != input.size() ||
        glyph_breaks.size() < glyphs.size()) {
        return false;
    }
    if (glyphs.empty()) return true;
    if (input.empty()) return false;
    for (std::size_t index = 0U; index < input.size(); ++index) {
        const std::uint64_t end =
            static_cast<std::uint64_t>(input[index].input_index) +
            input[index].input_length;
        if (end > std::numeric_limits<std::uint32_t>::max() ||
            (index != 0U && input[index].input_index <
                    static_cast<std::uint64_t>(input[index - 1U].input_index) +
                        input[index - 1U].input_length)) {
            return false;
        }
    }
    std::size_t scalar_cursor = 0U;
    std::size_t glyph_start = 0U;
    std::int32_t previous_cluster = -1;
    while (glyph_start < glyphs.size()) {
        const std::int32_t cluster = glyphs[glyph_start].cluster;
        if (cluster < 0 || cluster <= previous_cluster) return false;
        std::size_t glyph_end = glyph_start + 1U;
        while (glyph_end < glyphs.size() &&
            glyphs[glyph_end].cluster == cluster) {
            ++glyph_end;
        }
        if (glyph_end < glyphs.size() &&
            glyphs[glyph_end].cluster <= cluster) {
            return false;
        }
        const std::uint32_t next_cluster = glyph_end < glyphs.size()
            ? static_cast<std::uint32_t>(glyphs[glyph_end].cluster)
            : std::numeric_limits<std::uint32_t>::max();
        while (scalar_cursor < input.size() &&
            input[scalar_cursor].input_index < next_cluster) {
            ++scalar_cursor;
        }
        if (scalar_cursor == 0U) return false;
        std::fill(
            glyph_breaks.begin() + static_cast<std::ptrdiff_t>(glyph_start),
            glyph_breaks.begin() + static_cast<std::ptrdiff_t>(glyph_end),
            text_line_break_kind::prohibited);
        glyph_breaks[glyph_end - 1U] = scalar_breaks[scalar_cursor - 1U];
        previous_cluster = cluster;
        glyph_start = glyph_end;
    }
    return true;
}

text_layout_options convert_layout_options(
    const progpu_native_text_layout_request& request) noexcept {
    return text_layout_options{
        request.scale,
        request.maximum_width,
        request.line_height,
        request.maximum_lines,
        static_cast<shaping_direction>(request.direction),
        static_cast<text_trimming>(request.trimming),
        static_cast<text_alignment>(request.alignment),
        0U,
        request.ellipsis_glyph_id,
        request.ellipsis_advance};
}

void copy_layout_inputs(
    const progpu_native_text_layout_request& request,
    std::span<shaping_glyph> glyphs,
    std::span<text_line_break_kind> breaks) noexcept {
    for (std::size_t index = 0U; index < glyphs.size(); ++index) {
        const auto& source = request.glyphs[index];
        glyphs[index] = shaping_glyph{
            source.glyph_id,
            source.code_point,
            source.cluster,
            static_cast<shaping_glyph_flags>(source.flags),
            source.advance_x,
            source.advance_y,
            source.offset_x,
            source.offset_y};
        breaks[index] =
            static_cast<text_line_break_kind>(request.breaks_after[index]);
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
    std::span<unicode_scalar> normalization_scalars{};
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
        !arena.take(capacities.verification_glyphs, verification_glyphs) ||
        !arena.take(capacities.normalization_scalars, normalization_scalars)) {
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
        if (!context->try_get_plan(
                font, configuration.options, plan, error)) {
            result.error_code = static_cast<std::uint32_t>(error);
            return status_from_error(error);
        }
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
        arabic_flags,
        normalization_scalars};
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
        // The shaping engine retains OpenType Y-up design units internally.
        // The stable C ABI is the .NET/WebScene substitution boundary and
        // publishes the same Y-down design-unit convention as managed
        // ShapedGlyph at fontSize == unitsPerEm. Horizontal/vertical layout
        // therefore consumes the returned records without a second transform.
        glyphs[index] = progpu_native_text_shaping_glyph{
            source.glyph_id,
            source.code_point,
            source.cluster,
            static_cast<std::uint32_t>(source.flags),
            source.advance_x,
            negate_managed_metric(source.advance_y),
            source.offset_x,
            negate_managed_metric(source.offset_y)};
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
    if (!try_resolve_normalization_data(
            *request, normalization, normalization_pointer)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
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
        result->plans.reserve(progpu_native_text_context::plan_capacity);
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
        } else {
            const auto* normalization =
                get_default_unicode_normalization_data();
            if (normalization == nullptr) {
                delete result;
                return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
            }
            result->normalization = *normalization;
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

progpu_native_status progpu_native_text_context_add_fallback_font(
    progpu_native_text_context* context,
    const std::uint8_t* font_data,
    std::size_t font_size,
    std::uint32_t face_index,
    std::uint64_t identity,
    std::uint32_t* font_index) {
    if (font_index == nullptr) return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    *font_index = 0U;
    if (context == nullptr || font_data == nullptr || font_size == 0U ||
        context->fallback_fonts.size() >=
            std::numeric_limits<std::uint32_t>::max() - 1U) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    try {
        progpu_native_text_owned_font owned{};
        owned.bytes.assign(
            reinterpret_cast<const std::byte*>(font_data),
            reinterpret_cast<const std::byte*>(font_data) + font_size);
        owned.identity = identity;
        font_error error = font_error::none;
        if (!sfnt_font_view::try_create(
                owned.bytes, face_index, owned.font, &error)) {
            return status_from_error(error);
        }
        context->fallback_fonts.push_back(std::move(owned));
        *font_index =
            static_cast<std::uint32_t>(context->fallback_fonts.size());
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
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
    if (!try_resolve_normalization_data(
            *request, normalization, normalization_pointer)) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
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

progpu_native_status progpu_native_text_layout_get_requirements(
    const progpu_native_text_layout_request* request,
    progpu_native_text_layout_requirements* requirements) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_layout_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (!valid_layout_request(request)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    std::size_t scratch_bytes = 0U;
    if (!try_get_layout_scratch_bytes(request->glyph_count, scratch_bytes)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    requirements->glyph_capacity = request->glyph_count;
    requirements->line_capacity = request->glyph_count;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_layout(
    const progpu_native_text_layout_request* request,
    progpu_native_positioned_text_glyph* glyphs,
    std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines,
    std::uint32_t line_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_layout_result* result) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_layout_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (!valid_layout_request(request) ||
        (request->glyph_count != 0U &&
            (glyphs == nullptr || lines == nullptr || scratch == nullptr)) ||
        !has_aligned_pointer(glyphs, request->glyph_count) ||
        !has_aligned_pointer(lines, request->glyph_count) ||
        glyph_capacity < request->glyph_count ||
        line_capacity < request->glyph_count) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (request->glyph_count == 0U) {
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    std::size_t required_scratch = 0U;
    if (!try_get_layout_scratch_bytes(
            request->glyph_count, required_scratch) ||
        scratch_size < required_scratch) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    scratch_arena arena{scratch, scratch_size};
    std::span<shaping_glyph> native_glyphs{};
    std::span<text_line_break_kind> native_breaks{};
    std::span<shaping_glyph> public_metric_scratch{};
    std::span<positioned_text_glyph> native_positioned{};
    std::span<positioned_text_line> native_lines{};
    if (!arena.take(request->glyph_count, native_glyphs) ||
        !arena.take(request->glyph_count, native_breaks) ||
        !arena.take(request->glyph_count, public_metric_scratch) ||
        !arena.take(request->glyph_count, native_positioned) ||
        !arena.take(request->glyph_count, native_lines)) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    copy_layout_inputs(*request, native_glyphs, native_breaks);
    std::uint32_t written_glyphs = 0U;
    std::uint32_t written_lines = 0U;
    font_error error = font_error::none;
    if (!try_layout_open_type_text(
            native_glyphs,
            native_breaks,
            convert_layout_options(*request),
            public_metric_scratch,
            native_positioned,
            native_lines,
            written_glyphs,
            written_lines,
            &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    for (std::uint32_t index = 0U; index < written_glyphs; ++index) {
        const auto& source = native_positioned[index];
        glyphs[index] = progpu_native_positioned_text_glyph{
            source.glyph_index,
            source.glyph_id,
            0U,
            source.cluster,
            source.x,
            source.y,
            source.advance_x,
            source.advance_y};
    }
    for (std::uint32_t index = 0U; index < written_lines; ++index) {
        const auto& source = native_lines[index];
        lines[index] = progpu_native_positioned_text_line{
            source.glyph_start,
            source.glyph_count,
            source.input_start,
            source.input_end,
            source.width,
            source.baseline_y,
            source.height,
            static_cast<std::uint8_t>(source.clipped ? 1U : 0U),
            0U,
            0U,
            0U};
    }
    text_layout_metrics metrics{};
    if (!try_measure_positioned_text_lines(
            native_lines.first(written_lines),
            request->maximum_width,
            metrics,
            &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    result->glyph_count = written_glyphs;
    result->line_count = written_lines;
    result->content_width = metrics.content_width;
    result->content_height = metrics.content_height;
    result->measured_width = metrics.measured_width;
    result->measured_height = metrics.measured_height;
    result->scratch_bytes_used = arena.used();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_vertical_layout_get_requirements(
    const progpu_native_text_layout_request* request,
    progpu_native_text_vertical_layout_requirements* requirements) {
    if (requirements == nullptr || requirements->struct_size <
            sizeof(progpu_native_text_vertical_layout_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (!valid_vertical_layout_request(request)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    std::size_t scratch_bytes = 0U;
    if (!try_get_vertical_layout_scratch_bytes(
            request->glyph_count, scratch_bytes)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    requirements->glyph_capacity = request->glyph_count;
    requirements->column_capacity = request->glyph_count;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_vertical_layout(
    const progpu_native_text_layout_request* request,
    progpu_native_positioned_text_glyph* glyphs,
    std::uint32_t glyph_capacity,
    progpu_native_positioned_text_column* columns,
    std::uint32_t column_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_vertical_layout_result* result) {
    if (result == nullptr || result->struct_size <
            sizeof(progpu_native_text_vertical_layout_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (!valid_vertical_layout_request(request) ||
        (request->glyph_count != 0U &&
            (glyphs == nullptr || columns == nullptr || scratch == nullptr)) ||
        !has_aligned_pointer(glyphs, request->glyph_count) ||
        !has_aligned_pointer(columns, request->glyph_count) ||
        glyph_capacity < request->glyph_count ||
        column_capacity < request->glyph_count) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (request->glyph_count == 0U) return PROGPU_NATIVE_STATUS_SUCCESS;
    std::size_t required_scratch = 0U;
    if (!try_get_vertical_layout_scratch_bytes(
            request->glyph_count, required_scratch) ||
        scratch_size < required_scratch) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }

    scratch_arena arena{scratch, scratch_size};
    std::span<shaping_glyph> native_glyphs{};
    std::span<text_line_break_kind> native_breaks{};
    std::span<positioned_text_glyph> native_positioned{};
    std::span<positioned_text_column> native_columns{};
    if (!arena.take(request->glyph_count, native_glyphs) ||
        !arena.take(request->glyph_count, native_breaks) ||
        !arena.take(request->glyph_count, native_positioned) ||
        !arena.take(request->glyph_count, native_columns)) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    copy_layout_inputs(*request, native_glyphs, native_breaks);
    std::uint32_t written_glyphs = 0U;
    std::uint32_t written_columns = 0U;
    font_error error = font_error::none;
    if (!try_layout_vertical_shaped_text(
            native_glyphs,
            native_breaks,
            convert_layout_options(*request),
            native_positioned,
            native_columns,
            written_glyphs,
            written_columns,
            &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    for (std::uint32_t index = 0U; index < written_glyphs; ++index) {
        const auto& source = native_positioned[index];
        glyphs[index] = progpu_native_positioned_text_glyph{
            source.glyph_index,
            source.glyph_id,
            0U,
            source.cluster,
            source.x,
            source.y,
            source.advance_x,
            source.advance_y};
    }
    for (std::uint32_t index = 0U; index < written_columns; ++index) {
        const auto& source = native_columns[index];
        columns[index] = progpu_native_positioned_text_column{
            source.glyph_start,
            source.glyph_count,
            source.input_start,
            source.input_end,
            source.height,
            source.x,
            source.width,
            static_cast<std::uint8_t>(source.clipped ? 1U : 0U),
            0U,
            0U,
            0U};
    }
    text_layout_metrics metrics{};
    if (!try_measure_positioned_text_columns(
            native_columns.first(written_columns),
            request->maximum_width,
            metrics,
            &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_error(error);
    }
    result->glyph_count = written_glyphs;
    result->column_count = written_columns;
    result->content_width = metrics.content_width;
    result->content_height = metrics.content_height;
    result->measured_width = metrics.measured_width;
    result->measured_height = metrics.measured_height;
    result->scratch_bytes_used = arena.used();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_get_line_break_requirements(
    const progpu_native_text_scalar* input,
    std::uint32_t input_count,
    progpu_native_text_line_break_requirements* requirements) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_line_break_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (!valid_wire_scalars(input, input_count)) {
        requirements->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    std::size_t scratch_bytes = 0U;
    if (!try_get_line_break_scratch_bytes(input_count, scratch_bytes)) {
        requirements->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    requirements->break_capacity = input_count;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_resolve_line_breaks(
    const progpu_native_text_scalar* input,
    std::uint32_t input_count,
    std::uint8_t* breaks_after,
    std::uint32_t break_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_line_break_result* result) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_line_break_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (!valid_wire_scalars(input, input_count) ||
        break_capacity < input_count ||
        (input_count != 0U &&
            (breaks_after == nullptr || scratch == nullptr))) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    if (input_count == 0U) {
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    std::size_t required_scratch = 0U;
    if (!try_get_line_break_scratch_bytes(input_count, required_scratch) ||
        scratch_size < required_scratch) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    scratch_arena arena{scratch, scratch_size};
    std::span<unicode_scalar> native_input{};
    std::span<unicode_line_break_class> classes{};
    std::span<text_line_break_kind> native_breaks{};
    if (!arena.take(input_count, native_input) ||
        !arena.take(input_count, classes) ||
        !arena.take(input_count, native_breaks)) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    copy_scalars(input, native_input);
    unicode_error error = unicode_error::none;
    if (!try_resolve_unicode_line_breaks(
            native_input, classes, native_breaks, &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_unicode_error(error);
    }
    for (std::uint32_t index = 0U; index < input_count; ++index) {
        breaks_after[index] = static_cast<std::uint8_t>(native_breaks[index]);
    }
    result->break_count = input_count;
    result->scratch_bytes_used = arena.used();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_get_bidi_requirements(
    const progpu_native_text_scalar* input,
    std::uint32_t input_count,
    progpu_native_text_bidi_requirements* requirements) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_bidi_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (!valid_wire_scalars(input, input_count)) {
        requirements->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    std::size_t scratch_bytes = 0U;
    if (!try_get_bidi_scratch_bytes(input_count, scratch_bytes)) {
        requirements->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    requirements->level_capacity = input_count;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_resolve_bidi(
    const progpu_native_text_scalar* input,
    std::uint32_t input_count,
    std::int32_t requested_paragraph_level,
    progpu_native_text_bidi_level* levels,
    std::uint32_t level_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_bidi_result* result) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_bidi_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (!valid_wire_scalars(input, input_count) ||
        requested_paragraph_level < -1 || requested_paragraph_level > 1 ||
        level_capacity < input_count ||
        !has_aligned_pointer(levels, input_count) ||
        (input_count != 0U && scratch == nullptr)) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::invalid_argument);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    result->paragraph_level = requested_paragraph_level == 1 ? 1 : 0;
    if (input_count == 0U) {
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    std::size_t required_scratch = 0U;
    if (!try_get_bidi_scratch_bytes(input_count, required_scratch) ||
        scratch_size < required_scratch) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    scratch_arena arena{scratch, scratch_size};
    std::span<unicode_scalar> native_input{};
    std::span<unicode_bidi_unit> units{};
    std::span<std::uint32_t> indices{};
    std::span<unicode_bidi_level_run> runs{};
    std::span<unicode_bidi_bracket_pair> bracket_pairs{};
    std::span<unicode_bidi_level> native_levels{};
    if (!arena.take(input_count, native_input) ||
        !arena.take(input_count, units) ||
        !arena.take(static_cast<std::size_t>(input_count) * 4U, indices) ||
        !arena.take(input_count, runs) ||
        !arena.take(input_count / 2U, bracket_pairs) ||
        !arena.take(input_count, native_levels)) {
        result->error_code =
            static_cast<std::uint32_t>(unicode_error::insufficient_buffer);
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    copy_scalars(input, native_input);
    unicode_bidi_scratch bidi_scratch{units, indices, runs, bracket_pairs};
    std::int8_t paragraph_level = 0;
    std::uint32_t written = 0U;
    unicode_error error = unicode_error::none;
    if (!try_resolve_unicode_bidi(
            native_input,
            static_cast<std::int8_t>(requested_paragraph_level),
            bidi_scratch,
            native_levels,
            paragraph_level,
            written,
            &error)) {
        result->error_code = static_cast<std::uint32_t>(error);
        return status_from_unicode_error(error);
    }
    for (std::uint32_t index = 0U; index < written; ++index) {
        const auto& source = native_levels[index];
        levels[index] = progpu_native_text_bidi_level{
            source.input_index,
            source.input_length,
            source.level,
            0U};
    }
    result->level_count = written;
    result->paragraph_level = paragraph_level;
    result->scratch_bytes_used = arena.used();
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_context_get_paragraph_requirements(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout,
    progpu_native_text_paragraph_requirements* requirements) {
    if (requirements == nullptr ||
        requirements->struct_size <
            sizeof(progpu_native_text_paragraph_requirements)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *requirements = {};
    requirements->struct_size = sizeof(*requirements);
    if (context == nullptr || !valid_request(shaping, false) ||
        shaping->direction > PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT ||
        !valid_paragraph_layout_options(layout)) {
        requirements->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        requirements->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    paragraph_capacities capacities{};
    font_error error = font_error::none;
    if (!try_build_paragraph_capacities(
            *shaping,
            *context,
            capacities,
            error)) {
        requirements->error_code = static_cast<std::uint32_t>(error);
        requirements->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return status_from_error(error);
    }
    requirements->glyph_capacity = capacities.shaping.glyphs;
    requirements->line_capacity = capacities.shaping.glyphs;
    requirements->scratch_alignment = 1U;
    requirements->scratch_bytes = capacities.scratch_bytes;
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status progpu_native_text_context_layout_paragraph(
    progpu_native_text_context* context,
    const progpu_native_text_shape_request* shaping,
    const progpu_native_text_layout_options* layout,
    progpu_native_positioned_text_glyph* glyphs,
    std::uint32_t glyph_capacity,
    progpu_native_positioned_text_line* lines,
    std::uint32_t line_capacity,
    void* scratch,
    std::size_t scratch_size,
    progpu_native_text_paragraph_result* result) {
    if (result == nullptr ||
        result->struct_size < sizeof(progpu_native_text_paragraph_result)) {
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = sizeof(*result);
    if (context == nullptr || !valid_request(shaping, false) ||
        shaping->direction > PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT ||
        !valid_paragraph_layout_options(layout)) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::invalid_argument);
        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }
    result->paragraph_level =
        shaping->direction == PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT
        ? 1
        : 0;
    if (shaping->input_count == 0U) {
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    paragraph_capacities capacities{};
    font_error font_result = font_error::none;
    if (!try_build_paragraph_capacities(
            *shaping,
            *context,
            capacities,
            font_result)) {
        result->error_code = static_cast<std::uint32_t>(font_result);
        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return status_from_error(font_result);
    }
    if (glyph_capacity < capacities.shaping.glyphs ||
        line_capacity < capacities.shaping.glyphs ||
        !has_aligned_pointer(glyphs, capacities.shaping.glyphs) ||
        !has_aligned_pointer(lines, capacities.shaping.glyphs) ||
        scratch == nullptr || scratch_size < capacities.scratch_bytes) {
        result->error_code =
            static_cast<std::uint32_t>(font_error::insufficient_buffer);
        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
        return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    }

    try {
        scratch_arena arena{scratch, scratch_size};
        const std::size_t input_count = shaping->input_count;
        const std::size_t glyph_limit = capacities.shaping.glyphs;
        std::span<std::byte> shape_scratch{};
        std::span<progpu_native_text_shaping_glyph> run_glyphs{};
        std::span<unicode_scalar> native_input{};
        std::span<unicode_bidi_unit> bidi_units{};
        std::span<std::uint32_t> bidi_indices{};
        std::span<unicode_bidi_level_run> bidi_runs{};
        std::span<unicode_bidi_bracket_pair> bidi_pairs{};
        std::span<unicode_bidi_level> scalar_levels{};
        std::span<unicode_script_run> script_runs{};
        std::span<unicode_grapheme_cluster> graphemes{};
        std::span<font_fallback_candidate> fallback_candidates{};
        std::span<font_fallback_run> fallback_runs{};
        std::span<unicode_line_break_class> line_classes{};
        std::span<text_line_break_kind> scalar_breaks{};
        std::span<shaping_glyph> logical_glyphs{};
        std::span<std::int8_t> glyph_levels{};
        std::span<std::uint32_t> glyph_font_indices{};
        std::span<text_line_break_kind> glyph_breaks{};
        std::span<text_visual_cluster_group> visual_groups{};
        std::span<std::uint32_t> visual_indices{};
        std::span<positioned_text_glyph> positioned{};
        std::span<positioned_text_line> native_lines{};
        if (!arena.take(capacities.shaping.scratch_bytes, shape_scratch) ||
            !arena.take(glyph_limit, run_glyphs) ||
            !arena.take(input_count, native_input) ||
            !arena.take(input_count, bidi_units) ||
            !arena.take(input_count * 4U, bidi_indices) ||
            !arena.take(input_count, bidi_runs) ||
            !arena.take(input_count / 2U, bidi_pairs) ||
            !arena.take(input_count, scalar_levels) ||
            !arena.take(input_count, script_runs) ||
            !arena.take(input_count, graphemes) ||
            !arena.take(context->font_count(), fallback_candidates) ||
            !arena.take(input_count, fallback_runs) ||
            !arena.take(input_count, line_classes) ||
            !arena.take(input_count, scalar_breaks) ||
            !arena.take(glyph_limit, logical_glyphs) ||
            !arena.take(glyph_limit, glyph_levels) ||
            !arena.take(glyph_limit, glyph_font_indices) ||
            !arena.take(glyph_limit, glyph_breaks) ||
            !arena.take(glyph_limit, visual_groups) ||
            !arena.take(glyph_limit, visual_indices) ||
            !arena.take(glyph_limit, positioned) ||
            !arena.take(glyph_limit, native_lines)) {
            result->error_code =
                static_cast<std::uint32_t>(font_error::insufficient_buffer);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        }

        copy_scalars(shaping->input, native_input);
        unicode_error unicode_result = unicode_error::none;
        unicode_bidi_scratch bidi_scratch{
            bidi_units, bidi_indices, bidi_runs, bidi_pairs};
        const std::int8_t requested_level =
            shaping->direction == PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT
            ? 0
            : shaping->direction ==
                    PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT
                ? 1
                : -1;
        std::int8_t paragraph_level = 0;
        std::uint32_t scalar_level_count = 0U;
        if (!try_resolve_unicode_bidi(
                native_input,
                requested_level,
                bidi_scratch,
                scalar_levels,
                paragraph_level,
                scalar_level_count,
                &unicode_result)) {
            result->error_code = static_cast<std::uint32_t>(unicode_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_BIDI;
            return status_from_unicode_error(unicode_result);
        }
        result->paragraph_level = paragraph_level;
        const bool itemize_scripts = shaping->unicode_script == 0U ||
            shaping->unicode_script == default_script.value;
        std::uint32_t script_run_count = 1U;
        if (itemize_scripts) {
            if (!try_itemize_unicode_scripts(
                    native_input,
                    script_runs,
                    script_run_count,
                    &unicode_result)) {
                result->error_code =
                    static_cast<std::uint32_t>(unicode_result);
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return status_from_unicode_error(unicode_result);
            }
        } else {
            script_runs[0U] = unicode_script_run{
                0U,
                static_cast<std::uint32_t>(input_count),
                native_input.front().input_index,
                static_cast<std::uint32_t>(
                    static_cast<std::uint64_t>(native_input.back().input_index) +
                    native_input.back().input_length -
                    native_input.front().input_index),
                open_type_tag{shaping->unicode_script}};
        }
        std::uint32_t grapheme_count = 0U;
        if (!try_segment_unicode_graphemes(
                native_input, graphemes, grapheme_count, &unicode_result)) {
            result->error_code = static_cast<std::uint32_t>(unicode_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
            return status_from_unicode_error(unicode_result);
        }
        fallback_candidates[0U] = font_fallback_candidate{
            &context->font, 0U};
        for (std::size_t index = 0U;
             index < context->fallback_fonts.size();
             ++index) {
            const auto& fallback = context->fallback_fonts[index];
            fallback_candidates[index + 1U] = font_fallback_candidate{
                &fallback.font, fallback.identity};
        }
        std::uint32_t fallback_run_count = 0U;
        if (!try_itemize_font_fallback(
                native_input,
                graphemes.first(grapheme_count),
                fallback_candidates,
                0U,
                fallback_runs,
                fallback_run_count,
                &font_result)) {
            result->error_code = static_cast<std::uint32_t>(font_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
            return status_from_error(font_result);
        }
        if (!try_resolve_unicode_line_breaks(
                native_input,
                line_classes,
                scalar_breaks,
                &unicode_result)) {
            result->error_code = static_cast<std::uint32_t>(unicode_result);
            result->error_stage =
                PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LINE_BREAK;
            return status_from_unicode_error(unicode_result);
        }

        std::uint32_t logical_count = 0U;
        std::size_t scalar_start = 0U;
        std::size_t script_run_index = 0U;
        std::size_t fallback_run_index = 0U;
        while (scalar_start < input_count) {
            while (script_run_index < script_run_count &&
                scalar_start >= static_cast<std::size_t>(
                    script_runs[script_run_index].scalar_start) +
                    script_runs[script_run_index].scalar_count) {
                ++script_run_index;
            }
            if (script_run_index >= script_run_count) {
                result->error_code =
                    static_cast<std::uint32_t>(font_error::invalid_argument);
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
            const auto& script_run = script_runs[script_run_index];
            const std::size_t script_end =
                static_cast<std::size_t>(script_run.scalar_start) +
                script_run.scalar_count;
            while (fallback_run_index < fallback_run_count &&
                scalar_start >= static_cast<std::size_t>(
                    fallback_runs[fallback_run_index].scalar_index) +
                    fallback_runs[fallback_run_index].scalar_count) {
                ++fallback_run_index;
            }
            if (fallback_run_index >= fallback_run_count) {
                result->error_code =
                    static_cast<std::uint32_t>(font_error::invalid_argument);
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
            const auto& fallback_run = fallback_runs[fallback_run_index];
            const std::size_t fallback_end =
                static_cast<std::size_t>(fallback_run.scalar_index) +
                fallback_run.scalar_count;
            const auto* selected_font =
                context->font_at(fallback_run.font_index);
            if (selected_font == nullptr) {
                result->error_code =
                    static_cast<std::uint32_t>(font_error::invalid_argument);
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
            const std::int8_t level = scalar_levels[scalar_start].level;
            std::size_t scalar_end = scalar_start + 1U;
            while (scalar_end < input_count && scalar_end < script_end &&
                scalar_end < fallback_end &&
                scalar_levels[scalar_end].level == level) {
                ++scalar_end;
            }
            auto run_request = *shaping;
            run_request.input = shaping->input + scalar_start;
            run_request.input_count = static_cast<std::uint32_t>(
                scalar_end - scalar_start);
            run_request.direction = (level & 1) == 0
                ? PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT
                : PROGPU_NATIVE_TEXT_DIRECTION_RIGHT_TO_LEFT;
            if (shaping->unicode_script == 0U ||
                shaping->unicode_script == default_script.value) {
                run_request.unicode_script = script_run.script.value;
            }
            if (scalar_start != 0U) {
                run_request.pre_context = shaping->input;
                run_request.pre_context_count =
                    static_cast<std::uint32_t>(scalar_start);
            }
            if (scalar_end != input_count) {
                run_request.post_context = shaping->input + scalar_end;
                run_request.post_context_count = static_cast<std::uint32_t>(
                    input_count - scalar_end);
            }
            progpu_native_text_shape_result shape_result{};
            shape_result.struct_size = sizeof(shape_result);
            const auto shape_status = shape_core(
                run_request,
                *selected_font,
                context->has_normalization ? &context->normalization : nullptr,
                capacities.shaping,
                run_glyphs.data(),
                static_cast<std::uint32_t>(run_glyphs.size()),
                shape_scratch.data(),
                shape_scratch.size(),
                shape_result,
                context);
            if (shape_status != PROGPU_NATIVE_STATUS_SUCCESS) {
                result->error_code = shape_result.error_code;
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return shape_status;
            }
            if (!append_logical_bidi_run(
                    run_glyphs.first(shape_result.glyph_count),
                    level,
                    fallback_run.font_index,
                    logical_glyphs,
                    glyph_levels,
                    glyph_font_indices,
                    logical_count)) {
                result->error_code =
                    static_cast<std::uint32_t>(font_error::insufficient_buffer);
                result->error_stage =
                    PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
                return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
            }
            ++result->shaping_run_count;
            scalar_start = scalar_end;
        }
        result->shaped_glyph_count = logical_count;
        const auto logical = logical_glyphs.first(logical_count);
        if (!try_map_logical_cluster_breaks(
                std::span<const progpu_native_text_scalar>{
                    shaping->input, shaping->input_count},
                scalar_breaks,
                logical,
                glyph_breaks.first(logical_count))) {
            result->error_code =
                static_cast<std::uint32_t>(font_error::invalid_argument);
            result->error_stage =
                PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_CLUSTER_MAP;
            return PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
        }

        text_logical_layout_scratch logical_scratch{
            visual_groups, visual_indices};
        std::uint32_t positioned_count = 0U;
        std::uint32_t written_lines = 0U;
        if (!try_layout_logical_shaped_text(
                logical,
                glyph_breaks.first(logical_count),
                glyph_levels.first(logical_count),
                paragraph_level,
                convert_paragraph_layout_options(*layout, paragraph_level),
                logical_scratch,
                positioned,
                native_lines,
                positioned_count,
                written_lines,
                &font_result)) {
            result->error_code = static_cast<std::uint32_t>(font_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
            return status_from_error(font_result);
        }
        for (std::uint32_t index = 0U; index < positioned_count; ++index) {
            const auto& source = positioned[index];
            // Layout owns the synthetic ellipsis, so it has no source-glyph
            // index. The public paragraph contract resolves that caller-
            // supplied glyph against the primary face.
            const auto font_index = source.glyph_index ==
                    std::numeric_limits<std::uint32_t>::max()
                ? 0U
                : glyph_font_indices[source.glyph_index];
            glyphs[index] = progpu_native_positioned_text_glyph{
                source.glyph_index,
                source.glyph_id,
                font_index,
                source.cluster,
                source.x,
                source.y,
                source.advance_x,
                source.advance_y};
        }
        for (std::uint32_t index = 0U; index < written_lines; ++index) {
            const auto& source = native_lines[index];
            lines[index] = progpu_native_positioned_text_line{
                source.glyph_start,
                source.glyph_count,
                source.input_start,
                source.input_end,
                source.width,
                source.baseline_y,
                source.height,
                static_cast<std::uint8_t>(source.clipped ? 1U : 0U),
                0U,
                0U,
                0U};
        }
        text_layout_metrics metrics{};
        if (!try_measure_positioned_text_lines(
                native_lines.first(written_lines),
                layout->maximum_width,
                metrics,
                &font_result)) {
            result->error_code = static_cast<std::uint32_t>(font_result);
            result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_LAYOUT;
            return status_from_error(font_result);
        }
        result->glyph_count = positioned_count;
        result->line_count = written_lines;
        result->cached_plan_count =
            static_cast<std::uint32_t>(context->plans.size());
        result->plan_build_count = context->plan_build_count;
        result->content_width = metrics.content_width;
        result->content_height = metrics.content_height;
        result->measured_width = metrics.measured_width;
        result->measured_height = metrics.measured_height;
        result->scratch_bytes_used = arena.used();
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return PROGPU_NATIVE_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        result->error_stage = PROGPU_NATIVE_TEXT_PARAGRAPH_STAGE_SHAPING;
        return PROGPU_NATIVE_STATUS_INTERNAL_ERROR;
    }
}

} // extern "C"
