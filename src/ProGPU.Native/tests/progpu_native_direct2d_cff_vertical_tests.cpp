#include "../src/Direct2D/progpu_native_direct2d_vertical_metrics.hpp"
#include "../src/Direct2D/progpu_native_direct2d_prepared_glyphs.hpp"
#include "progpu_native_direct2d_font_source_fixture.hpp"
#include "progpu_native_direct2d_cff_vertical_fixture.hpp"

#include <cmath>
#include <cstdio>
#include <limits>

namespace {
using namespace progpu::native::direct2d::tests;
namespace d2d = progpu::native::direct2d;
namespace com = progpu::native::com;

bool check(bool value, const char* message)
{
    if (!value) std::fprintf(stderr, "CFF retained vertical contour: %s\n", message);
    return value;
}

bool contour_origins()
{
    for (const bool fractional : {false, true}) {
        font_stream stream; stream.bytes = make_cff_vertical_font(fractional); stream.declared_size = stream.bytes.size();
        font_loader loader; loader.stream = &stream; font_file file; file.loader = &loader;
        font_face face; face.files[0] = &file; face.declared_count = 1U; face.type = 0U;
        face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
        std::shared_ptr<const d2d::original_font_capture> source;
        std::shared_ptr<const d2d::retained_original_vertical_metrics> metrics;
        if (!check(d2d::capture_original_font(&face, source) == com::ok &&
                d2d::retained_original_vertical_metrics::create(source, metrics) == com::ok,
                "original CFF source metrics without VORG")) return false;
        for (const auto glyph : {std::uint16_t{1}, std::uint16_t{2}}) {
            const auto contours = cff_vertical_contours(glyph, fractional);
            d2d::original_vertical_outline_metrics result{};
            const double expected = glyph == 1U ? (fractional ? 381.5 : 380.0) : 240.0;
            const auto advance = glyph == 1U ? 1000U : 1100U;
            if (!check(metrics->read_outline(glyph, contours, result) == com::ok && result.has_origin &&
                    result.top_origin == expected && result.bottom_origin == expected - advance &&
                    result.advance_height == advance && result.origin_kind == d2d::original_vertical_origin_kind::cff_contour,
                    "literal cubic maximum plus signed bearing, without envelope or integer rounding")) return false;
            const double source_expected = glyph == 1U ? (fractional ? 482.0 : 480.0) : 340.0;
            if (!check(metrics->read_directwrite_outline(glyph, contours, result) == com::ok && result.has_origin &&
                    result.top_origin == source_expected && result.bottom_origin == source_expected - advance &&
                    result.advance_height == advance &&
                    result.origin_kind == d2d::original_vertical_origin_kind::cff_directwrite_controls,
                    "original DirectWrite source origin remains separate from exact curve bounds")) return false;
            const auto retained = result;
            for (unsigned fault = 0U; fault < 3U; ++fault) {
                auto malformed = contours;
                if (fault == 0U) malformed[1].kind = PROGPU_NATIVE_PATH_SEGMENT_ARC;
                if (fault == 1U) malformed[1].p1.y = std::numeric_limits<float>::infinity();
                if (fault == 2U) malformed[0].p2.x = std::numeric_limits<float>::quiet_NaN();
                if (!check(metrics->read_outline(glyph, malformed, result) == com::invalid_argument &&
                        result.top_origin == retained.top_origin && result.bottom_origin == retained.bottom_origin &&
                        result.has_origin == retained.has_origin && result.origin_kind == retained.origin_kind,
                        "late unsupported/nonfinite curve leaves the complete origin unchanged")) return false;
                if (!check(metrics->read_directwrite_outline(glyph, malformed, result) == com::invalid_argument &&
                        result.top_origin == retained.top_origin && result.bottom_origin == retained.bottom_origin &&
                        result.has_origin == retained.has_origin && result.origin_kind == retained.origin_kind,
                        "source control-envelope validation also leaves output atomic")) return false;
            }
        }
        d2d::original_vertical_outline_metrics empty{};
        if (!check(metrics->read_outline(0U, {}, empty) == com::ok && !empty.has_origin &&
                empty.advance_height == 900U && empty.top_side_bearing == 700,
                "empty CFF advances without fabricated bounds")) return false;
        if (!check(metrics->read_directwrite_outline(0U, {}, empty) == com::ok && !empty.has_origin &&
                empty.advance_height == 900U && empty.top_side_bearing == 700,
                "empty source CFF still consumes its actual advance without ink")) return false;

        // Additional algebraic controls isolate derivative degree and endpoint
        // handling. They exercise the reduction, not a claim that these are the
        // original font's contours. Every expected maximum is exact dyadic.
        // A separate zero-bearing owner prevents the tiny maximum disappearing
        // in the addition to an ordinary nonzero top side bearing.
        auto zero_source = std::make_shared<d2d::original_font_capture>(*source);
        auto tables = vertical_font_wire::original_tables(zero_source->files[0]);
        for (auto& table : tables)
            if (table.tag == 0x766D7478U) vertical_font_wire::put16(table.data, 6U, 0U);
        zero_source->files[0] = vertical_font_wire::assemble(std::move(tables), 0x4F54544FU);
        std::shared_ptr<const d2d::retained_original_vertical_metrics> zero_metrics;
        if (d2d::retained_original_vertical_metrics::create(zero_source, zero_metrics) != com::ok) return false;
        const float tiny = std::numeric_limits<float>::denorm_min();
        const std::array<progpu_native_path_segment, 7U> curves{{
            {{0, 0}, {1, 16}, {2, 0}, {}, PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC, 0, 0, 0},
            {{0, 2}, {1, 4}, {2, 6}, {}, PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC, 0, 0, 0},
            {{0, 0}, {1, 0}, {2, 0}, {3, 8}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0},
            {{0, 0}, {1, 8}, {2, 0}, {3, 8}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0},
            {{0, 0}, {1, tiny * 4}, {2, tiny * 4}, {3, 0}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0},
            {{0, 0}, {1, 4}, {2, 2}, {3, -22}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0},
            {{0, 0}, {1, 2}, {2, 8}, {3, -6}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0}}};
        const std::array<double, 7U> tops{8, 6, 8, 8, static_cast<double>(tiny) * 3, 1.625, 3};
        for (std::size_t index = 0U; index < curves.size(); ++index) {
            d2d::original_vertical_outline_metrics result{};
            if (!check(zero_metrics->read_outline(1U, std::span(&curves[index], 1U), result) == com::ok &&
                    result.top_origin == tops[index],
                    "quadratic/linear/repeated-root/endpoint/subnormal derivative policy")) return false;
        }
    }
    return true;
}

bool prepared_contour_origins()
{
    for (const bool fractional : {false, true}) {
        font_stream stream; stream.bytes = make_cff_vertical_font(fractional); stream.declared_size = stream.bytes.size();
        font_loader loader; loader.stream = &stream; font_file file; file.loader = &loader;
        font_face face; face.files[0] = &file; face.declared_count = 1U; face.type = 0U;
        face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
        std::shared_ptr<const d2d::original_font_capture> source;
        std::shared_ptr<d2d::prepared_original_font> font;
        if (d2d::capture_original_font(&face, source) != com::ok ||
            d2d::prepared_original_font::create(source, font) != com::ok) return false;
        com::pointer<d2d::compat::factory> factory;
        if (d2d::compat::create_factory(factory.put()) != com::ok) return false;
        const d2d::original_glyph_target frame{com::pointer<com::unknown>(factory.get()), 1U,
            {0, 1, -1, 0, 150, 0}, {8, 50}, {256U, 256U}, 144, 120, {}, d2d::compat::text_antialias_mode::grayscale};
        rendering_parameters parameters; parameters.mode = d2d::compat::rendering_mode::outline;
        const std::uint16_t ids[]{1U, 0U, 2U};
        const float advances[]{40, -7, 12};
        const d2d::compat::glyph_offset offsets[]{{0.5F, 0.25F}, {0, 0}, {-2, 3}};
        const auto reads = stream.reads;
        face.count_result = d2d::compat::not_implemented;
        for (const bool nominal : {false, true}) {
            const d2d::compat::glyph_run run{&face, 62.5F, 3U, ids, nominal ? nullptr : advances, offsets, -1, 2U};
            std::shared_ptr<const d2d::original_glyph_request> request;
            std::shared_ptr<const d2d::prepared_original_glyph_run> prepared;
            if (!check(d2d::capture_original_glyph_request(source, run, d2d::compat::measuring_mode::natural,
                    &parameters, frame, request) == com::ok && font->prepare(request, prepared) == com::ok &&
                    prepared->segments().size() == 4U && font->cached_glyph_count() == 3U,
                    "actual prepared CFF sideways source without VORG, including empty advance")) return false;
            const float first_end = fractional ? 38.625F : 38.5F;
            const float first_control = 13.5F;
            const float second_end = nominal ? 147.25F : 61.5F;
            const float second_control = nominal ? 122.25F : 36.5F;
            const std::array<progpu_native_path_segment, 4U> expected{{
                {{first_end, 67.25F}, {first_control, 67.25F}, {first_control, 49.75F}, {first_end, 49.75F},
                    PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0},
                {{first_end, 49.75F}, {first_end, 67.25F}, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0, 0, 0},
                {{second_end, 68.875F}, {second_control, 68.875F}, {second_control, 56.375F}, {second_end, 56.375F},
                    PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0},
                {{second_end, 56.375F}, {second_end, 68.875F}, {}, {}, PROGPU_NATIVE_PATH_SEGMENT_LINE, 0, 0, 0}}};
            for (std::size_t index = 0U; index < expected.size(); ++index) {
                const auto& actual = prepared->segments()[index]; const auto& reference = expected[index];
                const auto point = [](progpu_native_point a, progpu_native_point b) { return a.x == b.x && a.y == b.y; };
                if (!check(actual.kind == reference.kind && point(actual.p0, reference.p0) && point(actual.p1, reference.p1) &&
                        (actual.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE ||
                            (point(actual.p2, reference.p2) && point(actual.p3, reference.p3))),
                        "independent original DirectWrite cubic endpoints/handles and origin placement")) return false;
            }
            std::shared_ptr<const d2d::prepared_original_glyph_run> warm;
            if (!check(font->prepare(request, warm) == com::ok && warm->segments().size() == expected.size() &&
                    font->cached_glyph_count() == 3U && stream.reads == reads && face.outline_calls == 0U && face.table_calls == 0U,
                    "CFF contour origins are retained without source callbacks or cache replacement")) return false;
        }
    }
    return true;
}
} // namespace

bool progpu_native_direct2d_cff_vertical_tests() { return contour_origins() && prepared_contour_origins(); }
