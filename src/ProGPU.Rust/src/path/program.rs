//! Original native postfix validation, leaf records and split-program admission.
use super::*;
use crate::vector::GpuPathRecord;

pub(crate) const PROGRAM: u32 = 0x8000_0000;
pub(crate) const SIGNED: u32 = 0x4000_0000;
pub(crate) const WINDING: u32 = 0x2000_0000;
pub(crate) const VALUE: u32 = 0x1fff_ffff;

/// One original native postfix instruction. Leaves own their immutable geometry;
/// nested programs must be expressed explicitly in this instruction stream.
#[derive(Clone, Debug)]
pub enum PathInstruction {
    /// Original fill-rule predicate, represented as zero or one inside a signed sum.
    Leaf(Path),
    /// Original signed nonzero winding, retaining negative and even magnitudes.
    Winding(Path),
    /// Original empty stack value.
    Empty,
    /// Left operand excluding right.
    Difference,
    /// Intersection of both operands.
    Intersect,
    /// Union of both operands.
    Union,
    /// Exclusive-or of both operands.
    Xor,
    /// Right operand excluding left.
    ReverseDifference,
    /// Add original signed winding without reducing either operand to a predicate.
    Add,
    /// Negate the existing top stack value without consuming another operand.
    Negate,
}
impl PathInstruction {
    fn operation(&self) -> Option<u32> {
        Some(match self {
            Self::Difference => 1,
            Self::Intersect => 2,
            Self::Union => 3,
            Self::Xor => 4,
            Self::ReverseDifference => 5,
            Self::Add => 7,
            Self::Negate => 8,
            _ => return None,
        })
    }
}

/// Original native signed-program execution flag. Ordinary Boolean splitting
/// remains independently selected by the original source classifier.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum SignedWindingPolicy {
    /// Original default inline signed evaluator; proven whole-line batches use
    /// its separate canonical linear-winding entry point.
    #[default]
    Inline,
    /// Original explicit leaf, signed-row evaluation and coverage-pack phases.
    Staged,
}

