// Differential oracle: execute the original ProGPU-owned header unchanged.
#include "progpu_native_geometry_analytic.hpp"
#include "progpu_native_path_dispatch.hpp"
#include "progpu_native_path_pixel_mapping.hpp"
#include <cstdio>
#include <cstring>

int main(int argc, char** argv) {
    if (argc == 2 && std::strcmp(argv[1], "frames") == 0) {
        std::array<float, 23> input{};
        while (std::fread(input.data(), sizeof(input), 1, stdin) == 1) {
            const progpu_native_affine_2d transform{
                input[0], input[1], input[2], input[3], input[4], input[5]};
            float maximum{}, minimum{};
            const std::uint32_t accepted = progpu::native::try_get_stroke_scales(
                transform, maximum, minimum) ? 1U : 0U;
            if (std::fwrite(&accepted, sizeof(accepted), 1, stdout) != 1) return 10;
            if (!accepted) continue;
            const float dpi = input[6];
            std::array<float, 12> values{maximum, minimum,
                progpu::native::quantize_subpixel_phase(transform.m31 * dpi),
                progpu::native::quantize_subpixel_phase(transform.m32 * dpi)};
            std::array<progpu_native_point, 4> physical{}, atlas{};
            for (std::size_t i = 0; i < 4; ++i) {
                progpu::native::transform_point(transform, input[7 + 2*i], input[8 + 2*i],
                    values[4 + 2*i], values[5 + 2*i]);
                physical[i] = {values[4 + 2*i] * dpi, values[5 + 2*i] * dpi};
                atlas[i] = {input[15 + 2*i], input[16 + 2*i]};
            }
            const std::uint32_t exact = progpu::native::exact_path_pixel_projection(dpi) &&
                progpu::native::exact_path_pixel_mapping(physical, atlas) ? 1U : 0U;
            if (std::fwrite(values.data(), sizeof(values), 1, stdout) != 1) return 11;
            if (std::fwrite(&exact, sizeof(exact), 1, stdout) != 1) return 12;
        }
        return std::ferror(stdin) ? 13 : 0;
    }
    if (argc == 2) {
        std::vector<progpu::native::gpu_path_uniforms> uniforms;
        progpu::native::gpu_path_uniforms item{};
        while (std::fread(&item, sizeof(item), 1, stdin) == 1) uniforms.push_back(item);
        const auto plan = progpu::native::group_single_path_dispatches(uniforms);
        const auto count = static_cast<std::uint64_t>(plan.groups.size());
        if (std::fwrite(&count, sizeof(count), 1, stdout) != 1) return 7;
        for (const auto& group : plan.groups) {
            const std::uint64_t values[]{group.offset, group.bytes, group.x, group.y, group.count};
            if (std::fwrite(values, sizeof(values), 1, stdout) != 1) return 8;
        }
        if (std::fwrite(plan.uniforms.data(), 1, plan.uniforms.size(), stdout) != plan.uniforms.size()) return 9;
        return 0;
    }
    progpu_native_analytic_primitive primitive{};
    float padding{};
    while (std::fread(&primitive, sizeof(primitive), 1, stdin) == 1) {
        if (std::fread(&padding, sizeof(padding), 1, stdin) != 1) return 2;
        std::vector<progpu::native::vector_vertex> vertices;
        std::vector<std::uint32_t> indices;
        const std::uint32_t accepted = progpu::native::append_analytic_primitive(
            primitive, padding, vertices, indices) ? 1U : 0U;
        if (std::fwrite(&accepted, sizeof(accepted), 1, stdout) != 1) return 3;
        if (accepted != 0U) {
            if (std::fwrite(vertices.data(), sizeof(vertices[0]), vertices.size(), stdout) != vertices.size()) return 4;
            if (std::fwrite(indices.data(), sizeof(indices[0]), indices.size(), stdout) != indices.size()) return 5;
        }
    }
    return std::ferror(stdin) ? 6 : 0;
}
