#include "progpu_native_frame_execution_common.hpp"
#include "progpu_native_rgb_glyph_execution.hpp"
#include "GlyphRgbRasterizerWgsl.generated.hpp"
#include "GlyphRgbCompositeWgsl.generated.hpp"

namespace progpu::native::execution {
namespace {

struct rgb_instance final {
    float target_origin[2];
    float extent[2];
    std::uint32_t atlas_origin[2];
    std::uint32_t reserved[2];
    progpu_native_color foreground;
};
static_assert(sizeof(rgb_instance) == 48U);

bool create_rgb_composite_pipelines(progpu_native_engine& engine, WGPUPipelineLayout layout,
    std::array<WGPURenderPipeline, 3U>& pipelines, const std::array<const char*, 3U>& names)
{
    constexpr std::array<WGPUColorWriteMask, 3U> masks{
        WGPUColorWriteMask_Red, WGPUColorWriteMask_Green, WGPUColorWriteMask_Blue};
    WGPUBlendState blend{};
    blend.color = {WGPUBlendOperation_Add, WGPUBlendFactor_SrcAlpha, WGPUBlendFactor_OneMinusSrcAlpha};
    blend.alpha = {WGPUBlendOperation_Add, WGPUBlendFactor_Zero, WGPUBlendFactor_One};
    for (std::size_t channel = 0U; channel < names.size(); ++channel) {
        WGPUColorTargetState target{};
        target.format = engine.target_format;
        target.blend = &blend;
        target.writeMask = masks[channel];
        WGPUFragmentState stage{};
        stage.module = engine.rgb_glyph_pipelines.composite_shader;
        stage.entryPoint = webgpu::string_view(names[channel]);
        stage.targetCount = 1U;
        stage.targets = &target;
        WGPURenderPipelineDescriptor descriptor{};
        descriptor.layout = layout;
        descriptor.vertex.module = engine.rgb_glyph_pipelines.composite_shader;
        descriptor.vertex.entryPoint = webgpu::string_view("vs_rgb_composite");
        descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
        descriptor.primitive.frontFace = WGPUFrontFace_CCW;
        descriptor.primitive.cullMode = WGPUCullMode_None;
        descriptor.multisample.count = 1U;
        descriptor.multisample.mask = 0xFFFFFFFFU;
        descriptor.fragment = &stage;
        pipelines[channel] = wgpuDeviceCreateRenderPipeline(engine.device, &descriptor);
        if (pipelines[channel] == nullptr) return false;
    }
    return true;
}

bool ensure_rgb_pipelines(progpu_native_engine& engine, bool fragment)
{
    auto& owned = engine.rgb_glyph_pipelines;
    if (owned.composite[2] != nullptr && (fragment ? owned.fragment != nullptr : owned.compute != nullptr))
        return true;
    // A failed creation never leaves a partly usable cache. Ordinary glyph
    // pipelines and their R8 atlas remain separate from these RGB resources.
    owned.reset();
    const auto module = [&](const unsigned char* bytes, std::size_t size) {
        webgpu::wgsl_source source(bytes, size);
        WGPUShaderModuleDescriptor descriptor{};
        descriptor.nextInChain = source.chain();
        return wgpuDeviceCreateShaderModule(engine.device, &descriptor);
    };
    owned.raster_shader = module(generated::rgb_glyph_rasterizer_wgsl, generated::rgb_glyph_rasterizer_wgsl_size);
    owned.composite_shader = module(generated::rgb_glyph_composite_wgsl, generated::rgb_glyph_composite_wgsl_size);
    const auto finish = [&]() {
        if (owned.raster_shader == nullptr || owned.composite_shader == nullptr) return false;
        std::array<WGPUBindGroupLayoutEntry, 5U> raster{};
        const std::uint32_t count = fragment ? 4U : 5U;
        for (std::uint32_t index = 0U; index < count; ++index) {
            auto& entry = raster[index];
            entry.binding = fragment && index == 3U ? 4U : index;
            entry.visibility = fragment ? WGPUShaderStage_Fragment : WGPUShaderStage_Compute;
            entry.buffer.type = entry.binding == 0U || entry.binding == 4U ? WGPUBufferBindingType_Uniform :
                entry.binding == 3U ? WGPUBufferBindingType_Storage : WGPUBufferBindingType_ReadOnlyStorage;
            entry.buffer.hasDynamicOffset = entry.binding == 0U;
            entry.buffer.minBindingSize = entry.binding == 0U ? sizeof(gpu_glyph_uniforms) :
                entry.binding == 1U ? sizeof(gpu_glyph_record) :
                entry.binding == 2U ? sizeof(progpu_native_path_segment) : entry.binding == 4U ? 16U : 4U;
        }
        WGPUBindGroupLayoutDescriptor raster_layout{};
        raster_layout.entryCount = count;
        raster_layout.entries = raster.data();
        owned.raster_layout = wgpuDeviceCreateBindGroupLayout(engine.device, &raster_layout);
        if (owned.raster_layout == nullptr) return false;
        WGPUPipelineLayoutDescriptor raster_pipeline_layout{};
        raster_pipeline_layout.bindGroupLayoutCount = 1U;
        raster_pipeline_layout.bindGroupLayouts = &owned.raster_layout;
        owned.raster_pipeline_layout = wgpuDeviceCreatePipelineLayout(engine.device, &raster_pipeline_layout);
        if (owned.raster_pipeline_layout == nullptr) return false;
        if (!fragment) {
            WGPUComputePipelineDescriptor descriptor{};
            descriptor.layout = owned.raster_pipeline_layout;
            descriptor.compute.module = owned.raster_shader;
            descriptor.compute.entryPoint = webgpu::string_view("cs_rgb");
            owned.compute = wgpuDeviceCreateComputePipeline(engine.device, &descriptor);
            if (owned.compute == nullptr) return false;
        } else {
            WGPUColorTargetState target{};
            target.format = WGPUTextureFormat_RGBA8Unorm;
            target.writeMask = WGPUColorWriteMask_All;
            WGPUFragmentState stage{};
            stage.module = owned.raster_shader;
            stage.entryPoint = webgpu::string_view("fs_rgb");
            stage.targetCount = 1U;
            stage.targets = &target;
            WGPURenderPipelineDescriptor descriptor{};
            descriptor.layout = owned.raster_pipeline_layout;
            descriptor.vertex.module = owned.raster_shader;
            descriptor.vertex.entryPoint = webgpu::string_view("vs_raster_fallback");
            descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
            descriptor.primitive.frontFace = WGPUFrontFace_CCW;
            descriptor.primitive.cullMode = WGPUCullMode_None;
            descriptor.multisample.count = 1U;
            descriptor.multisample.mask = 0xFFFFFFFFU;
            descriptor.fragment = &stage;
            owned.fragment = wgpuDeviceCreateRenderPipeline(engine.device, &descriptor);
            if (owned.fragment == nullptr) return false;
        }
        std::array<WGPUBindGroupLayoutEntry, 3U> composite{};
        composite[0].binding = 0U;
        composite[0].visibility = WGPUShaderStage_Fragment;
        composite[0].texture.sampleType = WGPUTextureSampleType_UnfilterableFloat;
        composite[0].texture.viewDimension = WGPUTextureViewDimension_2D;
        composite[1].binding = 1U;
        composite[1].visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
        composite[1].buffer.type = WGPUBufferBindingType_ReadOnlyStorage;
        composite[1].buffer.minBindingSize = sizeof(rgb_instance);
        composite[2].binding = 2U;
        composite[2].visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
        composite[2].buffer.type = WGPUBufferBindingType_Uniform;
        composite[2].buffer.minBindingSize = 16U;
        WGPUBindGroupLayoutDescriptor composite_layout{};
        composite_layout.entryCount = composite.size();
        composite_layout.entries = composite.data();
        owned.composite_layout = wgpuDeviceCreateBindGroupLayout(engine.device, &composite_layout);
        if (owned.composite_layout == nullptr) return false;
        WGPUPipelineLayoutDescriptor composite_pipeline_layout{};
        composite_pipeline_layout.bindGroupLayoutCount = 1U;
        composite_pipeline_layout.bindGroupLayouts = &owned.composite_layout;
        owned.composite_pipeline_layout = wgpuDeviceCreatePipelineLayout(engine.device, &composite_pipeline_layout);
        if (owned.composite_pipeline_layout == nullptr) return false;
        return create_rgb_composite_pipelines(engine, owned.composite_pipeline_layout,
            owned.composite, {"fs_rgb_red", "fs_rgb_green", "fs_rgb_blue"});
    };
    if (finish()) return true;
    owned.reset();
    return false;
}

bool ensure_rgb_mask_pipelines(progpu_native_engine& engine, bool chained)
{
    auto& owned = engine.rgb_glyph_pipelines;
    const auto index = chained ? 1U : 0U;
    if (owned.masked_composite[index][2] != nullptr) return true;
    const auto create = [&]() {
        if (!(chained ? create_semantic_mask_chain_layout(engine) : create_layer_mask_resources(engine))) return false;
        if (owned.empty_layout == nullptr) {
            WGPUBindGroupLayoutDescriptor descriptor{};
            owned.empty_layout = wgpuDeviceCreateBindGroupLayout(engine.device, &descriptor);
            if (owned.empty_layout == nullptr) return false;
        }
        if (owned.empty_bind_group == nullptr) {
            WGPUBindGroupDescriptor descriptor{};
            descriptor.layout = owned.empty_layout;
            owned.empty_bind_group = wgpuDeviceCreateBindGroup(engine.device, &descriptor);
            if (owned.empty_bind_group == nullptr) return false;
        }
        const std::array<WGPUBindGroupLayout, 3U> layouts{owned.composite_layout, owned.empty_layout,
            chained ? engine.semantic_mask_chain_layout : engine.layer_mask_layout};
        WGPUPipelineLayoutDescriptor descriptor{};
        descriptor.bindGroupLayoutCount = layouts.size();
        descriptor.bindGroupLayouts = layouts.data();
        owned.masked_layouts[index] = wgpuDeviceCreatePipelineLayout(engine.device, &descriptor);
        return owned.masked_layouts[index] != nullptr && create_rgb_composite_pipelines(engine,
            owned.masked_layouts[index], owned.masked_composite[index], chained
                ? std::array<const char*, 3U>{"fs_rgb_red_chain", "fs_rgb_green_chain", "fs_rgb_blue_chain"}
                : std::array<const char*, 3U>{"fs_rgb_red_masked", "fs_rgb_green_masked", "fs_rgb_blue_masked"});
    };
    if (create()) return true;
    owned.reset();
    return false;
}

} // namespace

progpu_native_status encode_linear_rgb_glyphs(
    progpu_native_engine& engine, WGPUTextureView target,
    std::uint32_t target_width, std::uint32_t target_height,
    bool target_ignores_alpha, const rgb_glyph_policy& policy,
    const rgb_glyph_scissor& scissor,
    WGPUBindGroup mask_binding, WGPUBindGroup mask_chain_binding,
    std::span<const rgb_glyph_tile> glyphs,
    std::span<const progpu_native_path_segment> segments,
    rgb_glyph_metrics& metrics)
{
    metrics = {};
    // Admission is deliberately distinct from original DWrite rendering modes.
    // No unsupported gamma, contrast, intermediate ClearTypeLevel, sRGB target,
    // translucent background or CPU raster preference is silently normalized.
    if (engine.semantic_encoder == nullptr || target == nullptr || !target_ignores_alpha ||
        (mask_binding != nullptr && mask_chain_binding != nullptr) ||
        target_width == 0U || target_height == 0U || target_width > native_max_atlas_size ||
        target_height > native_max_atlas_size || glyphs.empty() || glyphs.size() > 65536U || segments.empty() ||
        segments.size() > 1048576U ||
        segments.size() > engine.max_buffer_size / sizeof(progpu_native_path_segment) ||
        policy.gamma != 1.0F || policy.enhanced_contrast != 0.0F || policy.cleartype_level != 1.0F ||
        policy.pixel_geometry > 2U || policy.filter_model != rgb_glyph_filter_model::full_pixel_box_8x8 ||
        (engine.target_format != WGPUTextureFormat_RGBA8Unorm && engine.target_format != WGPUTextureFormat_BGRA8Unorm) ||
        (engine.engine_flags & (PROGPU_NATIVE_ENGINE_GLYPH_INTRINSIC_SIMD_CPU_FALLBACK |
            PROGPU_NATIVE_ENGINE_GLYPH_SCALAR_CPU_FALLBACK)) != 0U)
        return engine.fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, "The explicit linear RGB glyph frame is not admitted.");
    if (scissor.width == 0U || scissor.height == 0U ||
        scissor.x >= target_width || scissor.y >= target_height ||
        scissor.width > target_width - scissor.x || scissor.height > target_height - scissor.y)
        return engine.fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, "The RGB glyph source scissor is outside its actual target.");
    for (const auto& segment : segments)
        if (!semantic::is_valid_semantic_segment(segment, false))
            return engine.fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, "An original RGB glyph segment is invalid.");
    try {
        std::uint64_t area = 0U;
        std::uint32_t atlas_width = 64U;
        for (const auto& glyph : glyphs) {
            const auto& outline = glyph.outline;
            if (outline.segment_count == 0U || outline.start_segment > segments.size() ||
                outline.segment_count > segments.size() - outline.start_segment ||
                outline.pad0 != 0U || outline.pad1 != 0U ||
                !std::isfinite(outline.min_x) || !std::isfinite(outline.min_y) ||
                !std::isfinite(outline.max_x) || !std::isfinite(outline.max_y) ||
                outline.min_x > outline.max_x || outline.min_y > outline.max_y ||
                !std::isfinite(glyph.x_start) || !std::isfinite(glyph.y_start) ||
                !std::isfinite(glyph.scale) || glyph.scale <= 0.0F || !std::isfinite(glyph.subpixel_x) ||
                glyph.width == 0U || glyph.height == 0U ||
                glyph.width > native_max_atlas_size || glyph.height > native_max_atlas_size ||
                glyph.target_x < -static_cast<std::int32_t>(native_max_atlas_size) ||
                glyph.target_y < -static_cast<std::int32_t>(native_max_atlas_size) ||
                glyph.target_x > static_cast<std::int32_t>(native_max_atlas_size) ||
                glyph.target_y > static_cast<std::int32_t>(native_max_atlas_size))
                return engine.fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, "An RGB glyph tile is invalid.");
            const std::array channels{glyph.foreground.r, glyph.foreground.g, glyph.foreground.b, glyph.foreground.a};
            for (const auto value : channels)
                if (!std::isfinite(value) || value < 0.0F || value > 1.0F)
                    return engine.fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, "Linear RGB glyph color is outside its explicit normalized domain.");
            // Bound the actual float arithmetic consumed by the shared walker,
            // including both displaced box extremes. Finite source values alone
            // do not prove that addition or division by a tiny scale stays finite.
            const std::array sample_extents{
                ((glyph.x_start - 1.0F / 3.0F) + 0.0625F - glyph.subpixel_x) / glyph.scale,
                ((glyph.x_start + static_cast<float>(glyph.width - 1U) + 1.0F / 3.0F) +
                    0.9375F - glyph.subpixel_x) / glyph.scale,
                -(glyph.y_start + 0.0625F) / glyph.scale,
                -(glyph.y_start + static_cast<float>(glyph.height - 1U) + 0.9375F) / glyph.scale};
            for (const auto coordinate : sample_extents)
                if (!std::isfinite(coordinate))
                    return engine.fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, "RGB glyph sample coordinates overflow their original float frame.");
            area += static_cast<std::uint64_t>(glyph.width) * glyph.height;
            if (area > static_cast<std::uint64_t>(native_max_atlas_size) * native_max_atlas_size)
                return engine.fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, "The RGB glyph atlas exceeds its bounded pixel budget.");
            while (atlas_width < glyph.width) atlas_width *= 2U;
        }
        while (static_cast<std::uint64_t>(atlas_width) * atlas_width < area && atlas_width < native_max_atlas_size)
            atlas_width *= 2U;
        std::vector<gpu_glyph_record> records;
        std::vector<gpu_glyph_uniforms> uniforms;
        std::vector<rgb_instance> instances;
        records.reserve(glyphs.size());
        uniforms.reserve(glyphs.size());
        instances.reserve(glyphs.size());
        std::uint32_t x = 0U, y = 0U, row_height = 0U;
        std::uint64_t staging_bytes = 0U;
        for (const auto& glyph : glyphs) {
            if (glyph.width > atlas_width - x) { x = 0U; y += row_height; row_height = 0U; }
            if (y > native_max_atlas_size || glyph.height > native_max_atlas_size - y)
                return engine.fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, "The bounded RGB glyph shelf cannot fit this original batch.");
            staging_bytes = (staging_bytes + 511U) & ~std::uint64_t{511U};
            const auto row_bytes = (glyph.width * 4U + 255U) & ~255U;
            // Also stay within WebGPU's portable storage-binding minimum. The
            // ordinary engine's total-buffer allowance can be larger than one
            // storage binding, especially for a borrowed device.
            const auto coverage_limit = std::min({semantic_max_coverage_bytes,
                engine.max_buffer_size, std::uint64_t{128U * 1024U * 1024U}});
            if (staging_bytes + static_cast<std::uint64_t>(row_bytes) * glyph.height > coverage_limit)
                return engine.fail(PROGPU_NATIVE_STATUS_INVALID_ARGUMENT, "RGB glyph staging exceeds the original coverage budget.");
            uniforms.push_back({glyph.x_start, glyph.y_start, glyph.scale, static_cast<std::uint32_t>(records.size()),
                static_cast<std::uint32_t>(staging_bytes / 4U), row_bytes / 4U, glyph.width, glyph.height,
                glyph.subpixel_x, static_cast<float>(x), static_cast<float>(y), 0.0F});
            records.push_back(glyph.outline);
            instances.push_back({{static_cast<float>(glyph.target_x), static_cast<float>(glyph.target_y)},
                {static_cast<float>(glyph.width), static_cast<float>(glyph.height)}, {x, y}, {0U, 0U}, glyph.foreground});
            staging_bytes += static_cast<std::uint64_t>(row_bytes) * glyph.height;
            x += glyph.width;
            row_height = std::max(row_height, glyph.height);
        }
        const auto atlas_height = y + row_height;
        std::vector<std::byte> uniform_bytes(glyphs.size() * 256U);
        for (std::size_t index = 0U; index < uniforms.size(); ++index)
            std::memcpy(uniform_bytes.data() + index * 256U, &uniforms[index], sizeof(gpu_glyph_uniforms));
        const bool fragment = (engine.engine_flags & PROGPU_NATIVE_ENGINE_GLYPH_RASTER_SHADER_FALLBACK) != 0U;
        if (!ensure_rgb_pipelines(engine, fragment))
            return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "The owned RGB glyph pipelines could not be created.");
        const bool masked = mask_binding != nullptr || mask_chain_binding != nullptr;
        const bool chained = mask_chain_binding != nullptr;
        if (masked && !ensure_rgb_mask_pipelines(engine, chained))
            return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "The source RGB glyph mask pipelines could not be created.");
        progpu_native_engine::raster_resource_lease lease(engine, true);
        auto& resources = lease.get();
        const auto upload = [&](WGPUBuffer& buffer, const void* data, std::uint64_t bytes,
                                webgpu::buffer_usage_flags usage) {
            if (bytes == 0U || bytes > engine.max_buffer_size) return false;
            WGPUBufferDescriptor descriptor{};
            descriptor.size = bytes;
            descriptor.usage = usage;
            if (data != nullptr) descriptor.usage |= WGPUBufferUsage_CopyDst;
            buffer = wgpuDeviceCreateBuffer(engine.device, &descriptor);
            if (buffer == nullptr) return false;
            if (data != nullptr) wgpuQueueWriteBuffer(engine.queue, buffer, 0U, data, bytes);
            return true;
        };
        const std::array<std::uint32_t, 4U> raster_policy{
            policy.pixel_geometry, static_cast<std::uint32_t>(policy.filter_model), 0U, 0U};
        const std::array<float, 4U> frame{static_cast<float>(target_width), static_cast<float>(target_height), 0.0F, 0.0F};
        if (!upload(resources.uniforms, uniform_bytes.data(), uniform_bytes.size(), WGPUBufferUsage_Uniform) ||
            !upload(resources.records, records.data(), records.size() * sizeof(gpu_glyph_record), WGPUBufferUsage_Storage) ||
            !upload(resources.segments, segments.data(), segments.size_bytes(), WGPUBufferUsage_Storage) ||
            !upload(resources.rgb_policy, raster_policy.data(), sizeof(raster_policy), WGPUBufferUsage_Uniform) ||
            !upload(resources.rgb_instances, instances.data(), instances.size() * sizeof(rgb_instance), WGPUBufferUsage_Storage) ||
            !upload(resources.rgb_frame, frame.data(), sizeof(frame), WGPUBufferUsage_Uniform) ||
            (!fragment && !upload(resources.coverage, nullptr, staging_bytes, WGPUBufferUsage_Storage | WGPUBufferUsage_CopySrc)))
            return engine.fail(PROGPU_NATIVE_STATUS_OUT_OF_MEMORY, "RGB glyph batch storage could not be allocated.");
        WGPUTextureDescriptor texture{};
        texture.dimension = WGPUTextureDimension_2D;
        texture.size = {atlas_width, atlas_height, 1U};
        texture.format = WGPUTextureFormat_RGBA8Unorm;
        texture.mipLevelCount = 1U;
        texture.sampleCount = 1U;
        texture.usage = WGPUTextureUsage_TextureBinding | WGPUTextureUsage_CopyDst | WGPUTextureUsage_RenderAttachment;
        resources.rgb_coverage = wgpuDeviceCreateTexture(engine.device, &texture);
        if (resources.rgb_coverage != nullptr)
            resources.rgb_coverage_view = wgpuTextureCreateView(resources.rgb_coverage, nullptr);
        if (resources.rgb_coverage_view == nullptr)
            return engine.fail(PROGPU_NATIVE_STATUS_OUT_OF_MEMORY, "RGB glyph coverage storage could not be allocated.");
        std::array<WGPUBindGroupEntry, 5U> entries{{
            {nullptr, 0U, resources.uniforms, 0U, sizeof(gpu_glyph_uniforms), nullptr, nullptr},
            {nullptr, 1U, resources.records, 0U, records.size() * sizeof(gpu_glyph_record), nullptr, nullptr},
            {nullptr, 2U, resources.segments, 0U, segments.size_bytes(), nullptr, nullptr},
            {nullptr, 3U, resources.coverage, 0U, staging_bytes, nullptr, nullptr},
            {nullptr, 4U, resources.rgb_policy, 0U, sizeof(raster_policy), nullptr, nullptr}}};
        if (fragment) entries[3] = entries[4];
        WGPUBindGroupDescriptor binding{};
        binding.layout = engine.rgb_glyph_pipelines.raster_layout;
        binding.entryCount = fragment ? 4U : 5U;
        binding.entries = entries.data();
        resources.bind_group = wgpuDeviceCreateBindGroup(engine.device, &binding);
        const std::array<WGPUBindGroupEntry, 3U> composite_entries{{
            {nullptr, 0U, nullptr, 0U, 0U, nullptr, resources.rgb_coverage_view},
            {nullptr, 1U, resources.rgb_instances, 0U, instances.size() * sizeof(rgb_instance), nullptr, nullptr},
            {nullptr, 2U, resources.rgb_frame, 0U, sizeof(frame), nullptr, nullptr}}};
        binding.layout = engine.rgb_glyph_pipelines.composite_layout;
        binding.entryCount = composite_entries.size();
        binding.entries = composite_entries.data();
        resources.rgb_composite = wgpuDeviceCreateBindGroup(engine.device, &binding);
        if (resources.bind_group == nullptr || resources.rgb_composite == nullptr)
            return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "RGB glyph batch bindings could not be created.");

        if (fragment) {
            WGPURenderPassColorAttachment attachment{};
            webgpu::initialize_color_attachment(attachment);
            attachment.view = resources.rgb_coverage_view;
            attachment.loadOp = WGPULoadOp_Clear;
            attachment.storeOp = WGPUStoreOp_Store;
            WGPURenderPassDescriptor descriptor{};
            descriptor.colorAttachmentCount = 1U;
            descriptor.colorAttachments = &attachment;
            auto pass = wgpuCommandEncoderBeginRenderPass(engine.semantic_encoder, &descriptor);
            if (pass == nullptr) return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "RGB glyph raster pass failed.");
            wgpuRenderPassEncoderSetPipeline(pass, engine.rgb_glyph_pipelines.fragment);
            for (std::size_t index = 0U; index < uniforms.size(); ++index) {
                const auto& value = uniforms[index];
                const auto offset = static_cast<std::uint32_t>(index * 256U);
                wgpuRenderPassEncoderSetBindGroup(pass, 0U, resources.bind_group, 1U, &offset);
                wgpuRenderPassEncoderSetViewport(pass, value.atlas_x, value.atlas_y,
                    static_cast<float>(value.width), static_cast<float>(value.height), 0.0F, 1.0F);
                wgpuRenderPassEncoderSetScissorRect(pass, static_cast<std::uint32_t>(value.atlas_x),
                    static_cast<std::uint32_t>(value.atlas_y), value.width, value.height);
                wgpuRenderPassEncoderDraw(pass, 3U, 1U, 0U, 0U);
            }
            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
        } else {
            WGPUComputePassDescriptor descriptor{};
            auto pass = wgpuCommandEncoderBeginComputePass(engine.semantic_encoder, &descriptor);
            if (pass == nullptr) return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "RGB glyph compute pass failed.");
            wgpuComputePassEncoderSetPipeline(pass, engine.rgb_glyph_pipelines.compute);
            for (std::size_t index = 0U; index < uniforms.size(); ++index) {
                const auto offset = static_cast<std::uint32_t>(index * 256U);
                wgpuComputePassEncoderSetBindGroup(pass, 0U, resources.bind_group, 1U, &offset);
                engine.dispatch_compute(pass, engine.rgb_glyph_pipelines.compute,
                    (uniforms[index].width + 15U) / 16U, (uniforms[index].height + 15U) / 16U, 1U);
            }
            wgpuComputePassEncoderEnd(pass);
            wgpuComputePassEncoderRelease(pass);
            for (const auto& value : uniforms) {
                webgpu::image_copy_buffer source{};
                source.buffer = resources.coverage;
                source.layout.offset = static_cast<std::uint64_t>(value.output_offset_words) * 4U;
                source.layout.bytesPerRow = value.output_row_words * 4U;
                source.layout.rowsPerImage = value.height;
                webgpu::image_copy_texture destination{};
                destination.texture = resources.rgb_coverage;
                destination.origin = {static_cast<std::uint32_t>(value.atlas_x), static_cast<std::uint32_t>(value.atlas_y), 0U};
                destination.aspect = WGPUTextureAspect_All;
                const WGPUExtent3D extent{value.width, value.height, 1U};
                wgpuCommandEncoderCopyBufferToTexture(engine.semantic_encoder, &source, &destination, &extent);
            }
        }
        WGPURenderPassColorAttachment attachment{};
        webgpu::initialize_color_attachment(attachment);
        attachment.view = target;
        attachment.loadOp = WGPULoadOp_Load;
        attachment.storeOp = WGPUStoreOp_Store;
        WGPURenderPassDescriptor descriptor{};
        descriptor.colorAttachmentCount = 1U;
        descriptor.colorAttachments = &attachment;
        auto pass = wgpuCommandEncoderBeginRenderPass(engine.semantic_encoder, &descriptor);
        if (pass == nullptr) return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR, "RGB glyph composition pass failed.");
        wgpuRenderPassEncoderSetViewport(pass, 0.0F, 0.0F, static_cast<float>(target_width), static_cast<float>(target_height), 0.0F, 1.0F);
        wgpuRenderPassEncoderSetScissorRect(pass, scissor.x, scissor.y, scissor.width, scissor.height);
        wgpuRenderPassEncoderSetBindGroup(pass, 0U, resources.rgb_composite, 0U, nullptr);
        const auto& pipelines = masked ? engine.rgb_glyph_pipelines.masked_composite[chained ? 1U : 0U]
            : engine.rgb_glyph_pipelines.composite;
        if (masked) {
            wgpuRenderPassEncoderSetBindGroup(pass, 1U, engine.rgb_glyph_pipelines.empty_bind_group, 0U, nullptr);
            wgpuRenderPassEncoderSetBindGroup(pass, 2U, chained ? mask_chain_binding : mask_binding, 0U, nullptr);
        }
        for (auto pipeline : pipelines) {
            wgpuRenderPassEncoderSetPipeline(pass, pipeline);
            wgpuRenderPassEncoderDraw(pass, 6U, static_cast<std::uint32_t>(instances.size()), 0U, 0U);
        }
        wgpuRenderPassEncoderEnd(pass);
        wgpuRenderPassEncoderRelease(pass);
        metrics.vertex_upload_bytes = records.size() * sizeof(gpu_glyph_record) +
            segments.size_bytes() + instances.size() * sizeof(rgb_instance);
        metrics.uniform_upload_bytes = uniform_bytes.size() + sizeof(raster_policy) + sizeof(frame);
        metrics.draw_calls = 3U + (fragment ? static_cast<std::uint32_t>(glyphs.size()) : 0U);
        return PROGPU_NATIVE_STATUS_SUCCESS;
    } catch (const std::bad_alloc&) {
        return engine.fail(PROGPU_NATIVE_STATUS_OUT_OF_MEMORY, "The bounded RGB glyph batch could not be retained.");
    }
}

} // namespace progpu::native::execution
