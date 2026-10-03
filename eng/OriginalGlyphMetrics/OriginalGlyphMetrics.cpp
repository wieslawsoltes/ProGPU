#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dwrite_3.h>
#include <d2d1.h>
#include <bcrypt.h>
#include <wrl/client.h>

#include "progpu_native_direct2d_vertical_font_fixture.hpp"
#include "progpu_native_direct2d_cff_vertical_fixture.hpp"

#include <atomic>
#include <bit>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <iomanip>
#include <sstream>
#include <stdexcept>
#include <string>

// Original installed DirectWrite observations only. This program links no
// ProGPU product library and uses no expected-value/font-decoder helper. The
// included repository fixtures only construct original, owned font bytes.
namespace {
using Microsoft::WRL::ComPtr;
namespace fixture = progpu::native::direct2d::tests;
using bytes = std::vector<std::byte>;
constexpr auto deadline = std::chrono::seconds(60);
const auto started = std::chrono::steady_clock::now();

void budget() {
    if (std::chrono::steady_clock::now() - started > deadline)
        throw std::runtime_error("original glyph observation deadline");
}
void required(HRESULT status, const char* operation) {
    if (FAILED(status)) throw std::runtime_error(std::string(operation) + " HRESULT=" + std::to_string(status));
}
std::string hex32(std::uint32_t value) {
    std::ostringstream text;
    text << '"' << std::hex << std::setw(8) << std::setfill('0') << value << '"';
    return text.str();
}
std::string bits(float value) { return hex32(std::bit_cast<std::uint32_t>(value)); }
std::string hr(HRESULT value) { return hex32(static_cast<std::uint32_t>(value)); }
std::string quoted(std::string_view text) {
    std::string result = "\"";
    for (const auto ch : text) {
        if (ch == '\\' || ch == '"') result += '\\';
        if (static_cast<unsigned char>(ch) < 32U) throw std::runtime_error("control character in identity");
        result += ch;
    }
    return result + '"';
}
std::string utf8(const std::wstring& text) {
    if (text.empty()) return {};
    const auto size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(),
        static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) throw std::runtime_error("identity UTF8 conversion");
    std::string result(static_cast<std::size_t>(size), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()),
        result.data(), size, nullptr, nullptr) != size) throw std::runtime_error("identity UTF8 conversion");
    return result;
}
std::wstring module_path(const wchar_t* name) {
    const auto module = GetModuleHandleW(name);
    if (module == nullptr) throw std::runtime_error("required original module absent");
    std::wstring path(32768U, L'\0');
    const auto written = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (written == 0U || written >= path.size()) throw std::runtime_error("loaded module path");
    path.resize(written);
    return path;
}
std::string sha256(std::span<const std::byte> data) {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
        throw std::runtime_error("SHA256 provider");
    struct cleanup final {
        BCRYPT_ALG_HANDLE& algorithm; BCRYPT_HASH_HANDLE& hash;
        ~cleanup() { if (hash != nullptr) BCryptDestroyHash(hash); if (algorithm != nullptr) BCryptCloseAlgorithmProvider(algorithm, 0); }
    } release{algorithm, hash};
    if (data.size() > 1024U * 1024U || BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) < 0 ||
        BCryptHashData(hash, reinterpret_cast<PUCHAR>(const_cast<std::byte*>(data.data())),
            static_cast<ULONG>(data.size()), 0) < 0) throw std::runtime_error("font SHA256 input");
    std::array<unsigned char, 32U> digest{};
    if (BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) < 0)
        throw std::runtime_error("font SHA256 finish");
    std::ostringstream text;
    for (const auto value : digest) text << std::hex << std::setw(2) << std::setfill('0') << static_cast<unsigned>(value);
    return text.str();
}
void create_font_file(const std::filesystem::path& path, const bytes& data) {
    const auto file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("refusing existing or unavailable font destination");
    DWORD written = 0;
    const bool okay = WriteFile(file, data.data(), static_cast<DWORD>(data.size()), &written, nullptr) && written == data.size();
    CloseHandle(file);
    if (!okay) throw std::runtime_error("original font artifact write");
}
template<class T, std::size_t N> std::string raw_words(const std::array<T, N>& value) {
    static_assert(sizeof(value) % sizeof(std::uint32_t) == 0U);
    std::array<std::uint32_t, sizeof(value) / sizeof(std::uint32_t)> words{};
    std::memcpy(words.data(), value.data(), sizeof(value));
    std::ostringstream result; result << '[';
    for (std::size_t i = 0; i < words.size(); ++i) result << (i == 0 ? "" : ",") << hex32(words[i]);
    result << ']'; return result.str();
}

