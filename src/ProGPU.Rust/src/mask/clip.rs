//! Original native clip placement, ordered composition and shared-atlas records.
use super::*;
use crate::path::{Path, RasterRequest};
use std::collections::HashMap;

/// Original retained path node. Boolean geometry retains its complete Path program.
#[derive(Clone, Debug)]
pub struct ClipPath {
    /// Original immutable segment/Boolean program generation.
    pub path: Path,
    /// Original source-to-target logical transform.
    pub transform: Affine,
    /// Original 1, 4 or 8 samples per axis.
    pub sample_grid: u32,
    /// Ordered intersect/difference operation.
    pub operation: MaskOperation,
}
impl ClipPath {
    /// Record an identity-transformed intersect node with an 8-by-8 grid.
    pub fn new(path: Path) -> Self {
        Self {
            path,
            transform: Affine::IDENTITY,
            sample_grid: 8,
            operation: MaskOperation::Intersect,
        }
    }
}

/// Exact original 16-byte path-transfer vertex; UVs use the final atlas extent.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct ClipVertex {
    /// Original clip-space XY.
    pub position: [f32; 2],
    /// Original phase-adjusted UV, normalized after final atlas packing.
    pub atlas_uv: [f32; 2],
}
/// Exact original 32-byte compose record in a 256-byte dynamic-uniform slot.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct ComposeUniforms {
    /// Original operation/alpha-channel/source-origin packed word.
    pub operation: u32,
    /// First-node bit and original packed vertical source origin.
    pub first: u32,
    /// Actual output width.
    pub width: u32,
    /// Actual output height.
    pub height: u32,
    /// Exact atlas-minus-device XY, proven integer-load flag and zero padding.
    pub pixel_mapping: [i32; 4],
}
const _: () = {
    assert!(size_of::<ClipVertex>() == 16);
    assert!(size_of::<ComposeUniforms>() == 32);
};

