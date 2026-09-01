#include "progpu_native_direct2d_compat.hpp"
#include "progpu_native_direct2d_scene_submission.hpp"
#include "progpu_native.h"

#if defined(_WIN32)
#  include <dwrite.h>
#  include <d2d1.h>
#  include <wincodec.h>
#endif

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <limits>
#include <vector>

namespace compat = progpu::native::direct2d::compat;
namespace core = progpu::native::direct2d::core;
namespace com = progpu::native::com;

namespace {

[[nodiscard]] bool approximately_equal(float left, float right) noexcept
{
    return std::abs(left - right) <= 0.0001F;
}

class simplified_sink final : public compat::geometry_sink {
public:
    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(
                interface_id,
                compat::simplified_geometry_sink_interface_id) ||
            com::guid_equal(
                interface_id, compat::geometry_sink_interface_id)) {
            *value = static_cast<compat::geometry_sink*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    void PROGPU_NATIVE_COM_CALL SetFillMode(compat::fill_mode value)
        noexcept override
    {
        fill_mode = value;
    }

    void PROGPU_NATIVE_COM_CALL SetSegmentFlags(compat::path_segment value)
        noexcept override
    {
        segment_flags = value;
    }

    void PROGPU_NATIVE_COM_CALL BeginFigure(
        compat::point_2f start,
        compat::figure_begin begin) noexcept override
    {
        first = start;
        figure_begin = begin;
        if (begin_count < begin_points.size()) {
            begin_points[begin_count] = start;
            figure_begins[begin_count] = begin;
            begin_line_offsets[begin_count] = line_point_count;
        }
        ++begin_count;
    }

    void PROGPU_NATIVE_COM_CALL AddLines(
        const compat::point_2f* points,
        std::uint32_t point_count) noexcept override
    {
        line_count += point_count;
        if (points != nullptr && point_count != 0U) {
            for (std::uint32_t index = 0U;
                 index < point_count &&
                    line_point_count < line_points.size();
                 ++index) {
                line_points[line_point_count++] = points[index];
            }
            last = points[point_count - 1U];
        }
    }

    void PROGPU_NATIVE_COM_CALL AddBeziers(
        const compat::bezier_segment* beziers,
        std::uint32_t value_count) noexcept override
    {
        bezier_count += value_count;
        if (beziers != nullptr && value_count != 0U) {
            last = beziers[value_count - 1U].point3;
        }
    }

    void PROGPU_NATIVE_COM_CALL EndFigure(compat::figure_end end)
        noexcept override
    {
        figure_end = end;
        if (end_count < figure_ends.size()) {
            figure_ends[end_count] = end;
            end_line_offsets[end_count] = line_point_count;
        }
        ++end_count;
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        ++close_count;
        return com::ok;
    }

    void PROGPU_NATIVE_COM_CALL AddLine(compat::point_2f point)
        noexcept override
    {
        ++line_count;
        if (line_point_count < line_points.size()) {
            line_points[line_point_count++] = point;
        }
        last = point;
    }

    void PROGPU_NATIVE_COM_CALL AddBezier(
        const compat::bezier_segment* bezier) noexcept override
    {
        ++bezier_count;
        if (bezier != nullptr) {
            last = bezier->point3;
        }
    }

    void PROGPU_NATIVE_COM_CALL AddQuadraticBezier(
        const compat::quadratic_bezier_segment* bezier) noexcept override
    {
        ++quadratic_count;
        if (bezier != nullptr) {
            last = bezier->point2;
        }
    }

    void PROGPU_NATIVE_COM_CALL AddQuadraticBeziers(
        const compat::quadratic_bezier_segment* beziers,
        std::uint32_t value_count) noexcept override
    {
        quadratic_count += value_count;
        if (beziers != nullptr && value_count != 0U) {
            last = beziers[value_count - 1U].point2;
        }
    }

    void PROGPU_NATIVE_COM_CALL AddArc(
        const compat::arc_segment* arc) noexcept override
    {
        ++arc_count;
        if (arc != nullptr) {
            last = arc->point;
        }
    }

    compat::fill_mode fill_mode = compat::fill_mode::alternate;
    compat::path_segment segment_flags =
        compat::path_segment::force_unstroked;
    compat::figure_begin figure_begin = compat::figure_begin::hollow;
    compat::figure_end figure_end = compat::figure_end::open;
    compat::point_2f first{};
    compat::point_2f last{};
    std::array<compat::point_2f, 8U> begin_points{};
    std::array<compat::figure_begin, 8U> figure_begins{};
    std::array<compat::figure_end, 8U> figure_ends{};
    std::array<std::size_t, 8U> begin_line_offsets{};
    std::array<std::size_t, 8U> end_line_offsets{};
    std::array<compat::point_2f, 64U> line_points{};
    std::size_t line_point_count = 0U;
    std::uint32_t begin_count = 0U;
    std::uint32_t end_count = 0U;
    std::uint32_t line_count = 0U;
    std::uint32_t bezier_count = 0U;
    std::uint32_t quadratic_count = 0U;
    std::uint32_t arc_count = 0U;
    std::uint32_t close_count = 0U;

private:
    friend class com::atomic_reference_count<simplified_sink>;
    ~simplified_sink() = default;
    com::atomic_reference_count<simplified_sink> reference_count_;
};

[[nodiscard]] bool captured_fill_contains(
    const simplified_sink& sink,
    compat::point_2f point) noexcept
{
    bool alternate = false;
    std::int32_t winding = 0;
    const std::size_t figure_count = std::min<std::size_t>(
        sink.begin_count,
        std::min<std::size_t>(sink.end_count, sink.begin_points.size()));
    for (std::size_t figure = 0U; figure < figure_count; ++figure) {
        if (sink.figure_begins[figure] != compat::figure_begin::filled) {
            continue;
        }
        compat::point_2f start = sink.begin_points[figure];
        compat::point_2f previous = start;
        const auto visit_edge = [&](compat::point_2f end) {
            const bool upward = previous.y <= point.y && end.y > point.y;
            const bool downward = previous.y > point.y && end.y <= point.y;
            if (upward || downward) {
                const double cross =
                    (static_cast<double>(end.x) - previous.x) *
                        (static_cast<double>(point.y) - previous.y) -
                    (static_cast<double>(end.y) - previous.y) *
                        (static_cast<double>(point.x) - previous.x);
                if ((upward && cross > 0.0) ||
                    (downward && cross < 0.0)) {
                    alternate = !alternate;
                    winding += upward ? 1 : -1;
                }
            }
            previous = end;
        };
        for (std::size_t line = sink.begin_line_offsets[figure];
             line < sink.end_line_offsets[figure];
             ++line) {
            visit_edge(sink.line_points[line]);
        }
        visit_edge(start);
    }
    return sink.fill_mode == compat::fill_mode::alternate
        ? alternate
        : winding != 0;
}

#if defined(_WIN32)
[[nodiscard]] bool captured_boundaries_match(
    const simplified_sink& left,
    const simplified_sink& right) noexcept
{
    if (left.fill_mode != right.fill_mode ||
        left.segment_flags != right.segment_flags ||
        left.begin_count != right.begin_count ||
        left.end_count != right.end_count ||
        left.line_point_count != right.line_point_count ||
        left.line_point_count > left.line_points.size() ||
        right.line_point_count > right.line_points.size()) {
        return false;
    }
    struct captured_edge final {
        compat::point_2f start{};
        compat::point_2f end{};
        bool matched = false;
    };
    const auto collect_edges = [](const simplified_sink& sink,
                                  std::array<captured_edge, 64U>& edges,
                                  std::size_t& edge_count) {
        edge_count = 0U;
        const std::size_t figure_count = std::min<std::size_t>(
            sink.begin_count,
            std::min<std::size_t>(sink.end_count, sink.begin_points.size()));
        for (std::size_t figure = 0U; figure < figure_count; ++figure) {
            compat::point_2f previous = sink.begin_points[figure];
            for (std::size_t line = sink.begin_line_offsets[figure];
                 line < sink.end_line_offsets[figure];
                 ++line) {
                edges[edge_count++] = {
                    previous, sink.line_points[line], false};
                previous = sink.line_points[line];
            }
            if (sink.figure_ends[figure] == compat::figure_end::closed &&
                (!approximately_equal(previous.x, sink.begin_points[figure].x) ||
                    !approximately_equal(
                        previous.y, sink.begin_points[figure].y))) {
                edges[edge_count++] = {
                    previous, sink.begin_points[figure], false};
            }
        }
    };
    std::array<captured_edge, 64U> left_edges{};
    std::array<captured_edge, 64U> right_edges{};
    std::size_t left_count = 0U;
    std::size_t right_count = 0U;
    collect_edges(left, left_edges, left_count);
    collect_edges(right, right_edges, right_count);
    if (left_count != right_count) {
        return false;
    }
    const auto same_point_value = [](compat::point_2f first,
                                     compat::point_2f second) {
        return approximately_equal(first.x, second.x) &&
            approximately_equal(first.y, second.y);
    };
    for (std::size_t left_index = 0U; left_index < left_count; ++left_index) {
        bool found = false;
        for (std::size_t right_index = 0U;
             right_index < right_count;
             ++right_index) {
            if (right_edges[right_index].matched) {
                continue;
            }
            const bool forward = same_point_value(
                    left_edges[left_index].start,
                    right_edges[right_index].start) &&
                same_point_value(
                    left_edges[left_index].end,
                    right_edges[right_index].end);
            const bool reverse = same_point_value(
                    left_edges[left_index].start,
                    right_edges[right_index].end) &&
                same_point_value(
                    left_edges[left_index].end,
                    right_edges[right_index].start);
            if (forward || reverse) {
                right_edges[right_index].matched = true;
                found = true;
                break;
            }
        }
        if (!found) {
            return false;
        }
    }
    return true;
}
#endif

class triangle_sink final : public compat::tessellation_sink {
public:
    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id())) {
            *value = static_cast<compat::tessellation_sink*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    void PROGPU_NATIVE_COM_CALL AddTriangles(
        const compat::triangle* values,
        std::uint32_t triangle_count) noexcept override
    {
        count += triangle_count;
        if (values != nullptr && triangle_count != 0U) {
            first = values[0U];
        }
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        return com::ok;
    }

    compat::triangle first{};
    std::uint32_t count = 0U;

private:
    friend class com::atomic_reference_count<triangle_sink>;
    ~triangle_sink() = default;
    com::atomic_reference_count<triangle_sink> reference_count_;
};

