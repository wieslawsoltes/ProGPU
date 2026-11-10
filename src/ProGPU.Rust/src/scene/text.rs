//! Source Compositor/PathAtlas text placement, separate from native path defaults.
use super::*;
use crate::vector::Matrix4;

fn affine(matrix: Matrix4) -> Result<Affine, Error> {
    let m = matrix.0;
    if m.iter().flatten().any(|v| !v.is_finite())
        || m[0][2] != 0.0
        || m[0][3] != 0.0
        || m[1][2] != 0.0
        || m[1][3] != 0.0
        || m[2] != [0.0, 0.0, 1.0, 0.0]
        || m[3][2] != 0.0
        || m[3][3] != 1.0
    {
        return Err(Error::UnsupportedExecution);
    }
    Ok(Affine([
        m[0][0], m[0][1], m[1][0], m[1][1], m[3][0], m[3][1],
    ]))
}

pub(super) fn validate_context(context: TextPaintContext) -> Result<(), Error> {
    affine(context.transform)?;
    if context.use_mvp
        || context.gpu_transforms_active
        || context.clip.is_some()
        || context.camera_view != Matrix4::IDENTITY
        || context.static_zoom != 1.0
    {
        return Err(Error::UnsupportedExecution);
    }
    if !context.semantic_opacity.is_finite() {
        return Err(Error::InvalidInput);
    }
    Ok(())
}

pub(super) fn path_command(paint: VectorGlyphPaint) -> Result<Option<Command>, Error> {
    path_command_with_source(paint, None)
}

fn path_command_with_source(
    paint: VectorGlyphPaint,
    compiled: Option<Option<Path>>,
) -> Result<Option<Command>, Error> {
    let Some(brush) = paint.brush else {
        return Ok(None);
    };
    let transform = affine(paint.transform)?;
    let policy = paint.policy;
    if !policy.coverage_gamma.is_finite()
        || !policy.raster_scale.is_finite()
        || policy.raster_scale <= 0.0
        || !paint.semantic_opacity.is_finite()
        || (policy.aliased && policy.coverage_transfer.encoding() != 0)
    {
        return Err(Error::InvalidInput);
    }
    let Some(path) = (match compiled {
        Some(path) => path,
        None => paint.path.compile_raster_path()?,
    }) else {
        return Ok(None);
    };
    let sampling = PathSampling::new(if policy.aliased {
        1
    } else if policy.sample_grid >= 8 {
        8
    } else if policy.sample_grid == 1 {
        1
    } else {
        4
    })?;
    let color = match brush.kind {
        BrushKind::Solid(color) => color,
        _ => [1.0; 4],
    };
    Ok(Some(Command::TextPath(
        PathDraw {
            path,
            transform,
            color,
            sampling,
        },
        MaterialPaint {
            brush,
            semantic_opacity: paint.semantic_opacity,
        },
        policy,
    )))
}

// Original ILogB/ScaleB semantics, including subnormal steps. Positive finite
// scale is checked by the consumer. No logarithm approximation changes buckets.
fn quantize_scale(value: f32) -> f32 {
    if !value.is_finite() || value == 0.0 {
        return value;
    }
    let bits = value.abs().to_bits();
    let biased = (bits >> 23) as i32;
    let exponent = if biased == 0 {
        31 - bits.leading_zeros() as i32 - 149
    } else {
        biased - 127
    };
    let power = exponent - 10;
    let step = if power < -149 {
        0.0
    } else if power < -126 {
        f32::from_bits(1 << (power + 149))
    } else {
        f32::from_bits(((power + 127) as u32) << 23)
    };
    if !step.is_finite() || step <= 0.0 {
        return value;
    }
    let quantized = (value / step).round_ties_even() * step;
    if quantized.is_finite() {
        quantized
    } else {
        value
    }
}

fn phase(value: f32, grid: u32) -> f32 {
    if !value.is_finite() {
        return 0.0;
    }
    let grid = grid.clamp(1, 64) as f32;
    let value = value - value.floor();
    let quantized = (value * grid).round_ties_even() / grid;
    if quantized >= 1.0 { 0.0 } else { quantized }
}

