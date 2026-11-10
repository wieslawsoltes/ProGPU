//! Original managed source bounds, independent padding and capture sizing policy.
use crate::Error;
/// Original source doubles, including raw invalid metadata and signed zero.
/// Construction never sanitizes padding; frame realization performs admission.
#[derive(Clone, Copy, Debug)]
pub struct SourceCapture {
    /// Original source X/Y/width/height doubles.
    pub bounds: [f64; 4],
    /// Independent left/top/right/bottom padding doubles.
    pub padding: [f64; 4],
}
impl PartialEq for SourceCapture {
    fn eq(&self, other: &Self) -> bool {
        self.bounds.map(f64::to_bits) == other.bounds.map(f64::to_bits)
            && self.padding.map(f64::to_bits) == other.padding.map(f64::to_bits)
    }
}
impl Eq for SourceCapture {}
impl SourceCapture {
    /// Admit original source float-domain endpoints and each raw padding independently.
    pub fn validate(self) -> Result<(), Error> {
        let [x, y, w, h] = self.bounds;
        if self.bounds.iter().any(|v| !v.is_finite())
            || w <= 0.0
            || h <= 0.0
            || [x, y, x + w, y + h]
                .iter()
                .any(|v| !(*v as f32).is_finite())
            || self
                .padding
                .iter()
                .any(|v| !v.is_finite() || *v < 0.0 || !(*v as f32).is_finite())
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    fn edges(self) -> [f32; 4] {
        let [x, y, w, h] = self.bounds;
        [
            x as f32 - self.padding[0] as f32,
            y as f32 - self.padding[1] as f32,
            (x + w) as f32 + self.padding[2] as f32,
            (y + h) as f32 + self.padding[3] as f32,
        ]
    }
}
/// Original managed capture descriptor. Logical projection, physical allocation,
/// source output edges and texture UVs deliberately remain independent.
#[derive(Clone, Copy, Debug)]
pub struct ManagedCaptureFrame {
    /// Original padded X/Y/width/height.
    pub padded_bounds: [f32; 4],
    /// Actual floating logical projection extent.
    pub logical_extent: [f32; 2],
    /// Ceiling bookkeeping dimensions, never a substitute for projection extent.
    pub logical_render_size: [u32; 2],
    /// Original complete physical texture dimensions.
    pub pixel_size: [u32; 2],
    /// Semantic DPI retained for text and snapping.
    pub dpi: f32,
    /// Explicit outward physical-lattice route.
    pub has_physical_origin: bool,
    /// Actual independent projection-to-viewport scale.
    pub pixels_per_unit: [f32; 2],
    /// Signed float-exact physical origin before rebasing.
    pub physical_origin: [f32; 2],
    /// Complete rebased raster X/Y/width/height.
    pub raster_bounds: [f32; 4],
    /// Independently narrowed/rebased left/top/right/bottom output edges.
    pub output_edges: [f32; 4],
    /// Original output UV bounds in the complete source texture.
    pub texture_uv_bounds: [f32; 4],
    /// Original row-vector source-to-raster 4x4 matrix.
    pub source_to_raster: [f32; 16],
}
const IDENTITY: [f32; 16] = [
    1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0,
];
fn ceiling(v: f32) -> Result<u32, Error> {
    let c = v.ceil();
    if !c.is_finite() || c <= 0.0 || f64::from(c) > f64::from(u32::MAX) {
        return Err(Error::InvalidInput);
    }
    Ok(c as u32)
}
fn raster_padding(v: f32) -> f32 {
    if v.is_finite() && v > 0.0 { v } else { 0.0 }
}
impl ManagedCaptureFrame {
    /// Original ordinary visual effect capture with independently resolved X/Y padding.
    pub fn resolved(bounds: [f32; 4], padding: [f32; 2], dpi: f32) -> Result<Self, Error> {
        if bounds.iter().chain(&padding).any(|v| !v.is_finite())
            || bounds[2] <= 0.0
            || bounds[3] <= 0.0
        {
            return Err(Error::InvalidInput);
        }
        Self::padded(
            [
                bounds[0] - padding[0],
                bounds[1] - padding[1],
                bounds[2] + padding[0] * 2.0,
                bounds[3] + padding[1] * 2.0,
            ],
            dpi,
        )
    }

