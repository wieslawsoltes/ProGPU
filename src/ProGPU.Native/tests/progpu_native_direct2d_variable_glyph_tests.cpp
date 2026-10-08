#include "progpu_native_direct2d_variable_glyph_fixture.hpp"
#include "progpu_native_text.hpp"

#include <bit>
#include <cstdio>
#include <limits>

namespace {
using namespace progpu::native::direct2d::tests;
namespace d2d = progpu::native::direct2d;
namespace compat = d2d::compat;
namespace com = progpu::native::com;
namespace text = progpu::native::text;

bool check(bool value, const char* message)
{
    if (!value) std::fprintf(stderr, "prepared variable glyph: %s\n", message);
    return value;
}

struct original_variable_source final {
    font_stream stream;
    font_loader loader;
    font_file file;
    font_face5 face;
    explicit original_variable_source(variable_font_options options = {}, float coordinate = 650.0F)
    {
        stream.bytes = make_variable_font(options); stream.declared_size = stream.bytes.size();
        loader.stream = &stream; file.loader = &loader; face.files = {&file}; face.declared_count = 1U;
        face.type = 1U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
        face.axes = {{0x74686777U, coordinate}};
    }
    com::result capture(std::shared_ptr<const d2d::original_font_capture>& result)
    {
        return d2d::capture_original_font(&face, result);
    }
};

std::size_t table_offset(const std::vector<std::byte>& bytes, std::uint32_t tag)
{
    using namespace variable_font_wire;
    for (std::size_t index = 0U; index < read16(bytes, 4U); ++index) {
        const auto record = 12U + index * 16U;
        if (read32(bytes, record) == tag) return read32(bytes, record + 8U);
    }
    throw std::invalid_argument("authored table missing");
}

bool axis_and_table_admission()
{
    original_variable_source original({true, true, true});
    // Both stack-owned COM fixtures must outlive every captured/prepared owner,
    // including the later static source that replaces these output handles.
    original_variable_source static_original;
    std::shared_ptr<const d2d::original_font_capture> source;
    std::shared_ptr<d2d::prepared_original_font> font;
    if (!check(original.capture(source) == com::ok && d2d::prepared_original_font::create(source, font) == com::ok,
        "real capture followed by paired variable preparation")) return false;
    const auto retained = font;
    for (unsigned fault = 0U; fault < 16U; ++fault) {
        auto bad = std::make_shared<d2d::original_font_capture>(*source);
        if (fault == 0U) bad->axis_values_available = false;
        if (fault == 1U) bad->has_variations = false;
        if (fault == 2U) bad->axis_values.clear();
        if (fault == 3U) bad->axis_values.push_back(bad->axis_values[0]);
        if (fault == 4U) bad->axis_values[0].tag = 0x77676874U; // wrong byte order is not wght
        if (fault == 5U) bad->axis_values[0].value = std::numeric_limits<float>::quiet_NaN();
        if (fault == 6U) bad->axis_values.push_back({0x544D5343U, 1}); // unknown non-fvar CSMT
        auto& bytes = bad->files[0];
        if (fault == 7U) variable_font_wire::put16(bytes, table_offset(bytes, 0x66766172U), 2U);
        if (fault == 8U) variable_font_wire::put16(bytes, table_offset(bytes, 0x61766172U), 2U);
        if (fault == 9U) variable_font_wire::put16(bytes, table_offset(bytes, 0x61766172U) + 6U, 2U);
        if (fault == 10U) variable_font_wire::put16(bytes, table_offset(bytes, 0x61766172U) + 18U, 0U);
        if (fault == 11U) variable_font_wire::put32(bytes, table_offset(bytes, 0x48564152U) + 12U, 0xFFFFFFF0U);
        if (fault == 12U) variable_font_wire::put16(bytes, table_offset(bytes, 0x67766172U) + 4U, 2U);
        if (fault == 13U) variable_font_wire::put16(bytes, table_offset(bytes, 0x66766172U) + 8U, 0U);
        if (fault == 14U) variable_font_wire::put16(bytes, table_offset(bytes, 0x67766172U), 2U);
        if (fault == 15U) variable_font_wire::put16(bytes, table_offset(bytes, 0x67766172U) + 14U, 2U);
        if (!check(com::failed(d2d::prepared_original_font::create(bad, font)) && font == retained &&
            font->cached_glyph_count() == 0U, "later source/table rejection preserves original prepared owner/cache")) return false;
    }
    // The SDK source list is NOT the fvar list: preserve five documented static
    // attributes while consuming only actual fvar axes, regardless of order.
    auto extended = std::make_shared<d2d::original_font_capture>(*source);
    extended->axis_values = {{0x68746477U, 100}, {0x6C617469U, 0}, {0x746E6C73U, -0.0F},
        {0x7A73706FU, 12}, {0x74686777U, 650}};
    std::shared_ptr<d2d::prepared_original_font> extended_font;
    if (!check(d2d::prepared_original_font::create(extended, extended_font) == com::ok &&
        extended_font->source() == extended && extended->axis_values.size() == 5U,
        "genuine fvar coordinate plus standard static attributes retained")) return false;
    static_original.stream.bytes = progpu::native::tests::make_hint_fault_font();
    static_original.stream.declared_size = static_original.stream.bytes.size();
    static_original.face.variable = false;
    static_original.face.axes = {{0x74686777U, 400}, {0x68746477U, 100}, {0x7A73706FU, 12}};
    if (!check(static_original.capture(source) == com::ok && source->axis_values_available && !source->has_variations &&
        d2d::prepared_original_font::create(source, font) == com::ok && font->source() == source,
        "static Face5 with nonempty design descriptors preserves ordinary static path")) return false;
    return true;
}

bool exact_float_normalization()
{
    auto bytes = make_variable_font();
    text::sfnt_font_view font;
    if (!text::sfnt_font_view::try_create(bytes, 0U, font)) return false;
    const std::array<std::int16_t, 5U> expected{0, 12288, 16384, -8192, -16384};
    for (std::size_t index = 0U; index < expected.size(); ++index) {
        std::int16_t normalized = 77;
        if (!check(font.try_normalize_variation_design_coordinate(0U, variable_font_cases[index].weight, normalized) &&
            normalized == expected[index], "independent fvar/avar positive/default/negative coordinates")) return false;
    }
    for (const auto value : {std::numeric_limits<float>::quiet_NaN(), std::numeric_limits<float>::infinity(),
            -std::numeric_limits<float>::infinity()}) {
        std::int16_t unchanged = 77;
        if (!check(!font.try_normalize_variation_design_coordinate(0U, value, unchanged) && unchanged == 77,
            "nonfinite source normalization has atomic output")) return false;
    }
    // A narrow authored axis makes premature float->16.16 rounding observable.
    // Original value = 2^-16 + 2^-26; normalized=1/2048; avar multiplies by1.5.
    const auto fvar = table_offset(bytes, 0x66766172U);
    variable_font_wire::put32(bytes, fvar + 20U, 0U);
    variable_font_wire::put32(bytes, fvar + 24U, 1U);
    variable_font_wire::put32(bytes, fvar + 28U, 3U);
    if (!text::sfnt_font_view::try_create(bytes, 0U, font)) return false;
    std::int16_t exact = 77, legacy = 77;
    if (!check(font.try_normalize_variation_design_coordinate(0U, 0.00001527369022369384765625F, exact) && exact == 12 &&
        font.try_normalize_variation_coordinate(0U, 1, legacy) && legacy == 0,
        "captured float is not narrowed through legacy16.16")) return false;
    variable_font_wire::put16(bytes, table_offset(bytes, 0x61766172U), 2U);
    exact = 77;
    if (!check(!font.try_normalize_variation_design_coordinate(0U, 0.00001527369022369384765625F, exact) && exact == 77,
        "unsupported avar version preserves output")) return false;
    return true;
}

bool prepared_run_atomicity_and_identity()
{
    com::pointer<compat::factory> factory;
    com::pointer<compat::scene_factory_native> scene_factory;
    com::pointer<compat::render_target> target;
    const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95D6U, 1U};
    if (compat::create_factory(factory.put()) != com::ok ||
        factory.as(compat::scene_factory_native_interface_id, scene_factory) != com::ok ||
        scene_factory->CreateSceneRenderTarget(&properties, target.put()) != com::ok) return false;
    rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
    const d2d::original_glyph_target frame{com::pointer<com::unknown>(target.get()), 1U,
        {1, 0, 0, 1, 0, 0}, {4, 28}, {64U, 64U}, 96, 96, {}, compat::text_antialias_mode::aliased};
    for (const auto& options : variable_pixel_font_options) {
        original_variable_source original(options);
        // Corrupt only glyph2's late tuple payload. First glyph and all global
        // tables remain valid; no cache from this failed run may be published.
        const auto gvar = table_offset(original.stream.bytes, 0x67766172U);
        const auto glyph2 = gvar + variable_font_wire::read32(original.stream.bytes, gvar + 16U) +
            variable_font_wire::read32(original.stream.bytes, gvar + 28U);
        variable_font_wire::put16(original.stream.bytes, glyph2 + 4U, 0xFFFFU);
        std::shared_ptr<const d2d::original_font_capture> source;
        std::shared_ptr<d2d::prepared_original_font> font;
        if (original.capture(source) != com::ok || d2d::prepared_original_font::create(source, font) != com::ok) return false;
        const auto reads = original.stream.reads, axes = original.face.value_reads;
        std::uint16_t indices[]{1U, 0U, 2U};
        compat::glyph_run run{&original.face, 31.25F, 1U, indices, nullptr, nullptr, 0, 2U};
        std::shared_ptr<const d2d::original_glyph_request> request;
        std::shared_ptr<const d2d::prepared_original_glyph_run> prepared;
        if (d2d::capture_original_glyph_request(source, run, compat::measuring_mode::natural, &parameters, frame, request) != com::ok ||
            font->prepare(request, prepared) != com::ok) return false;
        const auto retained = prepared;
        run.glyph_count = 3U;
        if (!check(d2d::capture_original_glyph_request(source, run, compat::measuring_mode::natural, &parameters, frame, request) == com::ok &&
            font->prepare(request, prepared) == com::invalid_argument && prepared == retained && font->cached_glyph_count() == 1U,
            "late gvar failure rejects whole run including earlier uncached empty-glyph metrics")) return false;
        // A distinct prepared source cannot consume another source's request,
        // even when its exact immutable bytes and axis values are identical.
        std::shared_ptr<const d2d::original_font_capture> other_source;
        std::shared_ptr<d2d::prepared_original_font> other_font;
        if (original.capture(other_source) != com::ok || d2d::prepared_original_font::create(other_source, other_font) != com::ok) return false;
        if (!check(other_font->prepare(request, prepared) == com::invalid_argument && prepared == retained &&
            other_font->cached_glyph_count() == 0U && source != other_source,
            "same bytes/coordinates never substitute a different original source owner")) return false;
        const auto final_reads = original.stream.reads, final_axes = original.face.value_reads;
        if (!check(final_reads > reads && final_axes > axes, "second owner was genuinely recaptured")) return false;
        original.face.count_result = compat::not_implemented; original.face.values_result = compat::not_implemented;
        original.face.axes[0].value = 100; original.stream.bytes.clear();
        run.glyph_count = 1U;
        if (!check(d2d::capture_original_glyph_request(source, run, compat::measuring_mode::natural, &parameters, frame, request) == com::ok &&
            font->prepare(request, prepared) == com::ok && prepared->segments().size() == 4U &&
            font->cached_glyph_count() == 1U && source->axis_values[0].value == 650 &&
            original.stream.reads == final_reads && original.face.value_reads == final_axes &&
            original.face.table_calls == 0U && original.face.outline_calls == 0U,
            "warm paired cache remains owned after failed run and mutable producer retirement")) return false;
    }
    return true;
}

