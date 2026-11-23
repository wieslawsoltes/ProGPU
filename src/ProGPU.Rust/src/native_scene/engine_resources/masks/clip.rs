//! Exact native raw clip-chain atlas, preserving complete source classifications.
use super::*;
use crate::native_scene::engine_resources::path::{
    NativePathCombineUniforms, NativePathRaster, NativePathUniforms, raster::program,
};
use crate::native_scene::semantic::values;
use crate::{
    geometry::Affine,
    mask::{ClipVertex, ComposeUniforms},
    vector::GpuPathRecord,
};
use std::collections::HashMap;
#[derive(Clone, Copy, Hash, PartialEq, Eq)]
struct Key {
    ranges: [u64; 4],
    bits: [u32; 9],
}
#[derive(Debug)]
pub(crate) struct ClipPlan {
    source: VectorMask,
    rasters: Vec<NativePathRaster>,
    records: Vec<GpuPathRecord>,
    uniforms: Vec<NativePathUniforms>,
    split_leaves: Vec<Arc<[NativePathUniforms]>>,
    signed_leaves: Vec<Arc<[NativePathUniforms]>>,
    combines: Vec<NativePathCombineUniforms>,
    signed_combines: Vec<NativePathCombineUniforms>,
    vertices: Vec<ClipVertex>,
    indices: Vec<u32>,
    compose: Vec<ComposeUniforms>,
    atlas_size: u32,
    coverage_bytes: u64,
    inline_operations: bool,
    inline_signed: bool,
    only_lines: bool,
    only_lines_or_cubics: bool,
}
impl ClipPlan {
    pub fn atlas_size(&self) -> u32 {
        self.atlas_size
    }
    pub fn raster_count(&self) -> usize {
        self.rasters.len()
    }
    pub fn vertices(&self) -> &[ClipVertex] {
        &self.vertices
    }
    pub fn uniforms(&self) -> &[ComposeUniforms] {
        &self.compose
    }
}
fn push<T>(values: &mut Vec<T>, value: T) -> Result<(), Error> {
    values.try_reserve(1).map_err(|_| Error::Capacity)?;
    values.push(value);
    Ok(())
}
fn quantize(value: f32) -> f32 {
    let phase = ((value - value.floor()) * 64.0).round() / 64.0;
    if phase >= 1.0 { 0.0 } else { phase }
}
pub(super) fn plan(source: &VectorMask, target: NativeMaskTarget) -> Result<ClipPlan, Error> {
    plan_with_atlas(source, target, 1024)
}
pub(super) fn plan_with_atlas(
    source: &VectorMask,
    target: NativeMaskTarget,
    retained_atlas_size: u32,
) -> Result<ClipPlan, Error> {
    if !matches!(retained_atlas_size, 1024 | 2048 | 4096) {
        return Err(Error::InvalidInput);
    }
    if source.paths.is_empty()
        || source.paths.len() > 1 << 16
        || source.segments.is_empty()
        || source.segments.len() > 1 << 24
        || source.nodes.len() > 1 << 22
    {
        return Err(Error::InvalidInput);
    }
    let (mut only_lines, mut only_lines_or_cubics) = (true, true);
    for segment in source.segments.iter() {
        if !values::segment(segment, true) {
            return Err(Error::InvalidInput);
        }
        only_lines &= segment.kind == PATH_SEGMENT_LINE;
        only_lines_or_cubics &= matches!(segment.kind, PATH_SEGMENT_LINE | PATH_SEGMENT_CUBIC);
    }
    let (
        mut rasters,
        mut records,
        mut uniforms,
        mut combines,
        mut signed_combines,
        mut vertices,
        mut indices,
        mut compose,
    ) = (
        Vec::new(),
        Vec::new(),
        Vec::new(),
        Vec::new(),
        Vec::new(),
        Vec::new(),
        Vec::new(),
        Vec::new(),
    );
    let (mut split_leaves, mut signed_leaves): (
        Vec<Vec<NativePathUniforms>>,
        Vec<Vec<NativePathUniforms>>,
    ) = (Vec::new(), Vec::new());
    let mut retained = HashMap::<Key, usize>::new();
    retained
        .try_reserve(source.paths.len())
        .map_err(|_| Error::Capacity)?;
    let (mut atlas_size, mut x, mut y, mut row, mut output) =
        (retained_atlas_size, 2u32, 2u32, 0u32, 0u32);
    let (mut inline_operations, mut inline_signed) = (false, false);
    // Semantic vector-mask resources set the original clip-chain flags to zero.
    let staged_signed_winding = false;
    let mut expected_nodes = 0u64;
    for (index, path) in source.paths.iter().enumerate() {
        if path.boolean_node_count != 0 && path.boolean_node_offset != expected_nodes {
            return Err(Error::InvalidInput);
        }
        expected_nodes += path.boolean_node_count;
        let path_record = wire::PathFill {
            segment_offset: path.segment_offset,
            segment_count: path.segment_count,
            boolean_node_offset: path.boolean_node_offset,
            boolean_node_count: path.boolean_node_count,
            min_x: path.min_x,
            min_y: path.min_y,
            max_x: path.max_x,
            max_y: path.max_y,
            fill_rule: path.fill_rule,
            sample_grid: path.sample_grid,
            ..Default::default()
        };
        if !values::boolean(
            path.segment_offset,
            path.segment_count,
            path.boolean_node_offset,
            path.boolean_node_count,
            path.fill_rule,
            &source.nodes,
        ) {
            return Err(Error::InvalidInput);
        }
        let t = localize_transform(
            path.transform,
            target.extent,
            target.presentation,
            target.dpi_scale,
        );
        let [maximum, _] = Affine([t.m11, t.m12, t.m21, t.m22, t.m31, t.m32]).scales()?;
        let scale = maximum * target.dpi_scale;
        if !scale.is_finite() || scale <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let phase = [
            quantize(t.m31 * target.dpi_scale),
            quantize(t.m32 * target.dpi_scale),
        ];
        let key = Key {
            ranges: [
                path.segment_offset,
                path.segment_count,
                path.boolean_node_offset,
                path.boolean_node_count,
            ],
            bits: [
                path.min_x.to_bits(),
                path.min_y.to_bits(),
                path.max_x.to_bits(),
                path.max_y.to_bits(),
                scale.to_bits(),
                phase[0].to_bits(),
                phase[1].to_bits(),
                path.fill_rule,
                path.sample_grid,
            ],
        };
        let min = [
            (path.min_x * scale).floor() - 4.0,
            (path.min_y * scale).floor() - 4.0,
        ];
        let max = [
            (path.max_x * scale).ceil() + 4.0,
            (path.max_y * scale).ceil() + 4.0,
        ];
        let size = [f64::from(max[0] - min[0]), f64::from(max[1] - min[1])];
        if size
            .into_iter()
            .any(|v| !v.is_finite() || v <= 0.0 || v > 4092.0)
        {
            return Err(Error::Capacity);
        }
        let (width, height) = (size[0] as u32, size[1] as u32);
        let raster_index = if let Some(&slot) = retained.get(&key) {
            slot
        } else {
            while width + 4 > atlas_size && atlas_size < 4096 {
                atlas_size *= 2;
            }
            if x + width + 2 > atlas_size {
                x = 2;
                y = y.checked_add(row + 2).ok_or(Error::Capacity)?;
                row = 0;
            }
            while y + height + 2 > atlas_size && atlas_size < 4096 {
                atlas_size *= 2;
            }
            if y + height + 2 > atlas_size {
                return Err(Error::Capacity);
            }
            let pitch = (width + 255) & !255;
            output = output.checked_add(511).ok_or(Error::Capacity)? & !511;
            let next = u64::from(output) + u64::from(pitch) * u64::from(height);
            let next = u32::try_from(next).map_err(|_| Error::Capacity)?;
            let slot = rasters.len();
            push(
                &mut rasters,
                NativePathRaster {
                    atlas_x: x,
                    atlas_y: y,
                    width,
                    height,
                    output_offset: output,
                    output_bytes_per_row: pitch,
                    scale_x: scale,
                    scale_y: scale,
                    raster_min_x: min[0],
                    raster_min_y: min[1],
                    subpixel_x: phase[0],
                    subpixel_y: phase[1],
                },
            )?;
            let program =
                program::append(&path_record, &source.nodes, &source.segments, &mut records)?;
            let signed = program.operation_kind & program::SIGNED != 0;
            let base = NativePathUniforms {
                origin: [min[0] - phase[0], min[1] - phase[1]],
                scale: [scale; 2],
                path_index: program.path_record_index,
                output_offset_words: output / 4,
                output_row_words: pitch / 4,
                width,
                height,
                sample_grid: path.sample_grid,
                path_index_b: program.program_index,
                path_op_kind: program.operation_kind,
            };
            if program.split_leaf_count != 0 && (!signed || staged_signed_winding) {
                let words_per_pixel = if signed { 64u64 } else { 2u64 };
                let words_per_row = u64::from(width) * words_per_pixel;
                let leaf_bytes = words_per_row * 4 * u64::from(height);
                let source_offset = (u64::from(next) + 255) & !255;
                let result_bytes = if signed {
                    u64::from(width) * u64::from(height) * 2 * 4
                } else {
                    0
                };
                let split_next =
                    source_offset + leaf_bytes * u64::from(program.split_leaf_count) + result_bytes;
                let split_next = u32::try_from(split_next).map_err(|_| Error::Capacity)?;
                let selected = if signed {
                    &mut signed_leaves
                } else {
                    &mut split_leaves
                };
                selected
                    .try_reserve((program.split_leaf_count as usize).saturating_sub(selected.len()))
                    .map_err(|_| Error::Capacity)?;
                selected.resize_with(
                    selected.len().max(program.split_leaf_count as usize),
                    Vec::new,
                );
                for leaf in 0..program.split_leaf_count {
                    let leaf_output = source_offset + leaf_bytes * u64::from(leaf);
                    push(
                        &mut selected[leaf as usize],
                        NativePathUniforms {
                            path_index: program.path_record_index + leaf,
                            output_offset_words: (leaf_output / 4) as u32,
                            output_row_words: words_per_row as u32,
                            path_index_b: 0,
                            path_op_kind: if signed { program::SIGNED } else { 0 },
                            ..base
                        },
                    )?;
                }
                let combine = NativePathCombineUniforms {
                    source_offset_words: (source_offset / 4) as u32,
                    source_stride_words: (leaf_bytes / 4) as u32,
                    source_count: program.split_leaf_count,
                    program_index: program.program_index,
                    program_count: program.operation_kind & !program::PROGRAM,
                    destination_offset_words: output / 4,
                    destination_row_words: pitch / 4,
                    width,
                    height,
                    sample_grid: path.sample_grid,
                };
                push(
                    if signed {
                        &mut signed_combines
                    } else {
                        &mut combines
                    },
                    combine,
                )?;
                output = split_next;
            } else {
                inline_operations |= program.operation_kind != 0;
                inline_signed |= signed;
                push(&mut uniforms, base)?;
                output = next;
            }
            retained.insert(key, slot);
            x += width + 2;
            row = row.max(height);
            slot
        };

        let raster = rasters[raster_index];
        let left = raster.raster_min_x / raster.scale_x;
        let top = raster.raster_min_y / raster.scale_y;
        let right = (raster.raster_min_x + raster.width as f32) / raster.scale_x;
        let bottom = (raster.raster_min_y + raster.height as f32) / raster.scale_y;
        let local = [[left, top], [right, top], [right, bottom], [left, bottom]];
        let atlas = [
            [
                raster.atlas_x as f32 + raster.subpixel_x,
                raster.atlas_y as f32 + raster.subpixel_y,
            ],
            [
                (raster.atlas_x + raster.width) as f32 + raster.subpixel_x,
                raster.atlas_y as f32 + raster.subpixel_y,
            ],
            [
                (raster.atlas_x + raster.width) as f32 + raster.subpixel_x,
                (raster.atlas_y + raster.height) as f32 + raster.subpixel_y,
            ],
            [
                raster.atlas_x as f32 + raster.subpixel_x,
                (raster.atlas_y + raster.height) as f32 + raster.subpixel_y,
            ],
        ];
        let start = u32::try_from(vertices.len()).map_err(|_| Error::Capacity)?;
        let mut device = [[0.0; 2]; 4];
        for corner in 0..4 {
            let p = transform_point(
                t,
                wire::Point {
                    x: local[corner][0],
                    y: local[corner][1],
                },
            );
            device[corner] = [p.x * target.dpi_scale, p.y * target.dpi_scale];
            push(
                &mut vertices,
                ClipVertex {
                    position: [
                        2.0 * p.x * target.dpi_scale / target.extent.width as f32 - 1.0,
                        1.0 - 2.0 * p.y * target.dpi_scale / target.extent.height as f32,
                    ],
                    atlas_uv: atlas[corner],
                },
            )?;
        }
        for offset in [0, 1, 2, 0, 2, 3] {
            push(&mut indices, start + offset)?;
        }
        let mut uniform = ComposeUniforms {
            operation: path.operation,
            first: u32::from(index == 0),
            width: target.extent.width,
            height: target.extent.height,
            pixel_mapping: [0; 4],
        };
        if crate::scene::exact_projection(target.dpi_scale)
            && crate::scene::exact_mapping(device, atlas)
        {
            uniform.pixel_mapping = [
                (atlas[0][0] - device[0][0]) as i32,
                (atlas[0][1] - device[0][1]) as i32,
                1,
                0,
            ];
        }
        push(&mut compose, uniform)?;
    }
    if expected_nodes != source.nodes.len() as u64 {
        return Err(Error::InvalidInput);
    }
    let inverse = 1.0 / atlas_size as f32;
    for vertex in &mut vertices {
        vertex.atlas_uv[0] *= inverse;
        vertex.atlas_uv[1] *= inverse;
    }
    let mut split = allocate(split_leaves.len())?;
    for leaf in split_leaves {
        split.push(Arc::from(leaf));
    }
    let mut signed = allocate(signed_leaves.len())?;
    for leaf in signed_leaves {
        signed.push(Arc::from(leaf));
    }
    Ok(ClipPlan {
        source: source.clone(),
        rasters,
        records,
        uniforms,
        split_leaves: split,
        signed_leaves: signed,
        combines,
        signed_combines,
        vertices,
        indices,
        compose,
        atlas_size,
        coverage_bytes: u64::from(output),
        inline_operations,
        inline_signed,
        only_lines,
        only_lines_or_cubics,
    })
}
#[cfg(feature = "gpu")]
impl crate::native_scene::engine_resources::path::gpu::NativeRasterPlan for ClipPlan {
    fn segments(&self) -> &[wire::PathSegment] {
        &self.source.segments
    }
    fn records(&self) -> &[GpuPathRecord] {
        &self.records
    }
    fn uniforms(&self) -> &[NativePathUniforms] {
        &self.uniforms
    }
    fn split_leaf_uniforms(&self) -> &[Arc<[NativePathUniforms]>] {
        &self.split_leaves
    }
    fn signed_leaf_uniforms(&self) -> &[Arc<[NativePathUniforms]>] {
        &self.signed_leaves
    }
    fn combine_uniforms(&self) -> &[NativePathCombineUniforms] {
        &self.combines
    }
    fn signed_combine_uniforms(&self) -> &[NativePathCombineUniforms] {
        &self.signed_combines
    }
    fn rasters(&self) -> &[NativePathRaster] {
        &self.rasters
    }
    fn atlas_size(&self) -> u32 {
        self.atlas_size
    }
    fn coverage_staging_bytes(&self) -> u64 {
        self.coverage_bytes
    }
    fn has_inline_operations(&self) -> bool {
        self.inline_operations
    }
    fn has_inline_signed_winding(&self) -> bool {
        self.inline_signed
    }
    fn has_only_lines(&self) -> bool {
        self.only_lines
    }
    fn has_only_lines_or_cubics(&self) -> bool {
        self.only_lines_or_cubics
    }
    fn vertex_bytes(&self) -> &[u8] {
        bytemuck::cast_slice(&self.vertices)
    }
    fn index_bytes(&self) -> &[u8] {
        bytemuck::cast_slice(&self.indices)
    }
}
