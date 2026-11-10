//! Original SourceVisual hit-only traversal over captured immutable producer facts.
//! No rendering callback, visual-size rectangle or raster mask is used as input.
use super::*;
use std::fmt::Debug;

/// Ownership-only lease of the actual source generation and its dependencies.
/// Implementors must freeze all facts copied into a snapshot until that snapshot
/// retires. Keeping this lease does not authorize a callback on a GPU thread.
pub trait SourceInputOwner: Debug + Send + Sync {}
impl<T: Debug + Send + Sync> SourceInputOwner for T {}

/// Synchronous source-adapter boundary. The implementation captures already
/// recorded commands and producer facts without OnRender or raster realization.
/// A UI-thread provider need not be Send or Sync: only its immutable result is
/// retained in the hit index. Changes require a newly captured generation.
pub trait SourceVisualProvider {
    fn capture_source_visual(&self) -> Result<Arc<SourceVisualSnapshot>>;
}

/// Original Visual.GetLocalTransform inputs, including the full source matrix.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SourceVisualTransform {
    pub transform: Matrix4,
    pub offset: [f32; 2],
    pub size: [f32; 2],
    pub render_transform_origin: [f32; 2],
    pub center_point: [f32; 3],
    pub scale: [f32; 3],
    /// Original radians passed to Matrix4x4.CreateRotationZ.
    pub rotation: f32,
}
impl Default for SourceVisualTransform {
    fn default() -> Self {
        Self {
            transform: Matrix4::IDENTITY,
            offset: [0.0; 2],
            size: [0.0; 2],
            render_transform_origin: [0.0; 2],
            center_point: [0.0; 3],
            scale: [1.0; 3],
            rotation: 0.0,
        }
    }
}
impl SourceVisualTransform {
    /// Original ordered translation/scale/rotation/restore-center composition.
    /// Source-runtime transcendental and SIMD pixel qualification is independent.
    pub fn local_matrix(self, offset_override: Option<[f32; 2]>) -> Matrix4 {
        let mut anchor = [
            self.size[0] * self.render_transform_origin[0],
            self.size[1] * self.render_transform_origin[1],
            0.0,
        ];
        if self.center_point != [0.0; 3] {
            anchor = self.center_point;
        }
        let offset = offset_override.unwrap_or(self.offset);
        let mut scale = Matrix4::IDENTITY;
        scale.0[0][0] = self.scale[0];
        scale.0[1][1] = self.scale[1];
        scale.0[2][2] = self.scale[2];
        let mut rotation = Matrix4::IDENTITY;
        let cosine = self.rotation.cos();
        let sine = self.rotation.sin();
        rotation.0[0][0] = cosine;
        rotation.0[0][1] = sine;
        rotation.0[1][0] = -sine;
        rotation.0[1][1] = cosine;
        let model = translation(anchor.map(|v| -v))
            * scale
            * rotation
            * translation([offset[0] + anchor[0], offset[1] + anchor[1], anchor[2]]);
        self.transform * model
    }
}
fn translation(p: [f32; 3]) -> Matrix4 {
    let mut result = Matrix4::IDENTITY;
    result.0[3][..3].copy_from_slice(&p);
    result
}

