//! Original DrawingContext material factories, distinct from raw command import.
use super::*;
use crate::{
    picture_archive::{ArchiveBrush, ArchiveCommandKind as K, ArchivePen},
    vector::Matrix4,
};
use std::sync::Arc;
impl ArchiveRecorder {
    /// Original rectangle factory: backdrop fill is an extension preceding any
    /// actual pen. Ordinary fills retain one analytic command.
    pub fn draw_rectangle(
        &mut self,
        brush: Option<Arc<ArchiveBrush>>,
        pen: Option<Arc<ArchivePen>>,
        rect: [f32; 4],
        transform: Matrix4,
    ) -> Result<()> {
        self.material_rectangle(brush, pen, rect, [0.0; 2], transform, false)
    }
    /// Original independent horizontal/vertical rounded rectangle radii.
    pub fn draw_rounded_rectangle(
        &mut self,
        brush: Option<Arc<ArchiveBrush>>,
        pen: Option<Arc<ArchivePen>>,
        rect: [f32; 4],
        radii: [f32; 2],
        transform: Matrix4,
    ) -> Result<()> {
        self.material_rectangle(brush, pen, rect, radii, transform, true)
    }
    fn material_rectangle(
        &mut self,
        mut brush: Option<Arc<ArchiveBrush>>,
        pen: Option<Arc<ArchivePen>>,
        rect: [f32; 4],
        radii: [f32; 2],
        transform: Matrix4,
        rounded: bool,
    ) -> Result<()> {
        let mut extension: Option<ArchiveCommand> = None;
        if let Some(ArchiveBrush::BackdropMaterial(material)) = brush.as_deref() {
            #[cfg(not(feature = "gpu"))]
            {
                let _ = material;
                return Err(ArchiveError::Unsupported);
            }
            #[cfg(feature = "gpu")]
            {
                extension = Some(ArchiveCommand {
                    kind: K::DrawExtension,
                    extension_id: 13,
                    transform,
                    backdrop_material: Some(Arc::new(
                        crate::backdrop::BackdropMaterialParameters::from_brush(
                            material,
                            rect,
                            [radii[0]; 4],
                            [radii[1]; 4],
                        ),
                    )),
                    ..Default::default()
                });
                brush = None;
            }
        }
        let count =
            usize::from(extension.is_some()) + usize::from(extension.is_none() || pen.is_some());
        if self
            .commands
            .len()
            .checked_add(count)
            .is_none_or(|n| n > MAX_COMMANDS)
        {
            return Err(ArchiveError::Capacity);
        }
        self.commands
            .try_reserve(count)
            .map_err(|_| ArchiveError::Capacity)?;
        let intercepted = extension.is_some();
        if let Some(extension) = extension {
            self.commands.push(extension);
        }
        if !intercepted || pen.is_some() {
            let is_pen_thickness_local = pen.is_some();
            self.commands.push(ArchiveCommand {
                kind: if rounded {
                    K::DrawRoundedRect
                } else {
                    K::DrawRect
                },
                brush,
                pen,
                rect,
                radius_x: radii[0],
                radius_y: radii[1],
                transform,
                is_pen_thickness_local,
                ..Default::default()
            });
        }
        Ok(())
    }
    /// Typed original DataParam owner; texture and host sources remain distinct.
    /// The versioned archive wire rejects this source-only payload on serialize.
    #[cfg(feature = "gpu")]
    pub fn draw_backdrop_material(
        &mut self,
        parameters: crate::backdrop::BackdropMaterialParameters,
        transform: Matrix4,
    ) -> Result<()> {
        self.record(ArchiveCommand {
            kind: K::DrawExtension,
            extension_id: 13,
            backdrop_material: Some(Arc::new(parameters)),
            transform,
            ..Default::default()
        })
    }
}
