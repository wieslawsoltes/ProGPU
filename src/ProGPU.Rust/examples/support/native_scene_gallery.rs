//! Shared production native-stream gallery. This owns real native GPU targets;
//! the ordinary host renderer only presents the completed native attachment.
use progpu::{
    font::ManagedFont,
    image::{GpuImage, ImageAlpha, ImageDraw, ImageSampling},
    native_scene::{
        NativeScene, NativeSceneBuilder, NativeSceneImageOptions,
        engine_gpu::{
            NativeRenderAttachment, NativeSceneRenderMetrics, NativeSceneRenderer,
            NativeSceneRendererOptions,
        },
        engine_resources::NativeExternalImages,
        execution::{NativeSceneFrame, NativeTargetFormat},
        wire::{self, WireRecord, constants::*},
    },
    scene::Scene,
};
use std::{error::Error, sync::Arc};
type Result<T> = std::result::Result<T, Box<dyn Error>>;
const SCENE_ID: u64 = 0x5255_5354_4e41_5449;
const SCOPE: u64 = 0x4e41_5449_5645_0001;
const WHITE: wire::Color = wire::Color {
    r: 1.0,
    g: 1.0,
    b: 1.0,
    a: 1.0,
};

pub struct NativeSceneGallery {
    renderer: NativeSceneRenderer,
    source: Arc<NativeScene>,
    frame: NativeSceneFrame,
    attachment: NativeRenderAttachment,
    external: NativeExternalImages,
    presentation: Scene,
    // The actual selected font outlives all independently copied glyph records.
    _font: Option<ManagedFont>,
    metrics: NativeSceneRenderMetrics,
    cold_metrics: Option<NativeSceneRenderMetrics>,
}
impl NativeSceneGallery {
    pub fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        size: [u32; 2],
        dpi: f32,
        font: Option<ManagedFont>,
    ) -> Result<Self> {
        if size.contains(&0)
            || size
                .iter()
                .any(|&n| n > device.limits().max_texture_dimension_2d)
            || !dpi.is_finite()
            || dpi <= 0.0
        {
            return Err(progpu::Error::InvalidInput.into());
        }
        // Original native masked/effected/advanced layers reject arbitrary
        // mapped presentations. Render at the actual authored logical extent and
        // host DPI; fitting the completed image is a separate host operation.
        let physical = [
            (960.0_f64 * f64::from(dpi)).ceil(),
            (640.0_f64 * f64::from(dpi)).ceil(),
        ];
        if physical
            .iter()
            .any(|&n| n < 1.0 || n > f64::from(device.limits().max_texture_dimension_2d))
        {
            return Err(progpu::Error::Capacity.into());
        }
        let native_size = [physical[0] as u32, physical[1] as u32];
        let source = Arc::new(build_scene(font.as_ref(), dpi)?);
        // This is the native engine's explicit actual format, including nested
        // image/mask/shader captures. Surface format conversion belongs to the
        // subsequent ordinary sampled presentation draw.
        let format = NativeTargetFormat::Rgba8Unorm;
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Native scene gallery retained attachment"),
            size: wgpu::Extent3d {
                width: native_size[0],
                height: native_size[1],
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: format.texture_format(),
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC
                | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        let attachment = NativeRenderAttachment::from_texture(device, &texture, None)?;
        let image = GpuImage::from_texture(device, &texture, ImageAlpha::Premultiplied)?;
        let mut presentation = Scene::default();
        let mut draw = ImageDraw::new(
            image,
            [0.0, 0.0, size[0] as f32 / dpi, size[1] as f32 / dpi],
        );
        draw.sampling = ImageSampling::Linear;
        presentation.draw_image(draw)?;
        let mut frame = NativeSceneFrame::new(native_size, dpi);
        frame.scene_id = SCENE_ID;
        frame.generation = 1;
        frame.clear_color = color([0.035, 0.045, 0.07, 1.0]);
        Ok(Self {
            renderer: NativeSceneRenderer::new(
                device,
                queue,
                format,
                NativeSceneRendererOptions::default(),
            )?,
            source,
            frame,
            attachment,
            external: NativeExternalImages::new(device, Vec::new())?,
            presentation,
            _font: font,
            metrics: Default::default(),
            cold_metrics: None,
        })
    }
    pub fn presentation(&self) -> &Scene {
        &self.presentation
    }
    pub fn target_size(&self) -> [u32; 2] {
        self.attachment.size()
    }
    pub fn metrics(&self) -> NativeSceneRenderMetrics {
        self.metrics
    }
    pub fn cold_metrics(&self) -> Option<NativeSceneRenderMetrics> {
        self.cold_metrics
    }
    pub fn has_font(&self) -> bool {
        self._font.is_some()
    }
    pub async fn render(&mut self) -> Result<()> {
        self.metrics = self
            .renderer
            .render(
                self.source.clone(),
                self.frame,
                SCOPE,
                self.attachment.clone(),
                self.external.clone(),
            )
            .await?;
        self.cold_metrics.get_or_insert(self.metrics);
        Ok(())
    }
}

