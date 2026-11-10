use super::*;
use crate::{
    image::ImageSampling,
    text::paint::{TextHintingMode, TextRenderingMode},
};

/// Actual presentation-only command dependencies. Unused inherited fields are
/// deliberately cleared from cache identity, matching original scene pages.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RenderPresentationDependencies {
    /// Local image commands bind inherited texture sampling.
    pub texture_sampling: bool,
    /// Local text commands bind inherited rendering mode.
    pub text_rendering: bool,
    /// Local text commands bind inherited outline hinting mode.
    pub text_hinting: bool,
}
/// Frozen original presentation state for an incremental owned command cache.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RenderPresentationState {
    /// Explicit actual local dependencies.
    pub dependencies: RenderPresentationDependencies,
    /// Original texture sampling only when selected by dependency metadata.
    pub texture_sampling: Option<ImageSampling>,
    /// Original rendering mode only when selected by dependency metadata.
    pub text_rendering: Option<TextRenderingMode>,
    /// Original hinting mode only when selected by dependency metadata.
    pub text_hinting: Option<TextHintingMode>,
}
impl RenderPresentationState {
    /// Collapse only fields whose producer explicitly declares no dependency.
    pub fn normalized(mut self) -> Self {
        if !self.dependencies.texture_sampling {
            self.texture_sampling = None;
        }
        if !self.dependencies.text_rendering {
            self.text_rendering = None;
        }
        if !self.dependencies.text_hinting {
            self.text_hinting = None;
        }
        self
    }
}

/// Complete original owner-returned command generation. Producers return their
/// real immutable Scene owner; no command arrays are duplicated by this layer.
#[derive(Clone, Debug)]
pub struct OwnedRenderCommands {
    /// Immutable actual recording and referenced resource generations.
    pub scene: Arc<Scene>,
    /// Original local-bounds contract; None disables local-content culling.
    pub local_bounds: Option<[f32; 4]>,
    /// Whether a renderer may retain this generation as an incremental page.
    pub can_cache_incremental_page: bool,
    /// Frozen actual inherited-state dependencies for page identity.
    pub presentation: RenderPresentationState,
}
impl OwnedRenderCommands {
    /// Retain ordinary immutable source commands with no inherited dependencies.
    pub fn new(scene: Arc<Scene>) -> Self {
        Self {
            scene,
            local_bounds: None,
            can_cache_incremental_page: true,
            presentation: Default::default(),
        }
    }
}
/// Explicit owned render-command provider. Calls occur during snapshot
/// preparation only. Mutations require Visual::invalidate; presentation-only
/// changes require Visual::invalidate_visual_state and a new presentation key.
pub trait OwnedRenderCommandSource {
    /// Snapshot one complete immutable generation without rendering or submitting.
    fn commands(&self) -> Result<OwnedRenderCommands, Error>;
    /// Read the actual current presentation key without rebuilding local content.
    fn presentation_state(&self) -> RenderPresentationState {
        RenderPresentationState::default()
    }
}

/// Immutable local page generation ready for the existing Scene renderer.
#[derive(Clone, Debug)]
pub struct VisualPage {
    scene: Arc<Scene>,
    content_version: i64,
    local_bounds: Option<[f32; 4]>,
    can_cache: bool,
    presentation: RenderPresentationState,
}
impl VisualPage {
    /// The real local Scene owner; outer visual frames are applied independently.
    pub fn scene(&self) -> &Arc<Scene> {
        &self.scene
    }
    /// Original local-content revision at publication.
    pub fn content_version(&self) -> i64 {
        self.content_version
    }
    /// Original producer's conservative local bounds, never an input shape.
    pub fn local_bounds(&self) -> Option<[f32; 4]> {
        self.local_bounds
    }
    /// Original producer's compiled-page retention permission.
    pub fn can_cache_incremental_page(&self) -> bool {
        self.can_cache
    }
    /// Frozen dependency-normalized page presentation identity.
    pub fn presentation_state(&self) -> RenderPresentationState {
        self.presentation
    }
}