pub(super) fn placement(draw: &PathDraw, policy: TextPathPolicy) -> Result<Placement, Error> {
    let [a, b, c, d, x, y] = draw.transform.0;
    let lengths = (wide::f32x4::from([a, c, 0.0, 0.0]) * wide::f32x4::from([a, c, 0.0, 0.0])
        + wide::f32x4::from([b, d, 0.0, 0.0]) * wide::f32x4::from([b, d, 0.0, 0.0]))
    .sqrt()
    .to_array();
    let mut scale = [lengths[0], lengths[1]].map(|s| if s < 0.0001 { 1.0 } else { s });
    if b.abs() > 0.0001 || c.abs() > 0.0001 {
        scale = [scale[0].max(scale[1]); 2];
    }
    scale = scale.map(|s| {
        let s = s * policy.raster_scale;
        if policy.quantize_scale {
            quantize_scale(s)
        } else {
            s
        }
    });
    if scale.iter().any(|s| !s.is_finite() || *s <= 0.0) {
        return Err(Error::InvalidInput);
    }
    let phase = [x, y].map(|v| phase(v * policy.raster_scale, policy.device_phase_grid));
    let bounds = draw.path.bounds();
    let mut minimum = [0.0; 2];
    let mut maximum = [0.0; 2];
    let mut size = [0; 2];
    for axis in 0..2 {
        // Source floors in double and performs the padding in integer space.
        let lo = f64::from(bounds[axis] * scale[axis]).floor() - 4.0;
        let hi = f64::from(bounds[axis + 2] * scale[axis]).ceil() + 4.0;
        if !lo.is_finite()
            || !hi.is_finite()
            || lo < f64::from(i32::MIN)
            || hi > f64::from(i32::MAX)
            || hi <= lo
            || hi - lo > 4092.0
        {
            return Err(Error::Capacity);
        }
        minimum[axis] = lo as i32 as f32;
        maximum[axis] = hi as i32 as f32;
        size[axis] = (hi - lo) as u32;
    }
    Ok(Placement {
        minimum,
        maximum,
        phase,
        request: RasterRequest {
            origin: [minimum[0] - phase[0], minimum[1] - phase[1]],
            scale,
            size,
            sample_grid: draw.sampling.sample_grid(),
        },
    })
}

pub(super) fn vertices(
    p: &Placement,
    draw: &PathDraw,
    policy: TextPathPolicy,
    origin: [u32; 2],
    dpi: f32,
) -> [VectorVertex; 4] {
    let [sx, sy] = p.request.scale;
    let [left, top] = [p.minimum[0] / sx, p.minimum[1] / sy];
    let [width, height] = p.request.size.map(|v| v as f32);
    let right = left + width / sx;
    let bottom = top + height / sy;
    let local = [[left, top], [right, top], [right, bottom], [left, bottom]];
    let [x, y] = origin.map(|v| v as f32);
    let [px, py] = p.phase;
    let uv = [
        [x + px, y + py],
        [x + width + px, y + py],
        [x + width + px, y + height + py],
        [x + px, y + height + py],
    ];
    // Matrix4x4/Vector2 source order: x*M11 + y*M21 + M41.
    let [a, b, c, d, tx, ty] = draw.transform.0;
    let xs = wide::f32x4::from(local.map(|v| v[0]));
    let ys = wide::f32x4::from(local.map(|v| v[1]));
    let vx = (xs * wide::f32x4::splat(a) + ys * wide::f32x4::splat(c) + wide::f32x4::splat(tx))
        .to_array();
    let vy = (xs * wide::f32x4::splat(b) + ys * wide::f32x4::splat(d) + wide::f32x4::splat(ty))
        .to_array();
    let positions = std::array::from_fn(|i| [vx[i], vy[i]]);
    let exact = dpi == 1.0 && exact_mapping(positions, uv);
    let transfer = policy.coverage_transfer.encoding();
    let gamma = if transfer != 0 {
        -(transfer as f32)
    } else {
        policy.coverage_gamma.max(0.0)
    };
    std::array::from_fn(|i| VectorVertex {
        position: positions[i],
        color: draw.color,
        texture_coordinate: uv[i],
        brush_index: 0.0,
        shape_size: local[i],
        corner_radius: gamma,
        stroke_thickness: if exact { -1.0 } else { 0.0 },
        shape_type: if policy.aliased { 1004.0 } else { 4.0 },
    })
}

#[cfg(feature = "gpu")]
pub(crate) struct ResolvedSceneText {
    pub scene: Scene,
    pub styles: Vec<crate::text::paint::GpuTextStyle>,
    pub residency: crate::text::glyph::GlyphAtlasResidency,
    pub statistics: crate::text::glyph::GlyphAtlasStatistics,
    pub coverage: crate::text::glyph::GlyphCoverageTexture,
    pub color: crate::text::glyph::GlyphColorTexture,
}

