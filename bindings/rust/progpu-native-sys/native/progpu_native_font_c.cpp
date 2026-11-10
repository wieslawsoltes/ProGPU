// The C ABI over the engine's font stack (progpu_native_font_c.h).

#include "progpu_native_font_c.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <new>
#include <span>
#include <string_view>
#include <vector>

#include "progpu_native_text.hpp"

namespace text = progpu::native::text;

struct progpu_native_font_catalog {
    text::system_font_catalog catalog;
};

struct progpu_native_font_face {
    // A face of a catalog: shares the mapped file with the catalog's other faces of the file.
    text::loaded_font_face loaded;
    // A face from bytes: the copy and the view over it.
    std::vector<std::byte> bytes;
    text::sfnt_font_view view;
    std::uint32_t face_index = 0U;
    bool from_catalog = false;

    const text::sfnt_font_view& font() const noexcept { return from_catalog ? loaded.font() : view; }
    std::span<const std::byte> data() const noexcept {
        return from_catalog ? loaded.data() : std::span<const std::byte>{bytes};
    }
};

namespace {

constexpr auto glyf_tag = text::open_type_tag::from_chars('g', 'l', 'y', 'f');
constexpr auto loca_tag = text::open_type_tag::from_chars('l', 'o', 'c', 'a');
constexpr auto gvar_tag = text::open_type_tag::from_chars('g', 'v', 'a', 'r');
constexpr auto cff1_tag = text::open_type_tag::from_chars('C', 'F', 'F', ' ');
constexpr auto cff2_tag = text::open_type_tag::from_chars('C', 'F', 'F', '2');
constexpr auto wght_tag = text::open_type_tag::from_chars('w', 'g', 'h', 't');
constexpr auto wdth_tag = text::open_type_tag::from_chars('w', 'd', 't', 'h');
constexpr auto ital_tag = text::open_type_tag::from_chars('i', 't', 'a', 'l');
constexpr auto slnt_tag = text::open_type_tag::from_chars('s', 'l', 'n', 't');
constexpr auto opsz_tag = text::open_type_tag::from_chars('o', 'p', 's', 'z');

int result(bool ok) noexcept { return ok ? 1 : 0; }

std::string_view view_of(const char* data, std::size_t size) noexcept {
    return size == 0U || data == nullptr ? std::string_view{} : std::string_view{data, size};
}

std::span<const std::int16_t> coordinates_of(const std::int16_t* data, std::size_t count) noexcept {
    return count == 0U || data == nullptr ? std::span<const std::int16_t>{}
                                           : std::span<const std::int16_t>{data, count};
}

text::font_style_request request_of(const progpu_native_font_style* style) noexcept {
    text::font_style_request request{};
    if (style != nullptr) {
        request.weight = style->weight;
        request.width = style->width;
        request.slant = style->slant == PROGPU_NATIVE_FONT_SLANT_ITALIC ? text::font_provider_slant::italic
            : style->slant == PROGPU_NATIVE_FONT_SLANT_OBLIQUE ? text::font_provider_slant::oblique
            : text::font_provider_slant::normal;
        request.optical_size_fixed = style->optical_size_fixed;
    }
    return request;
}

bool any_nonzero(std::span<const std::int16_t> coordinates) noexcept {
    return std::any_of(coordinates.begin(), coordinates.end(), [](std::int16_t value) { return value != 0; });
}

// The storage of one thread's outline decoding: grows to the largest glyph it has decoded.
struct outline_storage final {
    std::vector<progpu_native_path_segment> segments;
    std::vector<progpu_native_point> points, varied_points, component_offsets;
    std::vector<std::uint16_t> contours;
    std::vector<text::sfnt_outline_point> original_points;
    std::vector<text::sfnt_gvar_tuple_header> simple_headers, composite_headers;
    std::vector<std::int16_t> simple_regions, composite_regions, simple_x, simple_y, composite_x, composite_y;
    std::vector<std::uint32_t> simple_shared, simple_private, composite_shared, composite_private;
    std::vector<float> tuple_x, tuple_y;
    std::vector<std::uint8_t> touched;

