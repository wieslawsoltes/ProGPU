//! Original implicit-effect and raw-cache source compilation frames.
use super::CacheSamplerRasterFrame;
use crate::{Error, shader_effect::ManagedCaptureFrame, vector::Matrix4};

/// Actual source rendering target. Allocation, logical projection and semantic
/// DPI are independent. This descriptor does not prove source/device ownership;
/// callers retain those generations through the actual capture operation.
#[derive(Clone, Copy, Debug)]
pub struct SourceRenderFrame {
    size: [u32; 2],
    logical_extent: [f32; 2],
    logical_size: [u32; 2],
    dpi: f32,
    placement: Matrix4,
    projection: [f32; 16],
    suppress_cached_cleartype: bool,
    empty: bool,
    effect: Option<ManagedCaptureFrame>,
    cache: Option<CacheSamplerRasterFrame>,
    image_filter: bool,
}
impl SourceRenderFrame {
    /// Original ordinary CachedPicture layer capture. Logical projection keeps
    /// the exact source extent; allocation uses ceil(size * DPI * cache scale).
    /// Empty bounds and zero scale have no painted generation.
    pub fn cached_picture(
        source: &super::cached_picture::CachedPictureSnapshot,
        dpi: f32,
    ) -> Result<Option<Self>, Error> {
        source.validate()?;
        if !dpi.is_finite() || dpi <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let [x, y, w, h] = source.bounds;
        if w <= 0.0 || h <= 0.0 || source.render_scale <= 0.0 {
            return Ok(None);
        }
        let raster_scale = dpi * source.render_scale;
        let physical =
            (wide::f32x4::from([w, h, 0.0, 0.0]) * wide::f32x4::splat(raster_scale)).to_array();
        let dimensions = [
            w.ceil().max(1.0),
            h.ceil().max(1.0),
            physical[0].ceil().max(1.0),
            physical[1].ceil().max(1.0),
        ];
        if !raster_scale.is_finite()
            || raster_scale <= 0.0
            || dimensions
                .iter()
                .any(|v| !v.is_finite() || f64::from(*v) > f64::from(u32::MAX))
        {
            return Err(Error::Capacity);
        }
        let mut placement = Matrix4::IDENTITY;
        placement.0[3][0] = -x;
        placement.0[3][1] = -y;
        let mut frame = Self::create(
            [dimensions[2] as u32, dimensions[3] as u32],
            [w, h],
            raster_scale,
            placement,
            !source.enable_cleartype,
            false,
        )?;
        frame.logical_size = [dimensions[0] as u32, dimensions[1] as u32];
        Ok(Some(frame))
    }
    fn create(
        size: [u32; 2],
        logical_extent: [f32; 2],
        dpi: f32,
        placement: Matrix4,
        suppress_cached_cleartype: bool,
        empty: bool,
    ) -> Result<Self, Error> {
        if size.contains(&0)
            || logical_extent.iter().any(|v| !v.is_finite() || *v <= 0.0)
            || !dpi.is_finite()
            || dpi <= 0.0
            || placement.0.iter().flatten().any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        let m = placement.0;
        if m[0][0] <= 0.0
            || m[1][1] <= 0.0
            || m[2][2] != 1.0
            || m[3][3] != 1.0
            || [
                m[0][1], m[0][2], m[0][3], m[1][0], m[1][2], m[1][3], m[2][0], m[2][1], m[2][3],
                m[3][2],
            ]
            .iter()
            .any(|v| *v != 0.0)
        {
            return Err(Error::InvalidInput);
        }
        // Original RenderOffscreenCore projection. In particular, Z remains
        // positive one, and allocation/DPI do not replace logicalExtent.
        let projection = [
            2.0 / logical_extent[0],
            0.0,
            0.0,
            0.0,
            0.0,
            -2.0 / logical_extent[1],
            0.0,
            0.0,
            0.0,
            0.0,
            1.0,
            0.0,
            -1.0,
            1.0,
            0.0,
            1.0,
        ];
        if projection.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            size,
            logical_extent,
            logical_size: size,
            dpi,
            placement,
            projection,
            suppress_cached_cleartype,
            empty,
            effect: None,
            cache: None,
            image_filter: false,
        })
    }
    /// Original implicit-input capture. Physical-origin captures project their
    /// exact floating extent; legacy captures retain integer logical bookkeeping.
    /// Compilation receives root translation only, with original semantic DPI.
    pub fn effect(
        frame: ManagedCaptureFrame,
        suppress_cached_cleartype: bool,
    ) -> Result<Self, Error> {
        if frame.logical_render_size.contains(&0)
            || frame
                .logical_extent
                .iter()
                .any(|v| !v.is_finite() || *v <= 0.0)
            || frame
                .padded_bounds
                .iter()
                .chain(&frame.raster_bounds)
                .chain(&frame.output_edges)
                .chain(&frame.texture_uv_bounds)
                .chain(&frame.pixels_per_unit)
                .chain(&frame.physical_origin)
                .chain(&frame.source_to_raster)
                .any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        let (extent, origin) = if frame.has_physical_origin {
            (
                frame.logical_extent,
                [frame.raster_bounds[0], frame.raster_bounds[1]],
            )
        } else {
            (
                frame.logical_render_size.map(|v| v as f32),
                [frame.padded_bounds[0], frame.padded_bounds[1]],
            )
        };
        let mut placement = Matrix4::IDENTITY;
        placement.0[3][0] = -origin[0];
        placement.0[3][1] = -origin[1];
        let mut result = Self::create(
            frame.pixel_size,
            extent,
            frame.dpi,
            placement,
            suppress_cached_cleartype,
            false,
        )?;
        result.logical_size = frame.logical_render_size;
        result.effect = Some(frame);
        Ok(result)
    }
    /// Original independent raw cache: full original SourceToRaster command
    /// placement, pixel-sized projection, semantic DPI one, exact ClearType policy.
    /// Proven empty input still requires complete source ownership preflight.
    pub fn cache(frame: CacheSamplerRasterFrame, enable_cleartype: bool) -> Result<Self, Error> {
        let matrix = frame.source_to_raster();
        let placement = Matrix4(std::array::from_fn(|r| {
            std::array::from_fn(|c| matrix[r * 4 + c])
        }));
        let mut result = Self::create(
            frame.size(),
            frame.size().map(|v| v as f32),
            1.0,
            placement,
            !enable_cleartype,
            frame.is_empty(),
        )?;
        result.cache = Some(frame);
        Ok(result)
    }
    /// Original SKCanvas image-filter source capture: pixel-sized projection,
    /// semantic DPI one and the complete source-to-filter command transform.
    /// Full finite 2D affine transforms are retained. Perspective source geometry
    /// requires its independent source compiler and is rejected here.
    pub fn image_filter(size: [u32; 2], transform: Matrix4) -> Result<Self, Error> {
        let transform = if transform.0.iter().flatten().all(|v| *v == 0.0) {
            Matrix4::IDENTITY
        } else {
            transform
        };
        let m = transform.0;
        if m.iter().flatten().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        if m[0][2] != 0.0
            || m[0][3] != 0.0
            || m[1][2] != 0.0
            || m[1][3] != 0.0
            || m[2] != [0.0, 0.0, 1.0, 0.0]
            || m[3][2] != 0.0
            || m[3][3] != 1.0
        {
            return Err(Error::UnsupportedExecution);
        }
        let mut frame = Self::create(
            size,
            size.map(|v| v as f32),
            1.0,
            Matrix4::IDENTITY,
            false,
            false,
        )?;
        frame.placement = transform;
        frame.image_filter = true;
        Ok(frame)
    }
    /// Whether the source is an original full-frame image-filter capture.
    pub fn is_image_filter(self) -> bool {
        self.image_filter
    }
    /// Actual physical texture allocation.
    pub fn size(self) -> [u32; 2] {
        self.size
    }
    /// Original projection extent, never reconstructed from allocation/DPI.
    pub fn logical_extent(self) -> [f32; 2] {
        self.logical_extent
    }
    /// Original integer logical bookkeeping used by CanvasSize, independent of projection.
    pub fn logical_size(self) -> [u32; 2] {
        self.logical_size
    }
    /// Original semantic text/snapping/effect DPI.
    pub fn dpi_scale(self) -> f32 {
        self.dpi
    }
    /// Original compile-space placement, before path/stroke/text planning.
    pub fn source_to_raster(self) -> Matrix4 {
        self.placement
    }
    /// Actual root projection consumed by the canonical shaders.
    pub fn projection(self) -> [f32; 16] {
        self.projection
    }
    /// Original cache-source text presentation suppression.
    pub fn suppress_cached_cleartype(self) -> bool {
        self.suppress_cached_cleartype
    }
    /// Proven empty raw-cache realization; implicit input frames are nonempty.
    pub fn is_empty(self) -> bool {
        self.empty
    }
    /// Complete original effect frame, including independent output edges/UVs.
    pub fn effect_frame(self) -> Option<ManagedCaptureFrame> {
        self.effect
    }
    /// Complete original raw-cache source/DPI/device-cap generation.
    pub fn cache_frame(self) -> Option<CacheSamplerRasterFrame> {
        self.cache
    }
    /// Exact immutable target identity, including every original metadata bit.
    pub fn same_frame(self, other: Self) -> bool {
        self.size == other.size
            && self.image_filter == other.image_filter
            && self.logical_size == other.logical_size
            && self.logical_extent.map(f32::to_bits) == other.logical_extent.map(f32::to_bits)
            && self.dpi.to_bits() == other.dpi.to_bits()
            && self.placement.0.map(|r| r.map(f32::to_bits))
                == other.placement.0.map(|r| r.map(f32::to_bits))
            && self.projection.map(f32::to_bits) == other.projection.map(f32::to_bits)
            && self.suppress_cached_cleartype == other.suppress_cached_cleartype
            && self.empty == other.empty
            && match (self.effect, other.effect) {
                (Some(a), Some(b)) => a.same_capture(&b),
                (None, None) => true,
                _ => false,
            }
            && match (self.cache, other.cache) {
                (Some(a), Some(b)) => a.same_generation(b),
                (None, None) => true,
                _ => false,
            }
    }
}
