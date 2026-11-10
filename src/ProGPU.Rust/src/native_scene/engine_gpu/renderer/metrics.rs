//! Original frame accounting and independent actual subtree diagnostics.
//!
//! FrameMetrics is deliberately not a sum of every uploaded Rust buffer. The
//! native executor reports path vertices/indices and glyph instances, excludes
//! their outline/program slabs, and forwards only selected child counters.
use super::*;
use crate::native_scene::wire::WireRecord;

fn sum(values: impl IntoIterator<Item = u64>) -> Result<u64, Error> {
    values
        .into_iter()
        .try_fold(0u64, |a, b| a.checked_add(b).ok_or(Error::Capacity))
}
fn count(value: u64) -> Result<u32, Error> {
    u32::try_from(value).map_err(|_| Error::Capacity)
}
fn add(a: &mut u64, b: u64) -> Result<(), Error> {
    *a = a.checked_add(b).ok_or(Error::Capacity)?;
    Ok(())
}
fn add_count(a: &mut u32, b: u64) -> Result<(), Error> {
    *a = count(sum([u64::from(*a), b])?)?;
    Ok(())
}
fn remaining(all: u64, represented: u64) -> Result<u64, Error> {
    all.checked_sub(represented).ok_or(Error::ResourceDomain)
}
fn frame(plan: &NativeSceneResourcePlan) -> wire::FrameMetrics {
    wire::FrameMetrics {
        struct_size: wire::FrameMetrics::WIRE_SIZE as u32,
        command_count: plan.scene().envelope().header().command_count,
        payload_hash: crate::native_scene::identity::raw_fnv1a64(plan.scene().envelope().bytes()),
        ..Default::default()
    }
}
fn page_frame(
    frame: &mut wire::FrameMetrics,
    page: NativeScenePageMetrics,
    glyph_paint_bytes: u64,
) -> Result<(), Error> {
    frame.vertex_upload_bytes = sum([
        page.vector.vertex_upload_bytes,
        page.paths.vertex_upload_bytes,
        page.glyphs.instance_upload_bytes,
        page.images.vertex_bytes,
        page.three_d.upload_bytes,
    ])?;
    frame.index_upload_bytes = sum([
        page.vector.index_upload_bytes,
        page.paths.index_upload_bytes,
        page.images.index_bytes,
    ])?;
    frame.texture_upload_bytes = sum([
        page.paths.texture_upload_bytes,
        page.glyphs.texture_upload_bytes,
        page.images.texture_upload_bytes,
    ])?;
    frame.uniform_upload_bytes = sum([page.images.uniform_upload_bytes, glyph_paint_bytes])?;
    frame.coverage_staging_bytes = sum([
        page.paths.coverage_staging_bytes,
        page.glyphs.coverage_staging_bytes,
    ])?;
    frame.brush_upload_bytes = page.materials.brush_upload_bytes;
    frame.gradient_stop_upload_bytes = page.materials.gradient_upload_bytes;
    frame.text_style_upload_bytes = page.materials.style_upload_bytes;
    // Explicit CPU grayscale writes retain their separate outline/arena metric;
    // this field is only the original uploaded color bitmap row inventory.
    frame.color_glyph_upload_bytes = page.glyphs.texture_upload_bytes;
    Ok(())
}
fn image_frame(
    frame: &mut wire::FrameMetrics,
    image: NativeImageResourceMetrics,
) -> Result<(), Error> {
    add(&mut frame.vertex_upload_bytes, image.vertex_bytes)?;
    add(&mut frame.index_upload_bytes, image.index_bytes)?;
    add(&mut frame.texture_upload_bytes, image.texture_upload_bytes)?;
    add(&mut frame.uniform_upload_bytes, image.uniform_upload_bytes)
}
fn layer_bytes(m: NativeLayerResourceMetrics) -> Result<u64, Error> {
    sum([m.color_bytes, m.depth_bytes, m.effect_bytes])
}
fn layer_allocations(m: NativeLayerResourceMetrics) -> Result<u64, Error> {
    sum([
        m.color_allocations,
        m.depth_allocations,
        m.effect_allocations,
    ])
}

