// Algorithm: initialize through a compressed nominal cmap, preprocess Unicode and deterministic complex-script syllables, execute stage-aware OpenType substitutions with Indic/USE/Myanmar/Khmer reordering plus bounded Arabic presentation fallback/stretch, load metrics, execute GPOS or extent-based fallback mark positioning, then finalize output order.
// Time complexity: O(N*(log R + log V + log U + log D + log Q + log K) + S*N + L*N*log C) for typical bounded combining runs, and O(N^2 + S*N + L*N*log C) worst-case when all N scalars form one reverse-ordered combining run; R is cmap ranges, V variation-selector ranges, U Unicode-property ranges, D directional mappings, Q normalization records, K invalid-vowel constraints, S selected deterministic syllable-machine passes (at most one), L ordered ranged lookups, and C coverage size. Initialization, metrics, and output conversion are parallel while order-changing preprocessing and lookup mutation are serial.
// Space complexity: O(N + R + V + U + D + Q + K + M + G + L) storage for glyphs plus stable internal identities, cmap/variation/packed Unicode normalization and vowel-constraint data, M generated Indic/USE/Myanmar/Khmer transition words, G metrics and optional extents, and lookup commands; each invocation uses O(1) private storage and no textures.
// Workgroups contain 64 independent glyph invocations; Unicode preprocessing and the ordered lookup/position VMs use one invocation because they mutate shared order. The Arabic joining machine has 42 fixed transitions (168 bytes of private state), and each stch run is capped at 256 generated components inside the preallocated run capacity; the generated complex-script machines use uploaded state/category matrices with 256 fixed category entries per state. Runtime loops are bounded by uploaded counts/capacity and OpenType table counts. Lookup flags use GDEF glyph/mark classes and mark-set coverage without auxiliary allocations. All arithmetic is exact 32-bit integer design-unit arithmetic.

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
    variation_mapping_count: u32,
    reserved1: u32,
    reserved2: u32,
    reserved3: u32,
    reserved4: u32,
    reserved5: u32,
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

struct GlyphExtents {
    x_bearing: i32,
    y_bearing: i32,
    width: i32,
    height: i32,
    is_valid: u32,
};

struct LayoutVariationDelta {
    key: u32,
    delta: f32,
};

struct VariationMapping {
    start: u32,
    end: u32,
    selector: u32,
    glyph: u32,
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
    stage: u32,
};

