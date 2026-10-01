#include "progpu_native_frame_execution_common.hpp"
#include "progpu_native_semantic_glyph_paint.hpp"
#include "HintedGlyphPaintWgsl.generated.hpp"

namespace progpu::native::semantic {
namespace {

bool ensure_layouts(progpu_native_engine& engine) {
    if (engine.glyph_paint_uniform_layout != nullptr &&
        engine.glyph_paint_texture_layout != nullptr &&
        engine.glyph_paint_empty_mask_layout != nullptr &&
        engine.glyph_paint_empty_mask_bind_group != nullptr) return true;
    if (engine.glyph_paint_uniform_layout != nullptr ||
        engine.glyph_paint_texture_layout != nullptr ||
        engine.glyph_paint_empty_mask_layout != nullptr ||
        engine.glyph_paint_empty_mask_bind_group != nullptr) return false;
    std::array<WGPUBindGroupLayoutEntry, 4U> entries{};
    const std::array<std::uint64_t, 4U> sizes{{sizeof(gpu_uniforms),
        sizeof(progpu_native_scene_brush), sizeof(progpu_native_scene_gradient_stop),
        sizeof(progpu_native_scene_glyph_paint)}};
    for (std::uint32_t i = 0U; i < entries.size(); ++i) {
        entries[i].binding = i;
        entries[i].visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
        entries[i].buffer.type = i == 0U ? WGPUBufferBindingType_Uniform :
            WGPUBufferBindingType_ReadOnlyStorage;
        entries[i].buffer.minBindingSize = sizes[i];
    }
    WGPUBindGroupLayoutDescriptor descriptor{};
    descriptor.label = webgpu::string_view("ProGPU original hinted paint uniforms");
    descriptor.entryCount = entries.size(); descriptor.entries = entries.data();
    auto uniforms = wgpuDeviceCreateBindGroupLayout(engine.device, &descriptor);
    if (uniforms == nullptr) return false;
    std::array<WGPUBindGroupLayoutEntry, 2U> textures{};
    textures[0].binding = 0U; textures[0].visibility = WGPUShaderStage_Fragment;
    textures[0].sampler.type = WGPUSamplerBindingType_Filtering;
    textures[1].binding = 1U; textures[1].visibility = WGPUShaderStage_Fragment;
    textures[1].texture.sampleType = WGPUTextureSampleType_Float;
    textures[1].texture.viewDimension = WGPUTextureViewDimension_2D;
    descriptor.label = webgpu::string_view("ProGPU original hinted paint texture");
    descriptor.entryCount = textures.size(); descriptor.entries = textures.data();
    auto texture = wgpuDeviceCreateBindGroupLayout(engine.device, &descriptor);
    if (texture == nullptr) { wgpuBindGroupLayoutRelease(uniforms); return false; }
    // Paint textures occupy group 3. The unmasked entrypoint needs an explicit
    // empty group 2, not an unbound nonempty mask-chain layout in that slot.
    descriptor.label = webgpu::string_view("ProGPU unmasked hinted paint slot");
    descriptor.entryCount = 0U; descriptor.entries = nullptr;
    auto empty_mask = wgpuDeviceCreateBindGroupLayout(engine.device, &descriptor);
    if (empty_mask == nullptr) {
        wgpuBindGroupLayoutRelease(texture); wgpuBindGroupLayoutRelease(uniforms);
        return false;
    }
    WGPUBindGroupDescriptor empty_group_descriptor{};
    empty_group_descriptor.label = webgpu::string_view("ProGPU unmasked hinted paint binding");
    empty_group_descriptor.layout = empty_mask;
    auto empty_group = wgpuDeviceCreateBindGroup(engine.device, &empty_group_descriptor);
    if (empty_group == nullptr) {
        wgpuBindGroupLayoutRelease(empty_mask);
        wgpuBindGroupLayoutRelease(texture); wgpuBindGroupLayoutRelease(uniforms);
        return false;
    }
    engine.glyph_paint_uniform_layout = uniforms;
    engine.glyph_paint_texture_layout = texture;
    engine.glyph_paint_empty_mask_layout = empty_mask;
    engine.glyph_paint_empty_mask_bind_group = empty_group;
    return true;
}

WGPUBuffer select_uniform(progpu_native_engine& engine, std::uint32_t target_layer) noexcept {
    return target_layer == PROGPU_NATIVE_SCENE_NO_INDEX
        ? engine.semantic_destination_sampling_active ? engine.semantic_root_slot.uniform_buffer :
            engine.analytic_uniform_buffer
        : target_layer < engine.semantic_layer_slots.size()
            ? engine.semantic_layer_slots[target_layer].uniform_buffer : nullptr;
}

WGPURenderPipeline& pipeline_slot(progpu_native_engine& engine,
    bool masked, bool chained, bool premultiplied_output) noexcept {
    if (premultiplied_output) {
        return chained ? engine.glyph_paint_chain_pipeline :
            masked ? engine.glyph_paint_masked_pipeline : engine.glyph_paint_pipeline;
    }
    return chained ? engine.glyph_paint_straight_chain_pipeline :
        masked ? engine.glyph_paint_straight_masked_pipeline : engine.glyph_paint_straight_pipeline;
}

} // namespace

bool admit_glyph_paint_storage(progpu_native_engine& engine, std::uint64_t paint_bytes) {
    if (engine.glyph_paint_storage_limit == 0U) {
#if defined(PROGPU_NATIVE_DAWN_ABI)
        WGPULimits limits = WGPU_LIMITS_INIT;
        if (wgpuDeviceGetLimits(engine.device, &limits) != WGPUStatus_Success) return false;
#else
        WGPUSupportedLimits supported{};
        if (!wgpuDeviceGetLimits(engine.device, &supported)) return false;
        const auto& limits = supported.limits;
#endif
        if (limits.maxBindGroups < 4U || limits.maxStorageBuffersPerShaderStage < 3U ||
            limits.maxVertexAttributes < 9U || limits.maxVertexBufferArrayStride < sizeof(gpu_glyph_instance) ||
            limits.maxSampledTexturesPerShaderStage < 4U || limits.maxSamplersPerShaderStage < 3U ||
            limits.maxStorageBufferBindingSize < sizeof(progpu_native_scene_glyph_paint) ||
            limits.maxBufferSize < sizeof(progpu_native_scene_glyph_paint) || limits.maxTextureDimension2D == 0U)
            return false;
        engine.glyph_paint_storage_limit = limits.maxStorageBufferBindingSize;
        engine.glyph_paint_max_buffer_size = limits.maxBufferSize;
        engine.glyph_paint_texture_limit = limits.maxTextureDimension2D;
    }
    return paint_bytes != 0U && paint_bytes <= engine.glyph_paint_storage_limit &&
        paint_bytes <= engine.glyph_paint_max_buffer_size;
}

WGPURenderPipeline select_glyph_paint_pipeline(progpu_native_engine& engine,
    bool masked, bool chained, bool premultiplied_output) noexcept {
    return pipeline_slot(engine, masked, chained, premultiplied_output);
}

bool ensure_glyph_paint_pipeline(progpu_native_engine& engine,
    bool masked, bool chained, bool premultiplied_output) {
    auto& pipeline = pipeline_slot(engine, masked, chained, premultiplied_output);
    if (pipeline != nullptr) return true;
    if (!create_glyph_resources(engine) || !create_analytic_resources(engine) ||
        !ensure_layouts(engine) || (masked && !create_layer_mask_resources(engine)) ||
        (chained && !create_semantic_mask_chain_layout(engine))) return false;
    if (engine.glyph_paint_shader == nullptr) {
        webgpu::wgsl_source source(generated::hinted_glyph_paint_wgsl,
            generated::hinted_glyph_paint_wgsl_size);
        WGPUShaderModuleDescriptor descriptor{};
        descriptor.nextInChain = source.chain();
        descriptor.label = webgpu::string_view("ProGPU canonical original hinted glyph paint");
        engine.glyph_paint_shader = wgpuDeviceCreateShaderModule(engine.device, &descriptor);
        if (engine.glyph_paint_shader == nullptr) return false;
    }
    const std::array<WGPUBindGroupLayout, 4U> layouts{{engine.glyph_paint_uniform_layout,
        engine.text_atlas_layout, chained ? engine.semantic_mask_chain_layout :
            masked ? engine.layer_mask_layout : engine.glyph_paint_empty_mask_layout,
        engine.glyph_paint_texture_layout}};
    WGPUPipelineLayoutDescriptor layout_descriptor{};
    layout_descriptor.bindGroupLayoutCount = layouts.size(); layout_descriptor.bindGroupLayouts = layouts.data();
    auto layout = wgpuDeviceCreatePipelineLayout(engine.device, &layout_descriptor);
    if (layout == nullptr) return false;
    const std::array<WGPUVertexAttribute, 9U> attributes{{
        webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 0U, 0U),
        webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 8U, 1U),
        webgpu::vertex_attribute(WGPUVertexFormat_Float32x2, 16U, 2U),
        webgpu::vertex_attribute(WGPUVertexFormat_Float32x4, 24U, 3U),
        webgpu::vertex_attribute(WGPUVertexFormat_Float32x4, 40U, 4U),
        webgpu::vertex_attribute(WGPUVertexFormat_Float32x4, 56U, 5U),
        webgpu::vertex_attribute(WGPUVertexFormat_Float32x4, 72U, 6U),
        webgpu::vertex_attribute(WGPUVertexFormat_Float32, 88U, 7U),
        webgpu::vertex_attribute(WGPUVertexFormat_Uint32, 92U, 8U)}};
    WGPUVertexBufferLayout vertices{};
    vertices.arrayStride = sizeof(gpu_glyph_instance); vertices.stepMode = WGPUVertexStepMode_Instance;
    vertices.attributeCount = attributes.size(); vertices.attributes = attributes.data();
    const bool alpha_mask_target = engine.target_format == WGPUTextureFormat_R8Unorm;
    WGPUBlendState blend{};
    blend.color.srcFactor = alpha_mask_target || premultiplied_output
        ? WGPUBlendFactor_One : WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha = blend.color;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    WGPUColorTargetState target{};
    target.format = engine.target_format; target.blend = &blend; target.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.glyph_paint_shader;
    fragment.entryPoint = webgpu::string_view(alpha_mask_target
        ? chained ? "fs_mask_chain" : masked ? "fs_mask" : "fs_mask_unmasked"
        : premultiplied_output
            ? chained ? "fs_main_mask_chain_premultiplied" :
                masked ? "fs_main_premultiplied" : "fs_main_premultiplied_unmasked"
            : chained ? "fs_main_mask_chain" : masked ? "fs_main" : "fs_main_unmasked");
    fragment.targetCount = 1U; fragment.targets = &target;
    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = webgpu::string_view("ProGPU direct original occurrence paint");
    descriptor.layout = layout; descriptor.vertex.module = engine.glyph_paint_shader;
    descriptor.vertex.entryPoint = webgpu::string_view("vs_main");
    descriptor.vertex.bufferCount = 1U; descriptor.vertex.buffers = &vertices;
    descriptor.primitive.topology = WGPUPrimitiveTopology_TriangleList;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW; descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.multisample.count = 1U; descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment;
    pipeline = wgpuDeviceCreateRenderPipeline(engine.device, &descriptor);
    wgpuPipelineLayoutRelease(layout);
    return pipeline != nullptr;
}

