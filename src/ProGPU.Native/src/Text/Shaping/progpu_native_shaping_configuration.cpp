#include "progpu_native_text.hpp"

#include "progpu_native_shaping_options_internal.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Allocation-free composition of the ProGPU-owned managed ShapeCore planning
// boundary from OpenTypeTextShaper.cs and CpuOpenTypeShaper.cs at checkpoint
// 9089d6ca. Existing granular route/request/feature ports remain the only
// policy implementations; this unit connects them without per-feature calls.

namespace progpu::native::text {
namespace {

constexpr auto default_script =
    open_type_tag::from_chars('D', 'F', 'L', 'T');
constexpr std::uint32_t maximum_policy_features = 32U;

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

open_type_tag infer_script(
    std::span<const unicode_scalar> input,
    open_type_tag requested) noexcept {
    if (requested.value != 0U && requested != default_script) return requested;
    for (const auto& scalar : input) {
        const auto script = get_unicode_script(scalar.code_point);
        if (script != default_script) return script;
    }
    return default_script;
}

bool add_capacity(
    std::uint32_t left,
    std::uint32_t right,
    std::uint32_t& result) noexcept {
    const auto value = static_cast<std::uint64_t>(left) + right;
    if (value > std::numeric_limits<std::uint32_t>::max()) return false;
    result = static_cast<std::uint32_t>(value);
    return true;
}

} // namespace

bool try_get_open_type_shape_configuration_requirements(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const open_type_shape_configuration_request& request,
    open_type_shape_configuration_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (!detail::valid_shaping_options(
            request.direction,
            request.cluster_level,
            request.buffer_flags,
            true)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    open_type_requested_feature_requirements feature_requirements{};
    if (!try_get_open_type_requested_feature_requirements(
            request.features, feature_requirements, error)) return false;
    open_type_shaping_route route{};
    if (!try_resolve_open_type_shaping_route(
            font,
            infer_script(input, request.unicode_script),
            request.direction,
            route,
            error)) return false;
    std::uint32_t requested_capacity = 0U;
    std::uint32_t setting_capacity = 0U;
    if (!add_capacity(
            feature_requirements.base_feature_capacity,
            maximum_policy_features,
            requested_capacity) ||
        !add_capacity(
            requested_capacity,
            feature_requirements.ranged_feature_capacity,
            setting_capacity)) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    result = open_type_shape_configuration_requirements{
        feature_requirements.base_feature_capacity,
        feature_requirements.explicit_feature_capacity,
        requested_capacity,
        setting_capacity};
    set_error(error, font_error::none);
    return true;
}

bool try_prepare_open_type_shape_configuration(
    const sfnt_font_view& font,
    std::span<const unicode_scalar> input,
    const open_type_shape_configuration_request& request,
    std::span<open_type_feature_setting> base_feature_scratch,
    std::span<open_type_tag> explicit_feature_storage,
    std::span<open_type_tag> requested_feature_storage,
    std::span<shaping_feature> feature_setting_storage,
    open_type_shape_configuration& result,
    font_error* error) noexcept {
    result = {};
    open_type_shape_configuration_requirements requirements{};
    if (!try_get_open_type_shape_configuration_requirements(
            font, input, request, requirements, error)) return false;
    if (base_feature_scratch.size() < requirements.base_feature_capacity ||
        explicit_feature_storage.size() < requirements.explicit_feature_capacity ||
        requested_feature_storage.size() < requirements.requested_feature_capacity ||
        feature_setting_storage.size() < requirements.feature_setting_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    std::uint32_t base_written = 0U;
    std::uint32_t explicit_written = 0U;
    std::uint32_t ranged_written = 0U;
    auto ranged_storage = feature_setting_storage.subspan(
        requirements.requested_feature_capacity,
        requirements.feature_setting_capacity -
            requirements.requested_feature_capacity);
    if (!try_resolve_open_type_requested_features(
            request.features,
            base_feature_scratch,
            explicit_feature_storage,
            ranged_storage,
            base_written,
            explicit_written,
            ranged_written,
            error)) return false;

    open_type_shaping_route route{};
    if (!try_resolve_open_type_shaping_route(
            font,
            infer_script(input, request.unicode_script),
            request.direction,
            route,
            error)) return false;
    std::uint32_t requested_written = 0U;
    std::uint32_t global_settings_written = 0U;
    if (!try_resolve_open_type_feature_plan(
            route,
            base_feature_scratch.first(base_written),
            explicit_feature_storage.first(explicit_written),
            requested_feature_storage,
            feature_setting_storage.first(
                requirements.requested_feature_capacity),
            requested_written,
            global_settings_written,
            error)) return false;
    std::uint32_t active_ranges_written = 0U;
    for (std::uint32_t index = 0U; index < ranged_written; ++index) {
        const auto range = ranged_storage[index];
        const auto active = std::find(
            requested_feature_storage.begin(),
            requested_feature_storage.begin() + requested_written,
            range.tag) !=
            requested_feature_storage.begin() + requested_written;
        if (!active) continue;
        feature_setting_storage[
            global_settings_written + active_ranges_written] = range;
        ++active_ranges_written;
    }
    const std::uint32_t settings_written =
        global_settings_written + active_ranges_written;

    open_type_shape_run_options options{};
    options.script = route.layout_script;
    options.language = resolve_open_type_language_tag(request.language);
    options.direction = route.direction;
    options.requested_features =
        requested_feature_storage.first(requested_written);
    options.normalized_coordinates = request.normalized_coordinates;
    options.alternate_value = request.alternate_value;
    options.zero_mark_advances = request.zero_mark_advances;
    options.cluster_level = request.cluster_level;
    options.buffer_flags = request.buffer_flags;
    options.compose_hebrew_presentation_forms =
        route.compose_hebrew_presentation_forms;
    options.complex_script = route.complex_script;
    options.explicit_features =
        explicit_feature_storage.first(explicit_written);
    options.feature_settings =
        feature_setting_storage.first(settings_written);
    options.normalization_data = request.normalization_data;
    options.pre_context = request.pre_context;
    options.post_context = request.post_context;
    options.unicode_script = route.unicode_script;
    result = open_type_shape_configuration{
        route,
        options,
        base_written,
        requested_written,
        explicit_written,
        settings_written};
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
