#pragma once
#include "progpu_native_vertex_mesh.hpp"
#include <span>

namespace progpu::native {

inline bool valid_source_path_point(progpu_native_point p) noexcept {
    return std::isfinite(p.x) && std::isfinite(p.y) &&
        std::abs(p.x) <= 16384.F && std::abs(p.y) <= 16384.F &&
        p.x * 16.F == std::floor(p.x * 16.F) && p.y * 16.F == std::floor(p.y * 16.F);
}
inline bool valid_source_path_shape(const progpu_native_scene_path_fill& path,
    std::size_t count) noexcept {
    return path.segment_count > 0U && path.segment_offset <= count && path.segment_count <= count-path.segment_offset &&
        path.boolean_node_offset == 0U && path.boolean_node_count == 0U && path.sample_grid == 8U && path.fill_rule <= 1U &&
        path.color.r == 1.F && path.color.g == 1.F && path.color.b == 1.F && path.color.a == 1.F &&
        path.transform.m11 == 1.F && path.transform.m12 == 0.F && path.transform.m21 == 0.F &&
        path.transform.m22 == 1.F && path.transform.m31 == 0.F && path.transform.m32 == 0.F &&
        std::isfinite(path.min_x) && std::isfinite(path.min_y) && std::isfinite(path.max_x) && std::isfinite(path.max_y) &&
        path.min_x < path.max_x && path.min_y < path.max_y &&
        path.min_x >= -16384.F && path.min_y >= -16384.F && path.max_x <= 16384.F && path.max_y <= 16384.F;
}
// Read each segment by value, including raw unaligned caller buffers. Each
// original closed contour remains independent; no implicit joining or snapping.
inline bool valid_source_path_contours(const progpu_native_scene_path_fill& path,
    const std::byte* bytes, std::size_t count) noexcept {
    if (!bytes || !valid_source_path_shape(path,count)) return false;
    bool open=false;progpu_native_point start{},current{};
    const auto equal=[](auto a,auto b){return a.x==b.x&&a.y==b.y;};
    for(std::size_t i=path.segment_offset;i<path.segment_offset+path.segment_count;++i) {
        progpu_native_path_segment s{};std::memcpy(&s,bytes+i*sizeof(s),sizeof(s));
        if(s.kind!=PROGPU_NATIVE_PATH_SEGMENT_LINE||s.pad0||s.pad1||s.pad2||
            s.p2.x!=0.F||s.p2.y!=0.F||s.p3.x!=0.F||s.p3.y!=0.F||
            !valid_source_path_point(s.p0)||!valid_source_path_point(s.p1))return false;
        for(const auto p:{s.p0,s.p1})if(p.x<path.min_x||p.y<path.min_y||p.x>path.max_x||p.y>path.max_y)return false;
        if(!open){start=s.p0;open=true;}else if(!equal(s.p0,current))return false;
        current=s.p1;if(equal(current,start))open=false;
    }
    return !open;
}
inline bool valid_source_paths(std::span<const progpu_native_scene_path_fill> paths,
    std::span<const progpu_native_path_segment> segments,
    const progpu_native_scene_source_coverage_frame& frame) noexcept {
    if(!valid_source_coverage_frame(frame)||paths.empty()||segments.size()>1048576U)return false;
    std::size_t end=0U;
    for(const auto& path:paths){
        if(path.segment_offset!=end||!valid_source_path_contours(path,
            reinterpret_cast<const std::byte*>(segments.data()),segments.size()))return false;
        end+=path.segment_count;
    }
    return end==segments.size();
}
inline bool validate_source_path_draw(const std::byte* bytes,const progpu_native_scene_header& header,
    const progpu_native_scene_command& command,std::uint32_t& error_offset) noexcept {
    error_offset=command.payload_offset;
    if(command.resource_index>=header.resource_count||command.payload_size<sizeof(progpu_native_scene_draw_brushes)+sizeof(progpu_native_scene_source_coverage_frame))return false;
    progpu_native_scene_source_coverage_frame frame{};
    std::memcpy(&frame,bytes+command.payload_offset+command.payload_size-sizeof(frame),sizeof(frame));
    if(!valid_source_coverage_frame(frame))return false;
    progpu_native_scene_resource resource{};
    std::memcpy(&resource,bytes+header.resource_offset+std::size_t(command.resource_index)*header.resource_stride,sizeof(resource));
    if(resource.kind!=PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH||resource.payload_size==0U||resource.payload_size%sizeof(progpu_native_scene_path_fill)!=0U||
        resource.auxiliary_size%sizeof(progpu_native_path_segment)!=0U)return false;
    const auto count=resource.auxiliary_size/sizeof(progpu_native_path_segment);if(count>1048576U)return false;
    std::size_t end=0U;
    for(std::size_t offset=0;offset<resource.payload_size;offset+=sizeof(progpu_native_scene_path_fill)){
        progpu_native_scene_path_fill path{};std::memcpy(&path,bytes+resource.payload_offset+offset,sizeof(path));
        if(path.segment_offset!=end||!valid_source_path_contours(path,bytes+resource.auxiliary_offset,count)){
            error_offset=resource.payload_offset+static_cast<std::uint32_t>(offset);return false;}
        end+=path.segment_count;
    }
    return end==count;
}
}