/// A cold, conservative child-resource inventory. This is a preflight budget,
/// not evidence that a pass, upload or submission occurred. Actual preparation
/// and execution replace these estimates before a capture is published.
pub(super) fn preflight(
    plan: &NativeSceneResourcePlan,
    pages: NativeScenePageMetrics,
    masks: NativeMaskMetrics,
    shaders: &[NativeShaderEffectMetrics],
) -> Result<NativePictureWorkMetrics, Error> {
    let draws = plan.draws().metrics();
    let layers = plan.layers().metrics();
    let budget = plan.budget();
    let mut result = NativePictureWorkMetrics {
        frame: frame(plan),
        ..Default::default()
    };
    page_frame(
        &mut result.frame,
        pages,
        u64::from(draws.glyph_paint_count) * 96,
    )?;
    result.frame.draw_call_count = count(sum([
        u64::from(draws.draw_count),
        u64::from(layers.materialized_layer_count),
        u64::from(layers.advanced_layer_count) * 3,
        u64::from(layers.backdrop_layer_count) * 3,
    ])?)?;
    // The complete layer admission already retains pooled maxima, including
    // all effect/cache/destination slots. Mask device preflight may require a
    // larger retained atlas than its source minimum; keep that separate cost.
    result.texture_bytes = sum([
        pages.paths.texture_bytes,
        pages.glyphs.texture_bytes,
        pages.images.texture_bytes,
        pages.three_d.texture_bytes,
        budget.retained_layer_bytes,
        masks.texture_bytes,
    ])?;
    result.texture_allocations = sum([
        u64::from(plan.paths().is_some()),
        u64::from(plan.glyphs().is_some()) * 2,
        pages.images.texture_allocations,
        pages.three_d.texture_allocations,
        masks.texture_allocations,
        u64::from(layers.materialized_layer_count) * 4,
        budget.destination_texture_count,
    ])?;
    add(
        &mut result.frame.texture_upload_bytes,
        masks.texture_upload_bytes,
    )?;
    add(
        &mut result.frame.uniform_upload_bytes,
        sum([
            masks.uniform_upload_bytes,
            remaining(budget.all_effect_bytes, layers.shader_sampler_bytes)?,
            224,
            u64::from(layers.materialized_layer_count) * 224,
            u64::from(layers.advanced_layer_count) * 96,
        ])?,
    )?;
    result.render_passes = count(sum([
        1,
        u64::from(draws.draw_count),
        u64::from(layers.materialized_layer_count) * 2,
        u64::from(layers.advanced_layer_count) * 3,
        u64::from(layers.backdrop_layer_count) * 3,
        pages.paths.raster_passes,
        pages.glyphs.raster_passes,
        u64::from(masks.render_passes),
    ])?)?;
    result.compute_passes = count(sum([
        pages.paths.compute_passes,
        pages.glyphs.compute_passes,
        pages.images.live_blur_passes,
        u64::from(masks.compute_passes),
        u64::from(layers.effect_pass_count - layers.shader_effect_count),
    ])?)?;
    result.path_rasters = count(sum([
        pages.paths.raster_count,
        u64::from(masks.path_rasters),
    ])?)?;
    result.nested_captures = count(sum([
        pages.images.nested_pictures,
        u64::from(masks.picture_captures),
    ])?)?;
    result.live_blur_passes = pages.images.live_blur_passes;
    for shader in shaders {
        image_frame(&mut result.frame, shader.pictures)?;
        add(
            &mut result.texture_bytes,
            sum([
                shader.pictures.texture_bytes,
                shader.shader.output_texture_bytes,
            ])?,
        )?;
        add(
            &mut result.texture_allocations,
            shader.pictures.texture_allocations,
        )?;
        add_count(&mut result.nested_captures, shader.pictures.nested_pictures)?;
        add_count(&mut result.render_passes, shader.legacy_passes)?;
        add_count(&mut result.compute_passes, shader.pictures.live_blur_passes)?;
        add(
            &mut result.live_blur_passes,
            shader.pictures.live_blur_passes,
        )?;
    }
    for page in plan.rgb().iter().flatten() {
        let m = page.metrics();
        add(&mut result.frame.vertex_upload_bytes, m.vertex_upload_bytes)?;
        add(
            &mut result.frame.uniform_upload_bytes,
            m.uniform_upload_bytes,
        )?;
        add(&mut result.texture_bytes, m.texture_bytes)?;
        add(
            &mut result.texture_allocations,
            u64::from(page.drawable()) * 2,
        )?;
        add_count(&mut result.render_passes, u64::from(m.raster_passes))?;
        add_count(&mut result.compute_passes, u64::from(m.compute_passes))?;
    }
    Ok(result)
}

