//! Original Viewport3D extension producer followed by its ordinary texture draw.
use super::*;
use crate::{
    mesh3d::{Mesh3DRequest, Mesh3DViewport},
    vector::Matrix4,
};

/// Frozen offscreen viewport and independent receiving-picture placement.
/// The selected display DPI belongs to the viewport producer, including inside
/// nested captures. The picture's clip, blend and masks apply to its texture draw.
#[derive(Clone, Debug)]
pub struct Mesh3DViewportDraw {
    /// Actual immutable viewport, camera and physical target request.
    pub request: Mesh3DRequest,
    /// Ordinary receiving-picture texture rectangle in logical coordinates.
    pub destination: [f32; 4],
    /// Original complete consumer transform; texture positions use its XY terms.
    pub transform: Matrix4,
    /// Ordinary texture opacity, independent of the mesh record opacity.
    pub opacity: f32,
}
impl Mesh3DViewportDraw {
    /// Retain an explicitly sized viewport with its original logical destination.
    pub fn new(request: Mesh3DRequest) -> Result<Self, Error> {
        let [width, height] = request.viewport.viewport_size;
        let draw = Self {
            request,
            destination: [0.0, 0.0, width, height],
            transform: Matrix4::IDENTITY,
            opacity: 1.0,
        };
        draw.validate()?;
        Ok(draw)
    }
    /// Original Viewport3D display policy: truncate positive physical dimensions,
    /// clamp each to one, and select four samples below display scale 1.5.
    /// Supply the actual selected window display scale, not a capture's scale.
    pub fn from_display(
        viewport: Arc<Mesh3DViewport>,
        projection: Matrix4,
        view: Matrix4,
        display_dpi: f32,
    ) -> Result<Self, Error> {
        if !display_dpi.is_finite() || display_dpi <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let physical = viewport.viewport_size.map(|v| (v * display_dpi).max(1.0));
        if physical
            .iter()
            .any(|v| !v.is_finite() || *v >= u32::MAX as f32)
        {
            return Err(Error::Capacity);
        }
        Self::new(Mesh3DRequest {
            viewport,
            projection,
            view,
            size: physical.map(|v| v as u32),
            sample_count: if display_dpi >= 1.5 { 1 } else { 4 },
            opacity: 1.0,
        })
    }
    /// Validate the full immutable CPU inventory without acquiring source textures.
    pub fn validate(&self) -> Result<(), Error> {
        self.request.viewport.validate()?;
        let camera = self.request.view * self.transform;
        if camera.0.iter().flatten().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        if self.destination.iter().any(|v| !v.is_finite())
            || self
                .transform
                .0
                .iter()
                .chain(&self.request.projection.0)
                .chain(&self.request.view.0)
                .flatten()
                .any(|v| !v.is_finite())
            || !self.opacity.is_finite()
            || !(0.0..=1.0).contains(&self.opacity)
            || !self.request.opacity.is_finite()
            || self.request.size.contains(&0)
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    pub(crate) fn is_empty(&self) -> bool {
        self.request.is_empty()
            || self
                .request
                .viewport
                .viewport_size
                .iter()
                .any(|v| *v <= 0.0)
    }
    pub(crate) fn placed(&self, transform: Matrix4) -> Result<Self, Error> {
        let mut placed = self.clone();
        placed.transform = self.transform * transform;
        placed.validate()?;
        Ok(placed)
    }
    pub(crate) fn with_opacity(&self, opacity: f32) -> Result<Self, Error> {
        let mut placed = self.clone();
        // Original Extension8 records and subsequent Texture command each consume
        // ActiveOpacity. These are separate source operations, not one composite.
        placed.request.opacity *= opacity;
        placed.opacity *= opacity;
        placed.validate()?;
        Ok(placed)
    }
    pub(crate) fn image_draw(&self, image: crate::image::GpuImage) -> Result<ImageDraw, Error> {
        let mut draw = ImageDraw::new(ImageSource::Gpu(image), self.destination);
        let m = self.transform.0;
        draw.transform = Affine([m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1]]);
        draw.source = [0.0; 4];
        draw.opacity = self.opacity;
        draw.patch_policy = crate::image::ImagePatchPolicy::ManagedImage;
        draw.validate()?;
        Ok(draw)
    }
}
impl Scene {
    /// Record the original Mesh3D offscreen producer and its subsequent texture
    /// consumer. Full target/device admission precedes source acquisition.
    pub fn mesh3d(&mut self, draw: Mesh3DViewportDraw) -> Result<(), Error> {
        draw.validate()?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::Mesh3D(Arc::new(draw)));
        Ok(())
    }
}
