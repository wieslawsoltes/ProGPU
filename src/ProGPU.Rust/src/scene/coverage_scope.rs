//! Original compositor axis clips and alpha masks restore independently.
use super::*;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum CoverageKind {
    Clip,
    Mask,
}
impl Scene {
    pub(crate) fn push_retained_clip_edges(&mut self, mut edges: [f32; 4]) -> Result<(), Error> {
        // Intersected empty clips remain empty, with their actual near edge.
        edges[2] = edges[2].max(edges[0]);
        edges[3] = edges[3].max(edges[1]);
        source_capture::validate_edges(edges)?;
        let depth = self.scope_depth.checked_add(1).ok_or(Error::Capacity)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::PushClipEdges(edges));
        self.scope_depth = depth;
        self.has_scopes = true;
        Ok(())
    }

    /// Only original archive compilation emits this operator; ordinary public
    /// Scene scopes retain their existing single-stack contract.
    pub(crate) fn pop_archive_coverage(
        &mut self,
        kind: CoverageKind,
        source_clip: bool,
    ) -> Result<(), Error> {
        if self.scope_depth == self.blend_depth {
            return Err(Error::InvalidInput);
        }
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::PopCoverage { kind, source_clip });
        self.scope_depth -= 1;
        Ok(())
    }
}
