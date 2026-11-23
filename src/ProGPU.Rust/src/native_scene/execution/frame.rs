//! Safe typed counterparts of the original frame's independently readable prefixes.
use super::*;

/// Describes which original C descriptor prefix was actually supplied. Rust does
/// not fabricate a target-view pointer or assume a foreign pointer-sized layout.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum NativeFramePrefix {
    Incomplete,
    Legacy,
    Damage,
    #[default]
    Presentation,
}
#[derive(Clone, Copy, Debug)]
pub struct NativeSceneFrame {
    pub prefix: NativeFramePrefix,
    pub width: u32,
    pub height: u32,
    pub dpi_scale: f32,
    pub clear_color: wire::Color,
    pub scene_id: u64,
    pub generation: u64,
    pub flags: u32,
    pub damage: wire::ImageRect,
    pub presentation: wire::Presentation,
}
impl NativeSceneFrame {
    pub fn new(size: [u32; 2], dpi_scale: f32) -> Self {
        Self {
            prefix: NativeFramePrefix::Presentation,
            width: size[0],
            height: size[1],
            dpi_scale,
            clear_color: Default::default(),
            scene_id: 0,
            generation: 0,
            flags: 0,
            damage: Default::default(),
            presentation: Default::default(),
        }
    }
    pub fn effective_flags(&self) -> u32 {
        match self.prefix {
            NativeFramePrefix::Incomplete | NativeFramePrefix::Legacy => 0,
            _ => self.flags,
        }
    }
    /// Original O(1) presentation admission only. Frame identity, actual target
    /// ownership, damage and execution capability remain separate engine gates.
    pub fn resolve_presentation(&self) -> Option<wire::Presentation> {
        if self.prefix == NativeFramePrefix::Incomplete
            || self.width == 0
            || self.height == 0
            || !self.dpi_scale.is_finite()
            || self.dpi_scale <= 0.0
        {
            return None;
        }
        let flags = self.effective_flags();
        if flags
            & !(SCENE_FRAME_PRESERVE_TARGET
                | SCENE_FRAME_DAMAGE_RECT
                | SCENE_FRAME_PRESENTATION
                | SCENE_FRAME_CAPTURE_CPU_STAGES)
            != 0
        {
            return None;
        }
        let mut p = scalar_presentation([self.width, self.height], self.dpi_scale);
        if flags & SCENE_FRAME_PRESENTATION != 0 {
            if self.prefix != NativeFramePrefix::Presentation {
                return None;
            }
            p = self.presentation;
        }
        if p.struct_size != wire::Presentation::WIRE_SIZE as u32
            || p.reserved != 0
            || p.viewport_width == 0
            || p.viewport_height == 0
            || p.viewport_x >= self.width
            || p.viewport_y >= self.height
            || p.viewport_width > self.width - p.viewport_x
            || p.viewport_height > self.height - p.viewport_y
            || !p.dpi_scale_x.is_finite()
            || p.dpi_scale_x <= 0.0
            || !p.dpi_scale_y.is_finite()
            || p.dpi_scale_y <= 0.0
            || !(p.viewport_width as f32 / p.dpi_scale_x).is_finite()
            || !(p.viewport_height as f32 / p.dpi_scale_y).is_finite()
        {
            return None;
        }
        Some(p)
    }
}
pub fn resolve_effect(
    logical: wire::GroupEffect,
    p: wire::Presentation,
) -> Option<wire::GroupEffect> {
    let shadow = logical.kind == GROUP_EFFECT_DROP_SHADOW;
    let box_blur = logical.kind == GROUP_EFFECT_BOX_BLUR;
    if !shadow && !box_blur && logical.kind != GROUP_EFFECT_GAUSSIAN_BLUR
        || !p.dpi_scale_x.is_finite()
        || p.dpi_scale_x <= 0.0
        || !p.dpi_scale_y.is_finite()
        || p.dpi_scale_y <= 0.0
    {
        return None;
    }
    let mapped = scale_translate_four(
        [
            logical.sigma_x,
            logical.sigma_y,
            logical.offset_x,
            logical.offset_y,
        ],
        [p.dpi_scale_x, p.dpi_scale_y, p.dpi_scale_x, p.dpi_scale_y],
        [0.0; 4],
    );
    let maximum = if box_blur { 128.0 } else { 128.0 / 3.0 };
    if !mapped[0].is_finite()
        || !mapped[1].is_finite()
        || mapped[0] < 0.0
        || mapped[1] < 0.0
        || mapped[0] > maximum
        || mapped[1] > maximum
        || shadow && (!mapped[2].is_finite() || !mapped[3].is_finite())
    {
        return None;
    }
    Some(wire::GroupEffect {
        sigma_x: mapped[0],
        sigma_y: mapped[1],
        offset_x: mapped[2],
        offset_y: mapped[3],
        ..logical
    })
}
pub fn resolve_picture_frame(
    picture: &wire::LayerPictureMask,
    target: NativeScissor,
    raster_dpi: f32,
    parent: Option<wire::Presentation>,
) -> Option<NativeSceneFrame> {
    if !raster_dpi.is_finite()
        || raster_dpi <= 0.0
        || target.width == 0
        || target.height == 0
        || target.width > 16384
        || target.height > 16384
        || target.x > 16384 - target.width
        || target.y > 16384 - target.height
    {
        return None;
    }
    let source_extent = picture.flags & SCENE_PICTURE_MASK_SOURCE_EXTENT != 0;
    let size = if source_extent {
        [picture.reserved0, picture.reserved1]
    } else {
        [target.x + target.width, target.y + target.height]
    };
    if size.into_iter().any(|n| n == 0 || n > 16384) {
        return None;
    }
    let mut frame = NativeSceneFrame::new(size, raster_dpi);
    frame.presentation = scalar_presentation(size, raster_dpi);
    if source_extent {
        if !picture.bounds.width.is_finite()
            || picture.bounds.width <= 0.0
            || !picture.bounds.height.is_finite()
            || picture.bounds.height <= 0.0
        {
            return None;
        }
        frame.presentation.dpi_scale_x = size[0] as f32 / picture.bounds.width;
        frame.presentation.dpi_scale_y = size[1] as f32 / picture.bounds.height;
        frame.dpi_scale = frame.presentation.dpi_scale_y;
    } else if let Some(parent) = parent {
        if parent.viewport_x >= size[0] || parent.viewport_y >= size[1] {
            return None;
        }
        frame.presentation = parent;
        frame.presentation.viewport_width = parent.viewport_width.min(size[0] - parent.viewport_x);
        frame.presentation.viewport_height =
            parent.viewport_height.min(size[1] - parent.viewport_y);
    }
    frame.flags = SCENE_FRAME_PRESENTATION;
    frame.resolve_presentation()?;
    Some(frame)
}
pub fn supports_mapped_layer(layer: &wire::Layer) -> bool {
    let supported = SCENE_LAYER_BOUNDS
        | SCENE_LAYER_FORCE_ISOLATION
        | SCENE_LAYER_IGNORE_ALPHA
        | SCENE_LAYER_LINEAR_BYTE_OPACITY
        | SCENE_LAYER_ALIASED_COMPOSITE_BOUNDS;
    let aliased = layer.flags & SCENE_LAYER_ALIASED_COMPOSITE_BOUNDS != 0;
    layer.flags & !supported == 0
        && (!aliased
            || layer.flags == (SCENE_LAYER_BOUNDS | SCENE_LAYER_ALIASED_COMPOSITE_BOUNDS)
                && layer.blend_mode == BLEND_SRC
                && layer.opacity == 1.0
                && layer.content_revision == 0
                && layer.composite_revision == 0
                && layer.reserved0 == 0
                && layer.reserved1 == 0)
        && matches!(layer.blend_mode, BLEND_SRC | BLEND_SRC_OVER)
        && (layer.flags & (SCENE_LAYER_IGNORE_ALPHA | SCENE_LAYER_LINEAR_BYTE_OPACITY) == 0
            || layer.blend_mode == BLEND_SRC_OVER)
        && layer.mask_resource_index == SCENE_NO_INDEX
        && layer.effect_resource_index == SCENE_NO_INDEX
}
