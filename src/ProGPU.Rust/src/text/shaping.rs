/// Original 32-byte native shaping record. Positions and advances retain their
/// caller-selected units; glyph execution does not scale or reconstruct them.
/// Raw flags include public dependency bits and original private shaper metadata.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct ShapingGlyph {
    /// Original glyph ID; IDs above 65535 are ineligible for OpenType lookups.
    pub glyph_id: u32,
    /// Original Unicode code point, including caller-provided non-scalars.
    pub code_point: u32,
    /// Original signed source cluster.
    pub cluster: i32,
    /// Exact dependency and shaper metadata bits, without sanitization.
    pub flags: u32,
    /// Original horizontal advance or the explicit substitution provenance sentinel.
    pub advance_x: i32,
    /// Original vertical advance.
    pub advance_y: i32,
    /// Original horizontal offset.
    pub offset_x: i32,
    /// Original vertical offset.
    pub offset_y: i32,
}
