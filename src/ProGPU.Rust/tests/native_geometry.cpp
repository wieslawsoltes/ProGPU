// Differential oracle: execute the original ProGPU-owned header unchanged.
#include "progpu_native_geometry_analytic.hpp"
#include <cstdio>

int main() {
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