/// Actual source composite-clip family, independent of its parent placement.
#[derive(Clone, Debug)]
pub enum SourceVisualClip {
    Rectangle {
        bounds: [f32; 4],
        transform: Matrix4,
    },
    Geometry {
        geometry: Arc<PathGeometry>,
        transform: Matrix4,
    },
}
/// Original live visual properties relevant to source input. Opacity and optional
/// raster-cache policy are intentionally absent: neither changes source input.
#[derive(Clone, Debug)]
pub struct SourceVisualState {
    pub visible: bool,
    pub hit_test_id: i32,
    pub transform: SourceVisualTransform,
    /// None means no effect; Some is its actual PreservesSourceHitGeometry fact.
    pub effect_preserves_hit_geometry: Option<bool>,
    pub requires_layer_cache: bool,
    pub has_opacity_mask: bool,
    pub has_opacity_mask_picture: bool,
    pub clip_bounds: Option<[f32; 4]>,
    pub outer_clip_bounds: Option<[f32; 4]>,
    pub local_composite_clip: Option<SourceVisualClip>,
    pub outer_composite_clips: Arc<[SourceVisualClip]>,
    pub geometry_clip: Option<Arc<PathGeometry>>,
    pub has_template: bool,
}
impl Default for SourceVisualState {
    fn default() -> Self {
        Self {
            visible: true,
            hit_test_id: 0,
            transform: Default::default(),
            effect_preserves_hit_geometry: None,
            requires_layer_cache: false,
            has_opacity_mask: false,
            has_opacity_mask_picture: false,
            clip_bounds: None,
            outer_clip_bounds: None,
            local_composite_clip: None,
            outer_composite_clips: Arc::from([]),
            geometry_clip: None,
            has_template: false,
        }
    }
}
/// Original ISourceGeometryHitTestCommands contract and its immutable generation.
#[derive(Clone, Debug)]
pub struct SourceVisualCommands {
    pub commands: Arc<SourceCommandList>,
    /// Original interface default is false. No mask-content inference is allowed.
    pub opacity_mask_preserves_hit_geometry: bool,
}
impl SourceVisualCommands {
    pub fn new(commands: Arc<SourceCommandList>) -> Self {
        Self {
            commands,
            opacity_mask_preserves_hit_geometry: false,
        }
    }
}
/// Fully owned source node. None models a visual lacking the source interface;
/// it is admitted only while invisible. Children retain their source order.
#[derive(Clone, Debug)]
pub struct SourceVisualSnapshot {
    pub state: SourceVisualState,
    pub source: Option<SourceVisualCommands>,
    pub children: Arc<[Arc<SourceVisualSnapshot>]>,
    pub owner: Arc<dyn SourceInputOwner>,
}
/// Source-only nested objects cannot be reconstructed from archive payloads.
#[derive(Clone, Debug)]
pub enum SourceInputCommand {
    Recorded(Arc<CapturedHitCommand>),
    Picture {
        command: Arc<CapturedHitCommand>,
        source: Option<Arc<SourceCommandList>>,
    },
    Visual {
        command: Arc<CapturedHitCommand>,
        source: Option<Arc<SourceVisualSnapshot>>,
    },
    Glyph(Arc<SourceGlyphInput>),
}
impl SourceInputCommand {
    pub fn command(&self) -> &Arc<CapturedHitCommand> {
        match self {
            Self::Recorded(c)
            | Self::Picture { command: c, .. }
            | Self::Visual { command: c, .. } => c,
            Self::Glyph(g) => g.command(),
        }
    }
}
/// Exact source command inventory and original buffer-provider ownership. Each
/// CapturedHitCommand retains its actual provider; the lease retains source-only
/// resources that intentionally have no v1-v10 wire representation.
#[derive(Clone, Debug)]
pub struct SourceCommandList {
    pub commands: Arc<[SourceInputCommand]>,
    pub owner: Arc<dyn SourceInputOwner>,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SourceVisualCaptureOptions {
    pub offset_override: Option<[f32; 2]>,
    pub include_local_transform: bool,
    pub include_local_visual_state: bool,
}
impl Default for SourceVisualCaptureOptions {
    fn default() -> Self {
        Self {
            offset_override: None,
            include_local_transform: true,
            include_local_visual_state: true,
        }
    }
}

impl ArchiveHitTestCache {
    /// Call the producer synchronously, then consume only its frozen generation.
    /// Failed producer capture faults publication just like failed traversal.
    pub fn capture_source_visual(
        &mut self,
        provider: &dyn SourceVisualProvider,
        parent: Matrix4,
    ) -> Result<()> {
        if self.image_depth != 0 {
            return Ok(());
        }
        self.source_capture_operation(|cache| {
            let snapshot = provider.capture_source_visual()?;
            cache.add_source_visual(snapshot, parent)
        })
    }

    pub fn add_source_visual(
        &mut self,
        source: Arc<SourceVisualSnapshot>,
        parent: Matrix4,
    ) -> Result<()> {
        self.add_source_visual_with_options(source, parent, Default::default(), None)
    }
    /// Observer is synchronous and only observes explicit DrawVisual commands,
    /// preserving the original distinction from ordinary child traversal.
    pub fn add_source_visual_with_options(
        &mut self,
        source: Arc<SourceVisualSnapshot>,
        parent: Matrix4,
        options: SourceVisualCaptureOptions,
        observer: Option<&mut dyn FnMut(&Arc<SourceVisualSnapshot>) -> Result<()>>,
    ) -> Result<()> {
        if self.image_depth != 0 {
            return Ok(());
        }
        let mut observer = observer;
        self.source_capture_operation(|cache| {
            push(&mut cache.source_visuals, source.clone())?;
            cache.source_visual(&source, parent, options, &mut observer, 0)
        })
    }

