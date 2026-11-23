//! Actual attachment initialization history, separate from scene/source opacity.
use super::*;
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum NativeTargetLoad {
    Load,
    Clear(wire::Color),
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeDepthLoad {
    Load,
    ClearOne,
}
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeTargetPass {
    pub target: NativeLayerTargetId,
    pub color: NativeTargetLoad,
    pub depth: Option<NativeDepthLoad>,
    pub store_color: bool,
    pub store_depth: bool,
    pub ignores_alpha: bool,
    pub root_viewport: Option<[u32; 2]>,
}
/// Working replay state, discarded with the encoder on failure. This does not
/// infer opacity from a clear or publish cache content before submission.
pub struct NativeLayerPassState {
    size: [u32; 2],
    clear: wire::Color,
    root_color: bool,
    root_depth: bool,
    root_viewport: bool,
    slot_sizes: [Option<[u32; 2]>; LAYER_SLOT_COUNT as usize],
    color: [bool; LAYER_SLOT_COUNT as usize],
    depth: [bool; LAYER_SLOT_COUNT as usize],
    opaque: [bool; LAYER_SLOT_COUNT as usize],
}
impl NativeLayerPassState {
    pub fn new(
        plan: &NativeLayerTargetPlan,
        preserve_root: bool,
        certified_root_viewport: bool,
    ) -> Self {
        let mut slot_sizes = [None; LAYER_SLOT_COUNT as usize];
        for request in plan.requests() {
            if let NativeLayerTargetId::Slot(i) = request.target {
                slot_sizes[i as usize] = Some(request.size);
            }
        }
        Self {
            size: [plan.frame.width, plan.frame.height],
            clear: plan.frame.clear_color,
            root_color: preserve_root,
            root_depth: false,
            root_viewport: certified_root_viewport,
            slot_sizes,
            color: [false; LAYER_SLOT_COUNT as usize],
            depth: [false; LAYER_SLOT_COUNT as usize],
            opaque: [false; LAYER_SLOT_COUNT as usize],
        }
    }
    pub fn begin_root(&mut self, uses_depth: bool) -> NativeTargetPass {
        let color = if self.root_color {
            NativeTargetLoad::Load
        } else {
            NativeTargetLoad::Clear(self.clear)
        };
        let depth = uses_depth.then_some(if self.root_depth {
            NativeDepthLoad::Load
        } else {
            NativeDepthLoad::ClearOne
        });
        self.root_color = true;
        self.root_depth |= uses_depth;
        NativeTargetPass {
            target: NativeLayerTargetId::Root,
            color,
            depth,
            store_color: true,
            store_depth: uses_depth,
            ignores_alpha: false,
            root_viewport: self.root_viewport.then_some(self.size),
        }
    }
    /// A true background flag denotes the actual preceding background capture,
    /// including alpha-forcing/effect work, not merely a layer descriptor bit.
    pub fn begin_layer(
        &mut self,
        slot: u32,
        ignore_alpha: bool,
        background_initialized: bool,
    ) -> Result<NativeTargetPass, Error> {
        let i = self.slot_index(slot)?;
        self.depth[i] = false;
        self.opaque[i] = ignore_alpha;
        self.color[i] = true;
        Ok(NativeTargetPass {
            target: NativeLayerTargetId::Slot(slot),
            color: if background_initialized {
                NativeTargetLoad::Load
            } else {
                NativeTargetLoad::Clear(wire::Color {
                    r: 0.0,
                    g: 0.0,
                    b: 0.0,
                    a: if ignore_alpha { 1.0 } else { 0.0 },
                })
            },
            depth: None,
            store_color: true,
            store_depth: false,
            ignores_alpha: ignore_alpha,
            root_viewport: None,
        })
    }
    /// A source cache hit skips content; the original stored alpha policy remains
    /// explicit metadata on its actual slot and is never inferred from pixels.
    pub fn retain_cached_layer(&mut self, slot: u32, ignore_alpha: bool) -> Result<(), Error> {
        let i = self.slot_index(slot)?;
        self.color[i] = true;
        self.opaque[i] = ignore_alpha;
        Ok(())
    }
    pub fn resume(
        &mut self,
        target: NativeLayerTargetId,
        uses_depth: bool,
    ) -> Result<NativeTargetPass, Error> {
        if target == NativeLayerTargetId::Root {
            return Ok(self.begin_root(uses_depth));
        }
        let NativeLayerTargetId::Slot(slot) = target else {
            unreachable!()
        };
        let i = self.slot_index(slot)?;
        if !self.color[i] {
            return Err(Error::InvalidInput);
        }
        let depth = uses_depth.then_some(if self.depth[i] {
            NativeDepthLoad::Load
        } else {
            NativeDepthLoad::ClearOne
        });
        self.depth[i] |= uses_depth;
        Ok(NativeTargetPass {
            target,
            color: NativeTargetLoad::Load,
            depth,
            store_color: true,
            store_depth: uses_depth,
            ignores_alpha: self.opaque[i],
            root_viewport: None,
        })
    }
    /// Target Clear admission compares the actual selected attachment allocation,
    /// not the logical source extent or an enclosing target's alpha state.
    pub fn admit_target_clear(
        &self,
        target: NativeLayerTargetId,
        size: [u32; 2],
        ignores_alpha: bool,
    ) -> Result<(), Error> {
        let (actual, opaque) = match target {
            NativeLayerTargetId::Root => (self.size, false),
            NativeLayerTargetId::Slot(slot) => {
                let i = self.slot_index(slot)?;
                (
                    self.slot_sizes[i].ok_or(Error::InvalidInput)?,
                    self.opaque[i],
                )
            }
        };
        if size != actual || ignores_alpha != opaque {
            return Err(Error::ResourceDomain);
        }
        Ok(())
    }
    fn slot_index(&self, index: u32) -> Result<usize, Error> {
        let i = index as usize;
        if self.slot_sizes.get(i).is_none_or(Option::is_none) {
            Err(Error::InvalidInput)
        } else {
            Ok(i)
        }
    }
}
#[cfg(feature = "gpu")]
impl NativeTargetPass {
    pub fn color_operations(&self) -> wgpu::Operations<wgpu::Color> {
        wgpu::Operations {
            load: match self.color {
                NativeTargetLoad::Load => wgpu::LoadOp::Load,
                NativeTargetLoad::Clear(c) => wgpu::LoadOp::Clear(wgpu::Color {
                    r: f64::from(c.r),
                    g: f64::from(c.g),
                    b: f64::from(c.b),
                    a: f64::from(c.a),
                }),
            },
            store: wgpu::StoreOp::Store,
        }
    }
    pub fn depth_operations(&self) -> Option<wgpu::Operations<f32>> {
        self.depth.map(|d| wgpu::Operations {
            load: match d {
                NativeDepthLoad::Load => wgpu::LoadOp::Load,
                NativeDepthLoad::ClearOne => wgpu::LoadOp::Clear(1.0),
            },
            store: wgpu::StoreOp::Store,
        })
    }
}
