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
 * floats, page columns, pagination and bidirectional block ordering are not implied. */
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

/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentAnchorWidthRequest */
typedef struct progpu_native_document_anchor_width_request {
    float available_width;
    float horizontal_insets;
    float specified_width;
    float measured_width;
    uint32_t mode;
    uint32_t has_measurement;
} progpu_native_document_anchor_width_request;

/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentAnchorWidthResult */
typedef struct progpu_native_document_anchor_width_result {
    float content_width;
    float outer_width;
    uint32_t requires_remeasure;
    uint32_t reserved;
} progpu_native_document_anchor_width_result;

/* Synchronous batch over source-resolved width policy. mode: 0=fixed, 1=fill,
 * 2=fit-content; has_measurement is exactly 0/1. Metrics are finite nonnegative.
 * Borrowed aligned disjoint spans, count <= 1,048,576. No device/allocation.
 * All outputs remain untouched on any failure, including a later invalid item.
 * Output has one entry per request; reserved is zero. Does not measure children. */
PROGPU_NATIVE_API progpu_native_status progpu_native_document_resolve_anchor_widths(
    const progpu_native_document_anchor_width_request* requests, uint32_t count,
    progpu_native_document_anchor_width_result* results, uint32_t capacity);

/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentAnchorRequest */
typedef struct progpu_native_document_anchor_request {
    float left;
    float top;
    float right;
    float bottom;
    float width;
    float height;
    uint32_t alignment;
    uint32_t allow_delay;
    uint32_t maximum_attempts;
    uint32_t reserved;
} progpu_native_document_anchor_request;

/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentAnchorRectangle */
typedef struct progpu_native_document_anchor_rectangle {
    float left;
    float top;
    float right;
    float bottom;
} progpu_native_document_anchor_rectangle;

/* Place positive-size measured outer boxes in source order. Each accepted box
 * joins the supplied exclusions for subsequent requests. Alignment 0/1/2 means
 * left/center/right; allow_delay is 0/1; reserved must be zero. Each request owns
 * an explicit finite reference and attempt budget. Combined count <= 1,048,576.
 * Disjoint borrowed spans. Bounded O(E+N) temporary storage per changed batch;
 * no GPU, child measurement or per-anchor allocation. Outputs publish atomically.
 * UNSUPPORTED means no fit/budget exhausted, not permission to clip or overlap.
 * This is box collision placement, not source wrap-side or pagination policy. */
PROGPU_NATIVE_API progpu_native_status progpu_native_document_place_anchors(
    const progpu_native_document_anchor_request* requests, uint32_t count,
    const progpu_native_document_anchor_rectangle* exclusions, uint32_t exclusion_count,
    progpu_native_document_anchor_rectangle* results, uint32_t capacity);

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

/* Horizontal row in the existing preorder tree. Column widths are fixed outer
 * cell-track widths (including cell insets, excluding spacing), in DIPs.
 * Rows may reuse an identical column slice; distinct slices must be disjoint.
 * Half a spacing unit surrounds the row, with full spacing between cells/rows.
 * Sorted unique block indices. All direct row children must be declared cells.
 * This contract does not infer automatic widths or row-spanning cells. */
/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentRow */
typedef struct progpu_native_document_row {
    uint32_t block_index;
    uint32_t column_start;
    uint32_t column_count;
    uint32_t reserved;
    double cell_spacing;
} progpu_native_document_row;

/* Cell is a direct child of rows[row_index]. column_start is relative to that
 * row's column slice. Positive spans may leave unused tracks but never overlap;
 * cells follow increasing column order in each row. Nested rows remain valid.
 * Cell contents retain the ordinary block/line/object flow and source identity. */
/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentCell */
typedef struct progpu_native_document_cell {
    uint32_t block_index;
    uint32_t row_index;
    uint32_t column_start;
    uint32_t column_count;
} progpu_native_document_cell;

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

/* Explicit native paragraph extent. Sorted unique line-bearing leaf block
 * indices; the block's line range indexes paragraph-local positions. Width and
 * height include all fragment offsets/clearance, not a sum of fragment heights.
 * Each local line rectangle must fit this extent. Reserved must be zero. */
/* PROGPU_CSHARP_STRUCT: Public.NativeDocumentPositionedParagraph */
typedef struct progpu_native_document_positioned_paragraph {
    uint32_t block_index;
    uint32_t reserved;
    double width;
    double height;
} progpu_native_document_positioned_paragraph;

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
/* Batched row/cell placement within the same block forest. Columns are resolved
 * before formatting; rows measure the maximum cell outer height, not its sum.
 * Cell boxes stretch to the row height while their original lines stay top
 * aligned. Ordinary containers/objects, nested rows and all old entry points
 * retain their contracts. Rows cannot contain direct text or object metrics.
 * Complexity O(blocks + lines + columns + rows + cells), bounded temporary
 * storage, one crossing per width/arrange pass. Same atomic publication rules. */
PROGPU_NATIVE_API progpu_native_status progpu_native_document_resolve_widths_with_rows(
    const progpu_native_document_block* blocks, uint32_t block_count, double width,
    const progpu_native_document_row* rows, uint32_t row_count,
    const double* column_widths, uint32_t column_count,
    const progpu_native_document_cell* cells, uint32_t cell_count,
    progpu_native_document_box* boxes, uint32_t box_capacity);

PROGPU_NATIVE_API progpu_native_status progpu_native_document_arrange_with_rows(
    const progpu_native_document_block* blocks, uint32_t block_count, double width,
    const progpu_native_document_line* lines, uint32_t line_count,
    const progpu_native_document_object* objects, uint32_t object_count,
    const progpu_native_document_row* rows, uint32_t row_count,
    const double* column_widths, uint32_t column_count,
    const progpu_native_document_cell* cells, uint32_t cell_count,
    progpu_native_document_box* boxes, uint32_t box_capacity,
    progpu_native_document_line_position* positions, uint32_t position_capacity,
    progpu_native_document_flow_result* result);
/* Same shared row/block arrangement with explicit paragraph-local fragment
 * positions. local_position_count equals line_count when paragraph_count > 0,
 * otherwise zero. Entries for ordinary lines must be zero. All spans disjoint;
 * atomic publication and existing budgets apply. Source order is preserved,
 * including same-row RTL fragments. No reshaping or line-height prefix repair. */
PROGPU_NATIVE_API progpu_native_status progpu_native_document_arrange_with_positioned_paragraphs(
    const progpu_native_document_block* blocks, uint32_t block_count, double width,
    const progpu_native_document_line* lines, uint32_t line_count,
    const progpu_native_document_object* objects, uint32_t object_count,
    const progpu_native_document_row* rows, uint32_t row_count,
    const double* column_widths, uint32_t column_count,
    const progpu_native_document_cell* cells, uint32_t cell_count,
    const progpu_native_document_positioned_paragraph* paragraphs, uint32_t paragraph_count,
    const progpu_native_document_line_position* local_positions, uint32_t local_position_count,
    progpu_native_document_box* boxes, uint32_t box_capacity,
    progpu_native_document_line_position* positions, uint32_t position_capacity,
    progpu_native_document_flow_result* result);
#ifdef __cplusplus
}
#endif
#endif
