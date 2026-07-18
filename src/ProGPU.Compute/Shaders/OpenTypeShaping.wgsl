// Algorithm: initialize a shaping run by binary-searching a compressed nominal cmap, then load direction-aware design-unit metrics by glyph ID.
// Time complexity: O(N log R + L*N*log C) for N scalars, R cmap ranges, L ordered ranged lookups, and coverage size C; initialization, metrics, and output conversion are parallel while lookup mutation is serial.
// Space complexity: O(N + R + G + L) storage for glyphs plus stable internal identities, cmap ranges, G metrics, and lookup commands; each invocation uses O(1) private storage and no textures.
// Workgroups contain 64 independent glyph invocations; the ordered lookup VM uses one invocation because substitutions mutate shared order. Runtime loops are bounded by uploaded counts/capacity and OpenType table counts. Lookup flags use GDEF glyph/mark classes and mark-set coverage without auxiliary allocations. All arithmetic is exact 32-bit integer design-unit arithmetic.

struct Params {
    input_count: u32,
    capacity: u32,
    cmap_count: u32,
    metric_count: u32,
    direction: u32,
    lookup_count: u32,
    variation_count: u32,
    request_flags: u32,
    cluster_level: u32,
    script_tag: u32,
    reserved0: u32,
    reserved1: u32,
};

struct InputScalar {
    codepoint: u32,
    cluster: i32,
    flags: u32,
    reserved: u32,
};

struct CmapRange {
    start: u32,
    end: u32,
    glyph: u32,
    kind: u32,
};

struct GlyphMetric {
    advance_x: i32,
    advance_y: i32,
    origin_x: i32,
    origin_y: i32,
};

struct LayoutVariationDelta {
    key: u32,
    delta: f32,
};

struct ShapingGlyph {
    glyph_id: u32,
    codepoint: u32,
    cluster: i32,
    flags: u32,
    advance_x: i32,
    advance_y: i32,
    offset_x: i32,
    offset_y: i32,
};

struct GlyphState {
    serial: u32,
    ligature_id: u32,
    ligature_component: u32,
    attachment_chain: i32,
    attachment_type: u32,
    feature_mask: u32,
    syllable: u32,
    internal_flags: u32,
};

struct TableDirectory {
    gdef_offset: u32,
    gdef_length: u32,
    gsub_offset: u32,
    gsub_length: u32,
    gpos_offset: u32,
    gpos_length: u32,
    kern_offset: u32,
    kern_length: u32,
};

struct RunState {
    glyph_count: u32,
    status: u32,
    skip_count: u32,
    next_serial: u32,
    random_state: u32,
    reserved0: u32,
    reserved1: u32,
    reserved2: u32,
};

struct LookupCommand {
    table_kind: u32,
    lookup_offset: u32,
    lookup_type: u32,
    lookup_flags: u32,
    feature_tag: u32,
    feature_value: u32,
    range_start: u32,
    range_end: u32,
    command_flags: u32,
};

struct LookupTask {
    lookup_index: u32,
    target_serial: u32,
    depth: u32,
    feature_value: u32,
    feature_tag: u32,
};

@group(0) @binding(0) var<uniform> params: Params;
@group(0) @binding(1) var<storage, read> input_scalars: array<InputScalar>;
@group(0) @binding(2) var<storage, read> cmap_ranges: array<CmapRange>;
@group(0) @binding(3) var<storage, read> glyph_metrics: array<GlyphMetric>;
@group(0) @binding(4) var<storage, read_write> glyphs: array<ShapingGlyph>;
@group(0) @binding(5) var<uniform> table_directory: TableDirectory;
@group(0) @binding(6) var<storage, read> table_words: array<u32>;
@group(0) @binding(7) var<storage, read_write> run_state: RunState;
@group(0) @binding(8) var<storage, read> lookup_commands: array<LookupCommand>;
@group(0) @binding(9) var<storage, read_write> glyph_states: array<GlyphState>;
@group(0) @binding(10) var<storage, read> variation_deltas: array<LayoutVariationDelta>;

const FEATURE_EXPLICIT: u32 = 1u;
const FRACTION_NUMERATOR: u32 = 1u;
const FRACTION_DENOMINATOR: u32 = 2u;
const FRACTION_SLASH: u32 = 4u;
fn is_decimal(codepoint: u32) -> bool {
    return (codepoint >= 0x0030u && codepoint <= 0x0039u) ||
        (codepoint >= 0x0660u && codepoint <= 0x0669u) ||
        (codepoint >= 0x06f0u && codepoint <= 0x06f9u) ||
        (codepoint >= 0x07c0u && codepoint <= 0x07c9u) ||
        (codepoint >= 0x0966u && codepoint <= 0x096fu) ||
        (codepoint >= 0x09e6u && codepoint <= 0x09efu) ||
        (codepoint >= 0x0a66u && codepoint <= 0x0a6fu) ||
        (codepoint >= 0x0ae6u && codepoint <= 0x0aefu) ||
        (codepoint >= 0x0b66u && codepoint <= 0x0b6fu) ||
        (codepoint >= 0x0be6u && codepoint <= 0x0befu) ||
        (codepoint >= 0x0c66u && codepoint <= 0x0c6fu) ||
        (codepoint >= 0x0ce6u && codepoint <= 0x0cefu) ||
        (codepoint >= 0x0d66u && codepoint <= 0x0d6fu) ||
        (codepoint >= 0x0de6u && codepoint <= 0x0defu) ||
        (codepoint >= 0x0e50u && codepoint <= 0x0e59u) ||
        (codepoint >= 0x0ed0u && codepoint <= 0x0ed9u) ||
        (codepoint >= 0x0f20u && codepoint <= 0x0f29u) ||
        (codepoint >= 0x1040u && codepoint <= 0x1049u) ||
        (codepoint >= 0x1090u && codepoint <= 0x1099u) ||
        (codepoint >= 0x17e0u && codepoint <= 0x17e9u) ||
        (codepoint >= 0x1810u && codepoint <= 0x1819u) ||
        (codepoint >= 0x1946u && codepoint <= 0x194fu) ||
        (codepoint >= 0x19d0u && codepoint <= 0x19d9u) ||
        (codepoint >= 0x1a80u && codepoint <= 0x1a89u) ||
        (codepoint >= 0x1a90u && codepoint <= 0x1a99u) ||
        (codepoint >= 0x1b50u && codepoint <= 0x1b59u) ||
        (codepoint >= 0x1bb0u && codepoint <= 0x1bb9u) ||
        (codepoint >= 0x1c40u && codepoint <= 0x1c49u) ||
        (codepoint >= 0x1c50u && codepoint <= 0x1c59u) ||
        (codepoint >= 0xa620u && codepoint <= 0xa629u) ||
        (codepoint >= 0xa8d0u && codepoint <= 0xa8d9u) ||
        (codepoint >= 0xa900u && codepoint <= 0xa909u) ||
        (codepoint >= 0xa9d0u && codepoint <= 0xa9d9u) ||
        (codepoint >= 0xa9f0u && codepoint <= 0xa9f9u) ||
        (codepoint >= 0xaa50u && codepoint <= 0xaa59u) ||
        (codepoint >= 0xabf0u && codepoint <= 0xabf9u) ||
        (codepoint >= 0xff10u && codepoint <= 0xff19u) ||
        (codepoint >= 0x104a0u && codepoint <= 0x104a9u) ||
        (codepoint >= 0x10d30u && codepoint <= 0x10d39u) ||
        (codepoint >= 0x11066u && codepoint <= 0x1106fu) ||
        (codepoint >= 0x110f0u && codepoint <= 0x110f9u) ||
        (codepoint >= 0x11136u && codepoint <= 0x1113fu) ||
        (codepoint >= 0x111d0u && codepoint <= 0x111d9u) ||
        (codepoint >= 0x112f0u && codepoint <= 0x112f9u) ||
        (codepoint >= 0x11450u && codepoint <= 0x11459u) ||
        (codepoint >= 0x114d0u && codepoint <= 0x114d9u) ||
        (codepoint >= 0x11650u && codepoint <= 0x11659u) ||
        (codepoint >= 0x116c0u && codepoint <= 0x116c9u) ||
        (codepoint >= 0x11730u && codepoint <= 0x11739u) ||
        (codepoint >= 0x118e0u && codepoint <= 0x118e9u) ||
        (codepoint >= 0x11950u && codepoint <= 0x11959u) ||
        (codepoint >= 0x11bf0u && codepoint <= 0x11bf9u) ||
        (codepoint >= 0x11c50u && codepoint <= 0x11c59u) ||
        (codepoint >= 0x11d50u && codepoint <= 0x11d59u) ||
        (codepoint >= 0x11da0u && codepoint <= 0x11da9u) ||
        (codepoint >= 0x11f50u && codepoint <= 0x11f59u) ||
        (codepoint >= 0x16130u && codepoint <= 0x16139u) ||
        (codepoint >= 0x16a60u && codepoint <= 0x16a69u) ||
        (codepoint >= 0x16ac0u && codepoint <= 0x16ac9u) ||
        (codepoint >= 0x16b50u && codepoint <= 0x16b59u) ||
        (codepoint >= 0x1ccf0u && codepoint <= 0x1ccf9u) ||
        (codepoint >= 0x1d7ceu && codepoint <= 0x1d7ffu) ||
        (codepoint >= 0x1e140u && codepoint <= 0x1e149u) ||
        (codepoint >= 0x1e2f0u && codepoint <= 0x1e2f9u) ||
        (codepoint >= 0x1e4f0u && codepoint <= 0x1e4f9u) ||
        (codepoint >= 0x1e5f1u && codepoint <= 0x1e5fau) ||
        (codepoint >= 0x1e950u && codepoint <= 0x1e959u);
}

