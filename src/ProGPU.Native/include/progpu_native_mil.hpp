#ifndef PROGPU_NATIVE_MIL_HPP
#define PROGPU_NATIVE_MIL_HPP

#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <vector>

#include "progpu_native.h"
#include "progpu_native_mil_commands.generated.hpp"

namespace progpu::native::mil {


enum class status : std::uint32_t {
    success,
    end_of_batch,
    invalid_argument,
    malformed_batch,
    unknown_command,
    unsupported_command,
    duplicate_handle,
    invalid_handle,
    invalid_resource_type,
    resource_type_mismatch,
    invalid_graph,
    capacity_exceeded
};

struct command_view {
    command kind{command::invalid};
    std::span<const std::byte> packet{};
    std::uint32_t batch_offset{};
};

struct batch_metrics {
    std::uint32_t command_count{};
    std::uint32_t supported_command_count{};
    std::uint32_t unsupported_command_count{};
    std::uint32_t created_resource_count{};
    std::uint32_t deleted_resource_count{};
    std::uint32_t updated_resource_count{};
    std::uint32_t total_bytes{};
};

struct visual_snapshot {
    std::uint32_t handle{};
    double offset_x{};
    double offset_y{};
    double opacity{1.0};
    std::uint32_t content_handle{};
    std::uint32_t child_count{};
};

struct target_snapshot {
    std::uint32_t handle{};
    std::uint32_t root_handle{};
    float clear_red{};
    float clear_green{};
    float clear_blue{};
    float clear_alpha{};
    std::uint32_t flags{};
};

struct scene_metrics {
    std::uint32_t visual_count{};
    std::uint32_t rectangle_count{};
    std::uint32_t ellipse_count{};
    std::uint32_t rounded_rectangle_count{};
    std::uint32_t line_count{};
    std::uint32_t brush_count{};
    std::uint32_t maximum_visual_depth{};
    std::uint64_t stream_bytes{};
};

enum class scene_build_request_flags : std::uint32_t {
    none = 0U,
    visual_brush = 1U << 0U
};

enum class scene_build_result_flags : std::uint32_t {
    none = 0U,
    needs_more_cycles = 1U << 0U
};

struct scene_build_request {
    scene_build_request_flags flags{scene_build_request_flags::none};
    std::uint32_t target_handle{};
    std::uint64_t scene_id{};
    std::uint64_t generation{};
    double dpi_scale_x{1.0};
    double dpi_scale_y{1.0};
    std::uint64_t monotonic_time_nanoseconds{};
    std::uint64_t request_serial{};
};

struct scene_build_result {
    scene_build_result_flags flags{scene_build_result_flags::none};
    std::uint64_t request_serial{};
    std::uint64_t next_due_time_nanoseconds{};
    std::uint64_t stream_bytes{};
};

class batch_reader final {
public:
    explicit batch_reader(std::span<const std::byte> bytes) noexcept;

    status next(command_view& view) noexcept;
    std::uint32_t offset() const noexcept;

private:
    std::span<const std::byte> bytes_;
    std::uint32_t offset_{};
};

class channel final {
public:
    channel();
    ~channel();
    channel(channel&&) noexcept;
    channel& operator=(channel&&) noexcept;
    channel(const channel&) = delete;
    channel& operator=(const channel&) = delete;

    // Applies a batch transactionally. A malformed, unsupported, or invalid
    // command leaves the live resource graph unchanged.
    status apply(
        std::span<const std::byte> bytes,
        batch_metrics* metrics = nullptr) noexcept;

    // Binds pointer-free RGBA8 pixels to a canonical TYPE_BITMAPSOURCE
    // handle. WPF's native MilCmdBitmapSource carries an in-process WIC
    // pointer, so portable hosts provide the equivalent pixels through this
    // typed channel sideband before scene compilation.
    status set_bitmap_source_rgba8(
        std::uint32_t handle,
        std::uint32_t width,
        std::uint32_t height,
        std::uint32_t row_bytes,
        std::span<const std::byte> pixels,
        double dpi_x = 96.0,
        double dpi_y = 96.0) noexcept;

    // Declares a canonical TYPE_BITMAPSOURCE as a live external image. This
    // is the zero-copy counterpart to set_bitmap_source_rgba8 for typed
    // same-device image providers such as portable Win2D CanvasBitmap.
    status set_bitmap_source_external_image(
        std::uint32_t handle,
        std::uint32_t width,
        std::uint32_t height,
        double dpi_x = 96.0,
        double dpi_y = 96.0) noexcept;

    // Portable front-buffer binding for canonical TYPE_DOUBLEBUFFEREDBITMAP.
    // The canonical update/copy-forward packets keep their process pointer
    // and event fields zero; copied pixels or a same-device texture carry the
    // current front-buffer content through these typed sidebands.
    status set_double_buffered_bitmap_rgba8(
        std::uint32_t handle,
        std::uint32_t width,
        std::uint32_t height,
        std::uint32_t row_bytes,
        std::span<const std::byte> pixels,
        double dpi_x = 96.0,
        double dpi_y = 96.0) noexcept;

    status set_double_buffered_bitmap_external_image(
        std::uint32_t handle,
        std::uint32_t width,
        std::uint32_t height,
        double dpi_x = 96.0,
        double dpi_y = 96.0) noexcept;

    // Declares a canonical TYPE_MEDIAPLAYER as a live external image. The
    // semantic scene carries only dimensions and a stable resource identity;
    // the compositor receives the same-device texture view out of band.
    status set_media_player_external_image(
        std::uint32_t handle,
        std::uint32_t width,
        std::uint32_t height) noexcept;

