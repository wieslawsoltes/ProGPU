#pragma once

#include "../src/Direct2D/progpu_native_direct2d_prepared_glyphs.hpp"
#include "progpu_native_scene_builder.hpp"
#include "progpu_native_direct2d_copy_fixture.hpp"
#include "progpu_native_direct2d_font_source_fixture.hpp"
#include "progpu_native_hint_fault_fixture.hpp"
#include "progpu_native_direct2d_source_coverage_reference.hpp"
#include "progpu_native_direct2d_original_glyph_reference.hpp"

namespace progpu::native::direct2d::tests {

enum class prepared_pixel_path { original, prepared, independent_geometry, prepared_geometry, original_design_advances, prepared_outline };

// A separate geometry-policy control: use the actual prepared source contours
// with ordinary FillGeometry, while the literal independent geometry stays in
// each fixture. Neither geometry image supplies the glyph coverage reference.
template<class Require>
void draw_prepared_outline_geometry(compat::factory* factory, compat::render_target* target,
    const std::shared_ptr<prepared_original_font>& font, compat::rendering_parameters* parameters,
    compat::point_2f baseline, const compat::glyph_run& run, compat::brush* brush, Require require)
{
    original_glyph_target frame;
    frame.identity = com::pointer<com::unknown>(target);
    frame.generation = 1U; // Test-owned snapshot; never a recorder generation.
    target->GetTransform(&frame.transform);
    target->GetDpi(&frame.dpi_x, &frame.dpi_y);
    frame.baseline = baseline; frame.pixels = target->GetPixelSize();
    frame.format = target->GetPixelFormat(); frame.antialias = target->GetTextAntialiasMode();
    std::shared_ptr<const original_glyph_request> request;
    std::shared_ptr<const prepared_original_glyph_run> glyphs;
    require(capture_original_glyph_request(font->source(), run, compat::measuring_mode::natural,
        parameters, frame, request) == com::ok && font->prepare(request, glyphs) == com::ok,
        "geometry control retains the original complete glyph request");
    com::pointer<compat::path_geometry> geometry;
    com::pointer<compat::geometry_sink> sink;
    require(factory->CreatePathGeometry(geometry.put()) == com::ok && geometry->Open(sink.put()) == com::ok,
        "prepared geometry control sink");
    sink->SetFillMode(compat::fill_mode::winding);
    bool open = false;
    progpu_native_point start{}, previous{};
    for (const auto& segment : glyphs->segments()) {
        if (!open) {
            start = segment.p0;
            sink->BeginFigure({start.x, start.y}, compat::figure_begin::filled);
            open = true;
        } else require(segment.p0.x == previous.x && segment.p0.y == previous.y,
            "prepared geometry control preserves contour continuity");
        if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
            const compat::bezier_segment curve{{segment.p1.x, segment.p1.y},
                {segment.p2.x, segment.p2.y}, {segment.p3.x, segment.p3.y}};
            sink->AddBezier(&curve); previous = segment.p3;
        } else {
            require(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE, "prepared geometry line/cubic inventory");
            sink->AddLine({segment.p1.x, segment.p1.y}); previous = segment.p1;
        }
        if (previous.x == start.x && previous.y == start.y) {
            sink->EndFigure(compat::figure_end::closed); open = false;
        }
    }
    require(!open && sink->Close() == com::ok, "prepared geometry control closes every original contour");
    target->FillGeometry(geometry.get(), brush, nullptr);
}

template<class Render, class Require>
std::vector<std::uint8_t> verify_original_glyph_frame(Render render, Require require,
    std::uint32_t original_frame, const std::array<std::vector<std::byte>, 3U>& scenes,
    const std::array<progpu_native_scene_header, 3U>& headers, std::uint32_t draws = 1U,
    std::uint32_t warm_original_frame = 0U)
{
    std::array<std::vector<std::uint8_t>, 3U> images;
    for (std::size_t path = 0U; path < scenes.size(); ++path) {
        progpu_native_scene_frame_metrics cold_metrics{}; cold_metrics.struct_size = sizeof(cold_metrics);
        progpu_native_scene_frame_metrics warm_metrics{}; warm_metrics.struct_size = sizeof(warm_metrics);
        images[path] = render(path == 1U, scenes[path], headers[path], draws, cold_metrics);
        const auto warm = render(path == 1U, scenes[path], headers[path], draws, warm_metrics);
        require(images[path].size() == 64U * 64U * 4U && images[path] == warm,
            "glyph and both geometry controls preserve every cold/warm pixel");
        if (path == 0U && warm_original_frame != 0U)
            require(warm == original_glyph_coverage_pixels(warm_original_frame),
                "warm glyph matches its separately captured original Microsoft frame");
        require(warm_metrics.vertex_upload_bytes == 0U && warm_metrics.index_upload_bytes == 0U &&
            warm_metrics.coverage_staging_bytes == 0U, "glyph and geometry warm replay have zero retained uploads");
    }
    require(images[0] == original_glyph_coverage_pixels(original_frame),
        "prepared glyph matches every original Microsoft DrawGlyphRun pixel");
    require(images[1] == images[2], "independent and prepared geometry match every pixel under one fill policy");
    return std::move(images[0]);
}

