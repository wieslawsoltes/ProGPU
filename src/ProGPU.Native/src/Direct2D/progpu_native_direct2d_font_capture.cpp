#include "progpu_native_direct2d_font_capture.hpp"

#include <array>
#include <cstring>

namespace progpu::native::direct2d {
namespace {

// GetFiles owns each returned reference, even when a later file fails. Keep the
// complete fixed-capacity array under a guard before the source fills it.
struct source_files final {
    std::array<com::unknown*, original_font_capture::maximum_files> values{};
    ~source_files()
    {
        for (auto* value : values) if (value != nullptr) value->Release();
    }
};

struct source_fragment final {
    original_font_stream* stream;
    void* context;
    ~source_fragment() { stream->ReleaseFileFragment(context); }
};

[[nodiscard]] com::result capture_file(com::unknown* source,
    std::uint64_t remaining, std::vector<std::byte>& output)
{
    if (source == nullptr) return com::pointer_error;
    com::pointer<original_font_file> file;
    void* queried = nullptr;
    const auto query_status = source->QueryInterface(original_font_file_id, &queried);
    // COM promises null on failure. Retain any returned reference for cleanup
    // without using a failed query as an interface-admission proof.
    file.attach(static_cast<original_font_file*>(queried));
    if (com::failed(query_status)) return query_status;
    if (!file) return com::pointer_error;

    const void* key = nullptr;
    std::uint32_t key_size = 0U;
    auto status = file->GetReferenceKey(&key, &key_size);
    if (com::failed(status)) return status;
    if (key_size > original_font_capture::maximum_key_bytes ||
        (key_size != 0U && key == nullptr)) return com::invalid_argument;
    // Opaque keys are meaningful only to this exact retained loader. Snapshot
    // before another external callback; never interpret one as a filename.
    std::vector<std::byte> key_copy(key_size);
    if (key_size != 0U) std::memcpy(key_copy.data(), key, key_size);
    com::pointer<original_font_loader> loader;
    status = file->GetLoader(loader.put());
    if (com::failed(status)) return status;
    if (!loader) return com::pointer_error;
    com::pointer<original_font_stream> stream;
    status = loader->CreateStreamFromKey(key_copy.data(), key_size, stream.put());
    if (com::failed(status)) return status;
    if (!stream) return com::pointer_error;
    std::uint64_t size = 0U;
    status = stream->GetFileSize(&size);
    if (com::failed(status)) return status;
    if (size == 0U || size > remaining) return com::invalid_argument;
    std::vector<std::byte> bytes(static_cast<std::size_t>(size));
    const void* data = nullptr;
    void* context = nullptr;
    status = stream->ReadFileFragment(&data, 0U, size, &context);
    if (com::failed(status)) return status;
    // A successful fragment must be released exactly once, including a null
    // context. Failure never grants a fragment lease.
    const source_fragment fragment{stream.get(), context};
    if (data == nullptr) return com::pointer_error;
    std::memcpy(bytes.data(), data, bytes.size());
    output = std::move(bytes);
    return com::ok;
}

[[nodiscard]] bool valid_target(const original_glyph_target& value) noexcept
{
    const auto& matrix = value.transform;
    return value.identity && value.generation != 0U && value.pixels.width != 0U &&
        value.pixels.height != 0U && std::isfinite(value.dpi_x) && value.dpi_x > 0.0F &&
        std::isfinite(value.dpi_y) && value.dpi_y > 0.0F &&
        std::isfinite(value.baseline.x) && std::isfinite(value.baseline.y) &&
        std::isfinite(matrix.m11) && std::isfinite(matrix.m12) &&
        std::isfinite(matrix.m21) && std::isfinite(matrix.m22) &&
        std::isfinite(matrix.m31) && std::isfinite(matrix.m32) &&
        static_cast<std::uint32_t>(value.format.alpha) <= 3U &&
        static_cast<std::uint32_t>(value.antialias) <= 3U &&
        static_cast<std::uint32_t>(value.units) <= 1U &&
        static_cast<std::uint32_t>(value.blend) <= 4U;
}

} // namespace

com::result capture_original_font(compat::font_face* face,
    std::shared_ptr<const original_font_capture>& output) noexcept
{
    if (face == nullptr) return com::pointer_error;
    try {
        auto candidate = std::make_shared<original_font_capture>();
        candidate->face = com::pointer<compat::font_face>(face);
        candidate->face_type = face->GetType();
        candidate->face_index = face->GetIndex();
        candidate->simulations = face->GetSimulations();
        candidate->symbol_font = face->IsSymbolFont();
        candidate->glyph_count = face->GetGlyphCount();
        // All these are source identities, not coverage capabilities. Preserve
        // unknown future type/simulation values instead of mapping to defaults.
        std::uint32_t count = 0U;
        auto status = face->GetFiles(&count, nullptr);
        if (com::failed(status)) return status;
        if (count == 0U || count > original_font_capture::maximum_files)
            return com::invalid_argument;
        source_files files;
        const auto expected_count = count;
        status = face->GetFiles(&count, files.values.data());
        if (com::failed(status)) return status;
        if (count != expected_count) return com::invalid_argument;
        candidate->files.resize(count);
        auto remaining = original_font_capture::maximum_total_bytes;
        for (std::uint32_t index = 0U; index < count; ++index) {
            status = capture_file(files.values[index], remaining, candidate->files[index]);
            if (com::failed(status)) return status;
            remaining -= candidate->files[index].size();
        }
        output = std::move(candidate);
        return com::ok;
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return com::invalid_argument;
    }
}

com::result capture_original_glyph_request(std::shared_ptr<const original_font_capture> font,
    const compat::glyph_run& run, compat::measuring_mode measuring,
    compat::rendering_parameters* rendering, const original_glyph_target& target,
    std::shared_ptr<const original_glyph_request>& output) noexcept
{
    if (!font || font->face.get() != run.font_face_value || font->files.empty() ||
        !std::isfinite(run.font_em_size) || run.font_em_size <= 0.0F ||
        static_cast<std::uint32_t>(measuring) > 2U || !valid_target(target))
        return com::invalid_argument;
    try {
        auto candidate = std::make_shared<original_glyph_request>();
        candidate->font = std::move(font);
        candidate->em_size = run.font_em_size;
        candidate->sideways = run.is_sideways;
        candidate->bidi_level = run.bidi_level;
        candidate->measuring = measuring;
        // Copy all borrowed scalar/array data before target/parameter AddRef or
        // getters can reenter the caller. Only the target owner may validate its
        // live generation; this factory does not mutate that target.
        auto status = candidate->glyphs.capture(run.glyph_indices, run.glyph_advances,
            run.glyph_offsets, run.glyph_count, [](const compat::glyph_offset& offset) {
                return std::isfinite(offset.advance_offset) && std::isfinite(offset.ascender_offset);
            });
        if (com::failed(status)) return status;
        for (std::uint32_t index = 0U; index < candidate->glyphs.count(); ++index)
            if (candidate->glyphs.indices()[index] >= candidate->font->glyph_count)
                return com::invalid_argument;
        // First make a value copy without calling the target's AddRef. Acquiring
        // its strong reference is the only subsequent target callback.
        candidate->target.generation = target.generation;
        candidate->target.transform = target.transform;
        candidate->target.baseline = target.baseline;
        candidate->target.pixels = target.pixels;
        candidate->target.dpi_x = target.dpi_x;
        candidate->target.dpi_y = target.dpi_y;
        candidate->target.format = target.format;
        candidate->target.antialias = target.antialias;
        candidate->target.tag1 = target.tag1;
        candidate->target.tag2 = target.tag2;
        candidate->target.units = target.units;
        candidate->target.blend = target.blend;
        candidate->target.identity = target.identity;
        // As with every synchronous COM argument, the caller owns a live
        // parameters reference through this call, including reentrant AddRef.
        com::pointer<compat::rendering_parameters> retained_parameters(rendering);
        status = capture_text_rendering_values(retained_parameters.get(), candidate->rendering);
        if (com::failed(status)) return status;
        output = std::move(candidate);
        return com::ok;
    } catch (const std::bad_alloc&) {
        return com::out_of_memory;
    } catch (...) {
        return com::invalid_argument;
    }
}

} // namespace progpu::native::direct2d
