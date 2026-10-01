#pragma once

#include "progpu_native_text_hinting.h"
#include "progpu_native_hinted_paragraph_interaction.hpp"
#include "progpu_native_hinted_paragraph_glyph_frame.hpp"

namespace progpu::native::text {

// Same-module read-only diagnostics/native controls under the caller's handle
// lease. These are not a public ABI, provider crossing or mutable generation.
std::shared_ptr<const hinted_paragraph_generation> select_hinted_paragraph_generation(
    const progpu_native_hinted_paragraph* paragraph) noexcept;
std::shared_ptr<const hinted_paragraph_interaction> select_hinted_paragraph_interaction(
    const progpu_native_hinted_paragraph* paragraph) noexcept;
std::shared_ptr<const hinted_paragraph_glyph_frame> select_hinted_paragraph_frame_generation(
    const progpu_native_hinted_paragraph_frame* frame) noexcept;
std::shared_ptr<const hinted_paragraph_glyph_resource> select_hinted_glyph_resource_generation(
    const progpu_native_hinted_glyph_resource* resource) noexcept;

} // namespace progpu::native::text
