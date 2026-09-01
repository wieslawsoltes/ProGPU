#include "progpu_native_direct2d_render_target.hpp"

#include "progpu_native_scene_builder.hpp"
#include "../Scene/progpu_native_semantic_path_stroke.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <limits>
#include <mutex>
#include <new>
#include <numbers>
#include <span>
#include <utility>
#include <vector>

namespace progpu::native::direct2d::compat::detail {
namespace {

namespace semantic_path_stroke = progpu::native::semantic_path_stroke;

constexpr matrix_3x2_f identity_transform{
    1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
constexpr std::uint32_t dxgi_format_r8g8b8a8_unorm = 28U;
constexpr std::uint32_t dxgi_format_b8g8r8a8_unorm = 87U;
constexpr com::guid scene_bitmap_native_interface_id{
    0x559DFBE4U,
    0xD6B7U,
    0x45D2U,
    {0x82U, 0x25U, 0x75U, 0xF0U, 0x12U, 0xECU, 0x78U, 0xC3U}};
constexpr com::guid scene_bitmap_brush_native_interface_id{
    0x7FB7E4D7U,
    0xC094U,
    0x4ABAU,
    {0x8EU, 0x14U, 0x59U, 0xE8U, 0x67U, 0x75U, 0xC5U, 0x6BU}};
constexpr com::guid scene_layer_native_interface_id{
    0x71803498U,
    0x681EU,
    0x4B25U,
    {0xB1U, 0xEFU, 0x91U, 0x47U, 0x45U, 0x68U, 0x3CU, 0x17U}};

[[nodiscard]] bool valid_color(const color_f& value) noexcept
{
    return std::isfinite(value.red) && std::isfinite(value.green) &&
        std::isfinite(value.blue) && std::isfinite(value.alpha);
}

[[nodiscard]] bool valid_point(point_2f value) noexcept
{
    return std::isfinite(value.x) && std::isfinite(value.y);
}

[[nodiscard]] bool valid_rectangle(const rectangle_f& value) noexcept
{
    return core::rectangle_geometry::valid_rectangle(value);
}

[[nodiscard]] bool valid_native_rectangle(
    const progpu_native_image_rect& value) noexcept
{
    return std::isfinite(value.x) && std::isfinite(value.y) &&
        std::isfinite(value.width) && std::isfinite(value.height) &&
        value.width >= 0.0F && value.height >= 0.0F;
}

[[nodiscard]] progpu_native_image_rect intersect_rectangles(
    const progpu_native_image_rect& left,
    const progpu_native_image_rect& right) noexcept
{
    const float x = std::max(left.x, right.x);
    const float y = std::max(left.y, right.y);
    const float far_x = std::min(
        left.x + left.width,
        right.x + right.width);
    const float far_y = std::min(
        left.y + left.height,
        right.y + right.height);
    return {
        x,
        y,
        std::max(0.0F, far_x - x),
        std::max(0.0F, far_y - y)};
}

[[nodiscard]] bool infinite_rectangle(const rectangle_f& value) noexcept
{
    const float maximum = std::numeric_limits<float>::max();
    return value.left == -maximum && value.top == -maximum &&
        value.right == maximum && value.bottom == maximum;
}

[[nodiscard]] bool axis_preserving_transform(
    const matrix_3x2_f& value) noexcept
{
    return value.m12 == 0.0F && value.m21 == 0.0F;
}

[[nodiscard]] bool valid_dpi(float dpi_x, float dpi_y) noexcept
{
    return std::isfinite(dpi_x) && std::isfinite(dpi_y) &&
        dpi_x > 0.0F && dpi_y > 0.0F;
}

[[nodiscard]] bool valid_opacity(float value) noexcept
{
    return std::isfinite(value) && value >= 0.0F && value <= 1.0F;
}

[[nodiscard]] bool valid_extend_mode(extend_mode value) noexcept
{
    return value == extend_mode::clamp || value == extend_mode::wrap ||
        value == extend_mode::mirror;
}

[[nodiscard]] bool valid_bitmap_interpolation_mode(
    bitmap_interpolation_mode value) noexcept
{
    return value == bitmap_interpolation_mode::nearest_neighbor ||
        value == bitmap_interpolation_mode::linear;
}

[[nodiscard]] bool valid_brush_properties(
    const brush_properties& value) noexcept
{
    return valid_opacity(value.opacity) &&
        core::valid_transform(&value.transform);
}

[[nodiscard]] bool valid_rectangle(const rectangle_u& value) noexcept
{
    return value.left < value.right && value.top < value.bottom;
}

struct bitmap_snapshot final {
    std::uint32_t width = 0U;
    std::uint32_t height = 0U;
    std::uint32_t row_bytes = 0U;
    pixel_format format{};
    float dpi_x = 96.0F;
    float dpi_y = 96.0F;
    std::uint64_t generation = 0U;
};

struct scene_bitmap_native : com::unknown {
    virtual com::result PROGPU_NATIVE_COM_CALL GetSnapshot(
        bitmap_snapshot* snapshot) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL AddToScene(
        semantic_scene_builder* builder,
        std::uint32_t* resource_index,
        bitmap_snapshot* snapshot) const noexcept = 0;
    virtual com::result PROGPU_NATIVE_COM_CALL CopyPixels(
        const rectangle_u* source_rectangle,
        pixel_format expected_format,
        void* destination,
        std::uint32_t destination_pitch) const noexcept = 0;
};

struct scene_bitmap_brush_native : com::unknown {
    virtual com::result PROGPU_NATIVE_COM_CALL GetSceneSnapshot(
        bitmap** source,
        extend_mode* extend_x,
        extend_mode* extend_y,
        bitmap_interpolation_mode* interpolation,
        float* opacity,
        matrix_3x2_f* transform) const noexcept = 0;
};

struct scene_layer_native : com::unknown {
    virtual com::result PROGPU_NATIVE_COM_CALL BeginUse(
        const void* target,
        size_f required_size) noexcept = 0;
    virtual void PROGPU_NATIVE_COM_CALL EndUse(
        const void* target) noexcept = 0;
};

class portable_scene_path_sink final : public simplified_geometry_sink {
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
                interface_id, simplified_geometry_sink_interface_id)) {
            *value = static_cast<simplified_geometry_sink*>(this);
        } else {
            return com::no_interface;
        }
        AddRef();
        return com::ok;
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

    void PROGPU_NATIVE_COM_CALL SetFillMode(fill_mode value) noexcept override
    {
        if (closed_ || figure_started_ ||
            (value != fill_mode::alternate && value != fill_mode::winding)) {
            set_failure(com::invalid_argument);
            return;
        }
        fill_mode_ = value;
    }

    void PROGPU_NATIVE_COM_CALL SetSegmentFlags(
        path_segment value) noexcept override
    {
        constexpr std::uint32_t supported =
            static_cast<std::uint32_t>(path_segment::force_unstroked) |
            static_cast<std::uint32_t>(path_segment::force_round_line_join);
        if (closed_ ||
            (static_cast<std::uint32_t>(value) & ~supported) != 0U) {
            set_failure(com::invalid_argument);
        }
    }

    void PROGPU_NATIVE_COM_CALL BeginFigure(
        point_2f start,
        figure_begin begin) noexcept override
    {
        if (closed_ || figure_open_ || !valid_point(start) ||
            (begin != figure_begin::filled &&
                begin != figure_begin::hollow)) {
            set_failure(com::invalid_argument);
            return;
        }
        figure_started_ = true;
        figure_open_ = true;
        figure_filled_ = begin == figure_begin::filled;
        figure_start_ = start;
        current_point_ = start;
    }

    void PROGPU_NATIVE_COM_CALL AddLines(
        const point_2f* points,
        std::uint32_t point_count) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (point_count != 0U && points == nullptr)) {
            set_failure(com::invalid_argument);
            return;
        }
        for (std::uint32_t index = 0U; index < point_count; ++index) {
            if (!valid_point(points[index])) {
                set_failure(com::invalid_argument);
                return;
            }
            if (figure_filled_ &&
                !append_line(current_point_, points[index])) {
                return;
            }
            current_point_ = points[index];
        }
    }

    void PROGPU_NATIVE_COM_CALL AddBeziers(
        const bezier_segment* beziers,
        std::uint32_t bezier_count) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (bezier_count != 0U && beziers == nullptr)) {
            set_failure(com::invalid_argument);
            return;
        }
        for (std::uint32_t index = 0U; index < bezier_count; ++index) {
            const auto& bezier = beziers[index];
            if (!valid_point(bezier.point1) ||
                !valid_point(bezier.point2) ||
                !valid_point(bezier.point3)) {
                set_failure(com::invalid_argument);
                return;
            }
            if (figure_filled_ &&
                !append_cubic(current_point_, bezier)) {
                return;
            }
            current_point_ = bezier.point3;
        }
    }

    void PROGPU_NATIVE_COM_CALL EndFigure(figure_end end) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (end != figure_end::open && end != figure_end::closed)) {
            set_failure(com::invalid_argument);
            return;
        }
        if (figure_filled_ && end == figure_end::closed &&
            !same_point(current_point_, figure_start_) &&
            !append_line(current_point_, figure_start_)) {
            return;
        }
        figure_open_ = false;
        figure_filled_ = false;
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        if (closed_ || figure_open_) {
            set_failure(wrong_state);
        }
        closed_ = true;
        return failure_;
    }

    [[nodiscard]] std::span<const progpu_native_path_segment> segments()
        const noexcept
    {
        return segments_;
    }

    [[nodiscard]] std::uint32_t native_fill_rule() const noexcept
    {
        return fill_mode_ == fill_mode::alternate
            ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
            : PROGPU_NATIVE_FILL_RULE_NON_ZERO;
    }

private:
    static constexpr std::size_t maximum_segment_count = 1U << 20U;

    [[nodiscard]] static bool same_point(
        point_2f left,
        point_2f right) noexcept
    {
        return left.x == right.x && left.y == right.y;
    }

    void set_failure(com::result value) noexcept
    {
        if (com::succeeded(failure_)) {
            failure_ = value;
        }
    }

    [[nodiscard]] bool append(progpu_native_path_segment segment) noexcept
    {
        if (segments_.size() == maximum_segment_count) {
            set_failure(failure);
            return false;
        }
        try {
            segments_.push_back(segment);
            return true;
        } catch (const std::bad_alloc&) {
            set_failure(com::out_of_memory);
            return false;
        } catch (...) {
            set_failure(failure);
            return false;
        }
    }

    [[nodiscard]] bool append_line(point_2f start, point_2f end) noexcept
    {
        progpu_native_path_segment segment{};
        segment.p0 = {start.x, start.y};
        segment.p1 = {end.x, end.y};
        segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
        return append(segment);
    }

    [[nodiscard]] bool append_cubic(
        point_2f start,
        const bezier_segment& bezier) noexcept
    {
        progpu_native_path_segment segment{};
        segment.p0 = {start.x, start.y};
        segment.p1 = {bezier.point1.x, bezier.point1.y};
        segment.p2 = {bezier.point2.x, bezier.point2.y};
        segment.p3 = {bezier.point3.x, bezier.point3.y};
        segment.kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
        return append(segment);
    }

    friend class com::atomic_reference_count<portable_scene_path_sink>;
    ~portable_scene_path_sink() = default;

    com::atomic_reference_count<portable_scene_path_sink> reference_count_;
    std::vector<progpu_native_path_segment> segments_;
    point_2f figure_start_{};
    point_2f current_point_{};
    fill_mode fill_mode_ = fill_mode::alternate;
    com::result failure_ = com::ok;
    bool figure_open_ = false;
    bool figure_started_ = false;
    bool figure_filled_ = false;
    bool closed_ = false;
};

struct portable_scene_stroke_figure final {
    std::size_t segment_offset{};
    std::size_t segment_count{};
    point_2f start{};
    path_segment closing_flags = path_segment::none;
    bool closed{};
};

class portable_scene_stroke_sink final : public simplified_geometry_sink {
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
                interface_id, simplified_geometry_sink_interface_id)) {
            *value = static_cast<simplified_geometry_sink*>(this);
        } else {
            return com::no_interface;
        }
        AddRef();
        return com::ok;
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

    void PROGPU_NATIVE_COM_CALL SetFillMode(fill_mode value) noexcept override
    {
        if (closed_ || figure_open_ ||
            (value != fill_mode::alternate && value != fill_mode::winding)) {
            set_failure(com::invalid_argument);
        }
    }

    void PROGPU_NATIVE_COM_CALL SetSegmentFlags(
        path_segment value) noexcept override
    {
        constexpr std::uint32_t supported =
            static_cast<std::uint32_t>(path_segment::force_unstroked) |
            static_cast<std::uint32_t>(path_segment::force_round_line_join);
        if (closed_ ||
            (static_cast<std::uint32_t>(value) & ~supported) != 0U) {
            set_failure(com::invalid_argument);
        } else {
            current_flags_ = value;
        }
    }

    void PROGPU_NATIVE_COM_CALL BeginFigure(
        point_2f start,
        figure_begin begin) noexcept override
    {
        if (closed_ || figure_open_ || !valid_point(start) ||
            (begin != figure_begin::filled &&
                begin != figure_begin::hollow)) {
            set_failure(com::invalid_argument);
            return;
        }
        if (figures_.size() == maximum_figure_count ||
            segments_.size() == maximum_segment_count) {
            set_failure(failure);
            return;
        }
        current_figure_ = {};
        current_figure_.segment_offset = segments_.size();
        current_figure_.start = start;
        current_point_ = start;
        figure_open_ = true;
    }

    void PROGPU_NATIVE_COM_CALL AddLines(
        const point_2f* points,
        std::uint32_t point_count) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (point_count != 0U && points == nullptr) ||
            point_count > maximum_segment_count - segments_.size()) {
            set_failure(com::invalid_argument);
            return;
        }
        for (std::uint32_t index = 0U; index < point_count; ++index) {
            if (!valid_point(points[index])) {
                set_failure(com::invalid_argument);
                return;
            }
        }
        try {
            segments_.reserve(segments_.size() + point_count);
            segment_flags_.reserve(segment_flags_.size() + point_count);
            for (std::uint32_t index = 0U; index < point_count; ++index) {
                progpu_native_path_segment segment{};
                segment.p0 = {current_point_.x, current_point_.y};
                segment.p1 = {points[index].x, points[index].y};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                segments_.push_back(segment);
                segment_flags_.push_back(current_flags_);
                current_point_ = points[index];
            }
        } catch (const std::bad_alloc&) {
            set_failure(com::out_of_memory);
        } catch (...) {
            set_failure(failure);
        }
    }

    void PROGPU_NATIVE_COM_CALL AddBeziers(
        const bezier_segment* beziers,
        std::uint32_t bezier_count) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (bezier_count != 0U && beziers == nullptr) ||
            bezier_count > maximum_segment_count - segments_.size()) {
            set_failure(com::invalid_argument);
            return;
        }
        for (std::uint32_t index = 0U; index < bezier_count; ++index) {
            if (!valid_point(beziers[index].point1) ||
                !valid_point(beziers[index].point2) ||
                !valid_point(beziers[index].point3)) {
                set_failure(com::invalid_argument);
                return;
            }
        }
        try {
            segments_.reserve(segments_.size() + bezier_count);
            segment_flags_.reserve(segment_flags_.size() + bezier_count);
            for (std::uint32_t index = 0U; index < bezier_count; ++index) {
                const auto& bezier = beziers[index];
                progpu_native_path_segment segment{};
                segment.p0 = {current_point_.x, current_point_.y};
                segment.p1 = {bezier.point1.x, bezier.point1.y};
                segment.p2 = {bezier.point2.x, bezier.point2.y};
                segment.p3 = {bezier.point3.x, bezier.point3.y};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
                segments_.push_back(segment);
                segment_flags_.push_back(current_flags_);
                current_point_ = bezier.point3;
            }
        } catch (const std::bad_alloc&) {
            set_failure(com::out_of_memory);
        } catch (...) {
            set_failure(failure);
        }
    }

    void PROGPU_NATIVE_COM_CALL EndFigure(figure_end end) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (end != figure_end::open && end != figure_end::closed)) {
            set_failure(com::invalid_argument);
            return;
        }
        current_figure_.segment_count =
            segments_.size() - current_figure_.segment_offset;
        current_figure_.closing_flags = current_flags_;
        current_figure_.closed = end == figure_end::closed;
        try {
            figures_.push_back(current_figure_);
        } catch (const std::bad_alloc&) {
            set_failure(com::out_of_memory);
        } catch (...) {
            set_failure(failure);
        }
        figure_open_ = false;
    }

    com::result PROGPU_NATIVE_COM_CALL Close() noexcept override
    {
        if (closed_ || figure_open_) {
            set_failure(wrong_state);
        }
        closed_ = true;
        return failure_;
    }

    [[nodiscard]] std::span<const portable_scene_stroke_figure> figures()
        const noexcept
    {
        return figures_;
    }

    [[nodiscard]] std::span<const progpu_native_path_segment> segments()
        const noexcept
    {
        return segments_;
    }

    [[nodiscard]] std::span<const path_segment> segment_flags()
        const noexcept
    {
        return segment_flags_;
    }

private:
    static constexpr std::size_t maximum_figure_count = 1U << 20U;
    static constexpr std::size_t maximum_segment_count = 1U << 20U;

    void set_failure(com::result value) noexcept
    {
        if (com::succeeded(failure_)) {
            failure_ = value;
        }
    }

    friend class com::atomic_reference_count<portable_scene_stroke_sink>;
    ~portable_scene_stroke_sink() = default;

    com::atomic_reference_count<portable_scene_stroke_sink> reference_count_;
    std::vector<portable_scene_stroke_figure> figures_;
    std::vector<progpu_native_path_segment> segments_;
    std::vector<path_segment> segment_flags_;
    portable_scene_stroke_figure current_figure_{};
    point_2f current_point_{};
    path_segment current_flags_ = path_segment::none;
    com::result failure_ = com::ok;
    bool figure_open_ = false;
    bool closed_ = false;
};

