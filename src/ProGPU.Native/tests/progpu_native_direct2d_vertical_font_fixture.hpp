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
    if ((options.kind != vertical_font_kind::truetype && !cff) || instance != 0U || glyph >= 3U)
        throw std::out_of_range("authored static vertical glyph inventory");
    // Literal independent metrics. Empty TT has a real advance and vmtx TSB,
    // but no glyf bbox: do not invent its top/bottom phantom origins.
    vertical_glyph_expectation result{};
    if (glyph == 0U) {
        result.horizontal_advance = 480; result.vertical_advance = 900; result.top_side_bearing = 700;
        if (cff && options.vorg) { result.vertical_origin = 700; result.has_vertical_origin = true; result.bottom_side_bearing = 200; }
        return result;
    }
    if (glyph == 1U) {
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
} // namespace vertical_font_wire

// Source-owned font construction from the public OpenType glyf/loca/vhea/vmtx
// contracts. Existing font bytes are not altered. No native metric parser or
// expected-value generator participates. Bounded O(F) time/storage in bytes.
inline std::vector<std::byte> make_vertical_font(vertical_font_options options = {})
{
    using namespace vertical_font_wire;
    const bool cff = options.kind == vertical_font_kind::cff;
    if ((options.kind != vertical_font_kind::truetype && !cff) || options.vvar || options.side_bearing_maps ||
        options.origin_map || options.vorg != cff)
        throw std::invalid_argument("vertical fixture family not yet authored");
    auto tables = original_tables(cff ? make_cff_font(cff_font_kind::cff1_default) : make_variable_font());
    std::erase_if(tables,[](const table& entry) {
        return entry.tag == 0x66766172U || entry.tag == 0x61766172U || entry.tag == 0x67766172U ||
            entry.tag == 0x48564152U || entry.tag == 0x53544154U || entry.tag == 0x6670676DU ||
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
    put16(hmtx,0U,480U); put16(hmtx,4U,600U); put16(hmtx,6U,cff ? 20U : 12U);
    put16(hmtx,8U,700U); put16(hmtx,10U,static_cast<std::uint16_t>(cff ? -30 : -44)); set(tables,0x686D7478U,std::move(hmtx));
    for (auto& entry : tables) {
        if (entry.tag == 0x68656164U) {
            put16(entry.data,16U,0U); put16(entry.data,36U,static_cast<std::uint16_t>(-30));
            put16(entry.data,38U,static_cast<std::uint16_t>(-40)); put16(entry.data,40U,300U); put16(entry.data,42U,520U);
        } else if (entry.tag == 0x68686561U) {
            put16(entry.data,10U,700U); put16(entry.data,12U,static_cast<std::uint16_t>(cff ? -30 : -44));
            put16(entry.data,14U,cff ? 300U : 308U); put16(entry.data,16U,cff ? 300U : 292U); put16(entry.data,34U,3U);
        }
    }
    set(tables,0x76686561U,header(options.compact_metrics,cff)); set(tables,0x766D7478U,metrics(options.compact_metrics,cff));
    return assemble(std::move(tables),cff ? 0x4F54544FU : 0x00010000U);
}
} // namespace progpu::native::direct2d::tests
