#pragma once

#include <cstdint>

namespace progpu::native::effects {

// Exact retained-output identity for one depth-indexed semantic layer slot.
// Lookup, commit, and invalidation are O(1) time and O(1) inline storage.
// A scene or operation change and every texture reallocation fail closed.
struct semantic_output_cache_key {
    std::uint64_t scene_hash = 0U;
    std::uint64_t operation_id = 0U;
    std::uint32_t texture_generation = 0U;
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;

    friend constexpr bool operator==(
        const semantic_output_cache_key&,
        const semantic_output_cache_key&) noexcept = default;
};

struct semantic_output_cache {
    semantic_output_cache_key key{};
    bool valid = false;
};

[[nodiscard]] bool semantic_output_cache_hit(
    const semantic_output_cache& cache,
    const semantic_output_cache_key& key) noexcept;

void commit_semantic_output_cache(
    semantic_output_cache& cache,
    const semantic_output_cache_key& key) noexcept;

void invalidate_semantic_output_cache(
    semantic_output_cache& cache) noexcept;

} // namespace progpu::native::effects
