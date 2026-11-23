//! Original bounded native Boolean program classifier and GPU record writer.
use super::*;
pub(crate) const PROGRAM: u32 = 0x8000_0000;
pub(crate) const SIGNED: u32 = 0x4000_0000;
pub(crate) struct Reference {
    pub path_record_index: u32,
    pub program_index: u32,
    pub operation_kind: u32,
    pub split_leaf_count: u32,
}
pub(crate) fn append(
    path: &wire::PathFill,
    nodes: &[wire::PathBooleanNode],
    segments: &[wire::PathSegment],
    records: &mut Vec<GpuPathRecord>,
) -> Result<Reference, Error> {
    let start = count_u32(records.len())?;
    if path.boolean_node_count == 0 {
        push(
            records,
            GpuPathRecord {
                start_segment: u32::try_from(path.segment_offset).map_err(|_| Error::Capacity)?,
                segment_count: u32::try_from(path.segment_count).map_err(|_| Error::Capacity)?,
                bounds: [path.min_x, path.min_y, path.max_x, path.max_y],
                fill_rule: path.fill_rule,
                pad: 0,
            },
        )?;
        return Ok(Reference {
            path_record_index: start,
            program_index: 0,
            operation_kind: 0,
            split_leaf_count: 0,
        });
    }
    let nodes = nodes
        .get(
            path.boolean_node_offset as usize
                ..(path.boolean_node_offset + path.boolean_node_count) as usize,
        )
        .ok_or(Error::InvalidInput)?;
    let mut tokens = [0u32; 63];
    let mut leaves = 0u32;
    let mut signed = false;
    for (i, node) in nodes.iter().enumerate() {
        if matches!(node.kind, PATH_BOOLEAN_LEAF | PATH_BOOLEAN_WINDING_LEAF) {
            tokens[i] = leaves;
            leaves += 1;
            let winding = node.kind == PATH_BOOLEAN_WINDING_LEAF;
            if winding {
                tokens[i] |= 0x2000_0000;
                signed = true;
            }
            push(
                records,
                GpuPathRecord {
                    start_segment: u32::try_from(node.segment_offset)
                        .map_err(|_| Error::Capacity)?,
                    segment_count: u32::try_from(node.segment_count)
                        .map_err(|_| Error::Capacity)?,
                    bounds: [node.min_x, node.min_y, node.max_x, node.max_y],
                    fill_rule: node.fill_rule,
                    pad: u32::from(winding),
                },
            )?;
        } else if node.kind == PATH_BOOLEAN_EMPTY {
            tokens[i] = SIGNED;
        } else {
            tokens[i] = PROGRAM | (node.kind - 1);
            signed |= matches!(
                node.kind,
                PATH_BOOLEAN_WINDING_ADD | PATH_BOOLEAN_WINDING_NEGATE
            );
        }
    }
    let split = signed || left_xor(nodes) || translated(nodes, segments);
    let program = count_u32(records.len())?;
    for &token in &tokens[..nodes.len()] {
        push(
            records,
            GpuPathRecord {
                start_segment: token,
                segment_count: 0,
                bounds: [0.0; 4],
                fill_rule: 0,
                pad: 0,
            },
        )?;
    }
    Ok(Reference {
        path_record_index: start,
        program_index: program,
        operation_kind: PROGRAM | if signed { SIGNED } else { 0 } | nodes.len() as u32,
        split_leaf_count: if split { leaves } else { 0 },
    })
}
fn left_xor(nodes: &[wire::PathBooleanNode]) -> bool {
    nodes.len() >= 3
        && nodes.len() % 2 == 1
        && nodes[0].kind == PATH_BOOLEAN_LEAF
        && nodes[1..]
            .chunks_exact(2)
            .all(|n| n[0].kind == PATH_BOOLEAN_LEAF && n[1].kind == PATH_BOOLEAN_XOR)
}
fn near(a: f32, b: f32) -> bool {
    (a - b).abs() <= 0.00001 * 1.0f32.max(a.abs().max(b.abs()))
}
fn point(a: wire::Point, b: wire::Point, dx: f32, dy: f32) -> bool {
    near(a.x + dx, b.x) && near(a.y + dy, b.y)
}
fn invariant(a: wire::Point, b: wire::Point) -> bool {
    near(a.x, b.x) && near(a.y, b.y)
}
fn equal(a: &wire::PathSegment, b: &wire::PathSegment, dx: f32, dy: f32) -> bool {
    if a.kind != b.kind
        || a.pad0 != b.pad0
        || a.pad1 != b.pad1
        || a.pad2 != b.pad2
        || !point(a.p0, b.p0, dx, dy)
        || !point(a.p1, b.p1, dx, dy)
    {
        return false;
    }
    match a.kind {
        PATH_SEGMENT_LINE => invariant(a.p2, b.p2) && invariant(a.p3, b.p3),
        PATH_SEGMENT_QUADRATIC | PATH_SEGMENT_ARC => {
            point(a.p2, b.p2, dx, dy) && invariant(a.p3, b.p3)
        }
        PATH_SEGMENT_CUBIC => point(a.p2, b.p2, dx, dy) && point(a.p3, b.p3, dx, dy),
        _ => false,
    }
}
fn translated(nodes: &[wire::PathBooleanNode], segments: &[wire::PathSegment]) -> bool {
    if segments.is_empty() || nodes.len() < 2 {
        return false;
    }
    let mut comparisons = 0;
    for (i, a) in nodes.iter().enumerate() {
        if a.kind != PATH_BOOLEAN_LEAF
            || a.segment_count == 0
            || a.segment_offset > segments.len() as u64
            || a.segment_count > segments.len() as u64 - a.segment_offset
        {
            continue;
        }
        for b in &nodes[i + 1..] {
            if b.kind != PATH_BOOLEAN_LEAF
                || a.segment_count != b.segment_count
                || b.segment_offset > segments.len() as u64
                || b.segment_count > segments.len() as u64 - b.segment_offset
                || a.min_x.max(b.min_x) >= a.max_x.min(b.max_x)
                || a.min_y.max(b.min_y) >= a.max_y.min(b.max_y)
                || !near(a.max_x - a.min_x, b.max_x - b.min_x)
                || !near(a.max_y - a.min_y, b.max_y - b.min_y)
            {
                continue;
            }
            let first = segments[a.segment_offset as usize];
            let second = segments[b.segment_offset as usize];
            let (dx, dy) = (second.p0.x - first.p0.x, second.p0.y - first.p0.y);
            if near(dx, 0.0) && near(dy, 0.0) {
                continue;
            }
            let mut equivalent = true;
            for offset in 0..a.segment_count as usize {
                if comparisons == 1 << 20 {
                    return true;
                }
                comparisons += 1;
                if !equal(
                    &segments[a.segment_offset as usize + offset],
                    &segments[b.segment_offset as usize + offset],
                    dx,
                    dy,
                ) {
                    equivalent = false;
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
