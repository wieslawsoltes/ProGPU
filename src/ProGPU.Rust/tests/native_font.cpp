// Executes the unchanged original ProGPU loader and standalone writer.
#include "progpu_native_text.hpp"
#include <array>
#include <cstdio>
#include <cstdlib>
#include <vector>
using namespace progpu::native::text;
void emit(std::uint32_t value) {
    if (std::fwrite(&value, sizeof(value), 1, stdout) != 1) std::exit(2);
}
void payload(std::span<const std::byte> bytes) {
    emit(static_cast<std::uint32_t>(bytes.size()));
    if (std::fwrite(bytes.data(), 1, bytes.size(), stdout) != bytes.size()) std::exit(3);
}
int main() {
    std::uint32_t index{}, length{};
    while (std::fread(&index, sizeof(index), 1, stdin) == 1) {
        if (std::fread(&length, sizeof(length), 1, stdin) != 1) return 4;
        std::vector<std::byte> bytes(length);
        if (std::fread(bytes.data(), 1, bytes.size(), stdin) != bytes.size()) return 5;
        font_error error{};
        std::uint32_t count{};
        const auto counted = sfnt_font_view::try_get_face_count(bytes, count, &error);
        emit(static_cast<std::uint32_t>(error));
        if (counted) emit(count);
        sfnt_font_view face{};
        const auto accepted = sfnt_font_view::try_create(bytes, index, face, &error);
        emit(static_cast<std::uint32_t>(error));
        if (!accepted) continue;
        emit(face.face_index()); emit(face.face_offset()); emit(face.table_count());
        emit(face.uses_symbol_character_map());
        for (const auto* name : {"head", "hhea", "hmtx", "maxp", "loca", "glyf", "cmap", "CFF2", "zzzz"}) {
            sfnt_table_view table{};
            const auto found = face.try_get_table(open_type_tag::from_chars(name[0], name[1], name[2], name[3]), table);
            emit(found);
            if (found) { emit(table.checksum); payload(table.bytes); }
        }
        sfnt_header_metrics head{};
        const auto got_head = face.try_get_header_metrics(head); emit(got_head);
        if (got_head) { emit(head.units_per_em); emit(head.x_min); emit(head.y_min); emit(head.x_max); emit(head.y_max); emit(head.index_to_loc_format); }
        sfnt_horizontal_header_metrics horizontal{};
        const auto got_horizontal = face.try_get_horizontal_header_metrics(horizontal); emit(got_horizontal);
        if (got_horizontal) { emit(horizontal.ascender); emit(horizontal.descender); emit(horizontal.line_gap); emit(horizontal.advance_width_max); emit(horizontal.number_of_horizontal_metrics); }
        std::uint16_t glyph_count{};
        const auto got_count = face.try_get_glyph_count(glyph_count); emit(got_count);
        if (got_count) emit(glyph_count);
        for (const std::uint16_t glyph : {0, 1, 2, 3, 4, 5, 6, 7, 65535}) {
            sfnt_horizontal_glyph_metrics metrics{};
            const auto got_metrics = face.try_get_horizontal_glyph_metrics(glyph, metrics); emit(got_metrics);
            if (got_metrics) { emit(metrics.advance_width); emit(metrics.left_side_bearing); }
            sfnt_glyph_data_view data{};
            const auto got_data = face.try_get_glyph_data(glyph, data); emit(got_data);
            if (got_data) { emit(data.contour_count); emit(data.x_min); emit(data.y_min); emit(data.x_max); emit(data.y_max); payload(data.bytes); }
        }
        for (const std::uint32_t code : {0U, 32U, 0x41U, 0x42U, 0x43U, 0xffU, 0xf041U, 0xffffU, 0x1f600U, 0x10ffffU, 0xffffffffU}) {
            std::uint16_t glyph{};
            const auto found = face.try_get_glyph_index(code, glyph); emit(found);
            if (found) emit(glyph);
            for (const std::uint32_t selector : {0U, 0xfe0fU, 0xe0100U, 0xffffffffU}) {
                const auto varied = face.try_get_variation_glyph(code, selector, glyph); emit(varied);
                if (varied) emit(glyph);
            }
        }
        sfnt_standalone_requirements requirements{};
        const auto valid = face.try_get_standalone_requirements(requirements, &error);
        emit(static_cast<std::uint32_t>(error));
        if (valid) {
            std::vector<std::byte> output(requirements.font_bytes);
            std::vector<sfnt_directory_record> scratch(requirements.table_scratch_count);
            std::size_t written{};
            if (!face.try_create_standalone_font(output, scratch, written, nullptr, &error) || written != output.size()) return 6;
            payload(output);
        }
    }
    return std::ferror(stdin) ? 7 : 0;
}
