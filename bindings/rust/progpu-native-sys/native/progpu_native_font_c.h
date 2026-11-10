/*
 * A C ABI over the engine's own font stack (progpu_native_text.hpp), for hosts that cannot call
 * C++: the system font catalog (discovery, family matching, character fallback matching, face
 * loading), faces from bytes a host supplies, SFNT tables, metrics, names, the character map,
 * advances, style variations and unhinted glyph outlines in font design units. No platform text
 * API and no hinting engine is involved: every answer comes from the engine's OpenType parsers.
 *
 * A function returns nonzero on success. A catalog is immutable after its creation and may be
 * read from several threads. A face is immutable and may be used from several threads; it keeps
 * the font bytes alive (a mapped file of the catalog, or a copy of the bytes it was created
 * from) until it is destroyed. Strings are UTF-8 with a byte count, without a terminator.
 */
#ifndef PROGPU_NATIVE_FONT_C_H
#define PROGPU_NATIVE_FONT_C_H

#include <stddef.h>
#include <stdint.h>

#include "progpu_native.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct progpu_native_font_catalog progpu_native_font_catalog;
typedef struct progpu_native_font_face progpu_native_font_face;

/* The slant of a style request and of a catalog face. */
#define PROGPU_NATIVE_FONT_SLANT_NORMAL 0u
#define PROGPU_NATIVE_FONT_SLANT_ITALIC 1u
#define PROGPU_NATIVE_FONT_SLANT_OBLIQUE 2u

/* A requested style: the OpenType weight (1 to 1000), the width class (1 to 9), the slant, and
 * the optical size in signed 16.16 user units (zero keeps the default of the face). */
typedef struct progpu_native_font_style {
    int32_t weight;
    int32_t width;
    uint32_t slant;
    int32_t optical_size_fixed;
} progpu_native_font_style;

/* One face of a catalog. The strings are owned by the catalog and live as long as it does. */
typedef struct progpu_native_font_face_info {
    const char* family_name;
    size_t family_name_size;
    const char* full_name;
    size_t full_name_size;
    const char* file_path;
    size_t file_path_size;
    uint64_t identity;
    uint64_t family_identity;
    uint32_t face_index;
    uint16_t weight;
    uint8_t width;
    uint8_t slant;
} progpu_native_font_face_info;

/* The metrics of a face, in font design units (Y up, as the font stores them). */
typedef struct progpu_native_font_face_metrics {
    uint16_t units_per_em;
    uint16_t glyph_count;
    int16_t ascender;
    int16_t descender;
    int16_t line_gap;
    uint16_t weight;
    uint16_t width;
    uint16_t variation_axis_count;
    uint32_t italic;
    uint32_t outline_format; /* PROGPU_NATIVE_FONT_OUTLINE_* */
} progpu_native_font_face_metrics;

#define PROGPU_NATIVE_FONT_OUTLINE_NONE 0u
#define PROGPU_NATIVE_FONT_OUTLINE_TRUE_TYPE 1u
#define PROGPU_NATIVE_FONT_OUTLINE_CFF 2u
#define PROGPU_NATIVE_FONT_OUTLINE_CFF2 3u

/* The control box of an outline, in font design units (Y up). */
typedef struct progpu_native_font_glyph_bounds {
    float x_min;
    float y_min;
    float x_max;
    float y_max;
} progpu_native_font_glyph_bounds;

/* The fonts of the system (the font directories of the platform), discovered once. NULL when
 * the discovery fails. */
progpu_native_font_catalog* progpu_native_font_catalog_create_system(void);
/* The fonts of the given directories (UTF-8 paths). NULL when the discovery fails. */
progpu_native_font_catalog* progpu_native_font_catalog_create(const char* const* directories,
    const size_t* directory_sizes, size_t directory_count);
void progpu_native_font_catalog_destroy(progpu_native_font_catalog* catalog);
uint32_t progpu_native_font_catalog_face_count(const progpu_native_font_catalog* catalog);
int progpu_native_font_catalog_get_face_info(const progpu_native_font_catalog* catalog, uint32_t index,
    progpu_native_font_face_info* info);
/* The face of the family that is nearest to the style. Fails when the catalog has no such
 * family. */
int progpu_native_font_catalog_match_family(const progpu_native_font_catalog* catalog, const char* family,
    size_t family_size, const progpu_native_font_style* style, uint32_t* index);
/* A face that has a glyph for the code point: of the family when it has one (the family may be
 * empty), else of the fallback families the engine prefers for the code point and the BCP-47
 * language (which may be empty), else of any face. A face with the excluded identity is skipped
 * (zero excludes none). */
int progpu_native_font_catalog_match_character(const progpu_native_font_catalog* catalog, const char* family,
    size_t family_size, const progpu_native_font_style* style, const char* language, size_t language_size,
    uint32_t code_point, uint64_t excluded_identity, uint32_t* index, uint16_t* glyph);