fn color(c: [f32; 4]) -> wire::Color {
    wire::Color {
        r: c[0],
        g: c[1],
        b: c[2],
        a: c[3],
    }
}
fn point(x: f32, y: f32) -> wire::Point {
    wire::Point { x, y }
}
fn rect(x: f32, y: f32, width: f32, height: f32) -> wire::ImageRect {
    wire::ImageRect {
        x,
        y,
        width,
        height,
    }
}
fn identity() -> wire::Affine2d {
    NativeSceneBuilder::identity_transform()
}
fn analytic(kind: u32, bounds: wire::ImageRect, c: [f32; 4]) -> wire::AnalyticPrimitive {
    wire::AnalyticPrimitive {
        kind,
        x: bounds.x,
        y: bounds.y,
        width: bounds.width,
        height: bounds.height,
        corner_radius: if kind == PRIMITIVE_ROUNDED_RECTANGLE {
            18.0
        } else {
            0.0
        },
        color: color(c),
        transform: identity(),
        ..Default::default()
    }
}
fn shapes(
    b: &mut NativeSceneBuilder,
    primitives: &[wire::AnalyticPrimitive],
    bounds: wire::ImageRect,
    state: u32,
) -> Result<()> {
    b.draw_analytic(primitives, &[], bounds, state)?;
    Ok(())
}
fn layer(bounds: wire::ImageRect) -> wire::Layer {
    wire::Layer {
        flags: SCENE_LAYER_FORCE_ISOLATION | SCENE_LAYER_BOUNDS,
        bounds,
        opacity: 1.0,
        blend_mode: BLEND_SRC_OVER,
        mask_resource_index: SCENE_NO_INDEX,
        effect_resource_index: SCENE_NO_INDEX,
        content_revision: 1,
        composite_revision: 1,
        ..Default::default()
    }
}
fn image(
    b: &mut NativeSceneBuilder,
    resource: u32,
    size: [u32; 2],
    destination: wire::ImageRect,
    flags: u32,
    sampling: u32,
) -> Result<()> {
    b.draw_image(
        resource,
        wire::ImageDraw {
            struct_size: wire::ImageDraw::WIRE_SIZE as u32,
            flags,
            image_width: size[0],
            image_height: size[1],
            row_bytes: size[0] * 4,
            sampling,
            source_rect: rect(0.0, 0.0, size[0] as f32, size[1] as f32),
            destination_rect: destination,
            transform: identity(),
            opacity: 1.0,
            max_anisotropy: 1,
        },
        destination,
        SCENE_NO_INDEX,
        NativeSceneImageOptions::default(),
    )?;
    Ok(())
}
fn build_scene(font: Option<&ManagedFont>, dpi: f32) -> Result<NativeScene> {
    let mut b = NativeSceneBuilder::new(SCENE_ID, 1)?;
    let accents = [
        [0.12, 0.76, 0.97, 1.0],
        [0.5, 0.86, 0.45, 1.0],
        [0.98, 0.68, 0.24, 1.0],
        [0.75, 0.45, 0.92, 1.0],
        [0.95, 0.4, 0.47, 1.0],
        [0.25, 0.7, 0.85, 1.0],
    ];
    for (i, accent) in accents.into_iter().enumerate() {
        let x = 16.0 + (i % 3) as f32 * 314.0;
        let y = 18.0 + (i / 3) as f32 * 286.0;
        shapes(
            &mut b,
            &[
                analytic(
                    PRIMITIVE_ROUNDED_RECTANGLE,
                    rect(x, y, 300.0, 272.0),
                    [0.085, 0.105, 0.15, 1.0],
                ),
                analytic(
                    PRIMITIVE_RECTANGLE,
                    rect(x + 16.0, y + 16.0, 268.0, 4.0),
                    accent,
                ),
            ],
            rect(x, y, 300.0, 272.0),
            SCENE_NO_INDEX,
        )?;
    }
    // One retained native brush table drives analytic material and path pages.
    let stops = [
        wire::GradientStop {
            color: color(accents[0]),
            offset: 0.0,
            ..Default::default()
        },
        wire::GradientStop {
            color: color(accents[3]),
            offset: 1.0,
            ..Default::default()
        },
    ];
    let gradient = b.add_brush(
        wire::Brush {
            kind: SCENE_BRUSH_LINEAR_GRADIENT,
            opacity: 1.0,
            start_point: point(40.0, 70.0),
            end_point: point(284.0, 220.0),
            stop_count: 2,
            coordinate_transform0: [1.0, 0.0, 0.0, 0.0],
            coordinate_transform1: [0.0, 1.0, 0.0, 0.0],
            ..Default::default()
        },
        &stops,
    )?;
    b.draw_analytic(
        &[
            analytic(PRIMITIVE_ELLIPSE, rect(42.0, 65.0, 126.0, 126.0), [1.0; 4]),
            analytic(
                PRIMITIVE_ROUNDED_RECTANGLE,
                rect(155.0, 125.0, 126.0, 116.0),
                [1.0; 4],
            ),
        ],
        &[gradient, gradient],
        rect(42.0, 65.0, 239.0, 176.0),
        SCENE_NO_INDEX,
    )?;
    b.draw_geometry(
        &[wire::GeometryPrimitive {
            kind: GEOMETRY_TRIANGLE,
            p0: point(56.0, 243.0),
            p1: point(140.0, 209.0),
            p2: point(193.0, 255.0),
            color: color(accents[2]),
            transform: identity(),
            ..Default::default()
        }],
        &[],
        rect(56.0, 209.0, 137.0, 46.0),
        SCENE_NO_INDEX,
    )?;
    // Closed cubic and line path, retaining the complete native segment page.
    let segments = [
        wire::PathSegment {
            p0: point(363.0, 194.0),
            p1: point(345.0, 62.0),
            p2: point(579.0, 49.0),
            p3: point(592.0, 190.0),
            kind: PATH_SEGMENT_CUBIC,
            ..Default::default()
        },
        wire::PathSegment {
            p0: point(592.0, 190.0),
            p1: point(474.0, 251.0),
            kind: PATH_SEGMENT_LINE,
            ..Default::default()
        },
        wire::PathSegment {
            p0: point(474.0, 251.0),
            p1: point(363.0, 194.0),
            kind: PATH_SEGMENT_LINE,
            ..Default::default()
        },
    ];
    b.draw_paths(
        &[wire::PathFill {
            segment_count: 3,
            min_x: 345.0,
            min_y: 49.0,
            max_x: 592.0,
            max_y: 251.0,
            color: color(accents[1]),
            transform: identity(),
            fill_rule: FILL_RULE_NON_ZERO,
            sample_grid: 4,
            ..Default::default()
        }],
        &segments,
        &[],
        rect(345.0, 49.0, 247.0, 202.0),
        SCENE_NO_INDEX,
        &[],
    )?;
    // Embedded RGBA image and actual nested native picture source, sampled by
    // independent native image commands (the picture also exercises child cache).
    let mut pixels = Vec::with_capacity(32 * 32 * 4);
    for y in 0..32 {
        for x in 0..32 {
            let c = if (x / 4 + y / 4) % 2 == 0 {
                [250, 170, 55, 255]
            } else {
                [30, 72, 113, 255]
            };
            pixels.extend_from_slice(&c);
        }
    }
    let checker = b.add_rgba8_image(32, 32, 128, &pixels)?;
    image(
        &mut b,
        checker,
        [32, 32],
        rect(674.0, 68.0, 100.0, 170.0),
        0,
        IMAGE_SAMPLING_NEAREST,
    )?;
    let mut child = NativeSceneBuilder::new(SCENE_ID + 1, 1)?;
    shapes(
        &mut child,
        &[
            analytic(
                PRIMITIVE_ROUNDED_RECTANGLE,
                rect(0.0, 0.0, 96.0, 96.0),
                [0.1, 0.25, 0.42, 1.0],
            ),
            analytic(
                PRIMITIVE_ELLIPSE,
                rect(14.0, 16.0, 68.0, 68.0),
                [0.98, 0.68, 0.24, 0.8],
            ),
        ],
        rect(0.0, 0.0, 96.0, 96.0),
        SCENE_NO_INDEX,
    )?;
    let (child_bytes, _) = child.build()?;
    let picture = b.add_picture_image(
        wire::PictureImage {
            struct_size: wire::PictureImage::WIRE_SIZE as u32,
            width: 96,
            height: 96,
            dpi_scale: 1.0,
            ..Default::default()
        },
        None,
        &child_bytes,
    )?;
    image(
        &mut b,
        picture,
        [96, 96],
        rect(790.0, 68.0, 120.0, 120.0),
        SCENE_IMAGE_SOURCE_PREMULTIPLIED,
        IMAGE_SAMPLING_LINEAR,
    )?;
    image(
        &mut b,
        picture,
        [96, 96],
        rect(812.0, 200.0, 76.0, 62.0),
        SCENE_IMAGE_SOURCE_PREMULTIPLIED,
        IMAGE_SAMPLING_LINEAR,
    )?;
    // Materialized layer with rounded opacity mask and independent per-draw clip.
    let mask = b.add_rounded_rectangle_mask(wire::LayerMask {
        bounds: rect(46.0, 346.0, 240.0, 182.0),
        transform: identity(),
        corner_radii_x: [48.0; 4],
        corner_radii_y: [48.0; 4],
        opacity: 0.85,
        ..Default::default()
    })?;
    let clipped = b.add_state(wire::State {
        transform: identity(),
        opacity: 1.0,
        flags: SCENE_STATE_CLIP_RECT,
        clip_rect: rect(46.0, 348.0, 216.0, 166.0),
        ..Default::default()
    })?;
    b.push_layer(wire::Layer {
        mask_resource_index: mask,
        opacity: 0.8,
        ..layer(rect(34.0, 336.0, 266.0, 212.0))
    })?;
    shapes(
        &mut b,
        &[
            analytic(
                PRIMITIVE_ELLIPSE,
                rect(30.0, 344.0, 160.0, 180.0),
                accents[3],
            ),
            analytic(
                PRIMITIVE_ELLIPSE,
                rect(138.0, 376.0, 164.0, 170.0),
                accents[0],
            ),
        ],
        rect(30.0, 344.0, 272.0, 202.0),
        clipped,
    )?;
    b.pop_layer()?;
    // Original built-in shadow/blur compute order; later geometry stays sharp.
    let effect = b.add_effect_chain(
        &[
            wire::GroupEffect {
                kind: GROUP_EFFECT_DROP_SHADOW,
                revision: 1,
                sigma_x: 5.0,
                sigma_y: 5.0,
                offset_x: 12.0,
                offset_y: 14.0,
                color_r: 0.02,
                color_g: 0.02,
                color_b: 0.04,
                color_a: 0.95,
                ..Default::default()
            },
            wire::GroupEffect {
                kind: GROUP_EFFECT_GAUSSIAN_BLUR,
                revision: 1,
                sigma_x: 0.7,
                sigma_y: 0.7,
                ..Default::default()
            },
        ],
        1,
    )?;
    b.push_layer(wire::Layer {
        effect_resource_index: effect,
        ..layer(rect(352.0, 332.0, 258.0, 224.0))
    })?;
    shapes(
        &mut b,
        &[
            analytic(
                PRIMITIVE_ROUNDED_RECTANGLE,
                rect(374.0, 358.0, 184.0, 152.0),
                accents[4],
            ),
            analytic(
                PRIMITIVE_ELLIPSE,
                rect(425.0, 386.0, 82.0, 82.0),
                [1.0, 0.85, 0.63, 1.0],
            ),
        ],
        rect(374.0, 358.0, 184.0, 152.0),
        SCENE_NO_INDEX,
    )?;
    b.pop_layer()?;
    // Target-reading advanced layer blend over actual painter-position content.
    shapes(
        &mut b,
        &[analytic(
            PRIMITIVE_ELLIPSE,
            rect(676.0, 356.0, 150.0, 162.0),
            accents[5],
        )],
        rect(676.0, 356.0, 150.0, 162.0),
        SCENE_NO_INDEX,
    )?;
    b.push_layer(wire::Layer {
        blend_mode: BLEND_SCREEN,
        opacity: 0.85,
        ..layer(rect(740.0, 352.0, 176.0, 192.0))
    })?;
    shapes(
        &mut b,
        &[analytic(
            PRIMITIVE_ROUNDED_RECTANGLE,
            rect(748.0, 374.0, 154.0, 148.0),
            accents[3],
        )],
        rect(748.0, 374.0, 154.0, 148.0),
        SCENE_NO_INDEX,
    )?;
    b.pop_layer()?;
    if let Some(font) = font {
        owned_text(&mut b, font, dpi)?;
    }
    let (bytes, _) = b.build()?;
    Ok(NativeScene::new(bytes)?)
}