impl Path {
    /// Freeze a complete original native postfix program (1..=63 instructions,
    /// stack depth <=16). Signed instructions require a Nonzero root and raw
    /// winding leaves require Nonzero input. No contours or bounds are inferred.
    /// At least one nonempty leaf is required by the retained Path contract.
    pub fn from_program(
        instructions: &[PathInstruction],
        bounds: [f32; 4],
        fill_rule: FillRule,
    ) -> Result<Self, Error> {
        if instructions.is_empty() || instructions.len() > 63 {
            return Err(Error::InvalidInput);
        }
        let mut depth = 0;
        let mut signed = false;
        let mut total = 0usize;
        // Whole-inventory validation precedes copying any source generation.
        for instruction in instructions {
            match instruction {
                PathInstruction::Leaf(path) | PathInstruction::Winding(path) => {
                    let raw = matches!(instruction, PathInstruction::Winding(_));
                    if path.program.is_some()
                        || path.bounds[2] <= path.bounds[0]
                        || path.bounds[3] <= path.bounds[1]
                        || raw && path.fill_rule != FillRule::NonZero
                    {
                        return Err(Error::InvalidInput);
                    }
                    total = total
                        .checked_add(path.segments.len())
                        .ok_or(Error::Capacity)?;
                    if total > 1 << 20 {
                        return Err(Error::Capacity);
                    }
                    signed |= raw;
                    depth += 1;
                }
                PathInstruction::Empty => depth += 1,
                PathInstruction::Negate => {
                    if depth == 0 {
                        return Err(Error::InvalidInput);
                    }
                    signed = true;
                }
                _ => {
                    if depth < 2 {
                        return Err(Error::InvalidInput);
                    }
                    depth -= 1;
                    signed |= matches!(instruction, PathInstruction::Add);
                }
            }
            if depth > 16 {
                return Err(Error::InvalidInput);
            }
        }
        if depth != 1 || total == 0 || signed && fill_rule != FillRule::NonZero {
            return Err(Error::InvalidInput);
        }
        let mut segments = Vec::new();
        segments
            .try_reserve_exact(total)
            .map_err(|_| Error::Capacity)?;
        let mut records = Vec::new();
        let mut tokens = Vec::new();
        for instruction in instructions {
            let token = match instruction {
                PathInstruction::Leaf(path) | PathInstruction::Winding(path) => {
                    let raw = matches!(instruction, PathInstruction::Winding(_));
                    let index = records.len() as u32;
                    records.push(GpuPathRecord {
                        start_segment: segments.len() as u32,
                        segment_count: path.segments.len() as u32,
                        bounds: path.bounds,
                        fill_rule: path.fill_rule as u32,
                        pad: u32::from(raw),
                    });
                    segments.extend_from_slice(&path.segments);
                    index | if raw { WINDING } else { 0 }
                }
                PathInstruction::Empty => SIGNED,
                _ => PROGRAM | instruction.operation().ok_or(Error::InvalidInput)?,
            };
            tokens.push(GpuPathRecord {
                start_segment: token,
                ..Zeroable::zeroed()
            });
        }
        let split = signed || left_fold_xor(instructions) || translated_leaves(&segments, &records);
        Self::with_program(
            segments,
            bounds,
            fill_rule,
            RasterProgram {
                index_b: records.len() as u32,
                operation: PROGRAM | if signed { SIGNED } else { 0 } | tokens.len() as u32,
                records: records.into_boxed_slice(),
                tokens: tokens.into_boxed_slice(),
                split,
            },
        )
    }
    /// Whether this immutable program retains raw signed winding operations.
    pub fn has_signed_winding(&self) -> bool {
        self.program
            .as_ref()
            .is_some_and(|p| p.operation & SIGNED != 0)
    }
    /// Original native split-program classifier result. Signed programs use
    /// this candidate only when the explicit staged policy is selected.
    pub fn requires_split_program(&self) -> bool {
        self.program.as_ref().is_some_and(|p| p.split)
    }
}
fn left_fold_xor(nodes: &[PathInstruction]) -> bool {
    nodes.len() >= 3
        && nodes.len() % 2 == 1
        && matches!(nodes[0], PathInstruction::Leaf(_))
        && nodes[1..].chunks_exact(2).all(|pair| {
            matches!(pair[0], PathInstruction::Leaf(_)) && matches!(pair[1], PathInstruction::Xor)
        })
}
fn near(a: f32, b: f32) -> bool {
    (a - b).abs() <= 0.00001 * 1.0f32.max(a.abs().max(b.abs()))
}
fn translated_leaves(segments: &[Segment], records: &[GpuPathRecord]) -> bool {
    // Original bounded early-exit structural classifier: dependent comparisons
    // and kind-selected invariant fields retain their scalar traversal order.
    let mut comparisons = 0;
    for (i, a) in records.iter().enumerate() {
        if a.pad != 0 {
            continue;
        }
        for b in &records[i + 1..] {
            if b.pad != 0
                || a.segment_count != b.segment_count
                || a.bounds[0].max(b.bounds[0]) >= a.bounds[2].min(b.bounds[2])
                || a.bounds[1].max(b.bounds[1]) >= a.bounds[3].min(b.bounds[3])
                || !near(a.bounds[2] - a.bounds[0], b.bounds[2] - b.bounds[0])
                || !near(a.bounds[3] - a.bounds[1], b.bounds[3] - b.bounds[1])
            {
                continue;
            }
            let first = &segments[a.start_segment as usize..][..a.segment_count as usize];
            let second = &segments[b.start_segment as usize..][..b.segment_count as usize];
            let delta = [
                second[0].points[0][0] - first[0].points[0][0],
                second[0].points[0][1] - first[0].points[0][1],
            ];
            if near(delta[0], 0.0) && near(delta[1], 0.0) {
                continue;
            }
            let mut equivalent = true;
            for (left, right) in first.iter().zip(second) {
                if comparisons == 1 << 20 {
                    return true;
                }
                comparisons += 1;
                let translated = match left.kind {
                    0 => 2,
                    1 | 3 => 3,
                    2 => 4,
                    _ => 0,
                };
                if left.kind != right.kind || left.parameters != right.parameters || translated == 0
                {
                    equivalent = false;
                    break;
                }
                for p in 0..4 {
                    if !(0..2).all(|axis| {
                        near(
                            left.points[p][axis] + if p < translated { delta[axis] } else { 0.0 },
                            right.points[p][axis],
                        )
                    }) {
                        equivalent = false;
                        break;
                    }
                }
                if !equivalent {
                    break;
                }
            }
            if equivalent {
                return true;
            }
        }
    }
    false
}