/* Loads a face of the catalog (its file is mapped and shared by the faces of the file). NULL on
 * failure. */
progpu_native_font_face* progpu_native_font_catalog_load_face(const progpu_native_font_catalog* catalog,
    uint32_t index);

/* The number of faces in font bytes (one for a single font, more for a collection). */
int progpu_native_font_data_face_count(const void* data, size_t size, uint32_t* face_count);
/* A face of font bytes a host supplies (an SFNT font or collection); the bytes are copied. NULL
 * when the bytes are not a font the engine reads. */
progpu_native_font_face* progpu_native_font_face_create(const void* data, size_t size, uint32_t face_index);
void progpu_native_font_face_destroy(progpu_native_font_face* face);
/* The bytes of the whole font file and the index of the face in it, borrowed from the face. */
int progpu_native_font_face_get_data(const progpu_native_font_face* face, const void** data, size_t* size,
    uint32_t* face_index);
/* The identity of the catalog face, or zero for a face from bytes. */
uint64_t progpu_native_font_face_identity(const progpu_native_font_face* face);
/* One SFNT table (the tag in OpenType byte order, 'head' = 0x68656164), borrowed from the face. */
int progpu_native_font_face_get_table(const progpu_native_font_face* face, uint32_t tag, const void** data,
    size_t* size);
int progpu_native_font_face_get_metrics(const progpu_native_font_face* face,
    progpu_native_font_face_metrics* metrics);
/* A name of the name table (1 family, 2 subfamily, 4 full, 6 PostScript, 16 and 17 the
 * typographic family and subfamily) as UTF-8. Writes the byte count the name needs to size;
 * fails when the face has no such name or the capacity is too small. */
int progpu_native_font_face_get_name(const progpu_native_font_face* face, uint16_t name_id, char* utf8,
    size_t capacity, size_t* size);
/* The glyph of a code point; fails when the face has none. */
int progpu_native_font_face_get_glyph_index(const progpu_native_font_face* face, uint32_t code_point,
    uint16_t* glyph);
/* The horizontal advance of a glyph in font design units, at the normalized variation
 * coordinates (F2DOT14, in the axis order of the face; none for the default instance). */
int progpu_native_font_face_get_advance(const progpu_native_font_face* face, uint16_t glyph,
    const int16_t* coordinates, size_t coordinate_count, float* advance);
/* The normalized variation coordinates of a style (weight, width, slant and optical size mapped
 * to the wght, wdth, ital, slnt and opsz axes of the face): one F2DOT14 value per axis of the
 * face, in its axis order. Writes the axis count to written; zero axes for a static face. Sets
 * the bits of applied for the axes the face has (1 weight, 2 width, 4 italic, 8 slant, 16
 * optical size), so a host knows what is left to simulate. */
int progpu_native_font_face_resolve_style(const progpu_native_font_face* face,
    const progpu_native_font_style* style, int16_t* coordinates, size_t capacity, size_t* written,
    uint32_t* applied);
/* The unhinted outline of a glyph in font design units (Y up), at the normalized variation
 * coordinates: TrueType (static and varied, composites expanded), CFF and CFF2. The segments
 * are borrowed from storage of the calling thread and are valid until its next outline call. A
 * glyph without an outline (a space) succeeds with zero segments. */
int progpu_native_font_face_get_outline(const progpu_native_font_face* face, uint16_t glyph,
    const int16_t* coordinates, size_t coordinate_count, const progpu_native_path_segment** segments,
    size_t* segment_count, progpu_native_font_glyph_bounds* bounds);
/* How far the edges of a glyph outline move outwards, by the side they face, in the units of
 * the outline (Y up). Negative values move them inwards. */
typedef struct progpu_native_font_outline_growth {
    float left;
    float bottom;
    float right;
    float top;
} progpu_native_font_outline_growth;
/* Grows one glyph outline by a box: every edge moves outwards by the growth of the side it
 * faces, so stems gain left + right, bars gain bottom + top, slanted strokes gain both and
 * counters lose the same; unequal sides also shift the glyph. A host uses it to give text the
 * weight of a platform's own rasterizer. The result has more segments than the source (curves
 * are cut where they turn, joints are closed along the box) and is meant for the nonzero
 * winding rule; it is borrowed from storage of the calling thread, valid until its next call of
 * this function, and it is the source itself when there is nothing to grow (no growth, an
 * outline without area). The source may be what get_outline lent. The grown segments are a
 * different raster identity, so a glyph cache keeps both weights apart by itself. */
int progpu_native_font_outline_embolden(const progpu_native_path_segment* source, size_t source_count,
    const progpu_native_font_outline_growth* growth, const progpu_native_path_segment** segments,
    size_t* segment_count, progpu_native_font_glyph_bounds* bounds);

#ifdef __cplusplus
}
#endif

#endif
