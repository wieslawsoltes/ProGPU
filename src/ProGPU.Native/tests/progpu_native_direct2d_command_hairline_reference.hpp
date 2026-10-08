#pragma once

#include <memory>
#include "progpu_native_direct2d_hairline_dpi_fixture.hpp"

// Windows-only actual SDK resources and command-sink transport controls.
// Include after the original SDK/WRL declarations. No product dash helper is
// used to construct the expected values below.
namespace progpu::native::direct2d::tests {

template<class T, class Require>
T command_hairline_read(const std::vector<std::uint8_t>& bytes, std::size_t offset, Require require)
{
    require(offset <= bytes.size() && sizeof(T) <= bytes.size() - offset, "hairline wire bounds");
    T value{};
    std::memcpy(&value, bytes.data() + offset, sizeof(value));
    return value;
}

template<class Require>
void verify_command_hairline_dpi(progpu_native_direct2d_surface* surface,
    ID2D1DeviceContext* context, Require require)
{
    using Microsoft::WRL::ComPtr;
    ComPtr<ID2D1Factory> base_factory;
    context->GetFactory(base_factory.GetAddressOf());
    ComPtr<ID2D1Factory1> factory;
    require(base_factory.As(&factory) == S_OK, "hairline original factory1");
    ComPtr<ID2D1SolidColorBrush> brush;
    const D2D1_COLOR_F blue{0,0,1,1}, black{0,0,0,1};
    require(context->CreateSolidColorBrush(&blue, nullptr, brush.GetAddressOf()) == S_OK,
        "hairline original brush");
    const std::array<FLOAT,3U> dashes{3,2,1};
    std::array<ComPtr<ID2D1StrokeStyle1>,3U> styles;
    for (unsigned mode=0U; mode<styles.size(); ++mode) {
        const D2D1_STROKE_STYLE_PROPERTIES1 properties{
            D2D1_CAP_STYLE_FLAT,D2D1_CAP_STYLE_FLAT,D2D1_CAP_STYLE_FLAT,D2D1_LINE_JOIN_MITER,
            1.0F,D2D1_DASH_STYLE_CUSTOM,0.5F,static_cast<D2D1_STROKE_TRANSFORM_TYPE>(mode)};
        require(factory->CreateStrokeStyle(&properties,dashes.data(),3U,styles[mode].GetAddressOf()) == S_OK,
            "hairline original style1");
    }
    const auto transform = D2D1::Matrix3x2F(2, 0, 0, 1, 0, 0.25F);
    ComPtr<ID2D1PathGeometry> curve;
    ComPtr<ID2D1GeometrySink> geometry_sink;
    require(factory->CreatePathGeometry(curve.GetAddressOf()) == S_OK &&
        curve->Open(geometry_sink.GetAddressOf()) == S_OK, "hairline original curve");
    geometry_sink->BeginFigure({2,8},D2D1_FIGURE_BEGIN_HOLLOW);
    geometry_sink->AddBezier(D2D1::BezierSegment({5,12},{12,12},{20,8}));
    geometry_sink->EndFigure(D2D1_FIGURE_END_OPEN);
    require(geometry_sink->Close() == S_OK, "hairline original curve close");
    geometry_sink.Reset();

    using owner = std::unique_ptr<progpu_native_direct2d_scene_recorder,
        decltype(&progpu_native_direct2d_scene_recorder_destroy)>;
    const auto create = [&](const progpu_native_direct2d_target_extent* target) {
        progpu_native_direct2d_scene_recorder* raw=nullptr; int32_t hr=E_FAIL;
        const auto status=target != nullptr
            ? progpu_native_direct2d_scene_recorder_create_for_target(7150U,1U,target,nullptr,&raw,&hr)
            : progpu_native_direct2d_scene_recorder_create(7150U,1U,nullptr,&raw,&hr);
        require(status == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS && hr == S_OK && raw != nullptr,
            "hairline recorder owner");
        owner recorder(raw,progpu_native_direct2d_scene_recorder_destroy);
        void* acquired=nullptr;
        require(progpu_native_direct2d_scene_recorder_get_command_sink(raw,&acquired,&hr) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS && hr == S_OK, "hairline actual acquired sink");
        ComPtr<ID2D1CommandSink1> sink;
        sink.Attach(static_cast<ID2D1CommandSink1*>(acquired));
        require(sink->BeginDraw() == S_OK && sink->SetUnitMode(D2D1_UNIT_MODE_DIPS) == S_OK &&
            sink->SetTransform(&transform) == S_OK && sink->SetAntialiasMode(D2D1_ANTIALIAS_MODE_ALIASED) == S_OK,
            "hairline explicit DIP recording state");
        return std::pair{std::move(recorder),std::move(sink)};
    };
    const auto serialize = [&](progpu_native_direct2d_scene_recorder* recorder,
        progpu_native_direct2d_scene_stream_result& result) {
        result={}; result.struct_size=sizeof(result); int32_t hr=E_FAIL;
        require(progpu_native_direct2d_scene_recorder_build_stream(recorder,nullptr,0U,&result,&hr) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER && result.written_bytes == 0U,
            "hairline size pass");
        std::vector<std::uint8_t> bytes(static_cast<std::size_t>(result.required_bytes));
        require(progpu_native_direct2d_scene_recorder_build_stream(recorder,bytes.data(),bytes.size(),&result,&hr) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS && hr == S_OK && result.written_bytes == bytes.size(),
            "hairline complete scene publication");
        return bytes;
    };
    const auto check = [&](const std::vector<std::uint8_t>& bytes,
        const progpu_native_direct2d_scene_stream_result& result,unsigned mode,float dpi,bool curved) {
        const bool hairline=mode == 2U;
        require(result.translated_draw_count == 1U && result.command_count == 1U &&
            result.failure_reason == PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_NONE &&
            ((result.flags & PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_TARGET_DEPENDENT_STROKES) != 0U) == hairline &&
            (result.flags & PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_TARGET_DEPENDENT_MASKS) == 0U,
            "hairline dependency and original draw accounting");
        const auto header=command_hairline_read<progpu_native_scene_header>(bytes,0U,require);
        const auto command=command_hairline_read<progpu_native_scene_command>(bytes,header.command_offset,require);
        // Literal line is [4,8.25]-[40,8.25] after the original transform;
        // the genuinely curved arch adds exactly 3 DIPs at its midpoint.
        // Independent 96/192/384 physical one-pixel half extents: .5/.25/.125.
        const float padding=hairline ? (dpi == 96 ? 0.5F : dpi == 192 ? 0.25F : 0.125F) : 1.0F;
        const float pad_x=mode == 0U ? 2.0F : padding;
        require(command.bounds_x == 4.0F-pad_x && command.bounds_y == 8.25F-padding &&
            command.bounds_width == 36.0F+2.0F*pad_x && command.bounds_height == (curved ? 3.0F : 0.0F)+2.0F*padding,
            "hairline literal target-DIP bounds");
        const auto resource=command_hairline_read<progpu_native_scene_resource>(bytes,
            header.resource_offset+command.resource_index*header.resource_stride,require);
        if (curved) {
            require(resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_GEOMETRY_BATCH,
                "hairline original cubic stays semantic geometry");
            const auto primitive=command_hairline_read<progpu_native_geometry_primitive>(bytes,resource.payload_offset,require);
            require(((primitive.flags & PROGPU_NATIVE_PRIMITIVE_FLAG_HAIRLINE) != 0U) == hairline,
                "hairline original cubic mode");
        } else {
            require(resource.kind == PROGPU_NATIVE_SCENE_RESOURCE_STROKE_BATCH,"hairline retained line batch");
            const auto stroke=command_hairline_read<progpu_native_scene_stroke>(bytes,resource.payload_offset,require);
            const double factor=hairline ? (dpi == 96 ? 1.0 : dpi == 192 ? 0.5 : 0.25) : 1.0;
            require(stroke.point_count == 2U && stroke.dash_interval_count == 3U && stroke.dash_offset == 0.5*factor &&
                stroke.stroke_thickness == (hairline ? 0.0F : 2.0F), "hairline phase and ignored width");
            for (std::size_t index=0U; index<dashes.size(); ++index) {
                const auto interval=command_hairline_read<double>(bytes,
                    resource.auxiliary_offset+2U*sizeof(progpu_native_point)+index*sizeof(double),require);
                require(interval == static_cast<double>(dashes[index])*factor,"hairline odd SIMD tail interval");
            }
        }
    };
    for (const float dpi : {96.0F,192.0F,384.0F}) {
    for (unsigned mode=0U; mode<styles.size(); ++mode) {
    for (const bool curved : {false,true}) {
        progpu_native_direct2d_target_extent target{sizeof(target),64U,64U,0U,dpi,dpi};
        auto [recorder,sink]=create(&target);
        // Destroy caller metadata identity after creation, before any draw.
        target.dpi_x=std::numeric_limits<float>::quiet_NaN(); target.dpi_y=1;
        target.pixel_width=0U; target.pixel_height=0U;
        require((curved ? sink->DrawGeometry(curve.Get(),brush.Get(),mode == 2U ? 0.0F : 2.0F,styles[mode].Get())
            : sink->DrawLine({2,8},{20,8},brush.Get(),mode == 2U ? std::numeric_limits<float>::max() : 2.0F,styles[mode].Get()))
            == S_OK && sink->EndDraw() == S_OK,"hairline owned DPI callback");
        progpu_native_direct2d_scene_stream_result result{};
        check(serialize(recorder.get(),result),result,mode,dpi,curved);
        std::array<FLOAT,3U> readback{}; styles[mode]->GetDashes(readback.data(),3U);
        require(readback == dashes && styles[mode]->GetDashOffset() == 0.5F,
            "hairline conversion mutated original SDK style");
    } } }
    // Targetless normal/fixed remain supported; only hairline acquires DPI.
    for (unsigned mode=0U; mode<2U; ++mode) {
        auto [recorder,sink]=create(nullptr);
        require(sink->DrawLine({2,8},{20,8},brush.Get(),2,styles[mode].Get()) == S_OK && sink->EndDraw() == S_OK,
            "targetless normal/fixed compatibility");
        progpu_native_direct2d_scene_stream_result result{};
        check(serialize(recorder.get(),result),result,mode,96,false);
    }
    // Reject missing/unequal DPI before a hairline draw, retaining original
    // first failure and caller bytes even when an earlier draw was valid.
    for (unsigned rejected=0U; rejected<3U; ++rejected) {
        const progpu_native_direct2d_target_extent target{sizeof(target),64U,64U,0U,96,192};
        auto [recorder,sink]=create(rejected == 0U ? nullptr : &target);
        const D2D1_RECT_F fill{1,1,2,2};
        require(sink->FillRectangle(&fill,brush.Get()) == S_OK,"hairline preceding source draw");
        const HRESULT status=rejected == 2U ? sink->SetUnitMode(D2D1_UNIT_MODE_PIXELS)
            : sink->DrawGeometry(curve.Get(),brush.Get(),0,styles[2].Get());
        require(status == E_NOTIMPL && sink->EndDraw() == E_NOTIMPL,"hairline unsupported frame is explicit");
        std::array<std::uint8_t,256U> sentinel{}; sentinel.fill(0xA5U); const auto prior=sentinel;
        progpu_native_direct2d_scene_stream_result result{}; result.struct_size=sizeof(result); int32_t hr=S_OK;
        require(progpu_native_direct2d_scene_recorder_build_stream(recorder.get(),sentinel.data(),sentinel.size(),&result,&hr) ==
            PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED && hr == E_NOTIMPL && sentinel == prior &&
            result.required_bytes == 0U && result.written_bytes == 0U && result.translated_draw_count == 1U &&
            result.failure_callback_index == 5U && result.failure_reason == PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FAILURE_UNSUPPORTED_STATE &&
            (result.flags & PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_TARGET_DEPENDENT_STROKES) == 0U,
            "hairline rejected stream stays atomic and cannot Widen fallback");
    }
    {
        const progpu_native_direct2d_target_extent target{sizeof(target),64U,64U,0U,192,192};
        auto [recorder,sink]=create(&target);
        require(sink->DrawLine({2,8},{20,8},brush.Get(),0,styles[2].Get()) == S_OK && sink->Clear(&black) == S_OK &&
            sink->DrawLine({2,8},{20,8},brush.Get(),2,styles[1].Get()) == S_OK && sink->EndDraw() == S_OK,
            "hairline Clear replacement");
        progpu_native_direct2d_scene_stream_result result{};
        (void)serialize(recorder.get(),result);
        require(result.translated_draw_count == 2U && result.command_count == 1U &&
            (result.flags & PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_TARGET_DEPENDENT_STROKES) == 0U &&
            (result.flags & PROGPU_NATIVE_DIRECT2D_SCENE_STREAM_FLAG_HAS_LEADING_CLEAR) != 0U,
            "hairline Clear dependency versus cumulative source accounting");
    }
    // A real command list does not carry source target DPI in its callbacks.
    // The explicitly supplied surface supplies each new generation's target.
    float saved_x{},saved_y{}; context->GetDpi(&saved_x,&saved_y);
    D2D1_MATRIX_3X2_F saved_transform{}; context->GetTransform(&saved_transform);
    const auto saved_antialias=context->GetAntialiasMode();
    context->SetDpi(192,192);
    void* raw_list=nullptr; int32_t hr=E_FAIL;
    require(progpu_native_direct2d_surface_create_command_list(surface,&raw_list,&hr) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "hairline original command-list owner");
    ComPtr<ID2D1CommandList> list; list.Attach(static_cast<ID2D1CommandList*>(raw_list));
    require(progpu_native_direct2d_surface_begin_command_list_draw(surface,list.Get()) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,
        "hairline original command-list BeginDraw");
    context->SetTransform(transform); context->SetAntialiasMode(D2D1_ANTIALIAS_MODE_ALIASED);
    context->DrawLine({2,8},{20,8},brush.Get(),0,styles[2].Get());
    std::uint64_t tag1{},tag2{};
    require(progpu_native_direct2d_surface_end_command_list_draw(surface,&tag1,&tag2,&hr) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS && hr == S_OK,
        "hairline original command-list EndDraw");
    for (const float dpi : {96.0F,192.0F}) {
        context->SetDpi(dpi,dpi);
        progpu_native_direct2d_scene_stream_result result{}; result.struct_size=sizeof(result);
        require(progpu_native_direct2d_command_list_build_scene_stream(surface,list.Get(),7151U,static_cast<uint64_t>(dpi),
            nullptr,0U,&result,&hr) == PROGPU_NATIVE_DIRECT2D_STATUS_INSUFFICIENT_BUFFER,"hairline surface DPI size");
        std::vector<std::uint8_t> bytes(static_cast<std::size_t>(result.required_bytes));
        require(progpu_native_direct2d_command_list_build_scene_stream(surface,list.Get(),7151U,static_cast<uint64_t>(dpi),
            bytes.data(),bytes.size(),&result,&hr) == PROGPU_NATIVE_DIRECT2D_STATUS_SUCCESS,"hairline surface DPI stream");
        check(bytes,result,2U,dpi,false);
    }
    context->SetDpi(saved_x,saved_y);
    context->SetTransform(saved_transform); context->SetAntialiasMode(saved_antialias);
}

template<class Require>
void verify_original_hairline_dpi_pixels(ID2D1DeviceContext* source, Require require)
{
    using Microsoft::WRL::ComPtr;
    ComPtr<ID2D1Device> device;
    source->GetDevice(device.GetAddressOf());
    ComPtr<ID2D1DeviceContext> context;
    require(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE,context.GetAddressOf()) == S_OK,
        "hairline original independent context");
    ComPtr<ID2D1Factory> base_factory;
    context->GetFactory(base_factory.GetAddressOf());
    ComPtr<ID2D1Factory1> factory;
    require(base_factory.As(&factory) == S_OK,"hairline original pixel factory1");
    com::pointer<compat::factory> typed_factory;
    com::pointer<compat::render_target> typed_target;
    require(base_factory->QueryInterface(compat::factory_interface_id,reinterpret_cast<void**>(typed_factory.put())) == S_OK &&
        context->QueryInterface(compat::render_target_interface_id,reinterpret_cast<void**>(typed_target.put())) == S_OK,
        "hairline actual SDK typed resource identity");
    std::array<ComPtr<ID2D1StrokeStyle1>,4U> styles;
    std::array<com::pointer<compat::stroke_style>,4U> typed_styles;
    std::array<compat::stroke_style*,4U> injected{};
    for (unsigned band=0U; band<styles.size(); ++band) {
        const auto original=hairline_dpi_style(band);
        const D2D1_STROKE_STYLE_PROPERTIES1 properties{
            static_cast<D2D1_CAP_STYLE>(original.start_cap),static_cast<D2D1_CAP_STYLE>(original.end_cap),
            static_cast<D2D1_CAP_STYLE>(original.dash_cap),static_cast<D2D1_LINE_JOIN>(original.join),original.miter_limit,
            static_cast<D2D1_DASH_STYLE>(original.dash),original.dash_offset,D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE};
        require(factory->CreateStrokeStyle(&properties,hairline_dpi_dashes.data(),2U,styles[band].GetAddressOf()) == S_OK &&
            styles[band]->GetStrokeTransformType() == D2D1_STROKE_TRANSFORM_TYPE_HAIRLINE &&
            styles[band]->QueryInterface(compat::stroke_style_interface_id,reinterpret_cast<void**>(typed_styles[band].put())) == S_OK,
            "hairline original pixel style identity");
        injected[band]=typed_styles[band].get();
    }
    const auto target_properties=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96,96);
    const auto read_properties=D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_CPU_READ|D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        target_properties.pixelFormat,96,96);
    ComPtr<ID2D1Bitmap1> target,readback;
    require(context->CreateBitmap({64U,64U},nullptr,0U,&target_properties,target.GetAddressOf()) == S_OK &&
        context->CreateBitmap({64U,64U},nullptr,0U,&read_properties,readback.GetAddressOf()) == S_OK,
        "hairline original pixel targets");
    const auto pixels=[&] {
        context->SetTarget(nullptr);
        require(readback->CopyFromBitmap(nullptr,target.Get(),nullptr) == S_OK,"hairline original pixel copy");
        D2D1_MAPPED_RECT mapped{};
        require(readback->Map(D2D1_MAP_OPTIONS_READ,&mapped) == S_OK && mapped.pitch >= 256U,"hairline original pixel map");
        std::vector<std::uint8_t> result(64U*256U);
        for (std::size_t row=0U; row<64U; ++row) std::memcpy(result.data()+row*256U,mapped.bits+row*mapped.pitch,256U);
        require(readback->Unmap() == S_OK,"hairline original pixel unmap");
        return result;
    };
    unsigned configurations=0U;
    for (const float dpi : {96.0F,192.0F}) {
    for (const bool curved : {false,true}) {
        std::array<std::vector<std::uint8_t>,2U> images;
        for (unsigned path=0U; path<images.size(); ++path) {
            context->SetTarget(target.Get());
            record_hairline_dpi_case(typed_factory.get(),typed_target.get(),dpi,curved,path != 0U,
                std::span<compat::stroke_style* const>(injected),require);
            images[path]=pixels();
            hairline_dpi_pixels(images[path],true,require);
        }
        require(images[0] == images[1],"original hairline versus independent physical rectangles full-byte mismatch");
        for (unsigned band=0U; band<styles.size(); ++band) {
            std::array<FLOAT,2U> intervals{}; styles[band]->GetDashes(intervals.data(),2U);
            require(intervals == hairline_dpi_dashes && styles[band]->GetDashOffset() == hairline_dpi_style(band).dash_offset,
                "original hairline pixel source style changed");
        }
        ++configurations;
    } }
    require(configurations == 4U,"original hairline DPI line/cubic pixel inventory");
}

} // namespace progpu::native::direct2d::tests
