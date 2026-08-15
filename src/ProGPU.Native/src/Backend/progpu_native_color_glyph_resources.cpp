#include "progpu_native.h"

#if !defined(PROGPU_NATIVE_DAWN_ABI)
#include <webgpu.h>
#include <wgpu.h>
#else
#define WGPU_SKIP_DECLARATIONS
#include <webgpu.h>
#include "progpu_native_dawn.h"
#endif

#include "progpu_webgpu_compat.hpp"
#include "progpu_native_engine.hpp"
#include "progpu_native_gpu_records.hpp"
#include "progpu_native_pipeline.hpp"
#include "progpu_native_semantic_color_glyph.hpp"
#include "progpu_native_semantic_replay.hpp"

#include <algorithm>
#include <new>
#include <vector>

namespace progpu::native::semantic {

bool prepare_color_glyph_atlas(
    progpu_native_engine& engine,
    semantic_glyph_page& page,
    std::uint64_t scene_hash,
    std::uint64_t& upload_bytes) noexcept {
    upload_bytes = 0U;
    if (page.color_bitmaps.empty()) {
        page.color_rasters.clear();
        return true;
    }
    if (engine.color_glyph_atlas_owner_hash == scene_hash &&
        engine.color_glyph_atlas_texture_view != nullptr &&
        page.color_rasters.size() == page.color_bitmaps.size()) {
        return true;
    }

    std::vector<semantic_color_glyph_raster> rasters;
    std::uint32_t required_size = 256U;
    try {
        rasters.resize(page.color_bitmaps.size());
        for (;;) {
            std::uint32_t x = 2U;
            std::uint32_t y = 2U;
            std::uint32_t row_height = 0U;
            bool fits = true;
            for (std::size_t index = 0U;
                 index < page.color_bitmaps.size();
                 ++index) {
                const auto& bitmap = page.color_bitmaps[index];
                if (bitmap.width + 4U > required_size) {
                    fits = false;
                    break;
                }
                if (x + bitmap.width + 2U > required_size) {
                    x = 2U;
                    y += row_height + 2U;
                    row_height = 0U;
                }
                if (y + bitmap.height + 2U > required_size) {
                    fits = false;
                    break;
                }
                rasters[index] = {x, y};
                x += bitmap.width + 2U;
                row_height = std::max(row_height, bitmap.height);
            }
            if (fits) {
                break;
            }
            if (required_size >= native_max_atlas_size) {
                return false;
            }
            required_size *= 2U;
        }
    } catch (const std::bad_alloc&) {
        return false;
    }

    if (engine.glyph_atlas_texture == nullptr &&
        !create_glyph_resources(engine)) {
        return false;
    }
    if (engine.color_glyph_atlas_texture == nullptr ||
        engine.color_glyph_atlas_size < required_size) {
        WGPUTextureDescriptor descriptor{};
        descriptor.label = webgpu::string_view(
            "ProGPU native retained color glyph atlas");
        descriptor.usage = WGPUTextureUsage_TextureBinding |
            WGPUTextureUsage_CopyDst;
        descriptor.dimension = WGPUTextureDimension_2D;
        descriptor.size = {required_size, required_size, 1U};
        descriptor.format = WGPUTextureFormat_RGBA8Unorm;
        descriptor.mipLevelCount = 1U;
        descriptor.sampleCount = 1U;
        WGPUTexture replacement = wgpuDeviceCreateTexture(
            engine.device,
            &descriptor);
        if (replacement == nullptr) {
            return false;
        }
        WGPUTextureView replacement_view = wgpuTextureCreateView(
            replacement,
            nullptr);
        if (replacement_view == nullptr) {
            wgpuTextureDestroy(replacement);
            wgpuTextureRelease(replacement);
            return false;
        }
        WGPUTexture old_texture = engine.color_glyph_atlas_texture;
        WGPUTextureView old_view = engine.color_glyph_atlas_texture_view;
        const std::uint32_t old_size = engine.color_glyph_atlas_size;
        engine.color_glyph_atlas_texture = replacement;
        engine.color_glyph_atlas_texture_view = replacement_view;
        engine.color_glyph_atlas_size = required_size;
        if (!refresh_text_atlas_bind_group(engine)) {
            engine.color_glyph_atlas_texture = old_texture;
            engine.color_glyph_atlas_texture_view = old_view;
            engine.color_glyph_atlas_size = old_size;
            wgpuTextureViewRelease(replacement_view);
            wgpuTextureDestroy(replacement);
            wgpuTextureRelease(replacement);
            return false;
        }
        if (old_view != nullptr) {
            wgpuTextureViewRelease(old_view);
        }
        if (old_texture != nullptr) {
            wgpuTextureDestroy(old_texture);
            wgpuTextureRelease(old_texture);
        }
    }

    for (std::size_t index = 0U;
         index < page.color_bitmaps.size();
         ++index) {
        const auto& bitmap = page.color_bitmaps[index];
        const auto& raster = rasters[index];
        webgpu::image_copy_texture destination{};
        destination.texture = engine.color_glyph_atlas_texture;
        destination.origin = {raster.atlas_x, raster.atlas_y, 0U};
        destination.aspect = WGPUTextureAspect_All;
        webgpu::texture_data_layout layout{};
        layout.bytesPerRow = bitmap.row_bytes;
        layout.rowsPerImage = bitmap.height;
        const WGPUExtent3D extent{bitmap.width, bitmap.height, 1U};
        const std::size_t source_bytes =
            static_cast<std::size_t>(bitmap.row_bytes) *
                (bitmap.height - 1U) +
            static_cast<std::size_t>(bitmap.width) * 4U;
        wgpuQueueWriteTexture(
            engine.queue,
            &destination,
            page.color_pixels.data() + bitmap.pixel_offset,
            source_bytes,
            &layout,
            &extent);
        upload_bytes += source_bytes;
    }
    page.color_rasters = std::move(rasters);
    engine.color_glyph_atlas_owner_hash = scene_hash;
    return true;
}

} // namespace progpu::native::semantic
