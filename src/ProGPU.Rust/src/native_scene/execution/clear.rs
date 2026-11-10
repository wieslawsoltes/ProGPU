//! Original storage Clear packet and pixel-center clip, independent of paint.
use super::*;
use crate::native_scene::engine_resources::layers::{
    NativeLayerTargetId, NativeLayerTargetRequest,
};
use crate::scene::{ClearColor, TargetAlpha};

#[derive(Clone, Debug)]
pub struct NativeTargetClearOperation {
    frame: NativeCommandFrame,
    target: NativeLayerTargetId,
    size: [u32; 2],
    scissor: NativeScissor,
    color: ClearColor,
}
impl NativeTargetClearOperation {
    /// The source-order compiler supplies the actual assigned layer allocation.
    /// Logical target bounds may be smaller than the retained slot allocation.
    pub(crate) fn compile(
        frame: NativeCommandFrame,
        root: NativeSceneFrame,
        allocation: NativeLayerTargetRequest,
        partial_damage: Option<NativeScissor>,
    ) -> Result<Option<Self>, Error> {
        let command = frame.command();
        if command.record.kind != SCENE_COMMAND_CLEAR_TARGET
            || command.payload.len() != wire::Color::WIRE_SIZE
            || frame.source_state.flags & SCENE_STATE_MASK != 0
            || allocation.size.contains(&0)
        {
            return Err(Error::InvalidInput);
        }
        let c = wire::Color::read(command.payload).ok_or(Error::InvalidInput)?;
        let color = ClearColor::new([c.r, c.g, c.b, c.a])?;
        let state = frame.source_state;
        // Storage allocation remains outward rounded. The independent source
        // binary clip uses the exact original pixel-center interval instead.
        let mut scissor = if state.flags & SCENE_STATE_CLIP_RECT != 0 {
            resolve_aliased_composite_scissor(state.clip_rect, frame.target, frame.presentation)
        } else {
            resolve_target_scissor(
                &state,
                frame.target,
                [root.width, root.height],
                frame.presentation,
            )
        };
        if allocation.target == NativeLayerTargetId::Root {
            if allocation.size != [root.width, root.height] || frame.ignores_alpha {
                return Err(Error::ResourceDomain);
            }
            if let Some(damage) = partial_damage {
                scissor = scissor.intersection(damage);
            }
        }
        if !scissor.drawable {
            return Ok(None);
        }
        let [width, height] = allocation.size;
        if scissor.width == 0
            || scissor.height == 0
            || scissor.x >= width
            || scissor.y >= height
            || scissor.width > width - scissor.x
            || scissor.height > height - scissor.y
        {
            return Err(Error::ResourceDomain);
        }
        Ok(Some(Self {
            frame,
            target: allocation.target,
            size: allocation.size,
            scissor,
            color,
        }))
    }
    pub fn frame(&self) -> &NativeCommandFrame {
        &self.frame
    }
    pub fn target(&self) -> NativeLayerTargetId {
        self.target
    }
    pub fn size(&self) -> [u32; 2] {
        self.size
    }
    pub fn scissor(&self) -> NativeScissor {
        self.scissor
    }
    pub fn color(&self) -> ClearColor {
        self.color
    }
    pub fn target_alpha(&self) -> TargetAlpha {
        if self.frame.ignores_alpha {
            TargetAlpha::Ignore
        } else {
            TargetAlpha::Preserve
        }
    }
}
