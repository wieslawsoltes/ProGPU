//! Ordinary native text commands remain deferred until the actual target DPI.
use super::*;
impl Scene {
    pub fn draw_native_design_atlas(
        &mut self,
        draw: crate::text::native_atlas::NativeDesignAtlasDraw,
    ) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::NativeDesignAtlas(Arc::new(draw)));
        self.tree_text = true;
        Ok(())
    }
    pub(crate) fn native_atlas_leases(
        &self,
    ) -> Result<Vec<Arc<crate::text::native_atlas::NativeAtlasPlan>>, Error> {
        let count = self
            .commands
            .iter()
            .filter(|c| matches!(c, Command::NativeAtlasLease(_)))
            .count();
        let mut leases = Vec::new();
        leases
            .try_reserve_exact(count)
            .map_err(|_| Error::Capacity)?;
        for command in &self.commands {
            if let Command::NativeAtlasLease(plan) = command {
                leases.push(plan.clone());
            }
        }
        Ok(leases)
    }
}
