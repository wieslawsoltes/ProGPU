//! Original typed WpfShaderEffect visual metadata and independent recipe ownership.
use super::*;
use crate::shader_effect::{ManagedShaderParameters, OwnedShaderEffectSource, SourceCapture};

/// Immutable WpfShaderEffect generation. Generic parameter constructors preserve
/// borrowed sampler semantics; owned recipes retain independently leased producers.
/// This neutral adapter does not assert any external WPF provider qualification.
#[derive(Clone)]
pub struct WpfVisualEffect(Arc<Generation>);
#[derive(Clone)]
struct Generation {
    parameters: Parameters,
    source_capture: Option<SourceCapture>,
    padding: f32,
    capture_source_visual_opacity: bool,
}
#[derive(Clone)]
enum Parameters {
    Borrowed(ManagedShaderParameters),
    Owned(OwnedShaderEffectSource),
}
impl std::fmt::Debug for WpfVisualEffect {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WpfVisualEffect")
            .field("source_capture", &self.0.source_capture)
            .field("padding", &self.0.padding)
            .field(
                "capture_source_visual_opacity",
                &self.0.capture_source_visual_opacity,
            )
            .field("owned_recipe", &self.owned_source().is_some())
            .finish_non_exhaustive()
    }
}
impl PartialEq for WpfVisualEffect {
    fn eq(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}
impl Eq for WpfVisualEffect {}
impl WpfVisualEffect {
    /// Original generic defaults: scalar zero padding and opacity/mask on output.
    /// Nonnull samplers keep their original borrowed-image contract.
    pub fn new(parameters: ManagedShaderParameters) -> Result<Self, Error> {
        parameters.validate()?;
        Ok(Self(Arc::new(Generation {
            parameters: Parameters::Borrowed(parameters),
            source_capture: None,
            padding: 0.0,
            capture_source_visual_opacity: false,
        })))
    }
    /// Retain an immutable actual-target source recipe and its exact original
    /// four-edge capture descriptor. The explicit alpha-order flag is independent
    /// of source ownership; it never moves geometry clips into the capture.
    pub fn from_owned_source(
        source: OwnedShaderEffectSource,
        capture_source_visual_opacity: bool,
    ) -> Self {
        Self(Arc::new(Generation {
            source_capture: Some(source.source_capture()),
            parameters: Parameters::Owned(source),
            padding: 0.0,
            capture_source_visual_opacity,
        }))
    }
    /// Select source input alpha ordering explicitly for this new immutable generation.
    pub fn with_source_visual_opacity(mut self, value: bool) -> Self {
        Arc::make_mut(&mut self.0).capture_source_visual_opacity = value;
        self
    }
    /// Preserve raw scalar padding, including invalid values and signed zero.
    /// It applies only to the original legacy frame; source padding is independent.
    pub fn with_padding(mut self, padding: f32) -> Self {
        Arc::make_mut(&mut self.0).padding = padding;
        self
    }
    /// Freeze original independent double bounds/padding for borrowed parameters.
    /// An owned source already owns its descriptor and cannot be rebound here.
    pub fn with_source_capture(mut self, source: Option<SourceCapture>) -> Result<Self, Error> {
        if self.owned_source().is_some() {
            return Err(Error::ResourceDomain);
        }
        if let Some(source) = source {
            source.validate()?;
        }
        Arc::make_mut(&mut self.0).source_capture = source;
        Ok(self)
    }
    /// Original explicit root alpha/mask ordering, false for generic constructors.
    pub fn captures_source_visual_opacity(&self) -> bool {
        self.0.capture_source_visual_opacity
    }
    /// Original independent source descriptor, absent for the legacy route.
    pub fn source_capture(&self) -> Option<SourceCapture> {
        self.0.source_capture
    }
    /// Original raw scalar padding; never substituted for four source edges.
    pub fn padding(&self) -> f32 {
        self.0.padding
    }
    /// Borrowed immutable parameter generation, absent for target-time recipes.
    pub fn parameters(&self) -> Option<&ManagedShaderParameters> {
        match &self.0.parameters {
            Parameters::Borrowed(value) => Some(value),
            _ => None,
        }
    }
    /// Independently owned preparation source, if explicitly selected.
    pub fn owned_source(&self) -> Option<&OwnedShaderEffectSource> {
        match &self.0.parameters {
            Parameters::Owned(value) => Some(value),
            _ => None,
        }
    }
    /// Validate source policy before source dependency callbacks or capture work.
    pub fn validate(&self) -> Result<(), Error> {
        if let Some(capture) = self.source_capture() {
            capture.validate()?;
        }
        if let Some(parameters) = self.parameters() {
            parameters.validate()?;
        }
        Ok(())
    }
    pub(crate) fn preflight_device(&self, device: &wgpu::Device) -> Result<(), Error> {
        self.validate()?;
        if let Some(parameters) = self.parameters() {
            // Visit the complete original sampler inventory, including ignored
            // duplicates, before hidden/empty/zero-alpha paint shortcuts.
            let descriptor = parameters.descriptor();
            for image in descriptor
                .input
                .iter()
                .chain(descriptor.samplers.iter().filter_map(|v| v.image()))
            {
                if image.device() != device {
                    return Err(Error::ResourceDomain);
                }
                if image
                    .size()
                    .iter()
                    .any(|v| *v > device.limits().max_texture_dimension_2d)
                {
                    return Err(Error::Capacity);
                }
            }
        }
        Ok(())
    }
}