bool direct_paired_metrics()
{
    for (const auto& options : variable_pixel_font_options) {
        const auto bytes = make_variable_font(options);
        text::sfnt_font_view font;
        if (!text::sfnt_font_view::try_create(bytes, 0U, font)) return false;
        for (std::size_t index = 0U; index < variable_font_cases.size(); ++index) {
            std::array<std::int16_t, 1U> coordinates{};
            if (!font.try_normalize_variation_design_coordinate(0U, variable_font_cases[index].weight, coordinates[0])) return false;
            std::uint16_t region_count = 0U; bool hvar = false;
            if (!font.try_get_horizontal_advance_variation_region_count(coordinates, region_count, hvar)) return false;
            std::vector<float> scalars(region_count);
            text::sfnt_horizontal_metrics_variation_instance variation{};
            if (!font.try_prepare_horizontal_metrics_variation(coordinates, scalars, variation)) return false;
            for (std::uint16_t glyph = 0U; glyph < 3U; ++glyph) {
                std::uint32_t items = 0U;
                text::sfnt_glyph_phantom_variation_requirements requirements{};
                if (!font.try_get_glyph_variation_item_count(glyph, items) ||
                    !font.try_get_glyph_phantom_variation_requirements(glyph, items, requirements)) return false;
                std::vector<text::sfnt_gvar_tuple_header> headers(requirements.tuple_header_count);
                std::vector<std::int16_t> regions(requirements.region_coordinate_count), x(requirements.delta_count), y(requirements.delta_count);
                std::vector<std::uint32_t> shared(requirements.point_number_count), local(requirements.point_number_count);
                const text::sfnt_glyph_phantom_variation_scratch scratch{headers, regions, shared, local, x, y};
                const auto expected = expected_variable_glyph(glyph, index);
                float left = 777, right = 888, width = 999;
                if (!check(font.try_get_glyph_horizontal_phantom_deltas(glyph, coordinates, items, left, right, scratch) &&
                    left == expected.horizontal_origin && 500.0F + (right - left) == expected.advance &&
                    font.try_get_design_advance_width(glyph, coordinates, &variation.advance, width, scratch) && width == expected.advance,
                    "independent paired phantom and HVAR precedence metrics including empty glyph")) return false;
                float lsb = 123; bool has_lsb = false;
                if (!check(font.try_get_horizontal_left_side_bearing_variation(glyph, variation, lsb, has_lsb) &&
                    has_lsb == options.side_bearing_maps && (!has_lsb || glyph == 0U ||
                        lsb == expected.x_min - expected.horizontal_origin - 13.0F),
                    "HVAR optional bearing belongs to same normalized instance")) return false;
                left = 777; right = 888;
                if (!check(!font.try_get_glyph_horizontal_phantom_deltas(glyph, coordinates, items, left, right, {}) &&
                    left == 777 && right == 888, "insufficient phantom scratch preserves both outputs")) return false;
            }
        }
    }
    return true;
}
} // namespace

bool progpu_native_direct2d_variable_glyph_tests()
{
    return axis_and_table_admission() && exact_float_normalization() &&
        direct_paired_metrics() && prepared_run_atomicity_and_identity();
}
