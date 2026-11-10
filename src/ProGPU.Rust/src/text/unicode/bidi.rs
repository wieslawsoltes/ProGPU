//! Original ProGPU UAX #9 level resolver with bounded caller-owned scratch.
//! This preserves logical source order. Line wrapping, L2 cluster reordering and
//! glyph mirroring are separate stages; no displayed glyph order is fabricated.
use super::{BidiClass, DecodedText, UnicodeError, UnicodeScalar, allocate};
mod managed;
mod resolve;
pub use managed::{
    ManagedBidiParagraph, ManagedBidiRun, managed_visual_order, managed_visual_order_if_needed,
};

/// Original eight-byte resolved level record in logical source order.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct BidiLevel {
    /// Original byte/code-unit source index, unchanged by resolution.
    pub input_index: u32,
    /// Original source length, without normalization or range reconstruction.
    pub input_length: u16,
    /// Resolved embedding level, including retained explicit controls.
    pub level: i8,
    /// Original reserved byte, emitted as zero.
    pub reserved: u8,
}
/// Original mutable unit scratch. Every used slot is initialized by resolution.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BidiUnit {
    /// Original raw Unicode value; invalid values use the original L property.
    pub code_point: u32,
    /// Original source index.
    pub input_index: u32,
    /// Original source length.
    pub input_length: u16,
    /// Immutable source bidi class.
    pub original: BidiClass,
    /// Working class, updated by the original rules.
    pub resolved_type: BidiClass,
    /// Working embedding level.
    pub level: i8,
    /// Original reserved byte, initialized to zero.
    pub reserved: u8,
    /// Matching isolate slot, or -1.
    pub matching_isolate: i32,
}
impl Default for BidiUnit {
    fn default() -> Self {
        Self {
            code_point: 0,
            input_index: 0,
            input_length: 0,
            original: BidiClass::LeftToRight,
            resolved_type: BidiClass::LeftToRight,
            level: 0,
            reserved: 0,
            matching_isolate: -1,
        }
    }
}
/// Original level-run scratch used to join isolating run sequences.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BidiLevelRun {
    /// First active-index position.
    pub active_start: u32,
    /// Number of active-index positions.
    pub active_count: u32,
    /// Following run slot, or -1.
    pub next: i32,
    /// Original explicit level before implicit rules.
    pub explicit_level: i8,
    /// Whether an isolate run points to this run.
    pub has_predecessor: bool,
    /// Original reserved byte, initialized to zero on used slots.
    pub reserved0: u8,
    /// Original reserved byte, initialized to zero on used slots.
    pub reserved1: u8,
}
impl Default for BidiLevelRun {
    fn default() -> Self {
        Self {
            active_start: 0,
            active_count: 0,
            next: -1,
            explicit_level: 0,
            has_predecessor: false,
            reserved0: 0,
            reserved1: 0,
        }
    }
}
/// Original positions in an isolating sequence, not scalar/source coordinates.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct BidiBracketPair {
    /// Opening bracket position.
    pub open_position: u32,
    /// Closing bracket position.
    pub close_position: u32,
}
/// Exact original per-scalar scratch requirements, independent of scalar values.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BidiRequirements {
    /// One working unit per scalar.
    pub unit_count: u32,
    /// Four independent index lanes per scalar.
    pub index_count: u32,
    /// At most one level run per scalar.
    pub run_count: u32,
    /// At most floor(N/2) bracket pairs.
    pub bracket_pair_count: u32,
}
/// Reusable caller-provided scratch; capacities are checked before any mutation.
pub struct BidiScratch<'a> {
    /// Unit storage.
    pub units: &'a mut [BidiUnit],
    /// Isolate/active/run/sequence index storage.
    pub indices: &'a mut [u32],
    /// Level-run storage.
    pub runs: &'a mut [BidiLevelRun],
    /// Bracket-pair storage.
    pub bracket_pairs: &'a mut [BidiBracketPair],
}
/// Successful original paragraph resolution.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct BidiResolution {
    /// Resolved paragraph level, either zero or one.
    pub paragraph_level: i8,
    /// Number of output scalar records.
    pub written: u32,
}
/// Compute the original bounded requirement inventory without reading properties.
pub fn requirements(scalar_count: usize) -> Result<BidiRequirements, UnicodeError> {
    if scalar_count > (u32::MAX / 4) as usize {
        return Err(UnicodeError::InvalidArgument);
    }
    let count = scalar_count as u32;
    Ok(BidiRequirements {
        unit_count: count,
        index_count: count * 4,
        run_count: count,
        bracket_pair_count: count / 2,
    })
}
/// Resolve complete logical levels with no heap allocation. Requests 0/1 select
/// a direction; every other i8 value performs original first-strong inference.
/// Raw code points and source ranges retain the original permissive admission.
/// Every capacity is preflighted before writing scratch/output; tails stay intact.
pub fn resolve_into(
    input: &[UnicodeScalar],
    requested_paragraph_level: i8,
    scratch: BidiScratch<'_>,
    output: &mut [BidiLevel],
) -> Result<BidiResolution, UnicodeError> {
    let r = requirements(input.len())?;
    if scratch.units.len() < r.unit_count as usize
        || scratch.indices.len() < r.index_count as usize
        || scratch.runs.len() < r.run_count as usize
        || scratch.bracket_pairs.len() < r.bracket_pair_count as usize
        || output.len() < r.unit_count as usize
    {
        return Err(UnicodeError::Capacity);
    }
    let mut paragraph_level = i8::from(requested_paragraph_level == 1);
    if !input.is_empty() {
        paragraph_level = resolve::run(
            input,
            requested_paragraph_level,
            BidiScratch {
                units: &mut scratch.units[..input.len()],
                indices: &mut scratch.indices[..r.index_count as usize],
                runs: &mut scratch.runs[..input.len()],
                bracket_pairs: &mut scratch.bracket_pairs[..r.bracket_pair_count as usize],
            },
            output,
        );
    }
    Ok(BidiResolution {
        paragraph_level,
        written: r.unit_count,
    })
}
/// Allocated scratch generation reusable across equal or smaller paragraphs.
/// Construction is separate from the allocation-free resolving hot path.
pub struct BidiWorkspace {
    units: Vec<BidiUnit>,
    indices: Vec<u32>,
    runs: Vec<BidiLevelRun>,
    pairs: Vec<BidiBracketPair>,
}
impl BidiWorkspace {
    /// Allocate the exact original scratch inventory for a maximum scalar count.
    pub fn new(scalar_capacity: usize) -> Result<Self, UnicodeError> {
        let r = requirements(scalar_capacity)?;
        Ok(Self {
            units: allocate(r.unit_count as usize)?,
            indices: allocate(r.index_count as usize)?,
            runs: allocate(r.run_count as usize)?,
            pairs: allocate(r.bracket_pair_count as usize)?,
        })
    }
    /// Resolve with this generation's capacity; does not silently resize or allocate.
    pub fn resolve_into(
        &mut self,
        input: &[UnicodeScalar],
        requested_paragraph_level: i8,
        output: &mut [BidiLevel],
    ) -> Result<BidiResolution, UnicodeError> {
        resolve_into(
            input,
            requested_paragraph_level,
            BidiScratch {
                units: &mut self.units,
                indices: &mut self.indices,
                runs: &mut self.runs,
                bracket_pairs: &mut self.pairs,
            },
            output,
        )
    }
}
/// Immutable original encoded text, decoded scalar generation and resolved levels.
#[derive(Clone, Debug)]
pub struct BidiParagraph {
    text: DecodedText,
    levels: Vec<BidiLevel>,
    paragraph_level: i8,
}
impl BidiParagraph {
    /// Resolve and retain a complete decoded generation. The strict decoder's
    /// admission remains separate from raw `resolve_into`'s original policy.
    pub fn new(text: DecodedText, requested_paragraph_level: i8) -> Result<Self, UnicodeError> {
        let mut workspace = BidiWorkspace::new(text.scalars().len())?;
        let mut levels = allocate(text.scalars().len())?;
        let result =
            workspace.resolve_into(text.scalars(), requested_paragraph_level, &mut levels)?;
        Ok(Self {
            text,
            levels,
            paragraph_level: result.paragraph_level,
        })
    }
    /// Original immutable encoded and scalar source.
    pub fn text(&self) -> &DecodedText {
        &self.text
    }
    /// Levels in original logical source order, including formatting controls.
    pub fn levels(&self) -> &[BidiLevel] {
        &self.levels
    }
    /// Resolved paragraph direction, zero for LTR and one for RTL.
    pub fn paragraph_level(&self) -> i8 {
        self.paragraph_level
    }
}
