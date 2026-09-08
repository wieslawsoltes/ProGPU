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
#ifdef __cplusplus
}
#endif
#endif
