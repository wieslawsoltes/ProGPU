//! Original line/scatter data contracts, separate from target GPU realization.
use crate::{
    Error,
    vector::{Brush, BrushKind, Matrix4},
};
use std::{ops::Range, sync::Arc};

/// Original point record shape. Radii are physical pixels in the scatter shader.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum SeriesLayout {
    Xy,
    XyRadius,
}
impl SeriesLayout {
    pub fn stride(self) -> usize {
        match self {
            Self::Xy => 2,
            Self::XyRadius => 3,
        }
    }
}
/// Actual immutable source slice; clones retain the original full buffer owner.
/// NaN and infinity coordinates retain their original shader-side behavior.
#[derive(Clone, Debug)]
pub struct SeriesData {
    values: Arc<[f32]>,
    range: Range<usize>,
    count: u32,
    layout: SeriesLayout,
}
impl SeriesData {
    pub fn new(values: Arc<[f32]>, count: u32, layout: SeriesLayout) -> Result<Self, Error> {
        Self::slice(values, 0, count, layout)
    }
    pub fn slice(
        values: Arc<[f32]>,
        offset: usize,
        count: u32,
        layout: SeriesLayout,
    ) -> Result<Self, Error> {
        let length = (count as usize)
            .checked_mul(layout.stride())
            .ok_or(Error::Capacity)?;
        let end = offset.checked_add(length).ok_or(Error::Capacity)?;
        if end > values.len() {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            values,
            range: offset..end,
            count,
            layout,
        })
    }
    pub fn values(&self) -> &[f32] {
        &self.values[self.range.clone()]
    }
    pub fn point_count(&self) -> u32 {
        self.count
    }
    pub fn layout(&self) -> SeriesLayout {
        self.layout
    }
    pub fn owner(&self) -> &Arc<[f32]> {
        &self.values
    }
    pub fn offset(&self) -> usize {
        self.range.start
    }
}
/// Original shaders remain distinct: the extension line coverage is not the
/// built-in chart line coverage, even though their uniform layouts are identical.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum SeriesShader {
    #[default]
    BuiltIn,
    Extension,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum SeriesKind {
    Line,
    Scatter,
}
/// Actual packing work, independent of the canonical GPU rendering stage.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SeriesPackingPath {
    DirectUpload,
    CpuSimd,
    ScalarReference,
}

/// Source CPU generation or an explicitly uploaded same-device static generation.
#[derive(Clone, Debug)]
pub enum SeriesSource {
    Data(SeriesData),
    #[cfg(feature = "gpu")]
    Gpu(crate::renderer::GpuSeriesBuffer),
}
impl SeriesSource {
    pub fn as_data(&self) -> Option<&SeriesData> {
        match self {
            Self::Data(data) => Some(data),
            #[cfg(feature = "gpu")]
            Self::Gpu(_) => None,
        }
    }

    pub fn point_count(&self) -> u32 {
        match self {
            Self::Data(d) => d.point_count(),
            #[cfg(feature = "gpu")]
            Self::Gpu(d) => d.point_count(),
        }
    }
    pub fn layout(&self) -> SeriesLayout {
        match self {
            Self::Data(d) => d.layout(),
            #[cfg(feature = "gpu")]
            Self::Gpu(d) => d.layout(),
        }
    }
}
impl From<SeriesData> for SeriesSource {
    fn from(data: SeriesData) -> Self {
        Self::Data(data)
    }
}

