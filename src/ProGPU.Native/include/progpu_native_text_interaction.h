#ifndef PROGPU_NATIVE_TEXT_INTERACTION_H
#define PROGPU_NATIVE_TEXT_INTERACTION_H
#include "progpu_native.h"

#ifdef __cplusplus
extern "C" {
#endif

/* PROGPU_CSHARP_STRUCT: Public.NativeTextClusterBox */
typedef struct progpu_native_text_cluster_box {
    int32_t input_start;
    int32_t input_end;
    uint32_t line_index;
    int8_t bidi_level;
    uint8_t reserved0;
    uint8_t reserved1;
    uint8_t reserved2;
    float x;
    float y;
    float width;
    float height;
} progpu_native_text_cluster_box;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextCaretStop */
typedef struct progpu_native_text_caret_stop {
    int32_t input_position;
    uint32_t line_index;
    float x;
    float y;
    float height;
    int8_t bidi_level;
    uint8_t trailing;
    uint8_t reserved0;
    uint8_t reserved1;
} progpu_native_text_caret_stop;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextRectangle */
typedef struct progpu_native_text_rectangle {
    float x;
    float y;
    float width;
    float height;
} progpu_native_text_rectangle;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextHitTestResult */
typedef struct progpu_native_text_hit_test_result {
    int32_t input_position;
    uint32_t line_index;
    /* PROGPU_CSHARP_TYPE: NativeTextRectangle */
    progpu_native_text_rectangle bounds;
    int8_t bidi_level;
    uint8_t trailing;
    uint8_t inside;
    uint8_t reserved0;
} progpu_native_text_hit_test_result;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextInteractionRequest */
typedef struct progpu_native_text_interaction_request {
    uint32_t struct_size;
    uint32_t abi_version;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_positioned_text_glyph* glyphs;
    uint32_t glyph_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const progpu_native_positioned_text_line* lines;
    uint32_t line_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const int32_t* cluster_ends;
    uint32_t cluster_end_count;
    /* PROGPU_CSHARP_TYPE: nuint */
    const int8_t* bidi_levels;
    uint32_t bidi_level_count;
} progpu_native_text_interaction_request;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextInteractionRequirements */
typedef struct progpu_native_text_interaction_requirements {
    uint32_t struct_size;
    uint32_t cluster_box_capacity;
    uint32_t caret_stop_capacity;
    uint32_t error_code;
} progpu_native_text_interaction_requirements;

/* PROGPU_CSHARP_STRUCT: Public.NativeTextInteractionResult */
typedef struct progpu_native_text_interaction_result {
    uint32_t struct_size;
    uint32_t cluster_box_count;
    uint32_t caret_stop_count;
    uint32_t error_code;
} progpu_native_text_interaction_result;

/* Synchronous, allocation-free access to the shared native horizontal text
 * interaction algorithms. Inputs and outputs are borrowed, aligned and must
 * not overlap. Metadata is per positioned glyph, not per Unicode scalar:
 * cluster_ends uses original UTF input offsets, bidi_levels resolved levels.
 * No hidden glyph/font handles or device are required. Capacity failure leaves
 * output arrays untouched and publishes zero counts. Query outputs are cleared
 * on failure. Only successful results/counts may be consumed. */
PROGPU_NATIVE_API progpu_native_status progpu_native_text_interaction_get_requirements(
    const progpu_native_text_interaction_request* request,
    progpu_native_text_interaction_requirements* result);
PROGPU_NATIVE_API progpu_native_status progpu_native_text_interaction_build(
    const progpu_native_text_interaction_request* request,
    progpu_native_text_cluster_box* boxes, uint32_t box_capacity,
    progpu_native_text_caret_stop* carets, uint32_t caret_capacity,
    progpu_native_text_interaction_result* result);
PROGPU_NATIVE_API progpu_native_status progpu_native_text_interaction_hit_test(
    const progpu_native_text_cluster_box* boxes, uint32_t count, float x, float y,
    progpu_native_text_hit_test_result* result);
PROGPU_NATIVE_API progpu_native_status progpu_native_text_interaction_get_caret(
    const progpu_native_text_caret_stop* carets, uint32_t count,
    int32_t position, uint8_t trailing, progpu_native_text_caret_stop* result);
PROGPU_NATIVE_API progpu_native_status progpu_native_text_interaction_move_caret(
    const progpu_native_text_caret_stop* carets, uint32_t count,
    int32_t position, uint8_t trailing, int32_t direction,
    progpu_native_text_caret_stop* result);
PROGPU_NATIVE_API progpu_native_status progpu_native_text_interaction_get_selection(
    const progpu_native_text_cluster_box* boxes, uint32_t count,
    int32_t start, int32_t end,
    progpu_native_text_rectangle* rectangles, uint32_t capacity, uint32_t* written);

#ifdef __cplusplus
}
#endif
#endif
