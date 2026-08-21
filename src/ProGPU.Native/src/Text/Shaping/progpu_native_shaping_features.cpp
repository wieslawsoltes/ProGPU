#include "progpu_native_text.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>

// Exact allocation-free port of ProGPU-owned TextShapingOptions defaults,
// AddScriptFeatures, and AddDirectionalFeatures from OpenTypeTextShaper.cs at
// checkpoint 25762bb8. The caller-owned output doubles as bounded temporary
// storage; no feature crosses the managed/native boundary individually.

namespace progpu::native::text {
namespace {

constexpr auto tag(char a, char b, char c, char d) noexcept {
    return open_type_tag::from_chars(a, b, c, d);
}

constexpr std::array default_features{
    open_type_feature_setting{tag('r', 'v', 'r', 'n'), 1U},
    open_type_feature_setting{tag('f', 'r', 'a', 'c'), 1U},
    open_type_feature_setting{tag('n', 'u', 'm', 'r'), 1U},
    open_type_feature_setting{tag('d', 'n', 'o', 'm'), 1U},
    open_type_feature_setting{tag('c', 'c', 'm', 'p'), 1U},
    open_type_feature_setting{tag('l', 'o', 'c', 'l'), 1U},
    open_type_feature_setting{tag('i', 's', 'o', 'l'), 1U},
    open_type_feature_setting{tag('f', 'i', 'n', 'a'), 1U},
    open_type_feature_setting{tag('f', 'i', 'n', '2'), 1U},
    open_type_feature_setting{tag('f', 'i', 'n', '3'), 1U},
    open_type_feature_setting{tag('m', 'e', 'd', 'i'), 1U},
    open_type_feature_setting{tag('m', 'e', 'd', '2'), 1U},
    open_type_feature_setting{tag('i', 'n', 'i', 't'), 1U},
    open_type_feature_setting{tag('r', 'l', 'i', 'g'), 1U},
    open_type_feature_setting{tag('m', 'a', 'r', 'k'), 1U},
    open_type_feature_setting{tag('m', 'k', 'm', 'k'), 1U},
    open_type_feature_setting{tag('c', 'a', 'l', 't'), 1U},
    open_type_feature_setting{tag('c', 'l', 'i', 'g'), 1U},
    open_type_feature_setting{tag('c', 'u', 'r', 's'), 1U},
    open_type_feature_setting{tag('d', 'i', 's', 't'), 1U},
    open_type_feature_setting{tag('a', 'b', 'v', 'm'), 1U},
    open_type_feature_setting{tag('b', 'l', 'w', 'm'), 1U},
    open_type_feature_setting{tag('k', 'e', 'r', 'n'), 1U},
    open_type_feature_setting{tag('l', 'i', 'g', 'a'), 1U},
    open_type_feature_setting{tag('r', 'c', 'l', 't'), 1U},
    open_type_feature_setting{tag('r', 'a', 'n', 'd'), 0xFFFFU}};

constexpr std::array khmer_features{
    tag('p', 'r', 'e', 'f'), tag('b', 'l', 'w', 'f'),
    tag('a', 'b', 'v', 'f'), tag('p', 's', 't', 'f'),
    tag('c', 'f', 'a', 'r'), tag('p', 'r', 'e', 's'),
    tag('a', 'b', 'v', 's'), tag('b', 'l', 'w', 's'),
    tag('p', 's', 't', 's')};
constexpr std::array myanmar_features{
    tag('r', 'p', 'h', 'f'), tag('p', 'r', 'e', 'f'),
    tag('b', 'l', 'w', 'f'), tag('p', 's', 't', 'f'),
    tag('p', 'r', 'e', 's'), tag('a', 'b', 'v', 's'),
    tag('b', 'l', 'w', 's'), tag('p', 's', 't', 's')};
constexpr std::array hangul_features{
    tag('l', 'j', 'm', 'o'), tag('v', 'j', 'm', 'o'),
    tag('t', 'j', 'm', 'o')};
constexpr std::array indic_features{
    tag('n', 'u', 'k', 't'), tag('a', 'k', 'h', 'n'),
    tag('r', 'p', 'h', 'f'), tag('r', 'k', 'r', 'f'),
    tag('p', 'r', 'e', 'f'), tag('b', 'l', 'w', 'f'),
    tag('a', 'b', 'v', 'f'), tag('h', 'a', 'l', 'f'),
    tag('p', 's', 't', 'f'), tag('v', 'a', 't', 'u'),
    tag('c', 'j', 'c', 't'), tag('i', 'n', 'i', 't'),
    tag('p', 'r', 'e', 's'), tag('a', 'b', 'v', 's'),
    tag('b', 'l', 'w', 's'), tag('p', 's', 't', 's'),
    tag('h', 'a', 'l', 'n')};
constexpr std::array use_features{
    tag('n', 'u', 'k', 't'), tag('a', 'k', 'h', 'n'),
    tag('r', 'p', 'h', 'f'), tag('p', 'r', 'e', 'f'),
    tag('r', 'k', 'r', 'f'), tag('a', 'b', 'v', 'f'),
    tag('b', 'l', 'w', 'f'), tag('h', 'a', 'l', 'f'),
    tag('p', 's', 't', 'f'), tag('v', 'a', 't', 'u'),
    tag('c', 'j', 'c', 't'), tag('i', 's', 'o', 'l'),
    tag('i', 'n', 'i', 't'), tag('m', 'e', 'd', 'i'),
    tag('f', 'i', 'n', 'a'), tag('a', 'b', 'v', 's'),
    tag('b', 'l', 'w', 's'), tag('h', 'a', 'l', 'n'),
    tag('p', 'r', 'e', 's'), tag('p', 's', 't', 's')};

constexpr std::array khmer_order{
    tag('r', 'v', 'r', 'n'), tag('f', 'r', 'a', 'c'),
    tag('n', 'u', 'm', 'r'), tag('d', 'n', 'o', 'm'),
    tag('l', 'o', 'c', 'l'), tag('c', 'c', 'm', 'p'),
    tag('p', 'r', 'e', 'f'), tag('b', 'l', 'w', 'f'),
    tag('a', 'b', 'v', 'f'), tag('p', 's', 't', 'f'),
    tag('c', 'f', 'a', 'r'), tag('p', 'r', 'e', 's'),
    tag('a', 'b', 'v', 's'), tag('b', 'l', 'w', 's'),
    tag('p', 's', 't', 's')};
constexpr std::array arabic_order{
    tag('r', 'v', 'r', 'n'), tag('f', 'r', 'a', 'c'),
    tag('n', 'u', 'm', 'r'), tag('d', 'n', 'o', 'm'),
    tag('s', 't', 'c', 'h'), tag('c', 'c', 'm', 'p'),
    tag('l', 'o', 'c', 'l'), tag('i', 's', 'o', 'l'),
    tag('f', 'i', 'n', 'a'), tag('f', 'i', 'n', '2'),
    tag('f', 'i', 'n', '3'), tag('m', 'e', 'd', 'i'),
    tag('m', 'e', 'd', '2'), tag('i', 'n', 'i', 't'),
    tag('r', 'l', 'i', 'g'), tag('c', 'a', 'l', 't'),
    tag('r', 'c', 'l', 't'), tag('l', 'i', 'g', 'a'),
    tag('c', 'l', 'i', 'g'), tag('m', 's', 'e', 't')};
constexpr std::array common_order{
    tag('r', 'v', 'r', 'n'), tag('f', 'r', 'a', 'c'),
    tag('n', 'u', 'm', 'r'), tag('d', 'n', 'o', 'm'),
    tag('l', 'o', 'c', 'l'), tag('c', 'c', 'm', 'p'),
    tag('n', 'u', 'k', 't'), tag('a', 'k', 'h', 'n'),
    tag('r', 'p', 'h', 'f'), tag('p', 'r', 'e', 'f'),
    tag('r', 'k', 'r', 'f'), tag('a', 'b', 'v', 'f'),
    tag('b', 'l', 'w', 'f'), tag('h', 'a', 'l', 'f'),
    tag('p', 's', 't', 'f'), tag('v', 'a', 't', 'u'),
    tag('c', 'j', 'c', 't'), tag('i', 's', 'o', 'l'),
    tag('i', 'n', 'i', 't'), tag('m', 'e', 'd', 'i'),
    tag('f', 'i', 'n', 'a'), tag('a', 'b', 'v', 's'),
    tag('b', 'l', 'w', 's'), tag('h', 'a', 'l', 'n'),
    tag('p', 'r', 'e', 's'), tag('p', 's', 't', 's'),
    tag('l', 'j', 'm', 'o'), tag('v', 'j', 'm', 'o'),
    tag('t', 'j', 'm', 'o')};

void set_error(font_error* error, font_error value) noexcept {
    if (error != nullptr) *error = value;
}

bool contains(std::span<const open_type_tag> tags, open_type_tag value) noexcept {
    return std::find(tags.begin(), tags.end(), value) != tags.end();
}

bool contains(
    std::span<const open_type_feature_setting> features,
    open_type_tag value) noexcept {
    return std::any_of(features.begin(), features.end(), [value](const auto& item) {
        return item.tag == value;
    });
}

bool valid_direction(shaping_direction direction) noexcept {
    return static_cast<std::uint8_t>(direction) <=
        static_cast<std::uint8_t>(shaping_direction::bottom_to_top);
}

bool has_script_policy(const open_type_shaping_route& route) noexcept {
    return route.layout_script == tag('k', 'h', 'm', 'r') ||
        route.layout_script == tag('h', 'a', 'n', 'g') ||
        route.layout_script == tag('m', 'y', 'm', 'r') ||
        route.layout_script == tag('m', 'y', 'm', '2') ||
        route.use_shaper || route.indic_shaper || route.arabic_shaper;
}

std::span<const open_type_tag> script_features(
    const open_type_shaping_route& route) noexcept {
    if (route.layout_script == tag('k', 'h', 'm', 'r')) return khmer_features;
    if (route.layout_script == tag('m', 'y', 'm', 'r') ||
        route.layout_script == tag('m', 'y', 'm', '2')) return myanmar_features;
    if (route.layout_script == tag('h', 'a', 'n', 'g')) return hangul_features;
    if (route.indic_shaper) return indic_features;
    return use_features;
}

std::span<const open_type_tag> ordered_features(
    const open_type_shaping_route& route) noexcept {
    if (route.layout_script == tag('k', 'h', 'm', 'r')) return khmer_order;
    if (route.arabic_shaper) return arabic_order;
    return common_order;
}

std::span<const open_type_tag> directional_features(
    shaping_direction direction) noexcept {
    static constexpr std::array vertical{
        tag('v', 'e', 'r', 't'), tag('v', 'r', 't', '2'),
        tag('v', 'k', 'r', 'n')};
    static constexpr std::array rtl{
        tag('r', 't', 'l', 'a'), tag('r', 't', 'l', 'm')};
    static constexpr std::array ltr{
        tag('l', 't', 'r', 'a'), tag('l', 't', 'r', 'm')};
    if (direction == shaping_direction::top_to_bottom ||
        direction == shaping_direction::bottom_to_top) return vertical;
    return direction == shaping_direction::right_to_left
        ? std::span<const open_type_tag>{rtl}
        : std::span<const open_type_tag>{ltr};
}

bool is_vertical(shaping_direction direction) noexcept {
    return direction == shaping_direction::top_to_bottom ||
        direction == shaping_direction::bottom_to_top;
}

std::uint32_t latest_input_value(
    std::span<const open_type_feature_setting> features,
    open_type_tag requested,
    bool& found) noexcept {
    for (std::size_t index = features.size(); index > 0U; --index) {
        if (features[index - 1U].tag == requested) {
            found = true;
            return features[index - 1U].value;
        }
    }
    found = false;
    return 1U;
}

bool append_unique(
    std::span<shaping_feature> scratch,
    std::uint32_t& count,
    open_type_tag feature,
    std::uint32_t value) noexcept {
    for (std::uint32_t index = 0U; index < count; ++index) {
        if (scratch[index].tag == feature) {
            scratch[index].value = value;
            return true;
        }
    }
    if (count >= scratch.size()) return false;
    scratch[count++] = shaping_feature{feature, value, 0U, 0xFFFFFFFFU};
    return true;
}

bool emit_if_present(
    std::span<const shaping_feature> dictionary,
    open_type_tag feature,
    std::span<open_type_tag> output,
    std::uint32_t& count) noexcept {
    const bool exists = std::any_of(
        dictionary.begin(), dictionary.end(), [feature](const auto& item) {
            return item.tag == feature;
        });
    if (!exists || contains(output.first(count), feature)) return true;
    if (count >= output.size()) return false;
    output[count++] = feature;
    return true;
}

} // namespace

std::span<const open_type_feature_setting>
get_default_open_type_feature_settings() noexcept {
    return default_features;
}

bool try_get_open_type_feature_plan_requirements(
    const open_type_shaping_route& route,
    std::span<const open_type_feature_setting> base_features,
    open_type_feature_plan_requirements& result,
    font_error* error) noexcept {
    result = {};
    if (!valid_direction(route.direction) ||
        base_features.size() > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    std::size_t count = 0U;
    std::size_t scratch_count = 0U;
    if (!has_script_policy(route)) {
        count = base_features.size();
        if (is_vertical(route.direction)) {
            count -= static_cast<std::size_t>(std::count_if(
                base_features.begin(), base_features.end(), [](const auto& item) {
                    return item.tag == tag('k', 'e', 'r', 'n');
                }));
        }
        for (const auto feature : directional_features(route.direction)) {
            if (!contains(base_features, feature)) ++count;
        }
    } else {
        for (std::size_t index = 0U; index < base_features.size(); ++index) {
            bool first = true;
            for (std::size_t prior = 0U; prior < index; ++prior) {
                first &= base_features[prior].tag != base_features[index].tag;
            }
            count += first ? 1U : 0U;
        }
        scratch_count = count;
        const auto add_if_missing = [&](open_type_tag feature) {
            if (!contains(base_features, feature)) {
                ++count;
                ++scratch_count;
            }
        };
        for (const auto feature : script_features(route)) add_if_missing(feature);
        if (route.layout_script == tag('k', 'h', 'm', 'r')) {
            add_if_missing(tag('c', 'l', 'i', 'g'));
            // liga is inserted and disabled when it was not explicitly
            // requested; reserve it even though explicit tags are supplied
            // only to the write pass.
            add_if_missing(tag('l', 'i', 'g', 'a'));
        } else if (route.indic_shaper) {
            add_if_missing(tag('l', 'i', 'g', 'a'));
        } else if (route.arabic_shaper) {
            add_if_missing(tag('s', 't', 'c', 'h'));
            add_if_missing(tag('m', 's', 'e', 't'));
        }
        if (is_vertical(route.direction) && contains(base_features, tag('k', 'e', 'r', 'n'))) {
            --count;
        }
        for (const auto feature : directional_features(route.direction)) {
            bool present = contains(base_features, feature) ||
                contains(script_features(route), feature);
            if (!present) ++count;
        }
    }
    if (count > std::numeric_limits<std::uint32_t>::max()) {
        set_error(error, font_error::invalid_argument);
        return false;
    }
    const auto capacity = std::max(count, scratch_count);
    result.requested_feature_capacity = static_cast<std::uint32_t>(capacity);
    result.feature_setting_capacity = static_cast<std::uint32_t>(capacity);
    set_error(error, font_error::none);
    return true;
}

bool try_resolve_open_type_feature_plan(
    const open_type_shaping_route& route,
    std::span<const open_type_feature_setting> base_features,
    std::span<const open_type_tag> explicit_features,
    std::span<open_type_tag> requested_features_output,
    std::span<shaping_feature> feature_settings_output,
    std::uint32_t& requested_features_written,
    std::uint32_t& feature_settings_written,
    font_error* error) noexcept {
    requested_features_written = 0U;
    feature_settings_written = 0U;
    open_type_feature_plan_requirements requirements{};
    if (!try_get_open_type_feature_plan_requirements(
            route, base_features, requirements, error)) return false;
    if (requested_features_output.size() < requirements.requested_feature_capacity ||
        feature_settings_output.size() < requirements.feature_setting_capacity) {
        set_error(error, font_error::insufficient_buffer);
        return false;
    }

    std::uint32_t count = 0U;
    const bool script_policy = has_script_policy(route);
    if (!script_policy) {
        for (const auto feature : directional_features(route.direction)) {
            if (!contains(base_features, feature)) {
                requested_features_output[count++] = feature;
            }
        }
        for (const auto& feature : base_features) {
            if (is_vertical(route.direction) &&
                feature.tag == tag('k', 'e', 'r', 'n')) continue;
            requested_features_output[count++] = feature.tag;
        }
    } else {
        std::uint32_t dictionary_count = 0U;
        for (const auto& feature : base_features) {
            if (!append_unique(
                    feature_settings_output,
                    dictionary_count,
                    feature.tag,
                    feature.value)) return false;
        }
        for (const auto feature : script_features(route)) {
            bool found = false;
            latest_input_value(base_features, feature, found);
            if (!found && !append_unique(
                    feature_settings_output, dictionary_count, feature, 1U)) return false;
        }
        if (route.layout_script == tag('k', 'h', 'm', 'r')) {
            if (!contains(base_features, tag('c', 'l', 'i', 'g')) &&
                !append_unique(feature_settings_output, dictionary_count,
                    tag('c', 'l', 'i', 'g'), 1U)) return false;
            if (!contains(explicit_features, tag('l', 'i', 'g', 'a'))) {
                if (!append_unique(feature_settings_output, dictionary_count,
                        tag('l', 'i', 'g', 'a'), 0U)) return false;
            }
        } else if (route.indic_shaper) {
            if (!append_unique(feature_settings_output, dictionary_count,
                    tag('l', 'i', 'g', 'a'), 0U)) return false;
        } else if (route.arabic_shaper) {
            if (!contains(base_features, tag('s', 't', 'c', 'h')) &&
                !append_unique(feature_settings_output, dictionary_count,
                    tag('s', 't', 'c', 'h'), 1U)) return false;
            if (!contains(base_features, tag('m', 's', 'e', 't')) &&
                !append_unique(feature_settings_output, dictionary_count,
                    tag('m', 's', 'e', 't'), 1U)) return false;
        }

        for (const auto feature : directional_features(route.direction)) {
            const auto dictionary = feature_settings_output.first(dictionary_count);
            const bool present = std::any_of(
                dictionary.begin(), dictionary.end(), [feature](const auto& item) {
                    return item.tag == feature;
                });
            if (!present) requested_features_output[count++] = feature;
        }
        const auto dictionary = feature_settings_output.first(dictionary_count);
        for (const auto feature : ordered_features(route)) {
            if (!emit_if_present(dictionary, feature, requested_features_output, count)) return false;
        }
        for (const auto& feature : base_features) {
            if (!emit_if_present(dictionary, feature.tag, requested_features_output, count)) return false;
        }
        for (const auto& feature : dictionary) {
            if (!emit_if_present(dictionary, feature.tag, requested_features_output, count)) return false;
        }
        if (is_vertical(route.direction)) {
            const auto kern = tag('k', 'e', 'r', 'n');
            const auto end = requested_features_output.begin() + count;
            count = static_cast<std::uint32_t>(std::remove(
                requested_features_output.begin(), end, kern) -
                requested_features_output.begin());
        }
    }

    std::uint32_t settings_count = 0U;
    std::size_t original_index = 0U;
    for (std::uint32_t index = 0U; index < count; ++index) {
        const auto feature = requested_features_output[index];
        bool found = false;
        std::uint32_t value = 1U;
        if (!script_policy) {
            const auto inserted = count - static_cast<std::uint32_t>(
                base_features.size() - (is_vertical(route.direction)
                    ? std::count_if(base_features.begin(), base_features.end(),
                        [](const auto& item) {
                            return item.tag == tag('k', 'e', 'r', 'n');
                        })
                    : 0U));
            if (index >= inserted) {
                while (is_vertical(route.direction) &&
                    original_index < base_features.size() &&
                    base_features[original_index].tag == tag('k', 'e', 'r', 'n')) {
                    ++original_index;
                }
                value = base_features[original_index++].value;
                found = true;
            }
        } else {
            value = latest_input_value(base_features, feature, found);
        }
        if (!found) value = 1U;
        if (feature == tag('l', 'i', 'g', 'a') && route.indic_shaper) {
            value = 0U;
        } else if (feature == tag('l', 'i', 'g', 'a') &&
            route.layout_script == tag('k', 'h', 'm', 'r') &&
            !contains(explicit_features, feature)) {
            value = 0U;
        }
        if (value != 1U) {
            feature_settings_output[settings_count++] =
                shaping_feature{feature, value, 0U, 0xFFFFFFFFU};
        }
    }
    requested_features_written = count;
    feature_settings_written = settings_count;
    set_error(error, font_error::none);
    return true;
}

} // namespace progpu::native::text
