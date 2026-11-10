//! Original independent retained destination blend stack. Coverage and storage
//! Clear remain separate state and operators, never rectangular substitutes.
use super::*;

impl Scene {
    /// Push the original family-specific compositor blend policy. Images and
    /// spatial hinted paint resolve destination-reading modes; ordinary vectors
    /// and text use `LayerBlend::managed_vector_effective_mode` and original
    /// fixed factors. Semantic isolated layers keep their separate exact resolve.
    pub fn push_blend(&mut self, blend: LayerBlend) -> Result<(), Error> {
        let depth = self.scope_depth.checked_add(1).ok_or(Error::Capacity)?;
        let blend_depth = self.blend_depth.checked_add(1).ok_or(Error::Capacity)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::PushBlend(blend));
        self.scope_depth = depth;
        self.blend_depth = blend_depth;
        self.has_scopes = true;
        Ok(())
    }
    /// Restore only the original blend stack, independently of coverage scopes.
    /// An unmatched pop rejects before recorder mutation.
    pub fn pop_blend(&mut self) -> Result<(), Error> {
        if self.blend_depth == 0 || self.scope_depth == 0 {
            return Err(Error::InvalidInput);
        }
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::PopBlend);
        self.scope_depth -= 1;
        self.blend_depth -= 1;
        Ok(())
    }
}

impl LayerBlend {
    /// Original compositor source-output selection for fixed per-draw modes.
    pub(crate) fn requires_premultiplied(self) -> bool {
        matches!(
            self,
            Self::SrcIn
                | Self::SrcOut
                | Self::SrcAtop
                | Self::DstAtop
                | Self::Xor
                | Self::DstOver
                | Self::Modulate
                | Self::Multiply
                | Self::Screen
                | Self::Darken
                | Self::Lighten
                | Self::Exclusion
        )
    }
    /// Original managed Vector/Text selection. The five extra fixed-factor
    /// modes retain their original formulas, including their distinct alpha
    /// equations. Remaining advanced modes fall back to SrcOver, as in the
    /// original RenderPipelineCache; this does not claim exact layer blending.
    pub fn managed_vector_effective_mode(self) -> Self {
        match self {
            Self::Overlay
            | Self::ColorDodge
            | Self::ColorBurn
            | Self::HardLight
            | Self::SoftLight
            | Self::Difference
            | Self::Hue
            | Self::Saturation
            | Self::Color
            | Self::Luminosity => Self::SrcOver,
            _ => self,
        }
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn straight_draw_state(self) -> Result<wgpu::BlendState, Error> {
        self.managed_draw_state(false)
    }
    /// Original shared RenderPipelineCache factors. Each family owns the
    /// independent decision whether its shader emits premultiplied source.
    #[cfg(feature = "gpu")]
    pub(crate) fn managed_draw_state(self, premultiplied: bool) -> Result<wgpu::BlendState, Error> {
        use wgpu::{BlendComponent as C, BlendFactor as F, BlendOperation as O};
        let component = |src_factor, dst_factor, operation| C {
            src_factor,
            dst_factor,
            operation,
        };
        let over_alpha = component(F::One, F::OneMinusSrcAlpha, O::Add);
        let extra = match self {
            Self::Multiply => Some(wgpu::BlendState {
                color: component(F::Dst, F::OneMinusSrcAlpha, O::Add),
                alpha: over_alpha,
            }),
            Self::Screen => Some(wgpu::BlendState {
                color: component(F::One, F::OneMinusSrc, O::Add),
                alpha: component(F::One, F::OneMinusSrc, O::Add),
            }),
            Self::Darken | Self::Lighten => Some(wgpu::BlendState {
                color: component(
                    F::One,
                    F::One,
                    if self == Self::Darken { O::Min } else { O::Max },
                ),
                alpha: component(F::One, F::One, O::Max),
            }),
            Self::Exclusion => Some(wgpu::BlendState {
                color: component(F::OneMinusDst, F::OneMinusSrc, O::Add),
                alpha: over_alpha,
            }),
            _ => None,
        };
        if let Some(state) = extra {
            return Ok(state);
        }
        let selected = self.managed_vector_effective_mode();
        let mut state = selected.fixed()?;
        if !premultiplied && matches!(selected, Self::SrcOver | Self::Src | Self::Plus) {
            state.color.src_factor = F::SrcAlpha;
        }
        Ok(state)
    }
}

pub(super) fn admit_command(command: &Command, blend: LayerBlend) -> Result<(), Error> {
    let command = super::late_camera::split(command).0;
    if blend == LayerBlend::SrcOver {
        return Ok(());
    }
    match command {
        Command::Empty
        | Command::NativeDesignLease(_)
        | Command::NativeAtlasLease(_)
        | Command::NativeDesignAtlas(_)
        | Command::NativeDesignVector(_)
        | Command::CanvasTextPath(_)
        | Command::PushBlend(_)
        | Command::PopBlend
        | Command::PushMask(_)
        | Command::PushMaskDifference(_)
        | Command::PushClip(_)
        | Command::PushClipEdges(_)
        | Command::PopScope
        | Command::PopCoverage { .. }
        | Command::Clear(_)
        | Command::Analytic(_)
        | Command::VertexMesh(_)
        | Command::ArchiveGeometry(_)
        | Command::CompileOnly(..)
        | Command::LateCamera(..)
        | Command::ArchiveVector(_)
        | Command::CustomGrid(_)
        | Command::Series(_)
        | Command::ArchiveHatch(_)
        | Command::ArchiveThreeD(_)
        | Command::MaterialAnalytic(..)
        | Command::Path(_)
        | Command::MaterialPath(..)
        | Command::Stroke(_)
        | Command::PlacedStroke(..)
        | Command::TextPath(..)
        | Command::TileImage(_)
        | Command::PlacedTileImage(..) => Ok(()),
        Command::Image(_, source_blend, _, operation)
            if *source_blend == LayerBlend::SrcOver && operation.is_none() =>
        {
            Ok(())
        }
        Command::ImageEffect(draw)
            if draw.policy == crate::image_effect::ImageEffectDrawPolicy::ManagedExtension =>
        {
            Ok(())
        }
        #[cfg(feature = "gpu")]
        Command::BackdropMaterial(draw)
            if blend.is_fixed() && draw.blend == LayerBlend::SrcOver =>
        {
            Ok(())
        }
        #[cfg(feature = "gpu")]
        Command::Glyph(_)
        | Command::HintedGlyphPaint(_)
        | Command::Mesh3D(_)
        | Command::Mesh3DCamera(_) => Ok(()),
        // Other families own independent blend/filter contracts. Their remaining
        // scope transport rejects instead of silently using source-over.
        _ => Err(Error::UnsupportedExecution),
    }
}
