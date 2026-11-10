//! Original geometry decisions deferred until the complete target frame exists.
use super::{
    ArchiveCommand, ArchiveCommandKind, ArchivePicture, ArchiveReplayOptions, ArchiveVectorDraw,
    drawing_error,
};
use crate::{
    Error,
    scene::Scene,
    text::paint::{TextPathPolicy, VectorGlyphPaint},
    vector::{Matrix4, PathGeometry, StrokeDraw, StrokeFrame},
};
use std::sync::Arc;
mod rounded;
mod spline;
pub(crate) use rounded::DirectRoundedPath;
pub use spline::SplineGeometry;

/// Complete original archive path or spline, before scale-dependent selection.
#[derive(Clone, Debug)]
pub struct ArchiveGeometryDraw {
    command: Arc<ArchiveCommand>,
    path: Option<Arc<PathGeometry>>,
    points: Arc<[[f32; 2]]>,
    doubles: Arc<[f64]>,
    transform: Matrix4,
    opacity: f32,
    options: ArchiveReplayOptions,
}
impl ArchiveGeometryDraw {
    pub(crate) fn path(
        path: Arc<PathGeometry>,
        command: &ArchiveCommand,
        transform: Matrix4,
        opacity: f32,
        options: &ArchiveReplayOptions,
    ) -> Result<Self, Error> {
        let result = Self {
            command: Arc::new(command.clone()),
            path: Some(path),
            points: Arc::from([]),
            doubles: Arc::from([]),
            transform,
            opacity,
            options: options.clone(),
        };
        result.validate()?;
        Ok(result)
    }
    pub(crate) fn spline(
        picture: &ArchivePicture,
        command: &ArchiveCommand,
        transform: Matrix4,
        opacity: f32,
    ) -> Result<Self, Error> {
        let result = Self {
            command: Arc::new(command.clone()),
            path: None,
            points: picture.points.clone(),
            doubles: picture.doubles.clone(),
            transform,
            opacity,
            options: Default::default(),
        };
        result.validate()?;
        Ok(result)
    }
    pub(crate) fn compile_only(&self) -> bool {
        self.command.kind == ArchiveCommandKind::DrawExtension && self.command.extension_id == 4
    }
    pub fn command(&self) -> &Arc<ArchiveCommand> {
        &self.command
    }
    pub fn transform(&self) -> Matrix4 {
        self.transform
    }
    pub fn opacity(&self) -> f32 {
        self.opacity
    }
    pub(crate) fn placed(&self, transform: Matrix4) -> Result<Self, Error> {
        let mut result = self.clone();
        result.transform = result.transform * transform;
        result.validate()?;
        Ok(result)
    }
    pub(crate) fn with_opacity(&self, opacity: f32) -> Result<Self, Error> {
        let mut result = self.clone();
        result.opacity *= opacity;
        result.validate()?;
        Ok(result)
    }
    pub(crate) fn validate(&self) -> Result<(), Error> {
        super::replay::affine(self.transform).map_err(drawing_error)?;
        if !self.opacity.is_finite() {
            return Err(Error::InvalidInput);
        }
        if self.path.is_some()
            && let Some(brush) = &self.command.brush
        {
            brush.to_vector().map_err(drawing_error)?;
        }
        if let Some(pen) = &self.command.pen {
            pen.to_pen()
                .map_err(drawing_error)?
                .validate_join_semantics()?;
        }

        if self.command.use_gpu_transforms
            || self.options.text_context.gpu_transforms_active
            || self.options.text_context.use_mvp
            || self.options.text_context.camera_view != Matrix4::IDENTITY
        {
            return Err(Error::UnsupportedExecution);
        }
        Ok(())
    }
    pub(crate) fn resolve(&self, dpi: f32, offscreen: bool) -> Result<(Scene, bool), Error> {
        self.validate()?;
        if self.command.kind == ArchiveCommandKind::DrawSpline || self.compile_only() {
            return Ok((self.spline_scene()?, false));
        }
        let path = self.path.as_ref().ok_or(Error::InvalidInput)?;
        let c = &self.command;
        let mut scene = Scene::default();
        let mut final_target = false;
        if let Some(brush) = &c.brush {
            let direct = (!c.use_vector_glyph_rendering)
                .then(|| DirectRoundedPath::read(path))
                .flatten();
            let axis =
                self.transform.0[0][1].abs() <= 0.0001 && self.transform.0[1][0].abs() <= 0.0001;
            let direct = direct.filter(|v| axis || (!v.has_inner() && !v.has_rounding()));
            let mut compiled = false;
            if let Some(direct) = direct {
                if !direct.has_inner() && !direct.has_rounding() {
                    if !offscreen {
                        let mut rectangle = (**c).clone();
                        rectangle.kind = ArchiveCommandKind::DrawRect;
                        rectangle.pen = None;
                        rectangle.rect = direct.rectangle();
                        scene.draw_archive_vector(ArchiveVectorDraw::new(
                            Arc::new(rectangle),
                            Arc::from([]),
                            self.transform,
                            self.opacity,
                        )?)?;
                        final_target = true;
                        compiled = true;
                    }
                } else {
                    let draw = ArchiveVectorDraw::direct_path(
                        c.clone(),
                        Arc::new(direct),
                        self.transform,
                        self.opacity,
                    )?;
                    // The original helper falls back to the atlas when every
                    // triangle is degenerate after its exact finite/area gates.
                    if draw.direct_has_vertices()? {
                        scene.draw_archive_vector(draw)?;
                        compiled = true;
                    }
                }
            }
            if !compiled {
                let mut gamma = if c.path_coverage_gamma < 0.0 {
                    0.0
                } else {
                    c.path_coverage_gamma
                };
                if c.use_vector_glyph_rendering && c.font_size > 0.0 {
                    let font_scale = if c.has_font_transform && c.font_transform[0].is_finite() {
                        c.font_transform[0]
                    } else {
                        1.0
                    };
                    let skew = if c.has_font_transform && c.font_transform[1].is_finite() {
                        c.font_transform[1]
                    } else {
                        0.0
                    };
                    let m = self.transform.0;
                    gamma = if skew.abs() > 0.0001
                        || font_scale < 0.0
                        || m[0][1].abs() > 0.0001
                        || m[1][0].abs() > 0.0001
                        || m[0][0] < 0.0
                        || m[1][1] < 0.0
                    {
                        0.875
                    } else {
                        let zoom = self.options.text_context.static_zoom;
                        let zoom = if zoom.is_finite() && zoom > 0.0 {
                            zoom
                        } else {
                            1.0
                        };
                        let effective_dpi = if dpi.is_finite() && dpi > 0.0 {
                            dpi
                        } else {
                            1.0
                        };
                        let effective_dpi = effective_dpi * zoom;
                        let pixels = c.font_size.abs()
                            * self.transform.stroke_scales().map_or(1.0, |s| s[0])
                            * effective_dpi;
                        if pixels.is_finite() && pixels >= 24.0 {
                            0.5
                        } else {
                            0.72
                        }
                    };
                }
                scene.draw_text_path(VectorGlyphPaint {
                    path: path.clone(),
                    transform: self.transform,
                    brush: Some(Arc::new(brush.to_vector().map_err(drawing_error)?)),
                    semantic_opacity: self.opacity,
                    policy: TextPathPolicy {
                        aliased: c.is_edge_aliased,
                        sample_grid: c.path_sample_grid,
                        coverage_gamma: gamma,
                        device_phase_grid: 64,
                        quantize_scale: false,
                        raster_scale: dpi,
                        coverage_transfer: Default::default(),
                    },
                })?;
            }
        }
        self.stroke(&mut scene, path.clone())?;
        Ok((scene, final_target))
    }
    fn stroke(&self, scene: &mut Scene, path: Arc<PathGeometry>) -> Result<(), Error> {
        let Some(pen) = &self.command.pen else {
            return Ok(());
        };
        let mut draw = StrokeDraw::new(path, Arc::new(pen.to_pen().map_err(drawing_error)?));
        draw.frame = StrokeFrame {
            transform: self.transform,
            thickness_is_local: self.command.is_pen_thickness_local,
            recorded_transform: (self.command.transform != Matrix4([[0.0; 4]; 4]))
                .then_some(self.command.transform),
        };
        draw.aliased = self.command.is_edge_aliased;
        draw.semantic_opacity = self.opacity;
        scene.draw_stroke(draw)
    }
}