    template <class T>
    static void grow(std::vector<T>& storage, std::size_t count) {
        if (storage.size() < count) storage.resize(count);
    }

    void reserve(const text::sfnt_varied_glyph_requirements& requirements) {
        const auto& simple = requirements.simple_variation;
        const auto& composite = requirements.composite_variation;
        grow(contours, requirements.outline.simple_contour_scratch_count);
        grow(original_points, requirements.outline.simple_point_scratch_count);
        grow(varied_points, requirements.varied_simple_point_count);
        grow(component_offsets, requirements.component_offset_count);
        grow(points, requirements.outline.point_count);
        grow(segments, requirements.outline.path_segment_count);
        grow(simple_headers, simple.tuple_header_count);
        grow(simple_regions, simple.region_coordinate_count);
        grow(simple_shared, simple.point_number_count);
        grow(simple_private, simple.point_number_count);
        grow(simple_x, simple.delta_count);
        grow(simple_y, simple.delta_count);
        grow(tuple_x, simple.tuple_point_count);
        grow(tuple_y, simple.tuple_point_count);
        grow(touched, simple.tuple_point_count);
        grow(composite_headers, composite.tuple_header_count);
        grow(composite_regions, composite.region_coordinate_count);
        grow(composite_shared, composite.point_number_count);
        grow(composite_private, composite.point_number_count);
        grow(composite_x, composite.delta_count);
        grow(composite_y, composite.delta_count);
    }

