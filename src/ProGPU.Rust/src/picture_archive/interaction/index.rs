//! Original bounded quadtree topology, preserving source primitive order.
use super::*;
use bytemuck::{Pod, Zeroable};
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct HitNode {
    pub bounds_min: [f32; 2],
    pub bounds_max: [f32; 2],
    pub first_child: u32,
    pub child_count: u32,
    pub first_primitive: u32,
    pub primitive_count: u32,
}
const _: () = assert!(size_of::<HitNode>() == 32);
/// Immutable source input cache. Its exact primitive/path payload is suitable for
/// the original query kernel; node envelopes are never precise hit results.
#[derive(Clone, Debug)]
pub struct ArchiveHitIndex {
    pub(super) primitives: Arc<[HitPrimitive]>,
    pub(super) segments: Arc<[crate::path::Segment]>,
    nodes: Arc<[HitNode]>,
    indices: Arc<[u32]>,
    pub(super) pictures: Arc<[Arc<ArchivePicture>]>,
    pub(super) commands: Arc<[Arc<ArchiveCommand>]>,
    captured: Arc<[Arc<CapturedHitCommand>]>,
    source_visuals: Arc<[Arc<SourceVisualSnapshot>]>,
    source_lists: Arc<[Arc<SourceCommandList>]>,
    source_glyphs: Arc<[Arc<SourceGlyphInput>]>,
    native_scene: Option<crate::native_scene::NativeScene>,
}
impl ArchiveHitIndex {
    pub fn bounds(&self) -> [f32; 4] {
        let root = self.nodes[0];
        [
            root.bounds_min[0],
            root.bounds_min[1],
            root.bounds_max[0],
            root.bounds_max[1],
        ]
    }
    pub fn primitives(&self) -> &[HitPrimitive] {
        &self.primitives
    }
    pub fn path_segments(&self) -> &[crate::path::Segment] {
        &self.segments
    }
    pub fn nodes(&self) -> &[HitNode] {
        &self.nodes
    }
    pub fn primitive_indices(&self) -> &[u32] {
        &self.indices
    }
    pub fn picture_owners(&self) -> &[Arc<ArchivePicture>] {
        &self.pictures
    }
    pub fn command_owners(&self) -> &[Arc<ArchiveCommand>] {
        &self.commands
    }
    /// Build the original quadtree from caller-owned raw primitive/path arrays.
    /// Unknown raw kinds and flags remain untouched for the original query
    /// kernel's independent admission. No source-command owners are fabricated.
    pub fn from_primitives(
        primitives: Arc<[HitPrimitive]>,
        segments: Arc<[crate::path::Segment]>,
        max_depth: u32,
        max_per_node: u32,
    ) -> Result<Self> {
        if max_per_node == 0 {
            return Err(ArchiveError::InvalidData);
        }
        let mut tree = Tree {
            primitives: &primitives,
            nodes: Vec::new(),
            indices: Vec::new(),
            max_depth,
            max_per_node,
        };
        push(&mut tree.nodes, HitNode::default())?;
        if !primitives.is_empty() {
            let mut b = primitive_bounds(primitives[0]);
            let mut indices = Vec::new();
            reserve(&mut indices, primitives.len())?;
            for (i, p) in primitives.iter().enumerate() {
                b = union(b, primitive_bounds(*p));
                indices.push(u32::try_from(i).map_err(|_| ArchiveError::Capacity)?);
            }
            tree.fill(0, b, &indices, 0)?;
        }
        let nodes = tree.nodes.into();
        let indices = tree.indices.into();
        Ok(Self {
            primitives,
            segments,
            nodes,
            indices,
            pictures: Arc::from([]),
            commands: Arc::from([]),
            captured: Arc::from([]),
            source_visuals: Arc::from([]),
            source_lists: Arc::from([]),
            source_glyphs: Arc::from([]),
            native_scene: None,
        })
    }
    pub(super) fn build(
        cache: &ArchiveHitTestCache,
        max_depth: u32,
        max_per_node: u32,
    ) -> Result<Self> {
        let mut result = Self::from_primitives(
            copy(&cache.primitives)?.into(),
            copy(&cache.segments)?.into(),
            max_depth,
            max_per_node,
        )?;
        result.pictures = copy(&cache.pictures)?.into();
        result.commands = copy(&cache.commands)?.into();
        result.captured = copy(&cache.captured)?.into();
        result.source_visuals = copy(&cache.source_visuals)?.into();
        result.source_lists = copy(&cache.source_lists)?.into();
        result.source_glyphs = copy(&cache.source_glyphs)?.into();
        Ok(result)
    }
    pub fn source_visual_owners(&self) -> &[Arc<SourceVisualSnapshot>] {
        &self.source_visuals
    }
    pub fn source_command_owners(&self) -> &[Arc<SourceCommandList>] {
        &self.source_lists
    }
    pub fn source_glyph_owners(&self) -> &[Arc<SourceGlyphInput>] {
        &self.source_glyphs
    }
    pub fn captured_command_owners(&self) -> &[Arc<CapturedHitCommand>] {
        &self.captured
    }

