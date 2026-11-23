//! Native atlas layout, raster phase plans and exact material quad coordinates.
use super::*;
use crate::{geometry::Affine, vector::GpuPathRecord};
use bytemuck::{Pod, Zeroable};
use std::collections::HashMap;
pub(crate) mod program;
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct NativePathUniforms {
    pub origin: [f32; 2],
    pub scale: [f32; 2],
    pub path_index: u32,
    pub output_offset_words: u32,
    pub output_row_words: u32,
    pub width: u32,
    pub height: u32,
    pub sample_grid: u32,
    pub path_index_b: u32,
    pub path_op_kind: u32,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Pod, Zeroable)]
pub struct NativePathCombineUniforms {
    pub source_offset_words: u32,
    pub source_stride_words: u32,
    pub source_count: u32,
    pub program_index: u32,
    pub program_count: u32,
    pub destination_offset_words: u32,
    pub destination_row_words: u32,
    pub width: u32,
    pub height: u32,
    pub sample_grid: u32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct NativePathRaster {
    pub atlas_x: u32,
    pub atlas_y: u32,
    pub width: u32,
    pub height: u32,
    pub output_offset: u32,
    pub output_bytes_per_row: u32,
    pub scale_x: f32,
    pub scale_y: f32,
    pub raster_min_x: f32,
    pub raster_min_y: f32,
    pub subpixel_x: f32,
    pub subpixel_y: f32,
}
#[derive(Clone, Debug)]
pub struct NativePathRasterPage {
    source: Arc<NativePathPage>,
    rasters: Arc<[NativePathRaster]>,
    records: Arc<[GpuPathRecord]>,
    uniforms: Arc<[NativePathUniforms]>,
    split_leaves: Arc<[Arc<[NativePathUniforms]>]>,
    signed_leaves: Arc<[Arc<[NativePathUniforms]>]>,
    combines: Arc<[NativePathCombineUniforms]>,
    signed_combines: Arc<[NativePathCombineUniforms]>,
    vertices: Arc<[VectorVertex]>,
    indices: Arc<[u32]>,
    atlas_size: u32,
    coverage_staging_bytes: u64,
    inline_operations: bool,
    inline_signed: bool,
    only_lines: bool,
    only_lines_or_cubics: bool,
}
#[derive(Clone, Copy, Hash, PartialEq, Eq)]
struct Key {
    ranges: [u64; 4],
    bits: [u32; 9],
}
impl NativePathRasterPage {
    pub fn compile(
        source: Arc<NativePathPage>,
        retained_atlas_size: u32,
        staged_signed_winding: bool,
    ) -> Result<Self, Error> {
        if !matches!(retained_atlas_size, 1024 | 2048 | 4096 | 8192) {
            return Err(Error::InvalidInput);
        }
        let (
            mut rasters,
            mut records,
            mut uniforms,
            mut combines,
            mut signed_combines,
            mut vertices,
            mut indices,
        ) = (
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
        let mut retained: HashMap<Key, usize> = HashMap::new();
        retained
            .try_reserve(source.paths.len())
            .map_err(|_| Error::Capacity)?;
        let (mut only_lines, mut only_lines_or_cubics) = (true, true);
        for s in source.segments.iter() {
            if !values::segment(s, true) {
                return Err(Error::InvalidInput);
            }
            only_lines &= s.kind == PATH_SEGMENT_LINE;
            only_lines_or_cubics &= matches!(s.kind, PATH_SEGMENT_LINE | PATH_SEGMENT_CUBIC);
        }
        let (mut atlas_size, mut x, mut y, mut row, mut output) =
            (retained_atlas_size, 2u32, 2u32, 0u32, 0u32);
        let (mut inline_operations, mut inline_signed) = (false, false);
        let mut expected_nodes = 0u64;
        let mut draw_index = 0usize;
        for (index, path) in source.paths.iter().enumerate() {
            while draw_index + 1 < source.draws.len()
                && index * 6 >= source.draws[draw_index + 1].draw.first_index as usize
            {
                draw_index += 1;
            }
            let frame = source.frames.get(draw_index).ok_or(Error::InvalidInput)?;
            let dpi = frame.dpi_scale;
            values::path(path, source.segments.len() as u64, &source.nodes)
                .ok_or(Error::InvalidInput)?;
            if path.boolean_node_count != 0 && path.boolean_node_offset != expected_nodes {
                return Err(Error::InvalidInput);
            }
            expected_nodes += path.boolean_node_count;
            let physical = source.source_frames[index];
            let t = path.transform;
            let [maximum, _] = Affine([t.m11, t.m12, t.m21, t.m22, t.m31, t.m32]).scales()?;
            let scale = if physical.physical {
                1.0
            } else {
                maximum * dpi
            };
            if !scale.is_finite() || scale <= 0.0 {
                return Err(Error::InvalidInput);
            }
            let phase = if physical.physical {
                [0.0; 2]
            } else {
                [quantize(t.m31 * dpi), quantize(t.m32 * dpi)]
            };
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
                return Err(Error::UnsupportedExecution);
            }
            let (width, height) = (size[0] as u32, size[1] as u32);
            let raster_index = if let Some(&slot) = retained.get(&key) {
                slot
            } else {
                while width + 4 > atlas_size && atlas_size < 8192 {
                    atlas_size *= 2;
                }
                if x + width + 2 > atlas_size {
                    x = 2;
                    y = y.checked_add(row + 2).ok_or(Error::Capacity)?;
                    row = 0;
                }
                while y + height + 2 > atlas_size && atlas_size < 8192 {
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
                let program = program::append(path, &source.nodes, &source.segments, &mut records)?;
                let signed = program.operation_kind & program::SIGNED != 0;
                let base = NativePathUniforms {
                    origin: [min[0] - phase[0], min[1] - phase[1]],
                    scale: [scale; 2],
                    path_index: program.path_record_index,
                    output_offset_words: output / 4,
                    output_row_words: pitch / 4,
                    width,
                    height,
                    sample_grid: path.sample_grid & 0xff,
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
                    let split_next = source_offset
                        + leaf_bytes * u64::from(program.split_leaf_count)
                        + result_bytes;
                    let split_next = u32::try_from(split_next).map_err(|_| Error::Capacity)?;
                    let selected = if signed {
                        &mut signed_leaves
                    } else {
                        &mut split_leaves
                    };
                    selected
                        .try_reserve(
                            (program.split_leaf_count as usize).saturating_sub(selected.len()),
                        )
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
                        sample_grid: path.sample_grid & 0xff,
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
            let local = [
                [min[0] / scale, min[1] / scale],
                [max[0] / scale, min[1] / scale],
                [max[0] / scale, max[1] / scale],
                [min[0] / scale, max[1] / scale],
            ];
            let atlas = [
                [
                    raster.atlas_x as f32 + phase[0],
                    raster.atlas_y as f32 + phase[1],
                ],
                [
                    (raster.atlas_x + raster.width) as f32 + phase[0],
                    raster.atlas_y as f32 + phase[1],
                ],
                [
                    (raster.atlas_x + raster.width) as f32 + phase[0],
                    (raster.atlas_y + raster.height) as f32 + phase[1],
                ],
                [
                    raster.atlas_x as f32 + phase[0],
                    (raster.atlas_y + raster.height) as f32 + phase[1],
                ],
            ];
            let start = count_u32(vertices.len())?;
            let mut device = [[0.0; 2]; 4];
            for corner in 0..4 {
                let p = execution::transform_point(
                    t,
                    wire::Point {
                        x: local[corner][0],
                        y: local[corner][1],
                    },
                );
                device[corner] = [p.x * dpi, p.y * dpi];
                let mut vertex = VectorVertex {
                    position: [p.x, p.y],
                    color: [path.color.r, path.color.g, path.color.b, path.color.a],
                    texture_coordinate: atlas[corner],
                    brush_index: source.brush_indices[index] as f32,
                    shape_size: local[corner],
                    corner_radius: (path.sample_grid >> 8) as f32,
                    stroke_thickness: 0.0,
                    shape_type: 4.0,
                };
                if physical.physical {
                    device[corner] = vertex.position;
                    vertex.shape_size = [
                        local[corner][0] / physical.dpi_x,
                        local[corner][1] / physical.dpi_y,
                    ];
                    vertex.shape_type = 27.0;
                }
                push(&mut vertices, vertex)?;
            }
            if !physical.physical && pixel_projection(dpi) && pixel_mapping(&device, &atlas) {
                for v in &mut vertices[start as usize..] {
                    v.stroke_thickness = if dpi == 1.0 { -1.0 } else { -2.0 };
                }
            }
            for offset in [0, 1, 2, 0, 2, 3] {
                push(
                    &mut indices,
                    start.checked_add(offset).ok_or(Error::Capacity)?,
                )?;
            }
        }
        if expected_nodes != source.nodes.len() as u64 {
            return Err(Error::InvalidInput);
        }
        let mut split = allocate(split_leaves.len())?;
        for leaf in split_leaves {
            split.push(Arc::from(leaf));
        }
        let mut signed = allocate(signed_leaves.len())?;
        for leaf in signed_leaves {
            signed.push(Arc::from(leaf));
        }
        Ok(Self {
            source,
            rasters: rasters.into(),
            records: records.into(),
            uniforms: uniforms.into(),
            split_leaves: split.into(),
            signed_leaves: signed.into(),
            combines: combines.into(),
            signed_combines: signed_combines.into(),
            vertices: vertices.into(),
            indices: indices.into(),
            atlas_size,
            coverage_staging_bytes: u64::from(output),
            inline_operations,
            inline_signed,
            only_lines,
            only_lines_or_cubics,
        })
    }
    pub fn source(&self) -> &Arc<NativePathPage> {
        &self.source
    }
    pub fn rasters(&self) -> &[NativePathRaster] {
        &self.rasters
    }
    pub fn records(&self) -> &[GpuPathRecord] {
        &self.records
    }
    pub fn uniforms(&self) -> &[NativePathUniforms] {
        &self.uniforms
    }
    pub fn split_leaf_uniforms(&self) -> &[Arc<[NativePathUniforms]>] {
        &self.split_leaves
    }
    pub fn signed_leaf_uniforms(&self) -> &[Arc<[NativePathUniforms]>] {
        &self.signed_leaves
    }
    pub fn combine_uniforms(&self) -> &[NativePathCombineUniforms] {
        &self.combines
    }
    pub fn signed_combine_uniforms(&self) -> &[NativePathCombineUniforms] {
        &self.signed_combines
    }
    pub fn vertices(&self) -> &[VectorVertex] {
        &self.vertices
    }
    pub fn indices(&self) -> &[u32] {
        &self.indices
    }
    pub fn atlas_size(&self) -> u32 {
        self.atlas_size
    }
    pub fn coverage_staging_bytes(&self) -> u64 {
        self.coverage_staging_bytes
    }
    pub fn has_inline_operations(&self) -> bool {
        self.inline_operations
    }
    pub fn has_inline_signed_winding(&self) -> bool {
        self.inline_signed
    }
    pub fn has_only_lines(&self) -> bool {
        self.only_lines
    }
    pub fn has_only_lines_or_cubics(&self) -> bool {
        self.only_lines_or_cubics
    }
}
fn quantize(mut value: f32) -> f32 {
    value -= value.floor();
    let result = (value * 64.0).round() / 64.0;
    if result >= 1.0 { 0.0 } else { result }
}
fn pixel_projection(dpi: f32) -> bool {
    dpi.is_finite() && dpi >= 1.0 && dpi.to_bits() & 0x007f_ffff == 0
}
fn pixel_mapping(points: &[[f32; 2]; 4], atlas: &[[f32; 2]; 4]) -> bool {
    let dx = atlas[0][0] - points[0][0];
    let dy = atlas[0][1] - points[0][1];
    (0..4).all(|i| {
        points[i]
            .into_iter()
            .chain(atlas[i])
            .all(|v| v.abs() <= 8388608.0 && v.trunc() == v)
            && atlas[i][0] - points[i][0] == dx
            && atlas[i][1] - points[i][1] == dy
    })
}
