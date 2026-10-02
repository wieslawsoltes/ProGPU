#include "progpu_native_direct2d_cff_glyph_fixture.hpp"
#include "../src/Direct2D/progpu_native_direct2d_cff_source.hpp"

#include <bit>
#include <cstdio>
#include <cstring>
#include <limits>
#include <stdexcept>

namespace {
using namespace progpu::native::direct2d::tests;
namespace d2d = progpu::native::direct2d;
namespace compat = d2d::compat;
namespace com = progpu::native::com;
namespace text = progpu::native::text;

bool check(bool value, const char* message)
{
    if (!value) std::fprintf(stderr, "prepared CFF glyph: %s\n", message);
    return value;
}

std::uint32_t read32(std::span<const std::byte> bytes, std::size_t offset)
{
    return (std::to_integer<std::uint32_t>(bytes[offset]) << 24U) |
        (std::to_integer<std::uint32_t>(bytes[offset + 1U]) << 16U) |
        (std::to_integer<std::uint32_t>(bytes[offset + 2U]) << 8U) |
        std::to_integer<std::uint32_t>(bytes[offset + 3U]);
}

void put32(std::vector<std::byte>& bytes, std::size_t offset, std::uint32_t value)
{
    for (unsigned index = 0U; index < 4U; ++index)
        bytes[offset + index] = static_cast<std::byte>((value >> (24U - index * 8U)) & 255U);
}

std::size_t table_record(std::span<const std::byte> bytes, std::uint32_t tag)
{
    const auto count = (std::to_integer<unsigned>(bytes[4]) << 8U) | std::to_integer<unsigned>(bytes[5]);
    for (unsigned index = 0U; index < count; ++index) {
        const auto offset = 12U + static_cast<std::size_t>(index) * 16U;
        if (read32(bytes, offset) == tag) return offset;
    }
    throw std::invalid_argument("authored CFF fixture table missing");
}

struct original_cff_source final {
    font_stream stream;
    font_loader loader;
    font_file file;
    font_face5 face;
    explicit original_cff_source(cff_font_kind kind, std::size_t instance = 0U)
    {
        stream.bytes = make_cff_font(kind); stream.declared_size = stream.bytes.size();
        loader.stream = &stream; file.loader = &loader; face.files = {&file}; face.declared_count = 1U;
        face.type = 0U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
        face.variable = cff_font_case_count(kind) != 1U;
        face.axes = {{0x74686777U, cff_font_weight(instance)}};
    }
    com::result capture(std::shared_ptr<const d2d::original_font_capture>& output)
    {
        return d2d::capture_original_font(&face, output);
    }
};

bool matrix_metadata()
{
    // Literal DICT: [1 0 0 1 8 16] FontMatrix. The expected composition is
    // authored arithmetic, not read back from a prepared glyph.
    std::vector<std::byte> bytes{std::byte{140}, std::byte{139}, std::byte{139}, std::byte{140},
        std::byte{147}, std::byte{155}, std::byte{12}, std::byte{7}};
    d2d::detail::cff_source_dictionary parsed{};
    if (!check(d2d::detail::read_cff_source_dictionary(bytes, parsed) == com::ok && parsed.has_matrix &&
        parsed.matrix.m11 == 1 && parsed.matrix.m12 == 0 && parsed.matrix.m21 == 0 &&
        parsed.matrix.m22 == 1 && parsed.matrix.dx == 8 && parsed.matrix.dy == 16,
        "actual top/FD dictionary matrix survives parsing")) return false;
    const auto composed = d2d::detail::compose_cff_design_matrix(parsed.matrix,
        {1.0 / 1024, 0, 1.0 / 4096, 1.0 / 1024, 1.0 / 64, 0}, 1024);
    if (!check(composed.m11 == 1 && composed.m12 == 0 && composed.m21 == 0.25 && composed.m22 == 1 &&
        composed.dx == 28 && composed.dy == 16, "FD precedes top shear and top translation")) return false;
    const auto retained = parsed;
    for (unsigned fault = 0U; fault < 7U; ++fault) {
        auto bad = bytes;
        if (fault == 0U) bad.insert(bad.end(), bytes.begin(), bytes.end());
        if (fault == 1U) bad.pop_back();
        if (fault == 2U) bad.erase(bad.begin());
        if (fault == 3U) bad.insert(bad.end(), {std::byte{140}, std::byte{12}, std::byte{5}}); // stroked
        if (fault == 4U) bad.insert(bad.end(), {std::byte{140}, std::byte{12}, std::byte{6}}); // Type1
        if (fault == 5U) bad.insert(bad.end(), {std::byte{139}, std::byte{12}, std::byte{20}}); // synthetic
        if (fault == 6U) bad.insert(bad.end(), {std::byte{30}, std::byte{0x1B}, std::byte{0x99}, std::byte{0x99}, std::byte{0xFF}});
        if (!check(com::failed(d2d::detail::read_cff_source_dictionary(bad, parsed)) &&
            parsed.has_matrix == retained.has_matrix && parsed.matrix.dx == retained.matrix.dx &&
            parsed.matrix.dy == retained.matrix.dy, "unsupported/malformed matrix dictionary keeps output atomic")) return false;
    }
    return true;
}

bool family_and_axis_admission()
{
    original_cff_source original(cff_font_kind::cff2_variable_hvar, 1U);
    std::shared_ptr<const d2d::original_font_capture> source;
    std::shared_ptr<d2d::prepared_original_font> font;
    if (original.capture(source) != com::ok || d2d::prepared_original_font::create(source, font) != com::ok) return false;
    const auto retained = font;
    for (unsigned fault = 0U; fault < 14U; ++fault) {
        auto bad = std::make_shared<d2d::original_font_capture>(*source);
        if (fault == 0U) bad->face_type = 1U;
        if (fault == 1U) bad->axis_values_available = false;
        if (fault == 2U) bad->has_variations = false;
        if (fault == 3U) bad->axis_values.clear();
        if (fault == 4U) bad->axis_values.push_back(bad->axis_values[0]);
        if (fault == 5U) bad->axis_values[0].value = std::numeric_limits<float>::infinity();
        auto& bytes = bad->files[0];
        if (fault == 6U) put32(bytes, 0U, 0x00010000U); // not OTTO
        const auto hvar_record = table_record(bytes, 0x48564152U);
        const auto hvar = read32(bytes, hvar_record + 8U);
        if (fault == 7U) put32(bytes, hvar + 4U, 0U);
        if (fault == 8U) put32(bytes, hvar_record + 8U, 0xFFFFFFF0U);
        if (fault == 9U) put32(bytes, hvar, 0x00020000U);
        if (fault == 10U) put32(bytes, hvar_record, 0x676C7966U); // competing glyf must not win
        if (fault == 11U) put32(bytes, hvar_record, 0x43464620U); // conflicting CFF1
        if (fault == 12U) put32(bytes, table_record(bytes, 0x66766172U), 0x78787878U); // HVAR without fvar
        if (fault == 13U) put32(bytes, hvar_record, 0x67766172U); // never gvar fallback
        if (!check(com::failed(d2d::prepared_original_font::create(bad, font)) && font == retained &&
            font->cached_glyph_count() == 0U, "CFF family/axes/metrics rejection preserves original owner")) return false;
    }
    // A real optional source axis interface can include static descriptors;
    // only fvar axes drive the CFF2 blend generation.
    auto extended = std::make_shared<d2d::original_font_capture>(*source);
    extended->axis_values.insert(extended->axis_values.begin(), {0x68746477U, 100});
    if (!check(d2d::prepared_original_font::create(extended, font) == com::ok && font->source() == extended,
        "CFF2 retains extra standard static source descriptors")) return false;
    original_cff_source collection(cff_font_kind::cff2_variable_fixed, 2U);
    const auto sfnt = collection.stream.bytes;
    collection.stream.bytes.assign(16U + sfnt.size(), std::byte{0});
    auto& bytes = collection.stream.bytes;
    std::copy(sfnt.begin(), sfnt.end(), bytes.begin() + 16U);
    put32(bytes, 0U, 0x74746366U); put32(bytes, 4U, 0x00010000U); put32(bytes, 8U, 1U); put32(bytes, 12U, 16U);
    const auto table_count = (std::to_integer<unsigned>(sfnt[4]) << 8U) | std::to_integer<unsigned>(sfnt[5]);
    for (unsigned index = 0U; index < table_count; ++index) {
        const auto record = 28U + static_cast<std::size_t>(index) * 16U;
        put32(bytes, record + 8U, read32(bytes, record + 8U) + 16U);
    }
    collection.stream.declared_size = bytes.size(); collection.face.type = 2U;
    if (!check(collection.capture(source) == com::ok && d2d::prepared_original_font::create(source, font) == com::ok &&
        font->source()->face_type == 2U && font->source()->face_index == 0U,
        "original collection retains selected CFF2 face and file-relative table offsets")) return false;
    return true;
}

bool transformed_decode_atomicity()
{
    const auto bytes = make_cff_font(cff_font_kind::cff1_affine);
    text::sfnt_font_view font;
    text::sfnt_cff1_font_view cff{};
    if (!text::sfnt_font_view::try_create(bytes, 0U, font) || !font.try_get_cff1_font(3U, cff)) return false;
    std::array<progpu_native_path_segment, 4U> segments{};
    std::uint32_t written = 777U;
    const auto raw = expected_cff_glyph(cff_font_kind::cff1_default, 0U, 1U);
    if (!check(text::sfnt_cff_data::try_decode_outline(cff, 1U, segments, written) && written == raw.count &&
        std::memcmp(segments.data(), raw.segments.data(), sizeof(segments)) == 0,
        "old raw CFF reader remains in untransformed charstring coordinates")) return false;
    const auto unchanged = segments;
    const text::sfnt_cff_outline_transform valid{1, 0, 0.25, 1, 16, 0};
    if (!check(!text::sfnt_cff_data::try_decode_outline(cff, 1U, valid,
            std::span<progpu_native_path_segment>(segments).first(3U), written) && written == 0U &&
        std::memcmp(segments.data(), unchanged.data(), sizeof(segments)) == 0,
        "insufficient transformed output leaves the complete caller buffer untouched")) return false;
    for (unsigned fault = 0U; fault < 2U; ++fault) {
        auto invalid = valid;
        invalid.dx = fault == 0U ? std::numeric_limits<double>::infinity() : std::numeric_limits<double>::max();
        if (!check(!text::sfnt_cff_data::try_decode_outline(cff, 1U, invalid, segments, written) && written == 0U &&
            std::memcmp(segments.data(), unchanged.data(), sizeof(segments)) == 0,
            "invalid/overflowed source matrix preflight publishes no path prefix")) return false;
    }
    const auto mapped = expected_cff_glyph(cff_font_kind::cff1_affine, 0U, 1U);
    if (!check(text::sfnt_cff_data::try_decode_outline(cff, 1U, valid, segments, written) && written == mapped.count &&
        std::memcmp(segments.data(), mapped.segments.data(), sizeof(segments)) == 0,
        "additive source transform owns every point and closing edge")) return false;
    return true;
}

bool full_design_runs_and_atomicity()
{
    com::pointer<compat::factory> factory;
    com::pointer<compat::scene_factory_native> scenes;
    com::pointer<compat::render_target> target;
    const compat::scene_render_target_properties properties{64U, 64U, 96, 96, 0x95D9U, 1U};
    if (compat::create_factory(factory.put()) != com::ok ||
        factory.as(compat::scene_factory_native_interface_id, scenes) != com::ok ||
        scenes->CreateSceneRenderTarget(&properties, target.put()) != com::ok) return false;
    rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
    const d2d::original_glyph_target frame{com::pointer<com::unknown>(target.get()), 1U,
        {1, 0, 0, 1, 0, 0}, {0, 0}, {64U, 64U}, 96, 96, {}, compat::text_antialias_mode::aliased};
    for (const auto kind : cff_pixel_fonts) {
        for (std::size_t instance = 0U; instance < cff_font_case_count(kind); ++instance) {
            original_cff_source original(kind, instance);
            std::shared_ptr<const d2d::original_font_capture> source;
            std::shared_ptr<d2d::prepared_original_font> font;
            if (original.capture(source) != com::ok || d2d::prepared_original_font::create(source, font) != com::ok) return false;
            const std::uint16_t indices[]{0U, 1U, 2U};
            const float explicit_advances[]{-7, 0, 19};
            for (const bool nominal : {false, true}) {
                const compat::glyph_run run{&original.face, static_cast<float>(cff_font_units(kind)), 3U, indices,
                    nominal ? nullptr : explicit_advances, nullptr, 0, 2U};
                std::shared_ptr<const d2d::original_glyph_request> request;
                std::shared_ptr<const d2d::prepared_original_glyph_run> prepared;
                if (d2d::capture_original_glyph_request(source, run, compat::measuring_mode::natural, &parameters, frame, request) != com::ok ||
                    font->prepare(request, prepared) != com::ok) return false;
                std::vector<progpu_native_path_segment> expected;
                float pen = 0;
                for (std::size_t occurrence = 0U; occurrence < 3U; ++occurrence) {
                    const auto glyph = expected_cff_glyph(kind, instance, indices[occurrence]);
                    for (std::uint32_t index = 0U; index < glyph.count; ++index) {
                        auto segment = glyph.segments[index];
                        const auto point = [&](progpu_native_point p) { return progpu_native_point{p.x + pen, -p.y}; };
                        segment.p0 = point(segment.p0); segment.p1 = point(segment.p1);
                        if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
                            segment.p2 = point(segment.p2); segment.p3 = point(segment.p3);
                        }
                        expected.push_back(segment);
                    }
                    pen += nominal ? glyph.advance : explicit_advances[occurrence];
                }
                if (!check(prepared->segments().size() == expected.size() &&
                    std::memcmp(prepared->segments().data(), expected.data(), expected.size() * sizeof(expected[0])) == 0 &&
                    font->cached_glyph_count() == 3U && (prepared->request().glyphs.advances() == nullptr) == nominal,
                    "literal CFF cubic points+FD frames+fixed/varied metrics preserve complete source run")) return false;
                const auto retained = prepared;
                auto other = std::make_shared<d2d::original_font_capture>(*source);
                std::shared_ptr<d2d::prepared_original_font> other_font;
                if (!check(d2d::prepared_original_font::create(other, other_font) == com::ok &&
                    other_font->prepare(request, prepared) == com::invalid_argument && prepared == retained &&
                    other_font->cached_glyph_count() == 0U, "equal CFF bytes never substitute source owner")) return false;
            }

            // A malformed later glyph does not publish the earlier empty glyph
            // or replace a previously prepared/cached valid glyph.
            auto bad = std::make_shared<d2d::original_font_capture>(*source);
            text::sfnt_font_view parsed;
            if (!text::sfnt_font_view::try_create(bad->files[0], 0U, parsed)) return false;
            text::sfnt_cff_index_view strings{};
            text::sfnt_cff1_font_view cff1{};
            text::sfnt_cff2_font_view cff2{};
            if (parsed.try_get_cff1_font(3U, cff1)) strings = cff1.char_strings;
            else if (parsed.try_get_cff2_font(3U, cff2)) strings = cff2.char_strings;
            else return false;
            std::span<const std::byte> glyph2;
            if (!text::sfnt_cff_data::try_get_index_item(strings, 2U, glyph2) || glyph2.empty()) return false;
            bad->files[0][static_cast<std::size_t>(glyph2.data() - bad->files[0].data())] = std::byte{0};
            if (d2d::prepared_original_font::create(bad, font) != com::ok) return false;
            const std::uint16_t first[]{1U};
            compat::glyph_run run{&original.face, 32, 1U, first, nullptr, nullptr, 0, 2U};
            std::shared_ptr<const d2d::original_glyph_request> request;
            std::shared_ptr<const d2d::prepared_original_glyph_run> prepared;
            if (d2d::capture_original_glyph_request(bad, run, compat::measuring_mode::natural, &parameters, frame, request) != com::ok ||
                font->prepare(request, prepared) != com::ok) return false;
            const auto retained = prepared;
            run.glyph_indices = indices; run.glyph_count = 3U;
            if (!check(d2d::capture_original_glyph_request(bad, run, compat::measuring_mode::natural, &parameters, frame, request) == com::ok &&
                font->prepare(request, prepared) == com::invalid_argument && prepared == retained && font->cached_glyph_count() == 1U,
                "late Type2 error keeps whole-run output/cache atomic")) return false;
        }
    }
    return true;
}
} // namespace

bool progpu_native_direct2d_cff_glyph_tests()
{
    return matrix_metadata() && family_and_axis_admission() && transformed_decode_atomicity() && full_design_runs_and_atomicity();
}