    /// Actual admitted native stream generation, retained by native query
    /// imports through GPU submissions and pending query readbacks.
    pub fn native_scene_owner(&self) -> Option<&crate::native_scene::NativeScene> {
        self.native_scene.as_ref()
    }

    /// The caller owns an already admitted native hit page. Preserve its exact
    /// topology and ordered references; rebuilding the quadtree would change
    /// the original GPU query traversal order.
    pub(crate) fn from_native_scene(
        scene: crate::native_scene::NativeScene,
        primitives: Arc<[HitPrimitive]>,
        segments: Arc<[crate::path::Segment]>,
        nodes: Arc<[HitNode]>,
        indices: Arc<[u32]>,
    ) -> Self {
        Self {
            primitives,
            segments,
            nodes,
            indices,
            pictures: Arc::from([]),
            commands: Arc::from([]),
            captured: Arc::from([]),
            source_visuals: Arc::from([]),
            source_lists: Arc::from([]),
            source_glyphs: Arc::from([]),
            native_scene: Some(scene),
        }
    }
}
struct Tree<'a> {
    primitives: &'a [HitPrimitive],
    nodes: Vec<HitNode>,
    indices: Vec<u32>,
    max_depth: u32,
    max_per_node: u32,
}
impl Tree<'_> {
    fn fill(&mut self, index: usize, b: [f32; 4], values: &[u32], depth: u32) -> Result<()> {
        if depth >= self.max_depth
            || values.len() <= self.max_per_node as usize
            || [b[0], b[1]] == [b[2], b[3]]
        {
            return self.leaf(index, b, values);
        }
        let center = [(b[0] + b[2]) * 0.5, (b[1] + b[3]) * 0.5];
        let mut retained = Vec::new();
        let mut children: [Vec<u32>; 4] = std::array::from_fn(|_| Vec::new());
        for &value in values {
            let p = self.primitives[value as usize];
            let left = p.bounds_max[0] <= center[0];
            let right = p.bounds_min[0] >= center[0];
            let top = p.bounds_max[1] <= center[1];
            let bottom = p.bounds_min[1] >= center[1];
            let child = if top && left {
                Some(0)
            } else if top && right {
                Some(1)
            } else if bottom && left {
                Some(2)
            } else if bottom && right {
                Some(3)
            } else {
                None
            };
            if let Some(child) = child {
                push(&mut children[child], value)?;
            } else {
                push(&mut retained, value)?;
            }
        }
        let count = children.iter().filter(|c| !c.is_empty()).count();
        if count == 0 || (count == 1 && retained.is_empty()) {
            return self.leaf(index, b, values);
        }
        let first = u32::try_from(self.indices.len()).map_err(|_| ArchiveError::Capacity)?;
        reserve(&mut self.indices, retained.len())?;
        self.indices.extend_from_slice(&retained);
        let first_child = u32::try_from(self.nodes.len()).map_err(|_| ArchiveError::Capacity)?;
        let mut slots = [None; 4];
        for (i, c) in children.iter().enumerate() {
            if !c.is_empty() {
                slots[i] = Some(self.nodes.len());
                push(&mut self.nodes, HitNode::default())?;
            }
        }
        self.nodes[index] = HitNode {
            bounds_min: [b[0], b[1]],
            bounds_max: [b[2], b[3]],
            first_child,
            child_count: count as u32,
            first_primitive: first,
            primitive_count: retained.len() as u32,
        };
        let bounds = [
            [b[0], b[1], center[0], center[1]],
            [center[0], b[1], b[2], center[1]],
            [b[0], center[1], center[0], b[3]],
            [center[0], center[1], b[2], b[3]],
        ];
        for (i, c) in children.iter().enumerate() {
            if let Some(slot) = slots[i] {
                self.fill(slot, bounds[i], c, depth + 1)?;
            }
        }
        Ok(())
    }
    fn leaf(&mut self, index: usize, b: [f32; 4], values: &[u32]) -> Result<()> {
        let first = u32::try_from(self.indices.len()).map_err(|_| ArchiveError::Capacity)?;
        let count = u32::try_from(values.len()).map_err(|_| ArchiveError::Capacity)?;
        reserve(&mut self.indices, values.len())?;
        self.indices.extend_from_slice(values);
        self.nodes[index] = HitNode {
            bounds_min: [b[0], b[1]],
            bounds_max: [b[2], b[3]],
            first_primitive: first,
            primitive_count: count,
            ..Default::default()
        };
        Ok(())
    }
}
