//! Original retained canvas state and immutable command generations.
use crate::{
    Error, Scene,
    geometry::Affine,
    image::{ImageDraw, ImageSampling, ImageSource},
    paint::{Paint, PaintSnapshot, PaintStyle},
    scene::{LayerBlend, PathSampling},
    vector::{Matrix4, PathEffectKind, PathEffectSource, PathGeometry, Region, RoundRect},
};
use std::sync::Arc;
mod clip;
mod drawing;
mod matrix;
pub use drawing::CanvasPointMode;
mod prepare;
#[cfg(feature = "gpu")]
mod surface;
pub use matrix::CanvasMatrix;
#[cfg(feature = "gpu")]
pub use surface::SurfaceCanvas;
/// Original binary clip operation; inverse path fill remains independent.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum CanvasClipOperation {
    /// Retain common coverage.
    #[default]
    Intersect,
    /// Remove actual source coverage.
    Difference,
}
/// Complete original save-layer flags; unknown bits remain retained metadata.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SaveLayerFlags(pub u32);
impl SaveLayerFlags {
    /// Original retained LCD-preservation intent; independent text gates remain.
    pub const PRESERVE_LCD_TEXT: Self = Self(2);
    /// Original destination initialization request.
    pub const INITIALIZE_WITH_PREVIOUS: Self = Self(4);
    /// Original floating-color target request, independently gated by backend.
    pub const F16_COLOR_TYPE: Self = Self(16);
}
/// Immutable original layer bounds, source paint and independent backdrop graph.
#[derive(Clone, Debug, Default)]
pub struct SaveLayer {
    /// Original optional left/top/right/bottom bounds; None uses full canvas.
    pub bounds: Option<[f32; 4]>,
    /// Actual paint snapshot at the save boundary.
    pub paint: Option<PaintSnapshot>,
    /// Original backdrop image-filter graph, distinct from source image filter.
    pub backdrop: Option<crate::image_filter::ImageFilter>,
    /// Exact raw original flags.
    pub flags: SaveLayerFlags,
}
#[derive(Clone, Debug)]
struct Clip {
    geometry: Arc<PathGeometry>,
    matrix: CanvasMatrix,
    operation: CanvasClipOperation,
    rect: Option<[f32; 4]>,
}
#[derive(Clone, Copy, Debug)]
struct ClipState {
    bounds: [i32; 4],
    rect: bool,
}
impl ClipState {
    fn empty(self) -> bool {
        self.bounds[2] <= self.bounds[0] || self.bounds[3] <= self.bounds[1]
    }
}
#[derive(Clone, Debug)]
enum Command {
    DeviceColor {
        color: [f32; 4],
        blend: LayerBlend,
    },
    Clip(Clip),
    Pop,
    Path {
        geometry: Arc<PathGeometry>,
        matrix: CanvasMatrix,
        paint: PaintSnapshot,
        inverse: bool,
    },
    Analytic {
        geometry: Arc<PathGeometry>,
        shape: crate::geometry::Shape,
        bounds: [f32; 4],
        radius: f32,
        matrix: CanvasMatrix,
        paint: PaintSnapshot,
    },
    Image {
        draw: ImageDraw,
        paint: Option<PaintSnapshot>,
    },
    #[cfg(feature = "gpu")]
    Texture {
        image: crate::image::TextureImage,
        source: [f32; 4],
        destination: [f32; 4],
        sampling: ImageSampling,
        matrix: CanvasMatrix,
        paint: Option<PaintSnapshot>,
    },
    Picture {
        picture: CanvasPicture,
        matrix: CanvasMatrix,
        opacity: f32,
        blend: LayerBlend,
    },
    Leaf(Scene),
    Layer(Arc<Layer>),
}
#[derive(Clone, Debug)]
struct Layer {
    source: CanvasPicture,
    prior: Option<CanvasPicture>,
    clips: Arc<[Clip]>,
    description: SaveLayer,
    bounds: [f32; 4],
    matrix: CanvasMatrix,
}
struct LayerFrame {
    parent: Vec<Command>,
    parent_clip_base: usize,
    prior: Option<CanvasPicture>,
    clips: Arc<[Clip]>,
    description: SaveLayer,
    bounds: [f32; 4],
    matrix: CanvasMatrix,
}
struct Saved {
    matrix: CanvasMatrix,
    opacity: f32,
    clips: usize,
    clip_state: ClipState,
    layer: Option<LayerFrame>,
}
struct ActiveClip {
    clip: Clip,
    index: usize,
}
/// Frozen complete original recording; retained shaders/images/text keep their
/// actual owners and are evaluated only at explicit scene preparation.
#[derive(Clone, Debug)]
pub struct CanvasPicture {
    size: [f32; 2],
    commands: Arc<[Command]>,
}
impl CanvasPicture {
    /// Original canvas extent, independent of command ink bounds.
    pub fn size(&self) -> [f32; 2] {
        self.size
    }
    /// Recorded command count, including source scopes.
    pub fn len(&self) -> usize {
        self.commands.len()
    }
    /// Whether this picture contains any commands.
    pub fn is_empty(&self) -> bool {
        self.commands.is_empty()
    }
}
/// Safe mutable original canvas recorder. It owns command state and has no
/// implicit process-global graphics context or writable surface alias.
pub struct Canvas {
    size: [f32; 2],
    matrix: CanvasMatrix,
    opacity: f32,
    clip_state: ClipState,
    commands: Vec<Command>,
    context_clip_base: usize,
    saved: Vec<Saved>,
    clips: Vec<ActiveClip>,
    cpu_readback_regions: Option<Vec<[f32; 4]>>,
}
impl Canvas {
    /// Create a source recorder with the original whole-canvas clip-state bounds.
    pub fn new(size: [f32; 2]) -> Result<Self, Error> {
        if size.iter().any(|v| !v.is_finite() || *v < 0.0) {
            return Err(Error::InvalidInput);
        }
        let bounds = [0, 0, size[0].ceil() as i32, size[1].ceil() as i32];
        Ok(Self {
            size,
            matrix: CanvasMatrix::IDENTITY,
            opacity: 1.0,
            clip_state: ClipState {
                bounds,
                rect: bounds[2] > 0 && bounds[3] > 0,
            },
            commands: Vec::new(),
            context_clip_base: 0,
            saved: Vec::new(),
            clips: Vec::new(),
            cpu_readback_regions: None,
        })
    }
    /// Exact original total matrix including retained perspective coefficients.
    pub fn total_matrix(&self) -> CanvasMatrix {
        self.matrix
    }
    /// Original saved-state depth plus the implicit base state.
    pub fn save_count(&self) -> usize {
        self.saved.len() + 1
    }
    /// Original canvas extent.
    pub fn size(&self) -> [f32; 2] {
        self.size
    }
    /// Original state save returns the previous count; no drawing occurs.
    pub fn save(&mut self) -> Result<usize, Error> {
        self.saved.try_reserve(1).map_err(|_| Error::Capacity)?;
        let count = self.save_count();
        self.saved.push(Saved {
            matrix: self.matrix,
            opacity: self.opacity,
            clips: self.clips.len(),
            clip_state: self.clip_state,
            layer: None,
        });
        Ok(count)
    }
    /// Save a complete independent layer recipe. Source snapshots are immutable;
    /// source callbacks cannot occur later during GPU encoding.
    pub fn save_layer(&mut self, description: SaveLayer) -> Result<usize, Error> {
        if self.saved.iter().filter(|s| s.layer.is_some()).count() >= 16 {
            return Err(Error::Capacity);
        }
        let bounds = description
            .bounds
            .unwrap_or([0.0, 0.0, self.size[0], self.size[1]]);
        let clips: Arc<[Clip]> = self
            .clips
            .iter()
            .map(|c| c.clip.clone())
            .collect::<Vec<_>>()
            .into();
        let prior = (valid(bounds)
            && (description.backdrop.is_some()
                || description.flags.0 & SaveLayerFlags::INITIALIZE_WITH_PREVIOUS.0 != 0))
            .then(|| self.snapshot_current());
        let count = self.save()?;
        let parent = std::mem::take(&mut self.commands);
        let parent_clip_base = self.context_clip_base;
        self.context_clip_base = self.clips.len();
        self.saved.last_mut().ok_or(Error::ResourceDomain)?.layer = Some(LayerFrame {
            parent,
            parent_clip_base,
            prior,
            clips,
            description,
            bounds,
            matrix: self.matrix,
        });
        Ok(count)
    }
    /// Original restore at base is a no-op. Empty newly pushed clips are removed
    /// instead of publishing push/pop pairs around no drawing.
    pub fn restore(&mut self) -> Result<(), Error> {
        if let Some(saved) = self.saved.last_mut() {
            self.commands
                .try_reserve(self.clips.len() - saved.clips + 1)
                .map_err(|_| Error::Capacity)?;
            if let Some(layer) = &mut saved.layer {
                layer.parent.try_reserve(1).map_err(|_| Error::Capacity)?;
            }
        }
        let Some(saved) = self.saved.pop() else {
            return Ok(());
        };
        while self.clips.len() > saved.clips {
            let scope = self.clips.pop().ok_or(Error::ResourceDomain)?;
            if self.commands.len() == scope.index + 1
                && matches!(self.commands.last(), Some(Command::Clip(_)))
            {
                self.commands.pop();
            } else {
                self.push(Command::Pop)?;
            }
        }
        self.matrix = saved.matrix;
        self.opacity = saved.opacity;
        self.clip_state = saved.clip_state;
        if let Some(layer) = saved.layer {
            let source = CanvasPicture {
                size: self.size,
                commands: std::mem::take(&mut self.commands).into(),
            };
            self.commands = layer.parent;
            self.context_clip_base = layer.parent_clip_base;
            let generates = layer.description.backdrop.is_some()
                || layer.prior.is_some()
                || layer
                    .description
                    .paint
                    .as_ref()
                    .is_some_and(|p| p.color_filter.is_some() || p.image_filter.is_some());
            if valid(layer.bounds) && (generates || !source.is_empty()) {
                self.push(Command::Layer(Arc::new(Layer {
                    source,
                    prior: layer.prior,
                    clips: layer.clips,
                    description: layer.description,
                    bounds: layer.bounds,
                    matrix: layer.matrix,
                })))?;
            }
        }
        Ok(())
    }
    /// Counts below one restore to the original base state.
    pub fn restore_to_count(&mut self, count: i32) -> Result<(), Error> {
        let depth = count.max(1) as usize - 1;
        while self.saved.len() > depth {
            self.restore()?;
        }
        Ok(())
    }
    /// Finish all saved layers and scopes, publishing one independent picture.
    pub fn finish(mut self) -> Result<CanvasPicture, Error> {
        self.restore_to_count(1)?;
        while self.clips.pop().is_some() {
            self.push(Command::Pop)?;
        }
        Ok(CanvasPicture {
            size: self.size,
            commands: self.commands.into(),
        })
    }
    fn snapshot_current(&self) -> CanvasPicture {
        let mut commands: Vec<Command> = self.clips[..self.context_clip_base]
            .iter()
            .map(|c| Command::Clip(c.clip.clone()))
            .collect();
        commands.extend(self.commands.iter().cloned());
        commands.extend((0..self.clips.len()).map(|_| Command::Pop));
        CanvasPicture {
            size: self.size,
            commands: commands.into(),
        }
    }
    fn push(&mut self, command: Command) -> Result<(), Error> {
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(command);
        Ok(())
    }
    /// Original translation updates only translation components, even when the
    /// recorded matrix contains perspective; it is not generic concatenation.
    pub fn translate(&mut self, delta: [f32; 2]) {
        let m = &mut self.matrix.0;
        m[2] += delta[0] * m[0] + delta[1] * m[1];
        m[5] += delta[0] * m[3] + delta[1] * m[4];
    }
    /// Original scale updates the two affine basis columns only.
    pub fn scale(&mut self, scale: [f32; 2]) {
        let m = &mut self.matrix.0;
        m[0] *= scale[0];
        m[3] *= scale[0];
        m[1] *= scale[1];
        m[4] *= scale[1];
    }
    /// Original pivot translation order.
    pub fn scale_at(&mut self, scale: [f32; 2], pivot: [f32; 2]) {
        if scale == [1.0; 2] {
            return;
        }
        self.translate(pivot);
        self.scale(scale);
        self.translate(pivot.map(|v| -v));
    }
    /// Original full matrix concatenation and identity shortcuts.
    pub fn concat(&mut self, matrix: CanvasMatrix) {
        self.matrix = self.matrix.concat(matrix);
    }
    /// Original direct matrix assignment, retaining invalid/raw metadata until use.
    pub fn set_matrix(&mut self, matrix: CanvasMatrix) {
        self.matrix = matrix;
    }
    /// Restore exact identity without changing clips.
    pub fn reset_matrix(&mut self) {
        self.matrix = CanvasMatrix::IDENTITY;
    }
    /// Original double-modulus degree admission and float degrees-to-radians.
    pub fn rotate_degrees(&mut self, degrees: f32) {
        if f64::from(degrees) % 360.0 != 0.0 {
            self.concat(CanvasMatrix::rotation(
                degrees * (std::f32::consts::PI / 180.0),
            ));
        }
    }
    /// Original double-modulus radians admission.
    pub fn rotate_radians(&mut self, radians: f32) {
        if f64::from(radians) % (std::f64::consts::PI * 2.0) != 0.0 {
            self.concat(CanvasMatrix::rotation(radians));
        }
    }
    /// Original pivot translation order for degrees.
    pub fn rotate_degrees_at(&mut self, degrees: f32, pivot: [f32; 2]) {
        if f64::from(degrees) % 360.0 == 0.0 {
            return;
        }
        self.translate(pivot);
        self.rotate_degrees(degrees);
        self.translate(pivot.map(|v| -v));
    }
    /// Original skew coefficients, without converting them from angles.
    pub fn skew(&mut self, skew: [f32; 2]) {
        if skew != [0.0; 2] {
            self.concat(crate::vector::EffectMatrix([
                1.0, skew[0], 0.0, skew[1], 1.0, 0.0, 0.0, 0.0, 1.0,
            ]));
        }
    }
    /// Retained source canvases have no immediate attachment to invalidate.
    pub fn discard(&mut self) {}
}
fn valid(bounds: [f32; 4]) -> bool {
    bounds.iter().all(|v| v.is_finite()) && bounds[2] > bounds[0] && bounds[3] > bounds[1]
}
