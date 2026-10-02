#include "progpu_native_direct2d_font_capture.hpp"

#include <array>
#include <bit>
#include <cstring>
#include <unordered_set>

namespace progpu::native::direct2d {
namespace {

struct font_capture_scope;
thread_local const font_capture_scope* active_capture = nullptr;

struct font_capture_scope final {
    compat::font_face* face;
    const font_capture_scope* previous;
    com::unknown* identity = nullptr;
    explicit font_capture_scope(compat::font_face* value) noexcept
        : face(value), previous(active_capture) { active_capture = this; }
    ~font_capture_scope() { active_capture = previous; }
};

[[nodiscard]] com::result original_identity(com::unknown* source, com::pointer<com::unknown>& output) noexcept
{
    void* value = nullptr;
    const auto status = source->QueryInterface(com::unknown_interface_id(), &value);
    com::pointer<com::unknown> identity;
    identity.attach(static_cast<com::unknown*>(value));
    if (com::failed(status)) return status;
    if (status != com::ok || !identity) return com::pointer_error;
    output = std::move(identity);
    return com::ok;
}

[[nodiscard]] com::result read_axis_values(original_font_face5* face,
    std::vector<original_font_axis_value>& values, bool& variations)
{
    const auto count = face->GetFontAxisValueCount();
    if (count > original_font_capture::maximum_axes) return com::invalid_argument;
    const bool variable = face->HasVariations() != 0;
    if (variable && count == 0U) return com::invalid_argument;
    std::vector<original_font_axis_value> candidate(count);
    original_font_axis_value empty{};
    const auto status = face->GetFontAxisValues(candidate.empty() ? &empty : candidate.data(), count);
    if (com::failed(status)) return status;
    if (status != com::ok || face->GetFontAxisValueCount() != count || (face->HasVariations() != 0) != variable)
        return com::invalid_argument;
    std::unordered_set<std::uint32_t> tags;
    tags.reserve(count);
    for (const auto& axis : candidate) {
        if (!std::isfinite(axis.value)) return com::invalid_argument;
        for (unsigned shift = 0U; shift < 32U; shift += 8U) {
            const auto character = (axis.tag >> shift) & 255U;
            if (character < 32U || character > 126U) return com::invalid_argument;
        }
        if (!tags.insert(axis.tag).second) return com::invalid_argument;
    }
    values = std::move(candidate);
    variations = variable;
    return com::ok;
}

[[nodiscard]] bool same_axes(const std::vector<original_font_axis_value>& first,
    const std::vector<original_font_axis_value>& second) noexcept
{
    if (first.size() != second.size()) return false;
    for (std::size_t index = 0U; index < first.size(); ++index)
        if (first[index].tag != second[index].tag ||
            std::bit_cast<std::uint32_t>(first[index].value) != std::bit_cast<std::uint32_t>(second[index].value)) return false;
    return true;
}

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
    for (auto* current = active_capture; current != nullptr; current = current->previous)
        if (current->face == face) return compat::wrong_state;
    font_capture_scope capture_scope(face);
    try {
        auto candidate = std::make_shared<original_font_capture>();
        candidate->face = com::pointer<compat::font_face>(face);
        com::pointer<original_font_face5> axis_face;
        com::pointer<com::unknown> source_identity;
        void* queried = nullptr;
        auto status = face->QueryInterface(original_font_face5_id, &queried);
        axis_face.attach(static_cast<original_font_face5*>(queried));
        if (status == com::no_interface) {
            if (axis_face) return com::invalid_argument;
        } else {
            if (com::failed(status)) return status;
            if (status != com::ok || !axis_face) return com::pointer_error;
            status = original_identity(face, source_identity);
            if (com::failed(status)) return status;
            com::pointer<com::unknown> axis_identity;
            status = original_identity(axis_face.get(), axis_identity);
            if (com::failed(status)) return status;
            if (axis_identity.get() != source_identity.get()) return com::invalid_argument;
            for (auto* current = capture_scope.previous; current != nullptr; current = current->previous)
                if (current->identity == source_identity.get()) return compat::wrong_state;
            capture_scope.identity = source_identity.get();
            status = read_axis_values(axis_face.get(), candidate->axis_values, candidate->has_variations);
            if (com::failed(status)) return status;
            candidate->axis_values_available = true;
        }
        candidate->face_type = face->GetType();
        candidate->face_index = face->GetIndex();
        candidate->simulations = face->GetSimulations();
        candidate->symbol_font = face->IsSymbolFont();
        candidate->glyph_count = face->GetGlyphCount();
        // All these are source identities, not coverage capabilities. Preserve
        // unknown future type/simulation values instead of mapping to defaults.
        std::uint32_t count = 0U;
        status = face->GetFiles(&count, nullptr);
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
        if (axis_face) {
            // Font faces are immutable. Reject a fake/mutating provider instead
            // of combining earlier axis coordinates with later source files.
            std::vector<original_font_axis_value> observed;
            bool variations = false;
            status = read_axis_values(axis_face.get(), observed, variations);
            if (com::failed(status)) return status;
            if (variations != candidate->has_variations || !same_axes(observed, candidate->axis_values))
                return com::invalid_argument;
            com::pointer<com::unknown> final_source, final_axes;
            status = original_identity(face, final_source);
            if (com::failed(status)) return status;
            status = original_identity(axis_face.get(), final_axes);
            if (com::failed(status)) return status;
            if (source_identity.get() != final_source.get() || source_identity.get() != final_axes.get())
                return com::invalid_argument;
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
