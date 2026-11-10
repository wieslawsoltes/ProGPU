//! Fixed-size coverage snapshots and exact binary pixel-center clip boundaries.
use super::*;
use crate::mask::{Mask, MaskComponent, MaskOperation, MaskUniforms, TextureMask};
use bytemuck::Zeroable;
use wide::f32x4;

#[derive(Clone, Debug)]
pub(crate) struct CompiledDrawState {
    pub blend: LayerBlend,
    /// Original logical clip edges used by extension QuadClipper before projection.
    pub logical_clip: Option<[f32; 4]>,
    pub masks: [MaskUniforms; 4],
    pub count: usize,
    pub coverage: Option<TextureMask>,
    pub components: Arc<[MaskComponent]>,
    pub materialized: bool,
    /// Proven one-to-one physical R8 output, including independent bounded storage.
    pub local_coverage: bool,
    /// Physical left/top/right/bottom integer half-open boundaries.
    pub clip: Option<[u32; 4]>,
}
impl Default for CompiledDrawState {
    fn default() -> Self {
        Self {
            blend: LayerBlend::SrcOver,
            logical_clip: None,
            masks: [MaskUniforms::zeroed(); 4],
            count: 0,
            coverage: None,
            components: Arc::from([]),
            materialized: false,
            local_coverage: false,
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
    pub(crate) fn append_mask(
        &mut self,
        mask: Mask,
        operation: MaskOperation,
        dpi: f32,
    ) -> Result<(), Error> {
        let source_clip = if matches!(&mask, Mask::Geometry(_) | Mask::Source(_)) {
            self.logical_clip
        } else {
            None
        };
        self.append_mask_with_source_clip(mask, operation, dpi, source_clip)
    }
    pub(crate) fn append_mask_with_source_clip(
        &mut self,
        mask: Mask,
        operation: MaskOperation,
        dpi: f32,
        source_clip: Option<[f32; 4]>,
    ) -> Result<(), Error> {
        if self.components.len() == 64 {
            return Err(Error::Capacity);
        }
        let mut components = Vec::new();
        components
            .try_reserve_exact(self.components.len() + 1)
            .map_err(|_| Error::Capacity)?;
        #[cfg(feature = "gpu")]
        let replace = matches!(&mask, Mask::RenderedTexture(v) if v.replaces_chain)
            || matches!(&mask, Mask::FrameAnalytic(v) if v.replaces_chain);
        #[cfg(not(feature = "gpu"))]
        let replace = false;
        if !replace {
            components.extend_from_slice(&self.components);
        }
        components.push(MaskComponent {
            mask,
            operation,
            source_clip,
        });
        let direct = components.len() <= 4
            && components.iter().enumerate().all(|(i, c)| {
                c.operation == MaskOperation::Intersect
                    && !matches!(c.mask, Mask::Path(_) | Mask::Source(_) | Mask::Geometry(_))
                    && (i == 0 || analytic(&c.mask))
            });
        self.masks = [MaskUniforms::zeroed(); 4];
        self.coverage = None;
        self.materialized = !direct;
        self.count = if direct { components.len() } else { 1 };
        // Validate every original record even when it will be materialized later.
        for (i, c) in components.iter().enumerate() {
            if matches!(c.mask, Mask::Path(_) | Mask::Source(_) | Mask::Geometry(_)) {
                if matches!(c.mask, Mask::Source(_) | Mask::Geometry(_))
                    && c.operation != MaskOperation::Intersect
                {
                    return Err(Error::UnsupportedExecution);
                }
                c.mask.validate()?;
            } else {
                let uniforms = c.mask.uniforms(dpi)?;
                if direct {
                    self.masks[i] = uniforms;
                }
            }
        }
        if direct {
            self.coverage = match &components[0].mask {
                Mask::Coverage(mask) => Some(mask.clone().into()),
                Mask::Texture(mask) => Some(mask.clone()),
                #[cfg(feature = "gpu")]
                Mask::RenderedTexture(mask) => Some(mask.texture.clone()),
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
                    | Command::PushClipEdges(_)
                    | Command::PopScope
                    | Command::PopCoverage { .. }
                    | Command::PushBlend(_)
                    | Command::PopBlend => None,
                    Command::CustomGrid(_) | Command::Series(_) | Command::Mesh3DCamera(_) => None,
                    Command::ArchiveHatch(draw) if draw.uses_extension_pipeline() => None,
                    Command::ArchiveThreeD(draw)
                        if matches!(
                            draw.route(),
                            crate::picture_archive::ThreeDRoute::LineExtension
                                | crate::picture_archive::ThreeDRoute::AcisExtension
                        ) =>
                    {
                        None
                    }
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
        if self.scope_depth == self.blend_depth {
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
        let mut blend_stack = Vec::new();
        let mut current = 0;
        for command in &self.commands {
            #[cfg(feature = "gpu")]
            let glyph = matches!(command, Command::Glyph(_));
            #[cfg(not(feature = "gpu"))]
            let glyph = false;
            let state = &states[current];
            if state.count > 1
                && !state.materialized
                && state.blend.requires_premultiplied()
                && (glyph
                    || matches!(command, Command::ArchiveHatch(draw) if !draw.uses_extension_pipeline())
                    || matches!(command, Command::ArchiveThreeD(draw) if matches!(draw.route(), crate::picture_archive::ThreeDRoute::Line | crate::picture_archive::ThreeDRoute::Acis))
                    || matches!(
                        command,
                        Command::VertexMesh(_)
                            | Command::ArchiveGeometry(_)
                            | Command::ArchiveVector(_)
                            | Command::Analytic(_)
                            | Command::Path(_)
                            | Command::MaterialAnalytic(..)
                            | Command::MaterialPath(..)
                            | Command::Stroke(_)
                            | Command::PlacedStroke(..)
                            | Command::Text(_)
                            | Command::TextPath(..)
                    ))
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
            if let Command::BackdropMaterial(_) = command {
                let state = &states[current];
                if !state.materialized
                    && (state.count > 1
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
            if matches!(command, Command::PushBlend(_) | Command::PopBlend) {
                let mut candidate = states[current].clone();
                match command {
                    Command::PushBlend(blend) => {
                        blend_stack.try_reserve(1).map_err(|_| Error::Capacity)?;
                        blend_stack.push(candidate.blend);
                        candidate.blend = *blend;
                    }
                    Command::PopBlend => {
                        candidate.blend = blend_stack.pop().ok_or(Error::InvalidInput)?
                    }
                    _ => unreachable!(),
                }
                states.try_reserve(1).map_err(|_| Error::Capacity)?;
                current = states.len();
                states.push(candidate);
                changes.push(Some(current));
                continue;
            }
            if matches!(command, Command::PopScope) {
                let (_, previous) = stack.pop().ok_or(Error::InvalidInput)?;
                let mut candidate = states[previous].clone();
                candidate.blend = states[current].blend;
                states.try_reserve(1).map_err(|_| Error::Capacity)?;
                current = states.len();
                states.push(candidate);
                changes.push(Some(current));
                continue;
            }
            if let Command::PopCoverage { kind, .. } = command {
                let index = stack
                    .iter()
                    .rposition(|(candidate, _)| candidate == kind)
                    .ok_or(Error::InvalidInput)?;
                let (_, previous) = stack.remove(index);
                let mut candidate = match kind {
                    CoverageKind::Clip => {
                        let mut value = states[current].clone();
                        value.clip = states[previous].clip;
                        value.logical_clip = states[previous].logical_clip;
                        value
                    }
                    CoverageKind::Mask => {
                        let mut value = states[previous].clone();
                        value.clip = states[current].clip;
                        value.logical_clip = states[current].logical_clip;
                        value
                    }
                };
                candidate.blend = states[current].blend;
                states.try_reserve(1).map_err(|_| Error::Capacity)?;
                current = states.len();
                states.push(candidate);
                changes.push(Some(current));
                continue;
            }
            let mut candidate = match command {
                Command::PushMask(_)
                | Command::PushMaskDifference(_)
                | Command::PushClip(_)
                | Command::PushClipEdges(_) => states[current].clone(),
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
                Command::PushClip(bounds) | Command::PushClipEdges(bounds) => {
                    let edges = if matches!(command, Command::PushClip(_)) {
                        validate_clip(*bounds)?;
                        let [x, y, w, h] = *bounds;
                        [x, y, x + w, y + h]
                    } else {
                        source_capture::validate_edges(*bounds)?;
                        *bounds
                    };
                    candidate.logical_clip = Some(if let Some(old) = candidate.logical_clip {
                        [
                            edges[0].max(old[0]),
                            edges[1].max(old[1]),
                            edges[2].min(old[2]),
                            edges[3].min(old[3]),
                        ]
                    } else {
                        edges
                    });
                    let projected =
                        (f32x4::from(edges) * f32x4::splat(dpi) + f32x4::ZERO).to_array();
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
            stack.push((
                if matches!(command, Command::PushClip(_) | Command::PushClipEdges(_)) {
                    CoverageKind::Clip
                } else {
                    CoverageKind::Mask
                },
                current,
            ));
            current = states.len();
            states.push(candidate);
            changes.push(Some(current));
        }
        if !stack.is_empty() || !blend_stack.is_empty() {
            return Err(Error::InvalidInput);
        }
        Ok((states, changes))
    }
}

fn analytic(mask: &Mask) -> bool {
    match mask {
        Mask::Analytic(_) | Mask::AxisClipEdges(_) => true,
        #[cfg(feature = "gpu")]
        Mask::FrameAnalytic(_) => true,
        _ => false,
    }
}