fn prepare_fraction_masks() {
    for (var slash = 1u; slash + 1u < run_state.glyph_count; slash++) {
        if (glyphs[slash].codepoint != 0x2044u || !is_decimal(glyphs[slash - 1u].codepoint) ||
                !is_decimal(glyphs[slash + 1u].codepoint)) { continue; }
        var start = slash;
        loop {
            if (start == 0u || !is_decimal(glyphs[start - 1u].codepoint)) { break; }
            start -= 1u;
        }
        var end = slash + 1u;
        loop {
            if (end >= run_state.glyph_count || !is_decimal(glyphs[end].codepoint)) { break; }
            end += 1u;
        }
        for (var index = start; index < slash; index++) {
            glyph_states[index].feature_mask |= FRACTION_NUMERATOR;
        }
        glyph_states[slash].feature_mask |= FRACTION_SLASH;
        for (var index = slash + 1u; index < end; index++) {
            glyph_states[index].feature_mask |= FRACTION_DENOMINATOR;
        }
    }
}

fn feature_allowed(command: LookupCommand, position: u32) -> bool {
    if ((command.command_flags & FEATURE_EXPLICIT) != 0u) { return true; }
    if (command.feature_tag == 0x66726163u) { return glyph_states[position].feature_mask != 0u; }
    if (command.feature_tag == 0x6e756d72u) {
        return (glyph_states[position].feature_mask & FRACTION_NUMERATOR) != 0u;
    }
    if (command.feature_tag == 0x646e6f6du) {
        return (glyph_states[position].feature_mask & FRACTION_DENOMINATOR) != 0u;
    }
    return true;
}

fn table_u8(offset: u32) -> u32 {
    let word = table_words[offset >> 2u];
    return (word >> ((offset & 3u) * 8u)) & 0xffu;
}

fn table_u16(offset: u32) -> u32 {
    return (table_u8(offset) << 8u) | table_u8(offset + 1u);
}

fn table_u32(offset: u32) -> u32 {
    return (table_u8(offset) << 24u) | (table_u8(offset + 1u) << 16u) |
        (table_u8(offset + 2u) << 8u) | table_u8(offset + 3u);
}

fn coverage_index(offset: u32, glyph: u32) -> i32 {
    let format = table_u16(offset);
    if (format == 1u) {
        let count = table_u16(offset + 2u);
        var low = 0u;
        var high = count;
        loop {
            if (low >= high) { break; }
            let middle = low + ((high - low) >> 1u);
            let value = table_u16(offset + 4u + middle * 2u);
            if (glyph < value) { high = middle; }
            else if (glyph > value) { low = middle + 1u; }
            else { return i32(middle); }
        }
    } else if (format == 2u) {
        let count = table_u16(offset + 2u);
        for (var index = 0u; index < count; index++) {
            let record = offset + 4u + index * 6u;
            let start = table_u16(record);
            let end = table_u16(record + 2u);
            if (glyph >= start && glyph <= end) {
                return i32(table_u16(record + 4u) + glyph - start);
            }
        }
    }
    return -1;
}

fn class_value(offset: u32, glyph: u32) -> u32 {
    if (offset == 0u) { return 0u; }
    let format = table_u16(offset);
    if (format == 1u) {
        let start = table_u16(offset + 2u);
        let count = table_u16(offset + 4u);
        if (glyph >= start && glyph - start < count) {
            return table_u16(offset + 6u + (glyph - start) * 2u);
        }
    } else if (format == 2u) {
        let count = table_u16(offset + 2u);
        for (var index = 0u; index < count; index++) {
            let record = offset + 4u + index * 6u;
            let start = table_u16(record);
            let end = table_u16(record + 2u);
            if (glyph >= start && glyph <= end) { return table_u16(record + 4u); }
        }
    }
    return 0u;
}

fn gdef_class(relative_field: u32, glyph: u32) -> u32 {
    if (table_directory.gdef_length < relative_field + 2u) { return 0u; }
    let relative = table_u16(table_directory.gdef_offset + relative_field);
    if (relative == 0u) { return 0u; }
    return class_value(table_directory.gdef_offset + relative, glyph);
}

fn in_mark_filtering_set(lookup_offset: u32, glyph: u32) -> bool {
    if (table_directory.gdef_length < 14u) { return false; }
    let minor = table_u16(table_directory.gdef_offset + 2u);
    if (minor < 2u) { return false; }
    let sets_relative = table_u16(table_directory.gdef_offset + 12u);
    if (sets_relative == 0u) { return false; }
    let sets = table_directory.gdef_offset + sets_relative;
    if (table_u16(sets) != 1u) { return false; }
    let subtable_count = table_u16(lookup_offset + 4u);
    let set_index = table_u16(lookup_offset + 6u + subtable_count * 2u);
    let set_count = table_u16(sets + 2u);
    if (set_index >= set_count) { return false; }
    let coverage = sets + table_u32(sets + 4u + set_index * 4u);
    return coverage_index(coverage, glyph) >= 0;
}

fn lookup_ignored(position: u32, lookup_offset: u32, lookup_flags: u32) -> bool {
    let glyph = glyphs[position].glyph_id;
    let glyph_class = gdef_class(4u, glyph);
    if ((lookup_flags & 2u) != 0u && glyph_class == 1u) { return true; }
    if ((lookup_flags & 4u) != 0u && glyph_class == 2u) { return true; }
    if ((lookup_flags & 8u) != 0u && glyph_class == 3u) { return true; }
    if (glyph_class == 3u) {
        let attachment_type = lookup_flags >> 8u;
        if (attachment_type != 0u && gdef_class(10u, glyph) != attachment_type) { return true; }
        if ((lookup_flags & 16u) != 0u && !in_mark_filtering_set(lookup_offset, glyph)) { return true; }
    }
    return false;
}

fn next_eligible(start: u32, lookup_offset: u32, lookup_flags: u32) -> i32 {
    for (var index = start; index < run_state.glyph_count; index++) {
        if (!lookup_ignored(index, lookup_offset, lookup_flags)) { return i32(index); }
    }
    return -1;
}

fn previous_eligible(start: i32, lookup_offset: u32, lookup_flags: u32) -> i32 {
    var index = start;
    loop {
        if (index < 0) { break; }
        if (!lookup_ignored(u32(index), lookup_offset, lookup_flags)) { return index; }
        index -= 1;
    }
    return -1;
}

