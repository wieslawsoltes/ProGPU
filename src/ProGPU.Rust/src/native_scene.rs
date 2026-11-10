//! Original pointer-free native scene transport, semantic admission and ownership.
//!
//! An envelope proves only framing and scalar descriptor admission. It is not a
//! semantically validated scene and cannot be submitted to Renderer. Original
//! `NativeScene` applies the original ordered resource, command and stack gates.
//! Subsequent engine-specific payload and device admission remain independent;
//! neither transport nor semantic admission submits GPU work.
use std::sync::Arc;
pub mod builder;
pub use builder::{
    NativeSceneBuildError, NativeSceneBuildMetrics, NativeSceneBuilder, NativeSceneCompositeMask,
    NativeSceneFullImageCopy, NativeSceneHitTestOpacityMode, NativeSceneImageOptions,
    NativeSceneLayerHitTestMode, NativeSceneSaveOptions, NativeShapedTextSceneOptions,
};
pub mod identity;
pub use identity::NativeSceneContentHashes;
mod envelope;
mod hit;
pub(crate) mod semantic;
pub use semantic::{
    NativeScene, NativeSceneAdmissionError, NativeSceneMask, NativeScenePayload,
    NativeSceneResource, NativeSceneShader, NativeWireRecords,
};
pub mod wire;
mod writer;
pub use envelope::{CommandView, Commands, NativeSceneEnvelope, ResourceView, Resources};
pub use writer::{
    NativeSceneCommandSource, NativeScenePacket, NativeSceneResourceSource, NativeSceneStreamLayout,
};

pub const STREAM_MAGIC: u32 = 0x31534750;
pub const STREAM_VERSION: u32 = 1;
pub const ENDIAN_MARKER: u32 = 0x01020304;
pub const MAX_STREAM_BYTES: u32 = 256 * 1024 * 1024;
pub const MAX_COMMANDS: u32 = 1024 * 1024;
pub const MAX_RESOURCES: u32 = 256 * 1024;
pub const MAX_STACK_DEPTH: u32 = 64;
pub const MAX_MATERIALIZED_LAYERS: u32 = 16;
pub const MAX_PICTURE_DEPTH: u32 = 16;
pub const NO_INDEX: u32 = u32::MAX;
pub const RECORD_REQUIRED: u32 = 1;
pub const GLYPH_STYLED: u32 = 1 << 1;
pub const COLOR_GLYPH_BITMAPS: u32 = 1 << 2;
pub const EXTERNAL_IMAGE: u32 = 1 << 3;
pub const IMAGE_BGRA8: u32 = 1 << 4;
pub const IMAGE_R8: u32 = 1 << 5;
pub const IMAGE_PICTURE: u32 = 1 << 6;