/// Nominal Latin sample from the explicitly supplied owned face. It intentionally
/// makes no shaping/hinting/provider claim; native glyph records own real source
/// contours, including explicit closing edges, and native GPU atlas execution.
fn owned_text(b: &mut NativeSceneBuilder, font: &ManagedFont, dpi: f32) -> Result<()> {
    let em = 24.0;
    let scale = em / f32::from(font.metadata().units_per_em);
    let mut segments = Vec::new();
    let mut outlines = Vec::new();
    let mut glyphs = Vec::new();
    let mut x = 24.0;
    for ch in "Native scene / owned font / GPU atlas".chars() {
        let glyph = font.glyph_index(ch as u32)?;
        if let Some(source) = font.glyph_outline(glyph)? {
            if let Some(bounds) = source.bounds().filter(|r| r[2] > r[0] && r[3] > r[1]) {
                let start = segments.len();
                for figure in source.figures() {
                    let mut last = figure.start;
                    for segment in &figure.segments {
                        let p = segment.points.map(|p| point(p[0], p[1]));
                        segments.push(wire::PathSegment {
                            p0: p[0],
                            p1: p[1],
                            p2: p[2],
                            p3: p[3],
                            kind: segment.kind,
                            ..Default::default()
                        });
                        last = segment.points[segment.kind as usize + 1];
                    }
                    if last != figure.start {
                        segments.push(wire::PathSegment {
                            p0: point(last[0], last[1]),
                            p1: point(figure.start[0], figure.start[1]),
                            kind: PATH_SEGMENT_LINE,
                            ..Default::default()
                        });
                    }
                }
                if segments.len() > start {
                    let outline_index = outlines.len() as u32;
                    outlines.push(wire::GlyphOutline {
                        segment_offset: start as u64,
                        segment_count: (segments.len() - start) as u64,
                        min_x: bounds[0],
                        min_y: bounds[1],
                        max_x: bounds[2],
                        max_y: bounds[3],
                        raster_scale: scale * dpi,
                        subpixel_x: 0.0,
                    });
                    glyphs.push(wire::PositionedGlyph {
                        outline_index,
                        position: point(x, 612.0),
                        basis_x: point(1.0, 0.0),
                        basis_y: point(0.0, 1.0),
                        color: WHITE,
                        atlas_to_logical_scale: 1.0 / dpi,
                        ..Default::default()
                    });
                }
            }
        }
        x += font.advance_width(glyph, em)?;
    }
    if !glyphs.is_empty() {
        let resource = b.add_glyph_outlines(&outlines, &segments)?;
        b.draw_glyph_run(
            resource,
            &glyphs,
            rect(20.0, 580.0, (x - 20.0).max(1.0), 48.0),
            SCENE_NO_INDEX,
            SCENE_NO_INDEX,
            None,
        )?;
    }
    Ok(())
}
