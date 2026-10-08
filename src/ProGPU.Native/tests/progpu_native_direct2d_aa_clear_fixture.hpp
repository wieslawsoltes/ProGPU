#pragma once

#include "progpu_native_direct2d_layer_clear_fixture.hpp"

#include <algorithm>
#include <array>
#include <cmath>

namespace progpu::native::direct2d::tests {

enum class aa_clear_order { single, aa_binary, binary_aa, two_aa, owner_aa, aa_owner, empty_binary };
struct aa_clear_case {
    aa_clear_order order;
    unsigned color; // 0 opaque green, 1 null, 2 half-alpha green.
    bool repeat;
    bool ignore_alpha;
    float dpi_x;
    float dpi_y;
    float owner_opacity = 1.0F;
};
inline constexpr std::array aa_clear_cases{
    aa_clear_case{aa_clear_order::single, 0, false, false, 1, 1},
    aa_clear_case{aa_clear_order::single, 1, false, false, 1, 1},
    aa_clear_case{aa_clear_order::single, 2, false, false, 1, 1},
    aa_clear_case{aa_clear_order::aa_binary, 1, false, false, 1, 1},
    aa_clear_case{aa_clear_order::binary_aa, 2, false, false, 1, 1},
    aa_clear_case{aa_clear_order::two_aa, 1, false, false, 1, 1},
    aa_clear_case{aa_clear_order::owner_aa, 1, false, false, 1, 1},
    aa_clear_case{aa_clear_order::owner_aa, 2, false, false, 1, 1},
    aa_clear_case{aa_clear_order::aa_owner, 1, false, false, 1, 1},
    aa_clear_case{aa_clear_order::aa_owner, 2, false, false, 1, 1},
    aa_clear_case{aa_clear_order::empty_binary, 1, false, false, 1, 1},
    aa_clear_case{aa_clear_order::single, 1, true, false, 1, 1},
    aa_clear_case{aa_clear_order::single, 2, true, false, 1, 1},
    aa_clear_case{aa_clear_order::owner_aa, 1, false, true, 1, 1},
    aa_clear_case{aa_clear_order::single, 0, false, false, 2, 2},
    aa_clear_case{aa_clear_order::two_aa, 2, false, false, 2, 2},
    aa_clear_case{aa_clear_order::single, 2, false, false, 1, 2},
    aa_clear_case{aa_clear_order::aa_binary, 0, false, false, 2, 1},
    aa_clear_case{aa_clear_order::owner_aa, 0, false, false, 1, 1, 0.5F},
    aa_clear_case{aa_clear_order::aa_owner, 2, false, false, 1, 1, 0.5F},
    aa_clear_case{aa_clear_order::single, 0, false, false, 1.25F, 1.25F},
    aa_clear_case{aa_clear_order::single, 1, false, false, 1.5F, 1.5F},
    aa_clear_case{aa_clear_order::single, 2, false, false, 1.25F, 1.5F},
    aa_clear_case{aa_clear_order::two_aa, 2, false, false, 1.5F, 1.25F},
    aa_clear_case{aa_clear_order::aa_binary, 0, false, false, 1.25F, 1.5F},
    aa_clear_case{aa_clear_order::binary_aa, 1, false, false, 1.5F, 1.25F},
};

inline bool aa_clear_has_owner(const aa_clear_case& value)
{
    return value.order == aa_clear_order::owner_aa || value.order == aa_clear_order::aa_owner;
}
inline unsigned aa_clear_scope_count(const aa_clear_case& value)
{
    return value.order == aa_clear_order::single ? 1U : 2U;
}
inline unsigned aa_clear_count(const aa_clear_case& value)
{
    return value.order == aa_clear_order::empty_binary ? 0U : value.repeat ? 2U : 1U;
}
inline unsigned aa_clear_promoted_count(const aa_clear_case& value)
{
    return value.order == aa_clear_order::empty_binary || value.order == aa_clear_order::aa_owner ? 0U
        : value.order == aa_clear_order::two_aa ? 2U : 1U;
}
inline unsigned aa_clear_layer_count(const aa_clear_case& value)
{
    return aa_clear_has_owner(value) || value.order == aa_clear_order::two_aa ? 2U : 1U;
}
inline unsigned aa_clear_wire_count(const aa_clear_case& value)
{
    return 3U + aa_clear_count(value) + 2U * aa_clear_scope_count(value);
}
inline unsigned aa_clear_draw_calls(const aa_clear_case& value)
{
    const auto resolved = value.order == aa_clear_order::two_aa ? 2U : 1U;
    // Destination-aware restoration samples an owned root attachment, then
    // copies that completed root to the caller's target once per replay.
    // An empty binary Clear emits no operator, so the adjacent red and blue
    // analytic fills share one GPU draw while retaining both source commands.
    const auto fill_draws = value.order == aa_clear_order::empty_binary ? 2U : 3U;
    return fill_draws + aa_clear_count(value) + aa_clear_layer_count(value) + 2U * resolved + 1U;
}

// These are source rectangles BEFORE capture-time translation (4,6). The two
// AA clips share a fractional edge, exposing accidentally squared coverage.
inline constexpr compat::rectangle_f aa_clear_bounds{6.25F, 5.75F, 20.5F, 19.25F};
inline constexpr compat::rectangle_f aa_clear_second{6.25F, 8.25F, 22.25F, 22.75F};
inline constexpr compat::rectangle_f aa_clear_binary{9.5F, 7.5F, 24.5F, 22.5F};
inline constexpr compat::rectangle_f aa_clear_owner{0, 0, 26, 24};

template<class Push>
com::result record_antialiased_clear(compat::render_target* target,
    const aa_clear_case& value, Push push)
{
    const compat::color_f white{1, 1, 1, 1}, red{1, 0, 0, 1}, blue{0, 0, 1, 1};
    const compat::color_f green{0, 1, 0, 1}, half{0, 1, 0, 0.5F};
    const compat::matrix_3x2_f identity{1, 0, 0, 1, 0, 0}, capture{1, 0, 0, 1, 4, 6};
    const compat::matrix_3x2_f collapsed{0, 0, 0, 0, 123, 456};
    const compat::rectangle_f whole{0, 0, 64, 64}, suffix{16, 16, 18, 18}, outside{0, 0, 3, 3};
    const compat::rectangle_f empty{40, 40, 44, 44};
    com::pointer<compat::solid_color_brush> before, after;
    auto status = target->CreateSolidColorBrush(&red, nullptr, before.put());
    if (com::failed(status)) return status;
    status = target->CreateSolidColorBrush(&blue, nullptr, after.put());
    if (com::failed(status)) return status;
    const compat::layer_parameters owner{aa_clear_owner, nullptr, compat::antialias_mode::aliased,
        identity, value.owner_opacity, nullptr, compat::layer_options::none};
    target->BeginDraw();
    target->SetTransform(&identity);
    target->SetAntialiasMode(compat::antialias_mode::aliased);
    target->Clear(&white);
    const auto aa = [&](const compat::rectangle_f& rectangle) {
        target->SetTransform(&capture);
        target->PushAxisAlignedClip(&rectangle, compat::antialias_mode::per_primitive);
    };
    const auto binary = [&](const compat::rectangle_f& rectangle) {
        target->SetTransform(&capture);
        target->PushAxisAlignedClip(&rectangle, compat::antialias_mode::aliased);
    };
    const auto ordinary = [&] { target->SetTransform(&capture); push(owner, value.ignore_alpha); };
    const auto prefix = [&] { target->SetTransform(&identity); target->FillRectangle(&whole, before.get()); };
    switch (value.order) {
    case aa_clear_order::owner_aa: ordinary(); prefix(); aa(aa_clear_bounds); break;
    case aa_clear_order::aa_owner: aa(aa_clear_bounds); prefix(); ordinary(); break;
    case aa_clear_order::aa_binary: aa(aa_clear_bounds); binary(aa_clear_binary); prefix(); break;
    case aa_clear_order::binary_aa: binary(aa_clear_binary); aa(aa_clear_bounds); prefix(); break;
    case aa_clear_order::two_aa: aa(aa_clear_bounds); aa(aa_clear_second); prefix(); break;
    case aa_clear_order::empty_binary: aa(aa_clear_bounds); prefix(); binary(empty); break;
    case aa_clear_order::single: aa(aa_clear_bounds); prefix(); break;
    }
    target->SetTransform(&collapsed);
    target->SetTags(123U, 456U);
    if (value.repeat) target->Clear(value.color == 1U ? &green : nullptr);
    target->Clear(value.color == 1U ? nullptr : value.color == 2U ? &half : &green);
    compat::matrix_3x2_f retained{};
    target->GetTransform(&retained);
    std::uint64_t tag1{}, tag2{};
    target->GetTags(&tag1, &tag2);
    const bool unchanged = std::memcmp(&retained, &collapsed, sizeof(retained)) == 0 &&
        tag1 == 123U && tag2 == 456U && target->GetAntialiasMode() == compat::antialias_mode::aliased;
    if (value.order == aa_clear_order::empty_binary) target->PopAxisAlignedClip();
    target->SetTransform(&identity);
    target->FillRectangle(&suffix, after.get());
    if (value.order == aa_clear_order::aa_owner) target->PopLayer();
    else if (value.order != aa_clear_order::single && value.order != aa_clear_order::empty_binary)
        target->PopAxisAlignedClip();
    if (value.order == aa_clear_order::owner_aa) target->PopLayer();
    else target->PopAxisAlignedClip();
    target->FillRectangle(&outside, after.get());
    status = target->EndDraw(nullptr, nullptr);
    return com::failed(status) || unchanged ? status : compat::wrong_state;
}

inline com::result record_portable_antialiased_clear(compat::render_target* target,
    const aa_clear_case& value, bool legacy)
{
    com::pointer<compat::layer> layer;
    com::pointer<compat::scene_layer_options_native> options;
    auto status = legacy ? target->CreateLayer(nullptr, layer.put())
        : target->QueryInterface(compat::scene_layer_options_native_interface_id,
            reinterpret_cast<void**>(options.put()));
    if (com::failed(status)) return status;
    if (legacy && value.ignore_alpha) return compat::not_implemented;
    return record_antialiased_clear(target, value, [&](const compat::layer_parameters& parameters, bool opaque) {
        if (legacy) target->PushLayer(&parameters, layer.get());
        else {
            const compat::layer_parameters1 typed{parameters.content_bounds, parameters.geometric_mask,
                parameters.mask_antialias_mode, parameters.mask_transform, parameters.opacity,
                parameters.opacity_brush, opaque ? compat::layer_options1::ignore_alpha : compat::layer_options1::none};
            options->PushLayer1(&typed, nullptr);
        }
    });
}

inline bool antialiased_clear_contract(std::span<const std::byte> bytes, const aa_clear_case& value,
    unsigned source_fills = 3U)
{
    progpu_native_scene_header header{};
    if (!read_scene_value(bytes, 0U, header) ||
        header.command_count != source_fills + aa_clear_count(value) + 2U * aa_clear_scope_count(value)) return false;
    unsigned clears{}, layers{}, promoted{}, opaque{};
    for (unsigned i = 0; i < header.command_count; ++i) {
        progpu_native_scene_command command{};
        if (!read_scene_value(bytes, header.command_offset + std::uint64_t{i} * header.command_stride, command)) return false;
        if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_CLEAR_TARGET) {
            progpu_native_color color{};
            if (command.resource_index != PROGPU_NATIVE_SCENE_NO_INDEX || command.payload_size != sizeof(color) ||
                !read_scene_value(bytes, command.payload_offset, color)) return false;
            const unsigned kind = value.repeat && clears == 0U ? value.color == 1U ? 0U : 1U : value.color;
            if (color.r != 0 || color.b != 0 || color.g != (kind == 1U ? 0 : 1) ||
                color.a != (value.ignore_alpha ? 1 : kind == 1U ? 0 : kind == 2U ? 0.5F : 1)) return false;
            ++clears;
        } else if (command.kind == PROGPU_NATIVE_SCENE_COMMAND_PUSH_LAYER) {
            progpu_native_scene_layer layer{};
            if (!read_scene_value(bytes, command.payload_offset, layer) ||
                layer.opacity != (layer.mask_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX ? value.owner_opacity : 1.0F) ||
                layer.blend_mode != PROGPU_NATIVE_BLEND_SRC_OVER ||
                layer.effect_resource_index != PROGPU_NATIVE_SCENE_NO_INDEX) return false;
            ++layers;
            if ((layer.flags & PROGPU_NATIVE_SCENE_LAYER_INITIALIZE_FROM_BACKGROUND) != 0U) ++promoted;
            if ((layer.flags & PROGPU_NATIVE_SCENE_LAYER_IGNORE_ALPHA) != 0U) ++opaque;
            // In particular ordinary-inside-AA must not initialize the outer AA
            // from background, and ordinary transparent owners stay transparent.
            if (layer.mask_resource_index == PROGPU_NATIVE_SCENE_NO_INDEX &&
                (layer.flags & PROGPU_NATIVE_SCENE_LAYER_INITIALIZE_FROM_BACKGROUND) != 0U) return false;
        }
    }
    return clears == aa_clear_count(value) && layers == aa_clear_layer_count(value) &&
        promoted == aa_clear_promoted_count(value) && opaque == (value.ignore_alpha ? 1U : 0U);
}