pub(super) fn prepared(
    resources: &NativePreparedReplayResources,
    rgb: &[Option<PreparedNativeRgbGlyphPage>],
    warm_pages: bool,
) -> Result<NativeSceneRenderMetrics, Error> {
    let plan = resources.source.source();
    let pages = if warm_pages {
        NativeScenePageMetrics {
            images: resources
                .pages
                .images()
                .map_or(Default::default(), |p| p.replay_metrics()),
            three_d: resources
                .pages
                .three_d()
                .map_or(Default::default(), |p| p.replay_metrics()),
            ..Default::default()
        }
    } else {
        resources.pages.metrics()
    };
    let mut result = NativeSceneRenderMetrics {
        frame: frame(plan),
        pages,
        frames: resources.frames.metrics(),
        layers: resources.frames.targets().metrics(),
        composite: resources
            .composite
            .as_ref()
            .map_or(Default::default(), |p| p.metrics()),
        effects: resources
            .effects
            .as_ref()
            .map_or(Default::default(), |p| p.metrics()),
        advanced: resources
            .advanced
            .as_ref()
            .map_or(Default::default(), |p| p.metrics()),
        cache_hits: u32::from(warm_pages),
        ..Default::default()
    };
    let paint_bytes = if warm_pages {
        0
    } else {
        plan.glyphs().map_or(0, |g| g.paints().len() as u64 * 96)
    };
    page_frame(&mut result.frame, pages, paint_bytes)?;
    result.frame.family_switch_count = count(resources.source.metrics().family_switches)?;
    add(
        &mut result.frame.vertex_upload_bytes,
        result.composite.vertex_upload_bytes,
    )?;
    add(
        &mut result.frame.uniform_upload_bytes,
        sum([
            result.frames.root_uniform_upload_bytes,
            result.layers.uniform_upload_bytes,
            result.effects.uniform_upload_bytes,
            result.advanced.uniform_upload_bytes,
            result.advanced.layers.uniform_upload_bytes,
        ])?,
    )?;
    result.texture_bytes = sum([
        pages.paths.texture_bytes,
        pages.glyphs.texture_bytes,
        pages.images.texture_bytes,
        pages.three_d.texture_bytes,
        layer_bytes(result.layers)?,
        layer_bytes(result.advanced.layers)?,
        result.advanced.coverage_texture_bytes,
    ])?;
    result.texture_allocations = sum([
        u64::from(!warm_pages && resources.pages.paths().is_some()),
        u64::from(!warm_pages && resources.pages.glyphs().is_some()) * 2,
        pages.images.texture_allocations,
        pages.three_d.texture_allocations,
        layer_allocations(result.layers)?,
        layer_allocations(result.advanced.layers)?,
        result.advanced.coverage_texture_allocations,
    ])?;
    result.render_passes = count(sum([
        pages.paths.raster_passes,
        pages.glyphs.raster_passes,
    ])?)?;
    result.compute_passes = count(sum([
        pages.paths.compute_passes,
        pages.glyphs.compute_passes,
        pages.images.live_blur_passes,
    ])?)?;
    result.path_rasters = count(pages.paths.raster_count)?;
    result.nested_captures = count(pages.images.nested_pictures)?;
    result.live_blur_passes = pages.images.live_blur_passes;
    for mask in &resources.masks {
        result.masks.add(mask.metrics())?;
    }
    let masks = result.masks;
    // Source masks have their own storage/geometry diagnostics. Only original
    // bitmap uploads and mask uniforms enter parent frame accounting.
    add(
        &mut result.frame.texture_upload_bytes,
        masks.texture_upload_bytes,
    )?;
    add(
        &mut result.frame.uniform_upload_bytes,
        masks.uniform_upload_bytes,
    )?;
    add(&mut result.frame.submission_count, masks.submission_count)?;
    add(&mut result.texture_bytes, masks.texture_allocation_bytes)?;
    add(&mut result.texture_allocations, masks.texture_allocations)?;
    add_count(&mut result.render_passes, u64::from(masks.render_passes))?;
    add_count(&mut result.compute_passes, u64::from(masks.compute_passes))?;
    add_count(&mut result.path_rasters, u64::from(masks.path_rasters))?;
    add_count(
        &mut result.nested_captures,
        u64::from(masks.picture_captures),
    )?;
    add_count(
        &mut result.cache_hits,
        sum([
            u64::from(masks.picture_cache_hits),
            u64::from(masks.clip_cache_hits),
        ])?,
    )?;
    for shader in resources.shaders.iter().flatten() {
        let m = shader.metrics();
        image_frame(&mut result.frame, m.pictures)?;
        if m.shader.uniform_bytes != 0 {
            add(
                &mut result.frame.uniform_upload_bytes,
                u64::from(shader.plan().replay().uniform_bytes),
            )?;
        }
        add(
            &mut result.shader_uniform_upload_bytes,
            m.shader.uniform_bytes,
        )?;
        add(
            &mut result.texture_bytes,
            sum([m.pictures.texture_bytes, m.shader.output_texture_bytes])?,
        )?;
        add(
            &mut result.texture_allocations,
            m.pictures.texture_allocations,
        )?;
        add_count(&mut result.nested_captures, m.pictures.nested_pictures)?;
        add_count(&mut result.compute_passes, m.pictures.live_blur_passes)?;
        add(&mut result.live_blur_passes, m.pictures.live_blur_passes)?;
    }
    for packet in rgb.iter().flatten() {
        let m = packet.metrics();
        add(&mut result.frame.vertex_upload_bytes, m.vertex_upload_bytes)?;
        add(
            &mut result.frame.uniform_upload_bytes,
            m.uniform_upload_bytes,
        )?;
        add(&mut result.texture_bytes, m.texture_bytes)?;
        add(
            &mut result.texture_allocations,
            u64::from(packet.page().drawable()) * 2,
        )?;
        // Passes/draws are recorded only when the actual ordered packet encodes.
    }
    Ok(result)
}

