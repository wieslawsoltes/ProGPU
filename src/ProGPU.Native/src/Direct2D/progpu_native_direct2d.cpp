#include "progpu_native_direct2d.h"
#include "progpu_native_com.hpp"
#include "progpu_native_direct2d_core.hpp"
#include "progpu_native_direct2d_drawing_state.hpp"
#include "progpu_native_direct2d_path.hpp"
#include "progpu_native_direct2d_rectangle.hpp"
#include "progpu_native_direct2d_render_target.hpp"
#include "progpu_native_scene_builder.hpp"
#include "../Scene/progpu_native_semantic_path_stroke.hpp"

#include <d2d1_3.h>
#include <d3d11_4.h>
#include <dwrite_3.h>
#include <dxgi1_2.h>
#include <roapi.h>
#include <windows.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winstring.h>

#include <algorithm>
#include <atomic>
#include <array>
#include <cmath>
#include <cstring>
#include <iterator>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <span>
#include <string>
#include <utility>
#include <vector>

template<typename Interface>
using ComPtr = progpu::native::com::pointer<Interface>;

namespace direct2d_core = progpu::native::direct2d::core;
namespace direct2d_compat = progpu::native::direct2d::compat;

MIDL_INTERFACE("A27F0B5D-EC2C-4D4F-948F-0AA1E95E33E6")
IProGpuWin2DCanvasDevice : public IInspectable {
};

MIDL_INTERFACE("695C440D-04B3-4EDD-BFD9-63E51E9F7202")
IProGpuWin2DCanvasFactoryNative : public IInspectable {
public:
    virtual HRESULT STDMETHODCALLTYPE GetOrCreate(
        IProGpuWin2DCanvasDevice* canvas_device,
        IUnknown* resource,
        float dpi,
        IInspectable** wrapper) = 0;
};

MIDL_INTERFACE("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A")
IProGpuWin2DCanvasResourceWrapperNative : public IUnknown {
public:
    virtual HRESULT STDMETHODCALLTYPE GetNativeResource(
        IProGpuWin2DCanvasDevice* canvas_device,
        float dpi,
        REFIID interface_id,
        void** resource) = 0;
};

MIDL_INTERFACE("19967CEE-EA52-45DD-9FDA-D9703A9FD150")
IProGpuD2DCompatFactoryNative : public IUnknown {
public:
    virtual HRESULT STDMETHODCALLTYPE CreateSolidColorBrush(
        const D2D1_COLOR_F* color,
        const D2D1_BRUSH_PROPERTIES* properties,
        ID2D1SolidColorBrush** brush) = 0;
};

static_assert(
    sizeof(progpu_native_direct2d_guid) == sizeof(GUID),
    "Direct2D portable GUID layout changed");
static_assert(
    sizeof(progpu_native_direct2d_color_f) == sizeof(D2D1_COLOR_F),
    "Direct2D portable color layout changed");
static_assert(
    sizeof(progpu_native_direct2d_brush_properties) ==
        sizeof(D2D1_BRUSH_PROPERTIES),
    "Direct2D portable brush-properties layout changed");
static_assert(
    sizeof(progpu_native_direct2d_gradient_stop) ==
        sizeof(D2D1_GRADIENT_STOP),
    "Direct2D portable gradient-stop layout changed");
static_assert(
    sizeof(progpu_native_direct2d_stroke_style_properties) ==
        sizeof(D2D1_STROKE_STYLE_PROPERTIES1),
    "Direct2D portable stroke-style layout changed");
static_assert(
    sizeof(progpu_native_direct2d_bitmap_brush_properties) ==
        sizeof(D2D1_BITMAP_BRUSH_PROPERTIES1),
    "Direct2D portable bitmap-brush layout changed");
static_assert(
    sizeof(progpu_native_direct2d_image_brush_properties) ==
        sizeof(D2D1_IMAGE_BRUSH_PROPERTIES),
    "Direct2D portable image-brush layout changed");
static_assert(
    sizeof(progpu_native_direct2d_size_f) == sizeof(D2D1_SIZE_F),
    "Direct2D portable size layout changed");
static_assert(
    sizeof(progpu_native_direct2d_triangle) == sizeof(D2D1_TRIANGLE),
    "Direct2D portable triangle layout changed");
static_assert(
    sizeof(progpu_native_direct2d_matrix_4x4_f) ==
        sizeof(D2D1_MATRIX_4X4_F),
    "Direct2D portable 4x4 matrix layout changed");
static_assert(
    sizeof(progpu_native_direct2d_command_stream_summary) == 64U,
    "Direct2D command-stream summary layout changed");
static_assert(
    sizeof(progpu_native_direct2d_scene_stream_result) == 80U,
    "Direct2D scene-stream result layout changed");
static_assert(
    sizeof(progpu_native_direct2d_glyph_offset) ==
        sizeof(DWRITE_GLYPH_OFFSET),
    "DirectWrite portable glyph-offset layout changed");
static_assert(
    sizeof(uint16_t) == sizeof(wchar_t),
    "The Windows DirectWrite ABI requires 16-bit wchar_t");

enum class progpu_direct2d_draw_scope_kind : uint8_t {
    layer,
    axis_aligned_clip
};

constexpr uint32_t progpu_direct2d_max_draw_scope_depth = 4096U;

struct progpu_native_direct2d_surface {
    ComPtr<ID3D11Device> d3d_device;
    ComPtr<ID3D11Device4> d3d_device4;
    ComPtr<ID3D11DeviceContext> d3d_context;
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIDevice> dxgi_device;
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<IDXGISurface> dxgi_surface;
    ComPtr<IDXGIKeyedMutex> keyed_mutex;
    ComPtr<IInspectable> winrt_d3d_device;
    ComPtr<IProGpuWin2DCanvasFactoryNative> win2d_factory;
    ComPtr<IInspectable> win2d_canvas_device;
    ComPtr<IInspectable> win2d_canvas_render_target;
    ComPtr<ID2D1Factory2> d2d_factory;
    ComPtr<ID2D1Device1> d2d_device;
    ComPtr<ID2D1DeviceContext1> d2d_context;
    ComPtr<ID2D1Bitmap1> d2d_bitmap;
    ComPtr<IDWriteFactory3> dwrite_factory;
    HANDLE shared_handle = nullptr;
    HANDLE device_removed_event = nullptr;
    DWORD device_removed_cookie = 0U;
    bool device_removed_registered = false;
    DXGI_ADAPTER_DESC1 adapter_descriptor{};
    uint32_t width = 0U;
    uint32_t height = 0U;
    float dpi_x = 96.0F;
    float dpi_y = 96.0F;
    bool software_adapter = false;
    bool access_acquired = false;
    bool draw_active = false;
    bool command_list_draw_active = false;
    std::array<
        progpu_direct2d_draw_scope_kind,
        progpu_direct2d_max_draw_scope_depth> draw_scopes{};
    uint32_t draw_scope_depth = 0U;
    ComPtr<ID2D1CommandList> active_command_list;
    std::mutex access_mutex;
    std::atomic<uint64_t> content_version{0U};
    uint64_t resource_generation = 0U;
    std::atomic<int32_t> last_hresult{S_OK};
    std::atomic<int32_t> device_loss_hresult{S_OK};

    ~progpu_native_direct2d_surface()
    {
        if (draw_active && d2d_context) {
            while (draw_scope_depth != 0U) {
                --draw_scope_depth;
                if (draw_scopes[draw_scope_depth] ==
                    progpu_direct2d_draw_scope_kind::layer) {
                    d2d_context->PopLayer();
                } else {
                    d2d_context->PopAxisAlignedClip();
                }
            }
            D2D1_TAG tag1 = 0U;
            D2D1_TAG tag2 = 0U;
            HRESULT draw_hr = d2d_context->EndDraw(&tag1, &tag2);
            if (command_list_draw_active) {
                d2d_context->SetTarget(d2d_bitmap.Get());
                if (SUCCEEDED(draw_hr) && active_command_list) {
                    static_cast<void>(active_command_list->Close());
                }
            }
        }
        if (access_acquired && keyed_mutex) {
            static_cast<void>(keyed_mutex->ReleaseSync(0U));
        }
        if (d2d_context) {
            d2d_context->SetTarget(nullptr);
        }
        if (shared_handle != nullptr) {
            static_cast<void>(CloseHandle(shared_handle));
        }
        if (device_removed_registered && d3d_device4) {
            d3d_device4->UnregisterDeviceRemoved(device_removed_cookie);
        }
        if (device_removed_event != nullptr) {
            static_cast<void>(CloseHandle(device_removed_event));
        }
    }
};

struct progpu_native_direct2d_scene_recorder {
    void* command_sink = nullptr;
    uint64_t scene_id = 0U;
    uint64_t generation = 0U;
    std::mutex access_mutex;
};

namespace {

namespace semantic_path_stroke =
    progpu::native::semantic_path_stroke;

constexpr uint32_t dxgi_format_b8g8r8a8_unorm = 87U;
constexpr uint32_t d2d_alpha_mode_premultiplied = 1U;
std::atomic<uint64_t> next_resource_generation{1U};

progpu_native_direct2d_status status_from_win2d_hresult(HRESULT hr);

bool is_device_loss_hresult(HRESULT hr)
{
    return hr == DXGI_ERROR_DEVICE_REMOVED ||
        hr == DXGI_ERROR_DEVICE_RESET ||
        hr == D2DERR_RECREATE_TARGET;
}

void retain_device_loss(
    progpu_native_direct2d_surface& surface,
    HRESULT hr)
{
    if (!is_device_loss_hresult(hr)) {
        return;
    }
    int32_t expected = S_OK;
    static_cast<void>(surface.device_loss_hresult.compare_exchange_strong(
        expected,
        hr,
        std::memory_order_acq_rel,
        std::memory_order_acquire));
}

uint64_t allocate_resource_generation()
{
    uint64_t generation = next_resource_generation.fetch_add(
        1U,
        std::memory_order_acq_rel);
    if (generation != 0U) {
        return generation;
    }
    return next_resource_generation.fetch_add(1U, std::memory_order_acq_rel);
}

class BorrowedMemoryStream final : public IStream {
public:
    BorrowedMemoryStream(const uint8_t* data, uint32_t size) noexcept
        : data_(data), size_(size)
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, IID_ISequentialStream) ||
            IsEqualIID(interface_id, IID_IStream)) {
            *value = static_cast<IStream*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE Read(
        void* value,
        ULONG byte_count,
        ULONG* bytes_read) override
    {
        if (bytes_read != nullptr) {
            *bytes_read = 0U;
        }
        if (value == nullptr && byte_count != 0U) {
            return STG_E_INVALIDPOINTER;
        }
        const uint64_t available = size_ - position_;
        const ULONG count = static_cast<ULONG>(
            available < byte_count ? available : byte_count);
        if (count != 0U) {
            std::memcpy(value, data_ + position_, count);
            position_ += count;
        }
        if (bytes_read != nullptr) {
            *bytes_read = count;
        }
        return count == byte_count ? S_OK : S_FALSE;
    }

    HRESULT STDMETHODCALLTYPE Write(
        const void*,
        ULONG,
        ULONG*) override
    {
        return STG_E_ACCESSDENIED;
    }

    HRESULT STDMETHODCALLTYPE Seek(
        LARGE_INTEGER move,
        DWORD origin,
        ULARGE_INTEGER* new_position) override
    {
        int64_t basis = 0;
        if (origin == STREAM_SEEK_CUR) {
            basis = static_cast<int64_t>(position_);
        } else if (origin == STREAM_SEEK_END) {
            basis = static_cast<int64_t>(size_);
        } else if (origin != STREAM_SEEK_SET) {
            return STG_E_INVALIDFUNCTION;
        }
        if ((move.QuadPart > 0 &&
             basis > INT64_MAX - move.QuadPart) ||
            (move.QuadPart < 0 &&
             basis < INT64_MIN - move.QuadPart)) {
            return STG_E_INVALIDFUNCTION;
        }
        const int64_t result = basis + move.QuadPart;
        if (result < 0 || static_cast<uint64_t>(result) > size_) {
            return STG_E_INVALIDFUNCTION;
        }
        position_ = static_cast<uint64_t>(result);
        if (new_position != nullptr) {
            new_position->QuadPart = position_;
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE SetSize(ULARGE_INTEGER) override
    {
        return STG_E_ACCESSDENIED;
    }

    HRESULT STDMETHODCALLTYPE CopyTo(
        IStream* destination,
        ULARGE_INTEGER byte_count,
        ULARGE_INTEGER* bytes_read,
        ULARGE_INTEGER* bytes_written) override
    {
        if (bytes_read != nullptr) {
            bytes_read->QuadPart = 0U;
        }
        if (bytes_written != nullptr) {
            bytes_written->QuadPart = 0U;
        }
        if (destination == nullptr) {
            return STG_E_INVALIDPOINTER;
        }

        const uint64_t available = size_ - position_;
        const uint64_t transfer_count = std::min(
            available,
            byte_count.QuadPart);
        if (transfer_count == 0U) {
            return byte_count.QuadPart == 0U ? S_OK : S_FALSE;
        }
        const ULONG transfer_count_32 = static_cast<ULONG>(transfer_count);
        ULONG written = 0U;
        position_ += transfer_count;
        if (bytes_read != nullptr) {
            bytes_read->QuadPart = transfer_count;
        }
        const HRESULT result = destination->Write(
            data_ + position_ - transfer_count,
            transfer_count_32,
            &written);
        if (bytes_written != nullptr) {
            bytes_written->QuadPart = written;
        }
        if (FAILED(result)) {
            return result;
        }
        if (written != transfer_count_32) {
            return STG_E_MEDIUMFULL;
        }
        return transfer_count == byte_count.QuadPart ? S_OK : S_FALSE;
    }

    HRESULT STDMETHODCALLTYPE Commit(DWORD) override
    {
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Revert() override
    {
        return STG_E_REVERTED;
    }

    HRESULT STDMETHODCALLTYPE LockRegion(
        ULARGE_INTEGER,
        ULARGE_INTEGER,
        DWORD) override
    {
        return STG_E_INVALIDFUNCTION;
    }

    HRESULT STDMETHODCALLTYPE UnlockRegion(
        ULARGE_INTEGER,
        ULARGE_INTEGER,
        DWORD) override
    {
        return STG_E_INVALIDFUNCTION;
    }

    HRESULT STDMETHODCALLTYPE Stat(
        STATSTG* status,
        DWORD flags) override
    {
        if (status == nullptr) {
            return STG_E_INVALIDPOINTER;
        }
        if ((flags & ~STATFLAG_NONAME) != 0U) {
            return STG_E_INVALIDFLAG;
        }
        *status = {};
        status->type = STGTY_STREAM;
        status->cbSize.QuadPart = size_;
        status->grfMode = STGM_READ;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Clone(IStream** value) override
    {
        if (value == nullptr) {
            return STG_E_INVALIDPOINTER;
        }
        *value = nullptr;
        auto* clone = new (std::nothrow) BorrowedMemoryStream(
            data_,
            static_cast<uint32_t>(size_));
        if (clone == nullptr) {
            return E_OUTOFMEMORY;
        }
        clone->position_ = position_;
        *value = clone;
        return S_OK;
    }

private:
    std::atomic<ULONG> reference_count_{1U};
    const uint8_t* data_ = nullptr;
    uint64_t size_ = 0U;
    uint64_t position_ = 0U;
};

bool compat_finite_transform(
    const D2D1_MATRIX_3X2_F* value) noexcept
{
    return value == nullptr ||
        (std::isfinite(value->_11) && std::isfinite(value->_12) &&
            std::isfinite(value->_21) && std::isfinite(value->_22) &&
            std::isfinite(value->_31) && std::isfinite(value->_32));
}

const progpu_native_direct2d_matrix_3x2_f* compat_core_transform(
    const D2D1_MATRIX_3X2_F* transform,
    progpu_native_direct2d_matrix_3x2_f& storage) noexcept;

bool compat_compose_transform(
    const D2D1_MATRIX_3X2_F& first,
    const D2D1_MATRIX_3X2_F* second,
    D2D1_MATRIX_3X2_F& result) noexcept
{
    const progpu_native_direct2d_matrix_3x2_f portable_first{
        first._11,
        first._12,
        first._21,
        first._22,
        first._31,
        first._32};
    progpu_native_direct2d_matrix_3x2_f portable_second{};
    const auto* portable_second_pointer = compat_core_transform(
        second, portable_second);
    progpu_native_direct2d_matrix_3x2_f portable_result{};
    if (FAILED(direct2d_core::compose_transform(
            portable_first,
            portable_second_pointer,
            &portable_result))) {
        return false;
    }
    result._11 = portable_result.m11;
    result._12 = portable_result.m12;
    result._21 = portable_result.m21;
    result._22 = portable_result.m22;
    result._31 = portable_result.m31;
    result._32 = portable_result.m32;
    return true;
}

bool compat_finite_rectangle(const D2D1_RECT_F* value) noexcept
{
    return value != nullptr && std::isfinite(value->left) &&
        std::isfinite(value->top) && std::isfinite(value->right) &&
        std::isfinite(value->bottom) && value->right >= value->left &&
        value->bottom >= value->top;
}

bool compat_finite_ellipse(const D2D1_ELLIPSE* value) noexcept
{
    return value != nullptr && direct2d_core::valid_ellipse({
        {value->point.x, value->point.y},
        value->radiusX,
        value->radiusY});
}

bool compat_finite_rounded_rectangle(
    const D2D1_ROUNDED_RECT* value) noexcept
{
    return value != nullptr && direct2d_core::valid_rounded_rectangle({
        {value->rect.left,
            value->rect.top,
            value->rect.right,
            value->rect.bottom},
        value->radiusX,
        value->radiusY});
}

direct2d_core::rectangle_edges_f compat_core_rectangle(
    const D2D1_RECT_F& rectangle) noexcept
{
    return {
        rectangle.left,
        rectangle.top,
        rectangle.right,
        rectangle.bottom};
}

direct2d_core::ellipse_f compat_core_ellipse(
    const D2D1_ELLIPSE& ellipse) noexcept
{
    return {
        {ellipse.point.x, ellipse.point.y},
        ellipse.radiusX,
        ellipse.radiusY};
}

direct2d_core::rounded_rectangle_f compat_core_rounded_rectangle(
    const D2D1_ROUNDED_RECT& rectangle) noexcept
{
    return {
        compat_core_rectangle(rectangle.rect),
        rectangle.radiusX,
        rectangle.radiusY};
}

const progpu_native_direct2d_matrix_3x2_f* compat_core_transform(
    const D2D1_MATRIX_3X2_F* transform,
    progpu_native_direct2d_matrix_3x2_f& storage) noexcept
{
    if (transform == nullptr) {
        return nullptr;
    }
    storage = {
        transform->_11,
        transform->_12,
        transform->_21,
        transform->_22,
        transform->_31,
        transform->_32};
    return &storage;
}

class ProGpuD2DRectangleGeometry final : public ID2D1RectangleGeometry {
public:
    ProGpuD2DRectangleGeometry(
        ID2D1Factory1* factory,
        const D2D1_RECT_F& rectangle) noexcept
        : factory_(factory), rectangle_(rectangle)
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Resource)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Geometry)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1RectangleGeometry))) {
            *value = static_cast<ID2D1RectangleGeometry*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE GetFactory(
        ID2D1Factory** factory) const noexcept override
    {
        if (factory == nullptr) {
            return;
        }
        *factory = factory_.Get();
        if (*factory != nullptr) {
            (*factory)->AddRef();
        }
    }

    HRESULT STDMETHODCALLTYPE GetBounds(
        const D2D1_MATRIX_3X2_F* world_transform,
        D2D1_RECT_F* bounds) const noexcept override
    {
        if (bounds == nullptr) {
            return E_POINTER;
        }
        *bounds = {};
        progpu_native_direct2d_matrix_3x2_f transform{};
        direct2d_core::rectangle_edges_f result{};
        const direct2d_core::rectangle_geometry geometry(
            compat_core_rectangle(rectangle_));
        const HRESULT status = geometry.bounds(
            compat_core_transform(world_transform, transform), &result);
        if (FAILED(status)) {
            return status;
        }
        *bounds = {
            result.left, result.top, result.right, result.bottom};
        return status;
    }

    HRESULT STDMETHODCALLTYPE GetWidenedBounds(
        FLOAT stroke_width,
        ID2D1StrokeStyle* style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_RECT_F* bounds) const noexcept override
    {
        if (bounds == nullptr) {
            return E_POINTER;
        }
        *bounds = {};
        progpu_native_direct2d_matrix_3x2_f transform{};
        direct2d_compat::rectangle_f result{};
        const HRESULT status =
            direct2d_compat::detail::get_rectangle_widened_bounds(
                reinterpret_cast<direct2d_compat::factory*>(factory_.Get()),
                compat_core_rectangle(rectangle_),
                stroke_width,
                reinterpret_cast<direct2d_compat::stroke_style*>(style),
                compat_core_transform(world_transform, transform),
                flattening_tolerance,
                &result);
        if (SUCCEEDED(status)) {
            *bounds = {result.left, result.top, result.right, result.bottom};
        }
        return status;
    }

    HRESULT STDMETHODCALLTYPE StrokeContainsPoint(
        D2D1_POINT_2F point,
        FLOAT stroke_width,
        ID2D1StrokeStyle* style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        if (contains == nullptr) {
            return E_POINTER;
        }
        *contains = FALSE;
        progpu_native_direct2d_matrix_3x2_f transform{};
        std::int32_t result = 0;
        const HRESULT status =
            direct2d_compat::detail::rectangle_stroke_contains_point(
                compat_core_rectangle(rectangle_),
                {point.x, point.y},
                stroke_width,
                reinterpret_cast<direct2d_compat::stroke_style*>(style),
                compat_core_transform(world_transform, transform),
                flattening_tolerance,
                &result);
        *contains = result == 0 ? FALSE : TRUE;
        return status;
    }

    HRESULT STDMETHODCALLTYPE FillContainsPoint(
        D2D1_POINT_2F point,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        if (contains == nullptr) {
            return E_POINTER;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        std::uint32_t result = 0U;
        const direct2d_core::rectangle_geometry geometry(
            compat_core_rectangle(rectangle_));
        const HRESULT status = geometry.fill_contains_point(
            {point.x, point.y},
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            &result);
        *contains = result == 0U ? FALSE : TRUE;
        return status;
    }

    HRESULT STDMETHODCALLTYPE CompareWithGeometry(
        ID2D1Geometry* input_geometry,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        D2D1_GEOMETRY_RELATION* relation) const noexcept override
    {
        if (relation == nullptr) {
            return E_POINTER;
        }
        *relation = D2D1_GEOMETRY_RELATION_UNKNOWN;
        progpu_native_direct2d_matrix_3x2_f transform{};
        direct2d_compat::geometry_relation result =
            direct2d_compat::geometry_relation::unknown;
        const HRESULT status = direct2d_compat::detail::compare_rectangle(
            reinterpret_cast<direct2d_compat::factory*>(factory_.Get()),
            compat_core_rectangle(rectangle_),
            reinterpret_cast<direct2d_compat::geometry*>(input_geometry),
            compat_core_transform(input_geometry_transform, transform),
            flattening_tolerance,
            &result);
        *relation = static_cast<D2D1_GEOMETRY_RELATION>(result);
        return status;
    }

    HRESULT STDMETHODCALLTYPE Simplify(
        D2D1_GEOMETRY_SIMPLIFICATION_OPTION simplification_option,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        if (geometry_sink == nullptr) {
            return E_POINTER;
        }
        if ((simplification_option !=
                D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES &&
             simplification_option !=
                D2D1_GEOMETRY_SIMPLIFICATION_OPTION_LINES) ||
            !std::isfinite(flattening_tolerance) ||
            flattening_tolerance <= 0.0F) {
            return E_INVALIDARG;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        std::array<progpu_native_direct2d_point_2f, 4U> points{};
        const direct2d_core::rectangle_geometry geometry(
            compat_core_rectangle(rectangle_));
        const HRESULT status = geometry.vertices(
            compat_core_transform(world_transform, transform), points);
        if (FAILED(status)) {
            return status;
        }
        const std::array<D2D1_POINT_2F, 4U> native_points{{
            {points[0U].x, points[0U].y},
            {points[1U].x, points[1U].y},
            {points[2U].x, points[2U].y},
            {points[3U].x, points[3U].y}}};
        geometry_sink->SetFillMode(D2D1_FILL_MODE_WINDING);
        geometry_sink->SetSegmentFlags(D2D1_PATH_SEGMENT_NONE);
        geometry_sink->BeginFigure(
            native_points[0U], D2D1_FIGURE_BEGIN_FILLED);
        geometry_sink->AddLines(native_points.data() + 1U, 3U);
        geometry_sink->EndFigure(D2D1_FIGURE_END_CLOSED);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Tessellate(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1TessellationSink* tessellation_sink) const noexcept override
    {
        if (tessellation_sink == nullptr) {
            return E_POINTER;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        std::array<progpu_native_direct2d_triangle, 2U> core_triangles{};
        const direct2d_core::rectangle_geometry geometry(
            compat_core_rectangle(rectangle_));
        const HRESULT status = geometry.tessellate(
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            &core_triangles);
        if (FAILED(status)) {
            return status;
        }
        const std::array<D2D1_TRIANGLE, 2U> triangles{{
            {{core_triangles[0U].point1.x, core_triangles[0U].point1.y},
                {core_triangles[0U].point2.x, core_triangles[0U].point2.y},
                {core_triangles[0U].point3.x, core_triangles[0U].point3.y}},
            {{core_triangles[1U].point1.x, core_triangles[1U].point1.y},
                {core_triangles[1U].point2.x, core_triangles[1U].point2.y},
                {core_triangles[1U].point3.x, core_triangles[1U].point3.y}}}};
        tessellation_sink->AddTriangles(
            triangles.data(), static_cast<UINT32>(triangles.size()));
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE CombineWithGeometry(
        ID2D1Geometry* input_geometry,
        D2D1_COMBINE_MODE combine_mode,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        progpu_native_direct2d_matrix_3x2_f transform{};
        return direct2d_compat::detail::combine_rectangle(
            reinterpret_cast<direct2d_compat::factory*>(factory_.Get()),
            compat_core_rectangle(rectangle_),
            reinterpret_cast<direct2d_compat::geometry*>(input_geometry),
            static_cast<direct2d_compat::combine_mode>(combine_mode),
            compat_core_transform(input_geometry_transform, transform),
            flattening_tolerance,
            reinterpret_cast<direct2d_compat::simplified_geometry_sink*>(
                geometry_sink));
    }

    HRESULT STDMETHODCALLTYPE Outline(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        progpu_native_direct2d_matrix_3x2_f transform{};
        return direct2d_compat::detail::outline_rectangle(
            compat_core_rectangle(rectangle_),
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            reinterpret_cast<direct2d_compat::simplified_geometry_sink*>(
                geometry_sink));
    }

    HRESULT STDMETHODCALLTYPE ComputeArea(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* area) const noexcept override
    {
        if (area == nullptr) {
            return E_POINTER;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        const direct2d_core::rectangle_geometry geometry(
            compat_core_rectangle(rectangle_));
        return geometry.area(
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            area);
    }

    HRESULT STDMETHODCALLTYPE ComputeLength(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* length) const noexcept override
    {
        if (length == nullptr) {
            return E_POINTER;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        const direct2d_core::rectangle_geometry geometry(
            compat_core_rectangle(rectangle_));
        return geometry.length(
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            length);
    }

    HRESULT STDMETHODCALLTYPE ComputePointAtLength(
        FLOAT length,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_POINT_2F* point,
        D2D1_POINT_2F* unit_tangent_vector) const noexcept override
    {
        if (point == nullptr && unit_tangent_vector == nullptr) {
            return E_POINTER;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        progpu_native_direct2d_point_2f core_point{};
        progpu_native_direct2d_point_2f core_tangent{};
        const direct2d_core::rectangle_geometry geometry(
            compat_core_rectangle(rectangle_));
        const HRESULT status = geometry.point_at_length(
            length,
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            point == nullptr ? nullptr : &core_point,
            unit_tangent_vector == nullptr ? nullptr : &core_tangent);
        if (point != nullptr) {
            *point = {core_point.x, core_point.y};
        }
        if (unit_tangent_vector != nullptr) {
            *unit_tangent_vector = {core_tangent.x, core_tangent.y};
        }
        return status;
    }

    HRESULT STDMETHODCALLTYPE Widen(
        FLOAT stroke_width,
        ID2D1StrokeStyle* style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        progpu_native_direct2d_matrix_3x2_f transform{};
        return direct2d_compat::detail::widen_rectangle(
            compat_core_rectangle(rectangle_),
            stroke_width,
            reinterpret_cast<direct2d_compat::stroke_style*>(style),
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            reinterpret_cast<direct2d_compat::simplified_geometry_sink*>(
                geometry_sink));
    }

    void STDMETHODCALLTYPE GetRect(
        D2D1_RECT_F* rectangle) const noexcept override
    {
        if (rectangle != nullptr) {
            *rectangle = rectangle_;
        }
    }

private:
    std::atomic<ULONG> reference_count_{1U};
    ComPtr<ID2D1Factory1> factory_;
    D2D1_RECT_F rectangle_{};
};

class ProGpuD2DSolidColorBrush final : public ID2D1SolidColorBrush {
public:
    ProGpuD2DSolidColorBrush(
        ID2D1Factory1* factory,
        const D2D1_COLOR_F& color,
        const D2D1_BRUSH_PROPERTIES& properties) noexcept
        : factory_(factory),
          color_(color),
          opacity_(properties.opacity),
          transform_(properties.transform)
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Resource)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Brush)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1SolidColorBrush))) {
            *value = static_cast<ID2D1SolidColorBrush*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE GetFactory(
        ID2D1Factory** factory) const noexcept override
    {
        if (factory == nullptr) {
            return;
        }
        *factory = factory_.Get();
        if (*factory != nullptr) {
            (*factory)->AddRef();
        }
    }

    void STDMETHODCALLTYPE SetOpacity(FLOAT opacity) noexcept override
    {
        if (!std::isfinite(opacity) || opacity < 0.0F || opacity > 1.0F) {
            return;
        }
        const std::lock_guard lock(mutex_);
        opacity_ = opacity;
    }

    void STDMETHODCALLTYPE SetTransform(
        const D2D1_MATRIX_3X2_F* transform) noexcept override
    {
        if (!compat_finite_transform(transform) || transform == nullptr) {
            return;
        }
        const std::lock_guard lock(mutex_);
        transform_ = *transform;
    }

    FLOAT STDMETHODCALLTYPE GetOpacity() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return opacity_;
    }

    void STDMETHODCALLTYPE GetTransform(
        D2D1_MATRIX_3X2_F* transform) const noexcept override
    {
        if (transform == nullptr) {
            return;
        }
        const std::lock_guard lock(mutex_);
        *transform = transform_;
    }

    void STDMETHODCALLTYPE SetColor(
        const D2D1_COLOR_F* color) noexcept override
    {
        if (!finite_color_value(color)) {
            return;
        }
        const std::lock_guard lock(mutex_);
        color_ = *color;
    }

    D2D1_COLOR_F STDMETHODCALLTYPE GetColor() const noexcept override
    {
        const std::lock_guard lock(mutex_);
        return color_;
    }

private:
    static bool finite_color_value(
        const D2D1_COLOR_F* color) noexcept
    {
        return color != nullptr && std::isfinite(color->r) &&
            std::isfinite(color->g) && std::isfinite(color->b) &&
            std::isfinite(color->a);
    }

    std::atomic<ULONG> reference_count_{1U};
    ComPtr<ID2D1Factory1> factory_;
    mutable std::mutex mutex_;
    D2D1_COLOR_F color_{};
    FLOAT opacity_ = 1.0F;
    D2D1_MATRIX_3X2_F transform_ = D2D1::Matrix3x2F::Identity();
};

class ProGpuD2DStrokeStyle final : public ID2D1StrokeStyle1 {
public:
    ProGpuD2DStrokeStyle(
        ID2D1Factory1* factory,
        const D2D1_STROKE_STYLE_PROPERTIES1& properties,
        const FLOAT* dashes,
        UINT32 dash_count)
        : factory_(factory), properties_(properties)
    {
        if (dash_count != 0U) {
            dashes_.assign(dashes, dashes + dash_count);
        }
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Resource)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1StrokeStyle)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1StrokeStyle1))) {
            *value = static_cast<ID2D1StrokeStyle1*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE GetFactory(
        ID2D1Factory** factory) const noexcept override
    {
        if (factory == nullptr) {
            return;
        }
        *factory = factory_.Get();
        if (*factory != nullptr) {
            (*factory)->AddRef();
        }
    }

    D2D1_CAP_STYLE STDMETHODCALLTYPE GetStartCap() const noexcept override
    {
        return properties_.startCap;
    }

    D2D1_CAP_STYLE STDMETHODCALLTYPE GetEndCap() const noexcept override
    {
        return properties_.endCap;
    }

    D2D1_CAP_STYLE STDMETHODCALLTYPE GetDashCap() const noexcept override
    {
        return properties_.dashCap;
    }

    FLOAT STDMETHODCALLTYPE GetMiterLimit() const noexcept override
    {
        return properties_.miterLimit;
    }

    D2D1_LINE_JOIN STDMETHODCALLTYPE GetLineJoin() const noexcept override
    {
        return properties_.lineJoin;
    }

    FLOAT STDMETHODCALLTYPE GetDashOffset() const noexcept override
    {
        return properties_.dashOffset;
    }

    D2D1_DASH_STYLE STDMETHODCALLTYPE GetDashStyle() const noexcept override
    {
        return properties_.dashStyle;
    }

    UINT32 STDMETHODCALLTYPE GetDashesCount() const noexcept override
    {
        return static_cast<UINT32>(dashes_.size());
    }

    void STDMETHODCALLTYPE GetDashes(
        FLOAT* dashes,
        UINT32 dash_count) const noexcept override
    {
        if (dashes == nullptr || dash_count == 0U) {
            return;
        }
        std::copy_n(
            dashes_.data(),
            std::min<size_t>(dashes_.size(), dash_count),
            dashes);
    }

    D2D1_STROKE_TRANSFORM_TYPE STDMETHODCALLTYPE
    GetStrokeTransformType() const noexcept override
    {
        return properties_.transformType;
    }

private:
    std::atomic<ULONG> reference_count_{1U};
    ComPtr<ID2D1Factory1> factory_;
    D2D1_STROKE_STYLE_PROPERTIES1 properties_{};
    std::vector<FLOAT> dashes_;
};

bool compat_valid_stroke_style(
    const D2D1_STROKE_STYLE_PROPERTIES1* properties,
    const FLOAT* dashes,
    UINT32 dash_count) noexcept
{
    if (properties == nullptr ||
        static_cast<uint32_t>(properties->transformType) >
            static_cast<uint32_t>(D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE)) {
        return false;
    }
    const direct2d_core::stroke_style_properties_f core_properties{
        static_cast<direct2d_core::cap_style>(properties->startCap),
        static_cast<direct2d_core::cap_style>(properties->endCap),
        static_cast<direct2d_core::cap_style>(properties->dashCap),
        static_cast<direct2d_core::line_join>(properties->lineJoin),
        properties->miterLimit,
        static_cast<direct2d_core::dash_style>(properties->dashStyle),
        properties->dashOffset};
    return direct2d_core::valid_stroke_style(
        core_properties, dashes, dash_count);
}

enum class compat_path_state : uint32_t {
    fresh = 0U,
    open = 1U,
    closed = 2U,
    failed = 3U
};

enum class compat_path_segment_kind : uint8_t {
    line,
    cubic,
    quadratic,
    arc
};

struct compat_path_segment {
    compat_path_segment_kind kind = compat_path_segment_kind::line;
    D2D1_PATH_SEGMENT flags = D2D1_PATH_SEGMENT_NONE;
    D2D1_POINT_2F start{};
    D2D1_POINT_2F end{};
    D2D1_POINT_2F control1{};
    D2D1_POINT_2F control2{};
    D2D1_ARC_SEGMENT arc{};
};

struct compat_path_figure {
    D2D1_POINT_2F start{};
    D2D1_FIGURE_BEGIN begin = D2D1_FIGURE_BEGIN_FILLED;
    D2D1_FIGURE_END end = D2D1_FIGURE_END_OPEN;
    uint32_t first_segment = 0U;
    uint32_t first_public_segment = 0U;
    uint32_t segment_count = 0U;
};

struct compat_path_data {
    mutable std::mutex mutex;
    std::vector<compat_path_figure> figures;
    std::vector<compat_path_segment> segments;
    std::atomic<compat_path_state> published_state{compat_path_state::fresh};
    D2D1_FILL_MODE fill_mode = D2D1_FILL_MODE_ALTERNATE;
    D2D1_PATH_SEGMENT current_flags = D2D1_PATH_SEGMENT_NONE;
    D2D1_POINT_2F current_point{};
    uint32_t public_segment_count = 0U;
    HRESULT failure = S_OK;
    bool figure_open = false;
};

bool compat_finite_point(D2D1_POINT_2F point) noexcept
{
    return std::isfinite(point.x) && std::isfinite(point.y);
}

bool compat_same_point(
    D2D1_POINT_2F left,
    D2D1_POINT_2F right) noexcept
{
    return left.x == right.x && left.y == right.y;
}

bool compat_transform_point(
    D2D1_POINT_2F point,
    const D2D1_MATRIX_3X2_F* transform,
    D2D1_POINT_2F& result) noexcept
{
    if (!compat_finite_point(point) || !compat_finite_transform(transform)) {
        return false;
    }
    const D2D1_MATRIX_3X2_F matrix = transform == nullptr
        ? D2D1::Matrix3x2F::Identity()
        : *transform;
    const double x = static_cast<double>(point.x) * matrix._11 +
        static_cast<double>(point.y) * matrix._21 + matrix._31;
    const double y = static_cast<double>(point.x) * matrix._12 +
        static_cast<double>(point.y) * matrix._22 + matrix._32;
    constexpr double maximum =
        static_cast<double>(std::numeric_limits<float>::max());
    if (!std::isfinite(x) || !std::isfinite(y) ||
        std::abs(x) > maximum || std::abs(y) > maximum) {
        return false;
    }
    result = {static_cast<float>(x), static_cast<float>(y)};
    return true;
}

bool compat_valid_arc(const D2D1_ARC_SEGMENT& arc) noexcept
{
    const direct2d_core::arc_segment_f core_arc{
        {arc.point.x, arc.point.y},
        {arc.size.width, arc.size.height},
        arc.rotationAngle,
        static_cast<direct2d_core::arc_sweep_direction>(
            arc.sweepDirection),
        static_cast<direct2d_core::arc_size_kind>(arc.arcSize)};
    return direct2d_core::valid_arc_segment(core_arc);
}

bool compat_arc_to_cubics(
    D2D1_POINT_2F start,
    const D2D1_ARC_SEGMENT& arc,
    std::array<D2D1_BEZIER_SEGMENT, 4U>& cubics,
    uint32_t& cubic_count) noexcept
{
    const direct2d_core::arc_segment_f core_arc{
        {arc.point.x, arc.point.y},
        {arc.size.width, arc.size.height},
        arc.rotationAngle,
        static_cast<direct2d_core::arc_sweep_direction>(
            arc.sweepDirection),
        static_cast<direct2d_core::arc_size_kind>(arc.arcSize)};
    std::array<direct2d_core::cubic_bezier_segment_f, 4U> core_cubics{};
    if (FAILED(direct2d_core::arc_to_cubics(
            {start.x, start.y},
            core_arc,
            &core_cubics,
            &cubic_count))) {
        return false;
    }
    for (uint32_t index = 0U; index < cubic_count; ++index) {
        cubics[index] = {
            {core_cubics[index].point1.x, core_cubics[index].point1.y},
            {core_cubics[index].point2.x, core_cubics[index].point2.y},
            {core_cubics[index].point3.x, core_cubics[index].point3.y}};
    }
    return true;
}

double compat_point_line_distance_squared(
    D2D1_POINT_2F point,
    D2D1_POINT_2F start,
    D2D1_POINT_2F end) noexcept
{
    const double dx = static_cast<double>(end.x) - start.x;
    const double dy = static_cast<double>(end.y) - start.y;
    const double length_squared = dx * dx + dy * dy;
    if (length_squared == 0.0) {
        const double px = static_cast<double>(point.x) - start.x;
        const double py = static_cast<double>(point.y) - start.y;
        return px * px + py * py;
    }
    const double cross =
        (static_cast<double>(point.x) - start.x) * dy -
        (static_cast<double>(point.y) - start.y) * dx;
    return cross * cross / length_squared;
}

template<typename Callback>
bool compat_flatten_cubic(
    D2D1_POINT_2F start,
    D2D1_POINT_2F control1,
    D2D1_POINT_2F control2,
    D2D1_POINT_2F end,
    double tolerance_squared,
    uint32_t depth,
    Callback& callback)
{
    if (depth == 20U ||
        (compat_point_line_distance_squared(control1, start, end) <=
                tolerance_squared &&
            compat_point_line_distance_squared(control2, start, end) <=
                tolerance_squared)) {
        return callback(start, end);
    }
    const D2D1_POINT_2F p01 = {
        (start.x + control1.x) * 0.5F,
        (start.y + control1.y) * 0.5F};
    const D2D1_POINT_2F p12 = {
        (control1.x + control2.x) * 0.5F,
        (control1.y + control2.y) * 0.5F};
    const D2D1_POINT_2F p23 = {
        (control2.x + end.x) * 0.5F,
        (control2.y + end.y) * 0.5F};
    const D2D1_POINT_2F p012 = {
        (p01.x + p12.x) * 0.5F,
        (p01.y + p12.y) * 0.5F};
    const D2D1_POINT_2F p123 = {
        (p12.x + p23.x) * 0.5F,
        (p12.y + p23.y) * 0.5F};
    const D2D1_POINT_2F midpoint = {
        (p012.x + p123.x) * 0.5F,
        (p012.y + p123.y) * 0.5F};
    return compat_flatten_cubic(
               start,
               p01,
               p012,
               midpoint,
               tolerance_squared,
               depth + 1U,
               callback) &&
        compat_flatten_cubic(
            midpoint,
            p123,
            p23,
            end,
            tolerance_squared,
            depth + 1U,
            callback);
}

template<typename BeginCallback, typename LineCallback,
    typename CubicCallback, typename EndCallback>
bool compat_visit_path(
    const compat_path_data& data,
    const D2D1_MATRIX_3X2_F* transform,
    bool flatten,
    float tolerance,
    BeginCallback&& begin_callback,
    LineCallback&& line_callback,
    CubicCallback&& cubic_callback,
    EndCallback&& end_callback)
{
    const double tolerance_squared =
        static_cast<double>(tolerance) * tolerance;
    for (size_t figure_offset = 0U;
         figure_offset < data.figures.size();
         ++figure_offset) {
        const uint32_t figure_index =
            static_cast<uint32_t>(figure_offset);
        const auto& figure = data.figures[figure_offset];
        D2D1_POINT_2F transformed_start{};
        if (!compat_transform_point(
                figure.start, transform, transformed_start) ||
            !begin_callback(
                transformed_start, figure_index, figure)) {
            return false;
        }
        D2D1_POINT_2F current_source = figure.start;
        D2D1_POINT_2F current_target = transformed_start;
        for (uint32_t local_index = 0U;
             local_index < figure.segment_count;
             ++local_index) {
            const uint32_t storage_segment_index =
                figure.first_segment + local_index;
            const uint32_t segment_index =
                figure.first_public_segment + local_index;
            const auto& segment = data.segments[storage_segment_index];
            D2D1_POINT_2F end_target{};
            if (!compat_transform_point(
                    segment.end, transform, end_target)) {
                return false;
            }
            if (segment.kind == compat_path_segment_kind::line ||
                (segment.kind == compat_path_segment_kind::arc &&
                    (segment.arc.size.width == 0.0F ||
                        segment.arc.size.height == 0.0F))) {
                if (!line_callback(
                        current_target,
                        end_target,
                        segment_index,
                        figure_index,
                        segment.flags)) {
                    return false;
                }
            } else if (segment.kind == compat_path_segment_kind::cubic ||
                       segment.kind ==
                           compat_path_segment_kind::quadratic) {
                D2D1_POINT_2F control1_source = segment.control1;
                D2D1_POINT_2F control2_source = segment.control2;
                if (segment.kind == compat_path_segment_kind::quadratic) {
                    control1_source = {
                        current_source.x +
                            (segment.control1.x - current_source.x) *
                                (2.0F / 3.0F),
                        current_source.y +
                            (segment.control1.y - current_source.y) *
                                (2.0F / 3.0F)};
                    control2_source = {
                        segment.end.x +
                            (segment.control1.x - segment.end.x) *
                                (2.0F / 3.0F),
                        segment.end.y +
                            (segment.control1.y - segment.end.y) *
                                (2.0F / 3.0F)};
                }
                D2D1_POINT_2F control1_target{};
                D2D1_POINT_2F control2_target{};
                if (!compat_transform_point(
                        control1_source, transform, control1_target) ||
                    !compat_transform_point(
                        control2_source, transform, control2_target)) {
                    return false;
                }
                if (flatten) {
                    auto callback = [&](D2D1_POINT_2F line_start,
                                        D2D1_POINT_2F line_end) {
                        return line_callback(
                            line_start,
                            line_end,
                            segment_index,
                            figure_index,
                            segment.flags);
                    };
                    if (!compat_flatten_cubic(
                            current_target,
                            control1_target,
                            control2_target,
                            end_target,
                            tolerance_squared,
                            0U,
                            callback)) {
                        return false;
                    }
                } else if (!cubic_callback(
                               current_target,
                               control1_target,
                               control2_target,
                               end_target,
                               segment_index,
                               figure_index,
                               segment.flags)) {
                    return false;
                }
            } else {
                std::array<D2D1_BEZIER_SEGMENT, 4U> cubics{};
                uint32_t cubic_count = 0U;
                if (!compat_arc_to_cubics(
                        current_source,
                        segment.arc,
                        cubics,
                        cubic_count)) {
                    return false;
                }
                D2D1_POINT_2F cubic_start = current_target;
                for (uint32_t cubic_index = 0U;
                     cubic_index < cubic_count;
                     ++cubic_index) {
                    D2D1_POINT_2F control1_target{};
                    D2D1_POINT_2F control2_target{};
                    D2D1_POINT_2F cubic_end_target{};
                    if (!compat_transform_point(
                            cubics[cubic_index].point1,
                            transform,
                            control1_target) ||
                        !compat_transform_point(
                            cubics[cubic_index].point2,
                            transform,
                            control2_target) ||
                        !compat_transform_point(
                            cubics[cubic_index].point3,
                            transform,
                            cubic_end_target)) {
                        return false;
                    }
                    if (flatten) {
                        auto callback = [&](D2D1_POINT_2F line_start,
                                            D2D1_POINT_2F line_end) {
                            return line_callback(
                                line_start,
                                line_end,
                                segment_index,
                                figure_index,
                                segment.flags);
                        };
                        if (!compat_flatten_cubic(
                                cubic_start,
                                control1_target,
                                control2_target,
                                cubic_end_target,
                                tolerance_squared,
                                0U,
                                callback)) {
                            return false;
                        }
                    } else if (!cubic_callback(
                                   cubic_start,
                                   control1_target,
                                   control2_target,
                                   cubic_end_target,
                                   segment_index,
                                   figure_index,
                                   segment.flags)) {
                        return false;
                    }
                    cubic_start = cubic_end_target;
                }
            }
            current_source = segment.end;
            current_target = end_target;
        }
        if (!end_callback(
                current_target,
                transformed_start,
                figure_index,
                figure)) {
            return false;
        }
    }
    return true;
}

class ProGpuD2DGeometrySink final : public ID2D1GeometrySink {
public:
    explicit ProGpuD2DGeometrySink(
        std::shared_ptr<compat_path_data> data) noexcept
        : data_(std::move(data))
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(
                interface_id,
                __uuidof(ID2D1SimplifiedGeometrySink)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1GeometrySink))) {
            *value = static_cast<ID2D1GeometrySink*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE SetFillMode(
        D2D1_FILL_MODE fill_mode) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record_locked() || data_->figure_open ||
            !data_->figures.empty() ||
            (fill_mode != D2D1_FILL_MODE_ALTERNATE &&
                fill_mode != D2D1_FILL_MODE_WINDING)) {
            set_failure_locked(E_INVALIDARG);
            return;
        }
        data_->fill_mode = fill_mode;
    }

    void STDMETHODCALLTYPE SetSegmentFlags(
        D2D1_PATH_SEGMENT flags) noexcept override
    {
        constexpr uint32_t supported_flags =
            D2D1_PATH_SEGMENT_FORCE_UNSTROKED |
            D2D1_PATH_SEGMENT_FORCE_ROUND_LINE_JOIN;
        const std::lock_guard lock(data_->mutex);
        if (!can_record_locked() ||
            (static_cast<uint32_t>(flags) & ~supported_flags) != 0U) {
            set_failure_locked(E_INVALIDARG);
            return;
        }
        data_->current_flags = flags;
    }

    void STDMETHODCALLTYPE BeginFigure(
        D2D1_POINT_2F start,
        D2D1_FIGURE_BEGIN begin) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record_locked() || data_->figure_open ||
            !compat_finite_point(start) ||
            (begin != D2D1_FIGURE_BEGIN_FILLED &&
                begin != D2D1_FIGURE_BEGIN_HOLLOW) ||
            data_->figures.size() ==
                std::numeric_limits<uint32_t>::max()) {
            set_failure_locked(E_INVALIDARG);
            return;
        }
        try {
            data_->figures.push_back(
                {start,
                    begin,
                    D2D1_FIGURE_END_OPEN,
                    static_cast<uint32_t>(data_->segments.size()),
                    data_->public_segment_count,
                    0U});
        } catch (const std::bad_alloc&) {
            set_failure_locked(E_OUTOFMEMORY);
            return;
        } catch (...) {
            set_failure_locked(E_FAIL);
            return;
        }
        data_->current_point = start;
        data_->figure_open = true;
    }

    void STDMETHODCALLTYPE AddLines(
        const D2D1_POINT_2F* points,
        UINT32 point_count) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record_locked() || !data_->figure_open ||
            (point_count != 0U && points == nullptr)) {
            set_failure_locked(E_INVALIDARG);
            return;
        }
        for (uint32_t index = 0U; index < point_count; ++index) {
            if (!compat_finite_point(points[index])) {
                set_failure_locked(E_INVALIDARG);
                return;
            }
            compat_path_segment segment{};
            segment.kind = compat_path_segment_kind::line;
            segment.end = points[index];
            if (!append_segment_locked(segment)) {
                return;
            }
        }
    }

    void STDMETHODCALLTYPE AddBeziers(
        const D2D1_BEZIER_SEGMENT* beziers,
        UINT32 bezier_count) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record_locked() || !data_->figure_open ||
            (bezier_count != 0U && beziers == nullptr)) {
            set_failure_locked(E_INVALIDARG);
            return;
        }
        for (uint32_t index = 0U; index < bezier_count; ++index) {
            const auto& bezier = beziers[index];
            if (!compat_finite_point(bezier.point1) ||
                !compat_finite_point(bezier.point2) ||
                !compat_finite_point(bezier.point3)) {
                set_failure_locked(E_INVALIDARG);
                return;
            }
            compat_path_segment segment{};
            segment.kind = compat_path_segment_kind::cubic;
            segment.control1 = bezier.point1;
            segment.control2 = bezier.point2;
            segment.end = bezier.point3;
            if (!append_segment_locked(segment)) {
                return;
            }
        }
    }

    void STDMETHODCALLTYPE EndFigure(
        D2D1_FIGURE_END end) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record_locked() || !data_->figure_open ||
            (end != D2D1_FIGURE_END_OPEN &&
                end != D2D1_FIGURE_END_CLOSED)) {
            set_failure_locked(E_INVALIDARG);
            return;
        }
        if (end == D2D1_FIGURE_END_CLOSED &&
            data_->public_segment_count ==
                std::numeric_limits<uint32_t>::max()) {
            set_failure_locked(E_OUTOFMEMORY);
            return;
        }
        data_->figures.back().end = end;
        if (end == D2D1_FIGURE_END_CLOSED) {
            ++data_->public_segment_count;
        }
        data_->figure_open = false;
    }

    HRESULT STDMETHODCALLTYPE Close() noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (data_->published_state.load(std::memory_order_relaxed) !=
            compat_path_state::open) {
            return D2DERR_WRONG_STATE;
        }
        if (data_->figure_open) {
            set_failure_locked(D2DERR_WRONG_STATE);
        }
        const HRESULT result = data_->failure;
        data_->published_state.store(
            SUCCEEDED(result)
                ? compat_path_state::closed
                : compat_path_state::failed,
            std::memory_order_release);
        return result;
    }

    void STDMETHODCALLTYPE AddLine(
        D2D1_POINT_2F point) noexcept override
    {
        AddLines(&point, 1U);
    }

    void STDMETHODCALLTYPE AddBezier(
        const D2D1_BEZIER_SEGMENT* bezier) noexcept override
    {
        AddBeziers(bezier, bezier == nullptr ? 0U : 1U);
        if (bezier == nullptr) {
            const std::lock_guard lock(data_->mutex);
            set_failure_locked(E_INVALIDARG);
        }
    }

    void STDMETHODCALLTYPE AddQuadraticBezier(
        const D2D1_QUADRATIC_BEZIER_SEGMENT* bezier) noexcept override
    {
        AddQuadraticBeziers(bezier, bezier == nullptr ? 0U : 1U);
        if (bezier == nullptr) {
            const std::lock_guard lock(data_->mutex);
            set_failure_locked(E_INVALIDARG);
        }
    }

    void STDMETHODCALLTYPE AddQuadraticBeziers(
        const D2D1_QUADRATIC_BEZIER_SEGMENT* beziers,
        UINT32 bezier_count) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record_locked() || !data_->figure_open ||
            (bezier_count != 0U && beziers == nullptr)) {
            set_failure_locked(E_INVALIDARG);
            return;
        }
        for (uint32_t index = 0U; index < bezier_count; ++index) {
            const auto& bezier = beziers[index];
            if (!compat_finite_point(bezier.point1) ||
                !compat_finite_point(bezier.point2)) {
                set_failure_locked(E_INVALIDARG);
                return;
            }
            compat_path_segment segment{};
            segment.kind = compat_path_segment_kind::quadratic;
            segment.control1 = bezier.point1;
            segment.end = bezier.point2;
            if (!append_segment_locked(segment)) {
                return;
            }
        }
    }

    void STDMETHODCALLTYPE AddArc(
        const D2D1_ARC_SEGMENT* arc) noexcept override
    {
        const std::lock_guard lock(data_->mutex);
        if (!can_record_locked() || !data_->figure_open || arc == nullptr ||
            !compat_valid_arc(*arc)) {
            set_failure_locked(E_INVALIDARG);
            return;
        }
        compat_path_segment segment{};
        segment.kind = compat_path_segment_kind::arc;
        segment.arc = *arc;
        segment.end = arc->point;
        static_cast<void>(append_segment_locked(segment));
    }

private:
    ~ProGpuD2DGeometrySink()
    {
        const std::lock_guard lock(data_->mutex);
        if (data_->published_state.load(std::memory_order_relaxed) ==
            compat_path_state::open) {
            set_failure_locked(D2DERR_WRONG_STATE);
            data_->published_state.store(
                compat_path_state::failed,
                std::memory_order_release);
        }
    }

    bool can_record_locked() const noexcept
    {
        return data_->published_state.load(std::memory_order_relaxed) ==
                compat_path_state::open &&
            SUCCEEDED(data_->failure);
    }

    void set_failure_locked(HRESULT failure) noexcept
    {
        if (SUCCEEDED(data_->failure)) {
            data_->failure = failure;
        }
    }

    bool append_segment_locked(compat_path_segment segment) noexcept
    {
        if (!can_record_locked() || !data_->figure_open ||
            data_->segments.size() ==
                std::numeric_limits<uint32_t>::max() ||
            data_->public_segment_count ==
                std::numeric_limits<uint32_t>::max()) {
            set_failure_locked(D2DERR_WRONG_STATE);
            return false;
        }
        segment.start = data_->current_point;
        segment.flags = data_->current_flags;
        try {
            data_->segments.push_back(segment);
        } catch (const std::bad_alloc&) {
            set_failure_locked(E_OUTOFMEMORY);
            return false;
        } catch (...) {
            set_failure_locked(E_FAIL);
            return false;
        }
        ++data_->figures.back().segment_count;
        ++data_->public_segment_count;
        data_->current_point = segment.end;
        return true;
    }

    std::atomic<ULONG> reference_count_{1U};
    std::shared_ptr<compat_path_data> data_;
};

double compat_cubic_coordinate(
    double p0,
    double p1,
    double p2,
    double p3,
    double t) noexcept
{
    const double one_minus_t = 1.0 - t;
    return one_minus_t * one_minus_t * one_minus_t * p0 +
        3.0 * one_minus_t * one_minus_t * t * p1 +
        3.0 * one_minus_t * t * t * p2 + t * t * t * p3;
}

void compat_include_cubic_bounds(
    D2D1_POINT_2F start,
    D2D1_POINT_2F control1,
    D2D1_POINT_2F control2,
    D2D1_POINT_2F end,
    D2D1_RECT_F& bounds) noexcept
{
    bounds.left = std::min(bounds.left, std::min(start.x, end.x));
    bounds.top = std::min(bounds.top, std::min(start.y, end.y));
    bounds.right = std::max(bounds.right, std::max(start.x, end.x));
    bounds.bottom = std::max(bounds.bottom, std::max(start.y, end.y));
    const auto include_axis = [&](double p0,
                                  double p1,
                                  double p2,
                                  double p3,
                                  bool x_axis) {
        const double a = -p0 + 3.0 * p1 - 3.0 * p2 + p3;
        const double b = 3.0 * p0 - 6.0 * p1 + 3.0 * p2;
        const double c = -3.0 * p0 + 3.0 * p1;
        std::array<double, 2U> roots{};
        uint32_t root_count = 0U;
        const double quadratic = 3.0 * a;
        const double linear = 2.0 * b;
        const double scale = std::max(
            {1.0, std::abs(quadratic), std::abs(linear), std::abs(c)});
        const double epsilon =
            std::numeric_limits<double>::epsilon() * scale * 16.0;
        if (std::abs(quadratic) <= epsilon) {
            if (std::abs(linear) > epsilon) {
                roots[root_count++] = -c / linear;
            }
        } else {
            const double discriminant = linear * linear -
                4.0 * quadratic * c;
            if (discriminant >= 0.0) {
                const double root = std::sqrt(discriminant);
                roots[root_count++] =
                    (-linear + root) / (2.0 * quadratic);
                roots[root_count++] =
                    (-linear - root) / (2.0 * quadratic);
            }
        }
        for (uint32_t index = 0U; index < root_count; ++index) {
            const double t = roots[index];
            if (!(t > 0.0 && t < 1.0)) {
                continue;
            }
            const float value = static_cast<float>(compat_cubic_coordinate(
                p0, p1, p2, p3, t));
            if (x_axis) {
                bounds.left = std::min(bounds.left, value);
                bounds.right = std::max(bounds.right, value);
            } else {
                bounds.top = std::min(bounds.top, value);
                bounds.bottom = std::max(bounds.bottom, value);
            }
        }
    };
    include_axis(start.x, control1.x, control2.x, end.x, true);
    include_axis(start.y, control1.y, control2.y, end.y, false);
}

struct compat_flat_edge {
    D2D1_POINT_2F start{};
    D2D1_POINT_2F end{};
    uint32_t segment_index = 0U;
    uint32_t figure_index = 0U;
};

class ProGpuD2DPathGeometry final : public ID2D1PathGeometry1 {
public:
    explicit ProGpuD2DPathGeometry(ID2D1Factory1* factory)
        : factory_(factory), data_(std::make_shared<compat_path_data>())
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Resource)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Geometry)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1PathGeometry)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1PathGeometry1))) {
            *value = static_cast<ID2D1PathGeometry1*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE GetFactory(
        ID2D1Factory** factory) const noexcept override
    {
        if (factory == nullptr) {
            return;
        }
        *factory = factory_.Get();
        if (*factory != nullptr) {
            (*factory)->AddRef();
        }
    }

    HRESULT STDMETHODCALLTYPE GetBounds(
        const D2D1_MATRIX_3X2_F* world_transform,
        D2D1_RECT_F* bounds) const noexcept override
    {
        if (bounds == nullptr) {
            return E_POINTER;
        }
        *bounds = {};
        if (!is_closed()) {
            return D2DERR_WRONG_STATE;
        }
        if (!compat_finite_transform(world_transform)) {
            return E_INVALIDARG;
        }
        D2D1_RECT_F result = {
            std::numeric_limits<float>::max(),
            std::numeric_limits<float>::max(),
            -std::numeric_limits<float>::max(),
            -std::numeric_limits<float>::max()};
        bool has_bounds = false;
        const auto include = [&](D2D1_POINT_2F point) {
            result.left = std::min(result.left, point.x);
            result.top = std::min(result.top, point.y);
            result.right = std::max(result.right, point.x);
            result.bottom = std::max(result.bottom, point.y);
            has_bounds = true;
        };
        const bool visited = compat_visit_path(
            *data_,
            world_transform,
            false,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            [&](D2D1_POINT_2F start, uint32_t, const compat_path_figure&) {
                include(start);
                return true;
            },
            [&](D2D1_POINT_2F start,
                D2D1_POINT_2F end,
                uint32_t,
                uint32_t,
                D2D1_PATH_SEGMENT) {
                include(start);
                include(end);
                return true;
            },
            [&](D2D1_POINT_2F start,
                D2D1_POINT_2F control1,
                D2D1_POINT_2F control2,
                D2D1_POINT_2F end,
                uint32_t,
                uint32_t,
                D2D1_PATH_SEGMENT) {
                compat_include_cubic_bounds(
                    start, control1, control2, end, result);
                has_bounds = true;
                return true;
            },
            [](D2D1_POINT_2F,
               D2D1_POINT_2F,
               uint32_t,
               const compat_path_figure&) { return true; });
        if (!visited) {
            return E_INVALIDARG;
        }
        if (has_bounds) {
            *bounds = result;
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetWidenedBounds(
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_RECT_F* bounds) const noexcept override
    {
        if (bounds == nullptr) {
            return E_POINTER;
        }
        *bounds = {};
        direct2d_compat::path_geometry* raw_path = nullptr;
        const HRESULT cache_status = get_portable_path(&raw_path);
        progpu::native::com::pointer<direct2d_compat::path_geometry> path;
        path.attach(raw_path);
        if (FAILED(cache_status)) {
            return cache_status;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        direct2d_compat::rectangle_f result{};
        const HRESULT status = path->GetWidenedBounds(
            stroke_width,
            reinterpret_cast<direct2d_compat::stroke_style*>(stroke_style),
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            &result);
        if (SUCCEEDED(status)) {
            *bounds = {result.left, result.top, result.right, result.bottom};
        }
        return status;
    }

    HRESULT STDMETHODCALLTYPE StrokeContainsPoint(
        D2D1_POINT_2F point,
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        if (contains == nullptr) {
            return E_POINTER;
        }
        *contains = FALSE;
        direct2d_compat::path_geometry* raw_path = nullptr;
        const HRESULT cache_status = get_portable_path(&raw_path);
        progpu::native::com::pointer<direct2d_compat::path_geometry> path;
        path.attach(raw_path);
        if (FAILED(cache_status)) {
            return cache_status;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        std::int32_t result = 0;
        const HRESULT status = path->StrokeContainsPoint(
            {point.x, point.y},
            stroke_width,
            reinterpret_cast<direct2d_compat::stroke_style*>(stroke_style),
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            &result);
        *contains = result == 0 ? FALSE : TRUE;
        return status;
    }

    HRESULT STDMETHODCALLTYPE FillContainsPoint(
        D2D1_POINT_2F point,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        if (contains == nullptr) {
            return E_POINTER;
        }
        *contains = FALSE;
        if (!compat_finite_point(point) ||
            !valid_tolerance(flattening_tolerance) ||
            !compat_finite_transform(world_transform)) {
            return E_INVALIDARG;
        }
        std::vector<compat_flat_edge> edges;
        HRESULT hr = collect_flat_edges(
            world_transform,
            flattening_tolerance,
            true,
            edges);
        if (FAILED(hr)) {
            return hr;
        }
        int64_t winding = 0;
        bool alternate = false;
        bool boundary = false;
        const double tolerance_squared =
            static_cast<double>(flattening_tolerance) *
            flattening_tolerance;
        for (const auto& edge : edges) {
            if (data_->figures[edge.figure_index].begin !=
                D2D1_FIGURE_BEGIN_FILLED) {
                continue;
            }
            const double dx = static_cast<double>(edge.end.x) - edge.start.x;
            const double dy = static_cast<double>(edge.end.y) - edge.start.y;
            const double px = static_cast<double>(point.x) - edge.start.x;
            const double py = static_cast<double>(point.y) - edge.start.y;
            const double projection = px * dx + py * dy;
            const double length_squared = dx * dx + dy * dy;
            if (projection >= 0.0 && projection <= length_squared &&
                compat_point_line_distance_squared(
                    point, edge.start, edge.end) <= tolerance_squared) {
                boundary = true;
                break;
            }
            const bool upward = edge.start.y <= point.y &&
                edge.end.y > point.y;
            const bool downward = edge.start.y > point.y &&
                edge.end.y <= point.y;
            if (!upward && !downward) {
                continue;
            }
            const double cross = dx * py - dy * px;
            if ((upward && cross > 0.0) ||
                (downward && cross < 0.0)) {
                alternate = !alternate;
                winding += upward ? 1 : -1;
            }
        }
        *contains = boundary ||
                (data_->fill_mode == D2D1_FILL_MODE_ALTERNATE
                        ? alternate
                        : winding != 0)
            ? TRUE
            : FALSE;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE CompareWithGeometry(
        ID2D1Geometry* input_geometry,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        D2D1_GEOMETRY_RELATION* relation) const noexcept override
    {
        if (relation == nullptr) {
            return E_POINTER;
        }
        *relation = D2D1_GEOMETRY_RELATION_UNKNOWN;
        direct2d_compat::path_geometry* raw_path = nullptr;
        const HRESULT cache_status = get_portable_path(&raw_path);
        progpu::native::com::pointer<direct2d_compat::path_geometry> path;
        path.attach(raw_path);
        if (FAILED(cache_status)) {
            return cache_status;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        direct2d_compat::geometry_relation result =
            direct2d_compat::geometry_relation::unknown;
        const HRESULT status = path->CompareWithGeometry(
            reinterpret_cast<direct2d_compat::geometry*>(input_geometry),
            compat_core_transform(input_geometry_transform, transform),
            flattening_tolerance,
            &result);
        *relation = static_cast<D2D1_GEOMETRY_RELATION>(result);
        return status;
    }

    HRESULT STDMETHODCALLTYPE Simplify(
        D2D1_GEOMETRY_SIMPLIFICATION_OPTION simplification_option,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        if (geometry_sink == nullptr) {
            return E_POINTER;
        }
        if (!is_closed()) {
            return D2DERR_WRONG_STATE;
        }
        if ((simplification_option !=
                D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES &&
             simplification_option !=
                D2D1_GEOMETRY_SIMPLIFICATION_OPTION_LINES) ||
            !valid_tolerance(flattening_tolerance) ||
            !compat_finite_transform(world_transform)) {
            return E_INVALIDARG;
        }
        D2D1_PATH_SEGMENT current_flags = D2D1_PATH_SEGMENT_FORCE_DWORD;
        geometry_sink->SetFillMode(data_->fill_mode);
        const bool flatten = simplification_option ==
            D2D1_GEOMETRY_SIMPLIFICATION_OPTION_LINES;
        const bool visited = compat_visit_path(
            *data_,
            world_transform,
            flatten,
            flattening_tolerance,
            [&](D2D1_POINT_2F start,
                uint32_t,
                const compat_path_figure& figure) {
                geometry_sink->BeginFigure(start, figure.begin);
                return true;
            },
            [&](D2D1_POINT_2F,
                D2D1_POINT_2F end,
                uint32_t,
                uint32_t,
                D2D1_PATH_SEGMENT flags) {
                if (current_flags != flags) {
                    geometry_sink->SetSegmentFlags(flags);
                    current_flags = flags;
                }
                geometry_sink->AddLines(&end, 1U);
                return true;
            },
            [&](D2D1_POINT_2F,
                D2D1_POINT_2F control1,
                D2D1_POINT_2F control2,
                D2D1_POINT_2F end,
                uint32_t,
                uint32_t,
                D2D1_PATH_SEGMENT flags) {
                if (current_flags != flags) {
                    geometry_sink->SetSegmentFlags(flags);
                    current_flags = flags;
                }
                const D2D1_BEZIER_SEGMENT bezier = {
                    control1, control2, end};
                geometry_sink->AddBeziers(&bezier, 1U);
                return true;
            },
            [&](D2D1_POINT_2F,
                D2D1_POINT_2F,
                uint32_t,
                const compat_path_figure& figure) {
                geometry_sink->EndFigure(figure.end);
                return true;
            });
        return visited ? S_OK : E_INVALIDARG;
    }

    HRESULT STDMETHODCALLTYPE Tessellate(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1TessellationSink* tessellation_sink) const noexcept override
    {
        direct2d_compat::path_geometry* raw_path = nullptr;
        const HRESULT cache_status = get_portable_path(&raw_path);
        progpu::native::com::pointer<direct2d_compat::path_geometry> path;
        path.attach(raw_path);
        if (FAILED(cache_status)) {
            return cache_status;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        return path->Tessellate(
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            reinterpret_cast<direct2d_compat::tessellation_sink*>(
                tessellation_sink));
    }

    HRESULT STDMETHODCALLTYPE CombineWithGeometry(
        ID2D1Geometry* input_geometry,
        D2D1_COMBINE_MODE combine_mode,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        direct2d_compat::path_geometry* raw_path = nullptr;
        const HRESULT cache_status = get_portable_path(&raw_path);
        progpu::native::com::pointer<direct2d_compat::path_geometry> path;
        path.attach(raw_path);
        if (FAILED(cache_status)) {
            return cache_status;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        return path->CombineWithGeometry(
            reinterpret_cast<direct2d_compat::geometry*>(input_geometry),
            static_cast<direct2d_compat::combine_mode>(combine_mode),
            compat_core_transform(input_geometry_transform, transform),
            flattening_tolerance,
            reinterpret_cast<direct2d_compat::simplified_geometry_sink*>(
                geometry_sink));
    }

    HRESULT STDMETHODCALLTYPE Outline(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        direct2d_compat::path_geometry* raw_path = nullptr;
        const HRESULT cache_status = get_portable_path(&raw_path);
        progpu::native::com::pointer<direct2d_compat::path_geometry> path;
        path.attach(raw_path);
        if (FAILED(cache_status)) {
            return cache_status;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        return path->Outline(
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            reinterpret_cast<direct2d_compat::simplified_geometry_sink*>(
                geometry_sink));
    }

    HRESULT STDMETHODCALLTYPE ComputeArea(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* area) const noexcept override
    {
        if (area == nullptr) {
            return E_POINTER;
        }
        *area = 0.0F;
        direct2d_compat::path_geometry* raw_path = nullptr;
        const HRESULT cache_status = get_portable_path(&raw_path);
        progpu::native::com::pointer<direct2d_compat::path_geometry> path;
        path.attach(raw_path);
        if (FAILED(cache_status)) {
            return cache_status;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        return path->ComputeArea(
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            area);
    }

    HRESULT STDMETHODCALLTYPE ComputeLength(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* length) const noexcept override
    {
        if (length == nullptr) {
            return E_POINTER;
        }
        *length = 0.0F;
        if (!valid_tolerance(flattening_tolerance) ||
            !compat_finite_transform(world_transform)) {
            return E_INVALIDARG;
        }
        std::vector<compat_flat_edge> edges;
        HRESULT hr = collect_flat_edges(
            world_transform,
            flattening_tolerance,
            false,
            edges);
        if (FAILED(hr)) {
            return hr;
        }
        double result = 0.0;
        for (const auto& edge : edges) {
            result += std::hypot(
                static_cast<double>(edge.end.x) - edge.start.x,
                static_cast<double>(edge.end.y) - edge.start.y);
        }
        if (!std::isfinite(result) ||
            result > std::numeric_limits<float>::max()) {
            return E_INVALIDARG;
        }
        *length = static_cast<float>(result);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE ComputePointAtLength(
        FLOAT length,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_POINT_2F* point,
        D2D1_POINT_2F* unit_tangent_vector) const noexcept override
    {
        if (point == nullptr && unit_tangent_vector == nullptr) {
            return E_POINTER;
        }
        if (point != nullptr) {
            *point = {};
        }
        if (unit_tangent_vector != nullptr) {
            *unit_tangent_vector = {};
        }
        if (!std::isfinite(length) ||
            !valid_tolerance(flattening_tolerance) ||
            !compat_finite_transform(world_transform)) {
            return E_INVALIDARG;
        }
        std::vector<compat_flat_edge> edges;
        HRESULT hr = collect_flat_edges(
            world_transform,
            flattening_tolerance,
            false,
            edges);
        if (FAILED(hr)) {
            return hr;
        }
        return point_at_length(
            edges,
            std::max(length, 0.0F),
            0U,
            point,
            unit_tangent_vector,
            nullptr);
    }

    HRESULT STDMETHODCALLTYPE Widen(
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        direct2d_compat::path_geometry* raw_path = nullptr;
        const HRESULT cache_status = get_portable_path(&raw_path);
        progpu::native::com::pointer<direct2d_compat::path_geometry> path;
        path.attach(raw_path);
        if (FAILED(cache_status)) {
            return cache_status;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        return path->Widen(
            stroke_width,
            reinterpret_cast<direct2d_compat::stroke_style*>(stroke_style),
            compat_core_transform(world_transform, transform),
            flattening_tolerance,
            reinterpret_cast<direct2d_compat::simplified_geometry_sink*>(
                geometry_sink));
    }

    HRESULT STDMETHODCALLTYPE Open(
        ID2D1GeometrySink** geometry_sink) noexcept override
    {
        if (geometry_sink == nullptr) {
            return E_POINTER;
        }
        *geometry_sink = nullptr;
        const std::lock_guard lock(data_->mutex);
        if (data_->published_state.load(std::memory_order_relaxed) !=
            compat_path_state::fresh) {
            return D2DERR_WRONG_STATE;
        }
        auto* sink = new (std::nothrow) ProGpuD2DGeometrySink(data_);
        if (sink == nullptr) {
            return E_OUTOFMEMORY;
        }
        data_->published_state.store(
            compat_path_state::open,
            std::memory_order_release);
        *geometry_sink = sink;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Stream(
        ID2D1GeometrySink* geometry_sink) const noexcept override
    {
        if (geometry_sink == nullptr) {
            return E_POINTER;
        }
        if (!is_closed()) {
            return D2DERR_WRONG_STATE;
        }
        geometry_sink->SetFillMode(data_->fill_mode);
        D2D1_PATH_SEGMENT current_flags = D2D1_PATH_SEGMENT_FORCE_DWORD;
        for (const auto& figure : data_->figures) {
            geometry_sink->BeginFigure(figure.start, figure.begin);
            for (uint32_t local_index = 0U;
                 local_index < figure.segment_count;
                 ++local_index) {
                const auto& segment = data_->segments[
                    figure.first_segment + local_index];
                if (segment.flags != current_flags) {
                    geometry_sink->SetSegmentFlags(segment.flags);
                    current_flags = segment.flags;
                }
                switch (segment.kind) {
                case compat_path_segment_kind::line:
                    geometry_sink->AddLine(segment.end);
                    break;
                case compat_path_segment_kind::cubic: {
                    const D2D1_BEZIER_SEGMENT bezier = {
                        segment.control1,
                        segment.control2,
                        segment.end};
                    geometry_sink->AddBezier(&bezier);
                    break;
                }
                case compat_path_segment_kind::quadratic: {
                    const D2D1_QUADRATIC_BEZIER_SEGMENT bezier = {
                        segment.control1,
                        segment.end};
                    geometry_sink->AddQuadraticBezier(&bezier);
                    break;
                }
                case compat_path_segment_kind::arc:
                    geometry_sink->AddArc(&segment.arc);
                    break;
                }
            }
            geometry_sink->EndFigure(figure.end);
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetSegmentCount(
        UINT32* count) const noexcept override
    {
        if (count == nullptr) {
            return E_POINTER;
        }
        *count = 0U;
        if (!is_closed()) {
            return D2DERR_WRONG_STATE;
        }
        *count = data_->public_segment_count;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetFigureCount(
        UINT32* count) const noexcept override
    {
        if (count == nullptr) {
            return E_POINTER;
        }
        *count = 0U;
        if (!is_closed()) {
            return D2DERR_WRONG_STATE;
        }
        *count = static_cast<uint32_t>(data_->figures.size());
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE ComputePointAndSegmentAtLength(
        FLOAT length,
        UINT32 start_segment,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_POINT_DESCRIPTION* point_description) const noexcept override
    {
        if (point_description == nullptr) {
            return E_POINTER;
        }
        *point_description = {};
        if (!is_closed()) {
            return D2DERR_WRONG_STATE;
        }
        if (!std::isfinite(length) ||
            !valid_tolerance(flattening_tolerance) ||
            !compat_finite_transform(world_transform) ||
            static_cast<size_t>(start_segment) >=
                data_->public_segment_count) {
            return E_INVALIDARG;
        }
        std::vector<compat_flat_edge> edges;
        HRESULT hr = collect_flat_edges(
            world_transform,
            flattening_tolerance,
            false,
            edges);
        if (FAILED(hr)) {
            return hr;
        }
        D2D1_POINT_2F point{};
        D2D1_POINT_2F tangent{};
        size_t selected_edge = 0U;
        hr = point_at_length(
            edges,
            std::max(length, 0.0F),
            start_segment,
            &point,
            &tangent,
            &selected_edge);
        if (FAILED(hr)) {
            return hr;
        }
        const auto& edge = edges[selected_edge];
        double length_to_segment_end = 0.0;
        for (const auto& candidate : edges) {
            length_to_segment_end += std::hypot(
                static_cast<double>(candidate.end.x) - candidate.start.x,
                static_cast<double>(candidate.end.y) - candidate.start.y);
            if (candidate.segment_index == edge.segment_index) {
                continue;
            }
            if (candidate.segment_index > edge.segment_index) {
                length_to_segment_end -= std::hypot(
                    static_cast<double>(candidate.end.x) - candidate.start.x,
                    static_cast<double>(candidate.end.y) - candidate.start.y);
                break;
            }
        }
        if (!std::isfinite(length_to_segment_end) ||
            length_to_segment_end > std::numeric_limits<float>::max()) {
            return E_INVALIDARG;
        }
        point_description->point = point;
        point_description->unitTangentVector = tangent;
        point_description->endSegment = edge.segment_index;
        point_description->endFigure = edge.figure_index;
        point_description->lengthToEndSegment =
            static_cast<float>(length_to_segment_end);
        return S_OK;
    }

private:
    bool is_closed() const noexcept
    {
        return data_->published_state.load(std::memory_order_acquire) ==
            compat_path_state::closed;
    }

    static bool valid_tolerance(float tolerance) noexcept
    {
        return std::isfinite(tolerance) && tolerance > 0.0F;
    }

    HRESULT get_portable_path(
        direct2d_compat::path_geometry** path) const noexcept
    {
        if (path == nullptr) {
            return E_POINTER;
        }
        *path = nullptr;
        if (!is_closed()) {
            return D2DERR_WRONG_STATE;
        }
        const std::lock_guard lock(portable_path_mutex_);
        if (!portable_path_) {
            direct2d_compat::path_geometry* raw_candidate = nullptr;
            HRESULT status = direct2d_compat::detail::create_path_geometry(
                reinterpret_cast<direct2d_compat::factory*>(factory_.Get()),
                &raw_candidate);
            progpu::native::com::pointer<direct2d_compat::path_geometry>
                candidate;
            candidate.attach(raw_candidate);
            if (FAILED(status)) {
                return status;
            }
            direct2d_compat::geometry_sink* raw_sink = nullptr;
            status = candidate->Open(&raw_sink);
            progpu::native::com::pointer<direct2d_compat::geometry_sink> sink;
            sink.attach(raw_sink);
            if (FAILED(status)) {
                return status;
            }
            sink->SetFillMode(static_cast<direct2d_compat::fill_mode>(
                data_->fill_mode));
            D2D1_PATH_SEGMENT current_flags =
                D2D1_PATH_SEGMENT_FORCE_DWORD;
            for (const compat_path_figure& figure : data_->figures) {
                sink->BeginFigure(
                    {figure.start.x, figure.start.y},
                    static_cast<direct2d_compat::figure_begin>(
                        figure.begin));
                for (std::uint32_t local_index = 0U;
                     local_index < figure.segment_count;
                     ++local_index) {
                    const compat_path_segment& segment = data_->segments[
                        figure.first_segment + local_index];
                    if (segment.flags != current_flags) {
                        sink->SetSegmentFlags(
                            static_cast<direct2d_compat::path_segment>(
                                segment.flags));
                        current_flags = segment.flags;
                    }
                    switch (segment.kind) {
                    case compat_path_segment_kind::line: {
                        const direct2d_compat::point_2f point{
                            segment.end.x, segment.end.y};
                        sink->AddLine(point);
                        break;
                    }
                    case compat_path_segment_kind::cubic: {
                        const direct2d_compat::bezier_segment bezier{
                            {segment.control1.x, segment.control1.y},
                            {segment.control2.x, segment.control2.y},
                            {segment.end.x, segment.end.y}};
                        sink->AddBezier(&bezier);
                        break;
                    }
                    case compat_path_segment_kind::quadratic: {
                        const direct2d_compat::quadratic_bezier_segment
                            bezier{
                                {segment.control1.x, segment.control1.y},
                                {segment.end.x, segment.end.y}};
                        sink->AddQuadraticBezier(&bezier);
                        break;
                    }
                    case compat_path_segment_kind::arc: {
                        const direct2d_compat::arc_segment arc{
                            {segment.arc.point.x, segment.arc.point.y},
                            {segment.arc.size.width, segment.arc.size.height},
                            segment.arc.rotationAngle,
                            static_cast<direct2d_compat::sweep_direction>(
                                segment.arc.sweepDirection),
                            static_cast<direct2d_compat::arc_size>(
                                segment.arc.arcSize)};
                        sink->AddArc(&arc);
                        break;
                    }
                    }
                }
                sink->EndFigure(
                    static_cast<direct2d_compat::figure_end>(figure.end));
            }
            status = sink->Close();
            if (FAILED(status)) {
                return status;
            }
            portable_path_ = std::move(candidate);
        }
        *path = portable_path_.get();
        (*path)->AddRef();
        return S_OK;
    }

    HRESULT collect_flat_edges(
        const D2D1_MATRIX_3X2_F* transform,
        float tolerance,
        bool close_open_filled_figures,
        std::vector<compat_flat_edge>& edges) const noexcept
    {
        edges.clear();
        if (!is_closed()) {
            return D2DERR_WRONG_STATE;
        }
        try {
            edges.reserve(data_->segments.size() * 2U +
                data_->figures.size());
            const bool visited = compat_visit_path(
                *data_,
                transform,
                true,
                tolerance,
                [](D2D1_POINT_2F,
                   uint32_t,
                   const compat_path_figure&) { return true; },
                [&](D2D1_POINT_2F start,
                    D2D1_POINT_2F end,
                    uint32_t segment_index,
                    uint32_t figure_index,
                    D2D1_PATH_SEGMENT) {
                    edges.push_back(
                        {start, end, segment_index, figure_index});
                    return true;
                },
                [](D2D1_POINT_2F,
                   D2D1_POINT_2F,
                   D2D1_POINT_2F,
                   D2D1_POINT_2F,
                   uint32_t,
                   uint32_t,
                   D2D1_PATH_SEGMENT) { return true; },
                [&](D2D1_POINT_2F current,
                    D2D1_POINT_2F start,
                    uint32_t figure_index,
                    const compat_path_figure& figure) {
                    const bool close =
                        figure.end == D2D1_FIGURE_END_CLOSED ||
                        (close_open_filled_figures &&
                            figure.begin == D2D1_FIGURE_BEGIN_FILLED);
                    if (close && !compat_same_point(current, start)) {
                        const uint32_t segment_index =
                            figure.first_public_segment +
                            figure.segment_count;
                        edges.push_back(
                            {current, start, segment_index, figure_index});
                    }
                    return true;
                });
            return visited ? S_OK : E_INVALIDARG;
        } catch (const std::bad_alloc&) {
            edges.clear();
            return E_OUTOFMEMORY;
        } catch (...) {
            edges.clear();
            return E_FAIL;
        }
    }

    static HRESULT point_at_length(
        const std::vector<compat_flat_edge>& edges,
        float length,
        uint32_t start_segment,
        D2D1_POINT_2F* point,
        D2D1_POINT_2F* tangent,
        size_t* selected_edge) noexcept
    {
        if (edges.empty()) {
            return E_INVALIDARG;
        }
        double remaining = length;
        size_t last_eligible = std::numeric_limits<size_t>::max();
        for (size_t index = 0U; index < edges.size(); ++index) {
            const auto& edge = edges[index];
            if (edge.segment_index < start_segment) {
                continue;
            }
            const double dx = static_cast<double>(edge.end.x) - edge.start.x;
            const double dy = static_cast<double>(edge.end.y) - edge.start.y;
            const double edge_length = std::hypot(dx, dy);
            if (edge_length == 0.0) {
                continue;
            }
            last_eligible = index;
            if (remaining <= edge_length) {
                const double ratio = remaining / edge_length;
                if (point != nullptr) {
                    point->x = static_cast<float>(edge.start.x + dx * ratio);
                    point->y = static_cast<float>(edge.start.y + dy * ratio);
                }
                if (tangent != nullptr) {
                    tangent->x = static_cast<float>(dx / edge_length);
                    tangent->y = static_cast<float>(dy / edge_length);
                }
                if (selected_edge != nullptr) {
                    *selected_edge = index;
                }
                return S_OK;
            }
            remaining -= edge_length;
        }
        if (last_eligible == std::numeric_limits<size_t>::max()) {
            return E_INVALIDARG;
        }
        const auto& edge = edges[last_eligible];
        const double dx = static_cast<double>(edge.end.x) - edge.start.x;
        const double dy = static_cast<double>(edge.end.y) - edge.start.y;
        const double edge_length = std::hypot(dx, dy);
        if (point != nullptr) {
            *point = edge.end;
        }
        if (tangent != nullptr && edge_length != 0.0) {
            tangent->x = static_cast<float>(dx / edge_length);
            tangent->y = static_cast<float>(dy / edge_length);
        }
        if (selected_edge != nullptr) {
            *selected_edge = last_eligible;
        }
        return S_OK;
    }

    std::atomic<ULONG> reference_count_{1U};
    ComPtr<ID2D1Factory1> factory_;
    std::shared_ptr<compat_path_data> data_;
    mutable std::mutex portable_path_mutex_;
    mutable progpu::native::com::pointer<direct2d_compat::path_geometry>
        portable_path_;
};

class ProGpuD2DRoundedRectangleGeometry final :
    public ID2D1RoundedRectangleGeometry {
public:
    ProGpuD2DRoundedRectangleGeometry(
        ID2D1Factory1* factory,
        const D2D1_ROUNDED_RECT& rounded_rectangle,
        ID2D1PathGeometry1* path) noexcept
        : factory_(factory),
          rounded_rectangle_(rounded_rectangle),
          path_(path)
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Resource)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Geometry)) ||
            IsEqualIID(
                interface_id,
                __uuidof(ID2D1RoundedRectangleGeometry))) {
            *value = static_cast<ID2D1RoundedRectangleGeometry*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE GetFactory(
        ID2D1Factory** factory) const noexcept override
    {
        if (factory == nullptr) {
            return;
        }
        *factory = factory_.Get();
        if (*factory != nullptr) {
            (*factory)->AddRef();
        }
    }

    HRESULT STDMETHODCALLTYPE GetBounds(
        const D2D1_MATRIX_3X2_F* world_transform,
        D2D1_RECT_F* bounds) const noexcept override
    {
        return path_->GetBounds(world_transform, bounds);
    }

    HRESULT STDMETHODCALLTYPE GetWidenedBounds(
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_RECT_F* bounds) const noexcept override
    {
        return path_->GetWidenedBounds(
            stroke_width,
            stroke_style,
            world_transform,
            flattening_tolerance,
            bounds);
    }

    HRESULT STDMETHODCALLTYPE StrokeContainsPoint(
        D2D1_POINT_2F point,
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        return path_->StrokeContainsPoint(
            point,
            stroke_width,
            stroke_style,
            world_transform,
            flattening_tolerance,
            contains);
    }

    HRESULT STDMETHODCALLTYPE FillContainsPoint(
        D2D1_POINT_2F point,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        if (contains == nullptr) {
            return E_POINTER;
        }
        *contains = FALSE;
        progpu_native_direct2d_matrix_3x2_f transform_storage{};
        uint32_t core_contains = 0U;
        const HRESULT result =
            direct2d_core::rounded_rectangle_fill_contains_point(
                compat_core_rounded_rectangle(rounded_rectangle_),
                {point.x, point.y},
                compat_core_transform(
                    world_transform, transform_storage),
                flattening_tolerance,
                &core_contains);
        if (SUCCEEDED(result)) {
            *contains = core_contains == 0U ? FALSE : TRUE;
        }
        return result;
    }

    HRESULT STDMETHODCALLTYPE CompareWithGeometry(
        ID2D1Geometry* input_geometry,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        D2D1_GEOMETRY_RELATION* relation) const noexcept override
    {
        return path_->CompareWithGeometry(
            input_geometry,
            input_geometry_transform,
            flattening_tolerance,
            relation);
    }

    HRESULT STDMETHODCALLTYPE Simplify(
        D2D1_GEOMETRY_SIMPLIFICATION_OPTION simplification_option,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->Simplify(
            simplification_option,
            world_transform,
            flattening_tolerance,
            geometry_sink);
    }

    HRESULT STDMETHODCALLTYPE Tessellate(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1TessellationSink* tessellation_sink) const noexcept override
    {
        return path_->Tessellate(
            world_transform,
            flattening_tolerance,
            tessellation_sink);
    }

    HRESULT STDMETHODCALLTYPE CombineWithGeometry(
        ID2D1Geometry* input_geometry,
        D2D1_COMBINE_MODE combine_mode,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->CombineWithGeometry(
            input_geometry,
            combine_mode,
            input_geometry_transform,
            flattening_tolerance,
            geometry_sink);
    }

    HRESULT STDMETHODCALLTYPE Outline(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->Outline(
            world_transform,
            flattening_tolerance,
            geometry_sink);
    }

    HRESULT STDMETHODCALLTYPE ComputeArea(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* area) const noexcept override
    {
        return path_->ComputeArea(
            world_transform,
            flattening_tolerance,
            area);
    }

    HRESULT STDMETHODCALLTYPE ComputeLength(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* length) const noexcept override
    {
        return path_->ComputeLength(
            world_transform,
            flattening_tolerance,
            length);
    }

    HRESULT STDMETHODCALLTYPE ComputePointAtLength(
        FLOAT length,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_POINT_2F* point,
        D2D1_POINT_2F* unit_tangent_vector) const noexcept override
    {
        return path_->ComputePointAtLength(
            length,
            world_transform,
            flattening_tolerance,
            point,
            unit_tangent_vector);
    }

    HRESULT STDMETHODCALLTYPE Widen(
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->Widen(
            stroke_width,
            stroke_style,
            world_transform,
            flattening_tolerance,
            geometry_sink);
    }

    void STDMETHODCALLTYPE GetRoundedRect(
        D2D1_ROUNDED_RECT* rounded_rectangle) const noexcept override
    {
        if (rounded_rectangle != nullptr) {
            *rounded_rectangle = rounded_rectangle_;
        }
    }

private:
    std::atomic<ULONG> reference_count_{1U};
    ComPtr<ID2D1Factory1> factory_;
    D2D1_ROUNDED_RECT rounded_rectangle_{};
    ComPtr<ID2D1PathGeometry1> path_;
};

class ProGpuD2DEllipseGeometry final : public ID2D1EllipseGeometry {
public:
    ProGpuD2DEllipseGeometry(
        ID2D1Factory1* factory,
        const D2D1_ELLIPSE& ellipse,
        ID2D1PathGeometry1* path) noexcept
        : factory_(factory), ellipse_(ellipse), path_(path)
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Resource)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Geometry)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1EllipseGeometry))) {
            *value = static_cast<ID2D1EllipseGeometry*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE GetFactory(
        ID2D1Factory** factory) const noexcept override
    {
        if (factory == nullptr) {
            return;
        }
        *factory = factory_.Get();
        if (*factory != nullptr) {
            (*factory)->AddRef();
        }
    }

    HRESULT STDMETHODCALLTYPE GetBounds(
        const D2D1_MATRIX_3X2_F* world_transform,
        D2D1_RECT_F* bounds) const noexcept override
    {
        return path_->GetBounds(world_transform, bounds);
    }

    HRESULT STDMETHODCALLTYPE GetWidenedBounds(
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_RECT_F* bounds) const noexcept override
    {
        return path_->GetWidenedBounds(
            stroke_width,
            stroke_style,
            world_transform,
            flattening_tolerance,
            bounds);
    }

    HRESULT STDMETHODCALLTYPE StrokeContainsPoint(
        D2D1_POINT_2F point,
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        return path_->StrokeContainsPoint(
            point,
            stroke_width,
            stroke_style,
            world_transform,
            flattening_tolerance,
            contains);
    }

    HRESULT STDMETHODCALLTYPE FillContainsPoint(
        D2D1_POINT_2F point,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        if (contains == nullptr) {
            return E_POINTER;
        }
        *contains = FALSE;
        progpu_native_direct2d_matrix_3x2_f transform_storage{};
        std::uint32_t core_contains = 0U;
        const HRESULT result = direct2d_core::ellipse_fill_contains_point(
            compat_core_ellipse(ellipse_),
            {point.x, point.y},
            compat_core_transform(world_transform, transform_storage),
            flattening_tolerance,
            &core_contains);
        if (SUCCEEDED(result)) {
            *contains = core_contains == 0U ? FALSE : TRUE;
        }
        return result;
    }

    HRESULT STDMETHODCALLTYPE CompareWithGeometry(
        ID2D1Geometry* input_geometry,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        D2D1_GEOMETRY_RELATION* relation) const noexcept override
    {
        return path_->CompareWithGeometry(
            input_geometry,
            input_geometry_transform,
            flattening_tolerance,
            relation);
    }

    HRESULT STDMETHODCALLTYPE Simplify(
        D2D1_GEOMETRY_SIMPLIFICATION_OPTION simplification_option,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->Simplify(
            simplification_option,
            world_transform,
            flattening_tolerance,
            geometry_sink);
    }

    HRESULT STDMETHODCALLTYPE Tessellate(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1TessellationSink* tessellation_sink) const noexcept override
    {
        return path_->Tessellate(
            world_transform,
            flattening_tolerance,
            tessellation_sink);
    }

    HRESULT STDMETHODCALLTYPE CombineWithGeometry(
        ID2D1Geometry* input_geometry,
        D2D1_COMBINE_MODE combine_mode,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->CombineWithGeometry(
            input_geometry,
            combine_mode,
            input_geometry_transform,
            flattening_tolerance,
            geometry_sink);
    }

    HRESULT STDMETHODCALLTYPE Outline(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->Outline(
            world_transform,
            flattening_tolerance,
            geometry_sink);
    }

    HRESULT STDMETHODCALLTYPE ComputeArea(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* area) const noexcept override
    {
        return path_->ComputeArea(
            world_transform,
            flattening_tolerance,
            area);
    }

    HRESULT STDMETHODCALLTYPE ComputeLength(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* length) const noexcept override
    {
        // Direct2D's analytic ellipse length at tolerance t matches the
        // retained four-cubic approximation at t / 2. Arbitrary path
        // geometry intentionally retains the caller's unscaled tolerance.
        return path_->ComputeLength(
            world_transform,
            flattening_tolerance * 0.5F,
            length);
    }

    HRESULT STDMETHODCALLTYPE ComputePointAtLength(
        FLOAT length,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_POINT_2F* point,
        D2D1_POINT_2F* unit_tangent_vector) const noexcept override
    {
        return path_->ComputePointAtLength(
            length,
            world_transform,
            flattening_tolerance * 0.5F,
            point,
            unit_tangent_vector);
    }

    HRESULT STDMETHODCALLTYPE Widen(
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->Widen(
            stroke_width,
            stroke_style,
            world_transform,
            flattening_tolerance,
            geometry_sink);
    }

    void STDMETHODCALLTYPE GetEllipse(D2D1_ELLIPSE* ellipse) const noexcept override
    {
        if (ellipse != nullptr) {
            *ellipse = ellipse_;
        }
    }

private:
    std::atomic<ULONG> reference_count_{1U};
    ComPtr<ID2D1Factory1> factory_;
    D2D1_ELLIPSE ellipse_{};
    ComPtr<ID2D1PathGeometry1> path_;
};

class ProGpuD2DTransformedGeometry final :
    public ID2D1TransformedGeometry {
public:
    ProGpuD2DTransformedGeometry(
        ID2D1Factory1* factory,
        ID2D1Geometry* source,
        const D2D1_MATRIX_3X2_F& transform) noexcept
        : factory_(factory), source_(source), transform_(transform)
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Resource)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Geometry)) ||
            IsEqualIID(
                interface_id,
                __uuidof(ID2D1TransformedGeometry))) {
            *value = static_cast<ID2D1TransformedGeometry*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE GetFactory(
        ID2D1Factory** factory) const noexcept override
    {
        if (factory == nullptr) {
            return;
        }
        *factory = factory_.Get();
        if (*factory != nullptr) {
            (*factory)->AddRef();
        }
    }

    HRESULT STDMETHODCALLTYPE GetBounds(
        const D2D1_MATRIX_3X2_F* world_transform,
        D2D1_RECT_F* bounds) const noexcept override
    {
        if (bounds == nullptr) {
            return E_POINTER;
        }
        *bounds = {};
        D2D1_MATRIX_3X2_F composed{};
        if (!compose(world_transform, composed)) {
            return E_INVALIDARG;
        }
        return source_->GetBounds(&composed, bounds);
    }

    HRESULT STDMETHODCALLTYPE GetWidenedBounds(
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_RECT_F* bounds) const noexcept override
    {
        if (bounds == nullptr) {
            return E_POINTER;
        }
        *bounds = {};
        D2D1_MATRIX_3X2_F composed{};
        if (!compose(world_transform, composed)) {
            return E_INVALIDARG;
        }
        return source_->GetWidenedBounds(
            stroke_width,
            stroke_style,
            &composed,
            flattening_tolerance,
            bounds);
    }

    HRESULT STDMETHODCALLTYPE StrokeContainsPoint(
        D2D1_POINT_2F point,
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        if (contains == nullptr) {
            return E_POINTER;
        }
        *contains = FALSE;
        D2D1_MATRIX_3X2_F composed{};
        if (!compose(world_transform, composed)) {
            return E_INVALIDARG;
        }
        return source_->StrokeContainsPoint(
            point,
            stroke_width,
            stroke_style,
            &composed,
            flattening_tolerance,
            contains);
    }

    HRESULT STDMETHODCALLTYPE FillContainsPoint(
        D2D1_POINT_2F point,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        if (contains == nullptr) {
            return E_POINTER;
        }
        *contains = FALSE;
        D2D1_MATRIX_3X2_F composed{};
        if (!compose(world_transform, composed)) {
            return E_INVALIDARG;
        }
        return source_->FillContainsPoint(
            point,
            &composed,
            flattening_tolerance,
            contains);
    }

    HRESULT STDMETHODCALLTYPE CompareWithGeometry(
        ID2D1Geometry* input_geometry,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        D2D1_GEOMETRY_RELATION* relation) const noexcept override
    {
        if (relation == nullptr) {
            return E_POINTER;
        }
        *relation = D2D1_GEOMETRY_RELATION_UNKNOWN;
        direct2d_compat::path_geometry* raw_path = nullptr;
        const HRESULT cache_status = get_portable_path(&raw_path);
        progpu::native::com::pointer<direct2d_compat::path_geometry> path;
        path.attach(raw_path);
        if (FAILED(cache_status)) {
            return cache_status;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        direct2d_compat::geometry_relation result =
            direct2d_compat::geometry_relation::unknown;
        const HRESULT status = path->CompareWithGeometry(
            reinterpret_cast<direct2d_compat::geometry*>(input_geometry),
            compat_core_transform(input_geometry_transform, transform),
            flattening_tolerance,
            &result);
        *relation = static_cast<D2D1_GEOMETRY_RELATION>(result);
        return status;
    }

    HRESULT STDMETHODCALLTYPE Simplify(
        D2D1_GEOMETRY_SIMPLIFICATION_OPTION simplification_option,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        if (geometry_sink == nullptr) {
            return E_POINTER;
        }
        D2D1_MATRIX_3X2_F composed{};
        if (!compose(world_transform, composed)) {
            return E_INVALIDARG;
        }
        return source_->Simplify(
            simplification_option,
            &composed,
            flattening_tolerance,
            geometry_sink);
    }

    HRESULT STDMETHODCALLTYPE Tessellate(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1TessellationSink* tessellation_sink) const noexcept override
    {
        if (tessellation_sink == nullptr) {
            return E_POINTER;
        }
        D2D1_MATRIX_3X2_F composed{};
        if (!compose(world_transform, composed)) {
            return E_INVALIDARG;
        }
        return source_->Tessellate(
            &composed,
            flattening_tolerance,
            tessellation_sink);
    }

    HRESULT STDMETHODCALLTYPE CombineWithGeometry(
        ID2D1Geometry* input_geometry,
        D2D1_COMBINE_MODE combine_mode,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        direct2d_compat::path_geometry* raw_path = nullptr;
        const HRESULT cache_status = get_portable_path(&raw_path);
        progpu::native::com::pointer<direct2d_compat::path_geometry> path;
        path.attach(raw_path);
        if (FAILED(cache_status)) {
            return cache_status;
        }
        progpu_native_direct2d_matrix_3x2_f transform{};
        return path->CombineWithGeometry(
            reinterpret_cast<direct2d_compat::geometry*>(input_geometry),
            static_cast<direct2d_compat::combine_mode>(combine_mode),
            compat_core_transform(input_geometry_transform, transform),
            flattening_tolerance,
            reinterpret_cast<direct2d_compat::simplified_geometry_sink*>(
                geometry_sink));
    }

    HRESULT STDMETHODCALLTYPE Outline(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        if (geometry_sink == nullptr) {
            return E_POINTER;
        }
        D2D1_MATRIX_3X2_F composed{};
        if (!compose(world_transform, composed)) {
            return E_INVALIDARG;
        }
        return source_->Outline(
            &composed,
            flattening_tolerance,
            geometry_sink);
    }

    HRESULT STDMETHODCALLTYPE ComputeArea(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* area) const noexcept override
    {
        if (area == nullptr) {
            return E_POINTER;
        }
        *area = 0.0F;
        D2D1_MATRIX_3X2_F composed{};
        if (!compose(world_transform, composed)) {
            return E_INVALIDARG;
        }
        return source_->ComputeArea(
            &composed,
            flattening_tolerance,
            area);
    }

    HRESULT STDMETHODCALLTYPE ComputeLength(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* length) const noexcept override
    {
        if (length == nullptr) {
            return E_POINTER;
        }
        *length = 0.0F;
        D2D1_MATRIX_3X2_F composed{};
        if (!compose(world_transform, composed)) {
            return E_INVALIDARG;
        }
        return source_->ComputeLength(
            &composed,
            flattening_tolerance,
            length);
    }

    HRESULT STDMETHODCALLTYPE ComputePointAtLength(
        FLOAT length,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_POINT_2F* point,
        D2D1_POINT_2F* unit_tangent_vector) const noexcept override
    {
        if (point == nullptr && unit_tangent_vector == nullptr) {
            return E_POINTER;
        }
        if (point != nullptr) {
            *point = {};
        }
        if (unit_tangent_vector != nullptr) {
            *unit_tangent_vector = {};
        }
        D2D1_MATRIX_3X2_F composed{};
        if (!compose(world_transform, composed)) {
            return E_INVALIDARG;
        }
        return source_->ComputePointAtLength(
            length,
            &composed,
            flattening_tolerance,
            point,
            unit_tangent_vector);
    }

    HRESULT STDMETHODCALLTYPE Widen(
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        if (geometry_sink == nullptr) {
            return E_POINTER;
        }
        D2D1_MATRIX_3X2_F composed{};
        if (!compose(world_transform, composed)) {
            return E_INVALIDARG;
        }
        return source_->Widen(
            stroke_width,
            stroke_style,
            &composed,
            flattening_tolerance,
            geometry_sink);
    }

    void STDMETHODCALLTYPE GetSourceGeometry(
        ID2D1Geometry** source) const noexcept override
    {
        if (source == nullptr) {
            return;
        }
        *source = source_.Get();
        if (*source != nullptr) {
            (*source)->AddRef();
        }
    }

    void STDMETHODCALLTYPE GetTransform(
        D2D1_MATRIX_3X2_F* transform) const noexcept override
    {
        if (transform != nullptr) {
            *transform = transform_;
        }
    }

private:
    HRESULT get_portable_path(
        direct2d_compat::path_geometry** path) const noexcept
    {
        if (path == nullptr) {
            return E_POINTER;
        }
        *path = nullptr;
        const std::lock_guard lock(portable_path_mutex_);
        if (!portable_path_) {
            direct2d_compat::path_geometry* raw_candidate = nullptr;
            HRESULT status = direct2d_compat::detail::create_path_geometry(
                reinterpret_cast<direct2d_compat::factory*>(factory_.Get()),
                &raw_candidate);
            progpu::native::com::pointer<direct2d_compat::path_geometry>
                candidate;
            candidate.attach(raw_candidate);
            if (FAILED(status)) {
                return status;
            }
            direct2d_compat::geometry_sink* raw_sink = nullptr;
            status = candidate->Open(&raw_sink);
            progpu::native::com::pointer<direct2d_compat::geometry_sink> sink;
            sink.attach(raw_sink);
            if (FAILED(status)) {
                return status;
            }
            status = source_->Simplify(
                D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES,
                &transform_,
                D2D1_DEFAULT_FLATTENING_TOLERANCE,
                reinterpret_cast<ID2D1SimplifiedGeometrySink*>(sink.get()));
            if (FAILED(status)) {
                return status;
            }
            status = sink->Close();
            if (FAILED(status)) {
                return status;
            }
            portable_path_ = std::move(candidate);
        }
        *path = portable_path_.get();
        (*path)->AddRef();
        return S_OK;
    }

    bool compose(
        const D2D1_MATRIX_3X2_F* world_transform,
        D2D1_MATRIX_3X2_F& composed) const noexcept
    {
        return compat_compose_transform(
            transform_, world_transform, composed);
    }

    std::atomic<ULONG> reference_count_{1U};
    ComPtr<ID2D1Factory1> factory_;
    ComPtr<ID2D1Geometry> source_;
    D2D1_MATRIX_3X2_F transform_{};
    mutable std::mutex portable_path_mutex_;
    mutable progpu::native::com::pointer<direct2d_compat::path_geometry>
        portable_path_;
};

class ProGpuD2DGeometryGroup final : public ID2D1GeometryGroup {
public:
    ProGpuD2DGeometryGroup(
        ID2D1Factory1* factory,
        D2D1_FILL_MODE fill_mode,
        std::vector<ComPtr<ID2D1Geometry>>&& sources,
        ID2D1PathGeometry1* path) noexcept
        : factory_(factory),
          fill_mode_(fill_mode),
          sources_(std::move(sources)),
          path_(path)
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Resource)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Geometry)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1GeometryGroup))) {
            *value = static_cast<ID2D1GeometryGroup*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE GetFactory(
        ID2D1Factory** factory) const noexcept override
    {
        if (factory == nullptr) {
            return;
        }
        *factory = factory_.Get();
        if (*factory != nullptr) {
            (*factory)->AddRef();
        }
    }

    HRESULT STDMETHODCALLTYPE GetBounds(
        const D2D1_MATRIX_3X2_F* world_transform,
        D2D1_RECT_F* bounds) const noexcept override
    {
        return path_->GetBounds(world_transform, bounds);
    }

    HRESULT STDMETHODCALLTYPE GetWidenedBounds(
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_RECT_F* bounds) const noexcept override
    {
        return path_->GetWidenedBounds(
            stroke_width,
            stroke_style,
            world_transform,
            flattening_tolerance,
            bounds);
    }

    HRESULT STDMETHODCALLTYPE StrokeContainsPoint(
        D2D1_POINT_2F point,
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        return path_->StrokeContainsPoint(
            point,
            stroke_width,
            stroke_style,
            world_transform,
            flattening_tolerance,
            contains);
    }

    HRESULT STDMETHODCALLTYPE FillContainsPoint(
        D2D1_POINT_2F point,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        BOOL* contains) const noexcept override
    {
        return path_->FillContainsPoint(
            point,
            world_transform,
            flattening_tolerance,
            contains);
    }

    HRESULT STDMETHODCALLTYPE CompareWithGeometry(
        ID2D1Geometry* input_geometry,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        D2D1_GEOMETRY_RELATION* relation) const noexcept override
    {
        return path_->CompareWithGeometry(
            input_geometry,
            input_geometry_transform,
            flattening_tolerance,
            relation);
    }

    HRESULT STDMETHODCALLTYPE Simplify(
        D2D1_GEOMETRY_SIMPLIFICATION_OPTION simplification_option,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->Simplify(
            simplification_option,
            world_transform,
            flattening_tolerance,
            geometry_sink);
    }

    HRESULT STDMETHODCALLTYPE Tessellate(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1TessellationSink* tessellation_sink) const noexcept override
    {
        return path_->Tessellate(
            world_transform,
            flattening_tolerance,
            tessellation_sink);
    }

    HRESULT STDMETHODCALLTYPE CombineWithGeometry(
        ID2D1Geometry* input_geometry,
        D2D1_COMBINE_MODE combine_mode,
        const D2D1_MATRIX_3X2_F* input_geometry_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->CombineWithGeometry(
            input_geometry,
            combine_mode,
            input_geometry_transform,
            flattening_tolerance,
            geometry_sink);
    }

    HRESULT STDMETHODCALLTYPE Outline(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->Outline(
            world_transform,
            flattening_tolerance,
            geometry_sink);
    }

    HRESULT STDMETHODCALLTYPE ComputeArea(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* area) const noexcept override
    {
        return path_->ComputeArea(
            world_transform,
            flattening_tolerance,
            area);
    }

    HRESULT STDMETHODCALLTYPE ComputeLength(
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        FLOAT* length) const noexcept override
    {
        return path_->ComputeLength(
            world_transform,
            flattening_tolerance,
            length);
    }

    HRESULT STDMETHODCALLTYPE ComputePointAtLength(
        FLOAT length,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        D2D1_POINT_2F* point,
        D2D1_POINT_2F* unit_tangent_vector) const noexcept override
    {
        return path_->ComputePointAtLength(
            length,
            world_transform,
            flattening_tolerance,
            point,
            unit_tangent_vector);
    }

    HRESULT STDMETHODCALLTYPE Widen(
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style,
        const D2D1_MATRIX_3X2_F* world_transform,
        FLOAT flattening_tolerance,
        ID2D1SimplifiedGeometrySink* geometry_sink) const noexcept override
    {
        return path_->Widen(
            stroke_width,
            stroke_style,
            world_transform,
            flattening_tolerance,
            geometry_sink);
    }

    D2D1_FILL_MODE STDMETHODCALLTYPE GetFillMode() const noexcept override
    {
        return fill_mode_;
    }

    UINT32 STDMETHODCALLTYPE GetSourceGeometryCount() const noexcept override
    {
        return static_cast<UINT32>(sources_.size());
    }

    void STDMETHODCALLTYPE GetSourceGeometries(
        ID2D1Geometry** geometries,
        UINT32 geometries_count) const noexcept override
    {
        if (geometries == nullptr) {
            return;
        }
        const size_t count = std::min(
            static_cast<size_t>(geometries_count), sources_.size());
        for (size_t index = 0U; index < count; ++index) {
            geometries[index] = sources_[index].Get();
            geometries[index]->AddRef();
        }
    }

private:
    std::atomic<ULONG> reference_count_{1U};
    ComPtr<ID2D1Factory1> factory_;
    D2D1_FILL_MODE fill_mode_ = D2D1_FILL_MODE_ALTERNATE;
    std::vector<ComPtr<ID2D1Geometry>> sources_;
    ComPtr<ID2D1PathGeometry1> path_;
};

bool compat_geometry_source_chain_supported(
    ID2D1Geometry* geometry,
    uint32_t depth = 0U) noexcept
{
    if (geometry == nullptr || depth == 64U) {
        return false;
    }
    ComPtr<ID2D1GeometryGroup> group;
    if (SUCCEEDED(geometry->QueryInterface(IID_PPV_ARGS(&group)))) {
        return true;
    }
    ComPtr<ID2D1TransformedGeometry> transformed;
    if (SUCCEEDED(geometry->QueryInterface(IID_PPV_ARGS(&transformed)))) {
        ComPtr<ID2D1Geometry> source;
        transformed->GetSourceGeometry(&source);
        return compat_geometry_source_chain_supported(
            source.Get(), depth + 1U);
    }
    return true;
}

class CompatGroupGeometrySink final :
    public ID2D1SimplifiedGeometrySink {
public:
    explicit CompatGroupGeometrySink(
        ID2D1GeometrySink* target) noexcept
        : target_(target)
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(
                interface_id,
                __uuidof(ID2D1SimplifiedGeometrySink))) {
            *value = static_cast<ID2D1SimplifiedGeometrySink*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE SetFillMode(
        D2D1_FILL_MODE fill_mode) noexcept override
    {
        if (fill_mode != D2D1_FILL_MODE_ALTERNATE &&
            fill_mode != D2D1_FILL_MODE_WINDING) {
            fail(E_INVALIDARG);
        }
    }

    void STDMETHODCALLTYPE SetSegmentFlags(
        D2D1_PATH_SEGMENT flags) noexcept override
    {
        if (SUCCEEDED(failure_)) {
            target_->SetSegmentFlags(flags);
        }
    }

    void STDMETHODCALLTYPE BeginFigure(
        D2D1_POINT_2F start,
        D2D1_FIGURE_BEGIN begin) noexcept override
    {
        if (SUCCEEDED(failure_)) {
            target_->BeginFigure(start, begin);
        }
    }

    void STDMETHODCALLTYPE AddLines(
        const D2D1_POINT_2F* points,
        UINT32 points_count) noexcept override
    {
        if (SUCCEEDED(failure_)) {
            target_->AddLines(points, points_count);
        }
    }

    void STDMETHODCALLTYPE AddBeziers(
        const D2D1_BEZIER_SEGMENT* beziers,
        UINT32 beziers_count) noexcept override
    {
        if (SUCCEEDED(failure_)) {
            target_->AddBeziers(beziers, beziers_count);
        }
    }

    void STDMETHODCALLTYPE EndFigure(
        D2D1_FIGURE_END end) noexcept override
    {
        if (SUCCEEDED(failure_)) {
            target_->EndFigure(end);
        }
    }

    HRESULT STDMETHODCALLTYPE Close() noexcept override
    {
        fail(D2DERR_WRONG_STATE);
        return failure_;
    }

    HRESULT failure() const noexcept
    {
        return failure_;
    }

private:
    void fail(HRESULT value) noexcept
    {
        if (SUCCEEDED(failure_)) {
            failure_ = value;
        }
    }

    std::atomic<ULONG> reference_count_{1U};
    ComPtr<ID2D1GeometrySink> target_;
    HRESULT failure_ = S_OK;
};

class ProGpuD2DFactory final :
    public ID2D1Factory1,
    public ID2D1Multithread,
    public IProGpuD2DCompatFactoryNative,
    public direct2d_compat::scene_factory_native {
public:
    ProGpuD2DFactory() noexcept
    {
        D2D1_FACTORY_OPTIONS options{};
        system_effect_factory_result_ = D2D1CreateFactory(
            D2D1_FACTORY_TYPE_MULTI_THREADED,
            __uuidof(ID2D1Factory1),
            &options,
            reinterpret_cast<void**>(
                system_effect_factory_.ReleaseAndGetAddressOf()));
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Factory)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1Factory1))) {
            *value = static_cast<ID2D1Factory1*>(this);
        } else if (IsEqualIID(
                interface_id, __uuidof(ID2D1Multithread))) {
            *value = static_cast<ID2D1Multithread*>(this);
        } else if (IsEqualIID(
                interface_id,
                __uuidof(IProGpuD2DCompatFactoryNative))) {
            *value = static_cast<IProGpuD2DCompatFactoryNative*>(this);
        } else if (progpu::native::com::guid_equal(
                interface_id,
                direct2d_compat::scene_factory_native_interface_id)) {
            *value = static_cast<direct2d_compat::scene_factory_native*>(this);
        } else {
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE ReloadSystemMetrics() noexcept override
    {
        return S_OK;
    }

    void STDMETHODCALLTYPE GetDesktopDpi(
        FLOAT* dpi_x,
        FLOAT* dpi_y) noexcept override
    {
        if (dpi_x != nullptr) {
            *dpi_x = 96.0F;
        }
        if (dpi_y != nullptr) {
            *dpi_y = 96.0F;
        }
    }

    HRESULT STDMETHODCALLTYPE CreateRectangleGeometry(
        const D2D1_RECT_F* rectangle,
        ID2D1RectangleGeometry** rectangle_geometry) noexcept override
    {
        if (rectangle_geometry == nullptr) {
            return E_POINTER;
        }
        *rectangle_geometry = nullptr;
        if (!compat_finite_rectangle(rectangle)) {
            return E_INVALIDARG;
        }
        auto* geometry = new (std::nothrow) ProGpuD2DRectangleGeometry(
            this, *rectangle);
        if (geometry == nullptr) {
            return E_OUTOFMEMORY;
        }
        *rectangle_geometry = geometry;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE CreateRoundedRectangleGeometry(
        const D2D1_ROUNDED_RECT* rounded_rectangle,
        ID2D1RoundedRectangleGeometry** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (!compat_finite_rounded_rectangle(rounded_rectangle)) {
            return E_INVALIDARG;
        }

        progpu_native_direct2d_point_2f core_start{};
        std::array<progpu_native_direct2d_point_2f, 4U> core_line_ends{};
        std::array<direct2d_core::cubic_bezier_segment_f, 4U>
            core_corners{};
        HRESULT hr = direct2d_core::rounded_rectangle_to_path(
            compat_core_rounded_rectangle(*rounded_rectangle),
            &core_start,
            &core_line_ends,
            &core_corners);
        if (FAILED(hr)) {
            return hr;
        }
        const D2D1_POINT_2F start{core_start.x, core_start.y};
        std::array<D2D1_POINT_2F, 4U> line_ends{};
        std::array<D2D1_BEZIER_SEGMENT, 4U> corners{};
        for (std::size_t index = 0U; index < corners.size(); ++index) {
            line_ends[index] = {
                core_line_ends[index].x, core_line_ends[index].y};
            corners[index] = {
                {core_corners[index].point1.x,
                    core_corners[index].point1.y},
                {core_corners[index].point2.x,
                    core_corners[index].point2.y},
                {core_corners[index].point3.x,
                    core_corners[index].point3.y}};
        }

        ComPtr<ID2D1PathGeometry1> path;
        auto* raw_path = new (std::nothrow) ProGpuD2DPathGeometry(this);
        if (raw_path == nullptr) {
            return E_OUTOFMEMORY;
        }
        path.Attach(raw_path);
        ComPtr<ID2D1GeometrySink> sink;
        hr = path->Open(&sink);
        if (FAILED(hr)) {
            return hr;
        }
        sink->SetFillMode(D2D1_FILL_MODE_WINDING);
        sink->SetSegmentFlags(D2D1_PATH_SEGMENT_NONE);
        sink->BeginFigure(start, D2D1_FIGURE_BEGIN_FILLED);
        for (size_t index = 0U; index < corners.size(); ++index) {
            sink->AddLine(line_ends[index]);
            sink->AddBezier(corners[index]);
        }
        sink->EndFigure(D2D1_FIGURE_END_CLOSED);
        hr = sink->Close();
        if (FAILED(hr)) {
            return hr;
        }

        auto* geometry = new (std::nothrow)
            ProGpuD2DRoundedRectangleGeometry(
                this,
                *rounded_rectangle,
                path.Get());
        if (geometry == nullptr) {
            return E_OUTOFMEMORY;
        }
        *value = geometry;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE CreateEllipseGeometry(
        const D2D1_ELLIPSE* ellipse,
        ID2D1EllipseGeometry** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (!compat_finite_ellipse(ellipse)) {
            return E_INVALIDARG;
        }

        ComPtr<ID2D1PathGeometry1> path;
        auto* raw_path = new (std::nothrow) ProGpuD2DPathGeometry(this);
        if (raw_path == nullptr) {
            return E_OUTOFMEMORY;
        }
        path.Attach(raw_path);
        ComPtr<ID2D1GeometrySink> sink;
        HRESULT hr = path->Open(&sink);
        if (FAILED(hr)) {
            return hr;
        }

        progpu_native_direct2d_point_2f core_start{};
        std::array<direct2d_core::cubic_bezier_segment_f, 4U>
            core_segments{};
        hr = direct2d_core::ellipse_to_cubics(
            compat_core_ellipse(*ellipse), &core_start, &core_segments);
        if (FAILED(hr)) {
            static_cast<void>(sink->Close());
            return hr;
        }
        const D2D1_POINT_2F start{core_start.x, core_start.y};
        std::array<D2D1_BEZIER_SEGMENT, 4U> segments{};
        for (std::size_t index = 0U; index < segments.size(); ++index) {
            segments[index] = {
                {core_segments[index].point1.x,
                    core_segments[index].point1.y},
                {core_segments[index].point2.x,
                    core_segments[index].point2.y},
                {core_segments[index].point3.x,
                    core_segments[index].point3.y}};
        }
        sink->SetFillMode(D2D1_FILL_MODE_WINDING);
        sink->SetSegmentFlags(D2D1_PATH_SEGMENT_NONE);
        sink->BeginFigure(start, D2D1_FIGURE_BEGIN_FILLED);
        for (std::size_t index = 0U; index < segments.size(); ++index) {
            sink->AddBeziers(&segments[index], 1U);
            if (index + 1U < segments.size()) {
                sink->AddLine(segments[index].point3);
            }
        }
        sink->EndFigure(D2D1_FIGURE_END_CLOSED);
        hr = sink->Close();
        if (FAILED(hr)) {
            return hr;
        }

        auto* geometry = new (std::nothrow) ProGpuD2DEllipseGeometry(
            this,
            *ellipse,
            path.Get());
        if (geometry == nullptr) {
            return E_OUTOFMEMORY;
        }
        *value = geometry;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE CreateGeometryGroup(
        D2D1_FILL_MODE fill_mode,
        ID2D1Geometry** geometries,
        UINT32 geometries_count,
        ID2D1GeometryGroup** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if ((fill_mode != D2D1_FILL_MODE_ALTERNATE &&
                fill_mode != D2D1_FILL_MODE_WINDING) ||
            (geometries_count != 0U && geometries == nullptr)) {
            return E_INVALIDARG;
        }
        std::vector<ComPtr<ID2D1Geometry>> sources;
        try {
            sources.reserve(geometries_count);
            for (UINT32 index = 0U; index < geometries_count; ++index) {
                if (geometries[index] == nullptr) {
                    return E_INVALIDARG;
                }
                ComPtr<ID2D1Factory> source_factory;
                geometries[index]->GetFactory(&source_factory);
                if (source_factory.Get() !=
                    static_cast<ID2D1Factory*>(this)) {
                    return D2DERR_WRONG_FACTORY;
                }
                if (!compat_geometry_source_chain_supported(
                        geometries[index])) {
                    return E_NOTIMPL;
                }
                sources.emplace_back(geometries[index]);
            }
        } catch (const std::bad_alloc&) {
            return E_OUTOFMEMORY;
        } catch (...) {
            return E_FAIL;
        }

        ComPtr<ID2D1PathGeometry1> path;
        auto* raw_path = new (std::nothrow) ProGpuD2DPathGeometry(this);
        if (raw_path == nullptr) {
            return E_OUTOFMEMORY;
        }
        path.Attach(raw_path);
        ComPtr<ID2D1GeometrySink> sink;
        HRESULT hr = path->Open(&sink);
        if (FAILED(hr)) {
            return hr;
        }
        sink->SetFillMode(fill_mode);
        auto* raw_group_sink = new (std::nothrow)
            CompatGroupGeometrySink(sink.Get());
        if (raw_group_sink == nullptr) {
            static_cast<void>(sink->Close());
            return E_OUTOFMEMORY;
        }
        ComPtr<ID2D1SimplifiedGeometrySink> group_sink;
        group_sink.Attach(raw_group_sink);
        for (const auto& source : sources) {
            hr = source->Simplify(
                D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES,
                nullptr,
                D2D1_DEFAULT_FLATTENING_TOLERANCE,
                group_sink.Get());
            if (SUCCEEDED(hr)) {
                hr = raw_group_sink->failure();
            }
            if (FAILED(hr)) {
                static_cast<void>(sink->Close());
                return hr;
            }
        }
        hr = sink->Close();
        if (FAILED(hr)) {
            return hr;
        }

        auto* group = new (std::nothrow) ProGpuD2DGeometryGroup(
            this, fill_mode, std::move(sources), path.Get());
        if (group == nullptr) {
            return E_OUTOFMEMORY;
        }
        *value = group;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE CreateTransformedGeometry(
        ID2D1Geometry* source_geometry,
        const D2D1_MATRIX_3X2_F* transform,
        ID2D1TransformedGeometry** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (source_geometry == nullptr || transform == nullptr ||
            !compat_finite_transform(transform)) {
            return E_INVALIDARG;
        }
        ComPtr<ID2D1Factory> source_factory;
        source_geometry->GetFactory(&source_factory);
        if (source_factory.Get() != static_cast<ID2D1Factory*>(this)) {
            return D2DERR_WRONG_FACTORY;
        }
        auto* geometry = new (std::nothrow)
            ProGpuD2DTransformedGeometry(
                this, source_geometry, *transform);
        if (geometry == nullptr) {
            return E_OUTOFMEMORY;
        }
        *value = geometry;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE CreatePathGeometry(
        ID2D1PathGeometry** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        try {
            auto* geometry = new ProGpuD2DPathGeometry(this);
            *value = static_cast<ID2D1PathGeometry*>(geometry);
            return S_OK;
        } catch (const std::bad_alloc&) {
            return E_OUTOFMEMORY;
        } catch (...) {
            return E_FAIL;
        }
    }

    HRESULT STDMETHODCALLTYPE CreateStrokeStyle(
        const D2D1_STROKE_STYLE_PROPERTIES* properties,
        const FLOAT* dashes,
        UINT32 dash_count,
        ID2D1StrokeStyle** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (properties == nullptr) {
            return E_INVALIDARG;
        }
        const D2D1_STROKE_STYLE_PROPERTIES1 properties1 = {
            properties->startCap,
            properties->endCap,
            properties->dashCap,
            properties->lineJoin,
            properties->miterLimit,
            properties->dashStyle,
            properties->dashOffset,
            D2D1_STROKE_TRANSFORM_TYPE_NORMAL};
        if (!compat_valid_stroke_style(
                &properties1, dashes, dash_count)) {
            return E_INVALIDARG;
        }
        try {
            auto* stroke_style = new ProGpuD2DStrokeStyle(
                this, properties1, dashes, dash_count);
            *value = static_cast<ID2D1StrokeStyle*>(stroke_style);
            return S_OK;
        } catch (const std::bad_alloc&) {
            return E_OUTOFMEMORY;
        } catch (...) {
            return E_FAIL;
        }
    }

    HRESULT STDMETHODCALLTYPE CreateDrawingStateBlock(
        const D2D1_DRAWING_STATE_DESCRIPTION* description,
        IDWriteRenderingParams* text_rendering_parameters,
        ID2D1DrawingStateBlock** value) noexcept override
    {
        return direct2d_compat::detail::create_drawing_state_block(
            reinterpret_cast<direct2d_compat::factory*>(this),
            reinterpret_cast<
                const direct2d_compat::drawing_state_description*>(
                    description),
            reinterpret_cast<direct2d_compat::rendering_parameters*>(
                text_rendering_parameters),
            reinterpret_cast<direct2d_compat::drawing_state_block**>(value));
    }

    HRESULT STDMETHODCALLTYPE CreateWicBitmapRenderTarget(
        IWICBitmap*,
        const D2D1_RENDER_TARGET_PROPERTIES*,
        ID2D1RenderTarget** value) noexcept override
    {
        return unsupported(value);
    }

    HRESULT STDMETHODCALLTYPE CreateHwndRenderTarget(
        const D2D1_RENDER_TARGET_PROPERTIES*,
        const D2D1_HWND_RENDER_TARGET_PROPERTIES*,
        ID2D1HwndRenderTarget** value) noexcept override
    {
        return unsupported(value);
    }

    HRESULT STDMETHODCALLTYPE CreateDxgiSurfaceRenderTarget(
        IDXGISurface*,
        const D2D1_RENDER_TARGET_PROPERTIES*,
        ID2D1RenderTarget** value) noexcept override
    {
        return unsupported(value);
    }

    HRESULT STDMETHODCALLTYPE CreateDCRenderTarget(
        const D2D1_RENDER_TARGET_PROPERTIES*,
        ID2D1DCRenderTarget** value) noexcept override
    {
        return unsupported(value);
    }

    HRESULT STDMETHODCALLTYPE CreateDevice(
        IDXGIDevice*,
        ID2D1Device** value) noexcept override
    {
        return unsupported(value);
    }

    HRESULT STDMETHODCALLTYPE CreateStrokeStyle(
        const D2D1_STROKE_STYLE_PROPERTIES1* properties,
        const FLOAT* dashes,
        UINT32 dash_count,
        ID2D1StrokeStyle1** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (!compat_valid_stroke_style(properties, dashes, dash_count)) {
            return E_INVALIDARG;
        }
        try {
            auto* stroke_style = new ProGpuD2DStrokeStyle(
                this, *properties, dashes, dash_count);
            *value = stroke_style;
            return S_OK;
        } catch (const std::bad_alloc&) {
            return E_OUTOFMEMORY;
        } catch (...) {
            return E_FAIL;
        }
    }

    HRESULT STDMETHODCALLTYPE CreatePathGeometry(
        ID2D1PathGeometry1** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        try {
            auto* geometry = new ProGpuD2DPathGeometry(this);
            *value = static_cast<ID2D1PathGeometry1*>(geometry);
            return S_OK;
        } catch (const std::bad_alloc&) {
            return E_OUTOFMEMORY;
        } catch (...) {
            return E_FAIL;
        }
    }

    HRESULT STDMETHODCALLTYPE CreateDrawingStateBlock(
        const D2D1_DRAWING_STATE_DESCRIPTION1* description,
        IDWriteRenderingParams* text_rendering_parameters,
        ID2D1DrawingStateBlock1** value) noexcept override
    {
        return direct2d_compat::detail::create_drawing_state_block1(
            reinterpret_cast<direct2d_compat::factory*>(this),
            reinterpret_cast<
                const direct2d_compat::drawing_state_description1*>(
                    description),
            reinterpret_cast<direct2d_compat::rendering_parameters*>(
                text_rendering_parameters),
            reinterpret_cast<direct2d_compat::drawing_state_block1**>(value));
    }

    HRESULT STDMETHODCALLTYPE CreateGdiMetafile(
        IStream*,
        ID2D1GdiMetafile** value) noexcept override
    {
        return unsupported(value);
    }

    HRESULT STDMETHODCALLTYPE RegisterEffectFromStream(
        REFCLSID class_id,
        IStream* property_xml,
        const D2D1_PROPERTY_BINDING* bindings,
        UINT32 bindings_count,
        const PD2D1_EFFECT_FACTORY effect_factory) noexcept override
    {
        if (FAILED(system_effect_factory_result_)) {
            return system_effect_factory_result_;
        }
        return system_effect_factory_->RegisterEffectFromStream(
            class_id,
            property_xml,
            bindings,
            bindings_count,
            effect_factory);
    }

    HRESULT STDMETHODCALLTYPE RegisterEffectFromString(
        REFCLSID class_id,
        PCWSTR property_xml,
        const D2D1_PROPERTY_BINDING* bindings,
        UINT32 bindings_count,
        const PD2D1_EFFECT_FACTORY effect_factory) noexcept override
    {
        if (FAILED(system_effect_factory_result_)) {
            return system_effect_factory_result_;
        }
        return system_effect_factory_->RegisterEffectFromString(
            class_id,
            property_xml,
            bindings,
            bindings_count,
            effect_factory);
    }

    HRESULT STDMETHODCALLTYPE UnregisterEffect(
        REFCLSID class_id) noexcept override
    {
        if (FAILED(system_effect_factory_result_)) {
            return system_effect_factory_result_;
        }
        return system_effect_factory_->UnregisterEffect(class_id);
    }

    HRESULT STDMETHODCALLTYPE GetRegisteredEffects(
        CLSID* effects,
        UINT32 effects_count,
        UINT32* effects_returned,
        UINT32* effects_registered) const noexcept override
    {
        if (FAILED(system_effect_factory_result_)) {
            if (effects_returned != nullptr) {
                *effects_returned = 0U;
            }
            if (effects_registered != nullptr) {
                *effects_registered = 0U;
            }
            return system_effect_factory_result_;
        }
        return system_effect_factory_->GetRegisteredEffects(
            effects,
            effects_count,
            effects_returned,
            effects_registered);
    }

    HRESULT STDMETHODCALLTYPE GetEffectProperties(
        REFCLSID effect_id,
        ID2D1Properties** value) const noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (FAILED(system_effect_factory_result_)) {
            return system_effect_factory_result_;
        }
        return system_effect_factory_->GetEffectProperties(effect_id, value);
    }

    BOOL STDMETHODCALLTYPE GetMultithreadProtected() const noexcept override
    {
        return TRUE;
    }

    void STDMETHODCALLTYPE Enter() noexcept override
    {
        mutex_.lock();
    }

    void STDMETHODCALLTYPE Leave() noexcept override
    {
        mutex_.unlock();
    }

    HRESULT STDMETHODCALLTYPE CreateSolidColorBrush(
        const D2D1_COLOR_F* color,
        const D2D1_BRUSH_PROPERTIES* properties,
        ID2D1SolidColorBrush** brush) noexcept override
    {
        if (brush == nullptr) {
            return E_POINTER;
        }
        *brush = nullptr;
        if (color == nullptr || !std::isfinite(color->r) ||
            !std::isfinite(color->g) || !std::isfinite(color->b) ||
            !std::isfinite(color->a)) {
            return E_INVALIDARG;
        }
        D2D1_BRUSH_PROPERTIES actual_properties =
            D2D1::BrushProperties();
        if (properties != nullptr) {
            if (!std::isfinite(properties->opacity) ||
                properties->opacity < 0.0F ||
                properties->opacity > 1.0F ||
                !compat_finite_transform(&properties->transform)) {
                return E_INVALIDARG;
            }
            actual_properties = *properties;
        }
        auto* result = new (std::nothrow) ProGpuD2DSolidColorBrush(
            this,
            *color,
            actual_properties);
        if (result == nullptr) {
            return E_OUTOFMEMORY;
        }
        *brush = result;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE CreateSceneRenderTarget(
        const direct2d_compat::scene_render_target_properties* properties,
        direct2d_compat::render_target** value) noexcept override
    {
        return direct2d_compat::detail::create_scene_render_target(
            reinterpret_cast<direct2d_compat::factory*>(
                static_cast<ID2D1Factory1*>(this)),
            properties,
            value);
    }

private:
    template<typename T>
    static HRESULT unsupported(T** value) noexcept
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        return E_NOTIMPL;
    }

    std::atomic<ULONG> reference_count_{1U};
    std::recursive_mutex mutex_;
    ComPtr<ID2D1Factory1> system_effect_factory_;
    HRESULT system_effect_factory_result_ = E_FAIL;
};

class CommandStreamSummarySink final : public ID2D1CommandSink1 {
public:
    explicit CommandStreamSummarySink(bool require_supported_operations) noexcept
        : require_supported_operations_(require_supported_operations)
    {
        summary_.struct_size = static_cast<uint32_t>(sizeof(summary_));
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1CommandSink)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1CommandSink1))) {
            *value = static_cast<ID2D1CommandSink1*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE BeginDraw() noexcept override
    {
        if (begun_ || ended_) {
            return D2DERR_WRONG_STATE;
        }
        begun_ = true;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE EndDraw() noexcept override
    {
        if (!begun_ || ended_) {
            return D2DERR_WRONG_STATE;
        }
        ended_ = true;
        if (scope_depth_ != 0U) {
            return D2DERR_WRONG_STATE;
        }
        summary_.flags |=
            PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_BALANCED_SCOPES;
        if (overflow_) {
            return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
        }
        if (require_supported_operations_ &&
            summary_.unsupported_operation_count != 0U) {
            return E_NOTIMPL;
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE SetAntialiasMode(D2D1_ANTIALIAS_MODE) noexcept override
    {
        return record_state();
    }

    HRESULT STDMETHODCALLTYPE SetTags(D2D1_TAG, D2D1_TAG) noexcept override
    {
        return record_state();
    }

    HRESULT STDMETHODCALLTYPE SetTextAntialiasMode(
        D2D1_TEXT_ANTIALIAS_MODE) noexcept override
    {
        return record_state();
    }

    HRESULT STDMETHODCALLTYPE SetTextRenderingParams(
        IDWriteRenderingParams* text_rendering_params) noexcept override
    {
        HRESULT result = record_state();
        if (SUCCEEDED(result) && text_rendering_params != nullptr) {
            mark_unsupported(
                PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_HAS_TEXT_RENDERING_PARAMETERS);
        }
        return result;
    }

    HRESULT STDMETHODCALLTYPE SetTransform(
        const D2D1_MATRIX_3X2_F*) noexcept override
    {
        return record_state();
    }

    HRESULT STDMETHODCALLTYPE SetPrimitiveBlend(
        D2D1_PRIMITIVE_BLEND) noexcept override
    {
        return record_state();
    }

    HRESULT STDMETHODCALLTYPE SetPrimitiveBlend1(
        D2D1_PRIMITIVE_BLEND) noexcept override
    {
        return record_state();
    }

    HRESULT STDMETHODCALLTYPE SetUnitMode(D2D1_UNIT_MODE) noexcept override
    {
        return record_state();
    }

    HRESULT STDMETHODCALLTYPE Clear(const D2D1_COLOR_F*) noexcept override
    {
        return record_command(summary_.clear_count);
    }

    HRESULT STDMETHODCALLTYPE DrawGlyphRun(
        D2D1_POINT_2F,
        const DWRITE_GLYPH_RUN*,
        const DWRITE_GLYPH_RUN_DESCRIPTION*,
        ID2D1Brush*,
        DWRITE_MEASURING_MODE) noexcept override
    {
        HRESULT result = record_draw();
        if (SUCCEEDED(result)) {
            increment(summary_.text_draw_count);
        }
        return result;
    }

    HRESULT STDMETHODCALLTYPE DrawLine(
        D2D1_POINT_2F,
        D2D1_POINT_2F,
        ID2D1Brush*,
        FLOAT,
        ID2D1StrokeStyle*) noexcept override
    {
        return record_draw();
    }

    HRESULT STDMETHODCALLTYPE DrawGeometry(
        ID2D1Geometry*,
        ID2D1Brush*,
        FLOAT,
        ID2D1StrokeStyle*) noexcept override
    {
        return record_draw();
    }

    HRESULT STDMETHODCALLTYPE DrawRectangle(
        const D2D1_RECT_F*,
        ID2D1Brush*,
        FLOAT,
        ID2D1StrokeStyle*) noexcept override
    {
        return record_draw();
    }

    HRESULT STDMETHODCALLTYPE DrawBitmap(
        ID2D1Bitmap*,
        const D2D1_RECT_F*,
        FLOAT,
        D2D1_INTERPOLATION_MODE,
        const D2D1_RECT_F*,
        const D2D1_MATRIX_4X4_F*) noexcept override
    {
        HRESULT result = record_draw();
        if (SUCCEEDED(result)) {
            increment(summary_.image_draw_count);
        }
        return result;
    }

    HRESULT STDMETHODCALLTYPE DrawImage(
        ID2D1Image*,
        const D2D1_POINT_2F*,
        const D2D1_RECT_F*,
        D2D1_INTERPOLATION_MODE,
        D2D1_COMPOSITE_MODE) noexcept override
    {
        HRESULT result = record_draw();
        if (SUCCEEDED(result)) {
            increment(summary_.image_draw_count);
        }
        return result;
    }

    HRESULT STDMETHODCALLTYPE DrawGdiMetafile(
        ID2D1GdiMetafile*,
        const D2D1_POINT_2F*) noexcept override
    {
        HRESULT result = record_draw();
        if (SUCCEEDED(result)) {
            increment(summary_.image_draw_count);
            mark_unsupported(
                PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_HAS_GDI_METAFILE);
        }
        return result;
    }

    HRESULT STDMETHODCALLTYPE FillMesh(
        ID2D1Mesh*,
        ID2D1Brush*) noexcept override
    {
        HRESULT result = record_fill();
        if (SUCCEEDED(result)) {
            mark_unsupported(
                PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_HAS_MESH);
        }
        return result;
    }

    HRESULT STDMETHODCALLTYPE FillOpacityMask(
        ID2D1Bitmap*,
        ID2D1Brush*,
        const D2D1_RECT_F*,
        const D2D1_RECT_F*) noexcept override
    {
        HRESULT result = record_fill();
        if (SUCCEEDED(result)) {
            mark_unsupported(
                PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_HAS_OPACITY_MASK);
        }
        return result;
    }

    HRESULT STDMETHODCALLTYPE FillGeometry(
        ID2D1Geometry*,
        ID2D1Brush*,
        ID2D1Brush*) noexcept override
    {
        return record_fill();
    }

    HRESULT STDMETHODCALLTYPE FillRectangle(
        const D2D1_RECT_F*,
        ID2D1Brush*) noexcept override
    {
        return record_fill();
    }

    HRESULT STDMETHODCALLTYPE PushAxisAlignedClip(
        const D2D1_RECT_F*,
        D2D1_ANTIALIAS_MODE) noexcept override
    {
        HRESULT result = record_command(summary_.clip_push_count);
        if (FAILED(result)) {
            return result;
        }
        return push_scope(progpu_direct2d_draw_scope_kind::axis_aligned_clip);
    }

    HRESULT STDMETHODCALLTYPE PushLayer(
        const D2D1_LAYER_PARAMETERS1*,
        ID2D1Layer*) noexcept override
    {
        HRESULT result = record_command(summary_.layer_push_count);
        if (FAILED(result)) {
            return result;
        }
        return push_scope(progpu_direct2d_draw_scope_kind::layer);
    }

    HRESULT STDMETHODCALLTYPE PopAxisAlignedClip() noexcept override
    {
        HRESULT result = pop_scope(
            progpu_direct2d_draw_scope_kind::axis_aligned_clip);
        if (FAILED(result)) {
            return result;
        }
        return record_command(summary_.clip_pop_count);
    }

    HRESULT STDMETHODCALLTYPE PopLayer() noexcept override
    {
        HRESULT result = pop_scope(progpu_direct2d_draw_scope_kind::layer);
        if (FAILED(result)) {
            return result;
        }
        return record_command(summary_.layer_pop_count);
    }

    const progpu_native_direct2d_command_stream_summary& summary() const noexcept
    {
        return summary_;
    }

private:
    bool can_record() const noexcept
    {
        return begun_ && !ended_;
    }

    void increment(uint32_t& value) noexcept
    {
        if (value == std::numeric_limits<uint32_t>::max()) {
            overflow_ = true;
            return;
        }
        ++value;
    }

    HRESULT record_command(uint32_t& category) noexcept
    {
        if (!can_record()) {
            return D2DERR_WRONG_STATE;
        }
        increment(summary_.total_command_count);
        increment(category);
        return overflow_
            ? HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW)
            : S_OK;
    }

    HRESULT record_state() noexcept
    {
        return record_command(summary_.state_change_count);
    }

    HRESULT record_draw() noexcept
    {
        return record_command(summary_.draw_count);
    }

    HRESULT record_fill() noexcept
    {
        return record_command(summary_.fill_count);
    }

    void mark_unsupported(uint32_t flag) noexcept
    {
        summary_.flags |=
            PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_FLAG_HAS_UNSUPPORTED_OPERATIONS |
            flag;
        increment(summary_.unsupported_operation_count);
    }

    HRESULT push_scope(progpu_direct2d_draw_scope_kind kind) noexcept
    {
        if (scope_depth_ == scopes_.size()) {
            return E_BOUNDS;
        }
        scopes_[scope_depth_] = kind;
        ++scope_depth_;
        if (scope_depth_ > summary_.max_scope_depth) {
            summary_.max_scope_depth = scope_depth_;
        }
        return S_OK;
    }

    HRESULT pop_scope(progpu_direct2d_draw_scope_kind kind) noexcept
    {
        if (scope_depth_ == 0U || scopes_[scope_depth_ - 1U] != kind) {
            return D2DERR_WRONG_STATE;
        }
        --scope_depth_;
        return S_OK;
    }

    std::atomic<ULONG> reference_count_{1U};
    progpu_native_direct2d_command_stream_summary summary_{};
    std::array<
        progpu_direct2d_draw_scope_kind,
        progpu_direct2d_max_draw_scope_depth> scopes_{};
    uint32_t scope_depth_ = 0U;
    bool require_supported_operations_ = false;
    bool begun_ = false;
    bool ended_ = false;
    bool overflow_ = false;
};

class CommandScenePathSink final : public ID2D1SimplifiedGeometrySink {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(
                interface_id,
                __uuidof(ID2D1SimplifiedGeometrySink))) {
            *value = static_cast<ID2D1SimplifiedGeometrySink*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE SetFillMode(D2D1_FILL_MODE fill_mode) noexcept override
    {
        if (closed_ || figure_started_ ||
            (fill_mode != D2D1_FILL_MODE_ALTERNATE &&
                fill_mode != D2D1_FILL_MODE_WINDING)) {
            set_failure(E_INVALIDARG);
            return;
        }
        fill_mode_ = fill_mode;
    }

    void STDMETHODCALLTYPE SetSegmentFlags(
        D2D1_PATH_SEGMENT vertex_flags) noexcept override
    {
        constexpr uint32_t supported_flags =
            D2D1_PATH_SEGMENT_FORCE_UNSTROKED |
            D2D1_PATH_SEGMENT_FORCE_ROUND_LINE_JOIN;
        if (closed_ ||
            (static_cast<uint32_t>(vertex_flags) & ~supported_flags) != 0U) {
            set_failure(E_INVALIDARG);
        }
    }

    void STDMETHODCALLTYPE BeginFigure(
        D2D1_POINT_2F start_point,
        D2D1_FIGURE_BEGIN figure_begin) noexcept override
    {
        if (closed_ || figure_open_ || !finite(start_point) ||
            (figure_begin != D2D1_FIGURE_BEGIN_FILLED &&
                figure_begin != D2D1_FIGURE_BEGIN_HOLLOW)) {
            set_failure(E_INVALIDARG);
            return;
        }
        figure_started_ = true;
        figure_open_ = true;
        figure_filled_ = figure_begin == D2D1_FIGURE_BEGIN_FILLED;
        figure_start_ = start_point;
        current_point_ = start_point;
    }

    void STDMETHODCALLTYPE AddLines(
        const D2D1_POINT_2F* points,
        UINT32 point_count) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (point_count != 0U && points == nullptr)) {
            set_failure(E_INVALIDARG);
            return;
        }
        for (UINT32 index = 0U; index < point_count; ++index) {
            if (!finite(points[index])) {
                set_failure(E_INVALIDARG);
                return;
            }
            if (figure_filled_ && !append_line(current_point_, points[index])) {
                return;
            }
            current_point_ = points[index];
        }
    }

    void STDMETHODCALLTYPE AddBeziers(
        const D2D1_BEZIER_SEGMENT* beziers,
        UINT32 bezier_count) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (bezier_count != 0U && beziers == nullptr)) {
            set_failure(E_INVALIDARG);
            return;
        }
        for (UINT32 index = 0U; index < bezier_count; ++index) {
            const auto& bezier = beziers[index];
            if (!finite(bezier.point1) || !finite(bezier.point2) ||
                !finite(bezier.point3)) {
                set_failure(E_INVALIDARG);
                return;
            }
            if (figure_filled_ && !append_cubic(current_point_, bezier)) {
                return;
            }
            current_point_ = bezier.point3;
        }
    }

    void STDMETHODCALLTYPE EndFigure(D2D1_FIGURE_END figure_end) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (figure_end != D2D1_FIGURE_END_OPEN &&
                figure_end != D2D1_FIGURE_END_CLOSED)) {
            set_failure(E_INVALIDARG);
            return;
        }
        if (figure_filled_ && figure_end == D2D1_FIGURE_END_CLOSED &&
            !same_point(current_point_, figure_start_) &&
            !append_line(current_point_, figure_start_)) {
            return;
        }
        figure_open_ = false;
        figure_filled_ = false;
    }

    HRESULT STDMETHODCALLTYPE Close() noexcept override
    {
        if (closed_ || figure_open_) {
            set_failure(D2DERR_WRONG_STATE);
        }
        closed_ = true;
        return failure_;
    }

    std::span<const progpu_native_path_segment> segments() const noexcept
    {
        return segments_;
    }

    uint32_t fill_rule() const noexcept
    {
        return fill_mode_ == D2D1_FILL_MODE_ALTERNATE
            ? PROGPU_NATIVE_FILL_RULE_EVEN_ODD
            : PROGPU_NATIVE_FILL_RULE_NON_ZERO;
    }

    bool capacity_exceeded() const noexcept
    {
        return capacity_exceeded_;
    }

private:
    static constexpr size_t maximum_segment_count = 1U << 20U;

    static bool finite(D2D1_POINT_2F point) noexcept
    {
        return std::isfinite(point.x) && std::isfinite(point.y);
    }

    static bool same_point(
        D2D1_POINT_2F left,
        D2D1_POINT_2F right) noexcept
    {
        return left.x == right.x && left.y == right.y;
    }

    void set_failure(HRESULT value) noexcept
    {
        if (SUCCEEDED(failure_)) {
            failure_ = value;
        }
    }

    bool append(progpu_native_path_segment segment) noexcept
    {
        if (segments_.size() == maximum_segment_count) {
            capacity_exceeded_ = true;
            set_failure(HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW));
            return false;
        }
        try {
            segments_.push_back(segment);
            return true;
        } catch (const std::bad_alloc&) {
            set_failure(E_OUTOFMEMORY);
            return false;
        } catch (...) {
            set_failure(E_FAIL);
            return false;
        }
    }

    bool append_line(D2D1_POINT_2F start, D2D1_POINT_2F end) noexcept
    {
        progpu_native_path_segment segment{};
        segment.p0 = {start.x, start.y};
        segment.p1 = {end.x, end.y};
        segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
        return append(segment);
    }

    bool append_cubic(
        D2D1_POINT_2F start,
        const D2D1_BEZIER_SEGMENT& bezier) noexcept
    {
        progpu_native_path_segment segment{};
        segment.p0 = {start.x, start.y};
        segment.p1 = {bezier.point1.x, bezier.point1.y};
        segment.p2 = {bezier.point2.x, bezier.point2.y};
        segment.p3 = {bezier.point3.x, bezier.point3.y};
        segment.kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
        return append(segment);
    }

    std::atomic<ULONG> reference_count_{1U};
    std::vector<progpu_native_path_segment> segments_;
    D2D1_POINT_2F figure_start_{};
    D2D1_POINT_2F current_point_{};
    D2D1_FILL_MODE fill_mode_ = D2D1_FILL_MODE_ALTERNATE;
    HRESULT failure_ = S_OK;
    bool figure_open_ = false;
    bool figure_started_ = false;
    bool figure_filled_ = false;
    bool closed_ = false;
    bool capacity_exceeded_ = false;
};

struct command_scene_stroke_figure {
    size_t segment_offset = 0U;
    size_t segment_count = 0U;
    D2D1_POINT_2F start{};
    D2D1_PATH_SEGMENT closing_flags = D2D1_PATH_SEGMENT_NONE;
    bool closed = false;
};

class CommandSceneStrokeSink final : public ID2D1SimplifiedGeometrySink {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(
                interface_id, __uuidof(ID2D1SimplifiedGeometrySink))) {
            *value = static_cast<ID2D1SimplifiedGeometrySink*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE SetFillMode(D2D1_FILL_MODE fill_mode) noexcept override
    {
        if (closed_ || figure_open_ ||
            (fill_mode != D2D1_FILL_MODE_ALTERNATE &&
                fill_mode != D2D1_FILL_MODE_WINDING)) {
            set_failure(E_INVALIDARG);
        }
    }

    void STDMETHODCALLTYPE SetSegmentFlags(
        D2D1_PATH_SEGMENT vertex_flags) noexcept override
    {
        constexpr uint32_t supported_flags =
            D2D1_PATH_SEGMENT_FORCE_UNSTROKED |
            D2D1_PATH_SEGMENT_FORCE_ROUND_LINE_JOIN;
        const uint32_t flags = static_cast<uint32_t>(vertex_flags);
        if (closed_ || (flags & ~supported_flags) != 0U) {
            set_failure(E_INVALIDARG);
        } else {
            current_flags_ = vertex_flags;
        }
    }

    void STDMETHODCALLTYPE BeginFigure(
        D2D1_POINT_2F start_point,
        D2D1_FIGURE_BEGIN figure_begin) noexcept override
    {
        if (closed_ || figure_open_ || !finite(start_point) ||
            (figure_begin != D2D1_FIGURE_BEGIN_FILLED &&
                figure_begin != D2D1_FIGURE_BEGIN_HOLLOW)) {
            set_failure(E_INVALIDARG);
            return;
        }
        try {
            if (figures_.size() == maximum_figure_count ||
                segments_.size() == maximum_segment_count) {
                capacity_exceeded_ = true;
                set_failure(HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW));
                return;
            }
            current_figure_ = {};
            current_figure_.segment_offset = segments_.size();
            current_figure_.start = start_point;
            current_point_ = start_point;
            figure_open_ = true;
        } catch (const std::bad_alloc&) {
            set_failure(E_OUTOFMEMORY);
        } catch (...) {
            set_failure(E_FAIL);
        }
    }

    void STDMETHODCALLTYPE AddLines(
        const D2D1_POINT_2F* points,
        UINT32 point_count) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (point_count != 0U && points == nullptr)) {
            set_failure(E_INVALIDARG);
            return;
        }
        if (static_cast<uint64_t>(segments_.size()) + point_count >
            maximum_segment_count) {
            capacity_exceeded_ = true;
            set_failure(HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW));
            return;
        }
        for (UINT32 index = 0U; index < point_count; ++index) {
            if (!finite(points[index])) {
                set_failure(E_INVALIDARG);
                return;
            }
        }
        try {
            segments_.reserve(segments_.size() + point_count);
            segment_flags_.reserve(segment_flags_.size() + point_count);
            for (UINT32 index = 0U; index < point_count; ++index) {
                progpu_native_path_segment segment{};
                segment.p0 = {current_point_.x, current_point_.y};
                segment.p1 = {points[index].x, points[index].y};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                segments_.push_back(segment);
                segment_flags_.push_back(current_flags_);
                current_point_ = points[index];
            }
        } catch (const std::bad_alloc&) {
            set_failure(E_OUTOFMEMORY);
        } catch (...) {
            set_failure(E_FAIL);
        }
    }

    void STDMETHODCALLTYPE AddBeziers(
        const D2D1_BEZIER_SEGMENT* beziers,
        UINT32 bezier_count) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (bezier_count != 0U && beziers == nullptr)) {
            set_failure(E_INVALIDARG);
            return;
        }
        if (static_cast<uint64_t>(segments_.size()) + bezier_count >
            maximum_segment_count) {
            capacity_exceeded_ = true;
            set_failure(HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW));
            return;
        }
        for (UINT32 index = 0U; index < bezier_count; ++index) {
            if (!finite(beziers[index].point1) ||
                !finite(beziers[index].point2) ||
                !finite(beziers[index].point3)) {
                set_failure(E_INVALIDARG);
                return;
            }
        }
        try {
            segments_.reserve(segments_.size() + bezier_count);
            segment_flags_.reserve(segment_flags_.size() + bezier_count);
            for (UINT32 index = 0U; index < bezier_count; ++index) {
                progpu_native_path_segment segment{};
                segment.p0 = {current_point_.x, current_point_.y};
                segment.p1 = {
                    beziers[index].point1.x,
                    beziers[index].point1.y};
                segment.p2 = {
                    beziers[index].point2.x,
                    beziers[index].point2.y};
                segment.p3 = {
                    beziers[index].point3.x,
                    beziers[index].point3.y};
                segment.kind = PROGPU_NATIVE_PATH_SEGMENT_CUBIC;
                segments_.push_back(segment);
                segment_flags_.push_back(current_flags_);
                current_point_ = beziers[index].point3;
            }
        } catch (const std::bad_alloc&) {
            set_failure(E_OUTOFMEMORY);
        } catch (...) {
            set_failure(E_FAIL);
        }
    }

    void STDMETHODCALLTYPE EndFigure(
        D2D1_FIGURE_END figure_end) noexcept override
    {
        if (closed_ || !figure_open_ ||
            (figure_end != D2D1_FIGURE_END_OPEN &&
                figure_end != D2D1_FIGURE_END_CLOSED)) {
            set_failure(E_INVALIDARG);
            return;
        }
        current_figure_.segment_count =
            segments_.size() - current_figure_.segment_offset;
        current_figure_.closing_flags = current_flags_;
        current_figure_.closed =
            figure_end == D2D1_FIGURE_END_CLOSED;
        try {
            figures_.push_back(current_figure_);
        } catch (const std::bad_alloc&) {
            set_failure(E_OUTOFMEMORY);
        } catch (...) {
            set_failure(E_FAIL);
        }
        figure_open_ = false;
    }

    HRESULT STDMETHODCALLTYPE Close() noexcept override
    {
        if (closed_ || figure_open_) {
            set_failure(D2DERR_WRONG_STATE);
        }
        closed_ = true;
        return failure_;
    }

    std::span<const command_scene_stroke_figure> figures() const noexcept
    {
        return figures_;
    }

    std::span<const progpu_native_path_segment> segments() const noexcept
    {
        return segments_;
    }

    std::span<const D2D1_PATH_SEGMENT> segment_flags() const noexcept
    {
        return segment_flags_;
    }

    bool capacity_exceeded() const noexcept
    {
        return capacity_exceeded_;
    }

private:
    static constexpr uint64_t maximum_figure_count = 1U << 20U;
    static constexpr uint64_t maximum_segment_count = 1U << 24U;

    static bool finite(D2D1_POINT_2F point) noexcept
    {
        return std::isfinite(point.x) && std::isfinite(point.y);
    }

    void set_failure(HRESULT value) noexcept
    {
        if (SUCCEEDED(failure_)) {
            failure_ = value;
        }
    }

    std::atomic<ULONG> reference_count_{1U};
    std::vector<command_scene_stroke_figure> figures_;
    std::vector<progpu_native_path_segment> segments_;
    std::vector<D2D1_PATH_SEGMENT> segment_flags_;
    command_scene_stroke_figure current_figure_{};
    D2D1_POINT_2F current_point_{};
    D2D1_PATH_SEGMENT current_flags_ = D2D1_PATH_SEGMENT_NONE;
    HRESULT failure_ = S_OK;
    bool figure_open_ = false;
    bool closed_ = false;
    bool capacity_exceeded_ = false;
};

class CommandSceneStreamSink final : public ID2D1CommandSink1 {
public:
    CommandSceneStreamSink(
        uint64_t scene_id,
        uint64_t generation,
        const progpu_native_direct2d_command_stream_summary& summary)
        : builder_(scene_id, generation)
    {
        const uint64_t draw_count =
            static_cast<uint64_t>(summary.draw_count) + summary.fill_count;
        const uint64_t command_count = draw_count +
            summary.clip_push_count + summary.clip_pop_count;
        const uint64_t resource_count = draw_count +
            summary.clip_push_count + 1U;
        const uint64_t arena_bytes = draw_count * 192U +
            static_cast<uint64_t>(summary.clip_push_count) *
                sizeof(progpu_native_scene_state);
        if (command_count > std::numeric_limits<uint32_t>::max() ||
            resource_count > std::numeric_limits<uint32_t>::max() ||
            !builder_.reserve(
                static_cast<uint32_t>(command_count),
                static_cast<uint32_t>(resource_count),
                arena_bytes)) {
            builder_ready_ = false;
        } else {
            brush_cache_.reserve(static_cast<size_t>(draw_count));
        }
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) noexcept override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1CommandSink)) ||
            IsEqualIID(interface_id, __uuidof(ID2D1CommandSink1))) {
            *value = static_cast<ID2D1CommandSink1*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() noexcept override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() noexcept override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    HRESULT STDMETHODCALLTYPE BeginDraw() noexcept override
    {
        if (begun_ || ended_) {
            return fail(
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_DRAWING_STATE,
                D2DERR_WRONG_STATE,
                false);
        }
        begun_ = true;
        if (!builder_ready_) {
            return fail_builder(false);
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE EndDraw() noexcept override
    {
        if (!begun_ || ended_) {
            return fail(
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_DRAWING_STATE,
                D2DERR_WRONG_STATE,
                false);
        }
        ended_ = true;
        if (scope_depth_ != 0U) {
            return fail(
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_DRAWING_STATE,
                D2DERR_WRONG_STATE,
                false);
        }
        return failure_reason_ ==
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_NONE
            ? S_OK
            : E_NOTIMPL;
    }

    HRESULT STDMETHODCALLTYPE SetAntialiasMode(
        D2D1_ANTIALIAS_MODE mode) noexcept override
    {
        begin_callback();
        if (mode != D2D1_ANTIALIAS_MODE_PER_PRIMITIVE &&
            mode != D2D1_ANTIALIAS_MODE_ALIASED) {
            return fail_invalid_value();
        }
        antialias_mode_ = mode;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE SetTags(D2D1_TAG, D2D1_TAG) noexcept override
    {
        begin_callback();
        return can_record() ? S_OK : fail_drawing_state();
    }

    HRESULT STDMETHODCALLTYPE SetTextAntialiasMode(
        D2D1_TEXT_ANTIALIAS_MODE mode) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        if (mode > D2D1_TEXT_ANTIALIAS_MODE_ALIASED) {
            return fail_invalid_value();
        }
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE SetTextRenderingParams(
        IDWriteRenderingParams* parameters) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        return parameters == nullptr
            ? S_OK
            : fail_unsupported_state();
    }

    HRESULT STDMETHODCALLTYPE SetTransform(
        const D2D1_MATRIX_3X2_F* transform) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        if (transform == nullptr || !finite_transform(*transform)) {
            return fail_invalid_value();
        }
        transform_ = *transform;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE SetPrimitiveBlend(
        D2D1_PRIMITIVE_BLEND blend) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        return blend == D2D1_PRIMITIVE_BLEND_SOURCE_OVER
            ? S_OK
            : fail_unsupported_state();
    }

    HRESULT STDMETHODCALLTYPE SetPrimitiveBlend1(
        D2D1_PRIMITIVE_BLEND blend) noexcept override
    {
        return SetPrimitiveBlend(blend);
    }

    HRESULT STDMETHODCALLTYPE SetUnitMode(
        D2D1_UNIT_MODE mode) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        return mode == D2D1_UNIT_MODE_DIPS
            ? S_OK
            : fail_unsupported_state();
    }

    HRESULT STDMETHODCALLTYPE Clear(const D2D1_COLOR_F* color) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        if (has_clear_ || translated_draw_count_ != 0U || scope_depth_ != 0U) {
            return fail_unsupported_operation();
        }
        const D2D1_COLOR_F value = color == nullptr
            ? D2D1_COLOR_F{0.0F, 0.0F, 0.0F, 0.0F}
            : *color;
        if (!finite_color(value)) {
            return fail_invalid_value();
        }
        clear_color_ = value;
        has_clear_ = true;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE DrawGlyphRun(
        D2D1_POINT_2F,
        const DWRITE_GLYPH_RUN*,
        const DWRITE_GLYPH_RUN_DESCRIPTION*,
        ID2D1Brush*,
        DWRITE_MEASURING_MODE) noexcept override
    {
        return unsupported_resource_callback();
    }

    HRESULT STDMETHODCALLTYPE DrawLine(
        D2D1_POINT_2F point0,
        D2D1_POINT_2F point1,
        ID2D1Brush* brush,
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        if (stroke_style != nullptr) {
            return fail_unsupported_resource();
        }
        if (!finite_point(point0) || !finite_point(point1) ||
            !std::isfinite(stroke_width) || stroke_width <= 0.0F) {
            return fail_invalid_value();
        }
        uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        HRESULT hr = add_brush(brush, brush_index);
        if (FAILED(hr)) {
            return hr;
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
        const auto bounds = transformed_bounds(
            std::min(point0.x, point1.x) - radius,
            std::min(point0.y, point1.y) - radius,
            std::max(point0.x, point1.x) + radius,
            std::max(point0.y, point1.y) + radius);
        if (!builder_.draw_geometry(
                std::span<const progpu_native_geometry_primitive>(
                    &primitive,
                    1U),
                std::span<const uint32_t>(&brush_index, 1U),
                bounds)) {
            return fail_builder();
        }
        record_draw();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE DrawGeometry(
        ID2D1Geometry* geometry,
        ID2D1Brush* brush,
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        if (geometry == nullptr || brush == nullptr ||
            !std::isfinite(stroke_width) || stroke_width < 0.0F) {
            return fail_invalid_value();
        }
        if (antialias_mode_ != D2D1_ANTIALIAS_MODE_PER_PRIMITIVE) {
            return fail_unsupported_state();
        }
        return draw_stroked_geometry(
            geometry,
            brush,
            stroke_width,
            stroke_style);
    }

    HRESULT STDMETHODCALLTYPE DrawRectangle(
        const D2D1_RECT_F* rectangle,
        ID2D1Brush* brush,
        FLOAT stroke_width,
        ID2D1StrokeStyle* stroke_style) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        if (stroke_style != nullptr) {
            return fail_unsupported_resource();
        }
        if (!finite_rectangle(rectangle) || !std::isfinite(stroke_width) ||
            stroke_width <= 0.0F) {
            return fail_invalid_value();
        }
        return draw_rectangle(*rectangle, brush, stroke_width);
    }

    HRESULT STDMETHODCALLTYPE DrawBitmap(
        ID2D1Bitmap*,
        const D2D1_RECT_F*,
        FLOAT,
        D2D1_INTERPOLATION_MODE,
        const D2D1_RECT_F*,
        const D2D1_MATRIX_4X4_F*) noexcept override
    {
        return unsupported_resource_callback();
    }

    HRESULT STDMETHODCALLTYPE DrawImage(
        ID2D1Image*,
        const D2D1_POINT_2F*,
        const D2D1_RECT_F*,
        D2D1_INTERPOLATION_MODE,
        D2D1_COMPOSITE_MODE) noexcept override
    {
        return unsupported_resource_callback();
    }

    HRESULT STDMETHODCALLTYPE DrawGdiMetafile(
        ID2D1GdiMetafile*,
        const D2D1_POINT_2F*) noexcept override
    {
        return unsupported_resource_callback();
    }

    HRESULT STDMETHODCALLTYPE FillMesh(
        ID2D1Mesh*,
        ID2D1Brush*) noexcept override
    {
        return unsupported_resource_callback();
    }

    HRESULT STDMETHODCALLTYPE FillOpacityMask(
        ID2D1Bitmap*,
        ID2D1Brush*,
        const D2D1_RECT_F*,
        const D2D1_RECT_F*) noexcept override
    {
        return unsupported_resource_callback();
    }

    HRESULT STDMETHODCALLTYPE FillGeometry(
        ID2D1Geometry* geometry,
        ID2D1Brush* brush,
        ID2D1Brush* opacity_brush) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        if (geometry == nullptr || brush == nullptr) {
            return fail_invalid_value();
        }
        if (opacity_brush != nullptr) {
            return fail_unsupported_resource();
        }
        if (antialias_mode_ != D2D1_ANTIALIAS_MODE_PER_PRIMITIVE) {
            return fail_unsupported_state();
        }
        return draw_filled_geometry(geometry, brush);
    }

    HRESULT STDMETHODCALLTYPE FillRectangle(
        const D2D1_RECT_F* rectangle,
        ID2D1Brush* brush) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        if (!finite_rectangle(rectangle)) {
            return fail_invalid_value();
        }
        return draw_rectangle(*rectangle, brush, 0.0F);
    }

    HRESULT STDMETHODCALLTYPE PushAxisAlignedClip(
        const D2D1_RECT_F* rectangle,
        D2D1_ANTIALIAS_MODE antialias_mode) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        if (!finite_rectangle(rectangle)) {
            return fail_invalid_value();
        }
        if (antialias_mode != D2D1_ANTIALIAS_MODE_ALIASED) {
            return fail_unsupported_state();
        }
        if (clip_depth_ == clip_stack_.size() ||
            scope_depth_ == scope_stack_.size()) {
            return fail_capacity_exceeded();
        }
        progpu_native_image_rect clip = transformed_bounds(
            rectangle->left,
            rectangle->top,
            rectangle->right,
            rectangle->bottom);
        if (!finite_native_rectangle(clip)) {
            return fail_invalid_value();
        }
        if (clip_depth_ != 0U) {
            clip = intersect_rectangles(clip_stack_[clip_depth_ - 1U], clip);
            if (!finite_native_rectangle(clip)) {
                return fail_invalid_value();
            }
        }
        progpu_native_scene_state state =
            progpu::native::semantic_scene_builder::identity_state();
        state.flags = PROGPU_NATIVE_SCENE_STATE_CLIP_RECT;
        state.clip_rect = clip;
        uint32_t state_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (!builder_.add_state(state, state_index) ||
            !builder_.save(state_index)) {
            return fail_builder();
        }
        clip_stack_[clip_depth_] = clip;
        ++clip_depth_;
        scope_stack_[scope_depth_] = scope_axis_aligned_clip;
        ++scope_depth_;
        has_axis_aligned_clips_ = true;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE PushLayer(
        const D2D1_LAYER_PARAMETERS1* parameters,
        ID2D1Layer*) noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        if (parameters == nullptr ||
            !finite_rectangle(&parameters->contentBounds) ||
            !finite_transform(parameters->maskTransform) ||
            !std::isfinite(parameters->opacity) ||
            parameters->opacity < 0.0F || parameters->opacity > 1.0F) {
            return fail_invalid_value();
        }
        const bool full_target = infinite_rectangle(parameters->contentBounds);
        if (!full_target && !axis_preserving_transform(transform_)) {
            return fail_unsupported_state();
        }
        if (full_target && parameters->opacityBrush != nullptr) {
            return fail_unsupported_state();
        }
        if (parameters->maskAntialiasMode !=
                D2D1_ANTIALIAS_MODE_PER_PRIMITIVE &&
            parameters->maskAntialiasMode != D2D1_ANTIALIAS_MODE_ALIASED) {
            return fail_invalid_value();
        }
        if (parameters->layerOptions != D2D1_LAYER_OPTIONS1_NONE) {
            return fail_unsupported_state();
        }
        if (parameters->geometricMask != nullptr &&
            parameters->maskAntialiasMode !=
                D2D1_ANTIALIAS_MODE_PER_PRIMITIVE) {
            return fail_unsupported_state();
        }
        if (scope_depth_ == scope_stack_.size()) {
            return fail_capacity_exceeded();
        }
        progpu_native_image_rect bounds = full_target
            ? progpu_native_image_rect{}
            : transformed_bounds(
                parameters->contentBounds.left,
                parameters->contentBounds.top,
                parameters->contentBounds.right,
                parameters->contentBounds.bottom);
        if (!finite_native_rectangle(bounds)) {
            return fail_invalid_value();
        }
        uint32_t mask_resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (parameters->geometricMask != nullptr) {
            progpu_native_image_rect mask_bounds{};
            bool empty_mask = false;
            const HRESULT mask_hr = add_geometric_layer_mask(
                parameters->geometricMask,
                parameters->maskTransform,
                parameters->opacityBrush,
                parameters->contentBounds,
                mask_resource_index,
                mask_bounds,
                empty_mask);
            if (FAILED(mask_hr)) {
                return mask_hr;
            }
            if (empty_mask) {
                bounds = {};
            } else {
                bounds = full_target
                    ? mask_bounds
                    : intersect_rectangles(bounds, mask_bounds);
            }
        } else if (parameters->opacityBrush != nullptr) {
            bool empty_mask = false;
            const HRESULT mask_hr = add_opacity_brush_layer_mask(
                parameters->opacityBrush,
                parameters->contentBounds,
                mask_resource_index,
                empty_mask);
            if (FAILED(mask_hr)) {
                return mask_hr;
            }
            if (empty_mask) {
                bounds = {};
            }
        }
        const bool has_bounds = !full_target ||
            parameters->geometricMask != nullptr;
        const progpu_native_scene_layer layer{
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
        if (!builder_.push_layer(layer)) {
            return fail_builder();
        }
        scope_stack_[scope_depth_] = scope_opacity_layer;
        ++scope_depth_;
        has_opacity_layers_ = true;
        has_geometric_layer_masks_ |= parameters->geometricMask != nullptr;
        has_opacity_brush_layer_masks_ |=
            parameters->opacityBrush != nullptr;
        has_composite_layer_masks_ |= parameters->geometricMask != nullptr &&
            parameters->opacityBrush != nullptr;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE PopAxisAlignedClip() noexcept override
    {
        begin_callback();
        if (!can_record()) {
            return fail_drawing_state();
        }
        if (clip_depth_ == 0U || scope_depth_ == 0U ||
            scope_stack_[scope_depth_ - 1U] != scope_axis_aligned_clip) {
            return fail_drawing_state();
        }
        if (!builder_.restore()) {
            return fail_builder();
        }
        --clip_depth_;
        clip_stack_[clip_depth_] = {};
        --scope_depth_;
        scope_stack_[scope_depth_] = scope_none;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE PopLayer() noexcept override
    {
        begin_callback();
        if (!can_record() || scope_depth_ == 0U ||
            scope_stack_[scope_depth_ - 1U] != scope_opacity_layer) {
            return fail_drawing_state();
        }
        if (!builder_.pop_layer()) {
            return fail_builder();
        }
        --scope_depth_;
        scope_stack_[scope_depth_] = scope_none;
        return S_OK;
    }

    progpu::native::semantic_scene_builder& builder() noexcept
    {
        return builder_;
    }

    uint32_t failure_callback_index() const noexcept
    {
        return failure_callback_index_;
    }

    uint32_t failure_reason() const noexcept
    {
        return failure_reason_;
    }

    uint32_t translated_draw_count() const noexcept
    {
        return translated_draw_count_;
    }

    bool has_clear() const noexcept
    {
        return has_clear_;
    }

    bool has_aliased_primitives() const noexcept
    {
        return has_aliased_primitives_;
    }

    bool has_axis_aligned_clips() const noexcept
    {
        return has_axis_aligned_clips_;
    }

    bool has_gradient_brushes() const noexcept
    {
        return has_gradient_brushes_;
    }

    bool has_path_geometry() const noexcept
    {
        return has_path_geometry_;
    }

    bool has_stroked_path_geometry() const noexcept
    {
        return has_stroked_path_geometry_;
    }

    bool has_opacity_layers() const noexcept
    {
        return has_opacity_layers_;
    }

    bool has_geometric_layer_masks() const noexcept
    {
        return has_geometric_layer_masks_;
    }

    bool has_opacity_brush_layer_masks() const noexcept
    {
        return has_opacity_brush_layer_masks_;
    }

    bool has_composite_layer_masks() const noexcept
    {
        return has_composite_layer_masks_;
    }

    D2D1_COLOR_F clear_color() const noexcept
    {
        return clear_color_;
    }

    bool is_complete() const noexcept
    {
        return begun_ && ended_;
    }

private:
    struct brush_cache_entry {
        ComPtr<IUnknown> identity;
        D2D1_MATRIX_3X2_F draw_transform{};
        uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
    };

    struct command_scene_stroke_style {
        D2D1_CAP_STYLE start_cap = D2D1_CAP_STYLE_FLAT;
        D2D1_CAP_STYLE end_cap = D2D1_CAP_STYLE_FLAT;
        D2D1_CAP_STYLE dash_cap = D2D1_CAP_STYLE_FLAT;
        D2D1_LINE_JOIN line_join = D2D1_LINE_JOIN_MITER;
        D2D1_STROKE_TRANSFORM_TYPE transform_type =
            D2D1_STROKE_TRANSFORM_TYPE_NORMAL;
        float miter_limit = 10.0F;
        float dash_offset = 0.0F;
        std::vector<double> dash_intervals;
    };

    static bool finite_point(D2D1_POINT_2F point) noexcept
    {
        return std::isfinite(point.x) && std::isfinite(point.y);
    }

    static bool finite_color(D2D1_COLOR_F color) noexcept
    {
        return std::isfinite(color.r) && std::isfinite(color.g) &&
            std::isfinite(color.b) && std::isfinite(color.a);
    }

    static bool finite_transform(const D2D1_MATRIX_3X2_F& value) noexcept
    {
        return std::isfinite(value._11) && std::isfinite(value._12) &&
            std::isfinite(value._21) && std::isfinite(value._22) &&
            std::isfinite(value._31) && std::isfinite(value._32);
    }

    static bool same_transform(
        const D2D1_MATRIX_3X2_F& left,
        const D2D1_MATRIX_3X2_F& right) noexcept
    {
        return left._11 == right._11 && left._12 == right._12 &&
            left._21 == right._21 && left._22 == right._22 &&
            left._31 == right._31 && left._32 == right._32;
    }

    static bool finite_rectangle(const D2D1_RECT_F* value) noexcept
    {
        return value != nullptr && std::isfinite(value->left) &&
            std::isfinite(value->top) && std::isfinite(value->right) &&
            std::isfinite(value->bottom) && value->right >= value->left &&
            value->bottom >= value->top;
    }

    static bool infinite_rectangle(const D2D1_RECT_F& value) noexcept
    {
        const float maximum = std::numeric_limits<float>::max();
        return value.left == -maximum && value.top == -maximum &&
            value.right == maximum && value.bottom == maximum;
    }

    static bool axis_preserving_transform(
        const D2D1_MATRIX_3X2_F& value) noexcept
    {
        return value._12 == 0.0F && value._21 == 0.0F;
    }

    static bool finite_native_rectangle(
        const progpu_native_image_rect& value) noexcept
    {
        return std::isfinite(value.x) && std::isfinite(value.y) &&
            std::isfinite(value.width) && std::isfinite(value.height) &&
            value.width >= 0.0F && value.height >= 0.0F;
    }

    static progpu_native_image_rect intersect_rectangles(
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

    bool can_record() const noexcept
    {
        return begun_ && !ended_ && failure_reason_ ==
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_NONE;
    }

    void begin_callback() noexcept
    {
        if (callback_index_ != std::numeric_limits<uint32_t>::max()) {
            ++callback_index_;
        }
    }

    HRESULT fail(
        uint32_t reason,
        HRESULT hr,
        bool record_current = true) noexcept
    {
        if (failure_reason_ ==
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_NONE) {
            failure_reason_ = reason;
            failure_callback_index_ = record_current ? callback_index_ : 0U;
        }
        return hr;
    }

    HRESULT fail_drawing_state() noexcept
    {
        return fail(
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_DRAWING_STATE,
            D2DERR_WRONG_STATE);
    }

    HRESULT fail_invalid_value() noexcept
    {
        return fail(
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_INVALID_VALUE,
            E_INVALIDARG);
    }

    HRESULT fail_unsupported_state() noexcept
    {
        return fail(
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_UNSUPPORTED_STATE,
            E_NOTIMPL);
    }

    HRESULT fail_unsupported_resource() noexcept
    {
        return fail(
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_UNSUPPORTED_RESOURCE,
            E_NOTIMPL);
    }

    HRESULT fail_unsupported_operation() noexcept
    {
        return fail(
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_UNSUPPORTED_OPERATION,
            E_NOTIMPL);
    }

    HRESULT fail_builder(bool record_current = true) noexcept
    {
        const HRESULT hr = builder_.last_error() ==
                progpu::native::scene_build_error::out_of_memory
            ? E_OUTOFMEMORY
            : E_FAIL;
        return fail(
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_BUILDER,
            hr,
            record_current);
    }

    HRESULT fail_capacity_exceeded() noexcept
    {
        return fail(
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_CAPACITY_EXCEEDED,
            HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW));
    }

    HRESULT unsupported_resource_callback() noexcept
    {
        begin_callback();
        return can_record()
            ? fail_unsupported_resource()
            : fail_drawing_state();
    }

    HRESULT unsupported_operation_callback() noexcept
    {
        begin_callback();
        return can_record()
            ? fail_unsupported_operation()
            : fail_drawing_state();
    }

    uint32_t primitive_flags() noexcept
    {
        if (antialias_mode_ == D2D1_ANTIALIAS_MODE_ALIASED) {
            has_aliased_primitives_ = true;
            return PROGPU_NATIVE_PRIMITIVE_FLAG_EDGE_ALIASED;
        }
        return 0U;
    }

    progpu_native_affine_2d native_transform() const noexcept
    {
        return {
            transform_._11,
            transform_._12,
            transform_._21,
            transform_._22,
            transform_._31,
            transform_._32};
    }

    D2D1_POINT_2F transform_point(float x, float y) const noexcept
    {
        return {
            x * transform_._11 + y * transform_._21 + transform_._31,
            x * transform_._12 + y * transform_._22 + transform_._32};
    }

    progpu_native_image_rect transformed_bounds(
        float left,
        float top,
        float right,
        float bottom) const noexcept
    {
        const std::array<D2D1_POINT_2F, 4U> points{
            transform_point(left, top),
            transform_point(right, top),
            transform_point(right, bottom),
            transform_point(left, bottom)};
        float min_x = points[0].x;
        float min_y = points[0].y;
        float max_x = points[0].x;
        float max_y = points[0].y;
        for (size_t index = 1U; index < points.size(); ++index) {
            min_x = std::min(min_x, points[index].x);
            min_y = std::min(min_y, points[index].y);
            max_x = std::max(max_x, points[index].x);
            max_y = std::max(max_y, points[index].y);
        }
        return {min_x, min_y, max_x - min_x, max_y - min_y};
    }

    static bool try_invert_transform(
        const D2D1_MATRIX_3X2_F& source,
        D2D1_MATRIX_3X2_F& inverse) noexcept
    {
        const double determinant =
            static_cast<double>(source._11) * source._22 -
            static_cast<double>(source._12) * source._21;
        if (!std::isfinite(determinant) || determinant == 0.0) {
            return false;
        }
        const double reciprocal = 1.0 / determinant;
        const std::array<double, 6U> values{
            static_cast<double>(source._22) * reciprocal,
            -static_cast<double>(source._12) * reciprocal,
            -static_cast<double>(source._21) * reciprocal,
            static_cast<double>(source._11) * reciprocal,
            (static_cast<double>(source._21) * source._32 -
                static_cast<double>(source._31) * source._22) * reciprocal,
            (static_cast<double>(source._31) * source._12 -
                static_cast<double>(source._11) * source._32) * reciprocal};
        inverse._11 = static_cast<float>(values[0]);
        inverse._12 = static_cast<float>(values[1]);
        inverse._21 = static_cast<float>(values[2]);
        inverse._22 = static_cast<float>(values[3]);
        inverse._31 = static_cast<float>(values[4]);
        inverse._32 = static_cast<float>(values[5]);
        return std::all_of(
            values.begin(),
            values.end(),
            [](double value) {
                return std::isfinite(value) &&
                    value >= -std::numeric_limits<float>::max() &&
                    value <= std::numeric_limits<float>::max();
            }) && finite_transform(inverse);
    }

    static D2D1_MATRIX_3X2_F compose_transform(
        const D2D1_MATRIX_3X2_F& first,
        const D2D1_MATRIX_3X2_F& second) noexcept
    {
        D2D1_MATRIX_3X2_F result{};
        result._11 = first._11 * second._11 + first._12 * second._21;
        result._12 = first._11 * second._12 + first._12 * second._22;
        result._21 = first._21 * second._11 + first._22 * second._21;
        result._22 = first._21 * second._12 + first._22 * second._22;
        result._31 = first._31 * second._11 + first._32 * second._21 +
            second._31;
        result._32 = first._31 * second._12 + first._32 * second._22 +
            second._32;
        return result;
    }

    bool try_set_gradient_coordinate_transform(
        ID2D1Brush* source,
        progpu_native_scene_brush& destination) const noexcept
    {
        D2D1_MATRIX_3X2_F brush_transform{};
        source->GetTransform(&brush_transform);
        if (!finite_transform(brush_transform)) {
            return false;
        }
        D2D1_MATRIX_3X2_F inverse_draw{};
        D2D1_MATRIX_3X2_F inverse_brush{};
        if (!try_invert_transform(transform_, inverse_draw) ||
            !try_invert_transform(brush_transform, inverse_brush)) {
            return false;
        }
        const D2D1_MATRIX_3X2_F coordinate =
            compose_transform(inverse_draw, inverse_brush);
        if (!finite_transform(coordinate)) {
            return false;
        }
        destination.coordinate_transform0[0] = coordinate._11;
        destination.coordinate_transform0[1] = coordinate._21;
        destination.coordinate_transform0[2] = coordinate._31;
        destination.coordinate_transform1[0] = coordinate._12;
        destination.coordinate_transform1[1] = coordinate._22;
        destination.coordinate_transform1[2] = coordinate._32;
        return true;
    }

    static bool try_map_gradient_spread(
        D2D1_EXTEND_MODE source,
        uint32_t& destination) noexcept
    {
        switch (source) {
        case D2D1_EXTEND_MODE_CLAMP:
            destination = PROGPU_NATIVE_SCENE_GRADIENT_PAD;
            return true;
        case D2D1_EXTEND_MODE_WRAP:
            destination = PROGPU_NATIVE_SCENE_GRADIENT_REPEAT;
            return true;
        case D2D1_EXTEND_MODE_MIRROR:
            destination = PROGPU_NATIVE_SCENE_GRADIENT_REFLECT;
            return true;
        default:
            destination = PROGPU_NATIVE_SCENE_GRADIENT_PAD;
            return false;
        }
    }

    HRESULT translate_gradient_brush(
        ID2D1Brush* source,
        ID2D1GradientStopCollection* source_collection,
        progpu_native_scene_brush& brush,
        std::vector<progpu_native_scene_gradient_stop>& native_stops) noexcept
    {
        if (source_collection == nullptr) {
            return fail_invalid_value();
        }
        ComPtr<ID2D1GradientStopCollection1> collection;
        if (FAILED(source_collection->QueryInterface(
                IID_PPV_ARGS(&collection)))) {
            return fail_unsupported_resource();
        }
        if (collection->GetPreInterpolationSpace() != D2D1_COLOR_SPACE_SRGB ||
            collection->GetPostInterpolationSpace() != D2D1_COLOR_SPACE_SRGB ||
            !try_map_gradient_spread(
                collection->GetExtendMode(), brush.spread_method)) {
            return fail_unsupported_state();
        }
        const UINT32 stop_count = collection->GetGradientStopCount();
        if (stop_count == 0U) {
            return fail_invalid_value();
        }
        if (stop_count > PROGPU_NATIVE_SCENE_MAX_GRADIENT_STOPS) {
            return fail_capacity_exceeded();
        }

        try {
            std::vector<D2D1_GRADIENT_STOP> direct_stops(stop_count);
            collection->GetGradientStops1(direct_stops.data(), stop_count);
            native_stops.clear();
            native_stops.reserve(stop_count);
            float previous_offset =
                -std::numeric_limits<float>::infinity();
            float common_alpha = direct_stops.front().color.a;
            bool uniform_alpha = true;
            for (const auto& stop : direct_stops) {
                if (!std::isfinite(stop.position) ||
                    stop.position < previous_offset ||
                    !finite_color(stop.color)) {
                    return fail_invalid_value();
                }
                uniform_alpha = uniform_alpha &&
                    stop.color.a == common_alpha;
                native_stops.push_back({
                    {stop.color.r, stop.color.g, stop.color.b, stop.color.a},
                    stop.position,
                    0U,
                    0U,
                    0U});
                previous_offset = stop.position;
            }
            const D2D1_COLOR_INTERPOLATION_MODE interpolation =
                collection->GetColorInterpolationMode();
            if (interpolation != D2D1_COLOR_INTERPOLATION_MODE_STRAIGHT &&
                (interpolation !=
                        D2D1_COLOR_INTERPOLATION_MODE_PREMULTIPLIED ||
                    !uniform_alpha)) {
                return fail_unsupported_state();
            }
            if (!try_set_gradient_coordinate_transform(source, brush)) {
                return fail_unsupported_state();
            }
            brush.stop_count = stop_count;
            brush.color_interpolation_mode =
                PROGPU_NATIVE_SCENE_GRADIENT_INTERPOLATE_SRGB;
            const size_t inline_count = std::min<size_t>(
                native_stops.size(), std::size(brush.colors));
            for (size_t index = 0U; index < inline_count; ++index) {
                brush.colors[index] = native_stops[index].color;
                if (index < std::size(brush.offsets0)) {
                    brush.offsets0[index] = native_stops[index].offset;
                } else {
                    brush.offsets1[index - std::size(brush.offsets0)] =
                        native_stops[index].offset;
                }
            }
        } catch (const std::bad_alloc&) {
            return fail(
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_CAPACITY_EXCEEDED,
                E_OUTOFMEMORY);
        } catch (...) {
            return fail_invalid_value();
        }
        return S_OK;
    }

    HRESULT add_gradient_brush(
        ID2D1Brush* source,
        ID2D1GradientStopCollection* source_collection,
        progpu_native_scene_brush& brush,
        uint32_t& brush_index) noexcept
    {
        std::vector<progpu_native_scene_gradient_stop> native_stops;
        const HRESULT hr = translate_gradient_brush(
            source,
            source_collection,
            brush,
            native_stops);
        if (FAILED(hr)) {
            return hr;
        }
        if (!builder_.add_brush(brush, native_stops, brush_index)) {
            return fail_builder();
        }
        has_gradient_brushes_ = true;
        return S_OK;
    }

    HRESULT add_linear_gradient_brush(
        ID2D1LinearGradientBrush* source,
        uint32_t& brush_index) noexcept
    {
        const D2D1_POINT_2F start = source->GetStartPoint();
        const D2D1_POINT_2F end = source->GetEndPoint();
        const float opacity = source->GetOpacity();
        if (!finite_point(start) || !finite_point(end) ||
            !std::isfinite(opacity) || opacity < 0.0F || opacity > 1.0F) {
            return fail_invalid_value();
        }
        ComPtr<ID2D1GradientStopCollection> collection;
        source->GetGradientStopCollection(collection.GetAddressOf());
        progpu_native_scene_brush brush{};
        brush.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
        brush.opacity = opacity;
        brush.start_point = {start.x, start.y};
        brush.end_point = {end.x, end.y};
        return add_gradient_brush(
            source, collection.Get(), brush, brush_index);
    }

    HRESULT add_radial_gradient_brush(
        ID2D1RadialGradientBrush* source,
        uint32_t& brush_index) noexcept
    {
        const D2D1_POINT_2F center = source->GetCenter();
        const D2D1_POINT_2F offset = source->GetGradientOriginOffset();
        const float radius_x = source->GetRadiusX();
        const float radius_y = source->GetRadiusY();
        const float opacity = source->GetOpacity();
        const D2D1_POINT_2F origin = {
            center.x + offset.x,
            center.y + offset.y};
        if (!finite_point(center) || !finite_point(offset) ||
            !finite_point(origin) || !std::isfinite(radius_x) ||
            !std::isfinite(radius_y) || radius_x < 0.0F ||
            radius_y < 0.0F || (radius_x == 0.0F && radius_y == 0.0F) ||
            !std::isfinite(opacity) || opacity < 0.0F || opacity > 1.0F) {
            return fail_invalid_value();
        }
        ComPtr<ID2D1GradientStopCollection> collection;
        source->GetGradientStopCollection(collection.GetAddressOf());
        progpu_native_scene_brush brush{};
        brush.type = PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT;
        brush.opacity = opacity;
        brush.start_point = {origin.x, origin.y};
        brush.center = {center.x, center.y};
        brush.radius = radius_x;
        brush.radius_y = radius_y;
        return add_gradient_brush(
            source, collection.Get(), brush, brush_index);
    }

    HRESULT add_brush(
        ID2D1Brush* brush,
        uint32_t& brush_index) noexcept
    {
        brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (brush == nullptr) {
            return fail_invalid_value();
        }
        ComPtr<IUnknown> identity;
        if (FAILED(brush->QueryInterface(IID_PPV_ARGS(&identity)))) {
            return fail_unsupported_resource();
        }
        for (const auto& cached : brush_cache_) {
            if (cached.identity.Get() == identity.Get() &&
                same_transform(cached.draw_transform, transform_)) {
                brush_index = cached.brush_index;
                return S_OK;
            }
        }

        HRESULT result = E_NOTIMPL;
        ComPtr<ID2D1SolidColorBrush> solid;
        if (SUCCEEDED(brush->QueryInterface(IID_PPV_ARGS(&solid)))) {
            result = add_solid_brush(solid.Get(), brush_index);
        } else {
            ComPtr<ID2D1LinearGradientBrush> linear;
            if (SUCCEEDED(brush->QueryInterface(IID_PPV_ARGS(&linear)))) {
                result = add_linear_gradient_brush(linear.Get(), brush_index);
            } else {
                ComPtr<ID2D1RadialGradientBrush> radial;
                result = SUCCEEDED(
                        brush->QueryInterface(IID_PPV_ARGS(&radial)))
                    ? add_radial_gradient_brush(radial.Get(), brush_index)
                    : fail_unsupported_resource();
            }
        }
        if (FAILED(result)) {
            return result;
        }
        try {
            brush_cache_.push_back(
                {std::move(identity), transform_, brush_index});
        } catch (...) {
            // Caching is an optimization only; the semantic brush is already
            // retained by the scene builder and remains correct without it.
        }
        return S_OK;
    }

    HRESULT add_solid_brush(
        ID2D1Brush* brush,
        uint32_t& brush_index) noexcept
    {
        brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        if (brush == nullptr) {
            return fail_invalid_value();
        }
        ComPtr<ID2D1SolidColorBrush> solid;
        const HRESULT hr = brush->QueryInterface(IID_PPV_ARGS(&solid));
        if (FAILED(hr)) {
            return fail_unsupported_resource();
        }
        const D2D1_COLOR_F color = solid->GetColor();
        const float opacity = solid->GetOpacity();
        if (!finite_color(color) || !std::isfinite(opacity) ||
            opacity < 0.0F || opacity > 1.0F) {
            return fail_invalid_value();
        }
        if (!builder_.add_solid_brush(
                {color.r, color.g, color.b, color.a},
                opacity,
                brush_index)) {
            return fail_builder();
        }
        return S_OK;
    }

    HRESULT draw_rectangle(
        const D2D1_RECT_F& rectangle,
        ID2D1Brush* brush,
        float stroke_width) noexcept
    {
        uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        HRESULT hr = add_brush(brush, brush_index);
        if (FAILED(hr)) {
            return hr;
        }
        progpu_native_analytic_primitive primitive{};
        primitive.kind = PROGPU_NATIVE_PRIMITIVE_RECTANGLE;
        primitive.flags = primitive_flags();
        primitive.x = rectangle.left;
        primitive.y = rectangle.top;
        primitive.width = rectangle.right - rectangle.left;
        primitive.height = rectangle.bottom - rectangle.top;
        primitive.stroke_thickness = stroke_width;
        primitive.color = {1.0F, 1.0F, 1.0F, 1.0F};
        primitive.transform = native_transform();
        const float radius = stroke_width * 0.5F;
        const auto bounds = transformed_bounds(
            rectangle.left - radius,
            rectangle.top - radius,
            rectangle.right + radius,
            rectangle.bottom + radius);
        if (!builder_.draw_analytic(
                std::span<const progpu_native_analytic_primitive>(
                    &primitive,
                    1U),
                std::span<const uint32_t>(&brush_index, 1U),
                bounds)) {
            return fail_builder();
        }
        record_draw();
        return S_OK;
    }

    HRESULT draw_filled_geometry(
        ID2D1Geometry* geometry,
        ID2D1Brush* brush) noexcept
    {
        CommandScenePathSink* raw_sink = new (std::nothrow)
            CommandScenePathSink();
        if (raw_sink == nullptr) {
            return fail(
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_BUILDER,
                E_OUTOFMEMORY);
        }
        ComPtr<CommandScenePathSink> path_sink;
        path_sink.Attach(raw_sink);
        HRESULT hr = geometry->Simplify(
            D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES,
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            path_sink.Get());
        hr = finish_path_capture(path_sink.Get(), hr);
        if (FAILED(hr)) {
            return hr;
        }
        const auto segments = path_sink->segments();
        if (segments.empty()) {
            record_draw();
            return S_OK;
        }

        D2D1_RECT_F local_bounds{};
        hr = geometry->GetBounds(nullptr, &local_bounds);
        D2D1_RECT_F target_bounds{};
        if (SUCCEEDED(hr)) {
            hr = geometry->GetBounds(&transform_, &target_bounds);
        }
        if (FAILED(hr)) {
            return fail_unsupported_resource();
        }
        return draw_captured_path(
            path_sink.Get(),
            brush,
            local_bounds,
            target_bounds,
            native_transform(),
            false);
    }

    HRESULT add_geometric_layer_mask(
        ID2D1Geometry* geometry,
        const D2D1_MATRIX_3X2_F& mask_transform,
        ID2D1Brush* opacity_brush,
        const D2D1_RECT_F& content_bounds,
        uint32_t& resource_index,
        progpu_native_image_rect& target_bounds,
        bool& empty) noexcept
    {
        resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        target_bounds = {};
        empty = false;
        CommandScenePathSink* raw_sink = new (std::nothrow)
            CommandScenePathSink();
        if (raw_sink == nullptr) {
            return fail(
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_BUILDER,
                E_OUTOFMEMORY);
        }
        ComPtr<CommandScenePathSink> path_sink;
        path_sink.Attach(raw_sink);
        HRESULT hr = geometry->Simplify(
            D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES,
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            path_sink.Get());
        hr = finish_path_capture(path_sink.Get(), hr);
        if (FAILED(hr)) {
            return hr;
        }
        const auto segments = path_sink->segments();
        if (segments.empty()) {
            empty = true;
            return S_OK;
        }

        D2D1_RECT_F local_bounds{};
        hr = geometry->GetBounds(nullptr, &local_bounds);
        const D2D1_MATRIX_3X2_F target_transform =
            compose_transform(mask_transform, transform_);
        D2D1_RECT_F transformed_mask_bounds{};
        const bool finite_target_transform = finite_transform(target_transform);
        if (SUCCEEDED(hr) && finite_target_transform) {
            hr = geometry->GetBounds(
                &target_transform,
                &transformed_mask_bounds);
        }
        if (FAILED(hr) || !finite_target_transform ||
            !finite_rectangle(&local_bounds) ||
            !finite_rectangle(&transformed_mask_bounds)) {
            return fail_unsupported_resource();
        }
        if (local_bounds.right == local_bounds.left ||
            local_bounds.bottom == local_bounds.top ||
            transformed_mask_bounds.right == transformed_mask_bounds.left ||
            transformed_mask_bounds.bottom == transformed_mask_bounds.top) {
            empty = true;
            return S_OK;
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
                target_transform._11,
                target_transform._12,
                target_transform._21,
                target_transform._22,
                target_transform._31,
                target_transform._32},
            path_sink->fill_rule(),
            8U,
            PROGPU_NATIVE_CLIP_INTERSECT,
            0U};
        if (opacity_brush == nullptr) {
            if (!builder_.add_vector_clip_mask(
                    std::span<const progpu_native_scene_clip_path>(&path, 1U),
                    segments,
                    1.0F,
                    resource_index)) {
                return fail_builder();
            }
        } else {
            progpu_native_scene_layer_brush_mask brush_mask{};
            std::vector<progpu_native_scene_gradient_stop> stops;
            bool empty_brush = false;
            bool gradient = false;
            const HRESULT brush_hr = translate_opacity_brush_layer_mask(
                opacity_brush,
                content_bounds,
                brush_mask,
                stops,
                empty_brush,
                gradient);
            if (FAILED(brush_hr)) {
                return brush_hr;
            }
            if (empty_brush) {
                empty = true;
                return S_OK;
            }
            if (!builder_.add_composite_mask(
                    std::span<const progpu_native_scene_layer_brush_mask>(
                        &brush_mask,
                        1U),
                    {},
                    {},
                    {},
                    {},
                    std::span<const progpu_native_scene_clip_path>(&path, 1U),
                    segments,
                    {},
                    stops,
                    1.0F,
                    resource_index)) {
                return fail_builder();
            }
            has_gradient_brushes_ |= gradient;
        }
        target_bounds = {
            transformed_mask_bounds.left,
            transformed_mask_bounds.top,
            transformed_mask_bounds.right - transformed_mask_bounds.left,
            transformed_mask_bounds.bottom - transformed_mask_bounds.top};
        return S_OK;
    }

    HRESULT translate_opacity_brush_layer_mask(
        ID2D1Brush* source,
        const D2D1_RECT_F& content_bounds,
        progpu_native_scene_layer_brush_mask& mask,
        std::vector<progpu_native_scene_gradient_stop>& stops,
        bool& empty,
        bool& gradient) noexcept
    {
        mask = {};
        stops.clear();
        gradient = false;
        empty = content_bounds.right == content_bounds.left ||
            content_bounds.bottom == content_bounds.top;
        if (empty) {
            return S_OK;
        }

        progpu_native_scene_brush brush{};
        ComPtr<ID2D1SolidColorBrush> solid;
        HRESULT hr = source->QueryInterface(IID_PPV_ARGS(&solid));
        if (SUCCEEDED(hr)) {
            const D2D1_COLOR_F color = solid->GetColor();
            const float opacity = solid->GetOpacity();
            if (!finite_color(color) || !std::isfinite(opacity) ||
                opacity < 0.0F || opacity > 1.0F) {
                return fail_invalid_value();
            }
            brush.type = PROGPU_NATIVE_SCENE_BRUSH_SOLID;
            brush.opacity = opacity;
            brush.colors[0] = {color.r, color.g, color.b, color.a};
            brush.coordinate_transform0[0] = 1.0F;
            brush.coordinate_transform1[1] = 1.0F;
        } else {
            ComPtr<ID2D1LinearGradientBrush> linear;
            if (SUCCEEDED(source->QueryInterface(IID_PPV_ARGS(&linear)))) {
                const D2D1_POINT_2F start = linear->GetStartPoint();
                const D2D1_POINT_2F end = linear->GetEndPoint();
                const float opacity = linear->GetOpacity();
                if (!finite_point(start) || !finite_point(end) ||
                    !std::isfinite(opacity) || opacity < 0.0F ||
                    opacity > 1.0F) {
                    return fail_invalid_value();
                }
                ComPtr<ID2D1GradientStopCollection> collection;
                linear->GetGradientStopCollection(collection.GetAddressOf());
                brush.type = PROGPU_NATIVE_SCENE_BRUSH_LINEAR_GRADIENT;
                brush.opacity = opacity;
                brush.start_point = {start.x, start.y};
                brush.end_point = {end.x, end.y};
                hr = translate_gradient_brush(
                    linear.Get(), collection.Get(), brush, stops);
                gradient = true;
            } else {
                ComPtr<ID2D1RadialGradientBrush> radial;
                if (FAILED(source->QueryInterface(IID_PPV_ARGS(&radial)))) {
                    return fail_unsupported_resource();
                }
                const D2D1_POINT_2F center = radial->GetCenter();
                const D2D1_POINT_2F offset =
                    radial->GetGradientOriginOffset();
                const D2D1_POINT_2F origin = {
                    center.x + offset.x,
                    center.y + offset.y};
                const float radius_x = radial->GetRadiusX();
                const float radius_y = radial->GetRadiusY();
                const float opacity = radial->GetOpacity();
                if (!finite_point(center) || !finite_point(offset) ||
                    !finite_point(origin) || !std::isfinite(radius_x) ||
                    !std::isfinite(radius_y) || radius_x < 0.0F ||
                    radius_y < 0.0F ||
                    (radius_x == 0.0F && radius_y == 0.0F) ||
                    !std::isfinite(opacity) || opacity < 0.0F ||
                    opacity > 1.0F) {
                    return fail_invalid_value();
                }
                ComPtr<ID2D1GradientStopCollection> collection;
                radial->GetGradientStopCollection(collection.GetAddressOf());
                brush.type = PROGPU_NATIVE_SCENE_BRUSH_RADIAL_GRADIENT;
                brush.opacity = opacity;
                brush.start_point = {origin.x, origin.y};
                brush.center = {center.x, center.y};
                brush.radius = radius_x;
                brush.radius_y = radius_y;
                hr = translate_gradient_brush(
                    radial.Get(), collection.Get(), brush, stops);
                gradient = true;
            }
            if (FAILED(hr)) {
                return hr;
            }
        }

        mask.struct_size = sizeof(mask);
        mask.kind = PROGPU_NATIVE_SCENE_LAYER_MASK_BRUSH;
        mask.gradient_stop_count = static_cast<uint32_t>(stops.size());
        mask.bounds = {
            content_bounds.left,
            content_bounds.top,
            content_bounds.right - content_bounds.left,
            content_bounds.bottom - content_bounds.top};
        mask.transform = native_transform();
        mask.opacity = 1.0F;
        mask.brush = brush;
        return S_OK;
    }

    HRESULT add_opacity_brush_layer_mask(
        ID2D1Brush* source,
        const D2D1_RECT_F& content_bounds,
        uint32_t& resource_index,
        bool& empty) noexcept
    {
        resource_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        progpu_native_scene_layer_brush_mask mask{};
        std::vector<progpu_native_scene_gradient_stop> stops;
        bool gradient = false;
        const HRESULT hr = translate_opacity_brush_layer_mask(
            source,
            content_bounds,
            mask,
            stops,
            empty,
            gradient);
        if (FAILED(hr) || empty) {
            return hr;
        }
        if (!builder_.add_brush_mask(mask, stops, resource_index)) {
            return fail_builder();
        }
        has_gradient_brushes_ |= gradient;
        return S_OK;
    }

    HRESULT draw_stroked_geometry(
        ID2D1Geometry* geometry,
        ID2D1Brush* brush,
        float stroke_width,
        ID2D1StrokeStyle* stroke_style) noexcept
    {
        const HRESULT semantic_hr = draw_semantic_stroked_geometry(
            geometry, brush, stroke_width, stroke_style);
        if (semantic_hr != E_NOINTERFACE) {
            return semantic_hr;
        }

        CommandScenePathSink* raw_sink = new (std::nothrow)
            CommandScenePathSink();
        if (raw_sink == nullptr) {
            return fail(
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_BUILDER,
                E_OUTOFMEMORY);
        }
        ComPtr<CommandScenePathSink> path_sink;
        path_sink.Attach(raw_sink);
        HRESULT hr = geometry->Widen(
            stroke_width,
            stroke_style,
            &transform_,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            path_sink.Get());
        hr = finish_path_capture(path_sink.Get(), hr);
        if (FAILED(hr)) {
            return hr;
        }
        if (path_sink->segments().empty()) {
            record_draw();
            return S_OK;
        }

        D2D1_RECT_F target_bounds{};
        hr = geometry->GetWidenedBounds(
            stroke_width,
            stroke_style,
            &transform_,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            &target_bounds);
        if (FAILED(hr)) {
            return fail_unsupported_resource();
        }
        const progpu_native_affine_2d identity_transform{
            1.0F,
            0.0F,
            0.0F,
            1.0F,
            0.0F,
            0.0F};
        return draw_captured_path(
            path_sink.Get(),
            brush,
            target_bounds,
            target_bounds,
            identity_transform,
            true);
    }

    static HRESULT translate_stroke_style(
        ID2D1StrokeStyle* source,
        command_scene_stroke_style& result) noexcept
    {
        result = {};
        if (source == nullptr) {
            return S_OK;
        }
        result.start_cap = source->GetStartCap();
        result.end_cap = source->GetEndCap();
        result.dash_cap = source->GetDashCap();
        result.line_join = source->GetLineJoin();
        result.miter_limit = source->GetMiterLimit();
        result.dash_offset = source->GetDashOffset();
        const D2D1_DASH_STYLE dash_style = source->GetDashStyle();
        ComPtr<ID2D1StrokeStyle1> source1;
        if (SUCCEEDED(source->QueryInterface(IID_PPV_ARGS(&source1)))) {
            result.transform_type = source1->GetStrokeTransformType();
        }
        if (result.start_cap > D2D1_CAP_STYLE_TRIANGLE ||
            result.end_cap > D2D1_CAP_STYLE_TRIANGLE ||
            result.dash_cap > D2D1_CAP_STYLE_TRIANGLE ||
            result.line_join > D2D1_LINE_JOIN_MITER_OR_BEVEL ||
            result.transform_type > D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE ||
            !std::isfinite(result.miter_limit) ||
            result.miter_limit <= 0.0F ||
            !std::isfinite(result.dash_offset) ||
            dash_style > D2D1_DASH_STYLE_CUSTOM) {
            return E_INVALIDARG;
        }
        try {
            switch (dash_style) {
            case D2D1_DASH_STYLE_SOLID:
                break;
            case D2D1_DASH_STYLE_DASH:
                result.dash_intervals = {2.0, 2.0};
                break;
            case D2D1_DASH_STYLE_DOT:
                result.dash_intervals = {0.0, 2.0};
                break;
            case D2D1_DASH_STYLE_DASH_DOT:
                result.dash_intervals = {2.0, 2.0, 0.0, 2.0};
                break;
            case D2D1_DASH_STYLE_DASH_DOT_DOT:
                result.dash_intervals = {
                    2.0, 2.0, 0.0, 2.0, 0.0, 2.0};
                break;
            case D2D1_DASH_STYLE_CUSTOM: {
                const UINT32 count = source->GetDashesCount();
                constexpr UINT32 maximum_dash_count = 1U << 20U;
                if (count == 0U || count > maximum_dash_count) {
                    return E_INVALIDARG;
                }
                std::vector<FLOAT> dashes(count);
                source->GetDashes(dashes.data(), count);
                bool has_positive = false;
                result.dash_intervals.reserve(count);
                for (FLOAT dash : dashes) {
                    if (!std::isfinite(dash) || dash < 0.0F) {
                        return E_INVALIDARG;
                    }
                    has_positive = has_positive || dash > 0.0F;
                    result.dash_intervals.push_back(dash);
                }
                if (!has_positive) {
                    return E_INVALIDARG;
                }
                break;
            }
            default:
                return E_INVALIDARG;
            }
        } catch (const std::bad_alloc&) {
            return E_OUTOFMEMORY;
        } catch (...) {
            return E_FAIL;
        }
        return S_OK;
    }

    HRESULT draw_semantic_stroked_geometry(
        ID2D1Geometry* geometry,
        ID2D1Brush* brush,
        float stroke_width,
        ID2D1StrokeStyle* stroke_style) noexcept
    {
        command_scene_stroke_style style{};
        HRESULT hr = translate_stroke_style(stroke_style, style);
        if (FAILED(hr)) {
            return hr == E_OUTOFMEMORY
                ? fail(
                    PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_BUILDER,
                    hr)
                : fail_invalid_value();
        }
        if (stroke_width == 0.0F && style.transform_type !=
            D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE) {
            record_draw();
            return S_OK;
        }

        auto* raw_sink = new (std::nothrow) CommandSceneStrokeSink();
        if (raw_sink == nullptr) {
            return fail(
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_BUILDER,
                E_OUTOFMEMORY);
        }
        ComPtr<CommandSceneStrokeSink> stroke_sink;
        stroke_sink.Attach(raw_sink);
        hr = geometry->Simplify(
            D2D1_GEOMETRY_SIMPLIFICATION_OPTION_CUBICS_AND_LINES,
            nullptr,
            D2D1_DEFAULT_FLATTENING_TOLERANCE,
            stroke_sink.Get());
        const HRESULT close_hr = stroke_sink->Close();
        if (SUCCEEDED(hr)) {
            hr = close_hr;
        }
        if (FAILED(hr)) {
            if (stroke_sink->capacity_exceeded()) {
                return fail_capacity_exceeded();
            }
            if (hr == E_NOTIMPL) {
                return E_NOINTERFACE;
            }
            if (hr == E_OUTOFMEMORY) {
                return fail(
                    PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_BUILDER,
                    hr);
            }
            return E_NOINTERFACE;
        }

        try {
            struct stroke_run {
                size_t segment_offset{};
                size_t segment_count{};
                size_t smooth_join_offset{};
                bool closed{};
                bool start_uses_dash_cap{};
                bool end_uses_dash_cap{};
            };
            std::vector<stroke_run> runs;
            std::vector<progpu_native_path_segment> run_segments;
            std::vector<uint8_t> run_smooth_joins;
            const auto captured_segments = stroke_sink->segments();
            const auto captured_flags = stroke_sink->segment_flags();
            if (captured_segments.size() != captured_flags.size()) {
                return E_NOINTERFACE;
            }
            run_segments.reserve(captured_segments.size() +
                stroke_sink->figures().size());
            run_smooth_joins.reserve(captured_segments.size() +
                stroke_sink->figures().size());
            runs.reserve(stroke_sink->figures().size());

            for (const auto& figure : stroke_sink->figures()) {
                if (figure.segment_offset > captured_segments.size() ||
                    figure.segment_count > captured_segments.size() -
                        figure.segment_offset) {
                    return E_NOINTERFACE;
                }
                if (figure.segment_count == 0U) {
                    continue;
                }
                const auto figure_segments = captured_segments.subspan(
                    figure.segment_offset,
                    figure.segment_count);
                const auto figure_flags = captured_flags.subspan(
                    figure.segment_offset,
                    figure.segment_count);
                const auto last_point =
                    semantic_path_stroke::segment_end(figure_segments.back());
                const bool needs_closing_segment = figure.closed &&
                    (last_point.x != figure.start.x ||
                        last_point.y != figure.start.y);
                const size_t edge_count = figure.segment_count +
                    (needs_closing_segment ? 1U : 0U);
                const auto edge_segment = [&](size_t index) {
                    if (index < figure.segment_count) {
                        return figure_segments[index];
                    }
                    progpu_native_path_segment closing{};
                    closing.p0 = last_point;
                    closing.p1 = {figure.start.x, figure.start.y};
                    closing.kind = PROGPU_NATIVE_PATH_SEGMENT_LINE;
                    return closing;
                };
                const auto edge_flags = [&](size_t index) {
                    return index < figure.segment_count
                        ? figure_flags[index]
                        : figure.closing_flags;
                };
                const auto edge_stroked = [&](size_t index) {
                    return (edge_flags(index) &
                        D2D1_PATH_SEGMENT_FORCE_UNSTROKED) == 0U;
                };
                const auto edge_round_join = [&](size_t index) {
                    return (edge_flags(index) &
                        D2D1_PATH_SEGMENT_FORCE_ROUND_LINE_JOIN) != 0U;
                };
                const auto append_run = [&](size_t first,
                                            size_t count,
                                            bool closed,
                                            bool start_uses_dash_cap,
                                            bool end_uses_dash_cap) {
                    if (count == 0U) {
                        return;
                    }
                    const size_t segment_offset = run_segments.size();
                    const size_t smooth_join_offset =
                        run_smooth_joins.size();
                    for (size_t index = 0U; index < count; ++index) {
                        run_segments.push_back(
                            edge_segment((first + index) % edge_count));
                    }
                    for (size_t index = 0U; index < count; ++index) {
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
                for (size_t index = 0U; index < edge_count; ++index) {
                    all_stroked = all_stroked && edge_stroked(index);
                }
                if (all_stroked) {
                    append_run(0U, edge_count, figure.closed, false, false);
                    continue;
                }
                if (figure.closed) {
                    size_t gap = 0U;
                    while (gap < edge_count && edge_stroked(gap)) {
                        ++gap;
                    }
                    const size_t first_after_gap = (gap + 1U) % edge_count;
                    size_t consumed = 0U;
                    while (consumed < edge_count) {
                        while (consumed < edge_count && !edge_stroked(
                                (first_after_gap + consumed) % edge_count)) {
                            ++consumed;
                        }
                        const size_t first = consumed;
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
                    continue;
                }
                size_t index = 0U;
                while (index < edge_count) {
                    while (index < edge_count && !edge_stroked(index)) {
                        ++index;
                    }
                    const size_t first = index;
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
            if (runs.empty()) {
                record_draw();
                return S_OK;
            }

            bool use_polyline_batch = true;
            for (const auto& run : runs) {
                const auto segments = std::span(run_segments).subspan(
                    run.segment_offset,
                    run.segment_count);
                const auto smooth_joins = std::span(run_smooth_joins).subspan(
                    run.smooth_join_offset,
                    run.segment_count);
                use_polyline_batch = use_polyline_batch &&
                    (!run.closed || run.segment_count >= 2U) &&
                    std::all_of(
                        segments.begin(),
                        segments.end(),
                        [](const progpu_native_path_segment& segment) {
                            return segment.kind ==
                                PROGPU_NATIVE_PATH_SEGMENT_LINE;
                        }) &&
                    (style.line_join == D2D1_LINE_JOIN_ROUND ||
                        std::none_of(
                            smooth_joins.begin(),
                            smooth_joins.end(),
                            [](uint8_t smooth) { return smooth != 0U; }));
            }
            D2D1_RECT_F geometry_bounds{};
            progpu_native_image_rect bounds{};
            const float miter_extent = std::max(1.0F, style.miter_limit);
            if (style.transform_type ==
                D2D1_STROKE_TRANSFORM_TYPE_NORMAL) {
                hr = geometry->GetBounds(nullptr, &geometry_bounds);
                const float padding =
                    stroke_width * 0.5F * miter_extent;
                if (SUCCEEDED(hr)) {
                    bounds = transformed_bounds(
                        geometry_bounds.left - padding,
                        geometry_bounds.top - padding,
                        geometry_bounds.right + padding,
                        geometry_bounds.bottom + padding);
                }
            } else {
                hr = geometry->GetBounds(&transform_, &geometry_bounds);
                const float device_width = style.transform_type ==
                        D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE
                    ? 1.0F
                    : stroke_width;
                const float padding =
                    device_width * 0.5F * miter_extent;
                if (SUCCEEDED(hr)) {
                    bounds = {
                        geometry_bounds.left - padding,
                        geometry_bounds.top - padding,
                        geometry_bounds.right - geometry_bounds.left +
                            padding * 2.0F,
                        geometry_bounds.bottom - geometry_bounds.top +
                            padding * 2.0F};
                }
            }
            if (FAILED(hr)) {
                return E_NOINTERFACE;
            }
            if (!finite_native_rectangle(bounds)) {
                return fail_invalid_value();
            }

            if (use_polyline_batch) {
                std::vector<progpu_native_scene_stroke> strokes;
                std::vector<progpu_native_point> points;
                std::vector<double> doubles;
                std::vector<uint32_t> brush_indices;
                strokes.reserve(runs.size());
                brush_indices.reserve(runs.size());
                points.reserve(run_segments.size() + runs.size());
                if (!style.dash_intervals.empty()) {
                    const uint64_t interval_count =
                        static_cast<uint64_t>(style.dash_intervals.size()) *
                        runs.size();
                    if (interval_count >
                        std::numeric_limits<size_t>::max()) {
                        return fail_capacity_exceeded();
                    }
                    doubles.reserve(static_cast<size_t>(interval_count));
                }
                for (const auto& run : runs) {
                    const auto segments = std::span(run_segments).subspan(
                        run.segment_offset,
                        run.segment_count);
                    progpu_native_scene_stroke stroke{};
                    stroke.struct_size = sizeof(stroke);
                    stroke.kind = PROGPU_NATIVE_SCENE_STROKE_POLYLINE;
                    stroke.flags = run.closed
                        ? PROGPU_NATIVE_POLYLINE_FLAG_CLOSED
                        : 0U;
                    if (style.transform_type ==
                        D2D1_STROKE_TRANSFORM_TYPE_FIXED) {
                        stroke.flags |=
                            PROGPU_NATIVE_POLYLINE_FLAG_FIXED_DEVICE_STROKE;
                    } else if (style.transform_type ==
                        D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE) {
                        stroke.flags |= PROGPU_NATIVE_POLYLINE_FLAG_HAIRLINE;
                    }
                    stroke.point_offset = points.size();
                    stroke.point_count = segments.size() +
                        (run.closed ? 0U : 1U);
                    stroke.dash_interval_offset = doubles.size();
                    stroke.dash_interval_count = style.dash_intervals.size();
                    stroke.color = {1.0F, 1.0F, 1.0F, 1.0F};
                    stroke.transform = native_transform();
                    stroke.stroke_thickness = style.transform_type ==
                            D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE
                        ? 0.0F
                        : stroke_width;
                    stroke.miter_limit = std::max(1.0F, style.miter_limit);
                    stroke.dash_offset = style.dash_offset;
                    stroke.start_cap = run.start_uses_dash_cap
                        ? static_cast<uint32_t>(style.dash_cap)
                        : static_cast<uint32_t>(style.start_cap);
                    stroke.end_cap = run.end_uses_dash_cap
                        ? static_cast<uint32_t>(style.dash_cap)
                        : static_cast<uint32_t>(style.end_cap);
                    stroke.line_join = style.line_join ==
                            D2D1_LINE_JOIN_MITER_OR_BEVEL
                        ? PROGPU_NATIVE_STROKE_JOIN_MITER
                        : static_cast<uint32_t>(style.line_join);
                    stroke.dash_cap = static_cast<uint32_t>(style.dash_cap);
                    points.push_back(segments.front().p0);
                    const size_t end_count = segments.size() -
                        (run.closed ? 1U : 0U);
                    for (size_t index = 0U; index < end_count; ++index) {
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
                uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                hr = add_brush(brush, brush_index);
                if (FAILED(hr)) {
                    return hr;
                }
                std::fill(
                    brush_indices.begin(), brush_indices.end(), brush_index);
                if (!builder_.draw_strokes(
                        strokes,
                        points,
                        doubles,
                        brush_indices,
                        bounds)) {
                    return fail_builder();
                }
            } else {
                semantic_path_stroke::style semantic_style{};
                semantic_style.transform = native_transform();
                semantic_style.thickness = style.transform_type ==
                        D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE
                    ? 0.0F
                    : stroke_width;
                semantic_style.miter_limit =
                    std::max(1.0F, style.miter_limit);
                semantic_style.dash_offset = style.dash_offset;
                semantic_style.dash_cap =
                    static_cast<uint32_t>(style.dash_cap);
                semantic_style.line_join = style.line_join ==
                        D2D1_LINE_JOIN_MITER_OR_BEVEL
                    ? PROGPU_NATIVE_STROKE_JOIN_MITER
                    : static_cast<uint32_t>(style.line_join);
                semantic_style.primitive_flags = primitive_flags();
                if (style.transform_type ==
                    D2D1_STROKE_TRANSFORM_TYPE_FIXED) {
                    semantic_style.primitive_flags |=
                        PROGPU_NATIVE_PRIMITIVE_FLAG_FIXED_DEVICE_STROKE;
                } else if (style.transform_type ==
                    D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE) {
                    semantic_style.primitive_flags |=
                        PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE;
                }
                progpu::native::mil::curve_dash::run_buffer dash_scratch;
                std::vector<progpu_native_geometry_primitive> primitives;
                std::vector<uint32_t> brush_indices;
                primitives.reserve(run_segments.size() * 2U +
                    runs.size() * 2U);
                brush_indices.reserve(primitives.capacity());
                for (const auto& run : runs) {
                    semantic_style.start_cap = run.start_uses_dash_cap
                        ? static_cast<uint32_t>(style.dash_cap)
                        : static_cast<uint32_t>(style.start_cap);
                    semantic_style.end_cap = run.end_uses_dash_cap
                        ? static_cast<uint32_t>(style.dash_cap)
                        : static_cast<uint32_t>(style.end_cap);
                    const auto compile_result =
                        semantic_path_stroke::compile(
                            std::span(run_segments).subspan(
                                run.segment_offset,
                                run.segment_count),
                            std::span(run_smooth_joins).subspan(
                                run.smooth_join_offset,
                                run.segment_count),
                            run.closed,
                            style.dash_intervals,
                            semantic_style,
                            PROGPU_NATIVE_SCENE_NO_INDEX,
                            dash_scratch,
                            primitives,
                            brush_indices);
                    if (compile_result ==
                        semantic_path_stroke::result::capacity_exceeded) {
                        return fail_capacity_exceeded();
                    }
                    if (compile_result !=
                        semantic_path_stroke::result::success) {
                        return E_NOINTERFACE;
                    }
                }
                if (primitives.empty()) {
                    record_draw();
                    return S_OK;
                }
                uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
                hr = add_brush(brush, brush_index);
                if (FAILED(hr)) {
                    return hr;
                }
                std::fill(
                    brush_indices.begin(), brush_indices.end(), brush_index);
                if (!builder_.draw_geometry(
                        primitives,
                        brush_indices,
                        bounds)) {
                    return fail_builder();
                }
            }
            has_path_geometry_ = true;
            has_stroked_path_geometry_ = true;
            record_draw();
            return S_OK;
        } catch (const std::bad_alloc&) {
            return fail(
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_BUILDER,
                E_OUTOFMEMORY);
        } catch (...) {
            return fail_builder();
        }
    }

    HRESULT finish_path_capture(
        CommandScenePathSink* path_sink,
        HRESULT operation_hr) noexcept
    {
        const HRESULT close_hr = path_sink->Close();
        if (SUCCEEDED(operation_hr)) {
            operation_hr = close_hr;
        }
        if (SUCCEEDED(operation_hr)) {
            return S_OK;
        }
        if (path_sink->capacity_exceeded()) {
            return fail_capacity_exceeded();
        }
        if (operation_hr == E_OUTOFMEMORY) {
            return fail(
                PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_BUILDER,
                operation_hr);
        }
        return fail_unsupported_resource();
    }

    HRESULT draw_captured_path(
        CommandScenePathSink* path_sink,
        ID2D1Brush* brush,
        const D2D1_RECT_F& local_bounds,
        const D2D1_RECT_F& target_bounds,
        const progpu_native_affine_2d& path_transform,
        bool stroked) noexcept
    {
        const auto segments = path_sink->segments();
        if (segments.empty()) {
            record_draw();
            return S_OK;
        }
        if (!finite_rectangle(&local_bounds) ||
            !finite_rectangle(&target_bounds)) {
            return fail_invalid_value();
        }
        if (local_bounds.right == local_bounds.left ||
            local_bounds.bottom == local_bounds.top ||
            target_bounds.right == target_bounds.left ||
            target_bounds.bottom == target_bounds.top) {
            record_draw();
            return S_OK;
        }

        uint32_t brush_index = PROGPU_NATIVE_SCENE_NO_INDEX;
        HRESULT hr = add_brush(brush, brush_index);
        if (FAILED(hr)) {
            return hr;
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
            path_transform,
            path_sink->fill_rule(),
            8U};
        const progpu_native_image_rect bounds{
            target_bounds.left,
            target_bounds.top,
            target_bounds.right - target_bounds.left,
            target_bounds.bottom - target_bounds.top};
        if (!builder_.draw_paths(
                std::span<const progpu_native_scene_path_fill>(&path, 1U),
                segments,
                std::span<const uint32_t>(&brush_index, 1U),
                bounds)) {
            return fail_builder();
        }
        has_path_geometry_ = true;
        has_stroked_path_geometry_ |= stroked;
        record_draw();
        return S_OK;
    }

    void record_draw() noexcept
    {
        if (translated_draw_count_ != std::numeric_limits<uint32_t>::max()) {
            ++translated_draw_count_;
        }
    }

    std::atomic<ULONG> reference_count_{1U};
    progpu::native::semantic_scene_builder builder_;
    D2D1_MATRIX_3X2_F transform_ = D2D1::Matrix3x2F::Identity();
    D2D1_COLOR_F clear_color_{};
    std::array<
        progpu_native_image_rect,
        PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> clip_stack_{};
    static constexpr uint8_t scope_none = 0U;
    static constexpr uint8_t scope_axis_aligned_clip = 1U;
    static constexpr uint8_t scope_opacity_layer = 2U;
    std::array<uint8_t, PROGPU_NATIVE_SCENE_MAX_STACK_DEPTH> scope_stack_{};
    std::vector<brush_cache_entry> brush_cache_;
    D2D1_ANTIALIAS_MODE antialias_mode_ =
        D2D1_ANTIALIAS_MODE_PER_PRIMITIVE;
    uint32_t callback_index_ = 0U;
    uint32_t failure_callback_index_ = 0U;
    uint32_t failure_reason_ =
        PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_NONE;
    uint32_t translated_draw_count_ = 0U;
    uint32_t clip_depth_ = 0U;
    uint32_t scope_depth_ = 0U;
    bool builder_ready_ = true;
    bool begun_ = false;
    bool ended_ = false;
    bool has_clear_ = false;
    bool has_aliased_primitives_ = false;
    bool has_axis_aligned_clips_ = false;
    bool has_gradient_brushes_ = false;
    bool has_path_geometry_ = false;
    bool has_stroked_path_geometry_ = false;
    bool has_opacity_layers_ = false;
    bool has_geometric_layer_masks_ = false;
    bool has_opacity_brush_layer_masks_ = false;
    bool has_composite_layer_masks_ = false;
};

void initialize_scene_stream_result(
    CommandSceneStreamSink& sink,
    uint64_t scene_id,
    uint64_t generation,
    progpu_native_direct2d_scene_stream_result& result) noexcept
{
    result = {};
    result.struct_size = static_cast<uint32_t>(sizeof(result));
    result.scene_id = scene_id;
    result.generation = generation;
    result.failure_callback_index = sink.failure_callback_index();
    result.failure_reason = sink.failure_reason();
    result.translated_draw_count = sink.translated_draw_count();
    if (sink.has_clear()) {
        result.flags |=
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_LEADING_CLEAR;
        const D2D1_COLOR_F color = sink.clear_color();
        result.clear_color = {color.r, color.g, color.b, color.a};
    }
    if (sink.has_aliased_primitives()) {
        result.flags |=
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_ALIASED_PRIMITIVES;
    }
    if (sink.has_axis_aligned_clips()) {
        result.flags |=
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_AXIS_ALIGNED_CLIPS;
    }
    if (sink.has_gradient_brushes()) {
        result.flags |=
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_GRADIENT_BRUSHES;
    }
    if (sink.has_path_geometry()) {
        result.flags |=
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_PATH_GEOMETRY;
    }
    if (sink.has_stroked_path_geometry()) {
        result.flags |=
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_STROKED_PATH_GEOMETRY;
    }
    if (sink.has_opacity_layers()) {
        result.flags |=
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_OPACITY_LAYERS;
    }
    if (sink.has_geometric_layer_masks()) {
        result.flags |=
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_GEOMETRIC_LAYER_MASKS;
    }
    if (sink.has_opacity_brush_layer_masks()) {
        result.flags |=
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_OPACITY_BRUSH_LAYER_MASKS;
    }
    if (sink.has_composite_layer_masks()) {
        result.flags |=
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_COMPOSITE_LAYER_MASKS;
    }
}

HRESULT build_scene_stream(
    CommandSceneStreamSink& sink,
    uint64_t scene_id,
    uint64_t generation,
    uint8_t* destination,
    uint64_t destination_capacity,
    progpu_native_direct2d_scene_stream_result& result,
    HRESULT recording_result) noexcept
{
    initialize_scene_stream_result(sink, scene_id, generation, result);
    if (FAILED(recording_result)) {
        return recording_result;
    }
    if (!sink.is_complete()) {
        result.failure_reason =
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_DRAWING_STATE;
        return D2DERR_WRONG_STATE;
    }
    if (sink.failure_reason() !=
        PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_NONE) {
        return E_NOTIMPL;
    }

    const size_t required = sink.builder().required_stream_size();
    result.required_bytes = required;
    if (required == 0U) {
        result.failure_reason =
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_BUILDER;
        return E_FAIL;
    }
    if (destination_capacity < required) {
        return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    }

    size_t written = 0U;
    progpu::native::scene_build_metrics metrics{};
    if (!sink.builder().build_into(
            std::span<std::byte>(
                reinterpret_cast<std::byte*>(destination),
                static_cast<size_t>(destination_capacity)),
            written,
            &metrics)) {
        result.failure_reason =
            PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_BUILDER;
        return sink.builder().last_error() ==
                progpu::native::scene_build_error::out_of_memory
            ? E_OUTOFMEMORY
            : E_FAIL;
    }
    result.written_bytes = written;
    result.command_count = metrics.command_count;
    result.resource_count = metrics.resource_count;
    result.brush_count = metrics.brush_count;
    return S_OK;
}

class CallerTessellationSink final : public ID2D1TessellationSink {
public:
    CallerTessellationSink(
        progpu_native_direct2d_triangle* triangles,
        uint32_t capacity) noexcept
        : triangles_(triangles), capacity_(capacity)
    {
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(
        REFIID interface_id,
        void** value) override
    {
        if (value == nullptr) {
            return E_POINTER;
        }
        *value = nullptr;
        if (IsEqualIID(interface_id, IID_IUnknown) ||
            IsEqualIID(interface_id, __uuidof(ID2D1TessellationSink))) {
            *value = static_cast<ID2D1TessellationSink*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return reference_count_.fetch_add(1U, std::memory_order_relaxed) + 1U;
    }

    ULONG STDMETHODCALLTYPE Release() override
    {
        const ULONG remaining = reference_count_.fetch_sub(
            1U,
            std::memory_order_acq_rel) - 1U;
        if (remaining == 0U) {
            delete this;
        }
        return remaining;
    }

    void STDMETHODCALLTYPE AddTriangles(
        const D2D1_TRIANGLE* triangles,
        UINT triangle_count) noexcept override
    {
        if ((triangles == nullptr && triangle_count != 0U) || overflow_) {
            invalid_input_ = triangles == nullptr && triangle_count != 0U;
            return;
        }
        const uint64_t next_count =
            static_cast<uint64_t>(required_count_) + triangle_count;
        if (next_count > std::numeric_limits<uint32_t>::max()) {
            overflow_ = true;
            return;
        }
        const uint32_t available = required_count_ < capacity_
            ? capacity_ - required_count_
            : 0U;
        const uint32_t write_count = triangle_count < available
            ? triangle_count
            : available;
        for (uint32_t index = 0U; index < write_count; ++index) {
            progpu_native_direct2d_triangle& output =
                triangles_[required_count_ + index];
            output.point1.x = triangles[index].point1.x;
            output.point1.y = triangles[index].point1.y;
            output.point2.x = triangles[index].point2.x;
            output.point2.y = triangles[index].point2.y;
            output.point3.x = triangles[index].point3.x;
            output.point3.y = triangles[index].point3.y;
        }
        required_count_ = static_cast<uint32_t>(next_count);
    }

    HRESULT STDMETHODCALLTYPE Close() noexcept override
    {
        if (invalid_input_) {
            return E_POINTER;
        }
        return overflow_
            ? HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW)
            : S_OK;
    }

    uint32_t required_count() const noexcept
    {
        return required_count_;
    }

private:
    std::atomic<ULONG> reference_count_{1U};
    progpu_native_direct2d_triangle* triangles_ = nullptr;
    uint32_t capacity_ = 0U;
    uint32_t required_count_ = 0U;
    bool invalid_input_ = false;
    bool overflow_ = false;
};

bool luid_is_zero(const progpu_native_direct2d_surface_options& options)
{
    return options.adapter_luid_low == 0U &&
        options.adapter_luid_high == 0;
}

bool has_same_com_identity(IUnknown* left, IUnknown* right)
{
    if (left == nullptr || right == nullptr) {
        return false;
    }
    ComPtr<IUnknown> left_identity;
    ComPtr<IUnknown> right_identity;
    return SUCCEEDED(left->QueryInterface(IID_PPV_ARGS(&left_identity))) &&
        SUCCEEDED(right->QueryInterface(IID_PPV_ARGS(&right_identity))) &&
        left_identity.Get() == right_identity.Get();
}

bool is_finite(const progpu_native_direct2d_color_f& color)
{
    return std::isfinite(color.red) && std::isfinite(color.green) &&
        std::isfinite(color.blue) && std::isfinite(color.alpha);
}

bool is_finite(const progpu_native_direct2d_point_2f& point)
{
    return std::isfinite(point.x) && std::isfinite(point.y);
}

bool is_finite(const progpu_native_direct2d_matrix_3x2_f& matrix)
{
    return std::isfinite(matrix.m11) && std::isfinite(matrix.m12) &&
        std::isfinite(matrix.m21) && std::isfinite(matrix.m22) &&
        std::isfinite(matrix.m31) && std::isfinite(matrix.m32);
}

bool is_finite(const progpu_native_direct2d_matrix_4x4_f& matrix)
{
    return std::isfinite(matrix.m11) && std::isfinite(matrix.m12) &&
        std::isfinite(matrix.m13) && std::isfinite(matrix.m14) &&
        std::isfinite(matrix.m21) && std::isfinite(matrix.m22) &&
        std::isfinite(matrix.m23) && std::isfinite(matrix.m24) &&
        std::isfinite(matrix.m31) && std::isfinite(matrix.m32) &&
        std::isfinite(matrix.m33) && std::isfinite(matrix.m34) &&
        std::isfinite(matrix.m41) && std::isfinite(matrix.m42) &&
        std::isfinite(matrix.m43) && std::isfinite(matrix.m44);
}

D2D1_MATRIX_3X2_F to_native_matrix(
    const progpu_native_direct2d_matrix_3x2_f& matrix)
{
    D2D1_MATRIX_3X2_F result{};
    result._11 = matrix.m11;
    result._12 = matrix.m12;
    result._21 = matrix.m21;
    result._22 = matrix.m22;
    result._31 = matrix.m31;
    result._32 = matrix.m32;
    return result;
}

D2D1_MATRIX_4X4_F to_native_matrix(
    const progpu_native_direct2d_matrix_4x4_f& matrix)
{
    D2D1_MATRIX_4X4_F result{};
    result._11 = matrix.m11;
    result._12 = matrix.m12;
    result._13 = matrix.m13;
    result._14 = matrix.m14;
    result._21 = matrix.m21;
    result._22 = matrix.m22;
    result._23 = matrix.m23;
    result._24 = matrix.m24;
    result._31 = matrix.m31;
    result._32 = matrix.m32;
    result._33 = matrix.m33;
    result._34 = matrix.m34;
    result._41 = matrix.m41;
    result._42 = matrix.m42;
    result._43 = matrix.m43;
    result._44 = matrix.m44;
    return result;
}

bool is_valid(progpu_native_direct2d_color_space value)
{
    return value == PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_CUSTOM ||
        value == PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_SRGB ||
        value == PROGPU_NATIVE_DIRECT2D_COLOR_SPACE_SCRGB;
}

bool is_valid(progpu_native_direct2d_buffer_precision value)
{
    return value >= PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_UNKNOWN &&
        value <= PROGPU_NATIVE_DIRECT2D_BUFFER_PRECISION_32BPC_FLOAT;
}

bool is_valid(progpu_native_direct2d_extend_mode value)
{
    return value >= PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_CLAMP &&
        value <= PROGPU_NATIVE_DIRECT2D_EXTEND_MODE_MIRROR;
}

bool is_valid_interpolation_mode(uint32_t value)
{
    return value <=
        PROGPU_NATIVE_DIRECT2D_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC;
}

bool is_valid(progpu_native_direct2d_composite_mode value)
{
    return value >= PROGPU_NATIVE_DIRECT2D_COMPOSITE_MODE_SOURCE_OVER &&
        value <= PROGPU_NATIVE_DIRECT2D_COMPOSITE_MODE_MASK_INVERT;
}

bool is_valid(progpu_native_direct2d_antialias_mode value)
{
    return value == PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_PER_PRIMITIVE ||
        value == PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_ALIASED;
}

bool is_valid(progpu_native_direct2d_text_antialias_mode value)
{
    return value >= PROGPU_NATIVE_DIRECT2D_TEXT_ANTIALIAS_MODE_DEFAULT &&
        value <= PROGPU_NATIVE_DIRECT2D_TEXT_ANTIALIAS_MODE_ALIASED;
}

bool is_valid(progpu_native_direct2d_primitive_blend value)
{
    return value >= PROGPU_NATIVE_DIRECT2D_PRIMITIVE_BLEND_SOURCE_OVER &&
        value <= PROGPU_NATIVE_DIRECT2D_PRIMITIVE_BLEND_MAX;
}

bool is_valid(progpu_native_direct2d_unit_mode value)
{
    return value == PROGPU_NATIVE_DIRECT2D_UNIT_MODE_DIPS ||
        value == PROGPU_NATIVE_DIRECT2D_UNIT_MODE_PIXELS;
}

bool is_valid_layer_options(uint32_t value)
{
    constexpr uint32_t allowed =
        PROGPU_NATIVE_DIRECT2D_LAYER_OPTION_INITIALIZE_FROM_BACKGROUND |
        PROGPU_NATIVE_DIRECT2D_LAYER_OPTION_IGNORE_ALPHA;
    return (value & ~allowed) == 0U;
}

bool is_valid_text_format(
    const progpu_native_direct2d_text_format_properties& properties)
{
    return properties.struct_size == sizeof(properties) &&
        properties.font_weight >= 1U && properties.font_weight <= 999U &&
        properties.font_style <= PROGPU_NATIVE_DIRECT2D_FONT_STYLE_ITALIC &&
        properties.font_stretch >=
            PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_ULTRA_CONDENSED &&
        properties.font_stretch <=
            PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_ULTRA_EXPANDED &&
        std::isfinite(properties.font_size) && properties.font_size > 0.0F &&
        properties.text_alignment <=
            PROGPU_NATIVE_DIRECT2D_TEXT_ALIGNMENT_JUSTIFIED &&
        properties.paragraph_alignment <=
            PROGPU_NATIVE_DIRECT2D_PARAGRAPH_ALIGNMENT_CENTER &&
        properties.word_wrapping <=
            PROGPU_NATIVE_DIRECT2D_WORD_WRAPPING_CHARACTER &&
        properties.reading_direction <=
            PROGPU_NATIVE_DIRECT2D_READING_DIRECTION_BOTTOM_TO_TOP &&
        properties.flow_direction <=
            PROGPU_NATIVE_DIRECT2D_FLOW_DIRECTION_RIGHT_TO_LEFT &&
        std::isfinite(properties.incremental_tab_stop) &&
        properties.incremental_tab_stop >= 0.0F;
}

bool is_valid_font_face(
    const progpu_native_direct2d_font_face_properties& properties)
{
    return properties.struct_size == sizeof(properties) &&
        properties.font_weight >= 1U && properties.font_weight <= 999U &&
        properties.font_style <= PROGPU_NATIVE_DIRECT2D_FONT_STYLE_ITALIC &&
        properties.font_stretch >=
            PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_ULTRA_CONDENSED &&
        properties.font_stretch <=
            PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_ULTRA_EXPANDED;
}

bool is_valid_text_range_format(
    const progpu_native_direct2d_text_range_format& formatting,
    const void* drawing_effect_brush)
{
    constexpr uint32_t allowed =
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_SIZE |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_WEIGHT |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STYLE |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STRETCH |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_UNDERLINE |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_STRIKETHROUGH |
        PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_DRAWING_EFFECT;
    if (formatting.struct_size != sizeof(formatting) ||
        formatting.flags == PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_NONE ||
        (formatting.flags & ~allowed) != 0U ||
        formatting.range_length == 0U ||
        formatting.range_start > UINT32_MAX - formatting.range_length ||
        (drawing_effect_brush != nullptr &&
            (formatting.flags &
                PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_DRAWING_EFFECT) ==
                0U)) {
        return false;
    }
    if ((formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_SIZE) != 0U &&
        (!std::isfinite(formatting.font_size) ||
            formatting.font_size <= 0.0F)) {
        return false;
    }
    if ((formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_WEIGHT) != 0U &&
        (formatting.font_weight < 1U || formatting.font_weight > 999U)) {
        return false;
    }
    if ((formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STYLE) != 0U &&
        formatting.font_style > PROGPU_NATIVE_DIRECT2D_FONT_STYLE_ITALIC) {
        return false;
    }
    if ((formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STRETCH) != 0U &&
        (formatting.font_stretch <
                PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_ULTRA_CONDENSED ||
            formatting.font_stretch >
                PROGPU_NATIVE_DIRECT2D_FONT_STRETCH_ULTRA_EXPANDED)) {
        return false;
    }
    if ((formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_UNDERLINE) != 0U &&
        formatting.underline > 1U) {
        return false;
    }
    return (formatting.flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_STRIKETHROUGH) == 0U ||
        formatting.strikethrough <= 1U;
}

bool contains_null(const uint16_t* text, uint32_t length)
{
    for (uint32_t index = 0U; index < length; ++index) {
        if (text[index] == 0U) {
            return true;
        }
    }
    return false;
}

bool is_valid_draw_text_options(uint32_t value)
{
    constexpr uint32_t allowed =
        PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_NO_SNAP |
        PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_CLIP |
        PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_ENABLE_COLOR_FONT |
        PROGPU_NATIVE_DIRECT2D_DRAW_TEXT_OPTION_DISABLE_COLOR_BITMAP_SNAPPING;
    return (value & ~allowed) == 0U;
}

bool is_valid(progpu_native_direct2d_measuring_mode value)
{
    return value >= PROGPU_NATIVE_DIRECT2D_MEASURING_MODE_NATURAL &&
        value <= PROGPU_NATIVE_DIRECT2D_MEASURING_MODE_GDI_NATURAL;
}

bool is_valid(const progpu_native_direct2d_rect_f& rectangle)
{
    return std::isfinite(rectangle.x) && std::isfinite(rectangle.y) &&
        std::isfinite(rectangle.width) && std::isfinite(rectangle.height) &&
        rectangle.width >= 0.0F && rectangle.height >= 0.0F &&
        std::isfinite(rectangle.x + rectangle.width) &&
        std::isfinite(rectangle.y + rectangle.height);
}

D2D1_RECT_F to_native_rect(
    const progpu_native_direct2d_rect_f& rectangle)
{
    return D2D1::RectF(
        rectangle.x,
        rectangle.y,
        rectangle.x + rectangle.width,
        rectangle.y + rectangle.height);
}

bool try_get_copy_rectangle(
    const progpu_native_direct2d_rect_u* rectangle,
    D2D1_SIZE_U bounds,
    D2D1_RECT_U& result)
{
    if (rectangle == nullptr) {
        if (bounds.width == 0U || bounds.height == 0U) {
            return false;
        }
        result = D2D1::RectU(0U, 0U, bounds.width, bounds.height);
        return true;
    }
    const uint64_t right =
        static_cast<uint64_t>(rectangle->x) + rectangle->width;
    const uint64_t bottom =
        static_cast<uint64_t>(rectangle->y) + rectangle->height;
    if (rectangle->width == 0U || rectangle->height == 0U ||
        right > bounds.width || bottom > bounds.height) {
        return false;
    }
    result = D2D1::RectU(
        rectangle->x,
        rectangle->y,
        static_cast<uint32_t>(right),
        static_cast<uint32_t>(bottom));
    return true;
}

uint32_t bytes_per_pixel(DXGI_FORMAT format)
{
    switch (format) {
    case DXGI_FORMAT_R8_UNORM:
    case DXGI_FORMAT_A8_UNORM:
        return 1U;
    case DXGI_FORMAT_R8G8_UNORM:
    case DXGI_FORMAT_R16_UNORM:
    case DXGI_FORMAT_R16_FLOAT:
    case DXGI_FORMAT_B5G6R5_UNORM:
    case DXGI_FORMAT_B5G5R5A1_UNORM:
    case DXGI_FORMAT_B4G4R4A4_UNORM:
        return 2U;
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8X8_UNORM:
    case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB:
    case DXGI_FORMAT_R10G10B10A2_UNORM:
    case DXGI_FORMAT_R11G11B10_FLOAT:
    case DXGI_FORMAT_R16G16_UNORM:
    case DXGI_FORMAT_R16G16_FLOAT:
    case DXGI_FORMAT_R32_FLOAT:
        return 4U;
    case DXGI_FORMAT_R16G16B16A16_UNORM:
    case DXGI_FORMAT_R16G16B16A16_FLOAT:
    case DXGI_FORMAT_R32G32_FLOAT:
        return 8U;
    case DXGI_FORMAT_R32G32B32_FLOAT:
        return 12U;
    case DXGI_FORMAT_R32G32B32A32_FLOAT:
        return 16U;
    default:
        return 0U;
    }
}

HRESULT query_brush(void* brush, ComPtr<ID2D1Brush>& native_brush)
{
    return reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
}

HRESULT query_optional_stroke_style(
    void* stroke_style,
    ComPtr<ID2D1StrokeStyle>& native_stroke_style)
{
    return stroke_style == nullptr
        ? S_OK
        : reinterpret_cast<IUnknown*>(stroke_style)->QueryInterface(
              IID_PPV_ARGS(&native_stroke_style));
}

progpu_native_direct2d_status finish_draw_command(
    progpu_native_direct2d_surface& surface,
    HRESULT hr,
    int32_t& native_hresult)
{
    surface.last_hresult.store(hr, std::memory_order_release);
    native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

bool can_push_draw_scope(const progpu_native_direct2d_surface& surface)
{
    return surface.draw_scope_depth <
        progpu_direct2d_max_draw_scope_depth;
}

void push_draw_scope(
    progpu_native_direct2d_surface& surface,
    progpu_direct2d_draw_scope_kind kind)
{
    surface.draw_scopes[surface.draw_scope_depth] = kind;
    ++surface.draw_scope_depth;
}

void unwind_draw_scopes(progpu_native_direct2d_surface& surface)
{
    while (surface.draw_scope_depth != 0U) {
        --surface.draw_scope_depth;
        if (surface.draw_scopes[surface.draw_scope_depth] ==
            progpu_direct2d_draw_scope_kind::layer) {
            surface.d2d_context->PopLayer();
        } else {
            surface.d2d_context->PopAxisAlignedClip();
        }
    }
}

bool is_valid_effect_property(
    progpu_native_direct2d_effect_property_type type,
    uint32_t data_size)
{
    switch (type) {
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_BOOL:
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_UINT32:
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_INT32:
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_FLOAT:
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_ENUM:
            return data_size == sizeof(uint32_t);
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_VECTOR2:
            return data_size == sizeof(float) * 2U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_VECTOR3:
            return data_size == sizeof(float) * 3U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_VECTOR4:
            return data_size == sizeof(float) * 4U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_CLSID:
            return data_size == sizeof(progpu_native_direct2d_guid);
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_BLOB:
            return data_size != 0U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_MATRIX_3X2:
            return data_size == sizeof(float) * 6U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_MATRIX_4X3:
            return data_size == sizeof(float) * 12U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_MATRIX_4X4:
            return data_size == sizeof(float) * 16U;
        case PROGPU_NATIVE_DIRECT2D_EFFECT_PROPERTY_MATRIX_5X4:
            return data_size == sizeof(float) * 20U;
        default:
            return false;
    }
}

bool is_empty(const progpu_native_direct2d_guid& value)
{
    if (value.data1 != 0U || value.data2 != 0U || value.data3 != 0U) {
        return false;
    }
    for (uint32_t index = 0U; index < 8U; ++index) {
        if (value.data4[index] != 0U) {
            return false;
        }
    }
    return true;
}

bool is_valid(progpu_native_direct2d_color_interpolation_mode value)
{
    return value ==
            PROGPU_NATIVE_DIRECT2D_COLOR_INTERPOLATION_MODE_STRAIGHT ||
        value ==
            PROGPU_NATIVE_DIRECT2D_COLOR_INTERPOLATION_MODE_PREMULTIPLIED;
}

bool is_valid_cap_style(uint32_t value)
{
    return value <= PROGPU_NATIVE_DIRECT2D_CAP_STYLE_TRIANGLE;
}

bool is_valid_line_join(uint32_t value)
{
    return value <= PROGPU_NATIVE_DIRECT2D_LINE_JOIN_MITER_OR_BEVEL;
}

bool is_valid_dash_style(uint32_t value)
{
    return value <= PROGPU_NATIVE_DIRECT2D_DASH_STYLE_CUSTOM;
}

bool is_valid_stroke_transform_type(uint32_t value)
{
    return value <= PROGPU_NATIVE_DIRECT2D_STROKE_TRANSFORM_HAIRLINE;
}

bool is_geometry_path_segment_valid(
    const progpu_native_direct2d_path_segment& segment)
{
    if (segment.kind > static_cast<uint32_t>(
            PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_ARC) ||
        (segment.flags &
         ~(PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_FLAG_FORCE_UNSTROKED |
           PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_FLAG_FORCE_ROUND_LINE_JOIN)) !=
            0U ||
        (segment.arc_flags &
         ~(PROGPU_NATIVE_DIRECT2D_ARC_FLAG_CLOCKWISE |
           PROGPU_NATIVE_DIRECT2D_ARC_FLAG_LARGE)) != 0U ||
        !is_finite(segment.point1)) {
        return false;
    }
    switch (segment.kind) {
        case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_LINE:
            return segment.arc_flags == 0U;
        case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_QUADRATIC:
            return segment.arc_flags == 0U && is_finite(segment.point2);
        case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_CUBIC:
            return segment.arc_flags == 0U && is_finite(segment.point2) &&
                is_finite(segment.point3);
        case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_ARC:
            return is_finite(segment.size) &&
                segment.size.x >= 0.0F && segment.size.y >= 0.0F &&
                std::isfinite(segment.rotation_angle);
        default:
            return false;
    }
}

HRESULT create_path_geometry(
    progpu_native_direct2d_surface& surface,
    ComPtr<ID2D1PathGeometry1>& path)
{
    ComPtr<ID2D1PathGeometry> base_path;
    HRESULT hr = surface.d2d_factory->CreatePathGeometry(&base_path);
    if (FAILED(hr)) {
        return hr;
    }
    return base_path.As(&path);
}

GUID to_native_guid(const progpu_native_direct2d_guid& value)
{
    GUID result{};
    result.Data1 = value.data1;
    result.Data2 = value.data2;
    result.Data3 = value.data3;
    for (uint32_t index = 0U; index < 8U; ++index) {
        result.Data4[index] = value.data4[index];
    }
    return result;
}

bool luid_equals(
    const LUID& value,
    const progpu_native_direct2d_surface_options& options)
{
    return value.LowPart == options.adapter_luid_low &&
        value.HighPart == options.adapter_luid_high;
}

progpu_native_direct2d_status status_from_synchronization_hresult(HRESULT hr)
{
    if (hr == DXGI_ERROR_DEVICE_REMOVED ||
        hr == DXGI_ERROR_DEVICE_RESET ||
        hr == D2DERR_RECREATE_TARGET) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_LOST;
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_SYNCHRONIZATION_FAILED;
}

progpu_native_direct2d_status acquire_locked(
    progpu_native_direct2d_surface& surface,
    uint64_t acquire_key,
    uint32_t timeout_milliseconds)
{
    if (surface.draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE;
    }
    if (surface.access_acquired) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_ACCESS_ALREADY_ACQUIRED;
    }
    HRESULT hr = surface.keyed_mutex->AcquireSync(
        acquire_key,
        timeout_milliseconds);
    surface.last_hresult.store(hr, std::memory_order_release);
    if (FAILED(hr)) {
        return status_from_synchronization_hresult(hr);
    }
    surface.access_acquired = true;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status release_locked(
    progpu_native_direct2d_surface& surface,
    uint64_t release_key,
    bool advance_content_version)
{
    if (surface.draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE;
    }
    if (!surface.access_acquired) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_ACCESS_NOT_ACQUIRED;
    }
    HRESULT hr = surface.keyed_mutex->ReleaseSync(release_key);
    surface.last_hresult.store(hr, std::memory_order_release);
    if (FAILED(hr)) {
        return status_from_synchronization_hresult(hr);
    }
    surface.access_acquired = false;
    if (advance_content_version) {
        static_cast<void>(surface.content_version.fetch_add(
            1U,
            std::memory_order_acq_rel));
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

HRESULT select_adapter(
    const progpu_native_direct2d_surface_options& options,
    ComPtr<IDXGIAdapter1>& adapter)
{
    if (luid_is_zero(options)) {
        return S_OK;
    }

    ComPtr<IDXGIFactory1> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr)) {
        return hr;
    }

    for (UINT index = 0U;; ++index) {
        ComPtr<IDXGIAdapter1> candidate;
        hr = factory->EnumAdapters1(index, &candidate);
        if (hr == DXGI_ERROR_NOT_FOUND) {
            return DXGI_ERROR_NOT_FOUND;
        }
        if (FAILED(hr)) {
            return hr;
        }

        DXGI_ADAPTER_DESC1 descriptor{};
        hr = candidate->GetDesc1(&descriptor);
        if (FAILED(hr)) {
            return hr;
        }
        if (luid_equals(descriptor.AdapterLuid, options)) {
            adapter = std::move(candidate);
            return S_OK;
        }
    }
}

HRESULT create_d3d_device(
    const progpu_native_direct2d_surface_options& options,
    progpu_native_direct2d_surface& surface)
{
    HRESULT hr = select_adapter(options, surface.adapter);
    if (FAILED(hr)) {
        return hr;
    }

    UINT creation_flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    if ((options.flags &
         PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ENABLE_DEBUG) != 0U) {
        creation_flags |= D3D11_CREATE_DEVICE_DEBUG;
    }

    constexpr D3D_FEATURE_LEVEL feature_levels[] = {
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL_10_0
    };
    D3D_FEATURE_LEVEL selected_feature_level{};
    const bool force_warp =
        (options.flags &
         PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_FORCE_WARP) != 0U;
    D3D_DRIVER_TYPE driver_type = force_warp
        ? D3D_DRIVER_TYPE_WARP
        : (surface.adapter ? D3D_DRIVER_TYPE_UNKNOWN
                           : D3D_DRIVER_TYPE_HARDWARE);
    IDXGIAdapter* selected_adapter = surface.adapter.Get();
    hr = D3D11CreateDevice(
        selected_adapter,
        driver_type,
        nullptr,
        creation_flags,
        feature_levels,
        static_cast<UINT>(sizeof(feature_levels) / sizeof(feature_levels[0])),
        D3D11_SDK_VERSION,
        surface.d3d_device.GetAddressOf(),
        &selected_feature_level,
        surface.d3d_context.GetAddressOf());

    if (FAILED(hr) && !force_warp && !surface.adapter &&
        (options.flags &
         PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ALLOW_WARP_FALLBACK) != 0U) {
        surface.d3d_device.Reset();
        surface.d3d_context.Reset();
        hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            creation_flags & ~D3D11_CREATE_DEVICE_DEBUG,
            feature_levels,
            static_cast<UINT>(sizeof(feature_levels) / sizeof(feature_levels[0])),
            D3D11_SDK_VERSION,
            surface.d3d_device.GetAddressOf(),
            &selected_feature_level,
            surface.d3d_context.GetAddressOf());
    }
    if (FAILED(hr)) {
        return hr;
    }

    if (SUCCEEDED(surface.d3d_device.As(&surface.d3d_device4))) {
        surface.device_removed_event = CreateEventW(
            nullptr,
            TRUE,
            FALSE,
            nullptr);
        if (surface.device_removed_event != nullptr) {
            DWORD cookie = 0U;
            HRESULT registration_hr =
                surface.d3d_device4->RegisterDeviceRemovedEvent(
                    surface.device_removed_event,
                    &cookie);
            if (SUCCEEDED(registration_hr)) {
                surface.device_removed_cookie = cookie;
                surface.device_removed_registered = true;
            }
            else {
                static_cast<void>(CloseHandle(
                    surface.device_removed_event));
                surface.device_removed_event = nullptr;
            }
        }
    }

    hr = surface.d3d_device.As(&surface.dxgi_device);
    if (FAILED(hr)) {
        return hr;
    }
    ComPtr<IDXGIAdapter> actual_adapter;
    hr = surface.dxgi_device->GetAdapter(&actual_adapter);
    if (FAILED(hr)) {
        return hr;
    }
    hr = actual_adapter.As(&surface.adapter);
    if (FAILED(hr)) {
        return hr;
    }
    hr = surface.adapter->GetDesc1(&surface.adapter_descriptor);
    if (FAILED(hr)) {
        return hr;
    }
    surface.software_adapter =
        (surface.adapter_descriptor.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0U;
    return S_OK;
}

HRESULT create_direct2d_resources(
    const progpu_native_direct2d_surface_options& options,
    progpu_native_direct2d_surface& surface)
{
    D2D1_FACTORY_OPTIONS factory_options{};
    if ((options.flags &
         PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ENABLE_DEBUG) != 0U) {
        factory_options.debugLevel = D2D1_DEBUG_LEVEL_INFORMATION;
    }
    HRESULT hr = D2D1CreateFactory(
        D2D1_FACTORY_TYPE_MULTI_THREADED,
        __uuidof(ID2D1Factory2),
        &factory_options,
        reinterpret_cast<void**>(surface.d2d_factory.GetAddressOf()));
    if (FAILED(hr)) {
        return hr;
    }
    hr = DWriteCreateFactory(
        DWRITE_FACTORY_TYPE_SHARED,
        __uuidof(IDWriteFactory3),
        reinterpret_cast<IUnknown**>(
            surface.dwrite_factory.GetAddressOf()));
    if (FAILED(hr)) {
        return hr;
    }
    hr = surface.d2d_factory->CreateDevice(
        surface.dxgi_device.Get(),
        &surface.d2d_device);
    if (FAILED(hr)) {
        return hr;
    }
    hr = surface.d2d_device->CreateDeviceContext(
        D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
        &surface.d2d_context);
    if (FAILED(hr)) {
        return hr;
    }

    D3D11_TEXTURE2D_DESC texture_descriptor{};
    texture_descriptor.Width = options.width;
    texture_descriptor.Height = options.height;
    texture_descriptor.MipLevels = 1U;
    texture_descriptor.ArraySize = 1U;
    texture_descriptor.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    texture_descriptor.SampleDesc.Count = 1U;
    texture_descriptor.Usage = D3D11_USAGE_DEFAULT;
    texture_descriptor.BindFlags =
        D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    texture_descriptor.MiscFlags =
        D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX |
        D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    hr = surface.d3d_device->CreateTexture2D(
        &texture_descriptor,
        nullptr,
        &surface.texture);
    if (FAILED(hr)) {
        return hr;
    }
    hr = surface.texture.As(&surface.dxgi_surface);
    if (FAILED(hr)) {
        return hr;
    }
    hr = surface.texture.As(&surface.keyed_mutex);
    if (FAILED(hr)) {
        return hr;
    }

    ComPtr<IDXGIResource1> resource;
    hr = surface.texture.As(&resource);
    if (FAILED(hr)) {
        return hr;
    }
    hr = resource->CreateSharedHandle(
        nullptr,
        DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
        nullptr,
        &surface.shared_handle);
    if (FAILED(hr)) {
        return hr;
    }

    D2D1_BITMAP_PROPERTIES1 bitmap_properties{};
    bitmap_properties.pixelFormat = {
        DXGI_FORMAT_B8G8R8A8_UNORM,
        D2D1_ALPHA_MODE_PREMULTIPLIED
    };
    bitmap_properties.dpiX = surface.dpi_x;
    bitmap_properties.dpiY = surface.dpi_y;
    bitmap_properties.bitmapOptions =
        D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW;
    hr = surface.d2d_context->CreateBitmapFromDxgiSurface(
        surface.dxgi_surface.Get(),
        &bitmap_properties,
        &surface.d2d_bitmap);
    if (FAILED(hr)) {
        return hr;
    }
    surface.d2d_context->SetTarget(surface.d2d_bitmap.Get());
    surface.d2d_context->SetDpi(surface.dpi_x, surface.dpi_y);
    return S_OK;
}

HRESULT create_winrt_direct3d_device(
    progpu_native_direct2d_surface& surface)
{
    return CreateDirect3D11DeviceFromDXGIDevice(
        surface.dxgi_device.Get(),
        surface.winrt_d3d_device.GetAddressOf());
}

HRESULT get_win2d_factory(
    progpu_native_direct2d_surface& surface)
{
    if (surface.win2d_factory) {
        return S_OK;
    }
    constexpr wchar_t runtime_class_name[] =
        L"Microsoft.Graphics.Canvas.CanvasDevice";
    HSTRING class_name = nullptr;
    HRESULT hr = WindowsCreateString(
        runtime_class_name,
        static_cast<UINT32>(
            sizeof(runtime_class_name) / sizeof(runtime_class_name[0]) - 1U),
        &class_name);
    if (FAILED(hr)) {
        return hr;
    }

    hr = RoGetActivationFactory(
        class_name,
        __uuidof(IProGpuWin2DCanvasFactoryNative),
        reinterpret_cast<void**>(surface.win2d_factory.GetAddressOf()));
    static_cast<void>(WindowsDeleteString(class_name));
    if (FAILED(hr)) {
        surface.win2d_factory.Reset();
    }
    return hr;
}

HRESULT create_win2d_canvas_device(
    progpu_native_direct2d_surface& surface)
{
    if (surface.win2d_canvas_device) {
        return S_OK;
    }
    HRESULT hr = get_win2d_factory(surface);
    if (FAILED(hr)) {
        return hr;
    }
    return surface.win2d_factory->GetOrCreate(
        nullptr,
        surface.d2d_device.Get(),
        0.0F,
        surface.win2d_canvas_device.GetAddressOf());
}

HRESULT create_win2d_canvas_render_target(
    progpu_native_direct2d_surface& surface)
{
    HRESULT hr = create_win2d_canvas_device(surface);
    if (FAILED(hr)) {
        return hr;
    }
    ComPtr<IProGpuWin2DCanvasDevice> canvas_device;
    hr = surface.win2d_canvas_device.As(&canvas_device);
    if (FAILED(hr)) {
        return hr;
    }
    return surface.win2d_factory->GetOrCreate(
        canvas_device.Get(),
        surface.d2d_bitmap.Get(),
        surface.dpi_x,
        surface.win2d_canvas_render_target.GetAddressOf());
}

HRESULT create_win2d_wrapper(
    progpu_native_direct2d_surface& surface,
    IUnknown* native_resource,
    float dpi,
    IInspectable** wrapper)
{
    ComPtr<IDWriteTextLayout> text_layout;
    ComPtr<IDWriteTextFormat> text_format;
    ComPtr<IDWriteTypography> typography;
    ComPtr<IDWriteFontFaceReference> font_face_reference;
    bool is_text_layout = SUCCEEDED(
        native_resource->QueryInterface(IID_PPV_ARGS(&text_layout)));
    bool device_independent = !is_text_layout &&
        (SUCCEEDED(native_resource->QueryInterface(IID_PPV_ARGS(&text_format))) ||
         SUCCEEDED(native_resource->QueryInterface(IID_PPV_ARGS(&typography))) ||
         SUCCEEDED(native_resource->QueryInterface(
             IID_PPV_ARGS(&font_face_reference))));
    HRESULT hr = device_independent
        ? get_win2d_factory(surface)
        : create_win2d_canvas_device(surface);
    if (FAILED(hr)) {
        return hr;
    }
    ComPtr<IProGpuWin2DCanvasDevice> canvas_device;
    if (!device_independent) {
        hr = surface.win2d_canvas_device.As(&canvas_device);
        if (FAILED(hr)) {
            return hr;
        }
    }
    return surface.win2d_factory->GetOrCreate(
        device_independent ? nullptr : canvas_device.Get(),
        native_resource,
        device_independent ? 0.0F : dpi,
        wrapper);
}

HRESULT get_win2d_wrapper_native_resource(
    progpu_native_direct2d_surface& surface,
    IUnknown* wrapper,
    float dpi,
    REFIID interface_id,
    void** native_resource)
{
    bool device_independent =
        IsEqualIID(interface_id, __uuidof(IDWriteTextFormat)) ||
        IsEqualIID(interface_id, __uuidof(IDWriteTextFormat1)) ||
        IsEqualIID(interface_id, __uuidof(IDWriteTypography)) ||
        IsEqualIID(interface_id, __uuidof(IDWriteFontFaceReference));
    HRESULT hr = device_independent
        ? get_win2d_factory(surface)
        : create_win2d_canvas_device(surface);
    if (FAILED(hr)) {
        return hr;
    }
    ComPtr<IProGpuWin2DCanvasDevice> canvas_device;
    if (!device_independent) {
        hr = surface.win2d_canvas_device.As(&canvas_device);
        if (FAILED(hr)) {
            return hr;
        }
    }
    ComPtr<IProGpuWin2DCanvasResourceWrapperNative> resource_wrapper;
    hr = wrapper->QueryInterface(IID_PPV_ARGS(&resource_wrapper));
    if (FAILED(hr)) {
        return hr;
    }
    hr = resource_wrapper->GetNativeResource(
        device_independent ? nullptr : canvas_device.Get(),
        device_independent ? 0.0F : dpi,
        interface_id,
        native_resource);
    if (SUCCEEDED(hr) && *native_resource == nullptr) {
        return E_UNEXPECTED;
    }
    return hr;
}

progpu_native_direct2d_status status_from_win2d_hresult(HRESULT hr)
{
    if (hr == CO_E_NOTINITIALIZED) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_WINDOWS_RUNTIME_NOT_INITIALIZED;
    }
    if (hr == REGDB_E_CLASSNOTREG ||
        hr == HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND) ||
        hr == HRESULT_FROM_WIN32(ERROR_DLL_NOT_FOUND)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_WIN2D_RUNTIME_UNAVAILABLE;
    }
    if (hr == E_NOINTERFACE) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED;
    }
    if (hr == DXGI_ERROR_DEVICE_REMOVED ||
        hr == DXGI_ERROR_DEVICE_RESET ||
        hr == D2DERR_RECREATE_TARGET) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_LOST;
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
}

progpu_native_direct2d_status status_from_scene_stream_hresult(HRESULT hr)
{
    if (SUCCEEDED(hr)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
    }
    if (hr == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER;
    }
    if (hr == E_NOTIMPL || hr == E_NOINTERFACE) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED;
    }
    if (hr == D2DERR_WRONG_STATE) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    if (hr == E_INVALIDARG || hr == E_POINTER) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    if (hr == E_OUTOFMEMORY) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY;
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
}

template<typename T>
progpu_native_direct2d_status return_interface(
    const ComPtr<T>& source,
    void** value)
{
    if (!source) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
    }
    source->AddRef();
    *value = source.Get();
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

} // namespace

extern "C" {

uint32_t progpu_native_direct2d_get_abi_version(void)
{
    return PROGPU_NATIVE_DIRECT2D_ABI_VERSION;
}

progpu_native_direct2d_status
progpu_native_direct2d_compat_factory_create(
    void** factory,
    int32_t* native_hresult)
{
    if (factory != nullptr) {
        *factory = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (factory == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    auto* instance = new (std::nothrow) ProGpuD2DFactory();
    if (instance == nullptr) {
        *native_hresult = E_OUTOFMEMORY;
        return PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY;
    }
    *factory = static_cast<ID2D1Factory1*>(instance);
    *native_hresult = S_OK;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_compat_factory_create_solid_color_brush(
    void* factory,
    const progpu_native_direct2d_color_f* color,
    const progpu_native_direct2d_brush_properties* properties,
    void** brush,
    int32_t* native_hresult)
{
    if (brush != nullptr) {
        *brush = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (factory == nullptr || color == nullptr || brush == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    auto* factory1 = static_cast<ID2D1Factory1*>(factory);
    ComPtr<IProGpuD2DCompatFactoryNative> native_factory;
    HRESULT hr = factory1->QueryInterface(IID_PPV_ARGS(&native_factory));
    if (FAILED(hr)) {
        *native_hresult = hr;
        return status_from_scene_stream_hresult(hr);
    }
    D2D1_BRUSH_PROPERTIES native_properties{};
    const D2D1_BRUSH_PROPERTIES* native_properties_pointer = nullptr;
    if (properties != nullptr) {
        native_properties.opacity = properties->opacity;
        native_properties.transform._11 = properties->transform.m11;
        native_properties.transform._12 = properties->transform.m12;
        native_properties.transform._21 = properties->transform.m21;
        native_properties.transform._22 = properties->transform.m22;
        native_properties.transform._31 = properties->transform.m31;
        native_properties.transform._32 = properties->transform.m32;
        native_properties_pointer = &native_properties;
    }
    ComPtr<ID2D1SolidColorBrush> native_brush;
    const D2D1_COLOR_F native_color = {
        color->red,
        color->green,
        color->blue,
        color->alpha};
    hr = native_factory->CreateSolidColorBrush(
        &native_color,
        native_properties_pointer,
        &native_brush);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_scene_stream_hresult(hr);
    }
    native_brush->AddRef();
    *brush = native_brush.Get();
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_scene_recorder_create(
    uint64_t scene_id,
    uint64_t generation,
    const progpu_native_direct2d_command_stream_summary* capacity_hint,
    progpu_native_direct2d_scene_recorder** recorder,
    int32_t* native_hresult)
{
    if (recorder != nullptr) {
        *recorder = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (scene_id == 0U || generation == 0U || recorder == nullptr ||
        native_hresult == nullptr ||
        (capacity_hint != nullptr &&
            capacity_hint->struct_size != sizeof(*capacity_hint))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    progpu_native_direct2d_command_stream_summary hint{};
    hint.struct_size = static_cast<uint32_t>(sizeof(hint));
    if (capacity_hint != nullptr) {
        hint = *capacity_hint;
    }

    CommandSceneStreamSink* sink = nullptr;
    try {
        sink = new CommandSceneStreamSink(scene_id, generation, hint);
    } catch (const std::bad_alloc&) {
        *native_hresult = E_OUTOFMEMORY;
        return PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY;
    } catch (...) {
        *native_hresult = E_FAIL;
        return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
    }
    if (sink == nullptr) {
        *native_hresult = E_OUTOFMEMORY;
        return PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY;
    }

    auto instance =
        new (std::nothrow) progpu_native_direct2d_scene_recorder();
    if (instance == nullptr) {
        sink->Release();
        *native_hresult = E_OUTOFMEMORY;
        return PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY;
    }
    instance->command_sink = sink;
    instance->scene_id = scene_id;
    instance->generation = generation;
    *recorder = instance;
    *native_hresult = S_OK;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

void progpu_native_direct2d_scene_recorder_destroy(
    progpu_native_direct2d_scene_recorder* recorder)
{
    if (recorder == nullptr) {
        return;
    }
    auto* sink = static_cast<CommandSceneStreamSink*>(
        recorder->command_sink);
    recorder->command_sink = nullptr;
    if (sink != nullptr) {
        sink->Release();
    }
    delete recorder;
}

progpu_native_direct2d_status
progpu_native_direct2d_scene_recorder_get_command_sink(
    progpu_native_direct2d_scene_recorder* recorder,
    void** command_sink,
    int32_t* native_hresult)
{
    if (command_sink != nullptr) {
        *command_sink = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (recorder == nullptr || command_sink == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(recorder->access_mutex);
    auto* sink = static_cast<CommandSceneStreamSink*>(
        recorder->command_sink);
    if (sink == nullptr) {
        *native_hresult = D2DERR_WRONG_STATE;
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    sink->AddRef();
    *command_sink = static_cast<ID2D1CommandSink1*>(sink);
    *native_hresult = S_OK;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_scene_recorder_build_stream(
    progpu_native_direct2d_scene_recorder* recorder,
    uint8_t* destination,
    uint64_t destination_capacity,
    progpu_native_direct2d_scene_stream_result* result,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (recorder == nullptr || result == nullptr ||
        result->struct_size != sizeof(*result) || native_hresult == nullptr ||
        (destination == nullptr) != (destination_capacity == 0U) ||
        destination_capacity > std::numeric_limits<size_t>::max()) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(recorder->access_mutex);
    auto* sink = static_cast<CommandSceneStreamSink*>(
        recorder->command_sink);
    if (sink == nullptr) {
        *native_hresult = D2DERR_WRONG_STATE;
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    const HRESULT hr = build_scene_stream(
        *sink,
        recorder->scene_id,
        recorder->generation,
        destination,
        destination_capacity,
        *result,
        S_OK);
    *native_hresult = hr;
    return status_from_scene_stream_hresult(hr);
}

progpu_native_direct2d_status progpu_native_direct2d_surface_create(
    const progpu_native_direct2d_surface_options* options,
    progpu_native_direct2d_surface** surface,
    int32_t* native_hresult)
{
    if (surface != nullptr) {
        *surface = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (options == nullptr || surface == nullptr ||
        options->struct_size != sizeof(*options) ||
        options->width == 0U || options->height == 0U ||
        options->width > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
        options->height > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION ||
        !(options->dpi_x > 0.0F) || !(options->dpi_y > 0.0F) ||
        (options->flags &
         ~(PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ENABLE_DEBUG |
           PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_ALLOW_WARP_FALLBACK |
           PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_FORCE_WARP)) != 0U ||
        ((options->flags & PROGPU_NATIVE_DIRECT2D_SURFACE_FLAG_FORCE_WARP) != 0U &&
         !luid_is_zero(*options))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    auto instance =
        new (std::nothrow) progpu_native_direct2d_surface();
    if (instance == nullptr) {
        if (native_hresult != nullptr) {
            *native_hresult = E_OUTOFMEMORY;
        }
        return PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY;
    }
    instance->width = options->width;
    instance->height = options->height;
    instance->dpi_x = options->dpi_x;
    instance->dpi_y = options->dpi_y;
    instance->resource_generation = allocate_resource_generation();

    HRESULT hr = create_d3d_device(*options, *instance);
    if (FAILED(hr)) {
        if (native_hresult != nullptr) {
            *native_hresult = hr;
        }
        delete instance;
        return hr == DXGI_ERROR_NOT_FOUND
            ? PROGPU_NATIVE_DIRECT2D_STATUS_ADAPTER_NOT_FOUND
            : PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_CREATION_FAILED;
    }
    hr = create_direct2d_resources(*options, *instance);
    if (FAILED(hr)) {
        if (native_hresult != nullptr) {
            *native_hresult = hr;
        }
        delete instance;
        return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
    }
    hr = create_winrt_direct3d_device(*instance);
    if (FAILED(hr)) {
        if (native_hresult != nullptr) {
            *native_hresult = hr;
        }
        delete instance;
        return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
    }

    if (native_hresult != nullptr) {
        *native_hresult = S_OK;
    }
    *surface = instance;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

void progpu_native_direct2d_surface_destroy(
    progpu_native_direct2d_surface* surface)
{
    delete surface;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_get_descriptor(
    const progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_surface_descriptor* descriptor)
{
    if (surface == nullptr || descriptor == nullptr ||
        descriptor->struct_size != sizeof(*descriptor)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    descriptor->flags =
        PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_KEYED_MUTEX |
        PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_NT_HANDLE |
        (surface->software_adapter
             ? PROGPU_NATIVE_DIRECT2D_DESCRIPTOR_FLAG_SOFTWARE_ADAPTER
             : 0U);
    descriptor->width = surface->width;
    descriptor->height = surface->height;
    descriptor->dpi_x = surface->dpi_x;
    descriptor->dpi_y = surface->dpi_y;
    descriptor->dxgi_format = dxgi_format_b8g8r8a8_unorm;
    descriptor->alpha_mode = d2d_alpha_mode_premultiplied;
    descriptor->adapter_luid_low =
        surface->adapter_descriptor.AdapterLuid.LowPart;
    descriptor->adapter_luid_high =
        surface->adapter_descriptor.AdapterLuid.HighPart;
    descriptor->shared_nt_handle =
        reinterpret_cast<uintptr_t>(surface->shared_handle);
    descriptor->initial_acquire_key = 0U;
    descriptor->initial_release_key = 0U;
    descriptor->content_version =
        surface->content_version.load(std::memory_order_acquire);
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_get_device_loss_state(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_device_loss_state* state)
{
    if (surface == nullptr || state == nullptr ||
        state->struct_size != sizeof(*state)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    uint32_t flags = PROGPU_NATIVE_DIRECT2D_DEVICE_LOSS_FLAG_NONE;
    bool query_removal_reason = true;
    if (surface->device_removed_registered &&
        surface->device_removed_event != nullptr) {
        query_removal_reason = false;
        flags |=
            PROGPU_NATIVE_DIRECT2D_DEVICE_LOSS_FLAG_REMOVAL_EVENT_REGISTERED;
        if (WaitForSingleObject(surface->device_removed_event, 0U) ==
            WAIT_OBJECT_0) {
            flags |=
                PROGPU_NATIVE_DIRECT2D_DEVICE_LOSS_FLAG_REMOVAL_EVENT_SIGNALED;
            query_removal_reason = true;
        }
    }

    retain_device_loss(
        *surface,
        surface->last_hresult.load(std::memory_order_acquire));
    if (query_removal_reason) {
        HRESULT removal_hr = surface->d3d_device->GetDeviceRemovedReason();
        retain_device_loss(*surface, removal_hr);
    }
    HRESULT retained_hr = surface->device_loss_hresult.load(
        std::memory_order_acquire);
    if (FAILED(retained_hr)) {
        flags |= PROGPU_NATIVE_DIRECT2D_DEVICE_LOSS_FLAG_DEVICE_LOST;
    }

    state->flags = flags;
    state->reason_hresult = retained_hr;
    state->reserved = 0U;
    state->resource_generation = surface->resource_generation;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status progpu_native_direct2d_surface_get_interface(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_interface_kind kind,
    void** value)
{
    if (surface == nullptr || value == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    *value = nullptr;
    switch (kind) {
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D3D11_DEVICE:
            return return_interface(surface->d3d_device, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D3D11_DEVICE_CONTEXT:
            return return_interface(surface->d3d_context, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_ADAPTER1:
            return return_interface(surface->adapter, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_DEVICE:
            return return_interface(surface->dxgi_device, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_SURFACE:
            return return_interface(surface->dxgi_surface, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_DXGI_KEYED_MUTEX:
            return return_interface(surface->keyed_mutex, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D3D11_TEXTURE2D:
            return return_interface(surface->texture, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_FACTORY1: {
            ComPtr<ID2D1Factory1> result;
            if (FAILED(surface->d2d_factory.As(&result))) {
                return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
            }
            return return_interface(result, value);
        }
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_FACTORY2:
            return return_interface(surface->d2d_factory, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE: {
            ComPtr<ID2D1Device> result;
            if (FAILED(surface->d2d_device.As(&result))) {
                return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
            }
            return return_interface(result, value);
        }
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE1:
            return return_interface(surface->d2d_device, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT: {
            ComPtr<ID2D1DeviceContext> result;
            if (FAILED(surface->d2d_context.As(&result))) {
                return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
            }
            return return_interface(result, value);
        }
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT1:
            return return_interface(surface->d2d_context, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT4: {
            ComPtr<ID2D1DeviceContext4> result;
            if (FAILED(surface->d2d_context.As(&result))) {
                return PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED;
            }
            return return_interface(result, value);
        }
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT5: {
            ComPtr<ID2D1DeviceContext5> result;
            if (FAILED(surface->d2d_context.As(&result))) {
                return PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED;
            }
            return return_interface(result, value);
        }
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_DEVICE_CONTEXT7: {
            ComPtr<ID2D1DeviceContext7> result;
            if (FAILED(surface->d2d_context.As(&result))) {
                return PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED;
            }
            return return_interface(result, value);
        }
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_BITMAP: {
            ComPtr<ID2D1Bitmap> result;
            if (FAILED(surface->d2d_bitmap.As(&result))) {
                return PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
            }
            return return_interface(result, value);
        }
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_D2D1_BITMAP1:
            return return_interface(surface->d2d_bitmap, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_WINRT_DIRECT3D11_DEVICE:
            return return_interface(surface->winrt_d3d_device, value);
        case PROGPU_NATIVE_DIRECT2D_INTERFACE_DWRITE_FACTORY3:
            return return_interface(surface->dwrite_factory, value);
        default:
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
}

uint32_t progpu_native_direct2d_com_release(void* value)
{
    return value == nullptr
        ? 0U
        : reinterpret_cast<IUnknown*>(value)->Release();
}

progpu_native_direct2d_status progpu_native_direct2d_com_query_interface(
    void* value,
    const progpu_native_direct2d_guid* interface_id,
    void** result,
    int32_t* native_hresult)
{
    if (result != nullptr) {
        *result = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (value == nullptr || interface_id == nullptr || result == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    GUID native_interface_id = to_native_guid(*interface_id);
    HRESULT hr = reinterpret_cast<IUnknown*>(value)->QueryInterface(
        native_interface_id,
        result);
    *native_hresult = hr;
    if (SUCCEEDED(hr)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
    }
    *result = nullptr;
    return hr == E_NOINTERFACE
        ? PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED
        : PROGPU_NATIVE_DIRECT2D_STATUS_RESOURCE_CREATION_FAILED;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_canvas_device(
    progpu_native_direct2d_surface* surface,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || value == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    HRESULT hr = S_OK;
    if (!surface->win2d_canvas_device) {
        hr = create_win2d_canvas_device(*surface);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        surface->win2d_canvas_device.Reset();
        return status_from_win2d_hresult(hr);
    }
    return return_interface(surface->win2d_canvas_device, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_canvas_render_target(
    progpu_native_direct2d_surface* surface,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || value == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    HRESULT hr = S_OK;
    if (!surface->win2d_canvas_render_target) {
        hr = create_win2d_canvas_render_target(*surface);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        surface->win2d_canvas_render_target.Reset();
        return status_from_win2d_hresult(hr);
    }
    return return_interface(surface->win2d_canvas_render_target, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_native_resource(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_win2d_resource_kind resource_kind,
    const progpu_native_direct2d_guid* interface_id,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || interface_id == nullptr || value == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    HRESULT hr = S_OK;
    ComPtr<IInspectable> wrapper;
    ComPtr<IProGpuWin2DCanvasDevice> canvas_device;
    float dpi = 0.0F;
    switch (resource_kind) {
        case PROGPU_NATIVE_DIRECT2D_WIN2D_RESOURCE_CANVAS_DEVICE:
            hr = create_win2d_canvas_device(*surface);
            if (SUCCEEDED(hr)) {
                wrapper = surface->win2d_canvas_device;
            }
            break;
        case PROGPU_NATIVE_DIRECT2D_WIN2D_RESOURCE_CANVAS_RENDER_TARGET:
            hr = create_win2d_canvas_render_target(*surface);
            if (SUCCEEDED(hr)) {
                wrapper = surface->win2d_canvas_render_target;
                hr = surface->win2d_canvas_device.As(&canvas_device);
                dpi = surface->dpi_x;
            }
            break;
        default:
            surface->last_hresult.store(
                E_INVALIDARG,
                std::memory_order_release);
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    ComPtr<IProGpuWin2DCanvasResourceWrapperNative> resource_wrapper;
    if (SUCCEEDED(hr)) {
        hr = wrapper.As(&resource_wrapper);
    }
    if (SUCCEEDED(hr)) {
        GUID native_interface_id = to_native_guid(*interface_id);
        hr = resource_wrapper->GetNativeResource(
            canvas_device.Get(),
            dpi,
            native_interface_id,
            value);
        if (SUCCEEDED(hr) && *value == nullptr) {
            hr = E_UNEXPECTED;
        }
    }

    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        *value = nullptr;
        return status_from_win2d_hresult(hr);
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_solid_color_brush(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_color_f* color,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || color == nullptr || value == nullptr ||
        native_hresult == nullptr || !is_finite(*color)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    D2D1_COLOR_F native_color = {
        color->red,
        color->green,
        color->blue,
        color->alpha
    };
    ComPtr<ID2D1SolidColorBrush> brush;
    HRESULT hr = surface->d2d_context->CreateSolidColorBrush(
        native_color,
        brush.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(brush, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_brush_set_properties(
    progpu_native_direct2d_surface* surface,
    void* brush,
    const progpu_native_direct2d_brush_properties* properties,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || properties == nullptr ||
        native_hresult == nullptr || !std::isfinite(properties->opacity) ||
        properties->opacity < 0.0F || properties->opacity > 1.0F ||
        !is_finite(properties->transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Brush> native_brush;
    HRESULT hr = query_brush(brush, native_brush);
    if (SUCCEEDED(hr)) {
        native_brush->SetOpacity(properties->opacity);
        native_brush->SetTransform(to_native_matrix(properties->transform));
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_brush_get_properties(
    progpu_native_direct2d_surface* surface,
    void* brush,
    progpu_native_direct2d_brush_properties* properties,
    int32_t* native_hresult)
{
    if (properties != nullptr) {
        *properties = {};
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || properties == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Brush> native_brush;
    HRESULT hr = query_brush(brush, native_brush);
    if (SUCCEEDED(hr)) {
        properties->opacity = native_brush->GetOpacity();
        D2D1_MATRIX_3X2_F transform{};
        native_brush->GetTransform(&transform);
        properties->transform = {
            transform._11, transform._12,
            transform._21, transform._22,
            transform._31, transform._32
        };
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_solid_color_brush_set_color(
    progpu_native_direct2d_surface* surface,
    void* brush,
    const progpu_native_direct2d_color_f* color,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || color == nullptr ||
        native_hresult == nullptr || !is_finite(*color)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1SolidColorBrush> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        native_brush->SetColor(D2D1::ColorF(
            color->red, color->green, color->blue, color->alpha));
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_solid_color_brush_get_color(
    progpu_native_direct2d_surface* surface,
    void* brush,
    progpu_native_direct2d_color_f* color,
    int32_t* native_hresult)
{
    if (color != nullptr) {
        *color = {};
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || color == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1SolidColorBrush> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        const D2D1_COLOR_F value = native_brush->GetColor();
        *color = {value.r, value.g, value.b, value.a};
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_linear_gradient_brush_set_properties(
    progpu_native_direct2d_surface* surface,
    void* brush,
    const progpu_native_direct2d_linear_gradient_brush_properties* properties,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || properties == nullptr ||
        native_hresult == nullptr || !is_finite(properties->start_point) ||
        !is_finite(properties->end_point)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1LinearGradientBrush> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        native_brush->SetStartPoint(D2D1::Point2F(
            properties->start_point.x, properties->start_point.y));
        native_brush->SetEndPoint(D2D1::Point2F(
            properties->end_point.x, properties->end_point.y));
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_linear_gradient_brush_get_properties(
    progpu_native_direct2d_surface* surface,
    void* brush,
    progpu_native_direct2d_linear_gradient_brush_properties* properties,
    int32_t* native_hresult)
{
    if (properties != nullptr) {
        *properties = {};
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || properties == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1LinearGradientBrush> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        const D2D1_POINT_2F start = native_brush->GetStartPoint();
        const D2D1_POINT_2F end = native_brush->GetEndPoint();
        properties->start_point = {start.x, start.y};
        properties->end_point = {end.x, end.y};
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_radial_gradient_brush_set_properties(
    progpu_native_direct2d_surface* surface,
    void* brush,
    const progpu_native_direct2d_radial_gradient_brush_properties* properties,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || properties == nullptr ||
        native_hresult == nullptr || !is_finite(properties->center) ||
        !is_finite(properties->gradient_origin_offset) ||
        !std::isfinite(properties->radius_x) || properties->radius_x < 0.0F ||
        !std::isfinite(properties->radius_y) || properties->radius_y < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1RadialGradientBrush> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        native_brush->SetCenter(D2D1::Point2F(
            properties->center.x, properties->center.y));
        native_brush->SetGradientOriginOffset(D2D1::Point2F(
            properties->gradient_origin_offset.x,
            properties->gradient_origin_offset.y));
        native_brush->SetRadiusX(properties->radius_x);
        native_brush->SetRadiusY(properties->radius_y);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_radial_gradient_brush_get_properties(
    progpu_native_direct2d_surface* surface,
    void* brush,
    progpu_native_direct2d_radial_gradient_brush_properties* properties,
    int32_t* native_hresult)
{
    if (properties != nullptr) {
        *properties = {};
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || properties == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1RadialGradientBrush> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        const D2D1_POINT_2F center = native_brush->GetCenter();
        const D2D1_POINT_2F offset = native_brush->GetGradientOriginOffset();
        properties->center = {center.x, center.y};
        properties->gradient_origin_offset = {offset.x, offset.y};
        properties->radius_x = native_brush->GetRadiusX();
        properties->radius_y = native_brush->GetRadiusY();
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_gradient_stop_collection(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_gradient_stop* stops,
    uint32_t stop_count,
    progpu_native_direct2d_color_space pre_interpolation_space,
    progpu_native_direct2d_color_space post_interpolation_space,
    progpu_native_direct2d_buffer_precision buffer_precision,
    progpu_native_direct2d_extend_mode extend_mode,
    progpu_native_direct2d_color_interpolation_mode interpolation_mode,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || stops == nullptr || stop_count == 0U ||
        value == nullptr || native_hresult == nullptr ||
        !is_valid(pre_interpolation_space) ||
        !is_valid(post_interpolation_space) ||
        !is_valid(buffer_precision) || !is_valid(extend_mode) ||
        !is_valid(interpolation_mode)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    for (uint32_t index = 0U; index < stop_count; ++index) {
        if (!std::isfinite(stops[index].position) ||
            !is_finite(stops[index].color)) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1GradientStopCollection1> collection;
    HRESULT hr = surface->d2d_context->CreateGradientStopCollection(
        reinterpret_cast<const D2D1_GRADIENT_STOP*>(stops),
        stop_count,
        static_cast<D2D1_COLOR_SPACE>(pre_interpolation_space),
        static_cast<D2D1_COLOR_SPACE>(post_interpolation_space),
        static_cast<D2D1_BUFFER_PRECISION>(buffer_precision),
        static_cast<D2D1_EXTEND_MODE>(extend_mode),
        static_cast<D2D1_COLOR_INTERPOLATION_MODE>(interpolation_mode),
        collection.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(collection, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_linear_gradient_brush(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_linear_gradient_brush_properties* properties,
    const progpu_native_direct2d_brush_properties* brush_properties,
    void* gradient_stop_collection,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || properties == nullptr ||
        brush_properties == nullptr || gradient_stop_collection == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        !is_finite(properties->start_point) ||
        !is_finite(properties->end_point) ||
        !std::isfinite(brush_properties->opacity) ||
        !is_finite(brush_properties->transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES native_properties = {
        {properties->start_point.x, properties->start_point.y},
        {properties->end_point.x, properties->end_point.y}
    };
    D2D1_BRUSH_PROPERTIES native_brush_properties = {
        brush_properties->opacity,
        to_native_matrix(brush_properties->transform)
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1LinearGradientBrush> brush;
    HRESULT hr = surface->d2d_context->CreateLinearGradientBrush(
        native_properties,
        native_brush_properties,
        reinterpret_cast<ID2D1GradientStopCollection*>(
            gradient_stop_collection),
        brush.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(brush, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_radial_gradient_brush(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_radial_gradient_brush_properties* properties,
    const progpu_native_direct2d_brush_properties* brush_properties,
    void* gradient_stop_collection,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || properties == nullptr ||
        brush_properties == nullptr || gradient_stop_collection == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        !is_finite(properties->center) ||
        !is_finite(properties->gradient_origin_offset) ||
        !std::isfinite(properties->radius_x) ||
        !std::isfinite(properties->radius_y) ||
        !std::isfinite(brush_properties->opacity) ||
        !is_finite(brush_properties->transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES native_properties = {
        {properties->center.x, properties->center.y},
        {
            properties->gradient_origin_offset.x,
            properties->gradient_origin_offset.y
        },
        properties->radius_x,
        properties->radius_y
    };
    D2D1_BRUSH_PROPERTIES native_brush_properties = {
        brush_properties->opacity,
        to_native_matrix(brush_properties->transform)
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1RadialGradientBrush> brush;
    HRESULT hr = surface->d2d_context->CreateRadialGradientBrush(
        native_properties,
        native_brush_properties,
        reinterpret_cast<ID2D1GradientStopCollection*>(
            gradient_stop_collection),
        brush.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(brush, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_bitmap(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_bitmap_properties* properties,
    const uint8_t* pixels,
    uint64_t pixel_byte_count,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }

    uint64_t row_byte_count = properties == nullptr
        ? 0U
        : static_cast<uint64_t>(properties->width) * 4U;
    uint64_t required_byte_count = properties == nullptr ||
            properties->height == 0U
        ? 0U
        : static_cast<uint64_t>(properties->stride) *
                (properties->height - 1U) +
            row_byte_count;
    if (surface == nullptr || properties == nullptr || pixels == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        properties->width == 0U || properties->height == 0U ||
        properties->reserved != 0U ||
        row_byte_count > UINT32_MAX ||
        properties->stride < row_byte_count ||
        pixel_byte_count < required_byte_count ||
        !std::isfinite(properties->dpi_x) ||
        !std::isfinite(properties->dpi_y) ||
        properties->dpi_x <= 0.0F || properties->dpi_y <= 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_BITMAP_PROPERTIES1 native_properties = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_NONE,
        D2D1::PixelFormat(
            DXGI_FORMAT_B8G8R8A8_UNORM,
            D2D1_ALPHA_MODE_PREMULTIPLIED),
        properties->dpi_x,
        properties->dpi_y);
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Bitmap1> bitmap;
    HRESULT hr = surface->d2d_context->CreateBitmap(
        D2D1::SizeU(properties->width, properties->height),
        pixels,
        properties->stride,
        &native_properties,
        bitmap.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(bitmap, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_bitmap_get_descriptor(
    progpu_native_direct2d_surface* surface,
    void* bitmap,
    progpu_native_direct2d_bitmap_descriptor* descriptor,
    int32_t* native_hresult)
{
    if (descriptor != nullptr) {
        *descriptor = {};
        descriptor->struct_size = sizeof(*descriptor);
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || bitmap == nullptr || descriptor == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Bitmap1> native_bitmap;
    HRESULT hr = reinterpret_cast<IUnknown*>(bitmap)->QueryInterface(
        IID_PPV_ARGS(&native_bitmap));
    if (SUCCEEDED(hr)) {
        const D2D1_SIZE_U pixel_size = native_bitmap->GetPixelSize();
        const D2D1_SIZE_F size = native_bitmap->GetSize();
        const D2D1_PIXEL_FORMAT format = native_bitmap->GetPixelFormat();
        descriptor->pixel_width = pixel_size.width;
        descriptor->pixel_height = pixel_size.height;
        descriptor->width = size.width;
        descriptor->height = size.height;
        native_bitmap->GetDpi(&descriptor->dpi_x, &descriptor->dpi_y);
        descriptor->dxgi_format = static_cast<uint32_t>(format.format);
        descriptor->alpha_mode = static_cast<uint32_t>(format.alphaMode);
        descriptor->options = static_cast<uint32_t>(native_bitmap->GetOptions());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_bitmap_copy_from_memory(
    progpu_native_direct2d_surface* surface,
    void* bitmap,
    const progpu_native_direct2d_rect_u* destination_rectangle,
    const uint8_t* source_data,
    uint64_t source_byte_count,
    uint32_t source_pitch,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || bitmap == nullptr || source_data == nullptr ||
        source_byte_count == 0U || source_pitch == 0U ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Bitmap1> native_bitmap;
    HRESULT hr = reinterpret_cast<IUnknown*>(bitmap)->QueryInterface(
        IID_PPV_ARGS(&native_bitmap));
    D2D1_RECT_U native_destination{};
    if (SUCCEEDED(hr) && !try_get_copy_rectangle(
            destination_rectangle,
            native_bitmap->GetPixelSize(),
            native_destination)) {
        hr = E_INVALIDARG;
    }
    uint32_t pixel_bytes = 0U;
    if (SUCCEEDED(hr)) {
        pixel_bytes = bytes_per_pixel(native_bitmap->GetPixelFormat().format);
        if (pixel_bytes == 0U) {
            hr = D2DERR_UNSUPPORTED_PIXEL_FORMAT;
        }
    }
    if (SUCCEEDED(hr)) {
        const uint64_t width = native_destination.right -
            native_destination.left;
        const uint64_t height = native_destination.bottom -
            native_destination.top;
        const uint64_t row_bytes = width * pixel_bytes;
        const uint64_t required_bytes =
            (height - 1U) * source_pitch + row_bytes;
        if (row_bytes > source_pitch || required_bytes > source_byte_count) {
            hr = E_INVALIDARG;
        }
    }
    if (SUCCEEDED(hr)) {
        hr = native_bitmap->CopyFromMemory(
            &native_destination,
            source_data,
            source_pitch);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (hr == E_INVALIDARG) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_bitmap_copy_from_bitmap(
    progpu_native_direct2d_surface* surface,
    void* bitmap,
    const progpu_native_direct2d_point_2u* destination_point,
    void* source_bitmap,
    const progpu_native_direct2d_rect_u* source_rectangle,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || bitmap == nullptr || source_bitmap == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Bitmap1> native_bitmap;
    HRESULT hr = reinterpret_cast<IUnknown*>(bitmap)->QueryInterface(
        IID_PPV_ARGS(&native_bitmap));
    ComPtr<ID2D1Bitmap> native_source;
    if (SUCCEEDED(hr)) {
        hr = reinterpret_cast<IUnknown*>(source_bitmap)->QueryInterface(
            IID_PPV_ARGS(&native_source));
    }
    if (SUCCEEDED(hr) && has_same_com_identity(
            native_bitmap.Get(), native_source.Get())) {
        hr = E_INVALIDARG;
    }
    D2D1_RECT_U native_source_rectangle{};
    if (SUCCEEDED(hr) && !try_get_copy_rectangle(
            source_rectangle,
            native_source->GetPixelSize(),
            native_source_rectangle)) {
        hr = E_INVALIDARG;
    }
    D2D1_POINT_2U native_destination = destination_point == nullptr
        ? D2D1::Point2U(0U, 0U)
        : D2D1::Point2U(destination_point->x, destination_point->y);
    if (SUCCEEDED(hr)) {
        const D2D1_SIZE_U destination_size = native_bitmap->GetPixelSize();
        const uint64_t copy_width = native_source_rectangle.right -
            native_source_rectangle.left;
        const uint64_t copy_height = native_source_rectangle.bottom -
            native_source_rectangle.top;
        if (static_cast<uint64_t>(native_destination.x) + copy_width >
                destination_size.width ||
            static_cast<uint64_t>(native_destination.y) + copy_height >
                destination_size.height) {
            hr = E_INVALIDARG;
        }
    }
    if (SUCCEEDED(hr)) {
        hr = native_bitmap->CopyFromBitmap(
            &native_destination,
            native_source.Get(),
            &native_source_rectangle);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (hr == E_INVALIDARG) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_bitmap_brush(
    progpu_native_direct2d_surface* surface,
    void* bitmap,
    const progpu_native_direct2d_bitmap_brush_properties* properties,
    const progpu_native_direct2d_brush_properties* brush_properties,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || bitmap == nullptr || properties == nullptr ||
        brush_properties == nullptr || value == nullptr ||
        native_hresult == nullptr ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_x)) ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_y)) ||
        !is_valid_interpolation_mode(properties->interpolation_mode) ||
        !std::isfinite(brush_properties->opacity) ||
        !is_finite(brush_properties->transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_BITMAP_BRUSH_PROPERTIES1 native_properties = {
        static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_x),
        static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_y),
        static_cast<D2D1_INTERPOLATION_MODE>(
            properties->interpolation_mode)
    };
    D2D1_BRUSH_PROPERTIES native_brush_properties = {
        brush_properties->opacity,
        to_native_matrix(brush_properties->transform)
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1BitmapBrush1> brush;
    ComPtr<ID2D1Bitmap> native_bitmap;
    HRESULT hr = reinterpret_cast<IUnknown*>(bitmap)->QueryInterface(
        IID_PPV_ARGS(&native_bitmap));
    if (SUCCEEDED(hr)) {
        hr = surface->d2d_context->CreateBitmapBrush(
            native_bitmap.Get(),
            &native_properties,
            &native_brush_properties,
            brush.GetAddressOf());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(brush, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_bitmap_brush_set_properties(
    progpu_native_direct2d_surface* surface,
    void* brush,
    const progpu_native_direct2d_bitmap_brush_properties* properties,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || properties == nullptr ||
        native_hresult == nullptr ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_x)) ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_y)) ||
        !is_valid_interpolation_mode(properties->interpolation_mode)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1BitmapBrush1> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        native_brush->SetExtendModeX(
            static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_x));
        native_brush->SetExtendModeY(
            static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_y));
        native_brush->SetInterpolationMode1(
            static_cast<D2D1_INTERPOLATION_MODE>(
                properties->interpolation_mode));
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_bitmap_brush_get_properties(
    progpu_native_direct2d_surface* surface,
    void* brush,
    progpu_native_direct2d_bitmap_brush_properties* properties,
    int32_t* native_hresult)
{
    if (properties != nullptr) {
        *properties = {};
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || properties == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1BitmapBrush1> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        properties->extend_mode_x = native_brush->GetExtendModeX();
        properties->extend_mode_y = native_brush->GetExtendModeY();
        properties->interpolation_mode = native_brush->GetInterpolationMode1();
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_bitmap_brush_set_bitmap(
    progpu_native_direct2d_surface* surface,
    void* brush,
    void* bitmap,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1BitmapBrush1> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    ComPtr<ID2D1Bitmap> native_bitmap;
    if (SUCCEEDED(hr) && bitmap != nullptr) {
        hr = reinterpret_cast<IUnknown*>(bitmap)->QueryInterface(
            IID_PPV_ARGS(&native_bitmap));
    }
    if (SUCCEEDED(hr)) {
        native_brush->SetBitmap(native_bitmap.Get());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_bitmap_brush_get_bitmap(
    progpu_native_direct2d_surface* surface,
    void* brush,
    void** bitmap,
    int32_t* native_hresult)
{
    if (bitmap != nullptr) {
        *bitmap = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || bitmap == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1BitmapBrush1> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        ComPtr<ID2D1Bitmap> native_bitmap;
        native_brush->GetBitmap(&native_bitmap);
        if (native_bitmap.Get() != nullptr) {
            ComPtr<ID2D1Bitmap1> native_bitmap1;
            hr = native_bitmap.As(&native_bitmap1);
            if (SUCCEEDED(hr)) {
                *bitmap = native_bitmap1.Detach();
            }
        }
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_image_brush(
    progpu_native_direct2d_surface* surface,
    void* image,
    const progpu_native_direct2d_image_brush_properties* properties,
    const progpu_native_direct2d_brush_properties* brush_properties,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    const progpu_native_direct2d_rect_f* source_rectangle =
        properties == nullptr ? nullptr : &properties->source_rectangle;
    if (surface == nullptr || image == nullptr || properties == nullptr ||
        brush_properties == nullptr || value == nullptr ||
        native_hresult == nullptr ||
        !std::isfinite(source_rectangle->x) ||
        !std::isfinite(source_rectangle->y) ||
        !std::isfinite(source_rectangle->width) ||
        !std::isfinite(source_rectangle->height) ||
        source_rectangle->width <= 0.0F ||
        source_rectangle->height <= 0.0F ||
        !std::isfinite(source_rectangle->x + source_rectangle->width) ||
        !std::isfinite(source_rectangle->y + source_rectangle->height) ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_x)) ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_y)) ||
        !is_valid_interpolation_mode(properties->interpolation_mode) ||
        !std::isfinite(brush_properties->opacity) ||
        !is_finite(brush_properties->transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_IMAGE_BRUSH_PROPERTIES native_properties = {
        D2D1::RectF(
            source_rectangle->x,
            source_rectangle->y,
            source_rectangle->x + source_rectangle->width,
            source_rectangle->y + source_rectangle->height),
        static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_x),
        static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_y),
        static_cast<D2D1_INTERPOLATION_MODE>(
            properties->interpolation_mode)
    };
    D2D1_BRUSH_PROPERTIES native_brush_properties = {
        brush_properties->opacity,
        to_native_matrix(brush_properties->transform)
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1ImageBrush> brush;
    ComPtr<ID2D1Image> native_image;
    HRESULT hr = reinterpret_cast<IUnknown*>(image)->QueryInterface(
        IID_PPV_ARGS(&native_image));
    if (SUCCEEDED(hr)) {
        hr = surface->d2d_context->CreateImageBrush(
            native_image.Get(),
            &native_properties,
            &native_brush_properties,
            brush.GetAddressOf());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(brush, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_image_brush_set_properties(
    progpu_native_direct2d_surface* surface,
    void* brush,
    const progpu_native_direct2d_image_brush_properties* properties,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || properties == nullptr ||
        native_hresult == nullptr ||
        !is_valid(properties->source_rectangle) ||
        properties->source_rectangle.width <= 0.0F ||
        properties->source_rectangle.height <= 0.0F ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_x)) ||
        !is_valid(static_cast<progpu_native_direct2d_extend_mode>(
            properties->extend_mode_y)) ||
        !is_valid_interpolation_mode(properties->interpolation_mode)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1ImageBrush> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        const D2D1_RECT_F source_rectangle =
            to_native_rect(properties->source_rectangle);
        native_brush->SetSourceRectangle(&source_rectangle);
        native_brush->SetExtendModeX(
            static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_x));
        native_brush->SetExtendModeY(
            static_cast<D2D1_EXTEND_MODE>(properties->extend_mode_y));
        native_brush->SetInterpolationMode(
            static_cast<D2D1_INTERPOLATION_MODE>(
                properties->interpolation_mode));
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_image_brush_get_properties(
    progpu_native_direct2d_surface* surface,
    void* brush,
    progpu_native_direct2d_image_brush_properties* properties,
    int32_t* native_hresult)
{
    if (properties != nullptr) {
        *properties = {};
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || properties == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1ImageBrush> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        D2D1_RECT_F rectangle{};
        native_brush->GetSourceRectangle(&rectangle);
        properties->source_rectangle = {
            rectangle.left,
            rectangle.top,
            rectangle.right - rectangle.left,
            rectangle.bottom - rectangle.top
        };
        properties->extend_mode_x = native_brush->GetExtendModeX();
        properties->extend_mode_y = native_brush->GetExtendModeY();
        properties->interpolation_mode = native_brush->GetInterpolationMode();
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_image_brush_set_image(
    progpu_native_direct2d_surface* surface,
    void* brush,
    void* image,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1ImageBrush> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    ComPtr<ID2D1Image> native_image;
    if (SUCCEEDED(hr) && image != nullptr) {
        hr = reinterpret_cast<IUnknown*>(image)->QueryInterface(
            IID_PPV_ARGS(&native_image));
    }
    if (SUCCEEDED(hr)) {
        native_brush->SetImage(native_image.Get());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_image_brush_get_image(
    progpu_native_direct2d_surface* surface,
    void* brush,
    void** image,
    int32_t* native_hresult)
{
    if (image != nullptr) {
        *image = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr || image == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1ImageBrush> native_brush;
    HRESULT hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
        IID_PPV_ARGS(&native_brush));
    if (SUCCEEDED(hr)) {
        ComPtr<ID2D1Image> native_image;
        native_brush->GetImage(&native_image);
        *image = native_image.Detach();
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_command_list(
    progpu_native_direct2d_surface* surface,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || value == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1CommandList> command_list;
    HRESULT hr = surface->d2d_context->CreateCommandList(
        command_list.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(command_list, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_command_list_get_stream_summary(
    progpu_native_direct2d_surface* surface,
    void* command_list,
    uint32_t options,
    progpu_native_direct2d_command_stream_summary* summary,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || command_list == nullptr || summary == nullptr ||
        summary->struct_size != sizeof(*summary) || native_hresult == nullptr ||
        (options &
            ~PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_OPTION_REQUIRE_SUPPORTED_OPERATIONS) !=
            0U) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    *summary = {};
    summary->struct_size = static_cast<uint32_t>(sizeof(*summary));

    std::scoped_lock lock(surface->access_mutex);
    if (surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE;
    }

    ComPtr<ID2D1CommandList> native_command_list;
    HRESULT hr = reinterpret_cast<IUnknown*>(command_list)->QueryInterface(
        IID_PPV_ARGS(&native_command_list));
    if (SUCCEEDED(hr)) {
        CommandStreamSummarySink* sink = new (std::nothrow)
            CommandStreamSummarySink(
                (options &
                    PROGPU_NATIVE_DIRECT2D_COMMAND_STREAM_OPTION_REQUIRE_SUPPORTED_OPERATIONS) !=
                    0U);
        if (sink == nullptr) {
            hr = E_OUTOFMEMORY;
        } else {
            hr = native_command_list->Stream(sink);
            *summary = sink->summary();
            sink->Release();
        }
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (SUCCEEDED(hr)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
    }
    if (hr == E_NOTIMPL || hr == E_NOINTERFACE) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED;
    }
    if (hr == D2DERR_WRONG_STATE) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    return status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_command_list_build_scene_stream(
    progpu_native_direct2d_surface* surface,
    void* command_list,
    uint64_t scene_id,
    uint64_t generation,
    uint8_t* destination,
    uint64_t destination_capacity,
    progpu_native_direct2d_scene_stream_result* result,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || command_list == nullptr || scene_id == 0U ||
        generation == 0U || result == nullptr ||
        result->struct_size != sizeof(*result) || native_hresult == nullptr ||
        (destination == nullptr) != (destination_capacity == 0U) ||
        destination_capacity > std::numeric_limits<size_t>::max()) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    *result = {};
    result->struct_size = static_cast<uint32_t>(sizeof(*result));
    result->scene_id = scene_id;
    result->generation = generation;

    std::scoped_lock lock(surface->access_mutex);
    if (surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE;
    }

    ComPtr<ID2D1CommandList> native_command_list;
    HRESULT hr = reinterpret_cast<IUnknown*>(command_list)->QueryInterface(
        IID_PPV_ARGS(&native_command_list));
    progpu_native_direct2d_command_stream_summary summary{};
    summary.struct_size = static_cast<uint32_t>(sizeof(summary));
    if (SUCCEEDED(hr)) {
        CommandStreamSummarySink* summary_sink = new (std::nothrow)
            CommandStreamSummarySink(false);
        if (summary_sink == nullptr) {
            hr = E_OUTOFMEMORY;
        } else {
            hr = native_command_list->Stream(summary_sink);
            summary = summary_sink->summary();
            summary_sink->Release();
        }
    }

    CommandSceneStreamSink* scene_sink = nullptr;
    if (SUCCEEDED(hr)) {
        try {
            scene_sink = new CommandSceneStreamSink(
                scene_id,
                generation,
                summary);
        } catch (const std::bad_alloc&) {
            hr = E_OUTOFMEMORY;
        } catch (...) {
            hr = E_FAIL;
        }
    }
    if (SUCCEEDED(hr)) {
        hr = native_command_list->Stream(scene_sink);
    }
    if (scene_sink != nullptr) {
        hr = build_scene_stream(
            *scene_sink,
            scene_id,
            generation,
            destination,
            destination_capacity,
            *result,
            hr);
    }
    if (scene_sink != nullptr) {
        scene_sink->Release();
    }

    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (SUCCEEDED(hr)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
    }
    if (hr == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER;
    }
    if (hr == E_NOTIMPL || hr == E_NOINTERFACE) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED;
    }
    if (hr == D2DERR_WRONG_STATE) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    if (hr == E_INVALIDARG) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    if (hr == E_OUTOFMEMORY) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY;
    }
    return status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_effect(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_guid* effect_id,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || effect_id == nullptr || is_empty(*effect_id) ||
        value == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    GUID native_effect_id = to_native_guid(*effect_id);
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Effect> effect;
    HRESULT hr = surface->d2d_context->CreateEffect(
        native_effect_id,
        effect.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(effect, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_effect_set_input(
    progpu_native_direct2d_surface* surface,
    void* effect,
    uint32_t input_index,
    void* image,
    uint32_t invalidate,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || effect == nullptr || invalidate > 1U ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Effect> native_effect;
    HRESULT hr = reinterpret_cast<IUnknown*>(effect)->QueryInterface(
        IID_PPV_ARGS(&native_effect));
    if (SUCCEEDED(hr) && input_index >= native_effect->GetInputCount()) {
        hr = E_INVALIDARG;
    }
    ComPtr<ID2D1Image> native_image;
    if (SUCCEEDED(hr) && image != nullptr) {
        hr = reinterpret_cast<IUnknown*>(image)->QueryInterface(
            IID_PPV_ARGS(&native_image));
    }
    if (SUCCEEDED(hr)) {
        native_effect->SetInput(
            input_index,
            native_image.Get(),
            invalidate != 0U ? TRUE : FALSE);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_effect_set_input_effect(
    progpu_native_direct2d_surface* surface,
    void* effect,
    uint32_t input_index,
    void* input_effect,
    uint32_t invalidate,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || effect == nullptr || input_effect == nullptr ||
        invalidate > 1U || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Effect> native_effect;
    HRESULT hr = reinterpret_cast<IUnknown*>(effect)->QueryInterface(
        IID_PPV_ARGS(&native_effect));
    if (SUCCEEDED(hr) && input_index >= native_effect->GetInputCount()) {
        hr = E_INVALIDARG;
    }
    ComPtr<ID2D1Effect> native_input_effect;
    if (SUCCEEDED(hr)) {
        hr = reinterpret_cast<IUnknown*>(input_effect)->QueryInterface(
            IID_PPV_ARGS(&native_input_effect));
    }
    if (SUCCEEDED(hr)) {
        native_effect->SetInputEffect(
            input_index,
            native_input_effect.Get(),
            invalidate != 0U ? TRUE : FALSE);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_effect_set_value(
    progpu_native_direct2d_surface* surface,
    void* effect,
    uint32_t property_index,
    progpu_native_direct2d_effect_property_type property_type,
    const void* data,
    uint32_t data_size,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || effect == nullptr || data == nullptr ||
        native_hresult == nullptr ||
        !is_valid_effect_property(property_type, data_size)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Effect> native_effect;
    HRESULT hr = reinterpret_cast<IUnknown*>(effect)->QueryInterface(
        IID_PPV_ARGS(&native_effect));
    if (SUCCEEDED(hr) && property_index >= native_effect->GetPropertyCount()) {
        hr = E_INVALIDARG;
    }
    if (SUCCEEDED(hr)) {
        hr = native_effect->SetValue(
            property_index,
            static_cast<D2D1_PROPERTY_TYPE>(property_type),
            static_cast<const BYTE*>(data),
            data_size);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_effect_get_output(
    progpu_native_direct2d_surface* surface,
    void* effect,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || effect == nullptr || value == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Effect> native_effect;
    HRESULT hr = reinterpret_cast<IUnknown*>(effect)->QueryInterface(
        IID_PPV_ARGS(&native_effect));
    ComPtr<ID2D1Image> output;
    if (SUCCEEDED(hr)) {
        native_effect->GetOutput(output.GetAddressOf());
        if (!output) {
            hr = E_UNEXPECTED;
        }
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(output, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_layer(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_size_f* size,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || value == nullptr || native_hresult == nullptr ||
        (size != nullptr &&
         (!std::isfinite(size->width) || !std::isfinite(size->height) ||
          size->width <= 0.0F || size->height <= 0.0F))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_SIZE_F native_size{};
    const D2D1_SIZE_F* native_size_pointer = nullptr;
    if (size != nullptr) {
        native_size = {size->width, size->height};
        native_size_pointer = &native_size;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Layer> layer;
    HRESULT hr = surface->d2d_context->CreateLayer(
        native_size_pointer,
        layer.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(layer, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_drawing_state_block(
    progpu_native_direct2d_surface* surface,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || value == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Factory1> factory;
    HRESULT hr = surface->d2d_factory.As(&factory);
    ComPtr<ID2D1DrawingStateBlock1> drawing_state_block;
    if (SUCCEEDED(hr)) {
        hr = factory->CreateDrawingStateBlock(
            drawing_state_block.GetAddressOf());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(drawing_state_block, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_save_drawing_state(
    progpu_native_direct2d_surface* surface,
    void* drawing_state_block,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || drawing_state_block == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1DrawingStateBlock1> native_state;
    HRESULT hr = reinterpret_cast<IUnknown*>(drawing_state_block)->QueryInterface(
        IID_PPV_ARGS(&native_state));
    if (SUCCEEDED(hr)) {
        surface->d2d_context->SaveDrawingState(native_state.Get());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_restore_drawing_state(
    progpu_native_direct2d_surface* surface,
    void* drawing_state_block,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || drawing_state_block == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1DrawingStateBlock1> native_state;
    HRESULT hr = reinterpret_cast<IUnknown*>(drawing_state_block)->QueryInterface(
        IID_PPV_ARGS(&native_state));
    if (SUCCEEDED(hr)) {
        surface->d2d_context->RestoreDrawingState(native_state.Get());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_push_layer(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_layer_parameters* parameters,
    void* geometric_mask,
    void* opacity_brush,
    void* layer,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || parameters == nullptr || layer == nullptr ||
        native_hresult == nullptr || !is_valid(parameters->content_bounds) ||
        !is_valid(static_cast<progpu_native_direct2d_antialias_mode>(
            parameters->mask_antialias_mode)) ||
        !is_finite(parameters->mask_transform) ||
        !std::isfinite(parameters->opacity) || parameters->opacity < 0.0F ||
        parameters->opacity > 1.0F ||
        !is_valid_layer_options(parameters->options)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    if (!can_push_draw_scope(*surface)) {
        surface->last_hresult.store(
            D2DERR_WRONG_STATE,
            std::memory_order_release);
        *native_hresult = D2DERR_WRONG_STATE;
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }

    ComPtr<ID2D1Layer> native_layer;
    HRESULT hr = reinterpret_cast<IUnknown*>(layer)->QueryInterface(
        IID_PPV_ARGS(&native_layer));
    ComPtr<ID2D1Geometry> native_mask;
    if (SUCCEEDED(hr) && geometric_mask != nullptr) {
        hr = reinterpret_cast<IUnknown*>(geometric_mask)->QueryInterface(
            IID_PPV_ARGS(&native_mask));
    }
    ComPtr<ID2D1Brush> native_opacity_brush;
    if (SUCCEEDED(hr) && opacity_brush != nullptr) {
        hr = reinterpret_cast<IUnknown*>(opacity_brush)->QueryInterface(
            IID_PPV_ARGS(&native_opacity_brush));
    }
    if (SUCCEEDED(hr)) {
        D2D1_LAYER_PARAMETERS1 native_parameters{};
        native_parameters.contentBounds = D2D1::RectF(
            parameters->content_bounds.x,
            parameters->content_bounds.y,
            parameters->content_bounds.x +
                parameters->content_bounds.width,
            parameters->content_bounds.y +
                parameters->content_bounds.height);
        native_parameters.geometricMask = native_mask.Get();
        native_parameters.maskAntialiasMode =
            static_cast<D2D1_ANTIALIAS_MODE>(
                parameters->mask_antialias_mode);
        native_parameters.maskTransform =
            to_native_matrix(parameters->mask_transform);
        native_parameters.opacity = parameters->opacity;
        native_parameters.opacityBrush = native_opacity_brush.Get();
        native_parameters.layerOptions =
            static_cast<D2D1_LAYER_OPTIONS1>(parameters->options);
        surface->d2d_context->PushLayer(
            native_parameters,
            native_layer.Get());
        push_draw_scope(
            *surface,
            progpu_direct2d_draw_scope_kind::layer);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_pop_layer(
    progpu_native_direct2d_surface* surface,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    if (surface->draw_scope_depth == 0U ||
        surface->draw_scopes[surface->draw_scope_depth - 1U] !=
            progpu_direct2d_draw_scope_kind::layer) {
        surface->last_hresult.store(
            D2DERR_WRONG_STATE,
            std::memory_order_release);
        *native_hresult = D2DERR_WRONG_STATE;
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    surface->d2d_context->PopLayer();
    --surface->draw_scope_depth;
    surface->last_hresult.store(S_OK, std::memory_order_release);
    *native_hresult = S_OK;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_text_format(
    progpu_native_direct2d_surface* surface,
    const uint16_t* font_family,
    uint32_t font_family_length,
    const uint16_t* locale_name,
    uint32_t locale_name_length,
    const progpu_native_direct2d_text_format_properties* properties,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || font_family == nullptr ||
        font_family_length == 0U || locale_name == nullptr ||
        locale_name_length == 0U || properties == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        !is_valid_text_format(*properties) ||
        contains_null(font_family, font_family_length) ||
        contains_null(locale_name, locale_name_length)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    try {
        std::wstring family(
            reinterpret_cast<const wchar_t*>(font_family),
            font_family_length);
        std::wstring locale(
            reinterpret_cast<const wchar_t*>(locale_name),
            locale_name_length);
        std::scoped_lock lock(surface->access_mutex);
        ComPtr<IDWriteTextFormat> base_format;
        HRESULT hr = surface->dwrite_factory->CreateTextFormat(
            family.c_str(),
            nullptr,
            static_cast<DWRITE_FONT_WEIGHT>(properties->font_weight),
            static_cast<DWRITE_FONT_STYLE>(properties->font_style),
            static_cast<DWRITE_FONT_STRETCH>(properties->font_stretch),
            properties->font_size,
            locale.c_str(),
            &base_format);
        if (SUCCEEDED(hr)) {
            hr = base_format->SetTextAlignment(
                static_cast<DWRITE_TEXT_ALIGNMENT>(
                    properties->text_alignment));
        }
        if (SUCCEEDED(hr)) {
            hr = base_format->SetParagraphAlignment(
                static_cast<DWRITE_PARAGRAPH_ALIGNMENT>(
                    properties->paragraph_alignment));
        }
        if (SUCCEEDED(hr)) {
            hr = base_format->SetWordWrapping(
                static_cast<DWRITE_WORD_WRAPPING>(
                    properties->word_wrapping));
        }
        if (SUCCEEDED(hr)) {
            hr = base_format->SetReadingDirection(
                static_cast<DWRITE_READING_DIRECTION>(
                    properties->reading_direction));
        }
        if (SUCCEEDED(hr)) {
            hr = base_format->SetFlowDirection(
                static_cast<DWRITE_FLOW_DIRECTION>(
                    properties->flow_direction));
        }
        if (SUCCEEDED(hr) && properties->incremental_tab_stop > 0.0F) {
            hr = base_format->SetIncrementalTabStop(
                properties->incremental_tab_stop);
        }
        ComPtr<IDWriteTextFormat1> format;
        if (SUCCEEDED(hr)) {
            hr = base_format.As(&format);
        }
        surface->last_hresult.store(hr, std::memory_order_release);
        *native_hresult = hr;
        if (FAILED(hr)) {
            return status_from_win2d_hresult(hr);
        }
        return return_interface(format, value);
    } catch (const std::bad_alloc&) {
        std::scoped_lock lock(surface->access_mutex);
        surface->last_hresult.store(E_OUTOFMEMORY, std::memory_order_release);
        *native_hresult = E_OUTOFMEMORY;
        return PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY;
    }
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_text(
    progpu_native_direct2d_surface* surface,
    const uint16_t* text,
    uint32_t text_length,
    void* text_format,
    const progpu_native_direct2d_rect_f* layout_rectangle,
    void* default_fill_brush,
    uint32_t options,
    progpu_native_direct2d_measuring_mode measuring_mode,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || (text == nullptr && text_length != 0U) ||
        text_format == nullptr || layout_rectangle == nullptr ||
        default_fill_brush == nullptr || native_hresult == nullptr ||
        !is_valid(*layout_rectangle) ||
        !is_valid_draw_text_options(options) || !is_valid(measuring_mode)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<IDWriteTextFormat> format;
    HRESULT hr = reinterpret_cast<IUnknown*>(text_format)->QueryInterface(
        IID_PPV_ARGS(&format));
    ComPtr<ID2D1Brush> brush;
    if (SUCCEEDED(hr)) {
        hr = reinterpret_cast<IUnknown*>(default_fill_brush)->QueryInterface(
            IID_PPV_ARGS(&brush));
    }
    if (SUCCEEDED(hr) && text_length != 0U) {
        D2D1_RECT_F native_rectangle = D2D1::RectF(
            layout_rectangle->x,
            layout_rectangle->y,
            layout_rectangle->x + layout_rectangle->width,
            layout_rectangle->y + layout_rectangle->height);
        surface->d2d_context->DrawText(
            reinterpret_cast<const wchar_t*>(text),
            text_length,
            format.Get(),
            native_rectangle,
            brush.Get(),
            static_cast<D2D1_DRAW_TEXT_OPTIONS>(options),
            static_cast<DWRITE_MEASURING_MODE>(measuring_mode));
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_text_layout(
    progpu_native_direct2d_surface* surface,
    const uint16_t* text,
    uint32_t text_length,
    void* text_format,
    float maximum_width,
    float maximum_height,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || (text == nullptr && text_length != 0U) ||
        text_format == nullptr || value == nullptr || native_hresult == nullptr ||
        !std::isfinite(maximum_width) || maximum_width <= 0.0F ||
        !std::isfinite(maximum_height) || maximum_height <= 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    static constexpr wchar_t empty_text[] = L"";
    const wchar_t* native_text = text_length == 0U
        ? empty_text
        : reinterpret_cast<const wchar_t*>(text);
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<IDWriteTextFormat> format;
    HRESULT hr = reinterpret_cast<IUnknown*>(text_format)->QueryInterface(
        IID_PPV_ARGS(&format));
    ComPtr<IDWriteTextLayout> base_layout;
    if (SUCCEEDED(hr)) {
        hr = surface->dwrite_factory->CreateTextLayout(
            native_text,
            text_length,
            format.Get(),
            maximum_width,
            maximum_height,
            &base_layout);
    }
    ComPtr<IDWriteTextLayout4> layout;
    if (SUCCEEDED(hr)) {
        hr = base_layout.As(&layout);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(layout, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_text_layout_set_range_format(
    progpu_native_direct2d_surface* surface,
    void* text_layout,
    const progpu_native_direct2d_text_range_format* formatting,
    void* drawing_effect_brush,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || text_layout == nullptr || formatting == nullptr ||
        native_hresult == nullptr ||
        !is_valid_text_range_format(*formatting, drawing_effect_brush)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<IDWriteTextLayout> layout;
    HRESULT hr = reinterpret_cast<IUnknown*>(text_layout)->QueryInterface(
        IID_PPV_ARGS(&layout));
    ComPtr<ID2D1Brush> brush;
    if (SUCCEEDED(hr) && drawing_effect_brush != nullptr) {
        hr = reinterpret_cast<IUnknown*>(drawing_effect_brush)->QueryInterface(
            IID_PPV_ARGS(&brush));
    }
    const DWRITE_TEXT_RANGE range = {
        formatting->range_start,
        formatting->range_length};
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_SIZE) != 0U) {
        hr = layout->SetFontSize(formatting->font_size, range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_WEIGHT) != 0U) {
        hr = layout->SetFontWeight(
            static_cast<DWRITE_FONT_WEIGHT>(formatting->font_weight),
            range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STYLE) != 0U) {
        hr = layout->SetFontStyle(
            static_cast<DWRITE_FONT_STYLE>(formatting->font_style),
            range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_FONT_STRETCH) != 0U) {
        hr = layout->SetFontStretch(
            static_cast<DWRITE_FONT_STRETCH>(formatting->font_stretch),
            range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_UNDERLINE) != 0U) {
        hr = layout->SetUnderline(formatting->underline != 0U, range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_STRIKETHROUGH) != 0U) {
        hr = layout->SetStrikethrough(
            formatting->strikethrough != 0U,
            range);
    }
    if (SUCCEEDED(hr) && (formatting->flags &
            PROGPU_NATIVE_DIRECT2D_TEXT_RANGE_FORMAT_DRAWING_EFFECT) != 0U) {
        hr = layout->SetDrawingEffect(brush.Get(), range);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_typography(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_typography_feature* features,
    uint32_t feature_count,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    constexpr uint32_t maximum_feature_count = 4096U;
    if (surface == nullptr || features == nullptr || feature_count == 0U ||
        feature_count > maximum_feature_count || value == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    for (uint32_t index = 0U; index < feature_count; ++index) {
        if (features[index].name_tag == 0U) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<IDWriteTypography> typography;
    HRESULT hr = surface->dwrite_factory->CreateTypography(&typography);
    for (uint32_t index = 0U; SUCCEEDED(hr) && index < feature_count; ++index) {
        const DWRITE_FONT_FEATURE feature = {
            static_cast<DWRITE_FONT_FEATURE_TAG>(features[index].name_tag),
            features[index].parameter};
        hr = typography->AddFontFeature(feature);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(typography, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_text_layout_set_typography(
    progpu_native_direct2d_surface* surface,
    void* text_layout,
    void* typography,
    uint32_t range_start,
    uint32_t range_length,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || text_layout == nullptr || typography == nullptr ||
        range_length == 0U || range_start > UINT32_MAX - range_length ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<IDWriteTextLayout> layout;
    HRESULT hr = reinterpret_cast<IUnknown*>(text_layout)->QueryInterface(
        IID_PPV_ARGS(&layout));
    ComPtr<IDWriteTypography> native_typography;
    if (SUCCEEDED(hr)) {
        hr = reinterpret_cast<IUnknown*>(typography)->QueryInterface(
            IID_PPV_ARGS(&native_typography));
    }
    if (SUCCEEDED(hr)) {
        const DWRITE_TEXT_RANGE range = {range_start, range_length};
        hr = layout->SetTypography(native_typography.Get(), range);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_system_font_face_reference(
    progpu_native_direct2d_surface* surface,
    const uint16_t* font_family,
    uint32_t font_family_length,
    const progpu_native_direct2d_font_face_properties* properties,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || font_family == nullptr ||
        font_family_length == 0U || contains_null(font_family, font_family_length) ||
        properties == nullptr || !is_valid_font_face(*properties) ||
        value == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    try {
        const std::wstring family_name(
            reinterpret_cast<const wchar_t*>(font_family),
            font_family_length);
        std::scoped_lock lock(surface->access_mutex);
        ComPtr<IDWriteFactory> base_factory;
        HRESULT hr = surface->dwrite_factory.As(&base_factory);
        ComPtr<IDWriteFontCollection> collection;
        if (SUCCEEDED(hr)) {
            hr = base_factory->GetSystemFontCollection(&collection, FALSE);
        }
        UINT32 family_index = 0U;
        BOOL family_exists = FALSE;
        if (SUCCEEDED(hr)) {
            hr = collection->FindFamilyName(
                family_name.c_str(),
                &family_index,
                &family_exists);
        }
        if (SUCCEEDED(hr) && family_exists == FALSE) {
            hr = DWRITE_E_NOFONT;
        }
        ComPtr<IDWriteFontFamily> family;
        if (SUCCEEDED(hr)) {
            hr = collection->GetFontFamily(family_index, &family);
        }
        ComPtr<IDWriteFont> font;
        if (SUCCEEDED(hr)) {
            hr = family->GetFirstMatchingFont(
                static_cast<DWRITE_FONT_WEIGHT>(properties->font_weight),
                static_cast<DWRITE_FONT_STRETCH>(properties->font_stretch),
                static_cast<DWRITE_FONT_STYLE>(properties->font_style),
                &font);
        }
        ComPtr<IDWriteFont3> font3;
        if (SUCCEEDED(hr)) {
            hr = font.As(&font3);
        }
        ComPtr<IDWriteFontFaceReference> reference;
        if (SUCCEEDED(hr)) {
            hr = font3->GetFontFaceReference(&reference);
        }
        surface->last_hresult.store(hr, std::memory_order_release);
        *native_hresult = hr;
        if (FAILED(hr)) {
            return status_from_win2d_hresult(hr);
        }
        return return_interface(reference, value);
    } catch (const std::bad_alloc&) {
        std::scoped_lock lock(surface->access_mutex);
        surface->last_hresult.store(E_OUTOFMEMORY, std::memory_order_release);
        *native_hresult = E_OUTOFMEMORY;
        return PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY;
    }
}

progpu_native_direct2d_status
progpu_native_direct2d_font_face_reference_create_font_face(
    progpu_native_direct2d_surface* surface,
    void* font_face_reference,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || font_face_reference == nullptr ||
        value == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<IDWriteFontFaceReference> reference;
    HRESULT hr = reinterpret_cast<IUnknown*>(font_face_reference)->QueryInterface(
        IID_PPV_ARGS(&reference));
    ComPtr<IDWriteFontFace3> face3;
    if (SUCCEEDED(hr)) {
        hr = reference->CreateFontFace(&face3);
    }
    ComPtr<IDWriteFontFace5> face5;
    if (SUCCEEDED(hr)) {
        hr = face3.As(&face5);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(face5, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_glyph_run(
    progpu_native_direct2d_surface* surface,
    float baseline_origin_x,
    float baseline_origin_y,
    float font_em_size,
    void* font_face,
    const uint16_t* glyph_indices,
    uint32_t glyph_count,
    const float* glyph_advances,
    uint32_t glyph_advance_count,
    const progpu_native_direct2d_glyph_offset* glyph_offsets,
    uint32_t glyph_offset_count,
    uint32_t is_sideways,
    uint32_t bidi_level,
    void* foreground_brush,
    progpu_native_direct2d_measuring_mode measuring_mode,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    constexpr uint32_t maximum_glyph_count = 1U << 20U;
    if (surface == nullptr || !std::isfinite(baseline_origin_x) ||
        !std::isfinite(baseline_origin_y) || !std::isfinite(font_em_size) ||
        font_em_size <= 0.0F || font_face == nullptr ||
        glyph_indices == nullptr || glyph_count == 0U ||
        glyph_count > maximum_glyph_count ||
        (glyph_advances == nullptr
            ? glyph_advance_count != 0U
            : glyph_advance_count != glyph_count) ||
        (glyph_offsets == nullptr
            ? glyph_offset_count != 0U
            : glyph_offset_count != glyph_count) ||
        is_sideways > 1U || bidi_level > 125U ||
        foreground_brush == nullptr || !is_valid(measuring_mode) ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    for (uint32_t index = 0U; index < glyph_advance_count; ++index) {
        if (!std::isfinite(glyph_advances[index])) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
    }
    for (uint32_t index = 0U; index < glyph_offset_count; ++index) {
        if (!std::isfinite(glyph_offsets[index].advance_offset) ||
            !std::isfinite(glyph_offsets[index].ascender_offset)) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<IDWriteFontFace> face;
    HRESULT hr = reinterpret_cast<IUnknown*>(font_face)->QueryInterface(
        IID_PPV_ARGS(&face));
    ComPtr<ID2D1Brush> brush;
    if (SUCCEEDED(hr)) {
        hr = reinterpret_cast<IUnknown*>(foreground_brush)->QueryInterface(
            IID_PPV_ARGS(&brush));
    }
    if (SUCCEEDED(hr)) {
        const DWRITE_GLYPH_RUN run = {
            face.Get(),
            font_em_size,
            glyph_count,
            glyph_indices,
            glyph_advances,
            reinterpret_cast<const DWRITE_GLYPH_OFFSET*>(glyph_offsets),
            is_sideways != 0U,
            bidi_level};
        surface->d2d_context->DrawGlyphRun(
            D2D1::Point2F(baseline_origin_x, baseline_origin_y),
            &run,
            nullptr,
            brush.Get(),
            static_cast<DWRITE_MEASURING_MODE>(measuring_mode));
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_color_glyph_run(
    progpu_native_direct2d_surface* surface,
    float baseline_origin_x,
    float baseline_origin_y,
    float font_em_size,
    void* font_face,
    const uint16_t* glyph_indices,
    uint32_t glyph_count,
    const float* glyph_advances,
    uint32_t glyph_advance_count,
    const progpu_native_direct2d_glyph_offset* glyph_offsets,
    uint32_t glyph_offset_count,
    uint32_t is_sideways,
    uint32_t bidi_level,
    void* foreground_brush,
    uint32_t color_palette_index,
    progpu_native_direct2d_measuring_mode measuring_mode,
    progpu_native_direct2d_color_glyph_path* selected_path,
    int32_t* native_hresult)
{
    if (selected_path != nullptr) {
        *selected_path = static_cast<progpu_native_direct2d_color_glyph_path>(0);
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    constexpr uint32_t maximum_glyph_count = 1U << 20U;
    if (surface == nullptr || !std::isfinite(baseline_origin_x) ||
        !std::isfinite(baseline_origin_y) || !std::isfinite(font_em_size) ||
        font_em_size <= 0.0F || font_face == nullptr ||
        glyph_indices == nullptr || glyph_count == 0U ||
        glyph_count > maximum_glyph_count ||
        (glyph_advances == nullptr
            ? glyph_advance_count != 0U
            : glyph_advance_count != glyph_count) ||
        (glyph_offsets == nullptr
            ? glyph_offset_count != 0U
            : glyph_offset_count != glyph_count) ||
        is_sideways > 1U || bidi_level > 125U ||
        foreground_brush == nullptr || !is_valid(measuring_mode) ||
        selected_path == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    for (uint32_t index = 0U; index < glyph_advance_count; ++index) {
        if (!std::isfinite(glyph_advances[index])) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
    }
    for (uint32_t index = 0U; index < glyph_offset_count; ++index) {
        if (!std::isfinite(glyph_offsets[index].advance_offset) ||
            !std::isfinite(glyph_offsets[index].ascender_offset)) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<IDWriteFontFace> face;
    HRESULT hr = reinterpret_cast<IUnknown*>(font_face)->QueryInterface(
        IID_PPV_ARGS(&face));
    ComPtr<ID2D1Brush> foreground;
    if (SUCCEEDED(hr)) {
        hr = reinterpret_cast<IUnknown*>(foreground_brush)->QueryInterface(
            IID_PPV_ARGS(&foreground));
    }
    const DWRITE_GLYPH_RUN run = {
        face.Get(),
        font_em_size,
        glyph_count,
        glyph_indices,
        glyph_advances,
        reinterpret_cast<const DWRITE_GLYPH_OFFSET*>(glyph_offsets),
        is_sideways != 0U,
        bidi_level};
    const D2D1_POINT_2F origin =
        D2D1::Point2F(baseline_origin_x, baseline_origin_y);
    ComPtr<ID2D1DeviceContext7> context7;
    if (SUCCEEDED(hr)) {
        hr = surface->d2d_context.As(&context7);
    }
    if (SUCCEEDED(hr)) {
        context7->DrawGlyphRunWithColorSupport(
            origin,
            &run,
            nullptr,
            foreground.Get(),
            nullptr,
            color_palette_index,
            static_cast<DWRITE_MEASURING_MODE>(measuring_mode),
            D2D1_COLOR_BITMAP_GLYPH_SNAP_OPTION_DEFAULT);
        *selected_path =
            PROGPU_NATIVE_DIRECT2D_COLOR_GLYPH_PATH_DEVICE_CONTEXT7;
    } else if (hr == E_NOINTERFACE) {
        ComPtr<ID2D1DeviceContext4> context4;
        hr = surface->d2d_context.As(&context4);
        ComPtr<IDWriteFactory4> factory4;
        if (SUCCEEDED(hr)) {
            hr = surface->dwrite_factory.As(&factory4);
        }
        ComPtr<IDWriteColorGlyphRunEnumerator1> color_runs;
        if (SUCCEEDED(hr)) {
            constexpr DWRITE_GLYPH_IMAGE_FORMATS desired_formats =
                static_cast<DWRITE_GLYPH_IMAGE_FORMATS>(
                    DWRITE_GLYPH_IMAGE_FORMATS_TRUETYPE |
                    DWRITE_GLYPH_IMAGE_FORMATS_CFF |
                    DWRITE_GLYPH_IMAGE_FORMATS_COLR |
                    DWRITE_GLYPH_IMAGE_FORMATS_SVG |
                    DWRITE_GLYPH_IMAGE_FORMATS_PNG |
                    DWRITE_GLYPH_IMAGE_FORMATS_JPEG |
                    DWRITE_GLYPH_IMAGE_FORMATS_TIFF |
                    DWRITE_GLYPH_IMAGE_FORMATS_PREMULTIPLIED_B8G8R8A8);
            hr = factory4->TranslateColorGlyphRun(
                origin,
                &run,
                nullptr,
                desired_formats,
                static_cast<DWRITE_MEASURING_MODE>(measuring_mode),
                nullptr,
                color_palette_index,
                &color_runs);
        }
        if (hr == DWRITE_E_NOCOLOR) {
            context4->DrawGlyphRun(
                origin,
                &run,
                nullptr,
                foreground.Get(),
                static_cast<DWRITE_MEASURING_MODE>(measuring_mode));
            *selected_path =
                PROGPU_NATIVE_DIRECT2D_COLOR_GLYPH_PATH_MONOCHROME_NO_COLOR;
            hr = S_OK;
        } else if (SUCCEEDED(hr)) {
            ComPtr<ID2D1SolidColorBrush> color_brush;
            for (;;) {
                BOOL has_run = FALSE;
                hr = color_runs->MoveNext(&has_run);
                if (FAILED(hr) || has_run == FALSE) {
                    break;
                }
                const DWRITE_COLOR_GLYPH_RUN1* color_run = nullptr;
                hr = color_runs->GetCurrentRun(&color_run);
                if (FAILED(hr) || color_run == nullptr) {
                    if (SUCCEEDED(hr)) {
                        hr = E_UNEXPECTED;
                    }
                    break;
                }
                ID2D1Brush* run_brush = foreground.Get();
                if (color_run->paletteIndex != DWRITE_NO_PALETTE_INDEX) {
                    if (!color_brush) {
                        hr = context4->CreateSolidColorBrush(
                            color_run->runColor,
                            &color_brush);
                        if (FAILED(hr)) {
                            break;
                        }
                    } else {
                        color_brush->SetColor(color_run->runColor);
                    }
                    run_brush = color_brush.Get();
                }
                const D2D1_POINT_2F run_origin = D2D1::Point2F(
                    color_run->baselineOriginX,
                    color_run->baselineOriginY);
                switch (color_run->glyphImageFormat) {
                    case DWRITE_GLYPH_IMAGE_FORMATS_NONE:
                        break;
                    case DWRITE_GLYPH_IMAGE_FORMATS_PNG:
                    case DWRITE_GLYPH_IMAGE_FORMATS_JPEG:
                    case DWRITE_GLYPH_IMAGE_FORMATS_TIFF:
                    case DWRITE_GLYPH_IMAGE_FORMATS_PREMULTIPLIED_B8G8R8A8:
                        context4->DrawColorBitmapGlyphRun(
                            color_run->glyphImageFormat,
                            run_origin,
                            &color_run->glyphRun,
                            color_run->measuringMode,
                            D2D1_COLOR_BITMAP_GLYPH_SNAP_OPTION_DEFAULT);
                        break;
                    case DWRITE_GLYPH_IMAGE_FORMATS_SVG:
                        context4->DrawSvgGlyphRun(
                            run_origin,
                            &color_run->glyphRun,
                            run_brush,
                            nullptr,
                            color_palette_index,
                            color_run->measuringMode);
                        break;
                    default:
                        context4->DrawGlyphRun(
                            run_origin,
                            &color_run->glyphRun,
                            color_run->glyphRunDescription,
                            run_brush,
                            color_run->measuringMode);
                        break;
                }
            }
            if (SUCCEEDED(hr)) {
                *selected_path = PROGPU_NATIVE_DIRECT2D_COLOR_GLYPH_PATH_TRANSLATED_DEVICE_CONTEXT4;
            }
        }
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_svg_document(
    progpu_native_direct2d_surface* surface,
    const uint8_t* utf8_xml,
    uint32_t utf8_xml_byte_count,
    float viewport_width,
    float viewport_height,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    constexpr uint32_t maximum_svg_byte_count = 64U * 1024U * 1024U;
    if (surface == nullptr || value == nullptr || native_hresult == nullptr ||
        (utf8_xml_byte_count != 0U && utf8_xml == nullptr) ||
        utf8_xml_byte_count > maximum_svg_byte_count ||
        !std::isfinite(viewport_width) || viewport_width <= 0.0F ||
        !std::isfinite(viewport_height) || viewport_height <= 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1DeviceContext5> context5;
    HRESULT hr = surface->d2d_context.As(&context5);
    ComPtr<IStream> input;
    if (SUCCEEDED(hr) && utf8_xml_byte_count != 0U) {
        auto stream = new (std::nothrow) BorrowedMemoryStream(
            utf8_xml,
            utf8_xml_byte_count);
        if (stream == nullptr) {
            hr = E_OUTOFMEMORY;
        } else {
            input.Attach(stream);
        }
    }
    ComPtr<ID2D1SvgDocument> document;
    if (SUCCEEDED(hr)) {
        hr = context5->CreateSvgDocument(
            input.Get(),
            D2D1::SizeF(viewport_width, viewport_height),
            &document);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return hr == E_OUTOFMEMORY
            ? PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY
            : status_from_win2d_hresult(hr);
    }
    document->AddRef();
    *value = document.Get();
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_svg_document(
    progpu_native_direct2d_surface* surface,
    void* svg_document,
    float viewport_width,
    float viewport_height,
    float origin_x,
    float origin_y,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || svg_document == nullptr ||
        native_hresult == nullptr ||
        !std::isfinite(viewport_width) || viewport_width <= 0.0F ||
        !std::isfinite(viewport_height) || viewport_height <= 0.0F ||
        !std::isfinite(origin_x) || !std::isfinite(origin_y)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1SvgDocument> document;
    HRESULT hr = reinterpret_cast<IUnknown*>(svg_document)->QueryInterface(
        IID_PPV_ARGS(&document));
    ComPtr<ID2D1Factory> document_factory;
    if (SUCCEEDED(hr)) {
        document->GetFactory(&document_factory);
        if (!has_same_com_identity(
                document_factory.Get(),
                surface->d2d_factory.Get())) {
            hr = D2DERR_WRONG_RESOURCE_DOMAIN;
        }
    }
    ComPtr<ID2D1DeviceContext5> context5;
    if (SUCCEEDED(hr)) {
        hr = surface->d2d_context.As(&context5);
    }
    if (SUCCEEDED(hr)) {
        const D2D1_SIZE_F previous_viewport = document->GetViewportSize();
        D2D1_MATRIX_3X2_F previous_transform{};
        context5->GetTransform(&previous_transform);
        hr = document->SetViewportSize(
            D2D1::SizeF(viewport_width, viewport_height));
        if (SUCCEEDED(hr)) {
            D2D1_MATRIX_3X2_F translated = previous_transform;
            translated._31 = origin_x * previous_transform._11 +
                origin_y * previous_transform._21 +
                previous_transform._31;
            translated._32 = origin_x * previous_transform._12 +
                origin_y * previous_transform._22 +
                previous_transform._32;
            context5->SetTransform(&translated);
            context5->DrawSvgDocument(document.Get());
            context5->SetTransform(&previous_transform);
            hr = document->SetViewportSize(previous_viewport);
        }
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_text_layout(
    progpu_native_direct2d_surface* surface,
    float origin_x,
    float origin_y,
    void* text_layout,
    void* default_fill_brush,
    uint32_t options,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || !std::isfinite(origin_x) ||
        !std::isfinite(origin_y) || text_layout == nullptr ||
        default_fill_brush == nullptr || native_hresult == nullptr ||
        !is_valid_draw_text_options(options)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<IDWriteTextLayout> layout;
    HRESULT hr = reinterpret_cast<IUnknown*>(text_layout)->QueryInterface(
        IID_PPV_ARGS(&layout));
    ComPtr<ID2D1Brush> brush;
    if (SUCCEEDED(hr)) {
        hr = reinterpret_cast<IUnknown*>(default_fill_brush)->QueryInterface(
            IID_PPV_ARGS(&brush));
    }
    if (SUCCEEDED(hr)) {
        surface->d2d_context->DrawTextLayout(
            D2D1::Point2F(origin_x, origin_y),
            layout.Get(),
            brush.Get(),
            static_cast<D2D1_DRAW_TEXT_OPTIONS>(options));
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_rectangle_geometry(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_rect_f* rectangle,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || rectangle == nullptr || value == nullptr ||
        native_hresult == nullptr || !std::isfinite(rectangle->x) ||
        !std::isfinite(rectangle->y) || !std::isfinite(rectangle->width) ||
        !std::isfinite(rectangle->height) || rectangle->width < 0.0F ||
        rectangle->height < 0.0F ||
        !std::isfinite(rectangle->x + rectangle->width) ||
        !std::isfinite(rectangle->y + rectangle->height)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_RECT_F native_rectangle = {
        rectangle->x,
        rectangle->y,
        rectangle->x + rectangle->width,
        rectangle->y + rectangle->height
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1RectangleGeometry> geometry;
    HRESULT hr = surface->d2d_factory->CreateRectangleGeometry(
        native_rectangle,
        geometry.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(geometry, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_rounded_rectangle_geometry(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_rect_f* rectangle,
    float radius_x,
    float radius_y,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || rectangle == nullptr || value == nullptr ||
        native_hresult == nullptr || !std::isfinite(rectangle->x) ||
        !std::isfinite(rectangle->y) || !std::isfinite(rectangle->width) ||
        !std::isfinite(rectangle->height) || rectangle->width < 0.0F ||
        rectangle->height < 0.0F || !std::isfinite(radius_x) ||
        !std::isfinite(radius_y) || radius_x < 0.0F || radius_y < 0.0F ||
        !std::isfinite(rectangle->x + rectangle->width) ||
        !std::isfinite(rectangle->y + rectangle->height)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_ROUNDED_RECT native_rectangle = {
        {
            rectangle->x,
            rectangle->y,
            rectangle->x + rectangle->width,
            rectangle->y + rectangle->height
        },
        radius_x,
        radius_y
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1RoundedRectangleGeometry> geometry;
    HRESULT hr = surface->d2d_factory->CreateRoundedRectangleGeometry(
        native_rectangle,
        geometry.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(geometry, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_ellipse_geometry(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_point_2f* center,
    float radius_x,
    float radius_y,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || center == nullptr || value == nullptr ||
        native_hresult == nullptr || !is_finite(*center) ||
        !std::isfinite(radius_x) || !std::isfinite(radius_y) ||
        radius_x < 0.0F || radius_y < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_ELLIPSE native_ellipse = {
        {center->x, center->y},
        radius_x,
        radius_y
    };
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1EllipseGeometry> geometry;
    HRESULT hr = surface->d2d_factory->CreateEllipseGeometry(
        native_ellipse,
        geometry.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(geometry, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_path_geometry(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_fill_mode fill_mode,
    const progpu_native_direct2d_path_figure* figures,
    uint32_t figure_count,
    const progpu_native_direct2d_path_segment* segments,
    uint32_t segment_count,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || value == nullptr || native_hresult == nullptr ||
        (figure_count != 0U && figures == nullptr) ||
        (segment_count != 0U && segments == nullptr) ||
        (fill_mode != PROGPU_NATIVE_DIRECT2D_FILL_MODE_ALTERNATE &&
         fill_mode != PROGPU_NATIVE_DIRECT2D_FILL_MODE_WINDING)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    uint32_t expected_segment = 0U;
    for (uint32_t index = 0U; index < figure_count; ++index) {
        const auto& figure = figures[index];
        if (!is_finite(figure.start_point) || figure.reserved != 0U ||
            (figure.flags &
             ~(PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_FILLED |
               PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_CLOSED)) != 0U ||
            figure.first_segment != expected_segment ||
            figure.segment_count > segment_count - expected_segment) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
        expected_segment += figure.segment_count;
    }
    if (expected_segment != segment_count) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    for (uint32_t index = 0U; index < segment_count; ++index) {
        if (!is_geometry_path_segment_valid(segments[index])) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1PathGeometry1> path;
    HRESULT hr = create_path_geometry(*surface, path);
    ComPtr<ID2D1GeometrySink> sink;
    if (SUCCEEDED(hr)) {
        hr = path->Open(&sink);
    }
    if (SUCCEEDED(hr)) {
        sink->SetFillMode(static_cast<D2D1_FILL_MODE>(fill_mode));
        for (uint32_t figure_index = 0U;
             figure_index < figure_count;
             ++figure_index) {
            const auto& figure = figures[figure_index];
            sink->BeginFigure(
                {figure.start_point.x, figure.start_point.y},
                (figure.flags &
                 PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_FILLED) != 0U
                    ? D2D1_FIGURE_BEGIN_FILLED
                    : D2D1_FIGURE_BEGIN_HOLLOW);
            for (uint32_t offset = 0U;
                 offset < figure.segment_count;
                 ++offset) {
                const auto& segment =
                    segments[figure.first_segment + offset];
                sink->SetSegmentFlags(
                    static_cast<D2D1_PATH_SEGMENT>(segment.flags));
                switch (segment.kind) {
                    case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_LINE:
                        sink->AddLine({segment.point1.x, segment.point1.y});
                        break;
                    case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_QUADRATIC:
                        sink->AddQuadraticBezier({
                            {segment.point1.x, segment.point1.y},
                            {segment.point2.x, segment.point2.y}
                        });
                        break;
                    case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_CUBIC:
                        sink->AddBezier({
                            {segment.point1.x, segment.point1.y},
                            {segment.point2.x, segment.point2.y},
                            {segment.point3.x, segment.point3.y}
                        });
                        break;
                    case PROGPU_NATIVE_DIRECT2D_PATH_SEGMENT_ARC:
                        sink->AddArc({
                            {segment.point1.x, segment.point1.y},
                            {segment.size.x, segment.size.y},
                            segment.rotation_angle,
                            (segment.arc_flags &
                             PROGPU_NATIVE_DIRECT2D_ARC_FLAG_CLOCKWISE) != 0U
                                ? D2D1_SWEEP_DIRECTION_CLOCKWISE
                                : D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE,
                            (segment.arc_flags &
                             PROGPU_NATIVE_DIRECT2D_ARC_FLAG_LARGE) != 0U
                                ? D2D1_ARC_SIZE_LARGE
                                : D2D1_ARC_SIZE_SMALL
                        });
                        break;
                    default:
                        hr = E_INVALIDARG;
                        break;
                }
                if (FAILED(hr)) {
                    break;
                }
            }
            sink->EndFigure(
                (figure.flags &
                 PROGPU_NATIVE_DIRECT2D_PATH_FIGURE_FLAG_CLOSED) != 0U
                    ? D2D1_FIGURE_END_CLOSED
                    : D2D1_FIGURE_END_OPEN);
            if (FAILED(hr)) {
                break;
            }
        }
        HRESULT close_hr = sink->Close();
        if (SUCCEEDED(hr)) {
            hr = close_hr;
        }
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(path, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_transformed_geometry(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || transform == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        !is_finite(*transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform = to_native_matrix(*transform);
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1TransformedGeometry> result;
    HRESULT hr = surface->d2d_factory->CreateTransformedGeometry(
        reinterpret_cast<ID2D1Geometry*>(geometry),
        native_transform,
        result.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(result, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_combine_geometry(
    progpu_native_direct2d_surface* surface,
    void* geometry_a,
    void* geometry_b,
    progpu_native_direct2d_combine_mode combine_mode,
    const progpu_native_direct2d_matrix_3x2_f* geometry_b_transform,
    float flattening_tolerance,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry_a == nullptr || geometry_b == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        combine_mode < PROGPU_NATIVE_DIRECT2D_COMBINE_MODE_UNION ||
        combine_mode > PROGPU_NATIVE_DIRECT2D_COMBINE_MODE_EXCLUDE ||
        !std::isfinite(flattening_tolerance) || flattening_tolerance <= 0.0F ||
        (geometry_b_transform != nullptr &&
         !is_finite(*geometry_b_transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* native_transform_pointer = nullptr;
    if (geometry_b_transform != nullptr) {
        native_transform = to_native_matrix(*geometry_b_transform);
        native_transform_pointer = &native_transform;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1PathGeometry1> path;
    HRESULT hr = create_path_geometry(*surface, path);
    ComPtr<ID2D1GeometrySink> sink;
    if (SUCCEEDED(hr)) {
        hr = path->Open(&sink);
    }
    if (SUCCEEDED(hr)) {
        HRESULT combine_hr =
            reinterpret_cast<ID2D1Geometry*>(geometry_a)->CombineWithGeometry(
                reinterpret_cast<ID2D1Geometry*>(geometry_b),
                static_cast<D2D1_COMBINE_MODE>(combine_mode),
                native_transform_pointer,
                flattening_tolerance,
                sink.Get());
        HRESULT close_hr = sink->Close();
        hr = FAILED(combine_hr) ? combine_hr : close_hr;
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(path, value);
}

progpu_native_direct2d_status progpu_native_direct2d_geometry_get_bounds(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    progpu_native_direct2d_rect_f* bounds,
    int32_t* native_hresult)
{
    if (bounds != nullptr) {
        *bounds = {};
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || bounds == nullptr ||
        native_hresult == nullptr ||
        (transform != nullptr && !is_finite(*transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (transform != nullptr) {
        native_transform = to_native_matrix(*transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    D2D1_RECT_F native_bounds{};
    HRESULT hr = reinterpret_cast<ID2D1Geometry*>(geometry)->GetBounds(
        transform_pointer,
        &native_bounds);
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    bounds->x = native_bounds.left;
    bounds->y = native_bounds.top;
    bounds->width = native_bounds.right - native_bounds.left;
    bounds->height = native_bounds.bottom - native_bounds.top;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_geometry_get_widened_bounds(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    float stroke_width,
    void* stroke_style,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    float flattening_tolerance,
    progpu_native_direct2d_rect_f* bounds,
    int32_t* native_hresult)
{
    if (bounds != nullptr) {
        *bounds = {};
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || bounds == nullptr ||
        native_hresult == nullptr || !std::isfinite(stroke_width) ||
        stroke_width < 0.0F || !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        (transform != nullptr && !is_finite(*transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (transform != nullptr) {
        native_transform = to_native_matrix(*transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    D2D1_RECT_F native_bounds{};
    HRESULT hr = reinterpret_cast<ID2D1Geometry*>(geometry)->GetWidenedBounds(
        stroke_width,
        reinterpret_cast<ID2D1StrokeStyle*>(stroke_style),
        transform_pointer,
        flattening_tolerance,
        &native_bounds);
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    bounds->x = native_bounds.left;
    bounds->y = native_bounds.top;
    bounds->width = native_bounds.right - native_bounds.left;
    bounds->height = native_bounds.bottom - native_bounds.top;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_geometry_fill_contains_point(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    const progpu_native_direct2d_point_2f* point,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    float flattening_tolerance,
    uint32_t* contains,
    int32_t* native_hresult)
{
    if (contains != nullptr) {
        *contains = 0U;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || point == nullptr ||
        contains == nullptr || native_hresult == nullptr ||
        !is_finite(*point) || !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        (transform != nullptr && !is_finite(*transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (transform != nullptr) {
        native_transform = to_native_matrix(*transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    const D2D1_POINT_2F native_point = D2D1::Point2F(point->x, point->y);
    BOOL native_contains = FALSE;
    HRESULT hr = reinterpret_cast<ID2D1Geometry*>(geometry)->FillContainsPoint(
        native_point,
        transform_pointer,
        flattening_tolerance,
        &native_contains);
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    *contains = native_contains != FALSE ? 1U : 0U;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_geometry_stroke_contains_point(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    const progpu_native_direct2d_point_2f* point,
    float stroke_width,
    void* stroke_style,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    float flattening_tolerance,
    uint32_t* contains,
    int32_t* native_hresult)
{
    if (contains != nullptr) {
        *contains = 0U;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || point == nullptr ||
        contains == nullptr || native_hresult == nullptr ||
        !is_finite(*point) || !std::isfinite(stroke_width) ||
        stroke_width < 0.0F || !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        (transform != nullptr && !is_finite(*transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (transform != nullptr) {
        native_transform = to_native_matrix(*transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    const D2D1_POINT_2F native_point = D2D1::Point2F(point->x, point->y);
    BOOL native_contains = FALSE;
    HRESULT hr =
        reinterpret_cast<ID2D1Geometry*>(geometry)->StrokeContainsPoint(
            native_point,
            stroke_width,
            reinterpret_cast<ID2D1StrokeStyle*>(stroke_style),
            transform_pointer,
            flattening_tolerance,
            &native_contains);
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    *contains = native_contains != FALSE ? 1U : 0U;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status progpu_native_direct2d_geometry_compare(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    void* input_geometry,
    const progpu_native_direct2d_matrix_3x2_f* input_transform,
    float flattening_tolerance,
    progpu_native_direct2d_geometry_relation* relation,
    int32_t* native_hresult)
{
    if (relation != nullptr) {
        *relation = PROGPU_NATIVE_DIRECT2D_GEOMETRY_RELATION_UNKNOWN;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || input_geometry == nullptr ||
        relation == nullptr || native_hresult == nullptr ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        (input_transform != nullptr && !is_finite(*input_transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (input_transform != nullptr) {
        native_transform = to_native_matrix(*input_transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    D2D1_GEOMETRY_RELATION native_relation = D2D1_GEOMETRY_RELATION_UNKNOWN;
    HRESULT hr = reinterpret_cast<ID2D1Geometry*>(geometry)
        ->CompareWithGeometry(
            reinterpret_cast<ID2D1Geometry*>(input_geometry),
            transform_pointer,
            flattening_tolerance,
            &native_relation);
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    *relation = static_cast<progpu_native_direct2d_geometry_relation>(
        native_relation);
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status progpu_native_direct2d_geometry_compute_area(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    float flattening_tolerance,
    float* area,
    int32_t* native_hresult)
{
    if (area != nullptr) {
        *area = 0.0F;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || area == nullptr ||
        native_hresult == nullptr || !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        (transform != nullptr && !is_finite(*transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (transform != nullptr) {
        native_transform = to_native_matrix(*transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    HRESULT hr = reinterpret_cast<ID2D1Geometry*>(geometry)->ComputeArea(
        transform_pointer,
        flattening_tolerance,
        area);
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return FAILED(hr) ? status_from_win2d_hresult(hr)
                      : PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status progpu_native_direct2d_geometry_compute_length(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    float flattening_tolerance,
    float* length,
    int32_t* native_hresult)
{
    if (length != nullptr) {
        *length = 0.0F;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || length == nullptr ||
        native_hresult == nullptr || !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        (transform != nullptr && !is_finite(*transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (transform != nullptr) {
        native_transform = to_native_matrix(*transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    HRESULT hr = reinterpret_cast<ID2D1Geometry*>(geometry)->ComputeLength(
        transform_pointer,
        flattening_tolerance,
        length);
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return FAILED(hr) ? status_from_win2d_hresult(hr)
                      : PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_geometry_compute_point_at_length(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    float length,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    float flattening_tolerance,
    progpu_native_direct2d_point_2f* point,
    progpu_native_direct2d_point_2f* unit_tangent,
    int32_t* native_hresult)
{
    if (point != nullptr) {
        *point = {};
    }
    if (unit_tangent != nullptr) {
        *unit_tangent = {};
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || point == nullptr ||
        unit_tangent == nullptr || native_hresult == nullptr ||
        !std::isfinite(length) || length < 0.0F ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        (transform != nullptr && !is_finite(*transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (transform != nullptr) {
        native_transform = to_native_matrix(*transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    D2D1_POINT_2F native_point{};
    D2D1_POINT_2F native_tangent{};
    HRESULT hr = reinterpret_cast<ID2D1Geometry*>(geometry)
        ->ComputePointAtLength(
            length,
            transform_pointer,
            flattening_tolerance,
            &native_point,
            &native_tangent);
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    point->x = native_point.x;
    point->y = native_point.y;
    unit_tangent->x = native_tangent.x;
    unit_tangent->y = native_tangent.y;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status progpu_native_direct2d_geometry_simplify(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    progpu_native_direct2d_geometry_simplification_option option,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    float flattening_tolerance,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || value == nullptr ||
        native_hresult == nullptr ||
        option < PROGPU_NATIVE_DIRECT2D_GEOMETRY_SIMPLIFICATION_CUBICS_AND_LINES ||
        option > PROGPU_NATIVE_DIRECT2D_GEOMETRY_SIMPLIFICATION_LINES ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        (transform != nullptr && !is_finite(*transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (transform != nullptr) {
        native_transform = to_native_matrix(*transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Geometry> native_geometry;
    HRESULT hr = reinterpret_cast<IUnknown*>(geometry)->QueryInterface(
        IID_PPV_ARGS(&native_geometry));
    ComPtr<ID2D1PathGeometry1> path;
    if (SUCCEEDED(hr)) {
        hr = create_path_geometry(*surface, path);
    }
    ComPtr<ID2D1GeometrySink> sink;
    if (SUCCEEDED(hr)) {
        hr = path->Open(&sink);
    }
    if (SUCCEEDED(hr)) {
        const HRESULT operation_hr = native_geometry->Simplify(
            static_cast<D2D1_GEOMETRY_SIMPLIFICATION_OPTION>(option),
            transform_pointer,
            flattening_tolerance,
            sink.Get());
        const HRESULT close_hr = sink->Close();
        hr = FAILED(operation_hr) ? operation_hr : close_hr;
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(path, value);
}

progpu_native_direct2d_status progpu_native_direct2d_geometry_outline(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    float flattening_tolerance,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || value == nullptr ||
        native_hresult == nullptr ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        (transform != nullptr && !is_finite(*transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (transform != nullptr) {
        native_transform = to_native_matrix(*transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Geometry> native_geometry;
    HRESULT hr = reinterpret_cast<IUnknown*>(geometry)->QueryInterface(
        IID_PPV_ARGS(&native_geometry));
    ComPtr<ID2D1PathGeometry1> path;
    if (SUCCEEDED(hr)) {
        hr = create_path_geometry(*surface, path);
    }
    ComPtr<ID2D1GeometrySink> sink;
    if (SUCCEEDED(hr)) {
        hr = path->Open(&sink);
    }
    if (SUCCEEDED(hr)) {
        const HRESULT operation_hr = native_geometry->Outline(
            transform_pointer,
            flattening_tolerance,
            sink.Get());
        const HRESULT close_hr = sink->Close();
        hr = FAILED(operation_hr) ? operation_hr : close_hr;
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(path, value);
}

progpu_native_direct2d_status progpu_native_direct2d_geometry_widen(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    float stroke_width,
    void* stroke_style,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    float flattening_tolerance,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || value == nullptr ||
        native_hresult == nullptr || !std::isfinite(stroke_width) ||
        stroke_width < 0.0F || !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        (transform != nullptr && !is_finite(*transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (transform != nullptr) {
        native_transform = to_native_matrix(*transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Geometry> native_geometry;
    HRESULT hr = reinterpret_cast<IUnknown*>(geometry)->QueryInterface(
        IID_PPV_ARGS(&native_geometry));
    ComPtr<ID2D1StrokeStyle> native_stroke_style;
    if (SUCCEEDED(hr) && stroke_style != nullptr) {
        hr = reinterpret_cast<IUnknown*>(stroke_style)->QueryInterface(
            IID_PPV_ARGS(&native_stroke_style));
    }
    ComPtr<ID2D1PathGeometry1> path;
    if (SUCCEEDED(hr)) {
        hr = create_path_geometry(*surface, path);
    }
    ComPtr<ID2D1GeometrySink> sink;
    if (SUCCEEDED(hr)) {
        hr = path->Open(&sink);
    }
    if (SUCCEEDED(hr)) {
        const HRESULT operation_hr = native_geometry->Widen(
            stroke_width,
            native_stroke_style.Get(),
            transform_pointer,
            flattening_tolerance,
            sink.Get());
        const HRESULT close_hr = sink->Close();
        hr = FAILED(operation_hr) ? operation_hr : close_hr;
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(path, value);
}

progpu_native_direct2d_status progpu_native_direct2d_geometry_tessellate(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    float flattening_tolerance,
    progpu_native_direct2d_triangle* triangles,
    uint32_t triangle_capacity,
    uint32_t* triangle_count,
    int32_t* native_hresult)
{
    if (triangle_count != nullptr) {
        *triangle_count = 0U;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr ||
        triangle_count == nullptr || native_hresult == nullptr ||
        (triangle_capacity != 0U && triangles == nullptr) ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F ||
        (transform != nullptr && !is_finite(*transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    const D2D1_MATRIX_3X2_F* transform_pointer = nullptr;
    if (transform != nullptr) {
        native_transform = to_native_matrix(*transform);
        transform_pointer = &native_transform;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Geometry> native_geometry;
    HRESULT hr = reinterpret_cast<IUnknown*>(geometry)->QueryInterface(
        IID_PPV_ARGS(&native_geometry));
    CallerTessellationSink* sink = nullptr;
    if (SUCCEEDED(hr)) {
        sink = new (std::nothrow) CallerTessellationSink(
            triangles,
            triangle_capacity);
        if (sink == nullptr) {
            hr = E_OUTOFMEMORY;
        }
    }
    if (SUCCEEDED(hr)) {
        const HRESULT operation_hr = native_geometry->Tessellate(
            transform_pointer,
            flattening_tolerance,
            sink);
        const HRESULT close_hr = sink->Close();
        *triangle_count = sink->required_count();
        hr = FAILED(operation_hr) ? operation_hr : close_hr;
    }
    if (sink != nullptr) {
        static_cast<void>(sink->Release());
    }
    if (SUCCEEDED(hr) && *triangle_count > triangle_capacity) {
        hr = HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        surface->last_hresult.store(hr, std::memory_order_release);
        *native_hresult = hr;
        return PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER;
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return hr == E_OUTOFMEMORY
            ? PROGPU_NATIVE_DIRECT2D_STATUS_OUT_OF_MEMORY
            : status_from_win2d_hresult(hr);
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_filled_geometry_realization(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    float flattening_tolerance,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || value == nullptr ||
        native_hresult == nullptr ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Geometry> native_geometry;
    HRESULT hr = reinterpret_cast<IUnknown*>(geometry)->QueryInterface(
        IID_PPV_ARGS(&native_geometry));
    ComPtr<ID2D1GeometryRealization> realization;
    if (SUCCEEDED(hr)) {
        hr = surface->d2d_context->CreateFilledGeometryRealization(
            native_geometry.Get(),
            flattening_tolerance,
            &realization);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(realization, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_stroked_geometry_realization(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    float flattening_tolerance,
    float stroke_width,
    void* stroke_style,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || value == nullptr ||
        native_hresult == nullptr ||
        !std::isfinite(flattening_tolerance) ||
        flattening_tolerance <= 0.0F || !std::isfinite(stroke_width) ||
        stroke_width < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Geometry> native_geometry;
    HRESULT hr = reinterpret_cast<IUnknown*>(geometry)->QueryInterface(
        IID_PPV_ARGS(&native_geometry));
    ComPtr<ID2D1StrokeStyle> native_stroke_style;
    if (SUCCEEDED(hr) && stroke_style != nullptr) {
        hr = reinterpret_cast<IUnknown*>(stroke_style)->QueryInterface(
            IID_PPV_ARGS(&native_stroke_style));
    }
    ComPtr<ID2D1GeometryRealization> realization;
    if (SUCCEEDED(hr)) {
        hr = surface->d2d_context->CreateStrokedGeometryRealization(
            native_geometry.Get(),
            flattening_tolerance,
            stroke_width,
            native_stroke_style.Get(),
            &realization);
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(realization, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_geometry_realization(
    progpu_native_direct2d_surface* surface,
    void* realization,
    void* brush,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || realization == nullptr || brush == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1GeometryRealization> native_realization;
    HRESULT hr = reinterpret_cast<IUnknown*>(realization)->QueryInterface(
        IID_PPV_ARGS(&native_realization));
    ComPtr<ID2D1Brush> native_brush;
    if (SUCCEEDED(hr)) {
        hr = reinterpret_cast<IUnknown*>(brush)->QueryInterface(
            IID_PPV_ARGS(&native_brush));
    }
    if (SUCCEEDED(hr)) {
        surface->d2d_context->DrawGeometryRealization(
            native_realization.Get(),
            native_brush.Get());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    return SUCCEEDED(hr)
        ? PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS
        : status_from_win2d_hresult(hr);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_clear(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_color_f* color,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || native_hresult == nullptr ||
        (color != nullptr && !is_finite(*color))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    D2D1_COLOR_F native_color{};
    const D2D1_COLOR_F* native_color_pointer = nullptr;
    if (color != nullptr) {
        native_color = D2D1::ColorF(
            color->red,
            color->green,
            color->blue,
            color->alpha);
        native_color_pointer = &native_color;
    }
    surface->d2d_context->Clear(native_color_pointer);
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_set_transform(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_matrix_3x2_f* transform,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || transform == nullptr ||
        native_hresult == nullptr || !is_finite(*transform)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    surface->d2d_context->SetTransform(to_native_matrix(*transform));
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_get_transform(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_matrix_3x2_f* transform,
    int32_t* native_hresult)
{
    if (transform != nullptr) {
        *transform = {};
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || transform == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    D2D1_MATRIX_3X2_F native_transform{};
    surface->d2d_context->GetTransform(&native_transform);
    transform->m11 = native_transform._11;
    transform->m12 = native_transform._12;
    transform->m21 = native_transform._21;
    transform->m22 = native_transform._22;
    transform->m31 = native_transform._31;
    transform->m32 = native_transform._32;
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_set_antialias_mode(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_antialias_mode mode,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || native_hresult == nullptr || !is_valid(mode)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    surface->d2d_context->SetAntialiasMode(
        static_cast<D2D1_ANTIALIAS_MODE>(mode));
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_get_antialias_mode(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_antialias_mode* mode,
    int32_t* native_hresult)
{
    if (mode != nullptr) {
        *mode = PROGPU_NATIVE_DIRECT2D_ANTIALIAS_MODE_PER_PRIMITIVE;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || mode == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    *mode = static_cast<progpu_native_direct2d_antialias_mode>(
        surface->d2d_context->GetAntialiasMode());
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_set_text_antialias_mode(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_text_antialias_mode mode,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || native_hresult == nullptr || !is_valid(mode)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    surface->d2d_context->SetTextAntialiasMode(
        static_cast<D2D1_TEXT_ANTIALIAS_MODE>(mode));
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_get_text_antialias_mode(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_text_antialias_mode* mode,
    int32_t* native_hresult)
{
    if (mode != nullptr) {
        *mode = PROGPU_NATIVE_DIRECT2D_TEXT_ANTIALIAS_MODE_DEFAULT;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || mode == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    *mode = static_cast<progpu_native_direct2d_text_antialias_mode>(
        surface->d2d_context->GetTextAntialiasMode());
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_set_primitive_blend(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_primitive_blend blend,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || native_hresult == nullptr || !is_valid(blend)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    surface->d2d_context->SetPrimitiveBlend(
        static_cast<D2D1_PRIMITIVE_BLEND>(blend));
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_get_primitive_blend(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_primitive_blend* blend,
    int32_t* native_hresult)
{
    if (blend != nullptr) {
        *blend = PROGPU_NATIVE_DIRECT2D_PRIMITIVE_BLEND_SOURCE_OVER;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || blend == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    *blend = static_cast<progpu_native_direct2d_primitive_blend>(
        surface->d2d_context->GetPrimitiveBlend());
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_set_unit_mode(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_unit_mode mode,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || native_hresult == nullptr || !is_valid(mode)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    surface->d2d_context->SetUnitMode(static_cast<D2D1_UNIT_MODE>(mode));
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_get_unit_mode(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_unit_mode* mode,
    int32_t* native_hresult)
{
    if (mode != nullptr) {
        *mode = PROGPU_NATIVE_DIRECT2D_UNIT_MODE_DIPS;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || mode == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    *mode = static_cast<progpu_native_direct2d_unit_mode>(
        surface->d2d_context->GetUnitMode());
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_set_tags(
    progpu_native_direct2d_surface* surface,
    uint64_t tag1,
    uint64_t tag2,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    surface->d2d_context->SetTags(tag1, tag2);
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_get_tags(
    progpu_native_direct2d_surface* surface,
    uint64_t* tag1,
    uint64_t* tag2,
    int32_t* native_hresult)
{
    if (tag1 != nullptr) {
        *tag1 = 0U;
    }
    if (tag2 != nullptr) {
        *tag2 = 0U;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || tag1 == nullptr || tag2 == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    surface->d2d_context->GetTags(tag1, tag2);
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_set_dpi(
    progpu_native_direct2d_surface* surface,
    float dpi_x,
    float dpi_y,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    const bool reset_to_default = dpi_x == 0.0F && dpi_y == 0.0F;
    if (surface == nullptr || native_hresult == nullptr ||
        !std::isfinite(dpi_x) || !std::isfinite(dpi_y) ||
        (!reset_to_default && (dpi_x <= 0.0F || dpi_y <= 0.0F))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    surface->d2d_context->SetDpi(dpi_x, dpi_y);
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_get_dpi(
    progpu_native_direct2d_surface* surface,
    float* dpi_x,
    float* dpi_y,
    int32_t* native_hresult)
{
    if (dpi_x != nullptr) {
        *dpi_x = 0.0F;
    }
    if (dpi_y != nullptr) {
        *dpi_y = 0.0F;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || dpi_x == nullptr || dpi_y == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    surface->d2d_context->GetDpi(dpi_x, dpi_y);
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_line(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_point_2f point0,
    progpu_native_direct2d_point_2f point1,
    void* brush,
    float stroke_width,
    void* stroke_style,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr ||
        native_hresult == nullptr || !is_finite(point0) ||
        !is_finite(point1) || !std::isfinite(stroke_width) ||
        stroke_width < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1Brush> native_brush;
    HRESULT hr = query_brush(brush, native_brush);
    ComPtr<ID2D1StrokeStyle> native_stroke_style;
    if (SUCCEEDED(hr)) {
        hr = query_optional_stroke_style(
            stroke_style,
            native_stroke_style);
    }
    if (SUCCEEDED(hr)) {
        surface->d2d_context->DrawLine(
            D2D1::Point2F(point0.x, point0.y),
            D2D1::Point2F(point1.x, point1.y),
            native_brush.Get(),
            stroke_width,
            native_stroke_style.Get());
    }
    return finish_draw_command(*surface, hr, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_rectangle(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_rect_f* rectangle,
    void* brush,
    float stroke_width,
    void* stroke_style,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || rectangle == nullptr || brush == nullptr ||
        native_hresult == nullptr || !is_valid(*rectangle) ||
        !std::isfinite(stroke_width) || stroke_width < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1Brush> native_brush;
    HRESULT hr = query_brush(brush, native_brush);
    ComPtr<ID2D1StrokeStyle> native_stroke_style;
    if (SUCCEEDED(hr)) {
        hr = query_optional_stroke_style(
            stroke_style,
            native_stroke_style);
    }
    if (SUCCEEDED(hr)) {
        surface->d2d_context->DrawRectangle(
            to_native_rect(*rectangle),
            native_brush.Get(),
            stroke_width,
            native_stroke_style.Get());
    }
    return finish_draw_command(*surface, hr, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_fill_rectangle(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_rect_f* rectangle,
    void* brush,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || rectangle == nullptr || brush == nullptr ||
        native_hresult == nullptr || !is_valid(*rectangle)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1Brush> native_brush;
    HRESULT hr = query_brush(brush, native_brush);
    if (SUCCEEDED(hr)) {
        surface->d2d_context->FillRectangle(
            to_native_rect(*rectangle),
            native_brush.Get());
    }
    return finish_draw_command(*surface, hr, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_rounded_rectangle(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_rect_f* rectangle,
    float radius_x,
    float radius_y,
    void* brush,
    float stroke_width,
    void* stroke_style,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || rectangle == nullptr || brush == nullptr ||
        native_hresult == nullptr || !is_valid(*rectangle) ||
        !std::isfinite(radius_x) || radius_x < 0.0F ||
        !std::isfinite(radius_y) || radius_y < 0.0F ||
        !std::isfinite(stroke_width) || stroke_width < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1Brush> native_brush;
    HRESULT hr = query_brush(brush, native_brush);
    ComPtr<ID2D1StrokeStyle> native_stroke_style;
    if (SUCCEEDED(hr)) {
        hr = query_optional_stroke_style(
            stroke_style,
            native_stroke_style);
    }
    if (SUCCEEDED(hr)) {
        const D2D1_ROUNDED_RECT rounded_rectangle = {
            to_native_rect(*rectangle),
            radius_x,
            radius_y
        };
        surface->d2d_context->DrawRoundedRectangle(
            rounded_rectangle,
            native_brush.Get(),
            stroke_width,
            native_stroke_style.Get());
    }
    return finish_draw_command(*surface, hr, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_fill_rounded_rectangle(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_rect_f* rectangle,
    float radius_x,
    float radius_y,
    void* brush,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || rectangle == nullptr || brush == nullptr ||
        native_hresult == nullptr || !is_valid(*rectangle) ||
        !std::isfinite(radius_x) || radius_x < 0.0F ||
        !std::isfinite(radius_y) || radius_y < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1Brush> native_brush;
    HRESULT hr = query_brush(brush, native_brush);
    if (SUCCEEDED(hr)) {
        const D2D1_ROUNDED_RECT rounded_rectangle = {
            to_native_rect(*rectangle),
            radius_x,
            radius_y
        };
        surface->d2d_context->FillRoundedRectangle(
            rounded_rectangle,
            native_brush.Get());
    }
    return finish_draw_command(*surface, hr, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_ellipse(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_point_2f center,
    float radius_x,
    float radius_y,
    void* brush,
    float stroke_width,
    void* stroke_style,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr ||
        native_hresult == nullptr || !is_finite(center) ||
        !std::isfinite(radius_x) || radius_x < 0.0F ||
        !std::isfinite(radius_y) || radius_y < 0.0F ||
        !std::isfinite(stroke_width) || stroke_width < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1Brush> native_brush;
    HRESULT hr = query_brush(brush, native_brush);
    ComPtr<ID2D1StrokeStyle> native_stroke_style;
    if (SUCCEEDED(hr)) {
        hr = query_optional_stroke_style(
            stroke_style,
            native_stroke_style);
    }
    if (SUCCEEDED(hr)) {
        const D2D1_ELLIPSE ellipse = {
            D2D1::Point2F(center.x, center.y),
            radius_x,
            radius_y
        };
        surface->d2d_context->DrawEllipse(
            ellipse,
            native_brush.Get(),
            stroke_width,
            native_stroke_style.Get());
    }
    return finish_draw_command(*surface, hr, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_fill_ellipse(
    progpu_native_direct2d_surface* surface,
    progpu_native_direct2d_point_2f center,
    float radius_x,
    float radius_y,
    void* brush,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || brush == nullptr ||
        native_hresult == nullptr || !is_finite(center) ||
        !std::isfinite(radius_x) || radius_x < 0.0F ||
        !std::isfinite(radius_y) || radius_y < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1Brush> native_brush;
    HRESULT hr = query_brush(brush, native_brush);
    if (SUCCEEDED(hr)) {
        const D2D1_ELLIPSE ellipse = {
            D2D1::Point2F(center.x, center.y),
            radius_x,
            radius_y
        };
        surface->d2d_context->FillEllipse(ellipse, native_brush.Get());
    }
    return finish_draw_command(*surface, hr, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_geometry(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    void* brush,
    float stroke_width,
    void* stroke_style,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || brush == nullptr ||
        native_hresult == nullptr || !std::isfinite(stroke_width) ||
        stroke_width < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1Geometry> native_geometry;
    HRESULT hr = reinterpret_cast<IUnknown*>(geometry)->QueryInterface(
        IID_PPV_ARGS(&native_geometry));
    ComPtr<ID2D1Brush> native_brush;
    if (SUCCEEDED(hr)) {
        hr = query_brush(brush, native_brush);
    }
    ComPtr<ID2D1StrokeStyle> native_stroke_style;
    if (SUCCEEDED(hr)) {
        hr = query_optional_stroke_style(
            stroke_style,
            native_stroke_style);
    }
    if (SUCCEEDED(hr)) {
        surface->d2d_context->DrawGeometry(
            native_geometry.Get(),
            native_brush.Get(),
            stroke_width,
            native_stroke_style.Get());
    }
    return finish_draw_command(*surface, hr, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_fill_geometry(
    progpu_native_direct2d_surface* surface,
    void* geometry,
    void* brush,
    void* opacity_brush,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || geometry == nullptr || brush == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1Geometry> native_geometry;
    HRESULT hr = reinterpret_cast<IUnknown*>(geometry)->QueryInterface(
        IID_PPV_ARGS(&native_geometry));
    ComPtr<ID2D1Brush> native_brush;
    if (SUCCEEDED(hr)) {
        hr = query_brush(brush, native_brush);
    }
    ComPtr<ID2D1Brush> native_opacity_brush;
    if (SUCCEEDED(hr) && opacity_brush != nullptr) {
        hr = query_brush(opacity_brush, native_opacity_brush);
    }
    if (SUCCEEDED(hr)) {
        surface->d2d_context->FillGeometry(
            native_geometry.Get(),
            native_brush.Get(),
            native_opacity_brush.Get());
    }
    return finish_draw_command(*surface, hr, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_push_axis_aligned_clip(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_rect_f* clip_rectangle,
    progpu_native_direct2d_antialias_mode antialias_mode,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || clip_rectangle == nullptr ||
        native_hresult == nullptr || !is_valid(*clip_rectangle) ||
        !is_valid(antialias_mode)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    if (!can_push_draw_scope(*surface)) {
        surface->last_hresult.store(
            D2DERR_WRONG_STATE,
            std::memory_order_release);
        *native_hresult = D2DERR_WRONG_STATE;
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    surface->d2d_context->PushAxisAlignedClip(
        to_native_rect(*clip_rectangle),
        static_cast<D2D1_ANTIALIAS_MODE>(antialias_mode));
    push_draw_scope(
        *surface,
        progpu_direct2d_draw_scope_kind::axis_aligned_clip);
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_pop_axis_aligned_clip(
    progpu_native_direct2d_surface* surface,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    if (surface->draw_scope_depth == 0U ||
        surface->draw_scopes[surface->draw_scope_depth - 1U] !=
            progpu_direct2d_draw_scope_kind::axis_aligned_clip) {
        surface->last_hresult.store(
            D2DERR_WRONG_STATE,
            std::memory_order_release);
        *native_hresult = D2DERR_WRONG_STATE;
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    surface->d2d_context->PopAxisAlignedClip();
    --surface->draw_scope_depth;
    return finish_draw_command(*surface, S_OK, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_bitmap(
    progpu_native_direct2d_surface* surface,
    void* bitmap,
    const progpu_native_direct2d_rect_f* destination_rectangle,
    float opacity,
    progpu_native_direct2d_interpolation_mode interpolation_mode,
    const progpu_native_direct2d_rect_f* source_rectangle,
    const progpu_native_direct2d_matrix_4x4_f* perspective_transform,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || bitmap == nullptr ||
        native_hresult == nullptr ||
        (destination_rectangle != nullptr &&
            !is_valid(*destination_rectangle)) ||
        !std::isfinite(opacity) || opacity < 0.0F || opacity > 1.0F ||
        !is_valid_interpolation_mode(
            static_cast<uint32_t>(interpolation_mode)) ||
        (source_rectangle != nullptr && !is_valid(*source_rectangle)) ||
        (perspective_transform != nullptr &&
            !is_finite(*perspective_transform))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1Bitmap> native_bitmap;
    HRESULT hr = reinterpret_cast<IUnknown*>(bitmap)->QueryInterface(
        IID_PPV_ARGS(&native_bitmap));
    if (SUCCEEDED(hr)) {
        D2D1_RECT_F native_destination{};
        const D2D1_RECT_F* native_destination_pointer = nullptr;
        if (destination_rectangle != nullptr) {
            native_destination = to_native_rect(*destination_rectangle);
            native_destination_pointer = &native_destination;
        }
        D2D1_RECT_F native_source{};
        const D2D1_RECT_F* native_source_pointer = nullptr;
        if (source_rectangle != nullptr) {
            native_source = to_native_rect(*source_rectangle);
            native_source_pointer = &native_source;
        }
        D2D1_MATRIX_4X4_F native_perspective{};
        const D2D1_MATRIX_4X4_F* native_perspective_pointer = nullptr;
        if (perspective_transform != nullptr) {
            native_perspective = to_native_matrix(*perspective_transform);
            native_perspective_pointer = &native_perspective;
        }
        surface->d2d_context->DrawBitmap(
            native_bitmap.Get(),
            native_destination_pointer,
            opacity,
            static_cast<D2D1_INTERPOLATION_MODE>(interpolation_mode),
            native_source_pointer,
            native_perspective_pointer);
    }
    return finish_draw_command(*surface, hr, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_draw_image(
    progpu_native_direct2d_surface* surface,
    void* image,
    const progpu_native_direct2d_point_2f* target_offset,
    const progpu_native_direct2d_rect_f* image_rectangle,
    progpu_native_direct2d_interpolation_mode interpolation_mode,
    progpu_native_direct2d_composite_mode composite_mode,
    int32_t* native_hresult)
{
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || image == nullptr ||
        native_hresult == nullptr ||
        (target_offset != nullptr && !is_finite(*target_offset)) ||
        (image_rectangle != nullptr && !is_valid(*image_rectangle)) ||
        !is_valid_interpolation_mode(
            static_cast<uint32_t>(interpolation_mode)) ||
        !is_valid(composite_mode)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    ComPtr<ID2D1Image> native_image;
    HRESULT hr = reinterpret_cast<IUnknown*>(image)->QueryInterface(
        IID_PPV_ARGS(&native_image));
    if (SUCCEEDED(hr)) {
        D2D1_POINT_2F native_offset{};
        const D2D1_POINT_2F* native_offset_pointer = nullptr;
        if (target_offset != nullptr) {
            native_offset = D2D1::Point2F(
                target_offset->x,
                target_offset->y);
            native_offset_pointer = &native_offset;
        }
        D2D1_RECT_F native_rectangle{};
        const D2D1_RECT_F* native_rectangle_pointer = nullptr;
        if (image_rectangle != nullptr) {
            native_rectangle = to_native_rect(*image_rectangle);
            native_rectangle_pointer = &native_rectangle;
        }
        surface->d2d_context->DrawImage(
            native_image.Get(),
            native_offset_pointer,
            native_rectangle_pointer,
            static_cast<D2D1_INTERPOLATION_MODE>(interpolation_mode),
            static_cast<D2D1_COMPOSITE_MODE>(composite_mode));
    }
    return finish_draw_command(*surface, hr, *native_hresult);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_create_stroke_style(
    progpu_native_direct2d_surface* surface,
    const progpu_native_direct2d_stroke_style_properties* properties,
    const float* dashes,
    uint32_t dash_count,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || properties == nullptr || value == nullptr ||
        native_hresult == nullptr ||
        !is_valid_cap_style(properties->start_cap) ||
        !is_valid_cap_style(properties->end_cap) ||
        !is_valid_cap_style(properties->dash_cap) ||
        !is_valid_line_join(properties->line_join) ||
        !std::isfinite(properties->miter_limit) ||
        properties->miter_limit <= 0.0F ||
        !is_valid_dash_style(properties->dash_style) ||
        !std::isfinite(properties->dash_offset) ||
        !is_valid_stroke_transform_type(properties->transform_type) ||
        ((properties->dash_style ==
              PROGPU_NATIVE_DIRECT2D_DASH_STYLE_CUSTOM) !=
            (dashes != nullptr && dash_count != 0U)) ||
        (properties->dash_style !=
             PROGPU_NATIVE_DIRECT2D_DASH_STYLE_CUSTOM &&
         (dashes != nullptr || dash_count != 0U))) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    bool has_positive_dash = false;
    for (uint32_t index = 0U; index < dash_count; ++index) {
        if (!std::isfinite(dashes[index]) || dashes[index] < 0.0F) {
            return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
        }
        has_positive_dash = has_positive_dash || dashes[index] > 0.0F;
    }
    if (dash_count != 0U && !has_positive_dash) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    D2D1_STROKE_STYLE_PROPERTIES1 native_properties{};
    native_properties.startCap =
        static_cast<D2D1_CAP_STYLE>(properties->start_cap);
    native_properties.endCap =
        static_cast<D2D1_CAP_STYLE>(properties->end_cap);
    native_properties.dashCap =
        static_cast<D2D1_CAP_STYLE>(properties->dash_cap);
    native_properties.lineJoin =
        static_cast<D2D1_LINE_JOIN>(properties->line_join);
    native_properties.miterLimit = properties->miter_limit;
    native_properties.dashStyle =
        static_cast<D2D1_DASH_STYLE>(properties->dash_style);
    native_properties.dashOffset = properties->dash_offset;
    native_properties.transformType =
        static_cast<D2D1_STROKE_TRANSFORM_TYPE>(properties->transform_type);

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<ID2D1Factory1> factory;
    HRESULT hr = surface->d2d_factory.As(&factory);
    ComPtr<ID2D1StrokeStyle1> stroke_style;
    if (SUCCEEDED(hr)) {
        hr = factory->CreateStrokeStyle(
            native_properties,
            dashes,
            dash_count,
            stroke_style.GetAddressOf());
    }
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(stroke_style, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_or_create_win2d_wrapper(
    progpu_native_direct2d_surface* surface,
    void* native_resource,
    float dpi,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || native_resource == nullptr || value == nullptr ||
        native_hresult == nullptr || !std::isfinite(dpi) || dpi < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    ComPtr<IInspectable> wrapper;
    HRESULT hr = create_win2d_wrapper(
        *surface,
        reinterpret_cast<IUnknown*>(native_resource),
        dpi,
        wrapper.GetAddressOf());
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        return status_from_win2d_hresult(hr);
    }
    return return_interface(wrapper, value);
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_try_get_win2d_wrapper_native_resource(
    progpu_native_direct2d_surface* surface,
    void* wrapper,
    float dpi,
    const progpu_native_direct2d_guid* interface_id,
    void** value,
    int32_t* native_hresult)
{
    if (value != nullptr) {
        *value = nullptr;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || wrapper == nullptr || interface_id == nullptr ||
        value == nullptr || native_hresult == nullptr ||
        !std::isfinite(dpi) || dpi < 0.0F) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    GUID native_interface_id = to_native_guid(*interface_id);
    HRESULT hr = get_win2d_wrapper_native_resource(
        *surface,
        reinterpret_cast<IUnknown*>(wrapper),
        dpi,
        native_interface_id,
        value);
    surface->last_hresult.store(hr, std::memory_order_release);
    *native_hresult = hr;
    if (FAILED(hr)) {
        *value = nullptr;
        return status_from_win2d_hresult(hr);
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status progpu_native_direct2d_surface_acquire(
    progpu_native_direct2d_surface* surface,
    uint64_t acquire_key,
    uint32_t timeout_milliseconds)
{
    if (surface == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    return acquire_locked(
        *surface,
        acquire_key,
        timeout_milliseconds);
}

progpu_native_direct2d_status progpu_native_direct2d_surface_release(
    progpu_native_direct2d_surface* surface,
    uint64_t release_key)
{
    if (surface == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    return release_locked(*surface, release_key, true);
}

progpu_native_direct2d_status progpu_native_direct2d_surface_begin_draw(
    progpu_native_direct2d_surface* surface,
    uint64_t acquire_key,
    uint32_t timeout_milliseconds)
{
    if (surface == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    progpu_native_direct2d_status status = acquire_locked(
        *surface,
        acquire_key,
        timeout_milliseconds);
    if (status != PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS) {
        return status;
    }
    surface->d2d_context->BeginDraw();
    surface->draw_active = true;
    surface->command_list_draw_active = false;
    surface->draw_scope_depth = 0U;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status progpu_native_direct2d_surface_end_draw(
    progpu_native_direct2d_surface* surface,
    uint64_t release_key,
    uint64_t* tag1,
    uint64_t* tag2,
    int32_t* native_hresult)
{
    if (tag1 != nullptr) {
        *tag1 = 0U;
    }
    if (tag2 != nullptr) {
        *tag2 = 0U;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || tag1 == nullptr || tag2 == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active || surface->command_list_draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }
    bool unbalanced_scopes = surface->draw_scope_depth != 0U;
    unwind_draw_scopes(*surface);
    D2D1_TAG native_tag1 = 0U;
    D2D1_TAG native_tag2 = 0U;
    HRESULT draw_hr = surface->d2d_context->EndDraw(
        &native_tag1,
        &native_tag2);
    if (unbalanced_scopes && SUCCEEDED(draw_hr)) {
        draw_hr = D2DERR_WRONG_STATE;
    }
    surface->draw_active = false;
    *tag1 = native_tag1;
    *tag2 = native_tag2;
    *native_hresult = draw_hr;

    progpu_native_direct2d_status release_status =
        release_locked(*surface, release_key, SUCCEEDED(draw_hr));
    if (release_status != PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS) {
        return release_status;
    }
    surface->last_hresult.store(draw_hr, std::memory_order_release);
    if (SUCCEEDED(draw_hr)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
    }
    if (unbalanced_scopes) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    if (draw_hr == D2DERR_RECREATE_TARGET ||
        draw_hr == DXGI_ERROR_DEVICE_REMOVED ||
        draw_hr == DXGI_ERROR_DEVICE_RESET) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_LOST;
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_FAILED;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_begin_command_list_draw(
    progpu_native_direct2d_surface* surface,
    void* command_list)
{
    if (surface == nullptr || command_list == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }
    std::scoped_lock lock(surface->access_mutex);
    if (surface->draw_active) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE;
    }
    if (surface->access_acquired) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_ACCESS_ALREADY_ACQUIRED;
    }

    ID2D1CommandList* native_command_list =
        reinterpret_cast<ID2D1CommandList*>(command_list);
    native_command_list->AddRef();
    surface->active_command_list.Attach(native_command_list);
    surface->d2d_context->SetTarget(surface->active_command_list.Get());
    surface->d2d_context->BeginDraw();
    surface->draw_active = true;
    surface->command_list_draw_active = true;
    surface->draw_scope_depth = 0U;
    return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
}

progpu_native_direct2d_status
progpu_native_direct2d_surface_end_command_list_draw(
    progpu_native_direct2d_surface* surface,
    uint64_t* tag1,
    uint64_t* tag2,
    int32_t* native_hresult)
{
    if (tag1 != nullptr) {
        *tag1 = 0U;
    }
    if (tag2 != nullptr) {
        *tag2 = 0U;
    }
    if (native_hresult != nullptr) {
        *native_hresult = E_INVALIDARG;
    }
    if (surface == nullptr || tag1 == nullptr || tag2 == nullptr ||
        native_hresult == nullptr) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_INVALID_ARGUMENT;
    }

    std::scoped_lock lock(surface->access_mutex);
    if (!surface->draw_active || !surface->command_list_draw_active ||
        !surface->active_command_list) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE;
    }

    bool unbalanced_scopes = surface->draw_scope_depth != 0U;
    unwind_draw_scopes(*surface);
    D2D1_TAG native_tag1 = 0U;
    D2D1_TAG native_tag2 = 0U;
    HRESULT draw_hr = surface->d2d_context->EndDraw(
        &native_tag1,
        &native_tag2);
    surface->d2d_context->SetTarget(surface->d2d_bitmap.Get());
    HRESULT close_hr = SUCCEEDED(draw_hr)
        ? surface->active_command_list->Close()
        : draw_hr;
    HRESULT result_hr = unbalanced_scopes && SUCCEEDED(close_hr)
        ? D2DERR_WRONG_STATE
        : close_hr;
    surface->active_command_list.Reset();
    surface->draw_active = false;
    surface->command_list_draw_active = false;
    *tag1 = native_tag1;
    *tag2 = native_tag2;
    *native_hresult = result_hr;
    surface->last_hresult.store(result_hr, std::memory_order_release);

    if (SUCCEEDED(result_hr)) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS;
    }
    if (unbalanced_scopes) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DRAWING_STATE_MISMATCH;
    }
    if (result_hr == D2DERR_RECREATE_TARGET ||
        result_hr == DXGI_ERROR_DEVICE_REMOVED ||
        result_hr == DXGI_ERROR_DEVICE_RESET) {
        return PROGPU_NATIVE_DIRECT2D_STATUS_DEVICE_LOST;
    }
    return PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_FAILED;
}

int32_t progpu_native_direct2d_surface_get_last_hresult(
    const progpu_native_direct2d_surface* surface)
{
    return surface == nullptr
        ? E_INVALIDARG
        : surface->last_hresult.load(std::memory_order_acquire);
}

} // extern "C"