inline double aa_clear_pixel_area(const std::array<double, 4>& rectangle, int x, int y)
{
    return std::max(0.0, std::min(double(x + 1), rectangle[2]) - std::max(double(x), rectangle[0])) *
        std::max(0.0, std::min(double(y + 1), rectangle[3]) - std::max(double(y), rectangle[1]));
}

inline bool antialiased_clear_area_contract()
{
    struct area_case { std::array<double, 4> rectangle; int x; int y; double expected; };
    // Literal rational expectations, including the actual (4,6)-translated
    // source's physical upper-left corners. No inverse-DPI/local round trip.
    const std::array controls{
        area_case{{0, 0, 1, 1}, 0, 0, 1},
        area_case{{0.25, 0.75, 2, 2}, 0, 0, 3.0 / 16},
        area_case{{-1, -1, 0.25, 0.75}, 0, 0, 3.0 / 16},
        area_case{{12.8125, 14.6875, 30.625, 31.5625}, 12, 14, 15.0 / 256}, // 125% XY
        area_case{{15.375, 17.625, 36.75, 37.875}, 15, 17, 15.0 / 64}, // 150% XY
        area_case{{12.8125, 17.625, 30.625, 37.875}, 12, 17, 9.0 / 128}, // 125/150%
        area_case{{15.375, 14.6875, 36.75, 31.5625}, 15, 14, 25.0 / 128}, // 150/125%
        area_case{{20.5, 23.5, 49, 50.5}, 20, 23, 1.0 / 4}, // 200% XY
        area_case{{12.8125, 14.6875, 30.625, 31.5625}, 13, 15, 1},
        area_case{{12.8125, 14.6875, 30.625, 31.5625}, 11, 15, 0},
        area_case{{1, 0, 2, 1}, 0, 0, 0},
        area_case{{0.25, 0, 0.25, 1}, 0, 0, 0},
        area_case{{-0.25, -0.75, 1, 1}, -1, -1, 3.0 / 16},
        area_case{{0.8125, 0.6875, 18.625, 17.5625}, 0, 0, 15.0 / 256}, // integer target origin removed
        area_case{{0.125, 0.25, 0.375, 0.75}, 0, 0, 1.0 / 8},
        area_case{{0, 0, 0.5, 0.5}, 0, 0, 1.0 / 4},
    };
    for (const auto& value : controls)
        if (aa_clear_pixel_area(value.rectangle, value.x, value.y) != value.expected) return false;
    return true;
}

