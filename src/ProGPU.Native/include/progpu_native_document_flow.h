#ifndef PROGPU_NATIVE_DOCUMENT_FLOW_H
#define PROGPU_NATIVE_DOCUMENT_FLOW_H
#include "progpu_native.h"
#ifdef __cplusplus
extern "C" {
#endif

/* Preorder forest. parent_index == UINT32_MAX denotes a root. subtree_end is
 * exclusive. Containers have no lines; line ranges partition the supplied line
 * array in preorder. Insets are resolved padding plus border, not margins.
 * All metrics are finite nonnegative DIPs. Negative margins, fixed-height boxes,
 * floats, columns, pagination and bidirectional block ordering are not implied. */
/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentBlock */
typedef struct progpu_native_document_block {
    uint32_t parent_index;
    uint32_t subtree_end;
    uint32_t line_start;
    uint32_t line_count;
    double margin_left;
    double margin_top;
    double margin_right;
    double margin_bottom;
    double inset_left;
    double inset_top;
    double inset_right;
    double inset_bottom;
} progpu_native_document_block;

/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentLine */
typedef struct progpu_native_document_line {
    double width;
    double height;
} progpu_native_document_line;

/* A real source-measured non-text block. Entries are strictly ordered by block
 * index and target line-free leaves only. Width/height are finite nonnegative
 * DIPs, measured after resolve_widths. Zero size is an actual empty object, not
 * a transparent container through which adjoining margins may collapse.
 * Source retains object identity, drawing and text-position ownership. */
/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentObject */
typedef struct progpu_native_document_object {
    uint32_t block_index;
    uint32_t reserved;
    double width;
    double height;
} progpu_native_document_object;

/* Content box, excluding margins and insets. Width-only resolution sets Y and
 * Height to zero. A zero Width is a real exhausted constraint, never unbounded. */
/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentBox */
typedef struct progpu_native_document_box {
    double x;
    double y;
    double width;
    double height;
} progpu_native_document_box;

/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentLinePosition */
typedef struct progpu_native_document_line_position {
    double x;
    double y;
} progpu_native_document_line_position;

/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentFlowResult */
typedef struct progpu_native_document_flow_result {
    uint32_t struct_size;
    uint32_t block_count;
    uint32_t line_count;
    uint32_t reserved;
    double width;
    double height;
} progpu_native_document_flow_result;

/* Source-resolved break opportunities over already formatted lines. Boolean
 * fields are 0/1; forced page and column breaks are mutually exclusive and
 * override allow_break_before. First-line force flags do not create blank pages.
 * space_before applies inside a fragment; leading_space replaces it at the
 * start of each fragment, including the first. All distances are finite,
 * nonnegative DIPs; height is positive. Source policy resolves keep/widow/orphan
 * constraints into opportunities; this layer never guesses them from glyphs. */
/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentFragmentLine */
typedef struct progpu_native_document_fragment_line {
    uint32_t allow_break_before;
    uint32_t force_column_before;
    uint32_t force_page_before;
    uint32_t reserved;
    double height;
    double space_before;
    double leading_space;
} progpu_native_document_fragment_line;

/* Zero-based page/column; Y is relative to the column content origin. */
/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentFragmentPosition */
typedef struct progpu_native_document_fragment_position {
    uint32_t page;
    uint32_t column;
    double y;
} progpu_native_document_fragment_position;

/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentPaginationResult */
typedef struct progpu_native_document_pagination_result {
    uint32_t struct_size;
    uint32_t line_count;
    uint32_t fragment_count;
    uint32_t page_count;
} progpu_native_document_pagination_result;

/* Uniform-height, sequential columns. Takes the furthest fitting legal break
 * before the next forced boundary. Forced page breaks skip remaining columns.
 * A non-fitting indivisible range returns UNSUPPORTED: no line clipping,
 * emergency break or constraint relaxation is implicit. Empty input has zero
 * pages/fragments. Column count is 1..1024; content height is finite positive.
 * Same span/alias/budget/failure-publication rules as arrangement below.
 * Does not resolve widths, balance columns, split box decorations, shape text,
 * or retain document/font objects. Source consumers must implement those policies
 * explicitly before claiming a complete paginated document viewer. */
PROGPU_NATIVE_API progpu_native_status progpu_native_document_paginate(
    const progpu_native_document_fragment_line* lines, uint32_t line_count,
    double content_height, uint32_t column_count,
    progpu_native_document_fragment_position* positions, uint32_t position_capacity,
    progpu_native_document_pagination_result* result);

/* Synchronous borrowed spans; no pointers retained and no device required.
 * Arrays are aligned, disjoint and bounded to 1,048,576 elements, depth <= 128.
 * All outputs remain untouched on failure. One box per input node is published
 * on success. Width resolution ignores line ranges: format real paragraphs at
 * those widths, then arrange their source-owned line metrics without reshaping.
 * Caller initializes result.struct_size. Sibling margins collapse by max;
 * zero-inset container edges collapse with children, including empty containers.
 * Root outer margins remain part of the document extent. Extent width is at
 * least the requested width and includes overflowing lines and ancestor right
 * insets/margins. Line height is positive; width may be zero. Empty input yields
 * the requested width and zero height. */
PROGPU_NATIVE_API progpu_native_status progpu_native_document_resolve_widths(
    const progpu_native_document_block* blocks, uint32_t block_count, double width,
    progpu_native_document_box* boxes, uint32_t box_capacity);

PROGPU_NATIVE_API progpu_native_status progpu_native_document_arrange(
    const progpu_native_document_block* blocks, uint32_t block_count, double width,
    const progpu_native_document_line* lines, uint32_t line_count,
    progpu_native_document_box* boxes, uint32_t box_capacity,
    progpu_native_document_line_position* positions, uint32_t position_capacity,
    progpu_native_document_flow_result* result);

/* Same placement and failure-publication contract, with explicit measured
 * non-text leaves. Object metrics never enter the paragraph line array. Insets
 * and margins surround the measured content; following blocks move by its real
 * height, and overflow contributes to extent width. Object geometry is returned
 * in its existing block box. This does not implement inline objects, floats,
 * tables, fragmentation, source UI measurement or text editing semantics. */
PROGPU_NATIVE_API progpu_native_status progpu_native_document_arrange_with_objects(
    const progpu_native_document_block* blocks, uint32_t block_count, double width,
    const progpu_native_document_line* lines, uint32_t line_count,
    const progpu_native_document_object* objects, uint32_t object_count,
    progpu_native_document_box* boxes, uint32_t box_capacity,
    progpu_native_document_line_position* positions, uint32_t position_capacity,
    progpu_native_document_flow_result* result);
#ifdef __cplusplus
}
#endif
#endif
