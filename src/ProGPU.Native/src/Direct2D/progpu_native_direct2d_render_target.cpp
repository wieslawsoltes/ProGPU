#include "progpu_native_direct2d_render_target.hpp"

#include "progpu_native_scene_builder.hpp"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <limits>
#include <mutex>
#include <new>
#include <span>
#include <utility>
#include <vector>

namespace progpu::native::direct2d::compat::detail {
namespace {

constexpr matrix_3x2_f identity_transform{
    1.0F, 0.0F, 0.0F, 1.0F, 0.0F, 0.0F};

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

[[nodiscard]] bool valid_dpi(float dpi_x, float dpi_y) noexcept
{
    return std::isfinite(dpi_x) && std::isfinite(dpi_y) &&
        dpi_x > 0.0F && dpi_y > 0.0F;
}

[[nodiscard]] bool valid_opacity(float value) noexcept
{
    return std::isfinite(value) && value >= 0.0F && value <= 1.0F;
}

[[nodiscard]] bool valid_brush_properties(
    const brush_properties& value) noexcept
{
    return valid_opacity(value.opacity) &&
        core::valid_transform(&value.transform);
}

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
        size_u,
        const void*,
        std::uint32_t,
        const bitmap_properties*,
        bitmap** value) noexcept override
    {
        return unsupported_output(value);
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
        bitmap*,
        const bitmap_brush_properties*,
        const brush_properties*,
        bitmap_brush** value) noexcept override
    {
        return unsupported_output(value);
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
        const size_f*,
        layer** value) noexcept override
    {
        return unsupported_output(value);
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
        std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!add_brush(brush_value, brush_index)) {
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
        geometry*, brush*, float, stroke_style*) noexcept override
    {
        unsupported_draw();
    }

    void PROGPU_NATIVE_COM_CALL FillGeometry(
        geometry*, brush*, brush*) noexcept override
    {
        unsupported_draw();
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
        bitmap*, const rectangle_f*, float, bitmap_interpolation_mode,
        const rectangle_f*) noexcept override
    {
        unsupported_draw();
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
        const layer_parameters*, layer*) noexcept override
    {
        unsupported_draw();
    }

    void PROGPU_NATIVE_COM_CALL PopLayer() noexcept override
    {
        unsupported_draw();
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
        const rectangle_f*, antialias_mode) noexcept override
    {
        unsupported_draw();
    }

    void PROGPU_NATIVE_COM_CALL PopAxisAlignedClip() noexcept override
    {
        unsupported_draw();
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
        if (!builder_.reset(scene_id_, generation_)) {
            failure_ = builder_failure();
            return;
        }
        transform_ = identity_transform;
        antialias_mode_ = antialias_mode::per_primitive;
        text_antialias_mode_ = text_antialias_mode::default_value;
        tag1_ = 0U;
        tag2_ = 0U;
        clear_color_ = {};
        draw_count_ = 0U;
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

    [[nodiscard]] bool add_gradient_brush(
        brush* source,
        gradient_stop_collection* collection,
        progpu_native_scene_brush& native,
        std::uint32_t& brush_index) noexcept
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
            std::vector<progpu_native_scene_gradient_stop> native_stops;
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
            if (!builder_.add_brush(native, native_stops, brush_index)) {
                latch(builder_failure());
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
        std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!add_brush(brush_value, brush_index)) {
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
        std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!add_brush(brush_value, brush_index)) {
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
        std::uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!add_brush(brush_value, brush_index)) {
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

    friend class com::atomic_reference_count<portable_scene_render_target>;
    ~portable_scene_render_target() = default;

    com::atomic_reference_count<portable_scene_render_target> reference_count_;
    com::pointer<factory> owner_;
    mutable std::mutex mutex_;
    semantic_scene_builder builder_;
    std::uint64_t scene_id_ = 0U;
    std::uint64_t generation_ = 1U;
    std::uint64_t tag1_ = 0U;
    std::uint64_t tag2_ = 0U;
    std::uint32_t pixel_width_ = 0U;
    std::uint32_t pixel_height_ = 0U;
    std::uint32_t draw_count_ = 0U;
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
