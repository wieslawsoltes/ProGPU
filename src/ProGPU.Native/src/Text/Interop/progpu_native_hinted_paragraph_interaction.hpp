#pragma once

#include "progpu_native_hinted_paragraph_internal.hpp"

namespace progpu::native::text {

struct hinted_paragraph_interaction_result;

struct hinted_source_cluster_box final {
    std::int32_t input_start = 0, input_end = 0;
    std::uint32_t line_index = 0U;
    std::int8_t bidi_level = 0;
    std::uint8_t reserved0 = 0U, reserved1 = 0U, reserved2 = 0U;
    double x = 0.0, y = 0.0, width = 0.0, height = 0.0;
};
struct hinted_source_caret_stop final {
    std::int32_t input_position = 0;
    std::uint32_t line_index = 0U;
    double x = 0.0, y = 0.0, height = 0.0;
    std::int8_t bidi_level = 0;
    bool trailing = false;
    std::uint8_t reserved0 = 0U, reserved1 = 0U;
};
struct hinted_source_rectangle final { double x = 0.0, y = 0.0, width = 0.0, height = 0.0; };
struct hinted_source_hit final {
    std::int32_t input_position = 0;
    std::uint32_t line_index = 0U;
    hinted_source_rectangle bounds{};
    std::int8_t bidi_level = 0;
    bool trailing = false, inside = false;
    std::uint8_t reserved0 = 0U;
};

class hinted_paragraph_interaction final {
public:
    hinted_paragraph_interaction(const hinted_paragraph_interaction&) = delete;
    hinted_paragraph_interaction& operator=(const hinted_paragraph_interaction&) = delete;
    const std::shared_ptr<const hinted_paragraph_generation>& paragraph() const noexcept { return paragraph_; }
    std::span<const text_cluster_box> boxes() const noexcept { return boxes_; }
    std::span<const text_caret_stop> carets() const noexcept { return carets_; }
    std::span<const hinted_source_cluster_box> source_boxes() const noexcept { return source_boxes_; }
    std::span<const hinted_source_caret_stop> source_carets() const noexcept { return source_carets_; }
    bool hit_test_source(double x, double y, hinted_source_hit& result, font_error* error = nullptr) const noexcept;
    bool source_caret(std::int32_t input_position, bool trailing, hinted_source_caret_stop& result,
        font_error* error = nullptr) const noexcept;
    bool source_selection(std::int32_t input_start, std::int32_t input_end,
        std::span<hinted_source_rectangle> rectangles, std::uint32_t& written, font_error* error = nullptr) const noexcept;
    // Complete local allocation capacities, including unused storage. Caller
    // separately checks the reachable paragraph/run/source owners under its lease.
    bool allocation_aliases(const void* output, std::size_t bytes) const noexcept;

private:
    hinted_paragraph_interaction() = default;
    std::shared_ptr<const hinted_paragraph_generation> paragraph_{};
    std::vector<text_cluster_box> boxes_{};
    std::vector<text_caret_stop> carets_{};
    std::vector<hinted_source_cluster_box> source_boxes_{};
    std::vector<hinted_source_caret_stop> source_carets_{};
    friend struct hinted_paragraph_interaction_result;
    friend hinted_paragraph_interaction_result create_hinted_paragraph_interaction(
        std::shared_ptr<const hinted_paragraph_generation>) noexcept;
};

struct hinted_paragraph_interaction_result final {
    progpu_native_status status = PROGPU_NATIVE_STATUS_INVALID_ARGUMENT;
    font_error error = font_error::invalid_argument;
    std::shared_ptr<const hinted_paragraph_interaction> generation{};
};

// One original measured advance-interaction build over the exact retained
// paragraph. No reshape, bidi resolution, ink-derived origin or native global
// metric substitution. The result owns all geometry and retains its source;
// status/ownership return by value, without caller publication/alias pointers.
// This does not synthesize empty-hard-row carets or admit source editor wiring,
// vertical/fragment navigation, source Display, or a new public C ABI.
hinted_paragraph_interaction_result create_hinted_paragraph_interaction(
    std::shared_ptr<const hinted_paragraph_generation> paragraph) noexcept;

} // namespace progpu::native::text
