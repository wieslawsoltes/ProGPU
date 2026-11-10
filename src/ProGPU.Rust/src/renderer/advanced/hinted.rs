//! Original texture-backed hinted-paint source bounds and bounded frame binding.
//! Atlas coverage, source instances and paint storage remain the exact immutable
//! prepared generation; only the actual resolve frame has a separate binding.
use super::*;
use std::ops::Range;

pub(super) struct HintedSource {
    pub(super) range: Range<u32>,
    vertex_count: u32,
    pub(super) uniform: Option<wgpu::Buffer>,
}
impl HintedSource {
    pub(super) fn new(range: Range<u32>, vertex_count: u32) -> Self {
        Self {
            range,
            vertex_count,
            uniform: None,
        }
    }
}

pub(super) fn bounds(
    compiled: &CompiledScene,
    range: &Range<u32>,
    frame: RenderFrame,
    scissor: [u32; 4],
) -> Result<[u32; 4], Error> {
    let instances = compiled
        .glyph_instances()
        .get(range.start as usize..range.end as usize)
        .ok_or(Error::ResourceDomain)?;
    if instances.is_empty() {
        return Ok([0; 4]);
    }
    let mut minimum = [f32::INFINITY; 2];
    let mut maximum = [f32::NEG_INFINITY; 2];
    for instance in instances {
        // Preserve original offset+extent association before adding the original
        // logical position. Do not reconstruct outlines or quantize source bounds.
        for axis in 0..2 {
            let offset = instance.bearing_size[axis] / frame.dpi_scale;
            let extent = instance.bearing_size[axis + 2] / frame.dpi_scale;
            minimum[axis] = minimum[axis].min(instance.snapped_logical_position[axis] + offset);
            maximum[axis] =
                maximum[axis].max(instance.snapped_logical_position[axis] + (offset + extent));
        }
    }
    if minimum.iter().chain(&maximum).any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    let [ox, oy] = frame.origin;
    let [cx, cy, cw, ch] = scissor;
    let left = ((minimum[0] * frame.dpi_scale)
        .floor()
        .clamp(0.0, frame.root.width as f32) as u32)
        .max(ox + cx);
    let top = ((minimum[1] * frame.dpi_scale)
        .floor()
        .clamp(0.0, frame.root.height as f32) as u32)
        .max(oy + cy);
    let right = ((maximum[0] * frame.dpi_scale)
        .ceil()
        .clamp(0.0, frame.root.width as f32) as u32)
        .min(ox + cx + cw);
    let bottom = ((maximum[1] * frame.dpi_scale)
        .ceil()
        .clamp(0.0, frame.root.height as f32) as u32)
        .min(oy + cy + ch);
    Ok([
        left,
        top,
        right.saturating_sub(left),
        bottom.saturating_sub(top),
    ])
}
impl Renderer {
    /// Invoked after the exclusive original text transaction uploads its final
    /// storage. Every bounded source binds that storage without duplicating it.
    pub(in crate::renderer) fn finish_advanced_text(
        &self,
        scene: &mut PreparedScene,
    ) -> Result<(), Error> {
        let Some(advanced) = scene.advanced.as_mut() else {
            return Ok(());
        };
        for operation in &mut advanced.operations {
            if let Some(source) = &operation.hinted {
                let paint = scene
                    .text
                    .as_ref()
                    .and_then(|text| text.hinted.as_ref())
                    .ok_or(Error::ResourceDomain)?;
                operation.resolve_frame = Some(paint.with_frame(
                    &self.device,
                    source.uniform.as_ref().ok_or(Error::ResourceDomain)?,
                ));
            }
        }
        Ok(())
    }
    pub(super) fn draw_advanced_hinted(
        &self,
        pass: &mut wgpu::RenderPass<'_>,
        scene: &PreparedScene,
        operation: &Operation,
        mask: Option<&wgpu::BindGroup>,
    ) -> Result<(), Error> {
        let source = operation.hinted.as_ref().ok_or(Error::ResourceDomain)?;
        let text = scene.text.as_ref().ok_or(Error::ResourceDomain)?;
        let state = self
            .text
            .as_ref()
            .and_then(|text| text.hinted.as_ref())
            .ok_or(Error::ResourceDomain)?;
        let image = scene
            .images
            .as_ref()
            .and_then(|images| images.bindings.get(operation.source_binding))
            .ok_or(Error::ResourceDomain)?;
        state.draw(
            pass,
            text,
            source.range.clone(),
            source.vertex_count,
            mask,
            Some(image),
            None,
            Some(
                operation
                    .resolve_frame
                    .as_ref()
                    .ok_or(Error::ResourceDomain)?,
            ),
            scene
                .images
                .as_ref()
                .ok_or(Error::ResourceDomain)?
                .source_premultiplied(operation.source_binding)?,
        )
    }
}