fn eligible_at(position: u32, sequence_index: u32, lookup_offset: u32, lookup_flags: u32) -> i32 {
    var result = i32(position);
    if (lookup_ignored(position, lookup_offset, lookup_flags)) { return -1; }
    for (var index = 0u; index < sequence_index; index++) {
        result = next_eligible(u32(result) + 1u, lookup_offset, lookup_flags);
        if (result < 0) { return -1; }
    }
    return result;
}

fn find_serial(serial: u32) -> i32 {
    for (var index = 0u; index < run_state.glyph_count; index++) {
        if (glyph_states[index].serial == serial) { return i32(index); }
    }
    return -1;
}

fn lookup_from_index(table_base: u32, lookup_index: u32) -> u32 {
    let lookup_list = table_base + table_u16(table_base + 8u);
    let count = table_u16(lookup_list);
    if (lookup_index >= count) { return 0u; }
    return lookup_list + table_u16(lookup_list + 2u + lookup_index * 2u);
}

fn schedule_records(record_offset: u32, record_count: u32, position: u32,
    lookup_offset: u32, lookup_flags: u32, depth: u32, feature_value: u32, feature_tag: u32,
    tasks: ptr<function, array<LookupTask, 64>>, task_count: ptr<function, u32>) -> bool {
    var record = record_count;
    loop {
        if (record == 0u) { break; }
        record -= 1u;
        let sequence_index = table_u16(record_offset + record * 4u);
        let target_index = eligible_at(position, sequence_index, lookup_offset, lookup_flags);
        if (target_index < 0) { return false; }
        if (*task_count >= 64u || depth >= 64u) {
            run_state.status = 2u;
            return false;
        }
        (*tasks)[*task_count] = LookupTask(
            table_u16(record_offset + record * 4u + 2u),
            glyph_states[u32(target_index)].serial,
            depth + 1u,
            feature_value,
            feature_tag);
        *task_count += 1u;
    }
    return true;
}

fn apply_context_subtable(subtable: u32, position: u32, lookup_offset: u32, lookup_flags: u32,
    depth: u32, feature_value: u32, feature_tag: u32, tasks: ptr<function, array<LookupTask, 64>>,
    task_count: ptr<function, u32>) -> bool {
    let format = table_u16(subtable);
    if (format == 1u) {
        let covered = coverage_index(subtable + table_u16(subtable + 2u), glyphs[position].glyph_id);
        let set_count = table_u16(subtable + 4u);
        if (covered < 0 || u32(covered) >= set_count) { return false; }
        let set_relative = table_u16(subtable + 6u + u32(covered) * 2u);
        if (set_relative == 0u) { return false; }
        let rule_set = subtable + set_relative;
        let rule_count = table_u16(rule_set);
        for (var rule_index = 0u; rule_index < rule_count; rule_index++) {
            let rule = rule_set + table_u16(rule_set + 2u + rule_index * 2u);
            let glyph_count = table_u16(rule);
            let record_count = table_u16(rule + 2u);
            var matched = glyph_count != 0u;
            var last = i32(position);
            for (var input_index = 1u; input_index < glyph_count; input_index++) {
                last = next_eligible(u32(last) + 1u, lookup_offset, lookup_flags);
                if (last < 0 || glyphs[u32(last)].glyph_id != table_u16(rule + 2u + input_index * 2u)) {
                    matched = false;
                    break;
                }
            }
            if (!matched) { continue; }
            run_state.skip_count = max(run_state.skip_count, u32(last) - position);
            return schedule_records(rule + 2u + glyph_count * 2u, record_count, position,
                lookup_offset, lookup_flags, depth, feature_value, feature_tag, tasks, task_count);
        }
    } else if (format == 2u) {
        let covered = coverage_index(subtable + table_u16(subtable + 2u), glyphs[position].glyph_id);
        if (covered < 0) { return false; }
        let class_def = subtable + table_u16(subtable + 4u);
        let first_class = class_value(class_def, glyphs[position].glyph_id);
        let set_count = table_u16(subtable + 6u);
        if (first_class >= set_count) { return false; }
        let set_relative = table_u16(subtable + 8u + first_class * 2u);
        if (set_relative == 0u) { return false; }
        let class_set = subtable + set_relative;
        let rule_count = table_u16(class_set);
        for (var rule_index = 0u; rule_index < rule_count; rule_index++) {
            let rule = class_set + table_u16(class_set + 2u + rule_index * 2u);
            let glyph_count = table_u16(rule);
            let record_count = table_u16(rule + 2u);
            var matched = glyph_count != 0u;
            var last = i32(position);
            for (var input_index = 1u; input_index < glyph_count; input_index++) {
                last = next_eligible(u32(last) + 1u, lookup_offset, lookup_flags);
                if (last < 0 || class_value(class_def, glyphs[u32(last)].glyph_id) !=
                        table_u16(rule + 2u + input_index * 2u)) {
                    matched = false;
                    break;
                }
            }
            if (!matched) { continue; }
            run_state.skip_count = max(run_state.skip_count, u32(last) - position);
            return schedule_records(rule + 2u + glyph_count * 2u, record_count, position,
                lookup_offset, lookup_flags, depth, feature_value, feature_tag, tasks, task_count);
        }
    } else if (format == 3u) {
        let glyph_count = table_u16(subtable + 2u);
        let record_count = table_u16(subtable + 4u);
        if (glyph_count == 0u) { return false; }
        var last = i32(position);
        var matched = true;
        for (var input_index = 0u; input_index < glyph_count; input_index++) {
            if (input_index != 0u) { last = next_eligible(u32(last) + 1u, lookup_offset, lookup_flags); }
            if (last < 0 || coverage_index(subtable + table_u16(subtable + 6u + input_index * 2u),
                    glyphs[u32(last)].glyph_id) < 0) {
                matched = false;
                break;
            }
        }
        if (matched) {
            run_state.skip_count = max(run_state.skip_count, u32(last) - position);
            return schedule_records(subtable + 6u + glyph_count * 2u, record_count, position,
                lookup_offset, lookup_flags, depth, feature_value, feature_tag, tasks, task_count);
        }
    }
    return false;
}

fn match_backtrack_glyphs(offset: u32, count: u32, position: u32,
    lookup_offset: u32, lookup_flags: u32, class_def: u32) -> bool {
    var cursor = i32(position) - 1;
    for (var index = 0u; index < count; index++) {
        cursor = previous_eligible(cursor, lookup_offset, lookup_flags);
        if (cursor < 0) { return false; }
        let expected = table_u16(offset + index * 2u);
        if (class_def != 0u) {
            if (class_value(class_def, glyphs[u32(cursor)].glyph_id) != expected) { return false; }
        } else if (glyphs[u32(cursor)].glyph_id != expected) { return false; }
        cursor -= 1;
    }
    return true;
}