/// Immutable original clip plan. No shader, rasterization or pixel work occurs
/// while planning; each unique source/scale/phase request owns one atlas tile.
#[derive(Debug)]
pub struct ClipPlan {
    pub(crate) rasters: Vec<(Path, RasterRequest)>,
    pub(crate) vertices: Vec<ClipVertex>,
    pub(crate) uniforms: Vec<ComposeUniforms>,
    size: [u32; 2],
    dpi: f32,
    atlas_size: u32,
}
impl ClipPlan {
    /// Validate and freeze the complete native plan for this physical target.
    pub fn new(paths: &[ClipPath], size: [u32; 2], dpi: f32) -> Result<Self, Error> {
        if paths.is_empty()
            || paths.len() > 65536
            || size.contains(&0)
            || !dpi.is_finite()
            || dpi <= 0.0
        {
            return Err(Error::InvalidInput);
        }
        let mut rasters = Vec::new();
        let mut mapping = Vec::new();
        let mut retained = HashMap::new();
        rasters
            .try_reserve(paths.len())
            .map_err(|_| Error::Capacity)?;
        mapping
            .try_reserve(paths.len())
            .map_err(|_| Error::Capacity)?;
        for path in paths {
            if !matches!(path.sample_grid, 1 | 4 | 8) {
                return Err(Error::InvalidInput);
            }
            let bounds = path.path.bounds();
            if bounds[2] <= bounds[0] || bounds[3] <= bounds[1] {
                return Err(Error::InvalidInput);
            }
            let scale = path.transform.scales()?[0] * dpi;
            if !scale.is_finite() || scale <= 0.0 {
                return Err(Error::InvalidInput);
            }
            let phase = [path.transform.0[4], path.transform.0[5]].map(|v| {
                let physical = v * dpi;
                let phase = ((physical - physical.floor()) * 64.0).round() / 64.0;
                if phase >= 1.0 { 0.0 } else { phase }
            });
            if phase.iter().any(|v| !v.is_finite()) {
                return Err(Error::InvalidInput);
            }
            let minimum = [bounds[0], bounds[1]].map(|v| (v * scale).floor() - 4.0);
            let maximum = [bounds[2], bounds[3]].map(|v| (v * scale).ceil() + 4.0);
            let extent = [maximum[0] - minimum[0], maximum[1] - minimum[1]];
            if extent
                .iter()
                .any(|v| !v.is_finite() || *v <= 0.0 || *v > 4092.0)
            {
                return Err(Error::Capacity);
            }
            let request = RasterRequest {
                origin: [minimum[0] - phase[0], minimum[1] - phase[1]],
                scale: [scale; 2],
                size: extent.map(|v| v as u32),
                sample_grid: path.sample_grid,
            };
            let key = (
                path.path.raster_identity(),
                bounds.map(f32::to_bits),
                path.path.fill_rule() as u32,
                scale.to_bits(),
                phase.map(f32::to_bits),
                path.sample_grid,
            );
            let index = *retained.entry(key).or_insert_with(|| {
                let index = rasters.len();
                rasters.push((path.path.clone(), request));
                index
            });
            mapping.push((index, minimum, phase));
        }
        let layout = crate::scene::AtlasLayout::new(rasters.iter().map(|(_, r)| r.size))?;
        if layout.size > 4096 {
            return Err(Error::Capacity);
        }
        let inverse = 1.0 / layout.size as f32;
        let mut vertices = Vec::new();
        let mut uniforms = Vec::new();
        vertices
            .try_reserve_exact(paths.len() * 4)
            .map_err(|_| Error::Capacity)?;
        uniforms
            .try_reserve_exact(paths.len())
            .map_err(|_| Error::Capacity)?;
        for (ordinal, (path, &(index, minimum, phase))) in paths.iter().zip(&mapping).enumerate() {
            let request = rasters[index].1;
            let [left, top] = minimum.map(|v| v / request.scale[0]);
            // Keep the native min-plus-width expression, independently of the
            // original upper bound used to derive the extent.
            let right = (minimum[0] + request.size[0] as f32) / request.scale[0];
            let bottom = (minimum[1] + request.size[1] as f32) / request.scale[1];
            let local = [[left, top], [right, top], [right, bottom], [left, bottom]];
            let logical = local.map(|p| path.transform.transform_point(p));
            let device = logical.map(|p| p.map(|v| v * dpi));
            let [x, y] = layout.origins[index].map(|v| v as f32);
            let [w, h] = request.size.map(|v| v as f32);
            let uv = [
                [x + phase[0], y + phase[1]],
                [x + w + phase[0], y + phase[1]],
                [x + w + phase[0], y + h + phase[1]],
                [x + phase[0], y + h + phase[1]],
            ];
            let mut uniform = ComposeUniforms {
                operation: path.operation as u32,
                first: u32::from(ordinal == 0),
                width: size[0],
                height: size[1],
                pixel_mapping: [0; 4],
            };
            if crate::scene::exact_projection(dpi) && crate::scene::exact_mapping(device, uv) {
                uniform.pixel_mapping = [
                    (uv[0][0] - device[0][0]) as i32,
                    (uv[0][1] - device[0][1]) as i32,
                    1,
                    0,
                ];
            }
            // Four independent lanes preserve the source multiplication order.
            let xs = ((f32x4::splat(2.0) * f32x4::from(logical.map(|p| p[0]))) * f32x4::splat(dpi)
                / f32x4::splat(size[0] as f32)
                - f32x4::ONE)
                .to_array();
            let ys = (f32x4::ONE
                - (f32x4::splat(2.0) * f32x4::from(logical.map(|p| p[1]))) * f32x4::splat(dpi)
                    / f32x4::splat(size[1] as f32))
            .to_array();
            let us = (f32x4::from(uv.map(|p| p[0])) * f32x4::splat(inverse)).to_array();
            let vs = (f32x4::from(uv.map(|p| p[1])) * f32x4::splat(inverse)).to_array();
            for i in 0..4 {
                if [xs[i], ys[i], us[i], vs[i]].iter().any(|v| !v.is_finite()) {
                    return Err(Error::InvalidInput);
                }
                vertices.push(ClipVertex {
                    position: [xs[i], ys[i]],
                    atlas_uv: [us[i], vs[i]],
                });
            }
            uniforms.push(uniform);
        }
        Ok(Self {
            rasters,
            vertices,
            uniforms,
            size,
            dpi,
            atlas_size: layout.size,
        })
    }
    /// Actual physical target extent.
    pub fn size(&self) -> [u32; 2] {
        self.size
    }
    /// Original scalar target DPI.
    pub fn dpi_scale(&self) -> f32 {
        self.dpi
    }
    /// Final original square atlas extent.
    pub fn atlas_size(&self) -> u32 {
        self.atlas_size
    }
    /// Number of unique retained source/scale/phase requests.
    pub fn raster_count(&self) -> usize {
        self.rasters.len()
    }
    /// Exact original quad vertices in node order.
    pub fn vertices(&self) -> &[ClipVertex] {
        &self.vertices
    }
    /// Exact original 32-byte records in node order.
    pub fn uniforms(&self) -> &[ComposeUniforms] {
        &self.uniforms
    }
}
