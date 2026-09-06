#include "progpu_native_direct2d_compat.hpp"
#include "progpu_native_direct2d_scene_submission.hpp"
#include "progpu_native.h"
#include "../src/Direct2D/progpu_native_direct2d_path.hpp"

#if defined(_WIN32)
#  include <dwrite.h>
#  include <d2d1_1.h>
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
#include <span>
#include <vector>

namespace compat = progpu::native::direct2d::compat;
namespace core = progpu::native::direct2d::core;
namespace com = progpu::native::com;

#if defined(_WIN32)
static_assert(compat::render_target_has_layer_or_cliprect == D2DERR_RENDER_TARGET_HAS_LAYER_OR_CLIPRECT);
#endif

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
        ++set_fill_mode_count;
        fill_mode = value;
    }

    void PROGPU_NATIVE_COM_CALL SetSegmentFlags(compat::path_segment value)
        noexcept override
    {
        ++set_segment_flags_count;
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
            begin_segment_offsets[begin_count] = captured_segment_count;
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
                if (captured_segment_count < captured_segments.size()) {
                    captured_segments[captured_segment_count++] =
                        {false, {}, {}, points[index]};
                }
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
            for (std::uint32_t index = 0U;
                 index < value_count &&
                    captured_segment_count < captured_segments.size();
                 ++index) {
                captured_segments[captured_segment_count++] = {
                    true,
                    beziers[index].point1,
                    beziers[index].point2,
                    beziers[index].point3};
            }
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
            end_segment_offsets[end_count] = captured_segment_count;
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
        if (captured_segment_count < captured_segments.size()) {
            captured_segments[captured_segment_count++] =
                {false, {}, {}, point};
        }
        last = point;
    }

    void PROGPU_NATIVE_COM_CALL AddBezier(
        const compat::bezier_segment* bezier) noexcept override
    {
        ++bezier_count;
        if (bezier != nullptr) {
            if (captured_segment_count < captured_segments.size()) {
                captured_segments[captured_segment_count++] = {
                    true, bezier->point1, bezier->point2, bezier->point3};
            }
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
    struct captured_segment final {
        bool cubic{};
        compat::point_2f control1{};
        compat::point_2f control2{};
        compat::point_2f end{};
    };
    std::array<compat::point_2f, 32U> begin_points{};
    std::array<compat::figure_begin, 32U> figure_begins{};
    std::array<compat::figure_end, 32U> figure_ends{};
    std::array<std::size_t, 32U> begin_line_offsets{};
    std::array<std::size_t, 32U> end_line_offsets{};
    std::array<compat::point_2f, 64U> line_points{};
    std::array<std::size_t, 32U> begin_segment_offsets{};
    std::array<std::size_t, 32U> end_segment_offsets{};
    std::array<captured_segment, 512U> captured_segments{};
    std::size_t line_point_count = 0U;
    std::size_t captured_segment_count = 0U;
    std::uint32_t begin_count = 0U;
    std::uint32_t end_count = 0U;
    std::uint32_t line_count = 0U;
    std::uint32_t bezier_count = 0U;
    std::uint32_t quadratic_count = 0U;
    std::uint32_t arc_count = 0U;
    std::uint32_t close_count = 0U;
    std::uint32_t set_fill_mode_count = 0U;
    std::uint32_t set_segment_flags_count = 0U;

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
        for (std::size_t segment = sink.begin_segment_offsets[figure];
             segment < sink.end_segment_offsets[figure];
             ++segment) {
            const auto& captured = sink.captured_segments[segment];
            if (!captured.cubic) {
                visit_edge(captured.end);
                continue;
            }
            const compat::point_2f cubic_start = previous;
            constexpr std::uint32_t cubic_steps = 64U;
            for (std::uint32_t step = 1U; step <= cubic_steps; ++step) {
                const double amount =
                    static_cast<double>(step) / cubic_steps;
                const double inverse = 1.0 - amount;
                const double first_weight = inverse * inverse * inverse;
                const double second_weight =
                    3.0 * inverse * inverse * amount;
                const double third_weight =
                    3.0 * inverse * amount * amount;
                const double fourth_weight = amount * amount * amount;
                visit_edge({
                    static_cast<float>(
                        first_weight * cubic_start.x +
                        second_weight * captured.control1.x +
                        third_weight * captured.control2.x +
                        fourth_weight * captured.end.x),
                    static_cast<float>(
                        first_weight * cubic_start.y +
                        second_weight * captured.control1.y +
                        third_weight * captured.control2.y +
                        fourth_weight * captured.end.y)});
            }
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
            const std::uint32_t available = static_cast<std::uint32_t>(
                captured.size() - captured_count);
            const std::uint32_t copy_count =
                std::min(available, triangle_count);
            std::copy_n(
                values,
                copy_count,
                captured.begin() + captured_count);
            captured_count += copy_count;
        }
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        return com::ok;
    }

    compat::triangle first{};
    std::array<compat::triangle, 256U> captured{};
    std::uint32_t captured_count = 0U;
    std::uint32_t count = 0U;

private:
    friend class com::atomic_reference_count<triangle_sink>;
    ~triangle_sink() = default;
    com::atomic_reference_count<triangle_sink> reference_count_;
};

class fake_wic_bitmap_source final : public compat::wic_bitmap_source {
public:
    explicit fake_wic_bitmap_source(com::guid pixel_format)
        : pixels{
              0x00U, 0x00U, 0x80U, 0x80U,
              0x00U, 0x40U, 0x00U, 0x40U,
              0x20U, 0x00U, 0x00U, 0x20U,
              0xFFU, 0xFFU, 0xFFU, 0xFFU},
          pixel_format_(pixel_format)
    {
    }

    fake_wic_bitmap_source(
        com::guid pixel_format,
        std::uint32_t width,
        std::uint32_t height,
        std::vector<std::uint8_t> source_pixels) noexcept
        : pixels(std::move(source_pixels)),
          pixel_format_(pixel_format),
          width_(width),
          height_(height)
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
        *width = width_;
        *height = height_;
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
        const std::uint32_t row_bytes = width_ *
            (com::guid_equal(pixel_format_, compat::wic_pixel_format_8bpp_alpha) ? 1U : 4U);
        if (rectangle != nullptr || buffer == nullptr ||
            stride < row_bytes || height_ == 0U ||
            buffer_size < stride * height_ ||
            pixels.size() !=
                static_cast<std::size_t>(row_bytes) * height_) {
            return com::invalid_argument;
        }
        for (std::uint32_t row = 0U; row < height_; ++row) {
            std::memcpy(
                buffer + static_cast<std::size_t>(row) * stride,
                pixels.data() + static_cast<std::size_t>(row) * row_bytes,
                row_bytes);
        }
        return com::ok;
    }

    std::vector<std::uint8_t> pixels;
    std::uint32_t resolution_call_count = 0U;
    std::uint32_t copy_call_count = 0U;
    std::uint32_t last_stride = 0U;
    std::uint32_t last_buffer_size = 0U;

private:
    friend class com::atomic_reference_count<fake_wic_bitmap_source>;
    ~fake_wic_bitmap_source() = default;

    com::atomic_reference_count<fake_wic_bitmap_source> reference_count_;
    com::guid pixel_format_;
    std::uint32_t width_ = 2U;
    std::uint32_t height_ = 2U;
};

class fake_wic_bitmap_lock final : public compat::wic_bitmap_lock {
public:
    fake_wic_bitmap_lock(
        com::guid pixel_format,
        std::uint32_t width,
        std::uint32_t height,
        std::uint32_t stride,
        std::vector<std::uint8_t> source_pixels,
        std::uint32_t* destruction_count = nullptr) noexcept
        : pixels(std::move(source_pixels)),
          pixel_format_(pixel_format),
          width_(width),
          height_(height),
          stride_(stride),
          destruction_count_(destruction_count)
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
                interface_id, compat::wic_bitmap_lock_interface_id)) {
            *value = static_cast<compat::wic_bitmap_lock*>(this);
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
        ++size_call_count;
        if (width == nullptr || height == nullptr) {
            return com::invalid_argument;
        }
        *width = width_;
        *height = height_;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL GetStride(
        std::uint32_t* stride) noexcept override
    {
        ++stride_call_count;
        if (stride == nullptr) {
            return com::invalid_argument;
        }
        *stride = stride_;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL GetDataPointer(
        std::uint32_t* buffer_size,
        std::uint8_t** data) noexcept override
    {
        ++data_call_count;
        if (buffer_size == nullptr || data == nullptr ||
            pixels.size() > std::numeric_limits<std::uint32_t>::max()) {
            return com::invalid_argument;
        }
        *buffer_size = static_cast<std::uint32_t>(pixels.size());
        *data = pixels.data();
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL GetPixelFormat(
        com::guid* pixel_format) noexcept override
    {
        ++format_call_count;
        if (pixel_format == nullptr) {
            return com::invalid_argument;
        }
        *pixel_format = pixel_format_;
        return com::ok;
    }

    std::vector<std::uint8_t> pixels;
    std::uint32_t size_call_count = 0U;
    std::uint32_t stride_call_count = 0U;
    std::uint32_t data_call_count = 0U;
    std::uint32_t format_call_count = 0U;

private:
    friend class com::atomic_reference_count<fake_wic_bitmap_lock>;
    ~fake_wic_bitmap_lock() {
        if (destruction_count_ != nullptr) ++*destruction_count_;
    }

    com::atomic_reference_count<fake_wic_bitmap_lock> reference_count_;
    com::guid pixel_format_{};
    std::uint32_t width_ = 0U;
    std::uint32_t height_ = 0U;
    std::uint32_t stride_ = 0U;
    std::uint32_t* destruction_count_ = nullptr;
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

    // Native contour adaptation must not turn a return to the starting point
    // into a closed filled figure, or connect disjoint stroke runs.
    {
        std::array<progpu_native_path_segment, 3U> segments{};
        const std::array<progpu_native_point, 4U> points{{{0, 0}, {10, 0}, {0, 0}, {0, 10}}};
        for (std::size_t index = 0U; index < segments.size(); ++index) {
            segments[index].kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
            segments[index].p0 = points[index];
            segments[index].p1 = points[index + 1U];
        }
        const std::array<std::uint8_t, 3U> joins{1U, 0U, 1U};
        com::pointer<compat::path_geometry> path;
        if (compat::detail::create_native_stroke_geometry(factory.get(), segments, joins,
                false, path.put()) != com::ok) return 9001;
        com::pointer<simplified_sink> sink;
        sink.attach(new simplified_sink());
        if (path->Stream(sink.get()) != com::ok || sink->begin_count != 1U ||
            sink->end_count != 1U || sink->line_count != 3U ||
            sink->figure_begin != compat::figure_begin::hollow ||
            sink->figure_end != compat::figure_end::open ||
            sink->segment_flags != compat::path_segment::none) return 9002;
        com::pointer<compat::path_geometry> rejected;
        if (compat::detail::create_native_stroke_geometry(factory.get(), segments,
                std::span{joins}.first(2U), false, rejected.put()) != com::invalid_argument ||
            rejected.get() != nullptr) return 9003;
        segments[1U].p0.x = 11.0F;
        if (compat::detail::create_native_stroke_geometry(factory.get(), segments, joins,
                false, rejected.put()) != com::invalid_argument || rejected.get() != nullptr) return 9004;
        segments[1U].p0 = points[1U];
        if (compat::detail::create_native_stroke_geometry(factory.get(), segments, joins,
                true, path.put()) != com::ok) return 9005;
        sink.attach(new simplified_sink());
        if (path->Stream(sink.get()) != com::ok || sink->begin_count != 1U ||
            sink->figure_end != compat::figure_end::closed) return 9006;
    }

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
        raw_outline_sink->segment_flags !=
            compat::path_segment::force_unstroked ||
        raw_outline_sink->set_segment_flags_count != 0U ||
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
    auto* raw_zero_rectangle_widen_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        zero_rectangle_widen_sink;
    zero_rectangle_widen_sink.attach(raw_zero_rectangle_widen_sink);
    if (geometry->Widen(
            2.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            widen_sink.get()) != com::ok ||
        geometry->Widen(
            0.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            zero_rectangle_widen_sink.get()) != com::ok ||
        raw_widen_sink->fill_mode != compat::fill_mode::alternate ||
        raw_widen_sink->segment_flags !=
            compat::path_segment::force_unstroked ||
        raw_widen_sink->begin_count != 2U ||
        raw_widen_sink->end_count != 2U ||
        raw_widen_sink->line_count != 6U ||
        raw_widen_sink->bezier_count != 0U ||
        raw_widen_sink->line_point_count != 6U ||
        raw_zero_rectangle_widen_sink->fill_mode !=
            compat::fill_mode::alternate ||
        raw_zero_rectangle_widen_sink->set_fill_mode_count != 1U ||
        raw_zero_rectangle_widen_sink->set_segment_flags_count != 0U ||
        raw_zero_rectangle_widen_sink->begin_count != 2U ||
        raw_zero_rectangle_widen_sink->end_count != 2U ||
        raw_zero_rectangle_widen_sink->line_count != 6U ||
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
    auto* raw_zero_transformed_widen_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        zero_transformed_widen_sink;
    zero_transformed_widen_sink.attach(raw_zero_transformed_widen_sink);
    if (transformed->Widen(
            2.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            transformed_widen_sink.get()) != com::ok ||
        transformed->Widen(
            0.0F,
            nullptr,
            &transform,
            core::default_flattening_tolerance,
            zero_transformed_widen_sink.get()) != com::ok ||
        raw_transformed_widen_sink->fill_mode !=
            compat::fill_mode::winding ||
        raw_transformed_widen_sink->segment_flags !=
            compat::path_segment::force_unstroked ||
        raw_zero_transformed_widen_sink->fill_mode !=
            compat::fill_mode::winding ||
        raw_zero_transformed_widen_sink->set_fill_mode_count != 1U ||
        raw_zero_transformed_widen_sink->set_segment_flags_count != 0U ||
        raw_zero_transformed_widen_sink->begin_count != 0U ||
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

    compat::path_geometry* raw_open_query_path = nullptr;
    compat::geometry_sink* raw_open_query_sink = nullptr;
    if (factory->CreatePathGeometry(&raw_open_query_path) != com::ok ||
        raw_open_query_path == nullptr ||
        raw_open_query_path->Open(&raw_open_query_sink) != com::ok ||
        raw_open_query_sink == nullptr) {
      return 382;
    }
    com::pointer<compat::path_geometry> open_query_path;
    open_query_path.attach(raw_open_query_path);
    com::pointer<compat::geometry_sink> open_query_sink;
    open_query_sink.attach(raw_open_query_sink);
    open_query_sink->BeginFigure(
        {0.0F, 0.0F}, compat::figure_begin::hollow);
    constexpr std::array<compat::point_2f, 2U> open_query_points{{
        {4.0F, 0.0F},
        {4.0F, 4.0F},
    }};
    open_query_sink->AddLines(
        open_query_points.data(),
        static_cast<std::uint32_t>(open_query_points.size()));
    open_query_sink->EndFigure(compat::figure_end::open);
    if (open_query_sink->Close() != com::ok) {
      return 383;
    }
    open_query_sink.Reset();

    compat::path_geometry* raw_round_segment_path = nullptr;
    compat::geometry_sink* raw_round_segment_sink = nullptr;
    if (factory->CreatePathGeometry(&raw_round_segment_path) != com::ok ||
        raw_round_segment_path == nullptr ||
        raw_round_segment_path->Open(&raw_round_segment_sink) != com::ok ||
        raw_round_segment_sink == nullptr) {
      return 409;
    }
    com::pointer<compat::path_geometry> round_segment_path;
    round_segment_path.attach(raw_round_segment_path);
    com::pointer<compat::geometry_sink> round_segment_sink;
    round_segment_sink.attach(raw_round_segment_sink);
    round_segment_sink->BeginFigure(
        {0.0F, 0.0F}, compat::figure_begin::hollow);
    round_segment_sink->AddLine({4.0F, 0.0F});
    round_segment_sink->SetSegmentFlags(
        compat::path_segment::force_round_line_join);
    round_segment_sink->AddLine({4.0F, 4.0F});
    round_segment_sink->EndFigure(compat::figure_end::open);
    if (round_segment_sink->Close() != com::ok) {
      return 410;
    }
    round_segment_sink.Reset();

    compat::path_geometry* raw_unstroked_segment_path = nullptr;
    compat::geometry_sink* raw_unstroked_segment_sink = nullptr;
    if (factory->CreatePathGeometry(&raw_unstroked_segment_path) != com::ok ||
        raw_unstroked_segment_path == nullptr ||
        raw_unstroked_segment_path->Open(
            &raw_unstroked_segment_sink) != com::ok ||
        raw_unstroked_segment_sink == nullptr) {
      return 411;
    }
    com::pointer<compat::path_geometry> unstroked_segment_path;
    unstroked_segment_path.attach(raw_unstroked_segment_path);
    com::pointer<compat::geometry_sink> unstroked_segment_sink;
    unstroked_segment_sink.attach(raw_unstroked_segment_sink);
    unstroked_segment_sink->BeginFigure(
        {0.0F, 0.0F}, compat::figure_begin::hollow);
    unstroked_segment_sink->AddLine({4.0F, 0.0F});
    unstroked_segment_sink->SetSegmentFlags(
        compat::path_segment::force_unstroked);
    unstroked_segment_sink->AddLine({4.0F, 4.0F});
    unstroked_segment_sink->SetSegmentFlags(compat::path_segment::none);
    unstroked_segment_sink->AddLine({8.0F, 4.0F});
    unstroked_segment_sink->EndFigure(compat::figure_end::open);
    if (unstroked_segment_sink->Close() != com::ok) {
      return 412;
    }
    unstroked_segment_sink.Reset();

    compat::path_geometry* raw_open_curve_path = nullptr;
    compat::geometry_sink* raw_open_curve_sink = nullptr;
    if (factory->CreatePathGeometry(&raw_open_curve_path) != com::ok ||
        raw_open_curve_path == nullptr ||
        raw_open_curve_path->Open(&raw_open_curve_sink) != com::ok ||
        raw_open_curve_sink == nullptr) {
      return 404;
    }
    com::pointer<compat::path_geometry> open_curve_path;
    open_curve_path.attach(raw_open_curve_path);
    com::pointer<compat::geometry_sink> open_curve_sink;
    open_curve_sink.attach(raw_open_curve_sink);
    open_curve_sink->BeginFigure(
        {0.0F, 0.0F}, compat::figure_begin::hollow);
    const compat::bezier_segment open_curve_cubic{
        {2.0F, 0.0F}, {4.0F, 2.0F}, {6.0F, 2.0F}};
    open_curve_sink->AddBezier(&open_curve_cubic);
    const compat::quadratic_bezier_segment open_curve_quadratic{
        {8.0F, 2.0F}, {10.0F, 0.0F}};
    open_curve_sink->AddQuadraticBezier(&open_curve_quadratic);
    open_curve_sink->EndFigure(compat::figure_end::open);
    if (open_curve_sink->Close() != com::ok) {
      return 405;
    }
    open_curve_sink.Reset();

    compat::path_geometry* raw_multi_query_path = nullptr;
    compat::geometry_sink* raw_multi_query_sink = nullptr;
    if (factory->CreatePathGeometry(&raw_multi_query_path) != com::ok ||
        raw_multi_query_path == nullptr ||
        raw_multi_query_path->Open(&raw_multi_query_sink) != com::ok ||
        raw_multi_query_sink == nullptr) {
      return 391;
    }
    com::pointer<compat::path_geometry> multi_query_path;
    multi_query_path.attach(raw_multi_query_path);
    com::pointer<compat::geometry_sink> multi_query_sink;
    multi_query_sink.attach(raw_multi_query_sink);
    multi_query_sink->BeginFigure(
        {0.0F, 0.0F}, compat::figure_begin::hollow);
    constexpr std::array<compat::point_2f, 3U> multi_closed_points{{
        {2.0F, 0.0F},
        {2.0F, 2.0F},
        {0.0F, 2.0F},
    }};
    multi_query_sink->AddLines(
        multi_closed_points.data(),
        static_cast<std::uint32_t>(multi_closed_points.size()));
    multi_query_sink->EndFigure(compat::figure_end::closed);
    multi_query_sink->BeginFigure(
        {10.0F, 0.0F}, compat::figure_begin::hollow);
    constexpr std::array<compat::point_2f, 2U> multi_open_points{{
        {14.0F, 0.0F},
        {14.0F, 4.0F},
    }};
    multi_query_sink->AddLines(
        multi_open_points.data(),
        static_cast<std::uint32_t>(multi_open_points.size()));
    multi_query_sink->EndFigure(compat::figure_end::open);
    if (multi_query_sink->Close() != com::ok) {
      return 392;
    }
    multi_query_sink.Reset();

    compat::path_geometry* raw_multi_outline_path = nullptr;
    compat::geometry_sink* raw_multi_outline_path_sink = nullptr;
    if (factory->CreatePathGeometry(&raw_multi_outline_path) != com::ok ||
        raw_multi_outline_path == nullptr ||
        raw_multi_outline_path->Open(&raw_multi_outline_path_sink) !=
            com::ok ||
        raw_multi_outline_path_sink == nullptr) {
      return 418;
    }
    com::pointer<compat::path_geometry> multi_outline_path;
    multi_outline_path.attach(raw_multi_outline_path);
    com::pointer<compat::geometry_sink> multi_outline_path_sink;
    multi_outline_path_sink.attach(raw_multi_outline_path_sink);
    multi_outline_path_sink->BeginFigure(
        {0.0F, 0.0F}, compat::figure_begin::filled);
    multi_outline_path_sink->AddLines(
        multi_closed_points.data(),
        static_cast<std::uint32_t>(multi_closed_points.size()));
    multi_outline_path_sink->EndFigure(compat::figure_end::closed);
    multi_outline_path_sink->BeginFigure(
        {10.0F, 0.0F}, compat::figure_begin::filled);
    constexpr std::array<compat::point_2f, 3U>
        second_outline_points{{
            {10.0F, 2.0F},
            {12.0F, 2.0F},
            {12.0F, 0.0F},
        }};
    multi_outline_path_sink->AddLines(
        second_outline_points.data(),
        static_cast<std::uint32_t>(second_outline_points.size()));
    multi_outline_path_sink->EndFigure(compat::figure_end::closed);
    if (multi_outline_path_sink->Close() != com::ok) {
      return 419;
    }
    multi_outline_path_sink.Reset();
    auto* raw_multi_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> multi_outline_sink;
    multi_outline_sink.attach(raw_multi_outline_sink);
    if (multi_outline_path->Outline(
            nullptr,
            core::default_flattening_tolerance,
            multi_outline_sink.get()) != com::ok ||
        raw_multi_outline_sink->fill_mode !=
            compat::fill_mode::alternate ||
        raw_multi_outline_sink->segment_flags !=
            compat::path_segment::force_unstroked ||
        raw_multi_outline_sink->set_segment_flags_count != 0U ||
        raw_multi_outline_sink->begin_count != 2U ||
        raw_multi_outline_sink->end_count != 2U ||
        raw_multi_outline_sink->line_count != 8U ||
        !captured_fill_contains(*raw_multi_outline_sink, {1.0F, 1.0F}) ||
        !captured_fill_contains(*raw_multi_outline_sink, {11.0F, 1.0F}) ||
        captured_fill_contains(*raw_multi_outline_sink, {6.0F, 1.0F})) {
      return 420;
    }
    compat::path_geometry* raw_nested_outline_path = nullptr;
    compat::geometry_sink* raw_nested_outline_path_sink = nullptr;
    if (factory->CreatePathGeometry(&raw_nested_outline_path) != com::ok ||
        raw_nested_outline_path == nullptr ||
        raw_nested_outline_path->Open(&raw_nested_outline_path_sink) !=
            com::ok ||
        raw_nested_outline_path_sink == nullptr) {
      return 423;
    }
    com::pointer<compat::path_geometry> nested_outline_path;
    nested_outline_path.attach(raw_nested_outline_path);
    com::pointer<compat::geometry_sink> nested_outline_path_sink;
    nested_outline_path_sink.attach(raw_nested_outline_path_sink);
    nested_outline_path_sink->BeginFigure(
        {0.0F, 0.0F}, compat::figure_begin::filled);
    nested_outline_path_sink->AddLines(
        multi_closed_points.data(),
        static_cast<std::uint32_t>(multi_closed_points.size()));
    nested_outline_path_sink->EndFigure(compat::figure_end::closed);
    nested_outline_path_sink->BeginFigure(
        {0.5F, 0.5F}, compat::figure_begin::filled);
    constexpr std::array<compat::point_2f, 3U> nested_outline_points{{
        {1.5F, 0.5F},
        {1.5F, 1.5F},
        {0.5F, 1.5F},
    }};
    nested_outline_path_sink->AddLines(
        nested_outline_points.data(),
        static_cast<std::uint32_t>(nested_outline_points.size()));
    nested_outline_path_sink->EndFigure(compat::figure_end::closed);
    if (nested_outline_path_sink->Close() != com::ok) {
      return 424;
    }
    nested_outline_path_sink.Reset();
    auto* raw_nested_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> nested_outline_sink;
    nested_outline_sink.attach(raw_nested_outline_sink);
    float nested_outline_area = 0.0F;
    if (nested_outline_path->Outline(
            nullptr,
            core::default_flattening_tolerance,
            nested_outline_sink.get()) != com::ok ||
        nested_outline_path->ComputeArea(
            nullptr,
            core::default_flattening_tolerance,
            &nested_outline_area) != com::ok ||
        !approximately_equal(nested_outline_area, 3.0F) ||
        raw_nested_outline_sink->fill_mode !=
            compat::fill_mode::alternate ||
        raw_nested_outline_sink->set_fill_mode_count != 1U ||
        raw_nested_outline_sink->set_segment_flags_count != 0U ||
        raw_nested_outline_sink->begin_count != 2U ||
        raw_nested_outline_sink->end_count != 2U ||
        raw_nested_outline_sink->line_count != 8U ||
        !captured_fill_contains(*raw_nested_outline_sink, {0.25F, 0.25F}) ||
        captured_fill_contains(*raw_nested_outline_sink, {1.0F, 1.0F}) ||
        captured_fill_contains(*raw_nested_outline_sink, {3.0F, 1.0F})) {
      return 425;
    }
    auto* raw_nested_tessellation_sink = new triangle_sink();
    com::pointer<compat::tessellation_sink> nested_tessellation_sink;
    nested_tessellation_sink.attach(raw_nested_tessellation_sink);
    const com::result nested_tessellation_status =
        nested_outline_path->Tessellate(
            nullptr,
            core::default_flattening_tolerance,
            nested_tessellation_sink.get());
    if (nested_tessellation_status != com::ok ||
        raw_nested_tessellation_sink->count != 8U ||
        raw_nested_tessellation_sink->captured_count != 8U) {
      std::fprintf(
          stderr,
          "nested tessellation status=%d triangles=%u/%u\n",
          static_cast<int>(nested_tessellation_status),
          raw_nested_tessellation_sink->count,
          raw_nested_tessellation_sink->captured_count);
      return 459;
    }
    double nested_tessellated_area = 0.0;
    for (std::uint32_t index = 0U;
         index < raw_nested_tessellation_sink->captured_count; ++index) {
      const compat::triangle& value =
          raw_nested_tessellation_sink->captured[index];
      nested_tessellated_area += std::abs(
          (static_cast<double>(value.point2.x) - value.point1.x) *
              (static_cast<double>(value.point3.y) - value.point1.y) -
          (static_cast<double>(value.point2.y) - value.point1.y) *
              (static_cast<double>(value.point3.x) - value.point1.x)) * 0.5;
    }
    const auto nested_tessellation_contains = [raw_nested_tessellation_sink](
        compat::point_2f point) {
      for (std::uint32_t index = 0U;
           index < raw_nested_tessellation_sink->captured_count; ++index) {
        const compat::triangle& value =
            raw_nested_tessellation_sink->captured[index];
        const auto cross = [point](compat::point_2f first,
                                   compat::point_2f second) {
          return (static_cast<double>(second.x) - first.x) *
                  (static_cast<double>(point.y) - first.y) -
              (static_cast<double>(second.y) - first.y) *
                  (static_cast<double>(point.x) - first.x);
        };
        const double first = cross(value.point1, value.point2);
        const double second = cross(value.point2, value.point3);
        const double third = cross(value.point3, value.point1);
        if ((first >= 0.0 && second >= 0.0 && third >= 0.0) ||
            (first <= 0.0 && second <= 0.0 && third <= 0.0)) {
          return true;
        }
      }
      return false;
    };
    if (!approximately_equal(
            static_cast<float>(nested_tessellated_area), 3.0F) ||
        !nested_tessellation_contains({0.25F, 0.25F}) ||
        nested_tessellation_contains({1.0F, 1.0F}) ||
        nested_tessellation_contains({3.0F, 1.0F})) {
      return 460;
    }
    for (const compat::combine_mode mode : combination_modes) {
      auto* raw_multi_contour_boolean_sink = new simplified_sink();
      com::pointer<compat::simplified_geometry_sink>
          multi_contour_boolean_sink;
      multi_contour_boolean_sink.attach(raw_multi_contour_boolean_sink);
      if (multi_outline_path->CombineWithGeometry(
              nested_outline_path.get(),
              mode,
              nullptr,
              0.01F,
              multi_contour_boolean_sink.get()) != com::ok ||
          raw_multi_contour_boolean_sink->fill_mode !=
              compat::fill_mode::alternate ||
          raw_multi_contour_boolean_sink->segment_flags !=
              compat::path_segment::force_unstroked ||
          raw_multi_contour_boolean_sink->begin_count !=
              raw_multi_contour_boolean_sink->end_count) {
        return 444;
      }
      for (std::uint32_t y_index = 0U; y_index < 24U; ++y_index) {
        for (std::uint32_t x_index = 0U; x_index < 52U; ++x_index) {
          const compat::point_2f point{
              -0.23F + static_cast<float>(x_index) * 0.25F,
              -0.19F + static_cast<float>(y_index) * 0.17F};
          std::int32_t in_first = 0;
          std::int32_t in_second = 0;
          if (multi_outline_path->FillContainsPoint(
                  point, nullptr, 0.01F, &in_first) != com::ok ||
              nested_outline_path->FillContainsPoint(
                  point, nullptr, 0.01F, &in_second) != com::ok) {
            return 445;
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
                  *raw_multi_contour_boolean_sink, point) != expected) {
            return 446;
          }
        }
      }
    }
    const compat::rectangle_f multi_relation_envelope{
        -1.0F, -1.0F, 13.0F, 3.0F};
    const compat::rectangle_f nested_hole_interior{
        0.75F, 0.75F, 1.25F, 1.25F};
    compat::rectangle_geometry* raw_multi_relation_envelope = nullptr;
    compat::rectangle_geometry* raw_nested_hole_interior = nullptr;
    if (factory->CreateRectangleGeometry(
            &multi_relation_envelope,
            &raw_multi_relation_envelope) != com::ok ||
        raw_multi_relation_envelope == nullptr ||
        factory->CreateRectangleGeometry(
            &nested_hole_interior,
            &raw_nested_hole_interior) != com::ok ||
        raw_nested_hole_interior == nullptr) {
      if (raw_multi_relation_envelope != nullptr) {
        raw_multi_relation_envelope->Release();
      }
      if (raw_nested_hole_interior != nullptr) {
        raw_nested_hole_interior->Release();
      }
      return 449;
    }
    com::pointer<compat::rectangle_geometry> multi_relation_envelope_geometry;
    multi_relation_envelope_geometry.attach(raw_multi_relation_envelope);
    com::pointer<compat::rectangle_geometry> nested_hole_interior_geometry;
    nested_hole_interior_geometry.attach(raw_nested_hole_interior);
    compat::geometry_relation multi_contained_relation =
        compat::geometry_relation::unknown;
    compat::geometry_relation hole_disjoint_relation =
        compat::geometry_relation::unknown;
    compat::geometry_relation shared_boundary_relation =
        compat::geometry_relation::unknown;
    compat::geometry_relation multi_equal_relation =
        compat::geometry_relation::unknown;
    if (multi_outline_path->CompareWithGeometry(
            multi_relation_envelope_geometry.get(),
            nullptr,
            0.01F,
            &multi_contained_relation) != com::ok ||
        nested_outline_path->CompareWithGeometry(
            nested_hole_interior_geometry.get(),
            nullptr,
            0.01F,
            &hole_disjoint_relation) != com::ok ||
        multi_outline_path->CompareWithGeometry(
            nested_outline_path.get(),
            nullptr,
            0.01F,
            &shared_boundary_relation) != com::ok ||
        multi_outline_path->CompareWithGeometry(
            multi_outline_path.get(),
            nullptr,
            0.01F,
            &multi_equal_relation) != com::ok ||
        multi_contained_relation !=
            compat::geometry_relation::is_contained ||
        hole_disjoint_relation != compat::geometry_relation::disjoint ||
        shared_boundary_relation != compat::geometry_relation::contains ||
        multi_equal_relation != compat::geometry_relation::is_contained) {
      return 450;
    }
    compat::path_geometry* raw_winding_outline_path = nullptr;
    compat::geometry_sink* raw_winding_outline_path_sink = nullptr;
    if (factory->CreatePathGeometry(&raw_winding_outline_path) != com::ok ||
        raw_winding_outline_path == nullptr ||
        raw_winding_outline_path->Open(&raw_winding_outline_path_sink) !=
            com::ok ||
        raw_winding_outline_path_sink == nullptr) {
      return 428;
    }
    com::pointer<compat::path_geometry> winding_outline_path;
    winding_outline_path.attach(raw_winding_outline_path);
    com::pointer<compat::geometry_sink> winding_outline_path_sink;
    winding_outline_path_sink.attach(raw_winding_outline_path_sink);
    winding_outline_path_sink->SetFillMode(compat::fill_mode::winding);
    winding_outline_path_sink->BeginFigure(
        {0.0F, 0.0F}, compat::figure_begin::filled);
    winding_outline_path_sink->AddLines(
        multi_closed_points.data(),
        static_cast<std::uint32_t>(multi_closed_points.size()));
    winding_outline_path_sink->EndFigure(compat::figure_end::closed);
    winding_outline_path_sink->BeginFigure(
        {0.5F, 0.5F}, compat::figure_begin::filled);
    constexpr std::array<compat::point_2f, 3U>
        winding_hole_points{{
            {0.5F, 1.5F},
            {1.5F, 1.5F},
            {1.5F, 0.5F},
        }};
    winding_outline_path_sink->AddLines(
        winding_hole_points.data(),
        static_cast<std::uint32_t>(winding_hole_points.size()));
    winding_outline_path_sink->EndFigure(compat::figure_end::closed);
    if (winding_outline_path_sink->Close() != com::ok) {
      return 429;
    }
    winding_outline_path_sink.Reset();
    auto* raw_winding_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> winding_outline_sink;
    winding_outline_sink.attach(raw_winding_outline_sink);
    float winding_outline_area = 0.0F;
    if (winding_outline_path->Outline(
            nullptr,
            core::default_flattening_tolerance,
            winding_outline_sink.get()) != com::ok ||
        winding_outline_path->ComputeArea(
            nullptr,
            core::default_flattening_tolerance,
            &winding_outline_area) != com::ok ||
        !approximately_equal(winding_outline_area, 3.0F) ||
        raw_winding_outline_sink->fill_mode !=
            compat::fill_mode::alternate ||
        raw_winding_outline_sink->set_fill_mode_count != 1U ||
        raw_winding_outline_sink->set_segment_flags_count != 0U ||
        raw_winding_outline_sink->begin_count != 2U ||
        raw_winding_outline_sink->end_count != 2U ||
        raw_winding_outline_sink->line_count != 8U ||
        !captured_fill_contains(*raw_winding_outline_sink, {0.25F, 0.25F}) ||
        captured_fill_contains(*raw_winding_outline_sink, {1.0F, 1.0F}) ||
        captured_fill_contains(*raw_winding_outline_sink, {3.0F, 1.0F})) {
      return 430;
    }
    for (std::uint32_t winding = 0U; winding < 2U; ++winding) {
      compat::path_geometry* raw_overlap_outline_path = nullptr;
      compat::geometry_sink* raw_overlap_outline_path_sink = nullptr;
      if (factory->CreatePathGeometry(&raw_overlap_outline_path) != com::ok ||
          raw_overlap_outline_path == nullptr ||
          raw_overlap_outline_path->Open(
              &raw_overlap_outline_path_sink) != com::ok ||
          raw_overlap_outline_path_sink == nullptr) {
        return 435;
      }
      com::pointer<compat::path_geometry> overlap_outline_path;
      overlap_outline_path.attach(raw_overlap_outline_path);
      com::pointer<compat::geometry_sink> overlap_outline_path_sink;
      overlap_outline_path_sink.attach(raw_overlap_outline_path_sink);
      if (winding != 0U) {
        overlap_outline_path_sink->SetFillMode(
            compat::fill_mode::winding);
      }
      overlap_outline_path_sink->BeginFigure(
          {0.0F, 0.0F}, compat::figure_begin::filled);
      constexpr std::array<compat::point_2f, 3U>
          first_overlap_outline_points{{
              {3.0F, 0.0F},
              {3.0F, 3.0F},
              {0.0F, 3.0F},
          }};
      overlap_outline_path_sink->AddLines(
          first_overlap_outline_points.data(),
          static_cast<std::uint32_t>(
              first_overlap_outline_points.size()));
      overlap_outline_path_sink->EndFigure(
          compat::figure_end::closed);
      overlap_outline_path_sink->BeginFigure(
          {2.0F, 1.0F}, compat::figure_begin::filled);
      constexpr std::array<compat::point_2f, 3U>
          second_overlap_outline_points{{
              {5.0F, 1.0F},
              {5.0F, 4.0F},
              {2.0F, 4.0F},
          }};
      overlap_outline_path_sink->AddLines(
          second_overlap_outline_points.data(),
          static_cast<std::uint32_t>(
              second_overlap_outline_points.size()));
      overlap_outline_path_sink->EndFigure(
          compat::figure_end::closed);
      if (overlap_outline_path_sink->Close() != com::ok) {
        return 436;
      }
      overlap_outline_path_sink.Reset();
      auto* raw_overlap_outline_sink = new simplified_sink();
      com::pointer<compat::simplified_geometry_sink> overlap_outline_sink;
      overlap_outline_sink.attach(raw_overlap_outline_sink);
      float overlap_area = 0.0F;
      if (overlap_outline_path->Outline(
              nullptr,
              0.01F,
              overlap_outline_sink.get()) != com::ok ||
          overlap_outline_path->ComputeArea(
              nullptr, 0.01F, &overlap_area) != com::ok ||
          !approximately_equal(
              overlap_area, winding == 0U ? 14.0F : 16.0F) ||
          raw_overlap_outline_sink->fill_mode !=
              compat::fill_mode::alternate ||
          raw_overlap_outline_sink->set_fill_mode_count != 1U ||
          raw_overlap_outline_sink->set_segment_flags_count != 0U ||
          raw_overlap_outline_sink->begin_count !=
              (winding == 0U ? 2U : 1U) ||
          raw_overlap_outline_sink->end_count !=
              raw_overlap_outline_sink->begin_count ||
          raw_overlap_outline_sink->line_count !=
              (winding == 0U ? 12U : 8U) ||
          !captured_fill_contains(
              *raw_overlap_outline_sink, {1.0F, 1.0F}) ||
          captured_fill_contains(
              *raw_overlap_outline_sink, {2.5F, 2.0F}) !=
              (winding != 0U) ||
          !captured_fill_contains(
              *raw_overlap_outline_sink, {4.0F, 2.0F})) {
        return 437;
      }
    }
    compat::path_geometry* raw_self_outline_path = nullptr;
    compat::geometry_sink* raw_self_outline_path_sink = nullptr;
    if (factory->CreatePathGeometry(&raw_self_outline_path) != com::ok ||
        raw_self_outline_path == nullptr ||
        raw_self_outline_path->Open(&raw_self_outline_path_sink) != com::ok ||
        raw_self_outline_path_sink == nullptr) {
      return 438;
    }
    com::pointer<compat::path_geometry> self_outline_path;
    self_outline_path.attach(raw_self_outline_path);
    com::pointer<compat::geometry_sink> self_outline_path_sink;
    self_outline_path_sink.attach(raw_self_outline_path_sink);
    self_outline_path_sink->BeginFigure(
        {0.0F, 0.0F}, compat::figure_begin::filled);
    constexpr std::array<compat::point_2f, 3U> self_outline_points{{
        {4.0F, 4.0F},
        {0.0F, 4.0F},
        {4.0F, 0.0F},
    }};
    self_outline_path_sink->AddLines(
        self_outline_points.data(),
        static_cast<std::uint32_t>(self_outline_points.size()));
    self_outline_path_sink->EndFigure(compat::figure_end::closed);
    if (self_outline_path_sink->Close() != com::ok) {
      return 439;
    }
    self_outline_path_sink.Reset();
    auto* raw_self_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> self_outline_sink;
    self_outline_sink.attach(raw_self_outline_sink);
    float self_outline_area = 0.0F;
    if (self_outline_path->Outline(
            nullptr, 0.01F, self_outline_sink.get()) != com::ok ||
        self_outline_path->ComputeArea(
            nullptr, 0.01F, &self_outline_area) != com::ok ||
        !approximately_equal(self_outline_area, 8.0F) ||
        raw_self_outline_sink->fill_mode !=
            compat::fill_mode::alternate ||
        raw_self_outline_sink->set_fill_mode_count != 1U ||
        raw_self_outline_sink->set_segment_flags_count != 0U ||
        raw_self_outline_sink->begin_count != 2U ||
        raw_self_outline_sink->end_count != 2U ||
        raw_self_outline_sink->line_count != 6U ||
        !captured_fill_contains(*raw_self_outline_sink, {2.0F, 0.5F}) ||
        !captured_fill_contains(*raw_self_outline_sink, {2.0F, 3.5F}) ||
        captured_fill_contains(*raw_self_outline_sink, {0.5F, 2.0F})) {
      return 440;
    }
    compat::path_geometry* raw_star_outline_path = nullptr;
    compat::geometry_sink* raw_star_outline_path_sink = nullptr;
    if (factory->CreatePathGeometry(&raw_star_outline_path) != com::ok ||
        raw_star_outline_path == nullptr ||
        raw_star_outline_path->Open(&raw_star_outline_path_sink) != com::ok ||
        raw_star_outline_path_sink == nullptr) {
      return 451;
    }
    com::pointer<compat::path_geometry> star_outline_path;
    star_outline_path.attach(raw_star_outline_path);
    com::pointer<compat::geometry_sink> star_outline_path_sink;
    star_outline_path_sink.attach(raw_star_outline_path_sink);
    constexpr std::array<compat::point_2f, 5U> star_outline_points{{
        {0.0F, -5.0F},
        {2.938926F, 4.045085F},
        {-4.755283F, -1.545085F},
        {4.755283F, -1.545085F},
        {-2.938926F, 4.045085F},
    }};
    star_outline_path_sink->BeginFigure(
        star_outline_points[0U], compat::figure_begin::filled);
    star_outline_path_sink->AddLines(
        star_outline_points.data() + 1U,
        static_cast<std::uint32_t>(star_outline_points.size() - 1U));
    star_outline_path_sink->EndFigure(compat::figure_end::closed);
    if (star_outline_path_sink->Close() != com::ok) {
      return 452;
    }
    star_outline_path_sink.Reset();
    auto* raw_star_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> star_outline_sink;
    star_outline_sink.attach(raw_star_outline_sink);
    float star_outline_area = 0.0F;
    if (star_outline_path->Outline(
            nullptr, 0.01F, star_outline_sink.get()) != com::ok ||
        star_outline_path->ComputeArea(
            nullptr, 0.01F, &star_outline_area) != com::ok ||
        star_outline_area <= 0.0F ||
        raw_star_outline_sink->begin_count == 0U ||
        raw_star_outline_sink->begin_count !=
            raw_star_outline_sink->end_count) {
      return 453;
    }
    for (std::uint32_t y_index = 0U; y_index < 46U; ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 46U; ++x_index) {
        const compat::point_2f point{
            -5.17F + static_cast<float>(x_index) * 0.23F,
            -5.11F + static_cast<float>(y_index) * 0.23F};
        std::int32_t source_contains = 0;
        if (star_outline_path->FillContainsPoint(
                point, nullptr, 0.0001F, &source_contains) != com::ok ||
            captured_fill_contains(*raw_star_outline_sink, point) !=
                (source_contains != 0)) {
          std::fprintf(
              stderr,
              "star outline mismatch point=%g,%g source=%d output=%d "
              "area=%g figures=%u lines=%u\n",
              point.x,
              point.y,
              source_contains,
              captured_fill_contains(*raw_star_outline_sink, point) ? 1 : 0,
              star_outline_area,
              raw_star_outline_sink->begin_count,
              raw_star_outline_sink->line_count);
          return 454;
        }
      }
    }
    const auto captured_triangle_area = [](const triangle_sink& triangles) {
      double area = 0.0;
      for (std::uint32_t index = 0U;
           index < triangles.captured_count; ++index) {
        const compat::triangle& value = triangles.captured[index];
        area += std::abs(
            (static_cast<double>(value.point2.x) - value.point1.x) *
                (static_cast<double>(value.point3.y) - value.point1.y) -
            (static_cast<double>(value.point2.y) - value.point1.y) *
                (static_cast<double>(value.point3.x) - value.point1.x)) *
            0.5;
      }
      return area;
    };
    auto* raw_star_tessellation_sink = new triangle_sink();
    com::pointer<compat::tessellation_sink> star_tessellation_sink;
    star_tessellation_sink.attach(raw_star_tessellation_sink);
    if (star_outline_path->Tessellate(
            nullptr, 0.01F, star_tessellation_sink.get()) != com::ok ||
        raw_star_tessellation_sink->count == 0U ||
        raw_star_tessellation_sink->captured_count !=
            raw_star_tessellation_sink->count ||
        !approximately_equal(
            static_cast<float>(
                captured_triangle_area(*raw_star_tessellation_sink)),
            star_outline_area)) {
      return 463;
    }
    compat::path_geometry* raw_winding_star_outline_path = nullptr;
    compat::geometry_sink* raw_winding_star_outline_path_sink = nullptr;
    if (factory->CreatePathGeometry(
            &raw_winding_star_outline_path) != com::ok ||
        raw_winding_star_outline_path == nullptr ||
        raw_winding_star_outline_path->Open(
            &raw_winding_star_outline_path_sink) != com::ok ||
        raw_winding_star_outline_path_sink == nullptr) {
      return 455;
    }
    com::pointer<compat::path_geometry> winding_star_outline_path;
    winding_star_outline_path.attach(raw_winding_star_outline_path);
    com::pointer<compat::geometry_sink> winding_star_outline_path_sink;
    winding_star_outline_path_sink.attach(
        raw_winding_star_outline_path_sink);
    winding_star_outline_path_sink->SetFillMode(
        compat::fill_mode::winding);
    winding_star_outline_path_sink->BeginFigure(
        star_outline_points[0U], compat::figure_begin::filled);
    winding_star_outline_path_sink->AddLines(
        star_outline_points.data() + 1U,
        static_cast<std::uint32_t>(star_outline_points.size() - 1U));
    winding_star_outline_path_sink->EndFigure(
        compat::figure_end::closed);
    // The star center has winding +2.  This reverse-wound square subtracts
    // one layer, so the center must remain filled rather than become a hole.
    winding_star_outline_path_sink->BeginFigure(
        {-0.25F, -0.25F}, compat::figure_begin::filled);
    constexpr std::array<compat::point_2f, 3U>
        winding_star_subtraction_points{{
            {-0.25F, 0.25F},
            {0.25F, 0.25F},
            {0.25F, -0.25F},
        }};
    winding_star_outline_path_sink->AddLines(
        winding_star_subtraction_points.data(),
        static_cast<std::uint32_t>(
            winding_star_subtraction_points.size()));
    winding_star_outline_path_sink->EndFigure(
        compat::figure_end::closed);
    if (winding_star_outline_path_sink->Close() != com::ok) {
      return 456;
    }
    winding_star_outline_path_sink.Reset();
    auto* raw_winding_star_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        winding_star_outline_sink;
    winding_star_outline_sink.attach(raw_winding_star_outline_sink);
    float winding_star_outline_area = 0.0F;
    const com::result winding_star_outline_status =
        winding_star_outline_path->Outline(
            nullptr, 0.01F, winding_star_outline_sink.get());
    if (winding_star_outline_status != com::ok ||
        winding_star_outline_path->ComputeArea(
            nullptr, 0.01F, &winding_star_outline_area) != com::ok ||
        winding_star_outline_area <= star_outline_area ||
        raw_winding_star_outline_sink->begin_count == 0U ||
        raw_winding_star_outline_sink->begin_count !=
            raw_winding_star_outline_sink->end_count) {
      std::fprintf(
          stderr,
          "winding star outline status=%d area=%g alternate=%g "
          "figures=%u/%u lines=%u\n",
          static_cast<int>(winding_star_outline_status),
          winding_star_outline_area,
          star_outline_area,
          raw_winding_star_outline_sink->begin_count,
          raw_winding_star_outline_sink->end_count,
          raw_winding_star_outline_sink->line_count);
      return 457;
    }
    for (std::uint32_t y_index = 0U; y_index < 46U; ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 46U; ++x_index) {
        const compat::point_2f point{
            -5.13F + static_cast<float>(x_index) * 0.23F,
            -5.07F + static_cast<float>(y_index) * 0.23F};
        std::int32_t source_contains = 0;
        if (winding_star_outline_path->FillContainsPoint(
                point, nullptr, 0.0001F, &source_contains) != com::ok ||
            captured_fill_contains(
                *raw_winding_star_outline_sink, point) !=
                (source_contains != 0)) {
          return 458;
        }
      }
    }
    auto* raw_winding_star_tessellation_sink = new triangle_sink();
    com::pointer<compat::tessellation_sink>
        winding_star_tessellation_sink;
    winding_star_tessellation_sink.attach(
        raw_winding_star_tessellation_sink);
    if (winding_star_outline_path->Tessellate(
            nullptr,
            0.01F,
            winding_star_tessellation_sink.get()) != com::ok ||
        raw_winding_star_tessellation_sink->count == 0U ||
        raw_winding_star_tessellation_sink->captured_count !=
            raw_winding_star_tessellation_sink->count ||
        !approximately_equal(
            static_cast<float>(captured_triangle_area(
                *raw_winding_star_tessellation_sink)),
            winding_star_outline_area)) {
      return 464;
    }
    const compat::rectangle_f star_center_rectangle{
        -0.25F, -0.25F, 0.25F, 0.25F};
    compat::rectangle_geometry* raw_star_center_geometry = nullptr;
    if (factory->CreateRectangleGeometry(
            &star_center_rectangle,
            &raw_star_center_geometry) != com::ok ||
        raw_star_center_geometry == nullptr) {
      return 465;
    }
    com::pointer<compat::rectangle_geometry> star_center_geometry;
    star_center_geometry.attach(raw_star_center_geometry);
    compat::geometry_relation alternate_star_relation =
        compat::geometry_relation::unknown;
    compat::geometry_relation winding_star_relation =
        compat::geometry_relation::unknown;
    if (star_outline_path->CompareWithGeometry(
            star_center_geometry.get(),
            nullptr,
            0.01F,
            &alternate_star_relation) != com::ok ||
        winding_star_outline_path->CompareWithGeometry(
            star_center_geometry.get(),
            nullptr,
            0.01F,
            &winding_star_relation) != com::ok ||
        alternate_star_relation != compat::geometry_relation::disjoint ||
        winding_star_relation != compat::geometry_relation::contains) {
      return 466;
    }
    const std::array<compat::path_geometry*, 2U> star_boolean_paths{{
        star_outline_path.get(), winding_star_outline_path.get()}};
    for (compat::path_geometry* star_path : star_boolean_paths) {
      for (const compat::combine_mode combination : combination_modes) {
        auto* raw_star_boolean_sink = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink> star_boolean_sink;
        star_boolean_sink.attach(raw_star_boolean_sink);
        if (star_path->CombineWithGeometry(
                star_center_geometry.get(),
                combination,
                nullptr,
                0.01F,
                star_boolean_sink.get()) != com::ok) {
          return 467;
        }
        for (std::uint32_t y_index = 0U; y_index < 46U; ++y_index) {
          for (std::uint32_t x_index = 0U; x_index < 46U; ++x_index) {
            const compat::point_2f point{
                -5.13F + static_cast<float>(x_index) * 0.23F,
                -5.07F + static_cast<float>(y_index) * 0.23F};
            std::int32_t in_star = 0;
            std::int32_t in_rectangle = 0;
            if (star_path->FillContainsPoint(
                    point, nullptr, 0.0001F, &in_star) != com::ok ||
                star_center_geometry->FillContainsPoint(
                    point, nullptr, 0.0001F, &in_rectangle) != com::ok) {
              return 468;
            }
            bool expected = false;
            switch (combination) {
            case compat::combine_mode::union_value:
              expected = in_star != 0 || in_rectangle != 0;
              break;
            case compat::combine_mode::intersect:
              expected = in_star != 0 && in_rectangle != 0;
              break;
            case compat::combine_mode::xor_value:
              expected = (in_star != 0) != (in_rectangle != 0);
              break;
            case compat::combine_mode::exclude:
              expected = in_star != 0 && in_rectangle == 0;
              break;
            }
            if (captured_fill_contains(*raw_star_boolean_sink, point) !=
                expected) {
              return 469;
            }
          }
        }
      }
    }
    for (std::uint32_t winding = 0U; winding < 2U; ++winding) {
      compat::path_geometry* raw_triple_outline_path = nullptr;
      compat::geometry_sink* raw_triple_outline_path_sink = nullptr;
      if (factory->CreatePathGeometry(&raw_triple_outline_path) != com::ok ||
          raw_triple_outline_path == nullptr ||
          raw_triple_outline_path->Open(
              &raw_triple_outline_path_sink) != com::ok ||
          raw_triple_outline_path_sink == nullptr) {
        return 441;
      }
      com::pointer<compat::path_geometry> triple_outline_path;
      triple_outline_path.attach(raw_triple_outline_path);
      com::pointer<compat::geometry_sink> triple_outline_path_sink;
      triple_outline_path_sink.attach(raw_triple_outline_path_sink);
      if (winding != 0U) {
        triple_outline_path_sink->SetFillMode(
            compat::fill_mode::winding);
      }
      constexpr std::array<std::array<compat::point_2f, 4U>, 3U>
          triple_rectangles{{
              {{{0.0F, 0.0F}, {3.0F, 0.0F},
                 {3.0F, 3.0F}, {0.0F, 3.0F}}},
              {{{2.0F, 1.0F}, {5.0F, 1.0F},
                 {5.0F, 4.0F}, {2.0F, 4.0F}}},
              {{{1.0F, 2.0F}, {4.0F, 2.0F},
                 {4.0F, 5.0F}, {1.0F, 5.0F}}},
          }};
      for (const auto& rectangle_points : triple_rectangles) {
        triple_outline_path_sink->BeginFigure(
            rectangle_points[0], compat::figure_begin::filled);
        triple_outline_path_sink->AddLines(
            rectangle_points.data() + 1U,
            static_cast<std::uint32_t>(rectangle_points.size() - 1U));
        triple_outline_path_sink->EndFigure(
            compat::figure_end::closed);
      }
      if (triple_outline_path_sink->Close() != com::ok) {
        return 442;
      }
      triple_outline_path_sink.Reset();
      auto* raw_triple_outline_sink = new simplified_sink();
      com::pointer<compat::simplified_geometry_sink> triple_outline_sink;
      triple_outline_sink.attach(raw_triple_outline_sink);
      float triple_outline_area = 0.0F;
      if (triple_outline_path->Outline(
              nullptr, 0.01F, triple_outline_sink.get()) != com::ok ||
          triple_outline_path->ComputeArea(
              nullptr, 0.01F, &triple_outline_area) != com::ok ||
          !approximately_equal(
              triple_outline_area, winding == 0U ? 15.0F : 20.0F) ||
          raw_triple_outline_sink->fill_mode !=
              compat::fill_mode::alternate ||
          raw_triple_outline_sink->set_fill_mode_count != 1U ||
          raw_triple_outline_sink->set_segment_flags_count != 0U ||
          raw_triple_outline_sink->begin_count == 0U ||
          raw_triple_outline_sink->begin_count !=
              raw_triple_outline_sink->end_count ||
          !captured_fill_contains(
              *raw_triple_outline_sink, {0.5F, 0.5F}) ||
          captured_fill_contains(
              *raw_triple_outline_sink, {2.5F, 1.5F}) !=
              (winding != 0U) ||
          !captured_fill_contains(
              *raw_triple_outline_sink, {2.5F, 2.5F}) ||
          captured_fill_contains(
              *raw_triple_outline_sink, {3.5F, 3.5F}) !=
              (winding != 0U) ||
          !captured_fill_contains(
              *raw_triple_outline_sink, {2.0F, 4.5F})) {
        return 443;
      }
    }

    compat::path_geometry* raw_multi_rejected_widen_path = nullptr;
    compat::geometry_sink* raw_multi_rejected_widen_path_sink = nullptr;
    if (factory->CreatePathGeometry(
            &raw_multi_rejected_widen_path) != com::ok ||
        raw_multi_rejected_widen_path == nullptr ||
        raw_multi_rejected_widen_path->Open(
            &raw_multi_rejected_widen_path_sink) != com::ok ||
        raw_multi_rejected_widen_path_sink == nullptr) {
      return 397;
    }
    com::pointer<compat::path_geometry> multi_rejected_widen_path;
    multi_rejected_widen_path.attach(raw_multi_rejected_widen_path);
    com::pointer<compat::geometry_sink> multi_rejected_widen_path_sink;
    multi_rejected_widen_path_sink.attach(
        raw_multi_rejected_widen_path_sink);
    multi_rejected_widen_path_sink->BeginFigure(
        {0.0F, 0.0F}, compat::figure_begin::hollow);
    multi_rejected_widen_path_sink->AddLines(
        multi_closed_points.data(),
        static_cast<std::uint32_t>(multi_closed_points.size()));
    multi_rejected_widen_path_sink->EndFigure(
        compat::figure_end::closed);
    multi_rejected_widen_path_sink->BeginFigure(
        {10.0F, 0.0F}, compat::figure_begin::hollow);
    multi_rejected_widen_path_sink->AddLine({14.0F, 4.0F});
    multi_rejected_widen_path_sink->SetSegmentFlags(
        compat::path_segment::force_round_line_join);
    constexpr std::array<compat::point_2f, 2U>
        rejected_self_intersecting_points{{
            {10.0F, 4.0F},
            {14.0F, 0.0F},
        }};
    multi_rejected_widen_path_sink->AddLines(
        rejected_self_intersecting_points.data(),
        static_cast<std::uint32_t>(
            rejected_self_intersecting_points.size()));
    multi_rejected_widen_path_sink->EndFigure(
        compat::figure_end::closed);
    if (multi_rejected_widen_path_sink->Close() != com::ok) {
      return 398;
    }
    multi_rejected_widen_path_sink.Reset();

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
  const compat::stroke_style_properties bevel_path_stroke_properties{
      compat::cap_style::flat,
      compat::cap_style::flat,
      compat::cap_style::flat,
      compat::line_join::bevel,
      4.0F,
      compat::dash_style::solid,
      0.0F};
  compat::stroke_style *raw_bevel_path_stroke_style = nullptr;
  if (factory->CreateStrokeStyle(
          &bevel_path_stroke_properties, nullptr, 0U,
          &raw_bevel_path_stroke_style) != com::ok ||
      raw_bevel_path_stroke_style == nullptr) {
    return 352;
  }
  com::pointer<compat::stroke_style> bevel_path_stroke_style;
  bevel_path_stroke_style.attach(raw_bevel_path_stroke_style);
  compat::stroke_style_properties round_path_stroke_properties =
      bevel_path_stroke_properties;
  round_path_stroke_properties.join = compat::line_join::round;
  compat::stroke_style *raw_round_path_stroke_style = nullptr;
  if (factory->CreateStrokeStyle(
          &round_path_stroke_properties, nullptr, 0U,
          &raw_round_path_stroke_style) != com::ok ||
      raw_round_path_stroke_style == nullptr) {
    return 356;
  }
  com::pointer<compat::stroke_style> round_path_stroke_style;
  round_path_stroke_style.attach(raw_round_path_stroke_style);
  compat::stroke_style_properties closed_cover_dash_properties =
      round_path_stroke_properties;
  closed_cover_dash_properties.dash = compat::dash_style::custom;
  constexpr std::array<float, 2U> closed_cover_dashes{{100.0F, 1.0F}};
  compat::stroke_style* raw_closed_cover_dash_style = nullptr;
  if (factory->CreateStrokeStyle(
          &closed_cover_dash_properties,
          closed_cover_dashes.data(),
          static_cast<std::uint32_t>(closed_cover_dashes.size()),
          &raw_closed_cover_dash_style) != com::ok ||
      raw_closed_cover_dash_style == nullptr) {
    return 402;
  }
  com::pointer<compat::stroke_style> closed_cover_dash_style;
  closed_cover_dash_style.attach(raw_closed_cover_dash_style);
  compat::stroke_style_properties dashed_path_stroke_properties =
      bevel_path_stroke_properties;
  dashed_path_stroke_properties.dash = compat::dash_style::dash;
  compat::stroke_style *raw_dashed_path_stroke_style = nullptr;
  if (factory->CreateStrokeStyle(
          &dashed_path_stroke_properties, nullptr, 0U,
          &raw_dashed_path_stroke_style) != com::ok ||
      raw_dashed_path_stroke_style == nullptr) {
    return 360;
  }
  com::pointer<compat::stroke_style> dashed_path_stroke_style;
  dashed_path_stroke_style.attach(raw_dashed_path_stroke_style);
  compat::stroke_style_properties round_dashed_path_stroke_properties =
      dashed_path_stroke_properties;
  round_dashed_path_stroke_properties.dash_cap = compat::cap_style::round;
  compat::stroke_style *raw_round_dashed_path_stroke_style = nullptr;
  if (factory->CreateStrokeStyle(
          &round_dashed_path_stroke_properties, nullptr, 0U,
          &raw_round_dashed_path_stroke_style) != com::ok ||
      raw_round_dashed_path_stroke_style == nullptr) {
    return 361;
  }
  com::pointer<compat::stroke_style> round_dashed_path_stroke_style;
  round_dashed_path_stroke_style.attach(
      raw_round_dashed_path_stroke_style);
  compat::stroke_style_properties square_dashed_path_stroke_properties =
      dashed_path_stroke_properties;
  square_dashed_path_stroke_properties.dash_cap = compat::cap_style::square;
  compat::stroke_style *raw_square_dashed_path_stroke_style = nullptr;
  if (factory->CreateStrokeStyle(
          &square_dashed_path_stroke_properties, nullptr, 0U,
          &raw_square_dashed_path_stroke_style) != com::ok ||
      raw_square_dashed_path_stroke_style == nullptr) {
    return 370;
  }
  com::pointer<compat::stroke_style> square_dashed_path_stroke_style;
  square_dashed_path_stroke_style.attach(
      raw_square_dashed_path_stroke_style);
  compat::stroke_style_properties triangle_dashed_path_stroke_properties =
      dashed_path_stroke_properties;
  triangle_dashed_path_stroke_properties.dash_cap =
      compat::cap_style::triangle;
  compat::stroke_style *raw_triangle_dashed_path_stroke_style = nullptr;
  if (factory->CreateStrokeStyle(
          &triangle_dashed_path_stroke_properties, nullptr, 0U,
          &raw_triangle_dashed_path_stroke_style) != com::ok ||
      raw_triangle_dashed_path_stroke_style == nullptr) {
    return 371;
  }
  com::pointer<compat::stroke_style> triangle_dashed_path_stroke_style;
  triangle_dashed_path_stroke_style.attach(
      raw_triangle_dashed_path_stroke_style);
  compat::stroke_style_properties miter_dashed_path_stroke_properties =
      dashed_path_stroke_properties;
  miter_dashed_path_stroke_properties.join = compat::line_join::miter;
  miter_dashed_path_stroke_properties.dash_offset = 0.5F;
  compat::stroke_style *raw_miter_dashed_path_stroke_style = nullptr;
  if (factory->CreateStrokeStyle(
          &miter_dashed_path_stroke_properties, nullptr, 0U,
          &raw_miter_dashed_path_stroke_style) != com::ok ||
      raw_miter_dashed_path_stroke_style == nullptr) {
    return 374;
  }
  com::pointer<compat::stroke_style> miter_dashed_path_stroke_style;
  miter_dashed_path_stroke_style.attach(raw_miter_dashed_path_stroke_style);
  compat::stroke_style_properties clipped_miter_dashed_path_properties =
      miter_dashed_path_stroke_properties;
  clipped_miter_dashed_path_properties.miter_limit = 1.0F;
  compat::stroke_style *raw_clipped_miter_dashed_path_style = nullptr;
  if (factory->CreateStrokeStyle(
          &clipped_miter_dashed_path_properties, nullptr, 0U,
          &raw_clipped_miter_dashed_path_style) != com::ok ||
      raw_clipped_miter_dashed_path_style == nullptr) {
    return 380;
  }
  com::pointer<compat::stroke_style> clipped_miter_dashed_path_style;
  clipped_miter_dashed_path_style.attach(
      raw_clipped_miter_dashed_path_style);
  compat::stroke_style_properties miter_or_bevel_dashed_path_properties =
      miter_dashed_path_stroke_properties;
  miter_or_bevel_dashed_path_properties.join =
      compat::line_join::miter_or_bevel;
  miter_or_bevel_dashed_path_properties.miter_limit = 1.0F;
  compat::stroke_style *raw_miter_or_bevel_dashed_path_style = nullptr;
  if (factory->CreateStrokeStyle(
          &miter_or_bevel_dashed_path_properties, nullptr, 0U,
          &raw_miter_or_bevel_dashed_path_style) != com::ok ||
      raw_miter_or_bevel_dashed_path_style == nullptr) {
    return 376;
  }
  com::pointer<compat::stroke_style> miter_or_bevel_dashed_path_style;
  miter_or_bevel_dashed_path_style.attach(
      raw_miter_or_bevel_dashed_path_style);
  compat::stroke_style_properties round_join_dashed_path_properties =
      miter_dashed_path_stroke_properties;
  round_join_dashed_path_properties.join = compat::line_join::round;
  compat::stroke_style *raw_round_join_dashed_path_style = nullptr;
  if (factory->CreateStrokeStyle(
          &round_join_dashed_path_properties, nullptr, 0U,
          &raw_round_join_dashed_path_style) != com::ok ||
      raw_round_join_dashed_path_style == nullptr) {
    return 378;
  }
  com::pointer<compat::stroke_style> round_join_dashed_path_style;
  round_join_dashed_path_style.attach(raw_round_join_dashed_path_style);
  std::int32_t open_body = 0;
  std::int32_t open_flat_start = 0;
  std::int32_t open_miter_corner = 0;
  std::int32_t open_round_corner = 0;
  std::int32_t open_dash_body = 0;
  std::int32_t open_dash_gap = 0;
  std::int32_t open_square_dash_cap = 0;
  std::int32_t open_terminal_square_dash = 0;
  std::int32_t open_terminal_round_dash = 0;
  std::int32_t open_terminal_triangle_dash = 0;
  if (open_query_path->StrokeContainsPoint(
          {2.0F, 0.75F}, 2.0F, nullptr, nullptr,
          core::default_flattening_tolerance, &open_body) != com::ok ||
      open_query_path->StrokeContainsPoint(
          {-0.25F, 0.0F}, 2.0F, nullptr, nullptr,
          core::default_flattening_tolerance, &open_flat_start) != com::ok ||
      open_query_path->StrokeContainsPoint(
          {4.75F, -0.75F}, 2.0F, nullptr, nullptr,
          core::default_flattening_tolerance, &open_miter_corner) != com::ok ||
      open_query_path->StrokeContainsPoint(
          {4.6F, -0.6F}, 2.0F, round_path_stroke_style.get(), nullptr,
          core::default_flattening_tolerance, &open_round_corner) != com::ok ||
      open_query_path->StrokeContainsPoint(
          {0.5F, 0.0F}, 0.5F, dashed_path_stroke_style.get(), nullptr,
          0.001F, &open_dash_body) != com::ok ||
      open_query_path->StrokeContainsPoint(
          {1.5F, 0.0F}, 0.5F, dashed_path_stroke_style.get(), nullptr,
          0.001F, &open_dash_gap) != com::ok ||
      open_query_path->StrokeContainsPoint(
          {1.1F, 0.0F}, 0.5F,
          square_dashed_path_stroke_style.get(), nullptr,
          0.001F, &open_square_dash_cap) != com::ok ||
      open_query_path->StrokeContainsPoint(
          {4.058F, 3.836F}, 0.5F,
          square_dashed_path_stroke_style.get(), nullptr,
          0.001F, &open_terminal_square_dash) != com::ok ||
      open_query_path->StrokeContainsPoint(
          {4.15F, 3.85F}, 0.5F,
          round_dashed_path_stroke_style.get(), nullptr,
          0.001F, &open_terminal_round_dash) != com::ok ||
      open_query_path->StrokeContainsPoint(
          {4.0F, 3.8F}, 0.5F,
          triangle_dashed_path_stroke_style.get(), nullptr,
          0.001F, &open_terminal_triangle_dash) != com::ok ||
      open_body == 0 || open_flat_start != 0 ||
      open_miter_corner == 0 || open_round_corner == 0 ||
      open_dash_body == 0 || open_dash_gap != 0 ||
      open_square_dash_cap == 0 || open_terminal_square_dash == 0 ||
      open_terminal_round_dash == 0 || open_terminal_triangle_dash == 0) {
    return 384;
  }
  compat::rectangle_f open_default_bounds{};
  compat::rectangle_f open_round_bounds{};
  compat::rectangle_f open_dashed_bounds{};
  compat::rectangle_f open_transformed_bounds{};
  if (open_query_path->GetWidenedBounds(
          2.0F, nullptr, nullptr,
          core::default_flattening_tolerance,
          &open_default_bounds) != com::ok ||
      open_query_path->GetWidenedBounds(
          2.0F, round_path_stroke_style.get(), nullptr,
          core::default_flattening_tolerance,
          &open_round_bounds) != com::ok ||
      open_query_path->GetWidenedBounds(
          0.5F, square_dashed_path_stroke_style.get(), nullptr,
          0.001F, &open_dashed_bounds) != com::ok ||
      open_query_path->GetWidenedBounds(
          2.0F, nullptr, &transform,
          core::default_flattening_tolerance,
          &open_transformed_bounds) != com::ok ||
      !approximately_equal(open_default_bounds.left, 0.0F) ||
      !approximately_equal(open_default_bounds.top, -1.0F) ||
      !approximately_equal(open_default_bounds.right, 5.0F) ||
      !approximately_equal(open_default_bounds.bottom, 4.0F) ||
      !approximately_equal(open_round_bounds.left, 0.0F) ||
      !approximately_equal(open_round_bounds.top, -1.0F) ||
      !approximately_equal(open_round_bounds.right, 5.0F) ||
      !approximately_equal(open_round_bounds.bottom, 4.0F) ||
      !approximately_equal(open_dashed_bounds.left, 0.0F) ||
      !approximately_equal(open_dashed_bounds.top, -0.25F) ||
      !approximately_equal(open_dashed_bounds.right, 4.25F) ||
      !approximately_equal(open_dashed_bounds.bottom, 4.0F) ||
      !approximately_equal(open_transformed_bounds.left, 10.0F) ||
      !approximately_equal(open_transformed_bounds.top, -7.0F) ||
      !approximately_equal(open_transformed_bounds.right, 20.0F) ||
      !approximately_equal(open_transformed_bounds.bottom, 8.0F)) {
    return 388;
  }
  auto* raw_open_default_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> open_default_widen_sink;
  open_default_widen_sink.attach(raw_open_default_widen_sink);
  auto* raw_open_round_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> open_round_widen_sink;
  open_round_widen_sink.attach(raw_open_round_widen_sink);
  auto* raw_open_dashed_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> open_dashed_widen_sink;
  open_dashed_widen_sink.attach(raw_open_dashed_widen_sink);
  auto* raw_open_round_dashed_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      open_round_dashed_widen_sink;
  open_round_dashed_widen_sink.attach(raw_open_round_dashed_widen_sink);
  auto* raw_open_triangle_dashed_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      open_triangle_dashed_widen_sink;
  open_triangle_dashed_widen_sink.attach(
      raw_open_triangle_dashed_widen_sink);
  auto* raw_open_transformed_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      open_transformed_widen_sink;
  open_transformed_widen_sink.attach(raw_open_transformed_widen_sink);
  auto* raw_open_curve_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> open_curve_widen_sink;
  open_curve_widen_sink.attach(raw_open_curve_widen_sink);
  auto* raw_open_curve_round_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      open_curve_round_widen_sink;
  open_curve_round_widen_sink.attach(raw_open_curve_round_widen_sink);
  if (open_query_path->Widen(
          2.0F, nullptr, nullptr,
          core::default_flattening_tolerance,
          open_default_widen_sink.get()) != com::ok ||
      open_query_path->Widen(
          2.0F, round_path_stroke_style.get(), nullptr,
          core::default_flattening_tolerance,
          open_round_widen_sink.get()) != com::ok ||
      open_query_path->Widen(
          0.5F, square_dashed_path_stroke_style.get(), nullptr,
          0.001F, open_dashed_widen_sink.get()) != com::ok ||
      open_query_path->Widen(
          0.5F, round_dashed_path_stroke_style.get(), nullptr,
          0.001F, open_round_dashed_widen_sink.get()) != com::ok ||
      open_query_path->Widen(
          0.5F, triangle_dashed_path_stroke_style.get(), nullptr,
          0.001F, open_triangle_dashed_widen_sink.get()) != com::ok ||
      open_query_path->Widen(
          2.0F, nullptr, &transform,
          core::default_flattening_tolerance,
          open_transformed_widen_sink.get()) != com::ok ||
      open_curve_path->Widen(
          1.0F, nullptr, nullptr,
          0.02F, open_curve_widen_sink.get()) != com::ok ||
      open_curve_path->Widen(
          1.0F, round_path_stroke_style.get(), nullptr,
          0.02F, open_curve_round_widen_sink.get()) != com::ok ||
      raw_open_default_widen_sink->fill_mode !=
          compat::fill_mode::winding ||
      raw_open_default_widen_sink->segment_flags !=
          compat::path_segment::force_unstroked ||
      raw_open_default_widen_sink->begin_count != 1U ||
      raw_open_default_widen_sink->end_count != 1U ||
      raw_open_default_widen_sink->figure_end !=
          compat::figure_end::closed ||
      raw_open_round_widen_sink->bezier_count == 0U ||
      raw_open_dashed_widen_sink->begin_count == 0U ||
      raw_open_dashed_widen_sink->begin_count !=
          raw_open_dashed_widen_sink->end_count ||
      !captured_fill_contains(
          *raw_open_default_widen_sink, {4.75F, -0.75F}) ||
      !captured_fill_contains(
          *raw_open_round_widen_sink, {4.6F, -0.6F}) ||
      !captured_fill_contains(
          *raw_open_dashed_widen_sink, {1.1F, 0.0F}) ||
      !captured_fill_contains(
          *raw_open_dashed_widen_sink, {4.058F, 3.836F}) ||
      raw_open_round_dashed_widen_sink->bezier_count == 0U ||
      !captured_fill_contains(
          *raw_open_round_dashed_widen_sink, {4.15F, 3.85F}) ||
      !captured_fill_contains(
          *raw_open_triangle_dashed_widen_sink, {4.0F, 3.8F}) ||
      !captured_fill_contains(
          *raw_open_transformed_widen_sink, {19.5F, -6.25F}) ||
      raw_open_curve_widen_sink->begin_count != 1U ||
      raw_open_curve_round_widen_sink->bezier_count == 0U) {
    return 389;
  }
  auto* raw_round_segment_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> round_segment_widen_sink;
  round_segment_widen_sink.attach(raw_round_segment_widen_sink);
  auto* raw_unstroked_segment_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      unstroked_segment_widen_sink;
  unstroked_segment_widen_sink.attach(raw_unstroked_segment_widen_sink);
  auto* raw_unstroked_dashed_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      unstroked_dashed_widen_sink;
  unstroked_dashed_widen_sink.attach(raw_unstroked_dashed_widen_sink);
  compat::rectangle_f unstroked_segment_bounds{};
  std::int32_t default_miter_corner = 0;
  std::int32_t forced_round_corner = 0;
  std::int32_t unstroked_first = 0;
  std::int32_t unstroked_gap = 0;
  std::int32_t unstroked_last = 0;
  if (open_query_path->StrokeContainsPoint(
          {4.8F, -0.8F}, 2.0F, nullptr, nullptr, 0.01F,
          &default_miter_corner) != com::ok ||
      round_segment_path->StrokeContainsPoint(
          {4.8F, -0.8F}, 2.0F, nullptr, nullptr, 0.01F,
          &forced_round_corner) != com::ok ||
      unstroked_segment_path->StrokeContainsPoint(
          {2.0F, 0.0F}, 2.0F, nullptr, nullptr, 0.01F,
          &unstroked_first) != com::ok ||
      unstroked_segment_path->StrokeContainsPoint(
          {4.0F, 2.0F}, 2.0F, nullptr, nullptr, 0.01F,
          &unstroked_gap) != com::ok ||
      unstroked_segment_path->StrokeContainsPoint(
          {6.0F, 4.0F}, 2.0F, nullptr, nullptr, 0.01F,
          &unstroked_last) != com::ok ||
      unstroked_segment_path->GetWidenedBounds(
          2.0F, nullptr, nullptr, 0.01F,
          &unstroked_segment_bounds) != com::ok ||
      round_segment_path->Widen(
          2.0F, nullptr, nullptr, 0.01F,
          round_segment_widen_sink.get()) != com::ok ||
      unstroked_segment_path->Widen(
          2.0F, nullptr, nullptr, 0.01F,
          unstroked_segment_widen_sink.get()) != com::ok ||
      unstroked_segment_path->Widen(
          0.5F, square_dashed_path_stroke_style.get(), nullptr, 0.001F,
          unstroked_dashed_widen_sink.get()) != com::ok ||
      default_miter_corner == 0 || forced_round_corner != 0 ||
      unstroked_first == 0 || unstroked_gap != 0 || unstroked_last == 0 ||
      !approximately_equal(unstroked_segment_bounds.left, 0.0F) ||
      !approximately_equal(unstroked_segment_bounds.top, -1.0F) ||
      !approximately_equal(unstroked_segment_bounds.right, 8.0F) ||
      !approximately_equal(unstroked_segment_bounds.bottom, 5.0F) ||
      raw_round_segment_widen_sink->bezier_count == 0U ||
      raw_unstroked_segment_widen_sink->begin_count != 2U ||
      raw_unstroked_segment_widen_sink->begin_count !=
          raw_unstroked_segment_widen_sink->end_count ||
      raw_unstroked_dashed_widen_sink->begin_count < 2U ||
      captured_fill_contains(
          *raw_round_segment_widen_sink, {4.8F, -0.8F}) ||
      !captured_fill_contains(
          *raw_unstroked_segment_widen_sink, {2.0F, 0.0F}) ||
      captured_fill_contains(
          *raw_unstroked_segment_widen_sink, {4.0F, 2.0F}) ||
      !captured_fill_contains(
          *raw_unstroked_segment_widen_sink, {6.0F, 4.0F})) {
    return 413;
  }
  for (std::uint32_t y_index = 0U; y_index < 22U; ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 38U; ++x_index) {
      const compat::point_2f point{
          -0.37F + static_cast<float>(x_index) * 0.241F,
          -1.19F + static_cast<float>(y_index) * 0.303F};
      std::int32_t solid_contains = 0;
      std::int32_t dashed_contains = 0;
      if (unstroked_segment_path->StrokeContainsPoint(
              point, 2.0F, nullptr, nullptr, 0.01F,
              &solid_contains) != com::ok ||
          unstroked_segment_path->StrokeContainsPoint(
              point, 0.5F, square_dashed_path_stroke_style.get(), nullptr,
              0.001F, &dashed_contains) != com::ok ||
          captured_fill_contains(
              *raw_unstroked_segment_widen_sink, point) !=
              (solid_contains != 0) ||
          captured_fill_contains(
              *raw_unstroked_dashed_widen_sink, point) !=
              (dashed_contains != 0)) {
        return 414;
      }
    }
  }
  for (std::uint32_t y_index = 0U; y_index < 24U; ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 24U; ++x_index) {
      const compat::point_2f point{
          -1.183F + static_cast<float>(x_index) * 0.291F,
          -1.271F + static_cast<float>(y_index) * 0.283F};
      std::int32_t default_contains = 0;
      std::int32_t round_contains = 0;
      std::int32_t dashed_contains = 0;
      if (open_query_path->StrokeContainsPoint(
              point, 2.0F, nullptr, nullptr,
              core::default_flattening_tolerance,
              &default_contains) != com::ok ||
          open_query_path->StrokeContainsPoint(
              point, 2.0F, round_path_stroke_style.get(), nullptr,
              core::default_flattening_tolerance,
              &round_contains) != com::ok ||
          open_query_path->StrokeContainsPoint(
              point, 0.5F, square_dashed_path_stroke_style.get(), nullptr,
              0.001F, &dashed_contains) != com::ok ||
          captured_fill_contains(*raw_open_default_widen_sink, point) !=
              (default_contains != 0) ||
          captured_fill_contains(*raw_open_round_widen_sink, point) !=
              (round_contains != 0) ||
          captured_fill_contains(*raw_open_dashed_widen_sink, point) !=
              (dashed_contains != 0)) {
        std::fprintf(
            stderr,
            "open widen mismatch point=%g,%g default=%d/%d round=%d/%d "
            "dashed=%d/%d\n",
            point.x,
            point.y,
            captured_fill_contains(*raw_open_default_widen_sink, point)
                ? 1
                : 0,
            default_contains,
            captured_fill_contains(*raw_open_round_widen_sink, point)
                ? 1
                : 0,
            round_contains,
            captured_fill_contains(*raw_open_dashed_widen_sink, point)
                ? 1
                : 0,
            dashed_contains);
        return 390;
      }
    }
  }
  for (std::uint32_t y_index = 0U; y_index < 34U; ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 42U; ++x_index) {
      const compat::point_2f point{
          -1.17F + static_cast<float>(x_index) * 0.293F,
          -4.37F + static_cast<float>(y_index) * 0.271F};
      std::int32_t default_contains = 0;
      std::int32_t round_contains = 0;
      if (open_curve_path->StrokeContainsPoint(
              point, 1.0F, nullptr, nullptr,
              0.02F, &default_contains) != com::ok ||
          open_curve_path->StrokeContainsPoint(
              point, 1.0F, round_path_stroke_style.get(), nullptr,
              0.02F, &round_contains) != com::ok ||
          captured_fill_contains(*raw_open_curve_widen_sink, point) !=
              (default_contains != 0) ||
          captured_fill_contains(*raw_open_curve_round_widen_sink, point) !=
              (round_contains != 0)) {
        std::fprintf(
            stderr,
            "open curve widen mismatch point=%g,%g default=%d/%d "
            "round=%d/%d\n",
            point.x,
            point.y,
            captured_fill_contains(*raw_open_curve_widen_sink, point)
                ? 1
                : 0,
            default_contains,
            captured_fill_contains(*raw_open_curve_round_widen_sink, point)
                ? 1
                : 0,
            round_contains);
        std::fprintf(
            stderr,
            "curve widen segments=%zu/%zu lines=%u/%u beziers=%u/%u\n",
            raw_open_curve_widen_sink->captured_segment_count,
            raw_open_curve_round_widen_sink->captured_segment_count,
            raw_open_curve_widen_sink->line_count,
            raw_open_curve_round_widen_sink->line_count,
            raw_open_curve_widen_sink->bezier_count,
            raw_open_curve_round_widen_sink->bezier_count);
        return 406;
      }
    }
  }
  std::int32_t multi_closed_contains = 0;
  std::int32_t multi_open_contains = 0;
  std::int32_t multi_open_dash_body = 0;
  std::int32_t multi_open_dash_gap = 0;
  compat::rectangle_f multi_default_bounds{};
  compat::rectangle_f multi_dashed_bounds{};
  if (multi_query_path->StrokeContainsPoint(
          {0.0F, 1.0F}, 1.0F, nullptr, nullptr,
          core::default_flattening_tolerance,
          &multi_closed_contains) != com::ok ||
      multi_query_path->StrokeContainsPoint(
          {12.0F, 0.4F}, 1.0F, nullptr, nullptr,
          core::default_flattening_tolerance,
          &multi_open_contains) != com::ok ||
      multi_query_path->StrokeContainsPoint(
          {10.5F, 0.0F}, 0.5F,
          square_dashed_path_stroke_style.get(), nullptr,
          0.001F, &multi_open_dash_body) != com::ok ||
      multi_query_path->StrokeContainsPoint(
          {11.5F, 0.0F}, 0.5F,
          square_dashed_path_stroke_style.get(), nullptr,
          0.001F, &multi_open_dash_gap) != com::ok ||
      multi_query_path->GetWidenedBounds(
          2.0F, nullptr, nullptr,
          core::default_flattening_tolerance,
          &multi_default_bounds) != com::ok ||
      multi_query_path->GetWidenedBounds(
          0.5F, square_dashed_path_stroke_style.get(), nullptr,
          0.001F, &multi_dashed_bounds) != com::ok ||
      multi_closed_contains == 0 || multi_open_contains == 0 ||
      multi_open_dash_body == 0 || multi_open_dash_gap != 0 ||
      !approximately_equal(multi_default_bounds.left, -1.0F) ||
      !approximately_equal(multi_default_bounds.top, -1.0F) ||
      !approximately_equal(multi_default_bounds.right, 15.0F) ||
      !approximately_equal(multi_default_bounds.bottom, 4.0F) ||
      multi_dashed_bounds.left > -0.25F ||
      multi_dashed_bounds.right < 14.25F) {
    return 393;
  }
  auto* raw_multi_default_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> multi_default_widen_sink;
  multi_default_widen_sink.attach(raw_multi_default_widen_sink);
  auto* raw_multi_dashed_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> multi_dashed_widen_sink;
  multi_dashed_widen_sink.attach(raw_multi_dashed_widen_sink);
  const com::result multi_default_widen_status = multi_query_path->Widen(
          2.0F, nullptr, nullptr,
          core::default_flattening_tolerance,
          multi_default_widen_sink.get());
  const com::result multi_dashed_widen_status = multi_query_path->Widen(
          0.5F, square_dashed_path_stroke_style.get(), nullptr,
          0.001F, multi_dashed_widen_sink.get());
  if (multi_default_widen_status != com::ok ||
      multi_dashed_widen_status != com::ok ||
      raw_multi_default_widen_sink->fill_mode !=
          compat::fill_mode::winding ||
      raw_multi_default_widen_sink->segment_flags !=
          compat::path_segment::force_unstroked ||
      raw_multi_default_widen_sink->begin_count != 2U ||
      raw_multi_default_widen_sink->begin_count !=
          raw_multi_default_widen_sink->end_count ||
      raw_multi_dashed_widen_sink->begin_count < 2U ||
      raw_multi_dashed_widen_sink->begin_count !=
          raw_multi_dashed_widen_sink->end_count) {
    std::fprintf(
        stderr,
        "multi widen status=%ld/%ld figures=%u/%u ends=%u/%u\n",
        static_cast<long>(multi_default_widen_status),
        static_cast<long>(multi_dashed_widen_status),
        raw_multi_default_widen_sink->begin_count,
        raw_multi_dashed_widen_sink->begin_count,
        raw_multi_default_widen_sink->end_count,
        raw_multi_dashed_widen_sink->end_count);
    return 395;
  }
  for (std::uint32_t y_index = 0U; y_index < 28U; ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 52U; ++x_index) {
      const compat::point_2f point{
          -1.31F + static_cast<float>(x_index) * 0.327F,
          -1.29F + static_cast<float>(y_index) * 0.247F};
      std::int32_t default_contains = 0;
      std::int32_t dashed_contains = 0;
      if (multi_query_path->StrokeContainsPoint(
              point, 2.0F, nullptr, nullptr,
              core::default_flattening_tolerance,
              &default_contains) != com::ok ||
          multi_query_path->StrokeContainsPoint(
              point, 0.5F,
              square_dashed_path_stroke_style.get(), nullptr,
              0.001F, &dashed_contains) != com::ok ||
          captured_fill_contains(*raw_multi_default_widen_sink, point) !=
              (default_contains != 0) ||
          captured_fill_contains(*raw_multi_dashed_widen_sink, point) !=
              (dashed_contains != 0)) {
        std::fprintf(
            stderr,
            "multi-figure widen mismatch point=%g,%g default=%d/%d "
            "dashed=%d/%d\n",
            point.x,
            point.y,
            captured_fill_contains(*raw_multi_default_widen_sink, point)
                ? 1
                : 0,
            default_contains,
            captured_fill_contains(*raw_multi_dashed_widen_sink, point)
                ? 1
                : 0,
            dashed_contains);
        return 396;
      }
    }
  }
  auto* raw_multi_rejected_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      multi_rejected_widen_sink;
  multi_rejected_widen_sink.attach(raw_multi_rejected_widen_sink);
  if (multi_rejected_widen_path->Widen(
          1.0F, nullptr, nullptr,
          core::default_flattening_tolerance,
          multi_rejected_widen_sink.get()) != compat::not_implemented ||
      raw_multi_rejected_widen_sink->begin_count != 0U ||
      raw_multi_rejected_widen_sink->end_count != 0U ||
      raw_multi_rejected_widen_sink->line_count != 0U ||
      raw_multi_rejected_widen_sink->bezier_count != 0U) {
    return 399;
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
  struct path_stroke_case final {
    compat::point_2f point{};
    const compat::matrix_3x2_f *transform = nullptr;
    bool expected = false;
  };
  const std::array<path_stroke_case, 7U> path_stroke_cases{{
      {{0.1F, 1.1F}, nullptr, true},
      {{-0.5F, 0.5F}, nullptr, false},
      {{3.0F, 5.0F}, nullptr, false},
      {{0.5F, 4.0F}, nullptr, true},
      {{1.5F, 4.0F}, nullptr, true},
      {{10.2F, -0.7F}, &transform, true},
      {{16.0F, 11.0F}, &transform, false},
  }};
  for (std::size_t stroke_case_index = 0U;
       stroke_case_index < path_stroke_cases.size();
       ++stroke_case_index) {
    const path_stroke_case &stroke_case =
        path_stroke_cases[stroke_case_index];
    std::int32_t stroke_contains = 0;
    if (query_path->StrokeContainsPoint(
            stroke_case.point, 2.0F, nullptr, stroke_case.transform,
            core::default_flattening_tolerance, &stroke_contains) != com::ok ||
        (stroke_contains != 0) != stroke_case.expected) {
      return 330;
    }
  }
  std::int32_t bevel_corner_outside = 0;
  std::int32_t bevel_corner_inside = 0;
  if (query_path->StrokeContainsPoint(
          {0.1F, 1.1F}, 2.0F, bevel_path_stroke_style.get(), nullptr,
          core::default_flattening_tolerance, &bevel_corner_outside) !=
          com::ok ||
      query_path->StrokeContainsPoint(
          {0.6F, 1.6F}, 2.0F, bevel_path_stroke_style.get(), nullptr,
          core::default_flattening_tolerance, &bevel_corner_inside) !=
          com::ok ||
      bevel_corner_outside != 0 || bevel_corner_inside == 0) {
    return 353;
  }
  std::int32_t round_corner_outside = 0;
  std::int32_t round_corner_inside = 0;
  if (query_path->StrokeContainsPoint(
          {0.1F, 1.1F}, 2.0F, round_path_stroke_style.get(), nullptr,
          core::default_flattening_tolerance, &round_corner_outside) !=
          com::ok ||
      query_path->StrokeContainsPoint(
          {0.35F, 1.35F}, 2.0F, round_path_stroke_style.get(), nullptr,
          core::default_flattening_tolerance, &round_corner_inside) !=
          com::ok ||
      round_corner_outside != 0 || round_corner_inside == 0) {
    return 357;
  }
  std::int32_t dash_body = 0;
  std::int32_t dash_gap = 0;
  std::int32_t flat_dash_cap_gap = 0;
  std::int32_t round_dash_cap_gap = 0;
  std::int32_t square_dash_cap_gap = 0;
  std::int32_t triangle_dash_cap_gap = 0;
  std::int32_t square_dash_source_seam = 0;
  std::int32_t triangle_dash_source_seam = 0;
  std::int32_t round_dash_source_seam = 0;
  std::int32_t miter_dash_corner = 0;
  std::int32_t miter_or_bevel_dash_corner = 0;
  std::int32_t round_join_dash_corner = 0;
  std::int32_t clipped_miter_dash_inside = 0;
  std::int32_t clipped_miter_dash_tip = 0;
  compat::rectangle_f dashed_path_widened_bounds{};
  compat::rectangle_f round_dashed_path_widened_bounds{};
  compat::rectangle_f transformed_round_dashed_path_widened_bounds{};
  compat::rectangle_f zero_dashed_path_widened_bounds{};
  constexpr float dash_hit_tolerance = 0.001F;
  if (query_path->StrokeContainsPoint(
          {1.5F, 2.0F}, 0.5F, dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &dash_body) != com::ok ||
      query_path->StrokeContainsPoint(
          {2.5F, 2.0F}, 0.5F, dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &dash_gap) != com::ok ||
      query_path->StrokeContainsPoint(
          {2.2F, 2.0F}, 0.5F, dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &flat_dash_cap_gap) != com::ok ||
      query_path->StrokeContainsPoint(
          {2.2F, 2.0F}, 0.5F, round_dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &round_dash_cap_gap) != com::ok ||
      query_path->StrokeContainsPoint(
          {2.2F, 2.0F}, 0.5F, square_dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &square_dash_cap_gap) != com::ok ||
      query_path->StrokeContainsPoint(
          {2.2F, 2.0F}, 0.5F, triangle_dashed_path_stroke_style.get(),
          nullptr, dash_hit_tolerance, &triangle_dash_cap_gap) != com::ok ||
      query_path->StrokeContainsPoint(
          {0.978F, 1.774F}, 0.5F,
          square_dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &square_dash_source_seam) != com::ok ||
      query_path->StrokeContainsPoint(
          {0.978F, 1.774F}, 0.5F,
          triangle_dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &triangle_dash_source_seam) != com::ok ||
      query_path->StrokeContainsPoint(
          {0.978F, 1.774F}, 0.5F,
          round_dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &round_dash_source_seam) != com::ok ||
      query_path->StrokeContainsPoint(
          {5.2F, 1.8F}, 0.5F,
          miter_dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &miter_dash_corner) != com::ok ||
      query_path->StrokeContainsPoint(
          {5.2F, 1.8F}, 0.5F,
          miter_or_bevel_dashed_path_style.get(), nullptr,
          dash_hit_tolerance, &miter_or_bevel_dash_corner) != com::ok ||
      query_path->StrokeContainsPoint(
          {5.17F, 1.83F}, 0.5F,
          round_join_dashed_path_style.get(), nullptr,
          dash_hit_tolerance, &round_join_dash_corner) != com::ok ||
      query_path->StrokeContainsPoint(
          {5.17F, 1.83F}, 0.5F,
          clipped_miter_dashed_path_style.get(), nullptr,
          dash_hit_tolerance, &clipped_miter_dash_inside) != com::ok ||
      query_path->StrokeContainsPoint(
          {5.2F, 1.8F}, 0.5F,
          clipped_miter_dashed_path_style.get(), nullptr,
          dash_hit_tolerance, &clipped_miter_dash_tip) != com::ok ||
      dash_body == 0 || dash_gap != 0 || flat_dash_cap_gap != 0 ||
      round_dash_cap_gap == 0 || square_dash_cap_gap == 0 ||
      triangle_dash_cap_gap == 0 || miter_dash_corner == 0 ||
      miter_or_bevel_dash_corner != 0 || round_join_dash_corner == 0 ||
      clipped_miter_dash_inside == 0 || clipped_miter_dash_tip != 0) {
    std::fprintf(stderr,
                 "dash joins miter=%d miter-or-bevel=%d round=%d "
                 "clipped=%d/%d\n",
                 miter_dash_corner, miter_or_bevel_dash_corner,
                 round_join_dash_corner, clipped_miter_dash_inside,
                 clipped_miter_dash_tip);
    return 362;
  }
  if (query_path->GetWidenedBounds(
          0.5F, dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &dashed_path_widened_bounds) != com::ok ||
      query_path->GetWidenedBounds(
          0.5F, round_dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &round_dashed_path_widened_bounds) != com::ok ||
      query_path->GetWidenedBounds(
          0.5F, round_dashed_path_stroke_style.get(), &transform,
          dash_hit_tolerance,
          &transformed_round_dashed_path_widened_bounds) != com::ok ||
      query_path->GetWidenedBounds(
          0.0F, dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, &zero_dashed_path_widened_bounds) != com::ok ||
      !approximately_equal(dashed_path_widened_bounds.left, 0.75F) ||
      !approximately_equal(dashed_path_widened_bounds.top, 1.75F) ||
      !approximately_equal(dashed_path_widened_bounds.right, 5.25F) ||
      !approximately_equal(dashed_path_widened_bounds.bottom, 8.25F) ||
      !approximately_equal(round_dashed_path_widened_bounds.left, 0.75F) ||
      !approximately_equal(round_dashed_path_widened_bounds.top, 1.75F) ||
      !approximately_equal(round_dashed_path_widened_bounds.right, 5.25F) ||
      !approximately_equal(round_dashed_path_widened_bounds.bottom, 8.25F) ||
      !approximately_equal(
          transformed_round_dashed_path_widened_bounds.left, 11.5F) ||
      !approximately_equal(
          transformed_round_dashed_path_widened_bounds.top, 1.25F) ||
      !approximately_equal(
          transformed_round_dashed_path_widened_bounds.right, 20.5F) ||
      !approximately_equal(
          transformed_round_dashed_path_widened_bounds.bottom, 20.75F) ||
      !approximately_equal(zero_dashed_path_widened_bounds.left, 1.0F) ||
      !approximately_equal(zero_dashed_path_widened_bounds.top, 2.0F) ||
      !approximately_equal(zero_dashed_path_widened_bounds.right, 5.0F) ||
      !approximately_equal(zero_dashed_path_widened_bounds.bottom, 8.0F)) {
    return 366;
  }
  std::int32_t zero_width_edge = 0;
  std::int32_t zero_width_interior = 0;
  if (query_path->StrokeContainsPoint(
          {1.0F, 4.0F}, 0.0F, nullptr, nullptr,
          core::default_flattening_tolerance, &zero_width_edge) != com::ok ||
      query_path->StrokeContainsPoint(
          {3.0F, 4.0F}, 0.0F, nullptr, nullptr,
          core::default_flattening_tolerance, &zero_width_interior) !=
          com::ok ||
      zero_width_edge != 0 || zero_width_interior != 0) {
    return 334;
  }
  compat::rectangle_f path_widened_bounds{};
  compat::rectangle_f transformed_path_widened_bounds{};
  compat::rectangle_f zero_path_widened_bounds{};
  compat::rectangle_f concave_path_widened_bounds{};
  if (query_path->GetWidenedBounds(
          2.0F, nullptr, nullptr, core::default_flattening_tolerance,
          &path_widened_bounds) != com::ok ||
      query_path->GetWidenedBounds(
          2.0F, nullptr, &transform, core::default_flattening_tolerance,
          &transformed_path_widened_bounds) != com::ok ||
      query_path->GetWidenedBounds(
          0.0F, nullptr, nullptr, core::default_flattening_tolerance,
          &zero_path_widened_bounds) != com::ok ||
      boolean_path->GetWidenedBounds(
          2.0F, nullptr, nullptr, core::default_flattening_tolerance,
          &concave_path_widened_bounds) != com::ok ||
      !approximately_equal(path_widened_bounds.left, 0.0F) ||
      !approximately_equal(path_widened_bounds.top, 1.0F) ||
      !approximately_equal(path_widened_bounds.right, 6.0F) ||
      !approximately_equal(path_widened_bounds.bottom, 9.0F) ||
      !approximately_equal(transformed_path_widened_bounds.left, 10.0F) ||
      !approximately_equal(transformed_path_widened_bounds.top, -1.0F) ||
      !approximately_equal(transformed_path_widened_bounds.right, 22.0F) ||
      !approximately_equal(transformed_path_widened_bounds.bottom, 23.0F) ||
      !approximately_equal(zero_path_widened_bounds.left, 1.0F) ||
      !approximately_equal(zero_path_widened_bounds.top, 2.0F) ||
      !approximately_equal(zero_path_widened_bounds.right, 5.0F) ||
      !approximately_equal(zero_path_widened_bounds.bottom, 8.0F) ||
      !approximately_equal(concave_path_widened_bounds.left, 2.0F) ||
      !approximately_equal(concave_path_widened_bounds.top, 0.0F) ||
      !approximately_equal(concave_path_widened_bounds.right, 8.0F) ||
      !approximately_equal(concave_path_widened_bounds.bottom, 10.0F)) {
    return 342;
  }
  auto *raw_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> path_widen_sink;
  path_widen_sink.attach(raw_path_widen_sink);
  auto *raw_collapsed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> collapsed_path_widen_sink;
  collapsed_path_widen_sink.attach(raw_collapsed_path_widen_sink);
  auto *raw_consumed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> consumed_path_widen_sink;
  consumed_path_widen_sink.attach(raw_consumed_path_widen_sink);
  auto *raw_bevel_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> bevel_path_widen_sink;
  bevel_path_widen_sink.attach(raw_bevel_path_widen_sink);
  auto *raw_round_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> round_path_widen_sink;
  round_path_widen_sink.attach(raw_round_path_widen_sink);
  auto *raw_closed_cover_dash_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      closed_cover_dash_widen_sink;
  closed_cover_dash_widen_sink.attach(raw_closed_cover_dash_widen_sink);
  auto *raw_zero_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> zero_path_widen_sink;
  zero_path_widen_sink.attach(raw_zero_path_widen_sink);
  auto *raw_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> dashed_path_widen_sink;
  dashed_path_widen_sink.attach(raw_dashed_path_widen_sink);
  auto *raw_square_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      square_dashed_path_widen_sink;
  square_dashed_path_widen_sink.attach(raw_square_dashed_path_widen_sink);
  auto *raw_triangle_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      triangle_dashed_path_widen_sink;
  triangle_dashed_path_widen_sink.attach(raw_triangle_dashed_path_widen_sink);
  auto *raw_round_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      round_dashed_path_widen_sink;
  round_dashed_path_widen_sink.attach(raw_round_dashed_path_widen_sink);
  auto *raw_miter_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      miter_dashed_path_widen_sink;
  miter_dashed_path_widen_sink.attach(raw_miter_dashed_path_widen_sink);
  auto *raw_miter_or_bevel_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      miter_or_bevel_dashed_path_widen_sink;
  miter_or_bevel_dashed_path_widen_sink.attach(
      raw_miter_or_bevel_dashed_path_widen_sink);
  auto *raw_round_join_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      round_join_dashed_path_widen_sink;
  round_join_dashed_path_widen_sink.attach(
      raw_round_join_dashed_path_widen_sink);
  auto *raw_clipped_miter_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      clipped_miter_dashed_path_widen_sink;
  clipped_miter_dashed_path_widen_sink.attach(
      raw_clipped_miter_dashed_path_widen_sink);
  if (query_path->Widen(
          2.0F, nullptr, nullptr, core::default_flattening_tolerance,
          path_widen_sink.get()) != com::ok ||
      raw_path_widen_sink->fill_mode != compat::fill_mode::winding ||
      raw_path_widen_sink->segment_flags !=
          compat::path_segment::force_unstroked ||
      raw_path_widen_sink->begin_count != 2U ||
      raw_path_widen_sink->end_count != 2U ||
      raw_path_widen_sink->line_count != 6U ||
      query_path->Widen(
          4.0F, nullptr, nullptr,
          core::default_flattening_tolerance,
          collapsed_path_widen_sink.get()) != com::ok ||
      query_path->Widen(
          5.0F, nullptr, nullptr,
          core::default_flattening_tolerance,
          consumed_path_widen_sink.get()) != com::ok ||
      raw_collapsed_path_widen_sink->begin_count != 1U ||
      raw_collapsed_path_widen_sink->end_count != 1U ||
      raw_consumed_path_widen_sink->begin_count != 1U ||
      raw_consumed_path_widen_sink->end_count != 1U ||
      query_path->Widen(
          2.0F, bevel_path_stroke_style.get(), nullptr,
          core::default_flattening_tolerance,
          bevel_path_widen_sink.get()) != com::ok ||
      query_path->Widen(
          2.0F, round_path_stroke_style.get(), nullptr,
          core::default_flattening_tolerance,
          round_path_widen_sink.get()) != com::ok ||
      raw_bevel_path_widen_sink->begin_count != 2U ||
      raw_bevel_path_widen_sink->begin_count !=
          raw_bevel_path_widen_sink->end_count ||
      raw_round_path_widen_sink->begin_count != 2U ||
      raw_round_path_widen_sink->begin_count !=
          raw_round_path_widen_sink->end_count ||
      raw_round_path_widen_sink->bezier_count == 0U ||
      query_path->Widen(
          0.25F, closed_cover_dash_style.get(), nullptr,
          0.001F, closed_cover_dash_widen_sink.get()) != com::ok ||
      raw_closed_cover_dash_widen_sink->begin_count != 2U ||
      raw_closed_cover_dash_widen_sink->begin_count !=
          raw_closed_cover_dash_widen_sink->end_count ||
      raw_closed_cover_dash_widen_sink->bezier_count == 0U ||
      query_path->Widen(
          0.0F, nullptr, nullptr, core::default_flattening_tolerance,
          zero_path_widen_sink.get()) != com::ok ||
      raw_zero_path_widen_sink->begin_count != 0U ||
      raw_zero_path_widen_sink->end_count != 0U ||
      raw_zero_path_widen_sink->line_count != 0U ||
      raw_zero_path_widen_sink->bezier_count != 0U ||
      raw_zero_path_widen_sink->set_fill_mode_count != 1U ||
      raw_zero_path_widen_sink->set_segment_flags_count != 0U) {
    return 344;
  }
  for (std::uint32_t y_index = 0U; y_index < 28U; ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 22U; ++x_index) {
      const compat::point_2f point{
          -0.41F + static_cast<float>(x_index) * 0.317F,
          0.59F + static_cast<float>(y_index) * 0.347F};
      std::int32_t bevel_contains = 0;
      std::int32_t round_contains = 0;
      std::int32_t closed_cover_dash_contains = 0;
      std::int32_t collapsed_contains = 0;
      std::int32_t consumed_contains = 0;
      if (query_path->StrokeContainsPoint(
              point, 2.0F, bevel_path_stroke_style.get(), nullptr,
              core::default_flattening_tolerance,
              &bevel_contains) != com::ok ||
          query_path->StrokeContainsPoint(
              point, 2.0F, round_path_stroke_style.get(), nullptr,
              core::default_flattening_tolerance,
              &round_contains) != com::ok ||
          query_path->StrokeContainsPoint(
              point, 0.25F, closed_cover_dash_style.get(), nullptr,
              0.001F, &closed_cover_dash_contains) != com::ok ||
          query_path->StrokeContainsPoint(
              point, 4.0F, nullptr, nullptr,
              core::default_flattening_tolerance,
              &collapsed_contains) != com::ok ||
          query_path->StrokeContainsPoint(
              point, 5.0F, nullptr, nullptr,
              core::default_flattening_tolerance,
              &consumed_contains) != com::ok ||
          captured_fill_contains(*raw_bevel_path_widen_sink, point) !=
              (bevel_contains != 0) ||
          captured_fill_contains(*raw_round_path_widen_sink, point) !=
              (round_contains != 0) ||
          captured_fill_contains(
              *raw_closed_cover_dash_widen_sink, point) !=
              (closed_cover_dash_contains != 0) ||
          captured_fill_contains(*raw_collapsed_path_widen_sink, point) !=
              (collapsed_contains != 0) ||
          captured_fill_contains(*raw_consumed_path_widen_sink, point) !=
              (consumed_contains != 0)) {
        std::fprintf(
            stderr,
            "closed styled widen mismatch point=%g,%g bevel=%d/%d "
            "round=%d/%d\n",
            point.x,
            point.y,
            captured_fill_contains(*raw_bevel_path_widen_sink, point)
                ? 1
                : 0,
            bevel_contains,
            captured_fill_contains(*raw_round_path_widen_sink, point)
                ? 1
                : 0,
            round_contains);
        return 400;
      }
    }
  }
  if (query_path->Widen(
          0.5F, dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, dashed_path_widen_sink.get()) != com::ok ||
      query_path->Widen(
          0.5F, square_dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, square_dashed_path_widen_sink.get()) != com::ok ||
      query_path->Widen(
          0.5F, triangle_dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance,
          triangle_dashed_path_widen_sink.get()) != com::ok ||
      query_path->Widen(
          0.5F, round_dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, round_dashed_path_widen_sink.get()) != com::ok ||
      query_path->Widen(
          0.5F, miter_dashed_path_stroke_style.get(), nullptr,
          dash_hit_tolerance, miter_dashed_path_widen_sink.get()) != com::ok ||
      query_path->Widen(
          0.5F, miter_or_bevel_dashed_path_style.get(), nullptr,
          dash_hit_tolerance,
          miter_or_bevel_dashed_path_widen_sink.get()) != com::ok ||
      query_path->Widen(
          0.5F, round_join_dashed_path_style.get(), nullptr,
          dash_hit_tolerance,
          round_join_dashed_path_widen_sink.get()) != com::ok ||
      query_path->Widen(
          0.5F, clipped_miter_dashed_path_style.get(), nullptr,
          dash_hit_tolerance,
          clipped_miter_dashed_path_widen_sink.get()) != com::ok ||
      raw_dashed_path_widen_sink->fill_mode != compat::fill_mode::winding ||
      raw_dashed_path_widen_sink->segment_flags !=
          compat::path_segment::force_unstroked ||
      raw_dashed_path_widen_sink->begin_count == 0U ||
      raw_dashed_path_widen_sink->begin_count !=
          raw_dashed_path_widen_sink->end_count ||
      captured_fill_contains(*raw_dashed_path_widen_sink, {2.2F, 2.0F}) ||
      !captured_fill_contains(
          *raw_square_dashed_path_widen_sink, {2.2F, 2.0F}) ||
      !captured_fill_contains(
          *raw_triangle_dashed_path_widen_sink, {2.2F, 2.0F}) ||
      raw_round_dashed_path_widen_sink->begin_count == 0U ||
      raw_round_dashed_path_widen_sink->bezier_count == 0U ||
      !captured_fill_contains(
          *raw_miter_dashed_path_widen_sink, {5.2F, 1.8F}) ||
      captured_fill_contains(
          *raw_miter_or_bevel_dashed_path_widen_sink, {5.2F, 1.8F}) ||
      !captured_fill_contains(
          *raw_round_join_dashed_path_widen_sink, {5.17F, 1.83F}) ||
      raw_round_join_dashed_path_widen_sink->bezier_count == 0U ||
      !captured_fill_contains(
          *raw_clipped_miter_dashed_path_widen_sink, {5.17F, 1.83F}) ||
      captured_fill_contains(
          *raw_clipped_miter_dashed_path_widen_sink, {5.2F, 1.8F})) {
    return 367;
  }
  for (std::uint32_t y_index = 0U; y_index < 22U; ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 18U; ++x_index) {
      const compat::point_2f point{
          0.671F + static_cast<float>(x_index) * 0.307F,
          1.437F + static_cast<float>(y_index) * 0.337F};
      std::int32_t flat_contains = 0;
      std::int32_t square_contains = 0;
      std::int32_t triangle_contains = 0;
      std::int32_t round_contains = 0;
      std::int32_t miter_contains = 0;
      std::int32_t miter_or_bevel_contains = 0;
      std::int32_t round_join_contains = 0;
      std::int32_t clipped_miter_contains = 0;
      if (query_path->StrokeContainsPoint(
              point, 0.5F, dashed_path_stroke_style.get(), nullptr,
              dash_hit_tolerance, &flat_contains) != com::ok ||
          query_path->StrokeContainsPoint(
              point, 0.5F, square_dashed_path_stroke_style.get(), nullptr,
              dash_hit_tolerance, &square_contains) != com::ok ||
          query_path->StrokeContainsPoint(
              point, 0.5F, triangle_dashed_path_stroke_style.get(), nullptr,
              dash_hit_tolerance, &triangle_contains) != com::ok ||
          query_path->StrokeContainsPoint(
              point, 0.5F, round_dashed_path_stroke_style.get(), nullptr,
              dash_hit_tolerance, &round_contains) != com::ok ||
          query_path->StrokeContainsPoint(
              point, 0.5F, miter_dashed_path_stroke_style.get(), nullptr,
              dash_hit_tolerance, &miter_contains) != com::ok ||
          query_path->StrokeContainsPoint(
              point, 0.5F, miter_or_bevel_dashed_path_style.get(), nullptr,
              dash_hit_tolerance, &miter_or_bevel_contains) != com::ok ||
          query_path->StrokeContainsPoint(
              point, 0.5F, round_join_dashed_path_style.get(), nullptr,
              dash_hit_tolerance, &round_join_contains) != com::ok ||
          query_path->StrokeContainsPoint(
              point, 0.5F, clipped_miter_dashed_path_style.get(), nullptr,
              dash_hit_tolerance, &clipped_miter_contains) != com::ok ||
          captured_fill_contains(*raw_dashed_path_widen_sink, point) !=
              (flat_contains != 0) ||
          captured_fill_contains(*raw_square_dashed_path_widen_sink, point) !=
              (square_contains != 0) ||
          captured_fill_contains(
              *raw_triangle_dashed_path_widen_sink, point) !=
              (triangle_contains != 0) ||
          captured_fill_contains(*raw_round_dashed_path_widen_sink, point) !=
              (round_contains != 0) ||
          captured_fill_contains(*raw_miter_dashed_path_widen_sink, point) !=
              (miter_contains != 0) ||
          captured_fill_contains(
              *raw_miter_or_bevel_dashed_path_widen_sink, point) !=
              (miter_or_bevel_contains != 0) ||
          captured_fill_contains(
              *raw_round_join_dashed_path_widen_sink, point) !=
              (round_join_contains != 0) ||
          captured_fill_contains(
              *raw_clipped_miter_dashed_path_widen_sink, point) !=
              (clipped_miter_contains != 0)) {
        std::fprintf(stderr,
                     "dashed widen mismatch point=%g,%g "
                     "flat=%d/%d square=%d/%d triangle=%d/%d round=%d/%d "
                     "miter=%d/%d miter-or-bevel=%d/%d round-join=%d/%d "
                     "clipped-miter=%d/%d "
                     "figures=%u lines=%u\n",
                     point.x, point.y,
                     captured_fill_contains(*raw_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     flat_contains,
                     captured_fill_contains(
                         *raw_square_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     square_contains,
                     captured_fill_contains(
                         *raw_triangle_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     triangle_contains,
                     captured_fill_contains(
                         *raw_round_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     round_contains,
                     captured_fill_contains(
                         *raw_miter_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     miter_contains,
                     captured_fill_contains(
                         *raw_miter_or_bevel_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     miter_or_bevel_contains,
                     captured_fill_contains(
                         *raw_round_join_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     round_join_contains,
                     captured_fill_contains(
                         *raw_clipped_miter_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     clipped_miter_contains,
                     raw_dashed_path_widen_sink->begin_count,
                     raw_dashed_path_widen_sink->line_count);
        return 368;
      }
    }
  }
  for (std::uint32_t y_index = 0U; y_index < 20U; ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 16U; ++x_index) {
      const compat::point_2f point{
          -0.63F + static_cast<float>(x_index) * 0.49F,
          0.37F + static_cast<float>(y_index) * 0.49F};
      std::int32_t stroke_contains = 0;
      if (query_path->StrokeContainsPoint(
              point, 2.0F, nullptr, nullptr, 0.01F,
              &stroke_contains) != com::ok ||
          captured_fill_contains(*raw_path_widen_sink, point) !=
              (stroke_contains != 0)) {
        return 345;
      }
    }
  }
  auto *raw_concave_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> concave_path_widen_sink;
  concave_path_widen_sink.attach(raw_concave_path_widen_sink);
  auto *raw_concave_bevel_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      concave_bevel_path_widen_sink;
  concave_bevel_path_widen_sink.attach(
      raw_concave_bevel_path_widen_sink);
  auto *raw_concave_round_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      concave_round_path_widen_sink;
  concave_round_path_widen_sink.attach(
      raw_concave_round_path_widen_sink);
  if (boolean_path->Widen(
          0.4F, nullptr, nullptr, core::default_flattening_tolerance,
          concave_path_widen_sink.get()) != com::ok ||
      boolean_path->Widen(
          0.4F, bevel_path_stroke_style.get(), nullptr,
          core::default_flattening_tolerance,
          concave_bevel_path_widen_sink.get()) != com::ok ||
      boolean_path->Widen(
          0.4F, round_path_stroke_style.get(), nullptr,
          core::default_flattening_tolerance,
          concave_round_path_widen_sink.get()) != com::ok ||
      raw_concave_path_widen_sink->fill_mode !=
          compat::fill_mode::winding ||
      raw_concave_path_widen_sink->segment_flags !=
          compat::path_segment::force_unstroked ||
      raw_concave_path_widen_sink->begin_count != 2U ||
      raw_concave_path_widen_sink->end_count != 2U ||
      raw_concave_bevel_path_widen_sink->begin_count != 2U ||
      raw_concave_bevel_path_widen_sink->begin_count !=
          raw_concave_bevel_path_widen_sink->end_count ||
      raw_concave_round_path_widen_sink->begin_count != 2U ||
      raw_concave_round_path_widen_sink->begin_count !=
          raw_concave_round_path_widen_sink->end_count ||
      raw_concave_round_path_widen_sink->bezier_count == 0U) {
    return 348;
  }
  for (std::uint32_t y_index = 0U; y_index < 20U; ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 16U; ++x_index) {
      const compat::point_2f point{
          2.57F + static_cast<float>(x_index) * 0.31F,
          0.47F + static_cast<float>(y_index) * 0.47F};
      std::int32_t stroke_contains = 0;
      std::int32_t bevel_stroke_contains = 0;
      std::int32_t round_stroke_contains = 0;
      if (boolean_path->StrokeContainsPoint(
              point, 0.4F, nullptr, nullptr, 0.01F,
              &stroke_contains) != com::ok ||
          boolean_path->StrokeContainsPoint(
              point, 0.4F, bevel_path_stroke_style.get(), nullptr, 0.01F,
              &bevel_stroke_contains) != com::ok ||
          boolean_path->StrokeContainsPoint(
              point, 0.4F, round_path_stroke_style.get(), nullptr, 0.01F,
              &round_stroke_contains) != com::ok ||
          captured_fill_contains(*raw_concave_path_widen_sink, point) !=
              (stroke_contains != 0) ||
          captured_fill_contains(
              *raw_concave_bevel_path_widen_sink, point) !=
              (bevel_stroke_contains != 0) ||
          captured_fill_contains(
              *raw_concave_round_path_widen_sink, point) !=
              (round_stroke_contains != 0)) {
        std::fprintf(
            stderr,
            "concave styled widen mismatch point=%g,%g "
            "default=%d/%d bevel=%d/%d round=%d/%d\n",
            point.x,
            point.y,
            captured_fill_contains(*raw_concave_path_widen_sink, point)
                ? 1
                : 0,
            stroke_contains,
            captured_fill_contains(
                *raw_concave_bevel_path_widen_sink, point)
                ? 1
                : 0,
            bevel_stroke_contains,
            captured_fill_contains(
                *raw_concave_round_path_widen_sink, point)
                ? 1
                : 0,
            round_stroke_contains);
        return 349;
      }
    }
  }
  const std::array<path_stroke_case, 4U> concave_path_stroke_cases{{
      {{4.9F, 5.9F}, nullptr, true},
      {{5.5F, 6.5F}, nullptr, false},
      {{2.1F, 0.1F}, nullptr, true},
      {{1.8F, -0.2F}, nullptr, false},
  }};
  for (std::size_t stroke_case_index = 0U;
       stroke_case_index < concave_path_stroke_cases.size();
       ++stroke_case_index) {
    const path_stroke_case &stroke_case =
        concave_path_stroke_cases[stroke_case_index];
    std::int32_t stroke_contains = 0;
    if (boolean_path->StrokeContainsPoint(
            stroke_case.point, 2.0F, nullptr, nullptr,
            core::default_flattening_tolerance, &stroke_contains) != com::ok ||
        (stroke_contains != 0) != stroke_case.expected) {
      return 335;
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
    com::pointer<compat::drawing_state_block1> drawing_state1;
    if (drawing_state.as(
            compat::drawing_state_block1_interface_id,
            drawing_state1) != com::ok ||
        !drawing_state1) {
        return 107;
    }
    compat::drawing_state_description1 changed_drawing_state1{
        compat::antialias_mode::per_primitive,
        compat::text_antialias_mode::cleartype,
        41U,
        43U,
        {2.0F, 0.0F, 0.0F, 0.5F, -3.0F, 7.0F},
        compat::primitive_blend::copy,
        compat::unit_mode::pixels};
    drawing_state1->SetDescription1(&changed_drawing_state1);
    compat::drawing_state_description1 returned_drawing_state1{};
    drawing_state1->GetDescription1(&returned_drawing_state1);
    returned_drawing_state = {};
    drawing_state->GetDescription(&returned_drawing_state);
    if (returned_drawing_state1.blend != compat::primitive_blend::copy ||
        returned_drawing_state1.units != compat::unit_mode::pixels ||
        returned_drawing_state.tag1 != 41U ||
        returned_drawing_state.tag2 != 43U ||
        !approximately_equal(returned_drawing_state.transform.m11, 2.0F)) {
        return 108;
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
    // Automatic scopes share the same semantic stream as explicit resources,
    // but independent null layers may nest without acquiring a COM use lease.
    target->BeginDraw();
    target->PushLayer(&layer_parameters, nullptr);
    target->FillRectangle(&rectangle, target_brush.get());
    target->PopLayer();
    if (target->EndDraw(nullptr, nullptr) != com::ok) return 331;
    std::vector<std::byte> automatic_layer_scene(
        static_cast<std::size_t>(scene_target->GetRequiredSceneSize()));
    std::uint64_t automatic_layer_written = 0U;
    if (scene_target->BuildScene(automatic_layer_scene.data(), automatic_layer_scene.size(),
            &automatic_layer_written) != com::ok || automatic_layer_written != layer_scene_written ||
        automatic_layer_scene != layer_scene) return 332;
    target->BeginDraw();
    target->PushLayer(&layer_parameters, nullptr);
    target->PushLayer(&layer_parameters, target_layer.get());
    target->PushAxisAlignedClip(&outer_clip, compat::antialias_mode::aliased);
    target->PushLayer(&layer_parameters, nullptr);
    target->FillRectangle(&rectangle, target_brush.get());
    target->PopLayer();
    target->PopAxisAlignedClip();
    target->PopLayer();
    target->PopLayer();
    if (target->EndDraw(nullptr, nullptr) != com::ok) return 333;
    // Errors must release explicit leases even when automatic scopes surround
    // them, and the next BeginDraw must discard all failed automatic scopes.
    target->BeginDraw();
    target->PushLayer(&layer_parameters, nullptr);
    target->PushLayer(&layer_parameters, target_layer.get());
    target->PushLayer(&layer_parameters, nullptr);
    if (target->EndDraw(nullptr, nullptr) != compat::wrong_state ||
        scene_target->GetRequiredSceneSize() != 0U) return 334;
    target->BeginDraw();
    target->PushLayer(&layer_parameters, target_layer.get());
    target->PopLayer();
    if (target->EndDraw(nullptr, nullptr) != com::ok) return 335;
    target->BeginDraw();
    target->PushLayer(&layer_parameters, nullptr);
    target->PushAxisAlignedClip(&outer_clip, compat::antialias_mode::aliased);
    target->PopLayer();
    if (target->EndDraw(nullptr, nullptr) != compat::wrong_state) return 336;
    target->BeginDraw();
    target->PushLayer(&layer_parameters, nullptr);
    target->PopLayer();
    target->PopLayer();
    if (target->EndDraw(nullptr, nullptr) != compat::wrong_state) return 337;
    target->BeginDraw();
    for (std::uint32_t index = 0U; index <= PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH; ++index)
        target->PushLayer(&layer_parameters, nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::out_of_memory ||
        scene_target->GetRequiredSceneSize() != 0U) return 338;
    target->BeginDraw();
    target->PushLayer(nullptr, nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::invalid_argument) return 339;
    // The native secondary COM interface must own an active nested layer
    // even after the caller releases its primary interface reference.
    raw_layer = nullptr;
    if (target->CreateLayer(nullptr, &raw_layer) != com::ok ||
        raw_layer == nullptr) {
        return 192;
    }
    target->BeginDraw();
    target->PushLayer(&layer_parameters, target_layer.get());
    target->PushLayer(&layer_parameters, raw_layer);
    raw_layer->Release();
    raw_layer = nullptr;
    target->PopLayer();
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
    // A8 remains compact across upload, subrectangle copies, aliases, and scene
    // export; only the GPU color matrix maps the sampled red channel to alpha.
    for (const auto alpha : {compat::alpha_mode::premultiplied, compat::alpha_mode::straight,
            compat::alpha_mode::unknown}) {
        const compat::bitmap_properties properties{{65U, alpha}, 144.0F, 192.0F};
        const std::array<std::byte, 7U> pixels{std::byte{0}, std::byte{64}, std::byte{128},
            std::byte{0xee}, std::byte{192}, std::byte{254}, std::byte{255}};
        compat::bitmap* raw = nullptr;
        if (target->CreateBitmap({3U, 2U}, pixels.data(), 4U, &properties, &raw) != com::ok) return 343;
        com::pointer<compat::bitmap> mask;
        mask.attach(raw);
        const auto expected_alpha = alpha == compat::alpha_mode::unknown ? compat::alpha_mode::premultiplied : alpha;
        if (!mask || mask->GetPixelFormat().format != 65U || mask->GetPixelFormat().alpha != expected_alpha ||
            !approximately_equal(mask->GetSize().width, 2.0F) ||
            !approximately_equal(mask->GetSize().height, 1.0F)) return 344;
        const compat::rectangle_u update{1U, 0U, 3U, 1U};
        const std::array<std::byte, 2U> updated{std::byte{17}, std::byte{31}};
        if (mask->CopyFromMemory(&update, updated.data(), 1U) != com::invalid_argument ||
            mask->CopyFromMemory(&update, updated.data(), 2U) != com::ok) return 345;
        const compat::rectangle_u source{0U, 0U, 2U, 1U};
        const compat::point_2u destination{1U, 1U};
        if (mask->CopyFromBitmap(&destination, mask.get(), &source) != com::ok) return 346;
        raw = nullptr;
        if (target->CreateSharedBitmap(compat::bitmap_interface_id, mask.get(), nullptr, &raw) != com::ok) return 347;
        com::pointer<compat::bitmap> alias;
        alias.attach(raw);
        target->BeginDraw();
        target->DrawBitmap(alias.get(), nullptr, 0.5F, compat::bitmap_interpolation_mode::nearest_neighbor, nullptr);
        if (target->EndDraw(nullptr, nullptr) != com::ok) return 348;
        const auto size = scene_target->GetRequiredSceneSize();
        std::vector<std::byte> bytes(static_cast<std::size_t>(size));
        std::uint64_t written = 0U;
        if (size == 0U || scene_target->BuildScene(bytes.data(), size, &written) != com::ok || written != size) return 349;
        const auto* header = reinterpret_cast<const progpu_native_scene_header*>(bytes.data());
        const auto* command = reinterpret_cast<const progpu_native_scene_command*>(bytes.data() + header->command_offset);
        const auto* image = reinterpret_cast<const progpu_native_scene_image_draw*>(bytes.data() + command->payload_offset);
        const auto* matrix = reinterpret_cast<const progpu_native_scene_image_color_matrix*>(image + 1);
        const auto* resource = reinterpret_cast<const progpu_native_scene_resource*>(bytes.data() +
            header->resource_offset + command->resource_index * header->resource_stride);
        const std::array<std::byte, 7U> expected{std::byte{0}, std::byte{17}, std::byte{31},
            std::byte{0xee}, std::byte{192}, std::byte{0}, std::byte{17}};
        if (header->command_count != 1U || image->row_bytes != 4U || image->opacity != 0.5F ||
            (image->flags & PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX) == 0U ||
            resource->flags != (PROGPU_NATIVE_SCENE_RECORD_REQUIRED | PROGPU_NATIVE_SCENE_IMAGE_R8) ||
            resource->payload_size != expected.size() || matrix->alpha[0] != 1.0F ||
            matrix->alpha[3] != 0.0F || matrix->red[0] != 0.0F ||
            std::memcmp(bytes.data() + resource->payload_offset, expected.data(), expected.size()) != 0) return 350;
        compat::bitmap_brush* raw_brush = nullptr;
        if (target->CreateBitmapBrush(alias.get(), nullptr, nullptr, &raw_brush) != com::ok) return 351;
        com::pointer<compat::bitmap_brush> opacity;
        opacity.attach(raw_brush);
        auto parameters = layer_parameters;
        parameters.opacity_brush = opacity.get();
        target->BeginDraw();
        target->SetAntialiasMode(compat::antialias_mode::aliased);
        target->FillOpacityMask(alias.get(), target_brush.get(), compat::opacity_mask_content::graphics, nullptr, nullptr);
        target->PushLayer(&parameters, nullptr);
        target->FillRectangle(&layer_bounds, target_brush.get());
        target->PopLayer();
        if (target->EndDraw(nullptr, nullptr) != com::ok || scene_target->GetRequiredSceneSize() == 0U) return 352;
    }
    target->SetAntialiasMode(compat::antialias_mode::per_primitive);
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

    const compat::bitmap_properties ignored_alpha_bitmap_properties{
        {87U, compat::alpha_mode::ignore}, 96.0F, 96.0F};
    const std::array<std::uint8_t, 4U> ignored_alpha_pixels{
        0x20U, 0x40U, 0x80U, 0x00U};
    compat::bitmap* raw_ignored_alpha_bitmap = nullptr;
    if (target->CreateBitmap(
            {1U, 1U},
            ignored_alpha_pixels.data(),
            4U,
            &ignored_alpha_bitmap_properties,
            &raw_ignored_alpha_bitmap) != com::ok ||
        raw_ignored_alpha_bitmap == nullptr ||
        raw_ignored_alpha_bitmap->GetPixelFormat().alpha !=
            compat::alpha_mode::ignore) {
        return 490;
    }
    com::pointer<compat::bitmap> ignored_alpha_bitmap;
    ignored_alpha_bitmap.attach(raw_ignored_alpha_bitmap);
    target->BeginDraw();
    target->DrawBitmap(
        ignored_alpha_bitmap.get(),
        nullptr,
        1.0F,
        compat::bitmap_interpolation_mode::nearest_neighbor,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 491;
    }
    const std::uint64_t ignored_alpha_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> ignored_alpha_scene(
        static_cast<std::size_t>(ignored_alpha_scene_size));
    std::uint64_t ignored_alpha_scene_written = 0U;
    if (ignored_alpha_scene_size == 0U ||
        scene_target->BuildScene(
            ignored_alpha_scene.data(),
            ignored_alpha_scene.size(),
            &ignored_alpha_scene_written) != com::ok ||
        ignored_alpha_scene_written != ignored_alpha_scene_size) {
        return 491;
    }
    const auto* ignored_alpha_header = reinterpret_cast<
        const progpu_native_scene_header*>(ignored_alpha_scene.data());
    const auto* ignored_alpha_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        ignored_alpha_scene.data() + ignored_alpha_header->command_offset);
    const auto* ignored_alpha_draw = reinterpret_cast<
        const progpu_native_scene_image_draw*>(
        ignored_alpha_scene.data() + ignored_alpha_command->payload_offset);
    if (ignored_alpha_header->command_count != 1U ||
        ignored_alpha_command->kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE ||
        (ignored_alpha_draw->flags &
            PROGPU_NATIVE_SCENE_IMAGE_SOURCE_ALPHA_IGNORE) == 0U ||
        (ignored_alpha_draw->flags &
            PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED) == 0U) {
        return 491;
    }

    auto* raw_wic_source = new fake_wic_bitmap_source(
        compat::wic_pixel_format_32bpp_pbgra);
    com::pointer<compat::wic_bitmap_source> wic_source;
    wic_source.attach(raw_wic_source);
#if defined(_WIN32)
    if (!com::guid_equal(
            compat::wic_bitmap_source_interface_id,
            __uuidof(IWICBitmapSource)) ||
        !com::guid_equal(compat::wic_pixel_format_8bpp_alpha, GUID_WICPixelFormat8bppAlpha) ||
        !com::guid_equal(
            compat::wic_pixel_format_32bpp_pbgra,
            GUID_WICPixelFormat32bppPBGRA) ||
        !com::guid_equal(
            compat::wic_pixel_format_32bpp_bgra,
            GUID_WICPixelFormat32bppBGRA) ||
        !com::guid_equal(
            compat::wic_pixel_format_32bpp_prgba,
            GUID_WICPixelFormat32bppPRGBA) ||
        !com::guid_equal(
            compat::wic_pixel_format_32bpp_rgba,
            GUID_WICPixelFormat32bppRGBA)) {
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

    const std::vector<std::uint8_t> unpremultiplied_pixels{
        255U, 128U, 64U, 0U,
        255U, 128U, 64U, 1U,
        255U, 128U, 64U, 2U,
        200U, 100U, 50U, 127U,
        200U, 100U, 50U, 128U,
        17U, 89U, 231U, 254U,
        17U, 89U, 231U, 255U,
        1U, 254U, 127U, 128U,
        33U, 66U, 99U, 200U};
    const std::array<std::uint8_t, 36U> premultiplied_pixels{
        0U, 0U, 0U, 0U,
        1U, 1U, 0U, 1U,
        2U, 1U, 1U, 2U,
        100U, 50U, 25U, 127U,
        100U, 50U, 25U, 128U,
        17U, 89U, 230U, 254U,
        17U, 89U, 231U, 255U,
        1U, 127U, 64U, 128U,
        26U, 52U, 78U, 200U};
    const auto validate_unpremultiplied_wic = [&target, &scene_target](
            com::guid source_format,
            std::uint32_t expected_dxgi_format,
            std::uint32_t width,
            std::uint32_t height,
            const std::vector<std::uint8_t>& source_pixels,
            std::span<const std::uint8_t> expected_pixels) {
        auto* raw_source = new fake_wic_bitmap_source(
            source_format, width, height, source_pixels);
        com::pointer<compat::wic_bitmap_source> source;
        source.attach(raw_source);
        compat::bitmap* raw_bitmap = nullptr;
        if (target->CreateBitmapFromWicBitmap(
                static_cast<com::unknown*>(source.get()),
                nullptr,
                &raw_bitmap) != com::ok ||
            raw_bitmap == nullptr) {
            return false;
        }
        com::pointer<compat::bitmap> bitmap;
        bitmap.attach(raw_bitmap);
        if (bitmap->GetPixelFormat().format != expected_dxgi_format ||
            bitmap->GetPixelFormat().alpha !=
                compat::alpha_mode::premultiplied ||
            bitmap->GetPixelSize().width != width ||
            bitmap->GetPixelSize().height != height ||
            raw_source->copy_call_count != 1U ||
            raw_source->last_stride != width * 4U ||
            raw_source->last_buffer_size != width * height * 4U) {
            return false;
        }
        target->BeginDraw();
        target->DrawBitmap(
            bitmap.get(),
            nullptr,
            1.0F,
            compat::bitmap_interpolation_mode::nearest_neighbor,
            nullptr);
        if (target->EndDraw(nullptr, nullptr) != com::ok) {
            return false;
        }
        const std::uint64_t scene_size =
            scene_target->GetRequiredSceneSize();
        std::vector<std::byte> scene(static_cast<std::size_t>(scene_size));
        std::uint64_t scene_written = 0U;
        if (scene_size == 0U ||
            scene_target->BuildScene(
                scene.data(), scene.size(), &scene_written) != com::ok ||
            scene_written != scene_size) {
            return false;
        }
        const auto* header = reinterpret_cast<
            const progpu_native_scene_header*>(scene.data());
        const progpu_native_scene_resource* image = nullptr;
        for (std::uint32_t index = 0U;
             index < header->resource_count;
             ++index) {
            const auto* candidate = reinterpret_cast<
                const progpu_native_scene_resource*>(
                scene.data() + header->resource_offset +
                static_cast<std::size_t>(index) * header->resource_stride);
            if (candidate->kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
                image = candidate;
                break;
            }
        }
        return header->command_count == 1U && image != nullptr &&
            image->payload_size == expected_pixels.size() &&
            std::memcmp(
                scene.data() + image->payload_offset,
                expected_pixels.data(),
                expected_pixels.size()) == 0;
    };
    if (!validate_unpremultiplied_wic(
            compat::wic_pixel_format_32bpp_bgra,
            87U,
            9U,
            1U,
            unpremultiplied_pixels,
            premultiplied_pixels) ||
        !validate_unpremultiplied_wic(
            compat::wic_pixel_format_32bpp_rgba,
            28U,
            9U,
            1U,
            unpremultiplied_pixels,
            premultiplied_pixels)) {
        return 479;
    }

    constexpr std::uint32_t exhaustive_dimension = 256U;
    constexpr std::size_t exhaustive_pixel_count =
        static_cast<std::size_t>(exhaustive_dimension) *
        exhaustive_dimension;
    std::vector<std::uint8_t> exhaustive_unpremultiplied(
        exhaustive_pixel_count * 4U);
    std::vector<std::uint8_t> exhaustive_premultiplied(
        exhaustive_pixel_count * 4U);
    const auto scalar_premultiply = [](
        std::uint8_t channel,
        std::uint8_t alpha) {
        return static_cast<std::uint8_t>(
            (static_cast<std::uint32_t>(channel) * alpha + 127U) / 255U);
    };
    for (std::uint32_t alpha = 0U;
         alpha < exhaustive_dimension;
         ++alpha) {
        for (std::uint32_t channel = 0U;
             channel < exhaustive_dimension;
             ++channel) {
            const std::size_t offset =
                (static_cast<std::size_t>(alpha) * exhaustive_dimension +
                    channel) * 4U;
            const std::uint8_t first = static_cast<std::uint8_t>(channel);
            const std::uint8_t second = static_cast<std::uint8_t>(
                255U - channel);
            const std::uint8_t third = static_cast<std::uint8_t>(
                (channel * 73U + alpha * 17U) & 0xFFU);
            const auto alpha_byte = static_cast<std::uint8_t>(alpha);
            exhaustive_unpremultiplied[offset + 0U] = first;
            exhaustive_unpremultiplied[offset + 1U] = second;
            exhaustive_unpremultiplied[offset + 2U] = third;
            exhaustive_unpremultiplied[offset + 3U] = alpha_byte;
            exhaustive_premultiplied[offset + 0U] =
                scalar_premultiply(first, alpha_byte);
            exhaustive_premultiplied[offset + 1U] =
                scalar_premultiply(second, alpha_byte);
            exhaustive_premultiplied[offset + 2U] =
                scalar_premultiply(third, alpha_byte);
            exhaustive_premultiplied[offset + 3U] = alpha_byte;
        }
    }
    if (!validate_unpremultiplied_wic(
            compat::wic_pixel_format_32bpp_bgra,
            87U,
            exhaustive_dimension,
            exhaustive_dimension,
            exhaustive_unpremultiplied,
            exhaustive_premultiplied)) {
        return 480;
    }

    const std::vector<std::uint8_t> ignored_wic_source_pixels{
        200U, 100U, 50U, 127U};
    auto* raw_ignored_wic_source = new fake_wic_bitmap_source(
        compat::wic_pixel_format_32bpp_bgra,
        1U,
        1U,
        ignored_wic_source_pixels);
    com::pointer<compat::wic_bitmap_source> ignored_wic_source;
    ignored_wic_source.attach(raw_ignored_wic_source);
    const compat::bitmap_properties ignored_wic_properties{
        {87U, compat::alpha_mode::ignore}, 96.0F, 96.0F};
    compat::bitmap* raw_ignored_wic_bitmap = nullptr;
    if (target->CreateBitmapFromWicBitmap(
            static_cast<com::unknown*>(ignored_wic_source.get()),
            &ignored_wic_properties,
            &raw_ignored_wic_bitmap) != com::ok ||
        raw_ignored_wic_bitmap == nullptr ||
        raw_ignored_wic_bitmap->GetPixelFormat().alpha !=
            compat::alpha_mode::ignore) {
        return 492;
    }
    com::pointer<compat::bitmap> ignored_wic_bitmap;
    ignored_wic_bitmap.attach(raw_ignored_wic_bitmap);
    target->BeginDraw();
    target->DrawBitmap(
        ignored_wic_bitmap.get(),
        nullptr,
        1.0F,
        compat::bitmap_interpolation_mode::nearest_neighbor,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 492;
    }
    const std::uint64_t ignored_wic_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> ignored_wic_scene(
        static_cast<std::size_t>(ignored_wic_scene_size));
    std::uint64_t ignored_wic_scene_written = 0U;
    if (ignored_wic_scene_size == 0U ||
        scene_target->BuildScene(
            ignored_wic_scene.data(),
            ignored_wic_scene.size(),
            &ignored_wic_scene_written) != com::ok ||
        ignored_wic_scene_written != ignored_wic_scene_size) {
        return 492;
    }
    const auto* ignored_wic_header = reinterpret_cast<
        const progpu_native_scene_header*>(ignored_wic_scene.data());
    const progpu_native_scene_resource* ignored_wic_resource = nullptr;
    for (std::uint32_t index = 0U;
         index < ignored_wic_header->resource_count;
         ++index) {
        const auto* candidate = reinterpret_cast<
            const progpu_native_scene_resource*>(
            ignored_wic_scene.data() + ignored_wic_header->resource_offset +
            static_cast<std::size_t>(index) *
                ignored_wic_header->resource_stride);
        if (candidate->kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
            ignored_wic_resource = candidate;
            break;
        }
    }
    if (ignored_wic_resource == nullptr ||
        ignored_wic_resource->payload_size != ignored_wic_source_pixels.size() ||
        std::memcmp(
            ignored_wic_scene.data() + ignored_wic_resource->payload_offset,
            ignored_wic_source_pixels.data(),
            ignored_wic_source_pixels.size()) != 0) {
        return 492;
    }

    const auto captured_a8_bytes_equal = [&](compat::bitmap* bitmap, std::span<const std::uint8_t> expected,
        std::uint32_t expected_stride) {
        target->BeginDraw();
        target->DrawBitmap(bitmap, nullptr, 1.0F, compat::bitmap_interpolation_mode::linear, nullptr);
        if (target->EndDraw(nullptr, nullptr) != com::ok) return false;
        const auto size = scene_target->GetRequiredSceneSize();
        std::vector<std::byte> scene(static_cast<std::size_t>(size));
        std::uint64_t written = 0U;
        if (size == 0U || scene_target->BuildScene(scene.data(), size, &written) != com::ok || written != size) return false;
        const auto* header = reinterpret_cast<const progpu_native_scene_header*>(scene.data());
        const auto* command = reinterpret_cast<const progpu_native_scene_command*>(scene.data() + header->command_offset);
        const auto* image = reinterpret_cast<const progpu_native_scene_image_draw*>(scene.data() + command->payload_offset);
        const auto* resource = reinterpret_cast<const progpu_native_scene_resource*>(scene.data() + header->resource_offset +
            command->resource_index * header->resource_stride);
        return image->row_bytes == expected_stride &&
            (image->flags & PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX) != 0U &&
            resource->flags == (PROGPU_NATIVE_SCENE_RECORD_REQUIRED | PROGPU_NATIVE_SCENE_IMAGE_R8) &&
            resource->payload_size == expected.size() &&
            std::memcmp(scene.data() + resource->payload_offset, expected.data(), expected.size()) == 0;
    };
    for (const auto alpha : {compat::alpha_mode::premultiplied, compat::alpha_mode::straight,
            compat::alpha_mode::unknown}) {
        const compat::bitmap_properties properties{{65U, alpha}, 144.0F, 192.0F};
        auto* raw_source = new fake_wic_bitmap_source(compat::wic_pixel_format_8bpp_alpha,
            3U, 2U, {0U, 64U, 128U, 192U, 254U, 255U});
        com::pointer<compat::wic_bitmap_source> source;
        source.attach(raw_source);
        compat::bitmap* raw_imported = nullptr;
        if (target->CreateBitmapFromWicBitmap(source.get(), &properties, &raw_imported) != com::ok) return 353;
        com::pointer<compat::bitmap> imported;
        imported.attach(raw_imported);
        if (!imported || raw_source->copy_call_count != 1U || raw_source->last_stride != 3U ||
            raw_source->last_buffer_size != 6U || !approximately_equal(imported->GetSize().width, 2.0F) ||
            !approximately_equal(imported->GetSize().height, 1.0F)) return 354;
        raw_source->pixels.assign(6U, 17U);
        const std::array<std::uint8_t, 6U> original{0U, 64U, 128U, 192U, 254U, 255U};
        if (!captured_a8_bytes_equal(imported.get(), original, 3U)) return 355;
        std::uint32_t destroyed = 0U;
        auto* raw_lock = new fake_wic_bitmap_lock(compat::wic_pixel_format_8bpp_alpha,
            3U, 2U, 4U, {0U, 0U, 0U, 0xeeU, 0U, 0U, 0U}, &destroyed);
        com::pointer<compat::wic_bitmap_lock> lock;
        lock.attach(raw_lock);
        compat::bitmap* raw_locked = nullptr;
        if (target->CreateSharedBitmap(compat::wic_bitmap_lock_interface_id, lock.get(),
                &properties, &raw_locked) != com::ok) return 356;
        com::pointer<compat::bitmap> locked;
        locked.attach(raw_locked);
        if (locked->CopyFromBitmap(nullptr, imported.get(), nullptr) != com::ok) return 357;
        const compat::rectangle_u last_pixel{2U, 1U, 3U, 2U};
        const std::uint8_t replacement = 17U;
        const compat::rectangle_u self_source{0U, 0U, 2U, 1U};
        const compat::point_2u self_destination{1U, 0U};
        if (locked->CopyFromMemory(&last_pixel, &replacement, 1U) != com::ok ||
            locked->CopyFromBitmap(&self_destination, locked.get(), &self_source) != com::ok) return 358;
        const std::array<std::uint8_t, 7U> expected{0U, 0U, 64U, 0xeeU, 192U, 254U, 17U};
        if (!std::equal(expected.begin(), expected.end(), raw_lock->pixels.begin()) ||
            !captured_a8_bytes_equal(locked.get(), expected, 4U)) return 359;
        if (imported->CopyFromBitmap(nullptr, locked.get(), nullptr) != com::ok) return 360;
        const std::array<std::uint8_t, 6U> compact_expected{0U, 0U, 64U, 192U, 254U, 17U};
        if (!captured_a8_bytes_equal(imported.get(), compact_expected, 3U)) return 361;
        // An alias and the retained scene must keep the WIC lock alive after
        // the caller releases the primary lock and bitmap references.
        compat::bitmap* raw_alias = nullptr;
        if (target->CreateSharedBitmap(compat::bitmap_interface_id, locked.get(), nullptr, &raw_alias) != com::ok) return 362;
        com::pointer<compat::bitmap> alias;
        alias.attach(raw_alias);
        locked.reset();
        lock.reset();
        if (destroyed != 0U || !captured_a8_bytes_equal(alias.get(), expected, 4U)) return 363;
        alias.reset();
        if (destroyed != 0U) return 364;
        target->BeginDraw();
        if (destroyed != 1U || target->EndDraw(nullptr, nullptr) != com::ok) return 365;
    }
    for (const bool short_stride : {false, true}) {
        auto* raw_lock = new fake_wic_bitmap_lock(compat::wic_pixel_format_8bpp_alpha,
            3U, 2U, short_stride ? 2U : 4U, std::vector<std::uint8_t>(6U));
        com::pointer<compat::wic_bitmap_lock> lock;
        lock.attach(raw_lock);
        compat::bitmap* rejected = nullptr;
        if (target->CreateSharedBitmap(compat::wic_bitmap_lock_interface_id, lock.get(), nullptr,
                &rejected) != com::invalid_argument || rejected != nullptr) return 366;
    }
    const compat::bitmap_properties ignored_a8{{65U, compat::alpha_mode::ignore}, 96.0F, 96.0F};
    auto* raw_alpha_source = new fake_wic_bitmap_source(compat::wic_pixel_format_8bpp_alpha, 1U, 1U, {255U});
    com::pointer<compat::wic_bitmap_source> alpha_source;
    alpha_source.attach(raw_alpha_source);
    compat::bitmap* rejected_alpha = nullptr;
    if (target->CreateBitmapFromWicBitmap(alpha_source.get(), &ignored_a8, &rejected_alpha) !=
            compat::not_implemented || rejected_alpha != nullptr || raw_alpha_source->copy_call_count != 0U) return 367;

    std::vector<std::uint8_t> locked_pixels{
        0x01U, 0x02U, 0x03U, 0x04U,
        0x05U, 0x06U, 0x07U, 0x08U,
        0xA0U, 0xA1U, 0xA2U, 0xA3U,
        0x11U, 0x12U, 0x13U, 0x14U,
        0x15U, 0x16U, 0x17U, 0x18U};
    auto* raw_wic_lock = new fake_wic_bitmap_lock(
        compat::wic_pixel_format_32bpp_pbgra,
        2U,
        2U,
        12U,
        std::move(locked_pixels));
    com::pointer<compat::wic_bitmap_lock> wic_lock;
    wic_lock.attach(raw_wic_lock);
#if defined(_WIN32)
    if (!com::guid_equal(
            compat::wic_bitmap_lock_interface_id,
            __uuidof(IWICBitmapLock))) {
        return 481;
    }
    std::uint32_t native_lock_stride = 0U;
    auto* native_wic_lock = reinterpret_cast<IWICBitmapLock*>(wic_lock.get());
    if (FAILED(native_wic_lock->GetStride(&native_lock_stride)) ||
        native_lock_stride != 12U) {
        return 481;
    }
#endif
    compat::bitmap* raw_locked_bitmap = nullptr;
    if (target->CreateSharedBitmap(
            compat::wic_bitmap_lock_interface_id,
            wic_lock.get(),
            nullptr,
            &raw_locked_bitmap) != com::ok ||
        raw_locked_bitmap == nullptr) {
        return 482;
    }
    com::pointer<compat::bitmap> locked_bitmap;
    locked_bitmap.attach(raw_locked_bitmap);
    float locked_dpi_x = 0.0F;
    float locked_dpi_y = 0.0F;
    locked_bitmap->GetDpi(&locked_dpi_x, &locked_dpi_y);
    if (locked_bitmap->GetPixelSize().width != 2U ||
        locked_bitmap->GetPixelSize().height != 2U ||
        locked_bitmap->GetPixelFormat().format != 87U ||
        locked_bitmap->GetPixelFormat().alpha !=
            compat::alpha_mode::premultiplied ||
        !approximately_equal(locked_dpi_x, 96.0F) ||
        !approximately_equal(locked_dpi_y, 96.0F) ||
        raw_wic_lock->size_call_count != 1U ||
        raw_wic_lock->stride_call_count == 0U ||
        raw_wic_lock->data_call_count != 1U ||
        raw_wic_lock->format_call_count != 1U) {
        return 483;
    }
    raw_wic_lock->pixels[0U] = 0x31U;
    const std::array<std::uint8_t, 4U> locked_replacement{
        0x41U, 0x42U, 0x43U, 0x44U};
    const compat::rectangle_u locked_copy_source{0U, 0U, 1U, 1U};
    const compat::point_2u locked_copy_destination{0U, 0U};
    const compat::rectangle_u locked_replacement_rectangle{
        1U, 1U, 2U, 2U};
    if (locked_bitmap->CopyFromBitmap(
            &locked_copy_destination,
            portable_bitmap.get(),
            &locked_copy_source) != com::ok ||
        raw_wic_lock->pixels[0U] != 0x00U ||
        raw_wic_lock->pixels[2U] != 0xFFU ||
        locked_bitmap->CopyFromMemory(
            &locked_replacement_rectangle,
            locked_replacement.data(),
            4U) != com::ok ||
        raw_wic_lock->pixels[16U] != 0x41U ||
        raw_wic_lock->pixels[19U] != 0x44U) {
        return 484;
    }
    target->BeginDraw();
    target->DrawBitmap(
        locked_bitmap.get(),
        nullptr,
        1.0F,
        compat::bitmap_interpolation_mode::nearest_neighbor,
        nullptr);
    if (target->EndDraw(nullptr, nullptr) != com::ok) {
        return 485;
    }
    const std::uint64_t locked_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> locked_scene(
        static_cast<std::size_t>(locked_scene_size));
    std::uint64_t locked_scene_written = 0U;
    if (locked_scene_size == 0U ||
        scene_target->BuildScene(
            locked_scene.data(),
            locked_scene.size(),
            &locked_scene_written) != com::ok ||
        locked_scene_written != locked_scene_size) {
        return 485;
    }
    const auto* locked_header = reinterpret_cast<
        const progpu_native_scene_header*>(locked_scene.data());
    const progpu_native_scene_resource* locked_resource = nullptr;
    for (std::uint32_t index = 0U;
         index < locked_header->resource_count;
         ++index) {
        const auto* candidate = reinterpret_cast<
            const progpu_native_scene_resource*>(
            locked_scene.data() + locked_header->resource_offset +
            static_cast<std::size_t>(index) *
                locked_header->resource_stride);
        if (candidate->kind == PROGPU_NATIVE_SCENE_RESOURCE_IMAGE) {
            locked_resource = candidate;
            break;
        }
    }
    if (locked_resource == nullptr ||
        locked_resource->payload_size != raw_wic_lock->pixels.size() ||
        std::memcmp(
            locked_scene.data() + locked_resource->payload_offset,
            raw_wic_lock->pixels.data(),
            raw_wic_lock->pixels.size()) != 0) {
        return 485;
    }

    auto* raw_rgba_wic_lock = new fake_wic_bitmap_lock(
        compat::wic_pixel_format_32bpp_prgba,
        1U,
        1U,
        4U,
        {0x10U, 0x20U, 0x30U, 0x40U});
    com::pointer<compat::wic_bitmap_lock> rgba_wic_lock;
    rgba_wic_lock.attach(raw_rgba_wic_lock);
    compat::bitmap* raw_rgba_locked_bitmap = nullptr;
    const compat::bitmap_properties rgba_locked_properties{
        {28U, compat::alpha_mode::premultiplied}, 144.0F, 120.0F};
    if (target->CreateSharedBitmap(
            compat::wic_bitmap_lock_interface_id,
            rgba_wic_lock.get(),
            &rgba_locked_properties,
            &raw_rgba_locked_bitmap) != com::ok ||
        raw_rgba_locked_bitmap == nullptr ||
        raw_rgba_locked_bitmap->GetPixelFormat().format != 28U) {
        return 486;
    }
    com::pointer<compat::bitmap> rgba_locked_bitmap;
    rgba_locked_bitmap.attach(raw_rgba_locked_bitmap);

    auto* raw_straight_wic_lock = new fake_wic_bitmap_lock(
        compat::wic_pixel_format_32bpp_bgra,
        1U,
        1U,
        4U,
        {0x10U, 0x20U, 0x30U, 0x40U});
    com::pointer<compat::wic_bitmap_lock> straight_wic_lock;
    straight_wic_lock.attach(raw_straight_wic_lock);
    const compat::bitmap_properties ignored_straight_lock_properties{
        {87U, compat::alpha_mode::ignore}, 96.0F, 96.0F};
    compat::bitmap* raw_ignored_straight_lock_bitmap = nullptr;
    if (target->CreateSharedBitmap(
            compat::wic_bitmap_lock_interface_id,
            straight_wic_lock.get(),
            &ignored_straight_lock_properties,
            &raw_ignored_straight_lock_bitmap) != com::ok ||
        raw_ignored_straight_lock_bitmap == nullptr ||
        raw_ignored_straight_lock_bitmap->GetPixelFormat().alpha !=
            compat::alpha_mode::ignore) {
        return 487;
    }
    com::pointer<compat::bitmap> ignored_straight_lock_bitmap;
    ignored_straight_lock_bitmap.attach(raw_ignored_straight_lock_bitmap);
    compat::bitmap* rejected_locked_bitmap = reinterpret_cast<compat::bitmap*>(
        static_cast<std::uintptr_t>(1U));
    if (target->CreateSharedBitmap(
            compat::wic_bitmap_lock_interface_id,
            straight_wic_lock.get(),
            nullptr,
            &rejected_locked_bitmap) != compat::not_implemented ||
        rejected_locked_bitmap != nullptr) {
        return 487;
    }
    auto* raw_short_wic_lock = new fake_wic_bitmap_lock(
        compat::wic_pixel_format_32bpp_pbgra,
        2U,
        2U,
        7U,
        std::vector<std::uint8_t>(16U));
    com::pointer<compat::wic_bitmap_lock> short_wic_lock;
    short_wic_lock.attach(raw_short_wic_lock);
    rejected_locked_bitmap = reinterpret_cast<compat::bitmap*>(
        static_cast<std::uintptr_t>(1U));
    const compat::bitmap_properties mismatched_locked_properties{
        {28U, compat::alpha_mode::premultiplied}, 96.0F, 96.0F};
    if (target->CreateSharedBitmap(
            compat::wic_bitmap_lock_interface_id,
            short_wic_lock.get(),
            nullptr,
            &rejected_locked_bitmap) != com::invalid_argument ||
        rejected_locked_bitmap != nullptr ||
        target->CreateSharedBitmap(
            compat::wic_bitmap_lock_interface_id,
            wic_lock.get(),
            &mismatched_locked_properties,
            &rejected_locked_bitmap) != compat::not_implemented ||
        rejected_locked_bitmap != nullptr) {
        return 488;
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
    const compat::bitmap_properties ignored_shared_properties{
        {87U, compat::alpha_mode::ignore}, 96.0F, 96.0F};
    compat::bitmap* raw_ignored_shared_bitmap = nullptr;
    if (target->CreateSharedBitmap(
            compat::bitmap_interface_id,
            portable_bitmap.get(),
            &ignored_shared_properties,
            &raw_ignored_shared_bitmap) != com::ok ||
        raw_ignored_shared_bitmap == nullptr ||
        raw_ignored_shared_bitmap->GetPixelFormat().alpha !=
            compat::alpha_mode::ignore) {
        return 493;
    }
    com::pointer<compat::bitmap> ignored_shared_bitmap;
    ignored_shared_bitmap.attach(raw_ignored_shared_bitmap);
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
    const compat::rectangle_f ignored_shared_view_destination{
        38.0F, 3.0F, 54.0F, 19.0F};
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
    target->DrawBitmap(
        ignored_shared_bitmap.get(),
        &ignored_shared_view_destination,
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
    const auto* ignored_shared_command = reinterpret_cast<
        const progpu_native_scene_command*>(
        shared_scene.data() + shared_header->command_offset +
        shared_header->command_stride);
    const auto* ignored_shared_draw = reinterpret_cast<
        const progpu_native_scene_image_draw*>(
        shared_scene.data() + ignored_shared_command->payload_offset);
    if (shared_scene_summary.draw_count != 3U ||
        shared_header->command_count != 2U || shared_image_count != 1U ||
        shared_image_resource == nullptr ||
        ignored_shared_command->kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE ||
        (ignored_shared_draw->flags &
            PROGPU_NATIVE_SCENE_IMAGE_SOURCE_ALPHA_IGNORE) == 0U ||
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
            &raw_compatible_target) != com::ok ||
        raw_compatible_target == nullptr) {
        return 236;
    }
    com::pointer<compat::bitmap_render_target> nonuniform_compatible_target;
    nonuniform_compatible_target.attach(raw_compatible_target);
    float nonuniform_dpi_x = 0.0F;
    float nonuniform_dpi_y = 0.0F;
    nonuniform_compatible_target->GetDpi(
        &nonuniform_dpi_x, &nonuniform_dpi_y);
    const compat::size_f nonuniform_dip_size =
        nonuniform_compatible_target->GetSize();
    if (!approximately_equal(nonuniform_dpi_x, 96.0F) ||
        !approximately_equal(nonuniform_dpi_y, 192.0F) ||
        !approximately_equal(nonuniform_dip_size.width, 16.0F) ||
        !approximately_equal(nonuniform_dip_size.height, 12.0F)) {
        return 236;
    }
    raw_compatible_target = nullptr;
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
    const progpu_native_scene_resource* compatible_mask_resource = nullptr;
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
            compatible_mask_resource = candidate_resource;
            compatible_mask_picture = reinterpret_cast<
                const progpu_native_scene_layer_picture_mask*>(
                compatible_mask_scene.data() +
                candidate_resource->payload_offset);
            break;
        }
    }
    if (compatible_mask_header->command_count != 1U ||
        compatible_mask_picture == nullptr ||
        compatible_mask_picture->flags != 0U ||
        compatible_mask_picture->reserved0 != 0U ||
        compatible_mask_picture->reserved1 != 0U ||
        !approximately_equal(compatible_mask_picture->bounds.width, 24.0F) ||
        !approximately_equal(compatible_mask_picture->bounds.height, 16.0F) ||
        !approximately_equal(compatible_mask_picture->transform.m11, 1.0F) ||
        !approximately_equal(compatible_mask_picture->transform.m22, 1.0F)) {
        return 231;
    }
    // Mask capture uses the same image resource as DrawBitmap, including A8's
    // alpha-channel projection from the retained RGBA target, never sampled R.
    const auto* mask_image_bytes = compatible_mask_scene.data() + compatible_mask_resource->auxiliary_offset;
    const auto* mask_image_header = reinterpret_cast<const progpu_native_scene_header*>(mask_image_bytes);
    const auto* mask_image_resource = reinterpret_cast<const progpu_native_scene_resource*>(
        mask_image_bytes + mask_image_header->resource_offset);
    const auto* mask_image_command = reinterpret_cast<const progpu_native_scene_command*>(
        mask_image_bytes + mask_image_header->command_offset);
    const auto* mask_image_draw = reinterpret_cast<const progpu_native_scene_image_draw*>(
        mask_image_bytes + mask_image_command->payload_offset);
    const auto* mask_image_matrix = reinterpret_cast<const progpu_native_scene_image_color_matrix*>(
        reinterpret_cast<const std::byte*>(mask_image_draw) + sizeof(*mask_image_draw));
    if ((mask_image_resource->flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) == 0U ||
        mask_image_draw->image_width != 16U || mask_image_draw->image_height != 12U ||
        mask_image_draw->source_rect.x != 2.0F || mask_image_draw->source_rect.y != 1.0F ||
        mask_image_draw->source_rect.width != 8.0F || mask_image_draw->source_rect.height != 8.0F ||
        mask_image_draw->destination_rect.x != 60.0F || mask_image_draw->destination_rect.y != 4.0F ||
        mask_image_matrix->alpha[3] != 1.0F || mask_image_matrix->alpha[0] != 0.0F) return 231;

    // Captures are immutable per draw; a source redraw creates a new generation
    // while unchanged original/shared aliases reuse one parent scene resource.
    for (const std::uint32_t format : {28U, 87U, 65U}) {
        const compat::pixel_format source_format{format, compat::alpha_mode::premultiplied};
        const compat::size_u high_dpi_pixels{32U, 24U};
        compat::bitmap_render_target* raw_source_target = nullptr;
        if (target->CreateCompatibleRenderTarget(&compatible_size, &high_dpi_pixels,
                &source_format, compat::compatible_render_target_options::none, &raw_source_target) != com::ok)
            return 280;
        com::pointer<compat::bitmap_render_target> source_target;
        source_target.attach(raw_source_target);
        const compat::color_f first_clear{0.25F, 0.5F, 1.0F, 0.75F};
        const compat::color_f second_clear{1.0F, 0.0F, 0.0F, 0.5F};
        source_target->BeginDraw();
        source_target->Clear(&first_clear);
        if (source_target->EndDraw(nullptr, nullptr) != com::ok) return 280;
        compat::bitmap* raw_source_bitmap = nullptr;
        if (source_target->GetBitmap(&raw_source_bitmap) != com::ok) return 280;
        com::pointer<compat::bitmap> source_bitmap;
        source_bitmap.attach(raw_source_bitmap);
        compat::bitmap* raw_alias = nullptr;
        if (target->CreateSharedBitmap(compat::bitmap_interface_id, source_bitmap.get(), nullptr, &raw_alias) != com::ok)
            return 280;
        com::pointer<compat::bitmap> alias;
        alias.attach(raw_alias);
        target->BeginDraw();
        target->DrawBitmap(source_bitmap.get(), &compatible_destination, 0.625F,
            compat::bitmap_interpolation_mode::nearest_neighbor, &compatible_source);
        target->DrawBitmap(alias.get(), &compatible_destination, 1.0F,
            compat::bitmap_interpolation_mode::linear, &compatible_source);
        source_target->BeginDraw();
        source_target->Clear(&second_clear);
        if (source_target->EndDraw(nullptr, nullptr) != com::ok) return 280;
        target->DrawBitmap(alias.get(), &compatible_destination, 1.0F,
            compat::bitmap_interpolation_mode::linear, &compatible_source);
        if (target->EndDraw(nullptr, nullptr) != com::ok) return 280;
        std::vector<std::byte> draw_scene(static_cast<std::size_t>(scene_target->GetRequiredSceneSize()));
        std::uint64_t draw_written = 0U;
        if (scene_target->BuildScene(draw_scene.data(), draw_scene.size(), &draw_written) != com::ok) return 280;
        const auto* header = reinterpret_cast<const progpu_native_scene_header*>(draw_scene.data());
        if (header->resource_count != 2U || header->command_count != 3U || draw_written != draw_scene.size()) return 280;
        for (std::uint32_t i = 0U; i < 3U; ++i) {
            const auto* command = reinterpret_cast<const progpu_native_scene_command*>(
                draw_scene.data() + header->command_offset + i * header->command_stride);
            const auto* resource = reinterpret_cast<const progpu_native_scene_resource*>(
                draw_scene.data() + header->resource_offset + command->resource_index * header->resource_stride);
            const auto* descriptor = reinterpret_cast<const progpu_native_scene_picture_image*>(
                draw_scene.data() + resource->payload_offset);
            const auto* draw = reinterpret_cast<const progpu_native_scene_image_draw*>(draw_scene.data() + command->payload_offset);
            if (command->resource_index != (i == 2U ? 1U : 0U) ||
                (resource->flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) == 0U ||
                descriptor->width != 32U || descriptor->height != 24U || descriptor->dpi_scale != 2.0F ||
                descriptor->clear_color.r != (i == 2U ? 1.0F : 0.25F) ||
                descriptor->clear_color.a != (i == 2U ? 0.5F : 0.75F) ||
                draw->row_bytes != 128U || draw->source_rect.x != 4.0F || draw->source_rect.y != 2.0F ||
                draw->source_rect.width != 16.0F || draw->source_rect.height != 16.0F ||
                draw->opacity != (i == 0U ? 0.625F : 1.0F) ||
                (draw->flags & PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED) == 0U) return 280;
            if (format == 65U) {
                const auto* matrix = reinterpret_cast<const progpu_native_scene_image_color_matrix*>(
                    reinterpret_cast<const std::byte*>(draw) + sizeof(*draw));
                if ((draw->flags & PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX) == 0U ||
                    matrix->alpha[3] != 1.0F || matrix->alpha[0] != 0.0F) return 280;
            }
        }
        compat::bitmap_brush* raw_picture_brush = nullptr;
        const compat::bitmap_brush_properties picture_brush_properties{
            compat::extend_mode::wrap, compat::extend_mode::mirror,
            compat::bitmap_interpolation_mode::linear};
        if (target->CreateBitmapBrush(alias.get(), &picture_brush_properties, nullptr, &raw_picture_brush) != com::ok)
            return 281;
        com::pointer<compat::bitmap_brush> picture_brush;
        picture_brush.attach(raw_picture_brush);
        target->BeginDraw();
        target->FillRectangle(&compatible_destination, picture_brush.get());
        if (target->EndDraw(nullptr, nullptr) != com::ok) return 281;
        source_target->BeginDraw();
        source_target->DrawBitmap(alias.get(), nullptr, 1.0F, compat::bitmap_interpolation_mode::linear, nullptr);
        if (source_target->EndDraw(nullptr, nullptr) != compat::wrong_state) return 281;
        source_target->BeginDraw();
        source_target->Clear(&first_clear);
        target->BeginDraw();
        target->DrawBitmap(source_bitmap.get(), nullptr, 1.0F, compat::bitmap_interpolation_mode::linear, nullptr);
        if (target->EndDraw(nullptr, nullptr) != compat::wrong_state ||
            source_target->EndDraw(nullptr, nullptr) != com::ok) return 281;
        source_target->BeginDraw(); // Empty sessions preserve the complete bitmap.
        if (source_target->EndDraw(nullptr, nullptr) != com::ok) return 281;
        target->BeginDraw();
        target->DrawBitmap(source_bitmap.get(), nullptr, 1.0F, compat::bitmap_interpolation_mode::linear, nullptr);
        if (target->EndDraw(nullptr, nullptr) != com::ok) return 281;

        com::pointer<compat::scene_render_target_native> persistent_scene;
        if (source_target.as(compat::scene_render_target_native_interface_id, persistent_scene) != com::ok)
            return 282;
        const compat::rectangle_f persistent_rectangle{1.0F, 1.0F, 7.0F, 5.0F};
        // More sessions than the nested-picture depth limit: persistence must be
        // flat retained commands, not one recursive picture per drawing session.
        for (unsigned int session = 0U; session < 24U; ++session) {
            source_target->BeginDraw();
            source_target->FillRectangle(&persistent_rectangle, target_brush.get());
            if (source_target->EndDraw(nullptr, nullptr) != com::ok) return 282;
        }
        compat::scene_render_target_summary persistent_summary{};
        persistent_scene->GetSummary(&persistent_summary);
        if (persistent_summary.draw_count != 24U || persistent_summary.has_clear != 1 ||
            persistent_summary.clear_color.red != first_clear.red ||
            persistent_summary.clear_color.alpha != first_clear.alpha) return 282;
        std::vector<std::byte> retained_history(static_cast<std::size_t>(persistent_scene->GetRequiredSceneSize()));
        std::uint64_t retained_written = 0U;
        if (persistent_scene->BuildScene(retained_history.data(), retained_history.size(), &retained_written) != com::ok)
            return 282;
        const auto* retained_header = reinterpret_cast<const progpu_native_scene_header*>(retained_history.data());
        if (retained_written != retained_history.size() || retained_header->command_count != 24U) return 282;
        // Cached exports remain target-owned. Public buffers may be reused or
        // mutated without affecting the next capture, and failed writes leave
        // caller storage untouched.
        if (persistent_scene->GetRequiredSceneSize() != retained_history.size() ||
            persistent_scene->GetRequiredSceneSize() != retained_history.size()) return 286;
        std::vector<std::byte> export_buffer(retained_history.size() + 8U, std::byte{0x5a});
        std::uint64_t export_written = 123U;
        if (persistent_scene->BuildScene(nullptr, export_buffer.size(), &export_written) != com::invalid_argument ||
            export_written != 0U ||
            persistent_scene->BuildScene(export_buffer.data(), retained_history.size() - 1U, &export_written) != com::invalid_argument ||
            export_written != 0U ||
            !std::all_of(export_buffer.begin(), export_buffer.end(), [](std::byte b) { return b == std::byte{0x5a}; }))
            return 286;
        if (persistent_scene->BuildScene(export_buffer.data(), export_buffer.size(), &export_written) != com::ok ||
            export_written != retained_history.size() ||
            std::memcmp(export_buffer.data(), retained_history.data(), retained_history.size()) != 0 ||
            !std::all_of(export_buffer.begin() + static_cast<std::ptrdiff_t>(retained_history.size()),
                export_buffer.end(), [](std::byte b) { return b == std::byte{0x5a}; })) return 286;
        export_buffer[0] ^= std::byte{0xff};
        if (persistent_scene->BuildScene(export_buffer.data(), export_buffer.size(), &export_written) != com::ok ||
            std::memcmp(export_buffer.data(), retained_history.data(), retained_history.size()) != 0) return 286;
        for (std::uint32_t i = 0U; i < retained_header->resource_count; ++i) {
            const auto* resource = reinterpret_cast<const progpu_native_scene_resource*>(
                retained_history.data() + retained_header->resource_offset + i * retained_header->resource_stride);
            if ((resource->flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) != 0U) return 282;
        }
        compat::bitmap_render_target* raw_single_session = nullptr;
        if (target->CreateCompatibleRenderTarget(&compatible_size, &high_dpi_pixels,
                &source_format, compat::compatible_render_target_options::none, &raw_single_session) != com::ok)
            return 282;
        com::pointer<compat::bitmap_render_target> single_session;
        single_session.attach(raw_single_session);
        single_session->BeginDraw();
        single_session->Clear(&first_clear);
        for (unsigned int i = 0U; i < 24U; ++i)
            single_session->FillRectangle(&persistent_rectangle, target_brush.get());
        if (single_session->EndDraw(nullptr, nullptr) != com::ok) return 282;
        com::pointer<compat::scene_render_target_native> single_scene;
        if (single_session.as(compat::scene_render_target_native_interface_id, single_scene) != com::ok) return 282;
        std::vector<std::byte> single_bytes(static_cast<std::size_t>(single_scene->GetRequiredSceneSize()));
        std::uint64_t single_written = 0U;
        if (single_scene->BuildScene(single_bytes.data(), single_bytes.size(), &single_written) != com::ok) return 282;
        const auto* single_header = reinterpret_cast<const progpu_native_scene_header*>(single_bytes.data());
        if (single_header->command_count != retained_header->command_count ||
            single_header->resource_count != retained_header->resource_count) return 282;
        // Compare original single-session and retained multi-session producers:
        // scene/resource generations differ, command/resource payloads must not.
        for (std::uint32_t i = 0U; i < retained_header->command_count; ++i) {
            const auto* multi = reinterpret_cast<const progpu_native_scene_command*>(
                retained_history.data() + retained_header->command_offset + i * retained_header->command_stride);
            const auto* one = reinterpret_cast<const progpu_native_scene_command*>(
                single_bytes.data() + single_header->command_offset + i * single_header->command_stride);
            if (multi->kind != one->kind || multi->resource_index != one->resource_index ||
                multi->state_index != one->state_index || multi->payload_size != one->payload_size ||
                std::memcmp(retained_history.data() + multi->payload_offset,
                    single_bytes.data() + one->payload_offset, one->payload_size) != 0) return 282;
        }
        for (std::uint32_t i = 0U; i < retained_header->resource_count; ++i) {
            const auto* multi = reinterpret_cast<const progpu_native_scene_resource*>(
                retained_history.data() + retained_header->resource_offset + i * retained_header->resource_stride);
            const auto* one = reinterpret_cast<const progpu_native_scene_resource*>(
                single_bytes.data() + single_header->resource_offset + i * single_header->resource_stride);
            if (multi->kind != one->kind || multi->flags != one->flags || multi->payload_size != one->payload_size ||
                multi->auxiliary_size != one->auxiliary_size ||
                std::memcmp(retained_history.data() + multi->payload_offset,
                    single_bytes.data() + one->payload_offset, one->payload_size) != 0 ||
                std::memcmp(retained_history.data() + multi->auxiliary_offset,
                    single_bytes.data() + one->auxiliary_offset, one->auxiliary_size) != 0) return 282;
        }
        target->BeginDraw();
        target->DrawBitmap(alias.get(), nullptr, 1.0F, compat::bitmap_interpolation_mode::linear, nullptr);
        if (target->EndDraw(nullptr, nullptr) != com::ok) return 282;
        std::vector<std::byte> retained_parent(static_cast<std::size_t>(scene_target->GetRequiredSceneSize()));
        std::uint64_t parent_written = 0U;
        if (scene_target->BuildScene(retained_parent.data(), retained_parent.size(), &parent_written) != com::ok) return 282;
        const auto* parent_header = reinterpret_cast<const progpu_native_scene_header*>(retained_parent.data());
        const auto* parent_image = reinterpret_cast<const progpu_native_scene_resource*>(
            retained_parent.data() + parent_header->resource_offset);
        if (parent_image->auxiliary_size != retained_history.size() ||
            std::memcmp(retained_parent.data() + parent_image->auxiliary_offset,
                retained_history.data(), retained_history.size()) != 0) return 282;

        // A later full Clear replaces all history, while the already captured
        // parent retains the old commands and pixels' source metadata.
        source_target->BeginDraw();
        export_written = 123U;
        if (persistent_scene->GetRequiredSceneSize() != 0U ||
            persistent_scene->BuildScene(export_buffer.data(), export_buffer.size(), &export_written) != compat::wrong_state ||
            export_written != 0U) return 286;
        source_target->Clear(&second_clear);
        if (source_target->EndDraw(nullptr, nullptr) != com::ok) return 283;
        if (persistent_scene->GetRequiredSceneSize() >= retained_history.size() ||
            persistent_scene->BuildScene(export_buffer.data(), export_buffer.size(), &export_written) != com::ok)
            return 286;
        const auto* cleared_header = reinterpret_cast<const progpu_native_scene_header*>(export_buffer.data());
        if (cleared_header->command_count != 0U || cleared_header->resource_count != 0U ||
            cleared_header->generation <= retained_header->generation) return 286;
        persistent_scene->GetSummary(&persistent_summary);
        if (persistent_summary.draw_count != 0U || persistent_summary.clear_color.red != 1.0F ||
            persistent_summary.clear_color.alpha != 0.5F) return 283;
        source_target->BeginDraw();
        source_target->FillRectangle(&persistent_rectangle, target_brush.get());
        source_target->Clear(&first_clear);
        source_target->FillRectangle(&persistent_rectangle, target_brush.get());
        source_target->Clear(&second_clear);
        if (source_target->EndDraw(nullptr, nullptr) != com::ok) return 283;
        persistent_scene->GetSummary(&persistent_summary);
        if (persistent_summary.draw_count != 0U || persistent_summary.clear_color.alpha != 0.5F) return 283;
        std::vector<std::byte> parent_after_clear(retained_parent.size());
        if (scene_target->BuildScene(parent_after_clear.data(), parent_after_clear.size(), &parent_written) != com::ok ||
            parent_after_clear != retained_parent) return 283;

        // Bitmap DPI is fixed at target creation; render DPI belongs to the
        // captured image, not to the bitmap's logical-size view.
        source_target->SetDpi(144.0F, 144.0F);
        source_target->BeginDraw();
        source_target->Clear(&first_clear);
        source_target->FillRectangle(&persistent_rectangle, target_brush.get());
        if (source_target->EndDraw(nullptr, nullptr) != com::ok) return 284;
        compat::bitmap* raw_dpi_bitmap = nullptr;
        if (source_target->GetBitmap(&raw_dpi_bitmap) != com::ok) return 284;
        com::pointer<compat::bitmap> dpi_bitmap;
        dpi_bitmap.attach(raw_dpi_bitmap);
        float bitmap_dpi_x = 0.0F, bitmap_dpi_y = 0.0F;
        dpi_bitmap->GetDpi(&bitmap_dpi_x, &bitmap_dpi_y);
        if (bitmap_dpi_x != 192.0F || bitmap_dpi_y != 192.0F ||
            dpi_bitmap->GetSize().width != 16.0F || dpi_bitmap->GetSize().height != 12.0F) return 284;
        target->BeginDraw();
        target->DrawBitmap(dpi_bitmap.get(), nullptr, 1.0F, compat::bitmap_interpolation_mode::linear, nullptr);
        if (target->EndDraw(nullptr, nullptr) != com::ok) return 284;
        std::vector<std::byte> dpi_scene(static_cast<std::size_t>(scene_target->GetRequiredSceneSize()));
        std::uint64_t dpi_written = 0U;
        if (scene_target->BuildScene(dpi_scene.data(), dpi_scene.size(), &dpi_written) != com::ok) return 284;
        const auto* dpi_header = reinterpret_cast<const progpu_native_scene_header*>(dpi_scene.data());
        const auto* dpi_resource = reinterpret_cast<const progpu_native_scene_resource*>(dpi_scene.data() + dpi_header->resource_offset);
        const auto* dpi_picture = reinterpret_cast<const progpu_native_scene_picture_image*>(dpi_scene.data() + dpi_resource->payload_offset);
        const auto* dpi_command = reinterpret_cast<const progpu_native_scene_command*>(dpi_scene.data() + dpi_header->command_offset);
        const auto* dpi_draw = reinterpret_cast<const progpu_native_scene_image_draw*>(dpi_scene.data() + dpi_command->payload_offset);
        if (dpi_picture->dpi_scale != 1.5F || dpi_draw->destination_rect.width != 16.0F ||
            dpi_draw->destination_rect.height != 12.0F) return 284;
        // Changing raster DPI with old DIP commands cannot silently rescale the
        // bitmap. It needs an explicit new Clear until physical epochs exist.
        source_target->SetDpi(96.0F, 96.0F);
        if (persistent_scene->GetRequiredSceneSize() != 0U) return 285;
        source_target->BeginDraw();
        if (source_target->EndDraw(nullptr, nullptr) != compat::not_implemented) return 285;
        source_target->BeginDraw(); // Fresh target state after the reported error.
        source_target->FillRectangle(&persistent_rectangle, target_brush.get());
        if (source_target->EndDraw(nullptr, nullptr) != com::ok) return 285;
        persistent_scene->GetSummary(&persistent_summary);
        if (persistent_summary.draw_count != 1U || persistent_summary.has_clear != 1 ||
            persistent_summary.clear_color.alpha != 0.0F) return 285;
    }

    // Compatible storage uploads replace alpha, use physical pixel coordinates
    // independently of drawing state, and own the caller's padded upload bytes.
    for (const compat::pixel_format upload_format : {
            compat::pixel_format{28U, compat::alpha_mode::premultiplied},
            compat::pixel_format{87U, compat::alpha_mode::premultiplied},
            compat::pixel_format{87U, compat::alpha_mode::ignore},
            compat::pixel_format{65U, compat::alpha_mode::premultiplied}}) {
        const compat::size_f logical_size{4.0F, 4.0F};
        const compat::size_u pixel_size{8U, 8U};
        compat::bitmap_render_target* raw_upload_target = nullptr;
        if (target->CreateCompatibleRenderTarget(&logical_size, &pixel_size,
                &upload_format, compat::compatible_render_target_options::none, &raw_upload_target) != com::ok)
            return 287;
        com::pointer<compat::bitmap_render_target> upload_target;
        upload_target.attach(raw_upload_target);
        compat::bitmap* raw_upload_bitmap = nullptr;
        if (upload_target->GetBitmap(&raw_upload_bitmap) != com::ok) return 287;
        com::pointer<compat::bitmap> upload_bitmap;
        upload_bitmap.attach(raw_upload_bitmap);
        com::pointer<compat::scene_render_target_native> upload_scene;
        if (upload_bitmap.as(compat::scene_render_target_native_interface_id, upload_scene) != com::ok) return 287;
        const auto pixel_bytes = upload_format.format == 65U ? 1U : 4U;
        const auto pitch = pixel_bytes * 2U + 3U;
        std::vector<std::byte> upload_bytes(pitch + pixel_bytes * 2U, std::byte{0x40});
        const auto original_upload = upload_bytes;
        const compat::rectangle_u destination{2U, 1U, 4U, 3U};
        const compat::matrix_3x2_f unrelated_transform{2.0F, 0.0F, 0.0F, 3.0F, 200.0F, 300.0F};
        upload_target->SetTransform(&unrelated_transform);
        if (upload_bitmap->CopyFromMemory(&destination, upload_bytes.data(), pitch) != com::ok) return 287;
        std::fill(upload_bytes.begin(), upload_bytes.end(), std::byte{0xff});
        std::vector<std::byte> upload_stream(static_cast<std::size_t>(upload_scene->GetRequiredSceneSize()));
        std::uint64_t upload_written = 0U;
        if (upload_scene->BuildScene(upload_stream.data(), upload_stream.size(), &upload_written) != com::ok)
            return 287;
        const auto* header = reinterpret_cast<const progpu_native_scene_header*>(upload_stream.data());
        if (header->command_count != 3U || header->resource_count != 1U) return 287;
        const auto* resource = reinterpret_cast<const progpu_native_scene_resource*>(upload_stream.data() + header->resource_offset);
        const auto* commands = upload_stream.data() + header->command_offset;
        const auto* push = reinterpret_cast<const progpu_native_scene_command*>(commands);
        const auto* draw = reinterpret_cast<const progpu_native_scene_command*>(commands + header->command_stride);
        const auto* pop = reinterpret_cast<const progpu_native_scene_command*>(commands + 2U * header->command_stride);
        const auto* layer = reinterpret_cast<const progpu_native_scene_layer*>(upload_stream.data() + push->payload_offset);
        const auto* image = reinterpret_cast<const progpu_native_scene_image_draw*>(upload_stream.data() + draw->payload_offset);
        if (push->kind != PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER || pop->kind != PROGPU_NATIVE_SCENE_COMMAND_POP_LAYER ||
            draw->kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE || layer->blend_mode != PROGPU_NATIVE_BLEND_SRC ||
            layer->bounds.x != 1.0F || layer->bounds.y != 0.5F || layer->bounds.width != 1.0F || layer->bounds.height != 1.0F ||
            image->image_width != 2U || image->image_height != 2U || image->row_bytes != pitch ||
            image->transform.m11 != 1.0F || image->transform.m22 != 1.0F || image->transform.m31 != 0.0F ||
            image->transform.m32 != 0.0F || image->sampling != PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST ||
            resource->payload_size != original_upload.size() ||
            std::memcmp(upload_stream.data() + resource->payload_offset, original_upload.data(), original_upload.size()) != 0)
            return 287;
        if (upload_format.format == 65U) {
            const auto* matrix = reinterpret_cast<const progpu_native_scene_image_color_matrix*>(
                reinterpret_cast<const std::byte*>(image) + sizeof(*image));
            if ((resource->flags & PROGPU_NATIVE_SCENE_IMAGE_R8) == 0U ||
                (image->flags & PROGPU_NATIVE_SCENE_IMAGE_COLOR_MATRIX) == 0U || matrix->alpha[0] != 1.0F) return 287;
        }
        compat::scene_render_target_summary before_copy{};
        upload_scene->GetSummary(&before_copy);
        const compat::rectangle_u invalid_destination{7U, 0U, 9U, 2U};
        if (upload_bitmap->CopyFromMemory(nullptr, nullptr, pitch) != com::pointer_error ||
            upload_bitmap->CopyFromMemory(&invalid_destination, upload_bytes.data(), pitch) != com::invalid_argument ||
            upload_bitmap->CopyFromMemory(&destination, upload_bytes.data(), pixel_bytes * 2U - 1U) != com::invalid_argument)
            return 288;
        std::vector<std::byte> unchanged(upload_stream.size());
        if (upload_scene->BuildScene(unchanged.data(), unchanged.size(), &upload_written) != com::ok || unchanged != upload_stream)
            return 288;
        upload_target->BeginDraw();
        const compat::rectangle_f clip{0.0F, 0.0F, 2.0F, 2.0F};
        upload_target->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased);
        if (upload_bitmap->CopyFromMemory(&destination, upload_bytes.data(), pitch) != compat::wrong_state) return 288;
        upload_target->PopAxisAlignedClip();
        if (upload_bitmap->CopyFromMemory(&destination, upload_bytes.data(), pitch) != com::ok ||
            upload_target->EndDraw(nullptr, nullptr) != com::ok) return 288;
        compat::scene_render_target_summary after_copy{};
        upload_scene->GetSummary(&after_copy);
        if (after_copy.generation <= before_copy.generation || after_copy.draw_count != 2U) return 288;
        std::vector<std::byte> full_upload(8U * 8U * pixel_bytes, std::byte{0});
        if (upload_bitmap->CopyFromMemory(nullptr, full_upload.data(), 8U * pixel_bytes) != com::ok) return 288;
        const auto full_copy_size = upload_scene->GetRequiredSceneSize();
        for (unsigned int full_copy = 0U; full_copy < 48U; ++full_copy) {
            std::fill(full_upload.begin(), full_upload.end(), static_cast<std::byte>(full_copy));
            if (upload_bitmap->CopyFromMemory(nullptr, full_upload.data(), 8U * pixel_bytes) != com::ok ||
                upload_scene->GetRequiredSceneSize() != full_copy_size) return 294;
            compat::scene_render_target_summary full_copy_summary{};
            upload_scene->GetSummary(&full_copy_summary);
            if (full_copy_summary.draw_count != 1U) return 294;
        }
        std::vector<std::byte> compact_copy(static_cast<std::size_t>(full_copy_size));
        if (upload_scene->BuildScene(compact_copy.data(), compact_copy.size(), &upload_written) != com::ok) return 294;
        const auto* compact_header = reinterpret_cast<const progpu_native_scene_header*>(compact_copy.data());
        if (compact_header->command_count != 3U || compact_header->resource_count != 1U) return 294;
        // A failed full overwrite preserves the previous immutable export.
        if (upload_bitmap->CopyFromMemory(nullptr, full_upload.data(), 8U * pixel_bytes - 1U) != com::invalid_argument)
            return 294;
        std::vector<std::byte> compact_after_failure(compact_copy.size());
        if (upload_scene->BuildScene(compact_after_failure.data(), compact_after_failure.size(), &upload_written) != com::ok ||
            compact_after_failure != compact_copy) return 294;
        // Full coverage replaces invalid mixed-DPI history; a partial write
        // cannot reinterpret old content at the newly selected raster DPI.
        upload_target->SetDpi(144.0F, 144.0F);
        if (upload_bitmap->CopyFromMemory(&destination, upload_bytes.data(), pitch) != compat::not_implemented ||
            upload_bitmap->CopyFromMemory(nullptr, full_upload.data(), 8U * pixel_bytes) != com::ok ||
            upload_scene->GetRequiredSceneSize() == 0U) return 295;
        upload_target->SetDpi(192.0F, 192.0F);
        if (upload_bitmap->CopyFromMemory(nullptr, full_upload.data(), 8U * pixel_bytes) != com::ok) return 295;
        const compat::size_f chain_size{8.0F, 8.0F};
        compat::bitmap_render_target* raw_chain_target = nullptr;
        if (target->CreateCompatibleRenderTarget(&chain_size, &pixel_size, &upload_format,
                compat::compatible_render_target_options::none, &raw_chain_target) != com::ok) return 297;
        com::pointer<compat::bitmap_render_target> chain_target;
        chain_target.attach(raw_chain_target);
        compat::bitmap* raw_chain_bitmap = nullptr;
        if (chain_target->GetBitmap(&raw_chain_bitmap) != com::ok) return 297;
        com::pointer<compat::bitmap> chain_bitmap;
        chain_bitmap.attach(raw_chain_bitmap);
        for (unsigned int chain_copy = 0U; chain_copy < 64U; ++chain_copy) {
            if (chain_bitmap->CopyFromBitmap(nullptr, upload_bitmap.get(), nullptr) != com::ok ||
                upload_bitmap->CopyFromRenderTarget(nullptr, chain_target.get(), nullptr) != com::ok ||
                upload_scene->GetRequiredSceneSize() != full_copy_size) return 297;
        }
        std::vector<std::byte> chain_stream(static_cast<std::size_t>(upload_scene->GetRequiredSceneSize()));
        if (upload_scene->BuildScene(chain_stream.data(), chain_stream.size(), &upload_written) != com::ok) return 297;
        const auto* chain_header = reinterpret_cast<const progpu_native_scene_header*>(chain_stream.data());
        const auto* chain_resource = reinterpret_cast<const progpu_native_scene_resource*>(chain_stream.data() + chain_header->resource_offset);
        if (chain_header->command_count != 3U || chain_header->resource_count != 1U ||
            (chain_resource->flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) != 0U ||
            chain_resource->payload_size != full_upload.size() ||
            std::memcmp(chain_stream.data() + chain_resource->payload_offset, full_upload.data(), full_upload.size()) != 0)
            return 297;
        // Pixel-backed copies retain an image resource directly (not a nested
        // picture), crop source pixels and ignore either bitmap's DPI view.
        compat::bitmap* raw_pixel_source = nullptr;
        const compat::bitmap_properties source_properties{upload_format, 144.0F, 144.0F};
        std::vector<std::byte> pixel_source_data(4U * 4U * pixel_bytes, std::byte{0x20});
        if (target->CreateBitmap({4U, 4U}, pixel_source_data.data(), 4U * pixel_bytes,
                &source_properties, &raw_pixel_source) != com::ok) return 289;
        com::pointer<compat::bitmap> pixel_source;
        pixel_source.attach(raw_pixel_source);
        const compat::rectangle_u cropped_source{1U, 1U, 3U, 3U};
        std::array<com::pointer<compat::bitmap_render_target>, 2U> crop_targets;
        std::array<com::pointer<compat::bitmap>, 2U> crop_bitmaps;
        for (unsigned int crop = 0U; crop < 2U; ++crop) {
            const compat::size_f crop_logical{crop == 0U ? 1.0F : 2.0F, crop == 0U ? 1.0F : 2.0F};
            const compat::size_u crop_pixels{2U, 2U};
            compat::bitmap_render_target* raw_crop_target = nullptr;
            if (target->CreateCompatibleRenderTarget(&crop_logical, &crop_pixels, &upload_format,
                    compat::compatible_render_target_options::none, &raw_crop_target) != com::ok) return 299;
            crop_targets[crop].attach(raw_crop_target);
            compat::bitmap* raw_crop_bitmap = nullptr;
            if (crop_targets[crop]->GetBitmap(&raw_crop_bitmap) != com::ok) return 299;
            crop_bitmaps[crop].attach(raw_crop_bitmap);
        }
        if (crop_bitmaps[0]->CopyFromBitmap(nullptr, pixel_source.get(), &cropped_source) != com::ok) return 299;
        for (unsigned int copy = 0U; copy < 32U; ++copy) {
            if (crop_bitmaps[1]->CopyFromRenderTarget(nullptr, crop_targets[0].get(), nullptr) != com::ok ||
                crop_bitmaps[0]->CopyFromBitmap(nullptr, crop_bitmaps[1].get(), nullptr) != com::ok) return 299;
        }
        const compat::size_f one_logical{1.0F, 1.0F};
        const compat::size_u one_pixel{1U, 1U};
        compat::bitmap_render_target* raw_one_target = nullptr;
        if (target->CreateCompatibleRenderTarget(&one_logical, &one_pixel, &upload_format,
                compat::compatible_render_target_options::none, &raw_one_target) != com::ok) return 299;
        com::pointer<compat::bitmap_render_target> one_target;
        one_target.attach(raw_one_target);
        compat::bitmap* raw_one_bitmap = nullptr;
        if (one_target->GetBitmap(&raw_one_bitmap) != com::ok) return 299;
        com::pointer<compat::bitmap> one_bitmap;
        one_bitmap.attach(raw_one_bitmap);
        const compat::rectangle_u crop_again{1U, 0U, 2U, 1U};
        if (one_bitmap->CopyFromBitmap(nullptr, crop_bitmaps[0].get(), &crop_again) != com::ok) return 299;
        com::pointer<compat::scene_render_target_native> one_scene;
        if (one_bitmap.as(compat::scene_render_target_native_interface_id, one_scene) != com::ok) return 299;
        std::vector<std::byte> crop_stream(static_cast<std::size_t>(one_scene->GetRequiredSceneSize()));
        if (one_scene->BuildScene(crop_stream.data(), crop_stream.size(), &upload_written) != com::ok) return 299;
        const auto* crop_header = reinterpret_cast<const progpu_native_scene_header*>(crop_stream.data());
        const auto* crop_command = reinterpret_cast<const progpu_native_scene_command*>(crop_stream.data() +
            crop_header->command_offset + crop_header->command_stride);
        const auto* crop_image = reinterpret_cast<const progpu_native_scene_image_draw*>(crop_stream.data() + crop_command->payload_offset);
        const auto* crop_resource = reinterpret_cast<const progpu_native_scene_resource*>(crop_stream.data() + crop_header->resource_offset);
        if (crop_header->command_count != 3U || crop_header->resource_count != 1U ||
            crop_image->image_width != 4U || crop_image->image_height != 4U ||
            crop_image->source_rect.x != 2.0F || crop_image->source_rect.y != 1.0F ||
            crop_image->source_rect.width != 1.0F || crop_image->source_rect.height != 1.0F ||
            crop_image->row_bytes != 4U * pixel_bytes ||
            (crop_resource->flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) != 0U ||
            crop_resource->payload_size != pixel_source_data.size() ||
            std::memcmp(crop_stream.data() + crop_resource->payload_offset, pixel_source_data.data(), pixel_source_data.size()) != 0)
            return 299;
        const compat::point_2u copied_point{4U, 4U};
        if (upload_bitmap->CopyFromBitmap(&copied_point, pixel_source.get(), &cropped_source) != com::ok) return 289;
        std::vector<std::byte> pixel_copy_stream(static_cast<std::size_t>(upload_scene->GetRequiredSceneSize()));
        if (upload_scene->BuildScene(pixel_copy_stream.data(), pixel_copy_stream.size(), &upload_written) != com::ok) return 289;
        const auto* copy_header = reinterpret_cast<const progpu_native_scene_header*>(pixel_copy_stream.data());
        const auto* copy_command = reinterpret_cast<const progpu_native_scene_command*>(pixel_copy_stream.data() +
            copy_header->command_offset + (copy_header->command_count - 2U) * copy_header->command_stride);
        const auto* copy_image = reinterpret_cast<const progpu_native_scene_image_draw*>(pixel_copy_stream.data() + copy_command->payload_offset);
        const auto* copy_resource = reinterpret_cast<const progpu_native_scene_resource*>(pixel_copy_stream.data() +
            copy_header->resource_offset + copy_command->resource_index * copy_header->resource_stride);
        if (copy_image->source_rect.x != 1.0F || copy_image->source_rect.width != 2.0F ||
            copy_image->destination_rect.x != 2.0F || copy_image->destination_rect.width != 1.0F ||
            (copy_resource->flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) != 0U ||
            copy_resource->payload_size != pixel_source_data.size()) return 289;

        const compat::point_2u outside_point{7U, 7U};
        if (upload_bitmap->CopyFromBitmap(nullptr, nullptr, nullptr) != com::invalid_argument ||
            upload_bitmap->CopyFromRenderTarget(nullptr, nullptr, nullptr) != com::invalid_argument ||
            upload_bitmap->CopyFromBitmap(&outside_point, pixel_source.get(), &cropped_source) != com::invalid_argument)
            return 290;
        std::vector<std::byte> after_invalid_copy(pixel_copy_stream.size());
        if (upload_scene->BuildScene(after_invalid_copy.data(), after_invalid_copy.size(), &upload_written) != com::ok ||
            after_invalid_copy != pixel_copy_stream) return 290;

        // Capture a source that is still recording. Active copy snapshots must
        // not mark the source ended or poison its completed-export cache.
        const compat::size_f copy_logical_size{8.0F, 8.0F};
        compat::bitmap_render_target* raw_copy_target = nullptr;
        if (target->CreateCompatibleRenderTarget(&copy_logical_size, &pixel_size, &upload_format,
                compat::compatible_render_target_options::none, &raw_copy_target) != com::ok) return 291;
        com::pointer<compat::bitmap_render_target> copy_target;
        copy_target.attach(raw_copy_target);
        compat::bitmap* raw_copy_bitmap = nullptr;
        if (copy_target->GetBitmap(&raw_copy_bitmap) != com::ok) return 291;
        com::pointer<compat::bitmap> copy_bitmap;
        copy_bitmap.attach(raw_copy_bitmap);
        upload_target->BeginDraw();
        upload_target->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased);
        if (copy_bitmap->CopyFromRenderTarget(nullptr, upload_target.get(), nullptr) != compat::render_target_has_layer_or_cliprect)
            return 291;
        upload_target->PopAxisAlignedClip();
        if (copy_bitmap->CopyFromRenderTarget(nullptr, upload_target.get(), nullptr) != com::ok ||
            upload_scene->GetRequiredSceneSize() != 0U) return 291;
        const compat::color_f later_clear{0.0F, 0.0F, 1.0F, 1.0F};
        upload_target->Clear(&later_clear);
        if (upload_target->EndDraw(nullptr, nullptr) != com::ok) return 291;
        com::pointer<compat::scene_render_target_native> copied_scene;
        if (copy_bitmap.as(compat::scene_render_target_native_interface_id, copied_scene) != com::ok) return 291;
        std::vector<std::byte> captured_copy(static_cast<std::size_t>(copied_scene->GetRequiredSceneSize()));
        if (copied_scene->BuildScene(captured_copy.data(), captured_copy.size(), &upload_written) != com::ok) return 291;
        const auto* copied_header = reinterpret_cast<const progpu_native_scene_header*>(captured_copy.data());
        const auto* copied_resource = reinterpret_cast<const progpu_native_scene_resource*>(captured_copy.data() + copied_header->resource_offset);
        if (copied_header->command_count != 3U || (copied_resource->flags & PROGPU_NATIVE_SCENE_IMAGE_PICTURE) == 0U) return 291;
        const auto* captured_header = reinterpret_cast<const progpu_native_scene_header*>(captured_copy.data() + copied_resource->auxiliary_offset);
        if (captured_header->command_count <= 3U) return 291; // Later source Clear did not replace the capture.
        for (unsigned int picture_copy = 0U; picture_copy < 32U; ++picture_copy) {
            if (upload_bitmap->CopyFromBitmap(nullptr, copy_bitmap.get(), nullptr) != com::ok ||
                copy_bitmap->CopyFromRenderTarget(nullptr, upload_target.get(), nullptr) != com::ok ||
                copied_scene->GetRequiredSceneSize() != captured_copy.size()) return 298;
        }
        std::vector<std::byte> chained_picture(captured_copy.size());
        if (copied_scene->BuildScene(chained_picture.data(), chained_picture.size(), &upload_written) != com::ok) return 298;
        const auto* chained_header = reinterpret_cast<const progpu_native_scene_header*>(chained_picture.data());
        const auto* chained_resource = reinterpret_cast<const progpu_native_scene_resource*>(chained_picture.data() + chained_header->resource_offset);
        if (chained_resource->auxiliary_size != copied_resource->auxiliary_size ||
            std::memcmp(chained_picture.data() + chained_resource->auxiliary_offset,
                captured_copy.data() + copied_resource->auxiliary_offset, copied_resource->auxiliary_size) != 0)
            return 298;
        // Self-overlap through a shared alias consumes an immutable source and
        // has no source-to-destination COM ownership cycle.
        compat::bitmap* raw_copy_alias = nullptr;
        if (target->CreateSharedBitmap(compat::bitmap_interface_id, copy_bitmap.get(), nullptr, &raw_copy_alias) != com::ok)
            return 292;
        com::pointer<compat::bitmap> copy_alias;
        copy_alias.attach(raw_copy_alias);
        copy_target->BeginDraw();
        if (copy_bitmap->CopyFromBitmap(&copied_point, copy_alias.get(), &cropped_source) != com::ok ||
            copy_target->EndDraw(nullptr, nullptr) != com::ok) return 292;
        compat::scene_render_target_summary self_copy_summary{};
        copied_scene->GetSummary(&self_copy_summary);
        if (self_copy_summary.draw_count != 2U || copied_scene->GetRequiredSceneSize() <= captured_copy.size()) return 292;
        const auto before_noop_size = copied_scene->GetRequiredSceneSize();
        const compat::point_2u same_region{1U, 1U};
        if (copy_bitmap->CopyFromBitmap(&same_region, copy_alias.get(), &cropped_source) != com::ok ||
            copy_bitmap->CopyFromBitmap(nullptr, copy_alias.get(), nullptr) != com::ok ||
            copy_bitmap->CopyFromRenderTarget(nullptr, copy_target.get(), nullptr) != com::ok)
            return 296;
        compat::scene_render_target_summary after_noop{};
        copied_scene->GetSummary(&after_noop);
        if (after_noop.generation != self_copy_summary.generation ||
            after_noop.draw_count != self_copy_summary.draw_count || copied_scene->GetRequiredSceneSize() != before_noop_size)
            return 296;
        upload_target->BeginDraw();
        const compat::color_f invalid_clear{-1.0F, 0.0F, 0.0F, 1.0F};
        upload_target->Clear(&invalid_clear);
        if (copy_bitmap->CopyFromRenderTarget(nullptr, upload_target.get(), nullptr) != com::invalid_argument ||
            upload_target->EndDraw(nullptr, nullptr) != com::invalid_argument) return 293;
        compat::scene_render_target_summary after_failed_source{};
        copied_scene->GetSummary(&after_failed_source);
        if (after_failed_source.generation != self_copy_summary.generation ||
            after_failed_source.draw_count != self_copy_summary.draw_count) return 293;
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

    const compat::stroke_style_properties device_stroke_properties{
        compat::cap_style::round, compat::cap_style::round,
        compat::cap_style::round, compat::line_join::round,
        4.0F, compat::dash_style::custom, 0.5F};
    const float device_stroke_dashes[]{1.0F, 2.0F, 3.0F};
    const compat::matrix_3x2_f device_stroke_transform{
        2.0F, 0.25F, 0.5F, 3.0F, 10.0F, 20.0F};
    compat::matrix_3x2_f saved_stroke_transform{};
    target->GetTransform(&saved_stroke_transform);
    float saved_stroke_dpi_x{}, saved_stroke_dpi_y{};
    target->GetDpi(&saved_stroke_dpi_x, &saved_stroke_dpi_y);
    target->SetDpi(192.0F, 192.0F);
    for (const auto mode : {compat::stroke_transform_type::normal,
                           compat::stroke_transform_type::fixed,
                           compat::stroke_transform_type::hairline}) {
        com::pointer<compat::stroke_style1> device_style;
        if (compat::create_stroke_style1(
                factory.get(), &device_stroke_properties, mode,
                device_stroke_dashes, 3U, device_style.put()) != com::ok ||
            !device_style || device_style->GetStrokeTransformType() != mode) {
            return 411;
        }
        com::pointer<compat::stroke_style> device_base;
        com::pointer<compat::stroke_style1> device_roundtrip;
        if (device_style.as(compat::stroke_style_interface_id, device_base) != com::ok ||
            device_base.as(compat::stroke_style1_interface_id, device_roundtrip) != com::ok ||
            device_roundtrip.get() != device_style.get()) {
            return 412;
        }
#if defined(_WIN32)
        auto* sdk_style = reinterpret_cast<ID2D1StrokeStyle1*>(device_style.get());
        if (!com::guid_equal(compat::stroke_style1_interface_id,
                __uuidof(ID2D1StrokeStyle1)) ||
            static_cast<std::uint32_t>(sdk_style->GetStrokeTransformType()) !=
                static_cast<std::uint32_t>(mode) ||
            sdk_style->GetStartCap() != D2D1_CAP_STYLE_ROUND) {
            return 413;
        }
#endif
        for (const bool curved : {false, true}) {
            target->SetTransform(&device_stroke_transform);
            target->BeginDraw();
            const float requested_width = mode == compat::stroke_transform_type::hairline
                ? (curved ? std::numeric_limits<float>::max() : 0.0F) : 2.0F;
            if (curved) {
                target->DrawEllipse(&ellipse_value, target_brush.get(),
                    requested_width, device_style.get());
            } else {
                target->DrawLine({1.0F, 2.0F}, {18.0F, 12.0F}, target_brush.get(),
                    requested_width, device_style.get());
            }
            if (target->EndDraw(nullptr, nullptr) != com::ok) {
                return 414;
            }
            std::vector<std::byte> bytes(
                static_cast<std::size_t>(scene_target->GetRequiredSceneSize()));
            std::uint64_t written{};
            if (scene_target->BuildScene(bytes.data(), bytes.size(), &written) != com::ok ||
                written != bytes.size()) {
                return 415;
            }
            const auto* header = reinterpret_cast<const progpu_native_scene_header*>(bytes.data());
            if (header->command_count != 1U) {
                std::fprintf(stderr, "stroke mode=%u curved=%d commands=%u resources=%u\n",
                    static_cast<unsigned>(mode), curved, header->command_count, header->resource_count);
                return 416;
            }
            const progpu_native_scene_resource* device_stroke_resource = nullptr;
            const auto expected_kind = static_cast<std::uint32_t>(curved
                ? PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH
                : PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH);
            for (std::uint32_t index = 0U; index < header->resource_count; ++index) {
                const auto* candidate = reinterpret_cast<const progpu_native_scene_resource*>(
                    bytes.data() + header->resource_offset + index * header->resource_stride);
                if (candidate->kind == expected_kind) {
                    if (device_stroke_resource != nullptr) {
                        return 416;
                    }
                    device_stroke_resource = candidate;
                }
            }
            if (device_stroke_resource == nullptr) {
                return 416;
            }
            std::uint32_t flags{};
            float thickness{};
            if (curved) {
                if (device_stroke_resource->kind != PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH) {
                    return 417;
                }
                const auto* primitive = reinterpret_cast<const progpu_native_geometry_primitive*>(
                    bytes.data() + device_stroke_resource->payload_offset);
                flags = primitive->flags;
                thickness = primitive->stroke_thickness;
            } else {
                if (device_stroke_resource->kind != PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH) {
                    return 418;
                }
                const auto* stroke = reinterpret_cast<const progpu_native_scene_stroke*>(
                    bytes.data() + device_stroke_resource->payload_offset);
                flags = stroke->flags;
                thickness = stroke->stroke_thickness;
                const auto* intervals = reinterpret_cast<const double*>(
                    bytes.data() + device_stroke_resource->auxiliary_offset +
                    stroke->point_count * sizeof(progpu_native_point));
                const double scale = mode == compat::stroke_transform_type::hairline ? 0.5 : 1.0;
                if (stroke->dash_interval_count != 3U ||
                    stroke->dash_offset != 0.5 * scale) {
                    return 419;
                }
                for (std::size_t index = 0U; index < 3U; ++index) {
                    if (intervals[index] != device_stroke_dashes[index] * scale) {
                        return 419;
                    }
                }
            }
            const std::uint32_t fixed_flag = curved
                ? static_cast<std::uint32_t>(PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE)
                : static_cast<std::uint32_t>(PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE);
            const std::uint32_t hairline_flag = curved
                ? static_cast<std::uint32_t>(PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE)
                : static_cast<std::uint32_t>(PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE);
            const std::uint32_t expected = mode == compat::stroke_transform_type::fixed
                ? fixed_flag : mode == compat::stroke_transform_type::hairline ? hairline_flag : 0U;
            if ((flags & (fixed_flag | hairline_flag)) != expected ||
                thickness != (mode == compat::stroke_transform_type::hairline
                    ? 0.0F : requested_width)) {
                return 419;
            }
        }
    }
    target->SetTransform(&saved_stroke_transform);
    target->SetDpi(saved_stroke_dpi_x, saved_stroke_dpi_y);
    com::pointer<compat::stroke_style1> invalid_device_style;
    if (compat::create_stroke_style1(factory.get(), &device_stroke_properties,
            static_cast<compat::stroke_transform_type>(3U), nullptr, 0U,
            invalid_device_style.put()) != com::invalid_argument || invalid_device_style) {
        return 420;
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

    constexpr float maximum_float = std::numeric_limits<float>::max();
    compat::layer_parameters full_opacity_brush_layer_parameters =
        opacity_brush_layer_parameters;
    full_opacity_brush_layer_parameters.content_bounds = {
        -maximum_float,
        -maximum_float,
        maximum_float,
        maximum_float};
    const compat::matrix_3x2_f full_layer_transform{
        2.0F, 0.0F, 0.0F, 2.0F, 10.0F, 20.0F};
    target->SetTransform(&full_layer_transform);
    target->BeginDraw();
    target->PushLayer(
        &full_opacity_brush_layer_parameters, target_layer.get());
    target->FillRectangle(
        &layer_bounds, static_cast<compat::brush*>(target_brush.get()));
    target->PopLayer();
    const com::result full_opacity_brush_result =
        target->EndDraw(nullptr, nullptr);
    const compat::matrix_3x2_f identity_matrix{
        1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
    target->SetTransform(&identity_matrix);
    if (full_opacity_brush_result != com::ok ||
        scene_target->GetRequiredSceneSize() == 0U) {
        return 494;
    }
    const std::uint64_t full_opacity_brush_scene_size =
        scene_target->GetRequiredSceneSize();
    std::vector<std::byte> full_opacity_brush_scene(
        static_cast<std::size_t>(full_opacity_brush_scene_size));
    std::uint64_t full_opacity_brush_scene_written = 0U;
    if (scene_target->BuildScene(
            full_opacity_brush_scene.data(),
            full_opacity_brush_scene.size(),
            &full_opacity_brush_scene_written) != com::ok ||
        full_opacity_brush_scene_written !=
            full_opacity_brush_scene_size) {
        return 495;
    }
    const auto* full_opacity_brush_header = reinterpret_cast<
        const progpu_native_scene_header*>(
        full_opacity_brush_scene.data());
    const auto* full_opacity_brush_push = reinterpret_cast<
        const progpu_native_scene_command*>(
        full_opacity_brush_scene.data() +
        full_opacity_brush_header->command_offset);
    const auto* full_opacity_brush_layer = reinterpret_cast<
        const progpu_native_scene_layer*>(
        full_opacity_brush_scene.data() +
        full_opacity_brush_push->payload_offset);
    if (full_opacity_brush_push->kind !=
            PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER ||
        (full_opacity_brush_layer->flags &
            PROGPU_NATIVE_SCENE_LAYER_BOUNDS) != 0U ||
        full_opacity_brush_layer->mask_resource_index ==
            PROGPU_NATIVE_SCENE_NO_INDEX ||
        full_opacity_brush_layer->mask_resource_index >=
            full_opacity_brush_header->resource_count) {
        return 496;
    }
    const auto* full_opacity_brush_resource = reinterpret_cast<
        const progpu_native_scene_resource*>(
        full_opacity_brush_scene.data() +
        full_opacity_brush_header->resource_offset +
        static_cast<std::size_t>(
            full_opacity_brush_layer->mask_resource_index) *
            full_opacity_brush_header->resource_stride);
    const auto* full_opacity_brush_mask = reinterpret_cast<
        const progpu_native_scene_layer_brush_mask*>(
        full_opacity_brush_scene.data() +
        full_opacity_brush_resource->payload_offset);
    if (full_opacity_brush_resource->kind !=
            PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK ||
        full_opacity_brush_resource->payload_size <
            sizeof(progpu_native_scene_layer_brush_mask) ||
        full_opacity_brush_mask->kind !=
            PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH ||
        !approximately_equal(full_opacity_brush_mask->bounds.x, -5.0F) ||
        !approximately_equal(full_opacity_brush_mask->bounds.y, -10.0F) ||
        !approximately_equal(full_opacity_brush_mask->bounds.width, 320.0F) ||
        !approximately_equal(full_opacity_brush_mask->bounds.height, 240.0F) ||
        !approximately_equal(full_opacity_brush_mask->transform.m11, 2.0F) ||
        !approximately_equal(full_opacity_brush_mask->transform.m22, 2.0F) ||
        !approximately_equal(full_opacity_brush_mask->transform.m31, 10.0F) ||
        !approximately_equal(full_opacity_brush_mask->transform.m32, 20.0F)) {
        return 497;
    }

    compat::layer_parameters composite_mask_layer_parameters =
        masked_layer_parameters;
    const std::array affine_full_layer_transforms{
        compat::matrix_3x2_f{0.0F, 1.0F, -1.0F, 0.0F, 10.0F, 20.0F},
        compat::matrix_3x2_f{0.6F, 0.8F, -0.8F, 0.6F, -30.0F, 10.0F},
        compat::matrix_3x2_f{1.0F, 0.5F, 0.25F, -1.0F, 17.0F, -23.0F}};
    float saved_layer_dpi_x = 0.0F;
    float saved_layer_dpi_y = 0.0F;
    target->GetDpi(&saved_layer_dpi_x, &saved_layer_dpi_y);
    const auto layer_pixel_size = target->GetPixelSize();
    for (const auto dpi : {compat::size_f{96.0F, 96.0F}, compat::size_f{144.0F, 192.0F}}) {
        target->SetDpi(dpi.width, dpi.height);
        for (const auto& transform : affine_full_layer_transforms) {
            for (const bool gradient : {false, true}) {
                auto parameters = full_opacity_brush_layer_parameters;
                parameters.opacity_brush = gradient ? static_cast<compat::brush*>(linear_brush.get())
                    : static_cast<compat::brush*>(target_brush.get());
                target->SetTransform(&transform);
                target->BeginDraw();
                target->PushLayer(&parameters, target_layer.get());
                target->FillRectangle(&layer_bounds, target_brush.get());
                target->PopLayer();
                if (target->EndDraw(nullptr, nullptr) != com::ok) return 318;
                const auto size = scene_target->GetRequiredSceneSize();
                std::vector<std::byte> scene(static_cast<std::size_t>(size));
                std::uint64_t written = 0U;
                if (size == 0U || scene_target->BuildScene(scene.data(), scene.size(), &written) != com::ok ||
                    written != size) return 319;
                const auto* header = reinterpret_cast<const progpu_native_scene_header*>(scene.data());
                const auto* command = reinterpret_cast<const progpu_native_scene_command*>(scene.data() + header->command_offset);
                const auto* layer = reinterpret_cast<const progpu_native_scene_layer*>(scene.data() + command->payload_offset);
                if ((layer->flags & PROGPU_NATIVE_SCENE_LAYER_BOUNDS) != 0U ||
                    layer->mask_resource_index >= header->resource_count) return 320;
                const auto* resource = reinterpret_cast<const progpu_native_scene_resource*>(scene.data() +
                    header->resource_offset + layer->mask_resource_index * header->resource_stride);
                const auto* mask = reinterpret_cast<const progpu_native_scene_layer_brush_mask*>(scene.data() + resource->payload_offset);
                if (mask->kind != PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH ||
                    mask->transform.m11 != transform.m11 || mask->transform.m12 != transform.m12 ||
                    mask->transform.m21 != transform.m21 || mask->transform.m22 != transform.m22 ||
                    mask->transform.m31 != transform.m31 || mask->transform.m32 != transform.m32) return 321;
                // Independent double inverse oracle: every viewport corner must
                // remain covered by the local mask domain (float transport tolerance).
                const double determinant = double{transform.m11} * transform.m22 -
                    double{transform.m12} * transform.m21;
                for (const double x : {0.0, static_cast<double>(layer_pixel_size.width) * 96.0 / dpi.width}) {
                    for (const double y : {0.0, static_cast<double>(layer_pixel_size.height) * 96.0 / dpi.height}) {
                        const double local_x = ((x - transform.m31) * transform.m22 -
                            (y - transform.m32) * transform.m21) / determinant;
                        const double local_y = ((y - transform.m32) * transform.m11 -
                            (x - transform.m31) * transform.m12) / determinant;
                        constexpr double tolerance = 0.002;
                        if (local_x < mask->bounds.x - tolerance || local_y < mask->bounds.y - tolerance ||
                            local_x > double{mask->bounds.x} + mask->bounds.width + tolerance ||
                            local_y > double{mask->bounds.y} + mask->bounds.height + tolerance) return 322;
                    }
                }
            }
        }
    }
    const compat::matrix_3x2_f singular_layer_transform{1.0F, 2.0F, 2.0F, 4.0F, 0.0F, 0.0F};
    target->SetTransform(&singular_layer_transform);
    target->BeginDraw();
    target->PushLayer(&full_opacity_brush_layer_parameters, target_layer.get());
    if (target->EndDraw(nullptr, nullptr) != compat::not_implemented ||
        scene_target->GetRequiredSceneSize() != 0U) return 323;
    target->SetTransform(&identity_matrix);
    target->SetDpi(saved_layer_dpi_x, saved_layer_dpi_y);
    // Bitmap opacity is captured as a child image scene. Inspect its retained
    // contract independently of the eventual Windows/GPU image comparison.
    const auto inspect_bitmap_opacity = [&](const std::vector<std::byte>& scene,
        bool composite, std::uint32_t expected_grid,
        compat::bitmap_interpolation_mode interpolation,
        const compat::matrix_3x2_f& world) {
        const auto* header = reinterpret_cast<const progpu_native_scene_header*>(scene.data());
        const progpu_native_scene_layer_picture_mask* picture = nullptr;
        const std::byte* stream = nullptr;
        for (std::uint32_t index = 0U; index < header->resource_count; ++index) {
            const auto* resource = reinterpret_cast<const progpu_native_scene_resource*>(scene.data() +
                header->resource_offset + index * header->resource_stride);
            if (resource->kind != PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) continue;
            const auto* candidate = reinterpret_cast<const progpu_native_scene_layer_picture_mask*>(
                scene.data() + resource->payload_offset);
            if (!composite && candidate->kind == PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE) {
                picture = candidate;
                stream = scene.data() + resource->auxiliary_offset + picture->stream_offset;
                break;
            }
            if (composite && candidate->kind == PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE) {
                const auto* mask = reinterpret_cast<const progpu_native_scene_layer_composite_mask*>(candidate);
                if (mask->component_count != 2U || mask->picture_mask_count != 1U ||
                    mask->path_count != 1U || mask->brush_mask_count != 0U ||
                    mask->geometry_mask_count != 0U || mask->geometry_primitive_count != 0U ||
                    mask->gradient_stop_count != 0U || mask->opacity != 1.0F) return false;
                picture = reinterpret_cast<const progpu_native_scene_layer_picture_mask*>(
                    scene.data() + resource->auxiliary_offset);
                stream = reinterpret_cast<const std::byte*>(picture + 1) + picture->stream_offset;
                const auto* path = reinterpret_cast<const progpu_native_scene_clip_path*>(
                    reinterpret_cast<const std::byte*>(picture + 1) + mask->picture_stream_bytes);
                if (path->sample_grid != expected_grid || path->segment_count == 0U) return false;
                break;
            }
        }
        if (picture == nullptr || picture->kind != PROGPU_NATIVE_SCENE_LAYER_MASK_PICTURE ||
            picture->opacity != 1.0F || picture->transform.m11 != 1.0F ||
            picture->transform.m22 != 1.0F || picture->transform.m31 != 0.0F ||
            picture->transform.m32 != 0.0F || picture->stream_size < sizeof(progpu_native_scene_header)) return false;
        const auto* child = reinterpret_cast<const progpu_native_scene_header*>(stream);
        if (child->command_count != 1U) return false;
        const auto* command = reinterpret_cast<const progpu_native_scene_command*>(stream + child->command_offset);
        if (command->kind != PROGPU_NATIVE_SCENE_COMMAND_DRAW_IMAGE) return false;
        const auto* image = reinterpret_cast<const progpu_native_scene_image_draw*>(stream + command->payload_offset);
        const auto expected_sampling = interpolation == compat::bitmap_interpolation_mode::nearest_neighbor
            ? PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST : PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
        return image->opacity == 0.375F && image->sampling == expected_sampling &&
            (image->flags & PROGPU_NATIVE_SCENE_IMAGE_EXTENDED_SOURCE_RECT) != 0U &&
            ((image->flags >> PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_SHIFT) & 3U) == PROGPU_NATIVE_IMAGE_ADDRESS_REPEAT &&
            ((image->flags >> PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_V_SHIFT) & 3U) == PROGPU_NATIVE_IMAGE_ADDRESS_MIRROR_REPEAT &&
            image->transform.m11 == world.m11 && image->transform.m12 == world.m12 &&
            image->transform.m21 == world.m21 && image->transform.m22 == world.m22 &&
            approximately_equal(image->transform.m31, world.m11 + 2.0F * world.m21 + world.m31) &&
            approximately_equal(image->transform.m32, world.m12 + 2.0F * world.m22 + world.m32);
    };
    for (const auto interpolation : {compat::bitmap_interpolation_mode::nearest_neighbor,
            compat::bitmap_interpolation_mode::linear}) {
        for (const bool full_target : {false, true}) {
            for (const bool geometric : {false, true}) {
                for (const auto mode : {compat::antialias_mode::aliased, compat::antialias_mode::per_primitive}) {
                    auto parameters = full_target ? full_opacity_brush_layer_parameters : opacity_brush_layer_parameters;
                    parameters.opacity_brush = bitmap_brush.get();
                    parameters.geometric_mask = geometric ? path_base.get() : nullptr;
                    parameters.mask_antialias_mode = mode;
                    const auto world = full_target ? affine_full_layer_transforms[1] : identity_matrix;
                    bitmap_brush->SetOpacity(0.375F);
                    bitmap_brush->SetExtendModeX(compat::extend_mode::wrap);
                    bitmap_brush->SetExtendModeY(compat::extend_mode::mirror);
                    bitmap_brush->SetInterpolationMode(interpolation);
                    target->SetTransform(&world);
                    target->BeginDraw();
                    target->PushLayer(&parameters, target_layer.get());
                    // Snapshot lifetime must not depend on subsequent brush mutations.
                    bitmap_brush->SetBitmap(nullptr);
                    bitmap_brush->SetOpacity(0.875F);
                    target->FillRectangle(&layer_bounds, target_brush.get());
                    target->PopLayer();
                    bitmap_brush->SetBitmap(portable_bitmap.get());
                    // A main-scene image must not reuse child-scene resource indices.
                    target->FillRectangle(&layer_bounds, bitmap_brush.get());
                    if (target->EndDraw(nullptr, nullptr) != com::ok) return 324;
                    scene_target->GetSummary(&target_summary);
                    if (target_summary.draw_count != 2U) return 325;
                    const auto size = scene_target->GetRequiredSceneSize();
                    std::vector<std::byte> scene(static_cast<std::size_t>(size));
                    std::uint64_t written = 0U;
                    if (size == 0U || scene_target->BuildScene(scene.data(), size, &written) != com::ok ||
                        written != size || !inspect_bitmap_opacity(scene, geometric,
                            mode == compat::antialias_mode::aliased ? 1U : 8U, interpolation, world)) return 326;
                }
            }
        }
    }
    target->SetTransform(&identity_matrix);
    for (const bool bitmap_paint : {false, true}) {
        for (const auto mode : {compat::antialias_mode::aliased, compat::antialias_mode::per_primitive}) {
            bitmap_brush->SetOpacity(0.375F);
            target->SetAntialiasMode(mode);
            target->BeginDraw();
            target->FillGeometry(path_base.get(), bitmap_paint ? bitmap_brush_base.get()
                : static_cast<compat::brush*>(target_brush.get()), bitmap_brush.get());
            if (target->EndDraw(nullptr, nullptr) != com::ok) return 327;
            scene_target->GetSummary(&target_summary);
            if (target_summary.draw_count != 1U) return 328;
            const auto size = scene_target->GetRequiredSceneSize();
            std::vector<std::byte> scene(static_cast<std::size_t>(size));
            std::uint64_t written = 0U;
            if (size == 0U || scene_target->BuildScene(scene.data(), size, &written) != com::ok ||
                written != size || !inspect_bitmap_opacity(scene, bitmap_paint,
                    mode == compat::antialias_mode::aliased ? 1U : 8U,
                    compat::bitmap_interpolation_mode::linear, identity_matrix)) return 329;
        }
    }
    bitmap_brush->SetBitmap(nullptr);
    auto missing_bitmap_layer = opacity_brush_layer_parameters;
    missing_bitmap_layer.opacity_brush = bitmap_brush.get();
    target->BeginDraw();
    target->PushLayer(&missing_bitmap_layer, target_layer.get());
    if (target->EndDraw(nullptr, nullptr) != com::invalid_argument ||
        scene_target->GetRequiredSceneSize() != 0U) return 330;
    bitmap_brush->SetBitmap(portable_bitmap.get());
    bitmap_brush->SetOpacity(0.75F);
    bitmap_brush->SetExtendModeX(compat::extend_mode::clamp);
    bitmap_brush->SetExtendModeY(compat::extend_mode::wrap);
    target->SetAntialiasMode(compat::antialias_mode::per_primitive);
    composite_mask_layer_parameters.opacity_brush =
        static_cast<compat::brush*>(linear_brush.get());
    for (const auto* parameters : std::array<const compat::layer_parameters*, 6U>{&layer_parameters, &masked_layer_parameters,
            &opacity_brush_layer_parameters, &full_opacity_brush_layer_parameters,
            &composite_mask_layer_parameters, &missing_bitmap_layer}) {
        std::vector<std::byte> explicit_scene;
        for (const bool automatic : {false, true}) {
            target->BeginDraw();
            target->PushLayer(parameters, automatic ? nullptr : target_layer.get());
            target->FillRectangle(&layer_bounds, target_brush.get());
            target->PopLayer();
            if (target->EndDraw(nullptr, nullptr) != com::ok) return 340;
            const auto size = scene_target->GetRequiredSceneSize();
            std::vector<std::byte> scene(static_cast<std::size_t>(size));
            std::uint64_t written = 0U;
            if (size == 0U || scene_target->BuildScene(scene.data(), size, &written) != com::ok ||
                size != written) return 341;
            if (!automatic) explicit_scene = std::move(scene);
            else if (scene != explicit_scene) return 342;
        }
    }
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

    for (const auto mode : {compat::antialias_mode::aliased, compat::antialias_mode::per_primitive}) {
        const std::uint32_t expected_grid = mode == compat::antialias_mode::aliased ? 1U : 8U;
        for (const bool with_opacity : {false, true}) {
            auto parameters = masked_layer_parameters;
            parameters.mask_antialias_mode = mode;
            if (!with_opacity) parameters.opacity_brush = nullptr;
            // The mask's mode is independent of the target's geometry mode.
            target->SetAntialiasMode(mode == compat::antialias_mode::aliased
                ? compat::antialias_mode::per_primitive : compat::antialias_mode::aliased);
            target->BeginDraw();
            target->PushLayer(&parameters, target_layer.get());
            target->FillRectangle(&layer_bounds, target_brush.get());
            target->PopLayer();
            if (target->EndDraw(nullptr, nullptr) != com::ok) return 306;
            const auto size = scene_target->GetRequiredSceneSize();
            std::vector<std::byte> scene(static_cast<std::size_t>(size));
            std::uint64_t written = 0U;
            if (size == 0U || scene_target->BuildScene(scene.data(), scene.size(), &written) != com::ok ||
                written != size) return 307;
            const auto* header = reinterpret_cast<const progpu_native_scene_header*>(scene.data());
            const auto* command = reinterpret_cast<const progpu_native_scene_command*>(scene.data() + header->command_offset);
            const auto* layer = reinterpret_cast<const progpu_native_scene_layer*>(scene.data() + command->payload_offset);
            if (layer->mask_resource_index >= header->resource_count) return 308;
            const auto* resource = reinterpret_cast<const progpu_native_scene_resource*>(scene.data() +
                header->resource_offset + layer->mask_resource_index * header->resource_stride);
            const std::byte* payload = scene.data() + resource->payload_offset;
            std::size_t path_offset = sizeof(progpu_native_scene_layer_vector_mask);
            if (with_opacity) {
                const auto* mask = reinterpret_cast<const progpu_native_scene_layer_composite_mask*>(payload);
                if (mask->kind != PROGPU_NATIVE_SCENE_LAYER_MASK_COMPOSITE ||
                    mask->path_count != 1U || mask->brush_mask_count != 1U ||
                    mask->geometry_mask_count != 0U || mask->picture_mask_count != 0U) return 309;
                path_offset = sizeof(*mask) + sizeof(progpu_native_scene_layer_brush_mask);
            } else {
                const auto* mask = reinterpret_cast<const progpu_native_scene_layer_vector_mask*>(payload);
                if (mask->kind != PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN || mask->path_count != 1U)
                    return 310;
            }
            const auto* path = reinterpret_cast<const progpu_native_scene_clip_path*>(payload + path_offset);
            if (path->sample_grid != expected_grid) return 311;
        }
        target->SetAntialiasMode(mode);
        for (const bool bitmap_paint : {false, true}) {
            target->BeginDraw();
            target->FillGeometry(path_base.get(), bitmap_paint
                ? static_cast<compat::brush*>(bitmap_brush.get())
                : static_cast<compat::brush*>(target_brush.get()), nullptr);
            if (target->EndDraw(nullptr, nullptr) != com::ok) return 312;
            const auto size = scene_target->GetRequiredSceneSize();
            std::vector<std::byte> scene(static_cast<std::size_t>(size));
            std::uint64_t written = 0U;
            if (size == 0U || scene_target->BuildScene(scene.data(), scene.size(), &written) != com::ok ||
                written != size) return 313;
            const auto* header = reinterpret_cast<const progpu_native_scene_header*>(scene.data());
            bool found_coverage = false;
            for (std::uint32_t index = 0U; index < header->resource_count; ++index) {
                const auto* resource = reinterpret_cast<const progpu_native_scene_resource*>(scene.data() +
                    header->resource_offset + index * header->resource_stride);
                const std::byte* payload = scene.data() + resource->payload_offset;
                if (!bitmap_paint && resource->kind == PROGPU_NATIVE_SCENE_RESOURCE_PATH_BATCH) {
                    const auto* path = reinterpret_cast<const progpu_native_scene_path_fill*>(payload);
                    if (path->sample_grid != expected_grid) return 314;
                    found_coverage = true;
                } else if (bitmap_paint && resource->kind == PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) {
                    const auto* mask = reinterpret_cast<const progpu_native_scene_layer_vector_mask*>(payload);
                    if (mask->kind != PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN || mask->path_count != 1U)
                        return 315;
                    const auto* path = reinterpret_cast<const progpu_native_scene_clip_path*>(payload + sizeof(*mask));
                    if (path->sample_grid != expected_grid) return 316;
                    found_coverage = true;
                }
            }
            if (!found_coverage) return 317;
        }
    }
    target->SetAntialiasMode(compat::antialias_mode::per_primitive);

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
    target->SetAntialiasMode(compat::antialias_mode::per_primitive);
    target->BeginDraw();
    target->FillMesh(target_mesh.get(), target_brush.get());
    if (target->EndDraw(nullptr, nullptr) != compat::wrong_state ||
        scene_target->GetRequiredSceneSize() != 0U) {
        std::fprintf(stderr, "FillMesh must reject per-primitive antialiasing\n");
        return 300;
    }
    target->SetAntialiasMode(compat::antialias_mode::aliased);
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
        mesh_paths[2].segment_count != 3U ||
        mesh_paths[0].sample_grid != 1U ||
        mesh_paths[1].sample_grid != 1U ||
        mesh_paths[2].sample_grid != 1U) {
        return 215;
    }

    // The sampled-brush lane must use the same aliased mesh coverage as the
    // ordinary brush lane, without changing texture sampling or draw count.
    target->BeginDraw();
    target->FillMesh(target_mesh.get(), bitmap_brush.get());
    if (target->EndDraw(nullptr, nullptr) != com::ok) return 301;
    const auto bitmap_mesh_size = scene_target->GetRequiredSceneSize();
    std::vector<std::byte> bitmap_mesh_scene(static_cast<std::size_t>(bitmap_mesh_size));
    std::uint64_t bitmap_mesh_written = 0U;
    if (bitmap_mesh_size == 0U || scene_target->BuildScene(bitmap_mesh_scene.data(),
            bitmap_mesh_scene.size(), &bitmap_mesh_written) != com::ok ||
        bitmap_mesh_written != bitmap_mesh_size) return 302;
    const auto* bitmap_mesh_header = reinterpret_cast<const progpu_native_scene_header*>(bitmap_mesh_scene.data());
    bool found_mesh_mask = false;
    for (std::uint32_t index = 0U; index < bitmap_mesh_header->resource_count; ++index) {
        const auto* resource = reinterpret_cast<const progpu_native_scene_resource*>(
            bitmap_mesh_scene.data() + bitmap_mesh_header->resource_offset +
            index * bitmap_mesh_header->resource_stride);
        if (resource->kind != PROGPU_NATIVE_SCENE_RESOURCE_LAYER_MASK) continue;
        const auto* mask = reinterpret_cast<const progpu_native_scene_layer_vector_mask*>(
            bitmap_mesh_scene.data() + resource->payload_offset);
        if (mask->kind != PROGPU_NATIVE_SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN || mask->path_count != 1U)
            return 303;
        const auto* path = reinterpret_cast<const progpu_native_scene_clip_path*>(
            reinterpret_cast<const std::byte*>(mask) + sizeof(*mask));
        if (path->sample_grid != 1U || path->segment_count != 9U) return 304;
        found_mesh_mask = true;
    }
    if (!found_mesh_mask || bitmap_mesh_header->command_count != 1U) return 305;
    target->SetAntialiasMode(compat::antialias_mode::per_primitive);

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
    auto* native_target_layer = reinterpret_cast<ID2D1Layer*>(
        target_layer.get());
    auto* native_layer_brush = reinterpret_cast<ID2D1SolidColorBrush*>(
        target_brush.get());
    D2D1_MATRIX_3X2_F native_identity{};
    native_identity._11 = 1.0F;
    native_identity._12 = 0.0F;
    native_identity._21 = 0.0F;
    native_identity._22 = 1.0F;
    native_identity._31 = 0.0F;
    native_identity._32 = 0.0F;
    const float native_maximum = std::numeric_limits<float>::max();
    const D2D1_LAYER_PARAMETERS native_full_opacity_layer{
        {-native_maximum, -native_maximum, native_maximum, native_maximum},
        nullptr,
        D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
        native_identity,
        1.0F,
        native_layer_brush,
        D2D1_LAYER_OPTIONS_NONE};
    const D2D1_RECT_F native_layer_fill{0.0F, 0.0F, 20.0F, 20.0F};
    native_target->SetTransform(native_identity);
    native_target->BeginDraw();
    native_target->PushLayer(
        native_full_opacity_layer, native_target_layer);
    native_target->PushLayer(native_full_opacity_layer, nullptr);
    native_target->FillRectangle(native_layer_fill, native_layer_brush);
    native_target->PopLayer();
    native_target->PopLayer();
    if (FAILED(native_target->EndDraw())) {
        return 498;
    }
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
    ID2D1Bitmap* native_wic_locked_bitmap = nullptr;
    if (FAILED(native_target->CreateSharedBitmap(
            __uuidof(IWICBitmapLock),
            reinterpret_cast<IWICBitmapLock*>(wic_lock.get()),
            nullptr,
            &native_wic_locked_bitmap)) ||
        native_wic_locked_bitmap == nullptr ||
        native_wic_locked_bitmap->GetPixelSize().width != 2U ||
        native_wic_locked_bitmap->GetPixelSize().height != 2U ||
        native_wic_locked_bitmap->GetPixelFormat().format !=
            DXGI_FORMAT_B8G8R8A8_UNORM ||
        native_wic_locked_bitmap->GetPixelFormat().alphaMode !=
            D2D1_ALPHA_MODE_PREMULTIPLIED) {
        if (native_wic_locked_bitmap != nullptr) {
            native_wic_locked_bitmap->Release();
        }
        return 489;
    }
    native_wic_locked_bitmap->Release();
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
    native_compatible_target->BeginDraw();
    const D2D1_RECT_F native_persistent_rectangle{1.0F, 1.0F, 4.0F, 4.0F};
    native_compatible_target->FillRectangle(&native_persistent_rectangle, native_target_brush);
    if (FAILED(native_compatible_target->EndDraw())) {
        native_compatible_target->Release();
        native_target_brush->Release();
        std::fprintf(stderr, "portable Windows compatible bitmap persistent session failed\n");
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
    native_target->DrawBitmap(native_shared_compatible_bitmap,
        &native_compatible_destination, 0.5F,
        D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, nullptr);
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
    native_target->SetAntialiasMode(D2D1_ANTIALIAS_MODE_ALIASED);
    native_target->FillMesh(native_target_mesh, native_target_brush);
    native_target->SetAntialiasMode(D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
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

    const auto create_open_query_path = [](ID2D1Factory* path_factory,
                                            ID2D1PathGeometry** value) {
      if (path_factory == nullptr || value == nullptr) {
        return E_POINTER;
      }
      *value = nullptr;
      ID2D1PathGeometry* path_value = nullptr;
      ID2D1GeometrySink* sink_value = nullptr;
      HRESULT status = path_factory->CreatePathGeometry(&path_value);
      if (SUCCEEDED(status)) {
        status = path_value->Open(&sink_value);
      }
      if (SUCCEEDED(status)) {
        sink_value->BeginFigure(
            D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_HOLLOW);
        constexpr std::array<D2D1_POINT_2F, 2U> points{{
            {4.0F, 0.0F},
            {4.0F, 4.0F},
        }};
        sink_value->AddLines(points.data(), static_cast<UINT32>(points.size()));
        sink_value->EndFigure(D2D1_FIGURE_END_OPEN);
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
    ID2D1PathGeometry* portable_open_query_path = nullptr;
    ID2D1PathGeometry* system_open_query_path = nullptr;
    if (FAILED(create_open_query_path(
            native_factory, &portable_open_query_path)) ||
        FAILED(create_open_query_path(
            system_factory, &system_open_query_path)) ||
        portable_open_query_path == nullptr ||
        system_open_query_path == nullptr) {
      if (portable_open_query_path != nullptr) {
        portable_open_query_path->Release();
      }
      if (system_open_query_path != nullptr) {
        system_open_query_path->Release();
      }
      system_factory->Release();
      return 385;
    }
    const auto create_flagged_query_path = [](
        ID2D1Factory* path_factory,
        bool unstroked,
        ID2D1PathGeometry** value) {
      if (path_factory == nullptr || value == nullptr) {
        return E_POINTER;
      }
      *value = nullptr;
      ID2D1PathGeometry* path_value = nullptr;
      ID2D1GeometrySink* sink_value = nullptr;
      HRESULT status = path_factory->CreatePathGeometry(&path_value);
      if (SUCCEEDED(status)) {
        status = path_value->Open(&sink_value);
      }
      if (SUCCEEDED(status)) {
        sink_value->BeginFigure(
            D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_HOLLOW);
        sink_value->AddLine(D2D1_POINT_2F{4.0F, 0.0F});
        sink_value->SetSegmentFlags(
            unstroked ? D2D1_PATH_SEGMENT_FORCE_UNSTROKED
                      : D2D1_PATH_SEGMENT_FORCE_ROUND_LINE_JOIN);
        sink_value->AddLine(D2D1_POINT_2F{4.0F, 4.0F});
        if (unstroked) {
          sink_value->SetSegmentFlags(D2D1_PATH_SEGMENT_NONE);
          sink_value->AddLine(D2D1_POINT_2F{8.0F, 4.0F});
        }
        sink_value->EndFigure(D2D1_FIGURE_END_OPEN);
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
    ID2D1PathGeometry* raw_portable_round_segment_path = nullptr;
    ID2D1PathGeometry* raw_system_round_segment_path = nullptr;
    ID2D1PathGeometry* raw_portable_unstroked_segment_path = nullptr;
    ID2D1PathGeometry* raw_system_unstroked_segment_path = nullptr;
    if (FAILED(create_flagged_query_path(
            native_factory, false, &raw_portable_round_segment_path)) ||
        FAILED(create_flagged_query_path(
            system_factory, false, &raw_system_round_segment_path)) ||
        FAILED(create_flagged_query_path(
            native_factory, true, &raw_portable_unstroked_segment_path)) ||
        FAILED(create_flagged_query_path(
            system_factory, true, &raw_system_unstroked_segment_path)) ||
        raw_portable_round_segment_path == nullptr ||
        raw_system_round_segment_path == nullptr ||
        raw_portable_unstroked_segment_path == nullptr ||
        raw_system_unstroked_segment_path == nullptr) {
      if (raw_portable_round_segment_path != nullptr) {
        raw_portable_round_segment_path->Release();
      }
      if (raw_system_round_segment_path != nullptr) {
        raw_system_round_segment_path->Release();
      }
      if (raw_portable_unstroked_segment_path != nullptr) {
        raw_portable_unstroked_segment_path->Release();
      }
      if (raw_system_unstroked_segment_path != nullptr) {
        raw_system_unstroked_segment_path->Release();
      }
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 415;
    }
    com::pointer<ID2D1PathGeometry> portable_round_segment_path;
    portable_round_segment_path.attach(raw_portable_round_segment_path);
    com::pointer<ID2D1PathGeometry> system_round_segment_path;
    system_round_segment_path.attach(raw_system_round_segment_path);
    com::pointer<ID2D1PathGeometry> portable_unstroked_segment_path;
    portable_unstroked_segment_path.attach(
        raw_portable_unstroked_segment_path);
    com::pointer<ID2D1PathGeometry> system_unstroked_segment_path;
    system_unstroked_segment_path.attach(raw_system_unstroked_segment_path);
    auto* raw_portable_round_segment_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_round_segment_widen;
    portable_round_segment_widen.attach(raw_portable_round_segment_widen);
    auto* raw_system_round_segment_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> system_round_segment_widen;
    system_round_segment_widen.attach(raw_system_round_segment_widen);
    auto* raw_portable_unstroked_segment_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_unstroked_segment_widen;
    portable_unstroked_segment_widen.attach(
        raw_portable_unstroked_segment_widen);
    auto* raw_system_unstroked_segment_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        system_unstroked_segment_widen;
    system_unstroked_segment_widen.attach(raw_system_unstroked_segment_widen);
    D2D1_RECT_F portable_unstroked_bounds{};
    D2D1_RECT_F system_unstroked_bounds{};
    bool flagged_segment_matches =
        SUCCEEDED(portable_round_segment_path->Widen(
            2.0F, nullptr, nullptr, 0.01F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                portable_round_segment_widen.get()))) &&
        SUCCEEDED(system_round_segment_path->Widen(
            2.0F, nullptr, nullptr, 0.01F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_round_segment_widen.get()))) &&
        SUCCEEDED(portable_unstroked_segment_path->Widen(
            2.0F, nullptr, nullptr, 0.01F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                portable_unstroked_segment_widen.get()))) &&
        SUCCEEDED(system_unstroked_segment_path->Widen(
            2.0F, nullptr, nullptr, 0.01F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_unstroked_segment_widen.get()))) &&
        SUCCEEDED(portable_unstroked_segment_path->GetWidenedBounds(
            2.0F, nullptr, nullptr, 0.01F,
            &portable_unstroked_bounds)) &&
        SUCCEEDED(system_unstroked_segment_path->GetWidenedBounds(
            2.0F, nullptr, nullptr, 0.01F, &system_unstroked_bounds)) &&
        approximately_equal(
            portable_unstroked_bounds.left, system_unstroked_bounds.left) &&
        approximately_equal(
            portable_unstroked_bounds.top, system_unstroked_bounds.top) &&
        approximately_equal(
            portable_unstroked_bounds.right,
            system_unstroked_bounds.right) &&
        approximately_equal(
            portable_unstroked_bounds.bottom,
            system_unstroked_bounds.bottom);
    for (std::uint32_t y_index = 0U;
         flagged_segment_matches && y_index < 30U; ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 42U; ++x_index) {
        const D2D1_POINT_2F point{
            -1.13F + static_cast<float>(x_index) * 0.247F,
            -1.17F + static_cast<float>(y_index) * 0.249F};
        BOOL portable_round_contains = FALSE;
        BOOL system_round_contains = FALSE;
        BOOL portable_unstroked_contains = FALSE;
        BOOL system_unstroked_contains = FALSE;
        flagged_segment_matches =
            SUCCEEDED(portable_round_segment_path->StrokeContainsPoint(
                point, 2.0F, nullptr, nullptr, 0.01F,
                &portable_round_contains)) &&
            SUCCEEDED(system_round_segment_path->StrokeContainsPoint(
                point, 2.0F, nullptr, nullptr, 0.01F,
                &system_round_contains)) &&
            SUCCEEDED(portable_unstroked_segment_path->StrokeContainsPoint(
                point, 2.0F, nullptr, nullptr, 0.01F,
                &portable_unstroked_contains)) &&
            SUCCEEDED(system_unstroked_segment_path->StrokeContainsPoint(
                point, 2.0F, nullptr, nullptr, 0.01F,
                &system_unstroked_contains)) &&
            portable_round_contains == system_round_contains &&
            portable_unstroked_contains == system_unstroked_contains &&
            captured_fill_contains(
                *raw_portable_round_segment_widen,
                {point.x, point.y}) == (system_round_contains != FALSE) &&
            captured_fill_contains(
                *raw_portable_unstroked_segment_widen,
                {point.x, point.y}) ==
                (system_unstroked_contains != FALSE);
        if (!flagged_segment_matches) {
          const bool system_round_widen_contains = captured_fill_contains(
              *raw_system_round_segment_widen, {point.x, point.y});
          const bool system_unstroked_widen_contains = captured_fill_contains(
              *raw_system_unstroked_segment_widen, {point.x, point.y});
          if (system_round_widen_contains !=
                  (system_round_contains != FALSE) ||
              system_unstroked_widen_contains !=
                  (system_unstroked_contains != FALSE)) {
            flagged_segment_matches = true;
            continue;
          }
          std::fprintf(
              stderr,
              "flagged segment mismatch point=%g,%g round=%d/%d/%d "
              "unstroked=%d/%d/%d\n",
              point.x,
              point.y,
              portable_round_contains != FALSE ? 1 : 0,
              system_round_contains != FALSE ? 1 : 0,
              captured_fill_contains(
                  *raw_portable_round_segment_widen,
                  {point.x, point.y}) ? 1 : 0,
              portable_unstroked_contains != FALSE ? 1 : 0,
              system_unstroked_contains != FALSE ? 1 : 0,
              captured_fill_contains(
                  *raw_portable_unstroked_segment_widen,
                  {point.x, point.y}) ? 1 : 0);
          break;
        }
      }
    }
    if (!flagged_segment_matches) {
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 416;
    }
    const auto create_open_curve_path = [](ID2D1Factory* path_factory,
                                            ID2D1PathGeometry** value) {
      if (path_factory == nullptr || value == nullptr) {
        return E_POINTER;
      }
      *value = nullptr;
      ID2D1PathGeometry* path_value = nullptr;
      ID2D1GeometrySink* sink_value = nullptr;
      HRESULT status = path_factory->CreatePathGeometry(&path_value);
      if (SUCCEEDED(status)) {
        status = path_value->Open(&sink_value);
      }
      if (SUCCEEDED(status)) {
        sink_value->BeginFigure(
            D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_HOLLOW);
        const D2D1_BEZIER_SEGMENT cubic{
            {2.0F, 0.0F}, {4.0F, 2.0F}, {6.0F, 2.0F}};
        sink_value->AddBezier(&cubic);
        const D2D1_QUADRATIC_BEZIER_SEGMENT quadratic{
            {8.0F, 2.0F}, {10.0F, 0.0F}};
        sink_value->AddQuadraticBezier(quadratic);
        sink_value->EndFigure(D2D1_FIGURE_END_OPEN);
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
    ID2D1PathGeometry* raw_portable_open_curve_path = nullptr;
    ID2D1PathGeometry* raw_system_open_curve_path = nullptr;
    if (FAILED(create_open_curve_path(
            native_factory, &raw_portable_open_curve_path)) ||
        FAILED(create_open_curve_path(
            system_factory, &raw_system_open_curve_path)) ||
        raw_portable_open_curve_path == nullptr ||
        raw_system_open_curve_path == nullptr) {
      if (raw_portable_open_curve_path != nullptr) {
        raw_portable_open_curve_path->Release();
      }
      if (raw_system_open_curve_path != nullptr) {
        raw_system_open_curve_path->Release();
      }
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 407;
    }
    com::pointer<ID2D1PathGeometry> portable_open_curve_path;
    portable_open_curve_path.attach(raw_portable_open_curve_path);
    com::pointer<ID2D1PathGeometry> system_open_curve_path;
    system_open_curve_path.attach(raw_system_open_curve_path);
    auto* raw_portable_open_curve_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_open_curve_widen;
    portable_open_curve_widen.attach(raw_portable_open_curve_widen);
    auto* raw_system_open_curve_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> system_open_curve_widen;
    system_open_curve_widen.attach(raw_system_open_curve_widen);
    const HRESULT portable_open_curve_widen_status =
        portable_open_curve_path->Widen(
            1.0F,
            nullptr,
            nullptr,
            0.02F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                portable_open_curve_widen.get()));
    const HRESULT system_open_curve_widen_status =
        system_open_curve_path->Widen(
            1.0F,
            nullptr,
            nullptr,
            0.02F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_open_curve_widen.get()));
    constexpr std::array<D2D1_POINT_2F, 6U> open_curve_probes{{
        {1.0F, 0.2F},
        {3.0F, 1.0F},
        {6.0F, 2.0F},
        {8.0F, 1.4F},
        {3.0F, -1.0F},
        {6.0F, 3.0F},
    }};
    bool open_curve_matches =
        SUCCEEDED(portable_open_curve_widen_status) &&
        SUCCEEDED(system_open_curve_widen_status);
    for (const D2D1_POINT_2F probe : open_curve_probes) {
      BOOL portable_contains = FALSE;
      BOOL system_contains = FALSE;
      const HRESULT portable_status =
          portable_open_curve_path->StrokeContainsPoint(
              probe, 1.0F, nullptr, nullptr, 0.02F, &portable_contains);
      const HRESULT system_status =
          system_open_curve_path->StrokeContainsPoint(
              probe, 1.0F, nullptr, nullptr, 0.02F, &system_contains);
      open_curve_matches = open_curve_matches &&
          SUCCEEDED(portable_status) && SUCCEEDED(system_status) &&
          portable_contains == system_contains &&
          captured_fill_contains(
              *raw_portable_open_curve_widen,
              {probe.x, probe.y}) == (portable_contains != FALSE);
    }
    if (!open_curve_matches) {
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 408;
    }
    const auto create_multi_query_path = [](ID2D1Factory* path_factory,
                                             ID2D1PathGeometry** value) {
      if (path_factory == nullptr || value == nullptr) {
        return E_POINTER;
      }
      *value = nullptr;
      ID2D1PathGeometry* path_value = nullptr;
      ID2D1GeometrySink* sink_value = nullptr;
      HRESULT status = path_factory->CreatePathGeometry(&path_value);
      if (SUCCEEDED(status)) {
        status = path_value->Open(&sink_value);
      }
      if (SUCCEEDED(status)) {
        sink_value->BeginFigure(
            D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_HOLLOW);
        constexpr std::array<D2D1_POINT_2F, 3U> closed_points{{
            {2.0F, 0.0F},
            {2.0F, 2.0F},
            {0.0F, 2.0F},
        }};
        sink_value->AddLines(
            closed_points.data(), static_cast<UINT32>(closed_points.size()));
        sink_value->EndFigure(D2D1_FIGURE_END_CLOSED);
        sink_value->BeginFigure(
            D2D1_POINT_2F{10.0F, 0.0F}, D2D1_FIGURE_BEGIN_HOLLOW);
        constexpr std::array<D2D1_POINT_2F, 2U> open_points{{
            {14.0F, 0.0F},
            {14.0F, 4.0F},
        }};
        sink_value->AddLines(
            open_points.data(), static_cast<UINT32>(open_points.size()));
        sink_value->EndFigure(D2D1_FIGURE_END_OPEN);
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
    ID2D1PathGeometry* portable_multi_query_path = nullptr;
    ID2D1PathGeometry* system_multi_query_path = nullptr;
    if (FAILED(create_multi_query_path(
            native_factory, &portable_multi_query_path)) ||
        FAILED(create_multi_query_path(
            system_factory, &system_multi_query_path)) ||
        portable_multi_query_path == nullptr ||
        system_multi_query_path == nullptr) {
      if (portable_multi_query_path != nullptr) {
        portable_multi_query_path->Release();
      }
      if (system_multi_query_path != nullptr) {
        system_multi_query_path->Release();
      }
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 394;
    }
    const auto create_multi_outline_path = [](ID2D1Factory* path_factory,
                                               std::uint32_t scenario,
                                               ID2D1PathGeometry** value) {
      if (path_factory == nullptr || value == nullptr) {
        return E_POINTER;
      }
      *value = nullptr;
      ID2D1PathGeometry* path_value = nullptr;
      ID2D1GeometrySink* sink_value = nullptr;
      HRESULT status = path_factory->CreatePathGeometry(&path_value);
      if (SUCCEEDED(status)) {
        status = path_value->Open(&sink_value);
      }
      if (SUCCEEDED(status)) {
        if (scenario == 8U) {
          sink_value->BeginFigure(
              D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_FILLED);
          constexpr std::array<D2D1_POINT_2F, 3U> self_points{{
              {4.0F, 4.0F},
              {0.0F, 4.0F},
              {4.0F, 0.0F},
          }};
          sink_value->AddLines(
              self_points.data(),
              static_cast<UINT32>(self_points.size()));
          sink_value->EndFigure(D2D1_FIGURE_END_CLOSED);
        } else if (scenario == 15U) {
          const auto add_rectangle = [sink_value](
              float left, float top, float right, float bottom) {
            sink_value->BeginFigure(
                D2D1_POINT_2F{left, top}, D2D1_FIGURE_BEGIN_FILLED);
            const std::array<D2D1_POINT_2F, 3U> points{{
                {right, top}, {right, bottom}, {left, bottom}}};
            sink_value->AddLines(
                points.data(), static_cast<UINT32>(points.size()));
            sink_value->EndFigure(D2D1_FIGURE_END_CLOSED);
          };
          add_rectangle(0.0F, 0.0F, 10.0F, 10.0F);
          add_rectangle(1.0F, 1.0F, 5.0F, 5.0F);
          add_rectangle(6.0F, 1.0F, 9.0F, 4.0F);
          add_rectangle(2.0F, 2.0F, 3.0F, 3.0F);
        } else if (scenario >= 11U && scenario <= 14U) {
          if (scenario != 11U) {
            sink_value->SetFillMode(D2D1_FILL_MODE_WINDING);
          }
          constexpr std::array<D2D1_POINT_2F, 5U> star_points{{
              {0.0F, -5.0F},
              {2.938926F, 4.045085F},
              {-4.755283F, -1.545085F},
              {4.755283F, -1.545085F},
              {-2.938926F, 4.045085F},
          }};
          sink_value->BeginFigure(
              star_points[0U], D2D1_FIGURE_BEGIN_FILLED);
          if (scenario == 14U) {
            constexpr std::array<D2D1_POINT_2F, 4U>
                reverse_star_points{{
                    {-2.938926F, 4.045085F},
                    {4.755283F, -1.545085F},
                    {-4.755283F, -1.545085F},
                    {2.938926F, 4.045085F},
                }};
            sink_value->AddLines(
                reverse_star_points.data(),
                static_cast<UINT32>(reverse_star_points.size()));
          } else {
            sink_value->AddLines(
                star_points.data() + 1U,
                static_cast<UINT32>(star_points.size() - 1U));
          }
          sink_value->EndFigure(D2D1_FIGURE_END_CLOSED);
          if (scenario == 13U || scenario == 14U) {
            sink_value->BeginFigure(
                D2D1_POINT_2F{-0.25F, -0.25F},
                D2D1_FIGURE_BEGIN_FILLED);
            constexpr std::array<D2D1_POINT_2F, 3U>
                negative_subtraction{{
                    {-0.25F, 0.25F},
                    {0.25F, 0.25F},
                    {0.25F, -0.25F},
                }};
            constexpr std::array<D2D1_POINT_2F, 3U>
                positive_subtraction{{
                    {0.25F, -0.25F},
                    {0.25F, 0.25F},
                    {-0.25F, 0.25F},
                }};
            const auto& subtraction_points = scenario == 13U
                ? negative_subtraction
                : positive_subtraction;
            sink_value->AddLines(
                subtraction_points.data(),
                static_cast<UINT32>(subtraction_points.size()));
            sink_value->EndFigure(D2D1_FIGURE_END_CLOSED);
          }
        } else {
        if (scenario == 2U || scenario == 5U || scenario == 10U) {
          sink_value->SetFillMode(D2D1_FILL_MODE_WINDING);
        }
        const bool triple = scenario == 9U || scenario == 10U;
        const bool overlap =
            scenario == 4U || scenario == 5U || triple;
        const bool t_junction = scenario == 7U;
        const float first_right = t_junction
            ? 4.0F
            : (overlap ? 3.0F : 2.0F);
        const float first_bottom = first_right;
        sink_value->BeginFigure(
            D2D1_POINT_2F{0.0F, 0.0F}, D2D1_FIGURE_BEGIN_FILLED);
        const std::array<D2D1_POINT_2F, 3U> first_points{{
            {first_right, 0.0F},
            {first_right, first_bottom},
            {0.0F, first_bottom},
        }};
        sink_value->AddLines(
            first_points.data(), static_cast<UINT32>(first_points.size()));
        sink_value->EndFigure(D2D1_FIGURE_END_CLOSED);
        const bool nested = scenario == 1U || scenario == 2U;
        const bool adjacent = scenario == 3U;
        const bool corner = scenario == 6U;
        float left = 10.0F;
        float top = 0.0F;
        float right = 12.0F;
        float bottom = 2.0F;
        if (nested) {
          left = 0.5F;
          top = 0.5F;
          right = 1.5F;
          bottom = 1.5F;
        } else if (adjacent) {
          left = 2.0F;
          right = 4.0F;
        } else if (corner) {
          left = 2.0F;
          top = 2.0F;
          right = 4.0F;
          bottom = 4.0F;
        } else if (overlap) {
          left = 2.0F;
          top = 1.0F;
          right = 5.0F;
          bottom = 4.0F;
        } else if (t_junction) {
          left = 4.0F;
          top = 2.0F;
          right = 6.0F;
        }
        sink_value->BeginFigure(
            D2D1_POINT_2F{left, top}, D2D1_FIGURE_BEGIN_FILLED);
        if (t_junction) {
          const std::array<D2D1_POINT_2F, 2U> second_points{{
              {right, 1.0F}, {right, 3.0F}}};
          sink_value->AddLines(
              second_points.data(),
              static_cast<UINT32>(second_points.size()));
        } else {
          const std::array<D2D1_POINT_2F, 3U> second_points =
              scenario == 5U || scenario == 10U
              ? std::array<D2D1_POINT_2F, 3U>{{
                    {right, top}, {right, bottom}, {left, bottom}}}
              : std::array<D2D1_POINT_2F, 3U>{{
                    {left, bottom}, {right, bottom}, {right, top}}};
          sink_value->AddLines(
              second_points.data(),
              static_cast<UINT32>(second_points.size()));
        }
        sink_value->EndFigure(D2D1_FIGURE_END_CLOSED);
        if (triple) {
          sink_value->BeginFigure(
              D2D1_POINT_2F{1.0F, 2.0F}, D2D1_FIGURE_BEGIN_FILLED);
          constexpr std::array<D2D1_POINT_2F, 3U> third_points{{
              {4.0F, 2.0F},
              {4.0F, 5.0F},
              {1.0F, 5.0F},
          }};
          sink_value->AddLines(
              third_points.data(),
              static_cast<UINT32>(third_points.size()));
          sink_value->EndFigure(D2D1_FIGURE_END_CLOSED);
        }
        }
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
    ID2D1PathGeometry* portable_multi_outline_path = nullptr;
    ID2D1PathGeometry* system_multi_outline_path = nullptr;
    if (FAILED(create_multi_outline_path(
            native_factory, 0U, &portable_multi_outline_path)) ||
        FAILED(create_multi_outline_path(
            system_factory, 0U, &system_multi_outline_path)) ||
        portable_multi_outline_path == nullptr ||
        system_multi_outline_path == nullptr) {
      if (portable_multi_outline_path != nullptr) {
        portable_multi_outline_path->Release();
      }
      if (system_multi_outline_path != nullptr) {
        system_multi_outline_path->Release();
      }
      portable_multi_query_path->Release();
      system_multi_query_path->Release();
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 421;
    }
    auto* raw_portable_multi_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_multi_outline_sink;
    portable_multi_outline_sink.attach(raw_portable_multi_outline_sink);
    auto* raw_system_multi_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> system_multi_outline_sink;
    system_multi_outline_sink.attach(raw_system_multi_outline_sink);
    const HRESULT portable_multi_outline_status =
        portable_multi_outline_path->Outline(
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                portable_multi_outline_sink.get()));
    const HRESULT system_multi_outline_status =
        system_multi_outline_path->Outline(
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_multi_outline_sink.get()));
    bool multi_outline_matches =
        SUCCEEDED(portable_multi_outline_status) &&
        SUCCEEDED(system_multi_outline_status) &&
        raw_portable_multi_outline_sink->fill_mode ==
            raw_system_multi_outline_sink->fill_mode &&
        raw_portable_multi_outline_sink->segment_flags ==
            raw_system_multi_outline_sink->segment_flags &&
        raw_portable_multi_outline_sink->set_fill_mode_count ==
            raw_system_multi_outline_sink->set_fill_mode_count &&
        raw_portable_multi_outline_sink->set_segment_flags_count ==
            raw_system_multi_outline_sink->set_segment_flags_count &&
        raw_portable_multi_outline_sink->begin_count ==
            raw_system_multi_outline_sink->begin_count &&
        raw_portable_multi_outline_sink->end_count ==
            raw_system_multi_outline_sink->end_count &&
        raw_portable_multi_outline_sink->line_count ==
            raw_system_multi_outline_sink->line_count;
    for (std::uint32_t y_index = 0U;
         multi_outline_matches && y_index < 8U; ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 28U; ++x_index) {
        const compat::point_2f point{
            -0.25F + static_cast<float>(x_index) * 0.47F,
            -0.25F + static_cast<float>(y_index) * 0.37F};
        if (captured_fill_contains(
                *raw_portable_multi_outline_sink, point) !=
            captured_fill_contains(*raw_system_multi_outline_sink, point)) {
          multi_outline_matches = false;
          break;
        }
      }
    }
    portable_multi_outline_path->Release();
    system_multi_outline_path->Release();
    if (!multi_outline_matches) {
      std::fprintf(
          stderr,
          "multi outline status=%ld/%ld fill=%u/%u flags=%u/%u "
          "callbacks=%u/%u,%u/%u geometry=%u/%u,%u/%u,%u/%u\n",
          static_cast<long>(portable_multi_outline_status),
          static_cast<long>(system_multi_outline_status),
          static_cast<unsigned>(raw_portable_multi_outline_sink->fill_mode),
          static_cast<unsigned>(raw_system_multi_outline_sink->fill_mode),
          static_cast<unsigned>(
              raw_portable_multi_outline_sink->segment_flags),
          static_cast<unsigned>(raw_system_multi_outline_sink->segment_flags),
          raw_portable_multi_outline_sink->set_fill_mode_count,
          raw_system_multi_outline_sink->set_fill_mode_count,
          raw_portable_multi_outline_sink->set_segment_flags_count,
          raw_system_multi_outline_sink->set_segment_flags_count,
          raw_portable_multi_outline_sink->begin_count,
          raw_system_multi_outline_sink->begin_count,
          raw_portable_multi_outline_sink->end_count,
          raw_system_multi_outline_sink->end_count,
          raw_portable_multi_outline_sink->line_count,
          raw_system_multi_outline_sink->line_count);
      portable_multi_query_path->Release();
      system_multi_query_path->Release();
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 422;
    }
    ID2D1PathGeometry* portable_nested_outline_path = nullptr;
    ID2D1PathGeometry* system_nested_outline_path = nullptr;
    if (FAILED(create_multi_outline_path(
            native_factory, 1U, &portable_nested_outline_path)) ||
        FAILED(create_multi_outline_path(
            system_factory, 1U, &system_nested_outline_path)) ||
        portable_nested_outline_path == nullptr ||
        system_nested_outline_path == nullptr) {
      if (portable_nested_outline_path != nullptr) {
        portable_nested_outline_path->Release();
      }
      if (system_nested_outline_path != nullptr) {
        system_nested_outline_path->Release();
      }
      portable_multi_query_path->Release();
      system_multi_query_path->Release();
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 426;
    }
    auto* raw_portable_nested_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_nested_outline_sink;
    portable_nested_outline_sink.attach(raw_portable_nested_outline_sink);
    auto* raw_system_nested_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> system_nested_outline_sink;
    system_nested_outline_sink.attach(raw_system_nested_outline_sink);
    const HRESULT portable_nested_outline_status =
        portable_nested_outline_path->Outline(
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                portable_nested_outline_sink.get()));
    const HRESULT system_nested_outline_status =
        system_nested_outline_path->Outline(
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_nested_outline_sink.get()));
    bool nested_outline_matches =
        SUCCEEDED(portable_nested_outline_status) &&
        SUCCEEDED(system_nested_outline_status) &&
        raw_portable_nested_outline_sink->fill_mode ==
            raw_system_nested_outline_sink->fill_mode &&
        raw_portable_nested_outline_sink->segment_flags ==
            raw_system_nested_outline_sink->segment_flags &&
        raw_portable_nested_outline_sink->set_fill_mode_count ==
            raw_system_nested_outline_sink->set_fill_mode_count &&
        raw_portable_nested_outline_sink->set_segment_flags_count ==
            raw_system_nested_outline_sink->set_segment_flags_count &&
        raw_portable_nested_outline_sink->begin_count ==
            raw_system_nested_outline_sink->begin_count &&
        raw_portable_nested_outline_sink->end_count ==
            raw_system_nested_outline_sink->end_count &&
        raw_portable_nested_outline_sink->line_count ==
            raw_system_nested_outline_sink->line_count;
    for (std::uint32_t y_index = 0U;
         nested_outline_matches && y_index < 10U; ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 10U; ++x_index) {
        const compat::point_2f point{
            -0.25F + static_cast<float>(x_index) * 0.28F,
            -0.25F + static_cast<float>(y_index) * 0.28F};
        if (captured_fill_contains(
                *raw_portable_nested_outline_sink, point) !=
            captured_fill_contains(*raw_system_nested_outline_sink, point)) {
          nested_outline_matches = false;
          break;
        }
      }
    }
    auto* raw_portable_nested_triangles = new triangle_sink();
    com::pointer<compat::tessellation_sink> portable_nested_triangles;
    portable_nested_triangles.attach(raw_portable_nested_triangles);
    auto* raw_system_nested_triangles = new triangle_sink();
    com::pointer<compat::tessellation_sink> system_nested_triangles;
    system_nested_triangles.attach(raw_system_nested_triangles);
    const HRESULT portable_nested_tessellation_status =
        portable_nested_outline_path->Tessellate(
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            reinterpret_cast<ID2D1TessellationSink*>(
                portable_nested_triangles.get()));
    const HRESULT system_nested_tessellation_status =
        system_nested_outline_path->Tessellate(
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            reinterpret_cast<ID2D1TessellationSink*>(
                system_nested_triangles.get()));
    const auto tessellated_area = [](const triangle_sink& triangles) {
      double area = 0.0;
      for (std::uint32_t index = 0U;
           index < triangles.captured_count; ++index) {
        const compat::triangle& value = triangles.captured[index];
        area += std::abs(
            (static_cast<double>(value.point2.x) - value.point1.x) *
                (static_cast<double>(value.point3.y) - value.point1.y) -
            (static_cast<double>(value.point2.y) - value.point1.y) *
                (static_cast<double>(value.point3.x) - value.point1.x)) *
            0.5;
      }
      return area;
    };
    const auto tessellation_contains = [](const triangle_sink& triangles,
                                           compat::point_2f point) {
      for (std::uint32_t index = 0U;
           index < triangles.captured_count; ++index) {
        const compat::triangle& value = triangles.captured[index];
        const auto cross = [point](compat::point_2f first,
                                   compat::point_2f second) {
          return (static_cast<double>(second.x) - first.x) *
                  (static_cast<double>(point.y) - first.y) -
              (static_cast<double>(second.y) - first.y) *
                  (static_cast<double>(point.x) - first.x);
        };
        const double first = cross(value.point1, value.point2);
        const double second = cross(value.point2, value.point3);
        const double third = cross(value.point3, value.point1);
        if ((first >= 0.0 && second >= 0.0 && third >= 0.0) ||
            (first <= 0.0 && second <= 0.0 && third <= 0.0)) {
          return true;
        }
      }
      return false;
    };
    nested_outline_matches = nested_outline_matches &&
        SUCCEEDED(portable_nested_tessellation_status) &&
        SUCCEEDED(system_nested_tessellation_status) &&
        raw_portable_nested_triangles->captured_count ==
            raw_portable_nested_triangles->count &&
        raw_system_nested_triangles->captured_count ==
            raw_system_nested_triangles->count &&
        approximately_equal(
            static_cast<float>(
                tessellated_area(*raw_portable_nested_triangles)),
            static_cast<float>(
                tessellated_area(*raw_system_nested_triangles))) &&
        approximately_equal(
            static_cast<float>(
                tessellated_area(*raw_portable_nested_triangles)),
            3.0F);
    for (std::uint32_t y_index = 0U;
         nested_outline_matches && y_index < 18U; ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 18U; ++x_index) {
        const compat::point_2f point{
            -0.17F + static_cast<float>(x_index) * 0.14F,
            -0.13F + static_cast<float>(y_index) * 0.13F};
        if (tessellation_contains(
                *raw_portable_nested_triangles, point) !=
            tessellation_contains(*raw_system_nested_triangles, point)) {
          nested_outline_matches = false;
          break;
        }
      }
    }
    portable_nested_outline_path->Release();
    system_nested_outline_path->Release();
    if (!nested_outline_matches) {
      std::fprintf(
          stderr,
          "nested outline status=%ld/%ld fill=%u/%u flags=%u/%u "
          "callbacks=%u/%u,%u/%u geometry=%u/%u,%u/%u,%u/%u "
          "tessellation=%ld/%ld triangles=%u/%u area=%g/%g\n",
          static_cast<long>(portable_nested_outline_status),
          static_cast<long>(system_nested_outline_status),
          static_cast<unsigned>(raw_portable_nested_outline_sink->fill_mode),
          static_cast<unsigned>(raw_system_nested_outline_sink->fill_mode),
          static_cast<unsigned>(
              raw_portable_nested_outline_sink->segment_flags),
          static_cast<unsigned>(
              raw_system_nested_outline_sink->segment_flags),
          raw_portable_nested_outline_sink->set_fill_mode_count,
          raw_system_nested_outline_sink->set_fill_mode_count,
          raw_portable_nested_outline_sink->set_segment_flags_count,
          raw_system_nested_outline_sink->set_segment_flags_count,
          raw_portable_nested_outline_sink->begin_count,
          raw_system_nested_outline_sink->begin_count,
          raw_portable_nested_outline_sink->end_count,
          raw_system_nested_outline_sink->end_count,
          raw_portable_nested_outline_sink->line_count,
          raw_system_nested_outline_sink->line_count,
          static_cast<long>(portable_nested_tessellation_status),
          static_cast<long>(system_nested_tessellation_status),
          raw_portable_nested_triangles->count,
          raw_system_nested_triangles->count,
          tessellated_area(*raw_portable_nested_triangles),
          tessellated_area(*raw_system_nested_triangles));
      portable_multi_query_path->Release();
      system_multi_query_path->Release();
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 427;
    }
    ID2D1PathGeometry* portable_multi_hole_path = nullptr;
    ID2D1PathGeometry* system_multi_hole_path = nullptr;
    if (FAILED(create_multi_outline_path(
            native_factory, 15U, &portable_multi_hole_path)) ||
        FAILED(create_multi_outline_path(
            system_factory, 15U, &system_multi_hole_path)) ||
        portable_multi_hole_path == nullptr ||
        system_multi_hole_path == nullptr) {
      if (portable_multi_hole_path != nullptr) {
        portable_multi_hole_path->Release();
      }
      if (system_multi_hole_path != nullptr) {
        system_multi_hole_path->Release();
      }
      portable_multi_query_path->Release();
      system_multi_query_path->Release();
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 461;
    }
    auto* raw_portable_multi_hole_triangles = new triangle_sink();
    com::pointer<compat::tessellation_sink> portable_multi_hole_triangles;
    portable_multi_hole_triangles.attach(
        raw_portable_multi_hole_triangles);
    auto* raw_system_multi_hole_triangles = new triangle_sink();
    com::pointer<compat::tessellation_sink> system_multi_hole_triangles;
    system_multi_hole_triangles.attach(raw_system_multi_hole_triangles);
    const HRESULT portable_multi_hole_status =
        portable_multi_hole_path->Tessellate(
            nullptr,
            0.01F,
            reinterpret_cast<ID2D1TessellationSink*>(
                portable_multi_hole_triangles.get()));
    const HRESULT system_multi_hole_status =
        system_multi_hole_path->Tessellate(
            nullptr,
            0.01F,
            reinterpret_cast<ID2D1TessellationSink*>(
                system_multi_hole_triangles.get()));
    bool multi_hole_tessellation_matches =
        SUCCEEDED(portable_multi_hole_status) &&
        SUCCEEDED(system_multi_hole_status) &&
        raw_portable_multi_hole_triangles->captured_count ==
            raw_portable_multi_hole_triangles->count &&
        raw_system_multi_hole_triangles->captured_count ==
            raw_system_multi_hole_triangles->count &&
        approximately_equal(
            static_cast<float>(
                tessellated_area(*raw_portable_multi_hole_triangles)),
            76.0F) &&
        approximately_equal(
            static_cast<float>(
                tessellated_area(*raw_portable_multi_hole_triangles)),
            static_cast<float>(
                tessellated_area(*raw_system_multi_hole_triangles)));
    for (std::uint32_t y_index = 0U;
         multi_hole_tessellation_matches && y_index < 43U; ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 43U; ++x_index) {
        const compat::point_2f point{
            -0.13F + static_cast<float>(x_index) * 0.25F,
            -0.17F + static_cast<float>(y_index) * 0.25F};
        if (tessellation_contains(
                *raw_portable_multi_hole_triangles, point) !=
            tessellation_contains(*raw_system_multi_hole_triangles, point)) {
          multi_hole_tessellation_matches = false;
          break;
        }
      }
    }
    portable_multi_hole_path->Release();
    system_multi_hole_path->Release();
    if (!multi_hole_tessellation_matches) {
      std::fprintf(
          stderr,
          "multi-hole tessellation status=%ld/%ld triangles=%u/%u "
          "area=%g/%g\n",
          static_cast<long>(portable_multi_hole_status),
          static_cast<long>(system_multi_hole_status),
          raw_portable_multi_hole_triangles->count,
          raw_system_multi_hole_triangles->count,
          tessellated_area(*raw_portable_multi_hole_triangles),
          tessellated_area(*raw_system_multi_hole_triangles));
      portable_multi_query_path->Release();
      system_multi_query_path->Release();
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 462;
    }
    ID2D1PathGeometry* portable_multi_boolean_path = nullptr;
    ID2D1PathGeometry* system_multi_boolean_path = nullptr;
    ID2D1PathGeometry* portable_nested_boolean_path = nullptr;
    ID2D1PathGeometry* system_nested_boolean_path = nullptr;
    if (FAILED(create_multi_outline_path(
            native_factory, 0U, &portable_multi_boolean_path)) ||
        FAILED(create_multi_outline_path(
            system_factory, 0U, &system_multi_boolean_path)) ||
        FAILED(create_multi_outline_path(
            native_factory, 1U, &portable_nested_boolean_path)) ||
        FAILED(create_multi_outline_path(
            system_factory, 1U, &system_nested_boolean_path)) ||
        portable_multi_boolean_path == nullptr ||
        system_multi_boolean_path == nullptr ||
        portable_nested_boolean_path == nullptr ||
        system_nested_boolean_path == nullptr) {
      if (portable_multi_boolean_path != nullptr) {
        portable_multi_boolean_path->Release();
      }
      if (system_multi_boolean_path != nullptr) {
        system_multi_boolean_path->Release();
      }
      if (portable_nested_boolean_path != nullptr) {
        portable_nested_boolean_path->Release();
      }
      if (system_nested_boolean_path != nullptr) {
        system_nested_boolean_path->Release();
      }
      portable_multi_query_path->Release();
      system_multi_query_path->Release();
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 447;
    }
    bool multi_boolean_matches = true;
    for (std::size_t mode_index = 0U;
         multi_boolean_matches && mode_index < combination_modes.size();
         ++mode_index) {
      auto* raw_portable_multi_boolean_sink = new simplified_sink();
      com::pointer<compat::simplified_geometry_sink>
          portable_multi_boolean_sink;
      portable_multi_boolean_sink.attach(raw_portable_multi_boolean_sink);
      auto* raw_system_multi_boolean_sink = new simplified_sink();
      com::pointer<compat::simplified_geometry_sink>
          system_multi_boolean_sink;
      system_multi_boolean_sink.attach(raw_system_multi_boolean_sink);
      const HRESULT portable_status =
          portable_multi_boolean_path->CombineWithGeometry(
              portable_nested_boolean_path,
              static_cast<D2D1_COMBINE_MODE>(mode_index),
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                  portable_multi_boolean_sink.get()));
      const HRESULT system_status =
          system_multi_boolean_path->CombineWithGeometry(
              system_nested_boolean_path,
              static_cast<D2D1_COMBINE_MODE>(mode_index),
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                  system_multi_boolean_sink.get()));
      multi_boolean_matches = SUCCEEDED(portable_status) &&
          SUCCEEDED(system_status) &&
          raw_portable_multi_boolean_sink->fill_mode ==
              raw_system_multi_boolean_sink->fill_mode;
      for (std::uint32_t y_index = 0U;
           multi_boolean_matches && y_index < 24U; ++y_index) {
        for (std::uint32_t x_index = 0U; x_index < 52U; ++x_index) {
          const compat::point_2f point{
              -0.23F + static_cast<float>(x_index) * 0.25F,
              -0.19F + static_cast<float>(y_index) * 0.17F};
          if (captured_fill_contains(
                  *raw_portable_multi_boolean_sink, point) !=
              captured_fill_contains(
                  *raw_system_multi_boolean_sink, point)) {
            multi_boolean_matches = false;
            break;
          }
        }
      }
    }
    const D2D1_RECT_F system_multi_relation_envelope{
        -1.0F, -1.0F, 13.0F, 3.0F};
    const D2D1_RECT_F system_nested_hole_interior{
        0.75F, 0.75F, 1.25F, 1.25F};
    ID2D1RectangleGeometry* portable_multi_relation_envelope = nullptr;
    ID2D1RectangleGeometry* system_multi_relation_envelope_geometry = nullptr;
    ID2D1RectangleGeometry* portable_nested_hole_interior = nullptr;
    ID2D1RectangleGeometry* system_nested_hole_interior_geometry = nullptr;
    if (FAILED(native_factory->CreateRectangleGeometry(
            &system_multi_relation_envelope,
            &portable_multi_relation_envelope)) ||
        FAILED(system_factory->CreateRectangleGeometry(
            &system_multi_relation_envelope,
            &system_multi_relation_envelope_geometry)) ||
        FAILED(native_factory->CreateRectangleGeometry(
            &system_nested_hole_interior,
            &portable_nested_hole_interior)) ||
        FAILED(system_factory->CreateRectangleGeometry(
            &system_nested_hole_interior,
            &system_nested_hole_interior_geometry)) ||
        portable_multi_relation_envelope == nullptr ||
        system_multi_relation_envelope_geometry == nullptr ||
        portable_nested_hole_interior == nullptr ||
        system_nested_hole_interior_geometry == nullptr) {
      multi_boolean_matches = false;
    } else {
      std::array<D2D1_GEOMETRY_RELATION, 4U> portable_multi_relations{};
      std::array<D2D1_GEOMETRY_RELATION, 4U> system_multi_relations{};
      const std::array<HRESULT, 4U> portable_relation_statuses{{
          portable_multi_boolean_path->CompareWithGeometry(
              portable_multi_relation_envelope,
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &portable_multi_relations[0U]),
          portable_nested_boolean_path->CompareWithGeometry(
              portable_nested_hole_interior,
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &portable_multi_relations[1U]),
          portable_multi_boolean_path->CompareWithGeometry(
              portable_nested_boolean_path,
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &portable_multi_relations[2U]),
          portable_multi_boolean_path->CompareWithGeometry(
              portable_multi_boolean_path,
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &portable_multi_relations[3U]),
      }};
      const std::array<HRESULT, 4U> system_relation_statuses{{
          system_multi_boolean_path->CompareWithGeometry(
              system_multi_relation_envelope_geometry,
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &system_multi_relations[0U]),
          system_nested_boolean_path->CompareWithGeometry(
              system_nested_hole_interior_geometry,
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &system_multi_relations[1U]),
          system_multi_boolean_path->CompareWithGeometry(
              system_nested_boolean_path,
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &system_multi_relations[2U]),
          system_multi_boolean_path->CompareWithGeometry(
              system_multi_boolean_path,
              nullptr,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              &system_multi_relations[3U]),
      }};
      for (std::size_t relation_index = 0U;
           relation_index < portable_multi_relations.size();
           ++relation_index) {
        if (FAILED(portable_relation_statuses[relation_index]) ||
            FAILED(system_relation_statuses[relation_index]) ||
            portable_multi_relations[relation_index] !=
                system_multi_relations[relation_index]) {
          std::fprintf(
              stderr,
              "multi relation %zu status=%ld/%ld relation=%u/%u\n",
              relation_index,
              static_cast<long>(portable_relation_statuses[relation_index]),
              static_cast<long>(system_relation_statuses[relation_index]),
              static_cast<unsigned>(portable_multi_relations[relation_index]),
              static_cast<unsigned>(system_multi_relations[relation_index]));
          multi_boolean_matches = false;
          break;
        }
      }
    }
    if (portable_multi_relation_envelope != nullptr) {
      portable_multi_relation_envelope->Release();
    }
    if (system_multi_relation_envelope_geometry != nullptr) {
      system_multi_relation_envelope_geometry->Release();
    }
    if (portable_nested_hole_interior != nullptr) {
      portable_nested_hole_interior->Release();
    }
    if (system_nested_hole_interior_geometry != nullptr) {
      system_nested_hole_interior_geometry->Release();
    }
    portable_multi_boolean_path->Release();
    system_multi_boolean_path->Release();
    portable_nested_boolean_path->Release();
    system_nested_boolean_path->Release();
    if (!multi_boolean_matches) {
      portable_multi_query_path->Release();
      system_multi_query_path->Release();
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 448;
    }
    ID2D1PathGeometry* portable_winding_outline_path = nullptr;
    ID2D1PathGeometry* system_winding_outline_path = nullptr;
    if (FAILED(create_multi_outline_path(
            native_factory, 2U, &portable_winding_outline_path)) ||
        FAILED(create_multi_outline_path(
            system_factory, 2U, &system_winding_outline_path)) ||
        portable_winding_outline_path == nullptr ||
        system_winding_outline_path == nullptr) {
      if (portable_winding_outline_path != nullptr) {
        portable_winding_outline_path->Release();
      }
      if (system_winding_outline_path != nullptr) {
        system_winding_outline_path->Release();
      }
      portable_multi_query_path->Release();
      system_multi_query_path->Release();
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 431;
    }
    auto* raw_portable_winding_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_winding_outline_sink;
    portable_winding_outline_sink.attach(raw_portable_winding_outline_sink);
    auto* raw_system_winding_outline_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink> system_winding_outline_sink;
    system_winding_outline_sink.attach(raw_system_winding_outline_sink);
    const HRESULT portable_winding_outline_status =
        portable_winding_outline_path->Outline(
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                portable_winding_outline_sink.get()));
    const HRESULT system_winding_outline_status =
        system_winding_outline_path->Outline(
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_winding_outline_sink.get()));
    bool winding_outline_matches =
        SUCCEEDED(portable_winding_outline_status) &&
        SUCCEEDED(system_winding_outline_status) &&
        raw_portable_winding_outline_sink->fill_mode ==
            raw_system_winding_outline_sink->fill_mode &&
        raw_portable_winding_outline_sink->segment_flags ==
            raw_system_winding_outline_sink->segment_flags &&
        raw_portable_winding_outline_sink->set_fill_mode_count ==
            raw_system_winding_outline_sink->set_fill_mode_count &&
        raw_portable_winding_outline_sink->set_segment_flags_count ==
            raw_system_winding_outline_sink->set_segment_flags_count &&
        raw_portable_winding_outline_sink->begin_count ==
            raw_system_winding_outline_sink->begin_count &&
        raw_portable_winding_outline_sink->end_count ==
            raw_system_winding_outline_sink->end_count &&
        raw_portable_winding_outline_sink->line_count ==
            raw_system_winding_outline_sink->line_count;
    for (std::uint32_t y_index = 0U;
         winding_outline_matches && y_index < 10U; ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 10U; ++x_index) {
        const compat::point_2f point{
            -0.25F + static_cast<float>(x_index) * 0.28F,
            -0.25F + static_cast<float>(y_index) * 0.28F};
        if (captured_fill_contains(
                *raw_portable_winding_outline_sink, point) !=
            captured_fill_contains(*raw_system_winding_outline_sink, point)) {
          winding_outline_matches = false;
          break;
        }
      }
    }
    portable_winding_outline_path->Release();
    system_winding_outline_path->Release();
    if (!winding_outline_matches) {
      std::fprintf(
          stderr,
          "winding outline status=%ld/%ld fill=%u/%u flags=%u/%u "
          "callbacks=%u/%u,%u/%u geometry=%u/%u,%u/%u,%u/%u\n",
          static_cast<long>(portable_winding_outline_status),
          static_cast<long>(system_winding_outline_status),
          static_cast<unsigned>(
              raw_portable_winding_outline_sink->fill_mode),
          static_cast<unsigned>(raw_system_winding_outline_sink->fill_mode),
          static_cast<unsigned>(
              raw_portable_winding_outline_sink->segment_flags),
          static_cast<unsigned>(
              raw_system_winding_outline_sink->segment_flags),
          raw_portable_winding_outline_sink->set_fill_mode_count,
          raw_system_winding_outline_sink->set_fill_mode_count,
          raw_portable_winding_outline_sink->set_segment_flags_count,
          raw_system_winding_outline_sink->set_segment_flags_count,
          raw_portable_winding_outline_sink->begin_count,
          raw_system_winding_outline_sink->begin_count,
          raw_portable_winding_outline_sink->end_count,
          raw_system_winding_outline_sink->end_count,
          raw_portable_winding_outline_sink->line_count,
          raw_system_winding_outline_sink->line_count);
      portable_multi_query_path->Release();
      system_multi_query_path->Release();
      portable_open_query_path->Release();
      system_open_query_path->Release();
      system_factory->Release();
      return 432;
    }
    for (std::uint32_t scenario = 3U; scenario <= 15U; ++scenario) {
      ID2D1PathGeometry* portable_normalized_outline_path = nullptr;
      ID2D1PathGeometry* system_normalized_outline_path = nullptr;
      if (FAILED(create_multi_outline_path(
              native_factory,
              scenario,
              &portable_normalized_outline_path)) ||
          FAILED(create_multi_outline_path(
              system_factory,
              scenario,
              &system_normalized_outline_path)) ||
          portable_normalized_outline_path == nullptr ||
          system_normalized_outline_path == nullptr) {
        if (portable_normalized_outline_path != nullptr) {
          portable_normalized_outline_path->Release();
        }
        if (system_normalized_outline_path != nullptr) {
          system_normalized_outline_path->Release();
        }
        portable_multi_query_path->Release();
        system_multi_query_path->Release();
        portable_open_query_path->Release();
        system_open_query_path->Release();
        system_factory->Release();
        return 433;
      }
      auto* raw_portable_normalized_outline_sink = new simplified_sink();
      com::pointer<compat::simplified_geometry_sink>
          portable_normalized_outline_sink;
      portable_normalized_outline_sink.attach(
          raw_portable_normalized_outline_sink);
      auto* raw_system_normalized_outline_sink = new simplified_sink();
      com::pointer<compat::simplified_geometry_sink>
          system_normalized_outline_sink;
      system_normalized_outline_sink.attach(
          raw_system_normalized_outline_sink);
      const HRESULT portable_normalized_outline_status =
          portable_normalized_outline_path->Outline(
              nullptr,
              0.01F,
              reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                  portable_normalized_outline_sink.get()));
      const HRESULT system_normalized_outline_status =
          system_normalized_outline_path->Outline(
              nullptr,
              0.01F,
              reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                  system_normalized_outline_sink.get()));
      float portable_normalized_area = 0.0F;
      float system_normalized_area = 0.0F;
      const HRESULT portable_normalized_area_status =
          portable_normalized_outline_path->ComputeArea(
              nullptr, 0.01F, &portable_normalized_area);
      const HRESULT system_normalized_area_status =
          system_normalized_outline_path->ComputeArea(
              nullptr, 0.01F, &system_normalized_area);
      const float expected_normalized_area = scenario == 3U
          ? 8.0F
          : (scenario == 4U
              ? 14.0F
              : (scenario == 5U
                  ? 16.0F
                  : (scenario == 6U
                      ? 8.0F
                      : (scenario == 7U
                          ? 18.0F
                          : (scenario == 8U
                              ? 8.0F
                              : (scenario == 9U
                                  ? 15.0F
                                  : (scenario == 10U
                                      ? 20.0F
                                      : system_normalized_area)))))));
      bool normalized_outline_matches =
          SUCCEEDED(portable_normalized_outline_status) &&
          SUCCEEDED(system_normalized_outline_status) &&
          SUCCEEDED(portable_normalized_area_status) &&
          SUCCEEDED(system_normalized_area_status) &&
          approximately_equal(
              portable_normalized_area, system_normalized_area) &&
          approximately_equal(
              portable_normalized_area, expected_normalized_area) &&
          raw_portable_normalized_outline_sink->fill_mode ==
              raw_system_normalized_outline_sink->fill_mode &&
          raw_portable_normalized_outline_sink->segment_flags ==
              raw_system_normalized_outline_sink->segment_flags &&
          raw_portable_normalized_outline_sink->set_fill_mode_count ==
              raw_system_normalized_outline_sink->set_fill_mode_count &&
          raw_portable_normalized_outline_sink->set_segment_flags_count ==
              raw_system_normalized_outline_sink->set_segment_flags_count &&
          raw_portable_normalized_outline_sink->begin_count ==
              raw_system_normalized_outline_sink->begin_count &&
          raw_portable_normalized_outline_sink->end_count ==
              raw_system_normalized_outline_sink->end_count &&
          raw_portable_normalized_outline_sink->line_count ==
              raw_system_normalized_outline_sink->line_count;
      const std::uint32_t normalized_probe_rows =
          scenario >= 11U ? 26U : 20U;
      for (std::uint32_t y_index = 0U;
           normalized_outline_matches && y_index < normalized_probe_rows;
           ++y_index) {
        for (std::uint32_t x_index = 0U; x_index < 26U; ++x_index) {
          const float probe_start = scenario >= 11U ? -5.17F : -0.25F;
          const float probe_step = scenario >= 11U ? 0.41F : 0.23F;
          const compat::point_2f point{
              probe_start + static_cast<float>(x_index) * probe_step,
              probe_start + static_cast<float>(y_index) * probe_step};
          if (captured_fill_contains(
                  *raw_portable_normalized_outline_sink, point) !=
              captured_fill_contains(
                  *raw_system_normalized_outline_sink, point)) {
            normalized_outline_matches = false;
            break;
          }
        }
      }
      portable_normalized_outline_path->Release();
      system_normalized_outline_path->Release();
      if (!normalized_outline_matches) {
        std::fprintf(
            stderr,
            "normalized outline scenario=%u status=%ld/%ld area=%g/%g "
            "fill=%u/%u "
            "flags=%u/%u callbacks=%u/%u,%u/%u geometry=%u/%u,%u/%u,"
            "%u/%u\n",
            scenario,
            static_cast<long>(portable_normalized_outline_status),
            static_cast<long>(system_normalized_outline_status),
            portable_normalized_area,
            system_normalized_area,
            static_cast<unsigned>(
                raw_portable_normalized_outline_sink->fill_mode),
            static_cast<unsigned>(
                raw_system_normalized_outline_sink->fill_mode),
            static_cast<unsigned>(
                raw_portable_normalized_outline_sink->segment_flags),
            static_cast<unsigned>(
                raw_system_normalized_outline_sink->segment_flags),
            raw_portable_normalized_outline_sink->set_fill_mode_count,
            raw_system_normalized_outline_sink->set_fill_mode_count,
            raw_portable_normalized_outline_sink->set_segment_flags_count,
            raw_system_normalized_outline_sink->set_segment_flags_count,
            raw_portable_normalized_outline_sink->begin_count,
            raw_system_normalized_outline_sink->begin_count,
            raw_portable_normalized_outline_sink->end_count,
            raw_system_normalized_outline_sink->end_count,
            raw_portable_normalized_outline_sink->line_count,
            raw_system_normalized_outline_sink->line_count);
        portable_multi_query_path->Release();
        system_multi_query_path->Release();
        portable_open_query_path->Release();
        system_open_query_path->Release();
        system_factory->Release();
        return 434;
      }
    }
    const D2D1_RECT_F star_center_bounds{
        -0.25F, -0.25F, 0.25F, 0.25F};
    for (const std::uint32_t scenario : {11U, 13U}) {
      ID2D1PathGeometry* portable_star_consumer_path = nullptr;
      ID2D1PathGeometry* system_star_consumer_path = nullptr;
      ID2D1RectangleGeometry* portable_star_center = nullptr;
      ID2D1RectangleGeometry* system_star_center = nullptr;
      bool star_consumer_matches =
          SUCCEEDED(create_multi_outline_path(
              native_factory, scenario, &portable_star_consumer_path)) &&
          SUCCEEDED(create_multi_outline_path(
              system_factory, scenario, &system_star_consumer_path)) &&
          SUCCEEDED(native_factory->CreateRectangleGeometry(
              &star_center_bounds, &portable_star_center)) &&
          SUCCEEDED(system_factory->CreateRectangleGeometry(
              &star_center_bounds, &system_star_center)) &&
          portable_star_consumer_path != nullptr &&
          system_star_consumer_path != nullptr &&
          portable_star_center != nullptr &&
          system_star_center != nullptr;
      D2D1_GEOMETRY_RELATION portable_star_relation =
          D2D1_GEOMETRY_RELATION_UNKNOWN;
      D2D1_GEOMETRY_RELATION system_star_relation =
          D2D1_GEOMETRY_RELATION_UNKNOWN;
      if (star_consumer_matches) {
        star_consumer_matches =
            SUCCEEDED(portable_star_consumer_path->CompareWithGeometry(
                portable_star_center,
                nullptr,
                0.01F,
                &portable_star_relation)) &&
            SUCCEEDED(system_star_consumer_path->CompareWithGeometry(
                system_star_center,
                nullptr,
                0.01F,
                &system_star_relation)) &&
            portable_star_relation == system_star_relation;
      }
      for (std::size_t mode_index = 0U;
           star_consumer_matches && mode_index < combination_modes.size();
           ++mode_index) {
        auto* raw_portable_star_combination = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink>
            portable_star_combination;
        portable_star_combination.attach(raw_portable_star_combination);
        auto* raw_system_star_combination = new simplified_sink();
        com::pointer<compat::simplified_geometry_sink>
            system_star_combination;
        system_star_combination.attach(raw_system_star_combination);
        const HRESULT portable_status =
            portable_star_consumer_path->CombineWithGeometry(
                portable_star_center,
                static_cast<D2D1_COMBINE_MODE>(mode_index),
                nullptr,
                0.01F,
                reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                    portable_star_combination.get()));
        const HRESULT system_status =
            system_star_consumer_path->CombineWithGeometry(
                system_star_center,
                static_cast<D2D1_COMBINE_MODE>(mode_index),
                nullptr,
                0.01F,
                reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                    system_star_combination.get()));
        star_consumer_matches = SUCCEEDED(portable_status) &&
            SUCCEEDED(system_status);
        for (std::uint32_t y_index = 0U;
             star_consumer_matches && y_index < 46U; ++y_index) {
          for (std::uint32_t x_index = 0U; x_index < 46U; ++x_index) {
            const compat::point_2f point{
                -5.13F + static_cast<float>(x_index) * 0.23F,
                -5.07F + static_cast<float>(y_index) * 0.23F};
            if (captured_fill_contains(
                    *raw_portable_star_combination, point) !=
                captured_fill_contains(
                    *raw_system_star_combination, point)) {
              star_consumer_matches = false;
              break;
            }
          }
        }
      }
      if (portable_star_consumer_path != nullptr) {
        portable_star_consumer_path->Release();
      }
      if (system_star_consumer_path != nullptr) {
        system_star_consumer_path->Release();
      }
      if (portable_star_center != nullptr) {
        portable_star_center->Release();
      }
      if (system_star_center != nullptr) {
        system_star_center->Release();
      }
      if (!star_consumer_matches) {
        std::fprintf(
            stderr,
            "star consumer scenario=%u relation=%u/%u\n",
            scenario,
            static_cast<unsigned>(portable_star_relation),
            static_cast<unsigned>(system_star_relation));
        portable_multi_query_path->Release();
        system_multi_query_path->Release();
        portable_open_query_path->Release();
        system_open_query_path->Release();
        system_factory->Release();
        return 470;
      }
    }
    const D2D1_STROKE_STYLE_PROPERTIES open_dash_properties{
        D2D1_CAP_STYLE_FLAT,
        D2D1_CAP_STYLE_FLAT,
        D2D1_CAP_STYLE_SQUARE,
        D2D1_LINE_JOIN_ROUND,
        4.0F,
        D2D1_DASH_STYLE_DASH,
        0.0F};
    ID2D1StrokeStyle* portable_open_dash_style = nullptr;
    ID2D1StrokeStyle* system_open_dash_style = nullptr;
    if (FAILED(native_factory->CreateStrokeStyle(
            &open_dash_properties,
            nullptr,
            0U,
            &portable_open_dash_style)) ||
        FAILED(system_factory->CreateStrokeStyle(
            &open_dash_properties,
            nullptr,
            0U,
            &system_open_dash_style)) ||
        portable_open_dash_style == nullptr ||
        system_open_dash_style == nullptr) {
      if (portable_open_dash_style != nullptr) {
        portable_open_dash_style->Release();
      }
      if (system_open_dash_style != nullptr) {
        system_open_dash_style->Release();
      }
      portable_open_query_path->Release();
      system_open_query_path->Release();
      portable_multi_query_path->Release();
      system_multi_query_path->Release();
      system_factory->Release();
      return 386;
    }
    constexpr std::array<D2D1_POINT_2F, 6U> open_probe_points{{
        {2.0F, 0.75F},
        {-0.25F, 0.0F},
        {4.75F, -0.75F},
        {0.5F, 0.0F},
        {1.5F, 0.0F},
        {1.1F, 0.0F},
    }};
    bool open_probe_matches = true;
    auto* raw_portable_unstroked_dashed_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_unstroked_dashed_widen;
    portable_unstroked_dashed_widen.attach(
        raw_portable_unstroked_dashed_widen);
    auto* raw_system_unstroked_dashed_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        system_unstroked_dashed_widen;
    system_unstroked_dashed_widen.attach(raw_system_unstroked_dashed_widen);
    D2D1_RECT_F portable_unstroked_dashed_bounds{};
    D2D1_RECT_F system_unstroked_dashed_bounds{};
    open_probe_matches =
        SUCCEEDED(portable_unstroked_segment_path->GetWidenedBounds(
            0.5F, portable_open_dash_style, nullptr, 0.001F,
            &portable_unstroked_dashed_bounds)) &&
        SUCCEEDED(system_unstroked_segment_path->GetWidenedBounds(
            0.5F, system_open_dash_style, nullptr, 0.001F,
            &system_unstroked_dashed_bounds)) &&
        approximately_equal(
            portable_unstroked_dashed_bounds.left,
            system_unstroked_dashed_bounds.left) &&
        approximately_equal(
            portable_unstroked_dashed_bounds.top,
            system_unstroked_dashed_bounds.top) &&
        approximately_equal(
            portable_unstroked_dashed_bounds.right,
            system_unstroked_dashed_bounds.right) &&
        approximately_equal(
            portable_unstroked_dashed_bounds.bottom,
            system_unstroked_dashed_bounds.bottom) &&
        SUCCEEDED(portable_unstroked_segment_path->Widen(
            0.5F, portable_open_dash_style, nullptr, 0.001F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                portable_unstroked_dashed_widen.get()))) &&
        SUCCEEDED(system_unstroked_segment_path->Widen(
            0.5F, system_open_dash_style, nullptr, 0.001F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_unstroked_dashed_widen.get())));
    for (std::uint32_t y_index = 0U;
         open_probe_matches && y_index < 24U; ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 40U; ++x_index) {
        const D2D1_POINT_2F point{
            -0.37F + static_cast<float>(x_index) * 0.229F,
            -0.47F + static_cast<float>(y_index) * 0.217F};
        BOOL portable_contains = FALSE;
        BOOL system_contains = FALSE;
        const HRESULT portable_status =
            portable_unstroked_segment_path->StrokeContainsPoint(
                point, 0.5F, portable_open_dash_style, nullptr, 0.001F,
                &portable_contains);
        const HRESULT system_status =
            system_unstroked_segment_path->StrokeContainsPoint(
                point, 0.5F, system_open_dash_style, nullptr, 0.001F,
                &system_contains);
        const bool system_widen_contains = captured_fill_contains(
            *raw_system_unstroked_dashed_widen, {point.x, point.y});
        if (system_widen_contains != (system_contains != FALSE)) {
          continue;
        }
        if (FAILED(portable_status) || FAILED(system_status) ||
            portable_contains != system_contains ||
            captured_fill_contains(
                *raw_portable_unstroked_dashed_widen,
                {point.x, point.y}) != (system_contains != FALSE)) {
          std::fprintf(
              stderr,
              "unstroked dashed mismatch point=%g,%g contains=%d/%d "
              "widen=%d/%d\n",
              point.x,
              point.y,
              portable_contains != FALSE ? 1 : 0,
              system_contains != FALSE ? 1 : 0,
              captured_fill_contains(
                  *raw_portable_unstroked_dashed_widen,
                  {point.x, point.y}) ? 1 : 0,
              system_widen_contains ? 1 : 0);
          open_probe_matches = false;
          break;
        }
      }
    }
    constexpr std::array<D2D1_CAP_STYLE, 2U> terminal_dash_caps{{
        D2D1_CAP_STYLE_ROUND,
        D2D1_CAP_STYLE_TRIANGLE,
    }};
    constexpr std::array<D2D1_POINT_2F, 2U> terminal_dash_probes{{
        {4.15F, 3.85F},
        {4.0F, 3.8F},
    }};
    std::array<ID2D1StrokeStyle*, 2U> portable_terminal_dash_styles{};
    std::array<ID2D1StrokeStyle*, 2U> system_terminal_dash_styles{};
    for (std::size_t style_index = 0U;
         style_index < terminal_dash_caps.size();
         ++style_index) {
      D2D1_STROKE_STYLE_PROPERTIES terminal_properties =
          open_dash_properties;
      terminal_properties.dashCap = terminal_dash_caps[style_index];
      open_probe_matches = open_probe_matches &&
          SUCCEEDED(native_factory->CreateStrokeStyle(
              &terminal_properties,
              nullptr,
              0U,
              &portable_terminal_dash_styles[style_index])) &&
          SUCCEEDED(system_factory->CreateStrokeStyle(
              &terminal_properties,
              nullptr,
              0U,
              &system_terminal_dash_styles[style_index])) &&
          portable_terminal_dash_styles[style_index] != nullptr &&
          system_terminal_dash_styles[style_index] != nullptr;
      if (!open_probe_matches) {
        break;
      }
      BOOL portable_terminal_contains = FALSE;
      BOOL system_terminal_contains = FALSE;
      const HRESULT portable_terminal_status =
          portable_open_query_path->StrokeContainsPoint(
              terminal_dash_probes[style_index],
              0.5F,
              portable_terminal_dash_styles[style_index],
              nullptr,
              0.001F,
              &portable_terminal_contains);
      const HRESULT system_terminal_status =
          system_open_query_path->StrokeContainsPoint(
              terminal_dash_probes[style_index],
              0.5F,
              system_terminal_dash_styles[style_index],
              nullptr,
              0.001F,
              &system_terminal_contains);
      auto* raw_portable_terminal_widen = new simplified_sink();
      com::pointer<compat::simplified_geometry_sink>
          portable_terminal_widen;
      portable_terminal_widen.attach(raw_portable_terminal_widen);
      auto* raw_system_terminal_widen = new simplified_sink();
      com::pointer<compat::simplified_geometry_sink> system_terminal_widen;
      system_terminal_widen.attach(raw_system_terminal_widen);
      const HRESULT portable_terminal_widen_status =
          portable_open_query_path->Widen(
              0.5F,
              portable_terminal_dash_styles[style_index],
              nullptr,
              0.001F,
              reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                  portable_terminal_widen.get()));
      const HRESULT system_terminal_widen_status =
          system_open_query_path->Widen(
              0.5F,
              system_terminal_dash_styles[style_index],
              nullptr,
              0.001F,
              reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                  system_terminal_widen.get()));
      open_probe_matches = open_probe_matches &&
          SUCCEEDED(portable_terminal_status) &&
          SUCCEEDED(system_terminal_status) &&
          SUCCEEDED(portable_terminal_widen_status) &&
          SUCCEEDED(system_terminal_widen_status) &&
          portable_terminal_contains == system_terminal_contains &&
          portable_terminal_contains == TRUE;
    }
    for (std::size_t probe_index = 0U;
         probe_index < open_probe_points.size();
         ++probe_index) {
      const bool dashed_probe = probe_index >= 3U;
      BOOL portable_contains = FALSE;
      BOOL system_contains = FALSE;
      const HRESULT portable_status =
          portable_open_query_path->StrokeContainsPoint(
              open_probe_points[probe_index],
              dashed_probe ? 0.5F : 2.0F,
              dashed_probe ? portable_open_dash_style : nullptr,
              nullptr,
              0.001F,
              &portable_contains);
      const HRESULT system_status =
          system_open_query_path->StrokeContainsPoint(
              open_probe_points[probe_index],
              dashed_probe ? 0.5F : 2.0F,
              dashed_probe ? system_open_dash_style : nullptr,
              nullptr,
              0.001F,
              &system_contains);
      open_probe_matches = open_probe_matches &&
          SUCCEEDED(portable_status) && SUCCEEDED(system_status) &&
          portable_contains == system_contains;
    }
    constexpr std::array<D2D1_POINT_2F, 4U> multi_probe_points{{
        {0.0F, 1.0F},
        {12.0F, 0.4F},
        {10.5F, 0.0F},
        {11.5F, 0.0F},
    }};
    for (std::size_t probe_index = 0U;
         probe_index < multi_probe_points.size();
         ++probe_index) {
      const bool dashed_probe = probe_index >= 2U;
      BOOL portable_contains = FALSE;
      BOOL system_contains = FALSE;
      const HRESULT portable_status =
          portable_multi_query_path->StrokeContainsPoint(
              multi_probe_points[probe_index],
              dashed_probe ? 0.5F : 1.0F,
              dashed_probe ? portable_open_dash_style : nullptr,
              nullptr,
              0.001F,
              &portable_contains);
      const HRESULT system_status =
          system_multi_query_path->StrokeContainsPoint(
              multi_probe_points[probe_index],
              dashed_probe ? 0.5F : 1.0F,
              dashed_probe ? system_open_dash_style : nullptr,
              nullptr,
              0.001F,
              &system_contains);
      open_probe_matches = open_probe_matches &&
          SUCCEEDED(portable_status) && SUCCEEDED(system_status) &&
          portable_contains == system_contains;
    }
    D2D1_RECT_F portable_multi_default_bounds{};
    D2D1_RECT_F system_multi_default_bounds{};
    D2D1_RECT_F portable_multi_dashed_bounds{};
    D2D1_RECT_F system_multi_dashed_bounds{};
    const HRESULT portable_multi_default_bounds_status =
        portable_multi_query_path->GetWidenedBounds(
            2.0F,
            nullptr,
            nullptr,
            0.001F,
            &portable_multi_default_bounds);
    const HRESULT system_multi_default_bounds_status =
        system_multi_query_path->GetWidenedBounds(
            2.0F,
            nullptr,
            nullptr,
            0.001F,
            &system_multi_default_bounds);
    const HRESULT portable_multi_dashed_bounds_status =
        portable_multi_query_path->GetWidenedBounds(
            0.5F,
            portable_open_dash_style,
            nullptr,
            0.001F,
            &portable_multi_dashed_bounds);
    const HRESULT system_multi_dashed_bounds_status =
        system_multi_query_path->GetWidenedBounds(
            0.5F,
            system_open_dash_style,
            nullptr,
            0.001F,
            &system_multi_dashed_bounds);
    open_probe_matches = open_probe_matches &&
        SUCCEEDED(portable_multi_default_bounds_status) &&
        SUCCEEDED(system_multi_default_bounds_status) &&
        SUCCEEDED(portable_multi_dashed_bounds_status) &&
        SUCCEEDED(system_multi_dashed_bounds_status) &&
        approximately_equal(
            portable_multi_default_bounds.left,
            system_multi_default_bounds.left) &&
        approximately_equal(
            portable_multi_default_bounds.top,
            system_multi_default_bounds.top) &&
        approximately_equal(
            portable_multi_default_bounds.right,
            system_multi_default_bounds.right) &&
        approximately_equal(
            portable_multi_default_bounds.bottom,
            system_multi_default_bounds.bottom) &&
        approximately_equal(
            portable_multi_dashed_bounds.left,
            system_multi_dashed_bounds.left) &&
        approximately_equal(
            portable_multi_dashed_bounds.top,
            system_multi_dashed_bounds.top) &&
        approximately_equal(
            portable_multi_dashed_bounds.right,
            system_multi_dashed_bounds.right) &&
        approximately_equal(
            portable_multi_dashed_bounds.bottom,
            system_multi_dashed_bounds.bottom);
    auto* raw_portable_multi_default_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_multi_default_widen;
    portable_multi_default_widen.attach(raw_portable_multi_default_widen);
    auto* raw_system_multi_default_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        system_multi_default_widen;
    system_multi_default_widen.attach(raw_system_multi_default_widen);
    auto* raw_portable_multi_dashed_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_multi_dashed_widen;
    portable_multi_dashed_widen.attach(raw_portable_multi_dashed_widen);
    auto* raw_system_multi_dashed_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        system_multi_dashed_widen;
    system_multi_dashed_widen.attach(raw_system_multi_dashed_widen);
    const HRESULT portable_multi_default_widen_status =
        portable_multi_query_path->Widen(
            2.0F,
            nullptr,
            nullptr,
            0.001F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                portable_multi_default_widen.get()));
    const HRESULT system_multi_default_widen_status =
        system_multi_query_path->Widen(
            2.0F,
            nullptr,
            nullptr,
            0.001F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_multi_default_widen.get()));
    const HRESULT portable_multi_dashed_widen_status =
        portable_multi_query_path->Widen(
            0.5F,
            portable_open_dash_style,
            nullptr,
            0.001F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                portable_multi_dashed_widen.get()));
    const HRESULT system_multi_dashed_widen_status =
        system_multi_query_path->Widen(
            0.5F,
            system_open_dash_style,
            nullptr,
            0.001F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_multi_dashed_widen.get()));
    open_probe_matches = open_probe_matches &&
        SUCCEEDED(portable_multi_default_widen_status) &&
        SUCCEEDED(system_multi_default_widen_status) &&
        SUCCEEDED(portable_multi_dashed_widen_status) &&
        SUCCEEDED(system_multi_dashed_widen_status);
    for (std::uint32_t y_index = 0U;
         open_probe_matches && y_index < 28U;
         ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 52U; ++x_index) {
        const compat::point_2f point{
            -1.31F + static_cast<float>(x_index) * 0.327F,
            -1.29F + static_cast<float>(y_index) * 0.247F};
        BOOL system_dashed_contains = FALSE;
        const HRESULT system_dashed_contains_status =
            system_multi_query_path->StrokeContainsPoint(
                D2D1_POINT_2F{point.x, point.y},
                0.5F,
                system_open_dash_style,
                nullptr,
                0.001F,
                &system_dashed_contains);
        if (captured_fill_contains(
                *raw_portable_multi_default_widen, point) !=
                captured_fill_contains(
                    *raw_system_multi_default_widen, point) ||
            FAILED(system_dashed_contains_status) ||
            captured_fill_contains(
                *raw_portable_multi_dashed_widen, point) !=
                (system_dashed_contains != FALSE)) {
          std::fprintf(
              stderr,
              "system multi widen mismatch point=%g,%g default=%d/%d "
              "dashed=%d/%d figures=%u/%u/%u/%u\n",
              point.x,
              point.y,
              captured_fill_contains(
                  *raw_portable_multi_default_widen, point) ? 1 : 0,
              captured_fill_contains(
                  *raw_system_multi_default_widen, point) ? 1 : 0,
              captured_fill_contains(
                  *raw_portable_multi_dashed_widen, point) ? 1 : 0,
              system_dashed_contains != FALSE ? 1 : 0,
              raw_portable_multi_default_widen->begin_count,
              raw_system_multi_default_widen->begin_count,
              raw_portable_multi_dashed_widen->begin_count,
              raw_system_multi_dashed_widen->begin_count);
          open_probe_matches = false;
          break;
        }
      }
    }
    D2D1_RECT_F portable_open_default_bounds{};
    D2D1_RECT_F system_open_default_bounds{};
    D2D1_RECT_F portable_open_dashed_bounds{};
    D2D1_RECT_F system_open_dashed_bounds{};
    const HRESULT portable_open_default_bounds_status =
        portable_open_query_path->GetWidenedBounds(
            2.0F,
            nullptr,
            nullptr,
            0.001F,
            &portable_open_default_bounds);
    const HRESULT system_open_default_bounds_status =
        system_open_query_path->GetWidenedBounds(
            2.0F,
            nullptr,
            nullptr,
            0.001F,
            &system_open_default_bounds);
    const HRESULT portable_open_dashed_bounds_status =
        portable_open_query_path->GetWidenedBounds(
            0.5F,
            portable_open_dash_style,
            nullptr,
            0.001F,
            &portable_open_dashed_bounds);
    const HRESULT system_open_dashed_bounds_status =
        system_open_query_path->GetWidenedBounds(
            0.5F,
            system_open_dash_style,
            nullptr,
            0.001F,
            &system_open_dashed_bounds);
    open_probe_matches = open_probe_matches &&
        SUCCEEDED(portable_open_default_bounds_status) &&
        SUCCEEDED(system_open_default_bounds_status) &&
        SUCCEEDED(portable_open_dashed_bounds_status) &&
        SUCCEEDED(system_open_dashed_bounds_status) &&
        approximately_equal(
            portable_open_default_bounds.left,
            system_open_default_bounds.left) &&
        approximately_equal(
            portable_open_default_bounds.top,
            system_open_default_bounds.top) &&
        approximately_equal(
            portable_open_default_bounds.right,
            system_open_default_bounds.right) &&
        approximately_equal(
            portable_open_default_bounds.bottom,
            system_open_default_bounds.bottom) &&
        approximately_equal(
            portable_open_dashed_bounds.left,
            system_open_dashed_bounds.left) &&
        approximately_equal(
            portable_open_dashed_bounds.top,
            system_open_dashed_bounds.top) &&
        approximately_equal(
            portable_open_dashed_bounds.right,
            system_open_dashed_bounds.right) &&
        approximately_equal(
            portable_open_dashed_bounds.bottom,
            system_open_dashed_bounds.bottom);
    auto* raw_portable_open_default_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_open_default_widen;
    portable_open_default_widen.attach(raw_portable_open_default_widen);
    auto* raw_system_open_default_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        system_open_default_widen;
    system_open_default_widen.attach(raw_system_open_default_widen);
    auto* raw_portable_open_dashed_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        portable_open_dashed_widen;
    portable_open_dashed_widen.attach(raw_portable_open_dashed_widen);
    auto* raw_system_open_dashed_widen = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        system_open_dashed_widen;
    system_open_dashed_widen.attach(raw_system_open_dashed_widen);
    const HRESULT portable_open_default_widen_status =
        portable_open_query_path->Widen(
            2.0F,
            nullptr,
            nullptr,
            0.001F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                portable_open_default_widen.get()));
    const HRESULT system_open_default_widen_status =
        system_open_query_path->Widen(
            2.0F,
            nullptr,
            nullptr,
            0.001F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_open_default_widen.get()));
    const HRESULT portable_open_dashed_widen_status =
        portable_open_query_path->Widen(
            0.5F,
            portable_open_dash_style,
            nullptr,
            0.001F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                portable_open_dashed_widen.get()));
    const HRESULT system_open_dashed_widen_status =
        system_open_query_path->Widen(
            0.5F,
            system_open_dash_style,
            nullptr,
            0.001F,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_open_dashed_widen.get()));
    open_probe_matches = open_probe_matches &&
        SUCCEEDED(portable_open_default_widen_status) &&
        SUCCEEDED(system_open_default_widen_status) &&
        SUCCEEDED(portable_open_dashed_widen_status) &&
        SUCCEEDED(system_open_dashed_widen_status);
    for (std::uint32_t y_index = 0U;
         open_probe_matches && y_index < 20U;
         ++y_index) {
      for (std::uint32_t x_index = 0U; x_index < 20U; ++x_index) {
        const compat::point_2f point{
            -1.147F + static_cast<float>(x_index) * 0.347F,
            -1.219F + static_cast<float>(y_index) * 0.337F};
        if (captured_fill_contains(
                *raw_portable_open_default_widen, point) !=
                captured_fill_contains(
                    *raw_system_open_default_widen, point) ||
            captured_fill_contains(
                *raw_portable_open_dashed_widen, point) !=
                captured_fill_contains(
                    *raw_system_open_dashed_widen, point)) {
          BOOL portable_direct_contains = FALSE;
          BOOL system_direct_contains = FALSE;
          const HRESULT portable_direct_status =
              portable_open_query_path->StrokeContainsPoint(
                  D2D1_POINT_2F{point.x, point.y},
                  0.5F,
                  portable_open_dash_style,
                  nullptr,
                  0.001F,
                  &portable_direct_contains);
          const HRESULT system_direct_status =
              system_open_query_path->StrokeContainsPoint(
                  D2D1_POINT_2F{point.x, point.y},
                  0.5F,
                  system_open_dash_style,
                  nullptr,
                  0.001F,
                  &system_direct_contains);
          std::fprintf(
              stderr,
              "open system widen mismatch point=%g,%g default=%d/%d "
              "dashed=%d/%d direct=%d/%d:%08lx/%08lx "
              "statuses=%08lx/%08lx/%08lx/%08lx\n",
              point.x,
              point.y,
              captured_fill_contains(
                  *raw_portable_open_default_widen, point) ? 1 : 0,
              captured_fill_contains(
                  *raw_system_open_default_widen, point) ? 1 : 0,
              captured_fill_contains(
                  *raw_portable_open_dashed_widen, point) ? 1 : 0,
              captured_fill_contains(
                  *raw_system_open_dashed_widen, point) ? 1 : 0,
              portable_direct_contains ? 1 : 0,
              system_direct_contains ? 1 : 0,
              static_cast<unsigned long>(portable_direct_status),
              static_cast<unsigned long>(system_direct_status),
              static_cast<unsigned long>(
                  portable_open_default_widen_status),
              static_cast<unsigned long>(system_open_default_widen_status),
              static_cast<unsigned long>(
                  portable_open_dashed_widen_status),
              static_cast<unsigned long>(system_open_dashed_widen_status));
          open_probe_matches = false;
          break;
        }
      }
    }
    for (auto* terminal_style : portable_terminal_dash_styles) {
      if (terminal_style != nullptr) {
        terminal_style->Release();
      }
    }
    for (auto* terminal_style : system_terminal_dash_styles) {
      if (terminal_style != nullptr) {
        terminal_style->Release();
      }
    }
    portable_open_dash_style->Release();
    system_open_dash_style->Release();
    portable_open_query_path->Release();
    system_open_query_path->Release();
    portable_multi_query_path->Release();
    system_multi_query_path->Release();
    if (!open_probe_matches) {
      std::fprintf(
          stderr,
          "open bounds portable default=%g,%g,%g,%g dashed=%g,%g,%g,%g "
          "system default=%g,%g,%g,%g dashed=%g,%g,%g,%g statuses="
          "%08lx/%08lx/%08lx/%08lx widen="
          "%08lx/%08lx/%08lx/%08lx\n",
          portable_open_default_bounds.left,
          portable_open_default_bounds.top,
          portable_open_default_bounds.right,
          portable_open_default_bounds.bottom,
          portable_open_dashed_bounds.left,
          portable_open_dashed_bounds.top,
          portable_open_dashed_bounds.right,
          portable_open_dashed_bounds.bottom,
          system_open_default_bounds.left,
          system_open_default_bounds.top,
          system_open_default_bounds.right,
          system_open_default_bounds.bottom,
          system_open_dashed_bounds.left,
          system_open_dashed_bounds.top,
          system_open_dashed_bounds.right,
          system_open_dashed_bounds.bottom,
          static_cast<unsigned long>(portable_open_default_bounds_status),
          static_cast<unsigned long>(system_open_default_bounds_status),
          static_cast<unsigned long>(portable_open_dashed_bounds_status),
          static_cast<unsigned long>(system_open_dashed_bounds_status),
          static_cast<unsigned long>(portable_open_default_widen_status),
          static_cast<unsigned long>(system_open_default_widen_status),
          static_cast<unsigned long>(portable_open_dashed_widen_status),
          static_cast<unsigned long>(system_open_dashed_widen_status));
      system_factory->Release();
      return 387;
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
  const D2D1_STROKE_STYLE_PROPERTIES system_bevel_path_properties{
      D2D1_CAP_STYLE_FLAT,
      D2D1_CAP_STYLE_FLAT,
      D2D1_CAP_STYLE_FLAT,
      D2D1_LINE_JOIN_BEVEL,
      4.0F,
      D2D1_DASH_STYLE_SOLID,
      0.0F};
  ID2D1StrokeStyle *system_bevel_path_stroke_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_bevel_path_properties, nullptr, 0U,
          &system_bevel_path_stroke_style)) ||
      system_bevel_path_stroke_style == nullptr) {
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 354;
  }
  D2D1_STROKE_STYLE_PROPERTIES system_round_path_properties =
      system_bevel_path_properties;
  system_round_path_properties.lineJoin = D2D1_LINE_JOIN_ROUND;
  ID2D1StrokeStyle *system_round_path_stroke_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_round_path_properties, nullptr, 0U,
          &system_round_path_stroke_style)) ||
      system_round_path_stroke_style == nullptr) {
    system_bevel_path_stroke_style->Release();
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 358;
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
  for (std::size_t stroke_case_index = 0U;
       stroke_case_index < path_stroke_cases.size();
       ++stroke_case_index) {
    const path_stroke_case &stroke_case =
        path_stroke_cases[stroke_case_index];
    BOOL system_contains = FALSE;
    const D2D1_POINT_2F system_point{
        stroke_case.point.x, stroke_case.point.y};
    if (FAILED(system_query_boolean_path->StrokeContainsPoint(
            system_point, 2.0F, nullptr,
            reinterpret_cast<const D2D1_MATRIX_3X2_F *>(
                stroke_case.transform),
            D2D1_DEFAULT_FLATTENING_TOLERANCE, &system_contains)) ||
        (system_contains != FALSE) != stroke_case.expected) {
      system_query_boolean_path->Release();
      system_input_boolean_path->Release();
      system_factory->Release();
      return static_cast<int>(331U + stroke_case_index);
    }
  }
  BOOL system_bevel_corner_outside = FALSE;
  BOOL system_bevel_corner_inside = FALSE;
  if (FAILED(system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{0.1F, 1.1F}, 2.0F,
          system_bevel_path_stroke_style, nullptr,
          D2D1_DEFAULT_FLATTENING_TOLERANCE,
          &system_bevel_corner_outside)) ||
      FAILED(system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{0.6F, 1.6F}, 2.0F,
          system_bevel_path_stroke_style, nullptr,
          D2D1_DEFAULT_FLATTENING_TOLERANCE,
          &system_bevel_corner_inside)) ||
      system_bevel_corner_outside != FALSE ||
      system_bevel_corner_inside == FALSE) {
    system_bevel_path_stroke_style->Release();
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 355;
  }
  BOOL system_round_corner_outside = FALSE;
  BOOL system_round_corner_inside = FALSE;
  if (FAILED(system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{0.1F, 1.1F}, 2.0F,
          system_round_path_stroke_style, nullptr,
          D2D1_DEFAULT_FLATTENING_TOLERANCE,
          &system_round_corner_outside)) ||
      FAILED(system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{0.35F, 1.35F}, 2.0F,
          system_round_path_stroke_style, nullptr,
          D2D1_DEFAULT_FLATTENING_TOLERANCE,
          &system_round_corner_inside)) ||
      system_round_corner_outside != FALSE ||
      system_round_corner_inside == FALSE) {
    system_bevel_path_stroke_style->Release();
    system_round_path_stroke_style->Release();
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 359;
  }
  D2D1_STROKE_STYLE_PROPERTIES system_closed_cover_dash_properties =
      system_round_path_properties;
  system_closed_cover_dash_properties.dashStyle =
      D2D1_DASH_STYLE_CUSTOM;
  constexpr std::array<float, 2U> system_closed_cover_dashes{{
      100.0F,
      1.0F,
  }};
  ID2D1StrokeStyle* system_closed_cover_dash_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_closed_cover_dash_properties,
          system_closed_cover_dashes.data(),
          static_cast<UINT32>(system_closed_cover_dashes.size()),
          &system_closed_cover_dash_style)) ||
      system_closed_cover_dash_style == nullptr) {
    system_bevel_path_stroke_style->Release();
    system_round_path_stroke_style->Release();
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 403;
  }
  auto* raw_system_bevel_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_bevel_path_widen_sink;
  system_bevel_path_widen_sink.attach(raw_system_bevel_path_widen_sink);
  auto* raw_system_round_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_round_path_widen_sink;
  system_round_path_widen_sink.attach(raw_system_round_path_widen_sink);
  auto* raw_system_closed_cover_dash_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_closed_cover_dash_widen_sink;
  system_closed_cover_dash_widen_sink.attach(
      raw_system_closed_cover_dash_widen_sink);
  auto* raw_system_collapsed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_collapsed_path_widen_sink;
  system_collapsed_path_widen_sink.attach(
      raw_system_collapsed_path_widen_sink);
  auto* raw_system_consumed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_consumed_path_widen_sink;
  system_consumed_path_widen_sink.attach(raw_system_consumed_path_widen_sink);
  const HRESULT system_bevel_path_widen_status =
      system_query_boolean_path->Widen(
          2.0F,
          system_bevel_path_stroke_style,
          nullptr,
          0.001F,
          reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
              system_bevel_path_widen_sink.get()));
  const HRESULT system_round_path_widen_status =
      system_query_boolean_path->Widen(
          2.0F,
          system_round_path_stroke_style,
          nullptr,
          0.001F,
          reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
              system_round_path_widen_sink.get()));
  const HRESULT system_closed_cover_dash_widen_status =
      system_query_boolean_path->Widen(
          0.25F,
          system_closed_cover_dash_style,
          nullptr,
          0.001F,
          reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
              system_closed_cover_dash_widen_sink.get()));
  const HRESULT system_collapsed_path_widen_status =
      system_query_boolean_path->Widen(
          4.0F,
          nullptr,
          nullptr,
          0.001F,
          reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
              system_collapsed_path_widen_sink.get()));
  const HRESULT system_consumed_path_widen_status =
      system_query_boolean_path->Widen(
          5.0F,
          nullptr,
          nullptr,
          0.001F,
          reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
              system_consumed_path_widen_sink.get()));
  bool system_closed_style_widen_matches =
      SUCCEEDED(system_bevel_path_widen_status) &&
      SUCCEEDED(system_round_path_widen_status) &&
      SUCCEEDED(system_closed_cover_dash_widen_status) &&
      SUCCEEDED(system_collapsed_path_widen_status) &&
      SUCCEEDED(system_consumed_path_widen_status);
  for (std::uint32_t y_index = 0U;
       system_closed_style_widen_matches && y_index < 28U;
       ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 22U; ++x_index) {
      const compat::point_2f point{
          -0.41F + static_cast<float>(x_index) * 0.317F,
          0.59F + static_cast<float>(y_index) * 0.347F};
      BOOL system_bevel_contains = FALSE;
      BOOL system_round_contains = FALSE;
      BOOL system_closed_cover_dash_contains = FALSE;
      BOOL system_collapsed_contains = FALSE;
      BOOL system_consumed_contains = FALSE;
      const HRESULT system_bevel_contains_status =
          system_query_boolean_path->StrokeContainsPoint(
              D2D1_POINT_2F{point.x, point.y},
              2.0F,
              system_bevel_path_stroke_style,
              nullptr,
              0.001F,
              &system_bevel_contains);
      const HRESULT system_round_contains_status =
          system_query_boolean_path->StrokeContainsPoint(
              D2D1_POINT_2F{point.x, point.y},
              2.0F,
              system_round_path_stroke_style,
              nullptr,
              0.001F,
              &system_round_contains);
      const HRESULT system_closed_cover_dash_contains_status =
          system_query_boolean_path->StrokeContainsPoint(
              D2D1_POINT_2F{point.x, point.y},
              0.25F,
              system_closed_cover_dash_style,
              nullptr,
              0.001F,
              &system_closed_cover_dash_contains);
      const HRESULT system_collapsed_contains_status =
          system_query_boolean_path->StrokeContainsPoint(
              D2D1_POINT_2F{point.x, point.y},
              4.0F,
              nullptr,
              nullptr,
              0.001F,
              &system_collapsed_contains);
      const HRESULT system_consumed_contains_status =
          system_query_boolean_path->StrokeContainsPoint(
              D2D1_POINT_2F{point.x, point.y},
              5.0F,
              nullptr,
              nullptr,
              0.001F,
              &system_consumed_contains);
      bool near_round_boundary = false;
      bool near_closed_cover_boundary = false;
      constexpr std::array<compat::point_2f, 4U> corners{{
          {1.0F, 2.0F},
          {5.0F, 2.0F},
          {5.0F, 8.0F},
          {1.0F, 8.0F},
      }};
      for (const compat::point_2f corner : corners) {
        const double distance = std::hypot(
            static_cast<double>(point.x) - corner.x,
            static_cast<double>(point.y) - corner.y);
        near_round_boundary = near_round_boundary ||
            std::abs(distance - 1.0) < 0.005;
        near_closed_cover_boundary = near_closed_cover_boundary ||
            std::abs(distance - 0.125) < 0.001;
      }
      const bool round_matches = captured_fill_contains(
          *raw_round_path_widen_sink, point) ==
          (system_round_contains != FALSE);
      const bool closed_cover_matches = captured_fill_contains(
          *raw_closed_cover_dash_widen_sink, point) ==
          (system_closed_cover_dash_contains != FALSE);
      if (FAILED(system_bevel_contains_status) ||
          FAILED(system_round_contains_status) ||
          FAILED(system_closed_cover_dash_contains_status) ||
          FAILED(system_collapsed_contains_status) ||
          FAILED(system_consumed_contains_status) ||
          captured_fill_contains(*raw_bevel_path_widen_sink, point) !=
              (system_bevel_contains != FALSE) ||
          (!closed_cover_matches && !near_closed_cover_boundary) ||
          captured_fill_contains(*raw_collapsed_path_widen_sink, point) !=
              (system_collapsed_contains != FALSE) ||
          captured_fill_contains(*raw_consumed_path_widen_sink, point) !=
              (system_consumed_contains != FALSE) ||
          (!round_matches && !near_round_boundary)) {
        std::fprintf(
            stderr,
            "system closed styled widen mismatch point=%g,%g bevel=%d/%d "
            "round=%d/%d statuses=%08lx/%08lx widen=%08lx/%08lx\n",
            point.x,
            point.y,
            captured_fill_contains(*raw_bevel_path_widen_sink, point)
                ? 1
                : 0,
            system_bevel_contains != FALSE ? 1 : 0,
            captured_fill_contains(*raw_round_path_widen_sink, point)
                ? 1
                : 0,
            system_round_contains != FALSE ? 1 : 0,
            static_cast<unsigned long>(system_bevel_contains_status),
            static_cast<unsigned long>(system_round_contains_status),
            static_cast<unsigned long>(system_bevel_path_widen_status),
            static_cast<unsigned long>(system_round_path_widen_status));
        system_closed_style_widen_matches = false;
        break;
      }
    }
  }
  system_closed_cover_dash_style->Release();
  system_bevel_path_stroke_style->Release();
  system_round_path_stroke_style->Release();
  if (!system_closed_style_widen_matches) {
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 401;
  }
  D2D1_STROKE_STYLE_PROPERTIES system_dashed_path_properties =
      system_bevel_path_properties;
  system_dashed_path_properties.dashStyle = D2D1_DASH_STYLE_DASH;
  ID2D1StrokeStyle *system_dashed_path_stroke_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_dashed_path_properties, nullptr, 0U,
          &system_dashed_path_stroke_style)) ||
      system_dashed_path_stroke_style == nullptr) {
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 363;
  }
  D2D1_STROKE_STYLE_PROPERTIES system_round_dashed_path_properties =
      system_dashed_path_properties;
  system_round_dashed_path_properties.dashCap = D2D1_CAP_STYLE_ROUND;
  ID2D1StrokeStyle *system_round_dashed_path_stroke_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_round_dashed_path_properties, nullptr, 0U,
          &system_round_dashed_path_stroke_style)) ||
      system_round_dashed_path_stroke_style == nullptr) {
    system_dashed_path_stroke_style->Release();
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 364;
  }
  D2D1_STROKE_STYLE_PROPERTIES system_square_dashed_path_properties =
      system_dashed_path_properties;
  system_square_dashed_path_properties.dashCap = D2D1_CAP_STYLE_SQUARE;
  ID2D1StrokeStyle *system_square_dashed_path_stroke_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_square_dashed_path_properties, nullptr, 0U,
          &system_square_dashed_path_stroke_style)) ||
      system_square_dashed_path_stroke_style == nullptr) {
    system_round_dashed_path_stroke_style->Release();
    system_dashed_path_stroke_style->Release();
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 372;
  }
  D2D1_STROKE_STYLE_PROPERTIES system_triangle_dashed_path_properties =
      system_dashed_path_properties;
  system_triangle_dashed_path_properties.dashCap = D2D1_CAP_STYLE_TRIANGLE;
  ID2D1StrokeStyle *system_triangle_dashed_path_stroke_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_triangle_dashed_path_properties, nullptr, 0U,
          &system_triangle_dashed_path_stroke_style)) ||
      system_triangle_dashed_path_stroke_style == nullptr) {
    system_square_dashed_path_stroke_style->Release();
    system_round_dashed_path_stroke_style->Release();
    system_dashed_path_stroke_style->Release();
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 373;
  }
  D2D1_STROKE_STYLE_PROPERTIES system_miter_dashed_path_properties =
      system_dashed_path_properties;
  system_miter_dashed_path_properties.lineJoin = D2D1_LINE_JOIN_MITER;
  system_miter_dashed_path_properties.dashOffset = 0.5F;
  ID2D1StrokeStyle *system_miter_dashed_path_stroke_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_miter_dashed_path_properties, nullptr, 0U,
          &system_miter_dashed_path_stroke_style)) ||
      system_miter_dashed_path_stroke_style == nullptr) {
    system_triangle_dashed_path_stroke_style->Release();
    system_square_dashed_path_stroke_style->Release();
    system_round_dashed_path_stroke_style->Release();
    system_dashed_path_stroke_style->Release();
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 375;
  }
  D2D1_STROKE_STYLE_PROPERTIES system_clipped_miter_dashed_properties =
      system_miter_dashed_path_properties;
  system_clipped_miter_dashed_properties.miterLimit = 1.0F;
  ID2D1StrokeStyle *system_clipped_miter_dashed_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_clipped_miter_dashed_properties, nullptr, 0U,
          &system_clipped_miter_dashed_style)) ||
      system_clipped_miter_dashed_style == nullptr) {
    system_miter_dashed_path_stroke_style->Release();
    system_triangle_dashed_path_stroke_style->Release();
    system_square_dashed_path_stroke_style->Release();
    system_round_dashed_path_stroke_style->Release();
    system_dashed_path_stroke_style->Release();
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 381;
  }
  D2D1_STROKE_STYLE_PROPERTIES system_miter_or_bevel_dashed_properties =
      system_miter_dashed_path_properties;
  system_miter_or_bevel_dashed_properties.lineJoin =
      D2D1_LINE_JOIN_MITER_OR_BEVEL;
  system_miter_or_bevel_dashed_properties.miterLimit = 1.0F;
  ID2D1StrokeStyle *system_miter_or_bevel_dashed_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_miter_or_bevel_dashed_properties, nullptr, 0U,
          &system_miter_or_bevel_dashed_style)) ||
      system_miter_or_bevel_dashed_style == nullptr) {
    system_clipped_miter_dashed_style->Release();
    system_miter_dashed_path_stroke_style->Release();
    system_triangle_dashed_path_stroke_style->Release();
    system_square_dashed_path_stroke_style->Release();
    system_round_dashed_path_stroke_style->Release();
    system_dashed_path_stroke_style->Release();
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 377;
  }
  D2D1_STROKE_STYLE_PROPERTIES system_round_join_dashed_properties =
      system_miter_dashed_path_properties;
  system_round_join_dashed_properties.lineJoin = D2D1_LINE_JOIN_ROUND;
  ID2D1StrokeStyle *system_round_join_dashed_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_round_join_dashed_properties, nullptr, 0U,
          &system_round_join_dashed_style)) ||
      system_round_join_dashed_style == nullptr) {
    system_miter_or_bevel_dashed_style->Release();
    system_clipped_miter_dashed_style->Release();
    system_miter_dashed_path_stroke_style->Release();
    system_triangle_dashed_path_stroke_style->Release();
    system_square_dashed_path_stroke_style->Release();
    system_round_dashed_path_stroke_style->Release();
    system_dashed_path_stroke_style->Release();
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 379;
  }
  BOOL system_dash_body = FALSE;
  BOOL system_dash_gap = FALSE;
  BOOL system_flat_dash_cap_gap = FALSE;
  BOOL system_round_dash_cap_gap = FALSE;
  BOOL system_square_dash_cap_gap = FALSE;
  BOOL system_triangle_dash_cap_gap = FALSE;
  BOOL system_square_dash_source_seam = FALSE;
  BOOL system_triangle_dash_source_seam = FALSE;
  BOOL system_round_dash_source_seam = FALSE;
  BOOL system_miter_dash_corner = FALSE;
  BOOL system_miter_or_bevel_dash_corner = FALSE;
  BOOL system_round_join_dash_corner = FALSE;
  BOOL system_clipped_miter_dash_inside = FALSE;
  BOOL system_clipped_miter_dash_tip = FALSE;
  const HRESULT system_dash_body_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{1.5F, 2.0F}, 0.5F,
          system_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance, &system_dash_body);
  const HRESULT system_dash_gap_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{2.5F, 2.0F}, 0.5F,
          system_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance, &system_dash_gap);
  const HRESULT system_flat_dash_cap_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{2.2F, 2.0F}, 0.5F,
          system_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance,
          &system_flat_dash_cap_gap);
  const HRESULT system_round_dash_cap_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{2.2F, 2.0F}, 0.5F,
          system_round_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance,
          &system_round_dash_cap_gap);
  const HRESULT system_square_dash_cap_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{2.2F, 2.0F}, 0.5F,
          system_square_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance, &system_square_dash_cap_gap);
  const HRESULT system_triangle_dash_cap_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{2.2F, 2.0F}, 0.5F,
          system_triangle_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance, &system_triangle_dash_cap_gap);
  const HRESULT system_square_dash_source_seam_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{0.978F, 1.774F}, 0.5F,
          system_square_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance, &system_square_dash_source_seam);
  const HRESULT system_triangle_dash_source_seam_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{0.978F, 1.774F}, 0.5F,
          system_triangle_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance, &system_triangle_dash_source_seam);
  const HRESULT system_round_dash_source_seam_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{0.978F, 1.774F}, 0.5F,
          system_round_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance, &system_round_dash_source_seam);
  const HRESULT system_miter_dash_corner_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{5.2F, 1.8F}, 0.5F,
          system_miter_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance, &system_miter_dash_corner);
  const HRESULT system_miter_or_bevel_corner_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{5.2F, 1.8F}, 0.5F,
          system_miter_or_bevel_dashed_style, nullptr,
          dash_hit_tolerance, &system_miter_or_bevel_dash_corner);
  const HRESULT system_round_join_corner_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{5.17F, 1.83F}, 0.5F,
          system_round_join_dashed_style, nullptr,
          dash_hit_tolerance, &system_round_join_dash_corner);
  const HRESULT system_clipped_miter_inside_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{5.17F, 1.83F}, 0.5F,
          system_clipped_miter_dashed_style, nullptr,
          dash_hit_tolerance, &system_clipped_miter_dash_inside);
  const HRESULT system_clipped_miter_tip_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{5.2F, 1.8F}, 0.5F,
          system_clipped_miter_dashed_style, nullptr,
          dash_hit_tolerance, &system_clipped_miter_dash_tip);
  D2D1_RECT_F system_dashed_path_widened_bounds{};
  D2D1_RECT_F system_round_dashed_path_widened_bounds{};
  D2D1_RECT_F system_transformed_round_dashed_path_widened_bounds{};
  D2D1_RECT_F system_zero_dashed_path_widened_bounds{};
  const HRESULT system_dashed_bounds_status =
      system_query_boolean_path->GetWidenedBounds(
          0.5F, system_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance, &system_dashed_path_widened_bounds);
  const HRESULT system_round_dashed_bounds_status =
      system_query_boolean_path->GetWidenedBounds(
          0.5F, system_round_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance, &system_round_dashed_path_widened_bounds);
  const HRESULT system_transformed_round_dashed_bounds_status =
      system_query_boolean_path->GetWidenedBounds(
          0.5F, system_round_dashed_path_stroke_style,
          reinterpret_cast<const D2D1_MATRIX_3X2_F *>(&transform),
          dash_hit_tolerance,
          &system_transformed_round_dashed_path_widened_bounds);
  const HRESULT system_zero_dashed_bounds_status =
      system_query_boolean_path->GetWidenedBounds(
          0.0F, system_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance, &system_zero_dashed_path_widened_bounds);
  auto *raw_system_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_dashed_path_widen_sink;
  system_dashed_path_widen_sink.attach(raw_system_dashed_path_widen_sink);
  const HRESULT system_dashed_widen_status =
      system_query_boolean_path->Widen(
          0.5F, system_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_dashed_path_widen_sink.get()));
  auto *raw_system_square_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_square_dashed_path_widen_sink;
  system_square_dashed_path_widen_sink.attach(
      raw_system_square_dashed_path_widen_sink);
  const HRESULT system_square_dashed_widen_status =
      system_query_boolean_path->Widen(
          0.5F, system_square_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_square_dashed_path_widen_sink.get()));
  auto *raw_system_triangle_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_triangle_dashed_path_widen_sink;
  system_triangle_dashed_path_widen_sink.attach(
      raw_system_triangle_dashed_path_widen_sink);
  const HRESULT system_triangle_dashed_widen_status =
      system_query_boolean_path->Widen(
          0.5F, system_triangle_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_triangle_dashed_path_widen_sink.get()));
  auto *raw_system_round_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_round_dashed_path_widen_sink;
  system_round_dashed_path_widen_sink.attach(
      raw_system_round_dashed_path_widen_sink);
  const HRESULT system_round_dashed_widen_status =
      system_query_boolean_path->Widen(
          0.5F, system_round_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_round_dashed_path_widen_sink.get()));
  auto *raw_system_miter_dashed_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_miter_dashed_path_widen_sink;
  system_miter_dashed_path_widen_sink.attach(
      raw_system_miter_dashed_path_widen_sink);
  const HRESULT system_miter_dashed_widen_status =
      system_query_boolean_path->Widen(
          0.5F, system_miter_dashed_path_stroke_style, nullptr,
          dash_hit_tolerance,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_miter_dashed_path_widen_sink.get()));
  auto *raw_system_miter_or_bevel_dashed_widen_sink =
      new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_miter_or_bevel_dashed_widen_sink;
  system_miter_or_bevel_dashed_widen_sink.attach(
      raw_system_miter_or_bevel_dashed_widen_sink);
  const HRESULT system_miter_or_bevel_dashed_widen_status =
      system_query_boolean_path->Widen(
          0.5F, system_miter_or_bevel_dashed_style, nullptr,
          dash_hit_tolerance,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_miter_or_bevel_dashed_widen_sink.get()));
  auto *raw_system_round_join_dashed_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_round_join_dashed_widen_sink;
  system_round_join_dashed_widen_sink.attach(
      raw_system_round_join_dashed_widen_sink);
  const HRESULT system_round_join_dashed_widen_status =
      system_query_boolean_path->Widen(
          0.5F, system_round_join_dashed_style, nullptr,
          dash_hit_tolerance,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_round_join_dashed_widen_sink.get()));
  auto *raw_system_clipped_miter_dashed_widen_sink =
      new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_clipped_miter_dashed_widen_sink;
  system_clipped_miter_dashed_widen_sink.attach(
      raw_system_clipped_miter_dashed_widen_sink);
  const HRESULT system_clipped_miter_dashed_widen_status =
      system_query_boolean_path->Widen(
          0.5F, system_clipped_miter_dashed_style, nullptr,
          dash_hit_tolerance,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_clipped_miter_dashed_widen_sink.get()));
  system_round_join_dashed_style->Release();
  system_miter_or_bevel_dashed_style->Release();
  system_clipped_miter_dashed_style->Release();
  system_miter_dashed_path_stroke_style->Release();
  system_triangle_dashed_path_stroke_style->Release();
  system_square_dashed_path_stroke_style->Release();
  system_round_dashed_path_stroke_style->Release();
  system_dashed_path_stroke_style->Release();
  if (FAILED(system_dash_body_status) || FAILED(system_dash_gap_status) ||
      FAILED(system_flat_dash_cap_status) ||
      FAILED(system_round_dash_cap_status) ||
      FAILED(system_square_dash_cap_status) ||
      FAILED(system_triangle_dash_cap_status) ||
      FAILED(system_square_dash_source_seam_status) ||
      FAILED(system_triangle_dash_source_seam_status) ||
      FAILED(system_round_dash_source_seam_status) ||
      FAILED(system_miter_dash_corner_status) ||
      FAILED(system_miter_or_bevel_corner_status) ||
      FAILED(system_round_join_corner_status) ||
      FAILED(system_clipped_miter_inside_status) ||
      FAILED(system_clipped_miter_tip_status) ||
      FAILED(system_dashed_bounds_status) ||
      FAILED(system_round_dashed_bounds_status) ||
      FAILED(system_transformed_round_dashed_bounds_status) ||
      FAILED(system_zero_dashed_bounds_status) ||
      FAILED(system_dashed_widen_status) ||
      FAILED(system_square_dashed_widen_status) ||
      FAILED(system_triangle_dashed_widen_status) ||
      FAILED(system_round_dashed_widen_status) ||
      FAILED(system_miter_dashed_widen_status) ||
      FAILED(system_miter_or_bevel_dashed_widen_status) ||
      FAILED(system_round_join_dashed_widen_status) ||
      FAILED(system_clipped_miter_dashed_widen_status) ||
      (system_dash_body != FALSE) != (dash_body != 0) ||
      (system_dash_gap != FALSE) != (dash_gap != 0) ||
      (system_flat_dash_cap_gap != FALSE) != (flat_dash_cap_gap != 0) ||
      (system_round_dash_cap_gap != FALSE) != (round_dash_cap_gap != 0) ||
      (system_square_dash_cap_gap != FALSE) != (square_dash_cap_gap != 0) ||
      (system_triangle_dash_cap_gap != FALSE) !=
          (triangle_dash_cap_gap != 0) ||
      (system_square_dash_source_seam != FALSE) !=
          (square_dash_source_seam != 0) ||
      (system_triangle_dash_source_seam != FALSE) !=
          (triangle_dash_source_seam != 0) ||
      (system_round_dash_source_seam != FALSE) !=
          (round_dash_source_seam != 0) ||
      (system_miter_dash_corner != FALSE) != (miter_dash_corner != 0) ||
      (system_miter_or_bevel_dash_corner != FALSE) !=
          (miter_or_bevel_dash_corner != 0) ||
      (system_round_join_dash_corner != FALSE) !=
          (round_join_dash_corner != 0) ||
      (system_clipped_miter_dash_inside != FALSE) !=
          (clipped_miter_dash_inside != 0) ||
      (system_clipped_miter_dash_tip != FALSE) !=
          (clipped_miter_dash_tip != 0) ||
      !approximately_equal(
          system_dashed_path_widened_bounds.left,
          dashed_path_widened_bounds.left) ||
      !approximately_equal(
          system_dashed_path_widened_bounds.top,
          dashed_path_widened_bounds.top) ||
      !approximately_equal(
          system_dashed_path_widened_bounds.right,
          dashed_path_widened_bounds.right) ||
      !approximately_equal(
          system_dashed_path_widened_bounds.bottom,
          dashed_path_widened_bounds.bottom) ||
      !approximately_equal(
          system_round_dashed_path_widened_bounds.left,
          round_dashed_path_widened_bounds.left) ||
      !approximately_equal(
          system_round_dashed_path_widened_bounds.top,
          round_dashed_path_widened_bounds.top) ||
      !approximately_equal(
          system_round_dashed_path_widened_bounds.right,
          round_dashed_path_widened_bounds.right) ||
      !approximately_equal(
          system_round_dashed_path_widened_bounds.bottom,
          round_dashed_path_widened_bounds.bottom) ||
      !approximately_equal(
          system_transformed_round_dashed_path_widened_bounds.left,
          transformed_round_dashed_path_widened_bounds.left) ||
      !approximately_equal(
          system_transformed_round_dashed_path_widened_bounds.top,
          transformed_round_dashed_path_widened_bounds.top) ||
      !approximately_equal(
          system_transformed_round_dashed_path_widened_bounds.right,
          transformed_round_dashed_path_widened_bounds.right) ||
      !approximately_equal(
          system_transformed_round_dashed_path_widened_bounds.bottom,
          transformed_round_dashed_path_widened_bounds.bottom) ||
      !approximately_equal(
          system_zero_dashed_path_widened_bounds.left,
          zero_dashed_path_widened_bounds.left) ||
      !approximately_equal(
          system_zero_dashed_path_widened_bounds.top,
          zero_dashed_path_widened_bounds.top) ||
      !approximately_equal(
          system_zero_dashed_path_widened_bounds.right,
          zero_dashed_path_widened_bounds.right) ||
      !approximately_equal(
          system_zero_dashed_path_widened_bounds.bottom,
          zero_dashed_path_widened_bounds.bottom)) {
    std::fprintf(stderr,
                 "dashed stroke parity portable=%d,%d,%d,%d "
                 "system=%d,%d,%d,%d\n",
                 dash_body, dash_gap, flat_dash_cap_gap,
                 round_dash_cap_gap, static_cast<int>(system_dash_body),
                 static_cast<int>(system_dash_gap),
                 static_cast<int>(system_flat_dash_cap_gap),
                 static_cast<int>(system_round_dash_cap_gap));
    std::fprintf(stderr,
                 "dashed round widen beziers portable=%u system=%u "
                 "figures=%u/%u lines=%u/%u seam square=%d/%d "
                 "triangle=%d/%d round=%d/%d miter=%d/%d "
                 "miter-or-bevel=%d/%d round-join=%d/%d "
                 "clipped=%d/%d,%d/%d\n",
                 raw_round_dashed_path_widen_sink->bezier_count,
                 raw_system_round_dashed_path_widen_sink->bezier_count,
                 raw_round_dashed_path_widen_sink->begin_count,
                 raw_system_round_dashed_path_widen_sink->begin_count,
                 raw_round_dashed_path_widen_sink->line_count,
                 raw_system_round_dashed_path_widen_sink->line_count,
                 square_dash_source_seam,
                 static_cast<int>(system_square_dash_source_seam),
                 triangle_dash_source_seam,
                 static_cast<int>(system_triangle_dash_source_seam),
                 round_dash_source_seam,
                 static_cast<int>(system_round_dash_source_seam),
                 miter_dash_corner,
                 static_cast<int>(system_miter_dash_corner),
                 miter_or_bevel_dash_corner,
                 static_cast<int>(system_miter_or_bevel_dash_corner),
                 round_join_dash_corner,
                 static_cast<int>(system_round_join_dash_corner),
                 clipped_miter_dash_inside,
                 static_cast<int>(system_clipped_miter_dash_inside),
                 clipped_miter_dash_tip,
                 static_cast<int>(system_clipped_miter_dash_tip));
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 365;
  }
  for (std::uint32_t y_index = 0U; y_index < 22U; ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 18U; ++x_index) {
      const compat::point_2f point{
          0.671F + static_cast<float>(x_index) * 0.307F,
          1.437F + static_cast<float>(y_index) * 0.337F};
      if (captured_fill_contains(*raw_system_dashed_path_widen_sink, point) !=
              captured_fill_contains(*raw_dashed_path_widen_sink, point) ||
          captured_fill_contains(
              *raw_system_miter_dashed_path_widen_sink, point) !=
              captured_fill_contains(
                  *raw_miter_dashed_path_widen_sink, point) ||
          captured_fill_contains(
              *raw_system_miter_or_bevel_dashed_widen_sink, point) !=
              captured_fill_contains(
                  *raw_miter_or_bevel_dashed_path_widen_sink, point)) {
        std::fprintf(stderr,
                     "dashed widen system mismatch point=%g,%g "
                     "flat=%d/%d miter=%d/%d miter-or-bevel=%d/%d "
                     "portable_figures=%u system_figures=%u\n",
                     point.x, point.y,
                     captured_fill_contains(*raw_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     captured_fill_contains(
                         *raw_system_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     captured_fill_contains(
                         *raw_miter_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     captured_fill_contains(
                         *raw_system_miter_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     captured_fill_contains(
                         *raw_miter_or_bevel_dashed_path_widen_sink, point)
                         ? 1
                         : 0,
                     captured_fill_contains(
                         *raw_system_miter_or_bevel_dashed_widen_sink, point)
                         ? 1
                         : 0,
                     raw_dashed_path_widen_sink->begin_count,
                     raw_system_dashed_path_widen_sink->begin_count);
        system_query_boolean_path->Release();
        system_input_boolean_path->Release();
        system_factory->Release();
        return 369;
      }
    }
  }
  BOOL system_zero_width_edge = FALSE;
  BOOL system_zero_width_interior = FALSE;
  const HRESULT system_zero_edge_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{1.0F, 4.0F}, 0.0F, nullptr, nullptr,
          D2D1_DEFAULT_FLATTENING_TOLERANCE, &system_zero_width_edge);
  const HRESULT system_zero_interior_status =
      system_query_boolean_path->StrokeContainsPoint(
          D2D1_POINT_2F{3.0F, 4.0F}, 0.0F, nullptr, nullptr,
          D2D1_DEFAULT_FLATTENING_TOLERANCE, &system_zero_width_interior);
  if (FAILED(system_zero_edge_status) ||
      FAILED(system_zero_interior_status) ||
      system_zero_width_edge != FALSE ||
      system_zero_width_interior != FALSE) {
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return FAILED(system_zero_edge_status) ||
            FAILED(system_zero_interior_status)
        ? 339
        : system_zero_width_edge != FALSE ? 340 : 341;
  }
  D2D1_RECT_F system_path_widened_bounds{};
  D2D1_RECT_F system_transformed_path_widened_bounds{};
  D2D1_RECT_F system_zero_path_widened_bounds{};
  D2D1_RECT_F system_concave_path_widened_bounds{};
  if (FAILED(system_query_boolean_path->GetWidenedBounds(
          2.0F, nullptr, nullptr, D2D1_DEFAULT_FLATTENING_TOLERANCE,
          &system_path_widened_bounds)) ||
      FAILED(system_query_boolean_path->GetWidenedBounds(
          2.0F, nullptr,
          reinterpret_cast<const D2D1_MATRIX_3X2_F *>(&transform),
          D2D1_DEFAULT_FLATTENING_TOLERANCE,
          &system_transformed_path_widened_bounds)) ||
      FAILED(system_query_boolean_path->GetWidenedBounds(
          0.0F, nullptr, nullptr, D2D1_DEFAULT_FLATTENING_TOLERANCE,
          &system_zero_path_widened_bounds)) ||
      FAILED(system_input_boolean_path->GetWidenedBounds(
          2.0F, nullptr, nullptr, D2D1_DEFAULT_FLATTENING_TOLERANCE,
          &system_concave_path_widened_bounds)) ||
      !approximately_equal(
          system_path_widened_bounds.left, path_widened_bounds.left) ||
      !approximately_equal(
          system_path_widened_bounds.top, path_widened_bounds.top) ||
      !approximately_equal(
          system_path_widened_bounds.right, path_widened_bounds.right) ||
      !approximately_equal(
          system_path_widened_bounds.bottom, path_widened_bounds.bottom) ||
      !approximately_equal(
          system_transformed_path_widened_bounds.left,
          transformed_path_widened_bounds.left) ||
      !approximately_equal(
          system_transformed_path_widened_bounds.top,
          transformed_path_widened_bounds.top) ||
      !approximately_equal(
          system_transformed_path_widened_bounds.right,
          transformed_path_widened_bounds.right) ||
      !approximately_equal(
          system_transformed_path_widened_bounds.bottom,
          transformed_path_widened_bounds.bottom) ||
      !approximately_equal(
          system_zero_path_widened_bounds.left,
          zero_path_widened_bounds.left) ||
      !approximately_equal(
          system_zero_path_widened_bounds.top,
          zero_path_widened_bounds.top) ||
      !approximately_equal(
          system_zero_path_widened_bounds.right,
          zero_path_widened_bounds.right) ||
      !approximately_equal(
          system_zero_path_widened_bounds.bottom,
          zero_path_widened_bounds.bottom) ||
      !approximately_equal(
          system_concave_path_widened_bounds.left,
          concave_path_widened_bounds.left) ||
      !approximately_equal(
          system_concave_path_widened_bounds.top,
          concave_path_widened_bounds.top) ||
      !approximately_equal(
          system_concave_path_widened_bounds.right,
          concave_path_widened_bounds.right) ||
      !approximately_equal(
          system_concave_path_widened_bounds.bottom,
          concave_path_widened_bounds.bottom)) {
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 343;
  }
  auto *raw_system_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink> system_path_widen_sink;
  system_path_widen_sink.attach(raw_system_path_widen_sink);
  auto *raw_system_zero_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_zero_path_widen_sink;
  system_zero_path_widen_sink.attach(raw_system_zero_path_widen_sink);
  if (FAILED(system_query_boolean_path->Widen(
          2.0F, nullptr, nullptr, D2D1_DEFAULT_FLATTENING_TOLERANCE,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_path_widen_sink.get()))) ||
      FAILED(system_query_boolean_path->Widen(
          0.0F, nullptr, nullptr, D2D1_DEFAULT_FLATTENING_TOLERANCE,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_zero_path_widen_sink.get()))) ||
      raw_system_zero_path_widen_sink->begin_count !=
          raw_zero_path_widen_sink->begin_count ||
      raw_system_zero_path_widen_sink->end_count !=
          raw_zero_path_widen_sink->end_count ||
      raw_system_zero_path_widen_sink->line_count !=
          raw_zero_path_widen_sink->line_count ||
      raw_system_zero_path_widen_sink->bezier_count !=
          raw_zero_path_widen_sink->bezier_count ||
      raw_system_zero_path_widen_sink->set_fill_mode_count !=
          raw_zero_path_widen_sink->set_fill_mode_count ||
      raw_system_zero_path_widen_sink->set_segment_flags_count !=
          raw_zero_path_widen_sink->set_segment_flags_count ||
      raw_system_zero_path_widen_sink->fill_mode !=
          raw_zero_path_widen_sink->fill_mode ||
      raw_system_path_widen_sink->set_fill_mode_count !=
          raw_path_widen_sink->set_fill_mode_count ||
      raw_system_path_widen_sink->set_segment_flags_count !=
          raw_path_widen_sink->set_segment_flags_count ||
      raw_system_path_widen_sink->fill_mode !=
          raw_path_widen_sink->fill_mode) {
    std::fprintf(
        stderr,
        "zero widen callbacks system=%u/%u/%u/%u/%u/%u fill=%u "
        "portable=%u/%u/%u/%u/%u/%u fill=%u\n",
        raw_system_zero_path_widen_sink->begin_count,
        raw_system_zero_path_widen_sink->end_count,
        raw_system_zero_path_widen_sink->line_count,
        raw_system_zero_path_widen_sink->bezier_count,
        raw_system_zero_path_widen_sink->set_fill_mode_count,
        raw_system_zero_path_widen_sink->set_segment_flags_count,
        static_cast<unsigned>(raw_system_zero_path_widen_sink->fill_mode),
        raw_zero_path_widen_sink->begin_count,
        raw_zero_path_widen_sink->end_count,
        raw_zero_path_widen_sink->line_count,
        raw_zero_path_widen_sink->bezier_count,
        raw_zero_path_widen_sink->set_fill_mode_count,
        raw_zero_path_widen_sink->set_segment_flags_count,
        static_cast<unsigned>(raw_zero_path_widen_sink->fill_mode));
    std::fprintf(
        stderr,
        "nonzero widen callbacks system=%u/%u fill=%u flags=%u "
        "portable=%u/%u fill=%u flags=%u\n",
        raw_system_path_widen_sink->set_fill_mode_count,
        raw_system_path_widen_sink->set_segment_flags_count,
        static_cast<unsigned>(raw_system_path_widen_sink->fill_mode),
        static_cast<unsigned>(raw_system_path_widen_sink->segment_flags),
        raw_path_widen_sink->set_fill_mode_count,
        raw_path_widen_sink->set_segment_flags_count,
        static_cast<unsigned>(raw_path_widen_sink->fill_mode),
        static_cast<unsigned>(raw_path_widen_sink->segment_flags));
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 346;
  }
  for (std::uint32_t y_index = 0U; y_index < 20U; ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 16U; ++x_index) {
      const compat::point_2f point{
          -0.63F + static_cast<float>(x_index) * 0.49F,
          0.37F + static_cast<float>(y_index) * 0.49F};
      if (captured_fill_contains(*raw_system_path_widen_sink, point) !=
          captured_fill_contains(*raw_path_widen_sink, point)) {
        system_query_boolean_path->Release();
        system_input_boolean_path->Release();
        system_factory->Release();
        return 347;
      }
    }
  }
  auto *raw_system_concave_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_concave_path_widen_sink;
  system_concave_path_widen_sink.attach(
      raw_system_concave_path_widen_sink);
  auto *raw_system_concave_bevel_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_concave_bevel_path_widen_sink;
  system_concave_bevel_path_widen_sink.attach(
      raw_system_concave_bevel_path_widen_sink);
  auto *raw_system_concave_round_path_widen_sink = new simplified_sink();
  com::pointer<compat::simplified_geometry_sink>
      system_concave_round_path_widen_sink;
  system_concave_round_path_widen_sink.attach(
      raw_system_concave_round_path_widen_sink);
  ID2D1StrokeStyle *system_concave_bevel_style = nullptr;
  ID2D1StrokeStyle *system_concave_round_style = nullptr;
  if (FAILED(system_factory->CreateStrokeStyle(
          &system_bevel_path_properties, nullptr, 0U,
          &system_concave_bevel_style)) ||
      FAILED(system_factory->CreateStrokeStyle(
          &system_round_path_properties, nullptr, 0U,
          &system_concave_round_style)) ||
      system_concave_bevel_style == nullptr ||
      system_concave_round_style == nullptr ||
      FAILED(system_input_boolean_path->Widen(
          0.4F, nullptr, nullptr, D2D1_DEFAULT_FLATTENING_TOLERANCE,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_concave_path_widen_sink.get()))) ||
      FAILED(system_input_boolean_path->Widen(
          0.4F, system_concave_bevel_style, nullptr,
          D2D1_DEFAULT_FLATTENING_TOLERANCE,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_concave_bevel_path_widen_sink.get()))) ||
      FAILED(system_input_boolean_path->Widen(
          0.4F, system_concave_round_style, nullptr,
          D2D1_DEFAULT_FLATTENING_TOLERANCE,
          reinterpret_cast<ID2D1SimplifiedGeometrySink *>(
              system_concave_round_path_widen_sink.get())))) {
    if (system_concave_bevel_style != nullptr) {
      system_concave_bevel_style->Release();
    }
    if (system_concave_round_style != nullptr) {
      system_concave_round_style->Release();
    }
    system_query_boolean_path->Release();
    system_input_boolean_path->Release();
    system_factory->Release();
    return 350;
  }
  for (std::uint32_t y_index = 0U; y_index < 20U; ++y_index) {
    for (std::uint32_t x_index = 0U; x_index < 16U; ++x_index) {
      const compat::point_2f point{
          2.57F + static_cast<float>(x_index) * 0.31F,
          0.47F + static_cast<float>(y_index) * 0.47F};
      if (captured_fill_contains(
              *raw_system_concave_path_widen_sink, point) !=
              captured_fill_contains(*raw_concave_path_widen_sink, point) ||
          captured_fill_contains(
              *raw_system_concave_bevel_path_widen_sink, point) !=
              captured_fill_contains(
                  *raw_concave_bevel_path_widen_sink, point) ||
          captured_fill_contains(
              *raw_system_concave_round_path_widen_sink, point) !=
              captured_fill_contains(
                  *raw_concave_round_path_widen_sink, point)) {
        system_concave_bevel_style->Release();
        system_concave_round_style->Release();
        system_query_boolean_path->Release();
        system_input_boolean_path->Release();
        system_factory->Release();
        return 351;
      }
    }
  }
  system_concave_bevel_style->Release();
  system_concave_round_style->Release();
  for (std::size_t stroke_case_index = 0U;
       stroke_case_index < concave_path_stroke_cases.size();
       ++stroke_case_index) {
    const path_stroke_case &stroke_case =
        concave_path_stroke_cases[stroke_case_index];
    BOOL system_contains = FALSE;
    const D2D1_POINT_2F system_point{
        stroke_case.point.x, stroke_case.point.y};
    if (FAILED(system_input_boolean_path->StrokeContainsPoint(
            system_point, 2.0F, nullptr, nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE, &system_contains)) ||
        (system_contains != FALSE) != stroke_case.expected) {
      system_query_boolean_path->Release();
      system_input_boolean_path->Release();
      system_factory->Release();
      return static_cast<int>(336U + stroke_case_index);
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
    auto* raw_system_zero_rectangle_widen_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        system_zero_rectangle_widen_sink;
    system_zero_rectangle_widen_sink.attach(
        raw_system_zero_rectangle_widen_sink);
    const HRESULT system_zero_rectangle_widen_status =
        system_group_rectangle->Widen(
            0.0F,
            nullptr,
            &system_rectangle_transform,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                system_zero_rectangle_widen_sink.get()));
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
    auto* raw_system_zero_transformed_widen_sink = new simplified_sink();
    com::pointer<compat::simplified_geometry_sink>
        system_zero_transformed_widen_sink;
    system_zero_transformed_widen_sink.attach(
        raw_system_zero_transformed_widen_sink);
    const HRESULT system_zero_transformed_widen_status =
        system_transformed_rectangle == nullptr
        ? system_transformed_create_status
        : system_transformed_rectangle->Widen(
              0.0F,
              nullptr,
              &system_rectangle_transform,
              D2D1_DEFAULT_FLATTENING_TOLERANCE,
              reinterpret_cast<ID2D1SimplifiedGeometrySink*>(
                  system_zero_transformed_widen_sink.get()));
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
            system.set_fill_mode_count == portable.set_fill_mode_count &&
            system.set_segment_flags_count ==
                portable.set_segment_flags_count &&
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
        FAILED(system_zero_rectangle_widen_status) ||
        FAILED(system_transformed_widen_status) ||
        FAILED(system_zero_transformed_widen_status) ||
        !widen_sinks_match(*raw_system_widen_sink, *raw_widen_sink) ||
        !widen_sinks_match(
            *raw_system_zero_rectangle_widen_sink,
            *raw_zero_rectangle_widen_sink) ||
        !widen_sinks_match(
            *raw_system_transformed_widen_sink,
            *raw_transformed_widen_sink) ||
        !widen_sinks_match(
            *raw_system_zero_transformed_widen_sink,
            *raw_zero_transformed_widen_sink)) {
        std::fprintf(
            stderr,
            "widen matches base=%d zero=%d transformed=%d zero-transformed=%d\n",
            widen_sinks_match(*raw_system_widen_sink, *raw_widen_sink)
                ? 1
                : 0,
            widen_sinks_match(
                *raw_system_zero_rectangle_widen_sink,
                *raw_zero_rectangle_widen_sink)
                ? 1
                : 0,
            widen_sinks_match(
                *raw_system_transformed_widen_sink,
                *raw_transformed_widen_sink)
                ? 1
                : 0,
            widen_sinks_match(
                *raw_system_zero_transformed_widen_sink,
                *raw_zero_transformed_widen_sink)
                ? 1
                : 0);
        std::fprintf(
            stderr,
            "widen oracle base=%d/%u/%u/%u fill=%u callbacks=%u/%u "
            "portable=%u/%u transformed=%d/%u/%u/%u fill=%u "
            "callbacks=%u/%u portable=%u/%u\n",
            static_cast<int>(system_widen_status),
            raw_system_widen_sink->begin_count,
            raw_system_widen_sink->end_count,
            raw_system_widen_sink->line_count,
            static_cast<unsigned>(raw_system_widen_sink->fill_mode),
            raw_system_widen_sink->set_fill_mode_count,
            raw_system_widen_sink->set_segment_flags_count,
            raw_widen_sink->set_fill_mode_count,
            raw_widen_sink->set_segment_flags_count,
            static_cast<int>(system_transformed_widen_status),
            raw_system_transformed_widen_sink->begin_count,
            raw_system_transformed_widen_sink->end_count,
            raw_system_transformed_widen_sink->line_count,
            static_cast<unsigned>(
                raw_system_transformed_widen_sink->fill_mode),
            raw_system_transformed_widen_sink->set_fill_mode_count,
            raw_system_transformed_widen_sink->set_segment_flags_count,
        raw_transformed_widen_sink->set_fill_mode_count,
        raw_transformed_widen_sink->set_segment_flags_count);
        std::fprintf(
            stderr,
            "zero rectangle system status=%ld fill=%u flags=%u "
            "callbacks=%u/%u geometry=%u/%u/%u/%zu portable fill=%u "
            "flags=%u callbacks=%u/%u geometry=%u/%u/%u/%zu; "
            "transformed status=%ld "
            "system fill=%u callbacks=%u/%u portable fill=%u callbacks=%u/%u\n",
            static_cast<long>(system_zero_rectangle_widen_status),
            static_cast<unsigned>(
                raw_system_zero_rectangle_widen_sink->fill_mode),
            static_cast<unsigned>(
                raw_system_zero_rectangle_widen_sink->segment_flags),
            raw_system_zero_rectangle_widen_sink->set_fill_mode_count,
            raw_system_zero_rectangle_widen_sink->set_segment_flags_count,
            raw_system_zero_rectangle_widen_sink->begin_count,
            raw_system_zero_rectangle_widen_sink->end_count,
            raw_system_zero_rectangle_widen_sink->line_count,
            raw_system_zero_rectangle_widen_sink->line_point_count,
            static_cast<unsigned>(raw_zero_rectangle_widen_sink->fill_mode),
            static_cast<unsigned>(
                raw_zero_rectangle_widen_sink->segment_flags),
            raw_zero_rectangle_widen_sink->set_fill_mode_count,
            raw_zero_rectangle_widen_sink->set_segment_flags_count,
            raw_zero_rectangle_widen_sink->begin_count,
            raw_zero_rectangle_widen_sink->end_count,
            raw_zero_rectangle_widen_sink->line_count,
            raw_zero_rectangle_widen_sink->line_point_count,
            static_cast<long>(system_zero_transformed_widen_status),
            static_cast<unsigned>(
                raw_system_zero_transformed_widen_sink->fill_mode),
            raw_system_zero_transformed_widen_sink->set_fill_mode_count,
            raw_system_zero_transformed_widen_sink->set_segment_flags_count,
            static_cast<unsigned>(
                raw_zero_transformed_widen_sink->fill_mode),
            raw_zero_transformed_widen_sink->set_fill_mode_count,
            raw_zero_transformed_widen_sink->set_segment_flags_count);
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