fn apply_chain_context_subtable(subtable: u32, position: u32, lookup_offset: u32, lookup_flags: u32,
    depth: u32, feature_value: u32, feature_tag: u32, tasks: ptr<function, array<LookupTask, 64>>,
    task_count: ptr<function, u32>) -> bool {
    let format = table_u16(subtable);
    if (format == 1u) {
        let covered = coverage_index(subtable + table_u16(subtable + 2u), glyphs[position].glyph_id);
        let set_count = table_u16(subtable + 4u);
        if (covered < 0 || u32(covered) >= set_count) { return false; }
        let set_relative = table_u16(subtable + 6u + u32(covered) * 2u);
        if (set_relative == 0u) { return false; }
        let rule_set = subtable + set_relative;
        let rule_count = table_u16(rule_set);
        for (var rule_index = 0u; rule_index < rule_count; rule_index++) {
            let rule = rule_set + table_u16(rule_set + 2u + rule_index * 2u);
            let backtrack_count = table_u16(rule);
            var cursor = rule + 2u;
            if (!match_backtrack_glyphs(cursor, backtrack_count, position, lookup_offset, lookup_flags, 0u)) { continue; }
            cursor += backtrack_count * 2u;
            let input_count = table_u16(cursor);
            cursor += 2u;
            var last = i32(position);
            var matched = input_count != 0u;
            for (var input_index = 1u; input_index < input_count; input_index++) {
                last = next_eligible(u32(last) + 1u, lookup_offset, lookup_flags);
                if (last < 0 || glyphs[u32(last)].glyph_id != table_u16(cursor + (input_index - 1u) * 2u)) {
                    matched = false;
                    break;
                }
            }
            if (!matched) { continue; }
            cursor += (input_count - 1u) * 2u;
            let lookahead_count = table_u16(cursor);
            cursor += 2u;
            var lookahead = last;
            for (var lookahead_index = 0u; lookahead_index < lookahead_count; lookahead_index++) {
                lookahead = next_eligible(u32(lookahead) + 1u, lookup_offset, lookup_flags);
                if (lookahead < 0 || glyphs[u32(lookahead)].glyph_id != table_u16(cursor + lookahead_index * 2u)) {
                    matched = false;
                    break;
                }
            }
            if (!matched) { continue; }
            cursor += lookahead_count * 2u;
            let record_count = table_u16(cursor);
            run_state.skip_count = max(run_state.skip_count, u32(last) - position);
            return schedule_records(cursor + 2u, record_count, position, lookup_offset, lookup_flags,
                depth, feature_value, feature_tag, tasks, task_count);
        }
    } else if (format == 2u) {
        let covered = coverage_index(subtable + table_u16(subtable + 2u), glyphs[position].glyph_id);
        if (covered < 0) { return false; }
        let backtrack_class = subtable + table_u16(subtable + 4u);
        let input_class = subtable + table_u16(subtable + 6u);
        let lookahead_class = subtable + table_u16(subtable + 8u);
        let first_class = class_value(input_class, glyphs[position].glyph_id);
        let set_count = table_u16(subtable + 10u);
        if (first_class >= set_count) { return false; }
        let set_relative = table_u16(subtable + 12u + first_class * 2u);
        if (set_relative == 0u) { return false; }
        let rule_set = subtable + set_relative;
        let rule_count = table_u16(rule_set);
        for (var rule_index = 0u; rule_index < rule_count; rule_index++) {
            let rule = rule_set + table_u16(rule_set + 2u + rule_index * 2u);
            let backtrack_count = table_u16(rule);
            var cursor = rule + 2u;
            if (!match_backtrack_glyphs(cursor, backtrack_count, position, lookup_offset, lookup_flags,
                    backtrack_class)) { continue; }
            cursor += backtrack_count * 2u;
            let input_count = table_u16(cursor);
            cursor += 2u;
            var last = i32(position);
            var matched = input_count != 0u;
            for (var input_index = 1u; input_index < input_count; input_index++) {
                last = next_eligible(u32(last) + 1u, lookup_offset, lookup_flags);
                if (last < 0 || class_value(input_class, glyphs[u32(last)].glyph_id) !=
                        table_u16(cursor + (input_index - 1u) * 2u)) {
                    matched = false;
                    break;
                }
            }
            if (!matched) { continue; }
            cursor += (input_count - 1u) * 2u;
            let lookahead_count = table_u16(cursor);
            cursor += 2u;
            var lookahead = last;
            for (var lookahead_index = 0u; lookahead_index < lookahead_count; lookahead_index++) {
                lookahead = next_eligible(u32(lookahead) + 1u, lookup_offset, lookup_flags);
                if (lookahead < 0 || class_value(lookahead_class, glyphs[u32(lookahead)].glyph_id) !=
                        table_u16(cursor + lookahead_index * 2u)) {
                    matched = false;
                    break;
                }
            }
            if (!matched) { continue; }
            cursor += lookahead_count * 2u;
            let record_count = table_u16(cursor);
            run_state.skip_count = max(run_state.skip_count, u32(last) - position);
            return schedule_records(cursor + 2u, record_count, position, lookup_offset, lookup_flags,
                depth, feature_value, feature_tag, tasks, task_count);
        }
    } else if (format == 3u) {
        var cursor = subtable + 2u;
        let backtrack_count = table_u16(cursor);
        cursor += 2u;
        var backtrack = i32(position) - 1;
        var matched = true;
        for (var index = 0u; index < backtrack_count; index++) {
            backtrack = previous_eligible(backtrack, lookup_offset, lookup_flags);
            if (backtrack < 0 || coverage_index(subtable + table_u16(cursor + index * 2u),
                    glyphs[u32(backtrack)].glyph_id) < 0) { matched = false; break; }
            backtrack -= 1;
        }
        if (!matched) { return false; }
        cursor += backtrack_count * 2u;
        let input_count = table_u16(cursor);
        cursor += 2u;
        if (input_count == 0u) { return false; }
        var last = i32(position);
        for (var input_index = 0u; input_index < input_count; input_index++) {
            if (input_index != 0u) { last = next_eligible(u32(last) + 1u, lookup_offset, lookup_flags); }
            if (last < 0 || coverage_index(subtable + table_u16(cursor + input_index * 2u),
                    glyphs[u32(last)].glyph_id) < 0) { matched = false; break; }
        }
        if (!matched) { return false; }
        cursor += input_count * 2u;
        let lookahead_count = table_u16(cursor);
        cursor += 2u;
        var lookahead = last;
        for (var index = 0u; index < lookahead_count; index++) {
            lookahead = next_eligible(u32(lookahead) + 1u, lookup_offset, lookup_flags);
            if (lookahead < 0 || coverage_index(subtable + table_u16(cursor + index * 2u),
                    glyphs[u32(lookahead)].glyph_id) < 0) { matched = false; break; }
        }
        if (!matched) { return false; }
        cursor += lookahead_count * 2u;
        let record_count = table_u16(cursor);
        run_state.skip_count = max(run_state.skip_count, u32(last) - position);
        return schedule_records(cursor + 2u, record_count, position, lookup_offset, lookup_flags,
            depth, feature_value, feature_tag, tasks, task_count);
    }
    return false;
}

fn apply_reverse_chain_subtable(subtable: u32, position: u32,
    lookup_offset: u32, lookup_flags: u32) -> bool {
    if (table_u16(subtable) != 1u) { return false; }
    let covered = coverage_index(subtable + table_u16(subtable + 2u), glyphs[position].glyph_id);
    if (covered < 0) { return false; }
    var cursor = subtable + 4u;
    let backtrack_count = table_u16(cursor);
    cursor += 2u;
    var match_position = i32(position) - 1;
    for (var index = 0u; index < backtrack_count; index++) {
        match_position = previous_eligible(match_position, lookup_offset, lookup_flags);
        if (match_position < 0 || coverage_index(subtable + table_u16(cursor + index * 2u),
                glyphs[u32(match_position)].glyph_id) < 0) { return false; }
        match_position -= 1;
    }
    cursor += backtrack_count * 2u;
    let lookahead_count = table_u16(cursor);
    cursor += 2u;
    match_position = i32(position);
    for (var index = 0u; index < lookahead_count; index++) {
        match_position = next_eligible(u32(match_position) + 1u, lookup_offset, lookup_flags);
        if (match_position < 0 || coverage_index(subtable + table_u16(cursor + index * 2u),
                glyphs[u32(match_position)].glyph_id) < 0) { return false; }
    }
    cursor += lookahead_count * 2u;
    let glyph_count = table_u16(cursor);
    if (u32(covered) >= glyph_count) { return false; }
    glyphs[position].glyph_id = table_u16(cursor + 2u + u32(covered) * 2u);
    return true;
}

fn is_reverse_lookup(lookup_offset: u32, lookup_type: u32) -> bool {
    if (lookup_type == 8u) { return true; }
    if (lookup_type != 7u || table_u16(lookup_offset + 4u) == 0u) { return false; }
    let extension = lookup_offset + table_u16(lookup_offset + 6u);
    return table_u16(extension) == 1u && table_u16(extension + 2u) == 8u;
}

