#include "../src/Direct2D/progpu_native_direct2d_font_capture.hpp"
#include "../src/Direct2D/progpu_native_direct2d_prepared_glyphs.hpp"
#include "progpu_native_hint_fault_fixture.hpp"
#include "progpu_native_direct2d_font_source_fixture.hpp"

#include <array>
#include <cstdio>
#include <cstring>
#include <limits>

#if defined(_WIN32)
#include <dwrite.h>
#endif

namespace {
namespace capture = progpu::native::direct2d;
namespace compat = capture::compat;
namespace com = progpu::native::com;

using namespace progpu::native::direct2d::tests;

[[nodiscard]] bool check(bool value, const char* message)
{
    if (!value) std::fprintf(stderr, "original font capture: %s\n", message);
    return value;
}

[[nodiscard]] bool source_contracts()
{
    font_stream first_stream, second_stream;
    second_stream.bytes[0] = std::byte{0xCA};
    font_loader first_loader, second_loader;
    first_loader.stream = &first_stream;
    second_loader.stream = &second_stream;
    font_file first_file, second_file;
    first_file.loader = &first_loader;
    second_file.loader = &second_loader;
    font_face face;
    face.files = {&first_file, &second_file};
    std::shared_ptr<const capture::original_font_capture> font;
    if (!check(capture::capture_original_font(&face, font) == com::ok && font &&
        font->face.get() == &face && font->face_index == 2U && font->face_type == 3U &&
        font->simulations == 3U && font->symbol_font == 1 && font->glyph_count == 400U &&
        font->files.size() == 2U && font->files[0][0] == std::byte{1} &&
        font->files[1][0] == std::byte{0xCA}, "exact multi-file identity/order")) return false;
    const auto retained = font;
    first_stream.bytes[0] = std::byte{0xBB};
    if (!check(font->files[0][0] == std::byte{1}, "borrowed bytes escaped capture")) return false;
    const auto balanced = [&] {
        return first_file.references == 1U && second_file.references == 1U &&
            first_loader.references == 1U && second_loader.references == 1U &&
            first_stream.references == 1U && second_stream.references == 1U &&
            !first_stream.bad_release && !second_stream.bad_release &&
            face.references == 2U && face.table_calls == 0U && face.outline_calls == 0U;
    };
    // Every failed boundary preserves the exact earlier immutable result and
    // releases even files returned before a later GetFiles failure.
    for (std::uint32_t boundary = 0U; boundary < 8U; ++boundary) {
        auto* status = boundary == 0U ? &face.count_result : boundary == 1U ? &face.files_result :
            boundary == 2U ? &second_file.query_result : boundary == 3U ? &second_file.key_result :
            boundary == 4U ? &second_file.loader_result : boundary == 5U ? &second_loader.create_result :
            boundary == 6U ? &second_stream.size_result : &second_stream.read_result;
        *status = compat::not_implemented;
        const auto releases = second_stream.releases;
        const auto result = capture::capture_original_font(&face, font);
        *status = com::ok;
        if (!check(result == compat::not_implemented && font == retained && balanced() &&
            (boundary != 7U || second_stream.releases == releases),
            "source failure HRESULT/publication/ownership")) return false;
    }
    for (std::uint32_t invalid = 0U; invalid < 10U; ++invalid) {
        if (invalid == 0U) face.declared_count = 17U;
        if (invalid == 1U) face.declared_count = 0U;
        if (invalid == 2U) face.change_count = true;
        if (invalid == 3U) second_file.key_size = 65537U;
        if (invalid == 4U) second_stream.declared_size = 0U;
        if (invalid == 5U) second_stream.declared_size = capture::original_font_capture::maximum_total_bytes;
        if (invalid == 6U) second_file.null_key = true;
        if (invalid == 7U) second_file.loader = nullptr;
        if (invalid == 8U) second_loader.stream = nullptr;
        if (invalid == 9U) second_stream.null_data = true;
        const auto releases = second_stream.releases;
        const auto result = capture::capture_original_font(&face, font);
        face.declared_count = 2U; face.change_count = false;
        second_file.key_size = sizeof(second_file.key); second_file.null_key = false;
        second_file.loader = &second_loader; second_loader.stream = &second_stream;
        second_stream.declared_size = second_stream.bytes.size(); second_stream.null_data = false;
        if (!check(com::failed(result) && font == retained && balanced() &&
            (invalid != 9U || second_stream.releases == releases + 1U),
            "malformed source/budget atomicity/fragment cleanup")) return false;
    }
    first_file.change_key = true;
    first_stream.null_context = true;
    const auto releases = first_stream.releases;
    std::shared_ptr<const capture::original_font_capture> second_capture;
    if (!check(capture::capture_original_font(&face, second_capture) == com::ok &&
        first_stream.releases == releases + 1U && !first_stream.bad_release,
        "copied loader key/null fragment context")) return false;
    second_capture.reset();

    source_object<com::unknown> target_object;
    capture::original_glyph_target target{
        com::pointer<com::unknown>(&target_object), 71U, {1, 0.25F, -0.5F, 2, 3, -4},
        {5.25F, 21.5F}, {91U, 49U}, 144, 120, {87U, compat::alpha_mode::ignore},
        compat::text_antialias_mode::cleartype, 12U, 13U};
    std::array<std::uint16_t, 2U> indices{4U, 398U};
    std::array<float, 2U> advances{-2.0F, 8.75F};
    std::array<compat::glyph_offset, 2U> offsets{compat::glyph_offset{0.25F, -1.5F}, {2, 3}};
    compat::glyph_run run{&face, 17.5F, 2U, indices.data(), advances.data(), offsets.data(), -1, 5U};
    rendering_parameters parameters;
    struct mutation final { std::uint16_t* index; capture::original_glyph_target* target; };
    mutation source{indices.data(), &target};
    parameters.context = &source;
    parameters.callback = [](void* context) noexcept {
        auto& item = *static_cast<mutation*>(context);
        item.index[0] = 7U;
        item.target->generation = 99U;
        item.target->baseline.x = -300;
        item.target->identity.reset();
    };
    std::shared_ptr<const capture::original_glyph_request> request;
    if (!check(capture::capture_original_glyph_request(font, run, compat::measuring_mode::gdi_natural,
        &parameters, target, request) == com::ok && request && request->glyphs.indices()[0] == 4U &&
        request->glyphs.advances()[0] == -2.0F && request->glyphs.offsets()[0].ascender_offset == -1.5F &&
        request->em_size == 17.5F && request->sideways == -1 && request->bidi_level == 5U &&
        request->target.generation == 71U && request->target.baseline.x == 5.25F &&
        request->target.identity.get() == &target_object && request->target.dpi_x == 144 &&
        request->target.dpi_y == 120 && request->target.transform.m21 == -0.5F &&
        request->target.tag1 == 12U && request->target.tag2 == 13U &&
        request->rendering.supplied && request->rendering.gamma == 1.8F &&
        request->rendering.cleartype_level == 0.25F && request->rendering.pixel_geometry == 2U &&
        request->rendering.rendering_mode == 5U, "original request before reentrant getters")) return false;
    parameters.callback = nullptr;
    target = request->target;
    const auto original_request = request;
    parameters.gamma = std::numeric_limits<float>::quiet_NaN();
    if (!check(capture::capture_original_glyph_request(font, run, compat::measuring_mode::natural,
        &parameters, target, request) == com::invalid_argument && request == original_request,
        "invalid rendering values preserve request")) return false;
    indices[0] = 400U;
    if (!check(capture::capture_original_glyph_request(font, run, compat::measuring_mode::natural,
        nullptr, target, request) == com::invalid_argument && request == original_request,
        "original face glyph range")) return false;
    indices[0] = 0U;
    font_face other_face;
    parameters.gamma = 1.8F;
    for (std::uint32_t invalid = 0U; invalid < 12U; ++invalid) {
        auto candidate_target = target;
        auto candidate_run = run;
        auto measuring = compat::measuring_mode::natural;
        if (invalid == 0U) candidate_target.generation = 0U;
        if (invalid == 1U) candidate_target.identity.reset();
        if (invalid == 2U) candidate_target.pixels.width = 0U;
        if (invalid == 3U) candidate_target.dpi_y = 0;
        if (invalid == 4U) candidate_target.transform.m21 = std::numeric_limits<float>::infinity();
        if (invalid == 5U) candidate_target.baseline.x = std::numeric_limits<float>::quiet_NaN();
        if (invalid == 6U) candidate_target.units = static_cast<compat::unit_mode>(2U);
        if (invalid == 7U) candidate_target.blend = static_cast<compat::primitive_blend>(5U);
        if (invalid == 8U) candidate_run.font_face_value = &other_face;
        if (invalid == 9U) candidate_run.font_em_size = 0;
        if (invalid == 10U) candidate_run.glyph_count = capture::glyph_run_capture<compat::glyph_offset>::maximum_glyph_count + 1U;
        if (invalid == 11U) measuring = static_cast<compat::measuring_mode>(3U);
        const auto reads = parameters.reads;
        if (!check(capture::capture_original_glyph_request(font, candidate_run, measuring,
            &parameters, candidate_target, request) == com::invalid_argument && request == original_request &&
            parameters.reads == reads, "request preflight before callbacks/publication")) return false;
    }
    run.glyph_advances = nullptr; run.glyph_offsets = nullptr;
    if (!check(capture::capture_original_glyph_request(font, run, compat::measuring_mode::natural,
        nullptr, target, request) == com::ok && !request->rendering.supplied &&
        request->glyphs.advances() == nullptr && request->glyphs.offsets() == nullptr,
        "absent values stay absent")) return false;
    return balanced() && parameters.references == 1U;
}

[[nodiscard]] bool prepared_source_contracts()
{
    font_stream stream;
    stream.bytes = progpu::native::tests::make_hint_fault_font();
    stream.declared_size = stream.bytes.size();
    font_loader loader; loader.stream = &stream;
    font_file file; file.loader = &loader;
    font_face face; face.files[0] = &file; face.declared_count = 1U;
    face.type = 1U; face.index = 0U; face.simulations = 0U; face.glyph_count = 3U;
    std::shared_ptr<const capture::original_font_capture> font;
    if (!check(capture::capture_original_font(&face, font) == com::ok, "prepared original source acquisition")) return false;
    const auto original_reads = stream.reads;
    face.count_result = compat::not_implemented; // Any further font acquisition must fail.
    std::shared_ptr<capture::prepared_original_font> prepared_font;
    if (!check(capture::prepared_original_font::create(font, prepared_font) == com::ok &&
        prepared_font->cached_glyph_count() == 0U, "prepared parsed face/lazy glyph cache")) return false;

    com::pointer<compat::factory> factory;
    com::pointer<compat::scene_factory_native> scene_factory;
    com::pointer<compat::render_target> target;
    com::pointer<compat::scene_render_target_native> scene;
    com::pointer<capture::prepared_glyph_target> prepared_target;
    constexpr compat::scene_render_target_properties properties{128U, 128U, 144, 120, 8441U, 1U};
    if (!check(compat::create_factory(factory.put()) == com::ok &&
        factory.as(compat::scene_factory_native_interface_id, scene_factory) == com::ok &&
        scene_factory->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
        target.as(compat::scene_render_target_native_interface_id, scene) == com::ok &&
        target.as(capture::prepared_glyph_target_id, prepared_target) == com::ok,
        "actual prepared target capability")) return false;
    com::pointer<compat::solid_color_brush> brush;
    constexpr compat::color_f ink{0.25F, 0.5F, 0.75F, 0.5F};
    if (target->CreateSolidColorBrush(&ink, nullptr, brush.put()) != com::ok) return false;
    rendering_parameters parameters; parameters.mode = compat::rendering_mode::outline;
    struct release_parameters final {
        compat::render_target* target;
        ~release_parameters() { target->SetTextRenderingParams(nullptr); }
    } release{target.get()};
    target->SetTextRenderingParams(&parameters);
    target->SetTextAntialiasMode(compat::text_antialias_mode::grayscale);
    constexpr compat::matrix_3x2_f transform{1, 0.25F, -0.5F, 1, 7, 11};
    constexpr compat::rectangle_f clip{0, 0, 90, 90};
    constexpr compat::layer_parameters layer{clip, nullptr, compat::antialias_mode::aliased,
        {1, 0, 0, 1, 0, 0}, 0.5F, nullptr, compat::layer_options::none};
    const std::uint16_t indices[]{1U, 0U, 2U};
    const float advances[]{40, -7, 12};
    const compat::glyph_offset offsets[]{{0.5F, 0.25F}, {0, 0}, {-2, 3}};
    compat::glyph_run run{&face, 125, 3U, indices, advances, offsets, 0, 2U};
    for (unsigned repeat = 0U; repeat < 2U; ++repeat) {
        target->BeginDraw(); target->SetTransform(&transform);
        target->SetTags(17U, 29U);
        target->PushAxisAlignedClip(&clip, compat::antialias_mode::aliased);
        target->PushLayer(&layer, nullptr);
        if (!check(prepared_target->DrawOwnedGlyphRun(prepared_font, {8, 50}, &run, brush.get(),
            compat::measuring_mode::natural) == com::ok, "actual prepared draw under captured scopes")) return false;
        target->PopLayer(); target->PopAxisAlignedClip();
        if (!check(target->EndDraw(nullptr, nullptr) == com::ok && prepared_font->cached_glyph_count() == 3U &&
            face.outline_calls == 0U && face.table_calls == 0U && stream.reads == original_reads,
            "retained draw avoids font callbacks/decode replacement")) return false;
        compat::scene_render_target_summary summary{}; scene->GetSummary(&summary);
        if (!check(summary.draw_count == 1U, "one native retained glyph geometry draw")) return false;
        std::vector<std::byte> scene_bytes(static_cast<std::size_t>(scene->GetRequiredSceneSize()));
        std::uint64_t written = 0U;
        if (!check(scene->BuildScene(scene_bytes.data(), scene_bytes.size(), &written) == com::ok &&
            written == scene_bytes.size() && written > sizeof(progpu_native_scene_header),
            "prepared geometry retained in actual scene")) return false;
    }
    // Immutable original occurrence geometry includes a no-ink advance and a
    // glyph with intentionally faulty hint bytecode. OUTLINE never executes it.
    capture::original_glyph_target frame{com::pointer<com::unknown>(target.get()), 1U,
        transform, {8, 50}, {128U, 128U}, 144, 120, {}, compat::text_antialias_mode::grayscale};
    std::shared_ptr<const capture::original_glyph_request> request;
    std::shared_ptr<const capture::prepared_original_glyph_run> prepared;
    if (capture::capture_original_glyph_request(font, run, compat::measuring_mode::natural,
        &parameters, frame, request) != com::ok || prepared_font->prepare(request, prepared) != com::ok) return false;
    if (!check(prepared->segments().size() == 8U && prepared->request().font == font &&
        prepared->request().target.transform.m21 == -0.5F && prepared->request().target.dpi_x == 144,
        "owned contours and complete source frame")) return false;
    for (std::size_t index = 0U; index < 8U; ++index) {
        const auto& segment = prepared->segments()[index];
        const float left = index < 4U ? 10.125F : 40.625F, right = index < 4U ? 47.625F : 78.125F;
        const float top = index < 4U ? -1.875F : -4.625F, bottom = index < 4U ? 48.125F : 45.375F;
        const auto corner = [&](progpu_native_point point) {
            return (point.x == left || point.x == right) && (point.y == top || point.y == bottom);
        };
        if (!check(segment.kind == PROGPU_NATIVE_PATH_SEGMENT_LINE && corner(segment.p0) && corner(segment.p1),
            "independent original design/offset/advance coordinates")) return false;
    }
    const auto retained = prepared;
    for (unsigned unsupported = 0U; unsupported < 10U; ++unsupported) {
        auto candidate_run = run;
        if (unsupported < 6U) parameters.mode = static_cast<compat::rendering_mode>(unsupported);
        if (unsupported == 6U) candidate_run.is_sideways = 1;
        if (unsupported == 7U) candidate_run.bidi_level = 1U;
        if (unsupported == 8U) candidate_run.glyph_advances = nullptr;
        const auto measuring = unsupported == 9U ? compat::measuring_mode::gdi_natural : compat::measuring_mode::natural;
        if (capture::capture_original_glyph_request(font, candidate_run, measuring,
            &parameters, frame, request) != com::ok) return false;
        if (!check(prepared_font->prepare(request, prepared) == compat::not_implemented && prepared == retained &&
            prepared_font->cached_glyph_count() == 3U, "unimplemented original mode/placement remains atomic")) return false;
        parameters.mode = compat::rendering_mode::outline;
    }
    for (const bool replace_parameters : {false, true}) {
        parameters.context = target.get();
        parameters.callback = replace_parameters
            ? +[](void* context) noexcept { static_cast<compat::render_target*>(context)->SetTextRenderingParams(nullptr); }
            : +[](void* context) noexcept { const compat::matrix_3x2_f changed{2, 0, 0, 2, 90, 91};
                static_cast<compat::render_target*>(context)->SetTransform(&changed); };
        target->BeginDraw();
        if (!check(prepared_target->DrawOwnedGlyphRun(prepared_font, {8, 50}, &run, brush.get(),
            compat::measuring_mode::natural) == compat::wrong_state &&
            target->EndDraw(nullptr, nullptr) == compat::wrong_state,
            "source callback invalidation rejects prepared publication")) return false;
        parameters.callback = nullptr;
        compat::scene_render_target_summary summary{}; scene->GetSummary(&summary);
        if (!check(summary.draw_count == 0U, "invalidated request publishes no native draw")) return false;
    }
    // Release retained stack-owned parameter before that source object ends.
    target->SetTextRenderingParams(nullptr);
    return true;
}

#if defined(_WIN32)
[[nodiscard]] bool original_windows_contract()
{
    com::pointer<IDWriteFactory> factory;
    if (!check(SUCCEEDED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_ISOLATED, __uuidof(IDWriteFactory),
        reinterpret_cast<IUnknown**>(factory.put()))), "original factory")) return false;
    com::pointer<IDWriteFontCollection> collection;
    com::pointer<IDWriteFontFamily> family;
    com::pointer<IDWriteFont> original_font;
    com::pointer<IDWriteFontFace> original_face;
    if (!check(SUCCEEDED(factory->GetSystemFontCollection(collection.put())) &&
        collection->GetFontFamilyCount() != 0U && SUCCEEDED(collection->GetFontFamily(0U, family.put())) &&
        SUCCEEDED(family->GetFont(0U, original_font.put())) &&
        SUCCEEDED(original_font->CreateFontFace(original_face.put())), "original face")) return false;
    // Obtain the declared canonical face interface; no undocumented tail or
    // reinterpretation of an unrelated font/interface object is permitted.
    com::pointer<compat::font_face> face;
    if (!check(SUCCEEDED(original_face.as(compat::font_face_interface_id, face)), "typed original face")) return false;
    std::shared_ptr<const capture::original_font_capture> font;
    if (!check(capture::capture_original_font(face.get(), font) == com::ok &&
        font->face_index == original_face->GetIndex() && font->face_type == original_face->GetType() &&
        font->simulations == original_face->GetSimulations() && font->glyph_count == original_face->GetGlyphCount(),
        "original face metadata")) return false;
    UINT32 count = 0U;
    if (!check(SUCCEEDED(original_face->GetFiles(&count, nullptr)) && count == font->files.size(),
        "original ordered file count")) return false;
    std::vector<IDWriteFontFile*> files(count, nullptr);
    struct release_files final {
        std::vector<IDWriteFontFile*>& files;
        ~release_files() { for (auto* file : files) if (file != nullptr) file->Release(); }
    } release{files};
    if (!check(SUCCEEDED(original_face->GetFiles(&count, files.data())), "original files")) return false;
    for (UINT32 index = 0U; index < count; ++index) {
        com::pointer<IDWriteFontFileLoader> loader;
        com::pointer<IDWriteFontFileStream> stream;
        const void* key = nullptr; UINT32 key_size = 0U;
        UINT64 size = 0U;
        if (!check(SUCCEEDED(files[index]->GetReferenceKey(&key, &key_size)) &&
            SUCCEEDED(files[index]->GetLoader(loader.put())) &&
            SUCCEEDED(loader->CreateStreamFromKey(key, key_size, stream.put())) &&
            SUCCEEDED(stream->GetFileSize(&size)) && size == font->files[index].size(),
            "independent original stream")) return false;
        const void* bytes = nullptr; void* context = nullptr;
        if (!check(SUCCEEDED(stream->ReadFileFragment(&bytes, 0U, size, &context)), "original fragment")) return false;
        const bool identical = std::memcmp(bytes, font->files[index].data(), static_cast<std::size_t>(size)) == 0;
        stream->ReleaseFileFragment(context);
        if (!check(identical, "every original font byte")) return false;
    }
    return true;
}
#endif
} // namespace

bool progpu_native_direct2d_font_capture_tests()
{
    if (!source_contracts()) return false;
    if (!prepared_source_contracts()) return false;
#if defined(_WIN32)
    if (!original_windows_contract()) return false;
#endif
    return true;
}