    text::sfnt_varied_glyph_scratch borrow(const text::sfnt_varied_glyph_requirements& requirements) noexcept {
        const auto& simple = requirements.simple_variation;
        const auto& composite = requirements.composite_variation;
        return {
            std::span{contours}.first(requirements.outline.simple_contour_scratch_count),
            std::span{original_points}.first(requirements.outline.simple_point_scratch_count),
            std::span{varied_points}.first(requirements.varied_simple_point_count),
            std::span{component_offsets}.first(requirements.component_offset_count),
            {std::span{simple_headers}.first(simple.tuple_header_count),
                std::span{simple_regions}.first(simple.region_coordinate_count),
                std::span{simple_shared}.first(simple.point_number_count),
                std::span{simple_private}.first(simple.point_number_count),
                std::span{simple_x}.first(simple.delta_count),
                std::span{simple_y}.first(simple.delta_count),
                std::span{tuple_x}.first(simple.tuple_point_count),
                std::span{tuple_y}.first(simple.tuple_point_count),
                std::span{touched}.first(simple.tuple_point_count)},
            {std::span{composite_headers}.first(composite.tuple_header_count),
                std::span{composite_regions}.first(composite.region_coordinate_count),
                std::span{composite_shared}.first(composite.point_number_count),
                std::span{composite_private}.first(composite.point_number_count),
                std::span{composite_x}.first(composite.delta_count),
                std::span{composite_y}.first(composite.delta_count)}};
    }
};

outline_storage& thread_outline_storage() {
    thread_local outline_storage storage;
    return storage;
}

// Decodes the outline of the glyph into the storage; the source follows the tables of the face
// (glyf, with gvar at a varied instance; else CFF; else CFF2).
bool decode_outline(const text::sfnt_font_view& font, std::uint16_t glyph,
    std::span<const std::int16_t> coordinates, outline_storage& storage, std::uint32_t& segments_written) {
    segments_written = 0U;
    std::uint16_t glyph_count = 0U;
    if (!font.try_get_glyph_count(glyph_count) || glyph >= glyph_count) return false;

    text::sfnt_table_view glyf{};
    text::sfnt_table_view loca{};
    if (font.try_get_table(glyf_tag, glyf) && font.try_get_table(loca_tag, loca)) {
        std::uint32_t points_written = 0U;
        text::sfnt_table_view gvar{};
        if (any_nonzero(coordinates) && font.try_get_table(gvar_tag, gvar)) {
            text::sfnt_varied_glyph_requirements requirements{};
            if (!font.try_get_varied_glyph_requirements(glyph, requirements)) return false;
            storage.reserve(requirements);
            return font.try_decode_varied_glyph_outline(glyph, coordinates, storage.borrow(requirements),
                std::span{storage.points}.first(requirements.outline.point_count),
                std::span{storage.segments}.first(requirements.outline.path_segment_count), points_written,
                segments_written);
        }
        text::sfnt_expanded_glyph_requirements requirements{};
        if (!font.try_get_expanded_glyph_requirements(glyph, requirements)) return false;
        outline_storage::grow(storage.contours, requirements.simple_contour_scratch_count);
        outline_storage::grow(storage.original_points, requirements.simple_point_scratch_count);
        outline_storage::grow(storage.points, requirements.point_count);
        outline_storage::grow(storage.segments, requirements.path_segment_count);
        return font.try_decode_glyph_outline(glyph,
            std::span{storage.contours}.first(requirements.simple_contour_scratch_count),
            std::span{storage.original_points}.first(requirements.simple_point_scratch_count),
            std::span{storage.points}.first(requirements.point_count),
            std::span{storage.segments}.first(requirements.path_segment_count), points_written, segments_written);
    }

    text::sfnt_table_view table{};
    if (font.try_get_table(cff1_tag, table)) {
        text::sfnt_cff1_font_view cff{};
        text::sfnt_cff1_outline_requirements requirements{};
        if (!font.try_get_cff1_font(glyph_count, cff) ||
            !text::sfnt_cff_data::try_get_outline_requirements(cff, glyph, requirements)) {
            return false;
        }
        outline_storage::grow(storage.segments, requirements.path_segment_count);
        return text::sfnt_cff_data::try_decode_outline(cff, glyph,
            std::span{storage.segments}.first(requirements.path_segment_count), segments_written);
    }
    if (font.try_get_table(cff2_tag, table)) {
        text::sfnt_cff2_font_view cff{};
        text::sfnt_cff2_outline_requirements requirements{};
        if (!font.try_get_cff2_font(glyph_count, cff) ||
            !text::sfnt_cff_data::try_get_outline_requirements(cff, glyph, coordinates, requirements)) {
            return false;
        }
        outline_storage::grow(storage.segments, requirements.path_segment_count);
        return text::sfnt_cff_data::try_decode_outline(cff, glyph, coordinates,
            std::span{storage.segments}.first(requirements.path_segment_count), segments_written);
    }
    // A face without outlines (bitmap only): no segments.
    return true;
}

progpu_native_font_glyph_bounds control_box(std::span<const progpu_native_path_segment> segments) noexcept {
    float min_x = std::numeric_limits<float>::infinity();
    float min_y = std::numeric_limits<float>::infinity();
    float max_x = -std::numeric_limits<float>::infinity();
    float max_y = -std::numeric_limits<float>::infinity();
    bool any = false;
    const auto update = [&](progpu_native_point point) {
        if (!std::isfinite(point.x) || !std::isfinite(point.y)) return;
        min_x = std::min(min_x, point.x);
        min_y = std::min(min_y, point.y);
        max_x = std::max(max_x, point.x);
        max_y = std::max(max_y, point.y);
        any = true;
    };
    for (const auto& segment : segments) {
        update(segment.p0);
        update(segment.p1);
        if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC || segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
            update(segment.p2);
        }
        if (segment.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) update(segment.p3);
    }
    return any ? progpu_native_font_glyph_bounds{min_x, min_y, max_x, max_y} : progpu_native_font_glyph_bounds{};
}

progpu_native_font_catalog* discovered(progpu_native_font_catalog* catalog, bool ok) noexcept {
    if (catalog != nullptr && !ok) {
        delete catalog;
        return nullptr;
    }
    return catalog;
}

} // namespace