fn apply_single_substitution(subtable: u32, position: u32) -> bool {
    let format = table_u16(subtable);
    let coverage = subtable + table_u16(subtable + 2u);
    let covered = coverage_index(coverage, glyphs[position].glyph_id);
    if (covered < 0) { return false; }
    if (format == 1u) {
        let delta = i32(table_u16(subtable + 4u) << 16u) >> 16;
        glyphs[position].glyph_id = u32(i32(glyphs[position].glyph_id) + delta) & 0xffffu;
        return true;
    }
    if (format == 2u && u32(covered) < table_u16(subtable + 4u)) {
        glyphs[position].glyph_id = table_u16(subtable + 6u + u32(covered) * 2u);
        return true;
    }
    return false;
}

fn replace_multiple(subtable: u32, position: u32) -> bool {
    let covered = coverage_index(subtable + table_u16(subtable + 2u), glyphs[position].glyph_id);
    let sequence_count = table_u16(subtable + 4u);
    if (covered < 0 || u32(covered) >= sequence_count) { return false; }
    let sequence = subtable + table_u16(subtable + 6u + u32(covered) * 2u);
    let replacement_count = table_u16(sequence);
    if (replacement_count == 0u) {
        for (var cursor = position + 1u; cursor < run_state.glyph_count; cursor++) {
            glyphs[cursor - 1u] = glyphs[cursor];
            glyph_states[cursor - 1u] = glyph_states[cursor];
        }
        run_state.glyph_count -= 1u;
        return true;
    }
    let extra = replacement_count - 1u;
    if (run_state.glyph_count + extra > params.capacity) {
        run_state.status = 1u;
        return false;
    }
    var cursor = run_state.glyph_count;
    loop {
        if (cursor <= position + 1u) { break; }
        cursor -= 1u;
        glyphs[cursor + extra] = glyphs[cursor];
        glyph_states[cursor + extra] = glyph_states[cursor];
    }
    let source = glyphs[position];
    let source_state = glyph_states[position];
    for (var replacement = 0u; replacement < replacement_count; replacement++) {
        glyphs[position + replacement] = source;
        glyphs[position + replacement].glyph_id = table_u16(sequence + 2u + replacement * 2u);
        glyph_states[position + replacement] = source_state;
        if (replacement != 0u) {
            glyph_states[position + replacement].serial = run_state.next_serial;
            run_state.next_serial += 1u;
        }
    }
    run_state.glyph_count += extra;
    return true;
}

fn apply_alternate(subtable: u32, position: u32, feature_value: u32, feature_tag: u32) -> bool {
    let covered = coverage_index(subtable + table_u16(subtable + 2u), glyphs[position].glyph_id);
    let set_count = table_u16(subtable + 4u);
    if (covered < 0 || u32(covered) >= set_count) { return false; }
    let alternate_set = subtable + table_u16(subtable + 6u + u32(covered) * 2u);
    let count = table_u16(alternate_set);
    if (count == 0u) { return false; }
    var selected = min(max(feature_value, 1u), count) - 1u;
    if (feature_tag == 0x72616e64u && feature_value == 65535u) {
        run_state.random_state = (run_state.random_state * 48271u) % 2147483647u;
        selected = run_state.random_state % count;
    }
    glyphs[position].glyph_id = table_u16(alternate_set + 2u + selected * 2u);
    return true;
}

fn apply_ligature(subtable: u32, position: u32, lookup_offset: u32, lookup_flags: u32) -> bool {
    let covered = coverage_index(subtable + table_u16(subtable + 2u), glyphs[position].glyph_id);
    let set_count = table_u16(subtable + 4u);
    if (covered < 0 || u32(covered) >= set_count) { return false; }
    let ligature_set = subtable + table_u16(subtable + 6u + u32(covered) * 2u);
    let ligature_count = table_u16(ligature_set);
    for (var ligature_index = 0u; ligature_index < ligature_count; ligature_index++) {
        let ligature = ligature_set + table_u16(ligature_set + 2u + ligature_index * 2u);
        let component_count = table_u16(ligature + 2u);
        if (component_count == 0u) { continue; }
        var matched = true;
        var match_position = position;
        for (var component = 1u; component < component_count; component++) {
            let next = next_eligible(match_position + 1u, lookup_offset, lookup_flags);
            if (next < 0 || glyphs[u32(next)].glyph_id != table_u16(ligature + 2u + component * 2u)) {
                matched = false;
                break;
            }
            match_position = u32(next);
        }
        if (!matched) { continue; }
        var cluster = glyphs[position].cluster;
        for (var cursor = position + 1u; cursor <= match_position; cursor++) {
            cluster = min(cluster, glyphs[cursor].cluster);
        }
        glyphs[position].glyph_id = table_u16(ligature);
        glyphs[position].cluster = cluster;
        let ligature_id = run_state.reserved0 + 1u;
        run_state.reserved0 = ligature_id;
        glyph_states[position].ligature_id = ligature_id;
        glyph_states[position].ligature_component = component_count;
        var component_number = 1u;
        for (var cursor = position + 1u; cursor <= match_position; cursor++) {
            if (lookup_ignored(cursor, lookup_offset, lookup_flags)) {
                glyph_states[cursor].ligature_id = ligature_id;
                glyph_states[cursor].ligature_component = component_number;
            } else {
                component_number += 1u;
            }
        }
        for (var component = 1u; component < component_count; component++) {
            let remove_at = next_eligible(position + 1u, lookup_offset, lookup_flags);
            if (remove_at < 0) { break; }
            for (var cursor = u32(remove_at) + 1u; cursor < run_state.glyph_count; cursor++) {
                glyphs[cursor - 1u] = glyphs[cursor];
                glyph_states[cursor - 1u] = glyph_states[cursor];
            }
            run_state.glyph_count -= 1u;
        }
        return true;
    }
    return false;
}

fn apply_gsub_subtable(lookup_type: u32, subtable: u32, position: u32, value: u32, feature_tag: u32,
    lookup_offset: u32, lookup_flags: u32, depth: u32,
    tasks: ptr<function, array<LookupTask, 64>>, task_count: ptr<function, u32>) -> bool {
    if (lookup_type == 1u) { return apply_single_substitution(subtable, position); }
    if (lookup_type == 2u && table_u16(subtable) == 1u) { return replace_multiple(subtable, position); }
    if (lookup_type == 3u && table_u16(subtable) == 1u) {
        return apply_alternate(subtable, position, value, feature_tag);
    }
    if (lookup_type == 4u && table_u16(subtable) == 1u) {
        return apply_ligature(subtable, position, lookup_offset, lookup_flags);
    }
    if (lookup_type == 5u) {
        return apply_context_subtable(subtable, position, lookup_offset, lookup_flags,
            depth, value, feature_tag, tasks, task_count);
    }
    if (lookup_type == 6u) {
        return apply_chain_context_subtable(subtable, position, lookup_offset, lookup_flags,
            depth, value, feature_tag, tasks, task_count);
    }
    if (lookup_type == 8u) {
        return apply_reverse_chain_subtable(subtable, position, lookup_offset, lookup_flags);
    }
    return false;
}

fn apply_lookup_at(lookup_offset: u32, position: u32, feature_value: u32, feature_tag: u32, depth: u32,
    tasks: ptr<function, array<LookupTask, 64>>, task_count: ptr<function, u32>) -> bool {
    if (lookup_offset == 0u || position >= run_state.glyph_count) { return false; }
    let lookup_type = table_u16(lookup_offset);
    let lookup_flags = table_u16(lookup_offset + 2u);
    if (lookup_ignored(position, lookup_offset, lookup_flags)) { return false; }
    let subtable_count = table_u16(lookup_offset + 4u);
    for (var subtable_index = 0u; subtable_index < subtable_count; subtable_index++) {
        let subtable = lookup_offset + table_u16(lookup_offset + 6u + subtable_index * 2u);
        var effective_type = lookup_type;
        var effective_subtable = subtable;
        if (effective_type == 7u && table_u16(subtable) == 1u) {
            effective_type = table_u16(subtable + 2u);
            effective_subtable = subtable + table_u32(subtable + 4u);
        }
        if (apply_gsub_subtable(effective_type, effective_subtable, position, feature_value, feature_tag,
                lookup_offset, lookup_flags, depth, tasks, task_count)) { return true; }
    }
    return false;
}

