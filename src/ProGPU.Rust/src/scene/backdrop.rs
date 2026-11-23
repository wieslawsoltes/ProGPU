//! Original ordered backdrop extension commands, without eager target capture.
use super::*;
use crate::backdrop::BackdropMaterialDraw;
impl Scene {
    /// Retain actual source metadata. Host backdrop is captured only at this
    /// command's painter position while the actual target is being encoded.
    pub fn draw_backdrop_material(&mut self, draw: BackdropMaterialDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::BackdropMaterial(Box::new(draw)));
        Ok(())
    }
    pub(crate) fn backdrop_materials(
        &self,
    ) -> impl Iterator<Item = (usize, &BackdropMaterialDraw)> {
        self.commands
            .iter()
            .enumerate()
            .filter_map(|(i, c)| match c {
                Command::BackdropMaterial(draw) => Some((i, draw.as_ref())),
                _ => None,
            })
    }
}