class portable_bitmap final : public bitmap, public scene_bitmap_native {
public:
    portable_bitmap(
        factory* owner,
        size_u size,
        const bitmap_properties& properties,
        std::uint32_t row_bytes,
        std::vector<std::byte> pixels) noexcept
        : owner_(owner),
          size_(size),
          properties_(properties),
          row_bytes_(row_bytes),
          pixels_(std::move(pixels))
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
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(interface_id, bitmap_interface_id)) {
            *value = static_cast<bitmap*>(this);
        } else if (com::guid_equal(
                interface_id, scene_bitmap_native_interface_id)) {
            *value = static_cast<scene_bitmap_native*>(this);
        } else {
            return com::no_interface;
        }
        AddRef();
        return com::ok;
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

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    size_f PROGPU_NATIVE_COM_CALL GetSize() const noexcept override
    {
        return {
            static_cast<float>(size_.width) * 96.0F / properties_.dpi_x,
            static_cast<float>(size_.height) * 96.0F / properties_.dpi_y};
    }

    size_u PROGPU_NATIVE_COM_CALL GetPixelSize() const noexcept override
    {
        return size_;
    }

    pixel_format PROGPU_NATIVE_COM_CALL GetPixelFormat()
        const noexcept override
    {
        return properties_.pixel_format_value;
    }

    void PROGPU_NATIVE_COM_CALL GetDpi(
        float* dpi_x,
        float* dpi_y) const noexcept override
    {
        if (dpi_x != nullptr) {
            *dpi_x = properties_.dpi_x;
        }
        if (dpi_y != nullptr) {
            *dpi_y = properties_.dpi_y;
        }
    }

    com::result PROGPU_NATIVE_COM_CALL CopyFromBitmap(
        const point_2u* destination_point,
        bitmap* source,
        const rectangle_u* source_rectangle) noexcept override
    {
        if (source == nullptr) {
            return com::invalid_argument;
        }
        factory* raw_factory = nullptr;
        source->GetFactory(&raw_factory);
        com::pointer<factory> source_factory;
        source_factory.attach(raw_factory);
        if (source_factory.get() != owner_.get()) {
            return wrong_factory;
        }
        scene_bitmap_native* raw_native = nullptr;
        const com::result query = source->QueryInterface(
            scene_bitmap_native_interface_id,
            reinterpret_cast<void**>(&raw_native));
        com::pointer<scene_bitmap_native> native;
        native.attach(raw_native);
        if (com::failed(query) || !native) {
            return not_implemented;
        }
        bitmap_snapshot source_snapshot{};
        const com::result snapshot_result =
            native->GetSnapshot(&source_snapshot);
        if (com::failed(snapshot_result)) {
            return snapshot_result;
        }
        const rectangle_u actual_source = source_rectangle == nullptr
            ? rectangle_u{0U, 0U, source_snapshot.width,
                source_snapshot.height}
            : *source_rectangle;
        const point_2u actual_destination = destination_point == nullptr
            ? point_2u{0U, 0U}
            : *destination_point;
        if (!valid_rectangle(actual_source) ||
            actual_source.right > source_snapshot.width ||
            actual_source.bottom > source_snapshot.height ||
            actual_destination.x > size_.width ||
            actual_destination.y > size_.height) {
            return com::invalid_argument;
        }
        const std::uint32_t copy_width =
            actual_source.right - actual_source.left;
        const std::uint32_t copy_height =
            actual_source.bottom - actual_source.top;
        if (copy_width > size_.width - actual_destination.x ||
            copy_height > size_.height - actual_destination.y ||
            source_snapshot.format.format !=
                properties_.pixel_format_value.format ||
            source_snapshot.format.alpha !=
                properties_.pixel_format_value.alpha) {
            return com::invalid_argument;
        }
        const std::uint32_t compact_pitch = copy_width * 4U;
        try {
            std::vector<std::byte> copy(
                static_cast<std::size_t>(compact_pitch) * copy_height);
            const com::result copy_result = native->CopyPixels(
                &actual_source,
                properties_.pixel_format_value,
                copy.data(),
                compact_pitch);
            if (com::failed(copy_result)) {
                return copy_result;
            }
            const std::lock_guard lock(mutex_);
            if (generation_ == std::numeric_limits<std::uint64_t>::max()) {
                return failure;
            }
            for (std::uint32_t row = 0U; row < copy_height; ++row) {
                std::memcpy(
                    pixels_.data() +
                        static_cast<std::size_t>(actual_destination.y + row) *
                            row_bytes_ +
                        static_cast<std::size_t>(actual_destination.x) * 4U,
                    copy.data() + static_cast<std::size_t>(row) * compact_pitch,
                    compact_pitch);
            }
            ++generation_;
            return com::ok;
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
    }

    com::result PROGPU_NATIVE_COM_CALL CopyFromRenderTarget(
        const point_2u*, render_target*, const rectangle_u*) noexcept override
    {
        return not_implemented;
    }

    com::result PROGPU_NATIVE_COM_CALL CopyFromMemory(
        const rectangle_u* destination_rectangle,
        const void* source_data,
        std::uint32_t pitch) noexcept override
    {
        if (source_data == nullptr) {
            return com::pointer_error;
        }
        const rectangle_u rectangle = destination_rectangle == nullptr
            ? rectangle_u{0U, 0U, size_.width, size_.height}
            : *destination_rectangle;
        if (!valid_rectangle(rectangle) || rectangle.right > size_.width ||
            rectangle.bottom > size_.height) {
            return com::invalid_argument;
        }
        const std::uint32_t width = rectangle.right - rectangle.left;
        const std::uint32_t height = rectangle.bottom - rectangle.top;
        const std::uint32_t copy_bytes = width * 4U;
        if (pitch < copy_bytes) {
            return com::invalid_argument;
        }
        const std::lock_guard lock(mutex_);
        if (generation_ == std::numeric_limits<std::uint64_t>::max()) {
            return failure;
        }
        const auto* source = static_cast<const std::byte*>(source_data);
        for (std::uint32_t row = 0U; row < height; ++row) {
            std::memcpy(
                pixels_.data() +
                    static_cast<std::size_t>(rectangle.top + row) * row_bytes_ +
                    static_cast<std::size_t>(rectangle.left) * 4U,
                source + static_cast<std::size_t>(row) * pitch,
                copy_bytes);
        }
        ++generation_;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL GetSnapshot(
        bitmap_snapshot* snapshot) const noexcept override
    {
        if (snapshot == nullptr) {
            return com::pointer_error;
        }
        const std::lock_guard lock(mutex_);
        *snapshot = make_snapshot();
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL AddToScene(
        semantic_scene_builder* builder,
        std::uint32_t* resource_index,
        bitmap_snapshot* snapshot) const noexcept override
    {
        if (builder == nullptr || resource_index == nullptr ||
            snapshot == nullptr) {
            return com::pointer_error;
        }
        const std::lock_guard lock(mutex_);
        const bool bgra = properties_.pixel_format_value.format ==
            dxgi_format_b8g8r8a8_unorm;
        const bool added = bgra
            ? builder->add_bgra8_image(
                size_.width, size_.height, row_bytes_, pixels_,
                *resource_index)
            : builder->add_rgba8_image(
                size_.width, size_.height, row_bytes_, pixels_,
                *resource_index);
        if (!added) {
            return builder->last_error() == scene_build_error::out_of_memory
                ? com::out_of_memory
                : failure;
        }
        *snapshot = make_snapshot();
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CopyPixels(
        const rectangle_u* source_rectangle,
        pixel_format expected_format,
        void* destination,
        std::uint32_t destination_pitch) const noexcept override
    {
        if (source_rectangle == nullptr || destination == nullptr) {
            return com::pointer_error;
        }
        if (!valid_rectangle(*source_rectangle) ||
            source_rectangle->right > size_.width ||
            source_rectangle->bottom > size_.height ||
            expected_format.format != properties_.pixel_format_value.format ||
            expected_format.alpha != properties_.pixel_format_value.alpha) {
            return com::invalid_argument;
        }
        const std::uint32_t width =
            source_rectangle->right - source_rectangle->left;
        const std::uint32_t height =
            source_rectangle->bottom - source_rectangle->top;
        const std::uint32_t copy_bytes = width * 4U;
        if (destination_pitch < copy_bytes) {
            return com::invalid_argument;
        }
        const std::lock_guard lock(mutex_);
        auto* output = static_cast<std::byte*>(destination);
        for (std::uint32_t row = 0U; row < height; ++row) {
            std::memcpy(
                output + static_cast<std::size_t>(row) * destination_pitch,
                pixels_.data() +
                    static_cast<std::size_t>(source_rectangle->top + row) *
                        row_bytes_ +
                    static_cast<std::size_t>(source_rectangle->left) * 4U,
                copy_bytes);
        }
        return com::ok;
    }

private:
    [[nodiscard]] bitmap_snapshot make_snapshot() const noexcept
    {
        return {
            size_.width,
            size_.height,
            row_bytes_,
            properties_.pixel_format_value,
            properties_.dpi_x,
            properties_.dpi_y,
            generation_};
    }

    friend class com::atomic_reference_count<portable_bitmap>;
    ~portable_bitmap() = default;

    com::atomic_reference_count<portable_bitmap> reference_count_;
    com::pointer<factory> owner_;
    mutable std::mutex mutex_;
    size_u size_{};
    bitmap_properties properties_{};
    std::uint32_t row_bytes_ = 0U;
    std::vector<std::byte> pixels_;
    std::uint64_t generation_ = 1U;
};

class portable_bitmap_brush final :
    public bitmap_brush,
    public scene_bitmap_brush_native {
public:
    portable_bitmap_brush(
        factory* owner,
        bitmap* source,
        const bitmap_brush_properties& bitmap_properties_value,
        const brush_properties& properties) noexcept
        : owner_(owner),
          source_(source),
          extend_x_(bitmap_properties_value.extend_mode_x),
          extend_y_(bitmap_properties_value.extend_mode_y),
          interpolation_(bitmap_properties_value.interpolation_mode),
          opacity_(properties.opacity),
          transform_(properties.transform)
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
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(interface_id, brush_interface_id) ||
            com::guid_equal(interface_id, bitmap_brush_interface_id)) {
            *value = static_cast<bitmap_brush*>(this);
        } else if (com::guid_equal(
                interface_id, scene_bitmap_brush_native_interface_id)) {
            *value = static_cast<scene_bitmap_brush_native*>(this);
        } else {
            return com::no_interface;
        }
        AddRef();
        return com::ok;
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

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    void PROGPU_NATIVE_COM_CALL SetOpacity(float opacity) noexcept override
    {
        if (valid_opacity(opacity)) {
            const std::lock_guard lock(mutex_);
            opacity_ = opacity;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetTransform(
        const matrix_3x2_f* transform) noexcept override
    {
        if (transform != nullptr && core::valid_transform(transform)) {
            const std::lock_guard lock(mutex_);
            transform_ = *transform;
        }
    }

    float PROGPU_NATIVE_COM_CALL GetOpacity() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return opacity_;
    }

    void PROGPU_NATIVE_COM_CALL GetTransform(
        matrix_3x2_f* transform) const noexcept override
    {
        if (transform != nullptr) {
            const std::lock_guard lock(mutex_);
            *transform = transform_;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetExtendModeX(
        extend_mode extend) noexcept override
    {
        if (valid_extend_mode(extend)) {
            const std::lock_guard lock(mutex_);
            extend_x_ = extend;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetExtendModeY(
        extend_mode extend) noexcept override
    {
        if (valid_extend_mode(extend)) {
            const std::lock_guard lock(mutex_);
            extend_y_ = extend;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetInterpolationMode(
        bitmap_interpolation_mode interpolation) noexcept override
    {
        if (valid_bitmap_interpolation_mode(interpolation)) {
            const std::lock_guard lock(mutex_);
            interpolation_ = interpolation;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetBitmap(bitmap* value) noexcept override
    {
        if (value != nullptr) {
            factory* raw_factory = nullptr;
            value->GetFactory(&raw_factory);
            com::pointer<factory> value_factory;
            value_factory.attach(raw_factory);
            if (value_factory.get() != owner_.get()) {
                return;
            }
        }
        const std::lock_guard lock(mutex_);
        source_ = com::pointer<bitmap>(value);
    }

    extend_mode PROGPU_NATIVE_COM_CALL GetExtendModeX()
        const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return extend_x_;
    }

    extend_mode PROGPU_NATIVE_COM_CALL GetExtendModeY()
        const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return extend_y_;
    }

    bitmap_interpolation_mode PROGPU_NATIVE_COM_CALL GetInterpolationMode()
        const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return interpolation_;
    }

    void PROGPU_NATIVE_COM_CALL GetBitmap(bitmap** value)
        const noexcept override
    {
        if (value == nullptr) {
            return;
        }
        const std::lock_guard lock(mutex_);
        *value = source_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    com::result PROGPU_NATIVE_COM_CALL GetSceneSnapshot(
        bitmap** source,
        extend_mode* extend_x,
        extend_mode* extend_y,
        bitmap_interpolation_mode* interpolation,
        float* opacity,
        matrix_3x2_f* transform) const noexcept override
    {
        if (source == nullptr || extend_x == nullptr || extend_y == nullptr ||
            interpolation == nullptr || opacity == nullptr ||
            transform == nullptr) {
            return com::pointer_error;
        }
        const std::lock_guard lock(mutex_);
        *source = source_.get();
        if (*source != nullptr) {
            (*source)->AddRef();
        }
        *extend_x = extend_x_;
        *extend_y = extend_y_;
        *interpolation = interpolation_;
        *opacity = opacity_;
        *transform = transform_;
        return com::ok;
    }

private:
    friend class com::atomic_reference_count<portable_bitmap_brush>;
    ~portable_bitmap_brush() = default;

    com::atomic_reference_count<portable_bitmap_brush> reference_count_;
    com::pointer<factory> owner_;
    mutable std::mutex mutex_;
    com::pointer<bitmap> source_;
    extend_mode extend_x_ = extend_mode::clamp;
    extend_mode extend_y_ = extend_mode::clamp;
    bitmap_interpolation_mode interpolation_ =
        bitmap_interpolation_mode::linear;
    float opacity_ = 1.0F;
    matrix_3x2_f transform_ = identity_transform;
};

class portable_gradient_stop_collection final :
    public gradient_stop_collection {
public:
    portable_gradient_stop_collection(
        factory* owner,
        std::vector<gradient_stop> stops,
        gamma interpolation_gamma,
        extend_mode extend) noexcept
        : owner_(owner),
          stops_(std::move(stops)),
          interpolation_gamma_(interpolation_gamma),
          extend_(extend)
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
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(
                interface_id, gradient_stop_collection_interface_id)) {
            *value = static_cast<gradient_stop_collection*>(this);
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

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    std::uint32_t PROGPU_NATIVE_COM_CALL GetGradientStopCount()
        const noexcept override
    {
        return static_cast<std::uint32_t>(stops_.size());
    }

    void PROGPU_NATIVE_COM_CALL GetGradientStops(
        gradient_stop* gradient_stops,
        std::uint32_t gradient_stop_count) const noexcept override
    {
        if (gradient_stops == nullptr || gradient_stop_count == 0U) {
            return;
        }
        const std::size_t copy_count = std::min<std::size_t>(
            gradient_stop_count, stops_.size());
        std::copy_n(stops_.begin(), copy_count, gradient_stops);
    }

    gamma PROGPU_NATIVE_COM_CALL GetColorInterpolationGamma()
        const noexcept override
    {
        return interpolation_gamma_;
    }

    extend_mode PROGPU_NATIVE_COM_CALL GetExtendMode()
        const noexcept override
    {
        return extend_;
    }

private:
    friend class com::atomic_reference_count<
        portable_gradient_stop_collection>;
    ~portable_gradient_stop_collection() = default;

    com::atomic_reference_count<portable_gradient_stop_collection>
        reference_count_;
    com::pointer<factory> owner_;
    std::vector<gradient_stop> stops_;
    gamma interpolation_gamma_ = gamma::gamma_2_2;
    extend_mode extend_ = extend_mode::clamp;
};

class portable_linear_gradient_brush final :
    public linear_gradient_brush {
public:
    portable_linear_gradient_brush(
        factory* owner,
        const linear_gradient_brush_properties& gradient_properties,
        const brush_properties& properties,
        gradient_stop_collection* stops) noexcept
        : owner_(owner),
          stops_(stops),
          start_(gradient_properties.start_point),
          end_(gradient_properties.end_point),
          opacity_(properties.opacity),
          transform_(properties.transform)
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
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(interface_id, brush_interface_id) ||
            com::guid_equal(
                interface_id, linear_gradient_brush_interface_id)) {
            *value = static_cast<linear_gradient_brush*>(this);
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

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    void PROGPU_NATIVE_COM_CALL SetOpacity(float opacity) noexcept override
    {
        if (valid_opacity(opacity)) {
            const std::lock_guard lock(mutex_);
            opacity_ = opacity;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetTransform(
        const matrix_3x2_f* transform) noexcept override
    {
        if (transform != nullptr && core::valid_transform(transform)) {
            const std::lock_guard lock(mutex_);
            transform_ = *transform;
        }
    }

    float PROGPU_NATIVE_COM_CALL GetOpacity() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return opacity_;
    }

    void PROGPU_NATIVE_COM_CALL GetTransform(
        matrix_3x2_f* transform) const noexcept override
    {
        if (transform != nullptr) {
            const std::lock_guard lock(mutex_);
            *transform = transform_;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetStartPoint(point_2f start_point)
        noexcept override
    {
        if (valid_point(start_point)) {
            const std::lock_guard lock(mutex_);
            start_ = start_point;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetEndPoint(point_2f end_point)
        noexcept override
    {
        if (valid_point(end_point)) {
            const std::lock_guard lock(mutex_);
            end_ = end_point;
        }
    }

    point_2f PROGPU_NATIVE_COM_CALL GetStartPoint()
        const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return start_;
    }

    point_2f PROGPU_NATIVE_COM_CALL GetEndPoint() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return end_;
    }

    void PROGPU_NATIVE_COM_CALL GetGradientStopCollection(
        gradient_stop_collection** collection) const noexcept override
    {
        if (collection == nullptr) {
            return;
        }
        *collection = stops_.get();
        if (*collection != nullptr) {
            (*collection)->AddRef();
        }
    }

private:
    friend class com::atomic_reference_count<portable_linear_gradient_brush>;
    ~portable_linear_gradient_brush() = default;

    com::atomic_reference_count<portable_linear_gradient_brush>
        reference_count_;
    com::pointer<factory> owner_;
    com::pointer<gradient_stop_collection> stops_;
    mutable std::mutex mutex_;
    point_2f start_{};
    point_2f end_{};
    float opacity_ = 1.0F;
    matrix_3x2_f transform_ = identity_transform;
};

class portable_radial_gradient_brush final :
    public radial_gradient_brush {
public:
    portable_radial_gradient_brush(
        factory* owner,
        const radial_gradient_brush_properties& gradient_properties,
        const brush_properties& properties,
        gradient_stop_collection* stops) noexcept
        : owner_(owner),
          stops_(stops),
          center_(gradient_properties.center),
          origin_offset_(gradient_properties.gradient_origin_offset),
          radius_x_(gradient_properties.radius_x),
          radius_y_(gradient_properties.radius_y),
          opacity_(properties.opacity),
          transform_(properties.transform)
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
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(interface_id, brush_interface_id) ||
            com::guid_equal(
                interface_id, radial_gradient_brush_interface_id)) {
            *value = static_cast<radial_gradient_brush*>(this);
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

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    void PROGPU_NATIVE_COM_CALL SetOpacity(float opacity) noexcept override
    {
        if (valid_opacity(opacity)) {
            const std::lock_guard lock(mutex_);
            opacity_ = opacity;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetTransform(
        const matrix_3x2_f* transform) noexcept override
    {
        if (transform != nullptr && core::valid_transform(transform)) {
            const std::lock_guard lock(mutex_);
            transform_ = *transform;
        }
    }

    float PROGPU_NATIVE_COM_CALL GetOpacity() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return opacity_;
    }

    void PROGPU_NATIVE_COM_CALL GetTransform(
        matrix_3x2_f* transform) const noexcept override
    {
        if (transform != nullptr) {
            const std::lock_guard lock(mutex_);
            *transform = transform_;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetCenter(point_2f center) noexcept override
    {
        if (valid_point(center)) {
            const std::lock_guard lock(mutex_);
            center_ = center;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetGradientOriginOffset(
        point_2f gradient_origin_offset) noexcept override
    {
        if (valid_point(gradient_origin_offset)) {
            const std::lock_guard lock(mutex_);
            origin_offset_ = gradient_origin_offset;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetRadiusX(float radius_x) noexcept override
    {
        if (std::isfinite(radius_x) && radius_x >= 0.0F) {
            const std::lock_guard lock(mutex_);
            radius_x_ = radius_x;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetRadiusY(float radius_y) noexcept override
    {
        if (std::isfinite(radius_y) && radius_y >= 0.0F) {
            const std::lock_guard lock(mutex_);
            radius_y_ = radius_y;
        }
    }

    point_2f PROGPU_NATIVE_COM_CALL GetCenter() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return center_;
    }

    point_2f PROGPU_NATIVE_COM_CALL GetGradientOriginOffset()
        const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return origin_offset_;
    }

    float PROGPU_NATIVE_COM_CALL GetRadiusX() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return radius_x_;
    }

    float PROGPU_NATIVE_COM_CALL GetRadiusY() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return radius_y_;
    }

    void PROGPU_NATIVE_COM_CALL GetGradientStopCollection(
        gradient_stop_collection** collection) const noexcept override
    {
        if (collection == nullptr) {
            return;
        }
        *collection = stops_.get();
        if (*collection != nullptr) {
            (*collection)->AddRef();
        }
    }

private:
    friend class com::atomic_reference_count<portable_radial_gradient_brush>;
    ~portable_radial_gradient_brush() = default;

    com::atomic_reference_count<portable_radial_gradient_brush>
        reference_count_;
    com::pointer<factory> owner_;
    com::pointer<gradient_stop_collection> stops_;
    mutable std::mutex mutex_;
    point_2f center_{};
    point_2f origin_offset_{};
    float radius_x_ = 0.0F;
    float radius_y_ = 0.0F;
    float opacity_ = 1.0F;
    matrix_3x2_f transform_ = identity_transform;
};

class portable_layer final : public layer, public scene_layer_native {
public:
    portable_layer(factory* owner, size_f size) noexcept
        : owner_(owner), size_(size)
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
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(interface_id, layer_interface_id)) {
            *value = static_cast<layer*>(this);
        } else if (com::guid_equal(
                interface_id, scene_layer_native_interface_id)) {
            *value = static_cast<scene_layer_native*>(this);
        } else {
            return com::no_interface;
        }
        AddRef();
        return com::ok;
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

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    size_f PROGPU_NATIVE_COM_CALL GetSize() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return size_;
    }

    com::result PROGPU_NATIVE_COM_CALL BeginUse(
        const void* target,
        size_f required_size) noexcept override
    {
        if (target == nullptr || !std::isfinite(required_size.width) ||
            !std::isfinite(required_size.height) ||
            required_size.width < 0.0F || required_size.height < 0.0F) {
            return com::invalid_argument;
        }
        const std::lock_guard lock(mutex_);
        if (target_ != nullptr) {
            return wrong_state;
        }
        size_.width = std::max(size_.width, required_size.width);
        size_.height = std::max(size_.height, required_size.height);
        target_ = target;
        return com::ok;
    }

    void PROGPU_NATIVE_COM_CALL EndUse(
        const void* target) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (target_ == target) {
            target_ = nullptr;
        }
    }

private:
    friend class com::atomic_reference_count<portable_layer>;
    ~portable_layer() = default;

    com::atomic_reference_count<portable_layer> reference_count_;
    com::pointer<factory> owner_;
    mutable std::mutex mutex_;
    size_f size_{};
    const void* target_ = nullptr;
};

class portable_scene_render_target final :
    public render_target,
    public scene_render_target_native {
public:
    portable_scene_render_target(
        factory* owner,
        const scene_render_target_properties& properties)
        : owner_(owner),
          builder_(properties.scene_id, properties.generation),
          scene_id_(properties.scene_id),
          generation_(properties.generation),
          pixel_width_(properties.pixel_width),
          pixel_height_(properties.pixel_height),
          dpi_x_(properties.dpi_x),
          dpi_y_(properties.dpi_y)
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
            com::guid_equal(interface_id, resource_interface_id) ||
            com::guid_equal(interface_id, render_target_interface_id)) {
            *value = static_cast<render_target*>(this);
        } else if (com::guid_equal(
                interface_id, scene_render_target_native_interface_id)) {
            *value = static_cast<scene_render_target_native*>(this);
        } else {
            return com::no_interface;
        }
        AddRef();
        return com::ok;
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

    void PROGPU_NATIVE_COM_CALL GetFactory(factory** value) const
        noexcept override
    {
        if (value == nullptr) {
            return;
        }
        *value = owner_.get();
        if (*value != nullptr) {
            (*value)->AddRef();
        }
    }

    com::result PROGPU_NATIVE_COM_CALL CreateBitmap(
        size_u size,
        const void* source_data,
        std::uint32_t pitch,
        const bitmap_properties* properties,
        bitmap** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (properties == nullptr || size.width == 0U || size.height == 0U ||
            size.width > 16384U || size.height > 16384U) {
            return com::invalid_argument;
        }
        bitmap_properties actual = *properties;
        if (actual.pixel_format_value.format == 0U) {
            actual.pixel_format_value.format =
                dxgi_format_b8g8r8a8_unorm;
        }
        if (actual.pixel_format_value.alpha == alpha_mode::unknown) {
            actual.pixel_format_value.alpha = alpha_mode::premultiplied;
        }
        if ((actual.pixel_format_value.format !=
                dxgi_format_r8g8b8a8_unorm &&
                actual.pixel_format_value.format !=
                    dxgi_format_b8g8r8a8_unorm) ||
            actual.pixel_format_value.alpha != alpha_mode::premultiplied) {
            return not_implemented;
        }
        if (actual.dpi_x == 0.0F && actual.dpi_y == 0.0F) {
            const std::lock_guard lock(mutex_);
            actual.dpi_x = dpi_x_;
            actual.dpi_y = dpi_y_;
        } else if (!valid_dpi(actual.dpi_x, actual.dpi_y)) {
            return com::invalid_argument;
        }
        const std::uint64_t minimum_row_bytes =
            static_cast<std::uint64_t>(size.width) * 4U;
        const std::uint32_t stored_pitch = source_data == nullptr
            ? static_cast<std::uint32_t>(minimum_row_bytes)
            : pitch;
        const std::uint64_t required_bytes =
            static_cast<std::uint64_t>(stored_pitch) * (size.height - 1U) +
            minimum_row_bytes;
        if ((source_data != nullptr && pitch < minimum_row_bytes) ||
            required_bytes > PROGPU_NATIVE_SCENE_MAX_STREAM_BYTES ||
            required_bytes > std::numeric_limits<std::size_t>::max()) {
            return com::invalid_argument;
        }
        try {
            std::vector<std::byte> pixels(
                static_cast<std::size_t>(required_bytes));
            if (source_data != nullptr) {
                std::memcpy(pixels.data(), source_data, pixels.size());
            }
            auto* created = new (std::nothrow) portable_bitmap(
                owner_.get(), size, actual, stored_pitch, std::move(pixels));
            if (created == nullptr) {
                return com::out_of_memory;
            }
            *value = created;
            return com::ok;
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
    }

    com::result PROGPU_NATIVE_COM_CALL CreateBitmapFromWicBitmap(
        com::unknown*,
        const bitmap_properties*,
        bitmap** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateSharedBitmap(
        com::guid_ref,
        void*,
        const bitmap_properties*,
        bitmap** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateBitmapBrush(
        bitmap* source,
        const bitmap_brush_properties* bitmap_properties_value,
        const brush_properties* properties,
        bitmap_brush** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        const bitmap_brush_properties actual_bitmap_properties =
            bitmap_properties_value == nullptr
            ? bitmap_brush_properties{
                extend_mode::clamp,
                extend_mode::clamp,
                bitmap_interpolation_mode::linear}
            : *bitmap_properties_value;
        const brush_properties actual_properties = properties == nullptr
            ? brush_properties{1.0F, identity_transform}
            : *properties;
        if (!valid_extend_mode(actual_bitmap_properties.extend_mode_x) ||
            !valid_extend_mode(actual_bitmap_properties.extend_mode_y) ||
            !valid_bitmap_interpolation_mode(
                actual_bitmap_properties.interpolation_mode) ||
            !valid_brush_properties(actual_properties)) {
            return com::invalid_argument;
        }
        if (source != nullptr) {
            factory* raw_factory = nullptr;
            source->GetFactory(&raw_factory);
            com::pointer<factory> bitmap_factory;
            bitmap_factory.attach(raw_factory);
            if (bitmap_factory.get() != owner_.get()) {
                return wrong_factory;
            }
        }
        auto* created = new (std::nothrow) portable_bitmap_brush(
            owner_.get(),
            source,
            actual_bitmap_properties,
            actual_properties);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        *value = created;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CreateSolidColorBrush(
        const color_f* color,
        const brush_properties* properties,
        solid_color_brush** value) noexcept override
    {
        com::pointer<factory_native> resource_factory;
        const com::result query = owner_.as(
            factory_native_interface_id, resource_factory);
        return com::failed(query)
            ? query
            : resource_factory->CreateSolidColorBrush(color, properties, value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateGradientStopCollection(
        const gradient_stop* gradient_stops,
        std::uint32_t gradient_stop_count,
        gamma color_interpolation_gamma,
        extend_mode extend_mode_value,
        gradient_stop_collection** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        if (gradient_stops == nullptr || gradient_stop_count == 0U ||
            gradient_stop_count > PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS ||
            (color_interpolation_gamma != gamma::gamma_2_2 &&
                color_interpolation_gamma != gamma::gamma_1_0) ||
            (extend_mode_value != extend_mode::clamp &&
                extend_mode_value != extend_mode::wrap &&
                extend_mode_value != extend_mode::mirror)) {
            return com::invalid_argument;
        }
        float previous = -std::numeric_limits<float>::infinity();
        for (std::uint32_t index = 0U;
             index < gradient_stop_count;
             ++index) {
            const gradient_stop& stop = gradient_stops[index];
            if (!std::isfinite(stop.position) || stop.position < 0.0F ||
                stop.position > 1.0F || stop.position < previous ||
                !valid_color(stop.color)) {
                return com::invalid_argument;
            }
            previous = stop.position;
        }
        try {
            std::vector<gradient_stop> stops(
                gradient_stops, gradient_stops + gradient_stop_count);
            auto* created = new (std::nothrow)
                portable_gradient_stop_collection(
                    owner_.get(), std::move(stops),
                    color_interpolation_gamma, extend_mode_value);
            if (created == nullptr) {
                return com::out_of_memory;
            }
            *value = created;
            return com::ok;
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
    }

    com::result PROGPU_NATIVE_COM_CALL CreateLinearGradientBrush(
        const linear_gradient_brush_properties* gradient_properties,
        const brush_properties* properties,
        gradient_stop_collection* stops,
        linear_gradient_brush** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        const brush_properties actual_properties = properties == nullptr
            ? brush_properties{1.0F, identity_transform}
            : *properties;
        if (gradient_properties == nullptr || stops == nullptr ||
            !valid_point(gradient_properties->start_point) ||
            !valid_point(gradient_properties->end_point) ||
            !valid_brush_properties(actual_properties)) {
            return com::invalid_argument;
        }
        factory* raw_factory = nullptr;
        stops->GetFactory(&raw_factory);
        com::pointer<factory> stop_factory;
        stop_factory.attach(raw_factory);
        if (stop_factory.get() != owner_.get()) {
            return wrong_factory;
        }
        auto* created = new (std::nothrow) portable_linear_gradient_brush(
            owner_.get(), *gradient_properties, actual_properties, stops);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        *value = created;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CreateRadialGradientBrush(
        const radial_gradient_brush_properties* gradient_properties,
        const brush_properties* properties,
        gradient_stop_collection* stops,
        radial_gradient_brush** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        const brush_properties actual_properties = properties == nullptr
            ? brush_properties{1.0F, identity_transform}
            : *properties;
        if (gradient_properties == nullptr || stops == nullptr ||
            !valid_point(gradient_properties->center) ||
            !valid_point(gradient_properties->gradient_origin_offset) ||
            !std::isfinite(gradient_properties->radius_x) ||
            !std::isfinite(gradient_properties->radius_y) ||
            gradient_properties->radius_x < 0.0F ||
            gradient_properties->radius_y < 0.0F ||
            (gradient_properties->radius_x == 0.0F &&
                gradient_properties->radius_y == 0.0F) ||
            !valid_brush_properties(actual_properties)) {
            return com::invalid_argument;
        }
        factory* raw_factory = nullptr;
        stops->GetFactory(&raw_factory);
        com::pointer<factory> stop_factory;
        stop_factory.attach(raw_factory);
        if (stop_factory.get() != owner_.get()) {
            return wrong_factory;
        }
        auto* created = new (std::nothrow) portable_radial_gradient_brush(
            owner_.get(), *gradient_properties, actual_properties, stops);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        *value = created;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CreateCompatibleRenderTarget(
        const size_f*,
        const size_u*,
        const pixel_format*,
        compatible_render_target_options,
        bitmap_render_target** value) noexcept override
    {
        return unsupported_output(value);
    }

    com::result PROGPU_NATIVE_COM_CALL CreateLayer(
        const size_f* requested_size,
        layer** value) noexcept override
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        const size_f size = requested_size == nullptr
            ? size_f{}
            : *requested_size;
        if (!std::isfinite(size.width) || !std::isfinite(size.height) ||
            size.width < 0.0F || size.height < 0.0F) {
            return com::invalid_argument;
        }
        auto* created = new (std::nothrow) portable_layer(
            owner_.get(), size);
        if (created == nullptr) {
            return com::out_of_memory;
        }
        *value = created;
        return com::ok;
    }

    com::result PROGPU_NATIVE_COM_CALL CreateMesh(mesh** value)
        noexcept override
    {
        return unsupported_output(value);
    }

    void PROGPU_NATIVE_COM_CALL DrawLine(
        point_2f point0,
        point_2f point1,
        brush* brush_value,
        float stroke_width,
        stroke_style* style) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        if (style != nullptr) {
            latch(not_implemented);
            return;
        }
        if (!valid_point(point0) || !valid_point(point1) ||
            !std::isfinite(stroke_width) || stroke_width <= 0.0F) {
            latch(com::invalid_argument);
            return;
        }
        progpu_native_geometry_primitive primitive{};
        primitive.kind = PROGPU_NATIVE_GEOMETRY_LINE;
        primitive.flags = primitive_flags();
        primitive.p0 = {point0.x, point0.y};
        primitive.p1 = {point1.x, point1.y};
        primitive.stroke_thickness = stroke_width;
        primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
        primitive.transform = native_transform();
        const float radius = stroke_width * 0.5F;
        const rectangle_f local_bounds{
            std::min(point0.x, point1.x) - radius,
            std::min(point0.y, point1.y) - radius,
            std::max(point0.x, point1.x) + radius,
            std::max(point0.y, point1.y) + radius};
        const bitmap_brush_draw_result bitmap_result =
            draw_bitmap_brush_geometry(
                brush_value,
                std::span<const progpu_native_geometry_primitive>(
                    &primitive, 1U),
                local_bounds);
        if (bitmap_result != bitmap_brush_draw_result::not_bitmap) {
            return;
        }
        std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!add_brush(brush_value, brush_index)) {
            return;
        }
        const progpu_native_image_rect bounds = transformed_bounds(local_bounds);
        if (!builder_.draw_geometry(
                std::span<const progpu_native_geometry_primitive>(
                    &primitive, 1U),
                std::span<const std::uint32_t>(&brush_index, 1U),
                bounds)) {
            latch(builder_failure());
            return;
        }
        ++draw_count_;
    }

    void PROGPU_NATIVE_COM_CALL DrawRectangle(
        const rectangle_f* rectangle,
        brush* brush_value,
        float stroke_width,
        stroke_style* style) noexcept override
    {
        draw_analytic_rectangle(
            rectangle, brush_value, stroke_width, style, false);
    }

    void PROGPU_NATIVE_COM_CALL FillRectangle(
        const rectangle_f* rectangle,
        brush* brush_value) noexcept override
    {
        draw_analytic_rectangle(rectangle, brush_value, 0.0F, nullptr, true);
    }

    void PROGPU_NATIVE_COM_CALL DrawRoundedRectangle(
        const rounded_rectangle* rectangle,
        brush* brush_value,
        float stroke_width,
        stroke_style* style) noexcept override
    {
        draw_rounded_rectangle(
            rectangle, brush_value, stroke_width, style, false);
    }

    void PROGPU_NATIVE_COM_CALL FillRoundedRectangle(
        const rounded_rectangle* rectangle,
        brush* brush_value) noexcept override
    {
        draw_rounded_rectangle(rectangle, brush_value, 0.0F, nullptr, true);
    }

    void PROGPU_NATIVE_COM_CALL DrawEllipse(
        const ellipse* ellipse_value,
        brush* brush_value,
        float stroke_width,
        stroke_style* style) noexcept override
    {
        draw_ellipse(ellipse_value, brush_value, stroke_width, style, false);
    }

    void PROGPU_NATIVE_COM_CALL FillEllipse(
        const ellipse* ellipse_value,
        brush* brush_value) noexcept override
    {
        draw_ellipse(ellipse_value, brush_value, 0.0F, nullptr, true);
    }

    void PROGPU_NATIVE_COM_CALL DrawGeometry(
        geometry* geometry_value,
        brush* brush_value,
        float stroke_width,
        stroke_style* style) noexcept override
    {
        draw_stroked_geometry(
            geometry_value, brush_value, stroke_width, style);
    }

    void PROGPU_NATIVE_COM_CALL FillGeometry(
        geometry* geometry_value,
        brush* brush_value,
        brush* opacity_brush) noexcept override
    {
        draw_filled_geometry(geometry_value, brush_value, opacity_brush);
    }

    void PROGPU_NATIVE_COM_CALL FillMesh(mesh*, brush*) noexcept override
    {
        unsupported_draw();
    }

    void PROGPU_NATIVE_COM_CALL FillOpacityMask(
        bitmap*, brush*, opacity_mask_content, const rectangle_f*,
        const rectangle_f*) noexcept override
    {
        unsupported_draw();
    }

    void PROGPU_NATIVE_COM_CALL DrawBitmap(
        bitmap* bitmap_value,
        const rectangle_f* destination,
        float opacity,
        bitmap_interpolation_mode interpolation,
        const rectangle_f* source) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        if (bitmap_value == nullptr || !valid_opacity(opacity) ||
            (interpolation != bitmap_interpolation_mode::nearest_neighbor &&
                interpolation != bitmap_interpolation_mode::linear)) {
            latch(com::invalid_argument);
            return;
        }
        factory* raw_factory = nullptr;
        bitmap_value->GetFactory(&raw_factory);
        com::pointer<factory> bitmap_factory;
        bitmap_factory.attach(raw_factory);
        if (bitmap_factory.get() != owner_.get()) {
            latch(wrong_factory);
            return;
        }
        scene_bitmap_native* raw_native = nullptr;
        const com::result query = bitmap_value->QueryInterface(
            scene_bitmap_native_interface_id,
            reinterpret_cast<void**>(&raw_native));
        com::pointer<scene_bitmap_native> native;
        native.attach(raw_native);
        if (com::failed(query) || !native) {
            latch(not_implemented);
            return;
        }
        std::uint32_t resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        bitmap_snapshot snapshot{};
        if (!add_bitmap_resource(native.get(), resource_index, snapshot)) {
            return;
        }
        const rectangle_f bitmap_dips{
            0.0F,
            0.0F,
            static_cast<float>(snapshot.width) * 96.0F / snapshot.dpi_x,
            static_cast<float>(snapshot.height) * 96.0F / snapshot.dpi_y};
        const rectangle_f destination_rectangle = destination == nullptr
            ? bitmap_dips
            : *destination;
        const rectangle_f source_rectangle = source == nullptr
            ? bitmap_dips
            : *source;
        if (!valid_rectangle(destination_rectangle) ||
            !valid_rectangle(source_rectangle) ||
            source_rectangle.left < 0.0F || source_rectangle.top < 0.0F ||
            source_rectangle.right > bitmap_dips.right ||
            source_rectangle.bottom > bitmap_dips.bottom) {
            latch(com::invalid_argument);
            return;
        }
        progpu_native_scene_image_draw image{};
        image.image_width = snapshot.width;
        image.image_height = snapshot.height;
        image.row_bytes = snapshot.row_bytes;
        image.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED;
        image.sampling = interpolation ==
                bitmap_interpolation_mode::nearest_neighbor
            ? PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
            : PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
        image.max_anisotropy = 1U;
        const float pixels_per_dip_x = snapshot.dpi_x / 96.0F;
        const float pixels_per_dip_y = snapshot.dpi_y / 96.0F;
        image.source_rect = {
            source_rectangle.left * pixels_per_dip_x,
            source_rectangle.top * pixels_per_dip_y,
            (source_rectangle.right - source_rectangle.left) *
                pixels_per_dip_x,
            (source_rectangle.bottom - source_rectangle.top) *
                pixels_per_dip_y};
        image.destination_rect = {
            destination_rectangle.left,
            destination_rectangle.top,
            destination_rectangle.right - destination_rectangle.left,
            destination_rectangle.bottom - destination_rectangle.top};
        image.transform = native_transform();
        image.opacity = opacity;
        const progpu_native_image_rect bounds =
            transformed_bounds(destination_rectangle);
        if (com::failed(failure_)) {
            return;
        }
        if (!builder_.draw_image(resource_index, image, bounds)) {
            latch(builder_failure());
            return;
        }
        ++draw_count_;
    }

    void PROGPU_NATIVE_COM_CALL DrawText(
        const wchar_t*, std::uint32_t, text_format*, const rectangle_f*,
        brush*, draw_text_options, measuring_mode) noexcept override
    {
        unsupported_draw();
    }

    void PROGPU_NATIVE_COM_CALL DrawTextLayout(
        point_2f, text_layout*, brush*, draw_text_options) noexcept override
    {
        unsupported_draw();
    }

    void PROGPU_NATIVE_COM_CALL DrawGlyphRun(
        point_2f, const glyph_run*, brush*, measuring_mode) noexcept override
    {
        unsupported_draw();
    }

    void PROGPU_NATIVE_COM_CALL SetTransform(
        const matrix_3x2_f* transform) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (transform == nullptr || !core::valid_transform(transform)) {
            latch(com::invalid_argument);
            return;
        }
        transform_ = *transform;
    }

    void PROGPU_NATIVE_COM_CALL GetTransform(
        matrix_3x2_f* transform) const noexcept override
    {
        if (transform == nullptr) {
            return;
        }
        const std::lock_guard lock(mutex_);
        *transform = transform_;
    }

    void PROGPU_NATIVE_COM_CALL SetAntialiasMode(
        antialias_mode mode) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (mode != antialias_mode::per_primitive &&
            mode != antialias_mode::aliased) {
            latch(com::invalid_argument);
            return;
        }
        antialias_mode_ = mode;
    }

    antialias_mode PROGPU_NATIVE_COM_CALL GetAntialiasMode()
        const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return antialias_mode_;
    }

    void PROGPU_NATIVE_COM_CALL SetTextAntialiasMode(
        text_antialias_mode mode) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (mode > text_antialias_mode::aliased) {
            latch(com::invalid_argument);
            return;
        }
        text_antialias_mode_ = mode;
    }

    text_antialias_mode PROGPU_NATIVE_COM_CALL GetTextAntialiasMode()
        const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return text_antialias_mode_;
    }

    void PROGPU_NATIVE_COM_CALL SetTextRenderingParams(
        rendering_parameters* parameters) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (parameters != nullptr) {
            latch(not_implemented);
        }
    }

    void PROGPU_NATIVE_COM_CALL GetTextRenderingParams(
        rendering_parameters** parameters) const noexcept override
    {
        if (parameters != nullptr) {
            *parameters = nullptr;
        }
    }

    void PROGPU_NATIVE_COM_CALL SetTags(
        std::uint64_t tag1,
        std::uint64_t tag2) noexcept override
    {
        const std::lock_guard lock(mutex_);
        tag1_ = tag1;
        tag2_ = tag2;
    }

    void PROGPU_NATIVE_COM_CALL GetTags(
        std::uint64_t* tag1,
        std::uint64_t* tag2) const noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (tag1 != nullptr) {
            *tag1 = tag1_;
        }
        if (tag2 != nullptr) {
            *tag2 = tag2_;
        }
    }

    void PROGPU_NATIVE_COM_CALL PushLayer(
        const layer_parameters* parameters,
        layer* layer_value) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        if (parameters == nullptr || layer_value == nullptr ||
            !valid_rectangle(parameters->content_bounds) ||
            !core::valid_transform(&parameters->mask_transform) ||
            !valid_opacity(parameters->opacity)) {
            latch(com::invalid_argument);
            return;
        }
        if (parameters->mask_antialias_mode !=
                antialias_mode::per_primitive &&
            parameters->mask_antialias_mode != antialias_mode::aliased) {
            latch(com::invalid_argument);
            return;
        }
        if (parameters->options != layer_options::none &&
            parameters->options != layer_options::initialize_for_cleartype) {
            latch(com::invalid_argument);
            return;
        }
        if (parameters->options != layer_options::none) {
            latch(not_implemented);
            return;
        }
        if (parameters->geometric_mask != nullptr &&
            parameters->mask_antialias_mode !=
                antialias_mode::per_primitive) {
            latch(not_implemented);
            return;
        }
        const bool full_target = infinite_rectangle(
            parameters->content_bounds);
        if (full_target && parameters->opacity_brush != nullptr) {
            latch(not_implemented);
            return;
        }
        if (!full_target && !axis_preserving_transform(transform_)) {
            latch(not_implemented);
            return;
        }
        if (scope_depth_ == scope_stack_.size()) {
            latch(com::out_of_memory);
            return;
        }
        factory* raw_factory = nullptr;
        layer_value->GetFactory(&raw_factory);
        com::pointer<factory> layer_factory;
        layer_factory.attach(raw_factory);
        if (layer_factory.get() != owner_.get()) {
            latch(wrong_factory);
            return;
        }
        scene_layer_native* raw_native = nullptr;
        const com::result query_result = layer_value->QueryInterface(
            scene_layer_native_interface_id,
            reinterpret_cast<void**>(&raw_native));
        com::pointer<scene_layer_native> layer_native;
        layer_native.attach(raw_native);
        if (com::failed(query_result) || !layer_native) {
            latch(query_result == com::no_interface
                ? not_implemented
                : query_result);
            return;
        }
        progpu_native_image_rect bounds = full_target
            ? progpu_native_image_rect{}
            : transformed_bounds(parameters->content_bounds);
        if (com::failed(failure_)) {
            return;
        }
        if (!valid_native_rectangle(bounds)) {
            latch(com::invalid_argument);
            return;
        }
        std::uint32_t mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (parameters->geometric_mask != nullptr) {
            progpu_native_image_rect mask_bounds{};
            bool empty_mask = false;
            if (!add_geometric_layer_mask(
                    parameters->geometric_mask,
                    parameters->mask_transform,
                    parameters->opacity_brush,
                    parameters->content_bounds,
                    mask_resource_index,
                    mask_bounds,
                    empty_mask)) {
                return;
            }
            if (empty_mask) {
                bounds = {};
            } else {
                bounds = full_target
                    ? mask_bounds
                    : intersect_rectangles(bounds, mask_bounds);
            }
        } else if (parameters->opacity_brush != nullptr) {
            bool empty_mask = false;
            if (!add_opacity_brush_layer_mask(
                    parameters->opacity_brush,
                    parameters->content_bounds,
                    mask_resource_index,
                    empty_mask)) {
                return;
            }
            if (empty_mask) {
                bounds = {};
            }
        }
        const bool has_bounds = !full_target ||
            parameters->geometric_mask != nullptr;
        const size_f required_size = has_bounds
            ? size_f{bounds.width, bounds.height}
            : size_f{
                static_cast<float>(pixel_width_) * 96.0F / dpi_x_,
                static_cast<float>(pixel_height_) * 96.0F / dpi_y_};
        const com::result begin_use_result = layer_native->BeginUse(
            this, required_size);
        if (com::failed(begin_use_result)) {
            latch(begin_use_result);
            return;
        }
        const progpu_native_scene_layer native_layer{
            sizeof(progpu_native_scene_layer),
            has_bounds ? PROGPU_NATIVE_SCENE_LAYER_BOUNDS : 0U,
            bounds,
            parameters->opacity,
            PROGPU_NATIVE_BLEND_SRC_OVER,
            mask_resource_index,
            PROGPU_NATIVE_SCENE_NO_INDEX,
            0U,
            0U,
            0U,
            0U};
        if (!builder_.push_layer(native_layer)) {
            layer_native->EndUse(this);
            latch(builder_failure());
            return;
        }
        layer_stack_[scope_depth_] = layer_native;
        scope_stack_[scope_depth_] = scope_opacity_layer;
        ++scope_depth_;
    }

    void PROGPU_NATIVE_COM_CALL PopLayer() noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        if (scope_depth_ == 0U ||
            scope_stack_[scope_depth_ - 1U] != scope_opacity_layer ||
            !layer_stack_[scope_depth_ - 1U]) {
            latch(wrong_state);
            return;
        }
        if (!builder_.pop_layer()) {
            latch(builder_failure());
            return;
        }
        --scope_depth_;
        layer_stack_[scope_depth_]->EndUse(this);
        layer_stack_[scope_depth_].reset();
        scope_stack_[scope_depth_] = scope_none;
    }

    com::result PROGPU_NATIVE_COM_CALL Flush(
        std::uint64_t* tag1,
        std::uint64_t* tag2) noexcept override
    {
        const std::lock_guard lock(mutex_);
        publish_tags(tag1, tag2);
        return failure_;
    }

    void PROGPU_NATIVE_COM_CALL SaveDrawingState(
        drawing_state_block* state) const noexcept override
    {
        if (state == nullptr) {
            return;
        }
        const std::lock_guard lock(mutex_);
        const drawing_state_description description{
            antialias_mode_,
            text_antialias_mode_,
            tag1_,
            tag2_,
            transform_};
        state->SetDescription(&description);
        state->SetTextRenderingParams(nullptr);
    }

    void PROGPU_NATIVE_COM_CALL RestoreDrawingState(
        drawing_state_block* state) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (state == nullptr) {
            latch(com::invalid_argument);
            return;
        }
        drawing_state_description description{};
        state->GetDescription(&description);
        if ((description.antialias != antialias_mode::per_primitive &&
                description.antialias != antialias_mode::aliased) ||
            description.text_antialias > text_antialias_mode::aliased ||
            !core::valid_transform(&description.transform)) {
            latch(com::invalid_argument);
            return;
        }
        antialias_mode_ = description.antialias;
        text_antialias_mode_ = description.text_antialias;
        tag1_ = description.tag1;
        tag2_ = description.tag2;
        transform_ = description.transform;
    }

    void PROGPU_NATIVE_COM_CALL PushAxisAlignedClip(
        const rectangle_f* rectangle,
        antialias_mode mode) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        if (rectangle == nullptr || !valid_rectangle(*rectangle)) {
            latch(com::invalid_argument);
            return;
        }
        if (mode != antialias_mode::per_primitive &&
            mode != antialias_mode::aliased) {
            latch(com::invalid_argument);
            return;
        }
        if (clip_depth_ == clip_stack_.size() ||
            scope_depth_ == scope_stack_.size()) {
            latch(com::out_of_memory);
            return;
        }
        progpu_native_image_rect clip = transformed_bounds(*rectangle);
        if (com::failed(failure_)) {
            return;
        }
        if (!valid_native_rectangle(clip)) {
            latch(com::invalid_argument);
            return;
        }
        if (clip_depth_ != 0U) {
            clip = intersect_rectangles(
                clip_stack_[clip_depth_ - 1U], clip);
            if (!valid_native_rectangle(clip)) {
                latch(com::invalid_argument);
                return;
            }
        }
        std::uint8_t scope = scope_axis_aligned_clip;
        if (mode == antialias_mode::aliased) {
            auto state = semantic_scene_builder::identity_state();
            state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
            state.clip_rect = clip;
            std::uint32_t state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
            if (!builder_.add_state(state, state_index) ||
                !builder_.save(state_index)) {
                latch(builder_failure());
                return;
            }
        } else {
            progpu_native_scene_layer_mask mask{};
            mask.bounds = clip;
            mask.transform = {
                1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};
            mask.opacity = 1.0F;
            std::uint32_t mask_resource_index =
                PROGPU_NATIVE_SCENE_NO_INDEX;
            if (!builder_.add_rounded_rectangle_mask(
                    mask, mask_resource_index)) {
                latch(builder_failure());
                return;
            }
            const progpu_native_scene_layer native_layer{
                sizeof(progpu_native_scene_layer),
                PROGPU_NATIVE_SCENE_LAYER_BOUNDS,
                clip,
                1.0F,
                PROGPU_NATIVE_BLEND_SRC_OVER,
                mask_resource_index,
                PROGPU_NATIVE_SCENE_NO_INDEX,
                0U,
                0U,
                0U,
                0U};
            if (!builder_.push_layer(native_layer)) {
                latch(builder_failure());
                return;
            }
            scope = scope_antialiased_axis_clip;
        }
        clip_stack_[clip_depth_] = clip;
        ++clip_depth_;
        scope_stack_[scope_depth_] = scope;
        ++scope_depth_;
    }

    void PROGPU_NATIVE_COM_CALL PopAxisAlignedClip() noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        if (clip_depth_ == 0U || scope_depth_ == 0U ||
            (scope_stack_[scope_depth_ - 1U] != scope_axis_aligned_clip &&
                scope_stack_[scope_depth_ - 1U] !=
                    scope_antialiased_axis_clip)) {
            latch(wrong_state);
            return;
        }
        const bool antialiased = scope_stack_[scope_depth_ - 1U] ==
            scope_antialiased_axis_clip;
        if (!(antialiased ? builder_.pop_layer() : builder_.restore())) {
            latch(builder_failure());
            return;
        }
        --clip_depth_;
        clip_stack_[clip_depth_] = {};
        --scope_depth_;
        scope_stack_[scope_depth_] = scope_none;
    }

    void PROGPU_NATIVE_COM_CALL Clear(const color_f* clear_color)
        noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        const color_f value = clear_color == nullptr
            ? color_f{0.0F, 0.0F, 0.0F, 0.0F}
            : *clear_color;
        if (!valid_color(value)) {
            latch(com::invalid_argument);
            return;
        }
        if (has_clear_ || draw_count_ != 0U) {
            latch(not_implemented);
            return;
        }
        clear_color_ = value;
        has_clear_ = true;
    }

    void PROGPU_NATIVE_COM_CALL BeginDraw() noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (begun_ && !ended_) {
            latch(wrong_state);
            return;
        }
        if (begun_) {
            if (generation_ == std::numeric_limits<std::uint64_t>::max()) {
                failure_ = com::invalid_argument;
                return;
            }
            ++generation_;
        }
        release_active_layers();
        if (!builder_.reset(scene_id_, generation_)) {
            failure_ = builder_failure();
            return;
        }
        bitmap_resources_.clear();
        transform_ = identity_transform;
        antialias_mode_ = antialias_mode::per_primitive;
        text_antialias_mode_ = text_antialias_mode::default_value;
        tag1_ = 0U;
        tag2_ = 0U;
        clear_color_ = {};
        draw_count_ = 0U;
        clip_depth_ = 0U;
        scope_depth_ = 0U;
        clip_stack_.fill({});
        scope_stack_.fill(scope_none);
        failure_ = com::ok;
        has_clear_ = false;
        begun_ = true;
        ended_ = false;
    }

    com::result PROGPU_NATIVE_COM_CALL EndDraw(
        std::uint64_t* tag1,
        std::uint64_t* tag2) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (!begun_ || ended_) {
            publish_tags(tag1, tag2);
            return wrong_state;
        }
        if (scope_depth_ != 0U || clip_depth_ != 0U) {
            latch(wrong_state);
        }
        ended_ = true;
        publish_tags(tag1, tag2);
        return failure_;
    }

    pixel_format PROGPU_NATIVE_COM_CALL GetPixelFormat()
        const noexcept override
    {
        return {0U, alpha_mode::premultiplied};
    }

    void PROGPU_NATIVE_COM_CALL SetDpi(
        float dpi_x,
        float dpi_y) noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (dpi_x == 0.0F && dpi_y == 0.0F) {
            dpi_x_ = 96.0F;
            dpi_y_ = 96.0F;
            return;
        }
        if (!valid_dpi(dpi_x, dpi_y)) {
            latch(com::invalid_argument);
            return;
        }
        dpi_x_ = dpi_x;
        dpi_y_ = dpi_y;
    }

    void PROGPU_NATIVE_COM_CALL GetDpi(
        float* dpi_x,
        float* dpi_y) const noexcept override
    {
        const std::lock_guard lock(mutex_);
        if (dpi_x != nullptr) {
            *dpi_x = dpi_x_;
        }
        if (dpi_y != nullptr) {
            *dpi_y = dpi_y_;
        }
    }

    size_f PROGPU_NATIVE_COM_CALL GetSize() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return {
            static_cast<float>(pixel_width_) * 96.0F / dpi_x_,
            static_cast<float>(pixel_height_) * 96.0F / dpi_y_};
    }

    size_u PROGPU_NATIVE_COM_CALL GetPixelSize() const noexcept override
    {
        return {pixel_width_, pixel_height_};
    }

    std::uint32_t PROGPU_NATIVE_COM_CALL GetMaximumBitmapSize()
        const noexcept override
    {
        return std::numeric_limits<std::uint32_t>::max();
    }

    std::int32_t PROGPU_NATIVE_COM_CALL IsSupported(
        const render_target_properties*) const noexcept override
    {
        return 0;
    }

    std::uint64_t PROGPU_NATIVE_COM_CALL GetRequiredSceneSize()
        const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return ended_ && com::succeeded(failure_)
            ? static_cast<std::uint64_t>(builder_.required_stream_size())
            : 0U;
    }

    com::result PROGPU_NATIVE_COM_CALL BuildScene(
        void* destination,
        std::uint64_t destination_size,
        std::uint64_t* bytes_written) const noexcept override
    {
        if (bytes_written == nullptr) {
            return com::pointer_error;
        }
        *bytes_written = 0U;
        const std::lock_guard lock(mutex_);
        if (!ended_ || com::failed(failure_)) {
            return wrong_state;
        }
        const std::size_t required = builder_.required_stream_size();
        if (required == 0U) {
            return failure;
        }
        if (destination == nullptr || destination_size < required ||
            destination_size > std::numeric_limits<std::size_t>::max()) {
            return com::invalid_argument;
        }
        std::size_t written = 0U;
        if (!builder_.build_into(
                std::span<std::byte>(
                    static_cast<std::byte*>(destination),
                    static_cast<std::size_t>(destination_size)),
                written)) {
            return failure;
        }
        *bytes_written = static_cast<std::uint64_t>(written);
        return com::ok;
    }

    void PROGPU_NATIVE_COM_CALL GetSummary(
        scene_render_target_summary* summary) const noexcept override
    {
        if (summary == nullptr) {
            return;
        }
        const std::lock_guard lock(mutex_);
        *summary = {
            scene_id_,
            generation_,
            draw_count_,
            has_clear_ ? 1 : 0,
            clear_color_};
    }

private:
    template<typename Interface>
    [[nodiscard]] static com::result unsupported_output(
        Interface** value) noexcept
    {
        if (value == nullptr) {
            return com::pointer_error;
        }
        *value = nullptr;
        return not_implemented;
    }

    [[nodiscard]] bool can_draw() noexcept
    {
        if (!begun_ || ended_) {
            latch(wrong_state);
            return false;
        }
        return com::succeeded(failure_);
    }

    void latch(com::result value) noexcept
    {
        if (com::succeeded(failure_)) {
            failure_ = value;
        }
    }

    void unsupported_draw() noexcept
    {
        const std::lock_guard lock(mutex_);
        if (can_draw()) {
            latch(not_implemented);
        }
    }

    [[nodiscard]] com::result builder_failure() const noexcept
    {
        return builder_.last_error() == scene_build_error::out_of_memory
            ? com::out_of_memory
            : failure;
    }

    [[nodiscard]] static bool try_invert_transform(
        const matrix_3x2_f& source,
        matrix_3x2_f& inverse) noexcept
    {
        const double determinant =
            static_cast<double>(source.m11) * source.m22 -
            static_cast<double>(source.m12) * source.m21;
        if (!std::isfinite(determinant) || determinant == 0.0) {
            return false;
        }
        const double reciprocal = 1.0 / determinant;
        const double m11 = static_cast<double>(source.m22) * reciprocal;
        const double m12 = -static_cast<double>(source.m12) * reciprocal;
        const double m21 = -static_cast<double>(source.m21) * reciprocal;
        const double m22 = static_cast<double>(source.m11) * reciprocal;
        const double m31 =
            (static_cast<double>(source.m21) * source.m32 -
                static_cast<double>(source.m31) * source.m22) * reciprocal;
        const double m32 =
            (static_cast<double>(source.m31) * source.m12 -
                static_cast<double>(source.m11) * source.m32) * reciprocal;
        constexpr double maximum = std::numeric_limits<float>::max();
        const double values[]{m11, m12, m21, m22, m31, m32};
        if (!std::all_of(
                std::begin(values), std::end(values),
                [](double value) {
                    return std::isfinite(value) && value >= -maximum &&
                        value <= maximum;
                })) {
            return false;
        }
        inverse = {
            static_cast<float>(m11),
            static_cast<float>(m12),
            static_cast<float>(m21),
            static_cast<float>(m22),
            static_cast<float>(m31),
            static_cast<float>(m32)};
        return true;
    }

    [[nodiscard]] static matrix_3x2_f compose_transform(
        const matrix_3x2_f& first,
        const matrix_3x2_f& second) noexcept
    {
        return {
            first.m11 * second.m11 + first.m12 * second.m21,
            first.m11 * second.m12 + first.m12 * second.m22,
            first.m21 * second.m11 + first.m22 * second.m21,
            first.m21 * second.m12 + first.m22 * second.m22,
            first.m31 * second.m11 + first.m32 * second.m21 + second.m31,
            first.m31 * second.m12 + first.m32 * second.m22 + second.m32};
    }

    struct bitmap_resource_entry final {
        com::pointer<scene_bitmap_native> source;
        bitmap_snapshot snapshot{};
        std::uint32_t resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    };

    [[nodiscard]] bool add_bitmap_resource(
        scene_bitmap_native* source,
        std::uint32_t& resource_index,
        bitmap_snapshot& snapshot) noexcept
    {
        const com::result snapshot_result = source->GetSnapshot(&snapshot);
        if (com::failed(snapshot_result)) {
            latch(snapshot_result);
            return false;
        }
        const auto existing = std::find_if(
            bitmap_resources_.begin(),
            bitmap_resources_.end(),
            [source, &snapshot](const bitmap_resource_entry& entry) {
                return entry.source.get() == source &&
                    entry.snapshot.generation == snapshot.generation;
            });
        if (existing != bitmap_resources_.end()) {
            resource_index = existing->resource_index;
            snapshot = existing->snapshot;
            return true;
        }
        const com::result add_result =
            source->AddToScene(&builder_, &resource_index, &snapshot);
        if (com::failed(add_result)) {
            latch(add_result);
            return false;
        }
        try {
            bitmap_resources_.push_back({
                com::pointer<scene_bitmap_native>(source),
                snapshot,
                resource_index});
            return true;
        } catch (const std::bad_alloc&) {
            latch(com::out_of_memory);
            return false;
        } catch (...) {
            latch(failure);
            return false;
        }
    }

    enum class bitmap_brush_draw_result {
        not_bitmap,
        drawn,
        failed
    };

    [[nodiscard]] static std::uint32_t image_address_flags(
        extend_mode extend,
        std::uint32_t shift) noexcept
    {
        const std::uint32_t value = extend == extend_mode::wrap
            ? PROGPU_NATIVE_IMAGE_ADDRESS_REPEAT
            : extend == extend_mode::mirror
                ? PROGPU_NATIVE_IMAGE_ADDRESS_MIRROR_REPEAT
                : PROGPU_NATIVE_IMAGE_ADDRESS_CLAMP;
        return value << shift;
    }

    [[nodiscard]] bool draw_bitmap_brush_image(
        scene_bitmap_brush_native* brush_native,
        std::uint32_t mask_resource_index,
        const rectangle_f& local_bounds) noexcept
    {
        bitmap* raw_bitmap = nullptr;
        extend_mode extend_x = extend_mode::clamp;
        extend_mode extend_y = extend_mode::clamp;
        bitmap_interpolation_mode interpolation =
            bitmap_interpolation_mode::linear;
        float opacity = 1.0F;
        matrix_3x2_f brush_transform = identity_transform;
        const com::result brush_result = brush_native->GetSceneSnapshot(
            &raw_bitmap,
            &extend_x,
            &extend_y,
            &interpolation,
            &opacity,
            &brush_transform);
        com::pointer<bitmap> bitmap_value;
        bitmap_value.attach(raw_bitmap);
        if (com::failed(brush_result) || !bitmap_value ||
            !valid_extend_mode(extend_x) || !valid_extend_mode(extend_y) ||
            !valid_bitmap_interpolation_mode(interpolation) ||
            !valid_opacity(opacity) ||
            !core::valid_transform(&brush_transform)) {
            latch(com::failed(brush_result)
                ? brush_result
                : com::invalid_argument);
            return false;
        }
        factory* raw_factory = nullptr;
        bitmap_value->GetFactory(&raw_factory);
        com::pointer<factory> bitmap_factory;
        bitmap_factory.attach(raw_factory);
        if (bitmap_factory.get() != owner_.get()) {
            latch(wrong_factory);
            return false;
        }
        scene_bitmap_native* raw_source = nullptr;
        const com::result source_result = bitmap_value->QueryInterface(
            scene_bitmap_native_interface_id,
            reinterpret_cast<void**>(&raw_source));
        com::pointer<scene_bitmap_native> source;
        source.attach(raw_source);
        if (com::failed(source_result) || !source) {
            latch(not_implemented);
            return false;
        }
        std::uint32_t image_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        bitmap_snapshot snapshot{};
        if (!add_bitmap_resource(
                source.get(), image_resource_index, snapshot)) {
            return false;
        }
        matrix_3x2_f inverse_brush{};
        if (!try_invert_transform(brush_transform, inverse_brush)) {
            latch(com::invalid_argument);
            return false;
        }
        const auto transform_point = [](
            const matrix_3x2_f& transform,
            float x,
            float y) noexcept {
            return point_2f{
                x * transform.m11 + y * transform.m21 + transform.m31,
                x * transform.m12 + y * transform.m22 + transform.m32};
        };
        const std::array local_corners{
            point_2f{local_bounds.left, local_bounds.top},
            point_2f{local_bounds.right, local_bounds.top},
            point_2f{local_bounds.right, local_bounds.bottom},
            point_2f{local_bounds.left, local_bounds.bottom}};
        rectangle_f brush_bounds{};
        for (std::size_t index = 0U; index < local_corners.size(); ++index) {
            const point_2f point = transform_point(
                inverse_brush,
                local_corners[index].x,
                local_corners[index].y);
            if (!valid_point(point)) {
                latch(com::invalid_argument);
                return false;
            }
            if (index == 0U) {
                brush_bounds = {point.x, point.y, point.x, point.y};
            } else {
                brush_bounds.left = std::min(brush_bounds.left, point.x);
                brush_bounds.top = std::min(brush_bounds.top, point.y);
                brush_bounds.right = std::max(brush_bounds.right, point.x);
                brush_bounds.bottom = std::max(brush_bounds.bottom, point.y);
            }
        }
        if (!valid_rectangle(brush_bounds)) {
            latch(com::invalid_argument);
            return false;
        }
        const float pixels_per_dip_x = snapshot.dpi_x / 96.0F;
        const float pixels_per_dip_y = snapshot.dpi_y / 96.0F;
        progpu_native_scene_image_draw image{};
        image.image_width = snapshot.width;
        image.image_height = snapshot.height;
        image.row_bytes = snapshot.row_bytes;
        image.flags = PROGPU_NATIVE_SCENE_IMAGE_SOURCE_PREMULTIPLIED |
            PROGPU_NATIVE_SCENE_IMAGE_EXTENDED_SOURCE_RECT |
            image_address_flags(
                extend_x, PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_U_SHIFT) |
            image_address_flags(
                extend_y, PROGPU_NATIVE_SCENE_IMAGE_ADDRESS_V_SHIFT);
        image.sampling = interpolation ==
                bitmap_interpolation_mode::nearest_neighbor
            ? PROGPU_NATIVE_IMAGE_SAMPLING_NEAREST
            : PROGPU_NATIVE_IMAGE_SAMPLING_LINEAR;
        image.source_rect = {
            brush_bounds.left * pixels_per_dip_x,
            brush_bounds.top * pixels_per_dip_y,
            (brush_bounds.right - brush_bounds.left) * pixels_per_dip_x,
            (brush_bounds.bottom - brush_bounds.top) * pixels_per_dip_y};
        image.destination_rect = {
            brush_bounds.left,
            brush_bounds.top,
            brush_bounds.right - brush_bounds.left,
            brush_bounds.bottom - brush_bounds.top};
        const matrix_3x2_f image_transform = compose_transform(
            brush_transform, transform_);
        if (!core::valid_transform(&image_transform)) {
            latch(com::invalid_argument);
            return false;
        }
        image.transform = {
            image_transform.m11,
            image_transform.m12,
            image_transform.m21,
            image_transform.m22,
            image_transform.m31,
            image_transform.m32};
        image.opacity = opacity;
        image.max_anisotropy = 1U;

        auto state = semantic_scene_builder::identity_state();
        state.flags = PROGPU_NATIVE_SCENE_STATE_MASK;
        state.mask_resource_index = mask_resource_index;
        std::uint32_t state_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        const progpu_native_image_rect bounds = transformed_bounds(
            local_bounds);
        if (com::failed(failure_)) {
            return false;
        }
        if (!builder_.add_state(state, state_resource_index) ||
            !builder_.draw_image(
                image_resource_index,
                image,
                bounds,
                state_resource_index)) {
            latch(builder_failure());
            return false;
        }
        ++draw_count_;
        return true;
    }

    [[nodiscard]] bitmap_brush_draw_result draw_bitmap_brush_geometry(
        brush* brush_value,
        std::span<const progpu_native_geometry_primitive> primitives,
        const rectangle_f& local_bounds) noexcept
    {
        if (brush_value == nullptr) {
            latch(com::invalid_argument);
            return bitmap_brush_draw_result::failed;
        }
        scene_bitmap_brush_native* raw_native = nullptr;
        const com::result query = brush_value->QueryInterface(
            scene_bitmap_brush_native_interface_id,
            reinterpret_cast<void**>(&raw_native));
        com::pointer<scene_bitmap_brush_native> native;
        native.attach(raw_native);
        if (query == com::no_interface || !native) {
            return bitmap_brush_draw_result::not_bitmap;
        }
        if (com::failed(query)) {
            latch(query);
            return bitmap_brush_draw_result::failed;
        }
        progpu_native_scene_layer_geometry_mask mask{};
        mask.bounds = {
            local_bounds.left,
            local_bounds.top,
            local_bounds.right - local_bounds.left,
            local_bounds.bottom - local_bounds.top};
        mask.transform = native_transform();
        mask.opacity = 1.0F;
        mask.brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
        mask.brush.opacity = 1.0F;
        mask.brush.colors[0] = {1.0F, 1.0F, 1.0F, 1.0F};
        std::uint32_t mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder_.add_geometry_mask(
                mask, primitives, {}, mask_resource_index) ||
            !draw_bitmap_brush_image(
                native.get(), mask_resource_index, local_bounds)) {
            if (!com::failed(failure_)) {
                latch(builder_failure());
            }
            return bitmap_brush_draw_result::failed;
        }
        return bitmap_brush_draw_result::drawn;
    }

    [[nodiscard]] bitmap_brush_draw_result draw_bitmap_brush_analytic_mask(
        brush* brush_value,
        const rectangle_f& local_bounds,
        float radius_x,
        float radius_y) noexcept
    {
        if (brush_value == nullptr) {
            latch(com::invalid_argument);
            return bitmap_brush_draw_result::failed;
        }
        scene_bitmap_brush_native* raw_native = nullptr;
        const com::result query = brush_value->QueryInterface(
            scene_bitmap_brush_native_interface_id,
            reinterpret_cast<void**>(&raw_native));
        com::pointer<scene_bitmap_brush_native> native;
        native.attach(raw_native);
        if (query == com::no_interface || !native) {
            return bitmap_brush_draw_result::not_bitmap;
        }
        if (com::failed(query)) {
            latch(query);
            return bitmap_brush_draw_result::failed;
        }
        progpu_native_scene_layer_mask mask{};
        mask.bounds = {
            local_bounds.left,
            local_bounds.top,
            local_bounds.right - local_bounds.left,
            local_bounds.bottom - local_bounds.top};
        mask.transform = native_transform();
        std::fill_n(mask.corner_radii_x, 4U, radius_x);
        std::fill_n(mask.corner_radii_y, 4U, radius_y);
        mask.opacity = 1.0F;
        std::uint32_t mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder_.add_rounded_rectangle_mask(
                mask, mask_resource_index) ||
            !draw_bitmap_brush_image(
                native.get(), mask_resource_index, local_bounds)) {
            if (!com::failed(failure_)) {
                latch(builder_failure());
            }
            return bitmap_brush_draw_result::failed;
        }
        return bitmap_brush_draw_result::drawn;
    }

    [[nodiscard]] bitmap_brush_draw_result draw_bitmap_brush_path(
        brush* brush_value,
        std::span<const progpu_native_path_segment> segments,
        std::uint32_t fill_rule,
        const rectangle_f& local_bounds) noexcept
    {
        if (brush_value == nullptr || segments.empty()) {
            latch(com::invalid_argument);
            return bitmap_brush_draw_result::failed;
        }
        scene_bitmap_brush_native* raw_native = nullptr;
        const com::result query = brush_value->QueryInterface(
            scene_bitmap_brush_native_interface_id,
            reinterpret_cast<void**>(&raw_native));
        com::pointer<scene_bitmap_brush_native> native;
        native.attach(raw_native);
        if (query == com::no_interface || !native) {
            return bitmap_brush_draw_result::not_bitmap;
        }
        if (com::failed(query)) {
            latch(query);
            return bitmap_brush_draw_result::failed;
        }
        const progpu_native_scene_clip_path path{
            0U,
            segments.size(),
            0U,
            0U,
            local_bounds.left,
            local_bounds.top,
            local_bounds.right,
            local_bounds.bottom,
            native_transform(),
            fill_rule,
            8U,
            PROGPU_NATIVE_CLIP_INTERSECT,
            0U};
        std::uint32_t mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder_.add_vector_clip_mask(
                std::span<const progpu_native_scene_clip_path>(&path, 1U),
                segments,
                1.0F,
                mask_resource_index) ||
            !draw_bitmap_brush_image(
                native.get(), mask_resource_index, local_bounds)) {
            if (!com::failed(failure_)) {
                latch(builder_failure());
            }
            return bitmap_brush_draw_result::failed;
        }
        return bitmap_brush_draw_result::drawn;
    }

    [[nodiscard]] bool translate_opacity_brush_layer_mask(
        brush* source,
        const rectangle_f& content_bounds,
        progpu_native_scene_layer_brush_mask& mask,
        std::vector<progpu_native_scene_gradient_stop>& stops,
        bool& empty) noexcept
    {
        mask = {};
        stops.clear();
        empty = content_bounds.right == content_bounds.left ||
            content_bounds.bottom == content_bounds.top;
        if (empty) {
            return true;
        }
        if (source == nullptr || !valid_rectangle(content_bounds)) {
            latch(com::invalid_argument);
            return false;
        }
        factory* raw_factory = nullptr;
        source->GetFactory(&raw_factory);
        com::pointer<factory> brush_factory;
        brush_factory.attach(raw_factory);
        if (brush_factory.get() != owner_.get()) {
            latch(wrong_factory);
            return false;
        }

        progpu_native_scene_brush native{};
        linear_gradient_brush* raw_linear = nullptr;
        const com::result linear_query = source->QueryInterface(
            linear_gradient_brush_interface_id,
            reinterpret_cast<void**>(&raw_linear));
        com::pointer<linear_gradient_brush> linear;
        linear.attach(raw_linear);
        if (com::succeeded(linear_query) && linear) {
            const point_2f start = linear->GetStartPoint();
            const point_2f end = linear->GetEndPoint();
            const float opacity = linear->GetOpacity();
            if (!valid_point(start) || !valid_point(end) ||
                !valid_opacity(opacity)) {
                latch(com::invalid_argument);
                return false;
            }
            gradient_stop_collection* raw_collection = nullptr;
            linear->GetGradientStopCollection(&raw_collection);
            com::pointer<gradient_stop_collection> collection;
            collection.attach(raw_collection);
            native.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
            native.opacity = opacity;
            native.start_point = {start.x, start.y};
            native.end_point = {end.x, end.y};
            if (!translate_gradient_brush(
                    linear.get(), collection.get(), native, stops)) {
                return false;
            }
        } else {
            radial_gradient_brush* raw_radial = nullptr;
            const com::result radial_query = source->QueryInterface(
                radial_gradient_brush_interface_id,
                reinterpret_cast<void**>(&raw_radial));
            com::pointer<radial_gradient_brush> radial;
            radial.attach(raw_radial);
            if (com::succeeded(radial_query) && radial) {
                const point_2f center = radial->GetCenter();
                const point_2f offset = radial->GetGradientOriginOffset();
                const point_2f origin{
                    center.x + offset.x,
                    center.y + offset.y};
                const float radius_x = radial->GetRadiusX();
                const float radius_y = radial->GetRadiusY();
                const float opacity = radial->GetOpacity();
                if (!valid_point(center) || !valid_point(offset) ||
                    !valid_point(origin) || !std::isfinite(radius_x) ||
                    !std::isfinite(radius_y) || radius_x < 0.0F ||
                    radius_y < 0.0F ||
                    (radius_x == 0.0F && radius_y == 0.0F) ||
                    !valid_opacity(opacity)) {
                    latch(com::invalid_argument);
                    return false;
                }
                gradient_stop_collection* raw_collection = nullptr;
                radial->GetGradientStopCollection(&raw_collection);
                com::pointer<gradient_stop_collection> collection;
                collection.attach(raw_collection);
                native.type = PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT;
                native.opacity = opacity;
                native.start_point = {origin.x, origin.y};
                native.center = {center.x, center.y};
                native.radius = radius_x;
                native.radius_y = radius_y;
                if (!translate_gradient_brush(
                        radial.get(), collection.get(), native, stops)) {
                    return false;
                }
            } else {
                solid_color_brush* raw_solid = nullptr;
                const com::result solid_query = source->QueryInterface(
                    solid_color_brush_interface_id,
                    reinterpret_cast<void**>(&raw_solid));
                com::pointer<solid_color_brush> solid;
                solid.attach(raw_solid);
                if (com::failed(solid_query) || !solid) {
                    latch(not_implemented);
                    return false;
                }
                const color_f color = solid->GetColor();
                const float opacity = solid->GetOpacity();
                if (!valid_color(color) || !valid_opacity(opacity)) {
                    latch(com::invalid_argument);
                    return false;
                }
                native.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
                native.opacity = opacity;
                native.colors[0] = {
                    color.red, color.green, color.blue, color.alpha};
                native.coordinate_transform0[0] = 1.0F;
                native.coordinate_transform1[1] = 1.0F;
            }
        }

        mask.struct_size = sizeof(mask);
        mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH;
        mask.gradient_stop_count = static_cast<std::uint32_t>(stops.size());
        mask.bounds = {
            content_bounds.left,
            content_bounds.top,
            content_bounds.right - content_bounds.left,
            content_bounds.bottom - content_bounds.top};
        mask.transform = native_transform();
        mask.opacity = 1.0F;
        mask.brush = native;
        return true;
    }

    [[nodiscard]] bool add_opacity_brush_layer_mask(
        brush* source,
        const rectangle_f& content_bounds,
        std::uint32_t& resource_index,
        bool& empty) noexcept
    {
        resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        progpu_native_scene_layer_brush_mask mask{};
        std::vector<progpu_native_scene_gradient_stop> stops;
        if (!translate_opacity_brush_layer_mask(
                source, content_bounds, mask, stops, empty)) {
            return false;
        }
        if (empty) {
            return true;
        }
        if (!builder_.add_brush_mask(mask, stops, resource_index)) {
            latch(builder_failure());
            return false;
        }
        return true;
    }

    [[nodiscard]] bool add_geometric_layer_mask(
        geometry* geometry_value,
        const matrix_3x2_f& mask_transform,
        brush* opacity_brush,
        const rectangle_f& content_bounds,
        std::uint32_t& resource_index,
        progpu_native_image_rect& target_bounds,
        bool& empty) noexcept
    {
        resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        target_bounds = {};
        empty = false;
        if (geometry_value == nullptr ||
            !core::valid_transform(&mask_transform)) {
            latch(com::invalid_argument);
            return false;
        }
        factory* raw_factory = nullptr;
        geometry_value->GetFactory(&raw_factory);
        com::pointer<factory> geometry_factory;
        geometry_factory.attach(raw_factory);
        if (geometry_factory.get() != owner_.get()) {
            latch(wrong_factory);
            return false;
        }
        auto* raw_sink = new (std::nothrow) portable_scene_path_sink();
        if (raw_sink == nullptr) {
            latch(com::out_of_memory);
            return false;
        }
        com::pointer<portable_scene_path_sink> sink;
        sink.attach(raw_sink);
        com::result result = geometry_value->Simplify(
            geometry_simplification_option::cubics_and_lines,
            nullptr,
            core::default_flattening_tolerance,
            sink.get());
        const com::result close_result = sink->Close();
        if (com::succeeded(result)) {
            result = close_result;
        }
        if (com::failed(result)) {
            latch(result);
            return false;
        }
        const auto segments = sink->segments();
        if (segments.empty()) {
            empty = true;
            return true;
        }
        rectangle_f local_bounds{};
        result = geometry_value->GetBounds(nullptr, &local_bounds);
        const matrix_3x2_f target_transform = compose_transform(
            mask_transform, transform_);
        rectangle_f transformed_mask_bounds{};
        if (com::succeeded(result) &&
            core::valid_transform(&target_transform)) {
            result = geometry_value->GetBounds(
                &target_transform, &transformed_mask_bounds);
        }
        if (com::failed(result) || !valid_rectangle(local_bounds) ||
            !valid_rectangle(transformed_mask_bounds)) {
            latch(com::failed(result) ? result : com::invalid_argument);
            return false;
        }
        if (local_bounds.right == local_bounds.left ||
            local_bounds.bottom == local_bounds.top ||
            transformed_mask_bounds.right == transformed_mask_bounds.left ||
            transformed_mask_bounds.bottom == transformed_mask_bounds.top) {
            empty = true;
            return true;
        }
        const progpu_native_scene_clip_path path{
            0U,
            segments.size(),
            0U,
            0U,
            local_bounds.left,
            local_bounds.top,
            local_bounds.right,
            local_bounds.bottom,
            {
                target_transform.m11,
                target_transform.m12,
                target_transform.m21,
                target_transform.m22,
                target_transform.m31,
                target_transform.m32},
            sink->native_fill_rule(),
            8U,
            PROGPU_NATIVE_CLIP_INTERSECT,
            0U};
        if (opacity_brush == nullptr) {
            if (!builder_.add_vector_clip_mask(
                    std::span<const progpu_native_scene_clip_path>(
                        &path, 1U),
                    segments,
                    1.0F,
                    resource_index)) {
                latch(builder_failure());
                return false;
            }
        } else {
            progpu_native_scene_layer_brush_mask brush_mask{};
            std::vector<progpu_native_scene_gradient_stop> stops;
            bool empty_brush = false;
            if (!translate_opacity_brush_layer_mask(
                    opacity_brush,
                    content_bounds,
                    brush_mask,
                    stops,
                    empty_brush)) {
                return false;
            }
            if (empty_brush) {
                empty = true;
                return true;
            }
            if (!builder_.add_composite_mask(
                    std::span<const progpu_native_scene_layer_brush_mask>(
                        &brush_mask, 1U),
                    {},
                    {},
                    {},
                    {},
                    std::span<const progpu_native_scene_clip_path>(
                        &path, 1U),
                    segments,
                    {},
                    stops,
                    1.0F,
                    resource_index)) {
                latch(builder_failure());
                return false;
            }
        }
        target_bounds = {
            transformed_mask_bounds.left,
            transformed_mask_bounds.top,
            transformed_mask_bounds.right - transformed_mask_bounds.left,
            transformed_mask_bounds.bottom - transformed_mask_bounds.top};
        return true;
    }

    void draw_filled_geometry(
        geometry* geometry_value,
        brush* brush_value,
        brush* opacity_brush) noexcept
    {
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        if (geometry_value == nullptr || brush_value == nullptr) {
            latch(com::invalid_argument);
            return;
        }
        if (opacity_brush != nullptr) {
            latch(not_implemented);
            return;
        }
        factory* raw_factory = nullptr;
        geometry_value->GetFactory(&raw_factory);
        com::pointer<factory> geometry_factory;
        geometry_factory.attach(raw_factory);
        if (geometry_factory.get() != owner_.get()) {
            latch(wrong_factory);
            return;
        }
        raw_factory = nullptr;
        brush_value->GetFactory(&raw_factory);
        com::pointer<factory> brush_factory;
        brush_factory.attach(raw_factory);
        if (brush_factory.get() != owner_.get()) {
            latch(wrong_factory);
            return;
        }
        auto* raw_sink = new (std::nothrow) portable_scene_path_sink();
        if (raw_sink == nullptr) {
            latch(com::out_of_memory);
            return;
        }
        com::pointer<portable_scene_path_sink> sink;
        sink.attach(raw_sink);
        com::result result = geometry_value->Simplify(
            geometry_simplification_option::cubics_and_lines,
            nullptr,
            core::default_flattening_tolerance,
            sink.get());
        const com::result close_result = sink->Close();
        if (com::succeeded(result)) {
            result = close_result;
        }
        if (com::failed(result)) {
            latch(result);
            return;
        }
        const auto segments = sink->segments();
        if (segments.empty()) {
            return;
        }
        rectangle_f local_bounds{};
        rectangle_f target_bounds{};
        result = geometry_value->GetBounds(nullptr, &local_bounds);
        if (com::succeeded(result)) {
            result = geometry_value->GetBounds(&transform_, &target_bounds);
        }
        if (com::failed(result) || !valid_rectangle(local_bounds) ||
            !valid_rectangle(target_bounds)) {
            latch(com::failed(result) ? result : com::invalid_argument);
            return;
        }
        if (local_bounds.right == local_bounds.left ||
            local_bounds.bottom == local_bounds.top ||
            target_bounds.right == target_bounds.left ||
            target_bounds.bottom == target_bounds.top) {
            return;
        }

        const bitmap_brush_draw_result bitmap_result =
            draw_bitmap_brush_path(
                brush_value,
                segments,
                sink->native_fill_rule(),
                local_bounds);
        if (bitmap_result != bitmap_brush_draw_result::not_bitmap) {
            return;
        }

        std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!add_brush(brush_value, brush_index)) {
            return;
        }
        const progpu_native_scene_path_fill path{
            0U,
            segments.size(),
            0U,
            0U,
            local_bounds.left,
            local_bounds.top,
            local_bounds.right,
            local_bounds.bottom,
            {1.0F, 1.0F, 1.0F, 1.0F},
            native_transform(),
            sink->native_fill_rule(),
            8U};
        const progpu_native_image_rect bounds{
            target_bounds.left,
            target_bounds.top,
            target_bounds.right - target_bounds.left,
            target_bounds.bottom - target_bounds.top};
        if (!builder_.draw_paths(
                std::span<const progpu_native_scene_path_fill>(&path, 1U),
                segments,
                std::span<const std::uint32_t>(&brush_index, 1U),
                bounds)) {
            latch(builder_failure());
            return;
        }
        ++draw_count_;
    }

    struct stroke_style_snapshot final {
        cap_style start_cap = cap_style::flat;
        cap_style end_cap = cap_style::flat;
        cap_style dash_cap = cap_style::flat;
        line_join join = line_join::miter;
        float miter_limit = 10.0F;
        double dash_offset{};
        std::vector<double> dash_intervals;
    };

    [[nodiscard]] static com::result read_stroke_style(
        stroke_style* source,
        stroke_style_snapshot& result) noexcept
    {
        result = {};
        if (source == nullptr) {
            return com::ok;
        }
        result.start_cap = source->GetStartCap();
        result.end_cap = source->GetEndCap();
        result.dash_cap = source->GetDashCap();
        result.join = source->GetLineJoin();
        result.miter_limit = source->GetMiterLimit();
        result.dash_offset = source->GetDashOffset();
        const dash_style dash = source->GetDashStyle();
        if (result.start_cap > cap_style::triangle ||
            result.end_cap > cap_style::triangle ||
            result.dash_cap > cap_style::triangle ||
            result.join > line_join::miter_or_bevel ||
            !std::isfinite(result.miter_limit) ||
            result.miter_limit <= 0.0F ||
            !std::isfinite(result.dash_offset) ||
            dash > dash_style::custom) {
            return com::invalid_argument;
        }
        try {
            switch (dash) {
            case dash_style::solid:
                break;
            case dash_style::dash:
                result.dash_intervals = {2.0, 2.0};
                break;
            case dash_style::dot:
                result.dash_intervals = {0.0, 2.0};
                break;
            case dash_style::dash_dot:
                result.dash_intervals = {2.0, 2.0, 0.0, 2.0};
                break;
            case dash_style::dash_dot_dot:
                result.dash_intervals = {
                    2.0, 2.0, 0.0, 2.0, 0.0, 2.0};
                break;
            case dash_style::custom: {
                constexpr std::uint32_t maximum_dash_count = 1U << 20U;
                const std::uint32_t count = source->GetDashesCount();
                if (count == 0U || count > maximum_dash_count) {
                    return com::invalid_argument;
                }
                std::vector<float> dashes(count);
                source->GetDashes(dashes.data(), count);
                result.dash_intervals.reserve(count);
                bool has_positive = false;
                for (const float value : dashes) {
                    if (!std::isfinite(value) || value < 0.0F) {
                        return com::invalid_argument;
                    }
                    has_positive = has_positive || value > 0.0F;
                    result.dash_intervals.push_back(value);
                }
                if (!has_positive) {
                    return com::invalid_argument;
                }
                break;
            }
            }
        } catch (const std::bad_alloc&) {
            return com::out_of_memory;
        } catch (...) {
            return failure;
        }
        return com::ok;
    }

    void draw_stroked_geometry(
        geometry* geometry_value,
        brush* brush_value,
        float stroke_width,
        stroke_style* style_value) noexcept
    {
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        if (geometry_value == nullptr || brush_value == nullptr ||
            !std::isfinite(stroke_width) || stroke_width < 0.0F) {
            latch(com::invalid_argument);
            return;
        }
        const auto same_factory = [&](resource* value) {
            factory* raw_factory = nullptr;
            value->GetFactory(&raw_factory);
            com::pointer<factory> value_factory;
            value_factory.attach(raw_factory);
            return value_factory.get() == owner_.get();
        };
        if (!same_factory(geometry_value) || !same_factory(brush_value) ||
            (style_value != nullptr && !same_factory(style_value))) {
            latch(wrong_factory);
            return;
        }
        stroke_style_snapshot style{};
        com::result result = read_stroke_style(style_value, style);
        if (com::failed(result)) {
            latch(result);
            return;
        }
        if (stroke_width == 0.0F) {
            return;
        }

        auto* raw_sink = new (std::nothrow) portable_scene_stroke_sink();
        if (raw_sink == nullptr) {
            latch(com::out_of_memory);
            return;
        }
        com::pointer<portable_scene_stroke_sink> sink;
        sink.attach(raw_sink);
        result = geometry_value->Simplify(
            geometry_simplification_option::cubics_and_lines,
            nullptr,
            core::default_flattening_tolerance,
            sink.get());
        const com::result close_result = sink->Close();
        if (com::succeeded(result)) {
            result = close_result;
        }
        if (com::failed(result)) {
            latch(result);
            return;
        }

        struct stroke_run final {
            std::size_t segment_offset{};
            std::size_t segment_count{};
            std::size_t smooth_join_offset{};
            bool closed{};
            bool start_uses_dash_cap{};
            bool end_uses_dash_cap{};
        };
        try {
            std::vector<stroke_run> runs;
            std::vector<progpu_native_path_segment> run_segments;
            std::vector<std::uint8_t> run_smooth_joins;
            const auto captured_segments = sink->segments();
            const auto captured_flags = sink->segment_flags();
            if (captured_segments.size() != captured_flags.size()) {
                latch(failure);
                return;
            }
            run_segments.reserve(
                captured_segments.size() + sink->figures().size());
            run_smooth_joins.reserve(
                captured_segments.size() + sink->figures().size());
            runs.reserve(sink->figures().size());

            constexpr auto force_unstroked =
                static_cast<std::uint32_t>(path_segment::force_unstroked);
            constexpr auto force_round = static_cast<std::uint32_t>(
                path_segment::force_round_line_join);
            for (const auto& figure : sink->figures()) {
                if (figure.segment_offset > captured_segments.size() ||
                    figure.segment_count > captured_segments.size() -
                        figure.segment_offset) {
                    latch(failure);
                    return;
                }
                if (figure.segment_count == 0U) {
                    continue;
                }
                const auto figure_segments = captured_segments.subspan(
                    figure.segment_offset, figure.segment_count);
                const auto figure_flags = captured_flags.subspan(
                    figure.segment_offset, figure.segment_count);
                const auto last_point =
                    semantic_path_stroke::segment_end(figure_segments.back());
                const bool needs_closing_segment = figure.closed &&
                    (last_point.x != figure.start.x ||
                        last_point.y != figure.start.y);
                const std::size_t edge_count = figure.segment_count +
                    (needs_closing_segment ? 1U : 0U);
                const auto edge_segment = [&](std::size_t index) {
                    if (index < figure.segment_count) {
                        return figure_segments[index];
                    }
                    progpu_native_path_segment closing{};
                    closing.p0 = last_point;
                    closing.p1 = {figure.start.x, figure.start.y};
                    closing.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                    return closing;
                };
                const auto edge_flags = [&](std::size_t index) {
                    return index < figure.segment_count
                        ? figure_flags[index]
                        : figure.closing_flags;
                };
                const auto edge_stroked = [&](std::size_t index) {
                    return (static_cast<std::uint32_t>(edge_flags(index)) &
                        force_unstroked) == 0U;
                };
                const auto edge_round_join = [&](std::size_t index) {
                    return (static_cast<std::uint32_t>(edge_flags(index)) &
                        force_round) != 0U;
                };
                const auto append_run = [&](std::size_t first,
                                            std::size_t count,
                                            bool closed,
                                            bool start_uses_dash_cap,
                                            bool end_uses_dash_cap) {
                    if (count == 0U) {
                        return;
                    }
                    const std::size_t segment_offset = run_segments.size();
                    const std::size_t smooth_join_offset =
                        run_smooth_joins.size();
                    for (std::size_t index = 0U; index < count; ++index) {
                        run_segments.push_back(
                            edge_segment((first + index) % edge_count));
                    }
                    for (std::size_t index = 0U; index < count; ++index) {
                        const bool smooth = index + 1U < count
                            ? edge_round_join(
                                (first + index + 1U) % edge_count)
                            : closed && edge_round_join(first % edge_count);
                        run_smooth_joins.push_back(smooth ? 1U : 0U);
                    }
                    runs.push_back({
                        segment_offset,
                        count,
                        smooth_join_offset,
                        closed,
                        start_uses_dash_cap,
                        end_uses_dash_cap});
                };

                bool all_stroked = true;
                for (std::size_t index = 0U; index < edge_count; ++index) {
                    all_stroked = all_stroked && edge_stroked(index);
                }
                if (all_stroked) {
                    append_run(0U, edge_count, figure.closed, false, false);
                } else if (figure.closed) {
                    std::size_t gap = 0U;
                    while (gap < edge_count && edge_stroked(gap)) {
                        ++gap;
                    }
                    const std::size_t first_after_gap =
                        (gap + 1U) % edge_count;
                    std::size_t consumed = 0U;
                    while (consumed < edge_count) {
                        while (consumed < edge_count && !edge_stroked(
                                (first_after_gap + consumed) % edge_count)) {
                            ++consumed;
                        }
                        const std::size_t first = consumed;
                        while (consumed < edge_count && edge_stroked(
                                (first_after_gap + consumed) % edge_count)) {
                            ++consumed;
                        }
                        append_run(
                            first_after_gap + first,
                            consumed - first,
                            false,
                            true,
                            true);
                    }
                } else {
                    std::size_t index = 0U;
                    while (index < edge_count) {
                        while (index < edge_count && !edge_stroked(index)) {
                            ++index;
                        }
                        const std::size_t first = index;
                        while (index < edge_count && edge_stroked(index)) {
                            ++index;
                        }
                        append_run(
                            first,
                            index - first,
                            false,
                            first != 0U,
                            index != edge_count);
                    }
                }
            }
            if (runs.empty()) {
                return;
            }

            rectangle_f geometry_bounds{};
            result = geometry_value->GetBounds(nullptr, &geometry_bounds);
            if (com::failed(result) || !valid_rectangle(geometry_bounds)) {
                latch(com::failed(result) ? result : com::invalid_argument);
                return;
            }
            const float padding = stroke_width * 0.5F *
                std::max(1.0F, style.miter_limit);
            const rectangle_f local_bounds{
                geometry_bounds.left - padding,
                geometry_bounds.top - padding,
                geometry_bounds.right + padding,
                geometry_bounds.bottom + padding};
            const progpu_native_image_rect target_bounds =
                transformed_bounds(local_bounds);
            if (com::failed(failure_)) {
                return;
            }

            bool bitmap_brush = false;
            scene_bitmap_brush_native* raw_bitmap_brush = nullptr;
            const com::result bitmap_query = brush_value->QueryInterface(
                scene_bitmap_brush_native_interface_id,
                reinterpret_cast<void**>(&raw_bitmap_brush));
            com::pointer<scene_bitmap_brush_native> bitmap_identity;
            bitmap_identity.attach(raw_bitmap_brush);
            if (com::succeeded(bitmap_query) && bitmap_identity) {
                bitmap_brush = true;
            } else if (bitmap_query != com::no_interface) {
                latch(com::failed(bitmap_query) ? bitmap_query : failure);
                return;
            }

            bool use_polyline_batch = !bitmap_brush;
            for (const auto& run : runs) {
                const auto segments = std::span(run_segments).subspan(
                    run.segment_offset, run.segment_count);
                const auto smooth_joins = std::span(run_smooth_joins).subspan(
                    run.smooth_join_offset, run.segment_count);
                use_polyline_batch = use_polyline_batch &&
                    (!run.closed || run.segment_count >= 2U) &&
                    std::all_of(
                        segments.begin(),
                        segments.end(),
                        [](const progpu_native_path_segment& segment) {
                            return segment.kind ==
                                PROGPU_NATIVE_PATH_SEGMENT_LINE;
                        }) &&
                    (style.join == line_join::round ||
                        std::none_of(
                            smooth_joins.begin(),
                            smooth_joins.end(),
                            [](std::uint8_t smooth) {
                                return smooth != 0U;
                            }));
            }

            if (use_polyline_batch) {
                std::vector<progpu_native_scene_stroke> strokes;
                std::vector<progpu_native_point> points;
                std::vector<double> doubles;
                std::vector<std::uint32_t> brush_indices;
                strokes.reserve(runs.size());
                brush_indices.reserve(runs.size());
                points.reserve(run_segments.size() + runs.size());
                if (!style.dash_intervals.empty()) {
                    doubles.reserve(style.dash_intervals.size() * runs.size());
                }
                for (const auto& run : runs) {
                    const auto segments = std::span(run_segments).subspan(
                        run.segment_offset, run.segment_count);
                    progpu_native_scene_stroke stroke{};
                    stroke.struct_size = sizeof(stroke);
                    stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
                    stroke.flags = run.closed
                        ? static_cast<std::uint32_t>(
                            PROGPU_NATIVE_POLYLINE_FLAG_CLOSED)
                        : 0U;
                    stroke.point_offset = points.size();
                    stroke.point_count = segments.size() +
                        (run.closed ? 0U : 1U);
                    stroke.dash_interval_offset = doubles.size();
                    stroke.dash_interval_count = style.dash_intervals.size();
                    stroke.color = {1.0F, 1.0F, 1.0F, 1.0F};
                    stroke.transform = native_transform();
                    stroke.stroke_thickness = stroke_width;
                    stroke.miter_limit = std::max(1.0F, style.miter_limit);
                    stroke.dash_offset = style.dash_offset;
                    stroke.start_cap = run.start_uses_dash_cap
                        ? static_cast<std::uint32_t>(style.dash_cap)
                        : static_cast<std::uint32_t>(style.start_cap);
                    stroke.end_cap = run.end_uses_dash_cap
                        ? static_cast<std::uint32_t>(style.dash_cap)
                        : static_cast<std::uint32_t>(style.end_cap);
                    stroke.line_join = style.join == line_join::miter_or_bevel
                        ? static_cast<std::uint32_t>(
                            PROGPU_NATIVE_STROKE_JOIN_MITER)
                        : static_cast<std::uint32_t>(style.join);
                    stroke.dash_cap =
                        static_cast<std::uint32_t>(style.dash_cap);
                    points.push_back(segments.front().p0);
                    const std::size_t end_count = segments.size() -
                        (run.closed ? 1U : 0U);
                    for (std::size_t index = 0U; index < end_count; ++index) {
                        points.push_back(
                            semantic_path_stroke::segment_end(segments[index]));
                    }
                    doubles.insert(
                        doubles.end(),
                        style.dash_intervals.begin(),
                        style.dash_intervals.end());
                    strokes.push_back(stroke);
                    brush_indices.push_back(PROGPU_NATIVE_SCENE_NO_INDEX);
                }
                std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                if (!add_brush(brush_value, brush_index)) {
                    return;
                }
                std::fill(
                    brush_indices.begin(), brush_indices.end(), brush_index);
                if (!builder_.draw_strokes(
                        strokes, points, doubles, brush_indices, target_bounds)) {
                    latch(builder_failure());
                    return;
                }
            } else {
                semantic_path_stroke::style semantic_style{};
                semantic_style.transform = native_transform();
                semantic_style.thickness = stroke_width;
                semantic_style.miter_limit =
                    std::max(1.0F, style.miter_limit);
                semantic_style.dash_offset = style.dash_offset;
                semantic_style.dash_cap =
                    static_cast<std::uint32_t>(style.dash_cap);
                semantic_style.line_join = style.join ==
                        line_join::miter_or_bevel
                    ? static_cast<std::uint32_t>(
                        PROGPU_NATIVE_STROKE_JOIN_MITER)
                    : static_cast<std::uint32_t>(style.join);
                semantic_style.primitive_flags = primitive_flags();
                mil::curve_dash::run_buffer dash_scratch;
                std::vector<progpu_native_geometry_primitive> primitives;
                std::vector<std::uint32_t> brush_indices;
                primitives.reserve(
                    run_segments.size() * 2U + runs.size() * 2U);
                brush_indices.reserve(primitives.capacity());
                for (const auto& run : runs) {
                    semantic_style.start_cap = run.start_uses_dash_cap
                        ? static_cast<std::uint32_t>(style.dash_cap)
                        : static_cast<std::uint32_t>(style.start_cap);
                    semantic_style.end_cap = run.end_uses_dash_cap
                        ? static_cast<std::uint32_t>(style.dash_cap)
                        : static_cast<std::uint32_t>(style.end_cap);
                    const auto compile_result = semantic_path_stroke::compile(
                        std::span(run_segments).subspan(
                            run.segment_offset, run.segment_count),
                        std::span(run_smooth_joins).subspan(
                            run.smooth_join_offset, run.segment_count),
                        run.closed,
                        style.dash_intervals,
                        semantic_style,
                        PROGPU_NATIVE_SCENE_NO_INDEX,
                        dash_scratch,
                        primitives,
                        brush_indices);
                    if (compile_result ==
                        semantic_path_stroke::result::capacity_exceeded) {
                        latch(com::out_of_memory);
                        return;
                    }
                    if (compile_result !=
                        semantic_path_stroke::result::success) {
                        latch(failure);
                        return;
                    }
                }
                if (primitives.empty()) {
                    return;
                }
                if (bitmap_brush) {
                    const bitmap_brush_draw_result bitmap_result =
                        draw_bitmap_brush_geometry(
                            brush_value, primitives, local_bounds);
                    if (bitmap_result == bitmap_brush_draw_result::not_bitmap) {
                        latch(failure);
                    }
                    return;
                }
                std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                if (!add_brush(brush_value, brush_index)) {
                    return;
                }
                std::fill(
                    brush_indices.begin(), brush_indices.end(), brush_index);
                if (!builder_.draw_geometry(
                        primitives, brush_indices, target_bounds)) {
                    latch(builder_failure());
                    return;
                }
            }
            ++draw_count_;
        } catch (const std::bad_alloc&) {
            latch(com::out_of_memory);
        } catch (...) {
            latch(failure);
        }
    }

    [[nodiscard]] bool set_gradient_coordinate_transform(
        brush* source,
        progpu_native_scene_brush& destination) noexcept
    {
        matrix_3x2_f brush_transform{};
        source->GetTransform(&brush_transform);
        matrix_3x2_f inverse_draw{};
        matrix_3x2_f inverse_brush{};
        if (!core::valid_transform(&brush_transform) ||
            !try_invert_transform(transform_, inverse_draw) ||
            !try_invert_transform(brush_transform, inverse_brush)) {
            latch(com::invalid_argument);
            return false;
        }
        const matrix_3x2_f coordinate =
            compose_transform(inverse_draw, inverse_brush);
        if (!core::valid_transform(&coordinate)) {
            latch(com::invalid_argument);
            return false;
        }
        destination.coordinate_transform0[0] = coordinate.m11;
        destination.coordinate_transform0[1] = coordinate.m21;
        destination.coordinate_transform0[2] = coordinate.m31;
        destination.coordinate_transform1[0] = coordinate.m12;
        destination.coordinate_transform1[1] = coordinate.m22;
        destination.coordinate_transform1[2] = coordinate.m32;
        return true;
    }

    [[nodiscard]] bool translate_gradient_brush(
        brush* source,
        gradient_stop_collection* collection,
        progpu_native_scene_brush& native,
        std::vector<progpu_native_scene_gradient_stop>& native_stops) noexcept
    {
        if (collection == nullptr) {
            latch(com::invalid_argument);
            return false;
        }
        factory* raw_factory = nullptr;
        collection->GetFactory(&raw_factory);
        com::pointer<factory> collection_factory;
        collection_factory.attach(raw_factory);
        if (collection_factory.get() != owner_.get()) {
            latch(wrong_factory);
            return false;
        }
        const std::uint32_t stop_count = collection->GetGradientStopCount();
        if (stop_count == 0U ||
            stop_count > PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS) {
            latch(com::invalid_argument);
            return false;
        }
        switch (collection->GetExtendMode()) {
        case extend_mode::clamp:
            native.spread_method = PROGPU_NATIVE_SCENE_GRADIENT_PAD;
            break;
        case extend_mode::wrap:
            native.spread_method = PROGPU_NATIVE_SCENE_GRADIENT_REPEAT;
            break;
        case extend_mode::mirror:
            native.spread_method = PROGPU_NATIVE_SCENE_GRADIENT_REFLECT;
            break;
        default:
            latch(com::invalid_argument);
            return false;
        }
        switch (collection->GetColorInterpolationGamma()) {
        case gamma::gamma_2_2:
            native.color_interpolation_mode =
                PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB;
            break;
        case gamma::gamma_1_0:
            native.color_interpolation_mode =
                PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SCRGB;
            break;
        default:
            latch(com::invalid_argument);
            return false;
        }
        try {
            std::vector<gradient_stop> stops(stop_count);
            collection->GetGradientStops(stops.data(), stop_count);
            native_stops.clear();
            native_stops.reserve(stop_count);
            float previous = -std::numeric_limits<float>::infinity();
            for (const gradient_stop& stop : stops) {
                if (!std::isfinite(stop.position) || stop.position < 0.0F ||
                    stop.position > 1.0F || stop.position < previous ||
                    !valid_color(stop.color)) {
                    latch(com::invalid_argument);
                    return false;
                }
                native_stops.push_back({
                    {stop.color.red, stop.color.green, stop.color.blue,
                        stop.color.alpha},
                    stop.position,
                    0U,
                    0U,
                    0U});
                previous = stop.position;
            }
            native.stop_count = stop_count;
            const std::size_t inline_count = std::min<std::size_t>(
                native_stops.size(), 8U);
            for (std::size_t index = 0U; index < inline_count; ++index) {
                native.colors[index] = native_stops[index].color;
                if (index < 4U) {
                    native.offsets0[index] = native_stops[index].offset;
                } else {
                    native.offsets1[index - 4U] = native_stops[index].offset;
                }
            }
            if (!set_gradient_coordinate_transform(source, native)) {
                return false;
            }
            return true;
        } catch (const std::bad_alloc&) {
            latch(com::out_of_memory);
            return false;
        } catch (...) {
            latch(failure);
            return false;
        }
    }

    [[nodiscard]] bool add_gradient_brush(
        brush* source,
        gradient_stop_collection* collection,
        progpu_native_scene_brush& native,
        std::uint32_t& brush_index) noexcept
    {
        std::vector<progpu_native_scene_gradient_stop> native_stops;
        if (!translate_gradient_brush(
                source, collection, native, native_stops)) {
            return false;
        }
        if (!builder_.add_brush(native, native_stops, brush_index)) {
            latch(builder_failure());
            return false;
        }
        return true;
    }

    [[nodiscard]] bool add_brush(
        brush* brush_value,
        std::uint32_t& brush_index) noexcept
    {
        if (brush_value == nullptr) {
            latch(com::invalid_argument);
            return false;
        }
        factory* raw_factory = nullptr;
        brush_value->GetFactory(&raw_factory);
        com::pointer<factory> brush_factory;
        brush_factory.attach(raw_factory);
        if (brush_factory.get() != owner_.get()) {
            latch(wrong_factory);
            return false;
        }

        linear_gradient_brush* raw_linear = nullptr;
        const com::result linear_query = brush_value->QueryInterface(
            linear_gradient_brush_interface_id,
            reinterpret_cast<void**>(&raw_linear));
        com::pointer<linear_gradient_brush> linear;
        linear.attach(raw_linear);
        if (com::succeeded(linear_query) && linear) {
            const point_2f start = linear->GetStartPoint();
            const point_2f end = linear->GetEndPoint();
            const float opacity = linear->GetOpacity();
            if (!valid_point(start) || !valid_point(end) ||
                !valid_opacity(opacity)) {
                latch(com::invalid_argument);
                return false;
            }
            gradient_stop_collection* raw_collection = nullptr;
            linear->GetGradientStopCollection(&raw_collection);
            com::pointer<gradient_stop_collection> collection;
            collection.attach(raw_collection);
            progpu_native_scene_brush native{};
            native.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
            native.opacity = opacity;
            native.start_point = {start.x, start.y};
            native.end_point = {end.x, end.y};
            return add_gradient_brush(
                linear.get(), collection.get(), native, brush_index);
        }

        radial_gradient_brush* raw_radial = nullptr;
        const com::result radial_query = brush_value->QueryInterface(
            radial_gradient_brush_interface_id,
            reinterpret_cast<void**>(&raw_radial));
        com::pointer<radial_gradient_brush> radial;
        radial.attach(raw_radial);
        if (com::succeeded(radial_query) && radial) {
            const point_2f center = radial->GetCenter();
            const point_2f offset = radial->GetGradientOriginOffset();
            const point_2f origin{center.x + offset.x, center.y + offset.y};
            const float radius_x = radial->GetRadiusX();
            const float radius_y = radial->GetRadiusY();
            const float opacity = radial->GetOpacity();
            if (!valid_point(center) || !valid_point(offset) ||
                !valid_point(origin) || !std::isfinite(radius_x) ||
                !std::isfinite(radius_y) || radius_x < 0.0F ||
                radius_y < 0.0F ||
                (radius_x == 0.0F && radius_y == 0.0F) ||
                !valid_opacity(opacity)) {
                latch(com::invalid_argument);
                return false;
            }
            gradient_stop_collection* raw_collection = nullptr;
            radial->GetGradientStopCollection(&raw_collection);
            com::pointer<gradient_stop_collection> collection;
            collection.attach(raw_collection);
            progpu_native_scene_brush native{};
            native.type = PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT;
            native.opacity = opacity;
            native.start_point = {origin.x, origin.y};
            native.center = {center.x, center.y};
            native.radius = radius_x;
            native.radius_y = radius_y;
            return add_gradient_brush(
                radial.get(), collection.get(), native, brush_index);
        }

        solid_color_brush* raw_solid = nullptr;
        const com::result query = brush_value->QueryInterface(
            solid_color_brush_interface_id,
            reinterpret_cast<void**>(&raw_solid));
        com::pointer<solid_color_brush> solid;
        solid.attach(raw_solid);
        if (com::failed(query) || !solid) {
            latch(not_implemented);
            return false;
        }
        const color_f color = solid->GetColor();
        const float opacity = solid->GetOpacity();
        if (!valid_color(color) || !std::isfinite(opacity) ||
            opacity < 0.0F || opacity > 1.0F) {
            latch(com::invalid_argument);
            return false;
        }
        if (!builder_.add_solid_brush(
                {color.red, color.green, color.blue, color.alpha},
                opacity,
                brush_index)) {
            latch(builder_failure());
            return false;
        }
        return true;
    }

    [[nodiscard]] progpu_native_affine_2d native_transform() const noexcept
    {
        return {
            transform_.m11,
            transform_.m12,
            transform_.m21,
            transform_.m22,
            transform_.m31,
            transform_.m32};
    }

    [[nodiscard]] std::uint32_t primitive_flags() const noexcept
    {
        return antialias_mode_ == antialias_mode::aliased
            ? static_cast<std::uint32_t>(
                PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED)
            : 0U;
    }

    [[nodiscard]] progpu_native_image_rect transformed_bounds(
        const rectangle_f& local_bounds) noexcept
    {
        rectangle_f edges{};
        const core::rectangle_geometry bounds_geometry(local_bounds);
        if (com::failed(bounds_geometry.bounds(&transform_, &edges))) {
            latch(com::invalid_argument);
            return {};
        }
        return {
            edges.left,
            edges.top,
            edges.right - edges.left,
            edges.bottom - edges.top};
    }

    void draw_analytic_rectangle(
        const rectangle_f* rectangle,
        brush* brush_value,
        float stroke_width,
        stroke_style* style,
        bool fill) noexcept
    {
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        if (rectangle == nullptr || !valid_rectangle(*rectangle) ||
            !std::isfinite(stroke_width) || stroke_width < 0.0F ||
            (!fill && stroke_width == 0.0F) ||
            (stroke_width > 0.0F && style != nullptr)) {
            latch(style != nullptr ? not_implemented : com::invalid_argument);
            return;
        }
        progpu_native_analytic_primitive primitive{};
        primitive.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
        primitive.flags = primitive_flags();
        primitive.x = rectangle->left;
        primitive.y = rectangle->top;
        primitive.width = rectangle->right - rectangle->left;
        primitive.height = rectangle->bottom - rectangle->top;
        primitive.stroke_thickness = stroke_width;
        primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
        primitive.transform = native_transform();
        const float radius = stroke_width * 0.5F;
        const rectangle_f local_bounds{
            rectangle->left - radius,
            rectangle->top - radius,
            rectangle->right + radius,
            rectangle->bottom + radius};
        std::array<progpu_native_geometry_primitive, 4U> mask_primitives{};
        std::size_t mask_primitive_count = fill ? 1U : 4U;
        if (fill) {
            auto& mask = mask_primitives[0];
            mask.kind = PROGPU_NATIVE_GEOMETRY_QUADRILATERAL;
            mask.flags = primitive_flags();
            mask.p0 = {rectangle->left, rectangle->top};
            mask.p1 = {rectangle->right, rectangle->top};
            mask.p2 = {rectangle->right, rectangle->bottom};
            mask.p3 = {rectangle->left, rectangle->bottom};
            mask.color = {1.0F, 1.0F, 1.0F, 1.0F};
            mask.transform = native_transform();
        } else {
            constexpr std::array<std::array<std::size_t, 2U>, 4U> edges{{
                {0U, 1U}, {1U, 2U}, {2U, 3U}, {3U, 0U}}};
            const std::array points{
                progpu_native_point{rectangle->left, rectangle->top},
                progpu_native_point{rectangle->right, rectangle->top},
                progpu_native_point{rectangle->right, rectangle->bottom},
                progpu_native_point{rectangle->left, rectangle->bottom}};
            for (std::size_t index = 0U; index < edges.size(); ++index) {
                auto& mask = mask_primitives[index];
                mask.kind = PROGPU_NATIVE_GEOMETRY_LINE;
                mask.flags = primitive_flags();
                mask.p0 = points[edges[index][0]];
                mask.p1 = points[edges[index][1]];
                mask.stroke_thickness = stroke_width;
                mask.color = {1.0F, 1.0F, 1.0F, 1.0F};
                mask.transform = native_transform();
            }
        }
        const bitmap_brush_draw_result bitmap_result =
            draw_bitmap_brush_geometry(
                brush_value,
                std::span<const progpu_native_geometry_primitive>(
                    mask_primitives.data(), mask_primitive_count),
                local_bounds);
        if (bitmap_result != bitmap_brush_draw_result::not_bitmap) {
            return;
        }
        std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!add_brush(brush_value, brush_index)) {
            return;
        }
        const progpu_native_image_rect bounds = transformed_bounds(local_bounds);
        if (com::failed(failure_)) {
            return;
        }
        if (!builder_.draw_analytic(
                std::span<const progpu_native_analytic_primitive>(
                    &primitive, 1U),
                std::span<const std::uint32_t>(&brush_index, 1U),
                bounds)) {
            latch(builder_failure());
            return;
        }
        ++draw_count_;
    }

    void draw_rounded_rectangle(
        const rounded_rectangle* rectangle,
        brush* brush_value,
        float stroke_width,
        stroke_style* style,
        bool fill) noexcept
    {
        if (rectangle == nullptr || !std::isfinite(rectangle->radius_x) ||
            !std::isfinite(rectangle->radius_y) ||
            rectangle->radius_x < 0.0F || rectangle->radius_y < 0.0F) {
            const std::lock_guard lock(mutex_);
            if (can_draw()) {
                latch(com::invalid_argument);
            }
            return;
        }
        if (rectangle->radius_x != rectangle->radius_y) {
            const std::lock_guard lock(mutex_);
            if (can_draw()) {
                latch(not_implemented);
            }
            return;
        }
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        if (!valid_rectangle(rectangle->rectangle) ||
            !std::isfinite(stroke_width) || stroke_width < 0.0F ||
            (!fill && stroke_width == 0.0F)) {
            latch(com::invalid_argument);
            return;
        }
        if (style != nullptr) {
            latch(not_implemented);
            return;
        }
        progpu_native_analytic_primitive primitive{};
        primitive.kind = PROGPU_NATIVE_PRIMITIVE_ROUNDED_RECTANGLE;
        primitive.flags = primitive_flags();
        primitive.x = rectangle->rectangle.left;
        primitive.y = rectangle->rectangle.top;
        primitive.width = rectangle->rectangle.right - rectangle->rectangle.left;
        primitive.height = rectangle->rectangle.bottom - rectangle->rectangle.top;
        primitive.corner_radius = rectangle->radius_x;
        primitive.stroke_thickness = stroke_width;
        primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
        primitive.transform = native_transform();
        const float radius = stroke_width * 0.5F;
        const rectangle_f local_bounds{
            rectangle->rectangle.left - radius,
            rectangle->rectangle.top - radius,
            rectangle->rectangle.right + radius,
            rectangle->rectangle.bottom + radius};
        bitmap_brush_draw_result bitmap_result =
            bitmap_brush_draw_result::not_bitmap;
        if (fill) {
            bitmap_result = draw_bitmap_brush_analytic_mask(
                brush_value,
                rectangle->rectangle,
                rectangle->radius_x,
                rectangle->radius_y);
        } else {
            std::array<progpu_native_geometry_primitive, 8U>
                mask_primitives{};
            std::size_t mask_count = 0U;
            const float width = rectangle->rectangle.right -
                rectangle->rectangle.left;
            const float height = rectangle->rectangle.bottom -
                rectangle->rectangle.top;
            const float radius_x = std::min(
                rectangle->radius_x, width * 0.5F);
            const float radius_y = std::min(
                rectangle->radius_y, height * 0.5F);
            const auto append_line = [&](point_2f start, point_2f end) {
                if (start.x == end.x && start.y == end.y) {
                    return;
                }
                auto& mask = mask_primitives[mask_count++];
                mask.kind = PROGPU_NATIVE_GEOMETRY_LINE;
                mask.flags = primitive_flags();
                mask.p0 = {start.x, start.y};
                mask.p1 = {end.x, end.y};
                mask.stroke_thickness = stroke_width;
                mask.color = {1.0F, 1.0F, 1.0F, 1.0F};
                mask.transform = native_transform();
            };
            append_line(
                {rectangle->rectangle.left + radius_x,
                    rectangle->rectangle.top},
                {rectangle->rectangle.right - radius_x,
                    rectangle->rectangle.top});
            append_line(
                {rectangle->rectangle.right,
                    rectangle->rectangle.top + radius_y},
                {rectangle->rectangle.right,
                    rectangle->rectangle.bottom - radius_y});
            append_line(
                {rectangle->rectangle.right - radius_x,
                    rectangle->rectangle.bottom},
                {rectangle->rectangle.left + radius_x,
                    rectangle->rectangle.bottom});
            append_line(
                {rectangle->rectangle.left,
                    rectangle->rectangle.bottom - radius_y},
                {rectangle->rectangle.left,
                    rectangle->rectangle.top + radius_y});
            if (radius_x > 0.0F && radius_y > 0.0F) {
                constexpr std::array<float, 4U> starts{
                    -std::numbers::pi_v<float> * 0.5F,
                    0.0F,
                    std::numbers::pi_v<float> * 0.5F,
                    std::numbers::pi_v<float>};
                const std::array centers{
                    point_2f{
                        rectangle->rectangle.right - radius_x,
                        rectangle->rectangle.top + radius_y},
                    point_2f{
                        rectangle->rectangle.right - radius_x,
                        rectangle->rectangle.bottom - radius_y},
                    point_2f{
                        rectangle->rectangle.left + radius_x,
                        rectangle->rectangle.bottom - radius_y},
                    point_2f{
                        rectangle->rectangle.left + radius_x,
                        rectangle->rectangle.top + radius_y}};
                for (std::size_t index = 0U; index < centers.size(); ++index) {
                    auto& mask = mask_primitives[mask_count++];
                    mask.kind = PROGPU_NATIVE_GEOMETRY_ARC;
                    mask.flags = primitive_flags();
                    mask.p0 = {centers[index].x, centers[index].y};
                    mask.p1 = {radius_x, 0.0F};
                    mask.p2 = {0.0F, radius_y};
                    mask.p3 = {
                        starts[index],
                        std::numbers::pi_v<float> * 0.5F};
                    mask.stroke_thickness = stroke_width;
                    mask.color = {1.0F, 1.0F, 1.0F, 1.0F};
                    mask.transform = native_transform();
                }
            }
            bitmap_result = draw_bitmap_brush_geometry(
                brush_value,
                std::span<const progpu_native_geometry_primitive>(
                    mask_primitives.data(), mask_count),
                local_bounds);
        }
        if (bitmap_result != bitmap_brush_draw_result::not_bitmap) {
            return;
        }
        std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!add_brush(brush_value, brush_index)) {
            return;
        }
        const progpu_native_image_rect bounds = transformed_bounds(local_bounds);
        if (com::failed(failure_)) {
            return;
        }
        if (!builder_.draw_analytic(
                std::span<const progpu_native_analytic_primitive>(
                    &primitive, 1U),
                std::span<const std::uint32_t>(&brush_index, 1U),
                bounds)) {
            latch(builder_failure());
            return;
        }
        ++draw_count_;
    }

    void draw_ellipse(
        const ellipse* ellipse_value,
        brush* brush_value,
        float stroke_width,
        stroke_style* style,
        bool fill) noexcept
    {
        const std::lock_guard lock(mutex_);
        if (!can_draw()) {
            return;
        }
        if (ellipse_value == nullptr || !valid_point(ellipse_value->point) ||
            !std::isfinite(ellipse_value->radius_x) ||
            !std::isfinite(ellipse_value->radius_y) ||
            ellipse_value->radius_x < 0.0F ||
            ellipse_value->radius_y < 0.0F ||
            !std::isfinite(stroke_width) || stroke_width < 0.0F ||
            (!fill && stroke_width == 0.0F)) {
            latch(com::invalid_argument);
            return;
        }
        if (style != nullptr) {
            latch(not_implemented);
            return;
        }
        progpu_native_analytic_primitive primitive{};
        primitive.kind = PROGPU_NATIVE_PRIMITIVE_ELLIPSE;
        primitive.flags = primitive_flags();
        primitive.x = ellipse_value->point.x - ellipse_value->radius_x;
        primitive.y = ellipse_value->point.y - ellipse_value->radius_y;
        primitive.width = ellipse_value->radius_x * 2.0F;
        primitive.height = ellipse_value->radius_y * 2.0F;
        primitive.stroke_thickness = stroke_width;
        primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
        primitive.transform = native_transform();
        const float radius = stroke_width * 0.5F;
        const rectangle_f local_bounds{
            primitive.x - radius,
            primitive.y - radius,
            primitive.x + primitive.width + radius,
            primitive.y + primitive.height + radius};
        bitmap_brush_draw_result bitmap_result =
            bitmap_brush_draw_result::not_bitmap;
        if (fill) {
            const rectangle_f ellipse_bounds{
                primitive.x,
                primitive.y,
                primitive.x + primitive.width,
                primitive.y + primitive.height};
            bitmap_result = draw_bitmap_brush_analytic_mask(
                brush_value,
                ellipse_bounds,
                ellipse_value->radius_x,
                ellipse_value->radius_y);
        } else {
            progpu_native_geometry_primitive mask{};
            mask.kind = PROGPU_NATIVE_GEOMETRY_ARC;
            mask.flags = primitive_flags();
            mask.p0 = {
                ellipse_value->point.x,
                ellipse_value->point.y};
            mask.p1 = {ellipse_value->radius_x, 0.0F};
            mask.p2 = {0.0F, ellipse_value->radius_y};
            mask.p3 = {0.0F, std::numbers::pi_v<float> * 2.0F};
            mask.stroke_thickness = stroke_width;
            mask.color = {1.0F, 1.0F, 1.0F, 1.0F};
            mask.transform = native_transform();
            bitmap_result = draw_bitmap_brush_geometry(
                brush_value,
                std::span<const progpu_native_geometry_primitive>(
                    &mask, 1U),
                local_bounds);
        }
        if (bitmap_result != bitmap_brush_draw_result::not_bitmap) {
            return;
        }
        std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!add_brush(brush_value, brush_index)) {
            return;
        }
        const progpu_native_image_rect bounds = transformed_bounds(local_bounds);
        if (com::failed(failure_)) {
            return;
        }
        if (!builder_.draw_analytic(
                std::span<const progpu_native_analytic_primitive>(
                    &primitive, 1U),
                std::span<const std::uint32_t>(&brush_index, 1U),
                bounds)) {
            latch(builder_failure());
            return;
        }
        ++draw_count_;
    }

    void publish_tags(
        std::uint64_t* tag1,
        std::uint64_t* tag2) const noexcept
    {
        if (tag1 != nullptr) {
            *tag1 = tag1_;
        }
        if (tag2 != nullptr) {
            *tag2 = tag2_;
        }
    }

    void release_active_layers() noexcept
    {
        for (auto& active_layer : layer_stack_) {
            if (active_layer) {
                active_layer->EndUse(this);
                active_layer.reset();
            }
        }
    }

    friend class com::atomic_reference_count<portable_scene_render_target>;
    ~portable_scene_render_target()
    {
        release_active_layers();
    }

    com::atomic_reference_count<portable_scene_render_target> reference_count_;
    com::pointer<factory> owner_;
    mutable std::mutex mutex_;
    semantic_scene_builder builder_;
    std::vector<bitmap_resource_entry> bitmap_resources_;
    std::uint64_t scene_id_ = 0U;
    std::uint64_t generation_ = 1U;
    std::uint64_t tag1_ = 0U;
    std::uint64_t tag2_ = 0U;
    std::uint32_t pixel_width_ = 0U;
    std::uint32_t pixel_height_ = 0U;
    std::uint32_t draw_count_ = 0U;
    static constexpr std::uint8_t scope_none = 0U;
    static constexpr std::uint8_t scope_axis_aligned_clip = 1U;
    static constexpr std::uint8_t scope_opacity_layer = 2U;
    static constexpr std::uint8_t scope_antialiased_axis_clip = 3U;
    std::array<progpu_native_image_rect,
        PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> clip_stack_{};
    std::array<std::uint8_t,
        PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> scope_stack_{};
    std::array<com::pointer<scene_layer_native>,
        PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> layer_stack_{};
    std::size_t clip_depth_ = 0U;
    std::size_t scope_depth_ = 0U;
    float dpi_x_ = 96.0F;
    float dpi_y_ = 96.0F;
    matrix_3x2_f transform_ = identity_transform;
    antialias_mode antialias_mode_ = antialias_mode::per_primitive;
    text_antialias_mode text_antialias_mode_ =
        text_antialias_mode::default_value;
    color_f clear_color_{};
    com::result failure_ = com::ok;
    bool begun_ = false;
    bool ended_ = false;
    bool has_clear_ = false;
};

} // namespace

com::result create_scene_render_target(
    factory* owner,
    const scene_render_target_properties* properties,
    render_target** value) noexcept
{
    if (value == nullptr) {
        return com::pointer_error;
    }
    *value = nullptr;
    if (owner == nullptr || properties == nullptr ||
        properties->pixel_width == 0U || properties->pixel_height == 0U ||
        properties->scene_id == 0U || properties->generation == 0U ||
        !valid_dpi(properties->dpi_x, properties->dpi_y)) {
        return com::invalid_argument;
    }
    auto* created = new (std::nothrow) portable_scene_render_target(
        owner, *properties);
    if (created == nullptr) {
        return com::out_of_memory;
    }
    *value = created;
    return com::ok;
}

} // namespace progpu::native::direct2d::compat::detail
