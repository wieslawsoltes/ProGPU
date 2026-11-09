//! Fixed-size coverage snapshots and exact binary pixel-center clip boundaries.
use super::*;
use crate::mask::{CoverageMask, Mask, MaskUniforms};
use bytemuck::Zeroable;
use wide::f32x4;

#[derive(Clone, Debug)]
pub(crate) struct CompiledDrawState {
    pub masks: [MaskUniforms; 4],
    pub count: usize,
    pub coverage: Option<CoverageMask>,
    /// Physical left/top/right/bottom integer half-open boundaries.
    pub clip: Option<[u32; 4]>,
}
impl Default for CompiledDrawState {
    fn default() -> Self {
        Self {
            masks: [MaskUniforms::zeroed(); 4],
            count: 0,
            coverage: None,
            clip: None,
        }
    }
}
fn validate_clip(bounds: [f32; 4]) -> Result<(), Error> {
    let [x, y, w, h] = bounds;
    if [x, y, w, h, x + w, y + h].iter().any(|v| !v.is_finite()) || w < 0.0 || h < 0.0 {
        return Err(Error::InvalidInput);
    }
    Ok(())
}
impl Scene {
    /// Push per-draw coverage without an offscreen group. Up to four analytic
    /// masks, or one primary R8 mask followed by three analytic masks, can be
    /// compiled directly. Other compositions require the separate layer path.
    /// Recording is O(1), retaining the actual immutable pixel owner.
    pub fn push_mask(&mut self, mask: impl Into<Mask>) -> Result<(), Error> {
        let mask = mask.into();
        mask.validate()?;
        let depth = self.scope_depth.checked_add(1).ok_or(Error::Capacity)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::PushMask(Box::new(mask)));
        self.scope_depth = depth;
        self.has_scopes = true;
        Ok(())
    }
    /// Push a target-axis logical rectangle with exact binary pixel-center
    /// admission. No source transform is inferred. Empty rectangles suppress
    /// covered draws; the initial target clear remains outside these scopes.
    pub fn push_clip_rect(&mut self, bounds: [f32; 4]) -> Result<(), Error> {
        validate_clip(bounds)?;
        let depth = self.scope_depth.checked_add(1).ok_or(Error::Capacity)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::PushClip(bounds));
        self.scope_depth = depth;
        self.has_scopes = true;
        Ok(())
    }
    /// Restore the preceding mask or clip state. Underflow leaves recording
    /// unchanged; all scopes must be closed before compilation/preparation.
    pub fn pop_scope(&mut self) -> Result<(), Error> {
        if self.scope_depth == 0 {
            return Err(Error::InvalidInput);
        }
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::PopScope);
        self.scope_depth -= 1;
        Ok(())
    }

    pub(crate) fn compile_states(
        &self,
        dpi: f32,
    ) -> Result<(Vec<CompiledDrawState>, Vec<Option<usize>>), Error> {
        if !dpi.is_finite() || dpi <= 0.0 || self.scope_depth != 0 {
            return Err(Error::InvalidInput);
        }
        let mut states = vec![CompiledDrawState::default()];
        if !self.has_scopes {
            return Ok((states, Vec::new()));
        }
        let mut changes = Vec::new();
        changes
            .try_reserve_exact(self.commands.len())
            .map_err(|_| Error::Capacity)?;
        let mut stack = Vec::new();
        let mut current = 0;
        for command in &self.commands {
            if matches!(command, Command::PopScope) {
                current = stack.pop().ok_or(Error::InvalidInput)?;
                changes.push(Some(current));
                continue;
            }
            let mut candidate = match command {
                Command::PushMask(_) | Command::PushClip(_) => states[current].clone(),
                _ => {
                    changes.push(None);
                    continue;
                }
            };
            match command {
                Command::PushMask(mask) => {
                    if candidate.count == 4
                        || (matches!(mask.as_ref(), Mask::Coverage(_)) && candidate.count != 0)
                    {
                        return Err(Error::UnsupportedExecution);
                    }
                    candidate.masks[candidate.count] = mask.uniforms(dpi)?;
                    candidate.count += 1;
                    if let Mask::Coverage(source) = mask.as_ref() {
                        candidate.coverage = Some(source.clone());
                    }
                }
                Command::PushClip(bounds) => {
                    validate_clip(*bounds)?;
                    let [x, y, w, h] = *bounds;
                    let projected = (f32x4::from([x, y, x + w, y + h]) * f32x4::splat(dpi)
                        + f32x4::ZERO)
                        .to_array();
                    if projected.iter().any(|v| !v.is_finite()) {
                        return Err(Error::InvalidInput);
                    }
                    // Native aliased composite rule: each edge is ceil(edge-.5),
                    // subtracting in double so adjacent float values stay distinct.
                    let mut edges = projected.map(|v| {
                        (f64::from(v) - 0.5).ceil().clamp(0.0, f64::from(u32::MAX)) as u32
                    });
                    if let Some(previous) = candidate.clip {
                        edges = [
                            edges[0].max(previous[0]),
                            edges[1].max(previous[1]),
                            edges[2].min(previous[2]),
                            edges[3].min(previous[3]),
                        ];
                    }
                    candidate.clip = Some(edges);
                }
                _ => unreachable!(),
            }
            states.try_reserve(1).map_err(|_| Error::Capacity)?;
            stack.try_reserve(1).map_err(|_| Error::Capacity)?;
            stack.push(current);
            current = states.len();
            states.push(candidate);
            changes.push(Some(current));
        }
        if !stack.is_empty() {
            return Err(Error::InvalidInput);
        }
        Ok((states, changes))
    }
}