// These coordinates follow the authored font's two [13,13]..[313,413]
// rectangles at em 62.5 / UPM 1000, not a product decoder/placement query.
// The empty middle glyph advances by -3; offsets do not change the next pen.
inline constexpr std::array<compat::rectangle_f, 2U> prepared_reference_rectangles{{
    {4, 5, 22.75F, 30}, {24.25F, 2.5F, 43, 27.5F}}};

inline std::vector<std::byte> prepared_pixel_font(std::uint32_t origins)
{
    return origins == 0U ? progpu::native::tests::make_hint_fault_font()
        : origins == 1U ? progpu::native::tests::make_hint_fault_font(29, -19)
        : progpu::native::tests::make_hint_fault_font(-19, 29, true);
}

inline compat::point_2f prepared_pixel_baseline(bool nominal, bool right_to_left = false)
{
    if (right_to_left) return nominal ? compat::point_2f{60.59375F, 17.90625F}
        : compat::point_2f{60.1875F, 30.8125F};
    return nominal ? compat::point_2f{3.59375F, 17.90625F} : compat::point_2f{3.1875F, 30.8125F};
}

inline std::array<compat::rectangle_f, 2U> prepared_pixel_rectangles(std::uint32_t origins, bool nominal = false,
    bool right_to_left = false)
{
    if (right_to_left) {
        // Separate literal RTL oracle. Each left-oriented outline starts one
        // original design width left of its pen (31.25 or 15.625). Caller
        // advances 24,-3,24 move successive pens independently; the third
        // advanceOffset=-.75 moves right without changing the following pen.
        // Neither font decoding nor prepared placement supplies these values.
        if (nominal) {
            if (origins == 1U) return {{{45.875F, 5, 55.25F, 17.5F}, {13.875F, 2.5F, 23.25F, 15}}};
            if (origins == 2U) return {{{44.375F, 5, 53.75F, 17.5F}, {15.375F, 2.5F, 24.75F, 15}}};
            return {{{45.375F, 5, 54.75F, 17.5F}, {14.875F, 2.5F, 24.25F, 15}}};
        }
        if (origins == 1U) return {{{30.75F, 5, 49.5F, 30}, {7.5F, 2.5F, 26.25F, 27.5F}}};
        if (origins == 2U) return {{{27.75F, 5, 46.5F, 30}, {10.5F, 2.5F, 29.25F, 27.5F}}};
        return {{{29.75F, 5, 48.5F, 30}, {9.5F, 2.5F, 28.25F, 27.5F}}};
    }
    if (nominal) {
        // Original authored hmtx widths are 500 at em 31.25 / UPM 1000.
        // The no-ink middle glyph consumes 15.625, independently of offsets.
        if (origins == 1U) return {{{4.5F, 5, 13.875F, 17.5F}, {33.5F, 2.5F, 42.875F, 15}}};
        if (origins == 2U) return {{{3, 5, 12.375F, 17.5F}, {35, 2.5F, 44.375F, 15}}};
        return {{{4, 5, 13.375F, 17.5F}, {34.5F, 2.5F, 43.875F, 15}}};
    }
    // Independent exact design-unit bearing deltas at 1/16 scale. No product
    // bounds, metrics, decoded points or phantom values enter this oracle.
    if (origins == 1U) return {{{5, 5, 23.75F, 30}, {22.25F, 2.5F, 41, 27.5F}}};
    if (origins == 2U) return {{{2, 5, 20.75F, 30}, {25.25F, 2.5F, 44, 27.5F}}};
    return prepared_reference_rectangles;
}

inline compat::matrix_3x2_f prepared_pixel_transform(std::uint32_t variant)
{
    return variant >= 2U ? compat::matrix_3x2_f{1, 0.25F, -0.125F, 1, 7, 9}
        : compat::matrix_3x2_f{1, 0, 0, 1, (variant & 1U) != 0U ? 0.25F : 0.0F,
            (variant & 1U) != 0U ? 0.5F : 0.0F};
}

