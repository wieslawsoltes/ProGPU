#include "progpu_native_text_source.h"
#include "progpu_native_hinted_shape_fixture.hpp"
#include "../src/Text/Interop/progpu_native_hinted_paragraph_transport_internal.hpp"
#include <array>
#include <cmath>
#include <cstring>
#include <iostream>
#include <source_location>
#include <stdexcept>
#include <string>

namespace {
void require(bool value, std::source_location at = std::source_location::current()) {
    if (!value) throw std::runtime_error("source transport control at " + std::to_string(at.line()));
}
struct context_owner final {
    progpu_native_text_context* value = nullptr;
    ~context_owner() { progpu_native_text_context_destroy(value); }
};
struct paragraph_owner final {
    progpu_native_hinted_paragraph* value = nullptr;
    ~paragraph_owner() { progpu_native_hinted_paragraph_destroy(value); }
};
struct resource_owner final {
    progpu_native_hinted_glyph_resource* value = nullptr;
    ~resource_owner() { progpu_native_hinted_glyph_resource_destroy(value); }
};
void controls() {
    const auto bytes = progpu::native::tests::make_hinted_shape_font();
    context_owner context;
    require(progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION,
        reinterpret_cast<const std::uint8_t*>(bytes.data()), bytes.size(), 0U, nullptr, 0U, &context.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    progpu::native::text::sfnt_font_view font{};
    progpu::native::text::sfnt_header_metrics header{};
    require(progpu::native::text::sfnt_font_view::try_create(bytes, 0U, font) && font.try_get_header_metrics(header));
    std::array<progpu_native_text_scalar, 3U> input{{{'A', 0U, 1U, 0U, 0U, 0U},
        {' ', 1U, 1U, 0U, 0U, 0U}, {'B', 2U, 1U, 0U, 0U, 0U}}};
    progpu_native_text_shape_request shape{};
    shape.abi_version = PROGPU_NATIVE_ABI_VERSION; shape.struct_size = sizeof(shape);
    shape.input = input.data(); shape.input_count = static_cast<std::uint32_t>(input.size());
    shape.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    progpu_native_text_layout_options layout{}; layout.struct_size = sizeof(layout); layout.scale = 1.0F;
    layout.maximum_width = 200.0F; layout.direction = PROGPU_NATIVE_TEXT_DIRECTION_LEFT_TO_RIGHT;
    progpu_native_text_style_run style{}; style.scalar_count = shape.input_count;
    style.scale = 10.0F / static_cast<float>(header.units_per_em);
    progpu_native_text_style_metrics metrics{8.0F, 2.0F};
    progpu_native_hinted_paragraph_device_style device{0U, style.scale, static_cast<float>(1.0 / 1.5),
        960U, 960U, 40U, 0U, 0U, 0U, 0U, 0U};
    progpu_native_hinted_source_options options{PROGPU_NATIVE_ABI_VERSION, sizeof(options), 1U, 0U,
        10.0, 1.5, 200.0, 0.0, 0.0, 0U, 0U, 0U, 1U};
    progpu_native_hinted_source_style source{10.0, 8.0, 2.0};
    paragraph_owner paragraph;
    progpu_native_text_paragraph_result diagnostic{}; diagnostic.struct_size = sizeof(diagnostic); diagnostic.error_code = 71U;
    const auto create = [&] { return progpu_native_text_context_layout_hinted_source_paragraph(context.value, &shape, &layout,
        &style, 1U, &metrics, &device, 1U, nullptr, 0U, &options, &source, 1U, &paragraph.value, &diagnostic); };
    const auto saved_diagnostic = diagnostic;
    options.offset_policy = 2U;
    require(create() == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && paragraph.value == nullptr &&
        std::memcmp(&diagnostic, &saved_diagnostic, sizeof(diagnostic)) == 0);
    options.offset_policy = 0U; options.em_policy = 4U;
    require(create() == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && paragraph.value == nullptr &&
        std::memcmp(&diagnostic, &saved_diagnostic, sizeof(diagnostic)) == 0);
    options.em_policy = 0U; options.advance_policy = 3U;
    require(create() == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && paragraph.value == nullptr &&
        std::memcmp(&diagnostic, &saved_diagnostic, sizeof(diagnostic)) == 0);
    options.advance_policy = 0U; options.flags = 2U;
    require(create() == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && paragraph.value == nullptr &&
        std::memcmp(&diagnostic, &saved_diagnostic, sizeof(diagnostic)) == 0);
    options.flags = PROGPU_NATIVE_SOURCE_MEASURE_INTRINSIC_WIDTHS;
#if defined(PROGPU_NATIVE_FONT_HINTING)
    require(create() == PROGPU_NATIVE_STATUS_SUCCESS && paragraph.value != nullptr);
    progpu_native_hinted_source_paragraph_view view{};
    require(progpu_native_hinted_source_paragraph_borrow(paragraph.value, &view) == PROGPU_NATIVE_STATUS_SUCCESS &&
        view.style_count == 1U && view.options.pixels_per_dip == 1.5 && view.line_count == 1U &&
        view.glyph_count != 0U && view.box_count != 0U && view.caret_count != 0U);
    progpu_native_hinted_paragraph_counts counts{};
    require(progpu_native_hinted_paragraph_get_counts(paragraph.value, &counts, &diagnostic) == PROGPU_NATIVE_STATUS_SUCCESS &&
        counts.positioned_glyph_count == view.glyph_count && counts.cluster_box_count == 0U && counts.caret_stop_count == 0U);
    progpu_native_hinted_source_caret_stop caret{};
    const auto first = view.carets[0];
    require(progpu_native_hinted_source_paragraph_get_caret(paragraph.value, first.input_position, first.trailing, &caret) ==
        PROGPU_NATIVE_STATUS_SUCCESS && caret.x == first.x && caret.y == first.y && caret.bidi_level == first.bidi_level);
    const auto saved_caret = caret;
    require(progpu_native_hinted_source_paragraph_get_caret(paragraph.value, first.input_position, 2U, &caret) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && std::memcmp(&caret, &saved_caret, sizeof(caret)) == 0);
    progpu_native_hinted_source_hit hit{};
    require(progpu_native_hinted_source_paragraph_hit_test(paragraph.value, view.boxes[0].x,
        view.boxes[0].y + view.boxes[0].height * 0.5, &hit) == PROGPU_NATIVE_STATUS_SUCCESS && hit.inside != 0U);
    std::array<progpu_native_hinted_source_rectangle, 8U> rectangles{};
    rectangles.back() = {71.0, 72.0, 73.0, 74.0}; std::uint32_t written = 81U;
    require(progpu_native_hinted_source_paragraph_get_selection(paragraph.value, 0, 3, rectangles.data(), 0U, &written) !=
        PROGPU_NATIVE_STATUS_SUCCESS && written == 81U);
    require(progpu_native_hinted_source_paragraph_get_selection(paragraph.value, 0, 3, rectangles.data(), 8U, &written) ==
        PROGPU_NATIVE_STATUS_SUCCESS && written != 0U && rectangles.back().x == 71.0);
    require(progpu_native_hinted_source_paragraph_get_selection(paragraph.value, 0, 3,
        reinterpret_cast<progpu_native_hinted_source_rectangle*>(const_cast<progpu_native_hinted_source_caret_stop*>(view.carets)),
        1U, &written) == PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    require(progpu_native_hinted_source_paragraph_borrow(paragraph.value,
        reinterpret_cast<progpu_native_hinted_source_paragraph_view*>(const_cast<progpu_native_hinted_source_logical_metrics*>(view.logical_metrics))) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT);
    paragraph_owner reflow;
    require(progpu_native_hinted_source_paragraph_reflow(paragraph.value, 0, 100.0 / 1.5, &reflow.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    progpu_native_hinted_source_paragraph_view continued{};
    require(progpu_native_hinted_source_paragraph_borrow(reflow.value, &continued) == PROGPU_NATIVE_STATUS_SUCCESS &&
        continued.options.maximum_width == 100.0 / 1.5 && continued.options.pixels_per_dip == 1.5 && continued.styles[0].em_size == 10.0);
    progpu_native_hinted_source_intrinsic_widths widths{}, reflow_widths{};
    require(progpu_native_hinted_source_paragraph_get_intrinsic_widths(paragraph.value, &widths) == PROGPU_NATIVE_STATUS_SUCCESS &&
        widths.minimum > 0.0 && widths.maximum >= widths.minimum);
    require(progpu_native_hinted_source_paragraph_get_intrinsic_widths(reflow.value, &reflow_widths) == PROGPU_NATIVE_STATUS_SUCCESS &&
        reflow_widths.minimum == widths.minimum && reflow_widths.maximum == widths.maximum);
    paragraph_owner unmeasured;
    auto unmeasured_options = options; unmeasured_options.flags = 0U;
    require(progpu_native_text_context_layout_hinted_source_paragraph(context.value, &shape, &layout, &style, 1U,
        &metrics, &device, 1U, nullptr, 0U, &unmeasured_options, &source, 1U, &unmeasured.value, &diagnostic) == PROGPU_NATIVE_STATUS_SUCCESS);
    const auto saved_widths = widths;
    require(progpu_native_hinted_source_paragraph_get_intrinsic_widths(unmeasured.value, &widths) == PROGPU_NATIVE_STATUS_UNSUPPORTED &&
        std::memcmp(&widths, &saved_widths, sizeof(widths)) == 0);
    paragraph_owner source_policy;
    auto policy_options = options;
    policy_options.em_size = std::nextafter(10.0, 11.0);
    policy_options.pixels_per_dip = std::nextafter(1.5, 2.0);
    policy_options.em_policy = PROGPU_NATIVE_SOURCE_EM_FLOAT_CAPTURE_NEAREST_HALF_UP;
    policy_options.advance_policy = PROGPU_NATIVE_SOURCE_ADVANCE_IDEAL_UNITS;
    policy_options.offset_policy = PROGPU_NATIVE_SOURCE_OFFSET_IDEAL_UNITS;
    auto policy_style = source; policy_style.em_size = policy_options.em_size;
    require(progpu_native_text_context_layout_hinted_source_paragraph(context.value, &shape, &layout, &style, 1U,
        &metrics, &device, 1U, nullptr, 0U, &policy_options, &policy_style, 1U, &source_policy.value, &diagnostic) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    progpu_native_hinted_source_paragraph_view policy_view{};
    require(progpu_native_hinted_source_paragraph_borrow(source_policy.value, &policy_view) == PROGPU_NATIVE_STATUS_SUCCESS &&
        std::memcmp(&policy_view.options, &policy_options, sizeof(policy_options)) == 0 &&
        policy_view.styles[0].em_size == policy_style.em_size && policy_view.glyph_count == view.glyph_count);
    paragraph_owner policy_reflow;
    require(progpu_native_hinted_source_paragraph_reflow(source_policy.value, 0, 100.0 / 1.5, &policy_reflow.value) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    progpu_native_hinted_source_paragraph_view policy_continued{};
    auto continued_policy_options = policy_options; continued_policy_options.maximum_width = 100.0 / 1.5;
    require(progpu_native_hinted_source_paragraph_borrow(policy_reflow.value, &policy_continued) == PROGPU_NATIVE_STATUS_SUCCESS &&
        std::memcmp(&policy_continued.options, &continued_policy_options, sizeof(continued_policy_options)) == 0 &&
        policy_continued.styles[0].em_size == policy_style.em_size);
    paragraph_owner wrapped;
    require(progpu_native_hinted_source_paragraph_reflow(paragraph.value, 0, 1.0, &wrapped.value) == PROGPU_NATIVE_STATUS_SUCCESS);
    progpu_native_hinted_source_paragraph_view wrapped_view{};
    require(progpu_native_hinted_source_paragraph_borrow(wrapped.value, &wrapped_view) == PROGPU_NATIVE_STATUS_SUCCESS && wrapped_view.line_count >= 2U);
    bool shared_boundary = false;
    for (std::uint32_t i = 0U; i < wrapped_view.caret_count; ++i) {
        const auto c = wrapped_view.carets[i];
        require(progpu_native_hinted_source_paragraph_get_line_caret(wrapped.value, c.line_index, c.input_position, c.trailing, &caret) ==
            PROGPU_NATIVE_STATUS_SUCCESS && caret.line_index == c.line_index && caret.x == c.x && caret.y == c.y);
        for (std::uint32_t j = 0U; j < i; ++j)
            if (wrapped_view.carets[j].input_position == c.input_position && wrapped_view.carets[j].line_index != c.line_index)
                shared_boundary = true;
    }
    require(shared_boundary);
    for (std::uint32_t line = 0U; line < wrapped_view.line_count; ++line) {
        require(progpu_native_hinted_source_paragraph_hit_test_line(wrapped.value, line, 0.0, &hit) ==
            PROGPU_NATIVE_STATUS_SUCCESS && hit.line_index == line);
        require(progpu_native_hinted_source_paragraph_get_line_selection(wrapped.value, line, 0, 3, rectangles.data(), 8U, &written) ==
            PROGPU_NATIVE_STATUS_SUCCESS);
        for (std::uint32_t i = 0U; i < written; ++i) require(rectangles[i].y == wrapped_view.line_metrics[line].top);
    }
    const auto line_caret = caret; const auto line_hit = hit; const auto line_rectangles = rectangles; const auto line_written = written;
    require(progpu_native_hinted_source_paragraph_get_line_caret(wrapped.value, UINT32_MAX, 0, 0U, &caret) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && std::memcmp(&caret, &line_caret, sizeof(caret)) == 0);
    require(progpu_native_hinted_source_paragraph_hit_test_line(wrapped.value, UINT32_MAX, 0.0, &hit) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && std::memcmp(&hit, &line_hit, sizeof(hit)) == 0);
    require(progpu_native_hinted_source_paragraph_get_line_selection(wrapped.value, UINT32_MAX, 0, 3, rectangles.data(), 8U, &written) ==
        PROGPU_NATIVE_STATUS_INVALID_ARGUMENT && written == line_written && std::memcmp(rectangles.data(), line_rectangles.data(), sizeof(rectangles)) == 0);
    resource_owner resource;
    progpu_native_hinted_glyph_resource_request request{PROGPU_NATIVE_ABI_VERSION, sizeof(request), 1.5F,
        PROGPU_NATIVE_HINTED_PROJECTION_SCALAR_REFERENCE, PROGPU_NATIVE_HINTED_COVERAGE_ANTIALIASED_VECTOR, 0U};
    require(progpu_native_hinted_paragraph_prepare_glyph_resource_with_nominal_metrics(paragraph.value, &request, &resource.value) ==
        PROGPU_NATIVE_STATUS_SUCCESS);
    progpu_native_hinted_paragraph_destroy(paragraph.value); paragraph.value = nullptr;
    const std::uint32_t index = 0U; std::array<double, 2U> advances{71.0, 72.0};
    std::array<progpu_native_hinted_source_glyph_offset, 2U> offsets{{{81, 82}, {83, 84}}};
    require(progpu_native_hinted_glyph_resource_copy_source_metrics(resource.value, &index, 1U, 10.0, 1.5,
        advances.data(), 2U, offsets.data(), 2U) == PROGPU_NATIVE_STATUS_SUCCESS && advances[1] == 72.0 && offsets[1].x == 83.0);
    require(progpu_native_hinted_glyph_resource_copy_source_offsets(resource.value, &index, 1U, 10.0F,
        offsets.data(), 2U) == PROGPU_NATIVE_STATUS_UNSUPPORTED);
#else
    require(create() == PROGPU_NATIVE_STATUS_UNSUPPORTED && paragraph.value == nullptr &&
        std::memcmp(&diagnostic, &saved_diagnostic, sizeof(diagnostic)) == 0);
#endif
}
} // namespace
int main() {
    try { controls(); return 0; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