bool prepare_glyph_paint_pipelines(progpu_native_engine& engine,
    bool masked, bool chained) {
    const bool alpha_mask_target = engine.target_format == WGPUTextureFormat_R8Unorm;
    for (const auto& paint : engine.semantic_glyph_cache.paints) {
        if (!ensure_glyph_paint_pipeline(engine, masked, chained,
            glyph_paint_premultiplied_output(paint, alpha_mask_target))) return false;
    }
    return true;
}

bool prepare_glyph_paints(progpu_native_engine& engine,
    std::uint64_t identity, std::uint64_t& upload_bytes) {
    upload_bytes = 0U;
    const auto& page = engine.semantic_glyph_cache;
    if (page.paints.empty()) return true;
    if (page.paints.size() != page.paint_resources.size() ||
        !prepare_glyph_paint_pipelines(engine, false, false)) return false;
    const std::uint64_t required = page.paints.size() * sizeof(progpu_native_scene_glyph_paint);
    if (!admit_glyph_paint_storage(engine, required) ||
        engine.analytic_brush_buffer_size > engine.glyph_paint_storage_limit ||
        engine.analytic_gradient_buffer_size > engine.glyph_paint_storage_limit) return false;
    if (engine.glyph_paint_buffer == nullptr || required > engine.glyph_paint_buffer_size) {
        std::uint64_t capacity = 0U;
        if (!try_calculate_buffer_capacity(engine.glyph_paint_buffer_size, required, 96U,
            std::min({engine.max_buffer_size, engine.glyph_paint_storage_limit,
                engine.glyph_paint_max_buffer_size}), capacity)) return false;
        WGPUBufferDescriptor descriptor{};
        descriptor.label = webgpu::string_view("ProGPU retained original glyph paint records");
        descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst; descriptor.size = capacity;
        auto replacement = wgpuDeviceCreateBuffer(engine.device, &descriptor);
        if (replacement == nullptr) return false;
        engine.release_glyph_paint_uniform_bindings();
        if (engine.glyph_paint_buffer != nullptr) {
            wgpuBufferDestroy(engine.glyph_paint_buffer); wgpuBufferRelease(engine.glyph_paint_buffer);
        }
        engine.glyph_paint_buffer = replacement; engine.glyph_paint_buffer_size = capacity;
        engine.glyph_paint_owner_hash = 0U;
    }
    if (engine.glyph_paint_owner_hash != identity || engine.glyph_paint_owner_hash == 0U) {
        wgpuQueueWriteBuffer(engine.queue, engine.glyph_paint_buffer, 0U, page.paints.data(), required);
        engine.glyph_paint_owner_hash = identity;
        upload_bytes = required;
    }
    // Resolve the complete original view/sampler identity even when the new
    // page has exactly the old count. Count alone is not immutable identity.
    const auto resolve_texture = [&](std::size_t i, WGPUTextureView& view, WGPUSampler& sampler) {
        const auto& paint = page.paints[i];
        view = engine.analytic_sentinel_texture_view;
        sampler = engine.analytic_sentinel_sampler;
        if (paint.kind == PROGPU_NATIVE_SCENE_GLYPH_PAINT_TEXTURE) {
            if (!create_image_resources(engine)) return false;
            const auto& resource = page.paint_resources[i];
            const auto* binding = engine.find_semantic_external_image_binding(resource.resource_id, resource.generation);
            if (binding == nullptr || binding->view == nullptr || binding->width == 0U || binding->height == 0U ||
                binding->width > engine.glyph_paint_texture_limit || binding->height > engine.glyph_paint_texture_limit)
                return false;
            view = binding->view;
            const auto mode = (paint.flags & PROGPU_NATIVE_SCENE_GLYPH_PAINT_SAMPLING_MASK) >> 8U;
            const auto sampling = mode == 0U ? static_cast<std::uint32_t>(PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR) :
                mode == 1U ? static_cast<std::uint32_t>(PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST) : mode;
            const auto address_flags = static_cast<std::uint32_t>(paint.sampling[2]) <<
                PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_SHIFT |
                static_cast<std::uint32_t>(paint.sampling[3]) << PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_V_SHIFT;
            sampler = resolve_semantic_image_sampler(engine, sampling, 1U, address_flags);
        }
        return view != nullptr && sampler != nullptr;
    };
    bool same = engine.glyph_paint_texture_bindings.size() == page.paints.size();
    for (std::size_t i = 0U; same && i < page.paints.size(); ++i) {
        WGPUTextureView view = nullptr; WGPUSampler sampler = nullptr;
        if (!resolve_texture(i, view, sampler)) return false;
        const auto& binding = engine.glyph_paint_texture_bindings[i];
        same = binding.view == view && binding.sampler == sampler && binding.kind == page.paints[i].kind;
    }
    if (same) return true;
    std::vector<semantic_glyph_paint_texture_binding> replacements;
    const auto release = [&replacements]() noexcept {
        for (auto& binding : replacements) {
            if (binding.bind_group != nullptr) wgpuBindGroupRelease(binding.bind_group);
            if (binding.view != nullptr) wgpuTextureViewRelease(binding.view);
        }
    };
    try {
        replacements.resize(page.paints.size());
        for (std::size_t i = 0U; i < page.paints.size(); ++i) {
            const auto& paint = page.paints[i];
            WGPUTextureView view = nullptr; WGPUSampler sampler = nullptr;
            if (!resolve_texture(i, view, sampler)) { release(); return false; }
            std::array<WGPUBindGroupEntry, 2U> entries{};
            entries[0].binding = 0U; entries[0].sampler = sampler;
            entries[1].binding = 1U; entries[1].textureView = view;
            WGPUBindGroupDescriptor descriptor{};
            descriptor.label = webgpu::string_view("ProGPU original glyph paint texture binding");
            descriptor.layout = engine.glyph_paint_texture_layout;
            descriptor.entryCount = entries.size(); descriptor.entries = entries.data();
            replacements[i].bind_group = wgpuDeviceCreateBindGroup(engine.device, &descriptor);
            if (replacements[i].bind_group == nullptr) { release(); return false; }
            webgpu::texture_view_add_ref(view); replacements[i].view = view;
            replacements[i].sampler = sampler; replacements[i].kind = paint.kind;
        }
    } catch (const std::bad_alloc&) { release(); return false; }
    engine.release_glyph_paint_texture_bindings();
    engine.glyph_paint_texture_bindings = std::move(replacements);
    return true;
}