template<class Require>
void record_prepared_pixel_case(compat::factory* factory, compat::render_target* target,
    const std::shared_ptr<prepared_original_font>& font, compat::rendering_parameters* parameters,
    std::uint32_t variant, prepared_pixel_path path, Require require, compat::geometry* prepared_geometry = nullptr,
    std::uint32_t origins = 0U, bool nominal = false, const float* original_design_advances = nullptr,
    bool right_to_left = false)
{
    require(variant < 4U, "prepared glyph pixel inventory");
    const bool grayscale = (variant & 1U) != 0U;
    const auto transform = prepared_pixel_transform(variant);
    const compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
    const compat::rectangle_f clip{9, 8, 46, 38};
    const compat::layer_parameters layer{{0, 0, 64, 64}, nullptr, compat::antialias_mode::aliased,
        identity, 0.5F, nullptr, compat::layer_options::none};
    constexpr compat::color_f black{0, 0, 0, 1}, red{1, 0, 0, 1};
    com::pointer<compat::solid_color_brush> brush;
    require(target->CreateSolidColorBrush(&red, nullptr, brush.put()) == com::ok, "prepared pixel brush");
    const auto antialias = grayscale ? compat::antialias_mode::per_primitive : compat::antialias_mode::aliased;
    target->BeginDraw(); target->Clear(&black); target->SetTransform(&identity);
    target->SetAntialiasMode(antialias);
    target->SetTextAntialiasMode(grayscale ? compat::text_antialias_mode::grayscale : compat::text_antialias_mode::aliased);
    target->SetTextRenderingParams(parameters);
    if (variant >= 2U) target->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased);
    if (variant == 3U) target->PushLayer(&layer, nullptr);
    target->SetTransform(&transform);
    if (path == prepared_pixel_path::prepared_geometry) {
        require(prepared_geometry != nullptr, "original rasterizer prepared outline geometry");
        target->FillGeometry(prepared_geometry, brush.get(), nullptr);
    } else if (path == prepared_pixel_path::independent_geometry) {
        com::pointer<compat::path_geometry> geometry;
        com::pointer<compat::geometry_sink> sink;
        require(factory->CreatePathGeometry(geometry.put()) == com::ok && geometry->Open(sink.put()) == com::ok,
            "independent prepared glyph geometry");
        sink->SetFillMode(compat::fill_mode::winding);
        for (const auto& rectangle : prepared_pixel_rectangles(origins, nominal, right_to_left)) {
            sink->BeginFigure({rectangle.left, rectangle.bottom}, compat::figure_begin::filled);
            sink->AddLine({rectangle.right, rectangle.bottom});
            sink->AddLine({rectangle.right, rectangle.top});
            sink->AddLine({rectangle.left, rectangle.top});
            sink->EndFigure(compat::figure_end::closed);
        }
        require(sink->Close() == com::ok, "independent prepared glyph sink close");
        target->FillGeometry(geometry.get(), brush.get(), nullptr);
    } else {
        const std::uint16_t indices[]{1U, 0U, 2U};
        const float advances[]{24, -3, right_to_left ? 24.0F : 9.0F};
        const compat::glyph_offset offsets[]{{0, 0}, {0, 0}, {-0.75F, 2.5F}};
        const bool design_reference = path == prepared_pixel_path::original_design_advances;
        require(!design_reference || (nominal && original_design_advances != nullptr),
            "original nominal comparison requires actual design-metric query");
        const auto* selected_advances = design_reference ? original_design_advances : nominal ? nullptr : advances;
        const compat::glyph_run run{font->source()->face.get(), nominal ? 31.25F : 62.5F,
            3U, indices, selected_advances, offsets, 0, right_to_left ? 3U : 2U};
        const auto baseline = prepared_pixel_baseline(nominal, right_to_left);
        if (path == prepared_pixel_path::prepared_outline) {
            draw_prepared_outline_geometry(factory, target, font, parameters, baseline, run, brush.get(), require);
        } else if (path == prepared_pixel_path::original || design_reference) {
            target->DrawGlyphRun(baseline, &run, brush.get(), compat::measuring_mode::natural);
        } else {
            com::pointer<prepared_glyph_target> prepared;
            require(target->QueryInterface(prepared_glyph_target_id, reinterpret_cast<void**>(prepared.put())) == com::ok &&
                prepared->DrawOwnedGlyphRun(font, baseline, &run, brush.get(), compat::measuring_mode::natural) == com::ok,
                "actual prepared glyph retained draw");
        }
    }
    if (variant == 3U) target->PopLayer();
    if (variant >= 2U) target->PopAxisAlignedClip();
    require(target->EndDraw(nullptr, nullptr) == com::ok, "prepared glyph pixel EndDraw");
    target->SetTextRenderingParams(nullptr);
}

