#include "progpu_native_svg_document_internal.hpp"

#include <cctype>
#include <charconv>
#include <limits>

// Direct native port provenance: ProGPU-owned OpenTypeSvgGlyphParser XML
// contract at checkpoint b7849116. This intentionally implements only the
// bounded, non-DTD element/attribute surface consumed by the original parser;
// no third-party XML implementation is embedded.
namespace progpu::native::text::svg_document_detail {
namespace {

bool name_character(char value) noexcept {
    const auto byte = static_cast<unsigned char>(value);
    return std::isalnum(byte) != 0 || value == '_' || value == '-' ||
        value == ':' || value == '.';
}

void skip_space(std::string_view text, std::size_t& index) noexcept {
    while (index < text.size() &&
        std::isspace(static_cast<unsigned char>(text[index])) != 0) {
        ++index;
    }
}

bool append_code_point(std::string& output, std::uint32_t value) {
    if (value == 0U || value > 0x10FFFFU ||
        (value >= 0xD800U && value <= 0xDFFFU)) {
        return false;
    }
    if (value <= 0x7FU) {
        output.push_back(static_cast<char>(value));
    } else if (value <= 0x7FFU) {
        output.push_back(static_cast<char>(0xC0U | (value >> 6U)));
        output.push_back(static_cast<char>(0x80U | (value & 0x3FU)));
    } else if (value <= 0xFFFFU) {
        output.push_back(static_cast<char>(0xE0U | (value >> 12U)));
        output.push_back(static_cast<char>(0x80U | ((value >> 6U) & 0x3FU)));
        output.push_back(static_cast<char>(0x80U | (value & 0x3FU)));
    } else {
        output.push_back(static_cast<char>(0xF0U | (value >> 18U)));
        output.push_back(static_cast<char>(0x80U | ((value >> 12U) & 0x3FU)));
        output.push_back(static_cast<char>(0x80U | ((value >> 6U) & 0x3FU)));
        output.push_back(static_cast<char>(0x80U | (value & 0x3FU)));
    }
    return true;
}

bool decode_entities(std::string_view source, std::string& output) {
    output.clear();
    output.reserve(source.size());
    std::size_t cursor = 0U;
    while (cursor < source.size()) {
        if (source[cursor] != '&') {
            output.push_back(source[cursor++]);
            continue;
        }
        const auto end = source.find(';', cursor + 1U);
        if (end == std::string_view::npos) {
            return false;
        }
        const auto entity = source.substr(cursor + 1U, end - cursor - 1U);
        if (entity == "amp") {
            output.push_back('&');
        } else if (entity == "lt") {
            output.push_back('<');
        } else if (entity == "gt") {
            output.push_back('>');
        } else if (entity == "quot") {
            output.push_back('"');
        } else if (entity == "apos") {
            output.push_back('\'');
        } else if (entity.size() > 1U && entity[0] == '#') {
            std::uint32_t value = 0U;
            const bool hexadecimal = entity.size() > 2U &&
                (entity[1] == 'x' || entity[1] == 'X');
            const auto digits = entity.substr(hexadecimal ? 2U : 1U);
            if (digits.empty()) {
                return false;
            }
            const auto parsed = std::from_chars(
                digits.data(), digits.data() + digits.size(), value,
                hexadecimal ? 16 : 10);
            if (parsed.ec != std::errc{} ||
                parsed.ptr != digits.data() + digits.size() ||
                !append_code_point(output, value)) {
                return false;
            }
        } else {
            return false;
        }
        cursor = end + 1U;
    }
    return true;
}

bool parse_name(
    std::string_view text,
    std::size_t& index,
    std::string& result) {
    const auto start = index;
    while (index < text.size() && name_character(text[index])) {
        ++index;
    }
    if (start == index) {
        return false;
    }
    result.assign(text.substr(start, index - start));
    return true;
}

bool skip_markup(
    std::string_view text,
    std::size_t& index,
    std::string_view terminator) noexcept {
    const auto end = text.find(terminator, index);
    if (end == std::string_view::npos) {
        return false;
    }
    index = end + terminator.size();
    return true;
}

} // namespace

std::string_view local_name(std::string_view qualified_name) noexcept {
    const auto separator = qualified_name.rfind(':');
    return separator == std::string_view::npos
        ? qualified_name
        : qualified_name.substr(separator + 1U);
}

const std::string* find_attribute(
    const node& element,
    std::string_view requested_name) noexcept {
    for (const auto& item : element.attributes) {
        if (local_name(item.name) == requested_name) {
            return &item.value;
        }
    }
    return nullptr;
}

bool equals_ascii_ignore_case(
    std::string_view left,
    std::string_view right) noexcept {
    if (left.size() != right.size()) {
        return false;
    }
    for (std::size_t index = 0U; index < left.size(); ++index) {
        const auto a = static_cast<unsigned char>(left[index]);
        const auto b = static_cast<unsigned char>(right[index]);
        if (std::tolower(a) != std::tolower(b)) {
            return false;
        }
    }
    return true;
}

bool parse_document(std::string_view xml, document& result) noexcept {
    result = {};
    if (xml.empty() || xml.size() > maximum_document_bytes) {
        return false;
    }
    try {
        std::vector<std::size_t> stack;
        std::size_t index = 0U;
        while (index < xml.size()) {
            const auto opening = xml.find('<', index);
            if (opening == std::string_view::npos) {
                break;
            }
            index = opening;
            if (xml.substr(index, 4U) == "<!--") {
                index += 4U;
                if (!skip_markup(xml, index, "-->")) {
                    return false;
                }
                continue;
            }
            if (xml.substr(index, 2U) == "<?") {
                index += 2U;
                if (!skip_markup(xml, index, "?>")) {
                    return false;
                }
                continue;
            }
            if (xml.substr(index, 9U) == "<![CDATA[") {
                index += 9U;
                if (!skip_markup(xml, index, "]]>") ) {
                    return false;
                }
                continue;
            }
            if (xml.substr(index, 2U) == "<!") {
                return false;
            }
            index += 1U;
            if (index < xml.size() && xml[index] == '/') {
                ++index;
                skip_space(xml, index);
                std::string closing_name;
                if (!parse_name(xml, index, closing_name)) {
                    return false;
                }
                skip_space(xml, index);
                if (index >= xml.size() || xml[index++] != '>' ||
                    stack.empty() ||
                    result.nodes[stack.back()].name != closing_name) {
                    return false;
                }
                stack.pop_back();
                continue;
            }

            skip_space(xml, index);
            node element{};
            if (!parse_name(xml, index, element.name)) {
                return false;
            }
            bool self_closing = false;
            while (index < xml.size()) {
                skip_space(xml, index);
                if (index >= xml.size()) {
                    return false;
                }
                if (xml[index] == '>') {
                    ++index;
                    break;
                }
                if (xml[index] == '/' && index + 1U < xml.size() &&
                    xml[index + 1U] == '>') {
                    index += 2U;
                    self_closing = true;
                    break;
                }
                attribute item{};
                if (!parse_name(xml, index, item.name)) {
                    return false;
                }
                skip_space(xml, index);
                if (index >= xml.size() || xml[index++] != '=') {
                    return false;
                }
                skip_space(xml, index);
                if (index >= xml.size() ||
                    (xml[index] != '"' && xml[index] != '\'')) {
                    return false;
                }
                const char quote = xml[index++];
                const auto end = xml.find(quote, index);
                if (end == std::string_view::npos ||
                    !decode_entities(xml.substr(index, end - index),
                        item.value)) {
                    return false;
                }
                index = end + 1U;
                element.attributes.push_back(std::move(item));
            }

            element.parent = stack.empty() ? no_node : stack.back();
            const auto node_index = result.nodes.size();
            result.nodes.push_back(std::move(element));
            if (result.nodes[node_index].parent != no_node) {
                result.nodes[result.nodes[node_index].parent].children.push_back(
                    node_index);
            } else if (result.root == no_node) {
                result.root = node_index;
            } else {
                return false;
            }
            if (const auto* id = find_attribute(result.nodes[node_index], "id");
                id != nullptr && !id->empty()) {
                result.ids.try_emplace(*id, node_index);
            }
            if (!self_closing) {
                stack.push_back(node_index);
            }
        }
        return result.root != no_node && stack.empty();
    } catch (...) {
        result = {};
        return false;
    }
}

} // namespace progpu::native::text::svg_document_detail