    fn padded(bounds: [f32; 4], dpi: f32) -> Result<Self, Error> {
        if bounds.iter().any(|v| !v.is_finite()) || !dpi.is_finite() || dpi <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let logical = [bounds[2].max(1.0), bounds[3].max(1.0)];
        let logical_render_size = [ceiling(logical[0])?, ceiling(logical[1])?];
        let pixel_size = [ceiling(logical[0] * dpi)?, ceiling(logical[1] * dpi)?];
        Ok(Self {
            padded_bounds: bounds,
            logical_extent: logical,
            logical_render_size,
            pixel_size,
            dpi,
            has_physical_origin: false,
            pixels_per_unit: [dpi; 2],
            physical_origin: [0.0; 2],
            raster_bounds: bounds,
            output_edges: [
                bounds[0],
                bounds[1],
                bounds[0] + bounds[2],
                bounds[1] + bounds[3],
            ],
            texture_uv_bounds: [0.0, 0.0, 1.0, 1.0],
            source_to_raster: IDENTITY,
        })
    }
    /// Original symmetric shader-padding or explicit raster-override policy.
    /// A nonfinite override selects zero; invalid shader padding remains invalid.
    pub fn legacy(
        bounds: [f32; 4],
        shader_padding: f32,
        raster_override: Option<f32>,
        dpi: f32,
    ) -> Result<Self, Error> {
        let padding = if let Some(value) = raster_override {
            raster_padding(value)
        } else {
            if shader_padding.is_nan() {
                f32::NAN
            } else {
                shader_padding.max(0.0).ceil()
            }
        };
        if bounds.iter().any(|v| !v.is_finite())
            || bounds[2] <= 0.0
            || bounds[3] <= 0.0
            || !padding.is_finite()
        {
            return Err(Error::InvalidInput);
        }
        Self::padded(
            [
                bounds[0] - padding,
                bounds[1] - padding,
                bounds[2] + padding * 2.0,
                bounds[3] + padding * 2.0,
            ],
            dpi,
        )
    }
    /// Original scalar managed source frame. Actual source rebasing follows
    /// independent endpoint inflation and cannot change the captured extent.
    pub fn source(
        source: SourceCapture,
        translation: [f32; 2],
        raster_override: Option<f32>,
        dpi: f32,
    ) -> Result<Self, Error> {
        source.validate()?;
        if translation.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        if let Some(value) = raster_override {
            return Self::legacy(
                [
                    source.bounds[0] as f32 + translation[0],
                    source.bounds[1] as f32 + translation[1],
                    source.bounds[2] as f32,
                    source.bounds[3] as f32,
                ],
                0.0,
                Some(value),
                dpi,
            );
        }
        let [left, top, right, bottom] = source.edges();
        let width = right - left;
        let height = bottom - top;
        if [left, top, right, bottom].iter().any(|v| !v.is_finite())
            || width <= 0.0
            || height <= 0.0
        {
            return Err(Error::InvalidInput);
        }
        Self::padded(
            [left + translation[0], top + translation[1], width, height],
            dpi,
        )
    }
    /// Original independent-axis physical source capture. Semantic DPI does not
    /// replace actual projection/viewport scale; far output edges stay independent.
    pub fn source_physical(
        source: SourceCapture,
        translation: [f32; 2],
        pixels_per_unit: [f32; 2],
        dpi: f32,
    ) -> Result<Self, Error> {
        source.validate()?;
        if translation.iter().any(|v| !v.is_finite())
            || pixels_per_unit.iter().any(|v| !v.is_finite() || *v <= 0.0)
            || !dpi.is_finite()
            || dpi <= 0.0
        {
            return Err(Error::InvalidInput);
        }
        let edges = source.edges();
        let extent = [edges[2] - edges[0], edges[3] - edges[1]];
        if edges.iter().chain(extent.iter()).any(|v| !v.is_finite())
            || extent.iter().any(|v| *v <= 0.0)
        {
            return Err(Error::InvalidInput);
        }
        let physical = std::array::from_fn::<_, 4, _>(|i| edges[i] * pixels_per_unit[i % 2]);
        let origin = [physical[0].floor(), physical[1].floor()];
        let far = [physical[2].ceil(), physical[3].ceil()];
        let exact = [
            f64::from(far[0]) - f64::from(origin[0]),
            f64::from(far[1]) - f64::from(origin[1]),
        ];
        if origin.iter().chain(far.iter()).any(|v| !v.is_finite())
            || origin.iter().any(|v| *v < -16777216.0)
            || far.iter().any(|v| *v > 16777216.0)
            || exact.iter().any(|v| *v <= 0.0 || *v > 16777216.0)
        {
            return Err(Error::InvalidInput);
        }
        let pixels = exact.map(|v| v as f32);
        let logical = [
            pixels[0] / pixels_per_unit[0],
            pixels[1] / pixels_per_unit[1],
        ];
        let raster = [
            origin[0] / pixels_per_unit[0] + translation[0],
            origin[1] / pixels_per_unit[1] + translation[1],
            logical[0],
            logical[1],
        ];
        let padded = [
            edges[0] + translation[0],
            edges[1] + translation[1],
            extent[0],
            extent[1],
        ];
        let output = [
            padded[0],
            padded[1],
            edges[2] + translation[0],
            edges[3] + translation[1],
        ];
        let translated = [
            translation[0] * pixels_per_unit[0],
            translation[1] * pixels_per_unit[1],
        ];
        let mut matrix = IDENTITY;
        matrix[0] = pixels_per_unit[0];
        matrix[5] = pixels_per_unit[1];
        matrix[12] = -origin[0] - translated[0];
        matrix[13] = -origin[1] - translated[1];
        if raster
            .iter()
            .chain(padded.iter())
            .chain(output.iter())
            .chain([matrix[12], matrix[13], 2.0 / logical[0], -2.0 / logical[1]].iter())
            .any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            padded_bounds: padded,
            logical_extent: logical,
            logical_render_size: [ceiling(logical[0])?, ceiling(logical[1])?],
            pixel_size: pixels.map(|v| v as u32),
            dpi,
            has_physical_origin: true,
            pixels_per_unit,
            physical_origin: origin,
            raster_bounds: raster,
            output_edges: output,
            texture_uv_bounds: std::array::from_fn(|i| {
                (physical[i] - origin[i % 2]) / pixels[i % 2]
            }),
            source_to_raster: matrix,
        })
    }
    /// Original exact cache identity, retaining all source and physical signed zeros.
    pub fn same_capture(&self, other: &Self) -> bool {
        self.has_physical_origin == other.has_physical_origin
            && self.logical_render_size == other.logical_render_size
            && self.pixel_size == other.pixel_size
            && self.dpi.to_bits() == other.dpi.to_bits()
            && self.logical_extent.map(f32::to_bits) == other.logical_extent.map(f32::to_bits)
            && self.padded_bounds.map(f32::to_bits) == other.padded_bounds.map(f32::to_bits)
            && self.raster_bounds.map(f32::to_bits) == other.raster_bounds.map(f32::to_bits)
            && self.output_edges.map(f32::to_bits) == other.output_edges.map(f32::to_bits)
            && self.pixels_per_unit.map(f32::to_bits) == other.pixels_per_unit.map(f32::to_bits)
            && self.physical_origin.map(f32::to_bits) == other.physical_origin.map(f32::to_bits)
            && self.texture_uv_bounds.map(f32::to_bits) == other.texture_uv_bounds.map(f32::to_bits)
            && self.source_to_raster[12].to_bits() == other.source_to_raster[12].to_bits()
            && self.source_to_raster[13].to_bits() == other.source_to_raster[13].to_bits()
    }
}
/// Original actual projection/viewport scale, after coefficient rounding.
pub fn source_pixels_per_unit(
    projection: [f32; 16],
    viewport: [f32; 4],
) -> Result<[f32; 2], Error> {
    if viewport.iter().any(|v| !v.is_finite())
        || viewport[2] <= 0.0
        || viewport[3] <= 0.0
        || !projection[0].is_finite()
        || !projection[5].is_finite()
        || projection[1] != 0.0
        || projection[4] != 0.0
        || projection[3] != 0.0
        || projection[7] != 0.0
        || projection[15] != 1.0
    {
        return Err(Error::InvalidInput);
    }
    let half = [viewport[2] * 0.5, viewport[3] * 0.5];
    let result = [projection[0].abs() * half[0], projection[5].abs() * half[1]];
    if result.iter().any(|v| !v.is_finite() || *v <= 0.0) {
        return Err(Error::InvalidInput);
    }
    Ok(result)
}