template<class Render, class Require>
void verify_prepared_glyph_pixels(Render render, Require require)
{
    std::uint32_t original_frame = 1U;
    for (std::uint32_t origins = 0U; origins < 3U; ++origins) {
    font_stream stream; stream.bytes = prepared_pixel_font(origins); stream.declared_size = stream.bytes.size();
    font_loader loader; loader.stream = &stream;
    font_file file; file.loader = &loader;
    font_face face; face.files[0] = &file; face.declared_count = 1U;
    face.type = 1U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
    std::shared_ptr<const original_font_capture> source;
    std::shared_ptr<prepared_original_font> font;
    require(capture_original_font(&face, source) == com::ok && prepared_original_font::create(source, font) == com::ok,
        "prepared glyph original-byte owner");
    face.count_result = compat::not_implemented;
    const auto reads = stream.reads;
    rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
    com::pointer<compat::factory> factory;
    com::pointer<compat::scene_factory_native> scene_factory;
    require(compat::create_factory(factory.put()) == com::ok &&
        factory.as(compat::scene_factory_native_interface_id, scene_factory) == com::ok, "prepared pixel factory");
    for (const bool right_to_left : {false, true}) {
    for (const bool nominal : {false, true}) {
    for (std::uint32_t variant = 0U; variant < 4U; ++variant) {
        std::array<std::vector<std::byte>, 3U> scenes;
        std::array<progpu_native_scene_header, 3U> headers{};
        for (std::uint32_t reference = 0U; reference < 3U; ++reference) {
            const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95D1U,
                1U + origins * 48U + (right_to_left ? 24U : 0U) + (nominal ? 12U : 0U) + variant * 3U + reference};
            com::pointer<compat::render_target> target;
            com::pointer<compat::scene_render_target_native> scene;
            require(scene_factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                target.as(compat::scene_render_target_native_interface_id, scene) == com::ok, "prepared pixel target");
            record_prepared_pixel_case(factory.get(), target.get(), font, &parameters, variant,
                reference == 0U ? prepared_pixel_path::prepared : reference == 1U
                    ? prepared_pixel_path::independent_geometry : prepared_pixel_path::prepared_outline,
                require, nullptr, origins, nominal, nullptr, right_to_left);
            require(export_copy_scene(scene.get(), scenes[reference]) && read_scene_value(scenes[reference], 0U, headers[reference]),
                "prepared glyph immutable scene export");
        } // Source targets and brushes end before native replay.
        const auto cold = verify_original_glyph_frame(render, require, original_frame, scenes, headers);
        original_frame += 1U;
        if (variant == 0U && origins == 0U && !nominal && !right_to_left) {
            constexpr std::array<std::uint8_t, 4U> black{0, 0, 0, 255}, red{255, 0, 0, 255};
            require(std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + 10U) * 4U) &&
                std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + 30U) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data() + (10U * 64U + 23U) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data()), "prepared absolute ink/no-ink-advance/background pixels");
        }
        if (variant == 0U && origins == 0U && nominal && !right_to_left) {
            constexpr std::array<std::uint8_t, 4U> black{0, 0, 0, 255}, red{255, 0, 0, 255};
            require(std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + 10U) * 4U) &&
                std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + 40U) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data() + (10U * 64U + 25U) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data()), "nominal absolute ink/no-ink-advance/background pixels");
        }
        if (variant == 0U && origins == 0U && right_to_left) {
            constexpr std::array<std::uint8_t, 4U> black{0, 0, 0, 255}, red{255, 0, 0, 255};
            const std::uint32_t first = nominal ? 50U : 40U, gap = nominal ? 35U : 29U;
            require(std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + first) * 4U) &&
                std::equal(red.begin(), red.end(), cold.data() + (10U * 64U + 20U) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data() + (10U * 64U + gap) * 4U) &&
                std::equal(black.begin(), black.end(), cold.data()), "RTL absolute logical-glyph/no-ink-advance/background pixels");
        }
    }
    }
    }
    require(stream.reads == reads && face.outline_calls == 0U && face.table_calls == 0U && font->cached_glyph_count() == 3U,
        "prepared repeated draw must reuse original context without source callbacks");
    }
    require(original_frame == 49U, "complete original prepared_glyph frame inventory");
}
// Explicit preparation seam only: ordinary recorder selection remains gated.
// Original Windows source receipts independently fix these fractional edge
// values. They catch confusing mesh color blend numbering with layer blending.
template<class Render, class Require>
void verify_prepared_glyph_coverage_pixels(Render render, Require require)
{
    std::vector<std::byte> bytes;
    progpu_native_scene_header header{};
    {
        font_stream stream; stream.bytes = prepared_pixel_font(0U); stream.declared_size = stream.bytes.size();
        font_loader loader; loader.stream = &stream;
        font_file file; file.loader = &loader;
        font_face face; face.files[0] = &file; face.declared_count = 1U;
        face.type = 1U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
        std::shared_ptr<const original_font_capture> source;
        std::shared_ptr<prepared_original_font> font;
        com::pointer<compat::factory> factory;
        require(compat::create_factory(factory.put()) == com::ok && capture_original_font(&face, source) == com::ok &&
            prepared_original_font::create(source, font) == com::ok, "coverage mesh original font ownership");
        rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
        original_glyph_target frame{com::pointer<com::unknown>(factory.get()), 1U,
            prepared_pixel_transform(1U), prepared_pixel_baseline(false), {64U, 64U}, 96, 96, {}, compat::text_antialias_mode::grayscale};
        const std::uint16_t indices[]{1U, 0U, 2U};
        const float advances[]{24, -3, 24};
        const compat::glyph_offset offsets[]{{0, 0}, {0, 0}, {-0.75F, 2.5F}};
        const compat::glyph_run run{&face, 62.5F, 3U, indices, advances, offsets, 0, 2U};
        std::shared_ptr<const original_glyph_request> request;
        std::shared_ptr<const prepared_original_glyph_run> prepared;
        prepared_original_glyph_coverage coverage;
        require(capture_original_glyph_request(source, run, compat::measuring_mode::natural,
            &parameters, frame, request) == com::ok && font->prepare(request, prepared) == com::ok &&
            prepared->prepare_coverage(factory.get(), coverage) == com::ok, "complete prepared coverage mesh");
        semantic_scene_builder builder(0x95D228U, 1U);
        std::uint32_t brush = 0U;
        require(builder.add_solid_brush({1, 0, 0, 1}, 1, brush), "coverage source brush");
        const std::vector<std::uint32_t> brushes(coverage.meshes.size(), brush);
        require(builder.draw_source_coverage(coverage.meshes, coverage.vertices, brushes, coverage.bounds, coverage.frame) &&
            builder.build(bytes) && read_scene_value(bytes, 0U, header), "retained coverage mesh serialization");
    } // Font, source request, prepared output and builder end before replay.
    const auto cold = render(bytes, header), warm = render(bytes, header);
    require(cold.size() == 64U*64U*4U && cold == warm, "coverage mesh cold/warm exact pixels");
    const auto pixel = [&](unsigned x, unsigned y, std::uint8_t red) {
        const auto offset = (y*64U+x)*4U;
        return cold[offset] == red && cold[offset+1U] == 0U && cold[offset+2U] == 0U && cold[offset+3U] == 255U;
    };
    require(pixel(0, 0, 0) && pixel(10, 10, 255) && pixel(5, 5, 149) &&
        pixel(24, 3, 149) && pixel(43, 3, 75), "original source interior/background/fractional coverage bytes");
    // A sheared edge's coverage quad is not generally planar in (x,y,alpha).
    // Both original contour orientations must retain the same diagonal and
    // match the complete independently captured Microsoft frame.
    const auto original_shear = original_sheared_glyph_coverage_pixels();
    const auto original_layer = original_sheared_glyph_coverage_pixels(true);
    require(original_shear.size() == 64U*64U*4U && original_layer.size() == original_shear.size(),
        "original sheared source receipt extent");
    for (const bool half_opacity : {false, true}) for (const bool reverse_contours : {false, true}) {
        std::vector<std::byte> retained;
        progpu_native_scene_header retained_header{};
        {
            font_stream stream; stream.bytes = progpu::native::tests::make_hint_fault_font(13, 13, false, reverse_contours);
            stream.declared_size = stream.bytes.size();
            font_loader loader; loader.stream = &stream;
            font_file file; file.loader = &loader;
            font_face face; face.files[0] = &file; face.declared_count = 1U;
            face.type = 1U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
            std::shared_ptr<const original_font_capture> source;
            std::shared_ptr<prepared_original_font> font;
            com::pointer<compat::factory> factory;
            require(compat::create_factory(factory.put()) == com::ok && capture_original_font(&face, source) == com::ok &&
                prepared_original_font::create(source, font) == com::ok, "sheared winding source ownership");
            rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
            original_glyph_target frame{com::pointer<com::unknown>(factory.get()), 1U,
                prepared_pixel_transform(3U), prepared_pixel_baseline(true), {64U, 64U}, 96, 96, {}, compat::text_antialias_mode::grayscale};
            const std::uint16_t indices[]{1U, 0U, 2U};
            const compat::glyph_offset offsets[]{{0, 0}, {0, 0}, {-0.75F, 2.5F}};
            const compat::glyph_run run{&face, 31.25F, 3U, indices, nullptr, offsets, 0, 2U};
            std::shared_ptr<const original_glyph_request> request;
            std::shared_ptr<const prepared_original_glyph_run> prepared;
            prepared_original_glyph_coverage coverage;
            require(capture_original_glyph_request(source, run, compat::measuring_mode::natural,
                &parameters, frame, request) == com::ok && font->prepare(request, prepared) == com::ok &&
                prepared->prepare_coverage(factory.get(), coverage) == com::ok, "sheared winding coverage preparation");
            semantic_scene_builder builder(0x95D233U, 1U + (reverse_contours ? 1U : 0U) + (half_opacity ? 2U : 0U));
            std::uint32_t brush{}, state_index{};
            auto state = builder.identity_state(); state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
            state.clip_rect = {9, 8, 37, 30};
            require(builder.add_state(state, state_index) && builder.add_solid_brush({1, 0, 0, 1}, 1, brush),
                "sheared original source clip and brush");
            const std::vector<std::uint32_t> brushes(coverage.meshes.size(), brush);
            if (half_opacity) {
                const progpu_native_scene_layer layer{sizeof(layer),
                    PROGPU_NATIVE_SCENE_LAYER_BOUNDS | PROGPU_NATIVE_SCENE_LAYER_LINEAR_BYTE_OPACITY,
                    {0, 0, 64, 64}, .5F, PROGPU_NATIVE_BLEND_SRC_OVER,
                    PROGPU_NATIVE_SCENE_NO_INDEX, PROGPU_NATIVE_SCENE_NO_INDEX, 0U, 0U, 0U, 0U};
                require(builder.push_layer(layer), "original complete-frame source opacity layer");
            }
            require(builder.draw_source_coverage(coverage.meshes, coverage.vertices, brushes,
                coverage.bounds, coverage.frame, state_index) && (!half_opacity || builder.pop_layer()) && builder.build(retained) &&
                read_scene_value(retained, 0U, retained_header), "sheared immutable source scene");
        }
        const auto first = render(retained, retained_header), repeated = render(retained, retained_header);
        const auto& original = half_opacity ? original_layer : original_shear;
        require(first == original && repeated == original,
            "both windings, opacity layers and cold/warm frames retain every original sheared source pixel");
    }
    // Whole-path source controls retain original overlapping/physical bounds;
    // they must not become separately composited glyph coverage triangles.
    for (const std::uint32_t original_frame : {4U, 12U, 19U, 20U, 43U, 44U}) {
        std::vector<std::byte> retained;
        progpu_native_scene_header retained_header{};
        {
            const std::uint32_t origins = (original_frame - 1U) / 16U;
            const bool rtl = ((original_frame - 1U) % 16U) >= 8U;
            const std::uint32_t variant = (original_frame - 1U) % 4U;
            font_stream stream; stream.bytes = prepared_pixel_font(origins); stream.declared_size = stream.bytes.size();
            font_loader loader; loader.stream = &stream;
            font_file file; file.loader = &loader;
            font_face face; face.files[0] = &file; face.declared_count = 1U;
            face.type = 1U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
            std::shared_ptr<const original_font_capture> source;
            std::shared_ptr<prepared_original_font> font;
            rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
            com::pointer<compat::factory> factory;
            com::pointer<compat::scene_factory_native> scene_factory;
            com::pointer<compat::render_target> target;
            com::pointer<compat::scene_render_target_native> scene;
            const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95D234U, original_frame};
            require(capture_original_font(&face, source) == com::ok && prepared_original_font::create(source, font) == com::ok &&
                compat::create_factory(factory.put()) == com::ok &&
                factory.as(compat::scene_factory_native_interface_id, scene_factory) == com::ok &&
                scene_factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                target.as(compat::scene_render_target_native_interface_id, scene) == com::ok,
                "original physical whole-path source owners");
            record_prepared_pixel_case(factory.get(), target.get(), font, &parameters, variant,
                prepared_pixel_path::prepared, require, nullptr, origins, false, nullptr, rtl);
            require(export_copy_scene(scene.get(), retained) && read_scene_value(retained, 0U, retained_header),
                "original physical whole-path scene snapshot");
        }
        const auto expected = original_physical_glyph_coverage_pixels(original_frame);
        const auto physical_cold = render(retained, retained_header), physical_warm = render(retained, retained_header);
        require(expected.size() == 64U*64U*4U && physical_cold == expected && physical_warm == expected,
            "whole-path physical placement must retain every original Microsoft pixel, cold and warm");
    }
    // Original independently captured variable-font triangle. Ordinary Metal
    // interpolation produces red127 here; the source's snapped plane gives128.
    // Keep its original coordinates and binary endpoint coverage unchanged.
    semantic_scene_builder threshold(0x95D230U, 1U);
    std::uint32_t red{};
    require(threshold.add_solid_brush({1, 0, 0, 1}, 1, red), "threshold source brush");
    const std::array<progpu_native_point, 3U> points{{
        {12.822303771972656F, 25.219362258911133F},
        {11.96875F, 25.57291603088379F}, {12.46875F, 26.07291603088379F}}};
    std::array<progpu_native_scene_mesh_vertex, 3U> triangle{};
    for (std::size_t i = 0U; i < 3U; ++i) triangle[i] = {points[i], points[i], {1, 1, 1, i == 0U ? 1.F : 0.F}};
    progpu_native_scene_vertex_mesh mesh{};
    mesh.struct_size = sizeof(mesh); mesh.flags = PROGPU_NATIVE_VERTEX_MESH_EDGE_ALIASED;
    mesh.vertex_count = 3U; mesh.color_blend_mode = 5U; mesh.transform = threshold.identity_transform();
    const progpu_native_scene_source_coverage_frame frame{sizeof(frame), 1U, 1, 1, 64U, 64U, 0U, 0U};
    require(threshold.draw_source_coverage({&mesh, 1U}, triangle, {&red, 1U}, {11, 25, 2, 2}, frame) &&
        threshold.build(bytes) && read_scene_value(bytes, 0U, header), "threshold original triangle serialization");
    const auto exact = render(bytes, header), repeated = render(bytes, header);
    const auto offset = (25U * 64U + 12U) * 4U;
    require(exact.size() == 64U*64U*4U && exact == repeated && exact[offset] == 128U &&
        exact[offset+1U] == 0U && exact[offset+2U] == 0U && exact[offset+3U] == 255U,
        "original source threshold coverage must remain128 in both cold and warm frames");
}
// A separate literal world-space geometry/brush control proves that capture-time
// path projection preserves spatial paint, opacity, clips and independent DPI.
template<class Render, class Require>
void verify_filled_path_projection_pixels(Render render, Require require)
{
    for (const bool anisotropic : {false, true}) for (const bool layer : {false, true}) {
        const float dpi_x = anisotropic ? 2.F : 1.F, dpi_y = anisotropic ? 1.25F : 1.F;
        const progpu_native_scene_presentation presentation{sizeof(presentation), 0U, 0U,
            64U, 64U, dpi_x, dpi_y, 0U};
        std::array<std::vector<std::byte>, 2U> scenes;
        std::array<progpu_native_scene_header, 2U> headers{};
        for (std::uint32_t reference = 0U; reference < 2U; ++reference) {
            com::pointer<compat::factory> factory;
            com::pointer<compat::scene_factory_native> scene_factory;
            com::pointer<compat::render_target> target;
            com::pointer<compat::scene_render_target_native> scene;
            const compat::scene_render_target_properties properties{64U, 64U, 96*dpi_x, 96*dpi_y,
                0x95D2341U, 1U + reference + (layer ? 2U : 0U) + (anisotropic ? 4U : 0U)};
            require(compat::create_factory(factory.put()) == com::ok &&
                factory.as(compat::scene_factory_native_interface_id, scene_factory) == com::ok &&
                scene_factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                target.as(compat::scene_render_target_native_interface_id, scene) == com::ok,
                "filled-path paint source target");
            com::pointer<compat::gradient_stop_collection> stops;
            const compat::gradient_stop values[]{{0, {1, 0, 0, 1}}, {1, {0, 0, 1, 1}}};
            require(target->CreateGradientStopCollection(values, 2U, compat::gamma::gamma_2_2,
                compat::extend_mode::clamp, stops.put()) == com::ok, "filled-path gradient stops");
            const compat::linear_gradient_brush_properties gradient{{0, 0}, {8, 8}};
            // The literal world brush is the source brush (.5,0,0,2,1,3)
            // followed by the draw mapping (2,1,0,1,4,8), in that order.
            const compat::brush_properties brush_properties{.75F, reference != 0U
                ? compat::matrix_3x2_f{1, .5F, 0, 2, 6, 12}
                : compat::matrix_3x2_f{.5F, 0, 0, 2, 1, 3}};
            com::pointer<compat::linear_gradient_brush> brush;
            require(target->CreateLinearGradientBrush(&gradient, &brush_properties, stops.get(), brush.put()) == com::ok,
                "filled-path independent spatial brush");
            com::pointer<compat::path_geometry> geometry;
            com::pointer<compat::geometry_sink> sink;
            require(factory->CreatePathGeometry(geometry.put()) == com::ok && geometry->Open(sink.put()) == com::ok,
                "filled-path original geometry");
            const std::array<compat::point_2f, 4U> points = reference != 0U
                ? std::array<compat::point_2f, 4U>{{{8, 12}, {24, 20}, {24, 28}, {8, 20}}}
                : std::array<compat::point_2f, 4U>{{{2, 2}, {10, 2}, {10, 10}, {2, 10}}};
            sink->SetFillMode(compat::fill_mode::winding);
            sink->BeginFigure(points[0], compat::figure_begin::filled);
            for (std::size_t i = 1U; i < points.size(); ++i) sink->AddLine(points[i]);
            sink->EndFigure(compat::figure_end::closed);
            require(sink->Close() == com::ok, "filled-path closed original geometry");
            constexpr compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0};
            const compat::matrix_3x2_f transform = reference != 0U ? identity : compat::matrix_3x2_f{2, 1, 0, 1, 4, 8};
            const compat::rectangle_f clip{9, 10, 23, 27};
            const compat::layer_parameters opacity{{0, 0, 64/dpi_x, 64/dpi_y}, nullptr,
                compat::antialias_mode::aliased, identity, .5F, nullptr, compat::layer_options::none};
            constexpr compat::color_f black{0, 0, 0, 1};
            target->BeginDraw(); target->Clear(&black); target->SetTransform(&identity);
            target->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased);
            if (layer) target->PushLayer(&opacity, nullptr);
            target->SetTransform(&transform); target->FillGeometry(geometry.get(), brush.get(), nullptr);
            if (layer) target->PopLayer();
            target->PopAxisAlignedClip();
            require(target->EndDraw(nullptr, nullptr) == com::ok && export_copy_scene(scene.get(), scenes[reference]) &&
                read_scene_value(scenes[reference], 0U, headers[reference]), "filled-path complete retained snapshot");
        }
        const auto cold = render(scenes[0], headers[0], dpi_x, presentation);
        const auto warm = render(scenes[0], headers[0], dpi_x, presentation);
        const auto independent = render(scenes[1], headers[1], dpi_x, presentation);
        require(cold.size() == 64U*64U*4U && cold == warm && cold == independent &&
            std::any_of(cold.begin(), cold.end(), [](std::uint8_t value) { return value != 0U && value != 255U; }),
            "target path projection preserves independent world paint, opacity, clip and both DPI axes");
    }
}
// Explicit frame transport shares material and state handling with ordinary
// meshes. Render's rejection path verifies no GPU submission or upload occurs.
template<class Render, class Require>
void verify_source_coverage_frames(Render render, Require require)
{
    const progpu_native_scene_source_coverage_frame captured{sizeof(captured), 1U, 2.F, 1.25F, 64U, 64U, 0U, 0U};
    const progpu_native_scene_presentation presentation{sizeof(presentation), 0U, 0U, 64U, 64U, 2.F, 1.25F, 0U};
    const std::array<progpu_native_point, 6U> points{{{8, 10}, {24, 10}, {8, 30}, {24, 10}, {24, 30}, {8, 30}}};
    std::uint64_t owner = 0x95D231U;
    const auto scene = [&](unsigned coverage, bool gradient, float tx, float ty, bool clip) {
        semantic_scene_builder builder(owner++, 1U);
        std::uint32_t brush{}, state_index{};
        if (gradient) {
            progpu_native_scene_brush value{};
            value.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT; value.opacity = .5F;
            value.end_point = {32, 0}; value.stop_count = 2U;
            value.coordinate_transform0[0] = value.coordinate_transform1[1] = 1.F;
            const progpu_native_scene_gradient_stop stops[]{{{0, 0, 1, 1}, 0, 0, 0, 0}, {{1, 0, 0, 1}, 1, 0, 0, 0}};
            require(builder.add_brush(value, stops, brush), "source coverage gradient ownership");
        } else require(builder.add_solid_brush({1, 0, 0, 1}, .5F, brush), "source coverage opacity brush");
        auto state = builder.identity_state(); state.opacity = .5F;
        state.transform.m31 = tx; state.transform.m32 = ty;
        if (clip) { state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT; state.clip_rect = {6, 12, 4, 8}; }
        require(builder.add_state(state, state_index), "source coverage independent state");
        std::array<progpu_native_scene_mesh_vertex, 6U> vertices{};
        for (std::size_t i = 0U; i < points.size(); ++i) {
            const progpu_native_point uv{points[i].x / captured.dpi_scale_x, points[i].y / captured.dpi_scale_y};
            vertices[i] = {coverage ? points[i] : uv, uv, {1, 1, 1, 1}};
        }
        progpu_native_scene_vertex_mesh mesh{};
        mesh.struct_size = sizeof(mesh); mesh.flags = PROGPU_NATIVE_VERTEX_MESH_EDGE_ALIASED;
        mesh.vertex_count = 6U; mesh.color_blend_mode = 5U; mesh.transform = builder.identity_transform();
        const progpu_native_image_rect bounds{4, 8, 8, 16};
        if (coverage == 2U) {
            const std::array<progpu_native_path_segment, 4U> edges{{
                {{8, 10}, {24, 10}, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
                {{24, 10}, {24, 30}, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
                {{24, 30}, {8, 30}, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U},
                {{8, 30}, {8, 10}, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0U, 0U, 0U}}};
            progpu_native_scene_path_fill path{};
            path.segment_count = edges.size(); path.sample_grid = 8U;
            path.min_x = 8; path.min_y = 10; path.max_x = 24; path.max_y = 30;
            path.transform = builder.identity_transform(); path.color = {1, 1, 1, 1};
            require(builder.draw_source_paths({&path, 1U}, edges, {&brush, 1U}, bounds, captured, state_index),
                "source path frame draw");
        } else {
            require(coverage ? builder.draw_source_coverage({&mesh, 1U}, vertices, {&brush, 1U}, bounds, captured, state_index) :
                builder.draw_vertex_meshes({&mesh, 1U}, vertices, {}, {&brush, 1U}, bounds, state_index), "source coverage frame draw");
        }
        std::vector<std::byte> bytes; require(builder.build(bytes), "source coverage frame bytes"); return bytes;
    };
    const auto replay = [&](const auto& bytes, float dpi, const progpu_native_scene_presentation& physical,
        progpu_native_status expected = PROGPU_NATIVE_STATUS_SUCCESS) {
        progpu_native_scene_header header{}; require(read_scene_value(bytes, 0U, header), "source coverage frame header");
        return render(bytes, header, dpi, physical, expected);
    };
    for (const auto coverage : {1U, 2U}) {
    for (const bool gradient : {false, true}) for (const bool clip : {false, true}) {
        const auto candidate = scene(coverage, gradient, 2, 4, clip);
        const auto source = scene(0U, gradient, 2, 4, clip);
        const auto cold = replay(candidate, 2, presentation), warm = replay(candidate, 2, presentation);
        const auto control = replay(source, 2, presentation);
        require(cold.size() == 64U*64U*4U && cold == warm && cold == control,
            "source coverage keeps independent DPI, paint coordinates, clip, translation and opacity");
        if (!gradient && !clip) {
            const auto offset = (20U * 64U + 20U) * 4U;
            require(cold[offset] == 64U && cold[offset+3U] == 255U,
                "source coverage applies brush and state opacity exactly once");
        }
    }
    const auto original = scene(coverage, false, 0, 0, false);
    auto changed = presentation; changed.dpi_scale_y = 1.5F;
    replay(original, 2, changed, PROGPU_NATIVE_STATUS_UNSUPPORTED);
    changed = presentation; --changed.viewport_width;
    replay(original, 2, changed, PROGPU_NATIVE_STATUS_UNSUPPORTED);
    replay(scene(coverage, false, .125F, 0, false), 2, presentation, PROGPU_NATIVE_STATUS_UNSUPPORTED);
    if (coverage == 1U) {
        replay(scene(coverage, false, -5.F, 0, false), 2, presentation, PROGPU_NATIVE_STATUS_UNSUPPORTED);
    } else {
        // Unlike pre-triangulated source fringes, retained path coverage owns
        // its complete atlas even when its placement crosses the target edge.
        const auto shifted = scene(coverage, true, -5.F, 0, false);
        const auto clipped = replay(shifted, 2, presentation);
        require(clipped == replay(shifted, 2, presentation) &&
            clipped == replay(scene(0U, true, -5.F, 0, false), 2, presentation),
            "source paths preserve off-target coverage and original paint coordinates");
    }
    // A rejected physical frame does not poison the next exact original one.
    const auto recovered = replay(original, 2, presentation);
    require(recovered.size() == 64U*64U*4U, "source coverage original frame survives rejected replay");
    }
}
} // namespace progpu::native::direct2d::tests
