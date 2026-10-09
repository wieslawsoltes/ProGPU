#pragma once

#include "progpu_native_direct2d_font_capture.hpp"
#include "progpu_native.h"

#include <span>

namespace progpu::native::direct2d {

class prepared_original_font;

struct prepared_original_glyph_occurrence final {
    std::uint32_t first_segment = 0U;
    std::uint32_t segment_count = 0U;
};

// Separate immutable raster output. Original positioned contours and no-ink
// occurrences remain in the prepared run; coverage never repairs source data.
struct prepared_original_glyph_coverage final {
    std::vector<progpu_native_scene_vertex_mesh> meshes;
    std::vector<progpu_native_scene_mesh_vertex> vertices;
    progpu_native_image_rect bounds{};
};

class prepared_original_glyph_run final {
public:
    [[nodiscard]] const original_glyph_request& request() const noexcept { return *request_; }
    [[nodiscard]] std::span<const progpu_native_path_segment> segments() const noexcept { return segments_; }
    [[nodiscard]] std::span<const prepared_original_glyph_occurrence> occurrences() const noexcept { return occurrences_; }
    // Bounded, disjoint convex contour coverage in the original physical frame.
    // S_FALSE leaves output untouched and selects the existing general path.
    // Failures likewise publish nothing. This is private same-build transport.
    [[nodiscard]] com::result prepare_coverage(compat::factory* owner,
        prepared_original_glyph_coverage& output) const noexcept;
private:
    friend class prepared_original_font;
    std::shared_ptr<const original_glyph_request> request_;
    std::vector<progpu_native_path_segment> segments_;
    std::vector<prepared_original_glyph_occurrence> occurrences_;
};

// Owns one parsed original face and bounded, lazy design-outline cache. It never
// calls the original face, interprets hints, reshapes, maps characters or rasterizes.
// Distinct source objects are never combined merely because file bytes match.
class prepared_original_font final {
public:
    ~prepared_original_font();
    prepared_original_font(const prepared_original_font&) = delete;
    prepared_original_font& operator=(const prepared_original_font&) = delete;
    [[nodiscard]] static com::result create(std::shared_ptr<const original_font_capture> source,
        std::shared_ptr<prepared_original_font>& output) noexcept;
    [[nodiscard]] const std::shared_ptr<const original_font_capture>& source() const noexcept;
    [[nodiscard]] com::result prepare(std::shared_ptr<const original_glyph_request> request,
        std::shared_ptr<const prepared_original_glyph_run>& output) noexcept;
    [[nodiscard]] std::size_t cached_glyph_count() const noexcept;
private:
    struct state;
    explicit prepared_original_font(std::unique_ptr<state> value) noexcept;
    std::unique_ptr<state> state_;
};

// Private same-build C++ source capability. This is not an installed COM ABI or
// a C transport for STL owners. The original ID2D1RenderTarget vtable is unchanged.
inline constexpr com::guid prepared_glyph_target_id{
    0x871B7946U, 0x02A7U, 0x4FE1U, {0x94U, 0x3AU, 0xD2U, 0x29U, 0x6FU, 0x72U, 0x50U, 0x18U}};

struct prepared_glyph_target : com::unknown {
    // The actual recorder captures its own target/params/scopes under its source
    // lease. Source font preparation is explicit and amortized across draws.
    virtual com::result PROGPU_NATIVE_COM_CALL DrawOwnedGlyphRun(
        const std::shared_ptr<prepared_original_font>& source, compat::point_2f baseline,
        const compat::glyph_run* run, compat::brush* foreground,
        compat::measuring_mode measuring) noexcept = 0;
};

} // namespace progpu::native::direct2d