struct LookupTask {
    lookup_index: u32,
    target_serial: u32,
    origin_position: u32,
    sequence_index: u32,
    context_lookup_offset: u32,
    context_lookup_flags: u32,
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
@group(0) @binding(11) var<storage, read> variation_mappings: array<VariationMapping>;
@group(0) @binding(12) var<storage, read> unicode_data: array<u32>;
@group(0) @binding(13) var<storage, read> glyph_extents: array<GlyphExtents>;

const FEATURE_EXPLICIT: u32 = 1u;
// Lookup behavior mirrors HarfBuzz matcher semantics: syllable limits apply only
// to GSUB; ZWNJ is never skipped in input, while ZWJ skipping is feature-controlled.
const FEATURE_PER_SYLLABLE: u32 = 2u;
const FEATURE_MANUAL_ZWNJ: u32 = 4u;
const FEATURE_MANUAL_ZWJ: u32 = 8u;
const FEATURE_GPOS_MATCH: u32 = 16u;
const GLYPH_SUBSTITUTED: u32 = 2u;
const GLYPH_INVISIBLE: u32 = 4u;
const GLYPH_SPLIT_MATRA_COMPONENT: u32 = 32u;
const FRACTION_NUMERATOR: u32 = 1u;
const FRACTION_DENOMINATOR: u32 = 2u;
const FRACTION_SLASH: u32 = 4u;
const ARABIC_ACTION_SHIFT: u32 = 8u;
const ARABIC_ACTION_MASK: u32 = 7u << ARABIC_ACTION_SHIFT;
const ARABIC_ISOLATED: u32 = 0u;
const ARABIC_FINAL: u32 = 1u;
const ARABIC_FINAL2: u32 = 2u;
const ARABIC_FINAL3: u32 = 3u;
const ARABIC_MEDIAL: u32 = 4u;
const ARABIC_MEDIAL2: u32 = 5u;
const ARABIC_INITIAL: u32 = 6u;
const ARABIC_NONE: u32 = 7u;
const ARABIC_STRETCH_FIXED: u32 = 1u << 20u;
const ARABIC_STRETCH_REPEATING: u32 = 1u << 21u;
const HANGUL_LJMO: u32 = 1u << 16u;
const HANGUL_VJMO: u32 = 1u << 17u;
const HANGUL_TJMO: u32 = 1u << 18u;
const HANGUL_FEATURE_MASK: u32 = HANGUL_LJMO | HANGUL_VJMO | HANGUL_TJMO;
const KHMER_PREF: u32 = 1u << 20u;
const KHMER_POST_BASE: u32 = 1u << 21u;
const KHMER_CFAR: u32 = 1u << 22u;
const INDIC_POSITION_SHIFT: u32 = 8u;
const INDIC_POSITION_MASK: u32 = 0xffu << INDIC_POSITION_SHIFT;
const USE_RPHF_ELIGIBLE: u32 = 1u << 16u;
const USE_CATEGORY_SHIFT: u32 = 24u;

fn ligature_component(position: u32) -> u32 {
    return glyph_states[position].ligature_component & 0xffffu;
}

// Preserve HarfBuzz's independent byte-sized ligature-component count and
// multiple-substitution component in the existing 32-byte GlyphState record.
fn ligature_component_count(position: u32) -> u32 {
    return (glyph_states[position].ligature_component >> 16u) & 0xffu;
}

fn multiple_substitution_component(position: u32) -> u32 {
    return glyph_states[position].ligature_component >> 24u;
}

fn set_ligature_component(position: u32, component: u32) {
    glyph_states[position].ligature_component =
        (glyph_states[position].ligature_component & 0xffff0000u) | (component & 0xffffu);
}

fn set_ligature_component_count(position: u32, count: u32) {
    glyph_states[position].ligature_component =
        (glyph_states[position].ligature_component & 0xff00ffffu) | ((count & 0xffu) << 16u);
}

fn set_multiple_substitution_component(position: u32, component: u32) {
    glyph_states[position].ligature_component =
        (glyph_states[position].ligature_component & 0x00ffffffu) | ((component & 0xffu) << 24u);
}
const USE_CATEGORY_MASK: u32 = 0xffu << USE_CATEGORY_SHIFT;
const GLYPH_MULTIPLE_COMPONENT: u32 = 8u;
const GLYPH_MULTIPLIED: u32 = 16u;
const INDIC_RPHF: u32 = 1u << 24u;
const INDIC_PREF: u32 = 1u << 25u;
const INDIC_BLWF: u32 = 1u << 26u;
const INDIC_ABVF: u32 = 1u << 27u;
const INDIC_HALF: u32 = 1u << 28u;
const INDIC_PSTF: u32 = 1u << 29u;
const INDIC_INIT: u32 = 1u << 30u;
var<private> arabic_state_table: array<u32, 42> = array<u32, 42>(
    0x00000707u, 0x00020007u, 0x00010007u, 0x00020007u, 0x00010007u, 0x00060007u,
    0x00000707u, 0x00020007u, 0x00010007u, 0x00020007u, 0x00050207u, 0x00060007u,
    0x00000707u, 0x00020007u, 0x00010106u, 0x00030106u, 0x00040106u, 0x00060106u,
    0x00000707u, 0x00020007u, 0x00010104u, 0x00030104u, 0x00040104u, 0x00060104u,
    0x00000707u, 0x00020007u, 0x00010005u, 0x00020005u, 0x00050205u, 0x00060005u,
    0x00000707u, 0x00020007u, 0x00010000u, 0x00020000u, 0x00050200u, 0x00060000u,
    0x00000707u, 0x00020007u, 0x00010007u, 0x00020007u, 0x00050307u, 0x00060007u);
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

fn is_default_ignorable(codepoint: u32) -> bool {
    return codepoint == 0x00adu || codepoint == 0x034fu || codepoint == 0x061cu ||
        codepoint == 0x115fu || codepoint == 0x1160u || codepoint == 0x17b4u || codepoint == 0x17b5u ||
        (codepoint >= 0x180bu && codepoint <= 0x180fu) ||
        (codepoint >= 0x200bu && codepoint <= 0x200fu) ||
        (codepoint >= 0x202au && codepoint <= 0x202eu) ||
        (codepoint >= 0x2060u && codepoint <= 0x206fu) || codepoint == 0x3164u ||
        codepoint == 0xfeffu || codepoint == 0xffa0u ||
        (codepoint >= 0xfff0u && codepoint <= 0xfff8u) ||
        (codepoint >= 0xfe00u && codepoint <= 0xfe0fu) ||
        (codepoint >= 0x1bca0u && codepoint <= 0x1bcafu) ||
        (codepoint >= 0x1d173u && codepoint <= 0x1d17au) ||
        (codepoint >= 0xe0000u && codepoint <= 0xe0fffu);
}

fn is_variation_selector(codepoint: u32) -> bool {
    return (codepoint >= 0xfe00u && codepoint <= 0xfe0fu) ||
        (codepoint >= 0xe0100u && codepoint <= 0xe01efu);
}

fn variation_glyph(codepoint: u32, selector: u32) -> u32 {
    var low = 0u;
    var high = params.variation_mapping_count;
    loop {
        if (low >= high) { break; }
        let middle = low + ((high - low) >> 1u);
        let item = variation_mappings[middle];
        if (selector < item.selector || (selector == item.selector && codepoint < item.start)) {
            high = middle;
        } else if (selector > item.selector || codepoint > item.end) {
            low = middle + 1u;
        } else {
            return item.glyph;
        }
    }
    return 0xfffffffeu;
}

fn is_visible_shaping_control(codepoint: u32) -> bool {
    return codepoint == 0x200cu || (codepoint >= 0x180bu && codepoint <= 0x180eu) ||
        (codepoint >= 0xe0000u && codepoint <= 0xe007fu);
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

fn unicode_properties_a(codepoint: u32) -> u32 {
    var low = 0u;
    var high = unicode_data[1u];
    let base = unicode_data[2u];
    loop {
        if (low >= high) { break; }
        let middle = low + ((high - low) >> 1u);
        let record = base + middle * 4u;
        if (codepoint < unicode_data[record]) { high = middle; }
        else if (codepoint >= unicode_data[record + 1u]) { low = middle + 1u; }
        else { return unicode_data[record + 2u]; }
    }
    return 0u;
}

fn unicode_properties_b(codepoint: u32) -> u32 {
    var low = 0u;
    var high = unicode_data[1u];
    let base = unicode_data[2u];
    loop {
        if (low >= high) { break; }
        let middle = low + ((high - low) >> 1u);
        let record = base + middle * 4u;
        if (codepoint < unicode_data[record]) { high = middle; }
        else if (codepoint >= unicode_data[record + 1u]) { low = middle + 1u; }
        else { return unicode_data[record + 3u]; }
    }
    return 0u;
}

fn is_indic_syllable_script() -> bool {
    let script = params.script_tag;
    return script == 0x62656e67u || script == 0x626e6732u ||
        script == 0x64657661u || script == 0x64657632u ||
        script == 0x67756a72u || script == 0x676a7232u ||
        script == 0x67757275u || script == 0x67757232u ||
        script == 0x6b6e6461u || script == 0x6b6e6432u ||
        script == 0x6d6c796du || script == 0x6d6c6d32u ||
        script == 0x6f727961u || script == 0x6f727932u ||
        script == 0x74616d6cu || script == 0x746d6c32u ||
        script == 0x74656c75u || script == 0x74656c32u;
}

fn is_use_syllable_script() -> bool {
    let script = params.script_tag;
    return script == 0x626e6733u || script == 0x64657633u ||
        script == 0x676a7233u || script == 0x67757233u ||
        script == 0x6b6e6433u || script == 0x6d6c6d33u ||
        script == 0x6f727933u || script == 0x746d6c33u || script == 0x74656c33u ||
        script == 0x74696274u || script == 0x6d6f6e67u || script == 0x73696e68u ||
        script == 0x6a617661u || script == 0x6d617263u || script == 0x6c696d62u ||
        script == 0x74616c65u || script == 0x62756769u || script == 0x6b686172u ||
        script == 0x73796c6fu || script == 0x74666e67u || script == 0x62616c69u ||
        script == 0x6e6b6f6fu || script == 0x70686167u || script == 0x6368616du ||
        script == 0x6b616c69u || script == 0x6c657063u || script == 0x726a6e67u ||
        script == 0x73617572u || script == 0x73756e64u || script == 0x65677970u ||
        script == 0x6b746869u || script == 0x6d746569u || script == 0x6c616e61u ||
        script == 0x74617674u || script == 0x6261746bu || script == 0x62726168u ||
        script == 0x6d616e64u || script == 0x63616b6du || script == 0x706c7264u ||
        script == 0x73687264u || script == 0x74616b72u || script == 0x6475706cu ||
        script == 0x6772616eu || script == 0x6b686f6au || script == 0x73696e64u ||
        script == 0x6d61686au || script == 0x6d616e69u || script == 0x6d6f6469u ||
        script == 0x686d6e67u || script == 0x70686c70u || script == 0x73696464u ||
        script == 0x74697268u || script == 0x61686f6du || script == 0x6d756c74u ||
        script == 0x61646c6du || script == 0x62686b73u || script == 0x6e657761u ||
        script == 0x676f6e6du || script == 0x736f796fu || script == 0x7a616e62u ||
        script == 0x646f6772u || script == 0x676f6e67u || script == 0x726f6867u ||
        script == 0x6d616b61u || script == 0x6d656466u || script == 0x736f676fu ||
        script == 0x736f6764u || script == 0x656c796du || script == 0x6e616e64u ||
        script == 0x686d6e70u || script == 0x7763686fu || script == 0x63687273u ||
        script == 0x6469616bu || script == 0x6b697473u || script == 0x79657a69u ||
        script == 0x63706d6eu || script == 0x6f756772u || script == 0x746e7361u ||
        script == 0x746f746fu || script == 0x76697468u || script == 0x6b617769u ||
        script == 0x6e61676du;
}

fn syllable_machine_descriptor(machine: u32) -> u32 {
    if (machine >= unicode_data[11u]) { return 0xffffffffu; }
    return unicode_data[12u] + machine * 7u;
}

fn syllable_machine_transition(descriptor: u32, state: u32, category: u32) -> u32 {
    let state_count = unicode_data[descriptor + 2u];
    if (state >= state_count) { return 0xffffffffu; }
    return unicode_data[unicode_data[descriptor + 3u] + state * 256u + min(category, 255u)];
}

fn syllable_machine_eof(descriptor: u32, state: u32) -> u32 {
    let state_count = unicode_data[descriptor + 2u];
    if (state >= state_count) { return 0xffffffffu; }
    return unicode_data[unicode_data[descriptor + 6u] + state];
}

fn syllable_machine_from_action(descriptor: u32, state: u32) -> u32 {
    return unicode_data[unicode_data[descriptor + 5u] + state];
}

fn syllable_machine_to_action(descriptor: u32, state: u32) -> u32 {
    return unicode_data[unicode_data[descriptor + 4u] + state];
}

fn syllable_machine_token_start_action(machine: u32) -> u32 {
    if (machine == 0u) { return 10u; }
    if (machine == 1u) { return 3u; }
    if (machine == 2u) { return 2u; }
    return 7u;
}

fn syllable_machine_token_clear_action(machine: u32) -> u32 {
    if (machine == 0u) { return 9u; }
    if (machine == 1u) { return 2u; }
    if (machine == 2u) { return 1u; }
    return 6u;
}

fn use_machine_includes(position: u32) -> bool {
    let category = unicode_properties_b(glyphs[position].codepoint) & 0xffu;
    if (category == 6u) { return false; } // CGJ
    if (category != 14u) { return true; } // ZWNJ
    var following = position + 1u;
    while (following < run_state.glyph_count &&
            (unicode_properties_b(glyphs[following].codepoint) & 0xffu) == 6u) {
        following += 1u;
    }
    return following >= run_state.glyph_count ||
        (unicode_properties_b(glyphs[following].codepoint) & 0x100u) == 0u;
}

fn next_syllable_machine_position(machine: u32, position: u32) -> u32 {
    var next = position + 1u;
    if (machine != 1u) { return min(next, run_state.glyph_count); }
    while (next < run_state.glyph_count && !use_machine_includes(next)) { next += 1u; }
    return next;
}

fn first_syllable_machine_position(machine: u32) -> u32 {
    if (machine != 1u) { return 0u; }
    var position = 0u;
    while (position < run_state.glyph_count && !use_machine_includes(position)) { position += 1u; }
    return position;
}

fn syllable_category(machine: u32, position: u32) -> u32 {
    if (machine == 1u) { return unicode_properties_b(glyphs[position].codepoint) & 0xffu; }
    return (unicode_properties_a(glyphs[position].codepoint) >> 16u) & 0xffu;
}

fn assign_syllable(start: u32, end: u32, syllable_type: u32, serial: ptr<function, u32>) {
    if (start == 0xffffffffu || end < start) { return; }
    let value = ((*serial & 0x0fu) << 4u) | (syllable_type & 0x0fu);
    for (var index = start; index < end; index++) { glyph_states[index].syllable = value; }
    *serial += 1u;
    if (*serial == 16u) { *serial = 1u; }
}

fn run_syllable_machine(machine: u32) {
    if (run_state.glyph_count == 0u) { return; }
    let descriptor = syllable_machine_descriptor(machine);
    if (descriptor == 0xffffffffu) { run_state.status = 20u; return; }
    var state = unicode_data[descriptor + 1u];
    var position = first_syllable_machine_position(machine);
    var token_start = 0xffffffffu;
    var token_end = 0u;
    var pending_action = 0u;
    var serial = 1u;
    var steps = 0u;
    loop {
        steps += 1u;
        if (steps > (run_state.glyph_count + 1u) * 32u + 256u) {
            run_state.status = 21u;
            return;
        }
        let at_eof = position >= run_state.glyph_count;
        var transition = 0xffffffffu;
        if (at_eof) {
            transition = syllable_machine_eof(descriptor, state);
            if (transition == 0xffffffffu) { break; }
        } else {
            if (syllable_machine_from_action(descriptor, state) ==
                    syllable_machine_token_start_action(machine)) {
                token_start = position;
            }
            transition = syllable_machine_transition(descriptor, state, syllable_category(machine, position));
        }
        state = transition & 0xffffu;
        let action = transition >> 16u;
        let next_position = select(position, next_syllable_machine_position(machine, position), !at_eof);
        var resume = next_position;

        if (machine == 0u) {
            if (action == 2u) { token_end = next_position; }
            else if (action == 11u) { token_end = next_position; assign_syllable(token_start, token_end, 5u, &serial); }
            else if (action == 14u) { token_end = position; assign_syllable(token_start, token_end, 0u, &serial); resume = position; }
            else if (action == 15u) { token_end = position; assign_syllable(token_start, token_end, 1u, &serial); resume = position; }
            else if (action == 18u) { token_end = position; assign_syllable(token_start, token_end, 2u, &serial); resume = position; }
            else if (action == 20u) { token_end = position; assign_syllable(token_start, token_end, 3u, &serial); resume = position; }
            else if (action == 16u) { token_end = position; assign_syllable(token_start, token_end, 4u, &serial); resume = position; }
            else if (action == 17u) { token_end = position; assign_syllable(token_start, token_end, 5u, &serial); resume = position; }
            else if (action == 1u) { assign_syllable(token_start, token_end, 0u, &serial); resume = token_end; }
            else if (action == 3u) { assign_syllable(token_start, token_end, 1u, &serial); resume = token_end; }
            else if (action == 7u) { assign_syllable(token_start, token_end, 2u, &serial); resume = token_end; }
            else if (action == 8u) { assign_syllable(token_start, token_end, 3u, &serial); resume = token_end; }
            else if (action == 4u) { assign_syllable(token_start, token_end, 4u, &serial); resume = token_end; }
            else if (action == 6u) {
                var syllable_type = 5u;
                if (pending_action == 1u) { syllable_type = 0u; }
                else if (pending_action == 6u) { syllable_type = 4u; }
                assign_syllable(token_start, token_end, syllable_type, &serial); resume = token_end;
            } else if (action == 19u) { token_end = next_position; pending_action = 1u; }
            else if (action == 13u) { token_end = next_position; pending_action = 5u; }
            else if (action == 5u) { token_end = next_position; pending_action = 6u; }
            else if (action == 12u) { token_end = next_position; pending_action = 7u; }
        } else if (machine == 1u) {
            if (action == 7u) { token_end = next_position; }
            else if (action == 16u) { token_end = next_position; assign_syllable(token_start, token_end, 0u, &serial); }
            else if (action == 14u) { token_end = next_position; assign_syllable(token_start, token_end, 1u, &serial); }
            else if (action == 12u) { token_end = next_position; assign_syllable(token_start, token_end, 2u, &serial); }
            else if (action == 20u) { token_end = next_position; assign_syllable(token_start, token_end, 3u, &serial); }
            else if (action == 18u) { token_end = next_position; assign_syllable(token_start, token_end, 4u, &serial); }
            else if (action == 10u) { token_end = next_position; assign_syllable(token_start, token_end, 5u, &serial); }
            else if (action == 25u) { token_end = next_position; assign_syllable(token_start, token_end, 6u, &serial); }
            else if (action == 5u) { token_end = next_position; assign_syllable(token_start, token_end, 7u, &serial); }
            else if (action == 4u) { token_end = next_position; assign_syllable(token_start, token_end, 8u, &serial); }
            else if (action == 15u) { token_end = position; assign_syllable(token_start, token_end, 0u, &serial); resume = position; }
            else if (action == 13u) { token_end = position; assign_syllable(token_start, token_end, 1u, &serial); resume = position; }
            else if (action == 11u) { token_end = position; assign_syllable(token_start, token_end, 2u, &serial); resume = position; }
            else if (action == 19u) { token_end = position; assign_syllable(token_start, token_end, 3u, &serial); resume = position; }
            else if (action == 17u) { token_end = position; assign_syllable(token_start, token_end, 4u, &serial); resume = position; }
            else if (action == 9u) { token_end = position; assign_syllable(token_start, token_end, 5u, &serial); resume = position; }
            else if (action == 24u) { token_end = position; assign_syllable(token_start, token_end, 6u, &serial); resume = position; }
            else if (action == 21u) { token_end = position; assign_syllable(token_start, token_end, 7u, &serial); resume = position; }
            else if (action == 23u) { token_end = position; assign_syllable(token_start, token_end, 8u, &serial); resume = position; }
            else if (action == 1u) { assign_syllable(token_start, token_end, 5u, &serial); resume = token_end; }
            else if (action == 22u) { assign_syllable(token_start, token_end, select(8u, 7u, pending_action == 9u), &serial); resume = token_end; }
            else if (action == 6u) { token_end = next_position; pending_action = 8u; }
            else if (action == 8u) { token_end = next_position; pending_action = 9u; }
        } else if (machine == 2u) {
            if (action == 8u) { token_end = next_position; assign_syllable(token_start, token_end, 0u, &serial); }
            else if (action == 4u || action == 3u) { token_end = next_position; assign_syllable(token_start, token_end, 2u, &serial); }
            else if (action == 10u) { token_end = next_position; assign_syllable(token_start, token_end, 1u, &serial); }
            else if (action == 7u) { token_end = position; assign_syllable(token_start, token_end, 0u, &serial); resume = position; }
            else if (action == 9u) { token_end = position; assign_syllable(token_start, token_end, 1u, &serial); resume = position; }
            else if (action == 12u) { token_end = position; assign_syllable(token_start, token_end, 2u, &serial); resume = position; }
            else if (action == 11u) { assign_syllable(token_start, token_end, select(1u, 2u, pending_action == 2u), &serial); resume = token_end; }
            else if (action == 6u) { token_end = next_position; pending_action = 2u; }
            else if (action == 5u) { token_end = next_position; pending_action = 3u; }
        } else {
            if (action == 2u) { token_end = next_position; }
            else if (action == 8u) { token_end = next_position; assign_syllable(token_start, token_end, 2u, &serial); }
            else if (action == 10u) { token_end = position; assign_syllable(token_start, token_end, 0u, &serial); resume = position; }
            else if (action == 11u) { token_end = position; assign_syllable(token_start, token_end, 1u, &serial); resume = position; }
            else if (action == 12u) { token_end = position; assign_syllable(token_start, token_end, 2u, &serial); resume = position; }
            else if (action == 1u) { assign_syllable(token_start, token_end, 0u, &serial); resume = token_end; }
            else if (action == 3u) { assign_syllable(token_start, token_end, 1u, &serial); resume = token_end; }
            else if (action == 5u) { assign_syllable(token_start, token_end, select(2u, 1u, pending_action == 2u), &serial); resume = token_end; }
            else if (action == 4u) { token_end = next_position; pending_action = 2u; }
            else if (action == 9u) { token_end = next_position; pending_action = 3u; }
        }
        if (syllable_machine_to_action(descriptor, state) ==
                syllable_machine_token_clear_action(machine)) {
            token_start = 0xffffffffu;
        }
        position = resume;
    }
}

fn prepare_complex_syllables() {
    if (is_indic_syllable_script()) { run_syllable_machine(0u); }
    else if (is_use_syllable_script()) { run_syllable_machine(1u); }
    else if (params.script_tag == 0x6d796d72u || params.script_tag == 0x6d796d32u) {
        run_syllable_machine(2u);
    } else if (params.script_tag == 0x6b686d72u) { run_syllable_machine(3u); }
}

fn insert_broken_dotted_circles(broken_type: u32) {
    if ((params.request_flags & 0x10u) != 0u) { return; }
    if (nominal_glyph(0x25ccu) == 0u) { return; }
    var previous = 0u;
    var index = 0u;
    while (index < run_state.glyph_count) {
        let syllable = glyph_states[index].syllable;
        if (syllable == previous || (syllable & 0x0fu) != broken_type) {
            previous = syllable;
            index += 1u;
            continue;
        }
        previous = syllable;
        let cluster = glyphs[index].cluster;
        if (!insert_codepoint(index, 0x25ccu, cluster)) { return; }
        glyph_states[index].syllable = syllable;
        index += 2u;
    }
}

fn move_record_to_start(start: u32, position: u32) {
    if (position <= start) { return; }
    let value = glyphs[position];
    let value_state = glyph_states[position];
    var cursor = position;
    while (cursor > start) {
        glyphs[cursor] = glyphs[cursor - 1u];
        glyph_states[cursor] = glyph_states[cursor - 1u];
        cursor -= 1u;
    }
    glyphs[start] = value;
    glyph_states[start] = value_state;
}

fn move_pair_to_start(start: u32, position: u32) {
    if (position <= start) { return; }
    let first = glyphs[position];
    let first_state = glyph_states[position];
    let second = glyphs[position + 1u];
    let second_state = glyph_states[position + 1u];
    var cursor = position;
    while (cursor > start) {
        glyphs[cursor + 1u] = glyphs[cursor - 1u];
        glyph_states[cursor + 1u] = glyph_states[cursor - 1u];
        cursor -= 1u;
    }
    glyphs[start] = first;
    glyph_states[start] = first_state;
    glyphs[start + 1u] = second;
    glyph_states[start + 1u] = second_state;
}

fn prepare_khmer_syllable(start: u32, end: u32) {
    if (end <= start + 1u) { return; }
    for (var position = start + 1u; position < end; position++) {
        glyph_states[position].feature_mask |= KHMER_POST_BASE;
    }
    var coeng_count = 0u;
    var position = start + 1u;
    while (position < end) {
        let category = (unicode_properties_a(glyphs[position].codepoint) >> 16u) & 0xffu;
        if (category == 4u && coeng_count <= 2u && position + 1u < end) {
            coeng_count += 1u;
            let next_category = (unicode_properties_a(glyphs[position + 1u].codepoint) >> 16u) & 0xffu;
            if (next_category == 15u) {
                glyph_states[position].feature_mask |= KHMER_PREF;
                glyph_states[position + 1u].feature_mask |= KHMER_PREF;
                _ = merge_cluster(start, position + 2u);
                move_pair_to_start(start, position);
                for (var following = position + 2u; following < end; following++) {
                    glyph_states[following].feature_mask |= KHMER_CFAR;
                }
                coeng_count = 2u;
            }
        } else if (category == 22u) {
            glyphs[position].cluster = merge_cluster(start, position + 1u);
            move_record_to_start(start, position);
        }
        position += 1u;
    }
}

fn prepare_khmer_reordering() {
    if (params.script_tag != 0x6b686d72u) { return; }
    insert_broken_dotted_circles(1u);
    if (run_state.status != 0u) { return; }
    var start = 0u;
    while (start < run_state.glyph_count) {
        let syllable = glyph_states[start].syllable;
        var end = start + 1u;
        while (end < run_state.glyph_count && glyph_states[end].syllable == syllable) { end += 1u; }
        let syllable_type = syllable & 0x0fu;
        if (syllable_type == 0u || syllable_type == 1u) { prepare_khmer_syllable(start, end); }
        start = end;
    }
}

fn indic_category(position: u32) -> u32 {
    if (is_indic_syllable_script()) {
        return (glyph_states[position].internal_flags & USE_CATEGORY_MASK) >> USE_CATEGORY_SHIFT;
    }
    return (unicode_properties_a(glyphs[position].codepoint) >> 16u) & 0xffu;
}

fn initialize_indic_shaping_state() {
    if (!is_indic_syllable_script()) { return; }
    for (var position = 0u; position < run_state.glyph_count; position++) {
        let properties = unicode_properties_a(glyphs[position].codepoint);
        set_use_category(position, (properties >> 16u) & 0xffu);
        set_indic_position(position, (properties >> 24u) & 0xffu);
    }
}

fn set_indic_position(position: u32, value: u32) {
    glyph_states[position].internal_flags =
        (glyph_states[position].internal_flags & ~INDIC_POSITION_MASK) |
        ((value & 0xffu) << INDIC_POSITION_SHIFT);
}

fn get_indic_position(position: u32) -> u32 {
    return (glyph_states[position].internal_flags & INDIC_POSITION_MASK) >> INDIC_POSITION_SHIFT;
}

fn is_myanmar_consonant(position: u32) -> bool {
    if (glyph_states[position].ligature_id != 0u) { return false; }
    let category = indic_category(position);
    return category == 1u || category == 18u || category == 15u || category == 2u ||
        category == 10u || category == 11u;
}

fn move_record(position: u32, destination: u32) {
    if (position == destination) { return; }
    let value = glyphs[position];
    let value_state = glyph_states[position];
    if (destination < position) {
        var cursor = position;
        while (cursor > destination) {
            glyphs[cursor] = glyphs[cursor - 1u];
            glyph_states[cursor] = glyph_states[cursor - 1u];
            cursor -= 1u;
        }
    } else {
        var cursor = position;
        while (cursor < destination) {
            glyphs[cursor] = glyphs[cursor + 1u];
            glyph_states[cursor] = glyph_states[cursor + 1u];
            cursor += 1u;
        }
    }
    glyphs[destination] = value;
    glyph_states[destination] = value_state;
}

fn stable_sort_indic_positions(start: u32, end: u32) {
    var write = start;
    for (var desired = 0u; desired <= 14u; desired++) {
        var scan = write;
        while (scan < end) {
            if (min(get_indic_position(scan), 14u) == desired) {
                move_record(scan, write);
                write += 1u;
            }
            scan += 1u;
        }
    }
}

fn reorder_myanmar_syllable(start: u32, end: u32) {
    let has_reph = end >= start + 3u && indic_category(start) == 15u &&
        indic_category(start + 1u) == 32u && indic_category(start + 2u) == 4u;
    let limit = select(start, start + 3u, has_reph);
    var base_index = select(limit, start, has_reph);
    if (!has_reph) {
        for (var position = limit; position < end; position++) {
            if (is_myanmar_consonant(position)) { base_index = position; break; }
        }
    }
    var cursor = start;
    let reph_end = start + select(0u, 3u, has_reph);
    while (cursor < reph_end) { set_indic_position(cursor, 5u); cursor += 1u; }
    while (cursor < base_index) { set_indic_position(cursor, 3u); cursor += 1u; }
    if (cursor < end) { set_indic_position(cursor, 4u); cursor += 1u; }
    var current_position = 5u;
    while (cursor < end) {
        let category = indic_category(cursor);
        if (category == 36u) { set_indic_position(cursor, 3u); cursor += 1u; continue; }
        if (category == 22u) { set_indic_position(cursor, 2u); cursor += 1u; continue; }
        if (category == 40u) {
            set_indic_position(cursor, get_indic_position(cursor - 1u));
            cursor += 1u;
            continue;
        }
        if (current_position == 5u && category == 21u) {
            current_position = 8u;
            set_indic_position(cursor, current_position);
            cursor += 1u;
            continue;
        }
        if (current_position == 8u && category == 9u) {
            set_indic_position(cursor, 7u);
            cursor += 1u;
            continue;
        }
        if (current_position == 8u && category == 21u) {
            set_indic_position(cursor, current_position);
            cursor += 1u;
            continue;
        }
        if (current_position == 8u && category != 9u) { current_position = 9u; }
        set_indic_position(cursor, current_position);
        cursor += 1u;
    }
    _ = merge_cluster(start, end);
    stable_sort_indic_positions(start, end);

    var first_left = end;
    var last_left = end;
    for (var position = start; position < end; position++) {
        if (get_indic_position(position) != 2u) { continue; }
        if (first_left == end) { first_left = position; }
        last_left = position;
    }
    if (first_left < last_left) {
        reverse_records(first_left, last_left + 1u);
        var segment_start = first_left;
        for (var position = segment_start; position <= last_left; position++) {
            if (indic_category(position) != 22u) { continue; }
            reverse_records(segment_start, position + 1u);
            segment_start = position + 1u;
        }
    }
}

fn reorder_myanmar() {
    if (params.script_tag != 0x6d796d72u && params.script_tag != 0x6d796d32u) { return; }
    insert_broken_dotted_circles(1u);
    if (run_state.status != 0u) { return; }
    var start = 0u;
    while (start < run_state.glyph_count) {
        let syllable = glyph_states[start].syllable;
        var end = start + 1u;
        while (end < run_state.glyph_count && glyph_states[end].syllable == syllable) { end += 1u; }
        let syllable_type = syllable & 0x0fu;
        if (syllable_type == 0u || syllable_type == 1u) { reorder_myanmar_syllable(start, end); }
        start = end;
    }
}

fn clear_syllables() {
    for (var position = 0u; position < run_state.glyph_count; position++) {
        glyph_states[position].syllable = 0u;
    }
}

fn use_category(position: u32) -> u32 {
    return (glyph_states[position].internal_flags & USE_CATEGORY_MASK) >> USE_CATEGORY_SHIFT;
}

fn set_use_category(position: u32, category: u32) {
    glyph_states[position].internal_flags =
        (glyph_states[position].internal_flags & ~USE_CATEGORY_MASK) |
        ((category & 0xffu) << USE_CATEGORY_SHIFT);
}

fn set_arabic_action_range(start: u32, end: u32, action: u32) {
    for (var position = start; position < end; position++) { set_arabic_action(position, action); }
}

fn initialize_use_shaping_state() {
    if (!is_use_syllable_script()) { return; }
    for (var position = 0u; position < run_state.glyph_count; position++) {
        set_use_category(position, unicode_properties_b(glyphs[position].codepoint) & 0xffu);
    }
    var start = 0u;
    while (start < run_state.glyph_count) {
        let syllable = glyph_states[start].syllable;
        var end = start + 1u;
        while (end < run_state.glyph_count && glyph_states[end].syllable == syllable) { end += 1u; }
        let limit = select(min(3u, end - start), 1u, use_category(start) == 18u);
        for (var position = start; position < start + limit; position++) {
            glyph_states[position].internal_flags |= USE_RPHF_ELIGIBLE;
        }
        start = end;
    }
    if (uses_arabic_joining()) { return; }
    var previous_start = 0u;
    var previous_form = ARABIC_NONE;
    start = 0u;
    while (start < run_state.glyph_count) {
        let syllable = glyph_states[start].syllable;
        var end = start + 1u;
        while (end < run_state.glyph_count && glyph_states[end].syllable == syllable) { end += 1u; }
        let syllable_type = syllable & 0x0fu;
        if (syllable_type == 6u || syllable_type == 8u) {
            previous_form = ARABIC_NONE;
        } else {
            let joins = previous_form == ARABIC_FINAL || previous_form == ARABIC_ISOLATED;
            if (joins) {
                previous_form = select(ARABIC_INITIAL, ARABIC_MEDIAL, previous_form == ARABIC_FINAL);
                set_arabic_action_range(previous_start, start, previous_form);
            }
            previous_form = select(ARABIC_ISOLATED, ARABIC_FINAL, joins);
            set_arabic_action_range(start, end, previous_form);
        }
        previous_start = start;
        start = end;
    }
}

fn clear_substitution_flags() {
    for (var position = 0u; position < run_state.glyph_count; position++) {
        glyph_states[position].internal_flags &= ~GLYPH_SUBSTITUTED;
    }
}

fn record_use_repha() {
    var start = 0u;
    while (start < run_state.glyph_count) {
        let syllable = glyph_states[start].syllable;
        var end = start + 1u;
        while (end < run_state.glyph_count && glyph_states[end].syllable == syllable) { end += 1u; }
        var position = start;
        while (position < end && (glyph_states[position].internal_flags & USE_RPHF_ELIGIBLE) != 0u) {
            if ((glyph_states[position].internal_flags & GLYPH_SUBSTITUTED) != 0u) {
                set_use_category(position, 18u);
                break;
            }
            position += 1u;
        }
        start = end;
    }
}

fn record_use_prebase() {
    var start = 0u;
    while (start < run_state.glyph_count) {
        let syllable = glyph_states[start].syllable;
        var end = start + 1u;
        while (end < run_state.glyph_count && glyph_states[end].syllable == syllable) { end += 1u; }
        for (var position = start; position < end; position++) {
            if ((glyph_states[position].internal_flags & GLYPH_SUBSTITUTED) != 0u) {
                set_use_category(position, 22u);
                break;
            }
        }
        start = end;
    }
}

fn is_use_postbase(category: u32) -> bool {
    return category == 24u || category == 25u || category == 26u ||
        category == 45u || category == 46u || category == 47u ||
        category == 27u || category == 28u || category == 29u || category == 30u ||
        category == 33u || category == 34u || category == 35u || category == 22u ||
        category == 37u || category == 38u || category == 39u || category == 23u;
}

fn is_use_halant(position: u32) -> bool {
    let category = use_category(position);
    return (category == 12u || category == 53u || category == 44u) &&
        glyph_states[position].ligature_id == 0u;
}

fn insert_broken_use_dotted_circles() {
    if ((params.request_flags & 0x10u) != 0u || nominal_glyph(0x25ccu) == 0u) { return; }
    var previous = 0u;
    var position = 0u;
    while (position < run_state.glyph_count) {
        let syllable = glyph_states[position].syllable;
        if (syllable == previous || (syllable & 0x0fu) != 7u) {
            previous = syllable;
            position += 1u;
            continue;
        }
        previous = syllable;
        let source_position = position;
        while (position < run_state.glyph_count && glyph_states[position].syllable == syllable &&
                use_category(position) == 18u) { position += 1u; }
        if (!insert_codepoint(position, 0x25ccu, glyphs[source_position].cluster)) { return; }
        glyph_states[position].syllable = syllable;
        set_use_category(position, 1u);
        set_arabic_action(position,
            (glyph_states[source_position].feature_mask & ARABIC_ACTION_MASK) >> ARABIC_ACTION_SHIFT);
        position += 1u;
    }
}

fn reorder_use_syllable(start: u32, end: u32, syllable_type: u32) {
    if (syllable_type != 0u && syllable_type != 1u && syllable_type != 2u &&
            syllable_type != 5u && syllable_type != 7u) { return; }
    if (use_category(start) == 18u && end > start + 1u) {
        for (var position = start + 1u; position < end; position++) {
            let postbase = is_use_postbase(use_category(position)) || is_use_halant(position);
            if (postbase || position == end - 1u) {
                let destination = select(position, position - 1u, postbase);
                _ = merge_cluster(start, destination + 1u);
                move_record(start, destination);
                break;
            }
        }
    }
    var target_position = start;
    var position = start;
    while (position < end) {
        let category = use_category(position);
        if (is_use_halant(position)) {
            target_position = position + 1u;
        } else if ((category == 22u || category == 23u) &&
                (glyph_states[position].internal_flags & GLYPH_MULTIPLE_COMPONENT) == 0u &&
                target_position < position) {
            _ = merge_cluster(target_position, position + 1u);
            move_record(position, target_position);
        }
        position += 1u;
    }
}

fn reorder_use() {
    insert_broken_use_dotted_circles();
    if (run_state.status != 0u) { return; }
    var start = 0u;
    while (start < run_state.glyph_count) {
        let syllable = glyph_states[start].syllable;
        var end = start + 1u;
        while (end < run_state.glyph_count && glyph_states[end].syllable == syllable) { end += 1u; }
        reorder_use_syllable(start, end, syllable & 0x0fu);
        start = end;
    }
}

fn indic_base_script() -> u32 {
    let script = params.script_tag;
    if (script == 0x626e6732u) { return 0x62656e67u; }
    if (script == 0x64657632u) { return 0x64657661u; }
    if (script == 0x676a7232u) { return 0x67756a72u; }
    if (script == 0x67757232u) { return 0x67757275u; }
    if (script == 0x6b6e6432u) { return 0x6b6e6461u; }
    if (script == 0x6d6c6d32u) { return 0x6d6c796du; }
    if (script == 0x6f727932u) { return 0x6f727961u; }
    if (script == 0x746d6c32u) { return 0x74616d6cu; }
    if (script == 0x74656c32u) { return 0x74656c75u; }
    return script;
}

fn indic_old_spec() -> bool {
    let script = params.script_tag;
    return script != 0x626e6732u && script != 0x64657632u && script != 0x676a7232u &&
        script != 0x67757232u && script != 0x6b6e6432u && script != 0x6d6c6d32u &&
        script != 0x6f727932u && script != 0x746d6c32u && script != 0x74656c32u;
}

fn is_indic_joiner(position: u32) -> bool {
    let category = indic_category(position);
    return glyph_states[position].ligature_id == 0u && (category == 5u || category == 6u);
}

fn is_indic_halant(position: u32) -> bool {
    return glyph_states[position].ligature_id == 0u && indic_category(position) == 4u;
}

fn is_indic_consonant(position: u32) -> bool {
    if (glyph_states[position].ligature_id != 0u) { return false; }
    let category = indic_category(position);
    return category == 1u || category == 18u || category == 15u || category == 16u ||
        category == 2u || category == 10u || category == 11u;
}

fn indic_ligature_subtable_matches(subtable: u32, first: u32, second: u32, third: u32,
        input_count: u32) -> bool {
    let covered = coverage_index(subtable + table_u16(subtable + 2u), first);
    if (covered < 0 || u32(covered) >= table_u16(subtable + 4u)) { return false; }
    let ligature_set = subtable + table_u16(subtable + 6u + u32(covered) * 2u);
    let count = table_u16(ligature_set);
    for (var index = 0u; index < count; index++) {
        let ligature = ligature_set + table_u16(ligature_set + 2u + index * 2u);
        let components = table_u16(ligature + 2u);
        if (components != input_count) { continue; }
        if (components >= 2u && table_u16(ligature + 4u) != second) { continue; }
        if (components >= 3u && table_u16(ligature + 6u) != third) { continue; }
        return true;
    }
    return false;
}

fn feature_would_substitute(tag: u32, first: u32, second: u32, third: u32, input_count: u32) -> bool {
    for (var command_index = 0u; command_index < params.lookup_count; command_index++) {
        let command = lookup_commands[command_index];
        if (command.table_kind != 1u || command.feature_tag != tag || command.feature_value == 0u) { continue; }
        let lookup = command.lookup_offset;
        var lookup_type = table_u16(lookup);
        let subtable_count = table_u16(lookup + 4u);
        for (var subtable_index = 0u; subtable_index < subtable_count; subtable_index++) {
            var subtable = lookup + table_u16(lookup + 6u + subtable_index * 2u);
            var effective_type = lookup_type;
            if (effective_type == 7u && table_u16(subtable) == 1u) {
                effective_type = table_u16(subtable + 2u);
                subtable += table_u32(subtable + 4u);
            }
            if (effective_type == 4u && table_u16(subtable) == 1u &&
                    indic_ligature_subtable_matches(subtable, first, second, third, input_count)) {
                return true;
            }
            if ((effective_type == 1u || effective_type == 2u || effective_type == 3u ||
                    effective_type == 5u || effective_type == 6u || effective_type == 8u) &&
                    coverage_index(subtable + table_u16(subtable + 2u), first) >= 0) {
                return true;
            }
        }
    }
    return false;
}

fn indic_virama() -> u32 {
    switch indic_base_script() {
        case 0x64657661u: { return 0x094du; }
        case 0x62656e67u: { return 0x09cdu; }
        case 0x67757275u: { return 0x0a4du; }
        case 0x67756a72u: { return 0x0acdu; }
        case 0x6f727961u: { return 0x0b4du; }
        case 0x74616d6cu: { return 0x0bcdu; }
        case 0x74656c75u: { return 0x0c4du; }
        case 0x6b6e6461u: { return 0x0ccdu; }
        case 0x6d6c796du: { return 0x0d4du; }
        default: { return 0u; }
    }
}

fn update_indic_consonant_positions() {
    let virama_glyph = nominal_glyph(indic_virama());
    if (virama_glyph == 0u) { return; }
    for (var position = 0u; position < run_state.glyph_count; position++) {
        if (get_indic_position(position) != 4u) { continue; }
        let glyph = glyphs[position].glyph_id;
        if (feature_would_substitute(0x626c7766u, virama_glyph, glyph, 0u, 2u) ||
                feature_would_substitute(0x626c7766u, glyph, virama_glyph, 0u, 2u) ||
                feature_would_substitute(0x76617475u, virama_glyph, glyph, 0u, 2u) ||
                feature_would_substitute(0x76617475u, glyph, virama_glyph, 0u, 2u)) {
            set_indic_position(position, 8u);
        } else if (feature_would_substitute(0x70737466u, virama_glyph, glyph, 0u, 2u) ||
                feature_would_substitute(0x70737466u, glyph, virama_glyph, 0u, 2u) ||
                feature_would_substitute(0x70726566u, virama_glyph, glyph, 0u, 2u) ||
                feature_would_substitute(0x70726566u, glyph, virama_glyph, 0u, 2u)) {
            set_indic_position(position, 11u);
        }
    }
}

fn insert_broken_indic_dotted_circles() {
    if ((params.request_flags & 0x10u) != 0u || nominal_glyph(0x25ccu) == 0u) { return; }
    var previous = 0u;
    var position = 0u;
    while (position < run_state.glyph_count) {
        let syllable = glyph_states[position].syllable;
        if (syllable == previous || (syllable & 0x0fu) != 4u) {
            previous = syllable;
            position += 1u;
            continue;
        }
        previous = syllable;
        let source = position;
        while (position < run_state.glyph_count && glyph_states[position].syllable == syllable &&
                indic_category(position) == 14u) { position += 1u; }
        if (!insert_codepoint(position, 0x25ccu, glyphs[source].cluster)) { return; }
        glyph_states[position].syllable = syllable;
        set_use_category(position, 11u);
        set_indic_position(position, 14u);
        position += 1u;
    }
}

fn attach_indic_mark_positions(start: u32, end: u32, base_index: u32) {
    var last_position = 0u;
    for (var position = start; position < end; position++) {
        let category = indic_category(position);
        if (is_indic_joiner(position) || category == 3u || category == 12u ||
                category == 16u || category == 4u) {
            var attached_position = last_position;
            if (category == 4u && attached_position == 2u) {
                var prior = position;
                while (prior > start) {
                    prior -= 1u;
                    if (get_indic_position(prior) != 2u) {
                        attached_position = get_indic_position(prior);
                        break;
                    }
                }
            }
            set_indic_position(position, attached_position);
        } else if (get_indic_position(position) != 13u) {
            if (category == 13u && position > start && indic_category(position - 1u) == 8u) {
                set_indic_position(position - 1u, get_indic_position(position));
            }
            last_position = get_indic_position(position);
        }
    }
    if (end == start) { return; }
    var last = min(base_index, end - 1u);
    var position = last + 1u;
    while (position < end) {
        if (is_indic_consonant(position)) {
            for (var mark = last + 1u; mark < position; mark++) {
                if (get_indic_position(mark) < 13u) {
                    set_indic_position(mark, get_indic_position(position));
                }
            }
            last = position;
        } else if (indic_category(position) == 7u || indic_category(position) == 13u) {
            last = position;
        }
        position += 1u;
    }
}

fn reverse_indic_left_matras(start: u32, end: u32) {
    var first = end;
    var last = end;
    for (var position = start; position < end; position++) {
        if (get_indic_position(position) == 4u) { break; }
        if (get_indic_position(position) != 2u) { continue; }
        if (first == end) { first = position; }
        last = position;
    }
    if (first >= last) { return; }
    reverse_records(first, last + 1u);
    var group_start = first;
    for (var position = first; position <= last; position++) {
        let category = indic_category(position);
        if (category != 7u && category != 13u) { continue; }
        reverse_records(group_start, position + 1u);
        group_start = position + 1u;
    }
}

fn find_indic_base(start: u32, end: u32) -> u32 {
    for (var position = start; position < end; position++) {
        if (get_indic_position(position) == 4u) { return position; }
    }
    return end;
}

fn merge_indic_sort_clusters(start: u32, end: u32, base_index: u32, old_spec: bool) {
    if (base_index >= end) { return; }
    if (old_spec || end - start > 127u) { _ = merge_cluster(base_index, end); return; }
    for (var position = base_index; position < end; position++) {
        if ((glyph_states[position].attachment_type & 0xffu) == 0xffu) { continue; }
        var minimum = position;
        var maximum = position;
        var cursor = start + (glyph_states[position].attachment_type & 0xffu);
        var steps = 0u;
        while (cursor != position && cursor >= start && cursor < end && steps <= end - start) {
            minimum = min(minimum, cursor);
            maximum = max(maximum, cursor);
            let next = start + (glyph_states[cursor].attachment_type & 0xffu);
            glyph_states[cursor].attachment_type =
                (glyph_states[cursor].attachment_type & 0xffffff00u) | 0xffu;
            cursor = next;
            steps += 1u;
        }
        _ = merge_cluster(max(base_index, minimum), maximum + 1u);
    }
}

fn add_indic_mask(position: u32, mask: u32) { glyph_states[position].feature_mask |= mask; }
fn remove_indic_mask(position: u32, mask: u32) { glyph_states[position].feature_mask &= ~mask; }

fn setup_indic_feature_masks(start: u32, end: u32, base_index: u32, old_spec: bool) {
    var position = start;
    while (position < end && get_indic_position(position) == 1u) {
        add_indic_mask(position, INDIC_RPHF);
        position += 1u;
    }
    var pre_mask = INDIC_HALF;
    let script = indic_base_script();
    if (!old_spec && script != 0x74656c75u && script != 0x6b6e6461u) { pre_mask |= INDIC_BLWF; }
    for (position = start; position < base_index; position++) { add_indic_mask(position, pre_mask); }
    if (base_index < end) {
        for (position = base_index + 1u; position < end; position++) {
            add_indic_mask(position, INDIC_BLWF | INDIC_ABVF | INDIC_PSTF);
        }
    }
    if (base_index + 2u < end) {
        for (position = base_index + 1u; position + 1u < end; position++) {
            if (!feature_would_substitute(0x70726566u, glyphs[position].glyph_id,
                    glyphs[position + 1u].glyph_id, 0u, 2u)) { continue; }
            add_indic_mask(position, INDIC_PREF);
            add_indic_mask(position + 1u, INDIC_PREF);
            break;
        }
    }
    position = start + 1u;
    while (position < end) {
        if (indic_category(position) == 5u && is_indic_joiner(position)) {
            var prior = position;
            while (prior > start) {
                prior -= 1u;
                remove_indic_mask(prior, INDIC_HALF);
                if (is_indic_consonant(prior)) { break; }
            }
        }
        position += 1u;
    }
}

fn initial_reorder_indic_syllable(start: u32, end: u32, old_spec: bool) {
    let script = indic_base_script();
    if (script == 0x6b6e6461u && end >= start + 3u && indic_category(start) == 15u &&
            indic_category(start + 1u) == 4u && indic_category(start + 2u) == 6u) {
        _ = merge_cluster(start + 1u, start + 3u);
        let value = glyphs[start + 1u];
        let value_state = glyph_states[start + 1u];
        glyphs[start + 1u] = glyphs[start + 2u];
        glyph_states[start + 1u] = glyph_states[start + 2u];
        glyphs[start + 2u] = value;
        glyph_states[start + 2u] = value_state;
    }
    var limit = start;
    var base_index = end;
    var has_reph = false;
    let logical_reph = script == 0x6d6c796du;
    let explicit_reph = script == 0x74656c75u;
    if (end >= start + 3u &&
            ((!logical_reph && !explicit_reph && !is_indic_joiner(start + 2u)) ||
             (explicit_reph && indic_category(start + 2u) == 6u))) {
        let length = select(2u, 3u, explicit_reph);
        if (feature_would_substitute(0x72706866u, glyphs[start].glyph_id,
                glyphs[start + 1u].glyph_id, glyphs[start + 2u].glyph_id, length)) {
            limit += 2u;
            while (limit < end && is_indic_joiner(limit)) { limit += 1u; }
            base_index = start;
            has_reph = true;
        }
    } else if (logical_reph && indic_category(start) == 14u) {
        limit += 1u;
        while (limit < end && is_indic_joiner(limit)) { limit += 1u; }
        base_index = start;
        has_reph = true;
    }
    var seen_below = false;
    var reverse = end;
    while (reverse > limit) {
        reverse -= 1u;
        if (is_indic_consonant(reverse)) {
            let indic_position = get_indic_position(reverse);
            if (indic_position != 8u && (indic_position != 11u || seen_below)) {
                base_index = reverse;
                break;
            }
            if (indic_position == 8u) { seen_below = true; }
            base_index = reverse;
        } else if (reverse > start && indic_category(reverse) == 6u &&
                indic_category(reverse - 1u) == 4u) { break; }
    }
    if (has_reph && base_index == start && limit - base_index <= 2u) { has_reph = false; }
    for (var position = start; position < base_index; position++) {
        set_indic_position(position, min(3u, get_indic_position(position)));
    }
    if (base_index < end) { set_indic_position(base_index, 4u); }
    if (has_reph) { set_indic_position(start, 1u); }

    if (old_spec && base_index < end) {
        var position = base_index + 1u;
        while (position < end) {
            if (indic_category(position) != 4u) { position += 1u; continue; }
            var destination = end - 1u;
            while (destination > position && !is_indic_consonant(destination) &&
                    !(script == 0x6b6e6461u && indic_category(destination) == 4u)) {
                destination -= 1u;
            }
            if (indic_category(destination) != 4u && destination > position) {
                move_record(position, destination);
            }
            break;
        }
    }
    attach_indic_mark_positions(start, end, base_index);
    for (var position = start; position < end; position++) {
        glyph_states[position].attachment_type = min(position - start, 255u);
    }
    stable_sort_indic_positions(start, end);
    reverse_indic_left_matras(start, end);
    base_index = find_indic_base(start, end);
    merge_indic_sort_clusters(start, end, base_index, old_spec);
    for (var position = start; position < end; position++) {
        glyph_states[position].attachment_type &= 0xffffff00u;
    }
    setup_indic_feature_masks(start, end, base_index, old_spec);
}

fn initial_reorder_indic() {
    update_indic_consonant_positions();
    insert_broken_indic_dotted_circles();
    if (run_state.status != 0u) { return; }
    let old_spec = indic_old_spec();
    var start = 0u;
    while (start < run_state.glyph_count) {
        let syllable = glyph_states[start].syllable;
        var end = start + 1u;
        while (end < run_state.glyph_count && glyph_states[end].syllable == syllable) { end += 1u; }
        let syllable_type = syllable & 0x0fu;
        if (syllable_type == 0u || syllable_type == 1u || syllable_type == 2u || syllable_type == 4u) {
            initial_reorder_indic_syllable(start, end, old_spec);
        }
        start = end;
    }
}

fn indic_reph_position() -> u32 {
    switch indic_base_script() {
        case 0x62656e67u: { return 9u; }
        case 0x67757275u: { return 7u; }
        case 0x6f727961u, 0x6d6c796du: { return 5u; }
        case 0x74616d6cu, 0x74656c75u, 0x6b6e6461u: { return 12u; }
        default: { return 10u; }
    }
}

fn find_indic_reph_destination(start: u32, end: u32, base_index: u32) -> u32 {
    let desired = indic_reph_position();
    if (desired != 12u) {
        var explicit_halant = start + 1u;
        while (explicit_halant < base_index && !is_indic_halant(explicit_halant)) {
            explicit_halant += 1u;
        }
        if (explicit_halant < base_index) {
            if (explicit_halant + 1u < base_index && is_indic_joiner(explicit_halant + 1u)) {
                explicit_halant += 1u;
            }
            return explicit_halant;
        }
    }
    if (desired == 5u) {
        var destination = base_index;
        while (destination + 1u < end && get_indic_position(destination + 1u) <= 5u) {
            destination += 1u;
        }
        if (destination < end) { return destination; }
    }
    if (desired == 9u) {
        var destination = base_index;
        while (destination + 1u < end && get_indic_position(destination + 1u) != 11u &&
                get_indic_position(destination + 1u) != 12u &&
                get_indic_position(destination + 1u) != 13u) { destination += 1u; }
        if (destination < end) { return destination; }
    }
    var destination = end - 1u;
    while (destination > start && get_indic_position(destination) == 13u) { destination -= 1u; }
    if (is_indic_halant(destination)) {
        for (var position = base_index + 1u; position < destination; position++) {
            let category = indic_category(position);
            if (category == 7u || category == 13u) { destination -= 1u; break; }
        }
    }
    return destination;
}

fn is_indic_word_boundary(codepoint: u32) -> bool {
    let category = (unicode_properties_b(codepoint) >> 9u) & 0x1fu;
    return category == 11u || category == 12u || category == 13u || category == 14u ||
        category == 15u || (category >= 18u && category <= 24u);
}

fn final_reorder_indic_syllable(start: u32, end: u32) {
    if (start >= end) { return; }
    var reordered = false;
    let virama_glyph = nominal_glyph(indic_virama());
    if (virama_glyph != 0u) {
        for (var position = start; position < end; position++) {
            if (glyphs[position].glyph_id == virama_glyph && glyph_states[position].ligature_id != 0u &&
                    (glyph_states[position].internal_flags & GLYPH_MULTIPLIED) != 0u) {
                set_use_category(position, 4u);
                glyph_states[position].ligature_id = 0u;
                glyph_states[position].internal_flags &= ~GLYPH_MULTIPLIED;
            }
        }
    }
    var try_prebase = false;
    for (var position = start; position < end; position++) {
        if ((glyph_states[position].feature_mask & INDIC_PREF) != 0u) { try_prebase = true; break; }
    }
    var base_index = start;
    while (base_index < end && get_indic_position(base_index) < 4u) { base_index += 1u; }
    if (try_prebase && base_index + 1u < end) {
        for (var position = base_index + 1u; position < end; position++) {
            if ((glyph_states[position].feature_mask & INDIC_PREF) == 0u) { continue; }
            let formed = (glyph_states[position].internal_flags & GLYPH_SUBSTITUTED) != 0u &&
                glyph_states[position].ligature_id != 0u &&
                (glyph_states[position].internal_flags & GLYPH_MULTIPLIED) == 0u;
            if (!formed) {
                base_index = position;
                while (base_index < end && is_indic_halant(base_index)) { base_index += 1u; }
                if (base_index < end) { set_indic_position(base_index, 4u); }
                try_prebase = false;
            }
            break;
        }
    }
    if (indic_base_script() == 0x6d6c796du && base_index < end) {
        var position = base_index + 1u;
        while (position < end) {
            while (position < end && is_indic_joiner(position)) { position += 1u; }
            if (position == end || !is_indic_halant(position)) { break; }
            position += 1u;
            while (position < end && is_indic_joiner(position)) { position += 1u; }
            if (position < end && is_indic_consonant(position) && get_indic_position(position) == 8u) {
                base_index = position;
                set_indic_position(base_index, 4u);
            }
            position += 1u;
        }
    }
    if (base_index < end && base_index > start && get_indic_position(base_index) > 4u) {
        base_index -= 1u;
    }
    if (base_index == end && end > start && indic_category(end - 1u) == 6u) { base_index -= 1u; }
    while (base_index > start && base_index < end &&
            ((glyph_states[base_index].ligature_id == 0u && indic_category(base_index) == 3u) ||
             is_indic_halant(base_index))) { base_index -= 1u; }

    let script = indic_base_script();
    if (start + 1u < end && start < base_index) {
        var destination = select(base_index - 1u, base_index - 2u, base_index == end);
        if (script != 0x6d6c796du && script != 0x74616d6cu) {
            loop {
                while (destination > start && indic_category(destination) != 7u &&
                        indic_category(destination) != 13u && indic_category(destination) != 4u) {
                    destination -= 1u;
                }
                if (!is_indic_halant(destination) || get_indic_position(destination) == 2u) {
                    destination = start;
                    break;
                }
                if (destination + 1u < end && indic_category(destination + 1u) == 6u &&
                        destination > start) {
                    destination -= 1u;
                    continue;
                }
                break;
            }
        }
        if (destination > start && get_indic_position(destination) != 2u) {
            var position = destination;
            while (position > start) {
                if (get_indic_position(position - 1u) == 2u) {
                    move_record(position - 1u, destination);
                    _ = merge_cluster(destination, min(end, base_index + 1u));
                    reordered = true;
                    destination -= 1u;
                }
                position -= 1u;
            }
        } else {
            for (var position = start; position < base_index; position++) {
                if (get_indic_position(position) == 2u) {
                    _ = merge_cluster(position, min(end, base_index + 1u));
                    break;
                }
            }
        }
    }

    if (start + 1u < end && get_indic_position(start) == 1u) {
        let category_is_repha = indic_category(start) == 14u;
        let formed_reph = glyph_states[start].ligature_id != 0u &&
            (glyph_states[start].internal_flags & GLYPH_MULTIPLIED) == 0u;
        if (category_is_repha != formed_reph) {
            let destination = find_indic_reph_destination(start, end, base_index);
            _ = merge_cluster(start, destination + 1u);
            move_record(start, destination);
            reordered = true;
            if (start < base_index && base_index <= destination) { base_index -= 1u; }
        }
    }

    if (try_prebase && base_index + 1u < end) {
        for (var position = base_index + 1u; position < end; position++) {
            if ((glyph_states[position].feature_mask & INDIC_PREF) == 0u) { continue; }
            if (glyph_states[position].ligature_id != 0u &&
                    (glyph_states[position].internal_flags & GLYPH_MULTIPLIED) == 0u) {
                var destination = base_index;
                if (script != 0x6d6c796du && script != 0x74616d6cu) {
                    while (destination > start && indic_category(destination - 1u) != 7u &&
                            indic_category(destination - 1u) != 13u &&
                            indic_category(destination - 1u) != 4u) { destination -= 1u; }
                }
                if (destination > start && is_indic_halant(destination - 1u) &&
                        destination < end && is_indic_joiner(destination)) { destination += 1u; }
                _ = merge_cluster(destination, position + 1u);
                move_record(position, destination);
                reordered = true;
                if (destination <= base_index && base_index < position) { base_index += 1u; }
            }
            break;
        }
    }
    if (reordered || get_indic_position(start) == 2u) { _ = merge_cluster(start, end); }
    if (get_indic_position(start) == 2u &&
            (start == 0u || is_indic_word_boundary(glyphs[start - 1u].codepoint))) {
        add_indic_mask(start, INDIC_INIT);
    }
}

fn final_reorder_indic() {
    var start = 0u;
    while (start < run_state.glyph_count) {
        let syllable = glyph_states[start].syllable;
        var end = start + 1u;
        while (end < run_state.glyph_count && glyph_states[end].syllable == syllable) { end += 1u; }
        final_reorder_indic_syllable(start, end);
        start = end;
    }
}

fn handle_substitution_stage_transition(previous: u32, next: u32) {
    let myanmar = params.script_tag == 0x6d796d72u || params.script_tag == 0x6d796d32u;
    if (uses_arabic_joining() && previous <= 10u && next > 10u) {
        for (var position = 0u; position < run_state.glyph_count; position++) {
            if ((glyph_states[position].internal_flags & GLYPH_MULTIPLIED) == 0u) { continue; }
            glyph_states[position].feature_mask |= select(
                ARABIC_STRETCH_FIXED, ARABIC_STRETCH_REPEATING,
                (multiple_substitution_component(position) & 1u) != 0u);
        }
    }
    if (params.script_tag == 0x61726162u && previous <= 160u && next > 160u) {
        apply_arabic_fallback();
    }
    if (myanmar && previous <= 10u && next > 10u) { reorder_myanmar(); }
    if (myanmar && previous <= 50u && next > 50u) { clear_syllables(); }
    if (is_use_syllable_script() && previous <= 10u && next > 10u) { clear_substitution_flags(); }
    if (is_use_syllable_script() && previous <= 20u && next > 20u) {
        record_use_repha();
        clear_substitution_flags();
    }
    if (is_use_syllable_script() && previous <= 30u && next > 30u) { record_use_prebase(); }
    if (is_use_syllable_script() && previous <= 40u && next > 40u) { reorder_use(); }
    if (is_indic_syllable_script() && previous <= 60u && next > 60u) { initial_reorder_indic(); }
    if (is_indic_syllable_script() && previous <= 170u && next > 170u) { final_reorder_indic(); }
}

fn directional_mapping(codepoint: u32, vertical: bool) -> u32 {
    var low = 0u;
    var high = unicode_data[3u];
    let base = unicode_data[4u];
    loop {
        if (low >= high) { break; }
        let middle = low + ((high - low) >> 1u);
        let record = base + middle * 4u;
        let mapped_codepoint = unicode_data[record];
        if (codepoint < mapped_codepoint) { high = middle; }
        else if (codepoint > mapped_codepoint) { low = middle + 1u; }
        else { return select(unicode_data[record + 1u], unicode_data[record + 2u], vertical); }
    }
    return codepoint;
}

fn apply_directional_codepoint_fallback() {
    let backward = params.direction == 2u || params.direction == 4u;
    let vertical_direction = params.direction == 3u || params.direction == 4u;
    var has_vertical_substitution = false;
    if (vertical_direction) {
        for (var command_index = 0u; command_index < params.lookup_count; command_index++) {
            let command = lookup_commands[command_index];
            if (command.table_kind == 1u && command.feature_value != 0u &&
                    (command.feature_tag == 0x76657274u || command.feature_tag == 0x76727432u)) {
                has_vertical_substitution = true;
                break;
            }
        }
    }
    let vertical_fallback = vertical_direction && !has_vertical_substitution;
    if (!backward && !vertical_fallback) { return; }
    for (var position = 0u; position < run_state.glyph_count; position++) {
        let original = glyphs[position].codepoint;
        var mapped = original;
        if (backward) {
            let mirrored = directional_mapping(mapped, false);
            if (mirrored != mapped && nominal_glyph(mirrored) != 0u) { mapped = mirrored; }
        }
        if (vertical_fallback) {
            let vertical = directional_mapping(mapped, true);
            if (vertical != mapped && nominal_glyph(vertical) != 0u) { mapped = vertical; }
        }
        if (mapped != original) {
            glyphs[position].codepoint = mapped;
            glyphs[position].glyph_id = nominal_glyph(mapped);
        }
    }
}

fn modified_combining_class(codepoint: u32) -> u32 {
    if (codepoint == 0x1a60u || codepoint == 0x0fc6u) { return 254u; }
    if (codepoint == 0x0f39u) { return 127u; }
    let canonical = (unicode_properties_a(codepoint) >> 8u) & 0xffu;
    switch canonical {
        case 10u: { return 22u; } case 11u: { return 15u; } case 12u: { return 16u; }
        case 13u: { return 17u; } case 14u: { return 23u; } case 15u: { return 18u; }
        case 16u: { return 19u; } case 17u: { return 20u; } case 18u: { return 21u; }
        case 19u: { return 14u; } case 20u: { return 24u; } case 21u: { return 12u; }
        case 22u: { return 25u; } case 23u: { return 13u; } case 24u: { return 10u; }
        case 25u: { return 11u; } case 27u: { return 28u; } case 28u: { return 29u; }
        case 29u: { return 30u; } case 30u: { return 31u; } case 31u: { return 32u; }
        case 32u: { return 33u; } case 33u: { return 27u; } case 84u: { return 4u; }
        case 91u: { return 5u; } case 103u: { return 3u; } case 130u: { return 132u; }
        case 132u: { return 131u; }
        default: { return canonical; }
    }
}

fn canonical_combining_class(codepoint: u32) -> u32 {
    return (unicode_properties_a(codepoint) >> 8u) & 0xffu;
}

fn canonical_composition(first: u32, second: u32) -> u32 {
    let s_base = 0xac00u;
    let l_base = 0x1100u;
    let v_base = 0x1161u;
    let t_base = 0x11a7u;
    let l_count = 19u;
    let v_count = 21u;
    let t_count = 28u;
    let n_count = v_count * t_count;
    let s_count = l_count * n_count;
    if (first >= l_base && first < l_base + l_count &&
            second >= v_base && second < v_base + v_count) {
        return s_base + (first - l_base) * n_count + (second - v_base) * t_count;
    }
    if (first >= s_base && first < s_base + s_count &&
            (first - s_base) % t_count == 0u &&
            second > t_base && second < t_base + t_count) {
        return first + second - t_base;
    }
    var low = 0u;
    var high = unicode_data[9u];
    let base = unicode_data[10u];
    loop {
        if (low >= high) { break; }
        let middle = low + ((high - low) >> 1u);
        let record = base + middle * 3u;
        let record_first = unicode_data[record];
        let record_second = unicode_data[record + 1u];
        if (first < record_first || (first == record_first && second < record_second)) { high = middle; }
        else if (first > record_first || (first == record_first && second > record_second)) {
            low = middle + 1u;
        } else { return unicode_data[record + 2u]; }
    }
    return 0xffffffffu;
}

fn canonical_decomposition_record(codepoint: u32) -> u32 {
    var low = 0u;
    var high = unicode_data[5u];
    let base = unicode_data[6u];
    loop {
        if (low >= high) { break; }
        let middle = low + ((high - low) >> 1u);
        let record = base + middle * 3u;
        let value = unicode_data[record];
        if (codepoint < value) { high = middle; }
        else if (codepoint > value) { low = middle + 1u; }
        else { return record; }
    }
    return 0xffffffffu;
}

fn remove_record(position: u32) {
    for (var cursor = position + 1u; cursor < run_state.glyph_count; cursor++) {
        glyphs[cursor - 1u] = glyphs[cursor];
        glyph_states[cursor - 1u] = glyph_states[cursor];
    }
    run_state.glyph_count -= 1u;
}

fn insert_codepoint(position: u32, codepoint: u32, cluster: i32) -> bool {
    if (run_state.glyph_count >= params.capacity) { run_state.status = 1u; return false; }
    var cursor = run_state.glyph_count;
    while (cursor > position) {
        glyphs[cursor] = glyphs[cursor - 1u];
        glyph_states[cursor] = glyph_states[cursor - 1u];
        cursor -= 1u;
    }
    glyphs[position] = ShapingGlyph(nominal_glyph(codepoint), codepoint, cluster, 0u, 0, 0, 0, 0);
    glyph_states[position] = GlyphState(
        run_state.next_serial, 0u, 0u, 0, 0u, ARABIC_NONE << ARABIC_ACTION_SHIFT, 0u, 0u);
    run_state.next_serial += 1u;
    run_state.glyph_count += 1u;
    return true;
}

fn vowel_constraint_script() -> u32 {
    let script = params.script_tag;
    if (script == 0x626e6732u || script == 0x626e6733u) { return 0x62656e67u; }
    if (script == 0x64657632u || script == 0x64657633u) { return 0x64657661u; }
    if (script == 0x676a7232u || script == 0x676a7233u) { return 0x67756a72u; }
    if (script == 0x67757232u || script == 0x67757233u) { return 0x67757275u; }
    if (script == 0x6b6e6432u || script == 0x6b6e6433u) { return 0x6b6e6461u; }
    if (script == 0x6d6c6d32u || script == 0x6d6c6d33u) { return 0x6d6c796du; }
    if (script == 0x6f727932u || script == 0x6f727933u) { return 0x6f727961u; }
    if (script == 0x746d6c32u || script == 0x746d6c33u) { return 0x74616d6cu; }
    if (script == 0x74656c32u || script == 0x74656c33u) { return 0x74656c75u; }
    return script;
}

fn compare_vowel_constraint(record: u32, script: u32, first: u32, second: u32) -> i32 {
    let record_script = unicode_data[record];
    if (record_script < script) { return -1; }
    if (record_script > script) { return 1; }
    let record_first = unicode_data[record + 1u];
    if (record_first < first) { return -1; }
    if (record_first > first) { return 1; }
    let record_second = unicode_data[record + 2u];
    if (record_second < second) { return -1; }
    if (record_second > second) { return 1; }
    return 0;
}

fn vowel_constraint_match_length(first: u32, second: u32, third: u32) -> u32 {
    let script = vowel_constraint_script();
    let count = unicode_data[13u];
    let base = unicode_data[14u];
    var low = 0u;
    var high = count;
    while (low < high) {
        let middle = low + ((high - low) >> 1u);
        let comparison = compare_vowel_constraint(base + middle * 4u, script, first, second);
        if (comparison < 0) { low = middle + 1u; } else { high = middle; }
    }
    var index = low;
    while (index < count) {
        let record = base + index * 4u;
        if (compare_vowel_constraint(record, script, first, second) != 0) { break; }
        let expected_third = unicode_data[record + 3u];
        if (expected_third == 0u) { return 2u; }
        if (expected_third == third) { return 3u; }
        index += 1u;
    }
    return 0u;
}

fn apply_vowel_constraints() {
    var index = 0u;
    while (index + 1u < run_state.glyph_count) {
        var third = 0u;
        if (index + 2u < run_state.glyph_count) { third = glyphs[index + 2u].codepoint; }
        let length = vowel_constraint_match_length(
            glyphs[index].codepoint, glyphs[index + 1u].codepoint, third);
        if (length == 0u) { index += 1u; continue; }
        let final_index = index + length - 1u;
        if (!insert_codepoint(final_index, 0x25ccu, glyphs[final_index].cluster)) { return; }
        index += length + 1u;
    }
}

fn normalize_complex_script_diacritics() {
    if (!is_use_syllable_script() && !is_indic_syllable_script()) { return; }
    var has_indic_split_matra = false;
    var position = 0u;
    while (position < run_state.glyph_count) {
        let codepoint = glyphs[position].codepoint;
        let record = canonical_decomposition_record(codepoint);
        if (record == 0xffffffffu) { position += 1u; continue; }
        let count = unicode_data[record + 2u];
        if (count == 0u) { position += 1u; continue; }
        let first = decomposition_scalar(codepoint, record, 0u);
        if ((unicode_properties_b(first) & 0x100u) == 0u) { position += 1u; continue; }
        let extra = count - 1u;
        if (run_state.glyph_count + extra > params.capacity) { run_state.status = 1u; return; }
        var cursor = run_state.glyph_count;
        while (cursor > position + 1u) {
            cursor -= 1u;
            glyphs[cursor + extra] = glyphs[cursor];
            glyph_states[cursor + extra] = glyph_states[cursor];
        }
        let source = glyphs[position];
        let source_state = glyph_states[position];
        let restore_character_cluster = is_indic_syllable_script() && has_indic_split_matra;
        for (var component = 0u; component < count; component++) {
            let target_index = position + component;
            let scalar = decomposition_scalar(codepoint, record, component);
            glyphs[target_index] = source;
            glyphs[target_index].codepoint = scalar;
            glyphs[target_index].glyph_id = nominal_glyph(scalar);
            glyph_states[target_index] = source_state;
            if (restore_character_cluster) {
                glyph_states[target_index].attachment_chain = source.cluster;
                glyph_states[target_index].internal_flags |= GLYPH_SPLIT_MATRA_COMPONENT;
            }
            if (component != 0u) {
                glyph_states[target_index].serial = run_state.next_serial;
                run_state.next_serial += 1u;
            }
        }
        if (is_indic_syllable_script()) { has_indic_split_matra = true; }
        run_state.glyph_count += extra;
        position += count;
    }
}

fn restore_split_matra_character_clusters() {
    if (params.cluster_level != 1u) { return; }
    var changed = false;
    for (var index = 0u; index < run_state.glyph_count; index++) {
        if ((glyph_states[index].internal_flags & GLYPH_SPLIT_MATRA_COMPONENT) == 0u) { continue; }
        glyphs[index].cluster = glyph_states[index].attachment_chain;
        glyph_states[index].attachment_chain = 0;
        glyph_states[index].internal_flags &= ~GLYPH_SPLIT_MATRA_COMPONENT;
        changed = true;
    }
    if (!changed) { return; }
    for (var index = 1u; index < run_state.glyph_count; index++) {
        let cluster = glyphs[index].cluster;
        var previous = index;
        while (previous > 0u && glyphs[previous - 1u].cluster > cluster) {
            previous -= 1u;
            glyphs[previous].cluster = cluster;
        }
    }
}

fn expand_khmer_split_matras() {
    if (params.script_tag != 0x6b686d72u) { return; }
    var position = 0u;
    while (position < run_state.glyph_count) {
        let codepoint = glyphs[position].codepoint;
        if (codepoint != 0x17beu && codepoint != 0x17bfu && codepoint != 0x17c0u &&
                codepoint != 0x17c4u && codepoint != 0x17c5u) {
            position += 1u;
            continue;
        }
        let cluster = glyphs[position].cluster;
        if (!insert_codepoint(position, 0x17c1u, cluster)) { return; }
        position += 2u;
    }
}

fn reorder_canonical_combining_marks() {
    var segment_start = 0u;
    while (segment_start < run_state.glyph_count) {
        while (segment_start < run_state.glyph_count &&
                canonical_combining_class(glyphs[segment_start].codepoint) == 0u) {
            segment_start += 1u;
        }
        if (segment_start >= run_state.glyph_count) { break; }
        var segment_end = segment_start;
        while (segment_end < run_state.glyph_count &&
                canonical_combining_class(glyphs[segment_end].codepoint) != 0u) {
            segment_end += 1u;
        }
        for (var index = segment_start + 1u; index < segment_end; index++) {
            let value = glyphs[index];
            let value_state = glyph_states[index];
            let value_class = canonical_combining_class(value.codepoint);
            var destination = index;
            var crossed_cluster = 0x7fffffffi;
            while (destination > segment_start &&
                    canonical_combining_class(glyphs[destination - 1u].codepoint) > value_class) {
                crossed_cluster = min(crossed_cluster, glyphs[destination - 1u].cluster);
                glyphs[destination] = glyphs[destination - 1u];
                glyph_states[destination] = glyph_states[destination - 1u];
                destination -= 1u;
            }
            glyphs[destination] = value;
            glyph_states[destination] = value_state;
            if (params.cluster_level == 1u && destination < index) {
                for (var crossed = destination; crossed <= index; crossed++) {
                    glyphs[crossed].cluster = crossed_cluster;
                }
            }
        }
        segment_start = segment_end + 1u;
    }
}

fn compose_canonical_sequences() {
    var starter = 0xffffffffu;
    var last_class = 0u;
    var position = 0u;
    while (position < run_state.glyph_count) {
        let current_class = canonical_combining_class(glyphs[position].codepoint);
        if (starter != 0xffffffffu && (last_class < current_class || last_class == 0u)) {
            let composed = canonical_composition(glyphs[starter].codepoint, glyphs[position].codepoint);
            if (composed != 0xffffffffu) {
                glyphs[starter].codepoint = composed;
                glyphs[starter].glyph_id = nominal_glyph(composed);
                remove_record(position);
                continue;
            }
        }
        if (current_class == 0u) {
            starter = position;
            last_class = 0u;
        } else {
            last_class = current_class;
        }
        position += 1u;
    }
}

fn decomposition_scalar(codepoint: u32, record: u32, component: u32) -> u32 {
    let s_base = 0xac00u;
    let l_base = 0x1100u;
    let v_base = 0x1161u;
    let t_base = 0x11a7u;
    let t_count = 28u;
    let n_count = 588u;
    if (record != 0xffffffffu) {
        return unicode_data[unicode_data[8u] + unicode_data[record + 1u] + component];
    }
    let s_index = codepoint - s_base;
    if (component == 0u) { return l_base + s_index / n_count; }
    if (component == 1u) { return v_base + (s_index % n_count) / t_count; }
    return t_base + s_index % t_count;
}

fn decompose_missing_glyphs() {
    let s_base = 0xac00u;
    let s_count = 11172u;
    let t_count = 28u;
    var position = 0u;
    while (position < run_state.glyph_count) {
        if (glyphs[position].glyph_id != 0u) { position += 1u; continue; }
        let codepoint = glyphs[position].codepoint;
        if (codepoint == 0x2011u && nominal_glyph(0x2010u) != 0u) {
            glyphs[position].codepoint = 0x2010u;
            glyphs[position].glyph_id = nominal_glyph(0x2010u);
            position += 1u;
            continue;
        }
        var record = 0xffffffffu;
        var count = 0u;
        if (codepoint >= s_base && codepoint < s_base + s_count) {
            count = select(3u, 2u, (codepoint - s_base) % t_count == 0u);
        } else {
            record = canonical_decomposition_record(codepoint);
            if (record != 0xffffffffu) { count = unicode_data[record + 2u]; }
        }
        if (count == 0u) { position += 1u; continue; }
        let extra = count - 1u;
        if (run_state.glyph_count + extra > params.capacity) {
            run_state.status = 1u;
            return;
        }
        var cursor = run_state.glyph_count;
        while (cursor > position + 1u) {
            cursor -= 1u;
            glyphs[cursor + extra] = glyphs[cursor];
            glyph_states[cursor + extra] = glyph_states[cursor];
        }
        let source = glyphs[position];
        let source_state = glyph_states[position];
        for (var component = 0u; component < count; component++) {
            let target_index = position + component;
            let scalar = decomposition_scalar(codepoint, record, component);
            glyphs[target_index] = source;
            glyphs[target_index].codepoint = scalar;
            glyphs[target_index].glyph_id = nominal_glyph(scalar);
            glyph_states[target_index] = source_state;
            if (component != 0u) {
                glyph_states[target_index].serial = run_state.next_serial;
                run_state.next_serial += 1u;
            }
        }
        run_state.glyph_count += extra;
        position += count;
    }
}

fn normalize_unicode() {
    if (params.script_tag == 0x68616e67u) { return; }
    reorder_modified_combining_marks();
    compose_canonical_sequences();
    decompose_missing_glyphs();
}

fn is_hangul_l(codepoint: u32) -> bool {
    return (codepoint >= 0x1100u && codepoint <= 0x115fu) ||
        (codepoint >= 0xa960u && codepoint <= 0xa97cu);
}

fn is_hangul_v(codepoint: u32) -> bool {
    return (codepoint >= 0x1160u && codepoint <= 0x11a7u) ||
        (codepoint >= 0xd7b0u && codepoint <= 0xd7c6u);
}

fn is_hangul_t(codepoint: u32) -> bool {
    return (codepoint >= 0x11a8u && codepoint <= 0x11ffu) ||
        (codepoint >= 0xd7cbu && codepoint <= 0xd7fbu);
}

fn set_hangul_feature(position: u32, feature: u32) {
    glyph_states[position].feature_mask =
        (glyph_states[position].feature_mask & ~HANGUL_FEATURE_MASK) | feature;
}

fn merge_cluster(start_value: u32, end_value: u32) -> i32 {
    var start = start_value;
    var end = end_value;
    if (start >= end) {
        if (start < run_state.glyph_count) { return glyphs[start].cluster; }
        return 0;
    }
    let first_cluster = glyphs[start].cluster;
    let last_cluster = glyphs[end - 1u].cluster;
    while (start > 0u && glyphs[start - 1u].cluster == first_cluster) { start -= 1u; }
    while (end < run_state.glyph_count && glyphs[end].cluster == last_cluster) { end += 1u; }
    var cluster = 0x7fffffffi;
    for (var index = start; index < end; index++) { cluster = min(cluster, glyphs[index].cluster); }
    for (var index = start; index < end; index++) { glyphs[index].cluster = cluster; }
    return cluster;
}

fn insert_hangul_decomposition(position: u32, codepoint: u32, followed_by_trailing: bool) -> u32 {
    let syllable_index = codepoint - 0xac00u;
    let trailing_index = syllable_index % 28u;
    let count = select(3u, 2u, trailing_index == 0u);
    let extra = count - 1u;
    if (run_state.glyph_count + extra > params.capacity) {
        run_state.status = 1u;
        return 0u;
    }
    var cursor = run_state.glyph_count;
    while (cursor > position + 1u) {
        cursor -= 1u;
        glyphs[cursor + extra] = glyphs[cursor];
        glyph_states[cursor + extra] = glyph_states[cursor];
    }
    let source = glyphs[position];
    let source_state = glyph_states[position];
    let leading = 0x1100u + syllable_index / 588u;
    let vowel = 0x1161u + (syllable_index % 588u) / 28u;
    glyphs[position].codepoint = leading;
    glyphs[position].glyph_id = nominal_glyph(leading);
    set_hangul_feature(position, HANGUL_LJMO);
    glyphs[position + 1u] = source;
    glyphs[position + 1u].codepoint = vowel;
    glyphs[position + 1u].glyph_id = nominal_glyph(vowel);
    glyph_states[position + 1u] = source_state;
    glyph_states[position + 1u].serial = run_state.next_serial;
    run_state.next_serial += 1u;
    set_hangul_feature(position + 1u, HANGUL_VJMO);
    if (trailing_index != 0u) {
        let trailing = 0x11a7u + trailing_index;
        glyphs[position + 2u] = source;
        glyphs[position + 2u].codepoint = trailing;
        glyphs[position + 2u].glyph_id = nominal_glyph(trailing);
        glyph_states[position + 2u] = source_state;
        glyph_states[position + 2u].serial = run_state.next_serial;
        run_state.next_serial += 1u;
        set_hangul_feature(position + 2u, HANGUL_TJMO);
    }
    run_state.glyph_count += extra;
    if (followed_by_trailing) { set_hangul_feature(position + count, HANGUL_TJMO); }
    return count + select(0u, 1u, followed_by_trailing);
}

fn prepare_hangul_shaping() {
    if (params.script_tag != 0x68616e67u) { return; }
    for (var clear = 0u; clear < run_state.glyph_count; clear++) { set_hangul_feature(clear, 0u); }
    var position = 0u;
    while (position < run_state.glyph_count) {
        let codepoint = glyphs[position].codepoint;
        if (is_hangul_l(codepoint) && position + 1u < run_state.glyph_count &&
                is_hangul_v(glyphs[position + 1u].codepoint)) {
            var trailing = 0u;
            if (position + 2u < run_state.glyph_count && is_hangul_t(glyphs[position + 2u].codepoint)) {
                trailing = glyphs[position + 2u].codepoint;
            }
            let input_count = select(3u, 2u, trailing == 0u);
            var composed = 0xffffffffu;
            if (codepoint >= 0x1100u && codepoint <= 0x1112u &&
                    glyphs[position + 1u].codepoint >= 0x1161u &&
                    glyphs[position + 1u].codepoint <= 0x1175u &&
                    (trailing == 0u || (trailing >= 0x11a8u && trailing <= 0x11c2u))) {
                composed = 0xac00u + (codepoint - 0x1100u) * 588u +
                    (glyphs[position + 1u].codepoint - 0x1161u) * 28u +
                    select(trailing - 0x11a7u, 0u, trailing == 0u);
            }
            if (composed != 0xffffffffu && nominal_glyph(composed) != 0u) {
                glyphs[position].codepoint = composed;
                glyphs[position].glyph_id = nominal_glyph(composed);
                glyphs[position].cluster = merge_cluster(position, position + input_count);
                for (var removed = 1u; removed < input_count; removed++) { remove_record(position + 1u); }
                position += 1u;
                continue;
            }
            set_hangul_feature(position, HANGUL_LJMO);
            set_hangul_feature(position + 1u, HANGUL_VJMO);
            if (trailing != 0u) { set_hangul_feature(position + 2u, HANGUL_TJMO); }
            _ = merge_cluster(position, position + input_count);
            position += input_count;
            continue;
        }
        if (codepoint >= 0xac00u && codepoint <= 0xd7a3u) {
            let syllable_index = codepoint - 0xac00u;
            let trailing_index = syllable_index % 28u;
            if (trailing_index == 0u && position + 1u < run_state.glyph_count &&
                    glyphs[position + 1u].codepoint >= 0x11a8u &&
                    glyphs[position + 1u].codepoint <= 0x11c2u) {
                let combined = codepoint + glyphs[position + 1u].codepoint - 0x11a7u;
                if (nominal_glyph(combined) != 0u) {
                    glyphs[position].codepoint = combined;
                    glyphs[position].glyph_id = nominal_glyph(combined);
                    glyphs[position].cluster = merge_cluster(position, position + 2u);
                    remove_record(position + 1u);
                    position += 1u;
                    continue;
                }
            }
            let followed_by_trailing = trailing_index == 0u && position + 1u < run_state.glyph_count &&
                is_hangul_t(glyphs[position + 1u].codepoint) &&
                !(glyphs[position + 1u].codepoint >= 0x11a8u && glyphs[position + 1u].codepoint <= 0x11c2u);
            if (glyphs[position].glyph_id == 0u || followed_by_trailing) {
                let leading = 0x1100u + syllable_index / 588u;
                let vowel = 0x1161u + (syllable_index % 588u) / 28u;
                let trailing = 0x11a7u + trailing_index;
                if (nominal_glyph(leading) != 0u && nominal_glyph(vowel) != 0u &&
                        (trailing_index == 0u || nominal_glyph(trailing) != 0u)) {
                    let advanced = insert_hangul_decomposition(position, codepoint, followed_by_trailing);
                    if (run_state.status != 0u) { return; }
                    position += advanced;
                    continue;
                }
            }
        }
        position += 1u;
    }
}

fn is_thai_lao_above_base_mark(codepoint: u32) -> bool {
    let thai = codepoint & ~0x80u;
    return (thai >= 0x0e34u && thai <= 0x0e37u) ||
        (thai >= 0x0e47u && thai <= 0x0e4eu) || thai == 0x0e31u || thai == 0x0e3bu;
}

fn prepare_thai_lao() {
    let thai_script = params.script_tag == 0x74686169u;
    let lao_script = params.script_tag == 0x6c616f20u;
    if (!thai_script && !lao_script) { return; }
    let sara_am = select(0x0eb3u, 0x0e33u, thai_script);
    let nikhahit = select(0x0ecdu, 0x0e4du, thai_script);
    let sara_aa = sara_am - 1u;
    var position = 0u;
    while (position < run_state.glyph_count) {
        if (glyphs[position].codepoint != sara_am) { position += 1u; continue; }
        if (run_state.glyph_count + 1u > params.capacity) {
            run_state.status = 1u;
            return;
        }
        var cursor = run_state.glyph_count;
        while (cursor > position + 1u) {
            cursor -= 1u;
            glyphs[cursor + 1u] = glyphs[cursor];
            glyph_states[cursor + 1u] = glyph_states[cursor];
        }
        let source = glyphs[position];
        let source_state = glyph_states[position];
        glyphs[position].codepoint = nikhahit;
        glyphs[position].glyph_id = nominal_glyph(nikhahit);
        glyphs[position + 1u] = source;
        glyphs[position + 1u].codepoint = sara_aa;
        glyphs[position + 1u].glyph_id = nominal_glyph(sara_aa);
        glyph_states[position + 1u] = source_state;
        glyph_states[position + 1u].serial = run_state.next_serial;
        run_state.next_serial += 1u;
        run_state.glyph_count += 1u;
        var start = position;
        while (start > 0u && is_thai_lao_above_base_mark(glyphs[start - 1u].codepoint)) { start -= 1u; }
        if (start < position) {
            let nikhahit_glyph = glyphs[position];
            let nikhahit_state = glyph_states[position];
            for (var move_index = position; move_index > start; move_index--) {
                glyphs[move_index] = glyphs[move_index - 1u];
                glyph_states[move_index] = glyph_states[move_index - 1u];
            }
            glyphs[start] = nikhahit_glyph;
            glyph_states[start] = nikhahit_state;
        }
        let end = position + 2u;
        if (params.cluster_level == 0u || params.cluster_level == 3u) {
            _ = merge_cluster(select(start, start - 1u, start > 0u), end);
        } else if (params.cluster_level == 1u) {
            _ = merge_cluster(start, end);
        }
        position += 2u;
    }
}

fn reverse_records(start_value: u32, end_value: u32) {
    var start = start_value;
    var end = end_value;
    if (end == 0u) { return; }
    end -= 1u;
    loop {
        if (start >= end) { break; }
        let glyph = glyphs[start];
        let state = glyph_states[start];
        glyphs[start] = glyphs[end];
        glyph_states[start] = glyph_states[end];
        glyphs[end] = glyph;
        glyph_states[end] = state;
        start += 1u;
        end -= 1u;
    }
}

fn is_arabic_modifier_mark(codepoint: u32) -> bool {
    return codepoint == 0x0654u || codepoint == 0x0655u || codepoint == 0x0658u ||
        codepoint == 0x06dcu || codepoint == 0x06e3u || codepoint == 0x06e7u ||
        codepoint == 0x06e8u || codepoint == 0x08cau || codepoint == 0x08cbu ||
        codepoint == 0x08cdu || codepoint == 0x08ceu || codepoint == 0x08cfu ||
        codepoint == 0x08d3u || codepoint == 0x08f3u;
}

fn reorder_arabic_modifier_marks(start_value: u32, end: u32) {
    var start = start_value;
    for (var canonical = 220u; canonical <= 230u; canonical += 10u) {
        var first = start;
        loop {
            if (first >= end || modified_combining_class(glyphs[first].codepoint) >= canonical) { break; }
            first += 1u;
        }
        if (first == end || modified_combining_class(glyphs[first].codepoint) != canonical) { continue; }
        var last = first;
        loop {
            if (last >= end || modified_combining_class(glyphs[last].codepoint) != canonical ||
                    !is_arabic_modifier_mark(glyphs[last].codepoint)) { break; }
            last += 1u;
        }
        let count = last - first;
        if (count == 0u) { continue; }
        reverse_records(start, first);
        reverse_records(first, last);
        reverse_records(start, last);
        start += count;
    }
}

fn reorder_modified_combining_marks() {
    var segment_start = 0u;
    loop {
        while (segment_start < run_state.glyph_count &&
                modified_combining_class(glyphs[segment_start].codepoint) == 0u) {
            segment_start += 1u;
        }
        if (segment_start >= run_state.glyph_count) { break; }
        var segment_end = segment_start;
        while (segment_end < run_state.glyph_count &&
                modified_combining_class(glyphs[segment_end].codepoint) != 0u) {
            segment_end += 1u;
        }
        for (var index = segment_start + 1u; index < segment_end; index++) {
            let value = glyphs[index];
            let value_state = glyph_states[index];
            let value_class = modified_combining_class(value.codepoint);
            var destination = index;
            var crossed_cluster = 0x7fffffffi;
            while (destination > segment_start &&
                    modified_combining_class(glyphs[destination - 1u].codepoint) > value_class) {
                crossed_cluster = min(crossed_cluster, glyphs[destination - 1u].cluster);
                glyphs[destination] = glyphs[destination - 1u];
                glyph_states[destination] = glyph_states[destination - 1u];
                destination -= 1u;
            }
            glyphs[destination] = value;
            glyph_states[destination] = value_state;
            if (params.cluster_level == 1u && destination < index) {
                for (var crossed = destination; crossed <= index; crossed++) {
                    glyphs[crossed].cluster = crossed_cluster;
                }
            }
        }
        if (uses_arabic_joining()) { reorder_arabic_modifier_marks(segment_start, segment_end); }
        segment_start = segment_end + 1u;
    }
}

fn uses_arabic_joining() -> bool {
    let script = params.script_tag;
    return script == 0x61646c6du || script == 0x61726162u || script == 0x63687273u ||
        script == 0x726f6867u || script == 0x6d616e64u || script == 0x6d616e69u ||
        script == 0x6d6f6e67u || script == 0x6e6b6f6fu || script == 0x6f756772u ||
        script == 0x70686167u || script == 0x70686c70u || script == 0x736f6764u ||
        script == 0x73797263u;
}

fn set_arabic_action(position: u32, action: u32) {
    glyph_states[position].feature_mask =
        (glyph_states[position].feature_mask & ~ARABIC_ACTION_MASK) |
        ((action & 7u) << ARABIC_ACTION_SHIFT);
}

fn assign_arabic_joining_actions() {
    if (!uses_arabic_joining()) { return; }
    var previous = 0xffffffffu;
    var state = 0u;
    for (var position = 0u; position < run_state.glyph_count; position++) {
        let joining_type = unicode_properties_a(glyphs[position].codepoint) & 0xffu;
        if (joining_type == 6u) {
            set_arabic_action(position, ARABIC_NONE);
            continue;
        }
        let entry = arabic_state_table[state * 6u + joining_type];
        let previous_action = entry & 0xffu;
        if (previous_action != ARABIC_NONE && previous != 0xffffffffu) {
            set_arabic_action(previous, previous_action);
        }
        set_arabic_action(position, (entry >> 8u) & 0xffu);
        previous = position;
        state = (entry >> 16u) & 0xffu;
    }
}

fn feature_allowed(command: LookupCommand, position: u32) -> bool {
    if ((command.command_flags & FEATURE_EXPLICIT) != 0u) { return true; }
    if (command.feature_tag == 0x66726163u) {
        return (glyph_states[position].feature_mask &
            (FRACTION_NUMERATOR | FRACTION_DENOMINATOR | FRACTION_SLASH)) != 0u;
    }
    if (command.feature_tag == 0x6e756d72u) {
        return (glyph_states[position].feature_mask & FRACTION_NUMERATOR) != 0u;
    }
    if (command.feature_tag == 0x646e6f6du) {
        return (glyph_states[position].feature_mask & FRACTION_DENOMINATOR) != 0u;
    }
    if (command.feature_tag == 0x6c6a6d6fu) {
        return (glyph_states[position].feature_mask & HANGUL_LJMO) != 0u;
    }
    if (command.feature_tag == 0x766a6d6fu) {
        return (glyph_states[position].feature_mask & HANGUL_VJMO) != 0u;
    }
    if (command.feature_tag == 0x746a6d6fu) {
        return (glyph_states[position].feature_mask & HANGUL_TJMO) != 0u;
    }
    if (command.feature_tag == 0x70726566u && params.script_tag == 0x6b686d72u) {
        return (glyph_states[position].feature_mask & KHMER_PREF) != 0u;
    }
    if ((command.feature_tag == 0x626c7766u || command.feature_tag == 0x61627666u ||
            command.feature_tag == 0x70737466u) && params.script_tag == 0x6b686d72u) {
        return (glyph_states[position].feature_mask & KHMER_POST_BASE) != 0u;
    }
    if (command.feature_tag == 0x63666172u && params.script_tag == 0x6b686d72u) {
        return (glyph_states[position].feature_mask & KHMER_CFAR) != 0u;
    }
    if (is_indic_syllable_script()) {
        if (command.feature_tag == 0x72706866u) {
            return (glyph_states[position].feature_mask & INDIC_RPHF) != 0u;
        }
        if (command.feature_tag == 0x70726566u) {
            return (glyph_states[position].feature_mask & INDIC_PREF) != 0u;
        }
        if (command.feature_tag == 0x626c7766u) {
            return (glyph_states[position].feature_mask & INDIC_BLWF) != 0u;
        }
        if (command.feature_tag == 0x61627666u) {
            return (glyph_states[position].feature_mask & INDIC_ABVF) != 0u;
        }
        if (command.feature_tag == 0x68616c66u) {
            return (glyph_states[position].feature_mask & INDIC_HALF) != 0u;
        }
        if (command.feature_tag == 0x70737466u) {
            return (glyph_states[position].feature_mask & INDIC_PSTF) != 0u;
        }
        if (command.feature_tag == 0x696e6974u) {
            return (glyph_states[position].feature_mask & INDIC_INIT) != 0u;
        }
    }
    let arabic_action = (glyph_states[position].feature_mask & ARABIC_ACTION_MASK) >> ARABIC_ACTION_SHIFT;
    if (command.feature_tag == 0x69736f6cu) { return arabic_action == ARABIC_ISOLATED; }
    if (command.feature_tag == 0x66696e61u) { return arabic_action == ARABIC_FINAL; }
    if (command.feature_tag == 0x66696e32u) { return arabic_action == ARABIC_FINAL2; }
    if (command.feature_tag == 0x66696e33u) { return arabic_action == ARABIC_FINAL3; }
    if (command.feature_tag == 0x6d656469u) { return arabic_action == ARABIC_MEDIAL; }
    if (command.feature_tag == 0x6d656432u) { return arabic_action == ARABIC_MEDIAL2; }
    if (command.feature_tag == 0x696e6974u) { return arabic_action == ARABIC_INITIAL; }
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

fn outside_active_syllable(position: u32) -> bool {
    return (run_state.reserved2 & FEATURE_PER_SYLLABLE) != 0u &&
        glyph_states[position].syllable != run_state.reserved1;
}

fn lookup_ignored_mode(position: u32, lookup_offset: u32, lookup_flags: u32,
    context_match: bool, gpos_match: bool) -> bool {
    let codepoint = glyphs[position].codepoint;
    if (codepoint == 0x200cu) {
        if (gpos_match || (context_match && (run_state.reserved2 & FEATURE_MANUAL_ZWNJ) == 0u)) {
            return true;
        }
    } else if (codepoint == 0x200du) {
        if (gpos_match || context_match || (run_state.reserved2 & FEATURE_MANUAL_ZWJ) == 0u) {
            return true;
        }
    } else if (is_default_ignorable(codepoint) &&
            (glyph_states[position].internal_flags & GLYPH_SUBSTITUTED) == 0u &&
            (glyph_states[position].feature_mask & HANGUL_FEATURE_MASK) == 0u &&
            !is_visible_shaping_control(codepoint)) { return true; }
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

fn lookup_ignored(position: u32, lookup_offset: u32, lookup_flags: u32) -> bool {
    return lookup_ignored_mode(position, lookup_offset, lookup_flags, false,
        (run_state.reserved2 & FEATURE_GPOS_MATCH) != 0u);
}

fn context_lookup_ignored(position: u32, lookup_offset: u32, lookup_flags: u32) -> bool {
    return lookup_ignored_mode(position, lookup_offset, lookup_flags, true,
        (run_state.reserved2 & FEATURE_GPOS_MATCH) != 0u);
}

fn next_eligible(start: u32, lookup_offset: u32, lookup_flags: u32) -> i32 {
    for (var index = start; index < run_state.glyph_count; index++) {
        if (outside_active_syllable(index)) { return -1; }
        if (!lookup_ignored(index, lookup_offset, lookup_flags)) { return i32(index); }
    }
    return -1;
}

fn next_ligature_component(start: u32, expected_glyph: u32,
    lookup_offset: u32, lookup_flags: u32) -> i32 {
    for (var index = start; index < run_state.glyph_count; index++) {
        if (outside_active_syllable(index)) { return -1; }
        let codepoint = glyphs[index].codepoint;
        let visible = (glyph_states[index].internal_flags & GLYPH_SUBSTITUTED) != 0u ||
            (glyph_states[index].feature_mask & HANGUL_FEATURE_MASK) != 0u;
        if (is_default_ignorable(codepoint) && !visible) {
            let explicit_component = glyphs[index].glyph_id == expected_glyph ||
                codepoint == 0x034fu || codepoint == 0x200cu ||
                (codepoint == 0x200du && (run_state.reserved2 & FEATURE_MANUAL_ZWJ) != 0u) ||
                (codepoint >= 0x180bu && codepoint <= 0x180eu) ||
                (codepoint >= 0xe0000u && codepoint <= 0xe007fu);
            if (explicit_component) { return i32(index); }
            continue;
        }
        if (!lookup_ignored(index, lookup_offset, lookup_flags)) { return i32(index); }
    }
    return -1;
}

fn next_context_eligible(start: u32, lookup_offset: u32, lookup_flags: u32) -> i32 {
    for (var index = start; index < run_state.glyph_count; index++) {
        if (outside_active_syllable(index)) { return -1; }
        if (!context_lookup_ignored(index, lookup_offset, lookup_flags)) { return i32(index); }
    }
    return -1;
}

fn previous_context_eligible(start: i32, lookup_offset: u32, lookup_flags: u32) -> i32 {
    var index = start;
    loop {
        if (index < 0) { break; }
        if (outside_active_syllable(u32(index))) { return -1; }
        if (!context_lookup_ignored(u32(index), lookup_offset, lookup_flags)) { return index; }
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

fn fallback_feature_enabled(feature_tag: u32, position: u32) -> bool {
    let cluster = u32(max(glyphs[position].cluster, 0));
    for (var command_index = 0u; command_index < params.lookup_count; command_index++) {
        let command = lookup_commands[command_index];
        if (command.table_kind == 4u && command.feature_tag == feature_tag &&
                command.feature_value != 0u && cluster >= command.range_start &&
                cluster < command.range_end) { return true; }
    }
    return false;
}

fn arabic_fallback_word(field: u32) -> u32 {
    let directory = unicode_data[15u];
    if (directory == 0u || unicode_data[directory] != 0x41524246u) { return 0u; }
    return unicode_data[directory + field];
}

fn apply_arabic_fallback_forms() {
    let first = arabic_fallback_word(1u);
    let last = arabic_fallback_word(2u);
    let count = arabic_fallback_word(3u);
    let forms = arabic_fallback_word(4u);
    if (forms == 0u) { return; }
    for (var position = 0u; position < run_state.glyph_count; position++) {
        let action = (glyph_states[position].feature_mask & ARABIC_ACTION_MASK) >> ARABIC_ACTION_SHIFT;
        var form = 0xffffffffu;
        if (action == ARABIC_INITIAL && fallback_feature_enabled(0x696e6974u, position)) { form = 0u; }
        else if (action == ARABIC_MEDIAL && fallback_feature_enabled(0x6d656469u, position)) { form = 1u; }
        else if (action == ARABIC_FINAL && fallback_feature_enabled(0x66696e61u, position)) { form = 2u; }
        else if (action == ARABIC_ISOLATED && fallback_feature_enabled(0x69736f6cu, position)) { form = 3u; }
        let codepoint = glyphs[position].codepoint;
        if (form == 0xffffffffu || codepoint < first || codepoint > last) { continue; }
        let index = (codepoint - first) * 4u + form;
        if (index >= count) { continue; }
        let presentation = unicode_data[forms + index];
        if (presentation == 0u || glyphs[position].glyph_id != nominal_glyph(codepoint)) { continue; }
        let replacement = nominal_glyph(presentation);
        if (replacement != 0u && replacement != glyphs[position].glyph_id) {
            glyphs[position].glyph_id = replacement;
            glyph_states[position].internal_flags |= GLYPH_SUBSTITUTED;
        }
    }
}

fn fallback_mark_glyph(position: u32) -> bool {
    if (table_directory.gdef_length >= 6u && table_u16(table_directory.gdef_offset + 4u) != 0u) {
        return gdef_class(4u, glyphs[position].glyph_id) == 3u;
    }
    return is_unicode_mark(glyphs[position].codepoint);
}

fn next_fallback_component(start: u32, expected: u32, ignore_marks: bool) -> i32 {
    for (var position = start; position < run_state.glyph_count; position++) {
        if (ignore_marks && fallback_mark_glyph(position)) { continue; }
        return select(-1, i32(position), glyphs[position].glyph_id == expected);
    }
    return -1;
}

fn remove_fallback_component(position: u32) {
    for (var cursor = position + 1u; cursor < run_state.glyph_count; cursor++) {
        glyphs[cursor - 1u] = glyphs[cursor];
        glyph_states[cursor - 1u] = glyph_states[cursor];
    }
    run_state.glyph_count -= 1u;
}

fn apply_arabic_fallback_ligature_table(count_field: u32, offset_field: u32,
    additional_components: u32, ignore_marks: bool) {
    let count = arabic_fallback_word(count_field);
    let table = arabic_fallback_word(offset_field);
    let stride = additional_components + 2u;
    if (table == 0u || stride > 4u) { return; }
    var position = 0u;
    while (position < run_state.glyph_count) {
        if (!fallback_feature_enabled(0x726c6967u, position)) { position += 1u; continue; }
        var row = 0u;
        var replaced = false;
        while (row + stride <= count) {
            let expected_first = nominal_glyph(unicode_data[table + row]);
            if (expected_first == 0u || glyphs[position].glyph_id != expected_first) {
                row += stride;
                continue;
            }
            var components: array<u32, 3>;
            components[0] = position;
            var candidate = position;
            var matched = true;
            for (var component = 0u; component < additional_components; component++) {
                let expected = nominal_glyph(unicode_data[table + row + 1u + component]);
                if (expected == 0u) { matched = false; break; }
                let next = next_fallback_component(candidate + 1u, expected, ignore_marks);
                if (next < 0) { matched = false; break; }
                candidate = u32(next);
                components[component + 1u] = candidate;
            }
            let ligature = nominal_glyph(unicode_data[table + row + 1u + additional_components]);
            if (!matched || ligature == 0u) { row += stride; continue; }
            let cluster = merge_cluster(position, candidate + 1u);
            glyphs[position].glyph_id = ligature;
            glyphs[position].cluster = cluster;
            glyph_states[position].internal_flags |= GLYPH_SUBSTITUTED;
            glyph_states[position].internal_flags &= ~(GLYPH_MULTIPLIED | GLYPH_MULTIPLE_COMPONENT);
            set_multiple_substitution_component(position, 0u);
            let ligature_id = run_state.reserved0 + 1u;
            run_state.reserved0 = ligature_id;
            glyph_states[position].ligature_id = ligature_id;
            set_ligature_component_count(position, additional_components + 1u);
            var component_number = 1u;
            for (var cursor = position + 1u; cursor <= candidate; cursor++) {
                if (ignore_marks && fallback_mark_glyph(cursor)) {
                    glyph_states[cursor].ligature_id = ligature_id;
                    set_ligature_component(cursor, component_number);
                } else { component_number += 1u; }
            }
            var component = additional_components;
            loop {
                if (component == 0u) { break; }
                remove_fallback_component(components[component]);
                component -= 1u;
            }
            replaced = true;
            break;
        }
        position += 1u;
        if (replaced) { continue; }
    }
}

fn apply_arabic_fallback() {
    apply_arabic_fallback_forms();
    apply_arabic_fallback_ligature_table(5u, 6u, 2u, true);
    apply_arabic_fallback_ligature_table(7u, 8u, 1u, true);
    apply_arabic_fallback_ligature_table(9u, 10u, 1u, false);
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
        var target_serial = 0u;
        if (target_index >= 0) { target_serial = glyph_states[u32(target_index)].serial; }
        if (*task_count >= 64u || depth >= 64u) {
            run_state.status = 2u;
            return false;
        }
        (*tasks)[*task_count] = LookupTask(
            table_u16(record_offset + record * 4u + 2u),
            target_serial,
            position,
            sequence_index,
            lookup_offset,
            lookup_flags,
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
        cursor = previous_context_eligible(cursor, lookup_offset, lookup_flags);
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
                lookahead = next_context_eligible(u32(lookahead) + 1u, lookup_offset, lookup_flags);
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
                lookahead = next_context_eligible(u32(lookahead) + 1u, lookup_offset, lookup_flags);
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
            backtrack = previous_context_eligible(backtrack, lookup_offset, lookup_flags);
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
            lookahead = next_context_eligible(u32(lookahead) + 1u, lookup_offset, lookup_flags);
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
        match_position = previous_context_eligible(match_position, lookup_offset, lookup_flags);
        if (match_position < 0 || coverage_index(subtable + table_u16(cursor + index * 2u),
                glyphs[u32(match_position)].glyph_id) < 0) { return false; }
        match_position -= 1;
    }
    cursor += backtrack_count * 2u;
    let lookahead_count = table_u16(cursor);
    cursor += 2u;
    match_position = i32(position);
    for (var index = 0u; index < lookahead_count; index++) {
        match_position = next_context_eligible(u32(match_position) + 1u, lookup_offset, lookup_flags);
        if (match_position < 0 || coverage_index(subtable + table_u16(cursor + index * 2u),
                glyphs[u32(match_position)].glyph_id) < 0) { return false; }
    }
    cursor += lookahead_count * 2u;
    let glyph_count = table_u16(cursor);
    if (u32(covered) >= glyph_count) { return false; }
    glyphs[position].glyph_id = table_u16(cursor + 2u + u32(covered) * 2u);
    glyph_states[position].internal_flags |= GLYPH_SUBSTITUTED;
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
        glyph_states[position].internal_flags |= GLYPH_SUBSTITUTED;
        return true;
    }
    if (format == 2u && u32(covered) < table_u16(subtable + 4u)) {
        glyphs[position].glyph_id = table_u16(subtable + 6u + u32(covered) * 2u);
        glyph_states[position].internal_flags |= GLYPH_SUBSTITUTED;
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
        glyph_states[position + replacement].internal_flags |= GLYPH_SUBSTITUTED | GLYPH_MULTIPLIED;
        set_multiple_substitution_component(position + replacement, replacement);
        if (replacement != 0u) {
            glyph_states[position + replacement].internal_flags |= GLYPH_MULTIPLE_COMPONENT;
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
    glyph_states[position].internal_flags |= GLYPH_SUBSTITUTED;
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
            let expected = table_u16(ligature + 2u + component * 2u);
            let next = next_ligature_component(
                match_position + 1u, expected, lookup_offset, lookup_flags);
            if (next < 0 || glyphs[u32(next)].glyph_id != expected) {
                matched = false;
                break;
            }
            match_position = u32(next);
        }
        if (!matched) { continue; }
        let cluster = merge_cluster(position, match_position + 1u);
        glyphs[position].glyph_id = table_u16(ligature);
        glyph_states[position].internal_flags |= GLYPH_SUBSTITUTED;
        glyph_states[position].internal_flags &= ~(GLYPH_MULTIPLIED | GLYPH_MULTIPLE_COMPONENT);
        set_multiple_substitution_component(position, 0u);
        glyphs[position].cluster = cluster;
        let ligature_id = run_state.reserved0 + 1u;
        run_state.reserved0 = ligature_id;
        glyph_states[position].ligature_id = ligature_id;
        set_ligature_component_count(position, component_count);
        var component_number = 1u;
        var component_index = 1u;
        var next_component_position = -1;
        if (component_count > 1u) {
            next_component_position = next_ligature_component(
                position + 1u, table_u16(ligature + 4u), lookup_offset, lookup_flags);
        }
        for (var cursor = position + 1u; cursor <= match_position; cursor++) {
            if (i32(cursor) == next_component_position) {
                component_number += 1u;
                component_index += 1u;
                if (component_index < component_count) {
                    next_component_position = next_ligature_component(cursor + 1u,
                        table_u16(ligature + 2u + component_index * 2u), lookup_offset, lookup_flags);
                }
            } else {
                glyph_states[cursor].ligature_id = ligature_id;
                set_ligature_component(cursor, component_number);
            }
        }
        for (var component = 1u; component < component_count; component++) {
            let expected = table_u16(ligature + 2u + component * 2u);
            let remove_at = next_ligature_component(position + 1u, expected, lookup_offset, lookup_flags);
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
fn preprocess_glyphs(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x != 0u) { return; }
    normalize_unicode();
    if (run_state.status != 0u) { return; }
    var index = 1u;
    loop {
        if (index >= run_state.glyph_count) { break; }
        let selector = glyphs[index].codepoint;
        if (!is_variation_selector(selector)) {
            index += 1u;
            continue;
        }
        let selected = variation_glyph(glyphs[index - 1u].codepoint, selector);
        if (selected == 0xfffffffeu) {
            index += 1u;
            continue;
        }
        if (selected != 0xffffffffu) { glyphs[index - 1u].glyph_id = selected; }
        for (var cursor = index + 1u; cursor < run_state.glyph_count; cursor++) {
            glyphs[cursor - 1u] = glyphs[cursor];
            glyph_states[cursor - 1u] = glyph_states[cursor];
        }
        run_state.glyph_count -= 1u;
    }
    apply_vowel_constraints();
    if (run_state.status != 0u) { return; }
    normalize_complex_script_diacritics();
    if (run_state.status != 0u) { return; }
    expand_khmer_split_matras();
    if (run_state.status != 0u) { return; }
    prepare_hangul_shaping();
    if (run_state.status != 0u) { return; }
    prepare_thai_lao();
    if (run_state.status != 0u) { return; }
    apply_directional_codepoint_fallback();
    prepare_complex_syllables();
    if (run_state.status != 0u) { return; }
    initialize_indic_shaping_state();
    initialize_use_shaping_state();
    prepare_khmer_reordering();
    if (run_state.status != 0u) { return; }
    assign_arabic_joining_actions();
}

@compute @workgroup_size(1)
fn execute_lookups(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x != 0u) { return; }
    prepare_fraction_masks();
    var tasks: array<LookupTask, 64>;
    var task_count = 0u;
    var active_stage = 0u;
    for (var command_index = 0u; command_index < params.lookup_count; command_index++) {
        let command = lookup_commands[command_index];
        if (command.table_kind != 1u || command.feature_value == 0u) { continue; }
        if (command.stage != active_stage) {
            handle_substitution_stage_transition(active_stage, command.stage);
            if (run_state.status != 0u) { return; }
            active_stage = command.stage;
        }
        if (is_reverse_lookup(command.lookup_offset, command.lookup_type)) {
            var reverse_position = run_state.glyph_count;
            loop {
                if (reverse_position == 0u) { break; }
                reverse_position -= 1u;
                let cluster = u32(max(glyphs[reverse_position].cluster, 0));
                if (cluster < command.range_start || cluster >= command.range_end ||
                        !feature_allowed(command, reverse_position)) { continue; }
                run_state.reserved1 = glyph_states[reverse_position].syllable;
                run_state.reserved2 = command.command_flags;
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
            run_state.reserved1 = glyph_states[position].syllable;
            run_state.reserved2 = command.command_flags;
            _ = apply_lookup_at(command.lookup_offset, position, command.feature_value,
                command.feature_tag, 0u, &tasks, &task_count);
            loop {
                if (task_count == 0u || run_state.status != 0u) { break; }
                task_count -= 1u;
                let task = tasks[task_count];
                var target_index = find_serial(task.target_serial);
                if (target_index < 0) {
                    target_index = eligible_at(task.origin_position, task.sequence_index,
                        task.context_lookup_offset, task.context_lookup_flags);
                }
                if (target_index < 0) { continue; }
                let nested_lookup = lookup_from_index(table_directory.gsub_offset, task.lookup_index);
                _ = apply_lookup_at(nested_lookup, u32(target_index), task.feature_value,
                    task.feature_tag, task.depth, &tasks, &task_count);
            }
            if (run_state.status != 0u) { return; }
            position += run_state.skip_count;
        }
    }
    handle_substitution_stage_transition(active_stage, 0xffffffffu);
    run_state.reserved1 = 0u;
    run_state.reserved2 = 0u;
}

@compute @workgroup_size(1)
fn finalize_substitutions(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x != 0u) { return; }
    let preserve = (params.request_flags & 4u) != 0u;
    let remove = (params.request_flags & 8u) != 0u;
    if (!preserve) {
        let invisible_glyph = select(nominal_glyph(0x20u), 0u, remove);
        var index = 0u;
        loop {
            if (index >= run_state.glyph_count) { break; }
            if (!is_default_ignorable(glyphs[index].codepoint) ||
                    (glyph_states[index].internal_flags & GLYPH_SUBSTITUTED) != 0u ||
                    (glyph_states[index].feature_mask & HANGUL_FEATURE_MASK) != 0u) {
                index += 1u;
                continue;
            }
            if (invisible_glyph != 0u) {
                glyphs[index].glyph_id = invisible_glyph;
                glyph_states[index].internal_flags |= GLYPH_INVISIBLE;
                index += 1u;
                continue;
            }
            let removed_cluster = glyphs[index].cluster;
            if (index > 0u && (index + 1u >= run_state.glyph_count ||
                    glyphs[index + 1u].cluster != removed_cluster) &&
                    removed_cluster < glyphs[index - 1u].cluster) {
                let previous_cluster = glyphs[index - 1u].cluster;
                var previous = index;
                loop {
                    if (previous == 0u || glyphs[previous - 1u].cluster != previous_cluster) { break; }
                    previous -= 1u;
                    glyphs[previous].cluster = removed_cluster;
                }
            } else if (index == 0u && run_state.glyph_count > 1u) {
                let source_cluster = glyphs[1u].cluster;
                let merged_cluster = min(removed_cluster, source_cluster);
                for (var following = 1u; following < run_state.glyph_count; following++) {
                    if (glyphs[following].cluster != source_cluster) { break; }
                    glyphs[following].cluster = merged_cluster;
                }
            }
            for (var cursor = index + 1u; cursor < run_state.glyph_count; cursor++) {
                glyphs[cursor - 1u] = glyphs[cursor];
                glyph_states[cursor - 1u] = glyph_states[cursor];
            }
            run_state.glyph_count -= 1u;
        }
    }
    restore_split_matra_character_clusters();
}

fn legacy_kern_class(subtable: u32, length: u32, relative: u32, glyph: u32) -> u32 {
    if (relative == 0u || relative + 4u > length) { return 0u; }
    let class_table = subtable + relative;
    let first_glyph = table_u16(class_table);
    let count = table_u16(class_table + 2u);
    if (glyph < first_glyph || glyph - first_glyph >= count ||
            relative + 4u + (glyph - first_glyph + 1u) * 2u > length) { return 0u; }
    return table_u16(class_table + 4u + (glyph - first_glyph) * 2u);
}

fn next_legacy_kern_glyph(start: u32) -> i32 {
    for (var index = start; index < run_state.glyph_count; index++) {
        if (gdef_class(4u, glyphs[index].glyph_id) != 3u) { return i32(index); }
    }
    return -1;
}

fn apply_legacy_kern_adjustment(left: u32, right: u32, kerning: i32, cross_stream: bool) {
    if (kerning == 0) { return; }
    if (cross_stream) {
        glyphs[right].offset_y += kerning;
        return;
    }
    let first = kerning >> 1;
    let second = kerning - first;
    glyphs[left].advance_x += first;
    glyphs[right].advance_x += second;
    glyphs[right].offset_x += second;
}

fn apply_legacy_kern_format0(subtable: u32, header_size: u32, cross_stream: bool) {
    let body = subtable + header_size;
    let pair_count = table_u16(body);
    let records = body + 8u;
    for (var left = 0u; left + 1u < run_state.glyph_count; left++) {
        let right_signed = next_legacy_kern_glyph(left + 1u);
        if (right_signed < 0) { break; }
        let right = u32(right_signed);
        let key_left = glyphs[left].glyph_id;
        let key_right = glyphs[right].glyph_id;
        var low = 0u;
        var high = pair_count;
        var kerning = 0;
        loop {
            if (low >= high) { break; }
            let middle = low + ((high - low) >> 1u);
            let record = records + middle * 6u;
            let record_left = table_u16(record);
            let record_right = table_u16(record + 2u);
            if (key_left < record_left || (key_left == record_left && key_right < record_right)) {
                high = middle;
            } else if (key_left > record_left || key_right > record_right) {
                low = middle + 1u;
            } else {
                kerning = i32(table_u16(record + 4u) << 16u) >> 16;
                break;
            }
        }
        apply_legacy_kern_adjustment(left, right, kerning, cross_stream);
    }
}

fn apply_legacy_kern_format2(subtable: u32, header_size: u32, length: u32, cross_stream: bool) {
    let body = subtable + header_size;
    let left_table = table_u16(body + 2u);
    let right_table = table_u16(body + 4u);
    let array_offset = table_u16(body + 6u);
    for (var left = 0u; left + 1u < run_state.glyph_count; left++) {
        let right_signed = next_legacy_kern_glyph(left + 1u);
        if (right_signed < 0) { break; }
        let right = u32(right_signed);
        let value_offset = legacy_kern_class(subtable, length, left_table, glyphs[left].glyph_id) +
            legacy_kern_class(subtable, length, right_table, glyphs[right].glyph_id);
        var kerning = 0;
        if (value_offset >= array_offset && value_offset + 2u <= length) {
            kerning = i32(table_u16(subtable + value_offset) << 16u) >> 16;
        }
        apply_legacy_kern_adjustment(left, right, kerning, cross_stream);
    }
}

fn apply_legacy_kern(table: u32) {
    let apple = table_u32(table) == 0x00010000u;
    let count = select(table_u16(table + 2u), table_u32(table + 4u), apple);
    var subtable = table + select(4u, 8u, apple);
    for (var index = 0u; index < count; index++) {
        let header_size = select(6u, 8u, apple);
        let length = select(table_u16(subtable + 2u), table_u32(subtable), apple);
        if (length < header_size) { break; }
        let format = table_u8(subtable + select(4u, 5u, apple));
        let coverage = table_u8(subtable + select(5u, 4u, apple));
        let horizontal = select((coverage & 1u) != 0u, (coverage & 0x80u) == 0u, apple);
        let cross_stream = select((coverage & 4u) != 0u, (coverage & 0x40u) != 0u, apple);
        if (horizontal && format == 0u) { apply_legacy_kern_format0(subtable, header_size, cross_stream); }
        else if (horizontal && format == 2u) { apply_legacy_kern_format2(subtable, header_size, length, cross_stream); }
        subtable += length;
    }
}

fn is_unicode_mark(codepoint: u32) -> bool {
    return (unicode_properties_b(codepoint) & 0x100u) != 0u;
}

fn is_positioning_mark(position: u32) -> bool {
    if (table_directory.gdef_length >= 6u &&
            table_u16(table_directory.gdef_offset + 4u) != 0u) {
        return gdef_class(4u, glyphs[position].glyph_id) == 3u;
    }
    let category = (unicode_properties_b(glyphs[position].codepoint) >> 9u) & 0x1fu;
    return category == 5u && !is_default_ignorable(glyphs[position].codepoint);
}

fn uses_fallback_mark_positioning() -> bool {
    return !is_use_syllable_script() && !is_indic_syllable_script() &&
        params.script_tag != 0x6b686d72u && params.script_tag != 0x74686169u &&
        params.script_tag != 0x6c616f20u && params.script_tag != 0x6d796d72u &&
        params.script_tag != 0x6d796d32u && params.script_tag != 0x71616167u;
}

fn zero_mark_advances(adjust_offsets: bool) {
    for (var position = 0u; position < run_state.glyph_count; position++) {
        if (!is_positioning_mark(position)) { continue; }
        if (adjust_offsets) {
            glyphs[position].offset_x -= glyphs[position].advance_x;
            glyphs[position].offset_y -= glyphs[position].advance_y;
        }
        glyphs[position].advance_x = 0;
        glyphs[position].advance_y = 0;
    }
}

fn fallback_combining_class(codepoint: u32) -> u32 {
    var combining = modified_combining_class(codepoint);
    if (combining >= 200u) { return combining; }
    if ((codepoint & ~0xffu) == 0x0e00u) {
        if (combining == 0u) {
            if (codepoint == 0x0e31u || (codepoint >= 0x0e34u && codepoint <= 0x0e37u) ||
                    codepoint == 0x0e47u || (codepoint >= 0x0e4cu && codepoint <= 0x0e4eu)) {
                combining = 232u;
            } else if (codepoint == 0x0eb1u || (codepoint >= 0x0eb4u && codepoint <= 0x0eb7u) ||
                    codepoint == 0x0ebbu || codepoint == 0x0eccu || codepoint == 0x0ecdu) {
                combining = 230u;
            } else if (codepoint == 0x0ebcu) { combining = 220u; }
        } else if (codepoint == 0x0e3au) { combining = 222u; }
    }
    switch combining {
        case 22u: { return 220u; } case 15u: { return 220u; } case 16u: { return 220u; }
        case 17u: { return 220u; } case 23u: { return 220u; } case 18u: { return 220u; }
        case 19u: { return 220u; } case 20u: { return 220u; } case 21u: { return 220u; }
        case 24u: { return 220u; } case 25u: { return 220u; } case 13u: { return 214u; }
        case 10u: { return 232u; } case 11u: { return 228u; } case 14u: { return 228u; }
        case 26u: { return 230u; } case 27u: { return 230u; } case 28u: { return 230u; }
        case 29u: { return 230u; } case 31u: { return 230u; } case 32u: { return 230u; }
        case 34u: { return 230u; } case 35u: { return 230u; } case 36u: { return 230u; }
        case 30u: { return 220u; } case 33u: { return 220u; } case 103u: { return 232u; }
        case 107u: { return 232u; } case 118u: { return 220u; } case 129u: { return 220u; }
        case 132u: { return 220u; } case 122u: { return 230u; } case 130u: { return 230u; }
        default: { return combining; }
    }
}

fn position_fallback_above(base: ptr<function, GlyphExtents>, mark: GlyphExtents, gap: i32) -> i32 {
    var y_offset = (*base).y_bearing - (mark.y_bearing + mark.height);
    if ((gap > 0) != (y_offset > 0)) {
        let correction = -y_offset / 2;
        (*base).y_bearing += correction;
        (*base).height -= correction;
        y_offset += correction;
    }
    (*base).y_bearing -= mark.height;
    (*base).height += mark.height;
    return y_offset;
}

fn position_fallback_mark(position: u32, combining: u32,
    base: ptr<function, GlyphExtents>, gap: i32) {
    let glyph_id = glyphs[position].glyph_id;
    if (glyph_id >= params.metric_count || glyph_extents[glyph_id].is_valid == 0u) { return; }
    let mark = glyph_extents[glyph_id];
    var x_offset = (*base).x_bearing + ((*base).width - mark.width) / 2 - mark.x_bearing;
    if ((combining == 233u || combining == 234u) && params.direction == 1u) {
        x_offset = (*base).x_bearing + (*base).width - mark.width / 2 - mark.x_bearing;
    } else if ((combining == 233u || combining == 234u) && params.direction == 2u) {
        x_offset = (*base).x_bearing - mark.width / 2 - mark.x_bearing;
    } else if (combining == 200u || combining == 218u || combining == 228u) {
        x_offset = (*base).x_bearing - mark.x_bearing;
    } else if (combining == 216u || combining == 222u || combining == 232u) {
        x_offset = (*base).x_bearing + (*base).width - mark.width - mark.x_bearing;
    }
    var y_offset = 0;
    if (combining == 233u || combining == 218u || combining == 220u || combining == 222u) {
        (*base).height -= gap;
    }
    if (combining == 200u || combining == 202u || combining == 218u || combining == 220u ||
            combining == 222u || combining == 233u) {
        y_offset = (*base).y_bearing + (*base).height - mark.y_bearing;
        if ((gap > 0) == (y_offset > 0)) {
            (*base).height -= y_offset;
            y_offset = 0;
        }
        (*base).height += mark.height;
    } else if (combining == 228u || combining == 230u || combining == 232u || combining == 234u) {
        (*base).y_bearing += gap;
        (*base).height -= gap;
        y_offset = position_fallback_above(base, mark, gap);
    } else if (combining == 214u || combining == 216u) {
        y_offset = position_fallback_above(base, mark, gap);
    }
    glyphs[position].offset_x = x_offset;
    glyphs[position].offset_y = y_offset;
}

fn position_fallback_marks_around_base(base_index: u32, end: u32) {
    let base_glyph = glyphs[base_index].glyph_id;
    if (base_glyph >= params.metric_count || glyph_extents[base_glyph].is_valid == 0u) { return; }
    var base = glyph_extents[base_glyph];
    base.y_bearing += glyphs[base_index].offset_y;
    base.x_bearing = 0;
    base.width = glyph_metrics[base_glyph].advance_x;
    var x_offset = 0;
    var y_offset = 0;
    let forward = params.direction == 1u || params.direction == 3u;
    if (forward) {
        x_offset -= glyphs[base_index].advance_x;
        y_offset -= glyphs[base_index].advance_y;
    }
    var last_class = 255u;
    var last_component = 0xffffffffu;
    var class_extents = base;
    var component_extents = base;
    let component_count = ligature_component_count(base_index);
    for (var position = base_index + 1u; position < end; position++) {
        let combining = fallback_combining_class(glyphs[position].codepoint);
        if (combining == 0u) {
            if (forward) {
                x_offset -= glyphs[position].advance_x;
                y_offset -= glyphs[position].advance_y;
            } else {
                x_offset += glyphs[position].advance_x;
                y_offset += glyphs[position].advance_y;
            }
            continue;
        }
        if (component_count > 1u) {
            var component = ligature_component(position);
            if (component == 0u) { component = component_count - 1u; }
            else { component = min(component - 1u, component_count - 1u); }
            if (last_component != component) {
                last_component = component;
                last_class = 255u;
                component_extents = base;
                if (params.direction == 1u) {
                    component_extents.x_bearing += i32(component) * component_extents.width / i32(component_count);
                } else {
                    component_extents.x_bearing +=
                        i32(component_count - 1u - component) * component_extents.width / i32(component_count);
                }
                component_extents.width /= i32(component_count);
            }
        }
        if (last_class != combining) {
            last_class = combining;
            class_extents = component_extents;
        }
        position_fallback_mark(position, combining, &class_extents, i32(params.reserved1) / 16);
        glyphs[position].advance_x = 0;
        glyphs[position].advance_y = 0;
        glyphs[position].offset_x += x_offset;
        glyphs[position].offset_y += y_offset;
    }
}

fn apply_fallback_mark_positioning() {
    var cluster_start = 0u;
    for (var position = 1u; position < run_state.glyph_count; position++) {
        if (is_unicode_mark(glyphs[position].codepoint) ||
                is_default_ignorable(glyphs[position].codepoint)) { continue; }
        if (position - cluster_start >= 2u) {
            var base = cluster_start;
            while (base < position) {
                if (is_unicode_mark(glyphs[base].codepoint)) { base += 1u; continue; }
                var mark_end = base + 1u;
                while (mark_end < position && (is_unicode_mark(glyphs[mark_end].codepoint) ||
                        is_default_ignorable(glyphs[mark_end].codepoint))) { mark_end += 1u; }
                position_fallback_marks_around_base(base, mark_end);
                base = mark_end;
            }
        }
        cluster_start = position;
    }
    if (run_state.glyph_count - cluster_start >= 2u) {
        var base = cluster_start;
        while (base < run_state.glyph_count) {
            if (is_unicode_mark(glyphs[base].codepoint)) { base += 1u; continue; }
            var mark_end = base + 1u;
            while (mark_end < run_state.glyph_count &&
                    (is_unicode_mark(glyphs[mark_end].codepoint) ||
                     is_default_ignorable(glyphs[mark_end].codepoint))) { mark_end += 1u; }
            position_fallback_marks_around_base(base, mark_end);
            base = mark_end;
        }
    }
}

fn is_stretch_action(position: u32) -> bool {
    return (glyph_states[position].feature_mask &
        (ARABIC_STRETCH_FIXED | ARABIC_STRETCH_REPEATING)) != 0u;
}

fn is_arabic_stretch_word_character(codepoint: u32) -> bool {
    let category = (unicode_properties_b(codepoint) >> 9u) & 0x1fu;
    return category == 29u || category == 17u || category == 3u || category == 4u ||
        category == 6u || category == 7u || category == 5u || category == 8u ||
        category == 9u || category == 10u || category == 26u || category == 27u ||
        category == 25u || category == 28u;
}

fn reverse_shaping_records() {
    let middle = run_state.glyph_count >> 1u;
    for (var left = 0u; left < middle; left++) {
        let right = run_state.glyph_count - 1u - left;
        let glyph = glyphs[left];
        let state = glyph_states[left];
        glyphs[left] = glyphs[right];
        glyph_states[left] = glyph_states[right];
        glyphs[right] = glyph;
        glyph_states[right] = state;
    }
}

fn apply_arabic_stretch_core() {
    var scan = run_state.glyph_count;
    while (scan > 0u) {
        if (!is_stretch_action(scan - 1u)) { scan -= 1u; continue; }
        let end = scan;
        var fixed_width = 0;
        var repeating_width = 0;
        var fixed_count = 0u;
        var repeating_count = 0u;
        while (scan > 0u && is_stretch_action(scan - 1u)) {
            scan -= 1u;
            let glyph_id = glyphs[scan].glyph_id;
            var width = 0;
            if (glyph_id < params.metric_count) { width = glyph_metrics[glyph_id].advance_x; }
            if ((glyph_states[scan].feature_mask & ARABIC_STRETCH_FIXED) != 0u) {
                fixed_width += width;
                fixed_count += 1u;
            } else {
                repeating_width += width;
                repeating_count += 1u;
            }
        }
        let start = scan;
        var context = start;
        var total_width = 0;
        while (context > 0u && !is_stretch_action(context - 1u) &&
                (is_default_ignorable(glyphs[context - 1u].codepoint) ||
                 is_arabic_stretch_word_character(glyphs[context - 1u].codepoint))) {
            context -= 1u;
            total_width += glyphs[context].advance_x;
        }
        var remaining = total_width - fixed_width;
        var copies = 0u;
        if (remaining > repeating_width && repeating_width > 0) {
            copies = u32(remaining / repeating_width - 1);
        }
        var overlap = 0;
        let shortfall = remaining - repeating_width * i32(copies + 1u);
        if (shortfall > 0 && repeating_count > 0u) {
            copies += 1u;
            let excess = i32(copies + 1u) * repeating_width - remaining;
            if (excess > 0) {
                overlap = excess / i32(copies * repeating_count);
                remaining = 0;
            }
        }
        let base_count = fixed_count + repeating_count;
        var maximum_copies = 0u;
        if (repeating_count > 0u && base_count < 256u) {
            maximum_copies = (256u - base_count) / repeating_count;
        }
        copies = min(copies, maximum_copies);
        let added = copies * repeating_count;
        if (run_state.glyph_count + added > params.capacity) {
            run_state.status = 1u;
            return;
        }
        if (added > 0u) {
            var move_index = run_state.glyph_count;
            while (move_index > end) {
                move_index -= 1u;
                glyphs[move_index + added] = glyphs[move_index];
                glyph_states[move_index + added] = glyph_states[move_index];
            }
        }
        var write = end + added;
        var x_offset = remaining / 2;
        var source = end;
        while (source > start) {
            source -= 1u;
            var glyph = glyphs[source];
            let state = glyph_states[source];
            let glyph_id = glyph.glyph_id;
            var width = 0;
            if (glyph_id < params.metric_count) { width = glyph_metrics[glyph_id].advance_x; }
            let repeat = select(1u, copies + 1u,
                (state.feature_mask & ARABIC_STRETCH_REPEATING) != 0u);
            glyph.advance_x = 0;
            for (var copy = 0u; copy < repeat; copy++) {
                if (params.direction == 2u) {
                    x_offset -= width;
                    if (copy > 0u) { x_offset += overlap; }
                }
                glyph.offset_x = x_offset;
                write -= 1u;
                glyphs[write] = glyph;
                glyph_states[write] = state;
                if (params.direction != 2u) {
                    x_offset += width;
                    if (copy > 0u) { x_offset -= overlap; }
                }
            }
        }
        run_state.glyph_count += added;
        scan = start;
    }
}

fn apply_arabic_stretch() {
    var found = false;
    for (var position = 0u; position < run_state.glyph_count; position++) {
        if (is_stretch_action(position)) { found = true; break; }
    }
    if (!found) { return; }
    reverse_shaping_records();
    apply_arabic_stretch_core();
    reverse_shaping_records();
}

@compute @workgroup_size(1)
fn execute_positions(@builtin(global_invocation_id) id: vec3<u32>) {
    if (id.x != 0u) { return; }
    run_state.reserved1 = 0u;
    run_state.reserved2 = FEATURE_GPOS_MATCH;
    let has_gpos = table_directory.gpos_length != 0u;
    let forward = params.direction == 1u || params.direction == 3u;
    if (is_use_syllable_script() || params.script_tag == 0x6d796d72u ||
            params.script_tag == 0x6d796d32u) {
        zero_mark_advances(!has_gpos && forward);
    }
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
                var target_index = find_serial(task.target_serial);
                if (target_index < 0) {
                    target_index = eligible_at(task.origin_position, task.sequence_index,
                        task.context_lookup_offset, task.context_lookup_flags);
                }
                if (target_index < 0) { continue; }
                let nested_lookup = lookup_from_index(table_directory.gpos_offset, task.lookup_index);
                _ = apply_gpos_lookup_at(nested_lookup, u32(target_index), task.depth, &tasks, &task_count);
            }
            if (run_state.status != 0u) { return; }
        }
    }
    for (var command_index = 0u; command_index < params.lookup_count; command_index++) {
        let command = lookup_commands[command_index];
        if (command.table_kind == 3u && command.feature_value != 0u) {
            apply_legacy_kern(command.lookup_offset);
            break;
        }
    }
    let fallback_marks = uses_fallback_mark_positioning();
    if (fallback_marks || params.script_tag == 0x74686169u || params.script_tag == 0x6c616f20u) {
        zero_mark_advances(!has_gpos && forward);
    }
    resolve_attachments();
    if (!has_gpos && fallback_marks) { apply_fallback_mark_positioning(); }
    if (uses_arabic_joining()) { apply_arabic_stretch(); }
    run_state.reserved2 = 0u;
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

fn reverse_cursive_minor_offset(index: u32, new_parent: u32, horizontal: bool) {
    if (glyph_states[index].attachment_type != 2u &&
            glyph_states[index].attachment_type != 3u) { return; }

    var current = index;
    var old_parent = glyph_states[current].attachment_chain;
    var previous_minor = select(glyphs[current].offset_x, glyphs[current].offset_y, horizontal);
    glyph_states[current].attachment_chain = -1;
    glyph_states[current].attachment_type = 0u;

    for (var step = 0u; step < run_state.glyph_count; step++) {
        if (old_parent < 0 || u32(old_parent) >= run_state.glyph_count ||
                u32(old_parent) == new_parent) { break; }
        let reversed_index = u32(old_parent);
        let next_parent = glyph_states[reversed_index].attachment_chain;
        let next_minor = select(
            glyphs[reversed_index].offset_x,
            glyphs[reversed_index].offset_y,
            horizontal);
        let next_type = glyph_states[reversed_index].attachment_type;
        if (horizontal) {
            glyphs[reversed_index].offset_y = -previous_minor;
        } else {
            glyphs[reversed_index].offset_x = -previous_minor;
        }
        glyph_states[reversed_index].attachment_chain = i32(current);
        glyph_states[reversed_index].attachment_type = select(3u, 2u, horizontal);
        current = reversed_index;
        previous_minor = next_minor;
        if (next_type != 2u && next_type != 3u) { break; }
        old_parent = next_parent;
    }
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
    let horizontal = params.direction == 1u || params.direction == 2u;
    reverse_cursive_minor_offset(child, parent, horizontal);
    glyph_states[child].attachment_chain = i32(parent);
    if (horizontal) {
        glyph_states[child].attachment_type = 2u;
        glyphs[child].offset_y = select(exit.y - entry.y, entry.y - exit.y, rtl_lookup);
    } else {
        glyph_states[child].attachment_type = 3u;
        glyphs[child].offset_x = select(exit.x - entry.x, entry.x - exit.x, rtl_lookup);
    }
    if (glyph_states[parent].attachment_chain == i32(child) &&
            glyph_states[parent].attachment_type == glyph_states[child].attachment_type) {
        glyph_states[parent].attachment_chain = -1;
        glyph_states[parent].attachment_type = 0u;
        if (horizontal) {
            glyphs[parent].offset_y = 0;
        } else {
            glyphs[parent].offset_x = 0;
        }
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
    if (lookup_type == 6u && ligature_component(position) != 0u &&
            ligature_component(attachment_target) != 0u &&
            ligature_component(position) != ligature_component(attachment_target)) { return false; }
    let target_covered = coverage_index(target_coverage, glyphs[attachment_target].glyph_id);
    if (target_covered < 0) { return false; }
    if (lookup_type == 5u) {
        let ligature_attach = target_array + table_u16(target_array + 2u + u32(target_covered) * 2u);
        let component_count = table_u16(ligature_attach);
        if (component_count == 0u) { return false; }
        var component = component_count - 1u;
        if (ligature_component(position) != 0u) {
            component = min(ligature_component(position) - 1u, component);
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
    glyph_states[index] = GlyphState(
        index + 1u, 0u, 0u, 0, 0u, ARABIC_NONE << ARABIC_ACTION_SHIFT, 0u, 0u);
}

@compute @workgroup_size(64)
fn load_metrics(@builtin(global_invocation_id) id: vec3<u32>) {
    let index = id.x;
    if (index >= run_state.glyph_count) { return; }
    let glyph_id = glyphs[index].glyph_id;
    if (glyph_id >= params.metric_count) { return; }
    if ((glyph_states[index].internal_flags & GLYPH_INVISIBLE) != 0u) { return; }
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