/// Complete original draw metadata. Scale/translation apply to data before the
/// complete matrix and target projection; no CPU geometry expansion is performed.
#[derive(Clone, Debug)]
pub struct SeriesDraw {
    pub source: SeriesSource,
    pub kind: SeriesKind,
    pub shader: SeriesShader,
    pub transform: Matrix4,
    pub scale: [f32; 2],
    pub translate: [f32; 2],
    pub radius_or_width: f32,
    pub brush: Option<Arc<Brush>>,
    pub semantic_opacity: f32,
    /// Policy only for the original XY-to-XYR packing operation. Forced GPU
    /// packing rejects until an equivalent owned producer exists; drawing stays GPU.
    pub packing: crate::path::ExecutionPreference,
}
impl SeriesDraw {
    pub fn new(kind: SeriesKind, source: impl Into<SeriesSource>) -> Self {
        Self {
            source: source.into(),
            kind,
            shader: SeriesShader::BuiltIn,
            transform: Matrix4::IDENTITY,
            scale: [1.0; 2],
            translate: [0.0; 2],
            radius_or_width: 1.0,
            brush: None,
            semantic_opacity: 1.0,
            packing: crate::path::ExecutionPreference::Automatic,
        }
    }
    pub fn validate(&self) -> Result<(), Error> {
        // Original line storage is XY. Scatter may expand XY or retain XYZ.
        if self.kind == SeriesKind::Line && self.source.layout() != SeriesLayout::Xy {
            return Err(Error::InvalidInput);
        }
        if !self.semantic_opacity.is_finite() {
            return Err(Error::InvalidInput);
        }
        #[cfg(feature = "gpu")]
        if matches!(self.source, SeriesSource::Gpu(_))
            && self.kind == SeriesKind::Scatter
            && self.source.layout() != SeriesLayout::XyRadius
        {
            return Err(Error::InvalidInput);
        }
        self.packing_path()?;
        Ok(())
    }
    pub fn packing_path(&self) -> Result<SeriesPackingPath, Error> {
        if self.kind != SeriesKind::Scatter || self.source.layout() != SeriesLayout::Xy {
            return Ok(SeriesPackingPath::DirectUpload);
        }
        use crate::path::ExecutionPreference as P;
        match self.packing {
            P::Automatic | P::CpuSimd => Ok(SeriesPackingPath::CpuSimd),
            P::ScalarReference => Ok(SeriesPackingPath::ScalarReference),
            P::NativeCompute | P::GpuShader => Err(Error::UnsupportedExecution),
        }
    }
    pub fn is_renderable(&self) -> bool {
        self.source.point_count() >= if self.kind == SeriesKind::Line { 2 } else { 1 }
    }
    /// Original ResolveDrawCallBrushColor: nonsolid or missing paint is white;
    /// brush opacity and current semantic opacity multiply only the alpha lane.
    pub fn color(&self) -> [f32; 4] {
        let mut color = match self.brush.as_deref().map(|b| &b.kind) {
            Some(BrushKind::Solid(c)) => *c,
            _ => [1.0; 4],
        };
        color[3] *= self.brush.as_ref().map_or(1.0, |b| b.opacity) * self.semantic_opacity;
        color
    }
    pub fn placed(&self, transform: Matrix4) -> Self {
        let mut result = self.clone();
        result.transform = result.transform * transform;
        result
    }
    pub fn with_opacity(&self, opacity: f32) -> Result<Self, Error> {
        let mut result = self.clone();
        result.semantic_opacity *= opacity;
        result.validate()?;
        Ok(result)
    }
}

#[cfg(feature = "gpu")]
pub(crate) fn pack_scatter(
    data: &SeriesData,
    radius: f32,
    path: SeriesPackingPath,
) -> Result<Vec<f32>, Error> {
    let count = data.point_count() as usize;
    let size = count.checked_mul(3).ok_or(Error::Capacity)?;
    let mut output = Vec::new();
    output
        .try_reserve_exact(size)
        .map_err(|_| Error::Capacity)?;
    let source = data.values();
    let mut i = 0;
    if path == SeriesPackingPath::CpuSimd {
        use wide::f32x4 as V;
        while i + 4 <= count {
            let p = &source[i * 2..i * 2 + 8];
            // Independent bit-preserving lanes: no conversion or arithmetic.
            output.extend_from_slice(&V::from([p[0], p[1], radius, p[2]]).to_array());
            output.extend_from_slice(&V::from([p[3], radius, p[4], p[5]]).to_array());
            output.extend_from_slice(&V::from([radius, p[6], p[7], radius]).to_array());
            i += 4;
        }
    }
    while i < count {
        output.extend_from_slice(&[source[i * 2], source[i * 2 + 1], radius]);
        i += 1;
    }
    Ok(output)
}