@compute @workgroup_size(1)
fn execute_lookups(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x != 0u) { return; }
    prepare_fraction_masks();
    var tasks: array<LookupTask, 64>;
    var task_count = 0u;
    for (var command_index = 0u; command_index < params.lookup_count; command_index++) {
        let command = lookup_commands[command_index];
        if (command.table_kind != 1u || command.feature_value == 0u) { continue; }
        if (is_reverse_lookup(command.lookup_offset, command.lookup_type)) {
            var reverse_position = run_state.glyph_count;
            loop {
                if (reverse_position == 0u) { break; }
                reverse_position -= 1u;
                let cluster = u32(max(glyphs[reverse_position].cluster, 0));
                if (cluster < command.range_start || cluster >= command.range_end ||
                        !feature_allowed(command, reverse_position)) { continue; }
                _ = apply_lookup_at(command.lookup_offset, reverse_position, command.feature_value,
                    command.feature_tag, 0u, &tasks, &task_count);
                if (run_state.status != 0u) { return; }
            }
            continue;
        }
        for (var position = 0u; position < run_state.glyph_count; position++) {
            run_state.skip_count = 0u;
            let cluster = u32(max(glyphs[position].cluster, 0));
            if (cluster < command.range_start || cluster >= command.range_end) { continue; }
            if (!feature_allowed(command, position)) { continue; }
            _ = apply_lookup_at(command.lookup_offset, position, command.feature_value,
                command.feature_tag, 0u, &tasks, &task_count);
            loop {
                if (task_count == 0u || run_state.status != 0u) { break; }
                task_count -= 1u;
                let task = tasks[task_count];
                let target_index = find_serial(task.target_serial);
                if (target_index < 0) { continue; }
                let nested_lookup = lookup_from_index(table_directory.gsub_offset, task.lookup_index);
                _ = apply_lookup_at(nested_lookup, u32(target_index), task.feature_value,
                    task.feature_tag, task.depth, &tasks, &task_count);
            }
            if (run_state.status != 0u) { return; }
            position += run_state.skip_count;
        }
    }
}

@compute @workgroup_size(1)
fn execute_positions(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x != 0u) { return; }
    var tasks: array<LookupTask, 64>;
    var task_count = 0u;
    for (var command_index = 0u; command_index < params.lookup_count; command_index++) {
        let command = lookup_commands[command_index];
        if (command.table_kind != 2u || command.feature_value == 0u) { continue; }
        for (var position = 0u; position < run_state.glyph_count; position++) {
            let cluster = u32(max(glyphs[position].cluster, 0));
            if (cluster < command.range_start || cluster >= command.range_end ||
                    lookup_ignored(position, command.lookup_offset, command.lookup_flags)) { continue; }
            _ = apply_gpos_lookup_at(command.lookup_offset, position, 0u, &tasks, &task_count);
            loop {
                if (task_count == 0u || run_state.status != 0u) { break; }
                task_count -= 1u;
                let task = tasks[task_count];
                let target_index = find_serial(task.target_serial);
                if (target_index < 0) { continue; }
                let nested_lookup = lookup_from_index(table_directory.gpos_offset, task.lookup_index);
                _ = apply_gpos_lookup_at(nested_lookup, u32(target_index), task.depth, &tasks, &task_count);
            }
            if (run_state.status != 0u) { return; }
        }
    }
    resolve_attachments();
}

fn value_record_size(format: u32) -> u32 {
    return countOneBits(format & 0xffu) * 2u;
}

fn layout_variation_delta(offset: u32) -> f32 {
    if (offset == 0u || table_u16(offset + 4u) != 0x8000u) { return 0.0; }
    let key = (table_u16(offset) << 16u) | table_u16(offset + 2u);
    var low = 0u;
    var high = params.variation_count;
    loop {
        if (low >= high) { break; }
        let middle = low + ((high - low) >> 1u);
        let value = variation_deltas[middle];
        if (key < value.key) { high = middle; }
        else if (key > value.key) { low = middle + 1u; }
        else { return value.delta; }
    }
    return 0.0;
}

fn apply_value_record(offset: u32, format: u32, position: u32, subtable: u32) {
    var cursor = offset;
    if ((format & 1u) != 0u) {
        glyphs[position].offset_x += i32(table_u16(cursor) << 16u) >> 16;
        cursor += 2u;
    }
    if ((format & 2u) != 0u) {
        glyphs[position].offset_y += i32(table_u16(cursor) << 16u) >> 16;
        cursor += 2u;
    }
    if ((format & 4u) != 0u) {
        glyphs[position].advance_x += i32(table_u16(cursor) << 16u) >> 16;
        cursor += 2u;
    }
    if ((format & 8u) != 0u) {
        glyphs[position].advance_y += i32(table_u16(cursor) << 16u) >> 16;
        cursor += 2u;
    }
    if ((format & 16u) != 0u) {
        let relative = table_u16(cursor);
        if (relative != 0u) { glyphs[position].offset_x += i32(round(layout_variation_delta(subtable + relative))); }
        cursor += 2u;
    }
    if ((format & 32u) != 0u) {
        let relative = table_u16(cursor);
        if (relative != 0u) { glyphs[position].offset_y += i32(round(layout_variation_delta(subtable + relative))); }
        cursor += 2u;
    }
    if ((format & 64u) != 0u) {
        let relative = table_u16(cursor);
        if (relative != 0u) { glyphs[position].advance_x += i32(round(layout_variation_delta(subtable + relative))); }
        cursor += 2u;
    }
    if ((format & 128u) != 0u) {
        let relative = table_u16(cursor);
        if (relative != 0u) { glyphs[position].advance_y += i32(round(layout_variation_delta(subtable + relative))); }
    }
}

fn apply_single_position(subtable: u32, position: u32) -> bool {
    let covered = coverage_index(subtable + table_u16(subtable + 2u), glyphs[position].glyph_id);
    if (covered < 0) { return false; }
    let format = table_u16(subtable);
    let value_format = table_u16(subtable + 4u);
    if (format == 1u) {
        apply_value_record(subtable + 6u, value_format, position, subtable);
        return true;
    }
    if (format == 2u && u32(covered) < table_u16(subtable + 6u)) {
        apply_value_record(subtable + 8u + u32(covered) * value_record_size(value_format),
            value_format, position, subtable);
        return true;
    }
    return false;
}

fn apply_pair_position(subtable: u32, position: u32, lookup_offset: u32, lookup_flags: u32) -> bool {
    let covered = coverage_index(subtable + table_u16(subtable + 2u), glyphs[position].glyph_id);
    if (covered < 0) { return false; }
    let second_index = next_eligible(position + 1u, lookup_offset, lookup_flags);
    if (second_index < 0) { return false; }
    let second = u32(second_index);
    let value_format1 = table_u16(subtable + 4u);
    let value_format2 = table_u16(subtable + 6u);
    let value_size1 = value_record_size(value_format1);
    let value_size2 = value_record_size(value_format2);
    let format = table_u16(subtable);
    if (format == 1u) {
        let set_count = table_u16(subtable + 8u);
        if (u32(covered) >= set_count) { return false; }
        let pair_set = subtable + table_u16(subtable + 10u + u32(covered) * 2u);
        let pair_count = table_u16(pair_set);
        let record_size = 2u + value_size1 + value_size2;
        var low = 0u;
        var high = pair_count;
        loop {
            if (low >= high) { break; }
            let middle = low + ((high - low) >> 1u);
            let record = pair_set + 2u + middle * record_size;
            let glyph = table_u16(record);
            if (glyphs[second].glyph_id < glyph) { high = middle; }
            else if (glyphs[second].glyph_id > glyph) { low = middle + 1u; }
            else {
                if (value_format1 != 0u) { apply_value_record(record + 2u, value_format1, position, subtable); }
                if (value_format2 != 0u) { apply_value_record(record + 2u + value_size1, value_format2, second, subtable); }
                return true;
            }
        }
        return false;
    }
    if (format != 2u) { return false; }
    let class1 = class_value(subtable + table_u16(subtable + 8u), glyphs[position].glyph_id);
    let class2 = class_value(subtable + table_u16(subtable + 10u), glyphs[second].glyph_id);
    let class1_count = table_u16(subtable + 12u);
    let class2_count = table_u16(subtable + 14u);
    if (class1 >= class1_count || class2 >= class2_count) { return false; }
    let record = subtable + 16u + (class1 * class2_count + class2) * (value_size1 + value_size2);
    if (value_format1 != 0u) { apply_value_record(record, value_format1, position, subtable); }
    if (value_format2 != 0u) { apply_value_record(record + value_size1, value_format2, second, subtable); }
    return true;
}