// Independent scalar coverage: exact area of a source rectangle intersecting a
// unit physical pixel. No SDF, derivatives, product mask bytes or shader helper.
// Every stored layer/composite is independently quantized to RGBA8 UNORM.
inline bool antialiased_clear_pixels(std::span<const std::uint8_t> pixels,
    const aa_clear_case& value, bool bgra = false)
{
    using pixel = std::array<unsigned, 4>;
    if (pixels.size() != 64U * 256U) return false;
    const auto quantize = [](double v) { return static_cast<unsigned>(std::floor(std::clamp(v, 0.0, 255.0) + 0.5)); };
    const auto captured = [&](compat::rectangle_f r) {
        return std::array<double, 4>{(r.left + 4.0) * value.dpi_x, (r.top + 6.0) * value.dpi_y,
            (r.right + 4.0) * value.dpi_x, (r.bottom + 6.0) * value.dpi_y};
    };
    auto first = captured(aa_clear_bounds), second = captured(aa_clear_second);
    const auto binary = captured(aa_clear_binary), owner = captured(aa_clear_owner);
    // An aliased clip selects complete physical pixels by their centers. Its
    // fractional source edge must not become another antialiased area edge.
    second = {std::max(first[0], second[0]), std::max(first[1], second[1]),
        std::min(first[2], second[2]), std::min(first[3], second[3])};
    for (unsigned y = 0; y < 64U; ++y) for (unsigned x = 0; x < 64U; ++x) {
        const auto inside = [&](const auto& r) { return x + 0.5 >= r[0] && x + 0.5 < r[2] &&
            y + 0.5 >= r[1] && y + 0.5 < r[3]; };
        const auto coverage = [&](const auto& r) { return aa_clear_pixel_area(r, static_cast<int>(x), static_cast<int>(y)); };
        const auto compose = [&](pixel dst, pixel src, double c, bool initialized) {
            pixel out{};
            for (unsigned ch = 0; ch < 4U; ++ch) out[ch] = quantize(src[ch] * c + dst[ch] *
                (initialized ? 1.0 - c : 1.0 - src[3] / 255.0 * c));
            return out;
        };
        const auto group = [&](pixel dst, pixel src, bool visible) {
            // The original Direct2D layer applies group opacity to its stored
            // premultiplied bytes before SRC_OVER, including the source alpha.
            for (auto& channel : src) channel = quantize(channel * value.owner_opacity);
            return compose(dst, src, visible ? 1.0 : 0.0, false);
        };
        const bool binary_inside = value.order != aa_clear_order::aa_binary && value.order != aa_clear_order::binary_aa
            ? true : inside(binary);
        const bool suffix = x + 0.5 >= 16 * value.dpi_x && x + 0.5 < 18 * value.dpi_x &&
            y + 0.5 >= 16 * value.dpi_y && y + 0.5 < 18 * value.dpi_y && binary_inside;
        pixel root{255, 255, 255, 255};
        const pixel red{255, 0, 0, 255}, blue{0, 0, 255, 255}, empty{};
        pixel cleared = value.color == 1U ? empty : value.color == 2U ? pixel{0, 128, 0, 128} : pixel{0, 255, 0, 255};
        if (value.ignore_alpha) cleared = value.color == 1U ? pixel{0, 0, 0, 255} : pixel{0, 255, 0, 255};
        if (value.order == aa_clear_order::owner_aa) {
            auto inner = suffix ? blue : cleared;
            const auto content = compose(red, inner, coverage(first), true);
            root = group(root, content, inside(owner));
        } else if (value.order == aa_clear_order::aa_owner) {
            const auto content = group(red, suffix ? blue : cleared, inside(owner));
            root = compose(root, content, coverage(first), false);
        } else if (value.order == aa_clear_order::two_aa) {
            // Preserve both stored intermediates. The inner clip contributes
            // only its conditional area within the outer clip, so a common
            // edge is not applied twice; each attachment still rounds once.
            const auto outer = coverage(first);
            const auto inner = compose(root, suffix ? blue : cleared,
                outer > 0.0 ? coverage(second) / outer : 0.0, true);
            root = compose(root, inner, outer, true);
        } else if (value.order == aa_clear_order::empty_binary) {
            root = compose(root, suffix ? blue : red, coverage(first), false);
        } else {
            const auto content = binary_inside ? suffix ? blue : cleared : root;
            root = compose(root, content, coverage(first), true);
        }
        if (x + 0.5 < 3 * value.dpi_x && y + 0.5 < 3 * value.dpi_y) root = blue;
        for (unsigned ch = 0; ch < 4U; ++ch) {
            const unsigned actual = pixels[y * 256U + x * 4U + (bgra && ch < 3U ? 2U - ch : ch)];
            if (actual != root[ch]) {
                std::fprintf(stderr, "AA Clear order=%u color=%u dpi=(%g,%g) pixel=(%u,%u) channel=%u actual=%u expected=%u\n",
                    static_cast<unsigned>(value.order), value.color, double(value.dpi_x), double(value.dpi_y),
                    x, y, ch, actual, root[ch]);
                return false;
            }
        }
    }
    return true;
}