pub(super) fn executed(
    result: &mut NativeSceneRenderMetrics,
    execution: NativeReplayExecutionMetrics,
) -> Result<(), Error> {
    let mut candidate = *result;
    candidate.frame.draw_call_count = count(execution.draw_calls)?;
    add(&mut candidate.frame.submission_count, execution.submissions)?;
    add(
        &mut candidate.frame.uniform_upload_bytes,
        execution.clear_uniform_upload_bytes,
    )?;
    add_count(&mut candidate.render_passes, execution.render_passes)?;
    add_count(&mut candidate.compute_passes, execution.compute_passes)?;
    add_count(&mut candidate.cache_hits, execution.effect_cache_hits)?;
    candidate.execution = execution;
    *result = candidate;
    Ok(())
}

/// Actual child work is already represented by the image/shader/mask upload
/// adapters. Add only omitted diagnostics and submission deltas; preserve the
/// original picture-mask exception (child uniforms, not child texture/geometry).
pub(super) fn nested(
    result: &mut NativeSceneRenderMetrics,
    work: NativePictureWorkMetrics,
    mask_work: NativePictureWorkMetrics,
) -> Result<(), Error> {
    let mut candidate = *result;
    candidate.frame.texture_upload_bytes = remaining(
        candidate.frame.texture_upload_bytes,
        mask_work.frame.texture_upload_bytes,
    )?;
    add(
        &mut candidate.frame.submission_count,
        work.frame.submission_count,
    )?;
    add_count(
        &mut candidate.render_passes,
        remaining(
            u64::from(work.render_passes),
            u64::from(mask_work.render_passes),
        )?,
    )?;
    let other_compute = remaining(
        u64::from(work.compute_passes),
        u64::from(mask_work.compute_passes),
    )?;
    let other_blur = remaining(work.live_blur_passes, mask_work.live_blur_passes)?;
    add_count(
        &mut candidate.compute_passes,
        remaining(other_compute, other_blur)?,
    )?;
    add_count(
        &mut candidate.path_rasters,
        remaining(
            u64::from(work.path_rasters),
            u64::from(mask_work.path_rasters),
        )?,
    )?;
    add_count(
        &mut candidate.cache_hits,
        remaining(u64::from(work.cache_hits), u64::from(mask_work.cache_hits))?,
    )?;
    add(&mut candidate.live_blur_passes, mask_work.live_blur_passes)?;
    candidate.nested_work = work;
    *result = candidate;
    Ok(())
}
