#include "progpu_native_direct2d_variable_sideways_glyph_fixture.hpp"

#include <cstdio>
#include <cstring>
#include <limits>

namespace {
using namespace progpu::native::direct2d::tests;
namespace d2d = progpu::native::direct2d;
namespace compat = d2d::compat;
namespace com = progpu::native::com;
namespace wire = vertical_font_wire;

bool check(bool value, const char* message)
{
    if (!value) std::fprintf(stderr, "variable sideways glyph: %s\n", message);
    return value;
}

struct original_variable_vertical_source final {
    font_stream stream;
    font_loader loader;
    font_file file;
    font_face5 face;
    original_variable_vertical_source(vertical_font_options options, std::size_t instance)
    {
        stream.bytes = make_vertical_font(options); stream.declared_size = stream.bytes.size();
        loader.stream = &stream; file.loader = &loader; face.files = {&file}; face.declared_count = 1U;
        face.type = options.kind == vertical_font_kind::cff2_variable ? 0U : 1U;
        face.index = 0U; face.simulations = 0U; face.glyph_count = 3U; face.variable = true;
        face.axes = {{0x74686777U, vertical_font_weights.at(instance)}};
    }
};

bool boxes_match(std::span<const progpu_native_path_segment> segments,
    const std::array<compat::rectangle_f, 2U>& boxes)
{
    if (segments.size() != 8U) return false;
    for (std::size_t glyph = 0U; glyph < 2U; ++glyph) {
        const auto& box = boxes[glyph];
        const std::array<progpu_native_point, 4U> corners{{
            {box.right,box.bottom}, {box.right,box.top}, {box.left,box.top}, {box.left,box.bottom}}};
        for (std::size_t edge = 0U; edge < 4U; ++edge) {
            const auto& actual = segments[glyph*4U+edge];
            const auto a = corners[edge], b = corners[(edge+1U)%4U];
            if (actual.kind != PROGPU_NATIVE_PATH_SEGMENT_LINE || actual.p0.x != a.x || actual.p0.y != a.y ||
                actual.p1.x != b.x || actual.p1.y != b.y) return false;
        }
    }
    return true;
}

bool captured_instances_and_caches()
{
    com::pointer<compat::factory> factory;
    if (compat::create_factory(factory.put()) != com::ok) return false;
    const d2d::original_glyph_target frame{com::pointer<com::unknown>(factory.get()), 1U,
        {0,1,-1,0,48,0}, {4,20}, {64U,64U}, 144,120,{},compat::text_antialias_mode::grayscale};
    rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
    const std::uint16_t indices[]{1U,0U,2U};
    const float advances[]{16,-3,9};
    const compat::glyph_offset offsets[]{{0.25F,0.5F},{0,0},{-0.75F,2.5F}};
    for (const auto options : variable_sideways_pixel_fonts) for (std::size_t instance = 0U; instance < 5U; ++instance) {
        original_variable_vertical_source original(options, instance);
        std::shared_ptr<const d2d::original_font_capture> source;
        std::shared_ptr<d2d::prepared_original_font> font;
        if (d2d::capture_original_font(&original.face,source) != com::ok ||
            d2d::prepared_original_font::create(source,font) != com::ok) return false;
        const auto reads = original.stream.reads, axis_reads = original.face.value_reads;
        original.face.axes[0].value = 123; original.stream.bytes.assign(1U,std::byte{0xFF});
        compat::glyph_run run{&original.face,15.625F,3U,indices,advances,offsets,0,2U};
        std::shared_ptr<const d2d::original_glyph_request> request;
        std::shared_ptr<const d2d::prepared_original_glyph_run> prepared;
        if (d2d::capture_original_glyph_request(source,run,compat::measuring_mode::natural,&parameters,frame,request) != com::ok ||
            font->prepare(request,prepared) != com::ok) return false;
        const auto horizontal = prepared;
        for (const bool nominal : {false,true}) for (const auto sideways : {1,-1}) {
            run.is_sideways = sideways; run.glyph_advances = nominal ? nullptr : advances;
            if (!check(d2d::capture_original_glyph_request(source,run,compat::measuring_mode::natural,&parameters,frame,request) == com::ok &&
                font->prepare(request,prepared) == com::ok && boxes_match(prepared->segments(),
                    variable_sideways_pixel_rectangles(options,instance,nominal)) &&
                prepared->request().sideways == sideways && prepared->request().bidi_level == 2U &&
                (prepared->request().glyphs.advances() == nullptr) == nominal &&
                prepared->request().target.dpi_x == 144 && prepared->request().target.dpi_y == 120 &&
                prepared->request().target.transform.m12 == 1 && font->cached_glyph_count() == 3U,
                "original axes, varied horizontal/vertical origins, signed/null advances and retained outer frame")) return false;
            const auto first = prepared;
            if (!check(font->prepare(request,prepared) == com::ok && boxes_match(prepared->segments(),
                variable_sideways_pixel_rectangles(options,instance,nominal)) &&
                std::memcmp(first->segments().data(),prepared->segments().data(),prepared->segments().size_bytes()) == 0,
                "warm vertical metric/outline cache replays the same immutable design values")) return false;
        }
        run.is_sideways = 0; run.glyph_advances = advances;
        if (!check(d2d::capture_original_glyph_request(source,run,compat::measuring_mode::natural,&parameters,frame,request) == com::ok &&
            font->prepare(request,prepared) == com::ok && prepared->segments().size_bytes() == horizontal->segments().size_bytes() &&
            std::memcmp(prepared->segments().data(),horizontal->segments().data(),prepared->segments().size_bytes()) == 0 &&
            original.stream.reads == reads && original.face.value_reads == axis_reads && original.face.outline_calls == 0U &&
            original.face.table_calls == 0U, "lazy vertical ownership never changes horizontal cache or calls source again")) return false;
        const std::uint16_t repeated_indices[]{1U,0U,2U,1U};
        const float repeated_advances[]{16,-3,-13,0};
        const compat::glyph_offset repeated_offsets[]{{0.25F,0.5F},{0,0},{-0.75F,2.5F},{0.25F,0.5F}};
        const compat::glyph_run repeated{&original.face,15.625F,4U,repeated_indices,repeated_advances,repeated_offsets,1,2U};
        if (!check(d2d::capture_original_glyph_request(source,repeated,compat::measuring_mode::natural,&parameters,frame,request) == com::ok &&
            font->prepare(request,prepared) == com::ok && prepared->segments().size() == 12U &&
            std::memcmp(prepared->segments().data(),prepared->segments().data()+8U,4U*sizeof(progpu_native_path_segment)) == 0 &&
            font->cached_glyph_count() == 3U, "repeated glyph keeps both logical draws and reuses one immutable metric/outline owner")) return false;
        const auto retained = prepared;
        run.is_sideways = 1; run.bidi_level = 3U;
        if (!check(d2d::capture_original_glyph_request(source,run,compat::measuring_mode::natural,&parameters,frame,request) == com::ok &&
            font->prepare(request,prepared) == compat::not_implemented && prepared == retained && font->cached_glyph_count() == 3U,
            "combined sideways/RTL remains an explicit atomic gate")) return false;
    }
    return true;
}

std::size_t table_record(const wire::bytes& bytes, std::uint32_t tag)
{
    for (std::size_t item = 0U; item < wire::read16(bytes,4U); ++item) {
        const auto at = 12U+item*16U;
        if (wire::read32(bytes,at) == tag) return at;
    }
    throw std::invalid_argument("authored variable vertical table missing");
}

bool precedence_and_late_failure()
{
    auto options = variable_sideways_pixel_fonts[3]; options.compact_metrics = false;
    options.vvar_precedence_discriminator = true;
    original_variable_vertical_source original(options,2U);
    std::shared_ptr<const d2d::original_font_capture> source;
    if (d2d::capture_original_font(&original.face,source) != com::ok) return false;
    com::pointer<compat::factory> factory;
    if (compat::create_factory(factory.put()) != com::ok) return false;
    d2d::original_glyph_target frame{com::pointer<com::unknown>(factory.get()),1U,
        {1,0,0,1,0,0},{4,20},{64U,64U},96,96,{},compat::text_antialias_mode::grayscale};
    rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
    const std::uint16_t indices[]{1U,0U,2U};
    const compat::glyph_offset offsets[]{{0.25F,0.5F},{0,0},{-0.75F,2.5F}};
    for (const bool has_tsb_map : {true,false}) {
        auto chosen = std::make_shared<d2d::original_font_capture>(*source);
        if (!has_tsb_map) {
            auto& bytes = chosen->files[0]; const auto vvar = wire::read32(bytes,table_record(bytes,0x56564152U)+8U);
            wire::put32(bytes,vvar+12U,0U);
        }
        std::shared_ptr<d2d::prepared_original_font> font;
        if (d2d::prepared_original_font::create(chosen,font) != com::ok) return false;
        compat::glyph_run run{&original.face,15.625F,1U,indices,nullptr,offsets,0,2U};
        std::shared_ptr<const d2d::original_glyph_request> request;
        std::shared_ptr<const d2d::prepared_original_glyph_run> prepared;
        if (d2d::capture_original_glyph_request(chosen,run,compat::measuring_mode::natural,&parameters,frame,request) != com::ok ||
            font->prepare(request,prepared) != com::ok) return false;
        const auto before = prepared;
        run.is_sideways = 1; run.glyph_count = 3U;
        const compat::glyph_offset bad_offsets[]{{0,0},{0,0},{std::numeric_limits<float>::max(),0}};
        run.glyph_offsets = bad_offsets; frame.baseline.x = std::numeric_limits<float>::max();
        if (!check(d2d::capture_original_glyph_request(chosen,run,compat::measuring_mode::natural,&parameters,frame,request) == com::ok &&
            font->prepare(request,prepared) == com::invalid_argument && prepared == before && font->cached_glyph_count() == 1U,
            "late placement failure publishes neither vertical owner nor new outline entries")) return false;
        frame.baseline.x = 4; run.glyph_offsets = offsets;
        const std::array<compat::rectangle_f,2U> expected = has_tsb_map
            ? std::array<compat::rectangle_f,2U>{{{7.125F,19,14.125F,24.125F},{37.75F,20,46.5625F,23.875F}}}
            : std::array<compat::rectangle_f,2U>{{{6.75F,19,13.75F,24.125F},{38,20,46.8125F,23.875F}}};
        if (!check(d2d::capture_original_glyph_request(chosen,run,compat::measuring_mode::natural,&parameters,frame,request) == com::ok &&
            font->prepare(request,prepared) == com::ok && boxes_match(prepared->segments(),expected) && font->cached_glyph_count() == 3U,
            "VVAR advance wins over gvar; mapped TSB or original top phantom supplies the selected origin")) return false;
        const auto retained = prepared;
        std::shared_ptr<d2d::prepared_original_font> foreign;
        auto equal_bytes = std::make_shared<d2d::original_font_capture>(*chosen);
        if (!check(d2d::prepared_original_font::create(equal_bytes,foreign) == com::ok &&
            foreign->prepare(request,prepared) == com::invalid_argument && prepared == retained && foreign->cached_glyph_count() == 0U,
            "equal file/axis bytes never replace exact original source owner identity")) return false;
    }
    return true;
}

bool malformed_vvar_stays_lazy()
{
    original_variable_vertical_source original(variable_sideways_pixel_fonts[3],2U);
    std::shared_ptr<const d2d::original_font_capture> source;
    if (d2d::capture_original_font(&original.face,source) != com::ok) return false;
    com::pointer<compat::factory> factory;
    if (compat::create_factory(factory.put()) != com::ok) return false;
    const d2d::original_glyph_target frame{com::pointer<com::unknown>(factory.get()),1U,
        {1,0,0,1,0,0},{4,20},{64U,64U},96,96,{},compat::text_antialias_mode::grayscale};
    rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
    const std::uint16_t glyph = 1U;
    for (unsigned fault = 0U; fault < 7U; ++fault) {
        auto bad = std::make_shared<d2d::original_font_capture>(*source);
        auto& bytes = bad->files[0]; const auto entry = table_record(bytes,0x56564152U);
        const auto vvar = wire::read32(bytes,entry+8U);
        if (fault == 0U) wire::put32(bytes,entry+12U,23U);
        if (fault == 1U) wire::put32(bytes,vvar,0x00020000U);
        if (fault == 2U) wire::put32(bytes,entry+8U,0xFFFFFFF0U);
        if (fault == 3U) wire::put32(bytes,vvar+4U,0U);
        if (fault == 4U) wire::put32(bytes,vvar+12U,0xFFFFFFF0U);
        if (fault == 5U) wire::put32(bytes,table_record(bytes,0x6E616D65U),0x56564152U);
        if (fault == 6U) {
            const auto map = vvar+wire::read32(bytes,vvar+8U); bytes[map+4U] = std::byte{0xFF};
        }
        std::shared_ptr<d2d::prepared_original_font> font;
        if (!check(d2d::prepared_original_font::create(bad,font) == com::ok,
            "unused malformed VVAR does not change horizontal creation")) return false;
        compat::glyph_run run{&original.face,15.625F,1U,&glyph,nullptr,nullptr,0,2U};
        std::shared_ptr<const d2d::original_glyph_request> request;
        std::shared_ptr<const d2d::prepared_original_glyph_run> prepared;
        if (d2d::capture_original_glyph_request(bad,run,compat::measuring_mode::natural,&parameters,frame,request) != com::ok ||
            font->prepare(request,prepared) != com::ok) return false;
        const auto retained = prepared; run.is_sideways = 1;
        if (!check(d2d::capture_original_glyph_request(bad,run,compat::measuring_mode::natural,&parameters,frame,request) == com::ok &&
            font->prepare(request,prepared) == com::invalid_argument && prepared == retained && font->cached_glyph_count() == 1U,
            "actual sideways request rejects malformed VVAR atomically without damaging horizontal cache")) return false;
    }
    return true;
}
} // namespace

bool progpu_native_direct2d_variable_sideways_glyph_tests()
{
    return captured_instances_and_caches() && precedence_and_late_failure() && malformed_vvar_stays_lazy();
}
