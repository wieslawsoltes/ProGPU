// Differential oracle: execute the original ProGPU-owned header unchanged.
#include "progpu_native_geometry_analytic.hpp"
#include "progpu_native_path_dispatch.hpp"
#include <cstdio>

int main(int argc, char**) {
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
