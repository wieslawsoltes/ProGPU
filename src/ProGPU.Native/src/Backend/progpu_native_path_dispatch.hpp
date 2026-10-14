#pragma once

#include "progpu_native_gpu_records.hpp"
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <span>
#include <stdexcept>
#include <vector>

namespace progpu::native {

struct path_raster_dispatch {
    std::uint64_t offset;
    std::uint64_t bytes;
    std::uint32_t x;
    std::uint32_t y;
    std::uint32_t count;
};

struct path_raster_dispatch_plan {
    std::vector<std::byte> uniforms;
    std::vector<path_raster_dispatch> groups;
};

// Original ProGPU.Vector/PathAtlas.cs size-batch policy at 5853d276.
// Each shader Z slice still receives one unchanged 48-byte uniform. Only its
// binding position changes; source/record/output identities and shader math do
// not. O(N log N) CPU ordering, O(N) storage, no work for retained warm frames.
inline path_raster_dispatch_plan group_single_path_dispatches(
    std::span<const gpu_path_uniforms> source) {
    if (source.size() > (1U << 20U))
        throw std::length_error("The native path dispatch inventory exceeds its bound.");
    std::vector<std::uint32_t> order;
    order.reserve(source.size());
    for (std::size_t i = 0; i < source.size(); ++i) {
        if (source[i].path_op_kind != 0U)
            throw std::invalid_argument("Only independent single paths may be regrouped.");
        order.push_back(static_cast<std::uint32_t>(i));
    }
    // Division before addition keeps all uint32 widths/heights representable.
    const auto x = [&](std::uint32_t i) {
        return source[i].width / 64U + (source[i].width % 64U != 0U);
    };
    const auto y = [&](std::uint32_t i) {
        return source[i].height / 16U + (source[i].height % 16U != 0U);
    };
    std::sort(order.begin(), order.end(), [&](std::uint32_t a, std::uint32_t b) {
        if (x(a) != x(b)) return x(a) < x(b);
        if (y(a) != y(b)) return y(a) < y(b);
        return a < b;
    });
    path_raster_dispatch_plan result;
    result.groups.reserve(source.size());
    result.uniforms.reserve(source.size() * sizeof(gpu_path_uniforms));
    for (std::size_t first = 0; first < order.size();) {
        std::size_t end = first + 1U;
        while (end < order.size() && end - first < 65535U &&
            x(order[end]) == x(order[first]) && y(order[end]) == y(order[first])) ++end;
        const std::size_t offset = (result.uniforms.size() + 255U) & ~std::size_t{255U};
        const std::size_t bytes = (end - first) * sizeof(gpu_path_uniforms);
        result.uniforms.resize(offset + bytes);
        for (std::size_t i = first; i < end; ++i)
            std::memcpy(result.uniforms.data() + offset + (i - first) * sizeof(gpu_path_uniforms),
                &source[order[i]], sizeof(gpu_path_uniforms));
        result.groups.push_back({offset, bytes, x(order[first]), y(order[first]),
            static_cast<std::uint32_t>(end - first)});
        first = end;
    }
    return result;
}

} // namespace progpu::native
