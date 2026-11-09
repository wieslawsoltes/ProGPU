//! Immutable retained matrix and color-filter graph commands.
use super::*;
use crate::image::{ColorFilteredImageDraw, ImageColorDraw};

impl Scene {
    /// Record the original Texture matrix-then-luminance operation, retaining
    /// source geometry and exact matrix rows without creating GPU resources.
    pub fn draw_image_color(&mut self, draw: ImageColorDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::ImageColor(Box::new(draw)));
        Ok(())
    }
    /// Retain an immutable filter graph. Preparation resolves full-size source
    /// captures before final geometry, sampling, opacity and coverage.
    pub fn draw_color_filtered_image(&mut self, draw: ColorFilteredImageDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::ColorFilteredImage(Box::new(draw)));
        Ok(())
    }
    pub(crate) fn color_filtered_images(&self) -> impl Iterator<Item = &ColorFilteredImageDraw> {
        self.commands.iter().filter_map(|command| match command {
            Command::ColorFilteredImage(draw) => Some(draw.as_ref()),
            _ => None,
        })
    }
}
