//! Preserve original static vertex order while removing raster-only temporaries.
use super::*;
pub(super) struct Context {
    pub marked: bool,
    pub group: u64,
}
pub(super) fn finish(
    compiled: &mut CompiledScene,
    contexts: &[Context],
) -> Result<Vec<StaticDxfBatch>, Error> {
    if compiled.states.len()
        != contexts
            .len()
            .checked_mul(2)
            .and_then(|v| v.checked_add(1))
            .ok_or(Error::Capacity)?
    {
        return Err(Error::ResourceDomain);
    }
    let mut retained = Vec::new();
    retained
        .try_reserve_exact(compiled.mesh.vertices().len())
        .map_err(|_| Error::Capacity)?;
    retained.resize(compiled.mesh.vertices().len(), None::<bool>);
    let mut ranges = Vec::new();
    ranges
        .try_reserve_exact(compiled.draws.len())
        .map_err(|_| Error::Capacity)?;
    let mut context = None;
    for draw in &compiled.draws {
        let (kind, range) = match draw {
            DrawRange::State(index) => {
                context = if index % 2 == 1 {
                    contexts.get((index - 1) / 2)
                } else {
                    None
                };
                continue;
            }
            DrawRange::Vector(range) => (StaticDxfBatchKind::Vector, range),
            DrawRange::Hatch(range) => (StaticDxfBatchKind::Hatch, range),
            #[cfg(feature = "gpu")]
            DrawRange::Text(range) => (StaticDxfBatchKind::Text, range),
            _ => return Err(Error::UnsupportedExecution),
        };
        let context = context.ok_or(Error::ResourceDomain)?;
        if kind == StaticDxfBatchKind::Text {
            ranges.push((kind, range.clone(), context.group));
            continue;
        }
        for old in compiled
            .mesh
            .indices()
            .get(range.start as usize..range.end as usize)
            .ok_or(Error::ResourceDomain)?
        {
            let slot = retained
                .get_mut(*old as usize)
                .ok_or(Error::ResourceDomain)?;
            if slot.is_some_and(|mark| mark != context.marked) {
                return Err(Error::ResourceDomain);
            }
            *slot = Some(context.marked);
        }
        ranges.push((kind, range.clone(), context.group));
    }
    let mut map = Vec::new();
    map.try_reserve_exact(retained.len())
        .map_err(|_| Error::Capacity)?;
    map.resize(retained.len(), u32::MAX);
    let mut vertices = Vec::new();
    vertices
        .try_reserve_exact(retained.iter().filter(|v| v.is_some()).count())
        .map_err(|_| Error::Capacity)?;
    let mut next = 0u32;
    for (old, retained) in retained.iter().enumerate() {
        if retained.is_some() {
            map[old] = next;
            next = next.checked_add(1).ok_or(Error::Capacity)?;
        }
    }
    for (old, mark) in retained.iter().enumerate() {
        if let Some(mark) = mark {
            let mut vertex = compiled.mesh.vertices()[old];
            // Only this occurrence's private helper flag is removed. Raw shape
            // values never serve as a guessed witness of source camera history.
            if *mark {
                vertex.shape_type -= 100.0;
            }
            // Four original descriptor families encode the actual vertex base
            // in a float field. Rebase that integer identity with the indices;
            // geometric coordinates and all other payload bits stay unchanged.
            let kind = if vertex.shape_type >= 1000.0 {
                vertex.shape_type - 1000.0
            } else {
                vertex.shape_type
            };
            let ordinal = match kind {
                5.0 | 6.0 => Some(&mut vertex.corner_radius),
                22.0 => Some(&mut vertex.shape_size[0]),
                23.0 => Some(&mut vertex.color[2]),
                _ => None,
            };
            if let Some(ordinal) = ordinal {
                if !ordinal.is_finite()
                    || *ordinal < 0.0
                    || ordinal.fract() != 0.0
                    || *ordinal >= (1 << 24) as f32
                {
                    return Err(Error::ResourceDomain);
                }
                let mapped = *map.get(*ordinal as usize).ok_or(Error::ResourceDomain)?;
                if mapped == u32::MAX || mapped >= 1 << 24 {
                    return Err(Error::ResourceDomain);
                }
                *ordinal = mapped as f32;
            }
            vertex.shape_type += 200.0;
            vertices.push(vertex);
        }
    }
    let mut indices = Vec::new();
    indices
        .try_reserve_exact(compiled.mesh.indices().len())
        .map_err(|_| Error::Capacity)?;
    let mut batches: Vec<StaticDxfBatch> = Vec::new();
    batches
        .try_reserve_exact(ranges.len())
        .map_err(|_| Error::Capacity)?;
    let mut last_group = None;
    // Original CommitStaticDrawCalls flushes the vector stream first and the
    // text stream second within each extension-delimited group. Preserve each
    // stream's own monotonically increasing source ranges.
    ranges.sort_unstable_by_key(|(kind, range, group)| {
        (
            *group,
            u8::from(*kind == StaticDxfBatchKind::Text),
            range.start,
        )
    });
    for (kind, range, group) in ranges {
        if kind == StaticDxfBatchKind::Text {
            if last_group == Some(group)
                && let Some(last) = batches
                    .last_mut()
                    .filter(|b| b.kind == kind && b.indices.end == range.start)
            {
                last.indices.end = range.end;
            } else {
                batches.push(StaticDxfBatch {
                    kind,
                    indices: range,
                });
            }
            last_group = Some(group);
            continue;
        }
        let start = u32::try_from(indices.len()).map_err(|_| Error::Capacity)?;
        for old in &compiled.mesh.indices()[range.start as usize..range.end as usize] {
            let mapped = map[*old as usize];
            if mapped == u32::MAX {
                return Err(Error::ResourceDomain);
            }
            indices.push(mapped);
        }
        let end = u32::try_from(indices.len()).map_err(|_| Error::Capacity)?;
        if kind == StaticDxfBatchKind::Vector
            && last_group == Some(group)
            && let Some(last) = batches
                .last_mut()
                .filter(|b| b.kind == StaticDxfBatchKind::Vector)
        {
            last.indices.end = end;
        } else {
            batches.push(StaticDxfBatch {
                kind,
                indices: start..end,
            });
        }
        last_group = Some(group);
    }
    let mut mesh = Mesh::default();
    mesh.push_indexed(&vertices, &indices)?;
    compiled.mesh = mesh;
    compiled.camera = None;
    #[cfg(feature = "gpu")]
    for glyph in &mut compiled.glyphs {
        glyph.scale_bold_italic_flags[3] =
            crate::scene::Scene::static_dxf_text_flags(glyph.scale_bold_italic_flags[3]);
    }
    compiled.draws = batches
        .iter()
        .map(|b| {
            Ok(match b.kind {
                StaticDxfBatchKind::Vector => DrawRange::Vector(b.indices.clone()),
                StaticDxfBatchKind::Hatch => DrawRange::Hatch(b.indices.clone()),
                StaticDxfBatchKind::Text => {
                    #[cfg(feature = "gpu")]
                    {
                        DrawRange::Text(b.indices.clone())
                    }
                    #[cfg(not(feature = "gpu"))]
                    {
                        return Err(Error::UnsupportedExecution);
                    }
                }
            })
        })
        .collect::<Result<Vec<_>, Error>>()?;
    Ok(batches)
}
