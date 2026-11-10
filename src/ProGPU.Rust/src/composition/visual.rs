//! Owned retained visual trees from the original scene and composition layers.
//!
//! Handles are deliberately thread local. Parents and resource subscribers are
//! weak; immutable command pages and snapshots retain their actual generations.
//! Snapshot creation is a preparation boundary, not a callback during GPU draw.
use crate::{Error, scene::Scene, vector::Matrix4};
use std::{
    cell::RefCell,
    rc::{Rc, Weak},
    sync::Arc,
};

mod animation;
mod properties;
mod property_set;
mod resources;
mod shapes;
mod snapshot;
pub use animation::{AnimationProperty, VisualAnimation};
pub use properties::*;
pub use property_set::*;
pub use resources::*;
pub use shapes::*;
pub use snapshot::*;

/// Stable owned identity. Keeping this value alive prevents identity reuse.
#[derive(Clone, Debug)]
pub struct VisualIdentity(Rc<()>);
impl PartialEq for VisualIdentity {
    fn eq(&self, other: &Self) -> bool {
        Rc::ptr_eq(&self.0, &other.0)
    }
}
impl Eq for VisualIdentity {}

#[derive(Default)]
struct Domain;

/// Local composition ownership domain. Resource/tree mixing across domains is
/// rejected before detaching a child or changing an existing resource binding.
#[derive(Clone, Default)]
pub struct VisualCompositor(Rc<Domain>);
impl VisualCompositor {
    /// Create an independent composition domain.
    pub fn new() -> Self {
        Self::default()
    }
    /// Original base scene visual, with raw scene-property admission.
    pub fn create_visual(&self) -> Visual {
        Visual::create(self.0.clone(), false, Content::Empty, false)
    }
    /// Original scene container, including ordinary/topmost child partitions.
    pub fn create_container_visual(&self) -> ContainerVisual {
        ContainerVisual(Visual::create(self.0.clone(), true, Content::Empty, false))
    }
    /// Retain an immutable local drawing without copying its command storage.
    pub fn create_drawing_visual(&self, scene: Arc<Scene>) -> DrawingVisual {
        DrawingVisual(Visual::create(
            self.0.clone(),
            false,
            Content::Drawing(scene),
            false,
        ))
    }
    /// Composition sprite, with finite wrapper properties and relative layout.
    pub fn create_sprite_visual(&self) -> SpriteVisual {
        SpriteVisual(Visual::create(
            self.0.clone(),
            true,
            Content::Sprite(None),
            true,
        ))
    }
    /// Composition shape visual, clipped to its effective local size.
    pub fn create_shape_visual(&self) -> ShapeVisual {
        ShapeVisual(Visual::create(
            self.0.clone(),
            true,
            Content::Shapes {
                shapes: Vec::new(),
                view_box: None,
            },
            true,
        ))
    }
    /// Composition container with the original anchor/quaternion/relative frame.
    pub fn create_composition_container(&self) -> ContainerVisual {
        ContainerVisual(Visual::create(self.0.clone(), true, Content::Empty, true))
    }
    /// Original externally owned command-cache contract. The producer must
    /// invalidate the visual after changing commands or referenced resources.
    pub fn create_command_visual(&self, source: Rc<dyn OwnedRenderCommandSource>) -> Visual {
        Visual::create(self.0.clone(), false, Content::Source(source), false)
    }
}

#[derive(Clone)]
enum Content {
    Empty,
    Drawing(Arc<Scene>),
    Source(Rc<dyn OwnedRenderCommandSource>),
    Sprite(Option<CompositionBrush>),
    Shapes {
        shapes: Vec<CompositionShape>,
        view_box: Option<CompositionViewBox>,
    },
}
struct Node {
    identity: VisualIdentity,
    domain: Rc<Domain>,
    container: bool,
    parent: Weak<RefCell<Node>>,
    children: Vec<Visual>,
    topmost_count: usize,
    properties: VisualProperties,
    composition: Option<CompositionVisualProperties>,
    parent_size: [f32; 2],
    content: Content,
    effect: Option<CompositionEffect>,
    opacity_mask: Option<CompositionBrush>,
    page: Option<VisualPage>,
    versions: VisualVersions,
    dirty: bool,
    animations: Vec<(AnimationProperty, VisualAnimation)>,
    active_subtree: usize,
    custom_animation: Option<Rc<RefCell<dyn FnMut(f32)>>>,
    property_set: Option<CompositionPropertySet>,
    snapshot_preparing: bool,
}