fn read_anchor(offset: u32) -> vec2<i32> {
    let format = table_u16(offset);
    if (offset == 0u || format < 1u || format > 3u) { return vec2<i32>(0x7fffffffi, 0); }
    var x = i32(table_u16(offset + 2u) << 16u) >> 16;
    var y = i32(table_u16(offset + 4u) << 16u) >> 16;
    if (format == 3u) {
        let x_relative = table_u16(offset + 6u);
        let y_relative = table_u16(offset + 8u);
        if (x_relative != 0u) { x += i32(round(layout_variation_delta(offset + x_relative))); }
        if (y_relative != 0u) { y += i32(round(layout_variation_delta(offset + y_relative))); }
    }
    return vec2<i32>(x, y);
}

fn previous_covered(position: u32, lookup_offset: u32, lookup_flags: u32,
    coverage: u32, skip_marks: bool) -> i32 {
    var index = i32(position) - 1;
    loop {
        if (index < 0) { return -1; }
        let glyph_class = gdef_class(4u, glyphs[u32(index)].glyph_id);
        if (!lookup_ignored(u32(index), lookup_offset, lookup_flags) &&
                !(skip_marks && glyph_class == 3u)) {
            return select(-1, index, coverage_index(coverage, glyphs[u32(index)].glyph_id) >= 0);
        }
        index -= 1;
    }
    return -1;
}

fn apply_cursive_position(subtable: u32, position: u32,
    lookup_offset: u32, lookup_flags: u32) -> bool {
    if (table_u16(subtable) != 1u) { return false; }
    let coverage = subtable + table_u16(subtable + 2u);
    let covered = coverage_index(coverage, glyphs[position].glyph_id);
    let record_count = table_u16(subtable + 4u);
    if (covered < 0 || u32(covered) >= record_count) { return false; }
    let current_record = subtable + 6u + u32(covered) * 4u;
    let entry_relative = table_u16(current_record);
    if (entry_relative == 0u) { return false; }
    let previous_index = previous_covered(position, lookup_offset, lookup_flags, coverage, false);
    if (previous_index < 0) { return false; }
    let previous_coverage = coverage_index(coverage, glyphs[u32(previous_index)].glyph_id);
    if (previous_coverage < 0 || u32(previous_coverage) >= record_count) { return false; }
    let previous_record = subtable + 6u + u32(previous_coverage) * 4u;
    let exit_relative = table_u16(previous_record + 2u);
    if (exit_relative == 0u) { return false; }
    let entry = read_anchor(subtable + entry_relative);
    let exit = read_anchor(subtable + exit_relative);
    if (entry.x == 0x7fffffffi || exit.x == 0x7fffffffi) { return false; }
    let previous = u32(previous_index);
    if (params.direction == 1u) {
        glyphs[previous].advance_x = exit.x + glyphs[previous].offset_x;
        let delta = entry.x + glyphs[position].offset_x;
        glyphs[position].advance_x -= delta;
        glyphs[position].offset_x -= delta;
    } else if (params.direction == 2u) {
        let delta = exit.x + glyphs[previous].offset_x;
        glyphs[previous].advance_x -= delta;
        glyphs[previous].offset_x -= delta;
        glyphs[position].advance_x = entry.x + glyphs[position].offset_x;
    } else if (params.direction == 3u) {
        glyphs[previous].advance_y = exit.y + glyphs[previous].offset_y;
        let delta = entry.y + glyphs[position].offset_y;
        glyphs[position].advance_y -= delta;
        glyphs[position].offset_y -= delta;
    } else {
        let delta = exit.y + glyphs[previous].offset_y;
        glyphs[previous].advance_y -= delta;
        glyphs[previous].offset_y -= delta;
        glyphs[position].advance_y = entry.y;
    }
    let rtl_lookup = (lookup_flags & 1u) != 0u;
    let child = select(position, previous, rtl_lookup);
    let parent = select(previous, position, rtl_lookup);
    glyph_states[child].attachment_chain = i32(parent);
    if (params.direction == 1u || params.direction == 2u) {
        glyph_states[child].attachment_type = 2u;
        glyphs[child].offset_y = select(exit.y - entry.y, entry.y - exit.y, rtl_lookup);
    } else {
        glyph_states[child].attachment_type = 3u;
        glyphs[child].offset_x = select(exit.x - entry.x, entry.x - exit.x, rtl_lookup);
    }
    return true;
}

fn attach_mark(mark_index: u32, target_index: u32, mark_coverage_index: u32,
    target_coverage_index: u32, class_count: u32, mark_array: u32,
    target_anchor_base: u32, target_record_base: u32) -> bool {
    let mark_record = mark_array + 2u + mark_coverage_index * 4u;
    let mark_class = table_u16(mark_record);
    if (mark_class >= class_count) { return false; }
    let mark_anchor_relative = table_u16(mark_record + 2u);
    let target_record = target_record_base +
        (target_coverage_index * class_count + mark_class) * 2u;
    let target_anchor_relative = table_u16(target_record);
    if (mark_anchor_relative == 0u || target_anchor_relative == 0u) { return false; }
    let mark_anchor = read_anchor(mark_array + mark_anchor_relative);
    let target_anchor = read_anchor(target_anchor_base + target_anchor_relative);
    if (mark_anchor.x == 0x7fffffffi || target_anchor.x == 0x7fffffffi) { return false; }
    glyphs[mark_index].offset_x = target_anchor.x - mark_anchor.x;
    glyphs[mark_index].offset_y = target_anchor.y - mark_anchor.y;
    glyph_states[mark_index].attachment_chain = i32(target_index);
    glyph_states[mark_index].attachment_type = 1u;
    return true;
}

fn apply_mark_position(subtable: u32, position: u32, lookup_type: u32,
    lookup_offset: u32, lookup_flags: u32) -> bool {
    if (table_u16(subtable) != 1u) { return false; }
    let mark_coverage = subtable + table_u16(subtable + 2u);
    let mark_covered = coverage_index(mark_coverage, glyphs[position].glyph_id);
    if (mark_covered < 0) { return false; }
    let target_coverage = subtable + table_u16(subtable + 4u);
    let class_count = table_u16(subtable + 6u);
    if (class_count == 0u) { return false; }
    let mark_array = subtable + table_u16(subtable + 8u);
    let target_array = subtable + table_u16(subtable + 10u);
    let skip_marks = lookup_type != 6u;
    let target_index = previous_covered(position, lookup_offset, lookup_flags, target_coverage, skip_marks);
    if (target_index < 0) { return false; }
    let attachment_target = u32(target_index);
    let target_class = gdef_class(4u, glyphs[attachment_target].glyph_id);
    if (lookup_type == 4u && target_class != 0u && target_class != 1u) { return false; }
    if (lookup_type == 5u && target_class != 2u) { return false; }
    if (lookup_type == 6u && target_class != 3u) { return false; }
    if (lookup_type == 6u && glyph_states[position].ligature_component != 0u &&
            glyph_states[attachment_target].ligature_component != 0u &&
            glyph_states[position].ligature_component != glyph_states[attachment_target].ligature_component) { return false; }
    let target_covered = coverage_index(target_coverage, glyphs[attachment_target].glyph_id);
    if (target_covered < 0) { return false; }
    if (lookup_type == 5u) {
        let ligature_attach = target_array + table_u16(target_array + 2u + u32(target_covered) * 2u);
        let component_count = table_u16(ligature_attach);
        if (component_count == 0u) { return false; }
        var component = component_count - 1u;
        if (glyph_states[position].ligature_id == glyph_states[attachment_target].ligature_id &&
                glyph_states[position].ligature_component != 0u) {
            component = min(glyph_states[position].ligature_component - 1u, component);
        }
        return attach_mark(position, attachment_target, u32(mark_covered), 0u, class_count, mark_array,
            ligature_attach, ligature_attach + 2u + component * class_count * 2u);
    }
    return attach_mark(position, attachment_target, u32(mark_covered), u32(target_covered), class_count,
        mark_array, target_array, target_array + 2u);
}