/// Immutable visual subtree generation. A renderer must consume these scopes
/// and matrices at the actual target; local pages are not pre-scaled vertices.
#[derive(Clone, Debug)]
pub struct VisualSnapshot {
    /// Stable retained source identity.
    identity: VisualIdentity,
    /// Exact original revisions at publication.
    versions: VisualVersions,
    /// Frozen original visual and cold composite properties.
    properties: VisualProperties,
    /// Actual local matrix, before inherited placement.
    local_transform: Matrix4,
    /// Local times complete parent matrix in original source order.
    global_transform: Matrix4,
    /// Immutable real local command generation.
    page: VisualPage,
    /// Frozen effect value and resource revision; no live mutable parameter read.
    effect: Option<(i64, CompositionEffectValue)>,
    /// Real resolved opacity material in the original mask bounds.
    opacity_mask: Option<CompositionBrushSnapshot>,
    /// Original ordered child snapshots, including their independent frames.
    children: Vec<VisualSnapshot>,
    /// Explicit overlay partition in the retained child list.
    topmost_child_count: usize,
}
impl VisualSnapshot {
    /// Actual retained visual identity.
    pub fn identity(&self) -> &VisualIdentity {
        &self.identity
    }
    /// Original revisions captured with this complete subtree.
    pub fn versions(&self) -> VisualVersions {
        self.versions
    }
    /// Frozen source properties and independent composite scope descriptors.
    pub fn properties(&self) -> &VisualProperties {
        &self.properties
    }
    /// Actual complete local source frame.
    pub fn local_transform(&self) -> Matrix4 {
        self.local_transform
    }
    /// Actual complete inherited source frame.
    pub fn global_transform(&self) -> Matrix4 {
        self.global_transform
    }
    /// Real immutable local command page.
    pub fn page(&self) -> &VisualPage {
        &self.page
    }
    /// Actual effect resource revision and frozen parameter generation.
    pub fn effect(&self) -> Option<&(i64, CompositionEffectValue)> {
        self.effect.as_ref()
    }
    /// Frozen material generation in the original opacity-mask bounds.
    pub fn opacity_mask(&self) -> Option<&CompositionBrushSnapshot> {
        self.opacity_mask.as_ref()
    }
    /// Original ordered children, with their complete independently frozen state.
    pub fn children(&self) -> &[VisualSnapshot] {
        &self.children
    }
    /// Original final overlay partition length.
    pub fn topmost_child_count(&self) -> usize {
        self.topmost_child_count
    }
    /// Walk retained local pages in original painter order, including template
    /// children-before-content semantics. Scope application remains independent.
    pub fn visit_pages(&self, visitor: &mut impl FnMut(&VisualSnapshot)) {
        if !self.properties.has_template {
            visitor(self);
        }
        for child in &self.children {
            child.visit_pages(visitor);
        }
        if self.properties.has_template {
            visitor(self);
        }
    }
}
impl Visual {
    /// Retire dirty flags only for this exact successfully consumed generation.
    /// A later mutation or reparenting keeps the complete current tree dirty.
    pub fn mark_snapshot_clean(&self, snapshot: &VisualSnapshot) -> bool {
        let mut pending = vec![(self.clone(), snapshot)];
        let mut accepted = Vec::new();
        while let Some((visual, snapshot)) = pending.pop() {
            let node = visual.0.borrow();
            if node.identity != snapshot.identity
                || node.versions != snapshot.versions
                || node.children.len() != snapshot.children.len()
            {
                return false;
            }
            for (child, captured) in node.children.iter().zip(&snapshot.children) {
                pending.push((child.clone(), captured));
            }
            accepted.push(visual.clone());
        }
        for visual in accepted {
            visual.mark_clean();
        }
        true
    }
    /// Freeze the complete owned subtree and create changed local command pages.
    /// Pages publish together only after every source callback and child succeeds
    /// and the root's actual state/tree generation is still the captured one.
    pub fn snapshot(&self) -> Result<VisualSnapshot, Error> {
        let versions = self.versions();
        let mut pages = Vec::new();
        let result = self.snapshot_core(Matrix4::IDENTITY, 0, &mut pages)?;
        if self.versions() != versions {
            return Err(Error::InvalidInput);
        }
        if pages
            .iter()
            .any(|(visual, page)| visual.versions().content != page.content_version)
        {
            return Err(Error::InvalidInput);
        }
        for (visual, page) in pages {
            let mut node = visual.0.borrow_mut();
            node.page = Some(page);
        }
        Ok(result)
    }
    fn snapshot_core(
        &self,
        parent: Matrix4,
        depth: usize,
        pages: &mut Vec<(Visual, VisualPage)>,
    ) -> Result<VisualSnapshot, Error> {
        if depth >= 256 {
            return Err(Error::Capacity);
        }
        {
            let mut node = self.0.borrow_mut();
            if node.snapshot_preparing {
                return Err(Error::InvalidInput);
            }
            node.snapshot_preparing = true;
        }
        let _preparation = SnapshotPreparation(self.clone());
        let (
            identity,
            versions,
            mut properties,
            content,
            cached,
            effect,
            opacity_mask,
            children,
            topmost,
        ) = {
            let node = self.0.borrow();
            (
                node.identity.clone(),
                node.versions,
                node.properties.clone(),
                node.content.clone(),
                node.page.clone(),
                node.effect.clone(),
                node.opacity_mask.clone(),
                node.children.clone(),
                node.topmost_count,
            )
        };
        let bounds = properties.composite.opacity_mask_bounds.unwrap_or([
            0.0,
            0.0,
            properties.size[0],
            properties.size[1],
        ]);
        let effect = effect.map(|v| (v.revision(), v.value()));
        let opacity_mask = opacity_mask.map(|v| v.resolve(bounds)).transpose()?;
        let presentation = match &content {
            Content::Source(source) => source.presentation_state().normalized(),
            _ => Default::default(),
        };
        let page = if let Some(page) = cached
            .filter(|v| v.content_version == versions.content && v.presentation == presentation)
        {
            page
        } else {
            let commands = match &content {
                Content::Empty => OwnedRenderCommands::new(Arc::new(Scene::default())),
                Content::Drawing(scene) => OwnedRenderCommands::new(scene.clone()),
                Content::Source(source) => {
                    let commands = source.commands()?;
                    if commands.presentation.normalized() != presentation {
                        return Err(Error::InvalidInput);
                    }
                    commands
                }
                Content::Sprite(brush) => {
                    let mut scene = Scene::default();
                    let size = properties.size;
                    if let Some(brush) = brush
                        && size[0] > 0.0
                        && size[1] > 0.0
                    {
                        let bounds = [0.0, 0.0, size[0], size[1]];
                        brush.resolve(bounds)?.record(&mut scene, bounds)?;
                    }
                    let local_bounds = (!scene.is_empty()).then_some([0.0, 0.0, size[0], size[1]]);
                    OwnedRenderCommands {
                        local_bounds,
                        ..OwnedRenderCommands::new(Arc::new(scene))
                    }
                }
                Content::Shapes { shapes, view_box } => {
                    let mut scene = Scene::default();
                    shapes::record_shapes(&mut scene, shapes, view_box.as_ref(), properties.size)?;
                    let local_bounds = (!scene.is_empty()).then_some([
                        0.0,
                        0.0,
                        properties.size[0],
                        properties.size[1],
                    ]);
                    OwnedRenderCommands {
                        local_bounds,
                        ..OwnedRenderCommands::new(Arc::new(scene))
                    }
                }
            };
            let page = VisualPage {
                scene: commands.scene,
                content_version: versions.content,
                local_bounds: commands.local_bounds.or(properties.local_render_bounds),
                can_cache: commands.can_cache_incremental_page,
                presentation: commands.presentation.normalized(),
            };
            pages.try_reserve(1).map_err(|_| Error::Capacity)?;
            pages.push((self.clone(), page.clone()));
            page
        };
        if matches!(content, Content::Shapes { .. }) {
            properties.composite.clip_bounds =
                Some([0.0, 0.0, properties.size[0], properties.size[1]]);
        }
        let local_transform = properties.local_transform();
        let global_transform = local_transform * parent;
        let mut snapshots = Vec::new();
        snapshots
            .try_reserve_exact(children.len())
            .map_err(|_| Error::Capacity)?;
        for child in children {
            snapshots.push(child.snapshot_core(global_transform, depth + 1, pages)?);
        }
        if self.versions() != versions {
            return Err(Error::InvalidInput);
        }
        Ok(VisualSnapshot {
            identity,
            versions,
            properties,
            local_transform,
            global_transform,
            page,
            effect,
            opacity_mask,
            children: snapshots,
            topmost_child_count: topmost,
        })
    }
}

struct SnapshotPreparation(Visual);
impl Drop for SnapshotPreparation {
    fn drop(&mut self) {
        self.0.0.borrow_mut().snapshot_preparing = false;
    }
}
