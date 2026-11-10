//! Source static path-clip admission before the common immutable atlas pass.
use super::*;
use crate::{picture_archive::ArchiveCommandKind, vector::Matrix4};
impl Scene {
    #[cfg(feature = "gpu")]
    pub(crate) fn static_dxf_text_flags(flags: f32) -> f32 {
        text::late_camera::force_use_mvp(flags)
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn append_static_dxf_glyph(&mut self, instance: GlyphInstance) -> Result<(), Error> {
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::Glyph(instance));
        Ok(())
    }
    pub(crate) fn resolve_static_dxf_chunk(
        &self,
        dpi: f32,
        offscreen: bool,
        clip: Option<[f32; 4]>,
        gpu_active: bool,
    ) -> Result<(Self, bool), Error> {
        if self.has_scopes || self.scope_depth != 0 || self.inherited_clip.is_some() {
            return Err(Error::UnsupportedExecution);
        }
        let mut resolved = Self::default();
        for command in &self.commands {
            if let Command::ArchiveGeometry(draw) = command {
                let (mut child, final_target) = draw.resolve_with_camera(
                    dpi,
                    offscreen,
                    gpu_active.then_some(Matrix4::IDENTITY),
                )?;
                if !gpu_active
                    && draw.command().kind != ArchiveCommandKind::DrawSpline
                    && draw.command().brush.is_some()
                    && let Some([left, top, width, height]) = clip
                {
                    if let Some(fill) = child
                        .commands
                        .iter()
                        .find(|c| matches!(c, Command::TextPath(..)))
                    {
                        let Command::TextPath(path, _, policy) = fill else {
                            return Err(Error::ResourceDomain);
                        };
                        let placement = text::placement(path, *policy)?;
                        let vertices = text::vertices(&placement, path, *policy, [0; 2], dpi);
                        let p = vertices.map(|v| v.position);
                        let min_x = p[0][0].min(p[1][0]).min(p[2][0].min(p[3][0]));
                        let max_x = p[0][0].max(p[1][0]).max(p[2][0].max(p[3][0]));
                        let min_y = p[0][1].min(p[1][1]).min(p[2][1].min(p[3][1]));
                        let max_y = p[0][1].max(p[1][1]).max(p[2][1].max(p[3][1]));
                        if max_x <= left
                            || min_x >= left + width
                            || max_y <= top
                            || min_y >= top + height
                        {
                            // CompilePathCommand requests the atlas/registers
                            // its fill brush before this early return. Preserve
                            // that work, but omit the subsequent source stroke.
                            let fill = fill.clone();
                            child.commands.clear();
                            child
                                .commands
                                .push(Command::CompileOnly(Box::new(fill), draw.clone()));
                        }
                    }
                }
                child.final_target_paths |= final_target;
                resolved.append(&child)?;
                resolved.final_target_paths |= final_target;
            } else {
                resolved
                    .commands
                    .try_reserve(1)
                    .map_err(|_| Error::Capacity)?;
                resolved.commands.push(command.clone());
            }
        }
        if clip.is_some() && !gpu_active {
            for command in &resolved.commands {
                if let Command::Stroke(draw) | Command::PlacedStroke(draw, _) = command
                    && matches!(draw.prepare()?, Some(PreparedStroke::Fill(_)))
                {
                    // This rare outline fallback has its own original path
                    // culling/registration order; never substitute a scissor.
                    return Err(Error::UnsupportedExecution);
                }
            }
        }
        // Mesh and Hatch do not consult the active raster camera. Their source
        // static vertices still receive200, without an invented helper100 flag.
        let mark = gpu_active
            && resolved.commands.iter().all(|c| {
                #[cfg(feature = "gpu")]
                if matches!(c, Command::Glyph(_)) {
                    return true;
                }
                matches!(
                    c,
                    Command::Empty
                        | Command::ArchiveVector(_)
                        | Command::Stroke(_)
                        | Command::PlacedStroke(..)
                        | Command::TextPath(..)
                        | Command::MaterialPath(..)
                        | Command::Path(_)
                )
            });
        if mark {
            let mut result = Self::default();
            let final_target = resolved.final_target_paths;
            result.append_archive_camera(resolved, Matrix4::IDENTITY)?;
            result.final_target_paths = final_target;
            Ok((result, true))
        } else {
            Ok((resolved, false))
        }
    }
}
