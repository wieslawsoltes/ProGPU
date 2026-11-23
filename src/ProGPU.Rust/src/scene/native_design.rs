//! Explicit native unhinted vector recording and actual-target CPU preparation.
use super::*;
use crate::text::native_design_geometry::{
    NativeDesignGeometryError, NativeDesignVectorDraw, NativeDesignVectorPainter,
    NativeDesignVectorPlan,
};
pub(super) fn native_design_error(error: NativeDesignGeometryError) -> Error {
    match error {
        NativeDesignGeometryError::Drawing(e) => e,
        NativeDesignGeometryError::Font(crate::font::FontError::Capacity) => Error::Capacity,
        NativeDesignGeometryError::Font(crate::font::FontError::UnsupportedOutline) => {
            Error::UnsupportedExecution
        }
        _ => Error::InvalidInput,
    }
}
impl Scene {
    /// Record an actual native paragraph/fragment/vertical source for explicit
    /// monochrome vector-glyph painting. Original positions are never reshaped;
    /// path phase/gamma resolve at this Scene's actual target before compilation.
    /// All-no-ink selections retain the same source ownership as painted ones.
    pub fn draw_native_design_vector(&mut self, draw: NativeDesignVectorDraw) -> Result<(), Error> {
        draw.validate().map_err(native_design_error)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::NativeDesignVector(Arc::new(draw)));
        Ok(())
    }
    pub(super) fn expand_native_design(&self, dpi: f32) -> Result<Option<Self>, Error> {
        if !self
            .commands
            .iter()
            .any(|c| matches!(c, Command::NativeDesignVector(_)))
        {
            return Ok(None);
        }
        let mut candidate = self.clone();
        candidate.commands.clear();
        candidate
            .commands
            .try_reserve(self.commands.len())
            .map_err(|_| Error::Capacity)?;
        let mut painter = NativeDesignVectorPainter::default();
        for command in &self.commands {
            let Command::NativeDesignVector(draw) = command else {
                candidate.commands.push(command.clone());
                continue;
            };
            let mut context = draw.context();
            context.dpi_scale = dpi;
            let placed = draw.with_context(context).map_err(native_design_error)?;
            let plan = painter.prepare(&placed).map_err(native_design_error)?;
            candidate
                .commands
                .try_reserve(plan.paths().len() + 1)
                .map_err(|_| Error::Capacity)?;
            candidate
                .commands
                .push(Command::NativeDesignLease(plan.clone()));
            for p in plan.paths() {
                let m = p.transform.0;
                let transform = Affine([m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1]]);
                let color = match p.brush.kind {
                    BrushKind::Solid(color) => color,
                    _ => [1.0; 4],
                };
                let sampling = PathSampling::new(if p.policy.aliased { 1 } else { 8 })?;
                candidate.commands.push(Command::TextPath(
                    PathDraw {
                        path: p.path.clone(),
                        transform,
                        color,
                        sampling,
                    },
                    MaterialPaint {
                        brush: p.brush.clone(),
                        semantic_opacity: p.semantic_opacity,
                    },
                    p.policy,
                ));
            }
        }
        Ok(Some(candidate))
    }
    pub(super) fn native_design_leases(&self) -> Result<Vec<Arc<NativeDesignVectorPlan>>, Error> {
        let count = self
            .commands
            .iter()
            .filter(|c| matches!(c, Command::NativeDesignLease(_)))
            .count();
        let mut result = Vec::new();
        result
            .try_reserve_exact(count)
            .map_err(|_| Error::Capacity)?;
        for command in &self.commands {
            if let Command::NativeDesignLease(owner) = command {
                result.push(owner.clone());
            }
        }
        Ok(result)
    }
}