class fake_wic_bitmap_source final : public compat::wic_bitmap_source {
public:
    explicit fake_wic_bitmap_source(com::guid pixel_format) noexcept
        : pixel_format_(pixel_format)
    {
    }

    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(
                interface_id, compat::wic_bitmap_source_interface_id)) {
            *value = static_cast<compat::wic_bitmap_source*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    com::result PROGPU_NATIVE_COM_CALL GetSize(
        std::uint32_t* width,
        std::uint32_t* height) noexcept override
    {
        if (width == nullptr || height == nullptr) {
            return com::invalid_argument;
        }
        *width = 2U;
        *height = 2U;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL GetPixelFormat(
        com::guid* pixel_format) noexcept override
    {
        if (pixel_format == nullptr) {
            return com::invalid_argument;
        }
        *pixel_format = pixel_format_;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL GetResolution(
        double* dpi_x,
        double* dpi_y) noexcept override
    {
        ++resolution_call_count;
        if (dpi_x == nullptr || dpi_y == nullptr) {
            return com::invalid_argument;
        }
        *dpi_x = 144.0;
        *dpi_y = 120.0;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CopyPalette(
        com::unknown*) noexcept override
    {
        return compat::not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL CopyPixels(
        const compat::wic_rectangle* rectangle,
        std::uint32_t stride,
        std::uint32_t buffer_size,
        std::uint8_t* buffer) noexcept override
    {
        ++copy_call_count;
        last_stride = stride;
        last_buffer_size = buffer_size;
        if (rectangle != nullptr || buffer == nullptr || stride < 8U ||
            buffer_size < stride + 8U) {
            return com::invalid_argument;
        }
        std::memcpy(buffer, pixels.data(), 8U);
        std::memcpy(buffer + stride, pixels.data() + 8U, 8U);
        return com::ok;
    }

    std::array<std::uint8_t, 16U> pixels{
        0x00U, 0x00U, 0x80U, 0x80U,
        0x00U, 0x40U, 0x00U, 0x40U,
        0x20U, 0x00U, 0x00U, 0x20U,
        0xFFU, 0xFFU, 0xFFU, 0xFFU};
    std::uint32_t resolution_call_count = 0U;
    std::uint32_t copy_call_count = 0U;
    std::uint32_t last_stride = 0U;
    std::uint32_t last_buffer_size = 0U;

private:
    friend class com::atomic_reference_count<fake_wic_bitmap_source>;
    ~fake_wic_bitmap_source() = default;

    com::atomic_reference_count<fake_wic_bitmap_source> reference_count_;
    com::guid pixel_format_;
};

class fake_font_face final : public compat::font_face {
public:
    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(interface_id, compat::font_face_interface_id)) {
            *value = static_cast<compat::font_face*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    std::uint32_t PROGPU_NATIVE_COM_CALL GetType() noexcept override
    {
        return 0U;
    }

    com::result PROGPU_NATIVE_COM_CALL GetFiles(
        std::uint32_t* file_count,
        com::unknown**) noexcept override
    {
        if (file_count == nullptr) {
            return com::pointer_error;
        }
        *file_count = 0U;
        return com::ok;
    }

    std::uint32_t PROGPU_NATIVE_COM_CALL GetIndex() noexcept override
    {
        return 0U;
    }

    std::uint32_t PROGPU_NATIVE_COM_CALL GetSimulations() noexcept override
    {
        return 0U;
    }

    std::int32_t PROGPU_NATIVE_COM_CALL IsSymbolFont() noexcept override
    {
        return 0;
    }

    void PROGPU_NATIVE_COM_CALL GetMetrics(void*) noexcept override
    {
    }

    std::uint16_t PROGPU_NATIVE_COM_CALL GetGlyphCount() noexcept override
    {
        return 256U;
    }

    com::result PROGPU_NATIVE_COM_CALL GetDesignGlyphMetrics(
        const std::uint16_t*,
        std::uint32_t,
        void*,
        std::int32_t) noexcept override
    {
        return compat::not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL GetGlyphIndices(
        const std::uint32_t* code_points,
        std::uint32_t code_point_count,
        std::uint16_t* glyph_indices) noexcept override
    {
        if ((code_point_count != 0U &&
                (code_points == nullptr || glyph_indices == nullptr))) {
            return com::invalid_argument;
        }
        for (std::uint32_t index = 0U; index < code_point_count; ++index) {
            glyph_indices[index] = static_cast<std::uint16_t>(
                code_points[index] & 0xFFU);
        }
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL TryGetFontTable(
        std::uint32_t,
        const void** table_data,
        std::uint32_t* table_size,
        void** table_context,
        std::int32_t* exists) noexcept override
    {
        if (table_data == nullptr || table_size == nullptr ||
            table_context == nullptr || exists == nullptr) {
            return com::pointer_error;
        }
        *table_data = nullptr;
        *table_size = 0U;
        *table_context = nullptr;
        *exists = 0;
        return com::ok;
    }

    void PROGPU_NATIVE_COM_CALL ReleaseFontTable(void*) noexcept override
    {
    }

    com::result PROGPU_NATIVE_COM_CALL GetGlyphRunOutline(
        float em_size,
        const std::uint16_t* glyph_indices,
        const float* glyph_advances,
        const compat::glyph_offset* glyph_offsets,
        std::uint32_t glyph_count,
        std::int32_t is_sideways,
        std::int32_t is_right_to_left,
        compat::simplified_geometry_sink* sink) noexcept override
    {
        ++outline_call_count;
        last_em_size = em_size;
        last_glyph_count = glyph_count;
        last_is_sideways = is_sideways;
        last_is_right_to_left = is_right_to_left;
        if (!std::isfinite(em_size) || em_size <= 0.0F ||
            (glyph_count != 0U && glyph_indices == nullptr) || sink == nullptr) {
            return com::invalid_argument;
        }
        sink->SetFillMode(compat::fill_mode::winding);
        float pen = 0.0F;
        const float direction = is_right_to_left != 0 ? -1.0F : 1.0F;
        for (std::uint32_t index = 0U; index < glyph_count; ++index) {
            const float advance = glyph_advances == nullptr
                ? em_size * 0.5F
                : glyph_advances[index];
            const float advance_offset = glyph_offsets == nullptr
                ? 0.0F
                : glyph_offsets[index].advance_offset;
            const float ascender_offset = glyph_offsets == nullptr
                ? 0.0F
                : glyph_offsets[index].ascender_offset;
            const float left = pen + direction * advance_offset;
            const float right = left + direction * em_size * 0.4F;
            const float top = -em_size - ascender_offset;
            const float bottom = -ascender_offset;
            sink->BeginFigure(
                {left, top}, compat::figure_begin::filled);
            const compat::point_2f points[]{
                {right, top}, {right, bottom}, {left, bottom}};
            sink->AddLines(points, 3U);
            sink->EndFigure(compat::figure_end::closed);
            pen += direction * advance;
        }
        return com::ok;
    }

    std::uint32_t outline_call_count = 0U;
    float last_em_size = 0.0F;
    std::uint32_t last_glyph_count = 0U;
    std::int32_t last_is_sideways = 0;
    std::int32_t last_is_right_to_left = 0;

private:
    friend class com::atomic_reference_count<fake_font_face>;
    ~fake_font_face() = default;
    com::atomic_reference_count<fake_font_face> reference_count_;
};

class fake_rendering_parameters final
    : public compat::rendering_parameters {
public:
    explicit fake_rendering_parameters(
        std::uint32_t* destruction_count) noexcept
        : destruction_count_(destruction_count)
    {
    }

    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(
                interface_id,
                compat::rendering_parameters_interface_id)) {
            *value = static_cast<compat::rendering_parameters*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    float PROGPU_NATIVE_COM_CALL GetGamma() noexcept override
    {
        return 2.2F;
    }

    float PROGPU_NATIVE_COM_CALL GetEnhancedContrast() noexcept override
    {
        return 0.75F;
    }

    float PROGPU_NATIVE_COM_CALL GetClearTypeLevel() noexcept override
    {
        return 0.5F;
    }

    compat::pixel_geometry PROGPU_NATIVE_COM_CALL GetPixelGeometry()
        noexcept override
    {
        return compat::pixel_geometry::rgb;
    }

    compat::rendering_mode PROGPU_NATIVE_COM_CALL GetRenderingMode()
        noexcept override
    {
        return compat::rendering_mode::natural_symmetric;
    }

private:
    friend class com::atomic_reference_count<fake_rendering_parameters>;
    ~fake_rendering_parameters()
    {
        if (destruction_count_ != nullptr) {
            ++*destruction_count_;
        }
    }

    com::atomic_reference_count<fake_rendering_parameters> reference_count_;
    std::uint32_t* destruction_count_ = nullptr;
};

using fake_reserved_com_method = void (PROGPU_NATIVE_COM_CALL*)();

struct fake_text_layout_vtable final {
    com::result (PROGPU_NATIVE_COM_CALL* query_interface)(
        void*, com::guid_ref, void**);
    com::reference_count_value (PROGPU_NATIVE_COM_CALL* add_ref)(void*);
    com::reference_count_value (PROGPU_NATIVE_COM_CALL* release)(void*);
    fake_reserved_com_method methods_before_maximum_size[39U];
    float (PROGPU_NATIVE_COM_CALL* get_max_width)(void*);
    float (PROGPU_NATIVE_COM_CALL* get_max_height)(void*);
    fake_reserved_com_method methods_before_draw[14U];
    com::result (PROGPU_NATIVE_COM_CALL* draw)(
        void*, void*, compat::text_renderer*, float, float);
};

struct fake_text_layout final {
    const fake_text_layout_vtable* vtable = nullptr;
    compat::glyph_run glyphs{};
    std::uint32_t draw_call_count = 0U;
    std::uint32_t max_width_call_count = 0U;
    std::uint32_t max_height_call_count = 0U;
    std::int32_t pixel_snapping_disabled = 0;
    float pixels_per_dip = 0.0F;
    compat::matrix_3x2_f transform{};
    std::uint32_t reference_count = 1U;
    bool heap_owned = false;
};

com::result PROGPU_NATIVE_COM_CALL fake_layout_query_interface(
    void* value,
    com::guid_ref interface_id,
    void** result)
{
    if (result == nullptr) {
        return com::pointer_error;
    }
    *result = nullptr;
    if (!com::guid_equal(interface_id, com::unknown_interface_id()) &&
        !com::guid_equal(interface_id, compat::text_layout_interface_id)) {
        return com::no_interface;
    }
    *result = value;
    auto* layout = static_cast<fake_text_layout*>(value);
    ++layout->reference_count;
    return com::ok;
}

com::reference_count_value PROGPU_NATIVE_COM_CALL fake_layout_add_ref(
    void* value)
{
    auto* layout = static_cast<fake_text_layout*>(value);
    return ++layout->reference_count;
}

com::reference_count_value PROGPU_NATIVE_COM_CALL fake_layout_release(
    void* value)
{
    auto* layout = static_cast<fake_text_layout*>(value);
    if (layout->reference_count == 0U) {
        return 0U;
    }
    const std::uint32_t remaining = --layout->reference_count;
    if (remaining == 0U && layout->heap_owned) {
        delete layout;
    }
    return remaining;
}

[[nodiscard]] float PROGPU_NATIVE_COM_CALL fake_layout_get_max_width(
    void* value)
{
    auto* layout = static_cast<fake_text_layout*>(value);
    ++layout->max_width_call_count;
    return 80.0F;
}

[[nodiscard]] float PROGPU_NATIVE_COM_CALL fake_layout_get_max_height(
    void* value)
{
    auto* layout = static_cast<fake_text_layout*>(value);
    ++layout->max_height_call_count;
    return 30.0F;
}

com::result PROGPU_NATIVE_COM_CALL fake_layout_draw(
    void* value,
    void* client_drawing_context,
    compat::text_renderer* renderer,
    float origin_x,
    float origin_y)
{
    auto* layout = static_cast<fake_text_layout*>(value);
    ++layout->draw_call_count;
    if (renderer == nullptr || !std::isfinite(origin_x) ||
        !std::isfinite(origin_y)) {
        return com::invalid_argument;
    }
    void* queried_renderer = nullptr;
    com::result result = renderer->QueryInterface(
        compat::text_renderer_interface_id, &queried_renderer);
    if (com::failed(result) || queried_renderer == nullptr) {
        return com::no_interface;
    }
    static_cast<compat::text_renderer*>(queried_renderer)->Release();
    result = renderer->IsPixelSnappingDisabled(
        client_drawing_context, &layout->pixel_snapping_disabled);
    if (com::succeeded(result)) {
        result = renderer->GetCurrentTransform(
            client_drawing_context, &layout->transform);
    }
    if (com::succeeded(result)) {
        result = renderer->GetPixelsPerDip(
            client_drawing_context, &layout->pixels_per_dip);
    }
    if (com::succeeded(result)) {
        result = renderer->DrawGlyphRun(
            client_drawing_context,
            origin_x + 2.0F,
            origin_y + 14.0F,
            compat::measuring_mode::natural,
            &layout->glyphs,
            nullptr,
            nullptr);
    }
    const compat::underline underline_value{
        18.0F,
        1.0F,
        2.0F,
        12.0F,
        0U,
        0U,
        nullptr,
        compat::measuring_mode::natural};
    if (com::succeeded(result)) {
        result = renderer->DrawUnderline(
            client_drawing_context,
            origin_x + 2.0F,
            origin_y + 14.0F,
            &underline_value,
            nullptr);
    }
    const compat::strikethrough strikethrough_value{
        18.0F,
        1.0F,
        -5.0F,
        0U,
        0U,
        nullptr,
        compat::measuring_mode::natural};
    if (com::succeeded(result)) {
        result = renderer->DrawStrikethrough(
            client_drawing_context,
            origin_x + 2.0F,
            origin_y + 14.0F,
            &strikethrough_value,
            nullptr);
    }
    return result;
}

[[nodiscard]] fake_text_layout_vtable make_fake_text_layout_vtable()
{
    fake_text_layout_vtable result{};
    result.query_interface = &fake_layout_query_interface;
    result.add_ref = &fake_layout_add_ref;
    result.release = &fake_layout_release;
    result.get_max_width = &fake_layout_get_max_width;
    result.get_max_height = &fake_layout_get_max_height;
    result.draw = &fake_layout_draw;
    return result;
}

const fake_text_layout_vtable fake_layout_vtable =
    make_fake_text_layout_vtable();

class fake_text_format final : public compat::portable_text_layout_factory {
public:
    explicit fake_text_format(compat::glyph_run glyphs) noexcept
        : glyphs_(glyphs)
    {
    }

    com::result PROGPU_NATIVE_COM_CALL QueryInterface(
        com::guid_ref interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (com::guid_equal(interface_id, com::unknown_interface_id()) ||
            com::guid_equal(
                interface_id,
                compat::portable_text_layout_factory_interface_id)) {
            *value = static_cast<compat::portable_text_layout_factory*>(this);
            AddRef();
            return com::ok;
        }
        return com::no_interface;
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL AddRef()
        noexcept override
    {
        return reference_count_.add_ref();
    }

    com::reference_count_value PROGPU_NATIVE_COM_CALL Release()
        noexcept override
    {
        return reference_count_.release(this);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateTextLayout(
        const wchar_t* text,
        std::uint32_t text_length,
        float maximum_width,
        float maximum_height,
        compat::measuring_mode measuring,
        compat::text_layout** layout) noexcept override
    {
        if (layout == nullptr) {
            return com::pointer_error;
        }
        *layout = nullptr;
        if (text == nullptr || text_length == 0U ||
            !std::isfinite(maximum_width) ||
            !std::isfinite(maximum_height) || maximum_width < 0.0F ||
            maximum_height < 0.0F) {
            return com::invalid_argument;
        }
        auto* created = new (std::nothrow) fake_text_layout();
        if (created == nullptr) {
            return com::out_of_memory;
        }
        created->vtable = &fake_layout_vtable;
        created->glyphs = glyphs_;
        created->heap_owned = true;
        ++create_call_count;
        last_text_length = text_length;
        first_character = text[0U];
        last_maximum_width = maximum_width;
        last_maximum_height = maximum_height;
        last_measuring = measuring;
        *layout = reinterpret_cast<compat::text_layout*>(created);
        return com::ok;
    }

    std::uint32_t create_call_count = 0U;
    std::uint32_t last_text_length = 0U;
    wchar_t first_character = 0;
    float last_maximum_width = 0.0F;
    float last_maximum_height = 0.0F;
    compat::measuring_mode last_measuring =
        compat::measuring_mode::natural;

private:
    friend class com::atomic_reference_count<fake_text_format>;
    ~fake_text_format() = default;

    com::atomic_reference_count<fake_text_format> reference_count_;
    compat::glyph_run glyphs_{};
};

static_assert(
    offsetof(fake_text_layout_vtable, draw) == 58U * sizeof(void*));

} // namespace

static_assert(sizeof(compat::rectangle_f) == 16U);
static_assert(sizeof(compat::matrix_3x2_f) == 24U);
static_assert(sizeof(compat::geometry_relation) == 4U);
static_assert(sizeof(compat::quadratic_bezier_segment) == 16U);
static_assert(sizeof(compat::arc_segment) == 28U);
static_assert(sizeof(compat::ellipse) == 16U);
static_assert(sizeof(compat::rounded_rectangle) == 24U);
static_assert(sizeof(compat::stroke_style_properties) == 28U);
static_assert(sizeof(compat::drawing_state_description) == 48U);
static_assert(sizeof(compat::color_f) == 16U);
static_assert(sizeof(compat::brush_properties) == 28U);
static_assert(sizeof(compat::gradient_stop) == 20U);
static_assert(sizeof(compat::linear_gradient_brush_properties) == 16U);
static_assert(sizeof(compat::radial_gradient_brush_properties) == 24U);
static_assert(sizeof(compat::pixel_format) == 8U);
static_assert(sizeof(compat::size_u) == 8U);
static_assert(sizeof(compat::point_2u) == 8U);
static_assert(sizeof(compat::rectangle_u) == 16U);
static_assert(sizeof(compat::wic_rectangle) == 16U);
static_assert(sizeof(compat::glyph_offset) == 8U);
static_assert(
    sizeof(compat::glyph_run) == (sizeof(void*) == 8U ? 48U : 32U));
static_assert(
    sizeof(compat::underline) == (sizeof(void*) == 8U ? 40U : 32U));
static_assert(
    sizeof(compat::strikethrough) == (sizeof(void*) == 8U ? 40U : 28U));
static_assert(sizeof(compat::bitmap_properties) == 16U);
static_assert(sizeof(compat::bitmap_brush_properties) == 12U);
static_assert(sizeof(compat::scene_render_target_properties) == 32U);
static_assert(sizeof(compat::scene_render_target_summary) == 40U);
static_assert(sizeof(compat::scene_submission_diagnostics) == 32U);
static_assert(sizeof(compat::scene_render_options) == 16U);
static_assert(
    sizeof(compat::layer_parameters) == (sizeof(void*) == 8U ? 72U : 60U));
static_assert(offsetof(compat::layer_parameters, content_bounds) == 0U);
static_assert(offsetof(compat::layer_parameters, geometric_mask) == 16U);
static_assert(
    offsetof(compat::layer_parameters, mask_antialias_mode) ==
    16U + sizeof(void*));
static_assert(
    offsetof(compat::layer_parameters, mask_transform) ==
    20U + sizeof(void*));
static_assert(
    offsetof(compat::layer_parameters, opacity) == 44U + sizeof(void*));
static_assert(
    offsetof(compat::layer_parameters, opacity_brush) ==
    48U + sizeof(void*));
static_assert(
    offsetof(compat::layer_parameters, options) == 48U + 2U * sizeof(void*));

#if defined(_WIN32)
[[nodiscard]] D2D1_MATRIX_3X2_F make_native_matrix(
    float m11,
    float m12,
    float m21,
    float m22,
    float dx,
    float dy) noexcept
{
    D2D1_MATRIX_3X2_F value{};
    value._11 = m11;
    value._12 = m12;
    value._21 = m21;
    value._22 = m22;
    value._31 = dx;
    value._32 = dy;
    return value;
}

static_assert(
    sizeof(compat::layer_parameters) == sizeof(D2D1_LAYER_PARAMETERS));
static_assert(sizeof(compat::wic_rectangle) == sizeof(WICRect));
static_assert(sizeof(compat::glyph_offset) == sizeof(DWRITE_GLYPH_OFFSET));
static_assert(sizeof(compat::glyph_run) == sizeof(DWRITE_GLYPH_RUN));
static_assert(sizeof(compat::underline) == sizeof(DWRITE_UNDERLINE));
static_assert(
    sizeof(compat::strikethrough) == sizeof(DWRITE_STRIKETHROUGH));
static_assert(
    offsetof(compat::glyph_run, font_face_value) ==
    offsetof(DWRITE_GLYPH_RUN, fontFace));
static_assert(
    offsetof(compat::glyph_run, glyph_offsets) ==
    offsetof(DWRITE_GLYPH_RUN, glyphOffsets));
static_assert(
    offsetof(compat::glyph_run, bidi_level) ==
    offsetof(DWRITE_GLYPH_RUN, bidiLevel));
static_assert(
    offsetof(compat::wic_rectangle, x) == offsetof(WICRect, X));
static_assert(
    offsetof(compat::wic_rectangle, y) == offsetof(WICRect, Y));
static_assert(
    offsetof(compat::wic_rectangle, width) == offsetof(WICRect, Width));
static_assert(
    offsetof(compat::wic_rectangle, height) == offsetof(WICRect, Height));
static_assert(sizeof(compat::triangle) == sizeof(D2D1_TRIANGLE));
static_assert(
    offsetof(compat::layer_parameters, content_bounds) ==
    offsetof(D2D1_LAYER_PARAMETERS, contentBounds));
static_assert(
    offsetof(compat::layer_parameters, geometric_mask) ==
    offsetof(D2D1_LAYER_PARAMETERS, geometricMask));
static_assert(
    offsetof(compat::layer_parameters, mask_antialias_mode) ==
    offsetof(D2D1_LAYER_PARAMETERS, maskAntialiasMode));
static_assert(
    offsetof(compat::layer_parameters, mask_transform) ==
    offsetof(D2D1_LAYER_PARAMETERS, maskTransform));
static_assert(
    offsetof(compat::layer_parameters, opacity) ==
    offsetof(D2D1_LAYER_PARAMETERS, opacity));
static_assert(
    offsetof(compat::layer_parameters, opacity_brush) ==
    offsetof(D2D1_LAYER_PARAMETERS, opacityBrush));
static_assert(
    offsetof(compat::layer_parameters, options) ==
    offsetof(D2D1_LAYER_PARAMETERS, layerOptions));
#endif

int run_tests()
{
    if (compat::create_factory(nullptr) != com::pointer_error) {
        return 1;
    }

    com::pointer<compat::factory> factory;
    compat::factory* raw_factory = nullptr;
    if (compat::create_factory(&raw_factory) != com::ok ||
        raw_factory == nullptr) {
        return 2;
    }
    factory.attach(raw_factory);

    com::pointer<com::unknown> identity;
    if (factory.as(com::unknown_interface_id(), identity) != com::ok ||
        identity.get() != static_cast<com::unknown*>(raw_factory)) {
        return 3;
    }
    float dpi_x = 0.0F;
    float dpi_y = 0.0F;
    factory->GetDesktopDpi(&dpi_x, &dpi_y);
    if (!approximately_equal(dpi_x, 96.0F) ||
        !approximately_equal(dpi_y, 96.0F)) {
        return 4;
    }

    const compat::rectangle_f rectangle{1.0F, 2.0F, 5.0F, 8.0F};
    com::pointer<compat::rectangle_geometry> geometry;
    compat::rectangle_geometry* raw_geometry = nullptr;
    if (factory->CreateRectangleGeometry(&rectangle, &raw_geometry) !=
            com::ok ||
        raw_geometry == nullptr) {
        return 5;
    }
    geometry.attach(raw_geometry);

    compat::factory* original_factory = factory.get();
    identity.Reset();
    factory.Reset();

    com::pointer<compat::factory> parent;
    compat::factory* raw_parent = nullptr;
    geometry->GetFactory(&raw_parent);
    parent.attach(raw_parent);
    if (!parent || parent.get() != original_factory) {
        return 6;
    }
    factory = parent;

    com::pointer<compat::resource> resource;
    com::pointer<compat::geometry> geometry_base;
    if (geometry.as(compat::resource_interface_id, resource) != com::ok ||
        geometry.as(compat::geometry_interface_id, geometry_base) != com::ok ||
        resource.get() != static_cast<compat::resource*>(geometry.get()) ||
        geometry_base.get() != static_cast<compat::geometry*>(geometry.get())) {
        return 7;
    }

    compat::rectangle_f returned{};
    geometry->GetRect(&returned);
    if (!approximately_equal(returned.left, 1.0F) ||
        !approximately_equal(returned.bottom, 8.0F)) {
        return 8;
    }
    const compat::matrix_3x2_f transform{
        2.0F, 0.0F, 0.0F, 3.0F, 10.0F, -4.0F};
    if (geometry->GetBounds(&transform, &returned) != com::ok ||
        !approximately_equal(returned.left, 12.0F) ||
        !approximately_equal(returned.top, 2.0F) ||
        !approximately_equal(returned.right, 20.0F) ||
        !approximately_equal(returned.bottom, 20.0F)) {
        return 9;
    }
    compat::rectangle_f widened_bounds{};
    if (geometry->GetWidenedBounds(
            2.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            &widened_bounds) != com::ok ||
        !approximately_equal(widened_bounds.left, 10.0F) ||
        !approximately_equal(widened_bounds.top, -1.0F) ||
        !approximately_equal(widened_bounds.right, 22.0F) ||
        !approximately_equal(widened_bounds.bottom, 23.0F) ||
        geometry->GetWidenedBounds(
            -1.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            &returned) != com::invalid_argument) {
        return 268;
    }

    std::int32_t contains = 0;
    float area = 0.0F;
    float length = 0.0F;
    if (geometry->FillContainsPoint(
            {16.0F, 10.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 1 ||
        geometry->ComputeArea(
            &transform, core::default_flattening_tolerance, &area) !=
            com::ok ||
        geometry->ComputeLength(
            &transform, core::default_flattening_tolerance, &length) !=
            com::ok ||
        !approximately_equal(area, 144.0F) ||
        !approximately_equal(length, 52.0F)) {
        return 10;
    }
    std::int32_t stroke_edge_contains = 0;
    std::int32_t stroke_center_contains = 1;
    std::int32_t stroke_outside_contains = 1;
    if (geometry->StrokeContainsPoint(
            {11.0F, 10.0F},
            2.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            &stroke_edge_contains) != com::ok ||
        geometry->StrokeContainsPoint(
            {16.0F, 10.0F},
            2.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            &stroke_center_contains) != com::ok ||
        geometry->StrokeContainsPoint(
            {30.0F, 30.0F},
            2.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            &stroke_outside_contains) != com::ok ||
        stroke_edge_contains != 1 || stroke_center_contains != 0 ||
        stroke_outside_contains != 0) {
        return 269;
    }
    const std::array<compat::rectangle_f, 6U> relation_rectangles{{
        rectangle,
        compat::rectangle_f{2.0F, 3.0F, 4.0F, 7.0F},
        compat::rectangle_f{0.0F, 1.0F, 6.0F, 9.0F},
        compat::rectangle_f{4.0F, 7.0F, 7.0F, 10.0F},
        compat::rectangle_f{6.0F, 9.0F, 7.0F, 10.0F},
        compat::rectangle_f{5.0F, 2.0F, 6.0F, 8.0F},
    }};
    constexpr std::array expected_relations{
        compat::geometry_relation::is_contained,
        compat::geometry_relation::contains,
        compat::geometry_relation::is_contained,
        compat::geometry_relation::overlap,
        compat::geometry_relation::disjoint,
        compat::geometry_relation::overlap,
    };
    std::array<compat::geometry_relation, 6U> portable_relations{};
    for (std::size_t relation_index = 0U;
         relation_index < relation_rectangles.size();
         ++relation_index) {
        compat::rectangle_geometry* raw_relation_rectangle = nullptr;
        if (factory->CreateRectangleGeometry(
                &relation_rectangles[relation_index],
                &raw_relation_rectangle) != com::ok ||
            raw_relation_rectangle == nullptr) {
            return 274;
        }
        com::pointer<compat::rectangle_geometry> relation_rectangle;
        relation_rectangle.attach(raw_relation_rectangle);
        if (geometry->CompareWithGeometry(
                relation_rectangle.get(),
                nullptr,
                core::default_flattening_tolerance,
                &portable_relations[relation_index]) != com::ok ||
            portable_relations[relation_index] !=
                expected_relations[relation_index]) {
            return 275;
        }
    }
    const compat::matrix_3x2_f relation_transform{
        1.0F, 0.0F, 0.0F, 1.0F, -4.0F, -6.0F};
    compat::rectangle_geometry* raw_translated_relation = nullptr;
    compat::geometry_relation translated_relation =
        compat::geometry_relation::unknown;
    if (factory->CreateRectangleGeometry(
            &relation_rectangles[4U],
            &raw_translated_relation) != com::ok ||
        raw_translated_relation == nullptr) {
        return 276;
    }
    com::pointer<compat::rectangle_geometry> translated_relation_geometry;
    translated_relation_geometry.attach(raw_translated_relation);
    if (geometry->CompareWithGeometry(
            translated_relation_geometry.get(),
            &relation_transform,
            core::default_flattening_tolerance,
            &translated_relation) != com::ok ||
        translated_relation != compat::geometry_relation::contains) {
        return 277;
    }
    constexpr std::array combination_modes{
        compat::combine_mode::union_value,
        compat::combine_mode::intersect,
        compat::combine_mode::xor_value,
        compat::combine_mode::exclude};
    constexpr std::array<std::uint32_t, 4U> combination_figure_counts{
        1U, 1U, 2U, 1U};
    constexpr std::array<std::uint32_t, 4U> combination_line_counts{
        8U, 4U, 12U, 6U};
    constexpr std::array<compat::point_2f, 4U> combination_probes{{
        {2.0F, 3.0F},
        {4.5F, 7.5F},
        {6.0F, 9.0F},
        {0.0F, 0.0F},
    }};
    constexpr std::array<std::array<bool, 4U>, 4U>
        combination_expected{{
            {{true, true, true, false}},
            {{false, true, false, false}},
            {{true, false, true, false}},
            {{true, false, false, false}},
        }};
    for (std::size_t mode_index = 0U;
         mode_index < combination_modes.size();
         ++mode_index) {
        const compat::combine_mode mode = combination_modes[mode_index];
        compat::rectangle_geometry* raw_combination_rectangle = nullptr;
        if (factory->CreateRectangleGeometry(
                &relation_rectangles[3U],
                &raw_combination_rectangle) != com::ok ||
            raw_combination_rectangle == nullptr) {
            return 284;
        }
        com::pointer<compat::rectangle_geometry> combination_rectangle;
        combination_rectangle.attach(raw_combination_rectangle);
        auto* raw_combination_sink = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink> combination_sink;
        combination_sink.attach(raw_combination_sink);
        if (geometry->CombineWithGeometry(
                combination_rectangle.get(),
                mode,
                nullptr,
                core::default_flattening_tolerance,
                combination_sink.get()) != com::ok ||
            raw_combination_sink->fill_mode != compat::fill_mode::alternate ||
            raw_combination_sink->segment_flags !=
                compat::path_segment::force_unstroked ||
            raw_combination_sink->begin_count !=
                combination_figure_counts[mode_index] ||
            raw_combination_sink->end_count !=
                combination_figure_counts[mode_index] ||
            raw_combination_sink->line_count !=
                combination_line_counts[mode_index]) {
            return 285;
        }
        for (std::size_t probe_index = 0U;
             probe_index < combination_probes.size();
             ++probe_index) {
            if (captured_fill_contains(
                    *raw_combination_sink,
                    combination_probes[probe_index]) !=
                combination_expected[mode_index][probe_index]) {
                return 289;
            }
        }
    }
    compat::rectangle_geometry* raw_hole_rectangle = nullptr;
    if (factory->CreateRectangleGeometry(
            &relation_rectangles[1U], &raw_hole_rectangle) != com::ok ||
        raw_hole_rectangle == nullptr) {
        return 286;
    }
    com::pointer<compat::rectangle_geometry> hole_rectangle;
    hole_rectangle.attach(raw_hole_rectangle);
    auto* raw_hole_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> hole_sink;
    hole_sink.attach(raw_hole_sink);
    if (geometry->CombineWithGeometry(
            hole_rectangle.get(),
            compat::combine_mode::exclude,
            nullptr,
            core::default_flattening_tolerance,
            hole_sink.get()) != com::ok ||
        raw_hole_sink->begin_count != 2U ||
        raw_hole_sink->end_count != 2U ||
        raw_hole_sink->line_count != 8U ||
        !captured_fill_contains(*raw_hole_sink, {1.5F, 2.5F}) ||
        captured_fill_contains(*raw_hole_sink, {3.0F, 5.0F})) {
        return 287;
    }

    auto* raw_simplified_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> simplified;
    simplified.attach(raw_simplified_sink);
    if (geometry->Simplify(
            compat::geometry_simplification_option::lines,
            &transform,
            core::default_flattening_tolerance,
            simplified.get()) != com::ok ||
        raw_simplified_sink->fill_mode != compat::fill_mode::winding ||
        raw_simplified_sink->segment_flags != compat::path_segment::none ||
        raw_simplified_sink->begin_count != 1U ||
        raw_simplified_sink->end_count != 1U ||
        raw_simplified_sink->line_count != 3U ||
        raw_simplified_sink->bezier_count != 0U ||
        geometry->Simplify(
            compat::geometry_simplification_option::lines,
            &transform,
            std::numeric_limits<float>::infinity(),
            simplified.get()) != com::invalid_argument ||
        raw_simplified_sink->begin_count != 1U) {
        return 11;
    }

    auto* raw_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> outline_sink;
    outline_sink.attach(raw_outline_sink);
    if (geometry->Outline(
            &transform,
            core::default_flattening_tolerance,
            outline_sink.get()) != com::ok ||
        raw_outline_sink->fill_mode != compat::fill_mode::alternate ||
        raw_outline_sink->segment_flags != compat::path_segment::none ||
        raw_outline_sink->figure_begin != compat::figure_begin::filled ||
        raw_outline_sink->figure_end != compat::figure_end::closed ||
        raw_outline_sink->begin_count != 1U ||
        raw_outline_sink->end_count != 1U ||
        raw_outline_sink->line_count != 4U ||
        raw_outline_sink->bezier_count != 0U ||
        !approximately_equal(raw_outline_sink->first.x, 12.0F) ||
        !approximately_equal(raw_outline_sink->first.y, 2.0F) ||
        geometry->Outline(
            &transform,
            std::numeric_limits<float>::infinity(),
            outline_sink.get()) != com::invalid_argument ||
        raw_outline_sink->begin_count != 1U) {
        return 262;
    }
    auto* raw_widen_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> widen_sink;
    widen_sink.attach(raw_widen_sink);
    if (geometry->Widen(
            2.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            widen_sink.get()) != com::ok ||
        raw_widen_sink->fill_mode != compat::fill_mode::alternate ||
        raw_widen_sink->segment_flags !=
            compat::path_segment::force_unstroked ||
        raw_widen_sink->begin_count != 2U ||
        raw_widen_sink->end_count != 2U ||
        raw_widen_sink->line_count != 6U ||
        raw_widen_sink->bezier_count != 0U ||
        raw_widen_sink->line_point_count != 6U ||
        !approximately_equal(raw_widen_sink->line_points[0U].x, 22.0F) ||
        !approximately_equal(raw_widen_sink->line_points[0U].y, -1.0F) ||
        !approximately_equal(raw_widen_sink->line_points[3U].x, 18.0F) ||
        !approximately_equal(raw_widen_sink->line_points[3U].y, 5.0F)) {
        return 271;
    }

    auto* raw_triangle_sink = new triangle_sink();
    com::pointer<compat::tessellation_sink> triangles;
    triangles.attach(raw_triangle_sink);
    if (geometry->Tessellate(
            &transform,
            core::default_flattening_tolerance,
            triangles.get()) != com::ok ||
        raw_triangle_sink->count != 2U ||
        !approximately_equal(raw_triangle_sink->first.point1.x, 12.0F)) {
        return 12;
    }

    const compat::matrix_3x2_f local_transform{
        2.0F, 0.0F, 0.0F, 0.5F, 5.0F, 7.0F};
    compat::transformed_geometry* raw_transformed = nullptr;
    if (factory->CreateTransformedGeometry(
            geometry_base.get(), &local_transform, &raw_transformed) !=
            com::ok ||
        raw_transformed == nullptr) {
        return 13;
    }
    com::pointer<compat::transformed_geometry> transformed;
    transformed.attach(raw_transformed);
    com::pointer<compat::geometry> transformed_base;
    if (transformed.as(
            compat::geometry_interface_id, transformed_base) != com::ok ||
        !transformed_base) {
        return 14;
    }
    compat::geometry* raw_source = nullptr;
    transformed->GetSourceGeometry(&raw_source);
    com::pointer<compat::geometry> returned_source;
    returned_source.attach(raw_source);
    compat::matrix_3x2_f returned_transform{};
    transformed->GetTransform(&returned_transform);
    if (returned_source.get() != geometry_base.get() ||
        !approximately_equal(returned_transform.m31, 5.0F) ||
        transformed->GetBounds(&transform, &returned) != com::ok ||
        !approximately_equal(returned.left, 24.0F) ||
        !approximately_equal(returned.top, 20.0F) ||
        !approximately_equal(returned.right, 40.0F) ||
        !approximately_equal(returned.bottom, 29.0F) ||
        transformed->GetWidenedBounds(
            2.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            &returned) != com::ok ||
        !approximately_equal(returned.left, 22.0F) ||
        !approximately_equal(returned.top, 17.0F) ||
        !approximately_equal(returned.right, 42.0F) ||
        !approximately_equal(returned.bottom, 32.0F)) {
        return 15;
    }
    [[maybe_unused]] const compat::rectangle_f transformed_widened_bounds =
        returned;
    std::int32_t transformed_stroke_edge_contains = 0;
    std::int32_t transformed_stroke_center_contains = 1;
    if (transformed->StrokeContainsPoint(
            {23.0F, 24.0F},
            2.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            &transformed_stroke_edge_contains) != com::ok ||
        transformed->StrokeContainsPoint(
            {30.0F, 24.0F},
            2.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            &transformed_stroke_center_contains) != com::ok ||
        transformed_stroke_edge_contains != 1 ||
        transformed_stroke_center_contains != 0) {
        return 270;
    }
    compat::geometry_relation transformed_candidate_relation =
        compat::geometry_relation::unknown;
    compat::geometry_relation transformed_source_relation =
        compat::geometry_relation::unknown;
    if (geometry->CompareWithGeometry(
            transformed_base.get(),
            nullptr,
            core::default_flattening_tolerance,
            &transformed_candidate_relation) != com::ok ||
        transformed->CompareWithGeometry(
            geometry_base.get(),
            nullptr,
            core::default_flattening_tolerance,
            &transformed_source_relation) != com::ok ||
        transformed_candidate_relation !=
            compat::geometry_relation::disjoint ||
        transformed_source_relation != compat::geometry_relation::disjoint) {
        return 278;
    }
    const compat::matrix_3x2_f general_relation_transform{
        1.0F, 0.5F, 0.0F, 1.0F, 0.0F, 0.0F};
    const compat::matrix_3x2_f reflected_relation_transform{
        -1.0F, 0.0F, 0.0F, 1.0F, 6.0F, 0.0F};
    compat::geometry_relation general_relation =
        compat::geometry_relation::unknown;
    compat::geometry_relation shear_overlap_relation =
        compat::geometry_relation::unknown;
    compat::geometry_relation reflected_relation =
        compat::geometry_relation::unknown;
    compat::geometry_relation sheared_source_relation =
        compat::geometry_relation::unknown;
    compat::rectangle_geometry* raw_shear_overlap_rectangle = nullptr;
    if (factory->CreateRectangleGeometry(
            &relation_rectangles[1U],
            &raw_shear_overlap_rectangle) != com::ok ||
        raw_shear_overlap_rectangle == nullptr) {
        return 294;
    }
    com::pointer<compat::rectangle_geometry> shear_overlap_rectangle;
    shear_overlap_rectangle.attach(raw_shear_overlap_rectangle);
    compat::transformed_geometry* raw_sheared_source = nullptr;
    if (factory->CreateTransformedGeometry(
            geometry_base.get(),
            &general_relation_transform,
            &raw_sheared_source) != com::ok ||
        raw_sheared_source == nullptr) {
        return 295;
    }
    com::pointer<compat::transformed_geometry> sheared_source;
    sheared_source.attach(raw_sheared_source);
    if (geometry->CompareWithGeometry(
            translated_relation_geometry.get(),
            &general_relation_transform,
            core::default_flattening_tolerance,
            &general_relation) != com::ok ||
        general_relation != compat::geometry_relation::disjoint ||
        geometry->CompareWithGeometry(
            shear_overlap_rectangle.get(),
            &general_relation_transform,
            core::default_flattening_tolerance,
            &shear_overlap_relation) != com::ok ||
        shear_overlap_relation != compat::geometry_relation::overlap ||
        geometry->CompareWithGeometry(
            geometry_base.get(),
            &reflected_relation_transform,
            core::default_flattening_tolerance,
            &reflected_relation) != com::ok ||
        reflected_relation != compat::geometry_relation::is_contained ||
        sheared_source->CompareWithGeometry(
            geometry_base.get(),
            nullptr,
            core::default_flattening_tolerance,
            &sheared_source_relation) != com::ok ||
        sheared_source_relation != compat::geometry_relation::overlap) {
        return 296;
    }
    auto* raw_transformed_union_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> transformed_union_sink;
    transformed_union_sink.attach(raw_transformed_union_sink);
    auto* raw_transformed_intersection_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        transformed_intersection_sink;
    transformed_intersection_sink.attach(raw_transformed_intersection_sink);
    if (geometry->CombineWithGeometry(
            transformed_base.get(),
            compat::combine_mode::union_value,
            nullptr,
            core::default_flattening_tolerance,
            transformed_union_sink.get()) != com::ok ||
        raw_transformed_union_sink->begin_count != 2U ||
        raw_transformed_union_sink->line_count != 8U ||
        transformed->CombineWithGeometry(
            geometry_base.get(),
            compat::combine_mode::intersect,
            nullptr,
            core::default_flattening_tolerance,
            transformed_intersection_sink.get()) != com::ok ||
        raw_transformed_intersection_sink->begin_count != 0U ||
        raw_transformed_intersection_sink->line_count != 0U) {
        return 292;
    }
    constexpr std::array<compat::point_2f, 4U> affine_combination_probes{{
        {1.5F, 2.5F},
        {3.0F, 6.0F},
        {3.5F, 8.5F},
        {0.0F, 0.0F},
    }};
    constexpr std::array<std::array<bool, 4U>, 4U>
        affine_combination_expected{{
            {{true, true, true, false}},
            {{false, true, false, false}},
            {{true, false, true, false}},
            {{true, false, false, false}},
        }};
    for (std::size_t mode_index = 0U;
         mode_index < combination_modes.size();
         ++mode_index) {
        auto* raw_affine_combination_sink = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink>
            affine_combination_sink;
        affine_combination_sink.attach(raw_affine_combination_sink);
        if (geometry->CombineWithGeometry(
                shear_overlap_rectangle.get(),
                combination_modes[mode_index],
                &general_relation_transform,
                core::default_flattening_tolerance,
                affine_combination_sink.get()) != com::ok ||
            raw_affine_combination_sink->fill_mode !=
                compat::fill_mode::alternate ||
            raw_affine_combination_sink->segment_flags !=
                compat::path_segment::force_unstroked ||
            raw_affine_combination_sink->begin_count == 0U ||
            raw_affine_combination_sink->begin_count !=
                raw_affine_combination_sink->end_count) {
            return 298;
        }
        for (std::size_t probe_index = 0U;
             probe_index < affine_combination_probes.size();
             ++probe_index) {
            if (captured_fill_contains(
                    *raw_affine_combination_sink,
                    affine_combination_probes[probe_index]) !=
                affine_combination_expected[mode_index][probe_index]) {
                return 299;
            }
        }
    }
    constexpr std::array<compat::matrix_3x2_f, 3U>
        affine_combination_corpus{{
            {0.9F, 0.35F, -0.2F, 1.1F, 0.75F, -0.4F},
            {0.6F, -0.45F, 0.3F, 0.8F, 2.0F, 1.0F},
            {-0.8F, 0.25F, 0.15F, 1.0F, 6.5F, -0.5F},
        }};
    for (const auto& corpus_transform : affine_combination_corpus) {
        for (std::size_t mode_index = 0U;
             mode_index < combination_modes.size();
             ++mode_index) {
            auto* raw_corpus_sink = new simplified_sink();
            com::pointer<compat::simplified_geometry_sink> corpus_sink;
            corpus_sink.attach(raw_corpus_sink);
            if (geometry->CombineWithGeometry(
                    shear_overlap_rectangle.get(),
                    combination_modes[mode_index],
                    &corpus_transform,
                    core::default_flattening_tolerance,
                    corpus_sink.get()) != com::ok) {
                return 305;
            }
            for (std::uint32_t y_index = 0U; y_index < 14U; ++y_index) {
                for (std::uint32_t x_index = 0U; x_index < 12U; ++x_index) {
                    const compat::point_2f point{
                        -1.0F + static_cast<float>(x_index) * 0.73F + 0.19F,
                        -1.0F + static_cast<float>(y_index) * 0.81F + 0.23F};
                    std::int32_t in_first = 0;
                    std::int32_t in_second = 0;
                    if (geometry->FillContainsPoint(
                            point,
                            nullptr,
                            core::default_flattening_tolerance,
                            &in_first) != com::ok ||
                        shear_overlap_rectangle->FillContainsPoint(
                            point,
                            &corpus_transform,
                            core::default_flattening_tolerance,
                            &in_second) != com::ok) {
                        return 306;
                    }
                    bool expected = false;
                    switch (combination_modes[mode_index]) {
                    case compat::combine_mode::union_value:
                        expected = in_first != 0 || in_second != 0;
                        break;
                    case compat::combine_mode::intersect:
                        expected = in_first != 0 && in_second != 0;
                        break;
                    case compat::combine_mode::xor_value:
                        expected = (in_first != 0) != (in_second != 0);
                        break;
                    case compat::combine_mode::exclude:
                        expected = in_first != 0 && in_second == 0;
                        break;
                    }
                    if (captured_fill_contains(*raw_corpus_sink, point) !=
                        expected) {
                        return 307;
                    }
                }
            }
        }
    }
    const compat::matrix_3x2_f affine_source_candidate_transform{
        1.0F, 0.0F, 0.0F, 1.0F, 0.5F, 0.25F};
    constexpr std::array<compat::point_2f, 4U>
        affine_source_combination_probes{{
            {1.2F, 3.0F},
            {3.0F, 6.0F},
            {5.25F, 8.0F},
            {0.0F, 0.0F},
        }};
    for (std::size_t mode_index = 0U;
         mode_index < combination_modes.size();
         ++mode_index) {
        auto* raw_affine_source_sink = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink> affine_source_sink;
        affine_source_sink.attach(raw_affine_source_sink);
        if (sheared_source->CombineWithGeometry(
                geometry_base.get(),
                combination_modes[mode_index],
                &affine_source_candidate_transform,
                core::default_flattening_tolerance,
                affine_source_sink.get()) != com::ok ||
            raw_affine_source_sink->begin_count == 0U ||
            raw_affine_source_sink->begin_count !=
                raw_affine_source_sink->end_count) {
            return 300;
        }
        for (std::size_t probe_index = 0U;
             probe_index < affine_source_combination_probes.size();
             ++probe_index) {
            if (captured_fill_contains(
                    *raw_affine_source_sink,
                    affine_source_combination_probes[probe_index]) !=
                affine_combination_expected[mode_index][probe_index]) {
                return 301;
            }
        }
    }
    const std::array<compat::rectangle_f, 4U>
        affine_collinear_rectangles{{
            rectangle,
            {5.0F, 2.0F, 7.0F, 8.0F},
            {5.0F, 3.25F, 7.0F, 6.75F},
            {3.0F, 2.0F, 7.0F, 6.0F},
        }};
    for (const compat::rectangle_f& affine_collinear_rectangle :
         affine_collinear_rectangles) {
        compat::rectangle_geometry* raw_affine_collinear_geometry = nullptr;
        if (factory->CreateRectangleGeometry(
                &affine_collinear_rectangle,
                &raw_affine_collinear_geometry) != com::ok ||
            raw_affine_collinear_geometry == nullptr) {
            return 308;
        }
        com::pointer<compat::rectangle_geometry>
            affine_collinear_geometry;
        affine_collinear_geometry.attach(raw_affine_collinear_geometry);
        for (const compat::combine_mode mode : combination_modes) {
            auto* raw_affine_collinear_sink = new simplified_sink();
            com::pointer<compat::simplified_geometry_sink>
                affine_collinear_sink;
            affine_collinear_sink.attach(raw_affine_collinear_sink);
            if (sheared_source->CombineWithGeometry(
                    affine_collinear_geometry.get(),
                    mode,
                    &general_relation_transform,
                    core::default_flattening_tolerance,
                    affine_collinear_sink.get()) != com::ok ||
                raw_affine_collinear_sink->begin_count !=
                    raw_affine_collinear_sink->end_count) {
                return 309;
            }
            for (std::uint32_t y_index = 0U; y_index < 19U; ++y_index) {
                for (std::uint32_t x_index = 0U; x_index < 19U; ++x_index) {
                    const float local_x =
                        0.73F + static_cast<float>(x_index) * 0.41F;
                    const float local_y =
                        1.17F + static_cast<float>(y_index) * 0.43F;
                    const compat::point_2f point{
                        local_x * general_relation_transform.m11 +
                            local_y * general_relation_transform.m21 +
                            general_relation_transform.m31,
                        local_x * general_relation_transform.m12 +
                            local_y * general_relation_transform.m22 +
                            general_relation_transform.m32};
                    std::int32_t in_first = 0;
                    std::int32_t in_second = 0;
                    if (sheared_source->FillContainsPoint(
                            point,
                            nullptr,
                            core::default_flattening_tolerance,
                            &in_first) != com::ok ||
                        affine_collinear_geometry->FillContainsPoint(
                            point,
                            &general_relation_transform,
                            core::default_flattening_tolerance,
                            &in_second) != com::ok) {
                        return 310;
                    }
                    bool expected = false;
                    switch (mode) {
                    case compat::combine_mode::union_value:
                        expected = in_first != 0 || in_second != 0;
                        break;
                    case compat::combine_mode::intersect:
                        expected = in_first != 0 && in_second != 0;
                        break;
                    case compat::combine_mode::xor_value:
                        expected = (in_first != 0) != (in_second != 0);
                        break;
                    case compat::combine_mode::exclude:
                        expected = in_first != 0 && in_second == 0;
                        break;
                    }
                    if (captured_fill_contains(
                            *raw_affine_collinear_sink, point) != expected) {
                        return 311;
                    }
                }
            }
        }
    }
    auto* raw_transformed_widen_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> transformed_widen_sink;
    transformed_widen_sink.attach(raw_transformed_widen_sink);
    if (transformed->Widen(
            2.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            transformed_widen_sink.get()) != com::ok ||
        raw_transformed_widen_sink->fill_mode !=
            compat::fill_mode::winding ||
        raw_transformed_widen_sink->segment_flags !=
            compat::path_segment::force_unstroked ||
        raw_transformed_widen_sink->figure_ends[0U] !=
            compat::figure_end::open ||
        raw_transformed_widen_sink->begin_count != 1U ||
        raw_transformed_widen_sink->end_count != 1U ||
        raw_transformed_widen_sink->line_count != 26U ||
        raw_transformed_widen_sink->line_point_count != 26U ||
        !approximately_equal(
            raw_transformed_widen_sink->begin_points[0U].x, 24.0F) ||
        !approximately_equal(
            raw_transformed_widen_sink->begin_points[0U].y, 23.0F) ||
        !approximately_equal(
            raw_transformed_widen_sink->line_points[0U].x, 24.0F) ||
        !approximately_equal(
            raw_transformed_widen_sink->line_points[0U].y, 17.0F)) {
        return 272;
    }

    compat::factory* second_raw_factory = nullptr;
    if (compat::create_factory(&second_raw_factory) != com::ok) {
        return 16;
    }
    com::pointer<compat::factory> second_factory;
    second_factory.attach(second_raw_factory);
    compat::transformed_geometry* wrong_factory_geometry = nullptr;
    if (second_factory->CreateTransformedGeometry(
            geometry_base.get(),
            &local_transform,
            &wrong_factory_geometry) != compat::wrong_factory ||
        wrong_factory_geometry != nullptr) {
        return 17;
    }
    compat::rectangle_geometry* raw_cross_factory_rectangle = nullptr;
    if (second_factory->CreateRectangleGeometry(
            &rectangle, &raw_cross_factory_rectangle) != com::ok ||
        raw_cross_factory_rectangle == nullptr) {
        return 282;
    }
    com::pointer<compat::rectangle_geometry> cross_factory_rectangle;
    cross_factory_rectangle.attach(raw_cross_factory_rectangle);
    compat::geometry_relation rejected_relation =
        compat::geometry_relation::contains;
    if (geometry->CompareWithGeometry(
            cross_factory_rectangle.get(),
            nullptr,
            core::default_flattening_tolerance,
            &rejected_relation) != compat::wrong_factory ||
        rejected_relation != compat::geometry_relation::unknown ||
        geometry->CompareWithGeometry(
            translated_relation_geometry.get(),
            &general_relation_transform,
            core::default_flattening_tolerance,
            &rejected_relation) != com::ok ||
        rejected_relation != compat::geometry_relation::disjoint) {
        return 283;
    }
    rejected_relation = compat::geometry_relation::contains;
    if (geometry->CompareWithGeometry(
            translated_relation_geometry.get(),
            nullptr,
            0.0F,
            &rejected_relation) != com::invalid_argument ||
        rejected_relation != compat::geometry_relation::unknown) {
        return 283;
    }
    auto* raw_rejected_combination_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        rejected_combination_sink;
    rejected_combination_sink.attach(raw_rejected_combination_sink);
    if (geometry->CombineWithGeometry(
            cross_factory_rectangle.get(),
            compat::combine_mode::union_value,
            nullptr,
            core::default_flattening_tolerance,
            rejected_combination_sink.get()) != compat::wrong_factory ||
        geometry->CombineWithGeometry(
            translated_relation_geometry.get(),
            static_cast<compat::combine_mode>(99U),
            nullptr,
            core::default_flattening_tolerance,
            rejected_combination_sink.get()) != com::invalid_argument ||
        raw_rejected_combination_sink->begin_count != 0U) {
        return 293;
    }

    compat::path_geometry* raw_path = nullptr;
    if (factory->CreatePathGeometry(&raw_path) != com::ok ||
        raw_path == nullptr) {
        return 24;
    }
    com::pointer<compat::path_geometry> path;
    path.attach(raw_path);
    com::pointer<compat::resource> path_resource;
    com::pointer<compat::geometry> path_base;
    if (path.as(compat::resource_interface_id, path_resource) != com::ok ||
        path.as(compat::geometry_interface_id, path_base) != com::ok ||
        !path_resource || !path_base) {
        return 25;
    }
    compat::factory* raw_path_factory = nullptr;
    path->GetFactory(&raw_path_factory);
    com::pointer<compat::factory> path_factory;
    path_factory.attach(raw_path_factory);
    if (path_factory.get() != factory.get()) {
        return 26;
    }

    std::uint32_t path_segment_count = 99U;
    std::uint32_t path_figure_count = 99U;
    if (path->GetSegmentCount(&path_segment_count) != compat::wrong_state ||
        path_segment_count != 0U ||
        path->GetFigureCount(&path_figure_count) != compat::wrong_state ||
        path_figure_count != 0U) {
        return 27;
    }
    compat::geometry_sink* raw_path_sink = nullptr;
    if (path->Open(&raw_path_sink) != com::ok || raw_path_sink == nullptr) {
        return 28;
    }
    com::pointer<compat::geometry_sink> path_sink;
    path_sink.attach(raw_path_sink);
    compat::geometry_sink* duplicate_sink =
        reinterpret_cast<compat::geometry_sink*>(
            static_cast<std::uintptr_t>(1U));
    if (path->Open(&duplicate_sink) != compat::wrong_state ||
        duplicate_sink != nullptr) {
        return 29;
    }
    com::pointer<compat::simplified_geometry_sink> path_sink_base;
    if (path_sink.as(
            compat::simplified_geometry_sink_interface_id,
            path_sink_base) != com::ok ||
        !path_sink_base) {
        return 30;
    }

    path_sink->SetFillMode(compat::fill_mode::winding);
    path_sink->SetSegmentFlags(compat::path_segment::none);
    path_sink->BeginFigure({0.0F, 0.0F}, compat::figure_begin::filled);
    path_sink->AddLine({2.0F, 0.0F});
    const compat::bezier_segment cubic{
        {2.0F, 2.0F}, {4.0F, 2.0F}, {4.0F, 0.0F}};
    path_sink->AddBezier(&cubic);
    path_sink->SetSegmentFlags(
        compat::path_segment::force_round_line_join);
    const compat::quadratic_bezier_segment quadratic{
        {5.0F, -2.0F}, {6.0F, 0.0F}};
    path_sink->AddQuadraticBezier(&quadratic);
    path_sink->EndFigure(compat::figure_end::closed);
    if (path_sink->Close() != com::ok ||
        path_sink->Close() != compat::wrong_state) {
        return 31;
    }
    path_sink_base.Reset();
    path_sink.Reset();

    if (path->GetSegmentCount(&path_segment_count) != com::ok ||
        path_segment_count != 4U ||
        path->GetFigureCount(&path_figure_count) != com::ok ||
        path_figure_count != 1U) {
        return 32;
    }
    compat::rectangle_f path_bounds{};
    if (path->GetBounds(&transform, &path_bounds) != com::ok ||
        !approximately_equal(path_bounds.left, 10.0F) ||
        !approximately_equal(path_bounds.top, -7.0F) ||
        !approximately_equal(path_bounds.right, 22.0F) ||
        !approximately_equal(path_bounds.bottom, 0.5F)) {
        return 33;
    }

    auto* raw_path_stream = new simplified_sink();
    com::pointer<compat::geometry_sink> path_stream;
    path_stream.attach(raw_path_stream);
    if (path->Stream(path_stream.get()) != com::ok ||
        raw_path_stream->fill_mode != compat::fill_mode::winding ||
        raw_path_stream->begin_count != 1U ||
        raw_path_stream->end_count != 1U ||
        raw_path_stream->line_count != 1U ||
        raw_path_stream->bezier_count != 1U ||
        raw_path_stream->quadratic_count != 1U ||
        raw_path_stream->arc_count != 0U ||
        !approximately_equal(raw_path_stream->last.x, 6.0F)) {
        return 34;
    }

    auto* raw_path_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> path_simplified;
    path_simplified.attach(raw_path_simplified);
    if (path->Simplify(
            compat::geometry_simplification_option::cubics_and_lines,
            &transform,
            core::default_flattening_tolerance,
            path_simplified.get()) != com::ok ||
        raw_path_simplified->begin_count != 1U ||
        raw_path_simplified->line_count != 1U ||
        raw_path_simplified->bezier_count != 2U ||
        raw_path_simplified->quadratic_count != 0U ||
        !approximately_equal(raw_path_simplified->first.x, 10.0F) ||
        !approximately_equal(raw_path_simplified->last.x, 22.0F) ||
        !approximately_equal(raw_path_simplified->last.y, -4.0F)) {
        return 35;
    }

    compat::path_geometry* raw_query_path = nullptr;
    if (factory->CreatePathGeometry(&raw_query_path) != com::ok ||
        raw_query_path == nullptr) {
        return 208;
    }
    com::pointer<compat::path_geometry> query_path;
    query_path.attach(raw_query_path);
    compat::geometry_sink* raw_query_sink = nullptr;
    if (query_path->Open(&raw_query_sink) != com::ok ||
        raw_query_sink == nullptr) {
        return 209;
    }
    com::pointer<compat::geometry_sink> query_sink;
    query_sink.attach(raw_query_sink);
    query_sink->SetFillMode(compat::fill_mode::winding);
    query_sink->BeginFigure({1.0F, 2.0F}, compat::figure_begin::filled);
    const compat::point_2f query_points[]{
        {5.0F, 2.0F}, {5.0F, 8.0F}, {1.0F, 8.0F}};
    query_sink->AddLines(query_points, 3U);
    query_sink->EndFigure(compat::figure_end::closed);
    if (query_sink->Close() != com::ok) {
        return 210;
    }
    query_sink.Reset();
    contains = 0;
    area = 0.0F;
    length = 0.0F;
    compat::point_2f query_point{};
    compat::point_2f query_tangent{};
    if (query_path->FillContainsPoint(
            {16.0F, 10.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 1 ||
        query_path->ComputeArea(
            &transform,
            core::default_flattening_tolerance,
            &area) != com::ok ||
        query_path->ComputeLength(
            &transform,
            core::default_flattening_tolerance,
            &length) != com::ok ||
        query_path->ComputePointAtLength(
            4.0F,
            &transform,
            core::default_flattening_tolerance,
            &query_point,
            &query_tangent) != com::ok ||
        !approximately_equal(area, 144.0F) ||
        !approximately_equal(length, 52.0F) ||
        !approximately_equal(query_point.x, 16.0F) ||
        !approximately_equal(query_point.y, 2.0F) ||
        !approximately_equal(query_tangent.x, 1.0F) ||
        !approximately_equal(query_tangent.y, 0.0F)) {
        return 211;
    }
    auto* raw_query_triangles = new triangle_sink();
    com::pointer<compat::tessellation_sink> query_triangles;
    query_triangles.attach(raw_query_triangles);
    if (query_path->Tessellate(
            &transform,
            core::default_flattening_tolerance,
            query_triangles.get()) != com::ok ||
        raw_query_triangles->count != 2U ||
        !approximately_equal(raw_query_triangles->first.point1.x, 12.0F)) {
        return 218;
    }
    auto* raw_query_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> query_simplified;
    query_simplified.attach(raw_query_simplified);
    if (query_path->Simplify(
            compat::geometry_simplification_option::lines,
            &transform,
            core::default_flattening_tolerance,
            query_simplified.get()) != com::ok ||
        raw_query_simplified->fill_mode != compat::fill_mode::winding ||
        raw_query_simplified->begin_count != 1U ||
        raw_query_simplified->line_count != 3U ||
        raw_query_simplified->bezier_count != 0U ||
        raw_query_simplified->end_count != 1U) {
        return 212;
    }
    auto* raw_query_outline = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> query_outline;
    query_outline.attach(raw_query_outline);
    if (query_path->Outline(
            &transform,
            core::default_flattening_tolerance,
            query_outline.get()) != com::ok ||
        raw_query_outline->fill_mode != compat::fill_mode::alternate ||
        raw_query_outline->figure_begin != compat::figure_begin::filled ||
        raw_query_outline->figure_end != compat::figure_end::closed ||
        raw_query_outline->begin_count != 1U ||
        raw_query_outline->line_count != 4U ||
        raw_query_outline->end_count != 1U) {
        return 265;
    }

  compat::path_geometry *raw_boolean_path = nullptr;
  compat::geometry_sink *raw_boolean_path_sink = nullptr;
  if (factory->CreatePathGeometry(&raw_boolean_path) != com::ok ||
      raw_boolean_path == nullptr ||
      raw_boolean_path->Open(&raw_boolean_path_sink) != com::ok ||
      raw_boolean_path_sink == nullptr) {
    return 318;
  }
  com::pointer<compat::path_geometry> boolean_path;
  boolean_path.attach(raw_boolean_path);
  com::pointer<compat::geometry_sink> boolean_path_sink;
  boolean_path_sink.attach(raw_boolean_path_sink);
  boolean_path_sink->SetFillMode(compat::fill_mode::winding);
  boolean_path_sink->BeginFigure({3.0F, 1.0F}, compat::figure_begin::filled);
  constexpr std::array<compat::point_2f, 5U> boolean_path_points{{
      {7.0F, 1.0F},
      {7.0F, 5.0F},
      {4.0F, 5.0F},
      {4.0F, 9.0F},
      {3.0F, 9.0F},
  }};
  boolean_path_sink->AddLines(
      boolean_path_points.data(),
      static_cast<std::uint32_t>(boolean_path_points.size()));
  boolean_path_sink->EndFigure(compat::figure_end::closed);
  if (boolean_path_sink->Close() != com::ok) {
    return 319;
  }
  boolean_path_sink.Reset();
  com::pointer<compat::geometry> boolean_path_base;
  if (boolean_path.as(compat::geometry_interface_id, boolean_path_base) !=
          com::ok ||
      !boolean_path_base) {
    return 320;
  }
  const compat::matrix_3x2_f disjoint_path_transform{
      1.0F, 0.0F, 0.0F, 1.0F, 10.0F, 0.0F};
  const compat::matrix_3x2_f contained_path_transform{
      0.25F, 0.0F, 0.0F, 0.5F, 1.0F, 2.0F};
  const compat::matrix_3x2_f containing_path_transform{
      2.0F, 0.0F, 0.0F, 2.0F, -2.0F, -4.0F};
  const compat::matrix_3x2_f touching_path_transform{
      1.0F, 0.0F, 0.0F, 1.0F, 2.0F, 0.0F};
  struct path_relation_case final {
    compat::geometry *input = nullptr;
    const compat::matrix_3x2_f *transform = nullptr;
    compat::geometry_relation expected = compat::geometry_relation::unknown;
  };
  const std::array<path_relation_case, 6U> path_relation_cases{{
      {boolean_path_base.get(), nullptr, compat::geometry_relation::overlap},
      {query_path.get(), nullptr, compat::geometry_relation::is_contained},
      {boolean_path_base.get(), &disjoint_path_transform,
       compat::geometry_relation::disjoint},
      {boolean_path_base.get(), &contained_path_transform,
       compat::geometry_relation::contains},
      {query_path.get(), &containing_path_transform,
       compat::geometry_relation::is_contained},
      {boolean_path_base.get(), &touching_path_transform,
       compat::geometry_relation::overlap},
  }};
  for (const path_relation_case &relation_case : path_relation_cases) {
    compat::geometry_relation relation = compat::geometry_relation::unknown;
    if (query_path->CompareWithGeometry(
            relation_case.input, relation_case.transform,
            core::default_flattening_tolerance, &relation) != com::ok ||
        relation != relation_case.expected) {
      return 328;
    }
  }
  for (const compat::combine_mode mode : combination_modes) {
    auto *raw_path_boolean_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> path_boolean_sink;
    path_boolean_sink.attach(raw_path_boolean_sink);
    if (query_path->CombineWithGeometry(boolean_path_base.get(), mode, nullptr,
                                        core::default_flattening_tolerance,
                                        path_boolean_sink.get()) != com::ok ||
        raw_path_boolean_sink->fill_mode != compat::fill_mode::alternate ||
        raw_path_boolean_sink->segment_flags !=
            compat::path_segment::force_unstroked ||
        raw_path_boolean_sink->begin_count !=
            raw_path_boolean_sink->end_count) {
      return 321;
    }
    for (std::uint32_t y_index = 0U; y_index < 18U; ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 16U; ++x_index) {
        const compat::point_2f point{
            0.37F + static_cast<float>(x_index) * 0.47F,
            0.31F + static_cast<float>(y_index) * 0.53F};
        std::int32_t in_first = 0;
        std::int32_t in_second = 0;
        if (query_path->FillContainsPoint(point, nullptr, 0.01F, &in_first) !=
                com::ok ||
            boolean_path->FillContainsPoint(point, nullptr, 0.01F,
                                            &in_second) != com::ok) {
          return 322;
        }
        bool expected = false;
        switch (mode) {
        case compat::combine_mode::union_value:
          expected = in_first != 0 || in_second != 0;
          break;
        case compat::combine_mode::intersect:
          expected = in_first != 0 && in_second != 0;
          break;
        case compat::combine_mode::xor_value:
          expected = (in_first != 0) != (in_second != 0);
          break;
        case compat::combine_mode::exclude:
          expected = in_first != 0 && in_second == 0;
          break;
        }
        if (captured_fill_contains(*raw_path_boolean_sink, point) != expected) {
          return 323;
        }
      }
    }
  }
  constexpr std::array<std::uint32_t, 4U> identical_path_boolean_figure_counts{
      1U, 1U, 0U, 0U};
  for (std::size_t mode_index = 0U; mode_index < combination_modes.size();
       ++mode_index) {
    auto *raw_identical_path_boolean_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> identical_path_boolean_sink;
    identical_path_boolean_sink.attach(raw_identical_path_boolean_sink);
    if (query_path->CombineWithGeometry(
            query_path.get(), combination_modes[mode_index], nullptr,
            core::default_flattening_tolerance,
            identical_path_boolean_sink.get()) != com::ok ||
        raw_identical_path_boolean_sink->begin_count !=
            identical_path_boolean_figure_counts[mode_index] ||
        raw_identical_path_boolean_sink->begin_count !=
            raw_identical_path_boolean_sink->end_count ||
        captured_fill_contains(*raw_identical_path_boolean_sink,
                               {2.0F, 3.0F}) != (mode_index < 2U) ||
        captured_fill_contains(*raw_identical_path_boolean_sink,
                               {0.0F, 0.0F})) {
      return 324;
    }
  }

    compat::path_geometry* raw_arc_path = nullptr;
    if (factory->CreatePathGeometry(&raw_arc_path) != com::ok ||
        raw_arc_path == nullptr) {
        return 36;
    }
    com::pointer<compat::path_geometry> arc_path;
    arc_path.attach(raw_arc_path);
    compat::geometry_sink* raw_arc_sink = nullptr;
    if (arc_path->Open(&raw_arc_sink) != com::ok ||
        raw_arc_sink == nullptr) {
        return 37;
    }
    com::pointer<compat::geometry_sink> arc_sink;
    arc_sink.attach(raw_arc_sink);
    arc_sink->BeginFigure({0.0F, 0.0F}, compat::figure_begin::filled);
    const compat::arc_segment arc{
        {2.0F, 0.0F},
        {1.0F, 1.0F},
        0.0F,
        compat::sweep_direction::clockwise,
        compat::arc_size::small_value};
    arc_sink->AddArc(&arc);
    arc_sink->EndFigure(compat::figure_end::open);
    if (arc_sink->Close() != com::ok) {
        return 38;
    }
    arc_sink.Reset();
    path_bounds = {1.0F, 1.0F, 1.0F, 1.0F};
    auto* raw_arc_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> arc_simplified;
    arc_simplified.attach(raw_arc_simplified);
    if (arc_path->GetBounds(nullptr, &path_bounds) != com::ok ||
        !approximately_equal(path_bounds.left, 0.0F) ||
        !approximately_equal(path_bounds.top, -1.0F) ||
        !approximately_equal(path_bounds.right, 2.0F) ||
        !approximately_equal(path_bounds.bottom, 0.0F) ||
        arc_path->Simplify(
            compat::geometry_simplification_option::cubics_and_lines,
            nullptr,
            core::default_flattening_tolerance,
            arc_simplified.get()) != com::ok ||
        raw_arc_simplified->begin_count != 1U ||
        raw_arc_simplified->end_count != 1U ||
        raw_arc_simplified->bezier_count != 2U ||
        !approximately_equal(raw_arc_simplified->last.x, 2.0F)) {
        return 39;
    }
    auto* raw_arc_stream = new simplified_sink();
    com::pointer<compat::geometry_sink> arc_stream;
    arc_stream.attach(raw_arc_stream);
    if (arc_path->Stream(arc_stream.get()) != com::ok ||
        raw_arc_stream->arc_count != 1U ||
        !approximately_equal(raw_arc_stream->last.x, 2.0F)) {
        return 40;
    }

    const compat::ellipse ellipse_value{{2.0F, 3.0F}, 4.0F, 2.0F};
    compat::ellipse_geometry* raw_ellipse = nullptr;
    if (factory->CreateEllipseGeometry(&ellipse_value, &raw_ellipse) !=
            com::ok ||
        raw_ellipse == nullptr) {
        return 53;
    }
    com::pointer<compat::ellipse_geometry> ellipse;
    ellipse.attach(raw_ellipse);
    com::pointer<compat::geometry> ellipse_base;
    if (ellipse.as(compat::geometry_interface_id, ellipse_base) != com::ok ||
        !ellipse_base) {
        return 54;
    }
    compat::ellipse returned_ellipse{};
    ellipse->GetEllipse(&returned_ellipse);
    if (!approximately_equal(returned_ellipse.point.x, 2.0F) ||
        !approximately_equal(returned_ellipse.radius_y, 2.0F) ||
        ellipse->GetBounds(&transform, &returned) != com::ok ||
        !approximately_equal(returned.left, 6.0F) ||
        !approximately_equal(returned.top, -1.0F) ||
        !approximately_equal(returned.right, 22.0F) ||
        !approximately_equal(returned.bottom, 11.0F)) {
        return 55;
    }
    if (ellipse->FillContainsPoint(
            {14.0F, 5.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 1 ||
        ellipse->FillContainsPoint(
            {23.0F, 5.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 0) {
        return 56;
    }
    auto* raw_ellipse_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> ellipse_simplified;
    ellipse_simplified.attach(raw_ellipse_simplified);
    if (ellipse->Simplify(
            compat::geometry_simplification_option::cubics_and_lines,
            &transform,
            core::default_flattening_tolerance,
            ellipse_simplified.get()) != com::ok ||
        raw_ellipse_simplified->begin_count != 1U ||
        raw_ellipse_simplified->end_count != 1U ||
        raw_ellipse_simplified->line_count != 3U ||
        raw_ellipse_simplified->bezier_count != 4U ||
        raw_ellipse_simplified->figure_end != compat::figure_end::closed) {
        return 57;
    }

    const compat::rounded_rectangle rounded_rectangle_value{
        {0.0F, 0.0F, 10.0F, 8.0F}, 3.0F, 2.0F};
    compat::rounded_rectangle_geometry* raw_rounded_rectangle = nullptr;
    if (factory->CreateRoundedRectangleGeometry(
            &rounded_rectangle_value, &raw_rounded_rectangle) != com::ok ||
        raw_rounded_rectangle == nullptr) {
        return 67;
    }
    com::pointer<compat::rounded_rectangle_geometry> rounded_rectangle;
    rounded_rectangle.attach(raw_rounded_rectangle);
    com::pointer<compat::geometry> rounded_rectangle_base;
    if (rounded_rectangle.as(
            compat::geometry_interface_id, rounded_rectangle_base) !=
            com::ok ||
        !rounded_rectangle_base) {
        return 68;
    }
    compat::rounded_rectangle returned_rounded_rectangle{};
    rounded_rectangle->GetRoundedRect(&returned_rounded_rectangle);
    if (!approximately_equal(returned_rounded_rectangle.radius_x, 3.0F) ||
        !approximately_equal(
            returned_rounded_rectangle.rectangle.bottom, 8.0F) ||
        rounded_rectangle->GetBounds(&transform, &returned) != com::ok ||
        !approximately_equal(returned.left, 10.0F) ||
        !approximately_equal(returned.top, -4.0F) ||
        !approximately_equal(returned.right, 30.0F) ||
        !approximately_equal(returned.bottom, 20.0F)) {
        return 69;
    }
    if (rounded_rectangle->FillContainsPoint(
            {20.0F, 8.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 1 ||
        rounded_rectangle->FillContainsPoint(
            {10.2F, -3.7F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 0) {
        return 70;
    }
    auto* raw_rounded_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> rounded_simplified;
    rounded_simplified.attach(raw_rounded_simplified);
    if (rounded_rectangle->Simplify(
            compat::geometry_simplification_option::cubics_and_lines,
            &transform,
            core::default_flattening_tolerance,
            rounded_simplified.get()) != com::ok ||
        raw_rounded_simplified->begin_count != 1U ||
        raw_rounded_simplified->end_count != 1U ||
        raw_rounded_simplified->line_count != 4U ||
        raw_rounded_simplified->bezier_count != 4U ||
        raw_rounded_simplified->figure_end != compat::figure_end::closed) {
        return 71;
    }
    compat::rounded_rectangle invalid_rounded_rectangle =
        rounded_rectangle_value;
    invalid_rounded_rectangle.radius_x = -1.0F;
    raw_rounded_rectangle = reinterpret_cast<
        compat::rounded_rectangle_geometry*>(static_cast<std::uintptr_t>(1U));
    if (factory->CreateRoundedRectangleGeometry(
            &invalid_rounded_rectangle, &raw_rounded_rectangle) !=
            com::invalid_argument ||
        raw_rounded_rectangle != nullptr ||
        factory->CreateRoundedRectangleGeometry(nullptr, nullptr) !=
            com::pointer_error) {
        return 72;
    }

    std::array<compat::geometry*, 2U> group_sources{
        geometry_base.get(), ellipse_base.get()};
    compat::geometry_group* raw_group = nullptr;
    if (factory->CreateGeometryGroup(
            compat::fill_mode::alternate,
            group_sources.data(),
            static_cast<std::uint32_t>(group_sources.size()),
            &raw_group) != com::ok ||
        raw_group == nullptr) {
        return 77;
    }
    com::pointer<compat::geometry_group> group;
    group.attach(raw_group);
    com::pointer<compat::geometry> group_base;
    if (group.as(compat::geometry_interface_id, group_base) != com::ok ||
        !group_base ||
        group->GetFillMode() != compat::fill_mode::alternate ||
        group->GetSourceGeometryCount() != group_sources.size() ||
        group->GetBounds(&transform, &returned) != com::ok ||
        !approximately_equal(returned.left, 6.0F) ||
        !approximately_equal(returned.top, -1.0F) ||
        !approximately_equal(returned.right, 22.0F) ||
        !approximately_equal(returned.bottom, 20.0F)) {
        return 78;
    }
    std::array<compat::geometry*, 2U> returned_group_sources{};
    group->GetSourceGeometries(
        returned_group_sources.data(),
        static_cast<std::uint32_t>(returned_group_sources.size()));
    com::pointer<compat::geometry> returned_group_rectangle;
    com::pointer<compat::geometry> returned_group_ellipse;
    returned_group_rectangle.attach(returned_group_sources[0U]);
    returned_group_ellipse.attach(returned_group_sources[1U]);
    if (returned_group_rectangle.get() != geometry_base.get() ||
        returned_group_ellipse.get() != ellipse_base.get()) {
        return 79;
    }
    auto* raw_group_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> group_simplified;
    group_simplified.attach(raw_group_simplified);
    if (group->Simplify(
            compat::geometry_simplification_option::cubics_and_lines,
            &transform,
            core::default_flattening_tolerance,
            group_simplified.get()) != com::ok ||
        raw_group_simplified->fill_mode != compat::fill_mode::alternate ||
        raw_group_simplified->begin_count != 2U ||
        raw_group_simplified->end_count != 2U ||
        raw_group_simplified->line_count != 6U ||
        raw_group_simplified->bezier_count != 4U) {
        return 80;
    }
    contains = 1;
    if (group->FillContainsPoint(
            {16.0F, 10.0F},
            &transform,
            core::default_flattening_tolerance,
            &contains) != com::ok ||
        contains != 0) {
        return 87;
    }
    std::array<compat::geometry*, 1U> nested_group_source{group_base.get()};
    raw_group = nullptr;
    if (factory->CreateGeometryGroup(
            compat::fill_mode::winding,
            nested_group_source.data(),
            1U,
            &raw_group) != com::ok ||
        raw_group == nullptr) {
        return 81;
    }
    com::pointer<compat::geometry_group> nested_group;
    nested_group.attach(raw_group);
    std::int32_t portable_nested_group_contains = 0;
    compat::rectangle_f portable_nested_group_bounds{};
    auto* raw_nested_group_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        nested_group_simplified;
    nested_group_simplified.attach(raw_nested_group_simplified);
    if (nested_group->GetFillMode() != compat::fill_mode::winding ||
        nested_group->GetSourceGeometryCount() != 1U ||
        nested_group->GetBounds(
            &transform, &portable_nested_group_bounds) != com::ok ||
        !approximately_equal(portable_nested_group_bounds.left, 6.0F) ||
        !approximately_equal(portable_nested_group_bounds.top, -1.0F) ||
        !approximately_equal(portable_nested_group_bounds.right, 22.0F) ||
        !approximately_equal(portable_nested_group_bounds.bottom, 20.0F) ||
        nested_group->FillContainsPoint(
            {16.0F, 10.0F},
            &transform,
            core::default_flattening_tolerance,
            &portable_nested_group_contains) != com::ok ||
        nested_group->Simplify(
            compat::geometry_simplification_option::cubics_and_lines,
            &transform,
            core::default_flattening_tolerance,
            nested_group_simplified.get()) != com::ok ||
        raw_nested_group_simplified->fill_mode !=
            compat::fill_mode::winding ||
        raw_nested_group_simplified->begin_count != 2U ||
        raw_nested_group_simplified->end_count != 2U ||
        raw_nested_group_simplified->line_count != 6U ||
        raw_nested_group_simplified->bezier_count != 4U) {
        return 315;
    }
    raw_group = reinterpret_cast<compat::geometry_group*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateGeometryGroup(
            static_cast<compat::fill_mode>(99U),
            nullptr,
            0U,
            &raw_group) != com::invalid_argument ||
        raw_group != nullptr) {
        return 88;
    }
    raw_group = reinterpret_cast<compat::geometry_group*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateGeometryGroup(
            compat::fill_mode::winding,
            nullptr,
            1U,
            &raw_group) != com::invalid_argument ||
        raw_group != nullptr) {
        return 89;
    }
    raw_group = reinterpret_cast<compat::geometry_group*>(
        static_cast<std::uintptr_t>(1U));
    if (second_factory->CreateGeometryGroup(
            compat::fill_mode::winding,
            group_sources.data(),
            static_cast<std::uint32_t>(group_sources.size()),
            &raw_group) != compat::wrong_factory ||
        raw_group != nullptr) {
        return 90;
    }

    const compat::stroke_style_properties stroke_properties{
        compat::cap_style::round,
        compat::cap_style::square,
        compat::cap_style::triangle,
        compat::line_join::bevel,
        4.0F,
        compat::dash_style::custom,
        0.5F};
    const std::array<float, 4U> stroke_dashes{2.0F, 1.0F, 0.5F, 1.0F};
    compat::stroke_style* raw_stroke_style = nullptr;
    if (factory->CreateStrokeStyle(
            &stroke_properties,
            stroke_dashes.data(),
            static_cast<std::uint32_t>(stroke_dashes.size()),
            &raw_stroke_style) != com::ok ||
        raw_stroke_style == nullptr) {
        return 91;
    }
    com::pointer<compat::stroke_style> stroke_style;
    stroke_style.attach(raw_stroke_style);
    com::pointer<compat::resource> stroke_resource;
    if (stroke_style.as(
            compat::resource_interface_id, stroke_resource) != com::ok ||
        !stroke_resource ||
        stroke_style->GetStartCap() != compat::cap_style::round ||
        stroke_style->GetEndCap() != compat::cap_style::square ||
        stroke_style->GetDashCap() != compat::cap_style::triangle ||
        stroke_style->GetLineJoin() != compat::line_join::bevel ||
        !approximately_equal(stroke_style->GetMiterLimit(), 4.0F) ||
        !approximately_equal(stroke_style->GetDashOffset(), 0.5F) ||
        stroke_style->GetDashStyle() != compat::dash_style::custom ||
        stroke_style->GetDashesCount() !=
            static_cast<std::uint32_t>(stroke_dashes.size())) {
        return 92;
    }
    std::array<float, 4U> returned_stroke_dashes{};
    stroke_style->GetDashes(
        returned_stroke_dashes.data(),
        static_cast<std::uint32_t>(returned_stroke_dashes.size()));
    if (!approximately_equal(returned_stroke_dashes[0U], 2.0F) ||
        !approximately_equal(returned_stroke_dashes[1U], 1.0F) ||
        !approximately_equal(returned_stroke_dashes[2U], 0.5F) ||
        !approximately_equal(returned_stroke_dashes[3U], 1.0F)) {
        return 93;
    }
    raw_stroke_style = reinterpret_cast<compat::stroke_style*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateStrokeStyle(
            &stroke_properties, nullptr, 0U, &raw_stroke_style) !=
            com::invalid_argument ||
        raw_stroke_style != nullptr) {
        return 94;
    }
    compat::stroke_style_properties solid_stroke_properties =
        stroke_properties;
    solid_stroke_properties.dash = compat::dash_style::solid;
    raw_stroke_style = reinterpret_cast<compat::stroke_style*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateStrokeStyle(
            &solid_stroke_properties,
            stroke_dashes.data(),
            static_cast<std::uint32_t>(stroke_dashes.size()),
            &raw_stroke_style) != com::invalid_argument ||
        raw_stroke_style != nullptr) {
        return 95;
    }

    const compat::drawing_state_description drawing_state_description{
        compat::antialias_mode::aliased,
        compat::text_antialias_mode::grayscale,
        17U,
        23U,
        {1.0F, 0.25F, -0.5F, 2.0F, 3.0F, -4.0F}};
    std::uint32_t drawing_state_parameters_destruction_count = 0U;
    auto* raw_drawing_state_parameters = new fake_rendering_parameters(
        &drawing_state_parameters_destruction_count);
    com::pointer<fake_rendering_parameters> drawing_state_parameters;
    drawing_state_parameters.attach(raw_drawing_state_parameters);
    compat::drawing_state_block* raw_drawing_state = nullptr;
    if (factory->CreateDrawingStateBlock(
            &drawing_state_description,
            drawing_state_parameters.get(),
            &raw_drawing_state) != com::ok ||
        raw_drawing_state == nullptr) {
        return 100;
    }
    com::pointer<compat::drawing_state_block> drawing_state;
    drawing_state.attach(raw_drawing_state);
    com::pointer<compat::resource> drawing_state_resource;
    if (drawing_state.as(
            compat::resource_interface_id, drawing_state_resource) !=
            com::ok ||
        !drawing_state_resource) {
        return 101;
    }
    compat::drawing_state_description returned_drawing_state{};
    drawing_state->GetDescription(&returned_drawing_state);
    compat::rendering_parameters* raw_text_parameters = nullptr;
    drawing_state->GetTextRenderingParams(&raw_text_parameters);
    com::pointer<compat::rendering_parameters> returned_text_parameters;
    returned_text_parameters.attach(raw_text_parameters);
    if (returned_drawing_state.antialias !=
            compat::antialias_mode::aliased ||
        returned_drawing_state.text_antialias !=
            compat::text_antialias_mode::grayscale ||
        returned_drawing_state.tag1 != 17U ||
        returned_drawing_state.tag2 != 23U ||
        !approximately_equal(returned_drawing_state.transform.m12, 0.25F) ||
        returned_text_parameters.get() !=
            static_cast<compat::rendering_parameters*>(
                drawing_state_parameters.get())) {
        return 102;
    }
    compat::drawing_state_description changed_drawing_state =
        drawing_state_description;
    changed_drawing_state.tag1 = 31U;
    changed_drawing_state.transform.m31 = 9.0F;
    drawing_state->SetDescription(&changed_drawing_state);
    drawing_state->SetTextRenderingParams(nullptr);
    returned_drawing_state = {};
    raw_text_parameters = reinterpret_cast<compat::rendering_parameters*>(
        static_cast<std::uintptr_t>(1U));
    drawing_state->GetDescription(&returned_drawing_state);
    drawing_state->GetTextRenderingParams(&raw_text_parameters);
    if (returned_drawing_state.tag1 != 31U ||
        !approximately_equal(returned_drawing_state.transform.m31, 9.0F) ||
        raw_text_parameters != nullptr) {
        return 103;
    }
    compat::drawing_state_description invalid_drawing_state =
        drawing_state_description;
    invalid_drawing_state.transform.m11 =
        std::numeric_limits<float>::infinity();
    raw_drawing_state = reinterpret_cast<compat::drawing_state_block*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateDrawingStateBlock(
            &invalid_drawing_state, nullptr, &raw_drawing_state) !=
            com::invalid_argument ||
        raw_drawing_state != nullptr) {
        return 104;
    }
    raw_drawing_state = nullptr;
    if (factory->CreateDrawingStateBlock(
            nullptr, nullptr, &raw_drawing_state) != com::ok ||
        raw_drawing_state == nullptr) {
        return 105;
    }
    com::pointer<compat::drawing_state_block> default_drawing_state;
    default_drawing_state.attach(raw_drawing_state);
    returned_drawing_state = {};
    default_drawing_state->GetDescription(&returned_drawing_state);
    if (returned_drawing_state.antialias !=
            compat::antialias_mode::per_primitive ||
        returned_drawing_state.text_antialias !=
            compat::text_antialias_mode::default_value ||
        returned_drawing_state.tag1 != 0U ||
        returned_drawing_state.tag2 != 0U ||
        !approximately_equal(returned_drawing_state.transform.m11, 1.0F) ||
        !approximately_equal(returned_drawing_state.transform.m22, 1.0F)) {
        return 106;
    }

    com::pointer<compat::factory_native> resource_factory;
    if (factory.as(
            compat::factory_native_interface_id, resource_factory) !=
            com::ok ||
        !resource_factory) {
        return 109;
    }
    const compat::color_f brush_color{0.25F, 0.5F, 0.75F, 1.0F};
    const compat::brush_properties brush_properties{
        0.625F,
        {1.0F, 0.25F, -0.5F, 2.0F, 3.0F, -4.0F}};
    compat::solid_color_brush* raw_brush = nullptr;
    if (resource_factory->CreateSolidColorBrush(
            &brush_color, &brush_properties, &raw_brush) != com::ok ||
        raw_brush == nullptr) {
        return 110;
    }
    com::pointer<compat::solid_color_brush> solid_brush;
    solid_brush.attach(raw_brush);
    com::pointer<compat::resource> brush_resource;
    com::pointer<compat::brush> brush_base;
    if (solid_brush.as(
            compat::resource_interface_id, brush_resource) != com::ok ||
        solid_brush.as(compat::brush_interface_id, brush_base) != com::ok ||
        !brush_resource || !brush_base) {
        return 111;
    }
    compat::factory* raw_brush_factory = nullptr;
    solid_brush->GetFactory(&raw_brush_factory);
    com::pointer<compat::factory> brush_factory;
    brush_factory.attach(raw_brush_factory);
    compat::matrix_3x2_f returned_brush_transform{};
    solid_brush->GetTransform(&returned_brush_transform);
    const compat::color_f returned_brush_color = solid_brush->GetColor();
    if (brush_factory.get() != factory.get() ||
        !approximately_equal(solid_brush->GetOpacity(), 0.625F) ||
        !approximately_equal(returned_brush_transform.m12, 0.25F) ||
        !approximately_equal(returned_brush_transform.m31, 3.0F) ||
        !approximately_equal(returned_brush_color.red, 0.25F) ||
        !approximately_equal(returned_brush_color.blue, 0.75F)) {
        return 112;
    }
    const compat::color_f changed_brush_color{1.0F, 0.0F, 0.5F, 0.75F};
    const compat::matrix_3x2_f changed_brush_transform{
        2.0F, 0.0F, 0.0F, 3.0F, -1.0F, 5.0F};
    solid_brush->SetColor(&changed_brush_color);
    solid_brush->SetOpacity(0.5F);
    solid_brush->SetTransform(&changed_brush_transform);
    const compat::color_f changed_returned_brush_color =
        solid_brush->GetColor();
    returned_brush_transform = {};
    solid_brush->GetTransform(&returned_brush_transform);
    if (!approximately_equal(changed_returned_brush_color.red, 1.0F) ||
        !approximately_equal(changed_returned_brush_color.alpha, 0.75F) ||
        !approximately_equal(solid_brush->GetOpacity(), 0.5F) ||
        !approximately_equal(returned_brush_transform.m22, 3.0F) ||
        !approximately_equal(returned_brush_transform.m32, 5.0F)) {
        return 113;
    }
    compat::color_f invalid_brush_color = changed_brush_color;
    invalid_brush_color.green = std::numeric_limits<float>::infinity();
    compat::matrix_3x2_f invalid_brush_transform = changed_brush_transform;
    invalid_brush_transform.m11 =
        std::numeric_limits<float>::quiet_NaN();
    solid_brush->SetColor(&invalid_brush_color);
    solid_brush->SetOpacity(-1.0F);
    solid_brush->SetTransform(&invalid_brush_transform);
    returned_brush_transform = {};
    solid_brush->GetTransform(&returned_brush_transform);
    if (!approximately_equal(solid_brush->GetColor().green, 0.0F) ||
        !approximately_equal(solid_brush->GetOpacity(), 0.5F) ||
        !approximately_equal(returned_brush_transform.m11, 2.0F)) {
        return 114;
    }
    raw_brush = reinterpret_cast<compat::solid_color_brush*>(
        static_cast<std::uintptr_t>(1U));
    if (resource_factory->CreateSolidColorBrush(
            &invalid_brush_color, nullptr, &raw_brush) !=
            com::invalid_argument ||
        raw_brush != nullptr ||
        resource_factory->CreateSolidColorBrush(
            &brush_color, nullptr, nullptr) != com::pointer_error) {
        return 115;
    }

    com::pointer<compat::scene_factory_native> scene_factory;
    if (factory.as(
            compat::scene_factory_native_interface_id, scene_factory) !=
            com::ok ||
        !scene_factory) {
        return 118;
    }
    const compat::scene_render_target_properties target_properties{
        640U, 480U, 96.0F, 96.0F, 7001U, 11U};
    compat::render_target* raw_target = nullptr;
    if (scene_factory->CreateSceneRenderTarget(
            &target_properties, &raw_target) != com::ok ||
        raw_target == nullptr) {
        return 119;
    }
    com::pointer<compat::render_target> target;
    target.attach(raw_target);
    com::pointer<compat::resource> target_resource;
    com::pointer<compat::scene_render_target_native> scene_target;
    if (target.as(compat::resource_interface_id, target_resource) != com::ok ||
        target.as(
            compat::scene_render_target_native_interface_id,
            scene_target) != com::ok ||
        !target_resource || !scene_target) {
        return 120;
    }
    const compat::size_u target_pixel_size = target->GetPixelSize();
    const compat::size_f target_size = target->GetSize();
    if (target_pixel_size.width != 640U ||
        target_pixel_size.height != 480U ||
        !approximately_equal(target_size.width, 640.0F) ||
        !approximately_equal(target_size.height, 480.0F)) {
        return 121;
    }
    std::uint32_t rendering_parameters_destruction_count = 0U;
    auto* raw_rendering_parameters = new fake_rendering_parameters(
        &rendering_parameters_destruction_count);
    target->SetTextRenderingParams(raw_rendering_parameters);
    raw_rendering_parameters->Release();
    target->SaveDrawingState(default_drawing_state.get());
    target->SetTextRenderingParams(nullptr);
    if (rendering_parameters_destruction_count != 0U) {
        return 259;
    }
    target->RestoreDrawingState(default_drawing_state.get());
    default_drawing_state->SetTextRenderingParams(nullptr);
    compat::rendering_parameters* returned_rendering_parameters = nullptr;
    target->GetTextRenderingParams(&returned_rendering_parameters);
    if (returned_rendering_parameters == nullptr ||
        rendering_parameters_destruction_count != 0U ||
        !approximately_equal(
            returned_rendering_parameters->GetGamma(), 2.2F) ||
        returned_rendering_parameters->GetPixelGeometry() !=
            compat::pixel_geometry::rgb ||
        returned_rendering_parameters->GetRenderingMode() !=
            compat::rendering_mode::natural_symmetric) {
        if (returned_rendering_parameters != nullptr) {
            returned_rendering_parameters->Release();
        }
        return 259;
    }
    returned_rendering_parameters->Release();
    target->SetTextRenderingParams(nullptr);
    returned_rendering_parameters = reinterpret_cast<
        compat::rendering_parameters*>(static_cast<std::uintptr_t>(1U));
    target->GetTextRenderingParams(&returned_rendering_parameters);
    if (returned_rendering_parameters != nullptr ||
        rendering_parameters_destruction_count != 1U) {
        return 260;
    }
    compat::solid_color_brush* raw_target_brush = nullptr;
    if (target->CreateSolidColorBrush(
            &brush_color, nullptr, &raw_target_brush) != com::ok ||
        raw_target_brush == nullptr) {
        return 122;
    }
    com::pointer<compat::solid_color_brush> target_brush;
    target_brush.attach(raw_target_brush);
    const compat::gradient_stop gradient_stops[]{
        {0.0F, {1.0F, 0.0F, 0.0F, 1.0F}},
        {0.5F, {0.0F, 1.0F, 0.0F, 0.75F}},
        {1.0F, {0.0F, 0.0F, 1.0F, 1.0F}}};
    compat::gradient_stop_collection* raw_gradient_stops = nullptr;
    if (target->CreateGradientStopCollection(
            gradient_stops,
            3U,
            compat::gamma::gamma_2_2,
            compat::extend_mode::mirror,
            &raw_gradient_stops) != com::ok ||
        raw_gradient_stops == nullptr) {
        return 131;
    }
    com::pointer<compat::gradient_stop_collection> gradient_collection;
    gradient_collection.attach(raw_gradient_stops);
    compat::gradient_stop copied_gradient_stops[3]{};
    gradient_collection->GetGradientStops(copied_gradient_stops, 3U);
    if (gradient_collection->GetGradientStopCount() != 3U ||
        gradient_collection->GetColorInterpolationGamma() !=
            compat::gamma::gamma_2_2 ||
        gradient_collection->GetExtendMode() !=
            compat::extend_mode::mirror ||
        !approximately_equal(copied_gradient_stops[1].position, 0.5F) ||
        !approximately_equal(copied_gradient_stops[1].color.alpha, 0.75F)) {
        return 132;
    }
    const compat::linear_gradient_brush_properties linear_properties{
        {2.0F, 3.0F}, {30.0F, 21.0F}};
    compat::linear_gradient_brush* raw_linear_brush = nullptr;
    if (target->CreateLinearGradientBrush(
            &linear_properties,
            nullptr,
            gradient_collection.get(),
            &raw_linear_brush) != com::ok ||
        raw_linear_brush == nullptr) {
        return 133;
    }
    com::pointer<compat::linear_gradient_brush> linear_brush;
    linear_brush.attach(raw_linear_brush);
    linear_brush->SetStartPoint({4.0F, 5.0F});
    linear_brush->SetEndPoint({32.0F, 23.0F});
    linear_brush->SetStartPoint(
        {std::numeric_limits<float>::infinity(), 0.0F});
    if (!approximately_equal(linear_brush->GetStartPoint().x, 4.0F) ||
        !approximately_equal(linear_brush->GetEndPoint().y, 23.0F)) {
        return 134;
    }
    const compat::radial_gradient_brush_properties radial_properties{
        {17.0F, 14.0F}, {-2.0F, 1.0F}, 12.0F, 8.0F};
    const compat::brush_properties radial_brush_properties{
        0.875F, {1.0F, 0.0F, 0.0F, 1.0F, 1.0F, -1.0F}};
    compat::radial_gradient_brush* raw_radial_brush = nullptr;
    if (target->CreateRadialGradientBrush(
            &radial_properties,
            &radial_brush_properties,
            gradient_collection.get(),
            &raw_radial_brush) != com::ok ||
        raw_radial_brush == nullptr) {
        return 135;
    }
    com::pointer<compat::radial_gradient_brush> radial_brush;
    radial_brush.attach(raw_radial_brush);
    radial_brush->SetRadiusX(10.0F);
    radial_brush->SetRadiusY(-1.0F);
    if (!approximately_equal(radial_brush->GetRadiusX(), 10.0F) ||
        !approximately_equal(radial_brush->GetRadiusY(), 8.0F) ||
        !approximately_equal(radial_brush->GetOpacity(), 0.875F)) {
        return 136;
    }
    compat::gradient_stop invalid_gradient_stops[]{
        {0.75F, {1.0F, 0.0F, 0.0F, 1.0F}},
        {0.25F, {0.0F, 0.0F, 1.0F, 1.0F}}};
    raw_gradient_stops = reinterpret_cast<compat::gradient_stop_collection*>(
        static_cast<std::uintptr_t>(1U));
    if (target->CreateGradientStopCollection(
            invalid_gradient_stops,
            2U,
            compat::gamma::gamma_2_2,
            compat::extend_mode::clamp,
            &raw_gradient_stops) != com::invalid_argument ||
        raw_gradient_stops != nullptr) {
        return 137;
    }
    compat::factory* raw_other_factory = nullptr;
    if (compat::create_factory(&raw_other_factory) != com::ok ||
        raw_other_factory == nullptr) {
        return 140;
    }
    com::pointer<compat::factory> other_factory;
    other_factory.attach(raw_other_factory);
    com::pointer<compat::scene_factory_native> other_scene_factory;
    if (other_factory.as(
            compat::scene_factory_native_interface_id,
            other_scene_factory) != com::ok ||
        !other_scene_factory) {
        return 141;
    }
    compat::render_target* raw_other_target = nullptr;
    if (other_scene_factory->CreateSceneRenderTarget(
            &target_properties, &raw_other_target) != com::ok ||
        raw_other_target == nullptr) {
        return 142;
    }
    com::pointer<compat::render_target> other_target;
    other_target.attach(raw_other_target);
    compat::gradient_stop_collection* raw_foreign_stops = nullptr;
    if (other_target->CreateGradientStopCollection(
            gradient_stops,
            3U,
            compat::gamma::gamma_2_2,
            compat::extend_mode::clamp,
            &raw_foreign_stops) != com::ok ||
        raw_foreign_stops == nullptr) {
        return 143;
    }
    com::pointer<compat::gradient_stop_collection> foreign_stops;
    foreign_stops.attach(raw_foreign_stops);
    raw_linear_brush = reinterpret_cast<compat::linear_gradient_brush*>(
        static_cast<std::uintptr_t>(1U));
    if (target->CreateLinearGradientBrush(
            &linear_properties,
            nullptr,
            foreign_stops.get(),
            &raw_linear_brush) != compat::wrong_factory ||
        raw_linear_brush != nullptr) {
        return 144;
    }
    target->BeginDraw();
    const compat::color_f clear_color{0.05F, 0.1F, 0.15F, 1.0F};
    target->Clear(&clear_color);
    target->FillRectangle(
        &rectangle, static_cast<compat::brush*>(target_brush.get()));
    target->DrawLine(
        {0.0F, 0.0F},
        {20.0F, 10.0F},
        static_cast<compat::brush*>(target_brush.get()),
        2.0F,
        nullptr);
    const compat::rounded_rectangle target_rounded_rectangle{
        rounded_rectangle_value.rectangle, 2.0F, 2.0F};
    target->DrawRoundedRectangle(
        &target_rounded_rectangle,
        static_cast<compat::brush*>(target_brush.get()),
        1.5F,
        nullptr);
    target->FillEllipse(
        &ellipse_value, static_cast<compat::brush*>(target_brush.get()));
    target->FillRectangle(
        &rectangle, static_cast<compat::brush*>(linear_brush.get()));
    target->FillEllipse(
        &ellipse_value, static_cast<compat::brush*>(radial_brush.get()));
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 123;
    }
    compat::scene_render_target_summary target_summary{};
    scene_target->GetSummary(&target_summary);
    const std::uint64_t required_scene_size =
        scene_target->GetRequiredSceneSize();
    if (target_summary.scene_id != 7001U ||
        target_summary.generation != 11U ||
        target_summary.draw_count != 6U || target_summary.has_clear != 1 ||
        !approximately_equal(target_summary.clear_color.green, 0.1F) ||
        required_scene_size < sizeof(progpu_native_scene_header)) {
        return 124;
    }
    std::vector<std::byte> scene_bytes(
        static_cast<std::size_t>(required_scene_size));
    std::uint64_t written_scene_size = 0U;
    if (scene_target->BuildScene(
            scene_bytes.data(),
            scene_bytes.size(),
            &written_scene_size) != com::ok ||
        written_scene_size != required_scene_size) {
        return 125;
    }
    const auto* scene_header = reinterpret_cast<
        const progpu_native_scene_header*>(scene_bytes.data());
    if (scene_header->scene_id != 7001U ||
        scene_header->generation != 11U ||
        scene_header->command_count != 6U ||
        scene_header->total_size != written_scene_size) {
        return 126;
    }
    const auto* scene_resources = reinterpret_cast<
        const progpu_native_scene_resource*>(
        scene_bytes.data() + scene_header->resource_offset);
    const progpu_native_scene_resource* brush_table = nullptr;
    for (std::uint32_t index = 0U;
         index < scene_header->resource_count;
         ++index) {
        const auto* scene_resource = reinterpret_cast<
            const progpu_native_scene_resource*>(
            reinterpret_cast<const std::byte*>(scene_resources) +
            index * scene_header->resource_stride);
        if (scene_resource->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_BRUSH_TABLE) {
            brush_table = scene_resource;
            break;
        }
    }
    if (brush_table == nullptr ||
        brush_table->payload_size % sizeof(progpu_native_scene_brush) != 0U ||
        brush_table->auxiliary_size !=
            6U * sizeof(progpu_native_scene_gradient_stop)) {
        return 138;
    }
    const auto* scene_brushes = reinterpret_cast<
        const progpu_native_scene_brush*>(
        scene_bytes.data() + brush_table->payload_offset);
    const std::size_t scene_brush_count = brush_table->payload_size /
        sizeof(progpu_native_scene_brush);
    bool found_linear = false;
    bool found_radial = false;
    for (std::size_t index = 0U; index < scene_brush_count; ++index) {
        found_linear = found_linear ||
            (scene_brushes[index].type ==
                    PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT &&
                scene_brushes[index].spread_method ==
                    PROGPU_NATIVE_SCENE_GRADIENT_REFLECT &&
                scene_brushes[index].stop_count == 3U);
        found_radial = found_radial ||
            (scene_brushes[index].type ==
                    PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT &&
                approximately_equal(scene_brushes[index].radius, 10.0F) &&
                approximately_equal(scene_brushes[index].radius_y, 8.0F));
    }
    if (!found_linear || !found_radial) {
        return 139;
    }

    target->BeginDraw();
    const compat::rectangle_f outer_clip{2.0F, 3.0F, 12.0F, 13.0F};
    const compat::rectangle_f inner_clip{5.0F, 1.0F, 20.0F, 9.0F};
    target->PushAxisAlignedClip(
        &outer_clip, compat::antialias_mode::aliased);
    target->PushAxisAlignedClip(
        &inner_clip, compat::antialias_mode::aliased);
    target->FillRectangle(
        &rectangle, static_cast<compat::brush*>(target_brush.get()));
    target->PopAxisAlignedClip();
    target->PopAxisAlignedClip();
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 173;
    }
    const std::uint64_t clipped_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> clipped_scene(
        static_cast<std::size_t>(clipped_scene_size));
    std::uint64_t clipped_scene_written = 0U;
    if (clipped_scene_size < sizeof(progpu_native_scene_header) ||
        scene_target->BuildScene(
            clipped_scene.data(),
            clipped_scene.size(),
            &clipped_scene_written) != com::ok ||
        clipped_scene_written != clipped_scene_size) {
        return 174;
    }
    const auto* clipped_header = reinterpret_cast<
        const progpu_native_scene_header*>(clipped_scene.data());
    if (clipped_header->command_count != 5U) {
        return 175;
    }
    const auto clipped_command = [clipped_header, &clipped_scene](
        std::uint32_t index) {
        return reinterpret_cast<const progpu_native_scene_command*>(
            clipped_scene.data() + clipped_header->command_offset +
            static_cast<std::size_t>(index) *
                clipped_header->command_stride);
    };
    const auto* first_clip_save = clipped_command(0U);
    const auto* second_clip_save = clipped_command(1U);
    if (first_clip_save->kind != PROGPU_NATIVE_SCENE_COMMAND_SAVE ||
        second_clip_save->kind != PROGPU_NATIVE_SCENE_COMMAND_SAVE ||
        clipped_command(2U)->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
        clipped_command(3U)->kind != PROGPU_NATIVE_SCENE_COMMAND_RESTORE ||
        clipped_command(4U)->kind != PROGPU_NATIVE_SCENE_COMMAND_RESTORE ||
        first_clip_save->state_index >= clipped_header->resource_count ||
        second_clip_save->state_index >= clipped_header->resource_count) {
        return 176;
    }
    const auto clipped_resource = [clipped_header, &clipped_scene](
        std::uint32_t index) {
        return reinterpret_cast<const progpu_native_scene_resource*>(
            clipped_scene.data() + clipped_header->resource_offset +
            static_cast<std::size_t>(index) *
                clipped_header->resource_stride);
    };
    const auto* first_clip_resource = clipped_resource(
        first_clip_save->state_index);
    const auto* second_clip_resource = clipped_resource(
        second_clip_save->state_index);
    if (first_clip_resource->kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE ||
        second_clip_resource->kind != PROGPU_NATIVE_SCENE_RESOURCE_STATE ||
        first_clip_resource->payload_size <
            sizeof(progpu_native_scene_state) ||
        second_clip_resource->payload_size <
            sizeof(progpu_native_scene_state)) {
        return 177;
    }
    const auto* first_clip_state = reinterpret_cast<
        const progpu_native_scene_state*>(
            clipped_scene.data() + first_clip_resource->payload_offset);
    const auto* second_clip_state = reinterpret_cast<
        const progpu_native_scene_state*>(
            clipped_scene.data() + second_clip_resource->payload_offset);
    if (first_clip_state->flags != PROGPU_NATIVE_SCENE_STATE_CLIP_RECT ||
        second_clip_state->flags != PROGPU_NATIVE_SCENE_STATE_CLIP_RECT ||
        !approximately_equal(first_clip_state->clip_rect.x, 2.0F) ||
        !approximately_equal(first_clip_state->clip_rect.y, 3.0F) ||
        !approximately_equal(first_clip_state->clip_rect.width, 10.0F) ||
        !approximately_equal(first_clip_state->clip_rect.height, 10.0F) ||
        !approximately_equal(second_clip_state->clip_rect.x, 5.0F) ||
        !approximately_equal(second_clip_state->clip_rect.y, 3.0F) ||
        !approximately_equal(second_clip_state->clip_rect.width, 7.0F) ||
        !approximately_equal(second_clip_state->clip_rect.height, 6.0F)) {
        return 178;
    }

    target->BeginDraw();
    target->PushAxisAlignedClip(
        &outer_clip, compat::antialias_mode::per_primitive);
    target->FillRectangle(
        &rectangle, static_cast<compat::brush*>(target_brush.get()));
    target->PopAxisAlignedClip();
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 179;
    }
    const std::uint64_t antialiased_clip_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> antialiased_clip_scene(
        static_cast<std::size_t>(antialiased_clip_size));
    std::uint64_t antialiased_clip_written = 0U;
    if (antialiased_clip_size < sizeof(progpu_native_scene_header) ||
        scene_target->BuildScene(
            antialiased_clip_scene.data(),
            antialiased_clip_scene.size(),
            &antialiased_clip_written) != com::ok ||
        antialiased_clip_written != antialiased_clip_size) {
        return 179;
    }
    const auto* antialiased_clip_header = reinterpret_cast<
        const progpu_native_scene_header*>(antialiased_clip_scene.data());
    const auto* antialiased_clip_push = reinterpret_cast<
        const progpu_native_scene_command*>(
            antialiased_clip_scene.data() +
            antialiased_clip_header->command_offset);
    const auto* antialiased_clip_draw = reinterpret_cast<
        const progpu_native_scene_command*>(
            reinterpret_cast<const std::byte*>(antialiased_clip_push) +
            antialiased_clip_header->command_stride);
    const auto* antialiased_clip_pop = reinterpret_cast<
        const progpu_native_scene_command*>(
            reinterpret_cast<const std::byte*>(antialiased_clip_draw) +
            antialiased_clip_header->command_stride);
    if (antialiased_clip_header->command_count != 3U ||
        antialiased_clip_push->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
        antialiased_clip_draw->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
        antialiased_clip_pop->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER) {
        return 179;
    }
    target->BeginDraw();
    target->PopAxisAlignedClip();
    if (target->EndDraw(nullptr, nullptr) != compat::wrong_state ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 180;
    }
    target->BeginDraw();
    target->PushAxisAlignedClip(
        &outer_clip, compat::antialias_mode::aliased);
    if (target->EndDraw(nullptr, nullptr) != compat::wrong_state ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 181;
    }

    compat::layer* raw_layer = nullptr;
    if (target->CreateLayer(nullptr, &raw_layer) != com::ok ||
        raw_layer == nullptr) {
        return 182;
    }
    com::pointer<compat::layer> target_layer;
    target_layer.attach(raw_layer);
    com::pointer<compat::resource> layer_resource;
    com::pointer<compat::layer> queried_layer;
    if (target_layer.as(
            compat::resource_interface_id, layer_resource) != com::ok ||
        !layer_resource ||
        target_layer.as(compat::layer_interface_id, queried_layer) != com::ok ||
        queried_layer.get() != target_layer.get()) {
        return 183;
    }
    compat::factory* raw_layer_factory = nullptr;
    target_layer->GetFactory(&raw_layer_factory);
    com::pointer<compat::factory> layer_factory;
    layer_factory.attach(raw_layer_factory);
    const compat::size_f initial_layer_size = target_layer->GetSize();
    if (layer_factory.get() != factory.get() ||
        !approximately_equal(initial_layer_size.width, 0.0F) ||
        !approximately_equal(initial_layer_size.height, 0.0F)) {
        return 184;
    }
    const compat::size_f invalid_layer_size{-1.0F, 10.0F};
    raw_layer = reinterpret_cast<compat::layer*>(
        static_cast<std::uintptr_t>(1U));
    if (target->CreateLayer(&invalid_layer_size, &raw_layer) !=
            com::invalid_argument ||
        raw_layer != nullptr ||
        target->CreateLayer(nullptr, nullptr) != com::pointer_error) {
        return 185;
    }

    const compat::rectangle_f layer_bounds{0.0F, 0.0F, 20.0F, 20.0F};
    const compat::layer_parameters layer_parameters{
        layer_bounds,
        nullptr,
        compat::antialias_mode::per_primitive,
        {1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F},
        0.5F,
        nullptr,
        compat::layer_options::none};
    target->BeginDraw();
    target->PushLayer(&layer_parameters, target_layer.get());
    target->FillRectangle(
        &rectangle, static_cast<compat::brush*>(target_brush.get()));
    target->PopLayer();
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 186;
    }
    const compat::size_f grown_layer_size = target_layer->GetSize();
    const std::uint64_t layer_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> layer_scene(
        static_cast<std::size_t>(layer_scene_size));
    std::uint64_t layer_scene_written = 0U;
    if (!approximately_equal(grown_layer_size.width, 20.0F) ||
        !approximately_equal(grown_layer_size.height, 20.0F) ||
        layer_scene_size < sizeof(progpu_native_scene_header) ||
        scene_target->BuildScene(
            layer_scene.data(),
            layer_scene.size(),
            &layer_scene_written) != com::ok ||
        layer_scene_written != layer_scene_size) {
        return 187;
    }
    const auto* layer_header = reinterpret_cast<
        const progpu_native_scene_header*>(layer_scene.data());
    const auto layer_command = [layer_header, &layer_scene](
        std::uint32_t index) {
        return reinterpret_cast<const progpu_native_scene_command*>(
            layer_scene.data() + layer_header->command_offset +
            static_cast<std::size_t>(index) * layer_header->command_stride);
    };
    const auto* push_layer_command = layer_command(0U);
    if (layer_header->command_count != 3U ||
        push_layer_command->kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
        layer_command(1U)->kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
        layer_command(2U)->kind != PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER ||
        push_layer_command->payload_size < sizeof(progpu_native_scene_layer)) {
        return 188;
    }
    const auto* native_layer = reinterpret_cast<
        const progpu_native_scene_layer*>(
            layer_scene.data() + push_layer_command->payload_offset);
    if (native_layer->flags != PROGPU_NATIVE_SCENE_LAYER_BOUNDS ||
        !approximately_equal(native_layer->bounds.x, 0.0F) ||
        !approximately_equal(native_layer->bounds.y, 0.0F) ||
        !approximately_equal(native_layer->bounds.width, 20.0F) ||
        !approximately_equal(native_layer->bounds.height, 20.0F) ||
        !approximately_equal(native_layer->opacity, 0.5F) ||
        native_layer->blend_mode != PROGPU_NATIVE_BLEND_SRC_OVER ||
        native_layer->mask_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX ||
        native_layer->effect_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX) {
        return 189;
    }

    target->BeginDraw();
    target->PushLayer(&layer_parameters, target_layer.get());
    target->PushLayer(&layer_parameters, target_layer.get());
    if (target->EndDraw(nullptr, nullptr) != compat::wrong_state ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 190;
    }
    target->BeginDraw();
    target->PushLayer(&layer_parameters, target_layer.get());
    target->PushAxisAlignedClip(
        &outer_clip, compat::antialias_mode::aliased);
    target->PopLayer();
    if (target->EndDraw(nullptr, nullptr) != compat::wrong_state ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 191;
    }
    target->BeginDraw();
    target->PushLayer(&layer_parameters, target_layer.get());
    target->PopLayer();
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 192;
    }
    compat::layer_parameters unsupported_layer = layer_parameters;
    unsupported_layer.options =
        compat::layer_options::initialize_for_cleartype;
    target->BeginDraw();
    target->PushLayer(&unsupported_layer, target_layer.get());
    if (target->EndDraw(nullptr, nullptr) != compat::not_implemented ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 193;
    }
    target->BeginDraw();
    target->DrawRectangle(
        &rectangle,
        static_cast<compat::brush*>(target_brush.get()),
        0.0F,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::invalid_argument ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 127;
    }
    target->BeginDraw();
    target->DrawBitmap(
        nullptr,
        nullptr,
        1.0F,
        compat::bitmap_interpolation_mode::linear,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::invalid_argument ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 130;
    }

    const compat::bitmap_properties bitmap_properties{
        {87U, compat::alpha_mode::premultiplied}, 96.0F, 96.0F};
    const std::byte bitmap_pixels[]{
        std::byte{0x00}, std::byte{0x00}, std::byte{0xff}, std::byte{0xff},
        std::byte{0x00}, std::byte{0xff}, std::byte{0x00}, std::byte{0xff},
        std::byte{0xff}, std::byte{0x00}, std::byte{0x00}, std::byte{0xff},
        std::byte{0xff}, std::byte{0xff}, std::byte{0xff}, std::byte{0xff}};
    compat::bitmap* raw_bitmap = nullptr;
    if (target->CreateBitmap(
            {2U, 2U}, bitmap_pixels, 8U, &bitmap_properties, &raw_bitmap) !=
            com::ok ||
        raw_bitmap == nullptr) {
        return 146;
    }
    com::pointer<compat::bitmap> portable_bitmap;
    portable_bitmap.attach(raw_bitmap);
    const compat::size_u bitmap_pixel_size = portable_bitmap->GetPixelSize();
    const compat::size_f bitmap_size = portable_bitmap->GetSize();
    float bitmap_dpi_x = 0.0F;
    float bitmap_dpi_y = 0.0F;
    portable_bitmap->GetDpi(&bitmap_dpi_x, &bitmap_dpi_y);
    if (bitmap_pixel_size.width != 2U || bitmap_pixel_size.height != 2U ||
        !approximately_equal(bitmap_size.width, 2.0F) ||
        !approximately_equal(bitmap_size.height, 2.0F) ||
        !approximately_equal(bitmap_dpi_x, 96.0F) ||
        !approximately_equal(bitmap_dpi_y, 96.0F) ||
        portable_bitmap->GetPixelFormat().format != 87U) {
        return 147;
    }

    auto* raw_wic_source = new fake_wic_bitmap_source(
        compat::wic_pixel_format_32bpp_pbgra);
    com::pointer<compat::wic_bitmap_source> wic_source;
    wic_source.attach(raw_wic_source);
#if defined(_WIN32)
    if (!com::guid_equal(
            compat::wic_bitmap_source_interface_id,
            __uuidof(IWICBitmapSource)) ||
        !com::guid_equal(
            compat::wic_pixel_format_32bpp_pbgra,
            GUID_WICPixelFormat32bppPBGRA) ||
        !com::guid_equal(
            compat::wic_pixel_format_32bpp_prgba,
            GUID_WICPixelFormat32bppPRGBA)) {
        return 246;
    }
    std::uint32_t native_wic_width = 0U;
    std::uint32_t native_wic_height = 0U;
    auto* native_wic_source =
        reinterpret_cast<IWICBitmapSource*>(wic_source.get());
    if (FAILED(native_wic_source->GetSize(
            &native_wic_width, &native_wic_height)) ||
        native_wic_width != 2U || native_wic_height != 2U) {
        return 246;
    }
#endif
    compat::bitmap* raw_wic_bitmap = nullptr;
    if (target->CreateBitmapFromWicBitmap(
            static_cast<com::unknown*>(wic_source.get()),
            nullptr,
            &raw_wic_bitmap) != com::ok ||
        raw_wic_bitmap == nullptr) {
        return 247;
    }
    com::pointer<compat::bitmap> wic_bitmap;
    wic_bitmap.attach(raw_wic_bitmap);
    float wic_dpi_x = 0.0F;
    float wic_dpi_y = 0.0F;
    wic_bitmap->GetDpi(&wic_dpi_x, &wic_dpi_y);
    if (wic_bitmap->GetPixelFormat().format != 87U ||
        wic_bitmap->GetPixelFormat().alpha !=
            compat::alpha_mode::premultiplied ||
        !approximately_equal(wic_dpi_x, 96.0F) ||
        !approximately_equal(wic_dpi_y, 96.0F) ||
        raw_wic_source->resolution_call_count != 0U ||
        raw_wic_source->copy_call_count != 1U ||
        raw_wic_source->last_stride != 8U ||
        raw_wic_source->last_buffer_size != 16U) {
        return 248;
    }

    auto* raw_rgba_wic_source = new fake_wic_bitmap_source(
        compat::wic_pixel_format_32bpp_prgba);
    com::pointer<compat::wic_bitmap_source> rgba_wic_source;
    rgba_wic_source.attach(raw_rgba_wic_source);
    const compat::bitmap_properties rgba_wic_properties{
        {28U, compat::alpha_mode::premultiplied}, 144.0F, 120.0F};
    compat::bitmap* raw_rgba_wic_bitmap = nullptr;
    if (target->CreateBitmapFromWicBitmap(
            static_cast<com::unknown*>(rgba_wic_source.get()),
            &rgba_wic_properties,
            &raw_rgba_wic_bitmap) != com::ok ||
        raw_rgba_wic_bitmap == nullptr) {
        return 249;
    }
    com::pointer<compat::bitmap> rgba_wic_bitmap;
    rgba_wic_bitmap.attach(raw_rgba_wic_bitmap);
    rgba_wic_bitmap->GetDpi(&wic_dpi_x, &wic_dpi_y);
    compat::bitmap* rejected_wic_bitmap =
        reinterpret_cast<compat::bitmap*>(static_cast<std::uintptr_t>(1U));
    const compat::bitmap_properties mismatched_wic_properties{
        {87U, compat::alpha_mode::premultiplied}, 96.0F, 96.0F};
    if (rgba_wic_bitmap->GetPixelFormat().format != 28U ||
        !approximately_equal(wic_dpi_x, 144.0F) ||
        !approximately_equal(wic_dpi_y, 120.0F) ||
        raw_rgba_wic_source->copy_call_count != 1U ||
        target->CreateBitmapFromWicBitmap(
            static_cast<com::unknown*>(rgba_wic_source.get()),
            &mismatched_wic_properties,
            &rejected_wic_bitmap) != compat::not_implemented ||
        rejected_wic_bitmap != nullptr ||
        raw_rgba_wic_source->copy_call_count != 1U ||
        target->CreateBitmapFromWicBitmap(
            nullptr, nullptr, &rejected_wic_bitmap) !=
            com::invalid_argument ||
        rejected_wic_bitmap != nullptr ||
        target->CreateBitmapFromWicBitmap(
            static_cast<com::unknown*>(wic_source.get()),
            nullptr,
            nullptr) != com::pointer_error) {
        return 250;
    }

    target->BeginDraw();
    target->DrawBitmap(
        wic_bitmap.get(),
        nullptr,
        1.0F,
        compat::bitmap_interpolation_mode::nearest_neighbor,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 251;
    }
    const std::uint64_t wic_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> wic_scene(
        static_cast<std::size_t>(wic_scene_size));
    std::uint64_t wic_scene_written = 0U;
    if (scene_target->BuildScene(
            wic_scene.data(),
            wic_scene.size(),
            &wic_scene_written) != com::ok ||
        wic_scene_written != wic_scene_size) {
        return 251;
    }
    const auto* wic_header = reinterpret_cast<
        const progpu_native_scene_header*>(wic_scene.data());
    const progpu_native_scene_resource* wic_image_resource = nullptr;
    for (std::uint32_t index = 0U;
         index < wic_header->resource_count;
         ++index) {
        const auto* candidate_resource = reinterpret_cast<
            const progpu_native_scene_resource*>(
            wic_scene.data() + wic_header->resource_offset +
            static_cast<std::size_t>(index) *
                wic_header->resource_stride);
        if (candidate_resource->kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
            wic_image_resource = candidate_resource;
            break;
        }
    }
    if (wic_header->command_count != 1U ||
        wic_image_resource == nullptr ||
        wic_image_resource->payload_size != raw_wic_source->pixels.size() ||
        std::memcmp(
            wic_scene.data() + wic_image_resource->payload_offset,
            raw_wic_source->pixels.data(),
            raw_wic_source->pixels.size()) != 0) {
        return 251;
    }

    const compat::bitmap_properties shared_bitmap_properties{
        {0U, compat::alpha_mode::unknown}, 144.0F, 120.0F};
    compat::bitmap* raw_shared_bitmap = nullptr;
    if (target->CreateSharedBitmap(
            compat::bitmap_interface_id,
            portable_bitmap.get(),
            &shared_bitmap_properties,
            &raw_shared_bitmap) != com::ok ||
        raw_shared_bitmap == nullptr ||
        raw_shared_bitmap == portable_bitmap.get()) {
        return 252;
    }
    com::pointer<compat::bitmap> shared_bitmap;
    shared_bitmap.attach(raw_shared_bitmap);
    float shared_dpi_x = 0.0F;
    float shared_dpi_y = 0.0F;
    shared_bitmap->GetDpi(&shared_dpi_x, &shared_dpi_y);
    compat::bitmap* rejected_shared_bitmap =
        reinterpret_cast<compat::bitmap*>(static_cast<std::uintptr_t>(1U));
    const compat::bitmap_properties rejected_shared_properties{
        {28U, compat::alpha_mode::premultiplied}, 96.0F, 96.0F};
    if (shared_bitmap->GetPixelSize().width != 2U ||
        shared_bitmap->GetPixelFormat().format != 87U ||
        shared_bitmap->GetPixelFormat().alpha !=
            compat::alpha_mode::premultiplied ||
        !approximately_equal(shared_dpi_x, 144.0F) ||
        !approximately_equal(shared_dpi_y, 120.0F) ||
        target->CreateSharedBitmap(
            compat::bitmap_interface_id,
            portable_bitmap.get(),
            &rejected_shared_properties,
            &rejected_shared_bitmap) != compat::not_implemented ||
        rejected_shared_bitmap != nullptr ||
        target->CreateSharedBitmap(
            compat::wic_bitmap_source_interface_id,
            portable_bitmap.get(),
            nullptr,
            &rejected_shared_bitmap) != com::no_interface ||
        rejected_shared_bitmap != nullptr ||
        target->CreateSharedBitmap(
            compat::bitmap_interface_id,
            nullptr,
            nullptr,
            &rejected_shared_bitmap) != com::invalid_argument ||
        rejected_shared_bitmap != nullptr) {
        return 253;
    }

    const std::byte replacement[]{
        std::byte{0x30}, std::byte{0x20}, std::byte{0x10}, std::byte{0xff}};
    const compat::rectangle_u replacement_rectangle{1U, 0U, 2U, 1U};
    if (shared_bitmap->CopyFromMemory(
            &replacement_rectangle, replacement, 4U) != com::ok ||
        shared_bitmap->CopyFromRenderTarget(
            nullptr, target.get(), nullptr) != compat::not_implemented) {
        return 148;
    }
    const compat::rectangle_f source_view_destination{
        2.0F, 3.0F, 18.0F, 19.0F};
    const compat::rectangle_f shared_view_destination{
        20.0F, 3.0F, 36.0F, 19.0F};
    target->BeginDraw();
    target->DrawBitmap(
        portable_bitmap.get(),
        &source_view_destination,
        1.0F,
        compat::bitmap_interpolation_mode::nearest_neighbor,
        nullptr);
    target->DrawBitmap(
        shared_bitmap.get(),
        &shared_view_destination,
        1.0F,
        compat::bitmap_interpolation_mode::nearest_neighbor,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 255;
    }
    const std::uint64_t shared_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> shared_scene(
        static_cast<std::size_t>(shared_scene_size));
    std::uint64_t shared_scene_written = 0U;
    if (scene_target->BuildScene(
            shared_scene.data(),
            shared_scene.size(),
            &shared_scene_written) != com::ok ||
        shared_scene_written != shared_scene_size) {
        return 255;
    }
    const auto* shared_header = reinterpret_cast<
        const progpu_native_scene_header*>(shared_scene.data());
    std::uint32_t shared_image_count = 0U;
    const progpu_native_scene_resource* shared_image_resource = nullptr;
    for (std::uint32_t index = 0U;
         index < shared_header->resource_count;
         ++index) {
        const auto* candidate_resource = reinterpret_cast<
            const progpu_native_scene_resource*>(
            shared_scene.data() + shared_header->resource_offset +
            static_cast<std::size_t>(index) *
                shared_header->resource_stride);
        if (candidate_resource->kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
            shared_image_resource = candidate_resource;
            ++shared_image_count;
        }
    }
    compat::scene_render_target_summary shared_scene_summary{};
    scene_target->GetSummary(&shared_scene_summary);
    if (shared_scene_summary.draw_count != 2U ||
        shared_header->command_count != 1U || shared_image_count != 1U ||
        shared_image_resource == nullptr ||
        shared_scene[shared_image_resource->payload_offset + 4U] !=
            replacement[0]) {
        std::fprintf(
            stderr,
            "shared bitmap scene commands=%u images=%u resources=%u payload=%u\n",
            shared_header->command_count,
            shared_image_count,
            shared_header->resource_count,
            shared_image_resource == nullptr
                ? 0U
                : static_cast<unsigned int>(
                    std::to_integer<std::uint8_t>(
                        shared_scene[
                            shared_image_resource->payload_offset + 4U])));
        return 255;
    }
    compat::bitmap* raw_bitmap_copy = nullptr;
    if (target->CreateBitmap(
            {2U, 2U}, nullptr, 0U, &bitmap_properties, &raw_bitmap_copy) !=
            com::ok ||
        raw_bitmap_copy == nullptr) {
        return 154;
    }
    com::pointer<compat::bitmap> bitmap_copy;
    bitmap_copy.attach(raw_bitmap_copy);
    if (bitmap_copy->CopyFromBitmap(
            nullptr, shared_bitmap.get(), nullptr) != com::ok) {
        return 155;
    }
    target->BeginDraw();
    const compat::rectangle_f first_bitmap_destination{
        2.0F, 3.0F, 18.0F, 19.0F};
    target->DrawBitmap(
        bitmap_copy.get(),
        &first_bitmap_destination,
        0.75F,
        compat::bitmap_interpolation_mode::nearest_neighbor,
        nullptr);
    const compat::rectangle_f second_bitmap_destination{
        20.0F, 3.0F, 36.0F, 19.0F};
    target->DrawBitmap(
        bitmap_copy.get(),
        &second_bitmap_destination,
        1.0F,
        compat::bitmap_interpolation_mode::linear,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 149;
    }
    const std::uint64_t bitmap_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> bitmap_scene(
        static_cast<std::size_t>(bitmap_scene_size));
    std::uint64_t bitmap_scene_written = 0U;
    if (scene_target->BuildScene(
            bitmap_scene.data(),
            bitmap_scene.size(),
            &bitmap_scene_written) != com::ok ||
        bitmap_scene_written != bitmap_scene_size) {
        return 150;
    }
    const auto* bitmap_header = reinterpret_cast<
        const progpu_native_scene_header*>(bitmap_scene.data());
    std::uint32_t bitmap_resource_count = 0U;
    const progpu_native_scene_resource* image_resource = nullptr;
    for (std::uint32_t index = 0U;
         index < bitmap_header->resource_count;
         ++index) {
        const auto* candidate_resource = reinterpret_cast<
            const progpu_native_scene_resource*>(
            bitmap_scene.data() + bitmap_header->resource_offset +
            index * bitmap_header->resource_stride);
        if (candidate_resource->kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
            image_resource = candidate_resource;
            ++bitmap_resource_count;
        }
    }
    if (bitmap_header->command_count != 2U || bitmap_resource_count != 1U ||
        image_resource == nullptr ||
        (image_resource->flags & PROGPU_NATIVE_SCENE_IMAGE_BGRA8) == 0U ||
        image_resource->payload_size != sizeof(bitmap_pixels)) {
        return 151;
    }
    const auto* serialized_pixels =
        bitmap_scene.data() + image_resource->payload_offset;
    if (serialized_pixels[4] != replacement[0] ||
        serialized_pixels[5] != replacement[1] ||
        serialized_pixels[6] != replacement[2] ||
        serialized_pixels[7] != replacement[3]) {
        return 152;
    }

    const compat::rectangle_f opacity_mask_destination{
        40.0F, 3.0F, 56.0F, 19.0F};
    target->BeginDraw();
    target->FillOpacityMask(
        portable_bitmap.get(),
        static_cast<compat::brush*>(target_brush.get()),
        compat::opacity_mask_content::graphics,
        &opacity_mask_destination,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != compat::wrong_state ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 219;
    }
    target->BeginDraw();
    target->SetAntialiasMode(compat::antialias_mode::aliased);
    target->FillOpacityMask(
        portable_bitmap.get(),
        static_cast<compat::brush*>(target_brush.get()),
        compat::opacity_mask_content::text_natural,
        &opacity_mask_destination,
        nullptr);
    const com::result opacity_mask_end_status =
        target->EndDraw(nullptr, nullptr);
    if (opacity_mask_end_status != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        std::fprintf(
            stderr,
            "portable opacity mask EndDraw status: %d, scene size: %llu\n",
            static_cast<int>(opacity_mask_end_status),
            static_cast<unsigned long long>(
                scene_target->GetRequiredSceneSize()));
        return 220;
    }
    const std::uint64_t opacity_mask_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> opacity_mask_scene(
        static_cast<std::size_t>(opacity_mask_scene_size));
    std::uint64_t opacity_mask_scene_written = 0U;
    if (scene_target->BuildScene(
            opacity_mask_scene.data(),
            opacity_mask_scene.size(),
            &opacity_mask_scene_written) != com::ok ||
        opacity_mask_scene_written != opacity_mask_scene_size) {
        return 221;
    }
    const auto* opacity_mask_header = reinterpret_cast<
        const progpu_native_scene_header*>(opacity_mask_scene.data());
    const progpu_native_scene_resource* opacity_mask_resource = nullptr;
    for (std::uint32_t index = 0U;
         index < opacity_mask_header->resource_count;
         ++index) {
        const auto* candidate_resource = reinterpret_cast<
            const progpu_native_scene_resource*>(
            opacity_mask_scene.data() + opacity_mask_header->resource_offset +
            static_cast<std::size_t>(index) *
                opacity_mask_header->resource_stride);
        if (candidate_resource->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            opacity_mask_resource = candidate_resource;
            break;
        }
    }
    const auto* opacity_mask_picture = opacity_mask_resource == nullptr
        ? nullptr
        : reinterpret_cast<const progpu_native_scene_layer_picture_mask*>(
            opacity_mask_scene.data() +
            opacity_mask_resource->payload_offset);
    if (opacity_mask_header->command_count != 1U ||
        opacity_mask_resource == nullptr ||
        opacity_mask_picture == nullptr ||
        opacity_mask_picture->kind !=
            PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE ||
        opacity_mask_picture->stream_size <
            sizeof(progpu_native_scene_header)) {
        return 222;
    }

    const compat::size_f compatible_size{16.0F, 12.0F};
    const compat::size_u compatible_pixel_size{16U, 12U};
    const compat::pixel_format compatible_format{
        65U, compat::alpha_mode::premultiplied};
    compat::bitmap_render_target* raw_compatible_target = nullptr;
    const compat::size_u nonuniform_compatible_pixels{16U, 24U};
    if (target->CreateCompatibleRenderTarget(
            &compatible_size,
            &nonuniform_compatible_pixels,
            &compatible_format,
            compat::compatible_render_target_options::none,
            &raw_compatible_target) != compat::not_implemented ||
        raw_compatible_target != nullptr) {
        return 236;
    }
    if (target->CreateCompatibleRenderTarget(
            &compatible_size,
            &compatible_pixel_size,
            &compatible_format,
            compat::compatible_render_target_options::none,
            &raw_compatible_target) != com::ok ||
        raw_compatible_target == nullptr) {
        return 223;
    }
    com::pointer<compat::bitmap_render_target> compatible_target;
    compatible_target.attach(raw_compatible_target);
    com::pointer<compat::bitmap_render_target> queried_compatible_target;
    if (compatible_target.as(
            compat::bitmap_render_target_interface_id,
            queried_compatible_target) != com::ok ||
        !queried_compatible_target ||
        compatible_target->GetPixelFormat().format != 65U ||
        compatible_target->GetPixelSize().width != 16U ||
        compatible_target->GetPixelSize().height != 12U ||
        !approximately_equal(compatible_target->GetSize().width, 16.0F) ||
        !approximately_equal(compatible_target->GetSize().height, 12.0F)) {
        return 224;
    }
    compat::solid_color_brush* raw_compatible_brush = nullptr;
    const compat::color_f opaque_mask_color{1.0F, 1.0F, 1.0F, 1.0F};
    if (compatible_target->CreateSolidColorBrush(
            &opaque_mask_color, nullptr, &raw_compatible_brush) != com::ok ||
        raw_compatible_brush == nullptr) {
        return 225;
    }
    com::pointer<compat::solid_color_brush> compatible_brush;
    compatible_brush.attach(raw_compatible_brush);
    compatible_target->BeginDraw();
    const compat::color_f transparent_mask_color{};
    compatible_target->Clear(&transparent_mask_color);
    const compat::rectangle_f compatible_fill{2.0F, 1.0F, 10.0F, 9.0F};
    compatible_target->FillRectangle(
        &compatible_fill,
        static_cast<compat::brush*>(compatible_brush.get()));
    if (compatible_target->EndDraw(nullptr, nullptr) != com::ok) {
        return 226;
    }
    compat::bitmap* raw_compatible_bitmap = nullptr;
    if (compatible_target->GetBitmap(&raw_compatible_bitmap) != com::ok ||
        raw_compatible_bitmap == nullptr) {
        return 227;
    }
    com::pointer<compat::bitmap> compatible_bitmap;
    compatible_bitmap.attach(raw_compatible_bitmap);
    com::pointer<compat::scene_render_target_native>
        compatible_bitmap_scene;
    if (compatible_bitmap.as(
            compat::scene_render_target_native_interface_id,
            compatible_bitmap_scene) != com::ok ||
        !compatible_bitmap_scene ||
        compatible_bitmap_scene->GetRequiredSceneSize() == 0U ||
        compatible_bitmap->GetPixelFormat().format != 65U) {
        return 228;
    }
    compat::bitmap* raw_shared_compatible_bitmap = nullptr;
    if (target->CreateSharedBitmap(
            compat::bitmap_interface_id,
            compatible_bitmap.get(),
            nullptr,
            &raw_shared_compatible_bitmap) != com::ok ||
        raw_shared_compatible_bitmap == nullptr ||
        raw_shared_compatible_bitmap == compatible_bitmap.get()) {
        return 228;
    }
    com::pointer<compat::bitmap> shared_compatible_bitmap;
    shared_compatible_bitmap.attach(raw_shared_compatible_bitmap);
    com::pointer<compat::scene_render_target_native>
        shared_compatible_scene;
    if (shared_compatible_bitmap.as(
            compat::scene_render_target_native_interface_id,
            shared_compatible_scene) != com::ok ||
        !shared_compatible_scene ||
        shared_compatible_scene->GetRequiredSceneSize() !=
            compatible_bitmap_scene->GetRequiredSceneSize() ||
        shared_compatible_bitmap->GetPixelFormat().format != 65U) {
        return 228;
    }
    const compat::rectangle_f compatible_source{2.0F, 1.0F, 10.0F, 9.0F};
    const compat::rectangle_f compatible_destination{
        60.0F, 4.0F, 84.0F, 20.0F};
    target->BeginDraw();
    target->SetAntialiasMode(compat::antialias_mode::aliased);
    target->FillOpacityMask(
        shared_compatible_bitmap.get(),
        static_cast<compat::brush*>(target_brush.get()),
        compat::opacity_mask_content::graphics,
        &compatible_destination,
        &compatible_source);
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 229;
    }
    const std::uint64_t compatible_mask_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> compatible_mask_scene(
        static_cast<std::size_t>(compatible_mask_scene_size));
    std::uint64_t compatible_mask_scene_written = 0U;
    if (scene_target->BuildScene(
            compatible_mask_scene.data(),
            compatible_mask_scene.size(),
            &compatible_mask_scene_written) != com::ok ||
        compatible_mask_scene_written != compatible_mask_scene_size) {
        return 230;
    }
    const auto* compatible_mask_header = reinterpret_cast<
        const progpu_native_scene_header*>(compatible_mask_scene.data());
    const progpu_native_scene_layer_picture_mask*
        compatible_mask_picture = nullptr;
    for (std::uint32_t index = 0U;
         index < compatible_mask_header->resource_count;
         ++index) {
        const auto* candidate_resource = reinterpret_cast<
            const progpu_native_scene_resource*>(
            compatible_mask_scene.data() +
            compatible_mask_header->resource_offset +
            static_cast<std::size_t>(index) *
                compatible_mask_header->resource_stride);
        if (candidate_resource->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            compatible_mask_picture = reinterpret_cast<
                const progpu_native_scene_layer_picture_mask*>(
                compatible_mask_scene.data() +
                candidate_resource->payload_offset);
            break;
        }
    }
    if (compatible_mask_header->command_count != 1U ||
        compatible_mask_picture == nullptr ||
        compatible_mask_picture->flags !=
            PROGPU_NATIVE_SCENE_PICTURE_MASK_SOURCE_EXTENT ||
        compatible_mask_picture->reserved0 != 16U ||
        compatible_mask_picture->reserved1 != 12U ||
        !approximately_equal(compatible_mask_picture->bounds.width, 16.0F) ||
        !approximately_equal(compatible_mask_picture->bounds.height, 12.0F) ||
        !approximately_equal(compatible_mask_picture->transform.m11, 3.0F) ||
        !approximately_equal(compatible_mask_picture->transform.m22, 2.0F) ||
        !approximately_equal(compatible_mask_picture->transform.m31, 54.0F) ||
        !approximately_equal(compatible_mask_picture->transform.m32, 2.0F)) {
        return 231;
    }

    const compat::bitmap_brush_properties bitmap_brush_properties{
        compat::extend_mode::wrap,
        compat::extend_mode::mirror,
        compat::bitmap_interpolation_mode::nearest_neighbor};
    const compat::brush_properties bitmap_brush_base_properties{
        0.625F,
        {1.0F, 0.0F, 0.0F, 1.0F, 1.0F, 2.0F}};
    compat::bitmap_brush* raw_bitmap_brush = nullptr;
    if (target->CreateBitmapBrush(
            portable_bitmap.get(),
            &bitmap_brush_properties,
            &bitmap_brush_base_properties,
            &raw_bitmap_brush) != com::ok ||
        raw_bitmap_brush == nullptr) {
        return 156;
    }
    com::pointer<compat::bitmap_brush> bitmap_brush;
    bitmap_brush.attach(raw_bitmap_brush);
    com::pointer<compat::brush> bitmap_brush_base;
    compat::bitmap* returned_bitmap = nullptr;
    compat::matrix_3x2_f returned_bitmap_brush_transform{};
    bitmap_brush->GetBitmap(&returned_bitmap);
    bitmap_brush->GetTransform(&returned_bitmap_brush_transform);
    const bool bitmap_brush_identity_matches = returned_bitmap ==
        portable_bitmap.get();
    if (returned_bitmap != nullptr) {
        returned_bitmap->Release();
    }
    if (bitmap_brush.as(
            compat::brush_interface_id, bitmap_brush_base) != com::ok ||
        !bitmap_brush_base || !bitmap_brush_identity_matches ||
        bitmap_brush->GetExtendModeX() != compat::extend_mode::wrap ||
        bitmap_brush->GetExtendModeY() != compat::extend_mode::mirror ||
        bitmap_brush->GetInterpolationMode() !=
            compat::bitmap_interpolation_mode::nearest_neighbor ||
        !approximately_equal(bitmap_brush->GetOpacity(), 0.625F) ||
        !approximately_equal(returned_bitmap_brush_transform.m31, 1.0F) ||
        !approximately_equal(returned_bitmap_brush_transform.m32, 2.0F)) {
        return 157;
    }
    bitmap_brush->SetExtendModeX(compat::extend_mode::clamp);
    bitmap_brush->SetExtendModeY(compat::extend_mode::wrap);
    bitmap_brush->SetInterpolationMode(
        compat::bitmap_interpolation_mode::linear);
    bitmap_brush->SetOpacity(0.75F);
    bitmap_brush->SetExtendModeX(static_cast<compat::extend_mode>(99U));
    bitmap_brush->SetInterpolationMode(
        static_cast<compat::bitmap_interpolation_mode>(99U));
    if (bitmap_brush->GetExtendModeX() != compat::extend_mode::clamp ||
        bitmap_brush->GetExtendModeY() != compat::extend_mode::wrap ||
        bitmap_brush->GetInterpolationMode() !=
            compat::bitmap_interpolation_mode::linear ||
        !approximately_equal(bitmap_brush->GetOpacity(), 0.75F)) {
        return 158;
    }
    compat::bitmap* raw_foreign_bitmap = nullptr;
    if (other_target->CreateBitmap(
            {2U, 2U}, bitmap_pixels, 8U, &bitmap_properties,
            &raw_foreign_bitmap) != com::ok ||
        raw_foreign_bitmap == nullptr) {
        return 159;
    }
    com::pointer<compat::bitmap> foreign_bitmap;
    foreign_bitmap.attach(raw_foreign_bitmap);
    rejected_shared_bitmap = reinterpret_cast<compat::bitmap*>(
        static_cast<std::uintptr_t>(1U));
    if (target->CreateSharedBitmap(
            compat::bitmap_interface_id,
            foreign_bitmap.get(),
            nullptr,
            &rejected_shared_bitmap) != compat::wrong_factory ||
        rejected_shared_bitmap != nullptr) {
        return 253;
    }
    raw_bitmap_brush = reinterpret_cast<compat::bitmap_brush*>(
        static_cast<std::uintptr_t>(1U));
    if (target->CreateBitmapBrush(
            foreign_bitmap.get(), nullptr, nullptr, &raw_bitmap_brush) !=
            compat::wrong_factory ||
        raw_bitmap_brush != nullptr) {
        return 160;
    }
    bitmap_brush->SetBitmap(foreign_bitmap.get());
    returned_bitmap = nullptr;
    bitmap_brush->GetBitmap(&returned_bitmap);
    const bool rejected_foreign_bitmap = returned_bitmap ==
        portable_bitmap.get();
    if (returned_bitmap != nullptr) {
        returned_bitmap->Release();
    }
    if (!rejected_foreign_bitmap) {
        return 161;
    }

    target->BeginDraw();
    const compat::rectangle_f bitmap_brush_rectangle{
        4.0F, 5.0F, 20.0F, 17.0F};
    target->FillRectangle(
        &bitmap_brush_rectangle,
        static_cast<compat::brush*>(bitmap_brush.get()));
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 162;
    }
    const std::uint64_t bitmap_brush_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> bitmap_brush_scene(
        static_cast<std::size_t>(bitmap_brush_scene_size));
    std::uint64_t bitmap_brush_scene_written = 0U;
    if (scene_target->BuildScene(
            bitmap_brush_scene.data(),
            bitmap_brush_scene.size(),
            &bitmap_brush_scene_written) != com::ok ||
        bitmap_brush_scene_written != bitmap_brush_scene_size) {
        return 163;
    }
    const auto* bitmap_brush_header = reinterpret_cast<
        const progpu_native_scene_header*>(bitmap_brush_scene.data());
    const progpu_native_scene_resource* bitmap_brush_image = nullptr;
    const progpu_native_scene_resource* bitmap_brush_mask = nullptr;
    const progpu_native_scene_resource* bitmap_brush_state = nullptr;
    for (std::uint32_t index = 0U;
         index < bitmap_brush_header->resource_count;
         ++index) {
        const auto* brush_scene_resource = reinterpret_cast<
            const progpu_native_scene_resource*>(
            bitmap_brush_scene.data() +
            bitmap_brush_header->resource_offset +
            index * bitmap_brush_header->resource_stride);
        if (brush_scene_resource->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
            bitmap_brush_image = brush_scene_resource;
        } else if (brush_scene_resource->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            bitmap_brush_mask = brush_scene_resource;
        } else if (brush_scene_resource->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_STATE) {
            bitmap_brush_state = brush_scene_resource;
        }
    }
    const auto* bitmap_brush_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        bitmap_brush_scene.data() + bitmap_brush_header->command_offset);
    const auto* bitmap_brush_draw = reinterpret_cast<
        const progpu_native_scene_image_draw*>(
        bitmap_brush_scene.data() + bitmap_brush_command->payload_offset);
    const auto* bitmap_brush_mask_value = bitmap_brush_mask == nullptr
        ? nullptr
        : reinterpret_cast<const progpu_native_scene_layer_geometry_mask*>(
            bitmap_brush_scene.data() + bitmap_brush_mask->payload_offset);
    if (bitmap_brush_header->command_count != 1U ||
        bitmap_brush_image == nullptr || bitmap_brush_mask == nullptr ||
        bitmap_brush_state == nullptr || bitmap_brush_command->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE ||
        bitmap_brush_mask_value == nullptr ||
        bitmap_brush_mask_value->kind !=
            PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY ||
        bitmap_brush_mask_value->primitive_count != 1U ||
        bitmap_brush_draw->sampling !=
            PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR ||
        (bitmap_brush_draw->flags &
            PROGPU_NATIVE_SCENE_IMAGE_EXTENDED_SOURCE_RECT) == 0U ||
        ((bitmap_brush_draw->flags &
                PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_MASK) >>
            PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_SHIFT) !=
            PROGPU_NATIVE_IMAGE_ADDRESS_CLAMP ||
        ((bitmap_brush_draw->flags &
                PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_V_MASK) >>
            PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_V_SHIFT) !=
            PROGPU_NATIVE_IMAGE_ADDRESS_REPEAT ||
        !approximately_equal(bitmap_brush_draw->opacity, 0.75F) ||
        !approximately_equal(bitmap_brush_draw->transform.m31, 1.0F) ||
        !approximately_equal(bitmap_brush_draw->transform.m32, 2.0F)) {
        return 164;
    }

    target->BeginDraw();
    target->FillGeometry(
        path_base.get(),
        static_cast<compat::brush*>(target_brush.get()),
        nullptr);
    target->FillGeometry(
        path_base.get(),
        static_cast<compat::brush*>(bitmap_brush.get()),
        nullptr);
    target->DrawGeometry(
        path_base.get(),
        static_cast<compat::brush*>(target_brush.get()),
        2.0F,
        nullptr);
    target->DrawGeometry(
        path_base.get(),
        static_cast<compat::brush*>(bitmap_brush.get()),
        1.5F,
        stroke_style.get());
    target->DrawGeometry(
        static_cast<compat::geometry*>(geometry.get()),
        static_cast<compat::brush*>(target_brush.get()),
        1.0F,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 167;
    }
    const std::uint64_t geometry_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> geometry_scene(
        static_cast<std::size_t>(geometry_scene_size));
    std::uint64_t geometry_scene_written = 0U;
    if (scene_target->BuildScene(
            geometry_scene.data(),
            geometry_scene.size(),
            &geometry_scene_written) != com::ok ||
        geometry_scene_written != geometry_scene_size) {
        return 168;
    }
    const auto* geometry_header = reinterpret_cast<
        const progpu_native_scene_header*>(geometry_scene.data());
    const progpu_native_scene_resource* path_resource_record = nullptr;
    const progpu_native_scene_resource* vector_mask_record = nullptr;
    const progpu_native_scene_resource* geometry_resource_record = nullptr;
    const progpu_native_scene_resource* geometry_mask_record = nullptr;
    const progpu_native_scene_resource* stroke_resource_record = nullptr;
    for (std::uint32_t index = 0U;
         index < geometry_header->resource_count;
         ++index) {
        const auto* candidate = reinterpret_cast<
            const progpu_native_scene_resource*>(
            geometry_scene.data() + geometry_header->resource_offset +
            index * geometry_header->resource_stride);
        if (candidate->kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            path_resource_record = candidate;
        } else if (candidate->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
            geometry_resource_record = candidate;
        } else if (candidate->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
            stroke_resource_record = candidate;
        } else if (candidate->kind ==
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
            const auto* mask = reinterpret_cast<
                const progpu_native_scene_layer_vector_mask*>(
                geometry_scene.data() + candidate->payload_offset);
            if (mask->kind ==
                PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN) {
                vector_mask_record = candidate;
            } else if (mask->kind ==
                PROGPU_NATIVE_SCENE_LAYER_MASK_GEOMETRY) {
                geometry_mask_record = candidate;
            }
        }
    }
    const auto* first_geometry_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        geometry_scene.data() + geometry_header->command_offset);
    const auto* second_geometry_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        reinterpret_cast<const std::byte*>(first_geometry_command) +
        geometry_header->command_stride);
    const auto* third_geometry_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        reinterpret_cast<const std::byte*>(second_geometry_command) +
        geometry_header->command_stride);
    const auto* fourth_geometry_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        reinterpret_cast<const std::byte*>(third_geometry_command) +
        geometry_header->command_stride);
    const auto* fifth_geometry_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        reinterpret_cast<const std::byte*>(fourth_geometry_command) +
        geometry_header->command_stride);
    const auto* path_fill = path_resource_record == nullptr
        ? nullptr
        : reinterpret_cast<const progpu_native_scene_path_fill*>(
            geometry_scene.data() + path_resource_record->payload_offset);
    if (geometry_header->command_count != 5U ||
        path_resource_record == nullptr || vector_mask_record == nullptr ||
        geometry_resource_record == nullptr ||
        geometry_mask_record == nullptr || stroke_resource_record == nullptr ||
        first_geometry_command->kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH ||
        second_geometry_command->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE ||
        third_geometry_command->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY ||
        fourth_geometry_command->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE ||
        fifth_geometry_command->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_STROKE_BATCH ||
        path_fill == nullptr || path_fill->segment_count != 4U ||
        path_fill->fill_rule != PROGPU_NATIVE_FILL_RULE_NON_ZERO) {
        return 169;
    }

    const compat::rounded_rectangle unequal_rounded_rectangle{
        rounded_rectangle_value.rectangle, 2.0F, 3.0F};
    target->BeginDraw();
    target->FillRoundedRectangle(
        &unequal_rounded_rectangle,
        static_cast<compat::brush*>(target_brush.get()));
    target->DrawRoundedRectangle(
        &unequal_rounded_rectangle,
        static_cast<compat::brush*>(target_brush.get()),
        1.0F,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 244;
    }
    compat::scene_render_target_summary unequal_rounded_summary{};
    scene_target->GetSummary(&unequal_rounded_summary);
    if (unequal_rounded_summary.draw_count != 2U ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 245;
    }
    target->BeginDraw();
    target->DrawLine(
        {1.0F, 2.0F},
        {18.0F, 12.0F},
        static_cast<compat::brush*>(target_brush.get()),
        1.25F,
        stroke_style.get());
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 237;
    }
    target->BeginDraw();
    target->DrawRectangle(
        &rectangle,
        static_cast<compat::brush*>(target_brush.get()),
        1.5F,
        stroke_style.get());
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 238;
    }
    target->BeginDraw();
    target->DrawRoundedRectangle(
        &unequal_rounded_rectangle,
        static_cast<compat::brush*>(target_brush.get()),
        1.75F,
        stroke_style.get());
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 239;
    }
    target->BeginDraw();
    target->DrawEllipse(
        &ellipse_value,
        static_cast<compat::brush*>(target_brush.get()),
        2.0F,
        stroke_style.get());
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 240;
    }
    compat::scene_render_target_summary styled_primitive_summary{};
    scene_target->GetSummary(&styled_primitive_summary);
    const std::uint64_t styled_primitive_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> styled_primitive_scene(
        static_cast<std::size_t>(styled_primitive_scene_size));
    std::uint64_t styled_primitive_scene_written = 0U;
    if (styled_primitive_summary.draw_count != 1U ||
        styled_primitive_scene_size == 0U ||
        scene_target->BuildScene(
            styled_primitive_scene.data(),
            styled_primitive_scene.size(),
            &styled_primitive_scene_written) != com::ok ||
        styled_primitive_scene_written != styled_primitive_scene_size) {
        return 241;
    }
    const auto* styled_primitive_header = reinterpret_cast<
        const progpu_native_scene_header*>(styled_primitive_scene.data());
    if (styled_primitive_header->command_count != 1U) {
        return 242;
    }
    const auto* styled_primitive_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        styled_primitive_scene.data() +
        styled_primitive_header->command_offset);
    if (styled_primitive_command->kind !=
        PROGPU_NATIVE_SCENE_COMMAND_DRAW_GEOMETRY) {
        return 243;
    }

    auto* raw_font_face = new fake_font_face();
    com::pointer<fake_font_face> font_face;
    font_face.attach(raw_font_face);
    const std::uint16_t glyph_indices[]{41U, 42U};
    const float glyph_advances[]{8.0F, 9.0F};
    const compat::glyph_offset glyph_offsets[]{
        {0.0F, 0.0F}, {0.5F, 1.0F}};
    const compat::glyph_run glyph_run{
        font_face.get(),
        12.0F,
        2U,
        glyph_indices,
        glyph_advances,
        glyph_offsets,
        0,
        0U};
    target->SetTextAntialiasMode(compat::text_antialias_mode::aliased);
    target->BeginDraw();
    if (target->GetTextAntialiasMode() !=
        compat::text_antialias_mode::aliased) {
        return 267;
    }
    target->DrawGlyphRun(
        {20.0F, 50.0F},
        &glyph_run,
        static_cast<compat::brush*>(target_brush.get()),
        compat::measuring_mode::natural);
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        font_face->outline_call_count != 1U ||
        !approximately_equal(font_face->last_em_size, 12.0F) ||
        font_face->last_glyph_count != 2U ||
        font_face->last_is_sideways != 0 ||
        font_face->last_is_right_to_left != 0) {
        return 246;
    }
    compat::scene_render_target_summary glyph_run_summary{};
    scene_target->GetSummary(&glyph_run_summary);
    const std::uint64_t glyph_run_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> glyph_run_scene(
        static_cast<std::size_t>(glyph_run_scene_size));
    std::uint64_t glyph_run_scene_written = 0U;
    if (glyph_run_summary.draw_count != 1U ||
        glyph_run_scene_size == 0U ||
        scene_target->BuildScene(
            glyph_run_scene.data(),
            glyph_run_scene.size(),
            &glyph_run_scene_written) != com::ok ||
        glyph_run_scene_written != glyph_run_scene_size) {
        return 247;
    }
    const auto* glyph_run_header = reinterpret_cast<
        const progpu_native_scene_header*>(glyph_run_scene.data());
    const auto* glyph_run_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        glyph_run_scene.data() + glyph_run_header->command_offset);
    const progpu_native_scene_resource* glyph_path_resource = nullptr;
    for (std::uint32_t index = 0U;
         index < glyph_run_header->resource_count;
         ++index) {
        const auto* candidate = reinterpret_cast<
            const progpu_native_scene_resource*>(
            glyph_run_scene.data() + glyph_run_header->resource_offset +
            index * glyph_run_header->resource_stride);
        if (candidate->kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
            glyph_path_resource = candidate;
            break;
        }
    }
    const auto* glyph_path = glyph_path_resource == nullptr
        ? nullptr
        : reinterpret_cast<const progpu_native_scene_path_fill*>(
            glyph_run_scene.data() + glyph_path_resource->payload_offset);
    if (glyph_run_header->command_count != 1U ||
        glyph_run_command->kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH ||
        glyph_path == nullptr || glyph_path->sample_grid != 1U ||
        !approximately_equal(glyph_run_command->bounds_x, 20.0F) ||
        !approximately_equal(glyph_run_command->bounds_y, 37.0F) ||
        !approximately_equal(glyph_run_command->bounds_width, 13.3F) ||
        !approximately_equal(glyph_run_command->bounds_height, 13.0F)) {
        return 248;
    }
    target->SetTextAntialiasMode(compat::text_antialias_mode::grayscale);

    fake_text_layout text_layout_value{};
    text_layout_value.vtable = &fake_layout_vtable;
    text_layout_value.glyphs = glyph_run;
    const auto text_layout_options = static_cast<compat::draw_text_options>(
        static_cast<std::uint32_t>(compat::draw_text_options::no_snap) |
        static_cast<std::uint32_t>(compat::draw_text_options::clip));
    target->BeginDraw();
    target->DrawTextLayout(
        {10.0F, 20.0F},
        reinterpret_cast<compat::text_layout*>(&text_layout_value),
        static_cast<compat::brush*>(target_brush.get()),
        text_layout_options);
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        text_layout_value.draw_call_count != 1U ||
        text_layout_value.max_width_call_count != 1U ||
        text_layout_value.max_height_call_count != 1U ||
        text_layout_value.pixel_snapping_disabled != 1 ||
        !approximately_equal(text_layout_value.pixels_per_dip, 1.0F) ||
        !approximately_equal(text_layout_value.transform.m11, 1.0F) ||
        font_face->outline_call_count != 2U) {
        return 250;
    }
    compat::scene_render_target_summary text_layout_summary{};
    scene_target->GetSummary(&text_layout_summary);
    const std::uint64_t text_layout_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> text_layout_scene(
        static_cast<std::size_t>(text_layout_scene_size));
    std::uint64_t text_layout_scene_written = 0U;
    if (text_layout_summary.draw_count != 3U ||
        text_layout_scene_size == 0U ||
        scene_target->BuildScene(
            text_layout_scene.data(),
            text_layout_scene.size(),
            &text_layout_scene_written) != com::ok ||
        text_layout_scene_written != text_layout_scene_size) {
        return 251;
    }
    const auto* text_layout_header = reinterpret_cast<
        const progpu_native_scene_header*>(text_layout_scene.data());
    const auto text_layout_command = [
        text_layout_header, &text_layout_scene](std::uint32_t index) {
        return reinterpret_cast<const progpu_native_scene_command*>(
            text_layout_scene.data() + text_layout_header->command_offset +
            static_cast<std::size_t>(index) *
                text_layout_header->command_stride);
    };
    if (text_layout_header->command_count != 5U ||
        text_layout_command(0U)->kind != PROGPU_NATIVE_SCENE_COMMAND_SAVE ||
        text_layout_command(1U)->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH ||
        text_layout_command(2U)->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
        text_layout_command(3U)->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_DRAW_ANALYTIC ||
        text_layout_command(4U)->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_RESTORE) {
        return 252;
    }
    target->BeginDraw();
    target->DrawTextLayout(
        {10.0F, 20.0F},
        reinterpret_cast<compat::text_layout*>(&text_layout_value),
        static_cast<compat::brush*>(target_brush.get()),
        compat::draw_text_options::enable_color_font);
    if (target->EndDraw(nullptr, nullptr) != compat::not_implemented ||
        text_layout_value.draw_call_count != 1U ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 254;
    }

    auto* raw_text_format = new fake_text_format(glyph_run);
    com::pointer<fake_text_format> text_format;
    text_format.attach(raw_text_format);
    const wchar_t text_value[]{L'A', L'B', L'\0'};
    const compat::rectangle_f text_rectangle{5.0F, 6.0F, 85.0F, 36.0F};
    target->BeginDraw();
    target->DrawText(
        text_value,
        2U,
        reinterpret_cast<compat::text_format*>(text_format.get()),
        &text_rectangle,
        static_cast<compat::brush*>(target_brush.get()),
        text_layout_options,
        compat::measuring_mode::gdi_natural);
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        text_format->create_call_count != 1U ||
        text_format->last_text_length != 2U ||
        text_format->first_character != L'A' ||
        !approximately_equal(text_format->last_maximum_width, 80.0F) ||
        !approximately_equal(text_format->last_maximum_height, 30.0F) ||
        text_format->last_measuring !=
            compat::measuring_mode::gdi_natural ||
        font_face->outline_call_count != 3U) {
        return 255;
    }
    compat::scene_render_target_summary text_summary{};
    scene_target->GetSummary(&text_summary);
    const std::uint64_t text_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> text_scene(
        static_cast<std::size_t>(text_scene_size));
    std::uint64_t text_scene_written = 0U;
    if (text_summary.draw_count != 3U || text_scene_size == 0U ||
        scene_target->BuildScene(
            text_scene.data(), text_scene.size(), &text_scene_written) !=
            com::ok ||
        text_scene_written != text_scene_size) {
        return 256;
    }
    const auto* text_header = reinterpret_cast<
        const progpu_native_scene_header*>(text_scene.data());
    if (text_header->command_count != 5U) {
        return 257;
    }
    target->BeginDraw();
    target->DrawTextLayout(
        {10.0F, 20.0F},
        reinterpret_cast<compat::text_layout*>(&text_layout_value),
        static_cast<compat::brush*>(target_brush.get()),
        compat::draw_text_options::disable_color_bitmap_snapping);
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        text_layout_value.draw_call_count != 2U ||
        font_face->outline_call_count != 4U) {
        return 266;
    }

    compat::layer_parameters masked_layer_parameters = layer_parameters;
    masked_layer_parameters.geometric_mask = path_base.get();
    masked_layer_parameters.opacity = 0.75F;
    target->BeginDraw();
    target->PushLayer(&masked_layer_parameters, target_layer.get());
    target->FillRectangle(
        &layer_bounds, static_cast<compat::brush*>(target_brush.get()));
    target->PopLayer();
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 194;
    }
    const std::uint64_t masked_layer_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> masked_layer_scene(
        static_cast<std::size_t>(masked_layer_scene_size));
    std::uint64_t masked_layer_scene_written = 0U;
    if (scene_target->BuildScene(
            masked_layer_scene.data(),
            masked_layer_scene.size(),
            &masked_layer_scene_written) != com::ok ||
        masked_layer_scene_written != masked_layer_scene_size) {
        return 195;
    }
    const auto* masked_layer_header = reinterpret_cast<
        const progpu_native_scene_header*>(masked_layer_scene.data());
    const auto* masked_layer_push = reinterpret_cast<
        const progpu_native_scene_command*>(
            masked_layer_scene.data() +
            masked_layer_header->command_offset);
    if (masked_layer_header->command_count != 3U ||
        masked_layer_push->kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
        masked_layer_push->payload_size <
            sizeof(progpu_native_scene_layer)) {
        return 196;
    }
    const auto* masked_native_layer = reinterpret_cast<
        const progpu_native_scene_layer*>(
            masked_layer_scene.data() + masked_layer_push->payload_offset);
    if (masked_native_layer->mask_resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX ||
        masked_native_layer->mask_resource_index >=
            masked_layer_header->resource_count ||
        !approximately_equal(masked_native_layer->opacity, 0.75F)) {
        return 197;
    }
    const auto* masked_layer_resource = reinterpret_cast<
        const progpu_native_scene_resource*>(
            masked_layer_scene.data() +
            masked_layer_header->resource_offset +
            static_cast<std::size_t>(
                masked_native_layer->mask_resource_index) *
                masked_layer_header->resource_stride);
    const auto* masked_layer_mask = reinterpret_cast<
        const progpu_native_scene_layer_vector_mask*>(
            masked_layer_scene.data() +
            masked_layer_resource->payload_offset);
    if (masked_layer_resource->kind !=
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK ||
        masked_layer_resource->payload_size <
            sizeof(progpu_native_scene_layer_vector_mask) ||
        masked_layer_mask->kind !=
            PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN ||
        masked_layer_mask->path_count != 1U ||
        masked_layer_mask->segment_count == 0U) {
        return 198;
    }

    compat::layer_parameters opacity_brush_layer_parameters =
        layer_parameters;
    opacity_brush_layer_parameters.opacity_brush =
        static_cast<compat::brush*>(target_brush.get());
    target->BeginDraw();
    target->PushLayer(
        &opacity_brush_layer_parameters, target_layer.get());
    target->FillRectangle(
        &layer_bounds, static_cast<compat::brush*>(target_brush.get()));
    target->PopLayer();
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 200;
    }
    const std::uint64_t opacity_brush_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> opacity_brush_scene(
        static_cast<std::size_t>(opacity_brush_scene_size));
    std::uint64_t opacity_brush_scene_written = 0U;
    if (scene_target->BuildScene(
            opacity_brush_scene.data(),
            opacity_brush_scene.size(),
            &opacity_brush_scene_written) != com::ok ||
        opacity_brush_scene_written != opacity_brush_scene_size) {
        return 201;
    }
    const auto* opacity_brush_header = reinterpret_cast<
        const progpu_native_scene_header*>(opacity_brush_scene.data());
    const auto* opacity_brush_push = reinterpret_cast<
        const progpu_native_scene_command*>(
            opacity_brush_scene.data() +
            opacity_brush_header->command_offset);
    const auto* opacity_brush_layer = reinterpret_cast<
        const progpu_native_scene_layer*>(
            opacity_brush_scene.data() +
            opacity_brush_push->payload_offset);
    if (opacity_brush_push->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
        opacity_brush_layer->mask_resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX ||
        opacity_brush_layer->mask_resource_index >=
            opacity_brush_header->resource_count) {
        return 202;
    }
    const auto* opacity_brush_resource = reinterpret_cast<
        const progpu_native_scene_resource*>(
            opacity_brush_scene.data() +
            opacity_brush_header->resource_offset +
            static_cast<std::size_t>(
                opacity_brush_layer->mask_resource_index) *
                opacity_brush_header->resource_stride);
    const auto* opacity_brush_mask = reinterpret_cast<
        const progpu_native_scene_layer_brush_mask*>(
            opacity_brush_scene.data() +
            opacity_brush_resource->payload_offset);
    if (opacity_brush_resource->kind !=
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK ||
        opacity_brush_resource->payload_size <
            sizeof(progpu_native_scene_layer_brush_mask) ||
        opacity_brush_mask->kind != PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH ||
        opacity_brush_mask->brush.type != PROGPU_NATIVE_SCENE_BRUSH_SOLID) {
        return 203;
    }

    compat::layer_parameters composite_mask_layer_parameters =
        masked_layer_parameters;
    composite_mask_layer_parameters.opacity_brush =
        static_cast<compat::brush*>(linear_brush.get());
    target->BeginDraw();
    target->PushLayer(
        &composite_mask_layer_parameters, target_layer.get());
    target->FillRectangle(
        &layer_bounds, static_cast<compat::brush*>(target_brush.get()));
    target->PopLayer();
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 204;
    }
    const std::uint64_t composite_mask_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> composite_mask_scene(
        static_cast<std::size_t>(composite_mask_scene_size));
    std::uint64_t composite_mask_scene_written = 0U;
    if (scene_target->BuildScene(
            composite_mask_scene.data(),
            composite_mask_scene.size(),
            &composite_mask_scene_written) != com::ok ||
        composite_mask_scene_written != composite_mask_scene_size) {
        return 205;
    }
    const auto* composite_mask_header = reinterpret_cast<
        const progpu_native_scene_header*>(composite_mask_scene.data());
    const auto* composite_mask_push = reinterpret_cast<
        const progpu_native_scene_command*>(
            composite_mask_scene.data() +
            composite_mask_header->command_offset);
    const auto* composite_mask_layer = reinterpret_cast<
        const progpu_native_scene_layer*>(
            composite_mask_scene.data() +
            composite_mask_push->payload_offset);
    if (composite_mask_layer->mask_resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX ||
        composite_mask_layer->mask_resource_index >=
            composite_mask_header->resource_count) {
        return 206;
    }
    const auto* composite_mask_resource = reinterpret_cast<
        const progpu_native_scene_resource*>(
            composite_mask_scene.data() +
            composite_mask_header->resource_offset +
            static_cast<std::size_t>(
                composite_mask_layer->mask_resource_index) *
                composite_mask_header->resource_stride);
    const auto* composite_mask = reinterpret_cast<
        const progpu_native_scene_layer_composite_mask*>(
            composite_mask_scene.data() +
            composite_mask_resource->payload_offset);
    if (composite_mask_resource->kind !=
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK ||
        composite_mask_resource->payload_size <
            sizeof(progpu_native_scene_layer_composite_mask) ||
        composite_mask->kind != PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE ||
        composite_mask->component_count != 2U ||
        composite_mask->brush_mask_count != 1U ||
        composite_mask->path_count != 1U ||
        composite_mask->gradient_stop_count != 3U) {
        return 207;
    }

    masked_layer_parameters.mask_antialias_mode =
        compat::antialias_mode::aliased;
    target->BeginDraw();
    target->PushLayer(&masked_layer_parameters, target_layer.get());
    if (target->EndDraw(nullptr, nullptr) != compat::not_implemented ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 199;
    }

    target->BeginDraw();
    target->FillGeometry(
        path_base.get(),
        static_cast<compat::brush*>(target_brush.get()),
        static_cast<compat::brush*>(target_brush.get()));
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 170;
    }

    target->BeginDraw();
    target->DrawGeometry(
        path_base.get(),
        static_cast<compat::brush*>(target_brush.get()),
        -1.0F,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::invalid_argument ||
        scene_target->GetRequiredSceneSize() != 0U) {
        return 171;
    }

    target->BeginDraw();
    target->DrawGeometry(
        path_base.get(),
        static_cast<compat::brush*>(target_brush.get()),
        0.0F,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 172;
    }
    scene_target->GetSummary(&target_summary);
    if (target_summary.draw_count != 0U) {
        return 172;
    }

    compat::mesh* raw_target_mesh = nullptr;
    if (target->CreateMesh(nullptr) != com::pointer_error ||
        target->CreateMesh(&raw_target_mesh) != com::ok ||
        raw_target_mesh == nullptr) {
        return 208;
    }
    com::pointer<compat::mesh> target_mesh;
    target_mesh.attach(raw_target_mesh);
    com::pointer<compat::mesh> queried_target_mesh;
    if (target_mesh.as(
            compat::mesh_interface_id, queried_target_mesh) != com::ok ||
        !queried_target_mesh ||
        target_mesh->Open(nullptr) != com::pointer_error) {
        return 209;
    }
    compat::tessellation_sink* raw_mesh_sink = nullptr;
    if (target_mesh->Open(&raw_mesh_sink) != com::ok ||
        raw_mesh_sink == nullptr) {
        return 210;
    }
    com::pointer<compat::tessellation_sink> mesh_sink;
    mesh_sink.attach(raw_mesh_sink);
    const compat::triangle mesh_triangle{
        {20.0F, 5.0F}, {20.0F, 21.0F}, {4.0F, 21.0F}};
    mesh_sink->AddTriangles(&mesh_triangle, 1U);
    if (query_path->Tessellate(
            nullptr,
            core::default_flattening_tolerance,
            mesh_sink.get()) != com::ok ||
        mesh_sink->Close() != com::ok ||
        mesh_sink->Close() != compat::wrong_state ||
        target_mesh->Open(&raw_mesh_sink) != compat::wrong_state) {
        return 211;
    }
    target->BeginDraw();
    target->FillMesh(
        target_mesh.get(), static_cast<compat::brush*>(target_brush.get()));
    if (target->EndDraw(nullptr, nullptr) != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 212;
    }
    const std::uint64_t mesh_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> mesh_scene(
        static_cast<std::size_t>(mesh_scene_size));
    std::uint64_t mesh_scene_written = 0U;
    if (scene_target->BuildScene(
            mesh_scene.data(),
            mesh_scene.size(),
            &mesh_scene_written) != com::ok ||
        mesh_scene_written != mesh_scene_size) {
        return 213;
    }
    const auto* mesh_header = reinterpret_cast<
        const progpu_native_scene_header*>(mesh_scene.data());
    const auto* mesh_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        mesh_scene.data() + mesh_header->command_offset);
    if (mesh_header->command_count != 1U ||
        mesh_header->resource_count < 2U ||
        mesh_command->kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_PATH ||
        mesh_command->resource_index >= mesh_header->resource_count) {
        return 214;
    }
    const auto* mesh_resource = reinterpret_cast<
        const progpu_native_scene_resource*>(
        mesh_scene.data() + mesh_header->resource_offset +
        static_cast<std::size_t>(mesh_command->resource_index) *
            mesh_header->resource_stride);
    const auto* mesh_paths = reinterpret_cast<
        const progpu_native_scene_path_fill*>(
        mesh_scene.data() + mesh_resource->payload_offset);
    if (mesh_resource->kind != PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH ||
        mesh_resource->payload_size != 3U * sizeof(*mesh_paths) ||
        mesh_paths[0].segment_count != 3U ||
        mesh_paths[1].segment_offset != 3U ||
        mesh_paths[1].segment_count != 3U ||
        mesh_paths[2].segment_offset != 6U ||
        mesh_paths[2].segment_count != 3U) {
        return 215;
    }

    compat::render_target* unsupported =
        reinterpret_cast<compat::render_target*>(
        static_cast<std::uintptr_t>(1U));
    if (factory->CreateWicBitmapRenderTarget(
            nullptr, nullptr, &unsupported) !=
            compat::not_implemented ||
        unsupported != nullptr ||
        factory->CreateWicBitmapRenderTarget(
            nullptr, nullptr, nullptr) !=
            com::pointer_error) {
        return 18;
    }