extern "C" {

progpu_native_font_catalog* progpu_native_font_catalog_create_system(void) {
    auto* catalog = new (std::nothrow) progpu_native_font_catalog();
    return discovered(catalog, catalog != nullptr && catalog->catalog.try_discover_system_fonts());
}

progpu_native_font_catalog* progpu_native_font_catalog_create(const char* const* directories,
    const size_t* directory_sizes, size_t directory_count) {
    try {
        std::vector<std::string_view> views;
        views.reserve(directory_count);
        for (size_t index = 0U; index < directory_count; ++index) {
            views.push_back(view_of(directories[index], directory_sizes[index]));
        }
        auto* catalog = new (std::nothrow) progpu_native_font_catalog();
        return discovered(catalog, catalog != nullptr && catalog->catalog.try_discover_fonts(views));
    } catch (...) {
        return nullptr;
    }
}

void progpu_native_font_catalog_destroy(progpu_native_font_catalog* catalog) { delete catalog; }

uint32_t progpu_native_font_catalog_face_count(const progpu_native_font_catalog* catalog) {
    return catalog->catalog.face_count();
}

int progpu_native_font_catalog_get_face_info(const progpu_native_font_catalog* catalog, uint32_t index,
    progpu_native_font_face_info* info) {
    text::font_catalog_face_info face{};
    if (!catalog->catalog.try_get_face_info(index, face)) return 0;
    info->family_name = face.family_name.data();
    info->family_name_size = face.family_name.size();
    info->full_name = face.full_name.data();
    info->full_name_size = face.full_name.size();
    info->file_path = face.file_path.data();
    info->file_path_size = face.file_path.size();
    info->identity = face.identity;
    info->family_identity = face.family_identity;
    info->face_index = face.face_index;
    info->weight = face.weight;
    info->width = face.width;
    info->slant = static_cast<uint8_t>(face.slant);
    return 1;
}

int progpu_native_font_catalog_match_family(const progpu_native_font_catalog* catalog, const char* family,
    size_t family_size, const progpu_native_font_style* style, uint32_t* index) {
    return result(catalog->catalog.try_match_family(view_of(family, family_size), request_of(style), *index));
}

int progpu_native_font_catalog_match_character(const progpu_native_font_catalog* catalog, const char* family,
    size_t family_size, const progpu_native_font_style* style, const char* language, size_t language_size,
    uint32_t code_point, uint64_t excluded_identity, uint32_t* index, uint16_t* glyph) {
    const std::string_view tag = view_of(language, language_size);
    const std::span<const std::string_view> tags =
        tag.empty() ? std::span<const std::string_view>{} : std::span<const std::string_view>{&tag, 1U};
    return result(catalog->catalog.try_match_character(view_of(family, family_size), request_of(style), tags,
        code_point, excluded_identity, *index, *glyph));
}

progpu_native_font_face* progpu_native_font_catalog_load_face(const progpu_native_font_catalog* catalog,
    uint32_t index) {
    auto* face = new (std::nothrow) progpu_native_font_face();
    if (face == nullptr) return nullptr;
    text::font_catalog_face_info info{};
    if (!catalog->catalog.try_load_face(index, face->loaded) || !face->loaded.valid() ||
        !catalog->catalog.try_get_face_info(index, info)) {
        delete face;
        return nullptr;
    }
    face->from_catalog = true;
    face->face_index = info.face_index;
    return face;
}

int progpu_native_font_data_face_count(const void* data, size_t size, uint32_t* face_count) {
    if (data == nullptr || size == 0U) return 0;
    return result(text::sfnt_font_view::try_get_face_count(
        std::span<const std::byte>{static_cast<const std::byte*>(data), size}, *face_count));
}

progpu_native_font_face* progpu_native_font_face_create(const void* data, size_t size, uint32_t face_index) {
    if (data == nullptr || size == 0U) return nullptr;
    try {
        auto* face = new (std::nothrow) progpu_native_font_face();
        if (face == nullptr) return nullptr;
        const auto* bytes = static_cast<const std::byte*>(data);
        face->bytes.assign(bytes, bytes + size);
        face->face_index = face_index;
        text::sfnt_header_metrics header{};
        if (!text::sfnt_font_view::try_create(face->bytes, face_index, face->view) ||
            !face->view.try_get_header_metrics(header) || header.units_per_em == 0U) {
            delete face;
            return nullptr;
        }
        return face;
    } catch (...) {
        return nullptr;
    }
}

void progpu_native_font_face_destroy(progpu_native_font_face* face) { delete face; }

int progpu_native_font_face_get_data(const progpu_native_font_face* face, const void** data, size_t* size,
    uint32_t* face_index) {
    const auto bytes = face->data();
    *data = bytes.data();
    *size = bytes.size();
    *face_index = face->face_index;
    return result(!bytes.empty());
}

uint64_t progpu_native_font_face_identity(const progpu_native_font_face* face) {
    return face->from_catalog ? face->loaded.identity() : 0U;
}

int progpu_native_font_face_get_table(const progpu_native_font_face* face, uint32_t tag, const void** data,
    size_t* size) {
    text::sfnt_table_view table{};
    if (!face->font().try_get_table(text::open_type_tag{tag}, table)) {
        *data = nullptr;
        *size = 0U;
        return 0;
    }
    *data = table.bytes.data();
    *size = table.bytes.size();
    return 1;
}

int progpu_native_font_face_get_metrics(const progpu_native_font_face* face,
    progpu_native_font_face_metrics* metrics) {
    const auto& font = face->font();
    *metrics = {};
    text::sfnt_header_metrics header{};
    if (!font.try_get_header_metrics(header)) return 0;
    metrics->units_per_em = header.units_per_em;
    text::sfnt_horizontal_header_metrics horizontal{};
    if (font.try_get_horizontal_header_metrics(horizontal)) {
        metrics->ascender = horizontal.ascender;
        metrics->descender = horizontal.descender;
        metrics->line_gap = horizontal.line_gap;
    }
    std::uint16_t glyph_count = 0U;
    if (font.try_get_glyph_count(glyph_count)) metrics->glyph_count = glyph_count;
    text::sfnt_face_style style{};
    if (font.try_get_face_style(style)) {
        metrics->weight = style.weight;
        metrics->width = style.width;
        metrics->italic = style.italic ? 1U : 0U;
    } else {
        metrics->weight = 400U;
        metrics->width = 5U;
    }
    std::uint16_t axis_count = 0U;
    if (font.try_get_variation_axis_count(axis_count)) metrics->variation_axis_count = axis_count;
    text::sfnt_table_view table{};
    text::sfnt_table_view loca{};
    if (font.try_get_table(glyf_tag, table) && font.try_get_table(loca_tag, loca)) {
        metrics->outline_format = PROGPU_NATIVE_FONT_OUTLINE_TRUE_TYPE;
    } else if (font.try_get_table(cff1_tag, table)) {
        metrics->outline_format = PROGPU_NATIVE_FONT_OUTLINE_CFF;
    } else if (font.try_get_table(cff2_tag, table)) {
        metrics->outline_format = PROGPU_NATIVE_FONT_OUTLINE_CFF2;
    }
    return 1;
}

int progpu_native_font_face_get_name(const progpu_native_font_face* face, uint16_t name_id, char* utf8,
    size_t capacity, size_t* size) {
    text::sfnt_name_requirements requirements{};
    *size = 0U;
    if (!face->font().try_get_name_requirements(name_id, requirements)) return 0;
    *size = requirements.utf8_bytes;
    if (utf8 == nullptr || capacity < requirements.utf8_bytes) return 0;
    std::size_t written = 0U;
    if (!face->font().try_decode_name(name_id, std::span<char>{utf8, capacity}, written)) return 0;
    *size = written;
    return 1;
}

int progpu_native_font_face_get_glyph_index(const progpu_native_font_face* face, uint32_t code_point,
    uint16_t* glyph) {
    return result(face->font().try_get_glyph_index(code_point, *glyph));
}

int progpu_native_font_face_get_advance(const progpu_native_font_face* face, uint16_t glyph,
    const int16_t* coordinates, size_t coordinate_count, float* advance) {
    return result(face->font().try_get_design_advance_width(glyph, coordinates_of(coordinates, coordinate_count),
        *advance));
}

int progpu_native_font_face_resolve_style(const progpu_native_font_face* face,
    const progpu_native_font_style* style, int16_t* coordinates, size_t capacity, size_t* written,
    uint32_t* applied) {
    const auto& font = face->font();
    *written = 0U;
    if (applied != nullptr) *applied = 0U;
    std::uint16_t axis_count = 0U;
    if (!font.try_get_variation_axis_count(axis_count) || axis_count == 0U) return 1;
    *written = axis_count;
    if (coordinates == nullptr || capacity < axis_count) return 0;
    std::fill_n(coordinates, axis_count, static_cast<int16_t>(0));
    const auto request = request_of(style);
    text::font_style_variation_requirements requirements{};
    if (!text::try_get_font_style_variation_requirements(font, request, requirements)) return 0;
    if (requirements.setting_count == 0U) return 1;
    text::font_style_variation settings[8]{};
    if (requirements.setting_count > 8U) return 0;
    std::uint16_t setting_count = 0U;
    if (!text::try_resolve_font_style_variations(font, request,
            std::span<text::font_style_variation>{settings, requirements.setting_count}, setting_count)) {
        return 0;
    }
    std::uint32_t bits = 0U;
    for (std::uint16_t index = 0U; index < setting_count; ++index) {
        const auto& setting = settings[index];
        if (setting.axis_index >= axis_count) return 0;
        coordinates[setting.axis_index] = setting.normalized;
        if (setting.tag == wght_tag) bits |= 1U;
        else if (setting.tag == wdth_tag) bits |= 2U;
        else if (setting.tag == ital_tag) bits |= 4U;
        else if (setting.tag == slnt_tag) bits |= 8U;
        else if (setting.tag == opsz_tag) bits |= 16U;
    }
    if (applied != nullptr) *applied = bits;
    return 1;
}

int progpu_native_font_face_get_outline(const progpu_native_font_face* face, uint16_t glyph,
    const int16_t* coordinates, size_t coordinate_count, const progpu_native_path_segment** segments,
    size_t* segment_count, progpu_native_font_glyph_bounds* bounds) {
    *segments = nullptr;
    *segment_count = 0U;
    if (bounds != nullptr) *bounds = {};
    try {
        auto& storage = thread_outline_storage();
        std::uint32_t written = 0U;
        if (!decode_outline(face->font(), glyph, coordinates_of(coordinates, coordinate_count), storage, written)) {
            return 0;
        }
        const auto decoded = std::span<const progpu_native_path_segment>{storage.segments}.first(written);
        *segments = decoded.data();
        *segment_count = decoded.size();
        if (bounds != nullptr) *bounds = control_box(decoded);
        return 1;
    } catch (...) {
        return 0;
    }
}

int progpu_native_font_outline_embolden(const progpu_native_path_segment* source, size_t source_count,
    const progpu_native_font_outline_growth* growth, const progpu_native_path_segment** segments,
    size_t* segment_count, progpu_native_font_glyph_bounds* bounds) {
    if (segments != nullptr) *segments = nullptr;
    if (segment_count != nullptr) *segment_count = 0U;
    if (bounds != nullptr) *bounds = {};
    if (growth == nullptr || segments == nullptr || segment_count == nullptr) return 0;
    if (source == nullptr && source_count != 0U) return 0;
    try {
        const std::span<const progpu_native_path_segment> outline{source, source_count};
        // Storage of its own: the source is usually what get_outline lent.
        thread_local std::vector<progpu_native_path_segment> grown;
        const text::outline_growth sides{growth->left, growth->bottom, growth->right, growth->top};
        const auto result = text::embolden_outline(outline, sides, grown)
            ? std::span<const progpu_native_path_segment>{grown}
            : outline;
        *segments = result.data();
        *segment_count = result.size();
        if (bounds != nullptr) *bounds = control_box(result);
        return 1;
    } catch (...) {
        return 0;
    }
}

} // extern "C"