/// Original record values, separate from the managed picture command enum.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeSceneResourceKind {
    AnalyticBatch = 1,
    PathBatch = 2,
    GlyphRun = 3,
    Image = 4,
    State = 5,
    LayerMask = 6,
    EffectChain = 7,
    BrushTable = 8,
    TextStyleTable = 9,
    GeometryBatch = 10,
    PointBatch = 11,
    VertexMesh = 12,
    StrokeBatch = 13,
    Line3DBatch = 14,
    Mesh3DBatch = 15,
    HitTestIndex = 16,
    GuidelineSet = 17,
    TileComposite = 18,
    WpfShaderEffect = 19,
}
impl TryFrom<u32> for NativeSceneResourceKind {
    type Error = ();
    fn try_from(value: u32) -> Result<Self, ()> {
        Ok(match value {
            1 => Self::AnalyticBatch,
            2 => Self::PathBatch,
            3 => Self::GlyphRun,
            4 => Self::Image,
            5 => Self::State,
            6 => Self::LayerMask,
            7 => Self::EffectChain,
            8 => Self::BrushTable,
            9 => Self::TextStyleTable,
            10 => Self::GeometryBatch,
            11 => Self::PointBatch,
            12 => Self::VertexMesh,
            13 => Self::StrokeBatch,
            14 => Self::Line3DBatch,
            15 => Self::Mesh3DBatch,
            16 => Self::HitTestIndex,
            17 => Self::GuidelineSet,
            18 => Self::TileComposite,
            19 => Self::WpfShaderEffect,
            _ => return Err(()),
        })
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeSceneCommandKind {
    Save = 1,
    Restore = 2,
    PushLayer = 3,
    PopLayer = 4,
    ClearTarget = 5,
    DrawAnalytic = 16,
    DrawPath = 17,
    DrawGlyphRun = 18,
    DrawImage = 19,
    DrawGeometry = 20,
    DrawPointBatch = 21,
    DrawVertexMesh = 22,
    DrawStrokeBatch = 23,
    DrawLine3DBatch = 24,
    DrawMesh3DBatch = 25,
    DrawPaintedGlyphRun = 26,
    DrawRgbGlyphRun = 27,
    DrawSourceCoverage = 28,
    DrawSourcePath = 29,
}
impl TryFrom<u32> for NativeSceneCommandKind {
    type Error = ();
    fn try_from(value: u32) -> Result<Self, ()> {
        Ok(match value {
            1 => Self::Save,
            2 => Self::Restore,
            3 => Self::PushLayer,
            4 => Self::PopLayer,
            5 => Self::ClearTarget,
            16 => Self::DrawAnalytic,
            17 => Self::DrawPath,
            18 => Self::DrawGlyphRun,
            19 => Self::DrawImage,
            20 => Self::DrawGeometry,
            21 => Self::DrawPointBatch,
            22 => Self::DrawVertexMesh,
            23 => Self::DrawStrokeBatch,
            24 => Self::DrawLine3DBatch,
            25 => Self::DrawMesh3DBatch,
            26 => Self::DrawPaintedGlyphRun,
            27 => Self::DrawRgbGlyphRun,
            28 => Self::DrawSourceCoverage,
            29 => Self::DrawSourcePath,
            _ => return Err(()),
        })
    }
}
impl NativeSceneCommandKind {
    pub fn is_draw(self) -> bool {
        self as u32 >= 16
    }
    pub fn expected_resource(self) -> Option<NativeSceneResourceKind> {
        use NativeSceneResourceKind as R;
        Some(match self {
            Self::DrawAnalytic => R::AnalyticBatch,
            Self::DrawPath | Self::DrawSourcePath => R::PathBatch,
            Self::DrawGlyphRun | Self::DrawPaintedGlyphRun | Self::DrawRgbGlyphRun => R::GlyphRun,
            Self::DrawImage => R::Image,
            Self::DrawGeometry => R::GeometryBatch,
            Self::DrawPointBatch => R::PointBatch,
            Self::DrawVertexMesh | Self::DrawSourceCoverage => R::VertexMesh,
            Self::DrawStrokeBatch => R::StrokeBatch,
            Self::DrawLine3DBatch => R::Line3DBatch,
            Self::DrawMesh3DBatch => R::Mesh3DBatch,
            _ => return None,
        })
    }
}
/// Original wire diagnostics. Transport errors do not claim full scene validity.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum NativeSceneValidationError {
    #[default]
    None = 0,
    Header = 1,
    Range = 2,
    Record = 3,
    Id = 4,
    Stack = 5,
    Value = 6,
    Generation = 7,
    Unsupported = 8,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
#[error("native scene transport {kind:?} at byte {offset}")]
pub struct NativeSceneTransportError {
    pub kind: NativeSceneValidationError,
    pub offset: u32,
    /// Original allocation-failure status is distinct from malformed input.
    pub out_of_memory: bool,
}
type TransportResult<T> = Result<T, NativeSceneTransportError>;
fn error(kind: NativeSceneValidationError, offset: u32) -> NativeSceneTransportError {
    NativeSceneTransportError {
        kind,
        offset,
        out_of_memory: false,
    }
}
fn reserve<T>(count: usize, offset: u32) -> TransportResult<Vec<T>> {
    let mut result = Vec::new();
    result
        .try_reserve_exact(count)
        .map_err(|_| NativeSceneTransportError {
            kind: NativeSceneValidationError::Record,
            offset,
            out_of_memory: true,
        })?;
    Ok(result)
}
