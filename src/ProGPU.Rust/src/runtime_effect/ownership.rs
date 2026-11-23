//! Complete actual child-owner traversal, including image-pass identity filters.
//! No source callback, pixel evaluation or GPU allocation occurs here.
#[cfg(feature = "gpu")]
mod device {
    use super::super::{RuntimeEffectChild, RuntimeEffectInstance};
    use crate::{
        Error,
        image::{ColorFilter, ColorFilterKind, ImageSource},
        image_filter::{FilterBlender, FilterShaderSource},
    };
    use std::collections::HashSet;

    #[derive(Clone, Copy)]
    enum Owner<'a> {
        Shader(&'a FilterShaderSource),
        ColorFilter(&'a ColorFilter),
        Blender(&'a FilterBlender),
        Instance(&'a RuntimeEffectInstance),
    }
    fn visit(root: Owner<'_>, device: &wgpu::Device) -> Result<(), Error> {
        let mut pending = vec![(root, 0u16)];
        let mut visited = HashSet::new();
        while let Some((owner, depth)) = pending.pop() {
            if depth >= 256 || pending.len() > 16_384 || visited.len() > 16_384 {
                return Err(Error::Capacity);
            }
            let key = match owner {
                Owner::Shader(v) => (0u8, std::ptr::from_ref(v) as usize),
                Owner::ColorFilter(v) => (1, v.identity()),
                Owner::Blender(v) => (2, std::ptr::from_ref(v) as usize),
                Owner::Instance(v) => (3, v.identity()),
            };
            if !visited.insert(key) {
                continue;
            }
            let mut push = |child| {
                if pending.len() >= 16_384 {
                    return Err(Error::Capacity);
                }
                pending.try_reserve(1).map_err(|_| Error::Capacity)?;
                pending.push((child, depth + 1));
                Ok(())
            };
            match owner {
                Owner::Shader(shader) => match shader {
                    FilterShaderSource::Image { source, .. } => {
                        if let ImageSource::Gpu(image) = &source.image {
                            if image.device() != device {
                                return Err(Error::ResourceDomain);
                            }
                        }
                    }
                    FilterShaderSource::Picture { source, .. } => {
                        source.scene.preflight_source_capture_devices(device)?;
                    }
                    FilterShaderSource::Compose {
                        background,
                        foreground,
                        ..
                    } => {
                        push(Owner::Shader(foreground))?;
                        push(Owner::Shader(background))?;
                    }
                    FilterShaderSource::LocalMatrix { source, .. } => push(Owner::Shader(source))?,
                    FilterShaderSource::ColorFilter { source, filter } => {
                        push(Owner::ColorFilter(filter))?;
                        push(Owner::Shader(source))?;
                    }
                    FilterShaderSource::UnsupportedRuntime { children, .. } => {
                        for child in children.iter().rev() {
                            push(Owner::Shader(child))?;
                        }
                    }
                    FilterShaderSource::Runtime(instance) => push(Owner::Instance(instance))?,
                    FilterShaderSource::Empty | FilterShaderSource::Brush(_) => {}
                },
                Owner::ColorFilter(filter) => match filter.kind() {
                    ColorFilterKind::Runtime(instance) => push(Owner::Instance(instance))?,
                    ColorFilterKind::Compose { outer, inner } => {
                        push(Owner::ColorFilter(outer))?;
                        push(Owner::ColorFilter(inner))?;
                    }
                    ColorFilterKind::Lerp {
                        filter0, filter1, ..
                    } => {
                        push(Owner::ColorFilter(filter1))?;
                        push(Owner::ColorFilter(filter0))?;
                    }
                    _ => {}
                },
                Owner::Blender(FilterBlender::Runtime(instance)) => {
                    push(Owner::Instance(instance))?
                }
                Owner::Blender(_) => {}
                Owner::Instance(instance) => {
                    for child in instance.children().iter().rev().flatten() {
                        push(match child {
                            RuntimeEffectChild::Shader(shader) => Owner::Shader(shader),
                            RuntimeEffectChild::ColorFilter(filter) => Owner::ColorFilter(filter),
                            RuntimeEffectChild::Blender(blender) => Owner::Blender(blender),
                        })?;
                    }
                }
            }
        }
        Ok(())
    }
    impl RuntimeEffectInstance {
        /// Admit every actual typed child source against the selected device,
        /// including null/ignored/declaration-mismatched binding positions.
        pub fn preflight_source_devices(&self, device: &wgpu::Device) -> Result<(), Error> {
            visit(Owner::Instance(self), device)
        }
    }
    impl FilterShaderSource {
        /// Admit every real image, picture and nested runtime child before source
        /// execution policy or a transparent/identity shortcut is selected.
        pub fn preflight_source_devices(&self, device: &wgpu::Device) -> Result<(), Error> {
            visit(Owner::Shader(self), device)
        }
    }
    impl ColorFilter {
        /// Admit retained runtime children even when the original image path
        /// preserves its input and therefore schedules no color operation.
        pub fn preflight_source_devices(&self, device: &wgpu::Device) -> Result<(), Error> {
            visit(Owner::ColorFilter(self), device)
        }
    }
    impl FilterBlender {
        /// Admit all actual runtime child owners independently of execution.
        pub fn preflight_source_devices(&self, device: &wgpu::Device) -> Result<(), Error> {
            visit(Owner::Blender(self), device)
        }
    }
}