/// One retained visual. Cloning shares node identity; snapshots freeze state
/// and local command generations independently from subsequent mutations.
#[derive(Clone)]
pub struct Visual(Rc<RefCell<Node>>);
/// Weak visual reference for control callbacks and external observers. Capturing
/// this handle in an expression avoids a strong owner/callback cycle.
#[derive(Clone, Default)]
pub struct WeakVisual(Weak<RefCell<Node>>);
impl WeakVisual {
    /// Retain the exact original node only while it is still alive.
    pub fn upgrade(&self) -> Option<Visual> {
        self.0.upgrade().map(Visual)
    }
}
impl std::fmt::Debug for Visual {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let node = self.0.borrow();
        f.debug_struct("Visual")
            .field("identity", &node.identity)
            .field("versions", &node.versions)
            .field("children", &node.children.len())
            .finish()
    }
}
impl PartialEq for Visual {
    fn eq(&self, other: &Self) -> bool {
        Rc::ptr_eq(&self.0, &other.0)
    }
}
impl Eq for Visual {}
impl Visual {
    fn create(domain: Rc<Domain>, container: bool, content: Content, composition: bool) -> Self {
        let mut properties = VisualProperties::default();
        if composition {
            properties.render_transform_origin = [0.0; 2];
        }
        Self(Rc::new(RefCell::new(Node {
            identity: VisualIdentity(Rc::new(())),
            domain,
            container,
            parent: Weak::new(),
            children: Vec::new(),
            topmost_count: 0,
            properties,
            composition: composition.then(CompositionVisualProperties::default),
            parent_size: [0.0; 2],
            content,
            effect: None,
            opacity_mask: None,
            page: None,
            versions: VisualVersions::default(),
            dirty: true,
            animations: Vec::new(),
            active_subtree: 0,
            custom_animation: None,
            property_set: None,
            snapshot_preparing: false,
        })))
    }
    /// Stable generation identity, independent of current tree membership.
    pub fn identity(&self) -> VisualIdentity {
        self.0.borrow().identity.clone()
    }
    /// Borrow this node's lifetime without retaining its complete subtree.
    pub fn downgrade(&self) -> WeakVisual {
        WeakVisual(Rc::downgrade(&self.0))
    }
    /// Weak parent relationship; detached or dropped parents return None.
    pub fn parent(&self) -> Option<Visual> {
        self.0.borrow().parent.upgrade().map(Self)
    }
    /// Painter-ordered child handles; overlays occupy the final partition.
    pub fn children(&self) -> Vec<Visual> {
        self.0.borrow().children.clone()
    }
    /// Count of explicitly topmost children.
    pub fn topmost_child_count(&self) -> usize {
        self.0.borrow().topmost_count
    }
    /// Independent original state/content/tree revision counters.
    pub fn versions(&self) -> VisualVersions {
        self.0.borrow().versions
    }
    /// Whether local or descendant invalidation has reached this node.
    pub fn is_dirty(&self) -> bool {
        self.0.borrow().dirty
    }
    /// Clear only the original dirty flag, without changing any revision.
    pub fn mark_clean(&self) {
        self.0.borrow_mut().dirty = false;
    }
    /// Original scene properties, including cold state and independent clips.
    pub fn properties(&self) -> VisualProperties {
        self.0.borrow().properties.clone()
    }
    /// Apply raw scene properties. Only a size change invalidates local content;
    /// presentation changes propagate state invalidation to ancestors.
    pub fn set_properties(&self, properties: VisualProperties) -> Result<(), Error> {
        if properties
            .composite
            .effect_source_translation
            .is_some_and(|p| !p.iter().all(|v| v.is_finite()))
        {
            return Err(Error::InvalidInput);
        }
        let properties = properties.normalized_cache_scale();
        let (changed, size_changed) = {
            let mut node = self.0.borrow_mut();
            if node.composition.is_some() {
                return Err(Error::UnsupportedExecution);
            }
            let changed = node.properties != properties;
            let size_changed = node.properties.size != properties.size;
            if node.properties.local_render_bounds != properties.local_render_bounds {
                node.page = None;
            }
            node.properties = properties;
            (changed, size_changed)
        };
        if changed {
            self.invalidate_core(size_changed);
        }
        if size_changed {
            self.notify_parent_size();
        }
        Ok(())
    }
    /// Composition wrapper values, distinct from raw Scene.Visual semantics.
    pub fn composition_properties(&self) -> Option<CompositionVisualProperties> {
        self.0.borrow().composition
    }
    /// Apply finite composition properties and recompute the exact ordered
    /// center/scale/axis-rotation/orientation/transform/position expression.
    pub fn set_composition_properties(
        &self,
        properties: CompositionVisualProperties,
    ) -> Result<(), Error> {
        let properties = properties.normalized()?;
        {
            let mut node = self.0.borrow_mut();
            let Some(current) = &mut node.composition else {
                return Err(Error::UnsupportedExecution);
            };
            if *current == properties {
                return Ok(());
            }
            *current = properties;
        }
        self.refresh_composition();
        Ok(())
    }
    /// Edit independent retained clips/cache/effect frame properties on either
    /// visual family, while preserving its placement and layout policy.
    pub fn set_composite_state(&self, state: VisualCompositeState) -> Result<(), Error> {
        if state
            .effect_source_translation
            .is_some_and(|p| !p.iter().all(|v| v.is_finite()))
        {
            return Err(Error::InvalidInput);
        }
        let state = state.normalized();
        let changed = {
            let mut node = self.0.borrow_mut();
            if node.properties.composite == state {
                false
            } else {
                node.properties.composite = state;
                true
            }
        };
        if changed {
            self.invalidate_core(false);
        }
        Ok(())
    }
    /// Invalidate original local content and propagate state-only invalidation.
    pub fn invalidate(&self) {
        self.invalidate_core(true);
    }
    /// Invalidate presentation without discarding immutable local command pages.
    pub fn invalidate_visual_state(&self) {
        self.invalidate_core(false);
    }
    fn invalidate_core(&self, content: bool) {
        let mut current = Some(self.clone());
        let mut local = content;
        while let Some(visual) = current {
            let mut node = visual.0.borrow_mut();
            increment(&mut node.versions.change);
            if local {
                increment(&mut node.versions.content);
                node.page = None;
            }
            node.dirty = true;
            current = node.parent.upgrade().map(Self);
            local = false;
        }
    }
    fn invalidate_tree(&self) {
        let mut current = Some(self.clone());
        while let Some(visual) = current {
            let mut node = visual.0.borrow_mut();
            node.versions.tree = node.versions.tree.wrapping_add(1);
            current = node.parent.upgrade().map(Self);
        }
    }
    fn ensure_child(&self, child: &Visual) -> Result<(), Error> {
        {
            let node = self.0.borrow();
            if !node.container {
                return Err(Error::UnsupportedExecution);
            }
            if !Rc::ptr_eq(&node.domain, &child.0.borrow().domain) {
                return Err(Error::ResourceDomain);
            }
        }
        let mut ancestor = Some(self.clone());
        while let Some(node) = ancestor {
            if node == *child {
                return Err(Error::InvalidInput);
            }
            ancestor = node.parent();
        }
        Ok(())
    }
    /// Append below existing overlays, moving from the actual former parent.
    pub fn add_child(&self, child: Visual) -> Result<(), Error> {
        self.insert_core(child, None, false)
    }
    /// Clamp a source index within the ordinary partition after detaching.
    pub fn insert_child(&self, index: isize, child: Visual) -> Result<(), Error> {
        self.insert_core(child, Some(index), false)
    }
    /// Append an overlay above both ordinary and existing topmost children.
    pub fn add_topmost_child(&self, child: Visual) -> Result<(), Error> {
        self.insert_core(child, None, true)
    }
    fn insert_core(&self, child: Visual, index: Option<isize>, topmost: bool) -> Result<(), Error> {
        self.ensure_child(&child)?;
        self.0
            .borrow_mut()
            .children
            .try_reserve(1)
            .map_err(|_| Error::Capacity)?;
        if let Some(parent) = child.parent() {
            parent.remove_child(&child);
        }
        let activity = child.0.borrow().active_subtree;
        child.0.borrow_mut().parent = Rc::downgrade(&self.0);
        {
            let mut node = self.0.borrow_mut();
            let ordinary = node.children.len() - node.topmost_count;
            let index = if topmost {
                node.topmost_count += 1;
                node.children.len()
            } else {
                index.map_or(ordinary, |v| v.max(0) as usize).min(ordinary)
            };
            node.children.insert(index, child.clone());
        }
        self.adjust_activity(activity as isize);
        let parent_size = self.0.borrow().properties.size;
        child.set_parent_size(parent_size);
        self.invalidate_tree();
        self.invalidate();
        Ok(())
    }
    /// Remove only this exact child identity; an unrelated handle is a no-op.
    pub fn remove_child(&self, child: &Visual) -> bool {
        let removed = {
            let mut node = self.0.borrow_mut();
            let Some(index) = node.children.iter().position(|v| v == child) else {
                return false;
            };
            if index >= node.children.len() - node.topmost_count {
                node.topmost_count -= 1;
            }
            node.children.remove(index)
        };
        let activity = removed.0.borrow().active_subtree;
        removed.0.borrow_mut().parent = Weak::new();
        self.adjust_activity(-(activity as isize));
        removed.set_parent_size([0.0; 2]);
        self.invalidate_tree();
        self.invalidate();
        true
    }
    /// Detach all original children; clear always invalidates the tree/content.
    pub fn clear_children(&self) {
        let children = {
            let mut node = self.0.borrow_mut();
            node.topmost_count = 0;
            std::mem::take(&mut node.children)
        };
        for child in children {
            let activity = child.0.borrow().active_subtree;
            child.0.borrow_mut().parent = Weak::new();
            self.adjust_activity(-(activity as isize));
            child.set_parent_size([0.0; 2]);
        }
        self.invalidate_tree();
        self.invalidate();
    }
    /// Reorder within the child's current partition without changing parent.
    pub fn bring_child_to_front(&self, child: &Visual) {
        let changed = {
            let mut node = self.0.borrow_mut();
            let Some(index) = node.children.iter().position(|v| v == child) else {
                return;
            };
            let start = node.children.len() - node.topmost_count;
            let target = if index >= start {
                node.children.len() - 1
            } else {
                start - 1
            };
            if index == target {
                false
            } else {
                let item = node.children.remove(index);
                node.children.insert(target, item);
                true
            }
        };
        if changed {
            self.invalidate_tree();
            self.invalidate();
        }
    }
    /// Original composition collection relative insertion adjusts the index
    /// when moving an existing earlier sibling. Overlay APIs remain separate.
    pub fn insert_above(&self, child: Visual, sibling: &Visual) -> Result<(), Error> {
        self.insert_relative(child, sibling, true)
    }
    /// Insert immediately below an existing ordinary sibling.
    pub fn insert_below(&self, child: Visual, sibling: &Visual) -> Result<(), Error> {
        self.insert_relative(child, sibling, false)
    }
    fn insert_relative(&self, child: Visual, sibling: &Visual, above: bool) -> Result<(), Error> {
        if child == *sibling {
            return Ok(());
        }
        self.ensure_child(&child)?;
        let index = {
            let node = self.0.borrow();
            let sibling = node
                .children
                .iter()
                .position(|v| v == sibling)
                .ok_or(Error::InvalidInput)?;
            if sibling >= node.children.len() - node.topmost_count {
                return Err(Error::InvalidInput);
            }
            let mut index = sibling + usize::from(above);
            if node
                .children
                .iter()
                .position(|v| v == &child)
                .is_some_and(|old| old < index)
            {
                index -= 1;
            }
            index
        };
        self.insert_child(index as isize, child)
    }
    fn set_parent_size(&self, size: [f32; 2]) {
        let changed = {
            let mut node = self.0.borrow_mut();
            if node.parent_size == size {
                false
            } else {
                node.parent_size = size;
                node.composition.is_some()
            }
        };
        if changed {
            self.refresh_composition();
        }
    }
    fn notify_parent_size(&self) {
        let (size, children) = {
            let n = self.0.borrow();
            (n.properties.size, n.children.clone())
        };
        for child in children {
            child.set_parent_size(size);
        }
    }
    fn refresh_composition(&self) {
        let (changed, size_changed) = {
            let mut node = self.0.borrow_mut();
            let Some(value) = node.composition else {
                return;
            };
            let (size, transform) = value.resolve(node.parent_size);
            let size_changed = node.properties.size != size;
            let changed = size_changed
                || node.properties.transform != transform
                || node.properties.opacity != value.opacity
                || node.properties.visible != value.visible;
            node.properties.offset = [0.0; 2];
            node.properties.scale = [1.0; 3];
            node.properties.rotation = 0.0;
            node.properties.center_point = [0.0; 3];
            node.properties.render_transform_origin = [0.0; 2];
            node.properties.size = size;
            node.properties.transform = transform;
            node.properties.opacity = value.opacity;
            node.properties.visible = value.visible;
            (changed, size_changed)
        };
        if changed {
            self.invalidate_core(size_changed);
        }
        if size_changed {
            self.notify_parent_size();
        }
    }
    /// Actual complete original local transform, including the anchor fallback.
    pub fn local_transform(&self) -> Matrix4 {
        self.0.borrow().properties.local_transform()
    }
    /// Compose local first, then every actual parent in source row-vector order.
    pub fn global_transform(&self) -> Matrix4 {
        let mut transforms = vec![self.local_transform()];
        let mut parent = self.parent();
        while let Some(node) = parent {
            transforms.push(node.local_transform());
            parent = node.parent();
        }
        let mut transform = transforms.pop().expect("actual visual local transform");
        while let Some(local) = transforms.pop() {
            transform = local * transform;
        }
        transform
    }
    /// Coordinate-frame metadata remains independent of painted geometry.
    pub fn global_coordinate_transform(&self) -> Matrix4 {
        self.0.borrow().properties.coordinate_frame * self.global_transform()
    }
    /// Replace an effect owner, preserving old snapshot generations.
    pub fn set_effect(&self, effect: Option<CompositionEffect>) -> Result<(), Error> {
        self.replace_resource(effect, |node| &mut node.effect, false)
    }
    /// Replace an opacity-brush owner without treating alpha as hit geometry.
    pub fn set_opacity_mask(&self, brush: Option<CompositionBrush>) -> Result<(), Error> {
        self.replace_resource(brush, |node| &mut node.opacity_mask, false)
    }
    fn replace_resource<T: Clone>(
        &self,
        value: Option<CompositionResource<T>>,
        field: impl Fn(&mut Node) -> &mut Option<CompositionResource<T>>,
        content: bool,
    ) -> Result<(), Error> {
        if let Some(value) = &value {
            value.ensure_domain(&self.0.borrow().domain)?;
        }
        let old = {
            let mut node = self.0.borrow_mut();
            let current = field(&mut node);
            if same_resource(current.as_ref(), value.as_ref()) {
                return Ok(());
            }
            std::mem::replace(current, value.clone())
        };
        let owner = Subscriber::Visual(Rc::downgrade(&self.0));
        if let Some(old) = old {
            old.remove_owner(&owner);
        }
        if let Some(value) = value {
            value.add_owner(owner);
        }
        self.invalidate_core(content);
        Ok(())
    }
}

