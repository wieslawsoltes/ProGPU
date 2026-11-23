//! Retained full image-filter graphs with independently placed output geometry.
use super::*;
use crate::image_filter::ImageFilteredImageDraw;
impl Scene {
    /// Retain a full original filter graph and explicit source/canvas recipe.
    /// Output placement never silently changes the filter's independent frame.
    pub fn draw_image_filtered_image(&mut self, draw: ImageFilteredImageDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::ImageFilteredImage(Box::new(draw)));
        Ok(())
    }
    pub(crate) fn image_filtered_images(&self) -> impl Iterator<Item = &ImageFilteredImageDraw> {
        self.commands.iter().filter_map(|command| match command {
            Command::ImageFilteredImage(draw) => Some(draw.as_ref()),
            _ => None,
        })
    }
}
