#ifndef PROGPU_NATIVE_MIL_HPP
#define PROGPU_NATIVE_MIL_HPP

#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <vector>

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
        std::span<const std::byte> pixels) noexcept;

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

private:
    struct implementation;
    std::unique_ptr<implementation> implementation_;
};

constexpr bool is_known(command value) noexcept {
    const auto raw = static_cast<std::uint32_t>(value);
    return raw >= static_cast<std::uint32_t>(command::transport_sync_flush) &&
        raw <= static_cast<std::uint32_t>(command::bitmap_cache);
}

} // namespace progpu::native::mil

#endif
