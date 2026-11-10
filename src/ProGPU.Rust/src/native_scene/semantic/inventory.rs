//! Typed borrowed views of the complete immutable admitted stream generation.
use super::*;
use std::marker::PhantomData;

/// Original fixed-record inventory without pointer alignment or object casts.
/// `is_exact` distinguishes a complete inventory from deferred malformed engine
/// input that the original scene admission itself intentionally does not reject.
#[derive(Clone, Copy, Debug)]
pub struct NativeWireRecords<'a, T> {
    bytes: &'a [u8],
    marker: PhantomData<T>,
}
impl<'a, T: wire::WireRecord> NativeWireRecords<'a, T> {
    fn new(bytes: &'a [u8]) -> Self {
        Self {
            bytes,
            marker: PhantomData,
        }
    }
    pub fn bytes(self) -> &'a [u8] {
        self.bytes
    }
    pub fn len(self) -> usize {
        self.bytes.len() / T::WIRE_SIZE
    }
    pub fn is_empty(self) -> bool {
        self.bytes.is_empty()
    }
    pub fn is_exact(self) -> bool {
        self.bytes.len() % T::WIRE_SIZE == 0
    }
    pub fn get(self, index: usize) -> Option<T> {
        let start = index.checked_mul(T::WIRE_SIZE)?;
        T::read(self.bytes.get(start..start.checked_add(T::WIRE_SIZE)?)?)
    }
    pub fn iter(self) -> impl ExactSizeIterator<Item = T> + 'a
    where
        T: 'a,
    {
        self.bytes
            .chunks_exact(T::WIRE_SIZE)
            .map(|bytes| T::read(bytes).expect("complete original fixed record"))
    }
}
#[derive(Clone, Copy, Debug)]
pub enum NativeSceneMask {
    Analytic(wire::LayerMask),
    Coverage(wire::LayerCoverageMask),
    Chain(wire::LayerMaskChain),
    Vector(wire::LayerVectorMask),
    Brush(wire::LayerBrushMask),
    Composite(wire::LayerCompositeMask),
    Geometry(wire::LayerGeometryMask),
    Picture(wire::LayerPictureMask),
}
#[derive(Clone, Copy, Debug)]
pub enum NativeSceneShader {
    Program(wire::ShaderEffect),
    Picture(wire::ShaderEffectPicture),
    Derivatives(wire::ShaderEffectDerivatives),
    Capture(wire::ShaderEffectCapture),
    Samples(wire::ShaderEffectSamples),
    Affine(wire::ShaderEffectAffine),
}
/// Actual original resource payload family. Auxiliary arenas remain available
/// through the same ResourceView, including complete nested pictures and all
/// inactive source fields. Classification does not skip engine admission.
#[derive(Clone, Copy, Debug)]
pub enum NativeScenePayload<'a> {
    Analytic(NativeWireRecords<'a, wire::AnalyticPrimitive>),
    Paths(NativeWireRecords<'a, wire::PathFill>),
    Glyphs(NativeWireRecords<'a, wire::GlyphOutline>),
    ColorGlyphs(NativeWireRecords<'a, wire::ColorGlyphBitmap>),
    ImagePixels(&'a [u8]),
    ExternalImage,
    PictureImage {
        picture: wire::PictureImage,
        presentation: wire::Presentation,
    },
    State(wire::State),
    Mask(NativeSceneMask),
    EffectChain(wire::EffectChain),
    Brushes(NativeWireRecords<'a, wire::Brush>),
    TextStyles(NativeWireRecords<'a, wire::TextStyle>),
    Geometry(NativeWireRecords<'a, wire::GeometryPrimitive>),
    Points(NativeWireRecords<'a, wire::PointBatch>),
    VertexMeshes(NativeWireRecords<'a, wire::VertexMesh>),
    Strokes(NativeWireRecords<'a, wire::Stroke>),
    Lines3d(NativeWireRecords<'a, wire::Line3d>),
    Meshes3d(NativeWireRecords<'a, wire::Mesh3d>),
    HitTest(wire::HitTestIndex),
    Guidelines(wire::GuidelineSet),
    TileComposite(wire::TileComposite),
    Shader(NativeSceneShader),
    /// Only original optional unknown kinds can reach this retained view.
    OptionalUnknown {
        kind: u32,
        payload: &'a [u8],
    },
}
#[derive(Clone, Copy, Debug)]
pub struct NativeSceneResource<'a> {
    view: ResourceView<'a>,
}
impl<'a> NativeSceneResource<'a> {
    pub(super) fn new(view: ResourceView<'a>) -> Self {
        Self { view }
    }
    pub fn raw(self) -> ResourceView<'a> {
        self.view
    }
    pub fn auxiliary_records<T: wire::WireRecord>(self) -> NativeWireRecords<'a, T> {
        NativeWireRecords::new(self.view.auxiliary)
    }
    pub fn payload(self) -> TransportResult<NativeScenePayload<'a>> {
        use NativeScenePayload as P;
        let r = self.view.record;
        let bytes = self.view.payload;
        let failure = || error(E::Value, r.payload_offset);
        macro_rules! read {
            ($ty:ty) => {
                <$ty>::read(bytes).ok_or_else(failure)?
            };
        }
        Ok(match r.kind {
            SCENE_RESOURCE_ANALYTIC_BATCH => P::Analytic(NativeWireRecords::new(bytes)),
            SCENE_RESOURCE_PATH_BATCH => P::Paths(NativeWireRecords::new(bytes)),
            SCENE_RESOURCE_GLYPH_RUN if r.flags & COLOR_GLYPH_BITMAPS != 0 => {
                P::ColorGlyphs(NativeWireRecords::new(bytes))
            }
            SCENE_RESOURCE_GLYPH_RUN => P::Glyphs(NativeWireRecords::new(bytes)),
            SCENE_RESOURCE_IMAGE if r.flags & EXTERNAL_IMAGE != 0 => P::ExternalImage,
            SCENE_RESOURCE_IMAGE if r.flags & IMAGE_PICTURE != 0 => {
                let (picture, presentation) =
                    values::read_picture_image(bytes).ok_or_else(failure)?;
                P::PictureImage {
                    picture,
                    presentation,
                }
            }
            SCENE_RESOURCE_IMAGE => P::ImagePixels(bytes),
            SCENE_RESOURCE_STATE => P::State(read!(wire::State)),
            SCENE_RESOURCE_LAYER_MASK => {
                let kind = <u32 as wire::WireRecord>::read(bytes.get(4..).ok_or_else(failure)?)
                    .ok_or_else(failure)?;
                P::Mask(match kind {
                    SCENE_LAYER_MASK_ROUNDED_RECTANGLE => {
                        NativeSceneMask::Analytic(read!(wire::LayerMask))
                    }
                    SCENE_LAYER_MASK_COVERAGE_BITMAP => {
                        NativeSceneMask::Coverage(read!(wire::LayerCoverageMask))
                    }
                    SCENE_LAYER_MASK_ANALYTIC_CHAIN => {
                        NativeSceneMask::Chain(read!(wire::LayerMaskChain))
                    }
                    SCENE_LAYER_MASK_VECTOR_CLIP_CHAIN => {
                        NativeSceneMask::Vector(read!(wire::LayerVectorMask))
                    }
                    SCENE_LAYER_MASK_BRUSH => NativeSceneMask::Brush(read!(wire::LayerBrushMask)),
                    SCENE_LAYER_MASK_COMPOSITE => {
                        NativeSceneMask::Composite(read!(wire::LayerCompositeMask))
                    }
                    SCENE_LAYER_MASK_GEOMETRY => {
                        NativeSceneMask::Geometry(read!(wire::LayerGeometryMask))
                    }
                    SCENE_LAYER_MASK_PICTURE => {
                        NativeSceneMask::Picture(read!(wire::LayerPictureMask))
                    }
                    _ => return Err(failure()),
                })
            }
            SCENE_RESOURCE_EFFECT_CHAIN => P::EffectChain(read!(wire::EffectChain)),
            SCENE_RESOURCE_BRUSH_TABLE => P::Brushes(NativeWireRecords::new(bytes)),
            SCENE_RESOURCE_TEXT_STYLE_TABLE => P::TextStyles(NativeWireRecords::new(bytes)),
            SCENE_RESOURCE_GEOMETRY_BATCH => P::Geometry(NativeWireRecords::new(bytes)),
            SCENE_RESOURCE_POINT_BATCH => P::Points(NativeWireRecords::new(bytes)),
            SCENE_RESOURCE_VERTEX_MESH => P::VertexMeshes(NativeWireRecords::new(bytes)),
            SCENE_RESOURCE_STROKE_BATCH => P::Strokes(NativeWireRecords::new(bytes)),
            SCENE_RESOURCE_LINE_3D_BATCH => P::Lines3d(NativeWireRecords::new(bytes)),
            SCENE_RESOURCE_MESH_3D_BATCH => P::Meshes3d(NativeWireRecords::new(bytes)),
            SCENE_RESOURCE_HIT_TEST_INDEX => P::HitTest(read!(wire::HitTestIndex)),
            SCENE_RESOURCE_GUIDELINE_SET => P::Guidelines(read!(wire::GuidelineSet)),
            SCENE_RESOURCE_TILE_COMPOSITE => P::TileComposite(read!(wire::TileComposite)),
            SCENE_RESOURCE_WPF_SHADER_EFFECT => {
                let version = <u32 as wire::WireRecord>::read(bytes.get(4..).ok_or_else(failure)?)
                    .ok_or_else(failure)?;
                P::Shader(match version {
                    1 => NativeSceneShader::Program(read!(wire::ShaderEffect)),
                    2 => NativeSceneShader::Picture(read!(wire::ShaderEffectPicture)),
                    3 => NativeSceneShader::Derivatives(read!(wire::ShaderEffectDerivatives)),
                    4 => NativeSceneShader::Capture(read!(wire::ShaderEffectCapture)),
                    5 => NativeSceneShader::Samples(read!(wire::ShaderEffectSamples)),
                    6 => NativeSceneShader::Affine(read!(wire::ShaderEffectAffine)),
                    _ => return Err(failure()),
                })
            }
            kind => P::OptionalUnknown {
                kind,
                payload: bytes,
            },
        })
    }
}
