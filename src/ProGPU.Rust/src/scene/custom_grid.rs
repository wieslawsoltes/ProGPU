//! Original extension7 fullscreen NDC quad; source placement is not applied.
use super::*;
use crate::picture_archive::{ArchiveCommand, ArchiveCommandKind};
impl Scene {
    pub fn draw_custom_grid(&mut self, command: Arc<ArchiveCommand>) -> Result<(), Error> {
        validate(&command)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::CustomGrid(command));
        Ok(())
    }
}
pub(super) fn validate(command: &ArchiveCommand) -> Result<(), Error> {
    if command.kind != ArchiveCommandKind::DrawExtension || command.extension_id != 7 {
        return Err(Error::InvalidInput);
    }
    // The shader ignores shape markers, but original late-camera selection also
    // changes other draws in the shared target. Do not lose that source history.
    if command.use_gpu_transforms {
        return Err(Error::UnsupportedExecution);
    }
    Ok(())
}
pub(super) fn append(mesh: &mut Mesh) -> Result<(), Error> {
    let positions = [[-1.0, -1.0], [1.0, -1.0], [1.0, 1.0], [-1.0, 1.0]];
    let uv = [[0.0, 0.0], [1.0, 0.0], [1.0, 1.0], [0.0, 1.0]];
    mesh.push_quad(std::array::from_fn(|i| crate::geometry::VectorVertex {
        position: positions[i],
        color: [1.0; 4],
        texture_coordinate: uv[i],
        brush_index: 0.0,
        shape_size: [0.0; 2],
        corner_radius: 0.0,
        stroke_thickness: 0.0,
        shape_type: 7.0,
    }))
}
