#pragma once

#include "progpu_native_direct2d_variable_glyph_fixture.hpp"
#include "progpu_native_direct2d_cff_glyph_fixture.hpp"

namespace progpu::native::direct2d::tests {

template<class Render, class Require>
void verify_original_path_glyph_pixels(Render render, Require require)
{
    for (const unsigned original_frame : {53U, 63U, 73U, 83U, 93U, 103U, 113U, 123U, 141U, 143U, 147U, 149U}) {
        std::vector<std::byte> retained;
        progpu_native_scene_header header{};
        {
            const bool variable = original_frame < 129U;
            const bool rtl = variable && original_frame >= 89U;
            const auto option = variable ? ((original_frame - 49U) % 40U) / 10U : 0U;
            const auto cff_kind = original_frame < 145U ? cff_font_kind::cff2_variable_fixed : cff_font_kind::cff2_variable_hvar;
            const std::size_t instance = variable ? 2U : ((original_frame - (original_frame < 145U ? 139U : 145U)) / 2U);
            const auto variant = static_cast<std::uint32_t>(instance % 3U);
            font_stream stream;
            stream.bytes = variable ? make_variable_font(variable_pixel_font_options[option]) : make_cff_font(cff_kind);
            stream.declared_size = stream.bytes.size();
            font_loader loader; loader.stream = &stream;
            font_file file; file.loader = &loader;
            font_face5 face; face.files = {&file}; face.declared_count = 1U;
            face.type = variable ? 1U : 0U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
            face.axes = {{0x74686777U, variable ? variable_font_cases[instance].weight : cff_font_weight(instance)}};
            std::shared_ptr<const original_font_capture> source;
            std::shared_ptr<prepared_original_font> font;
            require(capture_original_font(&face, source) == com::ok && prepared_original_font::create(source, font) == com::ok,
                "source path original font, instance and occurrence owner");
            const auto reads = stream.reads, values = face.value_reads;
            face.count_result = compat::not_implemented; face.values_result = compat::not_implemented;
            face.axes[0].value = -12345; stream.bytes.assign(16U, std::byte{0});
            rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
            com::pointer<compat::factory> factory;
            com::pointer<compat::scene_factory_native> scene_factory;
            com::pointer<compat::render_target> target;
            com::pointer<compat::scene_render_target_native> scene;
            const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95D237U, original_frame};
            require(compat::create_factory(factory.put()) == com::ok &&
                factory.as(compat::scene_factory_native_interface_id, scene_factory) == com::ok &&
                scene_factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
                target.as(compat::scene_render_target_native_interface_id, scene) == com::ok,
                "source path original recorder target");
            if (variable) {
                record_variable_pixel_case(factory.get(), target.get(), font, &parameters, instance, false, variant,
                    variable_pixel_path::prepared, require, nullptr, nullptr, rtl, variable_pixel_font_options[option]);
            } else {
                record_cff_pixel_case(factory.get(), target.get(), font, &parameters, cff_kind, instance, false, variant,
                    cff_pixel_path::prepared, require);
            }
            require(export_copy_scene(scene.get(), retained) && read_scene_value(retained, 0U, header),
                "source path complete retained scene export");
            unsigned source_paths = 0U;
            for (unsigned i = 0; i < header.command_count; ++i) {
                progpu_native_scene_command command{};
                require(read_scene_value(retained, header.command_offset + i * header.command_stride, command),
                    "source path retained command read");
                source_paths += command.kind == PROGPU_NATIVE_SCENE_COMMAND_DRAW_SOURCE_PATH;
            }
            require(source_paths == 1U && stream.reads == reads && face.value_reads == values &&
                face.outline_calls == 0U && face.table_calls == 0U && font->cached_glyph_count() == 3U,
                "source path recorder owns original geometry without new source callbacks");
        }
        // All producer, target, font and brush owners end before either render.
        const auto expected = original_path_glyph_coverage_pixels(original_frame);
        for (const bool warm : {false, true}) {
            progpu_native_scene_frame_metrics metrics{}; metrics.struct_size = sizeof(metrics);
            const auto pixels = render(retained, header, metrics);
            require(expected.size() == 64U*64U*4U && pixels == expected,
                "source path retains every original variable/CFF Microsoft pixel, cold and warm");
            require(!warm || (metrics.vertex_upload_bytes == 0U && metrics.index_upload_bytes == 0U &&
                metrics.coverage_staging_bytes == 0U), "source path warm replay has zero retained uploads");
        }
    }
}
} // namespace progpu::native::direct2d::tests