fn resolve_attachments() {
    for (var index = 0u; index < run_state.glyph_count; index++) {
        glyph_states[index].internal_flags &= ~1u;
        if (glyph_states[index].attachment_type == 0u) { glyph_states[index].internal_flags |= 1u; }
    }
    for (var resolve_step = 0u; resolve_step < run_state.glyph_count; resolve_step++) {
        var changed = false;
        for (var index = 0u; index < run_state.glyph_count; index++) {
            if ((glyph_states[index].internal_flags & 1u) != 0u) { continue; }
            let target_signed = glyph_states[index].attachment_chain;
            if (target_signed < 0 || u32(target_signed) >= run_state.glyph_count) {
                glyph_states[index].internal_flags |= 1u;
                continue;
            }
            let attachment_target = u32(target_signed);
            if ((glyph_states[attachment_target].internal_flags & 1u) == 0u) { continue; }
            let attachment_type = glyph_states[index].attachment_type;
            if (attachment_type == 1u) {
                glyphs[index].offset_x += glyphs[attachment_target].offset_x;
                glyphs[index].offset_y += glyphs[attachment_target].offset_y;
                let forward = params.direction == 1u || params.direction == 3u;
                if (attachment_target < index) {
                    let start = select(attachment_target + 1u, attachment_target, forward);
                    let end = select(index + 1u, index, forward);
                    let sign = select(1, -1, forward);
                    for (var advance = start; advance < end; advance++) {
                        glyphs[index].offset_x += sign * glyphs[advance].advance_x;
                        glyphs[index].offset_y += sign * glyphs[advance].advance_y;
                    }
                } else if (attachment_target > index) {
                    let start = select(index + 1u, index, forward);
                    let end = select(attachment_target + 1u, attachment_target, forward);
                    let sign = select(-1, 1, forward);
                    for (var advance = start; advance < end; advance++) {
                        glyphs[index].offset_x += sign * glyphs[advance].advance_x;
                        glyphs[index].offset_y += sign * glyphs[advance].advance_y;
                    }
                }
            } else if (attachment_type == 2u) {
                glyphs[index].offset_y += glyphs[attachment_target].offset_y;
            } else if (attachment_type == 3u) {
                glyphs[index].offset_x += glyphs[attachment_target].offset_x;
            }
            glyph_states[index].internal_flags |= 1u;
            changed = true;
        }
        if (!changed) { break; }
    }
}

fn apply_gpos_subtable(lookup_type: u32, subtable: u32, position: u32,
    lookup_offset: u32, lookup_flags: u32, depth: u32,
    tasks: ptr<function, array<LookupTask, 64>>, task_count: ptr<function, u32>) -> bool {
    if (lookup_type == 1u) { return apply_single_position(subtable, position); }
    if (lookup_type == 2u) {
        return apply_pair_position(subtable, position, lookup_offset, lookup_flags);
    }
    if (lookup_type == 3u) {
        return apply_cursive_position(subtable, position, lookup_offset, lookup_flags);
    }
    if (lookup_type >= 4u && lookup_type <= 6u) {
        return apply_mark_position(subtable, position, lookup_type, lookup_offset, lookup_flags);
    }
    if (lookup_type == 7u) {
        return apply_context_subtable(subtable, position, lookup_offset, lookup_flags,
            depth, 1u, 0u, tasks, task_count);
    }
    if (lookup_type == 8u) {
        return apply_chain_context_subtable(subtable, position, lookup_offset, lookup_flags,
            depth, 1u, 0u, tasks, task_count);
    }
    return false;
}

fn apply_gpos_lookup_at(lookup_offset: u32, position: u32, depth: u32,
    tasks: ptr<function, array<LookupTask, 64>>, task_count: ptr<function, u32>) -> bool {
    if (lookup_offset == 0u || position >= run_state.glyph_count) { return false; }
    let lookup_type = table_u16(lookup_offset);
    let lookup_flags = table_u16(lookup_offset + 2u);
    if (lookup_ignored(position, lookup_offset, lookup_flags)) { return false; }
    let subtable_count = table_u16(lookup_offset + 4u);
    for (var subtable_index = 0u; subtable_index < subtable_count; subtable_index++) {
        let subtable = lookup_offset + table_u16(lookup_offset + 6u + subtable_index * 2u);
        var effective_type = lookup_type;
        var effective_subtable = subtable;
        if (effective_type == 9u && table_u16(subtable) == 1u) {
            effective_type = table_u16(subtable + 2u);
            effective_subtable = subtable + table_u32(subtable + 4u);
        }
        if (apply_gpos_subtable(effective_type, effective_subtable, position,
                lookup_offset, lookup_flags, depth, tasks, task_count)) { return true; }
    }
    return false;
}

fn nominal_glyph(codepoint: u32) -> u32 {
    var low = 0u;
    var high = params.cmap_count;
    loop {
        if (low >= high) { break; }
        let middle = low + ((high - low) >> 1u);
        let range = cmap_ranges[middle];
        if (codepoint < range.start) {
            high = middle;
        } else if (codepoint > range.end) {
            low = middle + 1u;
        } else if (range.kind == 1u) {
            return range.glyph;
        } else {
            return range.glyph + codepoint - range.start;
        }
    }
    return 0u;
}

@compute @workgroup_size(64)
fn initialize_glyphs(@builtin(global_invocation_id) id: vec3<u32>) {
    let index = id.x;
    if (index >= params.input_count) { return; }
    if (index == 0u) {
        run_state = RunState(params.input_count, 0u, 0u, params.input_count + 1u, 1u, 0u, 0u, 0u);
    }
    let input = input_scalars[index];
    glyphs[index] = ShapingGlyph(
        nominal_glyph(input.codepoint), input.codepoint, input.cluster, input.flags,
        0, 0, 0, 0);
    glyph_states[index] = GlyphState(index + 1u, 0u, 0u, 0, 0u, 0u, 0u, 0u);
}

@compute @workgroup_size(64)
fn load_metrics(@builtin(global_invocation_id) id: vec3<u32>) {
    let index = id.x;
    if (index >= run_state.glyph_count) { return; }
    let glyph_id = glyphs[index].glyph_id;
    if (glyph_id >= params.metric_count) { return; }
    let metric = glyph_metrics[glyph_id];
    if (params.direction == 3u || params.direction == 4u) {
        glyphs[index].advance_y = -metric.advance_y;
        glyphs[index].offset_x = -metric.origin_x;
        glyphs[index].offset_y = -metric.origin_y;
    } else {
        glyphs[index].advance_x = metric.advance_x;
    }
}

@compute @workgroup_size(64)
fn finalize_glyphs(@builtin(global_invocation_id) id: vec3<u32>) {
    let index = id.x;
    if (index >= run_state.glyph_count) { return; }
    // OpenType layout is evaluated in its native y-up design space. Public
    // shaping records use the renderer's y-down convention, matching the CPU
    // executor and HarfBuzz adapter boundary.
    let reverse = params.direction == 2u || params.direction == 4u;
    if (!reverse) {
        var value = glyphs[index];
        value.advance_y = -value.advance_y;
        value.offset_y = -value.offset_y;
        glyphs[index] = value;
        return;
    }
    let middle = (run_state.glyph_count + 1u) >> 1u;
    if (index >= middle) { return; }
    let partner = run_state.glyph_count - 1u - index;
    var first = glyphs[index];
    first.advance_y = -first.advance_y;
    first.offset_y = -first.offset_y;
    if (partner == index) {
        glyphs[index] = first;
        return;
    }
    var second = glyphs[partner];
    second.advance_y = -second.advance_y;
    second.offset_y = -second.offset_y;
    glyphs[index] = second;
    glyphs[partner] = first;
}
