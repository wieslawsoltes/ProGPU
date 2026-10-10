#pragma once

#include "progpu_native_clipped_miter_fixture.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>

// Windows-only original SDK rendering. Include after the SDK/WRL declarations.
// Authored but unexecuted: actual Microsoft pixels must satisfy both the
// independent filled-ring route and the literal shared full-byte oracle.
namespace progpu::native::direct2d::tests {

template<class Require>
void verify_original_clipped_miter_pixels(ID2D1DeviceContext* source, Require require)
{
    using Microsoft::WRL::ComPtr;
    ComPtr<ID2D1Device> device;
    source->GetDevice(device.GetAddressOf());
    ComPtr<ID2D1DeviceContext> context;
    require(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,
        context.GetAddressOf()) == S_OK, "clipped miter original independent context");
    context->SetUnitMode(D2D1_UNIT_MODE_DIPS);

    ComPtr<ID2D1Factory> base_factory;
    context->GetFactory(base_factory.GetAddressOf());
    ComPtr<ID2D1Factory1> factory;
    require(base_factory.As(&factory) == S_OK, "clipped miter original factory1");
    com::pointer<compat::factory> typed_factory;
    com::pointer<compat::render_target> typed_target;
    require(base_factory->QueryInterface(compat::factory_interface_id,
        reinterpret_cast<void**>(typed_factory.put())) == S_OK &&
        context->QueryInterface(compat::render_target_interface_id,
        reinterpret_cast<void**>(typed_target.put())) == S_OK,
        "clipped miter actual SDK typed resource identity");

    // Each transform mode is a genuine SDK Factory1 style, including Normal.
    // The shared recorder borrows these exact objects, never portable styles
    // or a replacement inferred from a requested stroke width.
    constexpr std::array<D2D1_STROKE_TRANSFORM_TYPE, 3U> transform_types{
        D2D1_STROKE_TRANSFORM_TYPE_NORMAL,
        D2D1_STROKE_TRANSFORM_TYPE_FIXED,
        D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE};
    std::array<ComPtr<ID2D1StrokeStyle1>, 3U> styles;
    std::array<com::pointer<compat::stroke_style>, 3U> typed_styles;
    const auto require_original_style = [&](unsigned mode) {
        require(styles[mode]->GetStartCap() == D2D1_CAP_STYLE_FLAT &&
            styles[mode]->GetEndCap() == D2D1_CAP_STYLE_FLAT &&
            styles[mode]->GetDashCap() == D2D1_CAP_STYLE_FLAT &&
            styles[mode]->GetLineJoin() == D2D1_LINE_JOIN_MITER &&
            styles[mode]->GetMiterLimit() == 1.0F &&
            styles[mode]->GetDashStyle() == D2D1_DASH_STYLE_SOLID &&
            styles[mode]->GetDashOffset() == 0.0F &&
            styles[mode]->GetDashesCount() == 0U &&
            styles[mode]->GetStrokeTransformType() == transform_types[mode],
            "clipped miter original source style changed");
    };
    for (unsigned mode = 0U; mode < styles.size(); ++mode) {
        const D2D1_STROKE_STYLE_PROPERTIES1 properties{
            D2D1_CAP_STYLE_FLAT, D2D1_CAP_STYLE_FLAT, D2D1_CAP_STYLE_FLAT,
            D2D1_LINE_JOIN_MITER, 1.0F, D2D1_DASH_STYLE_SOLID, 0.0F,
            transform_types[mode]};
        require(factory->CreateStrokeStyle(&properties, nullptr, 0U,
            styles[mode].GetAddressOf()) == S_OK &&
            styles[mode]->QueryInterface(compat::stroke_style_interface_id,
            reinterpret_cast<void**>(typed_styles[mode].put())) == S_OK,
            "clipped miter original style1 identity");
        require_original_style(mode);
    }

    const auto target_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96, 96);
    const auto read_properties = D2D1::BitmapProperties1(
        D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        target_properties.pixelFormat, 96, 96);
    ComPtr<ID2D1Bitmap1> target, readback;
    require(context->CreateBitmap({64U, 64U}, nullptr, 0U, &target_properties,
        target.GetAddressOf()) == S_OK &&
        context->CreateBitmap({64U, 64U}, nullptr, 0U, &read_properties,
        readback.GetAddressOf()) == S_OK, "clipped miter original pixel targets");
    const auto pixels = [&] {
        context->SetTarget(nullptr);
        require(readback->CopyFromBitmap(nullptr, target.Get(), nullptr) == S_OK,
            "clipped miter original pixel copy");
        std::vector<std::uint8_t> result(64U * 256U);
        D2D1_MAPPED_RECT mapped{};
        require(readback->Map(D2D1_MAP_OPTIONS_READ, &mapped) == S_OK &&
            mapped.bits != nullptr && mapped.pitch >= 256U,
            "clipped miter original pixel map");
        for (std::size_t row = 0U; row < 64U; ++row) {
            std::memcpy(result.data() + row * 256U, mapped.bits + row * mapped.pitch, 256U);
        }
        require(readback->Unmap() == S_OK, "clipped miter original pixel unmap");
        return result;
    };

    unsigned configurations = 0U;
    for (unsigned mode = 0U; mode < styles.size(); ++mode) {
        for (const bool rectangle : {false, true}) {
            std::array<std::vector<std::uint8_t>, 2U> images;
            for (unsigned route = 0U; route < images.size(); ++route) {
                context->SetTarget(target.Get());
                record_clipped_miter_case(typed_factory.get(), typed_target.get(),
                    mode, rectangle, route != 0U, typed_styles[mode].get(), require);
                images[route] = pixels();
                clipped_miter_pixels(images[route], true, mode, require);
                require_original_style(mode);
            }
            require(images[0] == images[1],
                "original clipped miter versus independent filled ring full-byte mismatch");
            ++configurations;
        }
    }
    require(configurations == 6U,
        "original clipped miter normal/fixed/hairline rectangle/path inventory");
}

} // namespace progpu::native::direct2d::tests
