//! Immutable isolated scene ownership and original bounded layer admission.
use super::*;
use crate::effects::EffectChain;

/// Original native destination compositing modes. These are independent of
/// image patch color blends. Destination-reading modes require a separate pass.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum LayerBlend {
    /// Premultiplied source over destination.
    #[default]
    SrcOver = 0,
    /// Replace destination within the layer quad.
    Src = 1,
    /// Keep destination.
    Dst = 2,
    /// Source inside destination alpha.
    SrcIn = 3,
    /// Destination inside source alpha.
    DstIn = 4,
    /// Source outside destination alpha.
    SrcOut = 5,
    /// Destination outside source alpha.
    DstOut = 6,
    /// Source atop destination.
    SrcAtop = 7,
    /// Destination atop source.
    DstAtop = 8,
    /// Exclusive source/destination coverage.
    Xor = 9,
    /// Destination over source.
    DstOver = 10,
    /// Multiply colors with source-over alpha.
    Multiply = 11,
    /// Screen colors.
    Screen = 12,
    /// Darken colors.
    Darken = 13,
    /// Lighten colors.
    Lighten = 14,
    /// Exclusion colors.
    Exclusion = 15,
    /// Add source and destination.
    Plus = 16,
    /// Clear within the composite quad; distinct from target-storage Clear.
    Clear = 17,
    /// Overlay colors.
    Overlay = 18,
    /// Color dodge.
    ColorDodge = 19,
    /// Color burn.
    ColorBurn = 20,
    /// Hard light.
    HardLight = 21,
    /// Soft light.
    SoftLight = 22,
    /// Difference colors.
    Difference = 23,
    /// Source hue.
    Hue = 24,
    /// Source saturation.
    Saturation = 25,
    /// Source color.
    Color = 26,
    /// Source luminosity.
    Luminosity = 27,
    /// Multiply both premultiplied color and alpha.
    Modulate = 28,
}
impl LayerBlend {
    /// Whether original fixed-function blending implements this exact mode.
    pub fn is_fixed(self) -> bool {
        matches!(
            self,
            Self::SrcOver
                | Self::Src
                | Self::Dst
                | Self::SrcIn
                | Self::DstIn
                | Self::SrcOut
                | Self::DstOut
                | Self::SrcAtop
                | Self::DstAtop
                | Self::Xor
                | Self::DstOver
                | Self::Plus
                | Self::Clear
                | Self::Modulate
        )
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn fixed(self) -> Result<wgpu::BlendState, Error> {
        use wgpu::BlendFactor as F;
        let (source, destination) = match self {
            Self::SrcOver => (F::One, F::OneMinusSrcAlpha),
            Self::Src => (F::One, F::Zero),
            Self::Dst => (F::Zero, F::One),
            Self::SrcIn => (F::DstAlpha, F::Zero),
            Self::DstIn => (F::Zero, F::SrcAlpha),
            Self::SrcOut => (F::OneMinusDstAlpha, F::Zero),
            Self::DstOut => (F::Zero, F::OneMinusSrcAlpha),
            Self::SrcAtop => (F::DstAlpha, F::OneMinusSrcAlpha),
            Self::DstAtop => (F::OneMinusDstAlpha, F::SrcAlpha),
            Self::Xor => (F::OneMinusDstAlpha, F::OneMinusSrcAlpha),
            Self::DstOver => (F::OneMinusDstAlpha, F::One),
            Self::Plus => (F::One, F::One),
            Self::Clear => (F::Zero, F::Zero),
            Self::Modulate => (F::Dst, F::Zero),
            _ => return Err(Error::UnsupportedExecution),
        };
        let color = wgpu::BlendComponent {
            src_factor: source,
            dst_factor: destination,
            operation: wgpu::BlendOperation::Add,
        };
        let alpha = wgpu::BlendComponent {
            src_factor: if self == Self::Modulate {
                F::DstAlpha
            } else {
                source
            },
            ..color
        };
        Ok(wgpu::BlendState { color, alpha })
    }
}

/// An owned isolated subtree in the root logical coordinate system. Its source
/// paints into transparent storage before effects and final opacity/blending.
/// Outer mask/clip scopes affect the composite once, independently of inner scopes.
#[derive(Clone, Debug)]
pub struct LayerDraw {
    /// Immutable balanced recording; cloning retains the actual source owners.
    pub scene: Arc<Scene>,
    /// Optional declared logical x/y/width/height. None uses the parent target.
    /// Bounds include caller-owned effect padding; no ink envelope is inferred.
    pub bounds: Option<[f32; 4]>,
    /// Final group opacity, finite 0..1, applied after source/effects.
    pub opacity: f32,
    /// Original destination blend, independent of image patch color blending.
    pub blend: LayerBlend,
    /// Original immutable Gaussian/box/drop-shadow chain.
    pub effects: Option<EffectChain>,
    /// Select linear filtering for fixed blends; false uses nearest. Advanced
    /// blends retain the original native linear source-resolve binding.
    pub linear_sampling: bool,
    // A public bounds edit invalidates this private source-placement witness.
    placed_bounds: Option<([u32; 4], [f32; 4])>,
}
impl LayerDraw {
    /// An isolated source-over group with unit opacity and parent-sized bounds.
    /// Accepts either an owned scene or an existing shared generation. Browser
    /// device handles retain their thread affinity; shared ownership does not
    /// promise cross-thread GPU access.
    pub fn new(scene: impl Into<Arc<Scene>>) -> Self {
        Self {
            scene: scene.into(),
            bounds: None,
            opacity: 1.0,
            blend: LayerBlend::SrcOver,
            effects: None,
            linear_sampling: true,
            placed_bounds: None,
        }
    }
    /// Original independent source edges when this is a placed capture command.
    pub(crate) fn bounds_edges(&self) -> Option<[f32; 4]> {
        self.bounds.map(|bounds| {
            if let Some((witness, edges)) = self.placed_bounds
                && witness == bounds.map(f32::to_bits)
            {
                return edges;
            }
            let [x, y, w, h] = bounds;
            [x, y, x + w, y + h]
        })
    }
    pub(super) fn place_bounds(&mut self, edges: Option<[f32; 4]>) {
        self.bounds = edges.map(|e| [e[0], e[1], e[2] - e[0], e[3] - e[1]]);
        self.placed_bounds = self
            .bounds
            .zip(edges)
            .map(|(b, e)| (b.map(f32::to_bits), e));
    }
    pub(crate) fn validate(&self) -> Result<(), Error> {
        if self.scene.scope_depth != 0
            || !self.opacity.is_finite()
            || !(0.0..=1.0).contains(&self.opacity)
        {
            return Err(Error::InvalidInput);
        }
        if let Some([x, y, w, h]) = self.bounds
            && (![x, y, w, h, x + w, y + h].iter().all(|v| v.is_finite()) || w < 0.0 || h < 0.0)
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
}
impl Scene {
    /// Record an owned isolated subtree in painter order. O(1), with no capture,
    /// geometry work or font callbacks. The original 16-target depth is bounded
    /// before mutation, including detached retained children.
    pub fn draw_layer(&mut self, draw: LayerDraw) -> Result<(), Error> {
        draw.validate()?;
        let depth = draw
            .scene
            .layer_depth
            .checked_add(1)
            .ok_or(Error::Capacity)?;
        if depth > 16 {
            return Err(Error::Capacity);
        }
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.has_axis |= draw.scene.has_axis;
        self.tree_text |= draw.scene.tree_text;
        self.commands.push(Command::Layer(Box::new(draw)));
        self.layer_depth = self.layer_depth.max(depth);
        Ok(())
    }
    /// Maximum retained isolated-target depth, excluding the root target.
    pub fn layer_depth(&self) -> u8 {
        self.layer_depth
    }

    /// Isolated children in original painter order.
    pub fn layers(&self) -> impl Iterator<Item = &LayerDraw> {
        self.commands.iter().filter_map(|command| match command {
            Command::Layer(layer) => Some(layer.as_ref()),
            _ => None,
        })
    }
}
