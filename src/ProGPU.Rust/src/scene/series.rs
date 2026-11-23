//! Painter-ordered original GPU series; source ownership stays immutable.
use super::*;
impl Scene {
    pub fn draw_series(&mut self, draw: crate::series::SeriesDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::Series(Arc::new(draw)));
        Ok(())
    }
}
