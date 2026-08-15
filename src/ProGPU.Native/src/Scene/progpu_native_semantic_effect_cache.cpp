#include "progpu_native_semantic_effect_cache.hpp"

namespace progpu::native::effects {

bool semantic_output_cache_hit(
    const semantic_output_cache& cache,
    const semantic_output_cache_key& key) noexcept {
    return cache.valid && key.scene_hash != 0U && key.operation_id != 0U &&
        cache.key == key;
}

void commit_semantic_output_cache(
    semantic_output_cache& cache,
    const semantic_output_cache_key& key) noexcept {
    cache.key = key;
    cache.valid = key.scene_hash != 0U && key.operation_id != 0U;
}

void invalidate_semantic_output_cache(
    semantic_output_cache& cache) noexcept {
    cache = {};
}

} // namespace progpu::native::effects
