//! Original owned shader recipes, completed sampler producers and immutable leases.
//! Preparation happens only at an actual renderer target, never during replay.
use super::{
    ManagedShaderDescriptor, ManagedShaderParameters, ManagedShaderProgram, ManagedShaderSampler,
    SourceCapture,
};
use crate::{
    Error,
    image::{GpuImage, ImageSampling},
    renderer::{CacheSamplerRaster, CapturedScene, CapturedSourceScene},
    scene::Scene,
};
use std::{future::Future, pin::Pin, sync::Arc};

/// Independently owned immutable source recipe. Implementations may realize
/// previously recorded sampler scenes through the exclusive preparation context.
/// They must not read mutable source UI, encode the consumer draw, or substitute
/// an outer target for the supplied actual target. Browser futures remain local.
pub trait ShaderEffectPreparation {
    /// Transfer one newly owned parameter generation for this actual target.
    /// Cancellation drops only this candidate's resources; completed earlier
    /// generations remain independently leased by the renderer and their draws.
    fn prepare<'a>(
        &'a self,
        context: crate::renderer::ShaderEffectPreparationContext<'a>,
    ) -> Pin<Box<dyn Future<Output = Result<OwnedShaderEffectParameters, Error>> + 'a>>;
}

/// Independent lease on one immutable source identity, original capture metadata
/// and preparation recipe. Dropping a caller lease cannot retire recorded users.
#[derive(Clone)]
pub struct OwnedShaderEffectSource(Arc<SourceGeneration>);
struct SourceGeneration {
    capture: SourceCapture,
    translation: [f32; 2],
    preparation: Box<dyn ShaderEffectPreparation>,
}
impl std::fmt::Debug for OwnedShaderEffectSource {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OwnedShaderEffectSource")
            .field("capture", &self.0.capture)
            .field("translation", &self.0.translation)
            .finish_non_exhaustive()
    }
}
impl OwnedShaderEffectSource {
    /// Move the original capture metadata and immutable preparation owner into
    /// a fresh source generation. Invalid metadata is rejected before callbacks.
    pub fn new(
        capture: SourceCapture,
        translation: [f32; 2],
        preparation: Box<dyn ShaderEffectPreparation>,
    ) -> Result<Self, Error> {
        capture.validate()?;
        if translation.iter().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        Ok(Self(Arc::new(SourceGeneration {
            capture,
            translation,
            preparation,
        })))
    }
    /// Exact original bounds and four independent padding doubles.
    pub fn source_capture(&self) -> SourceCapture {
        self.0.capture
    }
    /// Source host rebase applied after the independent capture extent.
    pub fn source_translation(&self) -> [f32; 2] {
        self.0.translation
    }
    /// Actual source owner identity, independent of later target variants.
    pub fn same_source(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
    pub(crate) fn identity(&self) -> usize {
        Arc::as_ptr(&self.0) as usize
    }
    pub(crate) fn preparation(&self) -> &dyn ShaderEffectPreparation {
        self.0.preparation.as_ref()
    }
}

/// Independently leased ordered source picture and recipe. The original source
/// recording stays intact while each physical target realizes its own captures.
#[derive(Clone)]
pub struct OwnedShaderEffectRecording(Arc<RecordingGeneration>);
struct RecordingGeneration {
    scene: Arc<Scene>,
    source: OwnedShaderEffectSource,
}
impl std::fmt::Debug for OwnedShaderEffectRecording {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OwnedShaderEffectRecording")
            .field("source", &self.0.source)
            .finish_non_exhaustive()
    }
}
impl OwnedShaderEffectRecording {
    /// Retain balanced immutable picture and independent recipe generations.
    /// This records ownership only; no source capture or font work occurs.
    pub fn new(
        scene: impl Into<Arc<Scene>>,
        source: OwnedShaderEffectSource,
    ) -> Result<Self, Error> {
        let scene = scene.into();
        scene.validate_owned_shader_source()?;
        Ok(Self(Arc::new(RecordingGeneration { scene, source })))
    }
    /// Original source picture, before any outer target placement.
    pub fn scene(&self) -> &Arc<Scene> {
        &self.0.scene
    }
    /// Actual independently retained recipe and capture descriptor.
    pub fn source(&self) -> &OwnedShaderEffectSource {
        &self.0.source
    }
    /// Whether two leases retain precisely the same recorded generation.
    pub fn same_recording(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
    pub(crate) fn identity(&self) -> usize {
        Arc::as_ptr(&self.0) as usize
    }
}

/// Completed sampler producer ownership. A borrowed `GpuImage` cannot construct
/// this type. Consume an actual completed capture/cache token; the original
/// producer and all its retained source metadata stay alive through every lease.
///
/// This does not freeze external aliases that callers previously retained.
/// Once published, callers must not mutate or explicitly destroy its texture.
#[derive(Clone)]
pub struct OwnedShaderEffectTexture(Arc<TextureGeneration>);
enum TextureGeneration {
    Capture(CapturedScene),
    Source(CapturedSourceScene),
    Cache(CacheSamplerRaster),
}
impl std::fmt::Debug for OwnedShaderEffectTexture {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OwnedShaderEffectTexture")
            .field("size", &self.image().size())
            .finish_non_exhaustive()
    }
}
impl OwnedShaderEffectTexture {
    /// Transfer an actual independently allocated completed scene capture.
    pub fn from_capture(capture: CapturedScene) -> Self {
        Self(Arc::new(TextureGeneration::Capture(capture)))
    }
    /// Transfer exact source picture, frame and completed source pixels together.
    pub fn from_source_capture(capture: CapturedSourceScene) -> Self {
        Self(Arc::new(TextureGeneration::Source(capture)))
    }
    /// Retain actual raw-cache pixels, source identity/revision and raster policy.
    /// Cloned cache tokens remain leases on the same completed immutable owner.
    pub fn from_cache_sampler(capture: CacheSamplerRaster) -> Self {
        Self(Arc::new(TextureGeneration::Cache(capture)))
    }
    /// Borrow the exact completed image. Do not mutate or destroy its texture.
    pub fn image(&self) -> &GpuImage {
        match self.0.as_ref() {
            TextureGeneration::Capture(c) => c.image(),
            TextureGeneration::Source(c) => c.image(),
            TextureGeneration::Cache(c) => c.image(),
        }
    }
    /// Actual producer's creating device, without a global/current lookup.
    pub fn device(&self) -> &wgpu::Device {
        self.image().device()
    }
    /// Actual source capture when this producer retained an original picture.
    pub fn source_capture(&self) -> Option<&CapturedSourceScene> {
        match self.0.as_ref() {
            TextureGeneration::Source(c) => Some(c),
            TextureGeneration::Cache(c) => Some(c.capture()),
            _ => None,
        }
    }
    /// Exact selected cache generation, if this is an independent raw sampler.
    pub fn cache_sampler(&self) -> Option<&CacheSamplerRaster> {
        match self.0.as_ref() {
            TextureGeneration::Cache(c) => Some(c),
            _ => None,
        }
    }
    /// Whether these leases retain the same completed producer token.
    pub fn same_generation(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}
impl From<CapturedScene> for OwnedShaderEffectTexture {
    fn from(value: CapturedScene) -> Self {
        Self::from_capture(value)
    }
}
impl From<CapturedSourceScene> for OwnedShaderEffectTexture {
    fn from(value: CapturedSourceScene) -> Self {
        Self::from_source_capture(value)
    }
}
impl From<CacheSamplerRaster> for OwnedShaderEffectTexture {
    fn from(value: CacheSamplerRaster) -> Self {
        Self::from_cache_sampler(value)
    }
}

/// Original ordered sampler slot with an independent completed producer lease.
/// Null slots and duplicate registers retain their original precedence semantics.
#[derive(Clone, Debug)]
pub struct OwnedShaderEffectSampler {
    register: u32,
    texture: Option<OwnedShaderEffectTexture>,
    sampling: ImageSampling,
}
impl OwnedShaderEffectSampler {
    /// Retain a completed producer or an original null/implicit-input slot.
    pub fn new(
        register: u32,
        texture: Option<OwnedShaderEffectTexture>,
        sampling: ImageSampling,
    ) -> Result<Self, Error> {
        ManagedShaderSampler::new(register, None, sampling)?;
        Ok(Self {
            register,
            texture,
            sampling,
        })
    }
    /// Original sampler register, including duplicate source entries.
    pub fn register(&self) -> u32 {
        self.register
    }
    /// Actual completed producer lease; null remains distinct from transparent.
    pub fn texture(&self) -> Option<&OwnedShaderEffectTexture> {
        self.texture.as_ref()
    }
    /// Original independently selected filtering policy.
    pub fn sampling(&self) -> ImageSampling {
        self.sampling
    }
}

/// Construction-time immutable-source parameters. Implicit input cannot be
/// supplied here: the renderer captures that source at the actual target.
/// Every nonnull sampler requires an actual completed producer token.
#[derive(Clone, Debug)]
pub struct OwnedShaderEffectDescriptor {
    /// Exact original WGSL body and explicit/derived key.
    pub program: Arc<ManagedShaderProgram>,
    /// Complete original float inventory; raw bits and unused suffix survive.
    pub constants: Arc<[f32]>,
    /// Complete original sampler order, including duplicate and null records.
    pub samplers: Vec<OwnedShaderEffectSampler>,
    /// Actual implicit source register in 0..16.
    pub source_register: u32,
    /// Original implicit-input filtering policy.
    pub sampling: ImageSampling,
}
impl OwnedShaderEffectDescriptor {
    /// Original zero-constant, register-zero, linear-input defaults.
    pub fn new(program: impl Into<Arc<ManagedShaderProgram>>) -> Self {
        Self {
            program: program.into(),
            constants: Arc::from([]),
            samplers: Vec::new(),
            source_register: 0,
            sampling: ImageSampling::Linear,
        }
    }
}

/// Move-only prepared result. Returning it transfers this complete immutable
/// generation once; it has no Clone implementation or independently disposable
/// payload. The owning renderer may retain one Arc through all prepared draws.
pub struct OwnedShaderEffectParameters {
    snapshot: ManagedShaderParameters,
    samplers: Arc<[OwnedShaderEffectSampler]>,
}
impl std::fmt::Debug for OwnedShaderEffectParameters {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OwnedShaderEffectParameters")
            .field("sampler_count", &self.samplers.len())
            .field(
                "source_register",
                &self.snapshot.descriptor().source_register,
            )
            .finish_non_exhaustive()
    }
}
impl OwnedShaderEffectParameters {
    /// Freeze complete original constants/register metadata and independent
    /// sampler leases. There is no borrowed-resource or implicit-input argument.
    pub fn new(descriptor: OwnedShaderEffectDescriptor) -> Result<Self, Error> {
        let mut samplers = Vec::new();
        samplers
            .try_reserve_exact(descriptor.samplers.len())
            .map_err(|_| Error::Capacity)?;
        for sampler in &descriptor.samplers {
            samplers.push(ManagedShaderSampler::new(
                sampler.register,
                sampler
                    .texture
                    .as_ref()
                    .map(|texture| texture.image().clone()),
                sampler.sampling,
            )?);
        }
        let snapshot = ManagedShaderParameters::new(ManagedShaderDescriptor {
            program: descriptor.program,
            constants: descriptor.constants,
            samplers: samplers.into(),
            input: None,
            source_register: descriptor.source_register,
            sampling: descriptor.sampling,
            source_overrides_sampler: false,
        })?;
        Ok(Self {
            snapshot,
            samplers: descriptor.samplers.into(),
        })
    }
    /// Original independent producer leases in complete descriptor order.
    pub fn samplers(&self) -> &[OwnedShaderEffectSampler] {
        &self.samplers
    }
    /// Exact original WGSL generation, without target-time wrapper replacement.
    pub fn program(&self) -> &Arc<ManagedShaderProgram> {
        &self.snapshot.descriptor().program
    }
    /// Complete immutable constant inventory, preserving original bits.
    pub fn constants(&self) -> &[f32] {
        &self.snapshot.descriptor().constants
    }
    /// Original implicit source register.
    pub fn source_register(&self) -> u32 {
        self.snapshot.descriptor().source_register
    }
    /// Original implicit input filtering policy.
    pub fn sampling(&self) -> ImageSampling {
        self.snapshot.descriptor().sampling
    }
    pub(crate) fn snapshot(&self) -> &ManagedShaderParameters {
        &self.snapshot
    }
    pub(crate) fn validate_device(&self, device: &wgpu::Device) -> Result<(), Error> {
        self.snapshot.validate()?;
        // Validate every retained producer, not only the winning nonnull slots.
        // A later target cannot hide an unrelated-device duplicate behind one
        // selected register or dispose an earlier target's sampler ownership.
        for sampler in self.samplers.iter() {
            if let Some(texture) = &sampler.texture {
                if texture.device() != device {
                    return Err(Error::ResourceDomain);
                }
                if texture
                    .image()
                    .size()
                    .iter()
                    .any(|v| *v == 0 || *v > device.limits().max_texture_dimension_2d)
                {
                    return Err(Error::Capacity);
                }
            }
        }
        Ok(())
    }
}