struct callback final { const char* kind; std::array<std::uint32_t, 6> lanes{}; std::uint32_t count = 0; };
class outline_sink final : public IDWriteGeometrySink {
public:
    explicit outline_sink(ID2D1GeometrySink* destination) : destination_(destination) { events_.reserve(128U); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** value) override {
        if (value == nullptr) return E_POINTER;
        *value = nullptr;
        if (id != __uuidof(IUnknown) && id != __uuidof(ID2D1SimplifiedGeometrySink)) return E_NOINTERFACE;
        *value = static_cast<IDWriteGeometrySink*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override { const auto left = --references_; if (left == 0U) delete this; return left; }
    void STDMETHODCALLTYPE SetFillMode(D2D1_FILL_MODE mode) override {
        record({"fill", {static_cast<std::uint32_t>(mode)}, 1}); destination_->SetFillMode(mode);
    }
    void STDMETHODCALLTYPE SetSegmentFlags(D2D1_PATH_SEGMENT flags) override {
        record({"flags", {static_cast<std::uint32_t>(flags)}, 1}); destination_->SetSegmentFlags(flags);
    }
    void STDMETHODCALLTYPE BeginFigure(D2D1_POINT_2F point, D2D1_FIGURE_BEGIN begin) override {
        record({"begin", {std::bit_cast<std::uint32_t>(point.x), std::bit_cast<std::uint32_t>(point.y),
            static_cast<std::uint32_t>(begin)}, 3}); destination_->BeginFigure(point, begin);
    }
    void STDMETHODCALLTYPE AddLines(const D2D1_POINT_2F* points, UINT32 count) override {
        record({"lines-call", {count}, 1});
        for (UINT32 i = 0; i < count; ++i) record({"line", {std::bit_cast<std::uint32_t>(points[i].x),
            std::bit_cast<std::uint32_t>(points[i].y)}, 2});
        destination_->AddLines(points, count);
    }
    void STDMETHODCALLTYPE AddBeziers(const D2D1_BEZIER_SEGMENT* curves, UINT32 count) override {
        record({"cubics-call", {count}, 1});
        for (UINT32 i = 0; i < count; ++i) record({"cubic", {std::bit_cast<std::uint32_t>(curves[i].point1.x),
            std::bit_cast<std::uint32_t>(curves[i].point1.y), std::bit_cast<std::uint32_t>(curves[i].point2.x),
            std::bit_cast<std::uint32_t>(curves[i].point2.y), std::bit_cast<std::uint32_t>(curves[i].point3.x),
            std::bit_cast<std::uint32_t>(curves[i].point3.y)}, 6});
        destination_->AddBeziers(curves, count);
    }
    void STDMETHODCALLTYPE EndFigure(D2D1_FIGURE_END end) override {
        record({"end", {static_cast<std::uint32_t>(end)}, 1}); destination_->EndFigure(end);
    }
    HRESULT STDMETHODCALLTYPE Close() override { ++close_calls_; return destination_->Close(); }
    std::string json() const {
        if (overflow_) throw std::runtime_error("outline callback inventory exceeds bound");
        std::ostringstream output; output << '[';
        for (std::size_t i = 0; i < events_.size(); ++i) {
            const auto& event = events_[i]; output << (i == 0 ? "" : ",") << "{\"kind\":" << quoted(event.kind) << ",\"bits\":[";
            for (std::size_t lane = 0; lane < event.count; ++lane) output << (lane == 0 ? "" : ",") << hex32(event.lanes[lane]);
            output << "]}";
        }
        output << ']'; return output.str();
    }
    std::size_t count() const { return events_.size(); }
    unsigned close_calls() const { return close_calls_; }
private:
    void record(callback value) noexcept {
        if (events_.size() >= 128U) { overflow_ = true; return; }
        events_.push_back(value); // Entire capacity reserved before entering COM.
    }
    std::atomic<ULONG> references_{1U};
    ComPtr<ID2D1GeometrySink> destination_;
    std::vector<callback> events_;
    unsigned close_calls_ = 0;
    bool overflow_ = false;
};

struct family final { const char* name; bytes data; bool variable; bool precision; };
bytes omit(bytes source, std::initializer_list<std::uint32_t> tags) {
    auto tables = fixture::vertical_font_wire::original_tables(source);
    std::erase_if(tables, [&](const auto& table) { return std::find(tags.begin(), tags.end(), table.tag) != tags.end(); });
    return fixture::vertical_font_wire::assemble(std::move(tables), fixture::vertical_font_wire::read32(source, 0U));
}
std::vector<family> families() {
    using fixture::vertical_font_kind;
    fixture::vertical_font_options tt{};
    fixture::vertical_font_options cff{}; cff.kind = vertical_font_kind::cff; cff.vorg = true;
    auto tt_variable = tt; tt_variable.kind = vertical_font_kind::truetype_variable; tt_variable.compact_metrics = true;
    auto tt_vvar = tt_variable; tt_vvar.vvar = true;
    auto tt_maps = tt_vvar; tt_maps.side_bearing_maps = true;
    auto tt_precedence = tt_maps; tt_precedence.vvar_precedence_discriminator = true;
    auto cff2 = cff; cff2.kind = vertical_font_kind::cff2_variable; cff2.compact_metrics = true;
    auto cff2_vvar = cff2; cff2_vvar.vvar = true; cff2_vvar.origin_map = true;
    auto cubic = fixture::make_cff_font(fixture::cff_font_kind::cff1_affine);
    auto cubic_tables = fixture::vertical_font_wire::original_tables(cubic);
    fixture::vertical_font_wire::set(cubic_tables, 0x76686561U, fixture::vertical_font_wire::header(false, true));
    fixture::vertical_font_wire::set(cubic_tables, 0x766D7478U, fixture::vertical_font_wire::metrics(false, true));
    auto cubic_vertical = fixture::vertical_font_wire::assemble(std::move(cubic_tables), 0x4F54544FU);
    return {
        {"tt-static", fixture::make_vertical_font(tt), false, false},
        {"tt-missing-vertical-pair", omit(fixture::make_vertical_font(tt), {0x76686561U, 0x766D7478U}), false, false},
        {"cff-vorg", fixture::make_vertical_font(cff), false, false},
        {"cff-no-vorg", omit(fixture::make_vertical_font(cff), {0x564F5247U}), false, false},
        {"cff-cubic-no-vorg", std::move(cubic_vertical), false, false},
        {"cff-cubic-no-vertical-pair", std::move(cubic), false, false},
        {"cff-origin-integer-extremum", fixture::make_cff_vertical_font(false), false, false},
        {"cff-origin-fractional-extremum", fixture::make_cff_vertical_font(true), false, false},
        {"tt-variable-gvar", fixture::make_vertical_font(tt_variable), true, false},
        {"tt-variable-vvar", fixture::make_vertical_font(tt_vvar), true, false},
        {"tt-variable-maps", fixture::make_vertical_font(tt_maps), true, true},
        {"tt-variable-precedence", fixture::make_vertical_font(tt_precedence), true, false},
        {"cff2-variable-fixed", fixture::make_vertical_font(cff2), true, false},
        {"cff2-variable-origin", fixture::make_vertical_font(cff2_vvar), true, true}};
}

std::size_t outline_count = 0U, analysis_count = 0U, instance_count = 0U;
void observe_face(std::ostream& output, IDWriteFactory5* factory, ID2D1Factory* geometry_factory, IDWriteFontFace* face) {
    constexpr std::array<UINT16, 3> indices{1U, 0U, 2U};
    constexpr float em = 15.625F;
    const std::array<float, 3> signed_advances{16.0F, -3.0F, 9.0F};
    const std::array<DWRITE_GLYPH_OFFSET, 3> offsets{{{0.25F, 0.5F}, {0.0F, 0.0F}, {-0.75F, 2.5F}}};
    DWRITE_FONT_METRICS font_metrics{}; face->GetMetrics(&font_metrics);
    output << "\"units_per_em\":" << font_metrics.designUnitsPerEm << ",\"glyph_count\":" << face->GetGlyphCount()
        << ",\"face_index\":" << face->GetIndex() << ",\"simulations\":" << face->GetSimulations()
        << ",\"em_bits\":" << bits(em) << ",\"glyphs\":[1,0,2],\"directions\":[";
    ComPtr<IDWriteFontFace1> face1; required(face->QueryInterface(IID_PPV_ARGS(face1.GetAddressOf())), "Face1");
    bool first_direction = true;
    for (const BOOL sideways : {0, 1, -1}) {
        budget();
        if (!first_direction) output << ','; first_direction = false;
        std::array<DWRITE_GLYPH_METRICS, 4> metrics{};
        std::array<INT32, 4> advances{};
        std::memset(metrics.data(), 0xA5, sizeof(metrics)); std::memset(advances.data(), 0xA5, sizeof(advances));
        const auto metrics_before = metrics; const auto advances_before = advances;
        const auto metrics_status = face->GetDesignGlyphMetrics(indices.data(), 3U, metrics.data(), sideways);
        const auto advances_status = face1->GetDesignGlyphAdvances(3U, indices.data(), advances.data(), sideways);
        output << "{\"sideways_raw\":" << sideways << ",\"metrics_hresult\":" << hr(metrics_status)
            << ",\"metrics_before_words\":" << raw_words(metrics_before) << ",\"metrics_after_words\":" << raw_words(metrics)
            << ",\"advances_hresult\":" << hr(advances_status) << ",\"advances_before_words\":" << raw_words(advances_before)
            << ",\"advances_after_words\":" << raw_words(advances) << ",\"runs\":[";
        std::array<float, 3> actual_advances{};
        const bool has_actual = SUCCEEDED(advances_status) && font_metrics.designUnitsPerEm != 0U;
        if (has_actual) for (std::size_t i = 0; i < actual_advances.size(); ++i)
            actual_advances[i] = static_cast<float>(advances[i]) * (em / static_cast<float>(font_metrics.designUnitsPerEm));
        bool first_run = true;
        for (unsigned path = 0; path < 3U; ++path) {
            if (path == 1U && !has_actual) continue; // Explicitly recorded unavailable actual-advance source, no guessed substitute.
            const auto selected_advances = path == 0U ? nullptr : path == 1U ? actual_advances.data() : signed_advances.data();
            for (const bool positioned : {false, true}) {
                // Seven named protocols per face, not a Cartesian sweep. All
                // raw flags and bidi levels remain explicit original inputs.
                const bool selected_protocol = sideways == 0 ?
                    (path == 0U && !positioned) || (path == 2U && positioned) : sideways == 1 ?
                    (path == 0U && !positioned) || (path == 1U && positioned) || (path == 2U && positioned) :
                    (path == 0U && positioned) || (path == 2U && !positioned);
                if (!selected_protocol) continue;
                const BOOL selected_rtl = sideways == 0 ? (path == 2U ? 1 : 0) :
                    sideways == 1 ? (path == 2U ? 1 : 0) : (path == 0U ? -1 : 0);
                const UINT32 selected_bidi = sideways == 0 ? (path == 2U ? 1U : 0U) :
                    sideways == 1 ? (path == 2U ? 1U : path == 1U ? 2U : 0U) : (path == 0U ? 3U : 2U);
                if (!first_run) output << ','; first_run = false;
                output << "{\"advance_source\":" << quoted(path == 0U ? "null" : path == 1U ? "actual-design-api" : "signed-literal")
                    << ",\"actual_advance_source_available\":" << (has_actual ? "true" : "false") << ",\"advance_bits\":[";
                if (selected_advances != nullptr) for (std::size_t i = 0; i < 3U; ++i) output << (i == 0 ? "" : ",") << bits(selected_advances[i]);
                output << "],\"offset_bits\":[";
                if (positioned) for (std::size_t i = 0; i < 3U; ++i) output << (i == 0 ? "" : ",") << '[' << bits(offsets[i].advanceOffset) << ',' << bits(offsets[i].ascenderOffset) << ']';
                output << "],\"outline_observations\":[";
                bool first_outline = true;
                for (const BOOL rtl : {selected_rtl}) {
                    budget(); ++outline_count;
                    ComPtr<ID2D1PathGeometry> geometry; ComPtr<ID2D1GeometrySink> geometry_sink;
                    required(geometry_factory->CreatePathGeometry(geometry.GetAddressOf()), "CPU path geometry");
                    required(geometry->Open(geometry_sink.GetAddressOf()), "CPU geometry sink");
                    ComPtr<outline_sink> sink; sink.Attach(new outline_sink(geometry_sink.Get()));
                    const auto status = face->GetGlyphRunOutline(em, indices.data(), selected_advances,
                        positioned ? offsets.data() : nullptr, 3U, sideways, rtl, sink.Get());
                    const auto original_close_calls = sink->close_calls();
                    const auto close_status = sink->Close(); // Separate observer finalization, never the API's HRESULT.
                    D2D1_RECT_F bounds{}; std::memset(&bounds, 0xA5, sizeof(bounds));
                    const auto before = bounds;
                    const auto bounds_status = geometry->GetBounds(nullptr, &bounds);
                    if (!first_outline) output << ','; first_outline = false;
                    output << "{\"rtl_raw\":" << rtl << ",\"hresult\":" << hr(status) << ",\"callback_records\":" << sink->count()
                        << ",\"api_close_calls\":" << original_close_calls << ",\"observer_close_hresult\":" << hr(close_status)
                        << ",\"callbacks\":" << sink->json() << ",\"bounds_hresult\":" << hr(bounds_status)
                        << ",\"bounds_before_bits\":[" << bits(before.left) << ',' << bits(before.top) << ',' << bits(before.right) << ',' << bits(before.bottom)
                        << "],\"bounds_after_bits\":[" << bits(bounds.left) << ',' << bits(bounds.top) << ',' << bits(bounds.right) << ',' << bits(bounds.bottom) << "]}";
                }
                output << "],\"run_analysis\":[";
                for (const UINT32 bidi : {selected_bidi}) {
                    budget(); ++analysis_count;
                    const DWRITE_GLYPH_RUN run{face, em, 3U, indices.data(), selected_advances,
                        positioned ? offsets.data() : nullptr, sideways, bidi};
                    ComPtr<IDWriteGlyphRunAnalysis> analysis;
                    const auto status = factory->CreateGlyphRunAnalysis(&run, nullptr, DWRITE_RENDERING_MODE1_ALIASED,
                        DWRITE_MEASURING_MODE_NATURAL, DWRITE_GRID_FIT_MODE_DISABLED, DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE,
                        0.0F, 0.0F, analysis.GetAddressOf());
                    RECT bounds{}; std::memset(&bounds, 0xA5, sizeof(bounds));
                    const auto before = bounds;
                    const auto bounds_status = analysis != nullptr ? analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_ALIASED_1x1, &bounds) : E_POINTER;
                    output << "{\"bidi_level\":" << bidi << ",\"hresult\":" << hr(status)
                        << ",\"output_before_null\":true,\"output_after_null\":" << (analysis == nullptr ? "true" : "false")
                        << ",\"bounds_called\":" << (analysis != nullptr ? "true" : "false") << ",\"bounds_hresult\":" << hr(bounds_status)
                        << ",\"bounds_before_words\":" << raw_words(std::array{before}) << ",\"bounds_after_words\":" << raw_words(std::array{bounds}) << '}';
                }
                output << "]}";
            }
        }
        output << "]}";
    }
    output << ']';
}

