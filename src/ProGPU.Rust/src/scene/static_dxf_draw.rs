//! Actual immutable StaticDxf extension0 producer, separate from archive bytes.
use super::*;
use crate::{
    picture_archive::{StaticDxfBuffer, StaticHatchViewport},
    vector::Matrix4,
};

/// The two original source routes keep their independent cache-source policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum StaticDxfRoute {
    /// Original DrawExtension(id0, DataParam). No source object is invented from
    /// portable archive fields; the caller supplies the actual owned buffer.
    #[default]
    Extension,
    /// Original direct DrawStaticDxf branch, including its explicit refusal to
    /// override baked text policy during ClearType-suppressing source captures.
    Legacy,
}

/// Ordered static source lease and independent final consumer placement.
/// Static extension rendering does not consume the outer semantic opacity.
#[derive(Clone, Debug)]
pub struct StaticDxfDraw {
    pub buffer: Option<StaticDxfBuffer>,
    pub transform: Matrix4,
    pub viewport: Option<StaticHatchViewport>,
    pub route: StaticDxfRoute,
}
impl StaticDxfDraw {
    pub fn new(buffer: StaticDxfBuffer) -> Self {
        Self {
            buffer: Some(buffer),
            transform: Matrix4::IDENTITY,
            viewport: None,
            route: StaticDxfRoute::Extension,
        }
    }
    /// Explicit original null payload. Policy checks precede this no-op.
    pub fn absent(route: StaticDxfRoute) -> Self {
        Self {
            buffer: None,
            transform: Matrix4::IDENTITY,
            viewport: None,
            route,
        }
    }
    pub fn validate(&self) -> Result<(), Error> {
        if self.transform.0.iter().flatten().any(|v| !v.is_finite())
            || self.viewport.is_some_and(|v| {
                (v.matrix() * self.transform)
                    .0
                    .iter()
                    .flatten()
                    .any(|n| !n.is_finite())
            })
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    pub(crate) fn placed(
        &self,
        placement: Matrix4,
        suppress_cached_cleartype: bool,
    ) -> Result<Self, Error> {
        self.validate_source_policy(suppress_cached_cleartype)?;
        let mut result = self.clone();
        result.transform = self.transform * placement;
        result.validate()?;
        Ok(result)
    }
    pub(crate) fn validate_source_policy(
        &self,
        suppress_cached_cleartype: bool,
    ) -> Result<(), Error> {
        self.validate()?;
        if suppress_cached_cleartype && self.route == StaticDxfRoute::Legacy {
            return Err(Error::UnsupportedExecution);
        }
        Ok(())
    }
}
impl Scene {
    pub fn draw_static_dxf(&mut self, draw: StaticDxfDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::StaticDxf(Arc::new(draw)));
        Ok(())
    }
    pub(crate) fn static_dxf_draws(&self) -> impl Iterator<Item = (usize, &Arc<StaticDxfDraw>)> {
        self.commands
            .iter()
            .enumerate()
            .filter_map(|(index, command)| {
                if let Command::StaticDxf(draw) = command {
                    Some((index, draw))
                } else {
                    None
                }
            })
    }
}
