#pragma once

#include "progpu_native_direct2d_cff_font_fixture.hpp"

namespace progpu::native::direct2d::tests {

enum class vertical_font_kind { truetype, truetype_variable, cff, cff2_variable };
struct vertical_font_options final {
    vertical_font_kind kind = vertical_font_kind::truetype;
    bool compact_metrics = false;
    bool vvar = false;
    bool side_bearing_maps = false;
    bool origin_map = false;
    bool vorg = false;
};
inline constexpr std::array<float,5U> vertical_font_weights{400,650,900,250,100};
inline std::size_t vertical_font_case_count(vertical_font_options options)
{
    return options.kind == vertical_font_kind::truetype_variable || options.kind == vertical_font_kind::cff2_variable ? 5U : 1U;
}
struct vertical_phantom_expectation final { float top, bottom; };
inline vertical_phantom_expectation expected_vertical_phantoms(std::size_t instance, std::uint16_t glyph)
{
    static constexpr std::array<std::array<vertical_phantom_expectation,3U>,5U> values{{
        {{{0,0},{0,0},{0,0}}}, {{{12,-20},{40,-8},{16,-48}}},
        {{{24,-40},{80,-16},{32,-96}}}, {{{-6,10},{-20,4},{-8,24}}}, {{{-12,20},{-40,8},{-16,48}}}};
    return values.at(instance).at(glyph);
}
struct vertical_glyph_expectation final {
    std::array<progpu_native_path_segment, 4U> segments{};
    std::uint32_t count = 0U;
    float x_min = 0, y_min = 0, x_max = 0, y_max = 0;
    float horizontal_origin = 0, horizontal_advance = 0;
    float vertical_origin = 0, vertical_advance = 0;
    float top_side_bearing = 0, bottom_side_bearing = 0;
    bool has_vertical_origin = false;
};

inline vertical_glyph_expectation expected_vertical_glyph(vertical_font_options options,
    std::size_t instance, std::uint16_t glyph)
{
    const bool cff = options.kind == vertical_font_kind::cff;
    const bool variable = options.kind == vertical_font_kind::truetype_variable;
    if ((options.kind != vertical_font_kind::truetype && !cff && !variable) ||
        instance >= vertical_font_case_count(options) || glyph >= 3U)
        throw std::out_of_range("authored static vertical glyph inventory");
    // Literal independent metrics. Empty TT has a real advance and vmtx TSB,
    // but no glyf bbox: do not invent its top/bottom phantom origins.
    vertical_glyph_expectation result{};
    if (variable) {
        struct row final { float x0,y0,x1,y1,h_origin,h_advance,v_origin,v_advance,top,bottom; };
        static constexpr std::array<std::array<row,3U>,5U> values{{
            {{{0,0,0,0,0,480,0,900,700,0},{20,-40,300,360,0,600,500,1000,140,460},{-30,20,170,520,0,700,600,1100,80,520}}},
            {{{0,0,0,0,0,496,0,932,712,0},{28,-34,332,390,4,624,540,1048,150,474},{-42,10,182,542,-6,716,616,1164,74,558}}},
            {{{0,0,0,0,0,512,0,964,724,0},{36,-28,364,420,8,648,580,1096,160,488},{-54,0,194,564,-12,732,632,1228,68,596}}},
            {{{0,0,0,0,0,472,0,884,694,0},{16,-43,284,345,-2,588,480,976,135,453},{-24,25,164,509,3,692,592,1068,83,501}}},
            {{{0,0,0,0,0,464,0,868,688,0},{12,-46,268,330,-4,576,460,952,130,446},{-18,30,158,498,6,684,584,1036,86,482}}}
        }};
        const auto& value = values.at(instance).at(glyph);
        result.x_min=value.x0; result.y_min=value.y0; result.x_max=value.x1; result.y_max=value.y1;
        result.horizontal_origin=value.h_origin; result.horizontal_advance=value.h_advance;
        result.vertical_origin=value.v_origin; result.vertical_advance=value.v_advance;
        result.top_side_bearing=value.top; result.bottom_side_bearing=value.bottom;
        if (glyph == 0U) return result;
        if (options.compact_metrics) {
            const float original_shared_advance_difference = glyph == 1U ? 100.0F : 200.0F;
            result.vertical_advance -= original_shared_advance_difference;
            result.bottom_side_bearing -= original_shared_advance_difference;
        }
    }
    else if (glyph == 0U) {
        result.horizontal_advance = 480; result.vertical_advance = 900; result.top_side_bearing = 700;
        if (cff && options.vorg) { result.vertical_origin = 700; result.has_vertical_origin = true; result.bottom_side_bearing = 200; }
        return result;
    }
    else if (glyph == 1U) {
        result.x_min = 20; result.y_min = -40; result.x_max = 300; result.y_max = 360;
        result.horizontal_origin = cff ? 0 : 8; result.horizontal_advance = 600;
        result.vertical_origin = cff ? 700 : 500; result.vertical_advance = options.compact_metrics ? 900 : 1000;
        result.top_side_bearing = cff ? 340 : 140;
        result.bottom_side_bearing = cff ? (options.compact_metrics ? 160 : 260) : (options.compact_metrics ? 360 : 460);
    } else {
        result.x_min = -30; result.y_min = 20; result.x_max = 170; result.y_max = 520;
        result.horizontal_origin = cff ? 0 : 14; result.horizontal_advance = 700;
        result.vertical_origin = 600; result.vertical_advance = options.compact_metrics ? 900 : 1100;
        result.top_side_bearing = 80; result.bottom_side_bearing = options.compact_metrics ? 320 : 520;
    }
    result.has_vertical_origin = true; result.count = 4U;
    const auto line = [](progpu_native_point start, progpu_native_point end) {
        return progpu_native_path_segment{start,end,{},{},PROGPU_NATIVE_PATH_SEGMENT_LINE,0U,0U,0U};
    };
    result.segments = {{line({result.x_min,result.y_min},{result.x_max,result.y_min}),
        line({result.x_max,result.y_min},{result.x_max,result.y_max}),
        line({result.x_max,result.y_max},{result.x_min,result.y_max}),
        line({result.x_min,result.y_max},{result.x_min,result.y_min})}};
    return result;
}

namespace vertical_font_wire {
using variable_font_wire::bytes;
using variable_font_wire::put16;
using variable_font_wire::put32;
using variable_font_wire::read16;
using variable_font_wire::read32;
using variable_font_wire::checksum;
using variable_font_wire::table;

inline bytes rectangle(std::int16_t left, std::int16_t bottom, std::int16_t right, std::int16_t top)
{
    bytes result(36U);
    put16(result,0U,1U); put16(result,2U,static_cast<std::uint16_t>(left));
    put16(result,4U,static_cast<std::uint16_t>(bottom)); put16(result,6U,static_cast<std::uint16_t>(right));
    put16(result,8U,static_cast<std::uint16_t>(top)); put16(result,10U,3U);
    // No instructions. Four explicit on-curve points, signed 16-bit deltas.
    for (std::size_t point = 0U; point < 4U; ++point) result[14U + point] = std::byte{1};
    const std::array<std::int16_t,4U> xs{left,static_cast<std::int16_t>(right-left),0,static_cast<std::int16_t>(left-right)};
    const std::array<std::int16_t,4U> ys{bottom,0,static_cast<std::int16_t>(top-bottom),0};
    for (std::size_t point = 0U; point < 4U; ++point) {
        put16(result,18U + point*2U,static_cast<std::uint16_t>(xs[point]));
        put16(result,26U + point*2U,static_cast<std::uint16_t>(ys[point]));
    }
    return result;
}

inline std::vector<table> original_tables(const bytes& source)
{
    std::vector<table> result;
    for (std::size_t item = 0U; item < read16(source,4U); ++item) {
        const auto record = 12U + item*16U;
        const auto at = read32(source,record+8U), size = read32(source,record+12U);
        result.push_back({read32(source,record),bytes(source.begin()+at,source.begin()+at+size)});
    }
    return result;
}
inline void set(std::vector<table>& tables, std::uint32_t tag, bytes value)
{
    for (auto& entry : tables) if (entry.tag == tag) { entry.data = std::move(value); return; }
    tables.push_back({tag,std::move(value)});
}
inline bytes assemble(std::vector<table> tables, std::uint32_t scaler)
{
    std::sort(tables.begin(),tables.end(),[](const table& first, const table& second) { return first.tag < second.tag; });
    std::size_t length = 12U + tables.size()*16U;
    for (auto& value : tables) {
        if (value.tag == 0x68656164U) put32(value.data,8U,0U);
        length += (value.data.size()+3U) & ~std::size_t{3U};
    }
    bytes result(length); put32(result,0U,scaler); put16(result,4U,static_cast<std::uint16_t>(tables.size()));
    std::uint16_t power = 1U, selector = 0U;
    while (static_cast<std::size_t>(power)*2U <= tables.size()) { power = static_cast<std::uint16_t>(power*2U); ++selector; }
    put16(result,6U,static_cast<std::uint16_t>(power*16U)); put16(result,8U,selector);
    put16(result,10U,static_cast<std::uint16_t>((tables.size()-power)*16U));
    std::size_t at = 12U + tables.size()*16U, head = 0U;
    for (std::size_t item = 0U; item < tables.size(); ++item) {
        const auto& value = tables[item]; const auto record = 12U + item*16U;
        put32(result,record,value.tag); put32(result,record+4U,checksum(value.data));
        put32(result,record+8U,static_cast<std::uint32_t>(at)); put32(result,record+12U,static_cast<std::uint32_t>(value.data.size()));
        std::copy(value.data.begin(),value.data.end(),result.begin()+static_cast<std::ptrdiff_t>(at));
        if (value.tag == 0x68656164U) head = at;
        at += (value.data.size()+3U) & ~std::size_t{3U};
    }
    if (head == 0U) throw std::logic_error("vertical font fixture requires original head");
    put32(result,head+8U,0xB1B0AFBAU-checksum(result)); return result;
}
inline bytes metrics(bool compact, bool cff)
{
    bytes result(compact ? 8U : 12U);
    put16(result,0U,900U); put16(result,2U,700U);
    if (compact) { put16(result,4U,cff ? 340U : 140U); put16(result,6U,80U); }
    else { put16(result,4U,1000U); put16(result,6U,cff ? 340U : 140U); put16(result,8U,1100U); put16(result,10U,80U); }
    return result;
}
inline bytes header(bool compact, bool cff)
{
    bytes result(36U);
    put32(result,0U,0x00011000U); // vhea1.1, genuine complete compact/noncompact table.
    put16(result,4U,500U); put16(result,6U,static_cast<std::uint16_t>(-500));
    put16(result,10U,compact ? 900U : 1100U); put16(result,12U,80U); put16(result,14U,cff && compact ? 160U : 200U);
    put16(result,16U,cff ? 740U : 580U); put16(result,20U,1U); put16(result,34U,compact ? 1U : 3U);
    return result;
}

inline bytes cff_contours()
{
    // Separate original-owned Type2 rectangles. Their source widths agree
    // with hmtx; this is not the prior CFF cubic fixture with guessed bounds.
    using cff_font_wire::number;
    using cff_font_wire::op;
    using cff_font_wire::append;
    using cff_font_wire::index;
    std::vector<bytes> glyphs;
    for (unsigned glyph = 0U; glyph < 3U; ++glyph) {
        bytes program;
        number(program,glyph == 0U ? 480 : glyph == 1U ? 600 : 700);
        if (glyph != 0U) {
            number(program,glyph == 1U ? 20 : -30); number(program,glyph == 1U ? -40 : 20); op(program,21U);
            for (const auto value : {glyph == 1U ? 280 : 200,0,0,glyph == 1U ? 400 : 500,glyph == 1U ? -280 : -200,0})
                number(program,value);
            op(program,5U);
        }
        op(program,14U); glyphs.push_back(std::move(program));
    }
    std::uint32_t charstrings = 0U, charset = 0U;
    const auto prefix = [&] {
        bytes top;
        for (const auto value : {-30,-40,300,520}) number(top,value);
        op(top,5U); cff_font_wire::offset(top,charset); op(top,15U);
        cff_font_wire::offset(top,charstrings); op(top,17U);
        number(top,0); number(top,0); op(top,18U);
        bytes result{std::byte{1},std::byte{0},std::byte{4},std::byte{4}};
        append(result,index({cff_font_wire::ascii("ProGPUVerticalCff-Regular")},false));
        append(result,index({top},false)); append(result,index({},false)); append(result,index({},false));
        return result;
    };
    auto result = prefix(); charstrings = static_cast<std::uint32_t>(result.size()); append(result,index(glyphs,false));
    charset = static_cast<std::uint32_t>(result.size());
    append(result,{std::byte{0},std::byte{0},std::byte{34},std::byte{0},std::byte{35}});
    const auto relocated = prefix(); std::copy(relocated.begin(),relocated.end(),result.begin()); return result;
}

inline bytes glyph_variations(unsigned glyph)
{
    constexpr std::array<std::array<std::int16_t,8U>,3U> xs{{
        {{4,36,0,0,0,0,0,0}},{{16,64,64,16,8,56,0,0}},{{-24,24,24,-24,-12,20,0,0}}}};
    constexpr std::array<std::array<std::int16_t,8U>,3U> ys{{
        {{0,0,24,-40,0,0,0,0}},{{12,12,60,60,0,0,80,-16}},{{-20,-20,44,44,0,0,32,-96}}}};
    const std::size_t points = glyph == 0U ? 4U : 8U;
    bytes result(16U); put16(result,0U,2U); put16(result,2U,16U);
    for (unsigned tuple = 0U; tuple < 2U; ++tuple) {
        const auto start = result.size(); result.push_back(std::byte{0}); // all points, including all four phantoms
        for (const auto* axis : {&xs[glyph],&ys[glyph]}) {
            result.push_back(static_cast<std::byte>(points-1U));
            for (std::size_t point = 0U; point < points; ++point) {
                const auto delta = tuple == 0U ? (*axis)[point] : -(*axis)[point]/2;
                result.push_back(static_cast<std::byte>(static_cast<std::uint8_t>(delta)));
            }
        }
        const auto at = 4U+tuple*6U;
        put16(result,at,static_cast<std::uint16_t>(result.size()-start)); put16(result,at+2U,0xA000U);
        put16(result,at+4U,tuple == 0U ? 0x4000U : 0xC000U);
    }
    return result;
}

inline bytes vertical_variations(bool maps)
{
    // Two independent regions and advance / optional TSB / optional BSB rows.
    // With explicit maps, advance rows are deliberately permuted (2,0,1).
    // This makes accidentally using implicit glyph indices observable.
    constexpr std::array<std::int16_t,9U> deltas{64,96,128,24,20,-12,40,28,76};
    const std::uint16_t rows = maps ? 9U : 3U;
    bytes result(24U+28U+10U+static_cast<std::size_t>(rows)*4U);
    put16(result,0U,1U); put32(result,4U,24U);
    put16(result,24U,1U); put32(result,26U,12U); put16(result,30U,1U); put32(result,32U,28U);
    put16(result,36U,1U); put16(result,38U,2U);
    put16(result,42U,0x4000U); put16(result,44U,0x4000U);
    put16(result,46U,0xC000U); put16(result,48U,0xC000U);
    put16(result,52U,rows); put16(result,54U,2U); put16(result,56U,2U); put16(result,60U,1U);
    for (std::size_t row = 0U; row < rows; ++row) {
        const auto source = maps && row < 3U ? (row+2U)%3U : row;
        put16(result,62U+row*4U,static_cast<std::uint16_t>(deltas[source]));
        put16(result,64U+row*4U,static_cast<std::uint16_t>(-deltas[source]/2));
    }
    if (maps) {
        for (unsigned map = 0U; map < 3U; ++map) {
            const auto at = result.size(); result.resize(at+7U); put32(result,8U+map*4U,static_cast<std::uint32_t>(at));
            result[at+1U]=std::byte{3}; put16(result,at+2U,3U);
            for (unsigned glyph = 0U; glyph < 3U; ++glyph)
                result[at+4U+glyph]=static_cast<std::byte>(map == 0U ? (glyph+1U)%3U : map*3U+glyph);
        }
    }
    return result;
}
} // namespace vertical_font_wire

// Source-owned font construction from the public OpenType glyf/loca/vhea/vmtx
// contracts. Existing font bytes are not altered. No native metric parser or
// expected-value generator participates. Bounded O(F) time/storage in bytes.
inline std::vector<std::byte> make_vertical_font(vertical_font_options options = {})
{
    using namespace vertical_font_wire;
    const bool cff = options.kind == vertical_font_kind::cff;
    const bool variable = options.kind == vertical_font_kind::truetype_variable;
    if ((options.kind != vertical_font_kind::truetype && !cff && !variable) || (options.vvar && !variable) ||
        (options.side_bearing_maps && !options.vvar) || options.origin_map || options.vorg != cff)
        throw std::invalid_argument("vertical fixture family not yet authored");
    auto tables = original_tables(cff ? make_cff_font(cff_font_kind::cff1_default) : make_variable_font());
    std::erase_if(tables,[&](const table& entry) {
        return (!variable && (entry.tag == 0x66766172U || entry.tag == 0x53544154U)) ||
            entry.tag == 0x61766172U || entry.tag == 0x67766172U || entry.tag == 0x48564152U || entry.tag == 0x6670676DU ||
            entry.tag == 0x70726570U || entry.tag == 0x63767420U;
    });
    if (cff) {
        set(tables,0x43464620U,cff_contours());
        bytes vorg(12U); put16(vorg,0U,1U); put16(vorg,4U,700U); put16(vorg,6U,1U);
        put16(vorg,8U,2U); put16(vorg,10U,600U); set(tables,0x564F5247U,std::move(vorg));
    } else {
        auto glyphs = rectangle(20,-40,300,360);
        const auto second = rectangle(-30,20,170,520); glyphs.insert(glyphs.end(),second.begin(),second.end());
        set(tables,0x676C7966U,std::move(glyphs));
        bytes loca(16U); put32(loca,8U,36U); put32(loca,12U,72U); set(tables,0x6C6F6361U,std::move(loca));
        bytes maxp(32U); put32(maxp,0U,0x00010000U); put16(maxp,4U,3U); put16(maxp,6U,4U);
        put16(maxp,8U,1U); put16(maxp,14U,1U); set(tables,0x6D617870U,std::move(maxp));
    }
    bytes hmtx(12U);
    put16(hmtx,0U,480U); put16(hmtx,4U,600U); put16(hmtx,6U,cff || variable ? 20U : 12U);
    put16(hmtx,8U,700U); put16(hmtx,10U,static_cast<std::uint16_t>(cff || variable ? -30 : -44)); set(tables,0x686D7478U,std::move(hmtx));
    for (auto& entry : tables) {
        if (entry.tag == 0x68656164U) {
            put16(entry.data,16U,variable ? 2U : 0U); put16(entry.data,36U,static_cast<std::uint16_t>(-30));
            put16(entry.data,38U,static_cast<std::uint16_t>(-40)); put16(entry.data,40U,300U); put16(entry.data,42U,520U);
        } else if (entry.tag == 0x68686561U) {
            put16(entry.data,10U,700U); put16(entry.data,12U,static_cast<std::uint16_t>(cff || variable ? -30 : -44));
            put16(entry.data,14U,cff || variable ? 300U : 308U); put16(entry.data,16U,cff || variable ? 300U : 292U); put16(entry.data,34U,3U);
        }
    }
    set(tables,0x76686561U,header(options.compact_metrics,cff)); set(tables,0x766D7478U,metrics(options.compact_metrics,cff));
    if (variable) {
        bytes gvar(36U); put16(gvar,0U,1U); put16(gvar,4U,1U); put32(gvar,8U,36U);
        put16(gvar,12U,3U); put16(gvar,14U,1U); put32(gvar,16U,36U);
        for (unsigned glyph = 0U; glyph < 3U; ++glyph) {
            put32(gvar,20U+glyph*4U,static_cast<std::uint32_t>(gvar.size()-36U));
            const auto data = glyph_variations(glyph); gvar.insert(gvar.end(),data.begin(),data.end());
        }
        put32(gvar,32U,static_cast<std::uint32_t>(gvar.size()-36U)); set(tables,0x67766172U,std::move(gvar));
        if (options.vvar) set(tables,0x56564152U,vertical_variations(options.side_bearing_maps));
    }
    return assemble(std::move(tables),cff ? 0x4F54544FU : 0x00010000U);
}
} // namespace progpu::native::direct2d::tests
