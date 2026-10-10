#pragma once
#include "progpu_native_scene_builder.hpp"

#include <array>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <vector>

namespace progpu::native::tests {

// Original ProGPU fixture, paired with CompositorClipTests. A scalar rectangle
// membership oracle checks every channel, independent of atlas/Boolean shaders.
inline bool build_path_pixel_mapping_fixture(bool clip, std::vector<std::byte>& stream)
{
    semantic_scene_builder builder(clip ? 0x9482U : 0x9481U, 1U);
    const auto identity = semantic_scene_builder::identity_transform();
    std::array<progpu_native_path_segment, 8> segments{};
    constexpr std::array<progpu_native_point, 8> points{{
        {0, 0}, {32, 0}, {32, 24}, {0, 24},
        {8, 4}, {40, 4}, {40, 20}, {8, 20}}};
    for (std::size_t i = 0; i < points.size(); ++i)
        segments[i] = {points[i], points[(i / 4) * 4 + (i + 1) % 4], {}, {},
            PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U};
    std::array<progpu_native_scene_path_boolean_node, 3> nodes{};
    nodes[0] = {0U, 4U, 0, 0, 32, 24, PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        PROGPU_NATIVE_PATH_BOOLEAN_LEAF, 0U, 0U};
    nodes[1] = {4U, 4U, 8, 4, 40, 20, PROGPU_NATIVE_FILL_RULE_NON_ZERO,
        PROGPU_NATIVE_PATH_BOOLEAN_LEAF, 0U, 0U};
    nodes[2].kind = PROGPU_NATIVE_PATH_BOOLEAN_UNION;
    progpu_native_analytic_primitive rectangle{};
    rectangle.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
    rectangle.width = rectangle.height = 64;
    rectangle.color = {1, 1, 1, 1};
    rectangle.transform = identity;
    constexpr progpu_native_image_rect bounds{0, 0, 64, 64};
    std::uint32_t white{}, red{};
    if (!builder.add_solid_brush({1, 1, 1, 1}, 1, white) ||
        !builder.add_solid_brush({1, 0, 0, 1}, 1, red) ||
        !builder.draw_analytic({&rectangle, 1U}, {&white, 1U}, bounds)) return false;
    auto transform = identity;
    transform.m31 = 8;
    transform.m32 = 40;
    if (clip) {
        const progpu_native_scene_clip_path path{0U, 8U, 0U, 3U,
            0, 0, 40, 24, transform, PROGPU_NATIVE_FILL_RULE_NON_ZERO, 4U,
            PROGPU_NATIVE_CLIP_INTERSECT, 0U};
        auto state = semantic_scene_builder::identity_state();
        state.flags = PROGPU_NATIVE_SCENE_STATE_MASK;
        std::uint32_t state_index{};
        if (!builder.add_vector_clip_mask({&path, 1U}, segments, nodes, 1,
                state.mask_resource_index) || !builder.add_state(state, state_index) ||
            !builder.draw_analytic({&rectangle, 1U}, {&red, 1U}, bounds, state_index)) return false;
    } else {
        const progpu_native_scene_path_fill path{0U, 8U, 0U, 3U,
            0, 0, 40, 24, {1, 1, 1, 1}, transform, PROGPU_NATIVE_FILL_RULE_NON_ZERO, 4U};
        if (!builder.draw_paths({&path, 1U}, segments, {&red, 1U}, bounds,
                PROGPU_NATIVE_SCENE_NO_INDEX, nodes)) return false;
    }
    return builder.build(stream);
}

template<typename Render, typename Require>
void verify_path_pixel_mapping(Render render, Require require)
{
    for (bool clip : {false, true}) {
        std::vector<std::byte> stream;
        require(build_path_pixel_mapping_fixture(clip, stream), "pixel mapping fixture construction failed");
        for (const float dpi : {1.0F, 2.0F}) for (unsigned frame = 0; frame < 2; ++frame) {
            const auto extent = static_cast<unsigned>(64.0F * dpi);
            progpu_native_scene_frame_metrics metrics{};
            const auto pixels = render(clip, stream, dpi, extent, metrics);
            require(pixels.size() == extent * extent * 4U, "pixel mapping readback size changed");
            if (frame != 0U)
                require(metrics.coverage_staging_bytes == 0U && metrics.vertex_upload_bytes == 0U &&
                    metrics.index_upload_bytes == 0U, "warm pixel mapping rebuilt retained geometry");
            for (unsigned y = 0; y < extent; ++y) {
                for (unsigned x = 0; x < extent; ++x) {
                    const auto px = (static_cast<float>(x) + .5F) / dpi;
                    const auto py = (static_cast<float>(y) + .5F) / dpi;
                    const bool inside = (px >= 8 && px < 40 && py >= 40 && py < 64) ||
                        (px >= 16 && px < 48 && py >= 44 && py < 60);
                    const std::array<std::uint8_t, 4> expected{{255,
                        static_cast<std::uint8_t>(inside ? 0 : 255),
                        static_cast<std::uint8_t>(inside ? 0 : 255), 255}};
                    for (unsigned channel = 0; channel < 4; ++channel) {
                        const auto actual = pixels[(y * extent + x) * 4U + channel];
                        if (actual != expected[channel]) {
                            std::fprintf(stderr, "Pixel mapping clip=%u dpi=%g frame=%u (%u,%u) channel=%u actual=%u expected=%u\n",
                                clip ? 1U : 0U, static_cast<double>(dpi), frame, x, y, channel, actual, expected[channel]);
                            require(false, "integer-translated union changed a pixel");
                        }
                    }
                }
            }
            std::fprintf(stderr, "Pixel mapping clip=%u dpi=%g frame=%u submissions=%llu coverage=%llu exact pixels passed\n",
                clip ? 1U : 0U, static_cast<double>(dpi), frame, static_cast<unsigned long long>(metrics.submission_count),
                static_cast<unsigned long long>(metrics.coverage_staging_bytes));
        }
    }
}

// Independent rational scanline oracle: sort exact intersections, fill spans,
// then average 8x8 samples. This does not use the shader's per-sample half-plane
// predicate. Include the observed endpoint failures, negative coordinates, both
// windings, the exact signed-product bound and the ordinary off-grid fallback.
template<typename Render, typename Require>
void verify_exact_line_winding(Render render, Require require)
{
    struct point32 { std::int32_t x, y; };
    struct shape { std::array<point32, 8> points; std::uint32_t count, contour; };
    constexpr std::array<shape, 7> shapes{{
        {{{{198,274},{1588,364},{618,1078}}},3,3},
        {{{{-408,-210},{690,190},{-206,946}}},3,3},
        {{{{-442,-230},{486,2},{366,962},{-562,730}}},4,4},
        {{{{-428,-24},{440,424},{544,20},{-324,-428},
            {-116,330},{752,778},{856,374},{-12,-74}}},8,4},
        {{{{-32766,-32766},{32766,32766},{32766,-32766}}},3,3},
        {{{{-32768,-32768},{32768,32768},{32768,-32768}}},3,3},
        {{{{289,259},{993,259},{993,1033},{289,1033}}},4,4}
    }};
    std::uint64_t generation=1U;
    for (const auto& fixture:shapes) for (bool reversed:{false,true}) {
        std::vector<progpu_native_path_segment> segments;
        std::array<std::uint32_t,64U*64U> coverage{};
        float min_x=1024.F,min_y=1024.F,max_x=-1024.F,max_y=-1024.F;
        for(std::uint32_t i=0;i<fixture.count;++i) {
            const auto first=(i/fixture.contour)*fixture.contour;
            const auto next=first+(i+1U)%fixture.contour;
            const auto a=fixture.points[reversed?next:i],b=fixture.points[reversed?i:next];
            const progpu_native_point p{a.x/32.F,a.y/32.F},q{b.x/32.F,b.y/32.F};
            min_x=std::min(min_x,p.x);min_y=std::min(min_y,p.y);
            max_x=std::max(max_x,p.x);max_y=std::max(max_y,p.y);
            segments.push_back({p,q,{},{},PROGPU_NATIVE_PATH_SEGMENT_LINE,0U,0U,0U});
        }
        struct intersection { std::int64_t numerator,denominator; int direction; };
        const auto ceil_div=[](std::int64_t n,std::int64_t d){return n/d+(n%d>0?1:0);};
        for(std::uint32_t y=0;y<64;++y) for(std::uint32_t sy=0;sy<8;++sy) {
            std::vector<intersection> hits;
            const std::int64_t sample_y=y*32U+sy*4U+2U;
            for(std::uint32_t i=0;i<fixture.count;++i) {
                const auto next=(i/fixture.contour)*fixture.contour+(i+1U)%fixture.contour;
                const auto a=fixture.points[i],b=fixture.points[next];
                if(sample_y<std::min(a.y,b.y)||sample_y>=std::max(a.y,b.y))continue;
                const std::int64_t dx=std::int64_t(b.x)-a.x,dy=std::int64_t(b.y)-a.y;
                auto numerator=std::int64_t(a.x)*dy+(sample_y-a.y)*dx;
                hits.push_back({dy>0?numerator:-numerator,dy>0?dy:-dy,dy>0?1:-1});
            }
            std::sort(hits.begin(),hits.end(),[](auto a,auto b){return a.numerator*b.denominator<b.numerator*a.denominator;});
            int winding=0;
            for(std::size_t i=0;i+1U<hits.size();++i) {
                winding+=hits[i].direction;if(winding==0)continue;
                const auto a=hits[i],b=hits[i+1U];
                const auto first=std::clamp<std::int64_t>(ceil_div(a.numerator-2*a.denominator,4*a.denominator),0,512);
                const auto end=std::clamp<std::int64_t>(ceil_div(b.numerator-2*b.denominator,4*b.denominator),0,512);
                for(auto sample=first;sample<end;++sample)++coverage[y*64U+sample/8U];
            }
        }
        semantic_scene_builder builder(0x9483U,++generation);
        std::uint32_t red{};
        const progpu_native_scene_path_fill path{0U,segments.size(),0U,0U,min_x,min_y,max_x,max_y,
            {1,1,1,1},semantic_scene_builder::identity_transform(),PROGPU_NATIVE_FILL_RULE_NON_ZERO,8U};
        require(builder.add_solid_brush({1,0,0,1},1.F,red)&&
            builder.draw_paths({&path,1U},segments,{&red,1U},{0,0,64,64}),"exact line fixture construction failed");
        std::vector<std::byte> stream;require(builder.build(stream),"exact line fixture serialization failed");
        progpu_native_scene_header header{};std::memcpy(&header,stream.data(),sizeof(header));
        for(unsigned warm=0;warm<2;++warm) {
            progpu_native_scene_frame_metrics metrics{};const auto pixels=render(stream,header,metrics);
            require(pixels.size()==64U*64U*4U,"exact line readback size changed");
            for(std::size_t i=0;i<coverage.size();++i) {
                const std::array<std::uint8_t,4> expected{{static_cast<std::uint8_t>((coverage[i]*255U+32U)/64U),0U,0U,255U}};
                if(!std::equal(expected.begin(),expected.end(),pixels.begin()+i*4U)) {
                    std::fprintf(stderr,"Exact line generation=%llu reversed=%d warm=%u (%zu,%zu) red=%u expected=%u\n",
                        static_cast<unsigned long long>(generation),reversed,warm,i%64U,i/64U,pixels[i*4U],expected[0]);
                    require(false,"bounded line winding changed a rational reference pixel");
                }
            }
            if(warm)require(metrics.coverage_staging_bytes==0U&&metrics.vertex_upload_bytes==0U&&metrics.index_upload_bytes==0U,
                "warm exact line winding rebuilt retained geometry");
        }
    }
    std::fprintf(stderr,"Exact line winding: 14 original/reversed cases, cold/warm rational pixels passed\n");
}
} // namespace progpu::native::tests