void observe(std::ostream& output, const std::filesystem::path& directory) {
    ComPtr<IDWriteFactory5> factory;
    required(DWriteCreateFactory(DWRITE_FACTORY_TYPE_ISOLATED, __uuidof(IDWriteFactory5),
        reinterpret_cast<IUnknown**>(factory.GetAddressOf())), "installed DirectWrite factory");
    ComPtr<ID2D1Factory> geometry_factory;
    required(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, geometry_factory.GetAddressOf()), "CPU geometry factory");
    ComPtr<IDWriteInMemoryFontFileLoader> loader;
    required(factory->CreateInMemoryFontFileLoader(loader.GetAddressOf()), "font loader");
    required(factory->RegisterFontFileLoader(loader.Get()), "font loader registration");
    struct unregister final {
        IDWriteFactory* factory; IDWriteFontFileLoader* loader;
        ~unregister() { factory->UnregisterFontFileLoader(loader); }
    } registration{factory.Get(), loader.Get()};
    output << "{\"schema\":1,\"qualified\":false,\"kind\":\"original-cpu-glyph-metrics\",\"pointer_bits\":" << sizeof(void*) * 8U
        << ",\"directwrite_module\":" << quoted(utf8(module_path(L"dwrite.dll")))
        << ",\"geometry_module\":" << quoted(utf8(module_path(L"d2d1.dll")))
        << ",\"metric_word_order\":[\"leftSideBearing\",\"advanceWidth\",\"rightSideBearing\",\"topSideBearing\",\"advanceHeight\",\"bottomSideBearing\",\"verticalOriginY\"],\"families\":[";
    bool first_family = true;
    for (const auto& item : families()) {
        budget();
        create_font_file(directory / (std::string(item.name) + ".font"), item.data);
        ComPtr<IDWriteFontFile> file;
        const auto file_status = loader->CreateInMemoryFontFileReference(factory.Get(), item.data.data(),
            static_cast<UINT32>(item.data.size()), nullptr, file.GetAddressOf());
        BOOL supported = std::bit_cast<BOOL>(std::uint32_t{0xA5A5A5A5U}); UINT32 face_count = 0xA5A5A5A5U;
        DWRITE_FONT_FILE_TYPE file_type = DWRITE_FONT_FILE_TYPE_UNKNOWN;
        DWRITE_FONT_FACE_TYPE face_type = DWRITE_FONT_FACE_TYPE_UNKNOWN;
        const auto analyze_status = file != nullptr ? file->Analyze(&supported, &file_type, &face_type, &face_count) : E_POINTER;
        ComPtr<IDWriteFontFace> face; IDWriteFontFile* files[]{file.Get()};
        const bool create_called = SUCCEEDED(analyze_status) && supported != 0 && face_count != 0U;
        const auto create_status = create_called ? factory->CreateFontFace(face_type, 1U, files, 0U,
            DWRITE_FONT_SIMULATIONS_NONE, face.GetAddressOf()) : E_POINTER;
        if (!first_family) output << ','; first_family = false;
        output << "{\"name\":" << quoted(item.name) << ",\"font_sha256\":" << quoted(sha256(item.data))
            << ",\"font_bytes\":" << item.data.size() << ",\"variable_requested\":" << (item.variable ? "true" : "false")
            << ",\"file_hresult\":" << hr(file_status) << ",\"analyze_called\":" << (file != nullptr ? "true" : "false")
            << ",\"analyze_hresult\":" << hr(analyze_status) << ",\"supported_raw\":" << supported << ",\"face_count\":" << face_count
            << ",\"file_type\":" << file_type << ",\"face_type\":" << face_type << ",\"create_called\":" << (create_called ? "true" : "false")
            << ",\"create_hresult\":" << hr(create_status) << ",\"requested_weight_bits\":[";
        if (item.variable) {
            for (std::size_t i = 0; i < fixture::vertical_font_weights.size(); ++i)
                output << (i == 0U ? "" : ",") << bits(fixture::vertical_font_weights[i]);
            if (item.precision) output << ',' << bits(650.125F);
        } else output << bits(400.0F);
        output << "],\"instances\":[";
        if (face != nullptr) {
            ComPtr<IDWriteFontFace5> face5; ComPtr<IDWriteFontResource> resource;
            const auto face5_status = face.As(&face5);
            const auto resource_status = face5 != nullptr ? face5->GetFontResource(resource.GetAddressOf()) : E_NOINTERFACE;
            std::vector<float> weights;
            if (item.variable) weights.assign(fixture::vertical_font_weights.begin(), fixture::vertical_font_weights.end());
            else weights.push_back(400.0F);
            if (item.precision) weights.push_back(650.125F);
            bool first_instance = true;
            for (const auto weight : weights) {
                ++instance_count; budget();
                ComPtr<IDWriteFontFace> selected = face;
                HRESULT instance_status = S_OK;
                ComPtr<IDWriteFontFace5> varied;
                if (item.variable) {
                    const DWRITE_FONT_AXIS_VALUE axis{DWRITE_FONT_AXIS_TAG_WEIGHT, weight};
                    instance_status = resource != nullptr ? resource->CreateFontFace(DWRITE_FONT_SIMULATIONS_NONE,
                        &axis, 1U, varied.GetAddressOf()) : E_NOINTERFACE;
                    selected = varied;
                }
                if (!first_instance) output << ','; first_instance = false;
                output << "{\"requested_weight_bits\":" << bits(weight) << ",\"face5_hresult\":" << hr(face5_status)
                    << ",\"resource_hresult\":" << hr(resource_status) << ",\"create_instance_called\":" << (item.variable && resource != nullptr ? "true" : "false")
                    << ",\"instance_hresult\":" << hr(instance_status) << ",\"actual_axis_values\":[";
                ComPtr<IDWriteFontFace5> selected5;
                HRESULT axis_status = E_NOINTERFACE;
                BOOL has_variations = FALSE;
                if (selected != nullptr && SUCCEEDED(selected.As(&selected5))) {
                    has_variations = selected5->HasVariations();
                    const auto count = selected5->GetFontAxisValueCount();
                    if (count > 16U) throw std::runtime_error("actual axis count bound");
                    std::vector<DWRITE_FONT_AXIS_VALUE> axes(count);
                    axis_status = selected5->GetFontAxisValues(axes.data(), count);
                    for (std::size_t axis = 0; axis < axes.size(); ++axis) output << (axis == 0 ? "" : ",")
                        << "{\"tag\":" << hex32(static_cast<std::uint32_t>(axes[axis].axisTag)) << ",\"value_bits\":" << bits(axes[axis].value) << '}';
                }
                output << "],\"axis_hresult\":" << hr(axis_status) << ",\"has_variations_raw\":" << has_variations
                    << ",\"metrics_called\":" << (selected != nullptr ? "true" : "false");
                if (selected != nullptr) { output << ','; observe_face(output, factory.Get(), geometry_factory.Get(), selected.Get()); }
                output << '}';
            }
        }
        output << "]}";
    }
    output << "],\"attempted_instances\":" << instance_count << ",\"outline_calls\":" << outline_count << ",\"analysis_calls\":" << analysis_count << '}';
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        if (argc != 2 || !std::filesystem::is_directory(argv[1])) throw std::runtime_error("expected fresh existing font output directory");
        const auto initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        required(initialized, "COM initialization");
        struct com_scope final { ~com_scope() { CoUninitialize(); } } scope;
        std::ostringstream output;
        observe(output, std::filesystem::path(argv[1]));
        const auto text = output.str();
        if (text.size() > 16U * 1024U * 1024U) throw std::runtime_error("observation JSON bound");
        if (std::fwrite(text.data(), 1U, text.size(), stdout) != text.size()) throw std::runtime_error("observation output");
        return 0;
    } catch (const std::exception& failure) {
        std::fprintf(stderr, "OriginalGlyphMetrics observer failure (not source API policy): %s\n", failure.what());
        return 1;
    }
}
