#include "progpu_native_direct2d_vertical_font_fixture.hpp"
#include "progpu_native_direct2d_font_axis_fixture.hpp"
#include "../src/Direct2D/progpu_native_direct2d_vertical_metrics.hpp"
#include "../src/Direct2D/progpu_native_direct2d_prepared_glyphs.hpp"
#include "progpu_native_text.hpp"

#include <cstdio>
#include <limits>

namespace {
using namespace progpu::native::direct2d::tests;
namespace d2d = progpu::native::direct2d;
namespace text = progpu::native::text;
namespace com = progpu::native::com;
namespace wire = vertical_font_wire;

bool check(bool value, const char* message)
{
    if (!value) std::fprintf(stderr, "original vertical metrics: %s\n", message);
    return value;
}

std::size_t record(const wire::bytes& bytes, std::uint32_t tag)
{
    for (std::size_t item = 0U; item < wire::read16(bytes, 4U); ++item) {
        const auto at = 12U + item * 16U;
        if (wire::read32(bytes, at) == tag) return at;
    }
    throw std::invalid_argument("authored vertical table not present");
}

struct source_font final {
    font_stream stream;
    font_loader loader;
    font_file file;
    font_face5 face;
    explicit source_font(vertical_font_options options)
    {
        stream.bytes = make_vertical_font(options); stream.declared_size = stream.bytes.size();
        loader.stream = &stream; file.loader = &loader; face.files = {&file}; face.declared_count = 1U;
        face.type = options.kind == vertical_font_kind::cff ? 0U : 1U;
        face.index = 0U; face.glyph_count = 3U; face.simulations = 0U;
    }
    com::result capture(std::shared_ptr<const d2d::original_font_capture>& output)
    {
        return d2d::capture_original_font(&face, output);
    }
};

bool same_metrics(const d2d::original_vertical_glyph_metrics& first,
    const d2d::original_vertical_glyph_metrics& second)
{
    return first.advance_height == second.advance_height && first.top_side_bearing == second.top_side_bearing &&
        first.top_origin == second.top_origin && first.bottom_origin == second.bottom_origin &&
        first.has_origin == second.has_origin && first.origin_kind == second.origin_kind;
}

bool static_source_metrics()
{
    for (const bool cff : {false, true}) for (const bool compact : {false, true}) {
        vertical_font_options options{};
        options.kind = cff ? vertical_font_kind::cff : vertical_font_kind::truetype;
        options.vorg = cff; options.compact_metrics = compact;
        source_font original(options);
        std::shared_ptr<const d2d::original_font_capture> source;
        std::shared_ptr<const d2d::retained_original_vertical_metrics> retained;
        if (!check(original.capture(source) == com::ok &&
            d2d::retained_original_vertical_metrics::create(source, retained) == com::ok &&
            retained->source() == source && retained->has_metrics(), "exact source owner and complete vertical metrics")) return false;
        const auto reads = original.stream.reads;
        const auto axis_reads = original.face.value_reads;
        // The helper borrows only the owned captured bytes. Later mutable COM
        // source contents cannot become its vertical metric generation.
        original.stream.bytes.assign(1U, std::byte{0xFF}); original.face.axes = {{0x74686777U, 999}};
        for (std::uint16_t glyph = 0U; glyph < 3U; ++glyph) {
            d2d::original_vertical_glyph_metrics actual{};
            const auto expected = expected_vertical_glyph(options, 0U, glyph);
            if (!check(retained->read_base(glyph, actual) == com::ok &&
                actual.advance_height == expected.vertical_advance && actual.top_side_bearing == expected.top_side_bearing &&
                actual.has_origin == expected.has_vertical_origin &&
                (!actual.has_origin || (actual.top_origin == expected.vertical_origin &&
                    actual.bottom_origin == expected.vertical_origin - expected.vertical_advance)) &&
                actual.origin_kind == (actual.has_origin ? (cff ? d2d::original_vertical_origin_kind::cff_vorg
                    : d2d::original_vertical_origin_kind::true_type_bounds) : d2d::original_vertical_origin_kind::unavailable),
                "literal full/compact bearings, top and bottom origins including empty glyph")) return false;
        }
        d2d::original_vertical_glyph_metrics sentinel{55U, -13, 71, -89, true, d2d::original_vertical_origin_kind::cff_vorg};
        const auto before = sentinel;
        if (!check(retained->read_base(3U, sentinel) == com::invalid_argument && same_metrics(sentinel, before) &&
            original.stream.reads == reads && original.face.value_reads == axis_reads,
            "later invalid glyph is atomic and no source callback occurs")) return false;
    }
    return true;
}

bool strict_tables_and_absence()
{
    vertical_font_options options{}; options.compact_metrics = true;
    source_font original(options);
    std::shared_ptr<const d2d::original_font_capture> source;
    std::shared_ptr<const d2d::retained_original_vertical_metrics> result;
    if (original.capture(source) != com::ok || d2d::retained_original_vertical_metrics::create(source, result) != com::ok) return false;
    const auto initial = result;
    for (unsigned fault = 0U; fault < 18U; ++fault) {
        auto bad = std::make_shared<d2d::original_font_capture>(*source);
        auto& bytes = bad->files[0];
        const auto vhea_record = record(bytes, 0x76686561U), vmtx_record = record(bytes, 0x766D7478U);
        const auto vhea = wire::read32(bytes, vhea_record + 8U);
        if (fault == 0U) wire::put32(bytes, vhea_record, 0x78787878U);
        if (fault == 1U) wire::put32(bytes, vmtx_record, 0x78787878U);
        if (fault == 2U) wire::put32(bytes, vhea_record + 12U, 35U);
        if (fault == 3U) wire::put32(bytes, vmtx_record + 12U, 7U); // final compact bearing missing
        if (fault == 4U) wire::put32(bytes, vhea, 0x00012000U);
        if (fault == 5U) wire::put16(bytes, vhea + 34U, 0U);
        if (fault == 6U) wire::put16(bytes, vhea + 34U, 4U);
        if (fault >= 7U && fault <= 11U) wire::put16(bytes, vhea + 24U + (fault - 7U) * 2U, 1U);
        if (fault == 12U) { wire::put32(bytes, vhea, 0x00010000U); wire::put16(bytes, vhea + 8U, 1U); }
        if (fault == 13U) wire::put32(bytes, vmtx_record + 8U, 0xFFFFFFF0U);
        if (fault == 14U) wire::put32(bytes, record(bytes, 0x6E616D65U), 0x766D7478U); // duplicate
        if (fault == 15U) bad->face_type = 0U;
        if (fault == 16U) bad->glyph_count = 2U;
        if (fault == 17U) wire::put32(bytes, record(bytes, 0x6E616D65U), 0x43464620U); // competing family
        if (!check(com::failed(d2d::retained_original_vertical_metrics::create(bad, result)) && result == initial,
            "malformed original vertical tables reject without replacing retained helper")) return false;
        if (fault == 3U) {
            std::shared_ptr<d2d::prepared_original_font> horizontal;
            if (!check(d2d::prepared_original_font::create(bad, horizontal) == com::ok,
                "unused malformed vertical table does not regress horizontal preparation")) return false;
        }
    }
    auto absent = std::make_shared<d2d::original_font_capture>(*source);
    auto& bytes = absent->files[0];
    wire::put32(bytes, record(bytes, 0x76686561U), 0x78787878U);
    wire::put32(bytes, record(bytes, 0x766D7478U), 0x79797979U);
    d2d::original_vertical_glyph_metrics sentinel{55U, -13, 71, -89, true, d2d::original_vertical_origin_kind::cff_vorg};
    const auto before = sentinel;
    if (!check(d2d::retained_original_vertical_metrics::create(absent, result) == com::ok && !result->has_metrics() &&
        result->read_base(1U, sentinel) == d2d::compat::not_implemented && same_metrics(sentinel, before),
        "two absent tables remain explicitly unavailable, not synthesized from horizontal metrics")) return false;
    auto version_one = std::make_shared<d2d::original_font_capture>(*source);
    auto& one = version_one->files[0]; wire::put32(one, wire::read32(one, record(one, 0x76686561U) + 8U), 0x00010000U);
    if (!check(d2d::retained_original_vertical_metrics::create(version_one, result) == com::ok,
        "complete original vhea version1.0 remains admitted")) return false;
    auto ignored = std::make_shared<d2d::original_font_capture>(*source);
    auto& tt = ignored->files[0]; const auto fake = record(tt, 0x6E616D65U);
    wire::put32(tt, fake, 0x564F5247U); wire::put32(tt, fake + 8U, 0xFFFFFFF0U);
    if (!check(d2d::retained_original_vertical_metrics::create(ignored, result) == com::ok &&
        result->read_base(1U, sentinel) == com::ok && sentinel.top_origin == 500,
        "TrueType ignores even malformed VORG rather than overriding original glyf origin")) return false;
    return true;
}

bool cff_vorg_controls()
{
    vertical_font_options options{}; options.kind = vertical_font_kind::cff; options.vorg = true;
    source_font original(options);
    std::shared_ptr<const d2d::original_font_capture> source;
    std::shared_ptr<const d2d::retained_original_vertical_metrics> result;
    if (original.capture(source) != com::ok || d2d::retained_original_vertical_metrics::create(source, result) != com::ok) return false;
    const auto initial = result;
    for (unsigned fault = 0U; fault < 7U; ++fault) {
        auto bad = std::make_shared<d2d::original_font_capture>(*source);
        auto& bytes = bad->files[0]; const auto entry = record(bytes, 0x564F5247U);
        const auto at = wire::read32(bytes, entry + 8U);
        if (fault == 0U) wire::put32(bytes, entry + 12U, 7U);
        if (fault == 1U) wire::put32(bytes, at, 0x00010001U);
        if (fault == 2U) wire::put16(bytes, at + 6U, 2U);
        if (fault == 3U) wire::put16(bytes, at + 8U, 3U);
        if (fault == 4U) wire::put32(bytes, entry + 8U, 0xFFFFFFF0U);
        if (fault == 5U) wire::put32(bytes, record(bytes, 0x6E616D65U), 0x564F5247U);
        if (fault == 6U) bad->face_type = 1U;
        if (!check(com::failed(d2d::retained_original_vertical_metrics::create(bad, result)) && result == initial,
            "malformed CFF VORG and conflicting original family preserve output")) return false;
    }
    for (const bool duplicate : {false, true}) {
        auto bad = std::make_shared<d2d::original_font_capture>(*source);
        auto tables = wire::original_tables(bad->files[0]); wire::bytes vorg(16U);
        wire::put16(vorg, 0U, 1U); wire::put16(vorg, 4U, 700U); wire::put16(vorg, 6U, 2U);
        wire::put16(vorg, 8U, 2U); wire::put16(vorg, 10U, 600U);
        wire::put16(vorg, 12U, duplicate ? 2U : 1U); wire::put16(vorg, 14U, 500U);
        wire::set(tables, 0x564F5247U, std::move(vorg)); bad->files[0] = wire::assemble(std::move(tables), 0x4F54544FU);
        if (!check(com::failed(d2d::retained_original_vertical_metrics::create(bad, result)) && result == initial,
            "VORG overrides must be strictly increasing and unique")) return false;
    }
    auto absent = std::make_shared<d2d::original_font_capture>(*source);
    auto& bytes = absent->files[0]; wire::put32(bytes, record(bytes, 0x564F5247U), 0x78787878U);
    d2d::original_vertical_glyph_metrics actual{};
    return check(d2d::retained_original_vertical_metrics::create(absent, result) == com::ok && result->has_metrics() &&
        result->read_base(2U, actual) == com::ok && actual.advance_height == 1100U && !actual.has_origin &&
        actual.origin_kind == d2d::original_vertical_origin_kind::unavailable,
        "CFF without VORG keeps actual advance but never substitutes a control-point envelope origin");
}

struct phantom_scratch final {
    std::vector<text::sfnt_gvar_tuple_header> headers;
    std::vector<std::int16_t> regions, xs, ys;
    std::vector<std::uint32_t> shared, selected;
    explicit phantom_scratch(text::sfnt_glyph_phantom_variation_requirements size)
        : headers(size.tuple_header_count), regions(size.region_coordinate_count), xs(size.delta_count),
          ys(size.delta_count), shared(size.point_number_count), selected(size.point_number_count) {}
    text::sfnt_glyph_phantom_variation_scratch spans() { return {headers, regions, shared, selected, xs, ys}; }
};

bool vertical_phantom_controls()
{
    vertical_font_options options{}; options.kind = vertical_font_kind::truetype_variable;
    const auto bytes = make_vertical_font(options);
    text::sfnt_font_view font;
    if (!text::sfnt_font_view::try_create(bytes, 0U, font)) return false;
    constexpr std::array<std::int16_t, 5U> coordinates{0, 8192, 16384, -8192, -16384};
    for (std::uint16_t glyph = 0U; glyph < 3U; ++glyph) {
        std::uint32_t count = 0U; text::sfnt_glyph_phantom_variation_requirements needs{};
        if (!font.try_get_glyph_variation_item_count(glyph, count) ||
            !font.try_get_glyph_phantom_variation_requirements(glyph, count, needs)) return false;
        phantom_scratch scratch(needs);
        for (std::size_t instance = 0U; instance < coordinates.size(); ++instance) {
            const std::array selected{coordinates[instance]};
            float top = 777, bottom = -999;
            const auto expected = expected_vertical_phantoms(instance, glyph);
            if (!check(font.try_get_glyph_vertical_phantom_deltas(glyph, selected, count, top, bottom, scratch.spans()) &&
                top == expected.top && bottom == expected.bottom,
                "top/bottom Y pair is independent of horizontal X pair at both tuple signs and empty glyph")) return false;
            float left = 0, right = 0, advance = 0;
            if (!check(font.try_get_glyph_horizontal_phantom_deltas(glyph, selected, count, left, right, scratch.spans()) &&
                font.try_get_glyph_phantom_advance_delta(glyph, selected, count, advance, scratch.spans()) && advance == right - left,
                "legacy horizontal pair and advance API remain paired")) return false;
        }
        for (unsigned fault = 0U; fault < 11U; ++fault) {
            auto spans = scratch.spans(); std::array<std::int16_t, 2U> selected{16384, 0};
            std::span<const std::int16_t> coords(selected.data(), 1U);
            std::uint32_t supplied_count = count;
            if (fault == 0U) spans.tuple_headers = spans.tuple_headers.first(spans.tuple_headers.size() - 1U);
            if (fault == 1U) spans.region_coordinates = spans.region_coordinates.first(spans.region_coordinates.size() - 1U);
            if (fault == 2U) spans.shared_point_numbers = spans.shared_point_numbers.first(spans.shared_point_numbers.size() - 1U);
            if (fault == 3U) spans.private_point_numbers = spans.private_point_numbers.first(spans.private_point_numbers.size() - 1U);
            if (fault == 4U) spans.x_deltas = spans.x_deltas.first(spans.x_deltas.size() - 1U);
            if (fault == 5U) spans.y_deltas = spans.y_deltas.first(spans.y_deltas.size() - 1U);
            if (fault == 6U) coords = {};
            if (fault == 7U) coords = selected;
            if (fault == 8U) selected[0] = 16385;
            if (fault == 9U) selected[0] = -16385;
            if (fault == 10U) ++supplied_count;
            float top = 777, bottom = -999;
            if (!check(!font.try_get_glyph_vertical_phantom_deltas(glyph, coords, supplied_count, top, bottom, spans) &&
                top == 777 && bottom == -999, "scratch/coordinate/item-count failures leave both outputs unchanged")) return false;
        }
    }
    for (unsigned fault = 0U; fault < 6U; ++fault) {
        auto bad = bytes; const auto entry = record(bad, 0x67766172U);
        const auto at = wire::read32(bad, entry + 8U);
        if (fault == 0U) wire::put32(bad, at, 0x00020000U);
        if (fault == 1U) wire::put16(bad, at + 14U, 3U);
        if (fault == 2U) wire::put32(bad, entry + 8U, 0xFFFFFFF0U);
        if (fault == 3U) wire::put32(bad, entry + 12U, 19U);
        if (fault == 4U) wire::put32(bad, record(bad, 0x6E616D65U), 0x67766172U);
        if (fault == 5U) wire::put32(bad, entry + 12U, wire::read32(bad, entry + 12U) - 1U);
        text::sfnt_font_view invalid;
        if (!text::sfnt_font_view::try_create(bad, 0U, invalid)) return false;
        std::array<text::sfnt_gvar_tuple_header, 2U> headers{};
        std::array<std::int16_t, 8U> regions{}, xs{}, ys{};
        std::array<std::uint32_t, 8U> shared{}, selected{};
        float top = 777, bottom = -999; const std::array<std::int16_t, 1U> coords{16384};
        if (!check(!invalid.try_get_glyph_vertical_phantom_deltas(2U, coords, 8U, top, bottom,
            {headers, regions, shared, selected, xs, ys}) && top == 777 && bottom == -999,
            "malformed present gvar never becomes absent or publishes a partial phantom pair")) return false;
    }
    return true;
}
} // namespace

bool progpu_native_direct2d_vertical_metrics_tests()
{
    return static_source_metrics() && strict_tables_and_absence() && cff_vorg_controls() && vertical_phantom_controls();
}
