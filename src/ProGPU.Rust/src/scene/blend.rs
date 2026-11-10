//! Original independent retained destination blend stack. Coverage and storage
//! Clear remain separate state and operators, never rectangular substitutes.
use super::*;

impl Scene {
    /// Push original per-draw destination blending. Fixed-function modes retain
    /// their actual geometry and fragment coverage; no isolated layer is created.
    /// Destination-reading vector modes require their independent resolve path.
    pub fn push_blend(&mut self, blend: LayerBlend) -> Result<(), Error> {
        if !blend.is_fixed() {
            return Err(Error::UnsupportedExecution);
        }
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
        )
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn straight_draw_state(self) -> Result<wgpu::BlendState, Error> {
        let mut state = self.fixed()?;
        if matches!(self, Self::SrcOver | Self::Src | Self::Plus) {
            state.color.src_factor = wgpu::BlendFactor::SrcAlpha;
        }
        Ok(state)
    }
}

pub(super) fn admit_command(command: &Command, blend: LayerBlend) -> Result<(), Error> {
    if blend == LayerBlend::SrcOver {
        return Ok(());
    }
    match command {
        Command::Empty
        | Command::PushBlend(_)
        | Command::PopBlend
        | Command::PushMask(_)
        | Command::PushMaskDifference(_)
        | Command::PushClip(_)
        | Command::PushClipEdges(_)
        | Command::PopScope
        | Command::Clear(_)
        | Command::Analytic(_)
        | Command::VertexMesh(_)
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
        #[cfg(feature = "gpu")]
        Command::Glyph(_) | Command::HintedGlyphPaint(_) => Ok(()),
        // Other families own independent blend/filter contracts. Their remaining
        // scope transport rejects instead of silently using source-over.
        _ => Err(Error::UnsupportedExecution),
    }
}
