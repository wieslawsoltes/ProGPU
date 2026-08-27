#include "progpu_native_3d_execution.hpp"
#include "Native3DWgsl.generated.hpp"

#include <algorithm>
#include <array>
#include <cstring>
#include <limits>
#include <vector>

namespace progpu::native::execution {
namespace {

template<typename T>
T read_record(const std::byte* bytes, std::size_t offset) noexcept {
    T value{};
    std::memcpy(&value, bytes + offset, sizeof(value));
    return value;
}

progpu_native_matrix_4x4 multiply(
    const progpu_native_matrix_4x4& left,
    const progpu_native_matrix_4x4& right) noexcept {
    progpu_native_matrix_4x4 result{};
    const auto* a = &left.m11;
    const auto* b = &right.m11;
    auto* r = &result.m11;
    for (std::size_t row = 0U; row < 4U; ++row) {
        for (std::size_t column = 0U; column < 4U; ++column) {
            float value = 0.0F;
            for (std::size_t inner = 0U; inner < 4U; ++inner) {
                value += a[row * 4U + inner] * b[inner * 4U + column];
            }
            r[row * 4U + column] = value;
        }
    }
    return result;
}

progpu_native_matrix_4x4 affine_matrix(
    const progpu_native_affine_2d& value) noexcept {
    return {
        value.m11, value.m12, 0.0F, 0.0F,
        value.m21, value.m22, 0.0F, 0.0F,
        0.0F, 0.0F, 1.0F, 0.0F,
        value.m31, value.m32, 0.0F, 1.0F};
}

void release_page_buffers(semantic_3d_page& page) noexcept {
    if (page.bind_group != nullptr) {
        wgpuBindGroupRelease(page.bind_group);
        page.bind_group = nullptr;
    }
    const auto release = [](WGPUBuffer& buffer) noexcept {
        if (buffer != nullptr) {
            wgpuBufferRelease(buffer);
            buffer = nullptr;
        }
    };
    release(page.camera_buffer);
    release(page.line_buffer);
    release(page.mesh_buffer);
    release(page.vertex_buffer);
    release(page.index_buffer);
    page.cache_valid = false;
}

WGPUBuffer create_storage_buffer(
    progpu_native_engine& engine,
    const char* label,
    const void* data,
    std::size_t size,
    std::size_t minimum_size) {
    const std::size_t allocated = std::max(size, minimum_size);
    WGPUBufferDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
    descriptor.size = allocated;
    WGPUBuffer result = wgpuDeviceCreateBuffer(engine.device, &descriptor);
    if (result != nullptr && size != 0U) {
        wgpuQueueWriteBuffer(engine.queue, result, 0U, data, size);
    }
    return result;
}

WGPURenderPipeline create_pipeline(
    progpu_native_engine& engine,
    const char* label,
    const char* vertex_entry,
    const char* fragment_entry,
    WGPUPrimitiveTopology topology) {
    WGPUVertexState vertex{};
    vertex.module = engine.semantic_3d_shader;
    vertex.entryPoint = progpu::native::webgpu::string_view(vertex_entry);

    WGPUBlendState blend{};
    blend.color.srcFactor = WGPUBlendFactor_SrcAlpha;
    blend.color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.color.operation = WGPUBlendOperation_Add;
    blend.alpha.srcFactor = WGPUBlendFactor_One;
    blend.alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
    blend.alpha.operation = WGPUBlendOperation_Add;
    WGPUColorTargetState color{};
    color.format = engine.target_format;
    color.blend = &blend;
    color.writeMask = WGPUColorWriteMask_All;
    WGPUFragmentState fragment{};
    fragment.module = engine.semantic_3d_shader;
    fragment.entryPoint = progpu::native::webgpu::string_view(fragment_entry);
    fragment.targetCount = 1U;
    fragment.targets = &color;

    WGPUDepthStencilState depth{};
    depth.format = WGPUTextureFormat_Depth24Plus;
#if defined(PROGPU_NATIVE_DAWN_ABI)
    depth.depthWriteEnabled = WGPUOptionalBool_True;
#else
    depth.depthWriteEnabled = true;
#endif
    depth.depthCompare = WGPUCompareFunction_LessEqual;
    depth.stencilFront.compare = WGPUCompareFunction_Always;
    depth.stencilBack.compare = WGPUCompareFunction_Always;
    depth.stencilReadMask = 0xFFFFFFFFU;
    depth.stencilWriteMask = 0xFFFFFFFFU;

    WGPURenderPipelineDescriptor descriptor{};
    descriptor.label = progpu::native::webgpu::string_view(label);
    descriptor.layout = engine.semantic_3d_pipeline_layout;
    descriptor.vertex = vertex;
    descriptor.primitive.topology = topology;
    descriptor.primitive.frontFace = WGPUFrontFace_CCW;
    descriptor.primitive.cullMode = WGPUCullMode_None;
    descriptor.depthStencil = &depth;
    descriptor.multisample.count = 1U;
    descriptor.multisample.mask = 0xFFFFFFFFU;
    descriptor.fragment = &fragment;
    return wgpuDeviceCreateRenderPipeline(engine.device, &descriptor);
}

} // namespace

bool create_semantic_3d_pipelines(progpu_native_engine& engine) {
    if (engine.semantic_line_3d_pipeline != nullptr &&
        engine.semantic_mesh_3d_pipeline != nullptr &&
        engine.semantic_mesh_strip_3d_pipeline != nullptr) {
        return true;
    }
    if (engine.semantic_3d_shader == nullptr) {
        progpu::native::webgpu::wgsl_source wgsl(
            progpu::native::generated::native_3d_wgsl,
            progpu::native::generated::native_3d_wgsl_size);
        WGPUShaderModuleDescriptor descriptor{};
        descriptor.nextInChain = wgsl.chain();
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU shared Native3D.wgsl");
        engine.semantic_3d_shader = wgpuDeviceCreateShaderModule(
            engine.device, &descriptor);
        if (engine.semantic_3d_shader == nullptr) {
            return false;
        }
    }
    if (engine.semantic_3d_layout == nullptr) {
        std::array<WGPUBindGroupLayoutEntry, 5U> entries{};
        const std::array<std::uint64_t, 5U> sizes{{
            sizeof(progpu::native::three_d::camera_record),
            sizeof(progpu::native::three_d::line_record),
            sizeof(progpu::native::three_d::mesh_record),
            sizeof(progpu_native_scene_mesh_3d_vertex),
            sizeof(std::uint32_t)}};
        for (std::uint32_t index = 0U; index < entries.size(); ++index) {
            entries[index].binding = index;
            entries[index].visibility =
                WGPUShaderStage_Vertex | WGPUShaderStage_Fragment;
            entries[index].buffer.type = WGPUBufferBindingType_ReadOnlyStorage;
            entries[index].buffer.minBindingSize = sizes[index];
        }
        WGPUBindGroupLayoutDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained 3D storage layout");
        descriptor.entryCount = entries.size();
        descriptor.entries = entries.data();
        engine.semantic_3d_layout = wgpuDeviceCreateBindGroupLayout(
            engine.device, &descriptor);
        if (engine.semantic_3d_layout == nullptr) {
            return false;
        }
    }
    if (engine.semantic_3d_pipeline_layout == nullptr) {
        WGPUPipelineLayoutDescriptor descriptor{};
        descriptor.label = progpu::native::webgpu::string_view(
            "ProGPU native retained 3D pipeline layout");
        descriptor.bindGroupLayoutCount = 1U;
        descriptor.bindGroupLayouts = &engine.semantic_3d_layout;
        engine.semantic_3d_pipeline_layout = wgpuDeviceCreatePipelineLayout(
            engine.device, &descriptor);
        if (engine.semantic_3d_pipeline_layout == nullptr) {
            return false;
        }
    }
    if (engine.semantic_line_3d_pipeline == nullptr) {
        engine.semantic_line_3d_pipeline = create_pipeline(
            engine, "ProGPU native retained 3D line pipeline",
            "vs_line_3d", "fs_line_3d", WGPUPrimitiveTopology_TriangleList);
    }
    if (engine.semantic_mesh_3d_pipeline == nullptr) {
        engine.semantic_mesh_3d_pipeline = create_pipeline(
            engine, "ProGPU native retained 3D mesh pipeline",
            "vs_mesh_3d", "fs_mesh_3d", WGPUPrimitiveTopology_TriangleList);
    }
    if (engine.semantic_mesh_strip_3d_pipeline == nullptr) {
        engine.semantic_mesh_strip_3d_pipeline = create_pipeline(
            engine, "ProGPU native retained 3D mesh strip pipeline",
            "vs_mesh_3d", "fs_mesh_3d", WGPUPrimitiveTopology_TriangleStrip);
    }
    return engine.semantic_line_3d_pipeline != nullptr &&
        engine.semantic_mesh_3d_pipeline != nullptr &&
        engine.semantic_mesh_strip_3d_pipeline != nullptr;
}

progpu_native_status compile_semantic_3d_page(
    progpu_native_engine& engine,
    const std::byte* bytes,
    const progpu_native_scene_header& header,
    const progpu_native_scene_frame& frame,
    std::uint32_t expected_draw_count,
    std::uint64_t& upload_bytes) {
    auto& page = engine.semantic_3d_cache;
    if (expected_draw_count == 0U) {
        upload_bytes = 0U;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    if (page.cache_valid && page.scene_hash == engine.semantic_hashes.three_d &&
        page.dpi_scale == frame.dpi_scale &&
        page.target_width == frame.width && page.target_height == frame.height &&
        page.draws.size() == expected_draw_count) {
        upload_bytes = 0U;
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    if (!create_semantic_3d_pipelines(engine)) {
        return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native retained 3D pipelines could not be created.");
    }

    std::vector<progpu::native::three_d::camera_record> cameras;
    std::vector<progpu::native::three_d::line_record> lines;
    std::vector<progpu::native::three_d::mesh_record> meshes;
    std::vector<progpu_native_scene_mesh_3d_vertex> vertices;
    std::vector<std::uint32_t> indices;
    std::vector<semantic_3d_draw> draws;
    std::vector<std::uint32_t> topologies;
    std::vector<std::uint32_t> mesh_index_counts;
    try {
        draws.reserve(expected_draw_count);
        semantic_state_cursor state_cursor(
            bytes, header, frame.dpi_scale);
        semantic_layer_target_cursor target_cursor(
            bytes, frame.width, frame.height, frame.dpi_scale);
        for (std::uint32_t command_index = 0U;
             command_index < header.command_count; ++command_index) {
            const auto command = read_record<progpu_native_scene_command>(
                bytes, header.command_offset +
                    static_cast<std::size_t>(command_index) * header.command_stride);
            const auto target = target_cursor.advance(command);
            const auto state = localize_semantic_state(
                state_cursor.advance(command), target, frame.dpi_scale);
            if (command.kind !=
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH &&
                command.kind !=
                    PROGPU_NATIVE_SCENE_COMMAND_DRAW_MESH_3D_BATCH) {
                continue;
            }
            const auto resource = read_record<progpu_native_scene_resource>(
                bytes, header.resource_offset +
                    static_cast<std::size_t>(command.resource_index) * header.resource_stride);
            const auto camera = read_record<progpu_native_scene_camera_3d>(
                bytes, command.payload_offset);
            progpu::native::three_d::camera_record gpu_camera{};
            gpu_camera.projection = camera.projection;
            gpu_camera.view = camera.view;
            gpu_camera.camera_position[0] = camera.camera_position.x;
            gpu_camera.camera_position[1] = camera.camera_position.y;
            gpu_camera.camera_position[2] = camera.camera_position.z;
            gpu_camera.camera_position[3] = 1.0F;
            gpu_camera.viewport[0] = static_cast<float>(std::max(1U, target.width));
            gpu_camera.viewport[1] = static_cast<float>(std::max(1U, target.height));
            gpu_camera.viewport[2] = frame.dpi_scale;
            gpu_camera.viewport[3] = 0.0F;
            const float target_width = gpu_camera.viewport[0];
            const float target_height = gpu_camera.viewport[1];
            const float viewport_left = std::clamp(
                command.bounds_x * frame.dpi_scale -
                    static_cast<float>(target.x),
                0.0F,
                target_width);
            const float viewport_top = std::clamp(
                command.bounds_y * frame.dpi_scale -
                    static_cast<float>(target.y),
                0.0F,
                target_height);
            const float viewport_right = std::clamp(
                (command.bounds_x + command.bounds_width) *
                    frame.dpi_scale - static_cast<float>(target.x),
                viewport_left,
                target_width);
            const float viewport_bottom = std::clamp(
                (command.bounds_y + command.bounds_height) *
                    frame.dpi_scale - static_cast<float>(target.y),
                viewport_top,
                target_height);
            gpu_camera.viewport_rect[0] = viewport_left;
            gpu_camera.viewport_rect[1] = viewport_top;
            gpu_camera.viewport_rect[2] = std::max(
                viewport_right - viewport_left, 1.0F);
            gpu_camera.viewport_rect[3] = std::max(
                viewport_bottom - viewport_top, 1.0F);
            const auto camera_index = static_cast<std::uint32_t>(cameras.size());
            cameras.push_back(gpu_camera);
            const auto state_transform = affine_matrix(state.transform);

            if (command.kind ==
                PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH) {
                const auto count = resource.payload_size /
                    sizeof(progpu_native_scene_line_3d);
                const auto first = static_cast<std::uint32_t>(lines.size());
                for (std::uint32_t index = 0U; index < count; ++index) {
                    const auto source = read_record<progpu_native_scene_line_3d>(
                        bytes, resource.payload_offset +
                            static_cast<std::size_t>(index) *
                                sizeof(progpu_native_scene_line_3d));
                    progpu::native::three_d::line_record line{};
                    line.start[0] = source.start.x;
                    line.start[1] = source.start.y;
                    line.start[2] = source.start.z;
                    line.start[3] = 1.0F;
                    line.end[0] = source.end.x;
                    line.end[1] = source.end.y;
                    line.end[2] = source.end.z;
                    line.end[3] = 1.0F;
                    line.color = source.color;
                    line.thickness = source.thickness;
                    line.opacity = source.opacity * state.opacity;
                    line.camera_index = camera_index;
                    line.transform = multiply(source.transform, state_transform);
                    lines.push_back(line);
                }
                draws.push_back({command.kind, first,
                    static_cast<std::uint32_t>(count)});
                continue;
            }

            const auto mesh_count = resource.payload_size /
                sizeof(progpu_native_scene_mesh_3d);
            std::size_t source_vertex_count = 0U;
            for (std::uint32_t index = 0U; index < mesh_count; ++index) {
                const auto mesh = read_record<progpu_native_scene_mesh_3d>(
                    bytes, resource.payload_offset +
                        static_cast<std::size_t>(index) *
                            sizeof(progpu_native_scene_mesh_3d));
                source_vertex_count = std::max(source_vertex_count,
                    static_cast<std::size_t>(mesh.vertex_offset) + mesh.vertex_count);
            }
            const auto vertex_base = static_cast<std::uint32_t>(vertices.size());
            const auto* source_vertices = reinterpret_cast<
                const progpu_native_scene_mesh_3d_vertex*>(
                    bytes + resource.auxiliary_offset);
            const auto* source_indices = reinterpret_cast<const std::uint32_t*>(
                bytes + resource.auxiliary_offset +
                    source_vertex_count * sizeof(progpu_native_scene_mesh_3d_vertex));
            vertices.insert(vertices.end(), source_vertices,
                source_vertices + source_vertex_count);
            const auto first = static_cast<std::uint32_t>(meshes.size());
            for (std::uint32_t index = 0U; index < mesh_count; ++index) {
                const auto source = read_record<progpu_native_scene_mesh_3d>(
                    bytes, resource.payload_offset +
                        static_cast<std::size_t>(index) *
                            sizeof(progpu_native_scene_mesh_3d));
                progpu::native::three_d::mesh_record mesh{};
                mesh.flags = source.flags;
                // Triangle strips are expanded once into canonical triangle
                // lists so derivative barycentric wire coverage is exact for
                // both public topology modes and the replay pipeline is stable.
                mesh.topology = PROGPU_NATIVE_MESH_3D_TRIANGLES;
                mesh.render_mode = source.render_mode;
                mesh.camera_index = camera_index;
                mesh.vertex_offset = vertex_base + source.vertex_offset;
                mesh.vertex_count = source.vertex_count;
                mesh.index_offset = static_cast<std::uint32_t>(indices.size());
                if (source.topology == PROGPU_NATIVE_MESH_3D_TRIANGLES) {
                    indices.insert(
                        indices.end(),
                        source_indices + source.index_offset,
                        source_indices + source.index_offset +
                            source.index_count);
                } else {
                    for (std::uint32_t strip = 2U;
                         strip < source.index_count;
                         ++strip) {
                        const auto a = source_indices[
                            source.index_offset + strip - 2U];
                        const auto b = source_indices[
                            source.index_offset + strip - 1U];
                        const auto c = source_indices[
                            source.index_offset + strip];
                        if (a == b || b == c || a == c) {
                            continue;
                        }
                        if ((strip & 1U) == 0U) {
                            indices.insert(indices.end(), {a, b, c});
                        } else {
                            indices.insert(indices.end(), {b, a, c});
                        }
                    }
                }
                mesh.index_count = static_cast<std::uint32_t>(
                    indices.size() - mesh.index_offset);
                mesh.model_transform = multiply(source.model_transform, state_transform);
                mesh.normal_transform = source.normal_transform;
                mesh.color = source.color;
                mesh.light_direction = source.light_direction;
                mesh.ambient_color = source.ambient_color;
                mesh.specular_color = source.specular_color;
                mesh.material_ambient = source.material_ambient;
                mesh.opacity = source.opacity * state.opacity;
                mesh.shading_mode = source.shading_mode;
                meshes.push_back(mesh);
                topologies.push_back(PROGPU_NATIVE_MESH_3D_TRIANGLES);
                mesh_index_counts.push_back(mesh.index_count);
            }
            draws.push_back({command.kind, first,
                static_cast<std::uint32_t>(mesh_count)});
        }
    } catch (const std::bad_alloc&) {
        return engine.fail(PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native retained 3D page could not be compiled.");
    }
    if (draws.size() != expected_draw_count) {
        return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native retained 3D draw count changed after preflight.");
    }

    release_page_buffers(page);
    page.camera_buffer = create_storage_buffer(engine, "ProGPU 3D cameras",
        cameras.data(), cameras.size() * sizeof(cameras[0]), sizeof(cameras[0]));
    page.line_buffer = create_storage_buffer(engine, "ProGPU 3D lines",
        lines.data(), lines.size() * sizeof(lines[0]), sizeof(lines[0]));
    page.mesh_buffer = create_storage_buffer(engine, "ProGPU 3D meshes",
        meshes.data(), meshes.size() * sizeof(meshes[0]), sizeof(meshes[0]));
    page.vertex_buffer = create_storage_buffer(engine, "ProGPU 3D vertices",
        vertices.data(), vertices.size() * sizeof(vertices[0]), sizeof(vertices[0]));
    page.index_buffer = create_storage_buffer(engine, "ProGPU 3D indices",
        indices.data(), indices.size() * sizeof(indices[0]), sizeof(indices[0]));
    if (page.camera_buffer == nullptr || page.line_buffer == nullptr ||
        page.mesh_buffer == nullptr || page.vertex_buffer == nullptr ||
        page.index_buffer == nullptr) {
        release_page_buffers(page);
        return engine.fail(PROGPU_NATIVE_STATUS_OUT_OF_MEMORY,
            "The native retained 3D GPU page could not be allocated.");
    }
    std::array<WGPUBindGroupEntry, 5U> entries{};
    const std::array<WGPUBuffer, 5U> buffers{{page.camera_buffer,
        page.line_buffer, page.mesh_buffer, page.vertex_buffer, page.index_buffer}};
    const std::array<std::uint64_t, 5U> sizes{{
        std::max<std::uint64_t>(sizeof(cameras[0]), cameras.size() * sizeof(cameras[0])),
        std::max<std::uint64_t>(sizeof(lines[0]), lines.size() * sizeof(lines[0])),
        std::max<std::uint64_t>(sizeof(meshes[0]), meshes.size() * sizeof(meshes[0])),
        std::max<std::uint64_t>(sizeof(vertices[0]), vertices.size() * sizeof(vertices[0])),
        std::max<std::uint64_t>(sizeof(indices[0]), indices.size() * sizeof(indices[0]))}};
    for (std::uint32_t index = 0U; index < entries.size(); ++index) {
        entries[index].binding = index;
        entries[index].buffer = buffers[index];
        entries[index].size = sizes[index];
    }
    WGPUBindGroupDescriptor bind_descriptor{};
    bind_descriptor.label = progpu::native::webgpu::string_view(
        "ProGPU native retained 3D storage binding");
    bind_descriptor.layout = engine.semantic_3d_layout;
    bind_descriptor.entryCount = entries.size();
    bind_descriptor.entries = entries.data();
    page.bind_group = wgpuDeviceCreateBindGroup(engine.device, &bind_descriptor);
    if (page.bind_group == nullptr) {
        release_page_buffers(page);
        return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native retained 3D storage binding could not be created.");
    }
    page.draws = std::move(draws);
    page.mesh_topologies = std::move(topologies);
    page.mesh_index_counts = std::move(mesh_index_counts);
    page.scene_hash = engine.semantic_hashes.three_d;
    page.dpi_scale = frame.dpi_scale;
    page.target_width = frame.width;
    page.target_height = frame.height;
    page.cache_valid = true;
    upload_bytes = cameras.size() * sizeof(cameras[0]) +
        lines.size() * sizeof(lines[0]) + meshes.size() * sizeof(meshes[0]) +
        vertices.size() * sizeof(vertices[0]) + indices.size() * sizeof(indices[0]);
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

progpu_native_status encode_semantic_3d_bundle_draw(
    progpu_native_engine& engine,
    WGPURenderBundleEncoder encoder,
    const semantic_3d_draw& draw) {
    if (encoder == nullptr || engine.semantic_3d_cache.bind_group == nullptr) {
        return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
            "The native retained 3D replay binding is unavailable.");
    }
    wgpuRenderBundleEncoderSetBindGroup(
        encoder, 0U, engine.semantic_3d_cache.bind_group, 0U, nullptr);
    if (draw.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_LINE_3D_BATCH) {
        wgpuRenderBundleEncoderSetPipeline(
            encoder, engine.semantic_line_3d_pipeline);
        wgpuRenderBundleEncoderDraw(
            encoder, 6U, draw.record_count, 0U, draw.first_record);
        return PROGPU_NATIVE_STATUS_SUCCESS;
    }
    for (std::uint32_t index = 0U; index < draw.record_count; ++index) {
        const std::uint32_t record = draw.first_record + index;
        if (record >= engine.semantic_3d_cache.mesh_topologies.size() ||
            record >= engine.semantic_3d_cache.mesh_index_counts.size()) {
            return engine.fail(PROGPU_NATIVE_STATUS_INTERNAL_ERROR,
                "The native retained 3D mesh topology index is invalid.");
        }
        const auto topology = engine.semantic_3d_cache.mesh_topologies[record];
        wgpuRenderBundleEncoderSetPipeline(encoder,
            topology == PROGPU_NATIVE_MESH_3D_TRIANGLE_STRIP
                ? engine.semantic_mesh_strip_3d_pipeline
                : engine.semantic_mesh_3d_pipeline);
        wgpuRenderBundleEncoderDraw(
            encoder,
            engine.semantic_3d_cache.mesh_index_counts[record],
            1U,
            0U,
            record);
    }
    return PROGPU_NATIVE_STATUS_SUCCESS;
}

} // namespace progpu::native::execution
