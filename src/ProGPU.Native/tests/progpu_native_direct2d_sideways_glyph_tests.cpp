#include "../src/Direct2D/progpu_native_direct2d_prepared_glyphs.hpp"
#include "progpu_native_direct2d_font_source_fixture.hpp"
#include "progpu_native_direct2d_vertical_font_fixture.hpp"

#include <cstdio>
#include <limits>

namespace {
using namespace progpu::native::direct2d::tests;
namespace d2d = progpu::native::direct2d;
namespace compat = d2d::compat;
namespace com = progpu::native::com;

bool check(bool value, const char* message)
{
    if (!value) std::fprintf(stderr, "prepared sideways glyph: %s\n", message);
    return value;
}

struct original_vertical_source final {
    font_stream stream;
    font_loader loader;
    font_file file;
    font_face face;
    explicit original_vertical_source(bool compact, bool cff = false)
    {
        vertical_font_options options{}; options.compact_metrics = compact;
        options.kind = cff ? vertical_font_kind::cff : vertical_font_kind::truetype; options.vorg = cff;
        stream.bytes = make_vertical_font(options); stream.declared_size = stream.bytes.size();
        loader.stream = &stream; file.loader = &loader; face.files = {&file}; face.declared_count = 1U;
        face.type = cff ? 0U : 1U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
    }
};

bool explicit_sideways_placement()
{
    for (const bool cff : {false, true}) {
    for (const bool compact : {false, true}) {
        original_vertical_source original(compact, cff);
        std::shared_ptr<const d2d::original_font_capture> source;
        std::shared_ptr<d2d::prepared_original_font> font;
        if (d2d::capture_original_font(&original.face, source) != com::ok ||
            d2d::prepared_original_font::create(source, font) != com::ok) return false;
        const auto reads = original.stream.reads;
        original.face.count_result = compat::not_implemented;
        original.stream.bytes[0] = std::byte{0xFF}; // Original retained bytes, not this producer.
        com::pointer<compat::factory> factory;
        if (compat::create_factory(factory.put()) != com::ok) return false;
        const d2d::original_glyph_target frame{com::pointer<com::unknown>(factory.get()), 1U,
            {0, 1, -1, 0, 117, 3}, {8, 50}, {512U, 256U}, 144, 120, {},
            compat::text_antialias_mode::grayscale};
        rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
        const std::uint16_t indices[]{1U, 0U, 2U};
        const float advances[]{40, -7, 12};
        const compat::glyph_offset offsets[]{{0.5F, 0.25F}, {0, 0}, {-2, 3}};
        compat::glyph_run run{&original.face, 125, 3U, indices, advances, offsets, 0, 2U};
        std::shared_ptr<const d2d::original_glyph_request> request;
        std::shared_ptr<const d2d::prepared_original_glyph_run> prepared;
        // Populate the horizontal cache before the first vertical request.
        if (d2d::capture_original_glyph_request(source, run, compat::measuring_mode::natural,
                &parameters, frame, request) != com::ok || font->prepare(request, prepared) != com::ok) return false;
        const auto horizontal = prepared;
        for (const auto sideways : {1, -1}) {
            for (const auto level : {0U, 2U}) {
                for (const bool nominal : {false, true}) {
                    run.is_sideways = sideways; run.bidi_level = level;
                    run.glyph_advances = nominal ? nullptr : advances;
                    if (!check(d2d::capture_original_glyph_request(source, run, compat::measuring_mode::natural,
                            &parameters, frame, request) == com::ok && font->prepare(request, prepared) == com::ok &&
                            prepared->request().sideways == sideways && prepared->request().bidi_level == level &&
                            (prepared->request().glyphs.advances() == nullptr) == nominal &&
                            prepared->segments().size() == 8U && font->cached_glyph_count() == 3U &&
                            prepared->request().target.transform.m12 == 1 &&
                            prepared->request().target.transform.m21 == -1 &&
                            prepared->request().target.dpi_x == 144 && prepared->request().target.dpi_y == 120,
                            "raw BOOL, logical run, actual source frame and horizontal cache survive sideways placement")) return false;
                    // Literal oracle for original asymmetric glyphs, independently
                    // authored before decoder/metric output: TT origins(308,500),
                    // (364,600), CFF origins(300,700),(350,600). The empty middle
                    // glyph consumes -7 or900/8; CFF uses real VORG, not glyf bounds.
                    const float second_left = nominal ? (compact ? 241.0F : 253.5F) : 49.0F;
                    const std::array<compat::rectangle_f, 2U> boxes = cff
                        ? std::array<compat::rectangle_f, 2U>{{{51, 49.75F, 101, 84.75F},
                            {second_left, 69.5F, second_left + 62.5F, 94.5F}}}
                        : std::array<compat::rectangle_f, 2U>{{{26, 50.75F, 76, 85.75F},
                            {second_left, 71.25F, second_left + 62.5F, 96.25F}}};
                    for (std::size_t glyph = 0U; glyph < boxes.size(); ++glyph) {
                        const auto& box = boxes[glyph]; float twice_area = 0;
                        for (std::size_t edge = 0U; edge < 4U; ++edge) {
                            const auto& segment = prepared->segments()[glyph * 4U + edge];
                            const auto corner = [&](progpu_native_point value) {
                                return (value.x == box.left || value.x == box.right) &&
                                    (value.y == box.top || value.y == box.bottom);
                            };
                            if (!check(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE &&
                                    corner(segment.p0) && corner(segment.p1),
                                    "independent rotated origin, signed offset and nominal/explicit advance boxes")) return false;
                            twice_area += segment.p0.x * segment.p1.y - segment.p1.x * segment.p0.y;
                        }
                        if (!check(twice_area == (glyph == 0U ? -3500.0F : -3125.0F),
                                "sideways rotates rather than reflecting or reversing source contours")) return false;
                    }
                }
            }
        }
        run.is_sideways = 0; run.bidi_level = 2U; run.glyph_advances = advances;
        if (!check(d2d::capture_original_glyph_request(source, run, compat::measuring_mode::natural,
                &parameters, frame, request) == com::ok && font->prepare(request, prepared) == com::ok &&
                prepared->segments().size() == horizontal->segments().size(),
                "horizontal replay after vertical owner publication")) return false;
        for (std::size_t index = 0U; index < prepared->segments().size(); ++index) {
            const auto& before = horizontal->segments()[index]; const auto& after = prepared->segments()[index];
            if (!check(before.p0.x == after.p0.x && before.p0.y == after.p0.y &&
                    before.p1.x == after.p1.x && before.p1.y == after.p1.y && before.kind == after.kind,
                    "sideways never modifies cached design contours or old prepared runs")) return false;
        }
        const auto retained = prepared;
        run.is_sideways = -1; run.bidi_level = 3U;
        if (!check(d2d::capture_original_glyph_request(source, run, compat::measuring_mode::natural,
                &parameters, frame, request) == com::ok && font->prepare(request, prepared) == compat::not_implemented &&
                prepared == retained && font->cached_glyph_count() == 3U,
                "unproven combined sideways/RTL contract stays atomic")) return false;
        if (!check(original.stream.reads == reads && original.face.outline_calls == 0U && original.face.table_calls == 0U,
                "sideways never reacquires source outlines, tables or font files")) return false;
    }
    }
    return true;
}

bool late_sideways_failure()
{
    original_vertical_source original(false);
    std::shared_ptr<const d2d::original_font_capture> source;
    std::shared_ptr<d2d::prepared_original_font> font;
    if (d2d::capture_original_font(&original.face, source) != com::ok ||
        d2d::prepared_original_font::create(source, font) != com::ok) return false;
    com::pointer<compat::factory> factory;
    if (compat::create_factory(factory.put()) != com::ok) return false;
    d2d::original_glyph_target frame{com::pointer<com::unknown>(factory.get()), 1U,
        {1, 0, 0, 1, 0, 0}, {8, 50}, {512U, 256U}, 96, 96, {}, compat::text_antialias_mode::grayscale};
    rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
    const std::uint16_t indices[]{1U, 0U, 2U};
    const float advances[]{40, -7, 12};
    compat::glyph_run run{&original.face, 125, 1U, indices, advances, nullptr, 0, 2U};
    std::shared_ptr<const d2d::original_glyph_request> request;
    std::shared_ptr<const d2d::prepared_original_glyph_run> prepared;
    if (d2d::capture_original_glyph_request(source, run, compat::measuring_mode::natural,
            &parameters, frame, request) != com::ok || font->prepare(request, prepared) != com::ok) return false;
    const auto retained = prepared;
    const compat::glyph_offset overflowing[]{{0, 0}, {0, 0}, {std::numeric_limits<float>::max(), 0}};
    run.glyph_count = 3U; run.is_sideways = 1; run.glyph_offsets = overflowing;
    frame.baseline.x = std::numeric_limits<float>::max();
    if (!check(d2d::capture_original_glyph_request(source, run, compat::measuring_mode::natural,
            &parameters, frame, request) == com::ok && font->prepare(request, prepared) == com::invalid_argument &&
            prepared == retained && font->cached_glyph_count() == 1U,
            "late sideways overflow does not publish empty or later outline cache additions")) return false;
    run.glyph_offsets = nullptr; frame.baseline.x = 8;
    if (!check(d2d::capture_original_glyph_request(source, run, compat::measuring_mode::natural,
            &parameters, frame, request) == com::ok && font->prepare(request, prepared) == com::ok &&
            font->cached_glyph_count() == 3U && prepared != retained,
            "failed sideways metric-owner preparation does not poison retry")) return false;
    return true;
}
} // namespace

bool progpu_native_direct2d_sideways_glyph_tests()
{
    return explicit_sideways_placement() && late_sideways_failure();
}
