#include "progpu_native_svg_document_internal.hpp"
#include "progpu_native_svg_number.hpp"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <numbers>

// Direct native port provenance: ProGPU-owned OpenTypeSvgGlyphParser value,
// color, and System.Numerics row-vector transform semantics at checkpoint
// b7849116.
namespace progpu::native::text::svg_document_detail {
namespace {

std::string_view trim(std::string_view value) noexcept {
    while (!value.empty() &&
        std::isspace(static_cast<unsigned char>(value.front())) != 0) {
        value.remove_prefix(1U);
    }
    while (!value.empty() &&
        std::isspace(static_cast<unsigned char>(value.back())) != 0) {
        value.remove_suffix(1U);
    }
    return value;
}

void skip_separators(std::string_view text, std::size_t& index) noexcept {
    while (index < text.size() &&
        (std::isspace(static_cast<unsigned char>(text[index])) != 0 ||
            text[index] == ',')) {
        ++index;
    }
}

bool read_number(
    std::string_view text,
    std::size_t& index,
    float& result) noexcept {
    skip_separators(text, index);
    if (index >= text.size()) {
        return false;
    }
    const auto start = index;
    if (text[index] == '+' || text[index] == '-') {
        ++index;
    }
    bool digits = false;
    while (index < text.size() && text[index] >= '0' && text[index] <= '9') {
        digits = true;
        ++index;
    }
    if (index < text.size() && text[index] == '.') {
        ++index;
        while (index < text.size() && text[index] >= '0' &&
            text[index] <= '9') {
            digits = true;
            ++index;
        }
    }
    if (!digits) {
        index = start;
        return false;
    }
    if (index < text.size() && (text[index] == 'e' || text[index] == 'E')) {
        const auto exponent = index++;
        if (index < text.size() &&
            (text[index] == '+' || text[index] == '-')) {
            ++index;
        }
        const auto exponent_digits = index;
        while (index < text.size() && text[index] >= '0' &&
            text[index] <= '9') {
            ++index;
        }
        if (exponent_digits == index) {
            index = exponent;
        }
    }
    std::size_t parsed_index = start;
    return svg_number_detail::try_parse(text, parsed_index, result) &&
        parsed_index == index;
}

float percentage_or_number(
    std::string_view text,
    float default_value) noexcept {
    text = trim(text);
    if (text.empty()) {
        return default_value;
    }
    float scale = 1.0F;
    if (text.back() == '%') {
        text.remove_suffix(1U);
        scale = 0.01F;
    }
    float value = 0.0F;
    if (!svg_number_detail::try_parse_exact(text, value)) {
        return default_value;
    }
    return value * scale;
}

int hex_nibble(char value) noexcept {
    if (value >= '0' && value <= '9') {
        return value - '0';
    }
    if (value >= 'a' && value <= 'f') {
        return value - 'a' + 10;
    }
    if (value >= 'A' && value <= 'F') {
        return value - 'A' + 10;
    }
    return -1;
}

bool parse_hex_byte(std::string_view text, std::size_t offset, float& value) {
    const int high = hex_nibble(text[offset]);
    const int low = hex_nibble(text[offset + 1U]);
    if (high < 0 || low < 0) {
        return false;
    }
    value = static_cast<float>((high << 4) | low) / 255.0F;
    return true;
}

progpu_native_affine_2d create_transform(
    std::string_view name,
    const std::vector<float>& values) noexcept {
    auto result = identity_transform();
    if (equals_ascii_ignore_case(name, "matrix") && values.size() >= 6U) {
        return {values[0], values[1], values[2], values[3], values[4],
            values[5]};
    }
    if (equals_ascii_ignore_case(name, "translate") && !values.empty()) {
        result.m31 = values[0];
        result.m32 = values.size() > 1U ? values[1] : 0.0F;
    } else if (equals_ascii_ignore_case(name, "scale") && !values.empty()) {
        result.m11 = values[0];
        result.m22 = values.size() > 1U ? values[1] : values[0];
    } else if (equals_ascii_ignore_case(name, "rotate") && !values.empty()) {
        const float angle = values[0] * std::numbers::pi_v<float> / 180.0F;
        const float cosine = std::cos(angle);
        const float sine = std::sin(angle);
        result = {cosine, sine, -sine, cosine, 0.0F, 0.0F};
        if (values.size() >= 3U) {
            progpu_native_affine_2d to_origin = identity_transform();
            to_origin.m31 = -values[1];
            to_origin.m32 = -values[2];
            progpu_native_affine_2d back = identity_transform();
            back.m31 = values[1];
            back.m32 = values[2];
            result = multiply(multiply(to_origin, result), back);
        }
    } else if (equals_ascii_ignore_case(name, "skewx") && !values.empty()) {
        result.m21 = std::tan(
            values[0] * std::numbers::pi_v<float> / 180.0F);
    } else if (equals_ascii_ignore_case(name, "skewy") && !values.empty()) {
        result.m12 = std::tan(
            values[0] * std::numbers::pi_v<float> / 180.0F);
    }
    return result;
}

} // namespace

float read_float(
    const node& element,
    std::string_view name,
    float default_value) noexcept {
    const auto* value = find_attribute(element, name);
    if (value == nullptr) {
        return default_value;
    }
    const auto text = trim(*value);
    float result = 0.0F;
    if (!svg_number_detail::try_parse_exact(text, result)) {
        return default_value;
    }
    return result;
}

float read_coordinate(
    const node& element,
    std::string_view name,
    float default_value,
    std::uint16_t units_per_em) noexcept {
    const auto* value = find_attribute(element, name);
    if (value == nullptr) {
        return default_value;
    }
    const auto text = trim(*value);
    if (!text.empty() && text.back() == '%') {
        auto percent = text;
        percent.remove_suffix(1U);
        percent = trim(percent);
        float result = 0.0F;
        return svg_number_detail::try_parse_exact(percent, result)
            ? result * 0.01F * units_per_em
            : default_value;
    }
    return read_float(element, name, default_value);
}

float read_unit_interval(
    const node& element,
    std::string_view name,
    float default_value) noexcept {
    const auto* value = find_attribute(element, name);
    return std::clamp(
        value == nullptr ? default_value :
            percentage_or_number(*value, default_value),
        0.0F, 1.0F);
}

bool parse_number_list(
    std::string_view text,
    std::vector<float>& values) noexcept {
    values.clear();
    try {
        std::size_t index = 0U;
        while (true) {
            skip_separators(text, index);
            if (index >= text.size()) {
                return true;
            }
            float value = 0.0F;
            if (!read_number(text, index, value)) {
                return true;
            }
            values.push_back(value);
        }
    } catch (...) {
        values.clear();
        return false;
    }
}

progpu_native_affine_2d identity_transform() noexcept {
    return {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
}

progpu_native_affine_2d multiply(
    const progpu_native_affine_2d& left,
    const progpu_native_affine_2d& right) noexcept {
    return {
        left.m11 * right.m11 + left.m12 * right.m21,
        left.m11 * right.m12 + left.m12 * right.m22,
        left.m21 * right.m11 + left.m22 * right.m21,
        left.m21 * right.m12 + left.m22 * right.m22,
        left.m31 * right.m11 + left.m32 * right.m21 + right.m31,
        left.m31 * right.m12 + left.m32 * right.m22 + right.m32};
}

progpu_native_point transform_point(
    progpu_native_point point,
    const progpu_native_affine_2d& transform) noexcept {
    return {
        point.x * transform.m11 + point.y * transform.m21 + transform.m31,
        point.x * transform.m12 + point.y * transform.m22 + transform.m32};
}

progpu_native_affine_2d parse_transform(std::string_view text) noexcept {
    auto result = identity_transform();
    std::size_t index = 0U;
    std::vector<float> values;
    try {
        while (index < text.size()) {
            skip_separators(text, index);
            const auto start = index;
            while (index < text.size() &&
                std::isalpha(static_cast<unsigned char>(text[index])) != 0) {
                ++index;
            }
            if (start == index) {
                break;
            }
            const auto name = text.substr(start, index - start);
            skip_separators(text, index);
            if (index >= text.size() || text[index++] != '(') {
                break;
            }
            const auto end = text.find(')', index);
            if (end == std::string_view::npos ||
                !parse_number_list(text.substr(index, end - index), values)) {
                break;
            }
            result = multiply(create_transform(name, values), result);
            index = end + 1U;
        }
    } catch (...) {
        return identity_transform();
    }
    return result;
}

bool try_parse_color(
    std::string_view text,
    progpu_native_color& color) noexcept {
    color = {};
    text = trim(text);
    if (text.size() >= 4U &&
        equals_ascii_ignore_case(text.substr(0U, 4U), "var(")) {
        const auto comma = text.find(',');
        text = comma == std::string_view::npos
            ? std::string_view{"black"}
            : trim(text.substr(comma + 1U));
        while (!text.empty() && (text.back() == ')' ||
            std::isspace(static_cast<unsigned char>(text.back())) != 0)) {
            text.remove_suffix(1U);
        }
    }
    if ((text.size() == 4U || text.size() == 5U) && text[0] == '#') {
        const int red = hex_nibble(text[1]);
        const int green = hex_nibble(text[2]);
        const int blue = hex_nibble(text[3]);
        const int alpha = text.size() == 5U ? hex_nibble(text[4]) : 15;
        if (red < 0 || green < 0 || blue < 0 || alpha < 0) {
            return false;
        }
        color = {red / 15.0F, green / 15.0F, blue / 15.0F,
            alpha / 15.0F};
        return true;
    }
    if ((text.size() == 7U || text.size() == 9U) && text[0] == '#') {
        if (!parse_hex_byte(text, 1U, color.r) ||
            !parse_hex_byte(text, 3U, color.g) ||
            !parse_hex_byte(text, 5U, color.b)) {
            return false;
        }
        color.a = 1.0F;
        return text.size() == 7U || parse_hex_byte(text, 7U, color.a);
    }
    if (equals_ascii_ignore_case(text, "black") ||
        equals_ascii_ignore_case(text, "currentcolor")) {
        color = {0.0F, 0.0F, 0.0F, 1.0F};
    } else if (equals_ascii_ignore_case(text, "white")) {
        color = {1.0F, 1.0F, 1.0F, 1.0F};
    } else if (equals_ascii_ignore_case(text, "red")) {
        color = {1.0F, 0.0F, 0.0F, 1.0F};
    } else if (equals_ascii_ignore_case(text, "green")) {
        color = {0.0F, 0.5F, 0.0F, 1.0F};
    } else if (equals_ascii_ignore_case(text, "blue")) {
        color = {0.0F, 0.0F, 1.0F, 1.0F};
    } else if (equals_ascii_ignore_case(text, "transparent")) {
        color = {};
    } else {
        return false;
    }
    return true;
}

} // namespace progpu::native::text::svg_document_detail