#if defined(_WIN32)
    if (!com::guid_equal(
            compat::factory_interface_id, __uuidof(ID2D1Factory)) ||
        !com::guid_equal(
            compat::tessellation_sink_interface_id,
            __uuidof(ID2D1TessellationSink)) ||
        !com::guid_equal(
            compat::mesh_interface_id, __uuidof(ID2D1Mesh)) ||
        !com::guid_equal(
            compat::rectangle_geometry_interface_id,
            __uuidof(ID2D1RectangleGeometry)) ||
        !com::guid_equal(
            compat::ellipse_geometry_interface_id,
            __uuidof(ID2D1EllipseGeometry)) ||
        !com::guid_equal(
            compat::rounded_rectangle_geometry_interface_id,
            __uuidof(ID2D1RoundedRectangleGeometry)) ||
        !com::guid_equal(
            compat::geometry_group_interface_id,
            __uuidof(ID2D1GeometryGroup)) ||
        !com::guid_equal(
            compat::stroke_style_interface_id,
            __uuidof(ID2D1StrokeStyle)) ||
        !com::guid_equal(
            compat::drawing_state_block_interface_id,
            __uuidof(ID2D1DrawingStateBlock)) ||
        !com::guid_equal(
            compat::brush_interface_id, __uuidof(ID2D1Brush)) ||
        !com::guid_equal(
            compat::solid_color_brush_interface_id,
            __uuidof(ID2D1SolidColorBrush)) ||
        !com::guid_equal(
            compat::bitmap_interface_id, __uuidof(ID2D1Bitmap)) ||
        !com::guid_equal(
            compat::bitmap_brush_interface_id, __uuidof(ID2D1BitmapBrush)) ||
        !com::guid_equal(
            compat::gradient_stop_collection_interface_id,
            __uuidof(ID2D1GradientStopCollection)) ||
        !com::guid_equal(
            compat::linear_gradient_brush_interface_id,
            __uuidof(ID2D1LinearGradientBrush)) ||
        !com::guid_equal(
            compat::radial_gradient_brush_interface_id,
            __uuidof(ID2D1RadialGradientBrush)) ||
        !com::guid_equal(
            compat::render_target_interface_id,
            __uuidof(ID2D1RenderTarget)) ||
        !com::guid_equal(
            compat::bitmap_render_target_interface_id,
            __uuidof(ID2D1BitmapRenderTarget)) ||
        !com::guid_equal(
            compat::transformed_geometry_interface_id,
            __uuidof(ID2D1TransformedGeometry)) ||
        !com::guid_equal(
            compat::path_geometry_interface_id,
            __uuidof(ID2D1PathGeometry)) ||
        !com::guid_equal(
            compat::simplified_geometry_sink_interface_id,
            __uuidof(ID2D1SimplifiedGeometrySink)) ||
        !com::guid_equal(
            compat::geometry_sink_interface_id,
            __uuidof(ID2D1GeometrySink)) ||
        sizeof(compat::rectangle_f) != sizeof(D2D1_RECT_F) ||
        sizeof(compat::ellipse) != sizeof(D2D1_ELLIPSE) ||
        sizeof(compat::rounded_rectangle) != sizeof(D2D1_ROUNDED_RECT) ||
        sizeof(compat::stroke_style_properties) !=
            sizeof(D2D1_STROKE_STYLE_PROPERTIES) ||
        sizeof(compat::drawing_state_description) !=
            sizeof(D2D1_DRAWING_STATE_DESCRIPTION) ||
        sizeof(compat::color_f) != sizeof(D2D1_COLOR_F) ||
        sizeof(compat::brush_properties) != sizeof(D2D1_BRUSH_PROPERTIES) ||
        sizeof(compat::gradient_stop) != sizeof(D2D1_GRADIENT_STOP) ||
        sizeof(compat::linear_gradient_brush_properties) !=
            sizeof(D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES) ||
        sizeof(compat::radial_gradient_brush_properties) !=
            sizeof(D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES) ||
        sizeof(compat::pixel_format) != sizeof(D2D1_PIXEL_FORMAT) ||
        sizeof(compat::size_u) != sizeof(D2D1_SIZE_U) ||
        sizeof(compat::point_2u) != sizeof(D2D1_POINT_2U) ||
        sizeof(compat::rectangle_u) != sizeof(D2D1_RECT_U) ||
        sizeof(compat::bitmap_properties) != sizeof(D2D1_BITMAP_PROPERTIES) ||
        sizeof(compat::bitmap_brush_properties) !=
            sizeof(D2D1_BITMAP_BRUSH_PROPERTIES) ||
        sizeof(compat::triangle) != sizeof(D2D1_TRIANGLE) ||
        sizeof(compat::quadratic_bezier_segment) !=
            sizeof(D2D1_QUADRATIC_BEZIER_SEGMENT) ||
        sizeof(compat::arc_segment) != sizeof(D2D1_ARC_SEGMENT)) {
        return 19;
    }
    auto* native_solid_brush =
        reinterpret_cast<ID2D1SolidColorBrush*>(solid_brush.get());
    ID2D1Brush* native_brush_base = nullptr;
    if (FAILED(native_solid_brush->QueryInterface(
            __uuidof(ID2D1Brush),
            reinterpret_cast<void**>(&native_brush_base))) ||
        native_brush_base == nullptr) {
        return 116;
    }
    const D2D1_COLOR_F native_portable_brush_color =
        native_solid_brush->GetColor();
    const FLOAT native_portable_brush_opacity =
        native_solid_brush->GetOpacity();
    D2D1_MATRIX_3X2_F native_portable_brush_transform{};
    native_solid_brush->GetTransform(&native_portable_brush_transform);
    native_brush_base->Release();
    if (!approximately_equal(native_portable_brush_color.r, 1.0F) ||
        !approximately_equal(native_portable_brush_color.a, 0.75F) ||
        !approximately_equal(native_portable_brush_opacity, 0.5F) ||
        !approximately_equal(native_portable_brush_transform._22, 3.0F)) {
        return 117;
    }
    auto* native_gradient_collection = reinterpret_cast<
        ID2D1GradientStopCollection*>(gradient_collection.get());
    D2D1_GRADIENT_STOP native_gradient_stops[3]{};
    native_gradient_collection->GetGradientStops(native_gradient_stops, 3U);
    auto* native_linear_brush = reinterpret_cast<
        ID2D1LinearGradientBrush*>(linear_brush.get());
    auto* native_radial_brush = reinterpret_cast<
        ID2D1RadialGradientBrush*>(radial_brush.get());
    ID2D1GradientStopCollection* returned_native_collection = nullptr;
    native_linear_brush->GetGradientStopCollection(
        &returned_native_collection);
    const D2D1_POINT_2F native_linear_start =
        native_linear_brush->GetStartPoint();
    const D2D1_POINT_2F native_radial_center =
        native_radial_brush->GetCenter();
    const float native_radial_radius_x = native_radial_brush->GetRadiusX();
    if (returned_native_collection != nullptr) {
        returned_native_collection->Release();
    }
    if (native_gradient_collection->GetGradientStopCount() != 3U ||
        native_gradient_collection->GetColorInterpolationGamma() !=
            D2D1_GAMMA_2_2 ||
        native_gradient_collection->GetExtendMode() !=
            D2D1_EXTEND_MODE_MIRROR ||
        !approximately_equal(native_gradient_stops[1].position, 0.5F) ||
        !approximately_equal(native_linear_start.x, 4.0F) ||
        !approximately_equal(native_radial_center.x, 17.0F) ||
        !approximately_equal(native_radial_radius_x, 10.0F) ||
        returned_native_collection == nullptr) {
        return 145;
    }
    auto* native_target = reinterpret_cast<ID2D1RenderTarget*>(target.get());
    std::uint32_t native_rendering_parameters_destruction_count = 0U;
    auto* native_rendering_parameters = new fake_rendering_parameters(
        &native_rendering_parameters_destruction_count);
    native_target->SetTextRenderingParams(
        reinterpret_cast<IDWriteRenderingParams*>(
            native_rendering_parameters));
    native_rendering_parameters->Release();
    IDWriteRenderingParams* returned_native_rendering_parameters = nullptr;
    native_target->GetTextRenderingParams(
        &returned_native_rendering_parameters);
    const float returned_native_gamma =
        returned_native_rendering_parameters == nullptr
        ? 0.0F
        : returned_native_rendering_parameters->GetGamma();
    if (returned_native_rendering_parameters != nullptr) {
        returned_native_rendering_parameters->Release();
    }
    native_target->SetTextRenderingParams(nullptr);
    if (!approximately_equal(returned_native_gamma, 2.2F) ||
        native_rendering_parameters_destruction_count != 1U) {
        return 261;
    }
    auto* native_bitmap = reinterpret_cast<ID2D1Bitmap*>(
        portable_bitmap.get());
    ID2D1Bitmap* queried_native_bitmap = nullptr;
    const D2D1_SIZE_U native_bitmap_pixel_size =
        native_bitmap->GetPixelSize();
    const D2D1_PIXEL_FORMAT native_bitmap_format =
        native_bitmap->GetPixelFormat();
    const D2D1_RECT_U native_bitmap_update_rectangle{0U, 1U, 1U, 2U};
    const std::uint8_t native_bitmap_update[]{0x44U, 0x33U, 0x22U, 0xffU};
    if (FAILED(native_bitmap->QueryInterface(
            __uuidof(ID2D1Bitmap),
            reinterpret_cast<void**>(&queried_native_bitmap))) ||
        queried_native_bitmap == nullptr ||
        native_bitmap_pixel_size.width != 2U ||
        native_bitmap_pixel_size.height != 2U ||
        native_bitmap_format.format != DXGI_FORMAT_B8G8R8A8_UNORM ||
        native_bitmap_format.alphaMode != D2D1_ALPHA_MODE_PREMULTIPLIED ||
        FAILED(native_bitmap->CopyFromMemory(
            &native_bitmap_update_rectangle,
            native_bitmap_update,
            4U))) {
        if (queried_native_bitmap != nullptr) {
            queried_native_bitmap->Release();
        }
        return 153;
    }
    queried_native_bitmap->Release();
    ID2D1Bitmap* native_shared_bitmap = nullptr;
    if (FAILED(native_target->CreateSharedBitmap(
            __uuidof(ID2D1Bitmap),
            native_bitmap,
            nullptr,
            &native_shared_bitmap)) ||
        native_shared_bitmap == nullptr ||
        native_shared_bitmap == native_bitmap ||
        native_shared_bitmap->GetPixelSize().width != 2U ||
        native_shared_bitmap->GetPixelFormat().format !=
            DXGI_FORMAT_B8G8R8A8_UNORM) {
        if (native_shared_bitmap != nullptr) {
            native_shared_bitmap->Release();
        }
        return 254;
    }
    native_shared_bitmap->Release();
    auto* native_bitmap_brush = reinterpret_cast<ID2D1BitmapBrush*>(
        bitmap_brush.get());
    ID2D1BitmapBrush* queried_native_bitmap_brush = nullptr;
    ID2D1Bitmap* returned_native_brush_bitmap = nullptr;
    if (FAILED(native_bitmap_brush->QueryInterface(
            __uuidof(ID2D1BitmapBrush),
            reinterpret_cast<void**>(&queried_native_bitmap_brush))) ||
        queried_native_bitmap_brush == nullptr) {
        return 165;
    }
    native_bitmap_brush->GetBitmap(&returned_native_brush_bitmap);
    const bool native_bitmap_brush_matches =
        returned_native_brush_bitmap == native_bitmap &&
        native_bitmap_brush->GetExtendModeX() == D2D1_EXTEND_MODE_CLAMP &&
        native_bitmap_brush->GetExtendModeY() == D2D1_EXTEND_MODE_WRAP &&
        native_bitmap_brush->GetInterpolationMode() ==
            D2D1_BITMAP_INTERPOLATION_MODE_LINEAR;
    if (returned_native_brush_bitmap != nullptr) {
        returned_native_brush_bitmap->Release();
    }
    queried_native_bitmap_brush->Release();
    if (!native_bitmap_brush_matches) {
        return 166;
    }
    const D2D1_SIZE_U native_target_pixel_size = native_target->GetPixelSize();
    const D2D1_SIZE_F native_target_size = native_target->GetSize();
    ID2D1SolidColorBrush* native_target_brush = nullptr;
    const D2D1_COLOR_F native_target_color{0.2F, 0.4F, 0.6F, 0.8F};
    if (native_target_pixel_size.width != 640U ||
        native_target_pixel_size.height != 480U ||
        !approximately_equal(native_target_size.width, 640.0F) ||
        FAILED(native_target->CreateSolidColorBrush(
            &native_target_color, nullptr, &native_target_brush)) ||
        native_target_brush == nullptr) {
        return 128;
    }
    const D2D1_SIZE_F native_compatible_size{16.0F, 12.0F};
    const D2D1_SIZE_U native_compatible_pixel_size{16U, 12U};
    const D2D1_PIXEL_FORMAT native_compatible_format{
        DXGI_FORMAT_A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED};
    ID2D1BitmapRenderTarget* native_compatible_target = nullptr;
    ID2D1BitmapRenderTarget* queried_native_compatible_target = nullptr;
    if (FAILED(native_target->CreateCompatibleRenderTarget(
            &native_compatible_size,
            &native_compatible_pixel_size,
            &native_compatible_format,
            D2D1_COMPATIBLE_RENDER_TARGET_OPTIONS_NONE,
            &native_compatible_target)) ||
        native_compatible_target == nullptr ||
        FAILED(native_compatible_target->QueryInterface(
            __uuidof(ID2D1BitmapRenderTarget),
            reinterpret_cast<void**>(&queried_native_compatible_target))) ||
        queried_native_compatible_target == nullptr) {
        if (queried_native_compatible_target != nullptr) {
            queried_native_compatible_target->Release();
        }
        if (native_compatible_target != nullptr) {
            native_compatible_target->Release();
        }
        native_target_brush->Release();
        std::fprintf(stderr,
            "portable Windows ID2D1BitmapRenderTarget creation/QI failed\n");
        return 232;
    }
    queried_native_compatible_target->Release();
    native_compatible_target->BeginDraw();
    const D2D1_COLOR_F native_compatible_clear{};
    native_compatible_target->Clear(&native_compatible_clear);
    if (FAILED(native_compatible_target->EndDraw())) {
        native_compatible_target->Release();
        native_target_brush->Release();
        std::fprintf(stderr,
            "portable Windows ID2D1BitmapRenderTarget EndDraw failed\n");
        return 233;
    }
    ID2D1Bitmap* native_compatible_bitmap = nullptr;
    if (FAILED(native_compatible_target->GetBitmap(
            &native_compatible_bitmap)) ||
        native_compatible_bitmap == nullptr ||
        native_compatible_bitmap->GetPixelFormat().format !=
            DXGI_FORMAT_A8_UNORM) {
        if (native_compatible_bitmap != nullptr) {
            native_compatible_bitmap->Release();
        }
        native_compatible_target->Release();
        native_target_brush->Release();
        std::fprintf(stderr,
            "portable Windows ID2D1BitmapRenderTarget GetBitmap failed\n");
        return 234;
    }
    ID2D1Bitmap* native_shared_compatible_bitmap = nullptr;
    if (FAILED(native_target->CreateSharedBitmap(
            __uuidof(ID2D1Bitmap),
            native_compatible_bitmap,
            nullptr,
            &native_shared_compatible_bitmap)) ||
        native_shared_compatible_bitmap == nullptr ||
        native_shared_compatible_bitmap->GetPixelFormat().format !=
            DXGI_FORMAT_A8_UNORM) {
        if (native_shared_compatible_bitmap != nullptr) {
            native_shared_compatible_bitmap->Release();
        }
        native_compatible_bitmap->Release();
        native_compatible_target->Release();
        native_target_brush->Release();
        std::fprintf(stderr,
            "portable Windows shared compatible bitmap failed\n");
        return 234;
    }
    native_target->BeginDraw();
    native_target->SetAntialiasMode(D2D1_ANTIALIAS_MODE_ALIASED);
    const D2D1_RECT_F native_compatible_destination{
        76.0F, 28.0F, 92.0F, 40.0F};
    native_target->FillOpacityMask(
        native_shared_compatible_bitmap,
        native_target_brush,
        D2D1_OPACITY_MASK_CONTENT_GRAPHICS,
        &native_compatible_destination,
        nullptr);
    const HRESULT native_compatible_draw_status = native_target->EndDraw();
    native_shared_compatible_bitmap->Release();
    native_compatible_bitmap->Release();
    native_compatible_target->Release();
    if (FAILED(native_compatible_draw_status)) {
        native_target_brush->Release();
        std::fprintf(stderr,
            "portable Windows compatible bitmap FillOpacityMask failed\n");
        return 235;
    }
    ID2D1Mesh* native_target_mesh = nullptr;
    ID2D1Mesh* queried_native_target_mesh = nullptr;
    ID2D1TessellationSink* native_mesh_sink = nullptr;
    D2D1_TRIANGLE native_mesh_triangle{};
    native_mesh_triangle.point1 = D2D1_POINT_2F{58.0F, 9.0F};
    native_mesh_triangle.point2 = D2D1_POINT_2F{70.0F, 9.0F};
    native_mesh_triangle.point3 = D2D1_POINT_2F{58.0F, 25.0F};
    if (FAILED(native_target->CreateMesh(&native_target_mesh)) ||
        native_target_mesh == nullptr ||
        FAILED(native_target_mesh->QueryInterface(
            __uuidof(ID2D1Mesh),
            reinterpret_cast<void**>(&queried_native_target_mesh))) ||
        queried_native_target_mesh == nullptr ||
        FAILED(native_target_mesh->Open(&native_mesh_sink)) ||
        native_mesh_sink == nullptr) {
        if (queried_native_target_mesh != nullptr) {
            queried_native_target_mesh->Release();
        }
        if (native_target_mesh != nullptr) {
            native_target_mesh->Release();
        }
        native_target_brush->Release();
        return 216;
    }
    queried_native_target_mesh->Release();
    native_mesh_sink->AddTriangles(&native_mesh_triangle, 1U);
    if (FAILED(native_mesh_sink->Close())) {
        native_mesh_sink->Release();
        native_target_mesh->Release();
        native_target_brush->Release();
        return 217;
    }
    native_mesh_sink->Release();
    auto* native_fake_font_face = new fake_font_face();
    const UINT16 native_glyph_indices[]{51U, 52U};
    const FLOAT native_glyph_advances[]{7.0F, 8.0F};
    const DWRITE_GLYPH_OFFSET native_glyph_offsets[]{
        {0.0F, 0.0F}, {0.25F, 0.5F}};
    const DWRITE_GLYPH_RUN native_glyph_run{
        reinterpret_cast<IDWriteFontFace*>(native_fake_font_face),
        11.0F,
        2U,
        native_glyph_indices,
        native_glyph_advances,
        native_glyph_offsets,
        FALSE,
        0U};
    native_target->BeginDraw();
    native_target->DrawGlyphRun(
        D2D1_POINT_2F{4.0F, 24.0F},
        &native_glyph_run,
        native_target_brush,
        DWRITE_MEASURING_MODE_NATURAL);
    const HRESULT native_glyph_status = native_target->EndDraw();
    const std::uint32_t native_outline_calls =
        native_fake_font_face->outline_call_count;
    native_fake_font_face->Release();
    if (FAILED(native_glyph_status) || native_outline_calls != 1U) {
        native_target_mesh->Release();
        native_target_brush->Release();
        std::fprintf(stderr,
            "portable Windows ID2D1RenderTarget DrawGlyphRun failed\n");
        return 249;
    }
    auto* native_layout_font_face = new fake_font_face();
    const UINT16 native_layout_glyph_indices[]{61U, 62U};
    const FLOAT native_layout_glyph_advances[]{6.0F, 7.0F};
    fake_text_layout native_text_layout{};
    native_text_layout.vtable = &fake_layout_vtable;
    native_text_layout.glyphs = {
        native_layout_font_face,
        10.0F,
        2U,
        native_layout_glyph_indices,
        native_layout_glyph_advances,
        nullptr,
        0,
        0U};
    native_target->BeginDraw();
    native_target->DrawTextLayout(
        D2D1_POINT_2F{12.0F, 32.0F},
        reinterpret_cast<IDWriteTextLayout*>(&native_text_layout),
        native_target_brush,
        static_cast<D2D1_DRAW_TEXT_OPTIONS>(
            D2D1_DRAW_TEXT_OPTIONS_CLIP |
            D2D1_DRAW_TEXT_OPTIONS_NO_SNAP));
    const HRESULT native_text_layout_status = native_target->EndDraw();
    const std::uint32_t native_layout_outline_calls =
        native_layout_font_face->outline_call_count;
    native_layout_font_face->Release();
    if (FAILED(native_text_layout_status) ||
        native_text_layout.draw_call_count != 1U ||
        native_layout_outline_calls != 1U ||
        native_text_layout.pixel_snapping_disabled != 1) {
        native_target_mesh->Release();
        native_target_brush->Release();
        std::fprintf(stderr,
            "portable Windows ID2D1RenderTarget DrawTextLayout failed\n");
        return 253;
    }
    auto* native_text_font_face = new fake_font_face();
    const UINT16 native_text_glyph_indices[]{71U, 72U};
    const FLOAT native_text_glyph_advances[]{6.0F, 7.0F};
    const compat::glyph_run native_text_glyph_run{
        native_text_font_face,
        10.0F,
        2U,
        native_text_glyph_indices,
        native_text_glyph_advances,
        nullptr,
        0,
        0U};
    auto* native_fake_text_format =
        new fake_text_format(native_text_glyph_run);
    const D2D1_RECT_F native_text_rectangle{
        18.0F, 36.0F, 98.0F, 66.0F};
    native_target->BeginDraw();
    native_target->DrawText(
        L"CD",
        2U,
        reinterpret_cast<IDWriteTextFormat*>(native_fake_text_format),
        &native_text_rectangle,
        native_target_brush,
        static_cast<D2D1_DRAW_TEXT_OPTIONS>(
            D2D1_DRAW_TEXT_OPTIONS_CLIP |
            D2D1_DRAW_TEXT_OPTIONS_NO_SNAP),
        DWRITE_MEASURING_MODE_GDI_NATURAL);
    const HRESULT native_text_status = native_target->EndDraw();
    const std::uint32_t native_text_create_calls =
        native_fake_text_format->create_call_count;
    const std::uint32_t native_text_outline_calls =
        native_text_font_face->outline_call_count;
    native_fake_text_format->Release();
    native_text_font_face->Release();
    if (FAILED(native_text_status) || native_text_create_calls != 1U ||
        native_text_outline_calls != 1U) {
        native_target_mesh->Release();
        native_target_brush->Release();
        std::fprintf(stderr,
            "portable Windows ID2D1RenderTarget DrawText failed\n");
        return 258;
    }
    compat::scene_render_target_summary native_target_baseline{};
    scene_target->GetSummary(&native_target_baseline);
    native_target->BeginDraw();
    const D2D1_RECT_F native_target_rectangle{8.0F, 9.0F, 30.0F, 40.0F};
    native_target->FillRectangle(
        &native_target_rectangle, native_target_brush);
    const D2D1_RECT_F native_bitmap_brush_rectangle{
        32.0F, 9.0F, 39.0F, 25.0F};
    native_target->FillRectangle(
        &native_bitmap_brush_rectangle, native_bitmap_brush);
    const D2D1_RECT_F native_bitmap_destination{40.0F, 9.0F, 56.0F, 25.0F};
    native_target->DrawBitmap(
        native_bitmap,
        &native_bitmap_destination,
        1.0F,
        D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR,
        nullptr);
    native_target->FillMesh(native_target_mesh, native_target_brush);
    const HRESULT native_target_end_status = native_target->EndDraw();
    native_target_mesh->Release();
    native_target_brush->Release();
    scene_target->GetSummary(&target_summary);
    if (FAILED(native_target_end_status) ||
        target_summary.generation != native_target_baseline.generation + 1U ||
        target_summary.draw_count != 4U ||
        scene_target->GetRequiredSceneSize() == 0U) {
        std::fprintf(
            stderr,
            "portable Windows target draw status=%d generation=%llu/%llu draws=%u scene=%llu\n",
            static_cast<int>(native_target_end_status),
            static_cast<unsigned long long>(target_summary.generation),
            static_cast<unsigned long long>(
                native_target_baseline.generation + 1U),
            target_summary.draw_count,
            static_cast<unsigned long long>(
                scene_target->GetRequiredSceneSize()));
        return 129;
    }
    auto* native_factory = reinterpret_cast<ID2D1Factory*>(factory.get());
    const D2D1_RECT_F native_rectangle{2.0F, 3.0F, 6.0F, 8.0F};
    ID2D1RectangleGeometry* native_geometry = nullptr;
    if (FAILED(native_factory->CreateRectangleGeometry(
            &native_rectangle, &native_geometry)) ||
        native_geometry == nullptr) {
        return 20;
    }
    float native_area = 0.0F;
    const HRESULT native_status = native_geometry->ComputeArea(
        nullptr, D2D1_DEFAULT_FLATTENING_TOLERANCE, &native_area);
    if (FAILED(native_status) || !approximately_equal(native_area, 20.0F)) {
        native_geometry->Release();
        return 21;
    }
    auto* raw_native_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> native_outline_sink;
    native_outline_sink.attach(raw_native_outline_sink);
    const HRESULT native_outline_status = native_geometry->Outline(
        nullptr,
        D2D1_DEFAULT_FLATTENING_TOLERANCE,
        reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
            native_outline_sink.get()));
    if (FAILED(native_outline_status) ||
        raw_native_outline_sink->fill_mode != compat::fill_mode::alternate ||
        raw_native_outline_sink->figure_begin !=
            compat::figure_begin::filled ||
        raw_native_outline_sink->figure_end !=
            compat::figure_end::closed ||
        raw_native_outline_sink->begin_count != 1U ||
        raw_native_outline_sink->end_count != 1U ||
        raw_native_outline_sink->line_count != 4U) {
        native_geometry->Release();
        return 263;
    }
    const D2D1_MATRIX_3X2_F native_transform = make_native_matrix(
        1.0F, 0.0F, 0.0F, 1.0F, 4.0F, -2.0F);
    ID2D1TransformedGeometry* native_transformed = nullptr;
    const HRESULT native_transformed_status =
        native_factory->CreateTransformedGeometry(
            native_geometry, &native_transform, &native_transformed);
    native_geometry->Release();
    if (FAILED(native_transformed_status) || native_transformed == nullptr) {
        return 22;
    }
    D2D1_RECT_F native_bounds{};
    const HRESULT native_bounds_status =
        native_transformed->GetBounds(nullptr, &native_bounds);
    native_transformed->Release();
    if (FAILED(native_bounds_status) ||
        !approximately_equal(native_bounds.left, 6.0F) ||
        !approximately_equal(native_bounds.top, 1.0F) ||
        !approximately_equal(native_bounds.right, 10.0F) ||
        !approximately_equal(native_bounds.bottom, 6.0F)) {
        return 23;
    }

    const D2D1_ELLIPSE native_ellipse_value{
        D2D1_POINT_2F{2.0F, 3.0F}, 4.0F, 2.0F};
    const D2D1_MATRIX_3X2_F native_ellipse_transform = make_native_matrix(
        0.5F, 1.25F, -0.75F, 0.25F, 4.0F, -3.0F);
    ID2D1EllipseGeometry* native_ellipse = nullptr;
    if (FAILED(native_factory->CreateEllipseGeometry(
            &native_ellipse_value, &native_ellipse)) ||
        native_ellipse == nullptr) {
        return 58;
    }
    D2D1_ELLIPSE returned_native_ellipse{};
    D2D1_RECT_F portable_ellipse_bounds{};
    BOOL portable_ellipse_contains = FALSE;
    native_ellipse->GetEllipse(&returned_native_ellipse);
    const HRESULT portable_ellipse_bounds_status =
        native_ellipse->GetBounds(
            &native_ellipse_transform, &portable_ellipse_bounds);
    const HRESULT portable_ellipse_contains_status =
        native_ellipse->FillContainsPoint(
            D2D1_POINT_2F{2.75F, 0.25F},
            &native_ellipse_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &portable_ellipse_contains);
    native_ellipse->Release();
    if (FAILED(portable_ellipse_bounds_status) ||
        FAILED(portable_ellipse_contains_status) ||
        portable_ellipse_contains != TRUE ||
        !approximately_equal(returned_native_ellipse.point.x, 2.0F) ||
        !approximately_equal(returned_native_ellipse.radiusY, 2.0F)) {
        return 59;
    }

    const D2D1_ROUNDED_RECT native_rounded_rectangle_value{
        D2D1_RECT_F{0.0F, 0.0F, 10.0F, 8.0F}, 3.0F, 2.0F};
    const D2D1_MATRIX_3X2_F native_rounded_rectangle_transform =
        make_native_matrix(
            0.5F, 1.25F, -0.75F, 0.25F, 4.0F, -3.0F);
    ID2D1RoundedRectangleGeometry* native_rounded_rectangle = nullptr;
    if (FAILED(native_factory->CreateRoundedRectangleGeometry(
            &native_rounded_rectangle_value, &native_rounded_rectangle)) ||
        native_rounded_rectangle == nullptr) {
        return 73;
    }
    D2D1_ROUNDED_RECT returned_native_rounded_rectangle{};
    D2D1_RECT_F portable_rounded_rectangle_bounds{};
    BOOL portable_rounded_rectangle_center_contains = FALSE;
    BOOL portable_rounded_rectangle_corner_contains = TRUE;
    native_rounded_rectangle->GetRoundedRect(
        &returned_native_rounded_rectangle);
    const HRESULT portable_rounded_rectangle_bounds_status =
        native_rounded_rectangle->GetBounds(
            &native_rounded_rectangle_transform,
            &portable_rounded_rectangle_bounds);
    const HRESULT portable_rounded_rectangle_center_status =
        native_rounded_rectangle->FillContainsPoint(
            D2D1_POINT_2F{3.5F, 4.25F},
            &native_rounded_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &portable_rounded_rectangle_center_contains);
    const HRESULT portable_rounded_rectangle_corner_status =
        native_rounded_rectangle->FillContainsPoint(
            D2D1_POINT_2F{3.975F, -2.85F},
            &native_rounded_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &portable_rounded_rectangle_corner_contains);
    native_rounded_rectangle->Release();
    if (FAILED(portable_rounded_rectangle_bounds_status) ||
        FAILED(portable_rounded_rectangle_center_status) ||
        FAILED(portable_rounded_rectangle_corner_status) ||
        portable_rounded_rectangle_center_contains != TRUE ||
        portable_rounded_rectangle_corner_contains != FALSE ||
        !approximately_equal(
            returned_native_rounded_rectangle.radiusX, 3.0F) ||
        !approximately_equal(
            returned_native_rounded_rectangle.rect.bottom, 8.0F)) {
        return 74;
    }

    std::array<ID2D1Geometry*, 2U> native_group_sources{
        reinterpret_cast<ID2D1Geometry*>(geometry_base.get()),
        reinterpret_cast<ID2D1Geometry*>(ellipse_base.get())};
    ID2D1GeometryGroup* native_group = nullptr;
    if (FAILED(native_factory->CreateGeometryGroup(
            D2D1_FILL_MODE_ALTERNATE,
            native_group_sources.data(),
            static_cast<UINT32>(native_group_sources.size()),
            &native_group)) ||
        native_group == nullptr) {
        return 82;
    }
    D2D1_RECT_F portable_group_bounds{};
    const HRESULT portable_group_bounds_status = native_group->GetBounds(
        &native_ellipse_transform, &portable_group_bounds);
    std::array<ID2D1Geometry*, 2U> returned_native_group_sources{};
    native_group->GetSourceGeometries(
        returned_native_group_sources.data(),
        static_cast<UINT32>(returned_native_group_sources.size()));
    const bool native_group_metadata_matches =
        native_group->GetFillMode() == D2D1_FILL_MODE_ALTERNATE &&
        native_group->GetSourceGeometryCount() ==
            static_cast<UINT32>(native_group_sources.size()) &&
        returned_native_group_sources[0U] == native_group_sources[0U] &&
        returned_native_group_sources[1U] == native_group_sources[1U];
    for (auto* returned_native_source : returned_native_group_sources) {
        if (returned_native_source != nullptr) {
            returned_native_source->Release();
        }
    }
    std::array<ID2D1Geometry*, 1U> native_nested_group_sources{
        native_group};
    ID2D1GeometryGroup* native_nested_group = nullptr;
    const HRESULT native_nested_group_create_status =
        native_factory->CreateGeometryGroup(
            D2D1_FILL_MODE_WINDING,
            native_nested_group_sources.data(),
            static_cast<UINT32>(native_nested_group_sources.size()),
            &native_nested_group);
    D2D1_RECT_F portable_native_nested_group_bounds{};
    BOOL portable_native_nested_group_contains = FALSE;
    auto* raw_portable_native_nested_group_simplified =
        new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_native_nested_group_simplified;
    portable_native_nested_group_simplified.attach(
        raw_portable_native_nested_group_simplified);
    const HRESULT portable_native_nested_group_bounds_status =
        native_nested_group == nullptr
        ? E_FAIL
        : native_nested_group->GetBounds(
              &native_ellipse_transform,
              &portable_native_nested_group_bounds);
    const HRESULT portable_native_nested_group_contains_status =
        native_nested_group == nullptr
        ? E_FAIL
        : native_nested_group->FillContainsPoint(
              D2D1_POINT_2F{2.75F, 0.25F},
              &native_ellipse_transform,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &portable_native_nested_group_contains);
    const HRESULT portable_native_nested_group_simplify_status =
        native_nested_group == nullptr
        ? E_FAIL
        : native_nested_group->Simplify(
              D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES,
              &native_ellipse_transform,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                  portable_native_nested_group_simplified.get()));
    if (native_nested_group != nullptr) {
        native_nested_group->Release();
    }
    native_group->Release();
    if (FAILED(portable_group_bounds_status) ||
        !native_group_metadata_matches ||
        FAILED(native_nested_group_create_status) ||
        FAILED(portable_native_nested_group_bounds_status) ||
        FAILED(portable_native_nested_group_contains_status) ||
        FAILED(portable_native_nested_group_simplify_status) ||
        !approximately_equal(
            portable_native_nested_group_bounds.left,
            portable_group_bounds.left) ||
        !approximately_equal(
            portable_native_nested_group_bounds.top,
            portable_group_bounds.top) ||
        !approximately_equal(
            portable_native_nested_group_bounds.right,
            portable_group_bounds.right) ||
        !approximately_equal(
            portable_native_nested_group_bounds.bottom,
            portable_group_bounds.bottom)) {
        return 83;
    }

    const D2D1_STROKE_STYLE_PROPERTIES native_stroke_properties{
        D2D1_CAP_STYLE_ROUND,
        D2D1_CAP_STYLE_SQUARE,
        D2D1_CAP_STYLE_TRIANGLE,
        D2D1_LINE_JOIN_BEVEL,
        4.0F,
        D2D1_DASH_STYLE_CUSTOM,
        0.5F};
    const std::array<float, 4U> native_stroke_dashes{
        2.0F, 1.0F, 0.5F, 1.0F};
    ID2D1StrokeStyle* native_stroke_style = nullptr;
    if (FAILED(native_factory->CreateStrokeStyle(
            &native_stroke_properties,
            native_stroke_dashes.data(),
            static_cast<UINT32>(native_stroke_dashes.size()),
            &native_stroke_style)) ||
        native_stroke_style == nullptr) {
        return 96;
    }
    std::array<float, 4U> portable_native_stroke_dashes{};
    native_stroke_style->GetDashes(
        portable_native_stroke_dashes.data(),
        static_cast<UINT32>(portable_native_stroke_dashes.size()));
    const bool portable_native_stroke_matches =
        native_stroke_style->GetStartCap() == D2D1_CAP_STYLE_ROUND &&
        native_stroke_style->GetEndCap() == D2D1_CAP_STYLE_SQUARE &&
        native_stroke_style->GetDashCap() == D2D1_CAP_STYLE_TRIANGLE &&
        native_stroke_style->GetLineJoin() == D2D1_LINE_JOIN_BEVEL &&
        approximately_equal(native_stroke_style->GetMiterLimit(), 4.0F) &&
        approximately_equal(native_stroke_style->GetDashOffset(), 0.5F) &&
        native_stroke_style->GetDashStyle() == D2D1_DASH_STYLE_CUSTOM &&
        native_stroke_style->GetDashesCount() ==
            static_cast<UINT32>(native_stroke_dashes.size());
    native_stroke_style->Release();
    if (!portable_native_stroke_matches) {
        return 97;
    }

    const D2D1_DRAWING_STATE_DESCRIPTION native_drawing_state_description{
        D2D1_ANTIALIAS_MODE_ALIASED,
        D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE,
        17U,
        23U,
        make_native_matrix(1.0F, 0.25F, -0.5F, 2.0F, 3.0F, -4.0F)};
    ID2D1DrawingStateBlock* native_drawing_state = nullptr;
    if (FAILED(native_factory->CreateDrawingStateBlock(
            &native_drawing_state_description,
            nullptr,
            &native_drawing_state)) ||
        native_drawing_state == nullptr) {
        return 107;
    }
    D2D1_DRAWING_STATE_DESCRIPTION portable_native_drawing_state{};
    native_drawing_state->GetDescription(&portable_native_drawing_state);
    D2D1_DRAWING_STATE_DESCRIPTION changed_native_drawing_state =
        native_drawing_state_description;
    changed_native_drawing_state.tag1 = 31U;
    changed_native_drawing_state.transform._31 = 9.0F;
    native_drawing_state->SetDescription(&changed_native_drawing_state);
    D2D1_DRAWING_STATE_DESCRIPTION returned_changed_native_drawing_state{};
    native_drawing_state->GetDescription(
        &returned_changed_native_drawing_state);
    IDWriteRenderingParams* portable_native_text_parameters =
        reinterpret_cast<IDWriteRenderingParams*>(
            static_cast<std::uintptr_t>(1U));
    native_drawing_state->GetTextRenderingParams(
        &portable_native_text_parameters);
    native_drawing_state->Release();
    if (portable_native_drawing_state.antialiasMode !=
            D2D1_ANTIALIAS_MODE_ALIASED ||
        portable_native_drawing_state.textAntialiasMode !=
            D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE ||
        portable_native_drawing_state.tag1 != 17U ||
        portable_native_drawing_state.tag2 != 23U ||
        returned_changed_native_drawing_state.tag1 != 31U ||
        !approximately_equal(
            returned_changed_native_drawing_state.transform._31, 9.0F) ||
        portable_native_text_parameters != nullptr) {
        return 108;
    }

    ID2D1PathGeometry* native_path = nullptr;
    if (FAILED(native_factory->CreatePathGeometry(&native_path)) ||
        native_path == nullptr) {
        return 41;
    }
    ID2D1GeometrySink* native_path_sink = nullptr;
    if (FAILED(native_path->Open(&native_path_sink)) ||
        native_path_sink == nullptr) {
        native_path->Release();
        return 42;
    }
    ID2D1SimplifiedGeometrySink* native_path_sink_base = nullptr;
    if (FAILED(native_path_sink->QueryInterface(
            __uuidof(ID2D1SimplifiedGeometrySink),
            reinterpret_cast<void**>(&native_path_sink_base))) ||
        native_path_sink_base == nullptr) {
        native_path_sink->Release();
        native_path->Release();
        return 43;
    }
    native_path_sink_base->Release();
    native_path_sink->SetFillMode(D2D1_FILL_MODE_WINDING);
    native_path_sink->BeginFigure(
        D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_FILLED);
    native_path_sink->AddLine(D2D1_POINT_2F{2.0F, 0.0F});
    const D2D1_QUADRATIC_BEZIER_SEGMENT native_quadratic{
        D2D1_POINT_2F{3.0F, 2.0F}, D2D1_POINT_2F{4.0F, 0.0F}};
    native_path_sink->SetSegmentFlags(
        D2D1_PATH_SEGMENT_FORCE_ROUND_LINE_JOIN);
    native_path_sink->AddQuadraticBezier(&native_quadratic);
    native_path_sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    const HRESULT native_path_close_status = native_path_sink->Close();
    native_path_sink->Release();
    if (FAILED(native_path_close_status)) {
        native_path->Release();
        return 44;
    }
    UINT32 native_path_segments = 0U;
    UINT32 native_path_figures = 0U;
    D2D1_RECT_F native_path_bounds{};
    if (FAILED(native_path->GetSegmentCount(&native_path_segments)) ||
        native_path_segments != 3U ||
        FAILED(native_path->GetFigureCount(&native_path_figures)) ||
        native_path_figures != 1U ||
        FAILED(native_path->GetBounds(nullptr, &native_path_bounds)) ||
        !approximately_equal(native_path_bounds.left, 0.0F) ||
        !approximately_equal(native_path_bounds.top, 0.0F) ||
        !approximately_equal(native_path_bounds.right, 4.0F) ||
        !approximately_equal(native_path_bounds.bottom, 1.0F)) {
        native_path->Release();
        return 45;
    }

    ID2D1PathGeometry* native_streamed_path = nullptr;
    ID2D1GeometrySink* native_streamed_sink = nullptr;
    if (FAILED(native_factory->CreatePathGeometry(&native_streamed_path)) ||
        native_streamed_path == nullptr ||
        FAILED(native_streamed_path->Open(&native_streamed_sink)) ||
        native_streamed_sink == nullptr) {
        if (native_streamed_path != nullptr) {
            native_streamed_path->Release();
        }
        native_path->Release();
        return 46;
    }
    const HRESULT native_stream_status =
        native_path->Stream(native_streamed_sink);
    const HRESULT native_stream_close_status = native_streamed_sink->Close();
    native_streamed_sink->Release();
    native_path->Release();
    native_path_segments = 0U;
    const HRESULT native_stream_count_status =
        native_streamed_path->GetSegmentCount(&native_path_segments);
    native_streamed_path->Release();
    if (FAILED(native_stream_status) || FAILED(native_stream_close_status) ||
        FAILED(native_stream_count_status) || native_path_segments != 3U) {
        return 47;
    }

    ID2D1Factory* system_factory = nullptr;
    if (FAILED(D2D1CreateFactory(
            D2D1_FACTORY_TYPE_SINGLE_THREADED,
            &system_factory)) ||
        system_factory == nullptr) {
        return 48;
    }
    ID2D1PathGeometry* system_path = nullptr;
    ID2D1GeometrySink* system_sink = nullptr;
    if (FAILED(system_factory->CreatePathGeometry(&system_path)) ||
        system_path == nullptr || FAILED(system_path->Open(&system_sink)) ||
        system_sink == nullptr) {
        if (system_path != nullptr) {
            system_path->Release();
        }
        system_factory->Release();
        return 49;
    }
    system_sink->SetFillMode(D2D1_FILL_MODE_WINDING);
    system_sink->BeginFigure(
        D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_FILLED);
    system_sink->AddLine(D2D1_POINT_2F{2.0F, 0.0F});
    system_sink->SetSegmentFlags(
        D2D1_PATH_SEGMENT_FORCE_ROUND_LINE_JOIN);
    system_sink->AddQuadraticBezier(&native_quadratic);
    system_sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    const HRESULT system_close_status = system_sink->Close();
    system_sink->Release();
    UINT32 system_segments = 0U;
    UINT32 system_figures = 0U;
    D2D1_RECT_F system_bounds{};
    const HRESULT system_segment_status =
        system_path->GetSegmentCount(&system_segments);
    const HRESULT system_figure_status =
        system_path->GetFigureCount(&system_figures);
    const HRESULT system_bounds_status =
        system_path->GetBounds(nullptr, &system_bounds);
    system_path->Release();
    if (FAILED(system_close_status) || FAILED(system_segment_status) ||
        FAILED(system_figure_status) || FAILED(system_bounds_status) ||
        system_segments != native_path_segments || system_figures != 1U ||
        !approximately_equal(system_bounds.left, native_path_bounds.left) ||
        !approximately_equal(system_bounds.top, native_path_bounds.top) ||
        !approximately_equal(system_bounds.right, native_path_bounds.right) ||
        !approximately_equal(
            system_bounds.bottom, native_path_bounds.bottom)) {
        system_factory->Release();
        return 50;
    }

  const auto create_system_polygon = [system_factory](
                                         const D2D1_POINT_2F *points,
                                         std::size_t point_count,
                                         ID2D1PathGeometry **value) {
    if (value == nullptr) {
      return E_POINTER;
    }
    *value = nullptr;
    if (points == nullptr || point_count < 3U ||
        point_count - 1U >
            static_cast<std::size_t>((std::numeric_limits<UINT32>::max)())) {
      return E_INVALIDARG;
    }
    ID2D1PathGeometry *path_value = nullptr;
    ID2D1GeometrySink *sink_value = nullptr;
    HRESULT status = system_factory->CreatePathGeometry(&path_value);
    if (SUCCEEDED(status)) {
      status = path_value->Open(&sink_value);
    }
    if (SUCCEEDED(status)) {
      sink_value->SetFillMode(D2D1_FILL_MODE_WINDING);
      sink_value->BeginFigure(points[0U], D2D1_FIGURE_BEGIN_FILLED);
      sink_value->AddLines(points + 1U, static_cast<UINT32>(point_count - 1U));
      sink_value->EndFigure(D2D1_FIGURE_END_CLOSED);
      status = sink_value->Close();
    }
    if (sink_value != nullptr) {
      sink_value->Release();
    }
    if (FAILED(status)) {
      if (path_value != nullptr) {
        path_value->Release();
      }
      return status;
    }
    *value = path_value;
    return S_OK;
  };
  constexpr std::array<D2D1_POINT_2F, 4U> system_query_polygon{{
      {1.0F, 2.0F},
      {5.0F, 2.0F},
      {5.0F, 8.0F},
      {1.0F, 8.0F},
  }};
  constexpr std::array<D2D1_POINT_2F, 6U> system_boolean_polygon{{
      {3.0F, 1.0F},
      {7.0F, 1.0F},
      {7.0F, 5.0F},
      {4.0F, 5.0F},
      {4.0F, 9.0F},
      {3.0F, 9.0F},
  }};
  ID2D1PathGeometry *system_query_boolean_path = nullptr;
  ID2D1PathGeometry *system_input_boolean_path = nullptr;
  if (FAILED(create_system_polygon(system_query_polygon.data(),
                                   system_query_polygon.size(),
                                   &system_query_boolean_path)) ||
      FAILED(create_system_polygon(system_boolean_polygon.data(),
                                   system_boolean_polygon.size(),
                                   &system_input_boolean_path)) ||
      system_query_boolean_path == nullptr ||
      system_input_boolean_path == nullptr) {
    if (system_query_boolean_path != nullptr) {
      system_query_boolean_path->Release();
    }
    if (system_input_boolean_path != nullptr) {
      system_input_boolean_path->Release();
    }
    system_factory->Release();
    return 325;
  }
  const std::array<ID2D1Geometry *, 6U> system_relation_inputs{{
      system_input_boolean_path,
      system_query_boolean_path,
      system_input_boolean_path,
      system_input_boolean_path,
      system_query_boolean_path,
      system_input_boolean_path,
  }};
  const std::array<const D2D1_MATRIX_3X2_F *, 6U> system_relation_transforms{{
      nullptr,
      nullptr,
      reinterpret_cast<const D2D1_MATRIX_3X2_F *>(&disjoint_path_transform),
      reinterpret_cast<const D2D1_MATRIX_3X2_F *>(&contained_path_transform),
      reinterpret_cast<const D2D1_MATRIX_3X2_F *>(&containing_path_transform),
      reinterpret_cast<const D2D1_MATRIX_3X2_F *>(&touching_path_transform),
  }};
  for (std::size_t case_index = 0U;
       case_index < path_relation_cases.size();
       ++case_index) {
    D2D1_GEOMETRY_RELATION system_relation =
        D2D1_GEOMETRY_RELATION_UNKNOWN;
    if (FAILED(system_query_boolean_path->CompareWithGeometry(
            system_relation_inputs[case_index],
            system_relation_transforms[case_index],
            D2D1_DEFAULT_FLATTENING_TOLERANCE, &system_relation)) ||
        static_cast<std::uint32_t>(system_relation) !=
            static_cast<std::uint32_t>(
                path_relation_cases[case_index].expected)) {
      system_query_boolean_path->Release();
      system_input_boolean_path->Release();
      system_factory->Release();
      return 329;
    }
  }
  for (std::size_t mode_index = 0U; mode_index < combination_modes.size();
       ++mode_index) {
    auto *raw_system_path_boolean_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> system_path_boolean_sink;
    system_path_boolean_sink.attach(raw_system_path_boolean_sink);
    auto *raw_portable_path_boolean_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> portable_path_boolean_sink;
    portable_path_boolean_sink.attach(raw_portable_path_boolean_sink);
    const HRESULT system_path_boolean_status =
        system_query_boolean_path->CombineWithGeometry(
            system_input_boolean_path,
            static_cast<D2D1_COMBINE_MODE>(mode_index), nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
                system_path_boolean_sink.get()));
    const com::result portable_path_boolean_status =
        query_path->CombineWithGeometry(boolean_path_base.get(),
                                        combination_modes[mode_index], nullptr,
                                        core::default_flattening_tolerance,
                                        portable_path_boolean_sink.get());
    if (FAILED(system_path_boolean_status) ||
        portable_path_boolean_status != com::ok) {
      system_query_boolean_path->Release();
      system_input_boolean_path->Release();
      system_factory->Release();
      return 326;
    }
    for (std::uint32_t y_index = 0U; y_index < 18U; ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 16U; ++x_index) {
        const compat::point_2f point{
            0.37F + static_cast<float>(x_index) * 0.47F,
            0.31F + static_cast<float>(y_index) * 0.53F};
        if (captured_fill_contains(*raw_system_path_boolean_sink, point) !=
            captured_fill_contains(*raw_portable_path_boolean_sink, point)) {
          system_query_boolean_path->Release();
          system_input_boolean_path->Release();
          system_factory->Release();
          return 327;
        }
      }
    }
  }
  system_query_boolean_path->Release();
  system_input_boolean_path->Release();

    ID2D1PathGeometry* system_arc_path = nullptr;
    ID2D1GeometrySink* system_arc_sink = nullptr;
    if (FAILED(system_factory->CreatePathGeometry(&system_arc_path)) ||
        system_arc_path == nullptr ||
        FAILED(system_arc_path->Open(&system_arc_sink)) ||
        system_arc_sink == nullptr) {
        if (system_arc_path != nullptr) {
            system_arc_path->Release();
        }
        system_factory->Release();
        return 51;
    }
    const D2D1_ARC_SEGMENT system_arc{
        D2D1_POINT_2F{2.0F, 0.0F},
        D2D1_SIZE_F{1.0F, 1.0F},
        0.0F,
        D2D1_SWEEP_DIRECTION_CLOCKWISE,
        D2D1_ARC_SIZE_SMALL};
    system_arc_sink->BeginFigure(
        D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_FILLED);
    system_arc_sink->AddArc(&system_arc);
    system_arc_sink->EndFigure(D2D1_FIGURE_END_OPEN);
    const HRESULT system_arc_close_status = system_arc_sink->Close();
    system_arc_sink->Release();
    D2D1_RECT_F system_arc_bounds{};
    const HRESULT system_arc_bounds_status =
        system_arc_path->GetBounds(nullptr, &system_arc_bounds);
    system_arc_path->Release();
    auto* portable_arc_path =
        reinterpret_cast<ID2D1PathGeometry*>(arc_path.get());
    D2D1_RECT_F portable_arc_bounds{};
    const HRESULT portable_arc_bounds_status =
        portable_arc_path->GetBounds(nullptr, &portable_arc_bounds);
    if (FAILED(system_arc_close_status) ||
        FAILED(system_arc_bounds_status) ||
        FAILED(portable_arc_bounds_status) ||
        !approximately_equal(
            system_arc_bounds.left, portable_arc_bounds.left) ||
        !approximately_equal(
            system_arc_bounds.top, portable_arc_bounds.top) ||
        !approximately_equal(
            system_arc_bounds.right, portable_arc_bounds.right) ||
        !approximately_equal(
            system_arc_bounds.bottom, portable_arc_bounds.bottom)) {
        system_factory->Release();
        return 52;
    }

    ID2D1EllipseGeometry* system_ellipse = nullptr;
    if (FAILED(system_factory->CreateEllipseGeometry(
            &native_ellipse_value, &system_ellipse)) ||
        system_ellipse == nullptr) {
        system_factory->Release();
        return 60;
    }
    D2D1_RECT_F system_ellipse_bounds{};
    BOOL system_ellipse_contains = FALSE;
    const HRESULT system_ellipse_bounds_status =
        system_ellipse->GetBounds(
            &native_ellipse_transform, &system_ellipse_bounds);
    const HRESULT system_ellipse_contains_status =
        system_ellipse->FillContainsPoint(
            D2D1_POINT_2F{2.75F, 0.25F},
            &native_ellipse_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &system_ellipse_contains);
    system_ellipse->Release();
    if (FAILED(system_ellipse_bounds_status) ||
        FAILED(system_ellipse_contains_status)) {
        system_factory->Release();
        return 61;
    }
    if (system_ellipse_contains != portable_ellipse_contains) {
        system_factory->Release();
        return 62;
    }
    if (!approximately_equal(
            system_ellipse_bounds.left, portable_ellipse_bounds.left)) {
        std::fprintf(
            stderr,
            "ellipse bounds system=(%.9g,%.9g,%.9g,%.9g) portable=(%.9g,%.9g,%.9g,%.9g)\n",
            system_ellipse_bounds.left,
            system_ellipse_bounds.top,
            system_ellipse_bounds.right,
            system_ellipse_bounds.bottom,
            portable_ellipse_bounds.left,
            portable_ellipse_bounds.top,
            portable_ellipse_bounds.right,
            portable_ellipse_bounds.bottom);
        system_factory->Release();
        return 63;
    }
    if (!approximately_equal(
            system_ellipse_bounds.top, portable_ellipse_bounds.top)) {
        system_factory->Release();
        return 64;
    }
    if (!approximately_equal(
            system_ellipse_bounds.right, portable_ellipse_bounds.right)) {
        system_factory->Release();
        return 65;
    }
    if (!approximately_equal(
            system_ellipse_bounds.bottom, portable_ellipse_bounds.bottom)) {
        system_factory->Release();
        return 66;
    }

    ID2D1RoundedRectangleGeometry* system_rounded_rectangle = nullptr;
    if (FAILED(system_factory->CreateRoundedRectangleGeometry(
            &native_rounded_rectangle_value, &system_rounded_rectangle)) ||
        system_rounded_rectangle == nullptr) {
        system_factory->Release();
        return 75;
    }
    D2D1_RECT_F system_rounded_rectangle_bounds{};
    BOOL system_rounded_rectangle_center_contains = FALSE;
    BOOL system_rounded_rectangle_corner_contains = TRUE;
    const HRESULT system_rounded_rectangle_bounds_status =
        system_rounded_rectangle->GetBounds(
            &native_rounded_rectangle_transform,
            &system_rounded_rectangle_bounds);
    const HRESULT system_rounded_rectangle_center_status =
        system_rounded_rectangle->FillContainsPoint(
            D2D1_POINT_2F{3.5F, 4.25F},
            &native_rounded_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &system_rounded_rectangle_center_contains);
    const HRESULT system_rounded_rectangle_corner_status =
        system_rounded_rectangle->FillContainsPoint(
            D2D1_POINT_2F{3.975F, -2.85F},
            &native_rounded_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &system_rounded_rectangle_corner_contains);
    system_rounded_rectangle->Release();
    if (FAILED(system_rounded_rectangle_bounds_status) ||
        FAILED(system_rounded_rectangle_center_status) ||
        FAILED(system_rounded_rectangle_corner_status) ||
        system_rounded_rectangle_center_contains !=
            portable_rounded_rectangle_center_contains ||
        system_rounded_rectangle_corner_contains !=
            portable_rounded_rectangle_corner_contains ||
        !approximately_equal(
            system_rounded_rectangle_bounds.left,
            portable_rounded_rectangle_bounds.left) ||
        !approximately_equal(
            system_rounded_rectangle_bounds.top,
            portable_rounded_rectangle_bounds.top) ||
        !approximately_equal(
            system_rounded_rectangle_bounds.right,
            portable_rounded_rectangle_bounds.right) ||
        !approximately_equal(
            system_rounded_rectangle_bounds.bottom,
            portable_rounded_rectangle_bounds.bottom)) {
        system_factory->Release();
        return 76;
    }

    const D2D1_RECT_F system_group_rectangle_value{
        1.0F, 2.0F, 5.0F, 8.0F};
    ID2D1RectangleGeometry* system_outline_rectangle = nullptr;
    ID2D1RectangleGeometry* system_group_rectangle = nullptr;
    ID2D1EllipseGeometry* system_group_ellipse = nullptr;
    if (FAILED(system_factory->CreateRectangleGeometry(
            &native_rectangle, &system_outline_rectangle)) ||
        system_outline_rectangle == nullptr ||
        FAILED(system_factory->CreateRectangleGeometry(
            &system_group_rectangle_value, &system_group_rectangle)) ||
        system_group_rectangle == nullptr ||
        FAILED(system_factory->CreateEllipseGeometry(
            &native_ellipse_value, &system_group_ellipse)) ||
        system_group_ellipse == nullptr) {
        if (system_outline_rectangle != nullptr) {
            system_outline_rectangle->Release();
        }
        if (system_group_rectangle != nullptr) {
            system_group_rectangle->Release();
        }
        if (system_group_ellipse != nullptr) {
            system_group_ellipse->Release();
        }
        system_factory->Release();
        return 84;
    }
    const std::array<D2D1_RECT_F, 6U> system_relation_rectangles{{
        system_group_rectangle_value,
        D2D1_RECT_F{2.0F, 3.0F, 4.0F, 7.0F},
        D2D1_RECT_F{0.0F, 1.0F, 6.0F, 9.0F},
        D2D1_RECT_F{4.0F, 7.0F, 7.0F, 10.0F},
        D2D1_RECT_F{6.0F, 9.0F, 7.0F, 10.0F},
        D2D1_RECT_F{5.0F, 2.0F, 6.0F, 8.0F},
    }};
    std::array<D2D1_GEOMETRY_RELATION, 6U> system_relations{};
    for (std::size_t relation_index = 0U;
         relation_index < system_relation_rectangles.size();
         ++relation_index) {
        ID2D1RectangleGeometry* relation_rectangle = nullptr;
        if (FAILED(system_factory->CreateRectangleGeometry(
                &system_relation_rectangles[relation_index],
                &relation_rectangle)) ||
            relation_rectangle == nullptr ||
            FAILED(system_group_rectangle->CompareWithGeometry(
                relation_rectangle,
                nullptr,
                D2D1_DEFAULT_FLATTENING_TOLERANCE,
                &system_relations[relation_index]))) {
            if (relation_rectangle != nullptr) {
                relation_rectangle->Release();
            }
            system_group_rectangle->Release();
            system_group_ellipse->Release();
            system_factory->Release();
            return 274;
        }
        relation_rectangle->Release();
    }
    for (std::size_t relation_index = 0U;
         relation_index < system_relations.size();
         ++relation_index) {
        if (static_cast<std::uint32_t>(system_relations[relation_index]) !=
            static_cast<std::uint32_t>(portable_relations[relation_index])) {
            system_group_rectangle->Release();
            system_group_ellipse->Release();
            system_factory->Release();
            return 279;
        }
    }
    ID2D1RectangleGeometry* system_translated_relation_rectangle = nullptr;
    D2D1_GEOMETRY_RELATION system_translated_relation =
        D2D1_GEOMETRY_RELATION_UNKNOWN;
    const D2D1_MATRIX_3X2_F system_relation_transform = make_native_matrix(
        relation_transform.m11,
        relation_transform.m12,
        relation_transform.m21,
        relation_transform.m22,
        relation_transform.m31,
        relation_transform.m32);
    if (FAILED(system_factory->CreateRectangleGeometry(
            &system_relation_rectangles[4U],
            &system_translated_relation_rectangle)) ||
        system_translated_relation_rectangle == nullptr ||
        FAILED(system_group_rectangle->CompareWithGeometry(
            system_translated_relation_rectangle,
            &system_relation_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &system_translated_relation))) {
        if (system_translated_relation_rectangle != nullptr) {
            system_translated_relation_rectangle->Release();
        }
        system_group_rectangle->Release();
        system_group_ellipse->Release();
        system_factory->Release();
        return 280;
    }
    system_translated_relation_rectangle->Release();
    if (static_cast<std::uint32_t>(system_translated_relation) !=
        static_cast<std::uint32_t>(translated_relation)) {
        system_group_rectangle->Release();
        system_group_ellipse->Release();
        system_factory->Release();
        return 281;
    }
    const D2D1_MATRIX_3X2_F system_general_relation_transform =
        make_native_matrix(
            general_relation_transform.m11,
            general_relation_transform.m12,
            general_relation_transform.m21,
            general_relation_transform.m22,
            general_relation_transform.m31,
            general_relation_transform.m32);
    const D2D1_MATRIX_3X2_F system_reflected_relation_transform =
        make_native_matrix(
            reflected_relation_transform.m11,
            reflected_relation_transform.m12,
            reflected_relation_transform.m21,
            reflected_relation_transform.m22,
            reflected_relation_transform.m31,
            reflected_relation_transform.m32);
    const auto compare_system_rectangle = [system_factory,
                                            system_group_rectangle](
        const D2D1_RECT_F& value,
        const D2D1_MATRIX_3X2_F* transform,
        compat::geometry_relation expected) {
        ID2D1RectangleGeometry* candidate = nullptr;
        D2D1_GEOMETRY_RELATION relation = D2D1_GEOMETRY_RELATION_UNKNOWN;
        const HRESULT create_status = system_factory->CreateRectangleGeometry(
            &value, &candidate);
        const HRESULT compare_status = candidate == nullptr
            ? create_status
            : system_group_rectangle->CompareWithGeometry(
                  candidate,
                  transform,
                  D2D1_DEFAULT_FLATTENING_TOLERANCE,
                  &relation);
        if (candidate != nullptr) {
            candidate->Release();
        }
        return SUCCEEDED(create_status) && SUCCEEDED(compare_status) &&
            static_cast<std::uint32_t>(relation) ==
                static_cast<std::uint32_t>(expected);
    };
    ID2D1TransformedGeometry* system_sheared_source = nullptr;
    D2D1_GEOMETRY_RELATION system_sheared_source_relation =
        D2D1_GEOMETRY_RELATION_UNKNOWN;
    const HRESULT system_sheared_source_create_status =
        system_factory->CreateTransformedGeometry(
            system_group_rectangle,
            &system_general_relation_transform,
            &system_sheared_source);
    const HRESULT system_sheared_source_compare_status =
        system_sheared_source == nullptr
        ? system_sheared_source_create_status
        : system_sheared_source->CompareWithGeometry(
              system_group_rectangle,
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &system_sheared_source_relation);
    if (!compare_system_rectangle(
            system_relation_rectangles[4U],
            &system_general_relation_transform,
            general_relation) ||
        !compare_system_rectangle(
            system_relation_rectangles[1U],
            &system_general_relation_transform,
            shear_overlap_relation) ||
        !compare_system_rectangle(
            system_group_rectangle_value,
            &system_reflected_relation_transform,
            reflected_relation) ||
        FAILED(system_sheared_source_create_status) ||
        FAILED(system_sheared_source_compare_status) ||
        static_cast<std::uint32_t>(system_sheared_source_relation) !=
            static_cast<std::uint32_t>(sheared_source_relation)) {
        if (system_sheared_source != nullptr) {
            system_sheared_source->Release();
        }
        system_group_rectangle->Release();
        system_group_ellipse->Release();
        system_factory->Release();
        return 297;
    }
    ID2D1RectangleGeometry* system_shear_overlap_rectangle = nullptr;
    if (FAILED(system_factory->CreateRectangleGeometry(
            &system_relation_rectangles[1U],
            &system_shear_overlap_rectangle)) ||
        system_shear_overlap_rectangle == nullptr ||
        system_sheared_source == nullptr) {
        if (system_shear_overlap_rectangle != nullptr) {
            system_shear_overlap_rectangle->Release();
        }
        if (system_sheared_source != nullptr) {
            system_sheared_source->Release();
        }
        system_group_rectangle->Release();
        system_group_ellipse->Release();
        system_factory->Release();
        return 302;
    }
    const D2D1_MATRIX_3X2_F system_affine_source_candidate_transform =
        make_native_matrix(
            affine_source_candidate_transform.m11,
            affine_source_candidate_transform.m12,
            affine_source_candidate_transform.m21,
            affine_source_candidate_transform.m22,
            affine_source_candidate_transform.m31,
            affine_source_candidate_transform.m32);
    for (std::size_t mode_index = 0U;
         mode_index < combination_modes.size();
         ++mode_index) {
        auto* raw_system_affine_sink = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink> system_affine_sink;
        system_affine_sink.attach(raw_system_affine_sink);
        auto* raw_portable_affine_sink = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink> portable_affine_sink;
        portable_affine_sink.attach(raw_portable_affine_sink);
        const HRESULT system_affine_status =
            system_group_rectangle->CombineWithGeometry(
                system_shear_overlap_rectangle,
                static_cast<D2D1_COMBINE_MODE>(mode_index),
                &system_general_relation_transform,
                D2D1_DEFAULT_FLATTENING_TOLERANCE,
                reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                    system_affine_sink.get()));
        const com::result portable_affine_status =
            geometry->CombineWithGeometry(
                shear_overlap_rectangle.get(),
                combination_modes[mode_index],
                &general_relation_transform,
                core::default_flattening_tolerance,
                portable_affine_sink.get());
        auto* raw_system_affine_source_sink = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink>
            system_affine_source_sink;
        system_affine_source_sink.attach(raw_system_affine_source_sink);
        auto* raw_portable_affine_source_sink = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink>
            portable_affine_source_sink;
        portable_affine_source_sink.attach(
            raw_portable_affine_source_sink);
        const HRESULT system_affine_source_status =
            system_sheared_source->CombineWithGeometry(
                system_group_rectangle,
                static_cast<D2D1_COMBINE_MODE>(mode_index),
                &system_affine_source_candidate_transform,
                D2D1_DEFAULT_FLATTENING_TOLERANCE,
                reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                    system_affine_source_sink.get()));
        const com::result portable_affine_source_status =
            sheared_source->CombineWithGeometry(
                geometry_base.get(),
                combination_modes[mode_index],
                &affine_source_candidate_transform,
                core::default_flattening_tolerance,
                portable_affine_source_sink.get());
        if (FAILED(system_affine_status) ||
            portable_affine_status != com::ok ||
            FAILED(system_affine_source_status) ||
            portable_affine_source_status != com::ok) {
            system_shear_overlap_rectangle->Release();
            system_sheared_source->Release();
            system_group_rectangle->Release();
            system_group_ellipse->Release();
            system_factory->Release();
            return 303;
        }
        for (std::size_t probe_index = 0U;
             probe_index < affine_combination_probes.size();
             ++probe_index) {
            const bool expected =
                affine_combination_expected[mode_index][probe_index];
            if (captured_fill_contains(
                    *raw_system_affine_sink,
                    affine_combination_probes[probe_index]) != expected ||
                captured_fill_contains(
                    *raw_portable_affine_sink,
                    affine_combination_probes[probe_index]) != expected ||
                captured_fill_contains(
                    *raw_system_affine_source_sink,
                    affine_source_combination_probes[probe_index]) !=
                    expected ||
                captured_fill_contains(
                    *raw_portable_affine_source_sink,
                    affine_source_combination_probes[probe_index]) !=
                    expected) {
                system_shear_overlap_rectangle->Release();
                system_sheared_source->Release();
                system_group_rectangle->Release();
                system_group_ellipse->Release();
                system_factory->Release();
                return 304;
            }
        }
    }
    for (const compat::rectangle_f& affine_collinear_rectangle :
         affine_collinear_rectangles) {
        const D2D1_RECT_F system_affine_collinear_rectangle{
            affine_collinear_rectangle.left,
            affine_collinear_rectangle.top,
            affine_collinear_rectangle.right,
            affine_collinear_rectangle.bottom};
        ID2D1RectangleGeometry* system_affine_collinear_geometry = nullptr;
        compat::rectangle_geometry* raw_portable_affine_collinear_geometry =
            nullptr;
        if (FAILED(system_factory->CreateRectangleGeometry(
                &system_affine_collinear_rectangle,
                &system_affine_collinear_geometry)) ||
            system_affine_collinear_geometry == nullptr ||
            factory->CreateRectangleGeometry(
                &affine_collinear_rectangle,
                &raw_portable_affine_collinear_geometry) != com::ok ||
            raw_portable_affine_collinear_geometry == nullptr) {
            if (system_affine_collinear_geometry != nullptr) {
                system_affine_collinear_geometry->Release();
            }
            system_shear_overlap_rectangle->Release();
            system_sheared_source->Release();
            system_group_rectangle->Release();
            system_group_ellipse->Release();
            system_factory->Release();
            return 312;
        }
        com::pointer<compat::rectangle_geometry>
            portable_affine_collinear_geometry;
        portable_affine_collinear_geometry.attach(
            raw_portable_affine_collinear_geometry);
        for (std::size_t mode_index = 0U;
             mode_index < combination_modes.size();
             ++mode_index) {
            auto* raw_system_affine_collinear_sink = new simplified_sink();
            com::pointer<compat::simplified_geometry_sink>
                system_affine_collinear_sink;
            system_affine_collinear_sink.attach(
                raw_system_affine_collinear_sink);
            auto* raw_portable_affine_collinear_sink = new simplified_sink();
            com::pointer<compat::simplified_geometry_sink>
                portable_affine_collinear_sink;
            portable_affine_collinear_sink.attach(
                raw_portable_affine_collinear_sink);
            const HRESULT system_affine_collinear_status =
                system_sheared_source->CombineWithGeometry(
                    system_affine_collinear_geometry,
                    static_cast<D2D1_COMBINE_MODE>(mode_index),
                    &system_general_relation_transform,
                    D2D1_DEFAULT_FLATTENING_TOLERANCE,
                    reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                        system_affine_collinear_sink.get()));
            const com::result portable_affine_collinear_status =
                sheared_source->CombineWithGeometry(
                    portable_affine_collinear_geometry.get(),
                    combination_modes[mode_index],
                    &general_relation_transform,
                    core::default_flattening_tolerance,
                    portable_affine_collinear_sink.get());
            if (FAILED(system_affine_collinear_status) ||
                portable_affine_collinear_status != com::ok) {
                system_affine_collinear_geometry->Release();
                system_shear_overlap_rectangle->Release();
                system_sheared_source->Release();
                system_group_rectangle->Release();
                system_group_ellipse->Release();
                system_factory->Release();
                return 313;
            }
            for (std::uint32_t y_index = 0U; y_index < 19U; ++y_index) {
                for (std::uint32_t x_index = 0U; x_index < 19U; ++x_index) {
                    const float local_x =
                        0.73F + static_cast<float>(x_index) * 0.41F;
                    const float local_y =
                        1.17F + static_cast<float>(y_index) * 0.43F;
                    const compat::point_2f point{
                        local_x * general_relation_transform.m11 +
                            local_y * general_relation_transform.m21 +
                            general_relation_transform.m31,
                        local_x * general_relation_transform.m12 +
                            local_y * general_relation_transform.m22 +
                            general_relation_transform.m32};
                    if (captured_fill_contains(
                            *raw_system_affine_collinear_sink, point) !=
                        captured_fill_contains(
                            *raw_portable_affine_collinear_sink, point)) {
                        system_affine_collinear_geometry->Release();
                        system_shear_overlap_rectangle->Release();
                        system_sheared_source->Release();
                        system_group_rectangle->Release();
                        system_group_ellipse->Release();
                        system_factory->Release();
                        return 314;
                    }
                }
            }
        }
        system_affine_collinear_geometry->Release();
    }
    system_shear_overlap_rectangle->Release();
    system_sheared_source->Release();
    ID2D1RectangleGeometry* system_combination_rectangle = nullptr;
    if (FAILED(system_factory->CreateRectangleGeometry(
            &system_relation_rectangles[3U],
            &system_combination_rectangle)) ||
        system_combination_rectangle == nullptr) {
        if (system_combination_rectangle != nullptr) {
            system_combination_rectangle->Release();
        }
        system_group_rectangle->Release();
        system_group_ellipse->Release();
        system_factory->Release();
        return 288;
    }
    compat::rectangle_geometry* raw_portable_combination_rectangle = nullptr;
    if (factory->CreateRectangleGeometry(
            &relation_rectangles[3U],
            &raw_portable_combination_rectangle) != com::ok ||
        raw_portable_combination_rectangle == nullptr) {
        system_combination_rectangle->Release();
        system_group_rectangle->Release();
        system_group_ellipse->Release();
        system_factory->Release();
        return 290;
    }
    com::pointer<compat::rectangle_geometry>
        portable_combination_rectangle;
    portable_combination_rectangle.attach(raw_portable_combination_rectangle);
    for (std::uint32_t mode_index = 0U; mode_index < 4U; ++mode_index) {
        auto* raw_system_combination_sink = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink>
            system_combination_sink;
        system_combination_sink.attach(raw_system_combination_sink);
        const HRESULT system_combination_status =
            system_group_rectangle->CombineWithGeometry(
                system_combination_rectangle,
                static_cast<D2D1_COMBINE_MODE>(mode_index),
                nullptr,
                D2D1_DEFAULT_FLATTENING_TOLERANCE,
                reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                    system_combination_sink.get()));
        auto* raw_portable_combination_sink = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink>
            portable_combination_sink;
        portable_combination_sink.attach(raw_portable_combination_sink);
        const com::result portable_combination_status =
            geometry->CombineWithGeometry(
                portable_combination_rectangle.get(),
                static_cast<compat::combine_mode>(mode_index),
                nullptr,
                core::default_flattening_tolerance,
                portable_combination_sink.get());
        if (FAILED(system_combination_status) ||
            portable_combination_status != com::ok ||
            !captured_boundaries_match(
                *raw_system_combination_sink,
                *raw_portable_combination_sink)) {
            system_combination_rectangle->Release();
            system_group_rectangle->Release();
            system_group_ellipse->Release();
            system_factory->Release();
            return 291;
        }
    }
    system_combination_rectangle->Release();
    auto* raw_system_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> system_outline_sink;
    system_outline_sink.attach(raw_system_outline_sink);
    const D2D1_MATRIX_3X2_F system_rectangle_transform =
        make_native_matrix(2.0F, 0.0F, 0.0F, 3.0F, 10.0F, -4.0F);
    D2D1_RECT_F system_widened_bounds{};
    const HRESULT system_widened_status =
        system_group_rectangle->GetWidenedBounds(
            2.0F,
            nullptr,
            &system_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &system_widened_bounds);
    BOOL system_stroke_edge_contains = FALSE;
    BOOL system_stroke_center_contains = TRUE;
    const HRESULT system_stroke_edge_status =
        system_group_rectangle->StrokeContainsPoint(
            D2D1_POINT_2F{11.0F, 10.0F},
            2.0F,
            nullptr,
            &system_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &system_stroke_edge_contains);
    const HRESULT system_stroke_center_status =
        system_group_rectangle->StrokeContainsPoint(
            D2D1_POINT_2F{16.0F, 10.0F},
            2.0F,
            nullptr,
            &system_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &system_stroke_center_contains);
    const D2D1_MATRIX_3X2_F system_local_transform = make_native_matrix(
        2.0F, 0.0F, 0.0F, 0.5F, 5.0F, 7.0F);
    ID2D1TransformedGeometry* system_transformed_rectangle = nullptr;
    const HRESULT system_transformed_create_status =
        system_factory->CreateTransformedGeometry(
            system_group_rectangle,
            &system_local_transform,
            &system_transformed_rectangle);
    D2D1_RECT_F system_transformed_widened_bounds{};
    const HRESULT system_transformed_widened_status =
        FAILED(system_transformed_create_status) ||
            system_transformed_rectangle == nullptr
        ? system_transformed_create_status
        : system_transformed_rectangle->GetWidenedBounds(
              2.0F,
              nullptr,
              &system_rectangle_transform,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &system_transformed_widened_bounds);
    BOOL system_transformed_stroke_edge_contains = FALSE;
    BOOL system_transformed_stroke_center_contains = TRUE;
    const HRESULT system_transformed_stroke_edge_status =
        system_transformed_rectangle == nullptr
        ? system_transformed_create_status
        : system_transformed_rectangle->StrokeContainsPoint(
              D2D1_POINT_2F{23.0F, 24.0F},
              2.0F,
              nullptr,
              &system_rectangle_transform,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &system_transformed_stroke_edge_contains);
    const HRESULT system_transformed_stroke_center_status =
        system_transformed_rectangle == nullptr
        ? system_transformed_create_status
        : system_transformed_rectangle->StrokeContainsPoint(
              D2D1_POINT_2F{30.0F, 24.0F},
              2.0F,
              nullptr,
              &system_rectangle_transform,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &system_transformed_stroke_center_contains);
    auto* raw_system_widen_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> system_widen_sink;
    system_widen_sink.attach(raw_system_widen_sink);
    const HRESULT system_widen_status = system_group_rectangle->Widen(
        2.0F,
        nullptr,
        &system_rectangle_transform,
        D2D1_DEFAULT_FLATTENING_TOLERANCE,
        reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
            system_widen_sink.get()));
    auto* raw_system_transformed_widen_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        system_transformed_widen_sink;
    system_transformed_widen_sink.attach(
        raw_system_transformed_widen_sink);
    const HRESULT system_transformed_widen_status =
        system_transformed_rectangle == nullptr
        ? system_transformed_create_status
        : system_transformed_rectangle->Widen(
              2.0F,
              nullptr,
              &system_rectangle_transform,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                  system_transformed_widen_sink.get()));
    D2D1_GEOMETRY_RELATION system_transformed_candidate_relation =
        D2D1_GEOMETRY_RELATION_UNKNOWN;
    D2D1_GEOMETRY_RELATION system_transformed_source_relation =
        D2D1_GEOMETRY_RELATION_UNKNOWN;
    const HRESULT system_transformed_candidate_relation_status =
        system_transformed_rectangle == nullptr
        ? system_transformed_create_status
        : system_group_rectangle->CompareWithGeometry(
              system_transformed_rectangle,
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &system_transformed_candidate_relation);
    const HRESULT system_transformed_source_relation_status =
        system_transformed_rectangle == nullptr
        ? system_transformed_create_status
        : system_transformed_rectangle->CompareWithGeometry(
              system_group_rectangle,
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &system_transformed_source_relation);
    if (system_transformed_rectangle != nullptr) {
        system_transformed_rectangle->Release();
    }
    const HRESULT system_outline_status = system_outline_rectangle->Outline(
        nullptr,
        D2D1_DEFAULT_FLATTENING_TOLERANCE,
        reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
            system_outline_sink.get()));
    system_outline_rectangle->Release();
    if (FAILED(system_widened_status) ||
        !approximately_equal(
            system_widened_bounds.left, widened_bounds.left) ||
        !approximately_equal(
            system_widened_bounds.top, widened_bounds.top) ||
        !approximately_equal(
            system_widened_bounds.right, widened_bounds.right) ||
        !approximately_equal(
            system_widened_bounds.bottom, widened_bounds.bottom) ||
        FAILED(system_stroke_edge_status) ||
        FAILED(system_stroke_center_status) ||
        system_stroke_edge_contains != stroke_edge_contains ||
        system_stroke_center_contains != stroke_center_contains ||
        FAILED(system_transformed_widened_status) ||
        !approximately_equal(
            system_transformed_widened_bounds.left,
            transformed_widened_bounds.left) ||
        !approximately_equal(
            system_transformed_widened_bounds.top,
            transformed_widened_bounds.top) ||
        !approximately_equal(
            system_transformed_widened_bounds.right,
            transformed_widened_bounds.right) ||
        !approximately_equal(
            system_transformed_widened_bounds.bottom,
            transformed_widened_bounds.bottom) ||
        FAILED(system_transformed_stroke_edge_status) ||
        FAILED(system_transformed_stroke_center_status) ||
        system_transformed_stroke_edge_contains !=
            transformed_stroke_edge_contains ||
        system_transformed_stroke_center_contains !=
            transformed_stroke_center_contains ||
        FAILED(system_transformed_candidate_relation_status) ||
        FAILED(system_transformed_source_relation_status) ||
        static_cast<std::uint32_t>(system_transformed_candidate_relation) !=
            static_cast<std::uint32_t>(transformed_candidate_relation) ||
        static_cast<std::uint32_t>(system_transformed_source_relation) !=
            static_cast<std::uint32_t>(transformed_source_relation) ||
        FAILED(system_outline_status) ||
        raw_system_outline_sink->fill_mode !=
            raw_native_outline_sink->fill_mode ||
        raw_system_outline_sink->figure_begin !=
            raw_native_outline_sink->figure_begin ||
        raw_system_outline_sink->figure_end !=
            raw_native_outline_sink->figure_end ||
        raw_system_outline_sink->begin_count !=
            raw_native_outline_sink->begin_count ||
        raw_system_outline_sink->end_count !=
            raw_native_outline_sink->end_count ||
        raw_system_outline_sink->line_count !=
            raw_native_outline_sink->line_count ||
        raw_system_outline_sink->line_point_count !=
            raw_native_outline_sink->line_point_count ||
        !std::equal(
            raw_system_outline_sink->line_points.begin(),
            raw_system_outline_sink->line_points.begin() +
                static_cast<std::ptrdiff_t>(
                    raw_system_outline_sink->line_point_count),
            raw_native_outline_sink->line_points.begin(),
            [](compat::point_2f left, compat::point_2f right) {
                return approximately_equal(left.x, right.x) &&
                    approximately_equal(left.y, right.y);
            })) {
        system_group_rectangle->Release();
        system_group_ellipse->Release();
        system_factory->Release();
        return 264;
    }
    const auto widen_sinks_match = [](
        const simplified_sink& system,
        const simplified_sink& portable) {
        if (system.begin_count > system.begin_points.size() ||
            portable.begin_count > portable.begin_points.size() ||
            system.end_count > system.figure_ends.size() ||
            portable.end_count > portable.figure_ends.size()) {
            return false;
        }
        return system.fill_mode == portable.fill_mode &&
            system.segment_flags == portable.segment_flags &&
            system.begin_count == portable.begin_count &&
            system.end_count == portable.end_count &&
            system.line_count == portable.line_count &&
            system.line_point_count == portable.line_point_count &&
            std::equal(
                system.begin_points.begin(),
                system.begin_points.begin() +
                    static_cast<std::ptrdiff_t>(system.begin_count),
                portable.begin_points.begin(),
                [](compat::point_2f left, compat::point_2f right) {
                    return approximately_equal(left.x, right.x) &&
                        approximately_equal(left.y, right.y);
                }) &&
            std::equal(
                system.figure_begins.begin(),
                system.figure_begins.begin() +
                    static_cast<std::ptrdiff_t>(system.begin_count),
                portable.figure_begins.begin()) &&
            std::equal(
                system.figure_ends.begin(),
                system.figure_ends.begin() +
                    static_cast<std::ptrdiff_t>(system.end_count),
                portable.figure_ends.begin()) &&
            std::equal(
                system.line_points.begin(),
                system.line_points.begin() +
                    static_cast<std::ptrdiff_t>(system.line_point_count),
                portable.line_points.begin(),
                [](compat::point_2f left, compat::point_2f right) {
                    return approximately_equal(left.x, right.x) &&
                        approximately_equal(left.y, right.y);
                });
    };
    if (FAILED(system_widen_status) ||
        FAILED(system_transformed_widen_status) ||
        !widen_sinks_match(*raw_system_widen_sink, *raw_widen_sink) ||
        !widen_sinks_match(
            *raw_system_transformed_widen_sink,
            *raw_transformed_widen_sink)) {
        std::fprintf(
            stderr,
            "widen oracle base=%d/%u/%u/%u fill=%u transformed=%d/%u/%u/%u fill=%u\n",
            static_cast<int>(system_widen_status),
            raw_system_widen_sink->begin_count,
            raw_system_widen_sink->end_count,
            raw_system_widen_sink->line_count,
            static_cast<unsigned>(raw_system_widen_sink->fill_mode),
            static_cast<int>(system_transformed_widen_status),
            raw_system_transformed_widen_sink->begin_count,
            raw_system_transformed_widen_sink->end_count,
            raw_system_transformed_widen_sink->line_count,
            static_cast<unsigned>(
                raw_system_transformed_widen_sink->fill_mode));
        const auto print_widen_sink = [](const char* name,
                                         const simplified_sink& sink) {
            for (std::size_t index = 0U;
                 index < sink.begin_count && index < sink.begin_points.size();
                 ++index) {
                std::fprintf(
                    stderr,
                    "%s begin[%zu]=%.9g,%.9g kind=%u flags=%u\n",
                    name,
                    index,
                    static_cast<double>(sink.begin_points[index].x),
                    static_cast<double>(sink.begin_points[index].y),
                    static_cast<unsigned>(sink.figure_begins[index]),
                    static_cast<unsigned>(sink.segment_flags));
            }
            for (std::size_t index = 0U;
                 index < sink.end_count && index < sink.figure_ends.size();
                 ++index) {
                std::fprintf(
                    stderr,
                    "%s end[%zu]=%u\n",
                    name,
                    index,
                    static_cast<unsigned>(sink.figure_ends[index]));
            }
            for (std::size_t index = 0U;
                 index < sink.line_point_count;
                 ++index) {
                std::fprintf(
                    stderr,
                    "%s line[%zu]=%.9g,%.9g\n",
                    name,
                    index,
                    static_cast<double>(sink.line_points[index].x),
                    static_cast<double>(sink.line_points[index].y));
            }
        };
        print_widen_sink("system-base", *raw_system_widen_sink);
        print_widen_sink("portable-base", *raw_widen_sink);
        print_widen_sink(
            "system-transformed", *raw_system_transformed_widen_sink);
        print_widen_sink(
            "portable-transformed", *raw_transformed_widen_sink);
        system_group_rectangle->Release();
        system_group_ellipse->Release();
        system_factory->Release();
        return 273;
    }
    std::array<ID2D1Geometry*, 2U> system_group_sources{
        system_group_rectangle, system_group_ellipse};
    auto* raw_system_direct_ellipse_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        system_direct_ellipse_simplified;
    system_direct_ellipse_simplified.attach(
        raw_system_direct_ellipse_simplified);
    const HRESULT system_direct_ellipse_simplify_status =
        system_group_ellipse->Simplify(
            D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES,
            &native_ellipse_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_direct_ellipse_simplified.get()));
    ID2D1GeometryGroup* system_group = nullptr;
    const HRESULT system_group_create_status =
        system_factory->CreateGeometryGroup(
            D2D1_FILL_MODE_ALTERNATE,
            system_group_sources.data(),
            static_cast<UINT32>(system_group_sources.size()),
            &system_group);
    system_group_rectangle->Release();
    system_group_ellipse->Release();
    if (FAILED(system_direct_ellipse_simplify_status) ||
        raw_system_direct_ellipse_simplified->line_count != 3U ||
        raw_system_direct_ellipse_simplified->bezier_count != 4U ||
        FAILED(system_group_create_status) || system_group == nullptr) {
        system_factory->Release();
        return 85;
    }
    D2D1_RECT_F system_group_bounds{};
    const HRESULT system_group_bounds_status = system_group->GetBounds(
        &native_ellipse_transform, &system_group_bounds);
    const bool system_group_metadata_matches =
        system_group->GetFillMode() == D2D1_FILL_MODE_ALTERNATE &&
        system_group->GetSourceGeometryCount() ==
            static_cast<UINT32>(system_group_sources.size());
    std::array<ID2D1Geometry*, 1U> system_nested_group_sources{
        system_group};
    ID2D1GeometryGroup* system_nested_group = nullptr;
    const HRESULT system_nested_group_create_status =
        system_factory->CreateGeometryGroup(
            D2D1_FILL_MODE_WINDING,
            system_nested_group_sources.data(),
            static_cast<UINT32>(system_nested_group_sources.size()),
            &system_nested_group);
    D2D1_RECT_F system_nested_group_bounds{};
    BOOL system_nested_group_contains = FALSE;
    auto* raw_system_nested_group_simplified = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        system_nested_group_simplified;
    system_nested_group_simplified.attach(
        raw_system_nested_group_simplified);
    const HRESULT system_nested_group_bounds_status =
        system_nested_group == nullptr
        ? E_FAIL
        : system_nested_group->GetBounds(
              &native_ellipse_transform, &system_nested_group_bounds);
    const HRESULT system_nested_group_contains_status =
        system_nested_group == nullptr
        ? E_FAIL
        : system_nested_group->FillContainsPoint(
              D2D1_POINT_2F{2.75F, 0.25F},
              &native_ellipse_transform,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &system_nested_group_contains);
    const HRESULT system_nested_group_simplify_status =
        system_nested_group == nullptr
        ? E_FAIL
        : system_nested_group->Simplify(
              D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES,
              &native_ellipse_transform,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                  system_nested_group_simplified.get()));
    if (system_nested_group != nullptr) {
        system_nested_group->Release();
    }
    system_group->Release();
    if (FAILED(system_group_bounds_status) ||
        !system_group_metadata_matches ||
        !approximately_equal(
            system_group_bounds.left, portable_group_bounds.left) ||
        !approximately_equal(
            system_group_bounds.top, portable_group_bounds.top) ||
        !approximately_equal(
            system_group_bounds.right, portable_group_bounds.right) ||
        !approximately_equal(
            system_group_bounds.bottom, portable_group_bounds.bottom)) {
        system_factory->Release();
        return 86;
    }
    if (FAILED(system_nested_group_create_status) ||
        FAILED(system_nested_group_bounds_status) ||
        FAILED(system_nested_group_contains_status) ||
        FAILED(system_nested_group_simplify_status) ||
        static_cast<std::int32_t>(system_nested_group_contains) !=
            static_cast<std::int32_t>(
                portable_native_nested_group_contains) ||
        raw_system_nested_group_simplified->fill_mode !=
            raw_portable_native_nested_group_simplified->fill_mode ||
        raw_system_nested_group_simplified->begin_count !=
            raw_portable_native_nested_group_simplified->begin_count ||
        raw_system_nested_group_simplified->end_count !=
            raw_portable_native_nested_group_simplified->end_count ||
        raw_system_nested_group_simplified->line_count !=
            raw_portable_native_nested_group_simplified->line_count ||
        raw_system_nested_group_simplified->bezier_count !=
            raw_portable_native_nested_group_simplified->bezier_count ||
        !approximately_equal(
            system_nested_group_bounds.left,
            portable_native_nested_group_bounds.left) ||
        !approximately_equal(
            system_nested_group_bounds.top,
            portable_native_nested_group_bounds.top) ||
        !approximately_equal(
            system_nested_group_bounds.right,
            portable_native_nested_group_bounds.right) ||
        !approximately_equal(
            system_nested_group_bounds.bottom,
            portable_native_nested_group_bounds.bottom)) {
        system_factory->Release();
        return 316;
    }

    ID2D1StrokeStyle* system_stroke_style = nullptr;
    if (FAILED(system_factory->CreateStrokeStyle(
            &native_stroke_properties,
            native_stroke_dashes.data(),
            static_cast<UINT32>(native_stroke_dashes.size()),
            &system_stroke_style)) ||
        system_stroke_style == nullptr) {
        system_factory->Release();
        return 98;
    }
    std::array<float, 4U> system_stroke_dashes{};
    system_stroke_style->GetDashes(
        system_stroke_dashes.data(),
        static_cast<UINT32>(system_stroke_dashes.size()));
    const bool system_stroke_matches =
        system_stroke_style->GetStartCap() == D2D1_CAP_STYLE_ROUND &&
        system_stroke_style->GetEndCap() == D2D1_CAP_STYLE_SQUARE &&
        system_stroke_style->GetDashCap() == D2D1_CAP_STYLE_TRIANGLE &&
        system_stroke_style->GetLineJoin() == D2D1_LINE_JOIN_BEVEL &&
        approximately_equal(system_stroke_style->GetMiterLimit(), 4.0F) &&
        approximately_equal(system_stroke_style->GetDashOffset(), 0.5F) &&
        system_stroke_style->GetDashStyle() == D2D1_DASH_STYLE_CUSTOM &&
        system_stroke_style->GetDashesCount() ==
            static_cast<UINT32>(native_stroke_dashes.size()) &&
        system_stroke_dashes == portable_native_stroke_dashes;
    system_stroke_style->Release();
    if (!system_stroke_matches) {
        system_factory->Release();
        return 99;
    }

    ID2D1DrawingStateBlock* system_drawing_state = nullptr;
    if (FAILED(system_factory->CreateDrawingStateBlock(
            &native_drawing_state_description,
            nullptr,
            &system_drawing_state)) ||
        system_drawing_state == nullptr) {
        system_factory->Release();
        return 109;
    }
    D2D1_DRAWING_STATE_DESCRIPTION system_drawing_state_description{};
    system_drawing_state->GetDescription(
        &system_drawing_state_description);
    IDWriteRenderingParams* system_text_parameters =
        reinterpret_cast<IDWriteRenderingParams*>(
            static_cast<std::uintptr_t>(1U));
    system_drawing_state->GetTextRenderingParams(&system_text_parameters);
    system_drawing_state->Release();
    system_factory->Release();
    if (system_drawing_state_description.antialiasMode !=
            portable_native_drawing_state.antialiasMode ||
        system_drawing_state_description.textAntialiasMode !=
            portable_native_drawing_state.textAntialiasMode ||
        system_drawing_state_description.tag1 !=
            portable_native_drawing_state.tag1 ||
        system_drawing_state_description.tag2 !=
            portable_native_drawing_state.tag2 ||
        !approximately_equal(
            system_drawing_state_description.transform._11,
            portable_native_drawing_state.transform._11) ||
        !approximately_equal(
            system_drawing_state_description.transform._12,
            portable_native_drawing_state.transform._12) ||
        !approximately_equal(
            system_drawing_state_description.transform._21,
            portable_native_drawing_state.transform._21) ||
        !approximately_equal(
            system_drawing_state_description.transform._22,
            portable_native_drawing_state.transform._22) ||
        !approximately_equal(
            system_drawing_state_description.transform._31,
            portable_native_drawing_state.transform._31) ||
        !approximately_equal(
            system_drawing_state_description.transform._32,
            portable_native_drawing_state.transform._32) ||
        system_text_parameters != nullptr) {
        return 110;
    }
#endif
    return 0;
}

int main()
{
    const int result = run_tests();
    if (result != 0) {
        std::fprintf(
            stderr,
            "portable Direct2D compatibility failure checkpoint: %d\n",
            result);
    }
    return result;
}
