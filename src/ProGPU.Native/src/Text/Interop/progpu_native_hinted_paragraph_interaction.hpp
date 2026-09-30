#pragma once

#include "progpu_native_hinted_paragraph_internal.hpp"

namespace progpu::native::text {

struct hinted_paragraph_interaction_result;

class hinted_paragraph_interaction final {
public:
    hinted_paragraph_interaction(const hinted_paragraph_interaction&) = delete;
    hinted_paragraph_interaction& operator=(const hinted_paragraph_interaction&) = delete;
    const std::shared_ptr<const hinted_paragraph_generation>& paragraph() const noexcept { return paragraph_; }
    std::span<const text_cluster_box> boxes() const noexcept { return boxes_; }
    std::span<const text_caret_stop> carets() const noexcept { return carets_; }

private:
    hinted_paragraph_interaction() = default;
    std::shared_ptr<const hinted_paragraph_generation> paragraph_{};
    std::vector<text_cluster_box> boxes_{};
    std::vector<text_caret_stop> carets_{};
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
