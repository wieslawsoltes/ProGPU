//! Counted original image producers retained through actual queue completion.
use super::*;
/// Clone the immutable source descriptors, not texture bytes or mutable producer
/// state. Graph and mask owners include source-only/runtime child dependencies.
#[derive(Debug)]
pub(super) struct ImageSourceOwners {
    _images: Vec<crate::image::ImageSource>,
    _backdrops: Vec<crate::backdrop::BackdropMaterialDraw>,
    _states: Vec<crate::scene::CompiledDrawState>,
    _effects: Vec<crate::image_effect::ImageEffectDraw>,
    _colors: Vec<crate::image::ColorFilteredImageDraw>,
    _filters: Vec<crate::image_filter::ImageFilteredImageDraw>,
    _shaders: Vec<crate::scene::ShaderEffectDraw>,
    _managed: Vec<crate::scene::ManagedShaderEffectDraw>,
}
impl ImageSourceOwners {
    pub(super) fn capture(source: &CompiledScene) -> Arc<Self> {
        Arc::new(Self {
            _images: source.images.iter().map(|v| v.image.clone()).collect(),
            _backdrops: source.backdrop_materials.clone(),
            _states: source.states.clone(),
            _effects: source.image_effects.clone(),
            _colors: source.color_filtered_images.clone(),
            _filters: source.image_filtered_images.clone(),
            _shaders: source.shader_effects.clone(),
            _managed: source.managed_shader_effects.clone(),
        })
    }
}
impl PreparedScene {
    /// Retain every actual source-image owner until all already submitted work is
    /// complete, independently of this prepared scene's lifetime. Call after an
    /// externally managed queue submission; Renderer submission APIs do it for you.
    pub fn retain_submitted_images(&self, queue: &wgpu::Queue) {
        if let Some(owners) = self.image_source_owners.clone() {
            queue.on_submitted_work_done(move || drop(owners));
        }
        if let Some(backdrops) = &self.backdrops {
            backdrops.retain_submitted(queue);
        }
        if let Some(images) = &self.images {
            images.retain_submitted(queue);
        }
        for child in &self.groups.children {
            child.scene.retain_submitted_images(queue);
            child.clip.retain_submitted(queue);
        }
        if let Some(root) = &self.groups.root {
            root.image.retain_submitted(queue);
        }
        if let Some(filters) = &self.image_filters {
            filters.retain_submitted(queue);
        }
        if let Some(filters) = &self.color_filters {
            filters.retain_submitted(queue);
        }
        if let Some(effects) = &self.image_effects {
            effects.retain_submitted(queue);
        }
        if let Some(shaders) = &self.shaders {
            shaders.retain_submitted(queue);
        }
        if let Some(shaders) = &self.managed_shaders {
            shaders.retain_submitted(queue);
        }
    }
}
