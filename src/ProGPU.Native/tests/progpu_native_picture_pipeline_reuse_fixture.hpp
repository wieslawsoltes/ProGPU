#pragma once

// Additive real-provider controls; no original corpus or fixture is modified.
namespace {
WGPUProcDeviceCreateRenderPipeline picture_reuse_create_pipeline{};
WGPUProcRenderPipelineRelease picture_reuse_release_pipeline{};
WGPUProcDeviceCreateBuffer picture_reuse_create_buffer{};
std::vector<WGPURenderPipeline> picture_reuse_pipelines;
std::vector<std::uint32_t> picture_reuse_releases;
bool picture_reuse_fail_uniform = false;
std::uint32_t picture_reuse_injected_failures = 0U;

bool picture_reuse_label(WGPUStringView label, const char* expected) {
    const auto size = std::strlen(expected);
    return label.data != nullptr &&
        (label.length == size || label.length == WGPU_STRLEN) &&
        std::strncmp(label.data, expected, size) == 0;
}

WGPURenderPipeline observe_picture_reuse_pipeline(
    WGPUDevice device, const WGPURenderPipelineDescriptor* descriptor) {
    auto pipeline = picture_reuse_create_pipeline(device, descriptor);
    if (pipeline != nullptr && picture_reuse_label(descriptor->label,
            "ProGPU native analytic primitive pipeline")) {
        picture_reuse_pipelines.push_back(pipeline);
        picture_reuse_releases.push_back(0U);
    }
    return pipeline;
}

void observe_picture_reuse_release(WGPURenderPipeline pipeline) {
    for (std::size_t index = 0U; index < picture_reuse_pipelines.size(); ++index) {
        if (picture_reuse_pipelines[index] == pipeline) {
            ++picture_reuse_releases[index];
            break;
        }
    }
    picture_reuse_release_pipeline(pipeline);
}

WGPUBuffer observe_picture_reuse_buffer(
    WGPUDevice device, const WGPUBufferDescriptor* descriptor) {
    if (picture_reuse_fail_uniform && picture_reuse_label(descriptor->label,
            "ProGPU native analytic frame uniforms")) {
        picture_reuse_fail_uniform = false;
        ++picture_reuse_injected_failures;
        return nullptr;
    }
    return picture_reuse_create_buffer(device, descriptor);
}

void* resolve_for_picture_pipeline_reuse(void* context, const char* name) {
    void* result = resolve_for_progpu(context, name);
    if (std::strcmp(name, "wgpuDeviceCreateRenderPipeline") == 0) {
        std::memcpy(&picture_reuse_create_pipeline, &result, sizeof(result));
        const WGPUProcDeviceCreateRenderPipeline observer = observe_picture_reuse_pipeline;
        std::memcpy(&result, &observer, sizeof(result));
    } else if (std::strcmp(name, "wgpuRenderPipelineRelease") == 0) {
        std::memcpy(&picture_reuse_release_pipeline, &result, sizeof(result));
        const WGPUProcRenderPipelineRelease observer = observe_picture_reuse_release;
        std::memcpy(&result, &observer, sizeof(result));
    } else if (std::strcmp(name, "wgpuDeviceCreateBuffer") == 0) {
        std::memcpy(&picture_reuse_create_buffer, &result, sizeof(result));
        const WGPUProcDeviceCreateBuffer observer = observe_picture_reuse_buffer;
        std::memcpy(&result, &observer, sizeof(result));
    }
    return result;
}

int verify_picture_pipeline_reuse(const char* library, const char* output, bool baseline) {
    void* module = dlopen(library, RTLD_NOW | RTLD_LOCAL);
    require(module != nullptr, dlerror());
    provider_api api(module);
    webscene_gpu_provider_options options{};
    options.struct_size = sizeof(options);
    options.required_capabilities = WEBSCENE_GPU_CAPABILITY_WEBGPU;
    auto* provider = api.create(&options);
    require(provider != nullptr, "Picture reuse provider unavailable");
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
    engine_options.resolve_proc = resolve_for_picture_pipeline_reuse;
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
    require(file != nullptr, "Picture reuse output could not be opened");
    constexpr std::array<std::array<unsigned, 4>, 3> orders{{
        {{0U, 1U, 0U, 1U}}, {{1U, 0U, 1U, 0U}}, {{0U, 2U, 0U, 0U}}}};
    for (std::uint32_t owner = 0U; owner < orders.size(); ++owner) {
        picture_reuse_pipelines.clear();
        picture_reuse_releases.clear();
        picture_reuse_fail_uniform = false;
        picture_reuse_injected_failures = 0U;
        progpu_native_engine* engine{};
        require(progpu_native_dawn_engine_create(&engine_options, &engine) ==
            PROGPU_NATIVE_STATUS_SUCCESS && engine, "Picture reuse engine creation failed");
        require(picture_reuse_pipelines.empty(), "Engine creation eagerly prepared analytics");
        bool saw_picture = false;
        std::uint64_t generation = 0U;
        for (const auto kind : orders[owner]) {
            auto scene = kind == 0U ? create_semantic_state_mask_scene_stream(64U, 64U) :
                create_semantic_picture_mask_scene_stream(64U, 64U);
            progpu_native_scene_header header{};
            std::memcpy(&header, scene.data(), sizeof(header));
            header.generation = ++generation;
            std::memcpy(scene.data(), &header, sizeof(header));
            progpu_native_scene_metrics update{};
            update.struct_size = sizeof(update);
            require(progpu_native_engine_update_scene(engine, scene.data(), scene.size(), &update) ==
                PROGPU_NATIVE_STATUS_SUCCESS, "Picture reuse scene rejected");
            saw_picture |= kind != 0U;
            std::vector<std::uint8_t> cold;
            picture_reuse_fail_uniform = kind == 2U;
            for (std::uint32_t replay = 0U; replay < (kind == 2U ? 1U : 2U); ++replay) {
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
                        if (kind == 2U) {
                            require(status != PROGPU_NATIVE_STATUS_SUCCESS &&
                                picture_reuse_injected_failures == 1U && !picture_reuse_fail_uniform,
                                "Failed child resources published analytic readiness");
                        } else {
                            require(status == PROGPU_NATIVE_STATUS_SUCCESS && metrics.draw_call_count > 0U,
                                "Picture reuse execution failed");
                            require(replay == 0U || metrics.vertex_upload_bytes == 0U,
                                "Picture warm replay uploaded a retained page");
                        }
                    });
                const auto expected = baseline && saw_picture ? 2U : 1U;
                require(picture_reuse_pipelines.size() == expected,
                    "Picture child compiled a duplicate parent analytic pipeline");
                if (kind == 2U) continue; // A failed submission is never a pixel reference.
                if (replay == 0U) cold = pixels;
                else require(cold == pixels, "Picture cold/warm complete pixels differ");
                require(std::fwrite(pixels.data(), 1U, pixels.size(), file) == pixels.size(),
                    "Picture reuse pixel receipt write failed");
            }
        }
        progpu_native_engine_destroy(engine);
        // A provider may intern identical pipelines and return another owned
        // reference to the same handle. Match releases to successful creations
        // per actual handle, rather than assuming distinct pointer identities.
        for (std::size_t index = 0U; index < picture_reuse_pipelines.size(); ++index) {
            const auto first = std::find(picture_reuse_pipelines.begin(),
                picture_reuse_pipelines.end(), picture_reuse_pipelines[index]);
            if (first != picture_reuse_pipelines.begin() + static_cast<std::ptrdiff_t>(index)) continue;
            const auto references = static_cast<std::uint32_t>(std::count(
                picture_reuse_pipelines.begin(), picture_reuse_pipelines.end(),
                picture_reuse_pipelines[index]));
            std::fprintf(stderr, "Picture owner=%u handle=%p created=%u released=%u\n",
                owner, static_cast<void*>(picture_reuse_pipelines[index]), references,
                picture_reuse_releases[index]);
            require(picture_reuse_releases[index] == references,
                "Parent/child pipeline ownership did not release each acquired reference exactly once");
        }
    }
    require(std::fclose(file) == 0, "Picture reuse pixel receipt close failed");
    resolve<WGPUProcQueueRelease>(api, provider, "wgpuQueueRelease")(queue);
    resolve<WGPUProcDeviceRelease>(api, provider, "wgpuDeviceRelease")(device);
    api.destroy(provider);
    dlclose(module);
    std::fprintf(stderr, "Picture analytic reuse: 3 independent owners, cold/warm frames and child failure passed\n");
    return 0;
}
} // namespace
