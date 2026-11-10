//! Exclusive temporary atlas ownership for completed source-mask captures.
use super::*;

/// The outer text compiler may already own its atlas outside Renderer while
/// materialized masks are prepared. A mask source must therefore neither borrow
/// that atlas's batch flag nor replace an independently retained renderer atlas.
/// Dropping this scope only releases owned handles and restores the exact owner;
/// it performs no flush, polling, source callback or GPU texture destruction.
struct SourceTextCapture<'a> {
    renderer: &'a mut Renderer,
    previous: Option<TextState>,
    previous_tree: bool,
}
impl<'a> SourceTextCapture<'a> {
    fn new(renderer: &'a mut Renderer) -> Self {
        let previous = renderer.text.take();
        let previous_tree = std::mem::replace(&mut renderer.text_tree_active, false);
        Self {
            renderer,
            previous,
            previous_tree,
        }
    }
}
impl Drop for SourceTextCapture<'_> {
    fn drop(&mut self) {
        // Any temporary atlas belongs only to the completed source consumer. Its
        // wgpu bind groups/textures are retained by the already submitted command
        // buffer; restoring an outer owner never releases/destroys those handles.
        self.renderer.text = self.previous.take();
        self.renderer.text_tree_active = self.previous_tree;
    }
}
impl Renderer {
    pub(super) async fn capture_mask_source(
        &mut self,
        source: &Scene,
        frame: RenderFrame,
    ) -> Result<(CapturedScene, crate::mask::MaskCompositionMetrics), Error> {
        let transaction = SourceTextCapture::new(self);
        let prepared = Box::pin(
            transaction
                .renderer
                .prepare_target_scene_inner(source, frame),
        )
        .await?;
        let masks = prepared.mask_composition_metrics();
        // TreePreparation completes this independent source's actual atlas batch
        // before capture. capture checks exact owner/residency, then submits and
        // retains the complete recorded producer inventory through completion.
        let captured = transaction.renderer.capture(&prepared, None).await?;
        Ok((captured, masks))
    }
}
