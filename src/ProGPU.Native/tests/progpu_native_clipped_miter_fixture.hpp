#pragma once

#include "progpu_native_miter_or_bevel_fixture.hpp"
#include "progpu_native_direct2d_copy_fixture.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>

namespace progpu::native::tests {

inline bool clipped_miter_triangles_and_flags()
{
    std::array<stroke_triangle, 8U> triangles{}, wpf{};
    for (float limit : {1.0F, 1.4F, 1.5F}) {
        const std::size_t count = create_join_triangles(triangles, 0U, 2, limit,
            {1,0}, {1,0}, {0,1}, false, true);
        if (count != (limit < 1.5F ? 3U : 2U) ||
            create_join_triangles(wpf, 0U, 2, limit, {1,0}, {1,0}, {0,1}, true) != count ||
            std::memcmp(triangles.data(), wpf.data(), count * sizeof(stroke_triangle)) != 0) return false;
        for (std::size_t index = 0U; index < count; ++index) {
            std::uint32_t exterior{}, internal{};
            classify_join_triangle_edges(0U, count, index, exterior, internal);
            if (exterior == 0U || (exterior & internal) != 0U) return false;
        }
    }
    if (create_join_triangles(triangles, 0U, 2, 1, {}, {1,0}, {-1,0}, false, true) != 0U ||
        create_join_triangles(triangles, 0U, 2, 1, {}, {1,0}, {-1,0}, true) != 3U ||
        create_join_triangles(triangles, 3U, 2, 1, {1,0}, {1,0}, {0,1}, false, true) != 1U) return false;

    constexpr progpu_native_affine_2d identity{1,0,0,1,0,0};
    progpu_native_scene_stroke stroke{};
    stroke.struct_size = sizeof(stroke);
    stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
    stroke.flags = PROGPU_NATIVE_POLYLINE_FLAG_CLOSED | PROGPU_NATIVE_POLYLINE_FLAG_CLIP_MITER_AT_LIMIT;
    stroke.point_count = 4U;
    stroke.stroke_thickness = 8;
    stroke.miter_limit = 1;
    stroke.color = {1,0,0,1};
    stroke.transform = identity;
    std::size_t points{}, doubles{};
    if (!semantic_stroke_resource_layout(&stroke, 1U, 4U * sizeof(progpu_native_point), points, doubles) ||
        points != 4U || doubles != 0U ||
        (make_semantic_polyline(stroke).flags & PROGPU_NATIVE_POLYLINE_FLAG_CLIP_MITER_AT_LIMIT) == 0U) return false;
    for (std::uint32_t join : {1U,2U,3U,4U}) {
        stroke.line_join = join;
        if (semantic_stroke_resource_layout(&stroke, 1U, 4U * sizeof(progpu_native_point), points, doubles) ||
            points != 0U || doubles != 0U) return false;
    }
    const auto segments = miter_or_bevel_rectangle(0U);
    constexpr std::array<std::uint8_t, 4U> smooth{};
    semantic_path_stroke::style style{};
    style.transform = identity;
    style.thickness = 8;
    style.miter_limit = 1;
    style.clip_miter_at_limit = true;
    mil::curve_dash::run_buffer scratch;
    std::vector<progpu_native_geometry_primitive> primitives;
    std::vector<std::uint32_t> brushes;
    if (semantic_path_stroke::compile(segments, smooth, true, {}, style, 7U,
            scratch, primitives, brushes) != semantic_path_stroke::result::success || primitives.size() != 8U) return false;
    std::size_t joins{};
    for (auto primitive : primitives) {
        if (!is_valid_geometry_primitive(primitive)) return false;
        const bool join = primitive.kind == PROGPU_NATIVE_GEOMETRY_PATH_JOIN;
        if (((primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_CLIP_MITER_AT_LIMIT) != 0U) != join) return false;
        if (join) {
            ++joins;
            primitive.flags |= 3U << PROGPU_NATIVE_PRIMITIVE_START_CAP_SHIFT;
        } else primitive.flags |= PROGPU_NATIVE_PRIMITIVE_FLAG_CLIP_MITER_AT_LIMIT;
        if (is_valid_geometry_primitive(primitive)) return false;
    }
    const auto before = primitives;
    const auto before_brushes = brushes;
    style.line_join = 3U;
    return joins == 4U && semantic_path_stroke::compile(segments, smooth, true, {}, style, 7U,
        scratch, primitives, brushes) == semantic_path_stroke::result::invalid && brushes == before_brushes &&
        primitives.size() == before.size() && std::memcmp(primitives.data(), before.data(),
            primitives.size() * sizeof(progpu_native_geometry_primitive)) == 0;
}

} // namespace progpu::native::tests

