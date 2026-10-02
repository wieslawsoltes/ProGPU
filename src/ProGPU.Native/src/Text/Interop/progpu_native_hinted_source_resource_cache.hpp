#pragma once

#include "progpu_native_hinted_source_fitting.hpp"
#include "progpu_native_text_source_resource.h"

namespace progpu::native::text {
// Only the original producer handle owns this immutable cache. The source view
// refers to that handle's separately owned double source cache; no view escapes
// the destruction-excluding borrow. Every placement witness is copied here.
struct hinted_source_resource_cache final {
    std::vector<progpu_native_text_shaping_glyph> raw{}, effective{}, prepared{};
    std::vector<progpu_native_hinted_source_positioning_run> positioning_runs{};
    std::vector<progpu_native_hinted_source_fitting_slice> slices{};
    std::vector<std::uint32_t> slice_indices{};
    std::vector<progpu_native_hinted_source_fitted_line> lines{};
    progpu_native_hinted_source_glyph_resource_view view{};
    bool allocation_aliases(const void* output, std::size_t bytes) const noexcept;
};

// All output publication follows validation and allocation. The complete source
// generation and cache must remain owned by the same resource handle afterward.
bool cache_hinted_source_resource(const hinted_paragraph_generation& paragraph,
    progpu_native_hinted_source_paragraph_view source,
    std::shared_ptr<const hinted_source_resource_cache>& result);
} // namespace progpu::native::text