    // Declares canonical TYPE_D3DIMAGE content as a live external image.
    // Canonical MilCmdD3DImage/MilCmdD3DImagePresent remain pointer-free in
    // portable batches; the lease provider owns backend synchronization and
    // content_version carries the retained present generation out of band.
    status set_d3d_image_external_image(
        std::uint32_t handle,
        std::uint32_t width,
        std::uint32_t height,
        std::uint64_t content_version) noexcept;

    // Binds the exact local content bounds used by WPF DrawingImage when it
    // maps its retained Drawing into an ImageDrawing destination rectangle.
    status set_drawing_image_bounds(
        std::uint32_t handle,
        double x,
        double y,
        double width,
        double height) noexcept;

    // Binds exact source-built DrawingGroup content bounds used for retained
    // spatial opacity-mask mapping and bounded group composition.
    status set_drawing_group_bounds(
        std::uint32_t handle,
        double x,
        double y,
        double width,
        double height) noexcept;

    // Binds source-built WPF Visual descendant bounds used to size an exact
    // target-space BitmapCache page, bounded effect isolation, or bounded
    // Visual opacity/opacity-mask group. Canonical MIL does not serialize
    // this compositor-owned derived metadata.
    status set_visual_cache_bounds(
        std::uint32_t handle,
        double x,
        double y,
        double width,
        double height) noexcept;

    // Binds the pointer-free flattened scene published by a source-built WPF
    // Viewport3DVisual to its canonical retained handle. Projection and depth
    // remain native GPU work; the MIL channel owns copied immutable payloads.
    status set_viewport3d_scene(
        std::uint32_t handle,
        const progpu_native_scene_camera_3d& camera,
        progpu_native_image_rect viewport,
        std::span<const progpu_native_scene_mesh_3d> meshes,
        std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
        std::span<const std::uint32_t> indices) noexcept;

    status set_viewport3d_scene(
        std::uint32_t handle,
        const progpu_native_scene_camera_3d& camera,
        progpu_native_image_rect viewport,
        std::span<const progpu_native_scene_mesh_3d> meshes,
        std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
        std::span<const std::uint32_t> indices,
        std::span<const progpu_native_scene_light_3d> lights) noexcept;

    status set_viewport3d_scene(
        std::uint32_t handle,
        const progpu_native_scene_camera_3d& camera,
        progpu_native_image_rect viewport,
        std::span<const progpu_native_scene_mesh_3d> meshes,
        std::span<const progpu_native_scene_mesh_3d_vertex> vertices,
        std::span<const std::uint32_t> indices,
        std::span<const progpu_native_scene_light_3d> lights,
        std::span<const progpu_native_scene_brush> materials,
        std::span<const progpu_native_scene_gradient_stop>
            gradient_stops) noexcept;

    // Binds copied SFNT/TTC bytes to a canonical TYPE_GLYPHRUN handle. The
    // canonical MilCmdGlyphRunCreate keeps indices, advances, offsets, origin,
    // and bounds on the wire but carries an in-process IDWriteFont pointer;
    // portable hosts replace only that pointer through this typed sideband.
    status set_glyph_run_font_sfnt(
        std::uint32_t handle,
        std::uint32_t face_index,
        std::uint32_t style_simulations,
        std::span<const std::byte> font_data) noexcept;

    std::size_t resource_count() const noexcept;
    bool has_resource(std::uint32_t handle) const noexcept;
    std::uint32_t resource_type(std::uint32_t handle) const noexcept;
    std::uint64_t resource_generation(std::uint32_t handle) const noexcept;

    // Diagnostic/source-planning query; never reads pixels or initializes a GPU.
    // On failure neither output is modified. Supports both bitmap resource types.
    status get_bitmap_source_dpi(
        std::uint32_t handle, double& dpi_x, double& dpi_y) const noexcept;
    bool try_get_visual(
        std::uint32_t handle,
        visual_snapshot& snapshot) const noexcept;
    bool try_get_visual_child(
        std::uint32_t handle,
        std::uint32_t index,
        std::uint32_t& child_handle) const noexcept;
    bool try_get_target(
        std::uint32_t handle,
        target_snapshot& snapshot) const noexcept;

    // Compiles one retained MIL target into the pointer-free semantic scene
    // stream consumed by both the wgpu-native and Dawn renderers.
    status build_scene(
        std::uint32_t target_handle,
        std::uint64_t scene_id,
        std::uint64_t generation,
        std::vector<std::byte>& stream,
        scene_metrics* metrics = nullptr) const noexcept;

    // Compiles a versioned frame request exactly once per full request key.
    // The returned view remains valid until a successful channel mutation or
    // a different request is compiled.
    status build_scene(
        const scene_build_request& request,
        std::span<const std::byte>& stream,
        scene_metrics* metrics = nullptr,
        scene_build_result* result = nullptr) noexcept;

private:
    struct implementation;
    struct build_cache;
    status build_scene_core(
        const implementation& source,
        std::uint32_t target_handle,
        std::uint64_t scene_id,
        std::uint64_t generation,
        const scene_build_request* request,
        std::vector<std::byte>& stream,
        scene_metrics* metrics,
        scene_build_result* result) const noexcept;
    std::unique_ptr<implementation> implementation_;
    std::unique_ptr<build_cache> build_cache_;
};

constexpr bool is_known(command value) noexcept {
    const auto raw = static_cast<std::uint32_t>(value);
    return raw >= static_cast<std::uint32_t>(command::transport_sync_flush) &&
        raw <= static_cast<std::uint32_t>(command::bitmap_cache);
}

} // namespace progpu::native::mil

#endif