#[cfg(feature = "gpu")]
impl Scene {
    // Caller exclusively owns the atlas transaction; failure/cancellation drops
    // that private renderer state, never leaving a live half-open batch.
    pub(crate) async fn resolve_text(
        &self,
        painter: &mut crate::text::paint::TextPainter,
        atlas: &mut crate::text::glyph::GlyphAtlas,
        dpi: f32,
    ) -> Result<ResolvedSceneText, Error> {
        use crate::text::paint::ResolvedTextCommand;
        let text_error = |error| Error::Text(Box::new(error));
        let mut plans = Vec::new();
        plans
            .try_reserve_exact(self.commands.len())
            .map_err(|_| Error::Capacity)?;
        for command in &self.commands {
            plans.push(if let Command::Text(record) = command {
                let (draw, mut context) = record.as_ref().clone();
                context.dpi_scale = dpi;
                validate_context(context)?;
                Some(painter.plan(&draw, context).map_err(text_error)?)
            } else {
                if let Command::HintedText(record) = command {
                    let mut context = record.1;
                    context.dpi_scale = dpi;
                    validate_context(context)?;
                    record.0.validate_atlas_paint(context)?;
                }
                None
            });
        }
        atlas.begin_batch().await?;
        let residency_start = atlas.batch_usage_count();
        let mut scene = Scene {
            has_scopes: self.has_scopes,
            inherited_clip: self.inherited_clip,
            ..Default::default()
        };
        let mut styles = Vec::new();
        let mut paths = HashMap::new();
        for (command, plan) in self.commands.iter().zip(plans) {
            scene.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
            if let Some(plan) = plan {
                let (resolved, run_styles) = painter
                    .resolve_pending(atlas, plan)
                    .await
                    .map_err(text_error)?;
                let base = styles.len();
                let count = base.checked_add(run_styles.len()).ok_or(Error::Capacity)?;
                if count > (1 << 24) {
                    return Err(Error::Capacity);
                }
                styles
                    .try_reserve(run_styles.len())
                    .map_err(|_| Error::Capacity)?;
                styles.extend_from_slice(&run_styles);
                scene
                    .commands
                    .try_reserve(resolved.len())
                    .map_err(|_| Error::Capacity)?;
                for item in resolved {
                    match item {
                        ResolvedTextCommand::Glyph(instance) => {
                            let mut instance = instance;
                            if instance.brush_index >= 0.0 {
                                instance.brush_index += base as f32;
                            }
                            scene.commands.push(Command::Glyph(instance));
                        }
                        ResolvedTextCommand::Vector(path) => {
                            if path.brush.is_none() {
                                continue;
                            }
                            // Retain actual geometry owners while deduplicating
                            // compilation; allocator address reuse cannot alias.
                            let key = Arc::as_ptr(&path.path) as usize;
                            let source = path.path.clone();
                            let previous = paths.get(&key).map(
                                |(_, path): &(Arc<crate::vector::PathGeometry>, Option<Path>)| {
                                    path.clone()
                                },
                            );
                            if let Some(mut command) = path_command_with_source(path, previous)? {
                                if let Command::TextPath(draw, _, _) = &mut command {
                                    paths
                                        .entry(key)
                                        .or_insert((source, Some(draw.path.clone())));
                                }
                                scene.commands.push(command);
                            } else {
                                paths.entry(key).or_insert((source, None));
                            }
                        }
                    }
                }
            } else if let Command::HintedText(record) = command {
                let mut context = record.1;
                context.dpi_scale = dpi;
                let resolved = record.0.resolve_pending(atlas, context).await?;
                let base = styles.len();
                let count = base
                    .checked_add(resolved.styles.len())
                    .ok_or(Error::Capacity)?;
                if count > (1 << 24) {
                    return Err(Error::Capacity);
                }
                styles
                    .try_reserve(resolved.styles.len())
                    .map_err(|_| Error::Capacity)?;
                styles.extend_from_slice(&resolved.styles);
                scene
                    .commands
                    .try_reserve(resolved.instances.len())
                    .map_err(|_| Error::Capacity)?;
                if let Some(paint) = resolved.material {
                    scene.commands.push(Command::HintedGlyphPaint(Box::new(
                        crate::text::hinted_replay::HintedMaterialInstances {
                            instances: resolved.instances,
                            paint,
                        },
                    )));
                } else {
                    for mut instance in resolved.instances {
                        if instance.brush_index >= 0.0 {
                            instance.brush_index += base as f32;
                        }
                        scene.commands.push(Command::Glyph(instance));
                    }
                }
            } else {
                scene.commands.push(command.clone());
            }
        }
        atlas.end_batch().await?;
        atlas.flush_pending().await?;
        // A later command can grow either atlas. Capture the FINAL textures and
        // revision once, after every pixel-coordinate instance is assembled.
        Ok(ResolvedSceneText {
            scene,
            styles,
            residency: atlas.capture_batch_residency(residency_start, None)?,
            statistics: atlas.statistics(),
            coverage: atlas.coverage().clone(),
            color: atlas.color().clone(),
        })
    }
}
