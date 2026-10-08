#pragma once

#include "progpu_native_direct2d_cff_translation_fixture.hpp"

#include "progpu_native_direct2d_cff_glyph_fixture.hpp"
#include <bit>

// Included after genuine Windows SDK declarations. This sink records only the
// original system GetGlyphRunOutline result, with the source y-down coordinate
// convention reflected back to the independent fixture's y-up design frame.
namespace progpu::native::direct2d::tests {

class original_cff_outline_sink final : public ID2D1SimplifiedGeometrySink {
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** output) override
    {
        if (output == nullptr) return E_POINTER;
        *output = nullptr;
        if (id != __uuidof(IUnknown) && id != __uuidof(ID2D1SimplifiedGeometrySink)) return E_NOINTERFACE;
        *output = static_cast<ID2D1SimplifiedGeometrySink*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override { return --references_; } // Scoped by the original synchronous call.
    void STDMETHODCALLTYPE SetFillMode(D2D1_FILL_MODE mode) noexcept override { fill_mode_ = mode; }
    void STDMETHODCALLTYPE SetSegmentFlags(D2D1_PATH_SEGMENT flags) noexcept override { flags_ = flags; }
    void STDMETHODCALLTYPE BeginFigure(D2D1_POINT_2F start, D2D1_FIGURE_BEGIN begin) noexcept override
    {
        if (open_ || begin != D2D1_FIGURE_BEGIN_FILLED || figures_ != 0U) valid_ = false;
        start_ = current_ = point(start); open_ = true; ++figures_;
    }
    void STDMETHODCALLTYPE AddLines(const D2D1_POINT_2F* points, UINT32 count) noexcept override
    {
        if (points == nullptr && count != 0U) { valid_ = false; return; }
        for (UINT32 item = 0U; item < count; ++item) {
            const auto end = point(points[item]); append({current_,end,{},{},PROGPU_NATIVE_PATH_SEGMENT_LINE,0,0,0}); current_ = end;
        }
    }
    void STDMETHODCALLTYPE AddBeziers(const D2D1_BEZIER_SEGMENT* curves, UINT32 count) noexcept override
    {
        if (curves == nullptr && count != 0U) { valid_ = false; return; }
        for (UINT32 item = 0U; item < count; ++item) {
            const auto end = point(curves[item].point3);
            append({current_,point(curves[item].point1),point(curves[item].point2),end,PROGPU_NATIVE_PATH_SEGMENT_CUBIC,0,0,0});
            current_ = end;
        }
    }
    void STDMETHODCALLTYPE EndFigure(D2D1_FIGURE_END end) noexcept override
    {
        if (!open_ || end != D2D1_FIGURE_END_CLOSED) valid_ = false;
        if (current_.x != start_.x || current_.y != start_.y)
            append({current_,start_,{},{},PROGPU_NATIVE_PATH_SEGMENT_LINE,0,0,0});
        open_ = false;
    }
    HRESULT STDMETHODCALLTYPE Close() noexcept override { return valid_ && !open_ ? S_OK : E_FAIL; }
    [[nodiscard]] std::span<const progpu_native_path_segment> segments() const { return {segments_.data(), count_}; }
    [[nodiscard]] bool complete() const { return valid_ && !open_ && references_ == 1U; }
    [[nodiscard]] D2D1_FILL_MODE fill_mode() const { return fill_mode_; }
    [[nodiscard]] D2D1_PATH_SEGMENT flags() const { return flags_; }
private:
    static progpu_native_point point(D2D1_POINT_2F value) { return {value.x, -value.y}; }
    void append(progpu_native_path_segment value)
    {
        if (!open_ || count_ == segments_.size()) { valid_ = false; return; }
        segments_[count_++] = value;
    }
    ULONG references_ = 1U;
    std::array<progpu_native_path_segment, 4U> segments_{};
    std::size_t count_ = 0U;
    std::uint32_t figures_ = 0U;
    progpu_native_point start_{}, current_{};
    D2D1_FILL_MODE fill_mode_ = D2D1_FILL_MODE_ALTERNATE;
    D2D1_PATH_SEGMENT flags_ = D2D1_PATH_SEGMENT_NONE;
    bool open_ = false, valid_ = true;
};

template<class Require, class Compare>
void verify_original_cff_glyph_pixels(ID2D1DeviceContext* source_context, IDWriteFactory* write_factory, Require require, Compare compare)
{
    using Microsoft::WRL::ComPtr;
    ComPtr<IDWriteFactory5> extended_factory;
    require(write_factory->QueryInterface(IID_PPV_ARGS(extended_factory.GetAddressOf())) == S_OK,
        "original CFF factory");
    ComPtr<IDWriteInMemoryFontFileLoader> loader;
    require(extended_factory->CreateInMemoryFontFileLoader(loader.GetAddressOf()) == S_OK &&
        write_factory->RegisterFontFileLoader(loader.Get()) == S_OK, "original CFF loader registration");
    struct unregister_loader final {
        IDWriteFactory* factory; IDWriteFontFileLoader* loader; Require& check;
        ~unregister_loader() { check(factory->UnregisterFontFileLoader(loader) == S_OK, "original CFF loader retirement"); }
    } registered{write_factory,loader.Get(),require};

    ComPtr<ID2D1Device> device;
    source_context->GetDevice(device.GetAddressOf());
    ComPtr<ID2D1DeviceContext> context;
    require(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, context.GetAddressOf()) == S_OK,
        "original CFF independent context");
    context->SetDpi(96,96);
    ComPtr<ID2D1Factory> factory;
    context->GetFactory(factory.GetAddressOf());
    com::pointer<compat::factory> typed_factory;
    com::pointer<compat::render_target> typed_target;
    require(factory->QueryInterface(compat::factory_interface_id, reinterpret_cast<void**>(typed_factory.put())) == S_OK &&
        context->QueryInterface(compat::render_target_interface_id, reinterpret_cast<void**>(typed_target.put())) == S_OK,
        "original CFF typed target/factory");
    const auto target_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_TARGET,
        D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM,D2D1_ALPHA_MODE_PREMULTIPLIED),96,96);
    const auto read_properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        target_properties.pixelFormat,96,96);
    ComPtr<ID2D1Bitmap1> target, readback;
    require(context->CreateBitmap({64U,64U},nullptr,0U,&target_properties,target.GetAddressOf()) == S_OK &&
        context->CreateBitmap({64U,64U},nullptr,0U,&read_properties,readback.GetAddressOf()) == S_OK,
        "original CFF pixel targets");
    ComPtr<IDWriteRenderingParams> parameters;
    require(write_factory->CreateCustomRenderingParams(1,0,0,DWRITE_PIXEL_GEOMETRY_FLAT,DWRITE_RENDERING_MODE_OUTLINE,
        parameters.GetAddressOf()) == S_OK, "original CFF OUTLINE parameters");
    com::pointer<compat::rendering_parameters> typed_parameters;
    require(parameters->QueryInterface(compat::rendering_parameters_interface_id,
        reinterpret_cast<void**>(typed_parameters.put())) == S_OK, "original CFF typed parameters");
    const auto copy_pixels = [&] {
        context->SetTarget(nullptr);
        require(readback->CopyFromBitmap(nullptr,target.Get(),nullptr) == S_OK, "original CFF pixel copy");
        D2D1_MAPPED_RECT mapped{};
        require(readback->Map(D2D1_MAP_OPTIONS_READ,&mapped) == S_OK && mapped.pitch >= 256U, "original CFF pixel map");
        std::vector<std::uint8_t> pixels(64U * 256U);
        for (std::size_t row = 0U; row < 64U; ++row)
            std::memcpy(pixels.data() + row * 256U,mapped.bits + row * mapped.pitch,256U);
        require(readback->Unmap() == S_OK, "original CFF pixel unmap"); return pixels;
    };
    std::uint64_t generation = 0U;
    std::size_t configurations = 0U;
    std::size_t translation_controls = 0U;
    for_each_cff_translation_control([&](std::size_t control_index, cff_font_kind kind,
        const cff_font_matrix_control& control, const std::array<bool, 3U>& emits) {
        const auto bytes = make_cff_font(kind, &control);
        ComPtr<IDWriteFontFile> file;
        require(loader->CreateInMemoryFontFileReference(write_factory, bytes.data(), static_cast<UINT32>(bytes.size()),
            nullptr, file.GetAddressOf()) == S_OK, "original CFF translation control file");
        IDWriteFontFile* files[]{file.Get()};
        ComPtr<IDWriteFontFace> face;
        require(write_factory->CreateFontFace(DWRITE_FONT_FACE_TYPE_CFF, 1U, files, 0U,
            DWRITE_FONT_SIMULATIONS_NONE, face.GetAddressOf()) == S_OK, "original CFF translation control face");
        // Request FD1 first and again after FD0 to prove the original face-wide
        // first-FD rule is independent of query order and warm source state.
        for (const auto glyph : std::array<std::uint16_t, 4U>{2U, 0U, 1U, 2U}) {
            const auto expected = expected_cff_translation_glyph(glyph, emits);
            original_cff_outline_sink observed;
            const float advance = 0;
            const auto outlined = face->GetGlyphRunOutline(1024, &glyph, &advance, nullptr, 1U, FALSE, FALSE, &observed);
            const auto closed = observed.Close();
            const bool matches = outlined == S_OK && closed == S_OK && observed.complete() &&
                observed.segments().size() == expected.count;
            if (!matches) std::fprintf(stderr, "Original CFF translation control=%zu kind=%u glyph=%u "
                "outline=%08lx close=%08lx complete=%u segments=%zu expected=%u\n", control_index,
                static_cast<unsigned>(kind), static_cast<unsigned>(glyph), static_cast<unsigned long>(outlined),
                static_cast<unsigned long>(closed), observed.complete() ? 1U : 0U, observed.segments().size(), expected.count);
            require(matches,
                "original CFF exact translation conversion and per-FD ink inventory");
            for (std::size_t segment = 0U; segment < expected.count; ++segment) {
                const auto& actual = observed.segments()[segment]; const auto& wanted = expected.segments[segment];
                require(actual.kind == wanted.kind && actual.p0.x == wanted.p0.x && actual.p0.y == wanted.p0.y &&
                    actual.p1.x == wanted.p1.x && actual.p1.y == wanted.p1.y && actual.p2.x == wanted.p2.x &&
                    actual.p2.y == wanted.p2.y && actual.p3.x == wanted.p3.x && actual.p3.y == wanted.p3.y,
                    "original CFF translated-to-zero matrix preserves all original contour coordinates");
            }
            context->SetTarget(target.Get()); context->BeginDraw();
            context->Clear(D2D1::ColorF(0, 0, 0, 1));
            context->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_ALIASED);
            context->SetTextRenderingParams(parameters.Get());
            ComPtr<ID2D1SolidColorBrush> brush;
            require(context->CreateSolidColorBrush(D2D1::ColorF(1, 0, 0, 1), brush.GetAddressOf()) == S_OK,
                "original CFF translation control brush");
            const DWRITE_GLYPH_RUN run{face.Get(), 32, 1U, &glyph, &advance, nullptr, FALSE, 0U};
            context->DrawGlyphRun({4, 28}, &run, brush.Get(), DWRITE_MEASURING_MODE_NATURAL);
            require(context->EndDraw() == S_OK, "original CFF translation control draw");
            const auto pixels = copy_pixels();
            std::size_t ink = 0U;
            for (std::size_t pixel = 0U; pixel < pixels.size(); pixel += 4U) {
                require(pixels[pixel] == 0U && pixels[pixel + 1U] == 0U && pixels[pixel + 3U] == 255U,
                    "original CFF translation control channels and alpha");
                ink += pixels[pixel + 2U] != 0U;
            }
            require(ink == (expected.count == 0U ? 0U : glyph == 1U ? 88U : 42U),
                "original CFF translation control preserves exact aliased ink inventory");
        }
        ++translation_controls;
    });
    require(translation_controls == 235U, "original CFF complete translation boundary inventory");
    std::fprintf(stderr, "Original CFF translation controls=%zu glyph-outline-and-pixel-observations=%zu\n",
        translation_controls, translation_controls * 4U);
    for (const auto kind : cff_pixel_fonts) {
        const auto bytes = make_cff_font(kind);
        ComPtr<IDWriteFontFile> file;
        require(loader->CreateInMemoryFontFileReference(write_factory,bytes.data(),static_cast<UINT32>(bytes.size()),
            nullptr,file.GetAddressOf()) == S_OK, "original authored CFF file ownership");
        BOOL supported = FALSE;
        DWRITE_FONT_FILE_TYPE file_type = DWRITE_FONT_FILE_TYPE_UNKNOWN;
        DWRITE_FONT_FACE_TYPE face_type = DWRITE_FONT_FACE_TYPE_UNKNOWN;
        UINT32 face_count = 0U;
        require(file->Analyze(&supported,&file_type,&face_type,&face_count) == S_OK && supported && face_count == 1U &&
            file_type == DWRITE_FONT_FILE_TYPE_CFF && face_type == DWRITE_FONT_FACE_TYPE_CFF,
            "original SDK identifies actual CFF outline family, not a fabricated TrueType face");
        IDWriteFontFile* files[]{file.Get()};
        ComPtr<IDWriteFontFace> default_face;
        require(write_factory->CreateFontFace(face_type,1U,files,0U,DWRITE_FONT_SIMULATIONS_NONE,
            default_face.GetAddressOf()) == S_OK, "original CFF default face");
        ComPtr<IDWriteFontFace5> default_extended;
        require(default_face.As(&default_extended) == S_OK &&
            (default_extended->HasVariations() != FALSE) == cff_font_is_variable(kind), "original CFF variation identity");
        ComPtr<IDWriteFontResource> resource;
        if (cff_font_is_variable(kind)) require(default_extended->GetFontResource(resource.GetAddressOf()) == S_OK,
            "original CFF2 variable font resource");
        for (std::size_t instance = 0U; instance < cff_font_case_count(kind); ++instance) {
            ComPtr<IDWriteFontFace5> face;
            if (cff_font_is_variable(kind)) {
                const DWRITE_FONT_AXIS_VALUE coordinate{DWRITE_FONT_AXIS_TAG_WEIGHT,cff_font_weight(instance)};
                require(resource->CreateFontFace(DWRITE_FONT_SIMULATIONS_NONE,&coordinate,1U,face.GetAddressOf()) == S_OK,
                    "original CFF2 requested axis instance");
            } else face = default_extended;
            require(face->GetType() == face_type && face->GetIndex() == 0U && face->GetSimulations() == DWRITE_FONT_SIMULATIONS_NONE,
                "original CFF face identity remains unchanged");
            // There is no DirectWrite FontMatrix getter. Observe the exact
            // original table bytes (including Top/FD matrices and selection),
            // then independently compare actual original outline coordinates.
            const bool cff2 = kind >= cff_font_kind::cff2_static;
            const UINT32 table_tag = cff2 ? DWRITE_MAKE_OPENTYPE_TAG('C','F','F','2') : DWRITE_MAKE_OPENTYPE_TAG('C','F','F',' ');
            const auto expected_table = cff_font_wire::font_table(kind);
            const void* actual_table = nullptr; UINT32 actual_size = 0U; void* table_context = nullptr; BOOL exists = FALSE;
            require(face->TryGetFontTable(table_tag,&actual_table,&actual_size,&table_context,&exists) == S_OK && exists &&
                actual_table != nullptr && actual_size == expected_table.size(), "original CFF matrix/FD table readback");
            const bool same_table = std::memcmp(actual_table,expected_table.data(),expected_table.size()) == 0;
            face->ReleaseFontTable(table_context);
            require(same_table, "original CFF table readback changed original authored bytes");

            const auto axis_count = face->GetFontAxisValueCount();
            require(axis_count <= original_font_capture::maximum_axes, "original CFF axis capture bound");
            std::vector<DWRITE_FONT_AXIS_VALUE> axes(axis_count);
            require(face->GetFontAxisValues(axes.data(),axis_count) == S_OK, "original CFF complete axis readback");
            com::pointer<compat::font_face> typed_face;
            std::shared_ptr<const original_font_capture> captured;
            require(face->QueryInterface(compat::font_face_interface_id,reinterpret_cast<void**>(typed_face.put())) == S_OK &&
                capture_original_font(typed_face.get(),captured) == S_OK && captured->face_type == static_cast<std::uint32_t>(face_type) &&
                captured->face_index == 0U && captured->simulations == 0U && captured->glyph_count == 3U &&
                captured->axis_values_available && captured->has_variations == cff_font_is_variable(kind) &&
                captured->files.size() == 1U && captured->files[0] == bytes && captured->axis_values.size() == axes.size(),
                "original CFF capture retains complete file/face/instance");
            bool found_weight = false;
            for (std::size_t axis = 0U; axis < axes.size(); ++axis) {
                require(captured->axis_values[axis].tag == static_cast<std::uint32_t>(axes[axis].axisTag) &&
                    std::bit_cast<std::uint32_t>(captured->axis_values[axis].value) == std::bit_cast<std::uint32_t>(axes[axis].value),
                    "original CFF source axis order/tag/bits");
                if (cff_font_is_variable(kind) && axes[axis].axisTag == DWRITE_FONT_AXIS_TAG_WEIGHT) {
                    require(!found_weight && std::bit_cast<std::uint32_t>(axes[axis].value) ==
                        std::bit_cast<std::uint32_t>(cff_font_weight(instance)), "original CFF2 exact user coordinate");
                    found_weight = true;
                }
            }
            require(!cff_font_is_variable(kind) || found_weight, "original CFF2 actual wght not an inferred default");
            std::shared_ptr<prepared_original_font> prepared;
            require(prepared_original_font::create(captured,prepared) == S_OK, "original CFF retained context");
            const std::uint16_t indices[]{1U,0U,2U};
            std::array<INT32,3U> design_advances{};
            DWRITE_FONT_METRICS metrics{};
            static_cast<IDWriteFontFace*>(face.Get())->GetMetrics(&metrics);
            require(metrics.designUnitsPerEm == cff_font_units(kind) &&
                face->GetDesignGlyphAdvances(3U,indices,design_advances.data(),FALSE) == S_OK,
                "original CFF design advance query");
            std::array<float,3U> original_advances{};
            for (std::size_t glyph_index = 0U; glyph_index < 3U; ++glyph_index) {
                const auto glyph = indices[glyph_index];
                const auto expected = expected_source_cff_glyph(kind,instance,glyph);
                require(static_cast<float>(design_advances[glyph_index]) == expected.advance,
                    "original CFF independent nominal advances, including empty glyph");
                original_advances[glyph_index] = static_cast<float>(design_advances[glyph_index]) / 32.0F;
                original_cff_outline_sink observed;
                const float no_following_advance = 0;
                require(face->GetGlyphRunOutline(static_cast<float>(cff_font_units(kind)),&glyph,&no_following_advance,
                    nullptr,1U,FALSE,FALSE,&observed) == S_OK && observed.Close() == S_OK && observed.complete() &&
                    observed.segments().size() == expected.count, "original CFF independent line/cubic inventory");
                for (std::size_t segment = 0U; segment < expected.count; ++segment) {
                    const auto& actual = observed.segments()[segment]; const auto& wanted = expected.segments[segment];
                    require(actual.kind == wanted.kind && actual.p0.x == wanted.p0.x && actual.p0.y == wanted.p0.y &&
                        actual.p1.x == wanted.p1.x && actual.p1.y == wanted.p1.y && actual.p2.x == wanted.p2.x &&
                        actual.p2.y == wanted.p2.y && actual.p3.x == wanted.p3.x && actual.p3.y == wanted.p3.y,
                        "original CFF exact independent cubic/FontMatrix/FD/blend coordinates");
                }
            }
            for (const bool nominal : {false,true}) {
                const auto variant = static_cast<std::uint32_t>((instance + (nominal ? 1U : 0U)) % 3U);
                const float advances[]{12,-3,9};
                const compat::glyph_offset offsets[]{{0,0},{0,0},{-0.75F,2.5F}};
                const compat::glyph_run run{captured->face.get(),static_cast<float>(cff_font_units(kind)) / 32.0F,3U,
                    indices,nominal ? nullptr : advances,offsets,0,2U};
                original_glyph_target frame;
                frame.identity = com::pointer<com::unknown>(typed_target.get()); frame.generation = ++generation;
                frame.transform = cff_pixel_transform(variant); frame.baseline = {4,28};
                frame.pixels = {64U,64U}; frame.dpi_x = 96; frame.dpi_y = 96;
                frame.format = {87U,compat::alpha_mode::premultiplied};
                frame.antialias = variant == 0U ? compat::text_antialias_mode::aliased : compat::text_antialias_mode::grayscale;
                std::shared_ptr<const original_glyph_request> request;
                std::shared_ptr<const prepared_original_glyph_run> glyphs;
                require(capture_original_glyph_request(captured,run,compat::measuring_mode::natural,
                    typed_parameters.get(),frame,request) == S_OK && prepared->prepare(request,glyphs) == S_OK &&
                    (glyphs->request().glyphs.advances() == nullptr) == nominal &&
                    glyphs->segments().size() == (cff_source_has_ink(kind) ? 7U : 0U),
                    "original CFF prepared occurrence identity and complete contours");
                com::pointer<compat::path_geometry> geometry;
                com::pointer<compat::geometry_sink> sink;
                require(typed_factory->CreatePathGeometry(geometry.put()) == S_OK && geometry->Open(sink.put()) == S_OK,
                    "original CFF prepared outline sink");
                sink->SetFillMode(compat::fill_mode::winding);
                for (std::size_t segment = 0U; segment < glyphs->segments().size(); ++segment) {
                    const auto& value = glyphs->segments()[segment];
                    if (segment == 0U || segment == 4U) sink->BeginFigure({value.p0.x,value.p0.y},compat::figure_begin::filled);
                    if (value.kind == PROGPU_NATIVE_PATH_SEGMENT_CUBIC) {
                        const compat::bezier_segment curve{{value.p1.x,value.p1.y},{value.p2.x,value.p2.y},{value.p3.x,value.p3.y}};
                        sink->AddBezier(&curve);
                    } else {
                        require(value.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE, "original CFF prepared segment kind");
                        sink->AddLine({value.p1.x,value.p1.y});
                    }
                    if (segment == 3U || segment == 6U) sink->EndFigure(compat::figure_end::closed);
                }
                require(sink->Close() == S_OK, "original CFF prepared outline close");
                const std::array paths{cff_pixel_path::original,cff_pixel_path::independent_geometry,
                    cff_pixel_path::prepared_geometry,cff_pixel_path::original_design_advances};
                std::array<std::vector<std::uint8_t>,4U> pixels;
                for (std::size_t path = 0U; path < (nominal ? 4U : 3U); ++path) {
                    context->SetTarget(target.Get());
                    record_cff_pixel_case(typed_factory.get(),typed_target.get(),prepared,typed_parameters.get(),kind,
                        instance,nominal,variant,paths[path],require,geometry.get(),original_advances.data());
                    pixels[path] = copy_pixels();
                }
                require(pixels[0].size() == 64U * 256U, "original glyph frame byte inventory");
                compare(pixels[0] == pixels[1] && pixels[0] == pixels[2],
                    "original CFF DrawGlyphRun/independent/prepared full-byte mismatch");
                if (nominal) compare(pixels[0] == pixels[3], "original CFF null versus actual design advance mismatch");
                ++configurations;
            }
        }
    }
    require(configurations == 22U, "original CFF full independent configuration inventory");
    std::fprintf(stderr, "Original CFF complete configurations=%zu\n", configurations);
}
} // namespace progpu::native::direct2d::tests
