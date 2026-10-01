#include "progpu_native_hinted_font.hpp"
#include "progpu_native_hint_fault_fixture.hpp"
#include "progpu_native_hinted_transport_controls.hpp"

#include <ft2build.h>
#include FT_FREETYPE_H
#include FT_MODULE_H

#include <array>
#include <cstring>
#include <fstream>
#include <iostream>
#include <limits>
#include <source_location>
#include <stdexcept>
#include <string>
#include <thread>

namespace {
using namespace progpu::native::text;

void require(bool value, std::source_location location = std::source_location::current())
{
    if (!value) throw std::runtime_error("Hinted-font control failed at line " + std::to_string(location.line()));
}

struct reference_owner final {
    FT_Library library = nullptr;
    FT_Face face = nullptr;
    ~reference_owner()
    {
        if (face != nullptr) FT_Done_Face(face);
        if (library != nullptr) FT_Done_FreeType(library);
    }
};

struct thread_join final {
    std::thread& value;
    ~thread_join() { if (value.joinable()) value.join(); }
};

std::vector<std::byte> read_font(const char* path)
{
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    require(file.is_open());
    const auto length = static_cast<std::streamoff>(file.tellg());
    require(length > 0 && length <= std::numeric_limits<FT_Long>::max());
    std::vector<std::byte> bytes(static_cast<std::size_t>(length));
    file.seekg(0);
    file.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    require(file.good());
    return bytes;
}

void configure_reference(reference_owner& owner, const hinted_font_configuration& configuration)
{
    unsigned int policy = static_cast<unsigned int>(configuration.policy);
    require(FT_Property_Set(owner.library, "truetype", "interpreter-version", &policy) == 0);
    FT_Size_RequestRec size{};
    size.type = FT_SIZE_REQUEST_TYPE_NOMINAL;
    size.width = static_cast<FT_Long>(configuration.x_pixels_per_em_26_6);
    size.height = static_cast<FT_Long>(configuration.y_pixels_per_em_26_6);
    require(FT_Request_Size(owner.face, &size) == 0);
    FT_Vector phase{static_cast<FT_Pos>(configuration.x_phase_26_6),
        static_cast<FT_Pos>(configuration.y_phase_26_6)};
    FT_Set_Transform(owner.face, nullptr, &phase);
}

void compare_frame(const hinted_font_identity& identity, FT_Face face)
{
    require(face != nullptr && face->size != nullptr);
    const auto& actual = identity.device_frame;
    const auto& expected = face->size->metrics;
    require(actual.units_per_em == face->units_per_EM && actual.x_pixels_per_em == expected.x_ppem &&
        actual.y_pixels_per_em == expected.y_ppem);
    require(actual.x_scale_16_16 == expected.x_scale && actual.y_scale_16_16 == expected.y_scale &&
        actual.driver_ascender_26_6 == expected.ascender && actual.driver_descender_26_6 == expected.descender &&
        actual.driver_height_26_6 == expected.height && actual.driver_maximum_advance_26_6 == expected.max_advance);
    require(actual.design_ascender == face->ascender && actual.design_descender == face->descender &&
        actual.design_height == face->height && actual.design_maximum_advance == face->max_advance_width);
}

void compare_slot(const hinted_glyph& glyph, FT_GlyphSlot slot)
{
    require(glyph.glyph_index == slot->glyph_index);
    require(glyph.advance_x_26_6 == slot->advance.x && glyph.advance_y_26_6 == slot->advance.y);
    require(glyph.width_26_6 == slot->metrics.width && glyph.height_26_6 == slot->metrics.height);
    require(glyph.horizontal_bearing_x_26_6 == slot->metrics.horiBearingX &&
        glyph.horizontal_bearing_y_26_6 == slot->metrics.horiBearingY);
    require(glyph.horizontal_advance_26_6 == slot->metrics.horiAdvance &&
        glyph.vertical_advance_26_6 == slot->metrics.vertAdvance &&
        glyph.vertical_bearing_x_26_6 == slot->metrics.vertBearingX &&
        glyph.vertical_bearing_y_26_6 == slot->metrics.vertBearingY);
    require(glyph.linear_horizontal_advance_16_16 == slot->linearHoriAdvance &&
        glyph.linear_vertical_advance_16_16 == slot->linearVertAdvance &&
        glyph.left_side_bearing_delta_26_6 == slot->lsb_delta &&
        glyph.right_side_bearing_delta_26_6 == slot->rsb_delta);
    require(glyph.outline_flags == slot->outline.flags);
    require(glyph.points.size() == static_cast<std::size_t>(slot->outline.n_points) &&
        glyph.tags.size() == glyph.points.size() &&
        glyph.contour_ends.size() == static_cast<std::size_t>(slot->outline.n_contours));
    for (std::size_t index = 0U; index < glyph.points.size(); ++index) {
        require(glyph.points[index].x_26_6 == slot->outline.points[index].x &&
            glyph.points[index].y_26_6 == slot->outline.points[index].y);
        require(glyph.tags[index] == static_cast<std::uint8_t>(slot->outline.tags[index]));
    }
    for (std::size_t index = 0U; index < glyph.contour_ends.size(); ++index)
        require(glyph.contour_ends[index] == slot->outline.contours[index]);
}

void verify_design_projection(const hinted_glyph_batch& batch)
{
    constexpr std::array policies{hinted_projection_policy::automatic,
        hinted_projection_policy::intrinsic_simd, hinted_projection_policy::scalar_reference};
    const auto& frame = batch.identity->device_frame;
    std::array<hinted_design_vector, 17> input{};
    for (std::size_t index = 0U; index < input.size(); ++index)
        input[index] = {static_cast<std::int32_t>(index) * 1234 - 4321,
            7654 - static_cast<std::int32_t>(index) * 2345};
    const hinted_outline_point sentinel{-77, -91};
    std::array<hinted_outline_point, 18> output{};
    for (const auto policy : policies) {
        for (std::size_t count = 0U; count <= input.size(); ++count) {
            output.fill(sentinel);
            const auto result = project_hinted_design_vectors(batch, {input.data(), count}, output, policy);
            require(result.error == hinted_projection_error::none && result.path ==
                (policy == hinted_projection_policy::scalar_reference ? hinted_projection_path::scalar_reference :
                    hinted_projection_path::intrinsic_simd));
            for (std::size_t index = 0U; index < output.size(); ++index)
                require(output[index] == (index < count ? hinted_outline_point{
                    FT_MulFix(input[index].x, frame.x_scale_16_16),
                    FT_MulFix(input[index].y, frame.y_scale_16_16)} : sentinel));
        }
        output.fill(sentinel);
        const auto untouched = output;
        require(project_hinted_design_vectors(batch, input, {output.data(), 16U}, policy).error ==
            hinted_projection_error::insufficient_capacity && output == untouched);
        auto overlapping = input;
        require(project_hinted_design_vectors(batch, overlapping,
            {reinterpret_cast<hinted_outline_point*>(overlapping.data()), overlapping.size()}, policy).error ==
            hinted_projection_error::invalid_argument);
        require(std::memcmp(overlapping.data(), input.data(), sizeof(input)) == 0);
        require(project_hinted_design_vectors(batch, {input.data(), 1U},
            {reinterpret_cast<hinted_outline_point*>(const_cast<std::byte*>(batch.identity->source->bytes.data())), 1U},
            policy).error == hinted_projection_error::invalid_argument);
        require(project_hinted_design_vectors(batch, {input.data(), 1U},
            {reinterpret_cast<hinted_outline_point*>(const_cast<hinted_font_device_frame*>(&frame)), 1U},
            policy).error == hinted_projection_error::invalid_argument);
    }
    output.fill(sentinel);
    const auto untouched = output;
    for (const auto policy : {hinted_projection_policy::native_compute, hinted_projection_policy::gpu_shader,
        static_cast<hinted_projection_policy>(0xFFFFFFFFU)}) {
        const auto result = project_hinted_design_vectors(batch, input, output, policy);
        require(result.error == hinted_projection_error::unsupported_policy && result.path == hinted_projection_path::none &&
            output == untouched);
    }

    // Independent unpacked arithmetic frames, not admitted font fixtures.
    auto identity = std::make_shared<hinted_font_identity>(*batch.identity);
    hinted_glyph_batch arithmetic;
    arithmetic.identity = identity;
    for (const auto policy : policies) {
        for (const std::int32_t scale : {65536, 32768}) {
            identity->device_frame.x_scale_16_16 = scale;
            identity->device_frame.y_scale_16_16 = scale;
            const std::array<hinted_design_vector, 5> edges{{
                {std::numeric_limits<std::int32_t>::min(), std::numeric_limits<std::int32_t>::max()},
                {1, -1}, {-3, 3}, {-1, 1}, {3, -3}}};
            for (std::size_t count = 0U; count <= edges.size(); ++count) {
                output.fill(sentinel);
                require(project_hinted_design_vectors(arithmetic, {edges.data(), count}, output, policy).error ==
                    hinted_projection_error::none);
                for (std::size_t index = 0U; index < output.size(); ++index)
                    require(output[index] == (index < count ? hinted_outline_point{
                        FT_MulFix(edges[index].x, scale), FT_MulFix(edges[index].y, scale)} : sentinel));
            }
        }
        identity->device_frame.x_scale_16_16 = std::numeric_limits<std::int32_t>::max();
        identity->device_frame.y_scale_16_16 = std::numeric_limits<std::int32_t>::max();
        for (std::size_t bad_index = 0U; bad_index < input.size(); ++bad_index) {
            for (const bool bad_y : {false, true}) {
                for (const std::int32_t invalid : {-65537, 65537}) {
                    std::array<hinted_design_vector, 17> bad{};
                    bad[bad_index] = bad_y ? hinted_design_vector{0, invalid} : hinted_design_vector{invalid, 0};
                    output.fill(sentinel);
                    require(project_hinted_design_vectors(arithmetic, bad, output, policy).error ==
                        hinted_projection_error::unsupported_frame && output == untouched);
                }
            }
        }
        const std::array<hinted_design_vector, 2> boundary{{{65536, -65536}, {-65536, 65536}}};
        output.fill(sentinel);
        require(project_hinted_design_vectors(arithmetic, boundary, output, policy).error == hinted_projection_error::none);
        for (std::size_t index = 0U; index < output.size(); ++index)
            require(output[index] == (index < boundary.size() ? hinted_outline_point{
                FT_MulFix(boundary[index].x, std::numeric_limits<std::int32_t>::max()),
                FT_MulFix(boundary[index].y, std::numeric_limits<std::int32_t>::max())} : sentinel));
        identity->device_frame.y_scale_16_16 = 0;
        output.fill(sentinel);
        require(project_hinted_design_vectors(arithmetic, input, output, policy).error ==
            hinted_projection_error::unsupported_frame && output == untouched);
    }
}

void verify_anchor_points(const hinted_glyph_batch& batch, FT_Face reference)
{
    FT_Vector phase{static_cast<FT_Pos>(batch.identity->x_phase_26_6),
        static_cast<FT_Pos>(batch.identity->y_phase_26_6)};
    FT_Set_Transform(reference, nullptr, &phase);
    for (std::size_t glyph = 0U; glyph < batch.glyphs.size(); ++glyph) {
        require(FT_Load_Glyph(reference, batch.glyphs[glyph].glyph_index,
            FT_LOAD_NO_BITMAP | FT_LOAD_NO_AUTOHINT | FT_LOAD_PEDANTIC | FT_LOAD_TARGET_NORMAL) == 0);
        for (std::size_t index = 0U; index < batch.glyphs[glyph].points.size(); ++index) {
            const auto result = get_hinted_anchor_point(batch, glyph, index);
            require(result.error == hinted_projection_error::none &&
                result.point.x_26_6 == reference->glyph->outline.points[index].x - phase.x &&
                result.point.y_26_6 == reference->glyph->outline.points[index].y - phase.y);
        }
        require(get_hinted_anchor_point(batch, glyph, batch.glyphs[glyph].points.size()).error ==
            hinted_projection_error::invalid_argument);
    }
    require(get_hinted_anchor_point(batch, batch.glyphs.size(), 0U).error == hinted_projection_error::invalid_argument);
    require(get_hinted_anchor_point(batch, std::numeric_limits<std::size_t>::max(), 0U).error ==
        hinted_projection_error::invalid_argument);

    // Different repeated descriptors intentionally retain different point data:
    // a glyph-ID map must never substitute the first occurrence's captured slot.
    auto identity = std::make_shared<hinted_font_identity>(*batch.identity);
    identity->x_phase_26_6 = 19U;
    identity->y_phase_26_6 = 37U;
    hinted_glyph_batch raw;
    raw.identity = identity;
    raw.glyphs.resize(2U);
    raw.glyphs[0].glyph_index = raw.glyphs[1].glyph_index = 7U;
    raw.glyphs[0].points.push_back({31, 53});
    raw.glyphs[1].points.push_back({41, 73});
    require(get_hinted_anchor_point(raw, 0U, 0U).point == hinted_outline_point{12, 16});
    require(get_hinted_anchor_point(raw, 1U, 0U).point == hinted_outline_point{22, 36});
    raw.glyphs[1].points[0] = {std::numeric_limits<long>::min(), 73};
    require(get_hinted_anchor_point(raw, 1U, 0U).error == hinted_projection_error::unsupported_frame);
    require(get_hinted_anchor_point(raw, 0U, 0U).point == hinted_outline_point{12, 16});
    identity->x_phase_26_6 = 64U;
    require(get_hinted_anchor_point(raw, 0U, 0U).error == hinted_projection_error::invalid_argument);
    identity->x_phase_26_6 = 0U;
    identity->y_phase_26_6 = 0U;
    raw.glyphs[1].points[0] = {std::numeric_limits<std::int32_t>::min(), std::numeric_limits<std::int32_t>::max()};
    const auto extremes = get_hinted_anchor_point(raw, 1U, 0U);
    require(extremes.error == hinted_projection_error::none && extremes.point == raw.glyphs[1].points[0]);
    if constexpr (sizeof(long) == 8U) {
        raw.glyphs[1].points[0].y_26_6 = static_cast<long>(static_cast<std::int64_t>(std::numeric_limits<std::int32_t>::max()) + 1);
        require(get_hinted_anchor_point(raw, 1U, 0U).error == hinted_projection_error::unsupported_frame);
    }
    require(raw.glyphs[0].points[0] == hinted_outline_point{31, 53});
    hinted_glyph_batch absent;
    require(get_hinted_anchor_point(absent, 0U, 0U).error == hinted_projection_error::invalid_argument);
}

void verify(const std::vector<std::byte>& original)
{
    reference_owner reference;
    require(FT_Init_FreeType(&reference.library) == 0);
    require(FT_New_Memory_Face(reference.library, reinterpret_cast<const FT_Byte*>(original.data()),
        static_cast<FT_Long>(original.size()), 0, &reference.face) == 0);
    require(!FT_HAS_MULTIPLE_MASTERS(reference.face));
    // Original IDs, duplicates and a real non-ink space; no source shaping is substituted.
    const auto a = FT_Get_Char_Index(reference.face, static_cast<FT_ULong>('A'));
    const auto m = FT_Get_Char_Index(reference.face, static_cast<FT_ULong>('m'));
    const auto space = FT_Get_Char_Index(reference.face, static_cast<FT_ULong>(' '));
    require(a != 0U && m != 0U && space != 0U);
    const std::array<std::uint32_t, 8> ids{a, m, space, 0U, m, a, m, space};
    bool actual_outline_hint_difference = false;
    bool actual_advance_hint_difference = false;
    for (const auto policy : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
        for (const std::uint32_t ppem : {12U, 13U, 14U, 17U}) {
            hinted_font_configuration configuration{ppem * 64U, ppem * 64U, policy, 0U, 0U, {}};
            auto input = original;
            std::unique_ptr<hinted_font> font;
            hinted_font_error error = hinted_font_error::hinting_failed;
            require(hinted_font::try_create(input, 0U, configuration, font, error));
            require(error == hinted_font_error::none);
            input.assign(input.size(), std::byte{0}); // caller mutation must not change the memory face
            std::shared_ptr<const hinted_glyph_batch> batch;
            require(font->try_capture(ids, batch, error));
            require(batch->identity->source->bytes == original && batch->glyphs.size() == ids.size());
            require(batch->identity->policy == policy && batch->identity->x_pixels_per_em_26_6 == ppem * 64U);
            require(batch->glyphs[0] == batch->glyphs[5] && batch->glyphs[1] == batch->glyphs[4]);
            require(batch->glyphs[2].points.empty() && batch->glyphs[2].advance_x_26_6 > 0);
            tests::verify_hinted_transport(*batch);
            configure_reference(reference, configuration);
            compare_frame(*batch->identity, reference.face);
            verify_design_projection(*batch);
            verify_anchor_points(*batch, reference.face);
            for (std::size_t index = 0U; index < ids.size(); ++index) {
                require(FT_Load_Glyph(reference.face, ids[index],
                    FT_LOAD_NO_BITMAP | FT_LOAD_NO_AUTOHINT | FT_LOAD_PEDANTIC | FT_LOAD_TARGET_NORMAL) == 0);
                compare_slot(batch->glyphs[index], reference.face->glyph);
                require(FT_Load_Glyph(reference.face, ids[index], FT_LOAD_NO_BITMAP | FT_LOAD_NO_HINTING) == 0);
                const auto slot = reference.face->glyph;
                actual_advance_hint_difference |= batch->glyphs[index].advance_x_26_6 != slot->advance.x;
                require(slot->outline.n_points >= 0 && batch->glyphs[index].points.size() ==
                    static_cast<std::size_t>(slot->outline.n_points));
                for (std::size_t point = 0U; point < batch->glyphs[index].points.size(); ++point)
                    actual_outline_hint_difference |= batch->glyphs[index].points[point].x_26_6 != slot->outline.points[point].x ||
                        batch->glyphs[index].points[point].y_26_6 != slot->outline.points[point].y;
            }
            const auto saved = batch;
            for (std::size_t length = 1U; length <= ids.size(); ++length) {
                auto bad_ids = ids;
                bad_ids[length - 1U] = 0xFFFFFFFFU;
                require(!font->try_capture(std::span(bad_ids).first(length), batch, error));
                require(error == hinted_font_error::invalid_argument && batch == saved);
            }
            // Every SIMD lane and the bounded tail independently reject both unsigned extremes.
            for (std::size_t index = 0U; index < ids.size(); ++index) {
                for (const auto invalid : {static_cast<std::uint32_t>(reference.face->num_glyphs), 0xFFFFFFFFU}) {
                    auto bad_ids = ids;
                    bad_ids[index] = invalid;
                    require(!font->try_capture(bad_ids, batch, error));
                    require(error == hinted_font_error::invalid_argument && batch == saved);
                }
            }
            std::shared_ptr<const hinted_glyph_batch> repeated;
            require(font->try_capture(ids, repeated, error));
            require(repeated->identity == saved->identity && repeated->glyphs == saved->glyphs);
            std::array<std::shared_ptr<const hinted_glyph_batch>, 2> concurrent{};
            std::array<bool, 2> success{true, true};
            const auto capture = [&](std::size_t worker) {
                hinted_font_error worker_error = hinted_font_error::hinting_failed;
                for (unsigned int attempt = 0U; attempt < 4U; ++attempt) {
                    if (!font->try_capture(ids, concurrent[worker], worker_error) ||
                        worker_error != hinted_font_error::none) {
                        success[worker] = false;
                        return;
                    }
                }
            };
            // Apple's supported libc++ lacks jthread; retain real concurrency
            // and exception-safe joining without changing the toolchain/gate.
            std::thread first(capture, 0U);
            const thread_join first_join{first};
            std::thread second(capture, 1U);
            const thread_join second_join{second};
            first.join();
            second.join();
            for (std::size_t worker = 0U; worker < concurrent.size(); ++worker)
                require(success[worker] && concurrent[worker]->identity == saved->identity &&
                    concurrent[worker]->glyphs == saved->glyphs);
            std::shared_ptr<const hinted_glyph_batch> empty;
            require(font->try_capture({}, empty, error) && empty->glyphs.empty());
            require(empty->identity == saved->identity);
            const auto* saved_font = font.get();
            for (const std::uint32_t mode : {0U, 38U, 0xFFFFFFFFU}) {
                auto bad = configuration;
                bad.policy = static_cast<font_hint_policy>(mode);
                require(!hinted_font::try_create(original, 0U, bad, font, error) && font.get() == saved_font);
            }
            for (const std::uint32_t value : {0U, 0xFFFFFFFFU}) {
                auto bad = configuration;
                bad.x_pixels_per_em_26_6 = value;
                require(!hinted_font::try_create(original, 0U, bad, font, error) && font.get() == saved_font);
            }
            auto bad = configuration;
            bad.x_phase_26_6 = 64U;
            require(!hinted_font::try_create(original, 0U, bad, font, error) && font.get() == saved_font);
            require(!hinted_font::try_create(original, 0x10000U, configuration, font, error) && font.get() == saved_font);
            const std::array<std::byte, 4> corrupt{};
            require(!hinted_font::try_create(corrupt, 0U, configuration, font, error) && font.get() == saved_font);
            const std::array<std::int32_t, 1> unexpected_axis{0};
            bad = configuration;
            bad.variation_coordinates_16_16 = unexpected_axis;
            require(!hinted_font::try_create(original, 0U, bad, font, error) && font.get() == saved_font);
            // A second owner changes phase without changing the first owner's slot or data.
            auto phase_configuration = configuration;
            phase_configuration.x_phase_26_6 = 19U;
            phase_configuration.y_phase_26_6 = 37U;
            std::unique_ptr<hinted_font> phase_font;
            require(hinted_font::try_create(saved->identity->source, phase_configuration, phase_font, error));
            std::shared_ptr<const hinted_glyph_batch> phased;
            require(phase_font->try_capture(ids, phased, error));
            require(phased->identity != saved->identity && phased->identity->x_phase_26_6 == 19U &&
                phased->identity->y_phase_26_6 == 37U);
            require(phased->identity->source == saved->identity->source);
            require(phased->identity->device_frame == saved->identity->device_frame);
            verify_anchor_points(*phased, reference.face);
            for (std::size_t glyph = 0U; glyph < ids.size(); ++glyph) {
                require(phased->glyphs[glyph].advance_x_26_6 == saved->glyphs[glyph].advance_x_26_6);
                require(phased->glyphs[glyph].points.size() == saved->glyphs[glyph].points.size());
                for (std::size_t point = 0U; point < phased->glyphs[glyph].points.size(); ++point) {
                    require(phased->glyphs[glyph].points[point].x_26_6 == saved->glyphs[glyph].points[point].x_26_6 + 19);
                    require(phased->glyphs[glyph].points[point].y_26_6 == saved->glyphs[glyph].points[point].y_26_6 + 37);
                }
            }
            font.reset();
            tests::verify_hinted_transport(*saved);
            phase_font.reset();
            compare_frame(*saved->identity, reference.face);
            verify_design_projection(*saved);
            verify_anchor_points(*saved, reference.face);
            require(saved->identity->source->bytes == original && saved->glyphs == repeated->glyphs);
        }
    }
    hinted_font_configuration fractional{13U * 64U + 17U, 14U * 64U + 33U,
        font_hint_policy::truetype_40, 19U, 37U, {}};
    std::unique_ptr<hinted_font> fractional_font;
    hinted_font_error error = hinted_font_error::hinting_failed;
    require(hinted_font::try_create(original, 0U, fractional, fractional_font, error));
    std::shared_ptr<const hinted_glyph_batch> fractional_batch;
    require(fractional_font->try_capture(ids, fractional_batch, error));
    require(fractional_batch->identity->x_pixels_per_em_26_6 == fractional.x_pixels_per_em_26_6 &&
        fractional_batch->identity->y_pixels_per_em_26_6 == fractional.y_pixels_per_em_26_6);
    configure_reference(reference, fractional);
    compare_frame(*fractional_batch->identity, reference.face);
    verify_design_projection(*fractional_batch);
    verify_anchor_points(*fractional_batch, reference.face);
    require(fractional_batch->identity->device_frame.x_scale_16_16 !=
        fractional_batch->identity->device_frame.y_scale_16_16);
    for (std::size_t index = 0U; index < ids.size(); ++index) {
        require(FT_Load_Glyph(reference.face, ids[index],
            FT_LOAD_NO_BITMAP | FT_LOAD_NO_AUTOHINT | FT_LOAD_PEDANTIC | FT_LOAD_TARGET_NORMAL) == 0);
        compare_slot(fractional_batch->glyphs[index], reference.face->glyph);
    }
    // Width rounding alone cannot stand in for real hinted outline changes.
    require(actual_outline_hint_difference && actual_advance_hint_difference);
}

void verify_native_hint_fault()
{
    const auto original = progpu::native::tests::make_hint_fault_font();
    reference_owner reference;
    require(FT_Init_FreeType(&reference.library) == 0);
    require(FT_New_Memory_Face(reference.library, reinterpret_cast<const FT_Byte*>(original.data()),
        static_cast<FT_Long>(original.size()), 0, &reference.face) == 0);
    for (const auto policy : {font_hint_policy::truetype_35, font_hint_policy::truetype_40}) {
        hinted_font_configuration configuration{14U * 64U, 14U * 64U, policy, 0U, 0U, {}};
        configure_reference(reference, configuration);
        constexpr FT_Int32 native_flags = FT_LOAD_NO_BITMAP | FT_LOAD_NO_AUTOHINT |
            FT_LOAD_PEDANTIC | FT_LOAD_TARGET_NORMAL;
        // Both outlines parse and scale. Only the second native program faults.
        require(FT_Load_Glyph(reference.face, 2U, FT_LOAD_NO_HINTING | FT_LOAD_NO_BITMAP) == 0);
        require(FT_Load_Glyph(reference.face, 1U, native_flags) == 0);
        std::unique_ptr<hinted_font> font;
        hinted_font_error error = hinted_font_error::invalid_font;
        require(hinted_font::try_create(original, 0U, configuration, font, error));
        const std::array<std::uint32_t, 1> good{1U};
        std::shared_ptr<const hinted_glyph_batch> batch;
        require(font->try_capture(good, batch, error));
        compare_slot(batch->glyphs[0], reference.face->glyph);
        const auto saved = batch;
        const auto exact = saved->glyphs;
        require(FT_Load_Glyph(reference.face, 2U, native_flags) != 0);
        const std::array<std::uint32_t, 3> late_fault{1U, 1U, 2U};
        for (unsigned int repeat = 0U; repeat < 2U; ++repeat) {
            require(!font->try_capture(late_fault, batch, error));
            require(error == hinted_font_error::hinting_failed && batch == saved && saved->glyphs == exact);
        }
        // A failed slot does not invalidate an earlier retained batch or poison
        // a later valid capture; no caller-visible prefix is published.
        std::shared_ptr<const hinted_glyph_batch> recovered;
        require(font->try_capture(good, recovered, error));
        require(recovered->identity == saved->identity && recovered->glyphs == exact);
    }
}

} // namespace

int main(int argc, char** argv)
{
    try {
        require(argc == 2);
        verify(read_font(argv[1]));
        verify_native_hint_fault();
        std::cout << "{\"glyphBatchControls\":true,\"nativeHintsObserved\":true,"
                     "\"slotDifferential\":true,\"nativeFaultAtomicity\":true,\"fixedWidthTransport\":true,"
                     "\"actualDeviceFrame\":true,\"hintedProjectionSIMD\":true,\"retainedAnchorPoints\":true}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