fn increment(value: &mut i64) {
    *value = value.wrapping_add(1);
    if *value < 0 {
        *value = 1;
    }
}

macro_rules! visual_handle {
    ($name:ident, $doc:literal) => {
        #[doc = $doc]
        #[derive(Clone, Debug)]
        pub struct $name(Visual);
        impl std::ops::Deref for $name {
            type Target = Visual;
            fn deref(&self) -> &Visual {
                &self.0
            }
        }
        impl From<$name> for Visual {
            fn from(value: $name) -> Self {
                value.0
            }
        }
        impl $name {
            /// Share the actual node as a base visual handle.
            pub fn visual(&self) -> Visual {
                self.0.clone()
            }
        }
    };
}
visual_handle!(
    ContainerVisual,
    "Original ordered visual container with weak parent ownership."
);
visual_handle!(
    DrawingVisual,
    "Visual retaining an immutable local Scene generation."
);
visual_handle!(
    SpriteVisual,
    "Composition rectangle content with a shared owned brush."
);
visual_handle!(
    ShapeVisual,
    "Composition shape collection with an independent view-box mapping."
);

impl DrawingVisual {
    /// Publish a new immutable local drawing and invalidate only its actual owners.
    pub fn set_scene(&self, scene: Arc<Scene>) {
        let changed = {
            let mut node = self.0.0.borrow_mut();
            let Content::Drawing(current) = &mut node.content else {
                unreachable!()
            };
            if Arc::ptr_eq(current, &scene) {
                false
            } else {
                *current = scene;
                true
            }
        };
        if changed {
            self.invalidate();
        }
    }
}
impl SpriteVisual {
    /// Bind the brush's actual resource identity, not a detached color snapshot.
    pub fn set_brush(&self, brush: Option<CompositionBrush>) -> Result<(), Error> {
        if let Some(brush) = &brush {
            brush.ensure_domain(&self.0.0.borrow().domain)?;
        }
        let old = {
            let mut node = self.0.0.borrow_mut();
            let Content::Sprite(current) = &mut node.content else {
                unreachable!()
            };
            if same_resource(current.as_ref(), brush.as_ref()) {
                return Ok(());
            }
            std::mem::replace(current, brush.clone())
        };
        let owner = Subscriber::Visual(Rc::downgrade(&self.0.0));
        if let Some(old) = old {
            old.remove_owner(&owner);
        }
        if let Some(brush) = brush {
            brush.add_owner(owner);
        }
        self.invalidate();
        Ok(())
    }
}
