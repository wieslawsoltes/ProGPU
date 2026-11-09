//! Fixed-size coverage snapshots and exact binary pixel-center clip boundaries.
use super::*;
use crate::mask::{Mask, MaskComponent, MaskOperation, MaskUniforms, TextureMask};
use bytemuck::Zeroable;
use wide::f32x4;

#[derive(Clone, Debug)]
pub(crate) struct CompiledDrawState {
    pub masks: [MaskUniforms; 4],
    pub count: usize,
    pub coverage: Option<TextureMask>,
    pub components: Arc<[MaskComponent]>,
    pub materialized: bool,
    /// Physical left/top/right/bottom integer half-open boundaries.
    pub clip: Option<[u32; 4]>,
}
impl Default for CompiledDrawState {
    fn default() -> Self {
        Self {
            masks: [MaskUniforms::zeroed(); 4],
            count: 0,
            coverage: None,
            components: Arc::from([]),
            materialized: false,
            clip: None,
        }
    }
}
impl CompiledDrawState {
    #[cfg(feature = "gpu")]
    pub(crate) fn direct(mask: Mask, dpi: f32) -> Result<Self, Error> {
        let mut result = Self::default();
        result.append_mask(mask, MaskOperation::Intersect, dpi)?;
        Ok(result)
    }
    fn append_mask(&mut self, mask: Mask, operation: MaskOperation, dpi: f32) -> Result<(), Error> {
        if self.components.len() == 64 {
            return Err(Error::Capacity);
        }
        let mut components = Vec::new();
        components
            .try_reserve_exact(self.components.len() + 1)
            .map_err(|_| Error::Capacity)?;
        components.extend_from_slice(&self.components);
        components.push(MaskComponent { mask, operation });
        let direct = components.len() <= 4
            && components.iter().enumerate().all(|(i, c)| {
                c.operation == MaskOperation::Intersect
                    && (i == 0 || matches!(c.mask, Mask::Analytic(_)))
            });
        self.masks = [MaskUniforms::zeroed(); 4];
        self.coverage = None;
        self.materialized = !direct;
        self.count = if direct { components.len() } else { 1 };
        // Validate every original record even when it will be materialized later.
        for (i, c) in components.iter().enumerate() {
            let uniforms = c.mask.uniforms(dpi)?;
            if direct {
                self.masks[i] = uniforms;
            }
        }
        if direct {
            self.coverage = match &components[0].mask {
                Mask::Coverage(mask) => Some(mask.clone().into()),
                Mask::Texture(mask) => Some(mask.clone()),
                _ => None,
            };
        }
        self.components = components.into();
        Ok(())
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
    #[cfg(feature = "gpu")]
    pub(crate) fn used_mask_states<'a>(
        &'a self,
        changes: &'a [Option<usize>],
    ) -> impl Iterator<Item = usize> + 'a {
        let mut current = 0;
        self.commands
            .iter()
            .enumerate()
            .filter_map(move |(index, command)| {
                if let Some(Some(next)) = changes.get(index) {
                    current = *next;
                }
                match command {
                    Command::Empty
                    | Command::Clear(_)
                    | Command::AxisClip(_)
                    | Command::ResolvedAxis(_)
                    | Command::PushMask(_)
                    | Command::PushMaskDifference(_)
                    | Command::PushClip(_)
                    | Command::PopScope => None,
                    Command::ImageEffect(draw) if draw.mask.is_some() => None,
                    _ => Some(current),
                }
            })
    }

    /// Push per-draw coverage without an offscreen group. Up to four analytic
    /// masks, or one primary sampled mask followed by three analytic masks, use
    /// direct bindings. Other ordered chains materialize through the original
    /// GPU composition path, with at most 64 coverage components.
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
    /// Retain the original complement operation against preceding coverage.
    /// The first difference starts from opaque coverage across the actual target.
    pub fn push_mask_difference(&mut self, mask: impl Into<Mask>) -> Result<(), Error> {
        let mask = mask.into();
        mask.validate()?;
        let depth = self.scope_depth.checked_add(1).ok_or(Error::Capacity)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands
            .push(Command::PushMaskDifference(Box::new(mask)));
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
        let mut states = vec![CompiledDrawState {
            clip: self.inherited_clip,
            ..Default::default()
        }];
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
            if let Command::ImageEffect(draw) = command {
                let state = &states[current];
                if draw.mask.is_none()
                    && !state.materialized
                    && (state.count > 3
                        || (state.coverage.is_some() && state.count > 1)
                        || state.masks[..state.count]
                            .iter()
                            .any(|m| m.options[0] > 4.5))
                {
                    let mut state = state.clone();
                    state.materialized = true;
                    state.count = 1;
                    state.masks = [MaskUniforms::zeroed(); 4];
                    state.coverage = None;
                    states.try_reserve(1).map_err(|_| Error::Capacity)?;
                    current = states.len();
                    states.push(state);
                    changes.push(Some(current));
                    continue;
                }
            }
            #[cfg(feature = "gpu")]
            if let Command::ManagedShaderEffect(draw) = command {
                let state = &states[current];
                if draw.mask.is_some() && state.count != 0 {
                    return Err(Error::UnsupportedExecution);
                }
                if !state.materialized
                    && (state.count > 1
                        || state.masks[..state.count]
                            .iter()
                            .any(|m| m.options[0] > 4.5))
                {
                    // The original managed helper consumes one exact mask. Keep
                    // full ordered chain/area semantics through the established
                    // GPU coverage producer rather than dropping extra records.
                    let mut state = state.clone();
                    state.materialized = true;
                    state.count = 1;
                    state.masks = [MaskUniforms::zeroed(); 4];
                    state.coverage = None;
                    states.try_reserve(1).map_err(|_| Error::Capacity)?;
                    current = states.len();
                    states.push(state);
                    changes.push(Some(current));
                    continue;
                }
            }
            if matches!(command, Command::Clear(_) | Command::AxisClip(_))
                && states[current].count != 0
            {
                return Err(Error::UnsupportedExecution);
            }
            #[cfg(feature = "gpu")]
            if matches!(command, Command::ShaderEffect(_)) && states[current].count != 0 {
                return Err(Error::UnsupportedExecution);
            }
            if matches!(command, Command::PopScope) {
                current = stack.pop().ok_or(Error::InvalidInput)?;
                changes.push(Some(current));
                continue;
            }
            let mut candidate = match command {
                Command::PushMask(_) | Command::PushMaskDifference(_) | Command::PushClip(_) => {
                    states[current].clone()
                }
                _ => {
                    changes.push(None);
                    continue;
                }
            };
            match command {
                Command::PushMask(mask) | Command::PushMaskDifference(mask) => {
                    candidate.append_mask(
                        mask.as_ref().clone(),
                        if matches!(command, Command::PushMaskDifference(_)) {
                            MaskOperation::Difference
                        } else {
                            MaskOperation::Intersect
                        },
                        dpi,
                    )?;
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
