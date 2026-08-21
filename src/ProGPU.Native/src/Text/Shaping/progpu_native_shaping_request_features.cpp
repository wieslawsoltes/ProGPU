#include "progpu_native_text.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Exact allocation-free port of ProGPU-owned CpuOpenTypeShaper.Shape feature
// request normalization from CpuOpenTypeShaper.cs at checkpoint 2dad8df4.
// Full-run values become the default-overridden baseline; partial records stay
// borrowed bulk ranges and never cross the managed/native boundary singly.

namespace progpu::native::text {
namespace {

constexpr std::uint32_t all_input = 0xFFFFFFFFU;

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

bool is_global(const shaping_feature& feature) noexcept {
    return feature.start == 0U && feature.end == all_input;
}

std::uint32_t managed_global_value(std::uint32_t value) noexcept {
    return std::min(
        value,
        static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max()));
}

bool has_prior_tag(
    std::span<const shaping_feature> features,
    std::size_t end,
    open_type_tag tag) noexcept {
    for (std::size_t index = 0U; index < end; ++index) {
        if (features[index].tag == tag) return true;
    }
    return false;
}

bool baseline_value(
    std::span<const shaping_feature> requested,
    open_type_tag tag,
    std::uint32_t& value) noexcept {
    for (std::size_t index = requested.size(); index > 0U; --index) {
        const auto& feature = requested[index - 1U];
        if (is_global(feature) && feature.tag == tag) {
            value = managed_global_value(feature.value);
            return true;
        }
    }
    for (const auto& feature : get_default_open_type_feature_settings()) {
        if (feature.tag == tag) {
            value = feature.value;
            return true;
        }
    }
    value = 0U;
    return false;
}

bool selected_nonzero_before(
    std::span<const shaping_feature> requested,
    std::size_t end,
    open_type_tag tag) noexcept {
    std::uint32_t value = 0U;
    if (baseline_value(requested, tag, value) && value != 0U) return true;
    for (std::size_t index = 0U; index < end; ++index) {
        const auto& feature = requested[index];
        if (feature.tag == tag && feature.value != 0U && !is_global(feature)) {
            return true;
        }
    }
    return false;
}

} // namespace

bool try_get_open_type_requested_feature_requirements(
    std::span<const shaping_feature> requested_features,
    open_type_requested_feature_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (requested_features.size() >
        std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::size_t base_count = get_default_open_type_feature_settings().size();
    std::size_t explicit_count = 0U;
    std::size_t ranged_count = 0U;
    for (std::size_t index = 0U; index < requested_features.size(); ++index) {
        const auto& feature = requested_features[index];
        if (feature.start > feature.end) {
            set_error(error, font_error::invalid_argument);
            return false;
        }
        if (!has_prior_tag(requested_features, index, feature.tag)) {
            ++explicit_count;
        }
        if (is_global(feature)) {
            const bool in_defaults = std::any_of(
                get_default_open_type_feature_settings().begin(),
                get_default_open_type_feature_settings().end(),
                [&feature](const auto& item) {
                    return item.tag == feature.tag;
                });
            bool prior_new_global = false;
            for (std::size_t prior = 0U; prior < index; ++prior) {
                prior_new_global |= is_global(requested_features[prior]) &&
                    requested_features[prior].tag == feature.tag;
            }
            if (!in_defaults && !prior_new_global) ++base_count;
        } else {
            ++ranged_count;
        }
        if (feature.value != 0U &&
            !selected_nonzero_before(requested_features, index, feature.tag)) {
            ++base_count;
        }
    }
    if (base_count > std::numeric_limits<std::uint32_t>::max() ||
        explicit_count > std::numeric_limits<std::uint32_t>::max() ||
        ranged_count > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    result = open_type_requested_feature_requirements{
        static_cast<std::uint32_t>(base_count),
        static_cast<std::uint32_t>(explicit_count),
        static_cast<std::uint32_t>(ranged_count)};
    set_error(error, font_error::none);
    return true;
}

bool try_resolve_open_type_requested_features(
    std::span<const shaping_feature> requested_features,
    std::span<open_type_feature_setting> base_features_output,
    std::span<open_type_tag> explicit_features_output,
    std::span<shaping_feature> ranged_features_output,
    std::uint32_t& base_features_written,
    std::uint32_t& explicit_features_written,
    std::uint32_t& ranged_features_written,
    font_error* error) noexcept {
    base_features_written = 0U;
    explicit_features_written = 0U;
    ranged_features_written = 0U;
    open_type_requested_feature_requirements requirements{};
    if (!try_get_open_type_requested_feature_requirements(
            requested_features, requirements, error)) return false;
    if (base_features_output.size() < requirements.base_feature_capacity ||
        explicit_features_output.size() < requirements.explicit_feature_capacity ||
        ranged_features_output.size() < requirements.ranged_feature_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    for (const auto& feature : get_default_open_type_feature_settings()) {
        base_features_output[base_features_written++] = feature;
    }
    for (const auto& feature : requested_features) {
        if (!is_global(feature)) continue;
        const auto existing = std::find_if(
            base_features_output.begin(),
            base_features_output.begin() + base_features_written,
            [&feature](const auto& item) { return item.tag == feature.tag; });
        const open_type_feature_setting value{
            feature.tag, managed_global_value(feature.value)};
        if (existing == base_features_output.begin() + base_features_written) {
            base_features_output[base_features_written++] = value;
        } else {
            *existing = value;
        }
    }
    for (const auto& feature : requested_features) {
        if (feature.value == 0U) continue;
        const bool enabled = std::any_of(
            base_features_output.begin(),
            base_features_output.begin() + base_features_written,
            [&feature](const auto& item) {
                return item.tag == feature.tag && item.value != 0U;
            });
        if (!enabled) {
            base_features_output[base_features_written++] =
                open_type_feature_setting{feature.tag, 1U};
        }
    }
    for (const auto& feature : requested_features) {
        const auto explicit_end =
            explicit_features_output.begin() + explicit_features_written;
        if (std::find(
                explicit_features_output.begin(),
                explicit_end,
                feature.tag) == explicit_end) {
            explicit_features_output[explicit_features_written++] = feature.tag;
        }
        if (!is_global(feature)) {
            ranged_features_output[ranged_features_written++] = feature;
        }
    }
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