inline bool antialiased_clear_source_contract(compat::factory* factory)
{
    if (!antialiased_clear_area_contract()) return false;
    com::pointer<compat::scene_factory_native> create;
    if (factory->QueryInterface(compat::scene_factory_native_interface_id,
        reinterpret_cast<void**>(create.put())) != com::ok) return false;
    for (const bool legacy : {true, false}) for (unsigned i = 0; i < aa_clear_cases.size(); ++i) {
        const auto& value = aa_clear_cases[i];
        if (legacy && value.ignore_alpha) continue;
        const compat::scene_render_target_properties properties{64, 64, 96 * value.dpi_x, 96 * value.dpi_y,
            0xACC0U + i + (legacy ? 0U : 32U), 1U};
        com::pointer<compat::render_target> target;
        com::pointer<compat::scene_render_target_native> scene;
        if (create->CreateSceneRenderTarget(&properties, target.put()) != com::ok ||
            target.as(compat::scene_render_target_native_interface_id, scene) != com::ok ||
            record_portable_antialiased_clear(target.get(), value, legacy) != com::ok) return false;
        std::vector<std::byte> bytes(static_cast<std::size_t>(scene->GetRequiredSceneSize()));
        std::uint64_t written{};
        if (scene->BuildScene(bytes.data(), bytes.size(), &written) != com::ok || written != bytes.size() ||
            !antialiased_clear_contract(bytes, value)) return false;
        const auto retained = bytes;
        target->BeginDraw();
        target->Clear(nullptr);
        if (target->EndDraw(nullptr, nullptr) != com::ok || bytes != retained ||
            !antialiased_clear_contract(bytes, value)) return false;
    }
    return true;
}

