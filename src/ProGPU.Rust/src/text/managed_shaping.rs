//! Complete original managed shaping record, independent of native packed flags.
/// All original C# GlyphRecord fields. Default is the original zero-initialized
/// record; `new` selects the original constructor's source/attachment sentinels.
/// Raw private bytes retain their exact values until the corresponding stage runs.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ManagedShapingGlyph {
    /// Original GlyphIndex field.
    pub glyph_index: u16,
    /// Original Cluster field.
    pub cluster: i32,
    /// Original GraphemeCluster field.
    pub grapheme_cluster: i32,
    /// Original CharacterCluster field.
    pub character_cluster: i32,
    /// Original CodePoint field.
    pub code_point: u32,
    /// Original Flags field.
    pub flags: u32,
    /// Original AdvanceX field.
    pub advance_x: i16,
    /// Original AdvanceY field.
    pub advance_y: i16,
    /// Original OffsetX field.
    pub offset_x: i16,
    /// Original OffsetY field.
    pub offset_y: i16,
    /// Original ArabicAction field.
    pub arabic_action: u8,
    /// Original SpaceFallback field.
    pub space_fallback: u8,
    /// Original FractionAction field.
    pub fraction_action: u8,
    /// Original ScriptFeatureMask field.
    pub script_feature_mask: u8,
    /// Original ScriptShaper field.
    pub script_shaper: u8,
    /// Original UseCategory field.
    pub use_category: u8,
    /// Original UseSyllable field.
    pub use_syllable: u8,
    /// Original IndicCategory field.
    pub indic_category: u8,
    /// Original IndicPosition field.
    pub indic_position: u8,
    /// Original IndicOriginalOrder field.
    pub indic_original_order: u8,
    /// Original UseRphfEligible field.
    pub use_rphf_eligible: u8,
    /// Original Substituted field.
    pub substituted: u8,
    /// Original Ligated field.
    pub ligated: u8,
    /// Original MultipleSubstitutionComponent field.
    pub multiple_substitution_component: u8,
    /// Original Multiplied field.
    pub multiplied: u8,
    /// Original LigatureComponentCount field.
    pub ligature_component_count: u8,
    /// Original LigatureComponent field.
    pub ligature_component: u8,
    /// Original HasCharacterCluster field.
    pub has_character_cluster: u8,
    /// Original Positioned field.
    pub positioned: u8,
    /// Original AttachmentKind field.
    pub attachment_kind: u8,
    /// Original AttachmentTarget field.
    pub attachment_target: i32,
}
impl ManagedShapingGlyph {
    /// Original GlyphRecord constructor, retaining source cluster and sentinels.
    pub fn new(glyph_index: u16, cluster: i32, code_point: u32) -> Self {
        Self {
            glyph_index,
            cluster,
            grapheme_cluster: cluster,
            code_point,
            arabic_action: 7,
            ligature_component: u8::MAX,
            attachment_target: -1,
            ..Self::default()
        }
    }
}
