#include "progpu_native_mil.hpp"

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <vector>

namespace {

using progpu::native::mil::batch_metrics;
using progpu::native::mil::channel;
using progpu::native::mil::command;
using progpu::native::mil::status;

void require(bool condition, const char* expression, int line) {
    if (condition) {
        return;
    }
    std::cerr << "line " << line << ": requirement failed: "
              << expression << '\n';
    std::abort();
}

#define PROGPU_REQUIRE(condition) require((condition), #condition, __LINE__)

template<typename T>
void append_value(std::vector<std::byte>& bytes, const T& value) {
    const auto previous = bytes.size();
    bytes.resize(previous + sizeof(T));
    std::memcpy(bytes.data() + previous, &value, sizeof(T));
}

template<typename... T>
void append_command(
    std::vector<std::byte>& batch,
    command kind,
    const T&... fields) {
    std::vector<std::byte> packet;
    append_value(packet, static_cast<std::uint32_t>(kind));
    (append_value(packet, fields), ...);
    const auto item_size = static_cast<std::uint32_t>(
        (packet.size() + sizeof(std::uint32_t) + 3U) & ~std::size_t{3U});
    append_value(batch, item_size);
    batch.insert(batch.end(), packet.begin(), packet.end());
    batch.resize(batch.size() + item_size - sizeof(std::uint32_t) - packet.size());
}

void append_create(
    std::vector<std::byte>& batch,
    std::uint32_t handle,
    std::uint32_t type) {
    append_command(batch, command::channel_create_resource, handle, type);
}

bool channel_retains_visual_target_graph() {
    constexpr std::uint32_t visual_type = 39U;
    constexpr std::uint32_t render_data_type = 43U;
    constexpr std::uint32_t target_type = 47U;
    std::vector<std::byte> batch;
    append_create(batch, 1U, visual_type);
    append_create(batch, 2U, visual_type);
    append_create(batch, 3U, render_data_type);
    append_create(batch, 4U, target_type);
    append_command(batch, command::visual_create, 1U);
    append_command(batch, command::visual_create, 2U);
    append_command(batch, command::visual_set_offset, 1U, 12.5, -3.0);
    append_command(batch, command::visual_set_alpha, 1U, 0.625);
    append_command(batch, command::visual_set_content, 1U, 3U);
    append_command(batch, command::visual_insert_child_at, 1U, 2U, 0U);

    const std::array<std::byte, 8> render_data{
        std::byte{8}, std::byte{0}, std::byte{0}, std::byte{0},
        std::byte{0x40}, std::byte{0}, std::byte{0}, std::byte{0}};
    std::vector<std::byte> render_packet;
    append_value(render_packet, static_cast<std::uint32_t>(command::render_data));
    append_value(render_packet, 3U);
    append_value(render_packet, static_cast<std::uint32_t>(render_data.size()));
    render_packet.insert(
        render_packet.end(), render_data.begin(), render_data.end());
    append_value(batch, static_cast<std::uint32_t>(render_packet.size() + 4U));
    batch.insert(batch.end(), render_packet.begin(), render_packet.end());

    append_command(
        batch,
        command::generic_target_create,
        4U,
        std::uint64_t{0U},
        std::uint64_t{0U},
        640U,
        480U,
        0U);
    append_command(batch, command::target_set_root, 4U, 1U);
    append_command(
        batch,
        command::target_set_clear_color,
        4U,
        0.1F,
        0.2F,
        0.3F,
        1.0F);
    append_command(batch, command::target_set_flags, 4U, 7U);

    channel state;
    batch_metrics metrics{};
    PROGPU_REQUIRE(state.apply(batch, &metrics) == status::success);
    PROGPU_REQUIRE(metrics.command_count == 15U);
    PROGPU_REQUIRE(metrics.supported_command_count == 15U);
    PROGPU_REQUIRE(metrics.created_resource_count == 4U);
    PROGPU_REQUIRE(state.resource_count() == 4U);
    PROGPU_REQUIRE(state.resource_generation(1U) == 6U);

    progpu::native::mil::visual_snapshot visual{};
    PROGPU_REQUIRE(state.try_get_visual(1U, visual));
    PROGPU_REQUIRE(visual.offset_x == 12.5);
    PROGPU_REQUIRE(visual.offset_y == -3.0);
    PROGPU_REQUIRE(visual.opacity == 0.625);
    PROGPU_REQUIRE(visual.content_handle == 3U);
    PROGPU_REQUIRE(visual.child_count == 1U);
    std::uint32_t child = 0U;
    PROGPU_REQUIRE(state.try_get_visual_child(1U, 0U, child));
    PROGPU_REQUIRE(child == 2U);

    progpu::native::mil::target_snapshot target{};
    PROGPU_REQUIRE(state.try_get_target(4U, target));
    PROGPU_REQUIRE(target.root_handle == 1U);
    PROGPU_REQUIRE(target.clear_red == 0.1F);
    PROGPU_REQUIRE(target.clear_green == 0.2F);
    PROGPU_REQUIRE(target.clear_blue == 0.3F);
    PROGPU_REQUIRE(target.clear_alpha == 1.0F);
    PROGPU_REQUIRE(target.flags == 7U);
    return true;
}

bool failed_batches_roll_back() {
    channel state;
    std::vector<std::byte> seed;
    append_create(seed, 1U, 39U);
    append_command(seed, command::visual_create, 1U);
    PROGPU_REQUIRE(state.apply(seed) == status::success);
    const auto generation = state.resource_generation(1U);

    std::vector<std::byte> invalid;
    append_command(invalid, command::visual_set_alpha, 1U, 0.25);
    append_command(invalid, command::visual_insert_child_at, 1U, 99U, 0U);
    PROGPU_REQUIRE(state.apply(invalid) == status::invalid_handle);
    progpu::native::mil::visual_snapshot snapshot{};
    PROGPU_REQUIRE(state.try_get_visual(1U, snapshot));
    PROGPU_REQUIRE(snapshot.opacity == 1.0);
    PROGPU_REQUIRE(state.resource_generation(1U) == generation);
    return true;
}

bool malformed_and_unsupported_packets_fail_closed() {
    channel state;
    const std::array malformed{
        std::byte{7}, std::byte{0}, std::byte{0}, std::byte{0},
        std::byte{1}, std::byte{0}, std::byte{0}, std::byte{0}};
    PROGPU_REQUIRE(state.apply(malformed) == status::malformed_batch);

    std::vector<std::byte> unknown;
    append_command(unknown, static_cast<command>(0x8eU));
    PROGPU_REQUIRE(state.apply(unknown) == status::unknown_command);

    std::vector<std::byte> unsupported;
    append_command(unsupported, command::draw_rectangle);
    batch_metrics metrics{};
    PROGPU_REQUIRE(
        state.apply(unsupported, &metrics) == status::unsupported_command);
    PROGPU_REQUIRE(metrics.unsupported_command_count == 1U);
    PROGPU_REQUIRE(state.resource_count() == 0U);
    return true;
}

} // namespace

int main() {
    PROGPU_REQUIRE(channel_retains_visual_target_graph());
    PROGPU_REQUIRE(failed_batches_roll_back());
    PROGPU_REQUIRE(malformed_and_unsupported_packets_fail_closed());
    return 0;
}