    /// Actual source DrawingContext/picture input without a surrounding visual.
    /// The same strict scope, glyph-authority and nested-source admission applies.
    pub fn add_source_commands(
        &mut self,
        source: Arc<SourceCommandList>,
        owner_id: i32,
        transform: Matrix4,
    ) -> Result<()> {
        self.source_capture_operation(|cache| {
            push(&mut cache.source_lists, source.clone())?;
            cache.source_commands(&source, owner_id, transform, &mut None, 0)
        })
    }
    pub(super) fn source_capture_operation<T>(
        &mut self,
        operation: impl FnOnce(&mut Self) -> Result<T>,
    ) -> Result<T> {
        if self.failed {
            return Err(ArchiveError::InvalidData);
        }
        let mut guard = SourceCaptureFault {
            cache: self,
            complete: false,
        };
        let result = operation(&mut *guard.cache);
        guard.complete = result.is_ok();
        result
    }

    fn source_visual(
        &mut self,
        visual: &SourceVisualSnapshot,
        parent: Matrix4,
        options: SourceVisualCaptureOptions,
        observer: &mut Option<&mut dyn FnMut(&Arc<SourceVisualSnapshot>) -> Result<()>>,
        depth: u32,
    ) -> Result<()> {
        source_depth(depth)?;
        let state = &visual.state;
        if !state.visible {
            return Ok(());
        }
        let source = visual.source.as_ref().ok_or(ArchiveError::Unsupported)?;
        if state.effect_preserves_hit_geometry == Some(false) || state.requires_layer_cache {
            return Err(ArchiveError::Unsupported);
        }
        if (state.has_opacity_mask || state.has_opacity_mask_picture)
            && !source.opacity_mask_preserves_hit_geometry
        {
            return Err(ArchiveError::Unsupported);
        }
        let local = if options.include_local_transform {
            state.transform.local_matrix(options.offset_override)
        } else {
            let p = options.offset_override.unwrap_or([0.0; 2]);
            translation([p[0], p[1], 0.0])
        };
        let global = local * parent;
        let clip_depth = self.clips.len();
        if options.include_local_visual_state {
            if let Some(bounds) = state.clip_bounds {
                self.source_rectangle_clip(bounds, global)?;
            }
            if let Some(bounds) = state.outer_clip_bounds {
                self.source_rectangle_clip(bounds, parent)?;
            }
            if let Some(clip) = &state.local_composite_clip {
                self.source_composite_clip(clip, global)?;
            }
            for clip in state.outer_composite_clips.iter() {
                self.source_composite_clip(clip, parent)?;
            }
            if let Some(path) = &state.geometry_clip {
                self.source_geometry_clip(path, global)?;
            }
        }
        if state.has_template {
            self.source_children(visual, global, observer, depth)?;
        }
        self.source_commands(&source.commands, state.hit_test_id, global, observer, depth)?;
        if !state.has_template {
            self.source_children(visual, global, observer, depth)?;
        }
        while self.clips.len() > clip_depth {
            self.pop_clip();
        }
        Ok(())
    }
    fn source_children(
        &mut self,
        visual: &SourceVisualSnapshot,
        transform: Matrix4,
        observer: &mut Option<&mut dyn FnMut(&Arc<SourceVisualSnapshot>) -> Result<()>>,
        depth: u32,
    ) -> Result<()> {
        for child in visual.children.iter() {
            self.source_visual(child, transform, Default::default(), observer, depth + 1)?;
        }
        Ok(())
    }
    fn source_composite_clip(&mut self, clip: &SourceVisualClip, transform: Matrix4) -> Result<()> {
        match clip {
            SourceVisualClip::Rectangle {
                bounds,
                transform: local,
            } => self.source_rectangle_clip(*bounds, *local * transform),
            SourceVisualClip::Geometry {
                geometry,
                transform: local,
            } => self.source_geometry_clip(geometry, *local * transform),
        }
    }
    fn source_rectangle_clip(&mut self, bounds: [f32; 4], transform: Matrix4) -> Result<()> {
        if bounds.iter().any(|v| !v.is_finite()) {
            return Err(ArchiveError::Unsupported);
        }
        if !invertible(transform) {
            return push(&mut self.clips, Clip::empty());
        }
        let m = transform.0;
        if (m[0][1] == 0.0 && m[1][0] == 0.0) || (m[0][0] == 0.0 && m[1][1] == 0.0) {
            return self.push_clip(bounds, transform);
        }
        self.source_geometry_clip(
            &PathGeometry::rectangle(bounds[0], bounds[1], bounds[2], bounds[3]),
            transform,
        )
    }
    fn source_geometry_clip(&mut self, geometry: &PathGeometry, transform: Matrix4) -> Result<()> {
        if !invertible(transform) {
            return push(&mut self.clips, Clip::empty());
        }
        let mut path = geometry.transformed(transform)?;
        if let Some(inherited) = self.clips.last().and_then(|c| c.path.as_ref()) {
            path = PathGeometry::combine_deferred(
                inherited.clone(),
                Arc::new(path),
                BooleanOperation::Intersect,
            );
        }
        // Keep the compiler's distinct failure versus complete-empty result. An
        // unavailable Boolean/operand is never an inherited rectangle fallback.
        let compiled = match path.compile_path() {
            Ok(value) => value,
            Err(Error::InvalidInput | Error::UnsupportedExecution) => {
                return Err(ArchiveError::Unsupported);
            }
            Err(error) => return Err(error.into()),
        };
        if compiled.record.is_none() || compiled.segments.is_empty() {
            if compiled.record.is_none()
                && compiled.segments.is_empty()
                && path.has_complete_hit_test_definition()
                && geometry.has_complete_hit_test_definition()
            {
                return push(&mut self.clips, Clip::empty());
            }
            return Err(ArchiveError::Unsupported);
        }
        let record = compiled.record.ok_or(ArchiveError::Unsupported)?;
        let start = u32::try_from(self.segments.len()).map_err(|_| ArchiveError::Capacity)?;
        let count = u32::try_from(compiled.segments.len()).map_err(|_| ArchiveError::Capacity)?;
        start.checked_add(count).ok_or(ArchiveError::Capacity)?;
        reserve(&mut self.segments, compiled.segments.len())?;
        self.segments.extend(compiled.segments);
        if record.bounds.iter().any(|v| !v.is_finite()) {
            return Err(ArchiveError::Unsupported);
        }
        let bounds = self
            .clips
            .last()
            .map_or(record.bounds, |c| intersect(record.bounds, c.bounds));
        if bounds[2] <= bounds[0] || bounds[3] <= bounds[1] {
            if !geometry.has_complete_hit_test_definition() {
                return Err(ArchiveError::Unsupported);
            }
            return push(&mut self.clips, Clip::empty());
        }
        push(
            &mut self.clips,
            Clip {
                bounds,
                path: Some(Arc::new(path)),
                compiled: Some(Compiled {
                    bounds: record.bounds,
                    start,
                    count,
                    rule: if record.fill_rule == 0 {
                        FillRule::Nonzero
                    } else {
                        FillRule::EvenOdd
                    },
                }),
            },
        )
    }
    fn source_commands(
        &mut self,
        source: &SourceCommandList,
        owner_id: i32,
        transform: Matrix4,
        observer: &mut Option<&mut dyn FnMut(&Arc<SourceVisualSnapshot>) -> Result<()>>,
        depth: u32,
    ) -> Result<()> {
        source_depth(depth)?;
        let clip_depth = self.clips.len();
        let opacity_depth = self.opacities.len();
        let image_depth = self.image_depth;
        let point_depth = self.point_stack.len();
        let mut mask_depth = 0u32;
        for entry in source.commands.iter() {
            let captured = entry.command();
            let mut command = (*captured.raster).clone();
            let mut resolved = if command.use_gpu_transforms {
                command.camera_view * transform
            } else {
                transform
            };
            if command.kind != K::DrawPath && command.transform != Matrix4([[0.0; 4]; 4]) {
                resolved = command.transform * resolved;
            }
            command.use_gpu_transforms = false;
            command.camera_view = Matrix4([[0.0; 4]; 4]);
            let c = Arc::new(CapturedHitCommand {
                raster: Arc::new(command),
                ..(**captured).clone()
            });
            if self.image_depth != 0 {
                self.add_captured_command(c, resolved, None)?;
                continue;
            }
            let command = &c.raster;
            let id = if command.hit_test_id != 0 {
                command.hit_test_id
            } else {
                owner_id
            };
            use SourceHitGeometryKind as S;
            if matches!(
                c.source.kind,
                S::PointRectangleBegin | S::PointEmptyBegin | S::PointRectangleEnd
            ) {
                if c.source.kind == S::PointRectangleEnd && self.point_stack.len() <= point_depth {
                    return Err(ArchiveError::InvalidData);
                }
                self.add_captured_command(c, resolved, Some(id))?;
                continue;
            }
            match command.kind {
                K::DrawPicture => match entry {
                    SourceInputCommand::Picture {
                        source: Some(child),
                        ..
                    } => self.source_commands(child, id, resolved, observer, depth + 1)?,
                    SourceInputCommand::Picture { source: None, .. } => {}
                    _ => return Err(ArchiveError::Unsupported),
                },
                K::DrawVisual => match entry {
                    SourceInputCommand::Visual {
                        source: Some(child),
                        ..
                    } => {
                        if let Some(callback) = observer.as_deref_mut() {
                            callback(child)?;
                        }
                        self.source_visual(
                            child,
                            resolved,
                            Default::default(),
                            observer,
                            depth + 1,
                        )?;
                    }
                    SourceInputCommand::Visual { source: None, .. } => {}
                    _ => return Err(ArchiveError::Unsupported),
                },
                K::PopClip | K::PopGeometryClip => {
                    if self.clips.len() <= clip_depth {
                        return Err(ArchiveError::InvalidData);
                    }
                    self.add_captured_command(c, resolved, None)?;
                }
                K::PopOpacity => {
                    if self.opacities.len() <= opacity_depth {
                        return Err(ArchiveError::InvalidData);
                    }
                    self.add_captured_command(c, resolved, None)?;
                }
                K::PushClip if c.image_hit_scope && id == 0 => self.image_depth = 1,
                K::PushClip if c.image_hit_scope => {
                    self.add_captured_command(c, resolved, Some(id))?
                }
                K::PushClip => self.source_rectangle_clip(command.rect, resolved)?,
                K::PushGeometryClip => {
                    let geometry = command
                        .path
                        .as_ref()
                        .ok_or(ArchiveError::Unsupported)?
                        .to_geometry()?;
                    self.source_geometry_clip(&geometry, resolved)?;
                }
                K::PushOpacity => self.add_captured_command(c, resolved, Some(id))?,
                K::PushOpacityMask => {
                    mask_depth += 1;
                    if mask_depth >= 256 {
                        return Err(ArchiveError::InvalidData);
                    }
                }
                K::PopOpacityMask => {
                    mask_depth = mask_depth.checked_sub(1).ok_or(ArchiveError::InvalidData)?;
                }
                K::DrawRect
                | K::DrawRoundedRect
                | K::DrawEllipse
                | K::DrawCircle
                | K::DrawLine
                | K::DrawBezier
                | K::DrawCubicBezier
                | K::DrawPath
                | K::DrawTexture
                | K::FillTriangle
                | K::FillQuad
                | K::DrawVertexMesh
                | K::DrawPointBatch
                | K::DrawPolyline => {
                    if id != 0 {
                        self.add_captured_command(c, resolved, Some(id))?;
                    }
                }
                K::DrawGlyphRun => {
                    // Only this explicit source-owned wrapper asserts original
                    // authoritative ink. Raw wire Rect/position arrays do not.
                    let SourceInputCommand::Glyph(glyph) = entry else {
                        return Err(ArchiveError::Unsupported);
                    };
                    glyph.admit()?;
                    if id != 0 {
                        self.add_captured_command(c, resolved, Some(id))?;
                    }
                }
                _ => return Err(ArchiveError::Unsupported),
            }
        }
        if mask_depth != 0
            || self.clips.len() != clip_depth
            || self.opacities.len() != opacity_depth
            || self.image_depth != image_depth
            || self.point_stack.len() != point_depth
        {
            return Err(ArchiveError::InvalidData);
        }
        Ok(())
    }
}
fn source_depth(depth: u32) -> Result<()> {
    if depth >= 256 {
        Err(ArchiveError::InvalidData)
    } else {
        Ok(())
    }
}

// Original catch-all fault publication also applies when an adapter unwinds.
// The panic itself is not swallowed or converted to an unrelated archive error.
struct SourceCaptureFault<'a> {
    cache: &'a mut ArchiveHitTestCache,
    complete: bool,
}
impl Drop for SourceCaptureFault<'_> {
    fn drop(&mut self) {
        if !self.complete {
            self.cache.failed = true;
        }
    }
}
