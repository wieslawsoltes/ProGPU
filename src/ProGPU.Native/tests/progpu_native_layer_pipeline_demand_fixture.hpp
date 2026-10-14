#pragma once

// Original ProGPU provenance: the mask-pipeline-demand fixture supplies the
// provider, resolver and independent scene/image controls. No foreign code.
namespace {
WGPUProcDeviceCreateBuffer layer_demand_create_buffer{};
WGPUProcBufferRelease layer_demand_release_buffer{};
WGPUProcDeviceCreateBindGroup layer_demand_create_bind_group{};
bool layer_demand_track_buffers = false;
bool layer_demand_fail_bind_group = false;
std::uint32_t layer_demand_failures = 0U;
std::vector<WGPUBuffer> layer_demand_buffers;
std::vector<std::uint32_t> layer_demand_buffer_releases;

bool layer_demand_label(WGPUStringView label, const char* expected) {
    const auto length = std::strlen(expected);
    return label.data != nullptr &&
        (label.length == length || label.length == WGPU_STRLEN) &&
        std::strncmp(label.data, expected, length) == 0;
}

WGPUBuffer observe_layer_demand_buffer(
    WGPUDevice device, const WGPUBufferDescriptor* descriptor) {
    const auto result = layer_demand_create_buffer(device, descriptor);
    if (layer_demand_track_buffers && result != nullptr &&
        (layer_demand_label(descriptor->label, "ProGPU native group composite uniforms") ||
         layer_demand_label(descriptor->label, "ProGPU native group composite vertices") ||
         layer_demand_label(descriptor->label, "ProGPU native group composite indices"))) {
        layer_demand_buffers.push_back(result);
        layer_demand_buffer_releases.push_back(0U);
    }
    return result;
}

void observe_layer_demand_buffer_release(WGPUBuffer buffer) {
    if (layer_demand_track_buffers) {
        for (std::size_t index = 0U; index < layer_demand_buffers.size(); ++index) {
            if (layer_demand_buffers[index] == buffer) {
                ++layer_demand_buffer_releases[index];
                break;
            }
        }
    }
    layer_demand_release_buffer(buffer);
}

WGPUBindGroup observe_layer_demand_bind_group(
    WGPUDevice device, const WGPUBindGroupDescriptor* descriptor) {
    if (layer_demand_fail_bind_group && layer_demand_label(descriptor->label,
            "ProGPU native group composite uniform bind group")) {
        layer_demand_fail_bind_group = false;
        ++layer_demand_failures;
        return nullptr;
    }
    return layer_demand_create_bind_group(device, descriptor);
}

void* resolve_for_layer_pipeline_demand(void* context, const char* name) {
    void* result = resolve_for_mask_pipeline_demand(context, name);
    if (std::strcmp(name, "wgpuDeviceCreateBuffer") == 0) {
        std::memcpy(&layer_demand_create_buffer, &result, sizeof(result));
        const WGPUProcDeviceCreateBuffer observer = observe_layer_demand_buffer;
        std::memcpy(&result, &observer, sizeof(result));
    } else if (std::strcmp(name, "wgpuBufferRelease") == 0) {
        std::memcpy(&layer_demand_release_buffer, &result, sizeof(result));
        const WGPUProcBufferRelease observer = observe_layer_demand_buffer_release;
        std::memcpy(&result, &observer, sizeof(result));
    } else if (std::strcmp(name, "wgpuDeviceCreateBindGroup") == 0) {
        std::memcpy(&layer_demand_create_bind_group, &result, sizeof(result));
        const WGPUProcDeviceCreateBindGroup observer = observe_layer_demand_bind_group;
        std::memcpy(&result, &observer, sizeof(result));
    }
    return result;
}

int verify_layer_pipeline_demand(const char* library, const char* output, bool baseline) {
    void* module = dlopen(library, RTLD_NOW | RTLD_LOCAL);
    require(module != nullptr, dlerror());
    provider_api api(module);
    webscene_gpu_provider_options options{};
    options.struct_size = sizeof(options);
    options.required_capabilities = WEBSCENE_GPU_CAPABILITY_WEBGPU;
    auto* provider = api.create(&options);
    require(provider != nullptr, "Layer demand provider unavailable");
    auto device = create_device(api, provider);
    auto queue = resolve<WGPUProcDeviceGetQueue>(api, provider, "wgpuDeviceGetQueue")(device);
    resolver_context resolver{&api, provider};
    progpu_native_dawn_engine_options engine_options{};
    engine_options.struct_size = sizeof(engine_options);
    engine_options.native_abi_version = PROGPU_NATIVE_ABI_VERSION;
    engine_options.adapter_abi_version = PROGPU_NATIVE_DAWN_ADAPTER_ABI_VERSION;
    engine_options.provider_abi_version = WEBSCENE_GPU_PROVIDER_ABI_VERSION;
    engine_options.target_format = PROGPU_NATIVE_TEXTURE_FORMAT_BGRA8_UNORM;
    engine_options.resolver_context = &resolver;
    engine_options.resolve_proc = resolve_for_layer_pipeline_demand;
    engine_options.instance = reinterpret_cast<std::uintptr_t>(api.get_instance(provider));
    engine_options.device = reinterpret_cast<std::uintptr_t>(device);
    engine_options.queue = reinterpret_cast<std::uintptr_t>(queue);
    webscene_gpu_canvas_configuration canvas{};
    canvas.struct_size = sizeof(canvas);
    canvas.device = engine_options.device;
    canvas.usage = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc;
    canvas.pixel_format = WEBSCENE_GPU_PIXEL_FORMAT_BGRA8_UNORM;
    canvas.alpha_mode = WEBSCENE_GPU_ALPHA_MODE_PREMULTIPLIED;
    canvas.buffer_count = 2U;
    auto* file = std::fopen(output, "wb");
    require(file != nullptr, "Layer demand output could not be opened");
    // Reuse the original independent fixtures, with a separately declared
    // transient byte-opacity policy. Both complete images are compared with
    // the unchanged producer; later ordinary composites must still initialize.
    constexpr std::array<std::array<unsigned, 5>, 2> orders{{
        {{3U, 0U, 1U, 2U, 3U}}, {{2U, 3U, 1U, 0U, 3U}}}};
    for (std::uint32_t owner = 0U; owner < orders.size(); ++owner) {
        mask_demand_pipeline_counts.fill(0U);
        progpu_native_engine* engine{};
        require(progpu_native_dawn_engine_create(&engine_options, &engine) ==
            PROGPU_NATIVE_STATUS_SUCCESS && engine, "Layer demand engine creation failed");
        bool saw_image = false, saw_layer = false, saw_specialized_layer = false;
        std::uint64_t generation = 0U;
        for (const auto kind : orders[owner]) {
            auto scene = kind == 0U ? create_semantic_state_mask_scene_stream(64U, 64U) :
                kind == 1U ? create_semantic_state_mask_media_scene_stream(64U, 64U) :
                create_semantic_masked_layer_scene_stream();
            if (kind == 3U) {
                progpu_native_scene_header policy_header{};
                std::memcpy(&policy_header, scene.data(), sizeof(policy_header));
                for (std::size_t index = 0U; index < policy_header.command_count; ++index) {
                    progpu_native_scene_command command{};
                    std::memcpy(&command, scene.data() + policy_header.command_offset +
                        index * policy_header.command_stride, sizeof(command));
                    if (command.kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) continue;
                    progpu_native_scene_layer layer{};
                    std::memcpy(&layer, scene.data() + command.payload_offset, sizeof(layer));
                    layer.flags |= PROGPU_NATIVE_SCENE_LAYER_LINEAR_BYTE_OPACITY;
                    layer.content_revision = 0U;
                    layer.composite_revision = 0U;
                    layer.opacity = 0.625F;
                    std::memcpy(scene.data() + command.payload_offset, &layer, sizeof(layer));
                }
            }
            progpu_native_scene_header header{};
            std::memcpy(&header, scene.data(), sizeof(header));
            header.generation = ++generation;
            std::memcpy(scene.data(), &header, sizeof(header));
            progpu_native_scene_metrics update{};
            update.struct_size = sizeof(update);
            require(progpu_native_engine_update_scene(engine, scene.data(), scene.size(), &update) ==
                PROGPU_NATIVE_STATUS_SUCCESS, "Layer demand scene rejected");
            saw_image |= kind == 1U;
            saw_layer |= kind == 2U;
            saw_specialized_layer |= kind == 3U;
            std::vector<std::uint8_t> cold;
            for (std::uint32_t replay = 0U; replay < 2U; ++replay) {
                auto pixels = render_retained_fixture(api, provider, canvas, engine,
                    [&](progpu_native_engine* selected, std::uintptr_t view) {
                        progpu_native_scene_frame frame{};
                        frame.struct_size = sizeof(frame);
                        frame.width = 64U; frame.height = 64U; frame.dpi_scale = 1.0F;
                        frame.target_view = view;
                        frame.clear_color = {0.01F, 0.02F, 0.04F, 1.0F};
                        frame.scene_id = header.scene_id; frame.generation = generation;
                        progpu_native_scene_frame_metrics metrics{};
                        metrics.struct_size = sizeof(metrics);
                        const auto status = progpu_native_engine_render_scene(selected, &frame, &metrics);
                        if (status != PROGPU_NATIVE_STATUS_SUCCESS) {
                            std::array<char, 1024U> error{};
                            progpu_native_engine_get_last_error(engine, error.data(), error.size());
                            std::fprintf(stderr, "Layer demand kind %u: %s\n", kind, error.data());
                        }
                        require(status == PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count > 0U,
                            "Layer demand execution failed");
                        require(replay == 0U || metrics.vertex_upload_bytes == 0U,
                            "Layer warm replay uploaded a retained page");
                    });
                const bool needs_default = saw_layer || (baseline && saw_specialized_layer);
                const std::array<std::uint32_t, 3U> expected{{
                    saw_image ? 1U : 0U, needs_default ? 1U : 0U, needs_default ? 1U : 0U}};
                if (mask_demand_pipeline_counts != expected) {
                    std::fprintf(stderr, "Layer demand owner=%u kind=%u actual=%u/%u/%u expected=%u/%u/%u\n",
                        owner, kind, mask_demand_pipeline_counts[0], mask_demand_pipeline_counts[1],
                        mask_demand_pipeline_counts[2], expected[0], expected[1], expected[2]);
                }
                require(mask_demand_pipeline_counts == expected,
                    "Layer bindings compiled unused default pipelines or failed later actual demand");
                if (replay == 0U) cold = pixels;
                else require(cold == pixels, "Layer cold/warm complete pixels differ");
                require(std::fwrite(pixels.data(), 1U, pixels.size(), file) == pixels.size(),
                    "Layer demand pixel receipt write failed");
            }
        }
        progpu_native_engine_destroy(engine);
    }
    if (!baseline) {
        layer_demand_track_buffers = true;
        layer_demand_fail_bind_group = true;
        layer_demand_failures = 0U;
        layer_demand_buffers.clear();
        layer_demand_buffer_releases.clear();
        mask_demand_pipeline_counts.fill(0U);
        progpu_native_engine* engine{};
        require(progpu_native_dawn_engine_create(&engine_options, &engine) ==
            PROGPU_NATIVE_STATUS_SUCCESS && engine, "Failed-binding engine creation failed");
        const auto scene = create_semantic_masked_layer_scene_stream();
        progpu_native_scene_header header{};
        std::memcpy(&header, scene.data(), sizeof(header));
        progpu_native_scene_metrics update{};
        update.struct_size = sizeof(update);
        require(progpu_native_engine_update_scene(engine, scene.data(), scene.size(), &update) ==
            PROGPU_NATIVE_STATUS_SUCCESS, "Failed-binding scene rejected before allocation");
        for (unsigned attempt = 0U; attempt < 2U; ++attempt) {
            // Failed submissions are never pixel references. The second call
            // must reject the partial owner without reallocating or publishing it.
            render_retained_fixture(api, provider, canvas, engine,
                [&](progpu_native_engine* selected, std::uintptr_t view) {
                    progpu_native_scene_frame frame{};
                    frame.struct_size = sizeof(frame);
                    frame.width = 64U; frame.height = 64U; frame.dpi_scale = 1.0F;
                    frame.target_view = view;
                    frame.scene_id = header.scene_id; frame.generation = header.generation;
                    progpu_native_scene_frame_metrics metrics{};
                    metrics.struct_size = sizeof(metrics);
                    require(progpu_native_engine_render_scene(selected, &frame, &metrics) !=
                        PROGPU_NATIVE_STATUS_SUCCESS && layer_demand_failures == 1U &&
                        !layer_demand_fail_bind_group,
                        "Partial layer bindings were published or reallocated");
                });
            require(layer_demand_buffers.size() == 3U &&
                mask_demand_pipeline_counts == std::array<std::uint32_t, 3U>{},
                "Failed layer bindings created pipelines or duplicate buffers");
        }
        progpu_native_engine_destroy(engine);
        require(layer_demand_buffer_releases == std::vector<std::uint32_t>(3U, 1U),
            "Failed layer bindings did not release each owned buffer exactly once");
        layer_demand_track_buffers = false;
    }
    require(std::fclose(file) == 0, "Layer demand pixel receipt close failed");
    resolve<WGPUProcQueueRelease>(api, provider, "wgpuQueueRelease")(queue);
    resolve<WGPUProcDeviceRelease>(api, provider, "wgpuDeviceRelease")(device);
    api.destroy(provider);
    dlclose(module);
    std::fprintf(stderr, "Layer pipeline demand: 2 independent owners, 10 scenes, 20 full frames passed\n");
    return 0;
}
} // namespace