WGPUBindGroup glyph_paint_uniform_binding(progpu_native_engine& engine, std::uint32_t target_layer) {
    const auto uniform = select_uniform(engine, target_layer);
    if (uniform == nullptr || engine.analytic_brush_buffer == nullptr ||
        engine.analytic_gradient_buffer == nullptr || engine.glyph_paint_buffer == nullptr) return nullptr;
    for (const auto& binding : engine.glyph_paint_uniform_bindings)
        if (binding.uniform == uniform && binding.brushes == engine.analytic_brush_buffer &&
            binding.stops == engine.analytic_gradient_buffer && binding.paints == engine.glyph_paint_buffer)
            return binding.bind_group;
    std::array<WGPUBindGroupEntry, 4U> entries{};
    const std::array<WGPUBuffer, 4U> buffers{{uniform, engine.analytic_brush_buffer,
        engine.analytic_gradient_buffer, engine.glyph_paint_buffer}};
    const std::array<std::uint64_t, 4U> sizes{{sizeof(gpu_uniforms), engine.analytic_brush_buffer_size,
        engine.analytic_gradient_buffer_size, engine.glyph_paint_buffer_size}};
    for (std::uint32_t i = 0U; i < entries.size(); ++i) {
        entries[i].binding = i; entries[i].buffer = buffers[i]; entries[i].size = sizes[i];
    }
    WGPUBindGroupDescriptor descriptor{};
    descriptor.label = webgpu::string_view("ProGPU retained original glyph paint uniform binding");
    descriptor.layout = engine.glyph_paint_uniform_layout;
    descriptor.entryCount = entries.size(); descriptor.entries = entries.data();
    auto group = wgpuDeviceCreateBindGroup(engine.device, &descriptor);
    if (group == nullptr) return nullptr;
    try {
        engine.glyph_paint_uniform_bindings.push_back({uniform, buffers[1], buffers[2], buffers[3], group});
    } catch (const std::bad_alloc&) { wgpuBindGroupRelease(group); return nullptr; }
    return group;
}

} // namespace progpu::native::semantic
