//! Whole-plan source/device/numeric/work admission before candidate GPU creation.
use super::*;
use crate::{effects::BlurUniforms, image_filter::FilterSampling, vector::Matrix4};
pub(super) fn add_cost(total: &mut (u64, u32), value: (u64, u32)) -> Result<(), Error> {
    total.0 = total.0.checked_add(value.0).ok_or(Error::Capacity)?;
    total.1 = total.1.checked_add(value.1).ok_or(Error::Capacity)?;
    if total.0 > 512 * 1024 * 1024 || total.1 > 16_384 {
        return Err(Error::Capacity);
    }
    Ok(())
}
pub(super) fn scene(renderer: &Renderer, scene: &Scene, depth: usize) -> Result<(u64, u32), Error> {
    if depth >= 16 {
        return Err(Error::Capacity);
    }
    let mut total = (0, 0);
    for draw in scene.image_filtered_images() {
        add_cost(&mut total, draw_cost(renderer, draw, depth)?)?;
    }
    Ok(total)
}
pub(super) fn draw(
    renderer: &Renderer,
    draw: &ImageFilteredImageDraw,
    depth: usize,
) -> Result<(u64, u32), Error> {
    draw_cost(renderer, draw, depth)
}
fn draw_cost(
    renderer: &Renderer,
    draw: &ImageFilteredImageDraw,
    depth: usize,
) -> Result<(u64, u32), Error> {
    if depth >= 16 {
        return Err(Error::Capacity);
    }
    draw.validate()?;
    draw.preflight_source_devices(&renderer.device)?;
    image_source(renderer, &draw.draw.image)?;
    let plan = draw.plan()?;
    let r = plan.resources();
    let mut total = (
        r.texture_bytes,
        r.render_passes
            .checked_add(r.compute_passes)
            .and_then(|v| v.checked_add(r.copy_passes))
            .ok_or(Error::Capacity)?,
    );
    let limits = renderer.device.limits();
    if limits.max_bind_groups < 4
        || limits.max_uniform_buffer_binding_size < 288
        || limits.max_storage_buffer_binding_size < 4096
        || limits.max_buffer_size < 4096
        || limits.max_sampled_textures_per_shader_stage < 3
        || limits.max_uniform_buffers_per_shader_stage < 4
    {
        return Err(Error::UnsupportedExecution);
    }
    if ImageFilteredImageDraw::needs_normalization(&draw.draw.image) {
        add_cost(
            &mut total,
            (
                u64::from(plan.request().source_size[0])
                    * u64::from(plan.request().source_size[1])
                    * 4,
                1,
            ),
        )?;
    }
    for step in plan.steps() {
        extent(&renderer.device, step.size)?;
        for &input in step.inputs.iter() {
            if input as usize > plan.steps().len() {
                return Err(Error::ResourceDomain);
            }
        }
        match &step.operation {
            Operation::GaussianBlur(parameters) => {
                gaussian(&renderer.device, step.size, parameters)?
            }
            Operation::Morphology(parameters) => {
                compute(&renderer.device, step.size)?;
                for p in parameters {
                    p.validate()?;
                }
                if parameters[0].direction != [1, 0]
                    || parameters[1].direction != [0, 1]
                    || parameters[0].dilate != parameters[1].dilate
                {
                    return Err(Error::InvalidInput);
                }
                if parameters.iter().all(|p| p.radius == 0)
                    && step.inputs.first() == Some(&0)
                    && !ImageFilteredImageDraw::needs_normalization(&draw.draw.image)
                {
                    if let ImageSource::Gpu(source) = &draw.draw.image {
                        if !source
                            .texture()
                            .usage()
                            .contains(wgpu::TextureUsages::COPY_SRC)
                        {
                            return Err(Error::ResourceDomain);
                        }
                    }
                }
            }
            Operation::Convolution { parameters, kernel } => {
                compute(&renderer.device, step.size)?;
                parameters.validate()?;
                let count = parameters.kernel_size[0] as usize * parameters.kernel_size[1] as usize;
                if kernel.len() < count {
                    return Err(Error::InvalidInput);
                }
                if count as u64 * 4 > u64::from(limits.max_storage_buffer_binding_size) {
                    return Err(Error::Capacity);
                }
            }
            Operation::Displacement(p) => {
                compute(&renderer.device, step.size)?;
                p.validate()?;
            }
            Operation::Magnifier(p) => {
                compute(&renderer.device, step.size)?;
                p.validate()?;
            }
            Operation::Lighting(p) => {
                compute(&renderer.device, step.size)?;
                p.validate()?;
            }
            Operation::ColorMatrix(m) => {
                if m.managed_values().iter().any(|v| !v.is_finite()) {
                    return Err(Error::InvalidInput);
                }
            }
            Operation::ColorTable(_) | Operation::TransferToSrgb(_) | Operation::Blend(_) => {
                compute(&renderer.device, step.size)?
            }
            Operation::NonlinearColor(p) => {
                compute(&renderer.device, step.size)?;
                if bytemuck::cast_slice::<_, f32>(std::slice::from_ref(p))
                    .iter()
                    .any(|v| !v.is_finite())
                    || !matches!(p.configuration[0], 0.0 | 1.0)
                    || !matches!(p.configuration[1], 0.0 | 1.0)
                    || !matches!(p.configuration[2], 0.0 | 1.0 | 2.0)
                    || p.configuration[3] < 0.0
                {
                    return Err(Error::InvalidInput);
                }
            }
            Operation::Overdraw(p) => {
                compute(&renderer.device, step.size)?;
                if p.colors
                    .iter()
                    .flatten()
                    .any(|v| !v.is_finite() || !(0.0..=1.0).contains(v))
                {
                    return Err(Error::InvalidInput);
                }
            }
            Operation::Arithmetic(p) => {
                compute(&renderer.device, step.size)?;
                if p.coefficients.iter().any(|v| !v.is_finite()) {
                    return Err(Error::InvalidInput);
                }
            }
            Operation::Composite { offsets } => {
                if offsets.len() != step.inputs.len()
                    || offsets.iter().flatten().any(|v| !v.is_finite())
                {
                    return Err(Error::InvalidInput);
                }
            }
            Operation::MatrixTransform { matrix, sampling } => {
                finite_matrix(*matrix)?;
                sampling_record(*sampling)?;
            }
            Operation::Crop { rect, transform } => {
                let mut clip = Scene::default();
                clip.push_image_filter_clip(rect.0, transform.effective())?;
            }
            Operation::Tile {
                source,
                destination,
                range,
            } => {
                if !source.is_valid()
                    || !destination.is_valid()
                    || range[0] > range[2]
                    || range[1] > range[3]
                {
                    return Err(Error::InvalidInput);
                }
                let draws = (i64::from(range[2]) - i64::from(range[0])) as u64
                    * (i64::from(range[3]) - i64::from(range[1])) as u64;
                if draws > plan.request().maximum_draws || draws * 248 > limits.max_buffer_size {
                    return Err(Error::Capacity);
                }
            }
            Operation::ImageSource(data) => {
                image_source(renderer, &data.source.image)?;
                sampling_record(data.sampling)?;
                let mut image =
                    ImageDraw::new(data.source.image.clone(), capture::rect(data.destination));
                image.source = capture::rect(data.source_rect);
                image.extended_source = true;
                capture::sample(&mut image, data.sampling);
                image.validate()?;
            }
            Operation::PictureSource { source, transform } => {
                let mut wrapper = Scene::default();
                wrapper.push_image_filter_clip(source.target.0, Matrix4::IDENTITY)?;
                wrapper.append(&source.scene)?;
                wrapper.pop_scope()?;
                let frame = crate::composition::SourceRenderFrame::image_filter(
                    step.size,
                    transform.effective(),
                )?;
                let placed = wrapper.place_source_capture(frame)?;
                source_scene(renderer, &placed, step.size, depth + 1, &mut total)?;
            }
            Operation::ShaderSource {
                source, transform, ..
            } => {
                add_cost(
                    &mut total,
                    source::preflight(renderer, source, *transform, step.size, depth + 1)?,
                )?;
            }
            Operation::UnsupportedRuntimeBlender(_) | Operation::RuntimeBlender(_) => {
                return Err(Error::UnsupportedExecution);
            }
            Operation::Transparent | Operation::Solid(_) => {}
        }
    }
    if draw.draw.sampling.uses_mipmaps() {
        if plan.output() == 0 && !ImageFilteredImageDraw::needs_normalization(&draw.draw.image) {
            if let ImageSource::Gpu(image) = &draw.draw.image {
                if !image
                    .texture()
                    .usage()
                    .contains(wgpu::TextureUsages::COPY_SRC)
                {
                    return Err(Error::ResourceDomain);
                }
            }
        }
        let (bytes, levels) = cf::mip_storage(plan.output_size());
        add_cost(&mut total, (bytes, levels - 1))?;
    }
    if total.0 > draw.request.maximum_texture_bytes || total.1 > 16_384 {
        return Err(Error::Capacity);
    }
    Ok(total)
}
pub(super) fn source_scene(
    renderer: &Renderer,
    source: &Scene,
    size: [u32; 2],
    depth: usize,
    cost: &mut (u64, u32),
) -> Result<(), Error> {
    if depth >= 16 {
        return Err(Error::Capacity);
    }
    source.preflight_source_capture_devices(&renderer.device)?;
    add_cost(cost, scene(renderer, source, depth)?)?;
    let frame: RenderFrame = Frame {
        width: size[0],
        height: size[1],
        dpi_scale: 1.0,
    }
    .into();
    if !source.has_text() && source.layer_depth() == 0 && !source.has_axis_clips() {
        let mut materials = MaterialTable::default();
        let compiled = source.compile_with_sampling(
            1.0,
            &mut materials,
            renderer
                .image_sampling
                .unwrap_or(ImageSamplingPath::NativeSampler),
        )?;
        renderer.validate_geometry(&compiled.mesh, frame)?;
        renderer.validate_materials(&compiled.mesh, Some(compiled.materials()))?;
        renderer.validate_masks(&compiled)?;
    }
    renderer.preflight_shaders(source, frame)?;
    renderer.preflight_managed_shaders(source, frame)?;
    if source.layer_depth() != 0 {
        renderer.preflight_layers(source, frame)?;
    } else {
        add_cost(cost, renderer.preflight_image_effects(source)?)?;
        add_cost(cost, renderer.preflight_color_filters(source)?)?;
        let (states, changes) = source.compile_states(1.0)?;
        renderer.validate_mask_states(&states)?;
        add_cost(
            cost,
            renderer.mask_resource_cost(source, &states, &changes, frame)?,
        )?;
    }
    Ok(())
}
pub(super) fn image_source(renderer: &Renderer, source: &ImageSource) -> Result<(), Error> {
    extent(&renderer.device, source.size())?;
    if let ImageSource::Gpu(image) = source {
        if image.device() != &renderer.device
            || !image
                .texture()
                .usage()
                .contains(wgpu::TextureUsages::TEXTURE_BINDING)
        {
            return Err(Error::ResourceDomain);
        }
        if !matches!(
            image.texture().format(),
            wgpu::TextureFormat::Rgba8Unorm
                | wgpu::TextureFormat::Bgra8Unorm
                | wgpu::TextureFormat::Rgba8UnormSrgb
                | wgpu::TextureFormat::Bgra8UnormSrgb
                | wgpu::TextureFormat::R8Unorm
        ) {
            return Err(Error::UnsupportedExecution);
        }
    }
    Ok(())
}
pub(super) fn extent(device: &wgpu::Device, size: [u32; 2]) -> Result<(), Error> {
    if size
        .iter()
        .any(|v| *v == 0 || *v > device.limits().max_texture_dimension_2d)
    {
        Err(Error::Capacity)
    } else {
        Ok(())
    }
}
pub(super) fn compute(device: &wgpu::Device, size: [u32; 2]) -> Result<(), Error> {
    extent(device, size)?;
    let l = device.limits();
    if l.max_compute_invocations_per_workgroup < 256
        || l.max_compute_workgroup_size_x < 16
        || l.max_compute_workgroup_size_y < 16
        || l.max_storage_textures_per_shader_stage < 1
        || l.max_sampled_textures_per_shader_stage < 2
        || l.max_storage_buffers_per_shader_stage < 1
    {
        return Err(Error::UnsupportedExecution);
    }
    if size
        .iter()
        .any(|v| v.div_ceil(16) > l.max_compute_workgroups_per_dimension)
    {
        return Err(Error::Capacity);
    }
    Ok(())
}
pub(super) fn gaussian(
    device: &wgpu::Device,
    size: [u32; 2],
    parameters: &[BlurUniforms; 2],
) -> Result<(), Error> {
    compute(device, size)?;
    if parameters.iter().any(|p| {
        !p.sigma.is_finite()
            || p.sigma < 0.0
            || p.radius > 128
            || p.kernel_type != 0
            || p.padding != 0
    }) {
        return Err(Error::InvalidInput);
    }
    Ok(())
}
pub(super) fn finite_matrix(matrix: Matrix4) -> Result<(), Error> {
    if matrix.0.iter().flatten().any(|v| !v.is_finite()) {
        Err(Error::InvalidInput)
    } else {
        Ok(())
    }
}
pub(super) fn sampling_record(sampling: FilterSampling) -> Result<(), Error> {
    if sampling
        .cubic
        .iter()
        .any(|v| !v.is_finite() || !(-16.0..=16.0).contains(v))
    {
        Err(Error::InvalidInput)
    } else {
        Ok(())
    }
}
