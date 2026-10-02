#pragma once

#include "progpu_native_text_source.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Original post-GSUB/pre-GPOS state for one run. A zero count explicitly means
 * the placement family is unavailable; it is not an empty/safe recipe. Prepared
 * records use wire Y-down units and original descriptor order, before placement.
 * No borrowed plan/context, native font handle or pointer identity is exported. */
/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourcePositioningRun */
typedef struct progpu_native_hinted_source_positioning_run {
    uint32_t prepared_start;
    uint32_t prepared_count;
    uint32_t reserved0;
    uint32_t reserved1;
} progpu_native_hinted_source_positioning_run;

/* One original prepared-range placement replay used by a final source line.
 * The logical range is contiguous within the original run. Every corresponding
 * owner descriptor belongs to exactly one occurrence in the prepared range. */
/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceFittingSlice */
typedef struct progpu_native_hinted_source_fitting_slice {
    uint32_t run_index;
    uint32_t prepared_start;
    uint32_t prepared_count;
    uint32_t logical_start;
    uint32_t logical_count;
    uint32_t reserved;
} progpu_native_hinted_source_fitting_slice;

/* Logical partition indices are relative to first_logical_glyph. */
/* PROGPU_CSHARP_STRUCT: Public.NativeHintedSourceFittedLine */
typedef struct progpu_native_hinted_source_fitted_line {
    uint32_t glyph_start;
    uint32_t glyph_count;
    double width;
    uint32_t clipped;
    uint32_t reserved;
} progpu_native_hinted_source_fitted_line;

/* Version 2 source extension to the UNCHANGED flat outline/font resource view.
 * Raw and effective records are both original-owner indexed, physical 26.6,
 * wire Y-down, BEFORE the selected source advance/offset policies. They never
 * replace the base view's original logical records. Source doubles, fitting
 * witnesses, actual writer output and interaction belong to this same immutable
 * generation. Counts not repeated below come from source/base view identity.
 * slice_indices has logical_count entries; UINT32_MAX means unchanged raw
 * placement, otherwise it names the exact retained slice covering that slot.
 * Imported records own copies; import does not reshape, hint or use a driver. */
/* PROGPU_CSHARP_STRUCT: NativeMethods.HintedSourceGlyphResourceView */
typedef struct progpu_native_hinted_source_glyph_resource_view {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t version;
    uint32_t flags;
    /* PROGPU_CSHARP_TYPE: HintedSourceParagraphView */
    progpu_native_hinted_source_paragraph_view source;
    uint32_t first_logical_glyph;
    uint32_t prepared_count;
    uint32_t slice_count;
    uint32_t reserved;
    /* PROGPU_CSHARP_TYPE: NativeHintedSourceIntrinsicWidths */
    progpu_native_hinted_source_intrinsic_widths intrinsic_widths;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_shaping_glyph* raw_logical_glyphs;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_shaping_glyph* effective_logical_glyphs;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_source_positioning_run* positioning_runs;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_text_shaping_glyph* prepared_glyphs;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_source_fitting_slice* slices;
    /* PROGPU_CSHARP_TYPE: nuint */
    const uint32_t* slice_indices;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_source_fitted_line* fitted_lines;
} progpu_native_hinted_source_glyph_resource_view;

/* Version 2 batch input. raster is mandatory; source is NULL only for the old
 * raw resource contract. A mixed raw/source batch is one atomic transaction,
 * never a sequence of separately committed old/new updates. Original resource
 * indices in bindings address this array unchanged. All borrowed views and
 * arrays remain alive until the synchronous import returns. */
/* PROGPU_CSHARP_STRUCT: NativeMethods.HintedGlyphResourceInput */
typedef struct progpu_native_hinted_glyph_resource_input {
    uint32_t abi_version;
    uint32_t struct_size;
    uint32_t version;
    uint32_t reserved;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_glyph_resource_view* raster;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_hinted_source_glyph_resource_view* source;
} progpu_native_hinted_glyph_resource_input;

/* Producer-only borrow under the same destruction-excluding resource lease as
 * the base view. Unknown/missing source capability returns Unsupported; all
 * failures leave outputs and immutable reachable storage untouched. */
PROGPU_NATIVE_API progpu_native_status progpu_native_hinted_glyph_resource_borrow_source(
    const progpu_native_hinted_glyph_resource* resource,
    progpu_native_hinted_source_glyph_resource_view* view);

/* Paired optional source-resource capability. Both providers validate/copy all
 * original bytes, source metadata and bindings before the existing MIL staged
 * update publishes. Old raw apply, wire layouts, providers and defaults remain
 * unchanged. Unsupported forms fail explicitly; no foreign handle crosses. */
PROGPU_NATIVE_API progpu_native_mil_status progpu_native_mil_channel_apply_with_source_glyph_resources(
    progpu_native_mil_channel* channel,
    const uint8_t* batch_bytes, size_t batch_size,
    const progpu_native_hinted_glyph_resource_input* resources, uint32_t resource_count,
    const progpu_native_mil_hinted_glyph_binding* bindings, uint32_t binding_count,
    const uint32_t* positioned_indices, uint32_t positioned_index_count);

#ifdef __cplusplus
}
#endif
