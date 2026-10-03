#include "../src/Direct2D/progpu_native_direct2d_vertical_metrics.hpp"
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
            }
        }
        d2d::original_vertical_outline_metrics empty{};
        if (!check(metrics->read_outline(0U, {}, empty) == com::ok && !empty.has_origin &&
                empty.advance_height == 900U && empty.top_side_bearing == 700,
                "empty CFF advances without fabricated bounds")) return false;

        // Additional algebraic controls isolate derivative degree and endpoint
        // handling. They exercise the reduction, not a claim that these are the
        // original font's contours. Every expected maximum is exact dyadic.
        const float tiny = std::numeric_limits<float>::denorm_min();
        const std::array<progpu_native_path_segment, 5U> curves{{
            {{0, 0}, {1, 16}, {2, 0}, {}, PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC, 0, 0, 0},
            {{0, 2}, {1, 4}, {2, 6}, {}, PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC, 0, 0, 0},
            {{0, 0}, {1, 0}, {2, 0}, {3, 8}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0},
            {{0, 0}, {1, 8}, {2, 0}, {3, 8}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0},
            {{0, 0}, {1, tiny * 4}, {2, tiny * 4}, {3, 0}, PROGPU_NATIVE_PATH_SEGMENT_CUBIC, 0, 0, 0}}};
        const std::array<double, 5U> tops{8, 6, 8, 8, static_cast<double>(tiny) * 3};
        for (std::size_t index = 0U; index < curves.size(); ++index) {
            d2d::original_vertical_outline_metrics result{};
            if (!check(metrics->read_outline(1U, std::span(&curves[index], 1U), result) == com::ok &&
                    result.top_origin == tops[index] + 80.0,
                    "quadratic/linear/repeated-root/endpoint/subnormal derivative policy")) return false;
        }
    }
    return true;
}
} // namespace

bool progpu_native_direct2d_cff_vertical_tests() { return contour_origins(); }
