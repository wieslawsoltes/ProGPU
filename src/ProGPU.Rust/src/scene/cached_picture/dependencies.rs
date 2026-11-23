//! Bound the complete cross-family source graph before retaining a live cache edge.
use super::*;
use crate::{
    image::{ColorFilter, ColorFilterKind},
    image_filter::{FilterBlender, FilterShaderSource, ImageFilter, ImageFilterKind},
    runtime_effect::{RuntimeEffectChild, RuntimeEffectInstance},
};
struct Visitor {
    target: usize,
    remaining: usize,
}
impl Visitor {
    fn enter(&mut self, depth: u16) -> Result<(), Error> {
        if depth >= 256 || self.remaining == 0 {
            return Err(Error::Capacity);
        }
        self.remaining -= 1;
        Ok(())
    }
    fn scene(&mut self, scene: &Scene, depth: u16) -> Result<(), Error> {
        self.enter(depth)?;
        for command in &scene.commands {
            match command {
                Command::CachedPicture(draw) => {
                    if draw.picture.identity() == self.target {
                        return Err(Error::ResourceDomain);
                    }
                    if let Some(scene) = draw.picture.current_recording()? {
                        self.scene(&scene, depth + 1)?;
                    }
                }
                Command::Layer(layer) => self.scene(&layer.scene, depth + 1)?,
                Command::AxisClip(axis) => self.scene(axis.scene(), depth + 1)?,
                #[cfg(feature = "gpu")]
                Command::OwnedShaderEffect(draw) => {
                    self.scene(draw.recording.scene(), depth + 1)?
                }
                Command::ColorFilteredImage(draw) => self.color(&draw.filter, depth + 1)?,
                Command::ImageFilteredImage(draw) => self.filter(&draw.filter, depth + 1)?,
                _ => {}
            }
        }
        Ok(())
    }
    fn filter(&mut self, filter: &ImageFilter, depth: u16) -> Result<(), Error> {
        self.enter(depth)?;
        if let Some(input) = filter.input() {
            self.filter(input, depth + 1)?;
        }
        match filter.kind() {
            ImageFilterKind::Compose { outer, inner } => {
                self.filter(outer, depth + 1)?;
                self.filter(inner, depth + 1)?;
            }
            ImageFilterKind::Arithmetic {
                background,
                foreground,
                ..
            }
            | ImageFilterKind::BlendMode {
                background,
                foreground,
                ..
            } => {
                if let Some(child) = background {
                    self.filter(child, depth + 1)?;
                }
                if let Some(child) = foreground {
                    self.filter(child, depth + 1)?;
                }
                if let ImageFilterKind::BlendMode { blender, .. } = filter.kind() {
                    self.blender(blender, depth + 1)?;
                }
            }
            ImageFilterKind::ColorFilter(color) => self.color(color, depth + 1)?,
            ImageFilterKind::DisplacementMap { displacement, .. } => {
                self.filter(displacement, depth + 1)?
            }
            ImageFilterKind::Merge(children) => {
                for child in children.iter().flatten() {
                    self.filter(child, depth + 1)?;
                }
            }
            ImageFilterKind::Shader {
                source: Some(source),
                ..
            } => self.shader(source, depth + 1)?,
            ImageFilterKind::Picture(source) => self.scene(&source.scene, depth + 1)?,
            _ => {}
        }
        Ok(())
    }
    fn shader(&mut self, shader: &FilterShaderSource, depth: u16) -> Result<(), Error> {
        self.enter(depth)?;
        match shader {
            FilterShaderSource::Picture { source, .. } => self.scene(&source.scene, depth + 1)?,
            FilterShaderSource::Compose {
                background,
                foreground,
                ..
            } => {
                self.shader(background, depth + 1)?;
                self.shader(foreground, depth + 1)?;
            }
            FilterShaderSource::LocalMatrix { source, .. } => self.shader(source, depth + 1)?,
            FilterShaderSource::ColorFilter { source, filter } => {
                self.shader(source, depth + 1)?;
                self.color(filter, depth + 1)?;
            }
            FilterShaderSource::UnsupportedRuntime { children, .. } => {
                for child in children.iter() {
                    self.shader(child, depth + 1)?;
                }
            }
            FilterShaderSource::Runtime(instance) => self.runtime(instance, depth + 1)?,
            _ => {}
        }
        Ok(())
    }
    fn color(&mut self, color: &ColorFilter, depth: u16) -> Result<(), Error> {
        self.enter(depth)?;
        match color.kind() {
            ColorFilterKind::Compose { outer, inner } => {
                self.color(outer, depth + 1)?;
                self.color(inner, depth + 1)?;
            }
            ColorFilterKind::Lerp {
                filter0, filter1, ..
            } => {
                self.color(filter0, depth + 1)?;
                self.color(filter1, depth + 1)?;
            }
            ColorFilterKind::Runtime(instance) => self.runtime(instance, depth + 1)?,
            _ => {}
        }
        Ok(())
    }
    fn blender(&mut self, blender: &FilterBlender, depth: u16) -> Result<(), Error> {
        self.enter(depth)?;
        if let FilterBlender::Runtime(instance) = blender {
            self.runtime(instance, depth + 1)?;
        }
        Ok(())
    }
    fn runtime(&mut self, instance: &RuntimeEffectInstance, depth: u16) -> Result<(), Error> {
        self.enter(depth)?;
        for child in instance.children().iter().flatten() {
            match child {
                RuntimeEffectChild::Shader(shader) => self.shader(shader, depth + 1)?,
                RuntimeEffectChild::ColorFilter(filter) => self.color(filter, depth + 1)?,
                RuntimeEffectChild::Blender(blender) => self.blender(blender, depth + 1)?,
            }
        }
        Ok(())
    }
}
pub(super) fn validate(scene: &Scene, target: usize, depth: u16) -> Result<(), Error> {
    Visitor {
        target,
        remaining: 16_384,
    }
    .scene(scene, depth)
}
