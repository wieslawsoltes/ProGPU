//! Immutable source AA groups, with Clear propagation stopped by ordinary owners.
use super::*;

/// Exact unit-opacity identity-axis group. Ordinary layers retain their separate
/// opacity, mask, blend and effect policy; those properties are not AA flags.
#[derive(Clone, Debug)]
pub struct AxisClipDraw {
    scene: Arc<Scene>,
    bounds: [f32; 4],
    placed_edges: Option<[f32; 4]>,
}

impl AxisClipDraw {
    /// Original balanced source generation, retained without geometry/font work.
    pub fn scene(&self) -> &Scene {
        &self.scene
    }
    /// Original logical x/y/width/height, before physical allocation rounding.
    pub fn bounds(&self) -> [f32; 4] {
        self.bounds
    }
    /// Independent source edges; never reconstruct a placed far edge from width.
    pub(crate) fn bounds_edges(&self) -> [f32; 4] {
        self.placed_edges.unwrap_or_else(|| {
            let [x, y, w, h] = self.bounds;
            [x, y, x + w, y + h]
        })
    }
    pub(super) fn placed(&self, scene: Arc<Scene>, edges: [f32; 4]) -> Self {
        Self {
            scene,
            bounds: [edges[0], edges[1], edges[2] - edges[0], edges[3] - edges[1]],
            placed_edges: Some(edges),
        }
    }
    /// Whether recorded storage Clear occurs inward of this group's nearest
    /// ordinary owner. Physical empty/binary clips are resolved at preparation;
    /// Clear inside another ordinary layer does not propagate.
    pub fn initializes_background(&self) -> bool {
        self.scene.storage_clear
    }
}
impl Scene {
    /// Retain one balanced source AA subtree. Recording is O(1); complete
    /// historical child depth is checked before mutation. Binary scopes inherit
    /// through this boundary; source Clear marks only consecutive AA ancestors.
    pub fn draw_axis_clip(
        &mut self,
        bounds: [f32; 4],
        source: impl Into<Arc<Scene>>,
    ) -> Result<(), Error> {
        let source = source.into();
        let [x, y, w, h] = bounds;
        if source.scope_depth != 0
            || ![x, y, w, h, x + w, y + h].iter().all(|v| v.is_finite())
            || w < 0.0
            || h < 0.0
        {
            return Err(Error::InvalidInput);
        }
        let depth = source.layer_depth.checked_add(1).ok_or(Error::Capacity)?;
        if depth > 16 {
            return Err(Error::Capacity);
        }
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.storage_clear |= source.storage_clear;
        self.tree_text |= source.tree_text;
        self.has_axis = true;
        self.layer_depth = self.layer_depth.max(depth);
        self.commands.push(Command::AxisClip(Box::new(AxisClipDraw {
            scene: source,
            bounds,
            placed_edges: None,
        })));
        Ok(())
    }
    /// Direct source AA groups in original painter order.
    pub fn axis_clips(&self) -> impl Iterator<Item = &AxisClipDraw> + '_ {
        self.commands.iter().filter_map(|c| match c {
            Command::AxisClip(a) => Some(a.as_ref()),
            _ => None,
        })
    }
    /// Whether this complete retained tree contains source AA groups.
    pub fn has_axis_clips(&self) -> bool {
        self.has_axis
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn has_tree_text(&self) -> bool {
        self.tree_text
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn has_storage_clear(&self) -> bool {
        self.storage_clear
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn storage_visits(
        &self,
    ) -> impl Iterator<Item = (usize, Option<&AxisClipDraw>)> + '_ {
        self.commands
            .iter()
            .enumerate()
            .filter_map(|(i, c)| match c {
                Command::Clear(_) => Some((i, None)),
                Command::AxisClip(a) => Some((i, Some(a.as_ref()))),
                _ => None,
            })
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn with_binary_clip(&self, clip: Option<[u32; 4]>) -> Self {
        let mut result = self.clone();
        result.inherited_clip = clip;
        result
    }
}

#[cfg(feature = "gpu")]
pub(crate) enum GroupRef<'a> {
    Layer(&'a LayerDraw),
    Axis(&'a AxisClipDraw),
}
#[cfg(feature = "gpu")]
pub(crate) enum GroupOutput {
    Layer(Option<(Box<ImageDraw>, [u32; 4])>),
    Axis(Option<usize>),
}
#[cfg(feature = "gpu")]
impl Scene {
    pub(crate) fn groups(&self) -> impl Iterator<Item = (usize, GroupRef<'_>)> + '_ {
        self.commands
            .iter()
            .enumerate()
            .filter_map(|(i, c)| match c {
                Command::Layer(l) => Some((i, GroupRef::Layer(l))),
                Command::AxisClip(a) => Some((i, GroupRef::Axis(a))),
                _ => None,
            })
    }
    pub(crate) fn replace_groups(&self, outputs: Vec<GroupOutput>) -> Result<Self, Error> {
        let mut outputs = outputs.into_iter();
        let mut result = Self {
            has_scopes: self.has_scopes,
            inherited_clip: self.inherited_clip,
            ..Default::default()
        };
        result
            .commands
            .try_reserve_exact(self.commands.len())
            .map_err(|_| Error::Capacity)?;
        for c in &self.commands {
            result.commands.push(match c {
                Command::Layer(l) => match outputs.next().ok_or(Error::ResourceDomain)? {
                    GroupOutput::Layer(Some((image, bounds))) => {
                        Command::Image(image, l.blend, Some(bounds), None)
                    }
                    GroupOutput::Layer(None) => Command::Empty,
                    _ => return Err(Error::ResourceDomain),
                },
                Command::AxisClip(_) => match outputs.next().ok_or(Error::ResourceDomain)? {
                    GroupOutput::Axis(Some(index)) => Command::ResolvedAxis(index),
                    GroupOutput::Axis(None) => Command::Empty,
                    _ => return Err(Error::ResourceDomain),
                },
                _ => c.clone(),
            });
        }
        if outputs.next().is_some() {
            return Err(Error::ResourceDomain);
        }
        Ok(result)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn historical_depth_rejects_before_recording() {
        let mut child = Scene::default();
        for depth in 1..=16 {
            let mut parent = Scene::default();
            if depth % 2 == 0 {
                parent.draw_layer(LayerDraw::new(child)).unwrap();
            } else {
                parent
                    .draw_axis_clip([0.0, 0.0, 32.0, 32.0], child)
                    .unwrap();
            }
            assert_eq!(parent.layer_depth(), depth);
            child = parent;
        }
        let mut target = Scene::default();
        target.clear_target([0.0; 4]).unwrap();
        let count = target.commands.len();
        assert!(matches!(
            target.draw_axis_clip([0.0; 4], child),
            Err(Error::Capacity)
        ));
        assert_eq!(target.commands.len(), count);
        assert_eq!(target.layer_depth(), 0);
        assert!(!target.has_axis_clips());
    }
    #[test]
    fn unbalanced_and_nonfinite_sources_leave_recording_unchanged() {
        let mut child = Scene::default();
        child.push_clip_rect([1.0, 1.0, 10.0, 10.0]).unwrap();
        let mut target = Scene::default();
        assert!(matches!(
            target.draw_axis_clip([0.0; 4], child),
            Err(Error::InvalidInput)
        ));
        assert!(matches!(
            target.draw_axis_clip([f32::NAN, 0.0, 1.0, 1.0], Scene::default()),
            Err(Error::InvalidInput)
        ));
        assert!(target.commands.is_empty());
        assert_eq!(target.layer_depth(), 0);
    }
    #[test]
    fn explicit_group_retains_original_bits_and_source_generation() {
        let mut source = Scene::default();
        source.clear_target([0.0; 4]).unwrap();
        let frozen = Arc::new(source.clone());
        let mut target = Scene::default();
        let bounds = [-0.0, 0.25, 0.0, 1.0];
        target.draw_axis_clip(bounds, frozen.clone()).unwrap();
        source.clear();
        let retained = target.axis_clips().next().unwrap();
        assert_eq!(
            retained.bounds().map(f32::to_bits),
            bounds.map(f32::to_bits)
        );
        assert!(Arc::ptr_eq(&retained.scene, &frozen));
        assert!(retained.initializes_background());
    }
    #[test]
    fn empty_binary_clip_does_not_admit_direct_masked_aa() {
        let mut target = Scene::default();
        target.push_clip_rect([0.0; 4]).unwrap();
        target
            .push_mask(crate::mask::AnalyticMask::rectangle([0.0, 0.0, 8.0, 8.0]))
            .unwrap();
        target
            .draw_axis_clip([1.0, 1.0, 4.0, 4.0], Scene::default())
            .unwrap();
        target.pop_scope().unwrap();
        target.pop_scope().unwrap();
        assert!(matches!(
            target.compile_states(1.0),
            Err(Error::UnsupportedExecution)
        ));
    }
}
