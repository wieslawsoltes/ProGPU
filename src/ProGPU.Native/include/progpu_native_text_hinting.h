#pragma once

#include <stdint.h>

/* Fixed-width snapshot records. These types do not create a font context,
 * advertise a provider capability, or admit source Display mode. */
typedef struct progpu_native_hinted_point {
    int64_t x_26_6;
    int64_t y_26_6;
} progpu_native_hinted_point;

typedef struct progpu_native_hinted_glyph {
    uint32_t glyph_index;
    uint32_t point_offset;
    uint32_t point_count;
    uint32_t contour_offset;
    uint32_t contour_count;
    uint32_t outline_flags;
    int64_t advance_x_26_6;
    int64_t advance_y_26_6;
    int64_t horizontal_bearing_x_26_6;
    int64_t horizontal_bearing_y_26_6;
    int64_t width_26_6;
    int64_t height_26_6;
    int64_t horizontal_advance_26_6;
    int64_t vertical_bearing_x_26_6;
    int64_t vertical_bearing_y_26_6;
    int64_t vertical_advance_26_6;
    int64_t linear_horizontal_advance_16_16;
    int64_t linear_vertical_advance_16_16;
    int64_t left_side_bearing_delta_26_6;
    int64_t right_side_bearing_delta_26_6;
} progpu_native_hinted_glyph;

typedef struct progpu_native_hinted_batch_counts {
    uint32_t glyphs;
    uint32_t points;
    uint32_t contours;
} progpu_native_hinted_batch_counts;