template<class Render, class Require>
void verify_antialiased_clear(Render render, Require require)
{
    require(antialiased_clear_area_contract(), "AA Clear independent pixel-area arithmetic changed");
    com::pointer<compat::factory> factory;
    com::pointer<compat::scene_factory_native> create;
    require(compat::create_factory(factory.put()) == com::ok &&
        factory.as(compat::scene_factory_native_interface_id, create) == com::ok, "AA Clear factory failed");
    for (const bool legacy : {true, false}) for (unsigned i = 0; i < aa_clear_cases.size(); ++i) {
        const auto& value = aa_clear_cases[i];
        if (legacy && value.ignore_alpha) continue; // IGNORE_ALPHA has no legacy API equivalent.
        const compat::scene_render_target_properties properties{64, 64, 96 * value.dpi_x, 96 * value.dpi_y,
            0xAC00U + i + (legacy ? 0U : 32U), 1U};
        com::pointer<compat::render_target> target;
        com::pointer<compat::scene_render_target_native> scene;
        require(create->CreateSceneRenderTarget(&properties, target.put()) == com::ok &&
            target.as(compat::scene_render_target_native_interface_id, scene) == com::ok &&
            record_portable_antialiased_clear(target.get(), value, legacy) == com::ok, "AA Clear source recording failed");
        std::vector<std::byte> bytes(static_cast<std::size_t>(scene->GetRequiredSceneSize()));
        std::uint64_t written{};
        require(scene->BuildScene(bytes.data(), bytes.size(), &written) == com::ok && written == bytes.size() &&
            antialiased_clear_contract(bytes, value), "AA Clear source layer promotion/order changed");
        const auto cold = render(scene.get(), value);
        const auto warm = render(scene.get(), value);
        require(cold == warm && antialiased_clear_pixels(cold, value), "AA Clear full RGBA bytes changed");
    }
}

} // namespace progpu::native::direct2d::tests