namespace progpu::native::direct2d::tests {

inline constexpr compat::rectangle_f clipped_miter_source_rect{10.25F,10.25F,50.25F,50.25F};

template<class Require>
void record_clipped_miter_case(compat::factory* factory, compat::render_target* target,
    unsigned mode, bool rectangle, bool independent, compat::stroke_style* injected, Require require)
{
    require(mode < 3U, "clipped miter mode inventory");
    const float radius = mode == 2U ? .5F : 4.0F;
    const auto source = clipped_miter_source_rect;
    constexpr compat::matrix_3x2_f identity{1,0,0,1,0,0};
    constexpr compat::color_f black{0,0,0,1}, red{1,0,0,1};
    target->SetDpi(96,96);
    target->SetTransform(&identity);
    target->SetAntialiasMode(compat::antialias_mode::aliased);
    target->BeginDraw(); target->Clear(&black);
    com::pointer<compat::solid_color_brush> brush;
    require(target->CreateSolidColorBrush(&red, nullptr, brush.put()) == com::ok, "clipped miter brush");
    com::pointer<compat::path_geometry> path;
    com::pointer<compat::geometry_sink> sink;
    const auto begin_path = [&] {
        require(factory->CreatePathGeometry(path.put()) == com::ok && path->Open(sink.put()) == com::ok,
            "clipped miter owned path");
    };
    if (independent) {
        begin_path(); sink->SetFillMode(compat::fill_mode::alternate);
        // Analytic intersection of the outer square and four miter-limit
        // planes. This fixture never invokes the product stroke/bounds solver.
        const float cut = static_cast<float>(static_cast<double>(radius) * (2.0 - std::sqrt(2.0)));
        const float l = source.left, r = source.right;
        const std::array<compat::point_2f,8U> outer{{
            {l-radius+cut,l-radius},{r+radius-cut,l-radius},{r+radius,l-radius+cut},{r+radius,r+radius-cut},
            {r+radius-cut,r+radius},{l-radius+cut,r+radius},{l-radius,r+radius-cut},{l-radius,l-radius+cut}}};
        sink->BeginFigure(outer[0], compat::figure_begin::filled);
        sink->AddLines(outer.data()+1U, 7U); sink->EndFigure(compat::figure_end::closed);
        const std::array<compat::point_2f,4U> inner{{{l+radius,l+radius},{r-radius,l+radius},
            {r-radius,r-radius},{l+radius,r-radius}}};
        sink->BeginFigure(inner[0], compat::figure_begin::filled);
        sink->AddLines(inner.data()+1U, 3U); sink->EndFigure(compat::figure_end::closed);
        require(sink->Close() == com::ok, "clipped miter independent close");
        target->FillGeometry(path.get(), brush.get(), nullptr);
    } else {
        com::pointer<compat::stroke_style1> owned_style;
        if (injected == nullptr) {
            const compat::stroke_style_properties properties{compat::cap_style::flat,compat::cap_style::flat,
                compat::cap_style::flat,compat::line_join::miter,1,compat::dash_style::solid,0};
            require(compat::create_stroke_style1(factory, &properties,
                static_cast<compat::stroke_transform_type>(mode), nullptr, 0U, owned_style.put()) == com::ok,
                "clipped miter typed style");
            injected = owned_style.get();
        }
        const float width = mode == 2U ? 123.0F : 8.0F;
        if (rectangle) target->DrawRectangle(&source, brush.get(), width, injected);
        else {
            begin_path();
            sink->BeginFigure({source.left,source.top}, compat::figure_begin::hollow);
            const std::array<compat::point_2f,3U> points{{{source.right,source.top},
                {source.right,source.bottom},{source.left,source.bottom}}};
            sink->AddLines(points.data(), 3U); sink->EndFigure(compat::figure_end::closed);
            require(sink->Close() == com::ok, "clipped miter source close");
            target->DrawGeometry(path.get(), brush.get(), width, injected);
        }
    }
    require(target->EndDraw(nullptr,nullptr) == com::ok, "clipped miter source recording");
}

template<class Require>
void clipped_miter_pixels(std::span<const std::uint8_t> pixels, bool bgra, unsigned mode, Require require)
{
    require(pixels.size() == 64U*64U*4U && mode < 3U, "clipped miter full frame");
    const double radius = mode == 2U ? .5 : 4;
    const double l = 10.25, r = 50.25, diagonal = radius * std::sqrt(2.0);
    std::size_t ink{};
    for (unsigned y = 0U; y < 64U; ++y) for (unsigned x = 0U; x < 64U; ++x) {
        const double px = static_cast<double>(x)+.5, py = static_cast<double>(y)+.5;
        const bool red = px >= l-radius && px < r+radius && py >= l-radius && py < r+radius &&
            !(px >= l+radius && px < r-radius && py >= l+radius && py < r-radius) &&
            l-px+l-py <= diagonal && px-r+l-py <= diagonal &&
            px-r+py-r <= diagonal && l-px+py-r <= diagonal;
        std::array<std::uint8_t,4U> expected{0U,0U,0U,255U};
        if (red) { expected[bgra ? 2U : 0U] = 255U; ++ink; }
        const auto* actual = pixels.data()+(y*64U+x)*4U;
        if (!std::equal(expected.begin(),expected.end(),actual))
            std::fprintf(stderr,"Clipped miter mode=%u pixel=%u,%u actual=%u,%u,%u,%u expected=%u,%u,%u,%u\n",
                mode,x,y,static_cast<unsigned>(actual[0]),static_cast<unsigned>(actual[1]),
                static_cast<unsigned>(actual[2]),static_cast<unsigned>(actual[3]),
                static_cast<unsigned>(expected[0]),static_cast<unsigned>(expected[1]),
                static_cast<unsigned>(expected[2]),static_cast<unsigned>(expected[3]));
        require(std::equal(expected.begin(),expected.end(),actual), "clipped miter full-byte independent coverage");
    }
    require(ink != 0U,"clipped miter cannot pass with empty rendering");
}

template<class Render, class Require>
void verify_clipped_miter_pixels(Render render, Require require)
{
    com::pointer<compat::factory> factory;
    com::pointer<compat::scene_factory_native> scene_factory;
    require(compat::create_factory(factory.put()) == com::ok &&
        factory.as(compat::scene_factory_native_interface_id,scene_factory) == com::ok,"clipped miter factory");
    std::uint64_t generation = 1U;
    for (unsigned mode = 0U; mode < 3U; ++mode) for (bool rectangle : {false,true}) {
        std::array<std::vector<std::byte>,2U> scenes;
        std::array<progpu_native_scene_header,2U> headers{};
        for (unsigned reference = 0U; reference < 2U; ++reference) {
            const compat::scene_render_target_properties properties{64U,64U,96,96,0xC11FU,generation++};
            com::pointer<compat::render_target> target;
            com::pointer<compat::scene_render_target_native> scene;
            require(scene_factory->CreateSceneRenderTarget(&properties,target.put()) == com::ok &&
                target.as(compat::scene_render_target_native_interface_id,scene) == com::ok,"clipped miter target");
            record_clipped_miter_case(factory.get(),target.get(),mode,rectangle,reference != 0U,nullptr,require);
            require(export_copy_scene(scene.get(),scenes[reference]) &&
                read_scene_value(scenes[reference],0U,headers[reference]) && headers[reference].command_count == 1U,
                "clipped miter one immutable source draw");
        }
        const auto cold = render(false,scenes[0],headers[0]);
        const auto warm = render(false,scenes[0],headers[0]);
        const auto independent = render(true,scenes[1],headers[1]);
        require(cold == warm && cold == independent,"clipped miter cold/warm/independent pixels");
        clipped_miter_pixels(cold,false,mode,require);
    }
}

} // namespace progpu::native::direct2d::tests
