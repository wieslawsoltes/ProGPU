#pragma once

#include "progpu_native.h"
#include "progpu_native_direct2d_variable_font_fixture.hpp"
#include <initializer_list>

namespace progpu::native::direct2d::tests {

enum class cff_font_kind {
    cff1_default, cff1_affine, cff1_cid, cff1_cid_inherited,
    cff2_static, cff2_variable_fixed, cff2_variable_hvar
};

inline bool cff_font_is_variable(cff_font_kind kind)
{
    return kind == cff_font_kind::cff2_variable_fixed || kind == cff_font_kind::cff2_variable_hvar;
}
inline std::uint16_t cff_font_units(cff_font_kind kind)
{
    return kind == cff_font_kind::cff1_default ? 1000U :
        kind <= cff_font_kind::cff1_cid_inherited ? 1024U : 2048U;
}
inline std::size_t cff_font_case_count(cff_font_kind kind) { return cff_font_is_variable(kind) ? 3U : 1U; }
inline float cff_font_weight(std::size_t instance)
{
    constexpr std::array<float, 3U> weights{400, 650, 900};
    return weights.at(instance);
}

struct cff_glyph_expectation final {
    std::array<progpu_native_path_segment, 4U> segments{};
    std::uint32_t count = 0U;
    float advance = 0;
};

inline cff_glyph_expectation expected_cff_glyph(cff_font_kind kind, std::size_t instance, std::uint16_t glyph)
{
    if (instance >= cff_font_case_count(kind) || glyph >= 3U)
        throw std::out_of_range("authored CFF glyph inventory");
    const auto line = [](progpu_native_point a, progpu_native_point b) {
        return progpu_native_path_segment{a, b, {}, {}, 0U, 0U, 0U, 0U};
    };
    const auto rectangle = [&](float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3) {
        return std::array{line({x0,y0}, {x1,y1}), line({x1,y1}, {x2,y2}),
            line({x2,y2}, {x3,y3}), line({x3,y3}, {x0,y0})};
    };
    const auto curve = [&](progpu_native_point a, progpu_native_point b, progpu_native_point c,
        progpu_native_point d, progpu_native_point e) {
        return std::array{progpu_native_path_segment{a,b,c,d,2U,0U,0U,0U},
            line(d,e), line(e,a), progpu_native_path_segment{}};
    };
    // Literal independent design-space geometry: no decoder, FontMatrix or
    // variation algorithm computes the oracle. Matrix controls include a
    // noncommuting top shear / FD translation and an omitted FD matrix.
    cff_glyph_expectation result{};
    static constexpr std::array<float, 3U> widths{500,600,700};
    static constexpr std::array<std::array<float, 3U>, 3U> hvar_widths{{
        {{500,600,700}}, {{532,648,764}}, {{564,696,828}}}};
    result.advance = kind == cff_font_kind::cff2_variable_hvar ? hvar_widths.at(instance).at(glyph) : widths.at(glyph);
    if (glyph == 0U) return result;
    result.count = glyph == 1U ? 4U : 3U;
    if (kind == cff_font_kind::cff1_affine || kind == cff_font_kind::cff1_cid ||
        kind == cff_font_kind::cff1_cid_inherited) {
        if (glyph == 1U) {
            result.segments = kind == cff_font_kind::cff1_cid
                ? rectangle(69,20,309,20,399,380,159,380)
                : rectangle(61,20,301,20,391,380,151,380);
        } else {
            result.segments = kind == cff_font_kind::cff1_affine
                ? curve({83.5F,30},{128.5F,210},{338.5F,330},{368.5F,210},{323.5F,30})
                : curve({87.5F,46},{132.5F,226},{342.5F,346},{372.5F,226},{327.5F,46});
        }
    } else if (cff_font_is_variable(kind) && instance == 1U) {
        result.segments = glyph == 1U ? rectangle(48,24,320,24,320,400,48,400)
            : curve({52,38},{52,234},{248,370},{316,234},{316,38});
    } else if (cff_font_is_variable(kind) && instance == 2U) {
        result.segments = glyph == 1U ? rectangle(56,28,360,28,360,420,56,420)
            : curve({44,46},{44,258},{256,410},{332,258},{332,46});
    } else {
        result.segments = glyph == 1U ? rectangle(40,20,280,20,280,380,40,380)
            : curve({60,30},{60,210},{240,330},{300,210},{300,30});
    }
    return result;
}

namespace cff_font_wire {
using variable_font_wire::bytes;
using variable_font_wire::put16;
using variable_font_wire::put32;
using variable_font_wire::read16;
using variable_font_wire::read32;
using variable_font_wire::checksum;
using variable_font_wire::table;

inline void append(bytes& to, const bytes& from) { to.insert(to.end(), from.begin(), from.end()); }
inline bytes ascii(std::string_view value)
{
    bytes result;
    for (const auto character : value) result.push_back(static_cast<std::byte>(character));
    return result;
}
inline void number(bytes& output, std::int32_t value)
{
    if (value >= -107 && value <= 107) output.push_back(static_cast<std::byte>(value + 139));
    else {
        output.push_back(std::byte{28}); const auto at = output.size(); output.resize(at + 2U);
        put16(output, at, static_cast<std::uint16_t>(value));
    }
}
inline void offset(bytes& output, std::uint32_t value)
{
    // Fixed-width DICT offsets keep relocation independent of their values.
    output.push_back(std::byte{29}); const auto at = output.size(); output.resize(at + 4U); put32(output, at, value);
}
inline void real(bytes& output, std::string_view value)
{
    output.push_back(std::byte{30});
    std::vector<std::uint8_t> digits;
    for (const auto character : value) {
        if (character >= '0' && character <= '9') digits.push_back(static_cast<std::uint8_t>(character - '0'));
        else if (character == '.') digits.push_back(10U);
        else if (character == '-') digits.push_back(14U);
        else throw std::invalid_argument("authored CFF real literal");
    }
    digits.push_back(15U); if ((digits.size() & 1U) != 0U) digits.push_back(15U);
    for (std::size_t at = 0U; at < digits.size(); at += 2U)
        output.push_back(static_cast<std::byte>((digits[at] << 4U) | digits[at + 1U]));
}
inline void op(bytes& output, unsigned value)
{
    if (value > 255U) { output.push_back(std::byte{12}); value -= 0x0C00U; }
    output.push_back(static_cast<std::byte>(value));
}
inline bytes index(const std::vector<bytes>& objects, bool cff2)
{
    const auto count_size = cff2 ? 4U : 2U;
    bytes result(count_size);
    if (cff2) put32(result, 0U, static_cast<std::uint32_t>(objects.size()));
    else put16(result, 0U, static_cast<std::uint16_t>(objects.size()));
    if (objects.empty()) return result;
    std::uint32_t last = 1U;
    for (const auto& object : objects) last += static_cast<std::uint32_t>(object.size());
    const unsigned size = last <= 255U ? 1U : last <= 65535U ? 2U : last <= 0xFFFFFFU ? 3U : 4U;
    result.push_back(static_cast<std::byte>(size));
    std::uint32_t cursor = 1U;
    for (std::size_t at = 0U; at <= objects.size(); ++at) {
        for (unsigned byte = size; byte > 0U; --byte)
            result.push_back(static_cast<std::byte>((cursor >> ((byte - 1U) * 8U)) & 255U));
        if (at < objects.size()) cursor += static_cast<std::uint32_t>(objects[at].size());
    }
    for (const auto& object : objects) append(result, object);
    return result;
}
inline void operands(bytes& output, std::initializer_list<std::int32_t> defaults,
    std::initializer_list<std::int32_t> deltas, bool variable)
{
    for (const auto value : defaults) number(output, value);
    if (variable) {
        if (defaults.size() != deltas.size()) throw std::invalid_argument("authored CFF2 blend arity");
        for (const auto value : deltas) number(output, value);
        number(output, static_cast<std::int32_t>(defaults.size())); op(output, 16U);
    }
}
inline bytes contour(unsigned glyph, bool cff2, bool variable)
{
    bytes result;
    if (glyph == 1U) {
        operands(result, {40,20}, {16,8}, variable); op(result, 21U);
        operands(result, {240,0,0,360,-240,0}, {64,0,0,32,-64,0}, variable); op(result, 5U);
    } else {
        operands(result, {60,30}, {-16,16}, variable); op(result, 21U);
        operands(result, {0,180,180,120,60,-120}, {0,32,32,32,16,-32}, variable); op(result, 8U);
        operands(result, {0,-180}, {0,-32}, variable); op(result, 5U);
    }
    if (!cff2) op(result, 11U); // CFF2 subroutines end at the INDEX boundary.
    return result;
}
inline bytes item_store(bool advances)
{
    // One +wght region: start0, peak1, end1. CFF2 stores deltas in
    // CharStrings; HVAR has three rows and its own independent store.
    bytes result(30U + (advances ? 6U : 0U));
    put16(result, 0U, 1U); put32(result, 2U, 12U); put16(result, 6U, 1U); put32(result, 8U, 22U);
    put16(result, 12U, 1U); put16(result, 14U, 1U); put16(result, 18U, 0x4000U); put16(result, 20U, 0x4000U);
    put16(result, 22U, advances ? 3U : 0U); put16(result, 24U, advances ? 1U : 0U);
    put16(result, 26U, 1U);
    if (advances) { put16(result, 30U, 64U); put16(result, 32U, 96U); put16(result, 34U, 128U); }
    return result;
}
inline bytes font_table(cff_font_kind kind)
{
    const bool cff2 = kind >= cff_font_kind::cff2_static;
    const bool cid = kind == cff_font_kind::cff1_cid || kind == cff_font_kind::cff1_cid_inherited;
    const bool variable = cff_font_is_variable(kind);
    struct positions final { std::uint32_t strings = 0U, charset = 0U, fd_array = 0U, fd_select = 0U, store = 0U;
        std::array<std::uint32_t, 2U> private_dict{}; } positions;
    const auto matrix = [&](bytes& target, int fd) {
        if (fd >= 0) {
            for (const auto value : {1,0,0,1,fd == 0 ? 8 : 0,fd == 1 ? 16 : 0}) number(target, value);
        } else if (cff2) {
            real(target, "0.00048828125"); number(target, 0); number(target, 0);
            real(target, "0.00048828125"); number(target, 0); number(target, 0);
        } else {
            real(target, "0.0009765625"); number(target, 0); real(target, "0.000244140625");
            real(target, "0.0009765625"); real(target, "0.015625"); number(target, 0);
        }
        op(target, 0x0C07U);
    };
    const auto top_dictionary = [&] {
        bytes top;
        if (cid) { number(top, 391); number(top, 392); number(top, 0); op(top, 0x0C1EU); }
        if (kind != cff_font_kind::cff1_default) matrix(top, -1);
        if (!cff2) {
            for (const auto value : {0,0,512,512}) number(top, value);
            op(top, 5U);
            offset(top, positions.charset); op(top, 15U);
        }
        offset(top, positions.strings); op(top, 17U);
        if (cid || cff2) {
            if (cid) { number(top, 3); op(top, 0x0C22U); }
            offset(top, positions.fd_array); op(top, 0x0C24U);
            offset(top, positions.fd_select); op(top, 0x0C25U);
        } else {
            number(top, 6); offset(top, positions.private_dict[0]); op(top, 18U);
        }
        if (variable) { offset(top, positions.store); op(top, 24U); }
        return top;
    };
    const auto prefix = [&] {
        const auto top = top_dictionary();
        bytes result;
        if (cff2) {
            result = {std::byte{2},std::byte{0},std::byte{5},std::byte{0},std::byte{0}};
            put16(result, 3U, static_cast<std::uint16_t>(top.size())); append(result, top);
        } else {
            result = {std::byte{1},std::byte{0},std::byte{4},std::byte{4}};
            append(result, index({ascii("ProGPUCffFixture-Regular")}, false));
            append(result, index({top}, false));
            append(result, cid ? index({ascii("Adobe"),ascii("Identity"),ascii("RectFD"),ascii("CurveFD")}, false) : index({}, false));
        }
        append(result, index({}, cff2)); return result;
    };
    const auto dictionaries = [&] {
        std::vector<bytes> objects;
        for (unsigned fd = 0U; fd < 2U; ++fd) {
            bytes dictionary;
            if (cid) {
                number(dictionary, static_cast<std::int32_t>(393U + fd)); op(dictionary, 0x0C26U);
                if (kind != cff_font_kind::cff1_cid_inherited || fd != 0U) matrix(dictionary, static_cast<int>(fd));
            }
            number(dictionary, 6); offset(dictionary, positions.private_dict[fd]); op(dictionary, 18U);
            objects.push_back(std::move(dictionary));
        }
        return index(objects, cff2);
    };
    bytes result = prefix();
    const auto prefix_size = result.size();
    std::vector<bytes> strings;
    for (unsigned glyph = 0U; glyph < 3U; ++glyph) {
        bytes value;
        if (!cff2) number(value, static_cast<std::int32_t>(500U + glyph * 100U));
        if (glyph != 0U) {
            number(value, glyph == 2U && !cid && !cff2 ? -106 : -107); op(value, 10U);
        }
        if (!cff2) op(value, 14U);
        strings.push_back(std::move(value));
    }
    positions.strings = static_cast<std::uint32_t>(result.size()); append(result, index(strings, cff2));
    if (!cff2) {
        positions.charset = static_cast<std::uint32_t>(result.size());
        const auto at = result.size(); result.resize(at + 5U);
        put16(result, at + 1U, cid ? 1U : 34U); put16(result, at + 3U, cid ? 2U : 35U);
    }
    std::size_t fd_size = 0U;
    if (cid || cff2) {
        positions.fd_select = static_cast<std::uint32_t>(result.size());
        append(result, {std::byte{0},std::byte{0},std::byte{0},std::byte{1}});
        positions.fd_array = static_cast<std::uint32_t>(result.size());
        const auto data = dictionaries(); fd_size = data.size(); append(result, data);
    }
    for (unsigned fd = 0U; fd < (cid || cff2 ? 2U : 1U); ++fd) {
        positions.private_dict[fd] = static_cast<std::uint32_t>(result.size());
        offset(result, 6U); op(result, 19U); // LocalSubr INDEX immediately after the six-byte PrivateDICT.
        append(result, !cid && !cff2 ? index({contour(1U,false,false),contour(2U,false,false)}, false)
            : index({contour(fd + 1U,cff2,variable)}, cff2));
    }
    if (variable) {
        positions.store = static_cast<std::uint32_t>(result.size());
        const auto store = item_store(false); const auto at = result.size(); result.resize(at + 2U);
        put16(result, at, static_cast<std::uint16_t>(store.size())); append(result, store);
    }
    const auto relocated = prefix();
    if (relocated.size() != prefix_size) throw std::logic_error("CFF fixture relocation changed prefix size");
    std::copy(relocated.begin(), relocated.end(), result.begin());
    if (cid || cff2) {
        const auto relocated_fd = dictionaries();
        if (relocated_fd.size() != fd_size) throw std::logic_error("CFF fixture relocation changed FD size");
        std::copy(relocated_fd.begin(), relocated_fd.end(), result.begin() + positions.fd_array);
    }
    return result;
}

inline bytes names()
{
    struct entry final { std::uint16_t id; std::u16string_view value; };
    constexpr std::array records{entry{1,u"ProGPU CFF Fixture"},entry{2,u"Regular"},
        entry{4,u"ProGPU CFF Fixture Regular"},entry{6,u"ProGPUCffFixture-Regular"},
        entry{16,u"ProGPU CFF Fixture"},entry{17,u"Regular"},entry{256,u"Weight"}};
    const auto start = 6U + records.size() * 12U; bytes result(start);
    put16(result, 2U, static_cast<std::uint16_t>(records.size())); put16(result, 4U, static_cast<std::uint16_t>(start));
    for (std::size_t item = 0U; item < records.size(); ++item) {
        const auto at = 6U + item * 12U;
        put16(result, at, 3U); put16(result, at + 2U, 1U); put16(result, at + 4U, 0x0409U);
        put16(result, at + 6U, records[item].id); put16(result, at + 8U, static_cast<std::uint16_t>(records[item].value.size() * 2U));
        put16(result, at + 10U, static_cast<std::uint16_t>(result.size() - start));
        for (const auto character : records[item].value) {
            const auto position = result.size(); result.resize(position + 2U); put16(result, position, static_cast<std::uint16_t>(character));
        }
    }
    return result;
}
} // namespace cff_font_wire

// Independent, original-owned OpenType/CFF byte construction from the public
// Adobe 5176/5177 and OpenType CFF2/HVAR formats. No foreign font/engine or
// product decoder generates fixture bytes or expected paths. Bounded O(F)
// time/storage in the tiny emitted font; existing TrueType fixture is unchanged.
inline std::vector<std::byte> make_cff_font(cff_font_kind kind)
{
    using namespace cff_font_wire;
    if (kind < cff_font_kind::cff1_default || kind > cff_font_kind::cff2_variable_hvar)
        throw std::invalid_argument("authored CFF font kind");
    const bool variable = cff_font_is_variable(kind);
    // Reuse only original-owned sfnt metadata/table assembly; all outline,
    // hint/phantom, old avar/gvar and old HVAR data is removed.
    const auto original = make_variable_font();
    std::vector<table> tables;
    for (std::size_t item = 0U; item < read16(original, 4U); ++item) {
        const auto record = 12U + item * 16U;
        const auto tag = read32(original, record);
        if (tag == 0x676C7966U || tag == 0x6C6F6361U || tag == 0x67766172U || tag == 0x61766172U ||
            tag == 0x48564152U || tag == 0x6670676DU || tag == 0x70726570U || tag == 0x63767420U ||
            (!variable && (tag == 0x66766172U || tag == 0x53544154U))) continue;
        const auto at = read32(original, record + 8U), size = read32(original, record + 12U);
        table value{tag, bytes(original.begin() + at, original.begin() + at + size)};
        if (tag == 0x68656164U) {
            put32(value.data, 8U, 0U); put16(value.data, 16U, 0U); put16(value.data, 18U, cff_font_units(kind));
            put16(value.data, 36U, 0U); put16(value.data, 38U, 0U); put16(value.data, 40U, 512U); put16(value.data, 42U, 512U);
            put16(value.data, 50U, 0U);
        } else if (tag == 0x6D617870U) {
            value.data.assign(6U, std::byte{0}); put32(value.data, 0U, 0x00005000U); put16(value.data, 4U, 3U);
        } else if (tag == 0x686D7478U) {
            value.data.assign(12U, std::byte{0});
            for (unsigned glyph = 0U; glyph < 3U; ++glyph) put16(value.data, glyph * 4U, static_cast<std::uint16_t>(500U + glyph * 100U));
            const bool affine = kind >= cff_font_kind::cff1_affine && kind <= cff_font_kind::cff1_cid_inherited;
            put16(value.data, 6U, kind == cff_font_kind::cff1_cid ? 69U : affine ? 61U : 40U);
            put16(value.data, 10U, kind == cff_font_kind::cff1_cid || kind == cff_font_kind::cff1_cid_inherited ? 87U : affine ? 83U : 60U);
        } else if (tag == 0x68686561U) {
            put16(value.data, 10U, 828U); put16(value.data, 12U, 0U); put16(value.data, 14U, 0U);
            put16(value.data, 16U, 512U); put16(value.data, 34U, 3U);
        } else if (tag == 0x6E616D65U) value.data = names();
        tables.push_back(std::move(value));
    }
    tables.push_back({kind >= cff_font_kind::cff2_static ? 0x43464632U : 0x43464620U, font_table(kind)});
    if (kind == cff_font_kind::cff2_variable_hvar) {
        bytes hvar(20U); put16(hvar, 0U, 1U); put32(hvar, 4U, 20U); append(hvar, item_store(true));
        tables.push_back({0x48564152U, std::move(hvar)});
    }
    std::sort(tables.begin(), tables.end(), [](const table& first, const table& second) { return first.tag < second.tag; });
    std::size_t length = 12U + tables.size() * 16U;
    for (const auto& value : tables) length += (value.data.size() + 3U) & ~std::size_t{3U};
    bytes result(length); put32(result, 0U, 0x4F54544FU); put16(result, 4U, static_cast<std::uint16_t>(tables.size()));
    std::uint16_t power = 1U, selector = 0U;
    while (static_cast<std::size_t>(power) * 2U <= tables.size()) { power = static_cast<std::uint16_t>(power * 2U); ++selector; }
    put16(result, 6U, static_cast<std::uint16_t>(power * 16U)); put16(result, 8U, selector);
    put16(result, 10U, static_cast<std::uint16_t>((tables.size() - power) * 16U));
    std::size_t cursor = 12U + tables.size() * 16U, head = 0U;
    for (std::size_t item = 0U; item < tables.size(); ++item) {
        const auto& value = tables[item]; const auto record = 12U + item * 16U;
        put32(result, record, value.tag); put32(result, record + 4U, checksum(value.data));
        put32(result, record + 8U, static_cast<std::uint32_t>(cursor)); put32(result, record + 12U, static_cast<std::uint32_t>(value.data.size()));
        std::copy(value.data.begin(), value.data.end(), result.begin() + static_cast<std::ptrdiff_t>(cursor));
        if (value.tag == 0x68656164U) head = cursor;
        cursor += (value.data.size() + 3U) & ~std::size_t{3U};
    }
    put32(result, head + 8U, 0xB1B0AFBAU - checksum(result)); return result;
}
} // namespace progpu::native::direct2d::tests
