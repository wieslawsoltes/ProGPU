//! Original uniform/coverage rings, ordered passes and portable packed-R8 copies.
use super::*;

impl GlyphRasterizer {
    /// Clear the same actual coverage attachment without changing its identity.
    /// Callers must invalidate associated residency before publishing new draws.
    pub async fn clear_coverage_texture(&self, target: &GlyphCoverageTexture) -> Result<(), Error> {
        if !Arc::ptr_eq(&target.owner, &self.owner) {
            return Err(Error::ResourceDomain);
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let view = target.texture.create_view(&Default::default());
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU clear existing glyph coverage"),
            });
        {
            let _pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("ProGPU clear existing glyph coverage"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
        }
        self.queue.submit([encoder.finish()]);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(())
    }
    /// Create and clear an actual R8 target on this device. Atlas packing is a
    /// separate owner; returned textures never imply glyph residency by themselves.
    pub async fn create_coverage_texture(
        &self,
        size: [u32; 2],
    ) -> Result<GlyphCoverageTexture, Error> {
        self.create_target(size, wgpu::TextureFormat::R8Unorm).await
    }
    /// Create and clear the original separate RGBA color atlas attachment.
    pub async fn create_color_texture(&self, size: [u32; 2]) -> Result<GlyphColorTexture, Error> {
        Ok(GlyphColorTexture(
            self.create_target(size, wgpu::TextureFormat::Rgba8Unorm)
                .await?,
        ))
    }
    async fn create_target(
        &self,
        size: [u32; 2],
        format: wgpu::TextureFormat,
    ) -> Result<GlyphCoverageTexture, Error> {
        if size.contains(&0)
            || size
                .iter()
                .any(|&s| s > self.device.limits().max_texture_dimension_2d)
        {
            return Err(Error::InvalidInput);
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU glyph coverage atlas generation"),
            size: wgpu::Extent3d {
                width: size[0],
                height: size[1],
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format,
            usage: wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC
                | wgpu::TextureUsages::COPY_DST
                | wgpu::TextureUsages::RENDER_ATTACHMENT,
            view_formats: &[],
        });
        let view = texture.create_view(&Default::default());
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU clear glyph atlas"),
            });
        {
            let _pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("ProGPU clear glyph coverage"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                ..Default::default()
            });
        }
        self.queue.submit([encoder.finish()]);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(GlyphCoverageTexture {
            owner: self.owner.clone(),
            texture,
            size,
        })
    }
    /// Grow a coverage generation with the original GPU-only base-level copy.
    /// The old texture remains valid for already-retained consumer views/commands.
    pub async fn grow_coverage_texture(
        &self,
        previous: &GlyphCoverageTexture,
        size: [u32; 2],
    ) -> Result<GlyphCoverageTexture, Error> {
        if !Arc::ptr_eq(&previous.owner, &self.owner) {
            return Err(Error::ResourceDomain);
        }
        if size[0] < previous.size[0] || size[1] < previous.size[1] {
            return Err(Error::InvalidInput);
        }
        let next = self.create_target(size, previous.texture.format()).await?;
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU grow glyph coverage"),
            });
        encoder.copy_texture_to_texture(
            previous.texture.as_image_copy(),
            next.texture.as_image_copy(),
            wgpu::Extent3d {
                width: previous.size[0],
                height: previous.size[1],
                depth_or_array_layers: 1,
            },
        );
        self.queue.submit([encoder.finish()]);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(next)
    }
    /// Grow color storage with the same original GPU-only clear/base-level copy.
    pub async fn grow_color_texture(
        &self,
        previous: &GlyphColorTexture,
        size: [u32; 2],
    ) -> Result<GlyphColorTexture, Error> {
        Ok(GlyphColorTexture(
            self.grow_coverage_texture(&previous.0, size).await?,
        ))
    }
    /// Clear the same actual color attachment without replacing its identity.
    pub async fn clear_color_texture(&self, target: &GlyphColorTexture) -> Result<(), Error> {
        self.clear_coverage_texture(&target.0).await
    }
    /// Write exact tight straight-alpha RGBA bytes into a selected color region.
    /// Queue texture writes retain the original unpadded row pitch; no readback,
    /// premultiplication, resizing or synthetic GPU raster submission is added.
    pub async fn write_color_glyph(
        &mut self,
        target: &GlyphColorTexture,
        origin: [u32; 2],
        size: [u32; 2],
        pixels: &[u8],
    ) -> Result<(), Error> {
        if !Arc::ptr_eq(&target.0.owner, &self.owner) {
            return Err(Error::ResourceDomain);
        }
        let row = size[0].checked_mul(4).ok_or(Error::Capacity)?;
        if size.contains(&0) || u64::from(row) * u64::from(size[1]) != pixels.len() as u64 {
            return Err(Error::InvalidInput);
        }
        for axis in 0..2 {
            if origin[axis] > target.0.size[axis] || size[axis] > target.0.size[axis] - origin[axis]
            {
                return Err(Error::InvalidInput);
            }
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        self.queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &target.0.texture,
                mip_level: 0,
                origin: wgpu::Origin3d {
                    x: origin[0],
                    y: origin[1],
                    z: 0,
                },
                aspect: wgpu::TextureAspect::All,
            },
            pixels,
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(row),
                rows_per_image: Some(size[1]),
            },
            wgpu::Extent3d {
                width: size[0],
                height: size[1],
                depth_or_array_layers: 1,
            },
        );
        self.metrics.bitmap_upload_writes += 1;
        self.metrics.bitmap_upload_bytes += pixels.len() as u64;
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(())
    }
    /// Rasterize a complete request inventory with bounded reusable staging.
    /// Admission precedes GPU mutation. Normal groups share one pass/submission;
    /// oversized compute glyphs use the original separate immediate route.
    /// No CPU coverage, readback, repacking, driver replacement or shader changes.
    /// Caller atlas owners publish requested regions only after success.
    pub async fn rasterize_into(
        &mut self,
        target: &GlyphCoverageTexture,
        requests: &[GlyphRasterRequest],
    ) -> Result<(), Error> {
        if !Arc::ptr_eq(&target.owner, &self.owner) {
            return Err(Error::ResourceDomain);
        }
        let mut binary = false;
        for request in requests {
            self.validate_request(target, request)?;
            binary |= request.mode == GlyphRasterMode::Binary;
        }
        if requests.is_empty() {
            return Ok(());
        }
        if binary {
            self.ensure_binary().await?;
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        self.flush_outline_uploads();
        let mut start = 0;
        while start < requests.len() {
            let first_bytes =
                coverage_bytes(&requests[start]).expect("preflight checked coverage capacity");
            if self.path == GlyphExecutionPath::NativeCompute
                && first_bytes > self.options.coverage_ring_bytes
            {
                self.encode_group(target, &requests[start..start + 1], true);
                start += 1;
                continue;
            }
            let mut end = start;
            let mut uniform_bytes = 0u64;
            let mut coverage_end = 0u64;
            while end < requests.len() {
                let bytes = u64::from(
                    coverage_bytes(&requests[end]).expect("preflight checked coverage capacity"),
                );
                let next_coverage = align_copy(coverage_end) + bytes;
                if uniform_bytes + 256 > u64::from(self.options.uniform_ring_bytes)
                    || (self.path == GlyphExecutionPath::NativeCompute
                        && next_coverage > u64::from(self.options.coverage_ring_bytes))
                {
                    break;
                }
                uniform_bytes += 256;
                coverage_end = next_coverage;
                end += 1;
            }
            self.encode_group(target, &requests[start..end], false);
            start = end;
        }
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(())
    }
    pub(in crate::text::glyph) fn validate_request(
        &self,
        target: &GlyphCoverageTexture,
        request: &GlyphRasterRequest,
    ) -> Result<(), Error> {
        if !Arc::ptr_eq(&request.slot.owner, &self.owner)
            || request.slot.generation != self.generation
            || request.slot.index >= self.record_count
        {
            return Err(Error::ResourceDomain);
        }
        if request.size.contains(&0)
            || !request.scale.is_finite()
            || request.scale <= 0.0
            || !request.subpixel_x.is_finite()
            || request.origin.iter().any(|v| !v.is_finite())
            || request.slot.record.bounds.iter().any(|v| !v.is_finite())
            || request.slot.record.bounds[0] > request.slot.record.bounds[2]
            || request.slot.record.bounds[1] > request.slot.record.bounds[3]
        {
            return Err(Error::InvalidInput);
        }
        for axis in 0..2 {
            if request.atlas_origin[axis] > target.size[axis]
                || request.size[axis] > target.size[axis] - request.atlas_origin[axis]
            {
                return Err(Error::InvalidInput);
            }
        }
        let bytes = coverage_bytes(request)?;
        if self.path == GlyphExecutionPath::NativeCompute {
            check_buffer(&self.device, u64::from(bytes))?;
            let limit = self.device.limits().max_compute_workgroups_per_dimension;
            let groups = if request.mode == GlyphRasterMode::Binary {
                request.size
            } else {
                [
                    request.size[0].div_ceil(4).div_ceil(16),
                    request.size[1].div_ceil(16),
                ]
            };
            if groups.iter().any(|&g| g > limit) {
                return Err(Error::Capacity);
            }
        }
        if request.mode == GlyphRasterMode::Binary {
            if request.slot.record.segment_count == 0 {
                return Err(Error::InvalidInput);
            }
            if self.path != GlyphExecutionPath::NativeCompute
                || request.slot.record.segment_count > 256
            {
                return Err(Error::UnsupportedExecution);
            }
            // Original retained binary route consumes prepared physical outlines.
            // It does not scale, rephase or resnap them at the GPU boundary.
            if request.scale != 1.0
                || request.subpixel_x != 0.0
                || request.origin.iter().any(|&v| {
                    v.fract() != 0.0
                        || f64::from(v) < f64::from(i32::MIN)
                        || f64::from(v) > f64::from(i32::MAX)
                })
            {
                return Err(Error::InvalidInput);
            }
        }
        Ok(())
    }
    fn encode_group(
        &mut self,
        target: &GlyphCoverageTexture,
        requests: &[GlyphRasterRequest],
        oversized: bool,
    ) {
        let binary = requests.iter().any(|r| r.mode == GlyphRasterMode::Binary);
        let bytes =
            u64::from(coverage_bytes(&requests[0]).expect("preflight checked coverage capacity"));
        let local_uniform = oversized.then(|| {
            self.device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("ProGPU oversized glyph uniform"),
                size: 48,
                usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            })
        });
        let local_coverage = oversized.then(|| coverage_buffer(&self.device, bytes, true));
        let local_horizontal =
            (oversized && binary).then(|| coverage_buffer(&self.device, bytes, false));
        let uniform_buffer = local_uniform.as_ref().unwrap_or(&self.uniforms);
        let coverage = local_coverage.as_ref().or(self.coverage.as_ref());
        let horizontal = local_horizontal.as_ref().or(self.horizontal.as_ref());
        let mut offset = 0;
        for (index, request) in requests.iter().enumerate() {
            offset = align_copy(offset);
            let row = row_bytes(request.size[0]).expect("preflight checked row capacity");
            let uniforms = GlyphUniforms {
                x_start: request.origin[0],
                y_start: request.origin[1],
                scale: request.scale,
                glyph_index: request.slot.index,
                output_offset_words: if self.path == GlyphExecutionPath::NativeCompute {
                    (offset / 4) as u32
                } else {
                    0
                },
                output_row_words: if self.path == GlyphExecutionPath::NativeCompute {
                    row / 4
                } else {
                    0
                },
                width: request.size[0],
                height: request.size[1],
                subpixel_x: request.subpixel_x,
                atlas_x: request.atlas_origin[0] as f32,
                atlas_y: request.atlas_origin[1] as f32,
                padding: 0.0,
            };
            let start = index * 256;
            self.uniform_upload[start..start + 48].copy_from_slice(bytemuck::bytes_of(&uniforms));
            offset += u64::from(row) * u64::from(request.size[1]);
        }
        let upload_bytes = if oversized { 48 } else { requests.len() * 256 };
        self.queue
            .write_buffer(uniform_buffer, 0, &self.uniform_upload[..upload_bytes]);
        self.metrics.uniform_upload_writes += 1;
        self.metrics.uniform_upload_bytes += upload_bytes as u64;
        let buffers = Bindings {
            uniforms: uniform_buffer,
            records: &self.records,
            segments: &self.segments,
            coverage,
            horizontal,
        };
        let local_bindings = oversized.then(|| {
            self.metrics.bind_groups += 1;
            let layout = if binary {
                &self
                    .binary
                    .as_ref()
                    .expect("binary pipeline admitted")
                    .layout
            } else {
                &self.layout
            };
            create_bindings(&self.device, layout, buffers, binary)
        });
        if !oversized {
            if requests
                .iter()
                .any(|r| r.mode == GlyphRasterMode::Grayscale)
                && self.ring_bindings.is_none()
            {
                self.ring_bindings =
                    Some(create_bindings(&self.device, &self.layout, buffers, false));
                self.metrics.bind_groups += 1;
            }
            if binary && self.binary_bindings.is_none() {
                self.binary_bindings = Some(create_bindings(
                    &self.device,
                    &self
                        .binary
                        .as_ref()
                        .expect("binary pipeline admitted")
                        .layout,
                    buffers,
                    true,
                ));
                self.metrics.bind_groups += 1;
            }
        }
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU glyph raster batch"),
            });
        match &self.pipeline {
            Pipeline::Compute(gray) => {
                {
                    let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                        label: Some("ProGPU ordered glyph coverage"),
                        timestamp_writes: None,
                    });
                    for (index, request) in requests.iter().enumerate() {
                        let bindings = local_bindings
                            .as_ref()
                            .or(if request.mode == GlyphRasterMode::Binary {
                                self.binary_bindings.as_ref()
                            } else {
                                self.ring_bindings.as_ref()
                            })
                            .expect("selected bindings exist");
                        let dynamic_offset = if oversized { 0 } else { (index * 256) as u32 };
                        pass.set_bind_group(0, bindings, &[dynamic_offset]);
                        if request.mode == GlyphRasterMode::Binary {
                            let pipelines = self.binary.as_ref().expect("binary pipeline admitted");
                            pass.set_pipeline(&pipelines.horizontal);
                            pass.dispatch_workgroups(1, request.size[1], 1);
                            pass.set_pipeline(&pipelines.vertical);
                            pass.dispatch_workgroups(1, request.size[0], 1);
                        } else {
                            pass.set_pipeline(gray);
                            pass.dispatch_workgroups(
                                request.size[0].div_ceil(4).div_ceil(16),
                                request.size[1].div_ceil(16),
                                1,
                            );
                        }
                    }
                }
                let mut offset = 0;
                for request in requests {
                    offset = align_copy(offset);
                    let row = row_bytes(request.size[0]).expect("preflight checked row capacity");
                    let mut destination = target.texture.as_image_copy();
                    destination.origin = wgpu::Origin3d {
                        x: request.atlas_origin[0],
                        y: request.atlas_origin[1],
                        z: 0,
                    };
                    encoder.copy_buffer_to_texture(
                        wgpu::TexelCopyBufferInfo {
                            buffer: coverage.expect("compute output exists"),
                            layout: wgpu::TexelCopyBufferLayout {
                                offset,
                                bytes_per_row: Some(row),
                                rows_per_image: Some(request.size[1]),
                            },
                        },
                        destination,
                        wgpu::Extent3d {
                            width: request.size[0],
                            height: request.size[1],
                            depth_or_array_layers: 1,
                        },
                    );
                    offset += u64::from(row) * u64::from(request.size[1]);
                }
                self.metrics.compute_passes += 1;
            }
            Pipeline::Raster(pipeline) => {
                let view = target.texture.create_view(&Default::default());
                {
                    let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                        label: Some("ProGPU glyph fragment coverage"),
                        color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                            view: &view,
                            depth_slice: None,
                            resolve_target: None,
                            ops: wgpu::Operations {
                                load: wgpu::LoadOp::Load,
                                store: wgpu::StoreOp::Store,
                            },
                        })],
                        ..Default::default()
                    });
                    pass.set_pipeline(pipeline);
                    let bindings = self.ring_bindings.as_ref().expect("raster bindings exist");
                    for (index, request) in requests.iter().enumerate() {
                        pass.set_bind_group(0, bindings, &[(index * 256) as u32]);
                        pass.set_viewport(
                            request.atlas_origin[0] as f32,
                            request.atlas_origin[1] as f32,
                            request.size[0] as f32,
                            request.size[1] as f32,
                            0.0,
                            1.0,
                        );
                        pass.set_scissor_rect(
                            request.atlas_origin[0],
                            request.atlas_origin[1],
                            request.size[0],
                            request.size[1],
                        );
                        pass.draw(0..3, 0..1);
                    }
                }
                self.metrics.raster_passes += 1;
            }
        }
        self.queue.submit([encoder.finish()]);
        self.metrics.raster_submissions += 1;
        self.metrics.rasterized_glyphs += requests.len() as u64;
    }
}
#[derive(Clone, Copy)]
struct Bindings<'a> {
    uniforms: &'a wgpu::Buffer,
    records: &'a wgpu::Buffer,
    segments: &'a wgpu::Buffer,
    coverage: Option<&'a wgpu::Buffer>,
    horizontal: Option<&'a wgpu::Buffer>,
}
fn create_bindings(
    device: &wgpu::Device,
    layout: &wgpu::BindGroupLayout,
    buffers: Bindings<'_>,
    binary: bool,
) -> wgpu::BindGroup {
    let mut entries = vec![
        wgpu::BindGroupEntry {
            binding: 0,
            resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                buffer: buffers.uniforms,
                offset: 0,
                size: NonZeroU64::new(48),
            }),
        },
        wgpu::BindGroupEntry {
            binding: 1,
            resource: buffers.records.as_entire_binding(),
        },
        wgpu::BindGroupEntry {
            binding: 2,
            resource: buffers.segments.as_entire_binding(),
        },
    ];
    if let Some(coverage) = buffers.coverage {
        entries.push(wgpu::BindGroupEntry {
            binding: 3,
            resource: coverage.as_entire_binding(),
        });
    }
    if binary {
        entries.push(wgpu::BindGroupEntry {
            binding: 4,
            resource: buffers
                .horizontal
                .expect("binary scratch exists")
                .as_entire_binding(),
        });
    }
    device.create_bind_group(&wgpu::BindGroupDescriptor {
        label: Some("ProGPU glyph raster bindings"),
        layout,
        entries: &entries,
    })
}
fn align_copy(value: u64) -> u64 {
    (value + 511) & !511
}
fn row_bytes(width: u32) -> Result<u32, Error> {
    Ok(width.checked_add(255).ok_or(Error::Capacity)? & !255)
}
fn coverage_bytes(request: &GlyphRasterRequest) -> Result<u32, Error> {
    row_bytes(request.size[0])?
        .checked_mul(request.size[1])
        .ok_or(Error::Capacity)
}
