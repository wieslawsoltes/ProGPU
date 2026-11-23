use super::super::{ImageFormat, RasterSnapshot};
use super::*;
impl TextureImage {
    /// Original bitmap-to-texture producer. Pixel bytes are copied before GPU
    /// publication; opaque alpha is forced and premultiplied bytes stay encoded.
    pub fn from_raster(
        context: &ImageContext,
        source: &RasterSnapshot,
        execution: RasterExecution,
    ) -> Result<Self, ImageResourceError> {
        if !matches!(
            source.info().color_type,
            PixelColorType::Rgba8888
                | PixelColorType::Bgra8888
                | PixelColorType::Rgb888x
                | PixelColorType::Rgb565
        ) {
            return Err(RasterError::UnsupportedFormat.into());
        }
        let image = source.to_image(execution)?;
        let size = image.size();
        let mut bytes = super::super::raster::zeros(byte_count(size)?)?;
        let row = size[0] as usize * 4;
        for y in 0..size[1] as usize {
            bytes[y * row..(y + 1) * row].copy_from_slice(
                &image.pixels()[y * image.stride() as usize..y * image.stride() as usize + row],
            );
        }
        if image.format() == ImageFormat::Bgra8 {
            swizzle(&mut bytes, execution);
        }
        let info = source.info().clone();
        let gpu = upload(context, &info, &bytes, 1, false)?;
        Ok(Self::create(
            context.clone(),
            gpu,
            info,
            TextureOwnership::Owned,
            Some(bytes.into()),
            false,
            None,
        ))
    }
    /// Make a new owned base-level copy of the selected source pixels; deferred
    /// shader draws can retain it independently of a borrowed mutable producer.
    pub fn owned_copy(&self) -> Result<Self, ImageResourceError> {
        self.copy_texture(false)
    }
    fn copy_texture(&self, mipmapped: bool) -> Result<Self, ImageResourceError> {
        self.context().admit()?;
        if !self
            .storage
            .image
            .texture()
            .usage()
            .contains(wgpu::TextureUsages::COPY_SRC)
        {
            return Err(ImageResourceError::TextureContract);
        }
        let size = [self.region[2], self.region[3]];
        let texture = allocate(
            self.context(),
            size,
            self.storage.image.texture().format(),
            if mipmapped { mip_count(size) } else { 1 },
            mipmapped,
        )?;
        let mut encoder =
            self.context()
                .device()
                .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                    label: Some("ProGPU retained image owned copy"),
                });
        encoder.copy_texture_to_texture(
            wgpu::TexelCopyTextureInfo {
                texture: self.storage.image.texture(),
                mip_level: 0,
                origin: wgpu::Origin3d {
                    x: self.region[0],
                    y: self.region[1],
                    z: 0,
                },
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::TexelCopyTextureInfo {
                texture: &texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            wgpu::Extent3d {
                width: size[0],
                height: size[1],
                depth_or_array_layers: 1,
            },
        );
        self.context().queue().submit([encoder.finish()]);
        self.gpu_image().retain_submitted(self.context().queue());
        let gpu = GpuImage::from_texture(
            self.context().device(),
            &texture,
            self.storage.image.alpha(),
        )?;
        Ok(Self::create(
            self.context().clone(),
            gpu,
            self.info(),
            TextureOwnership::Owned,
            self.portable_pixels()?,
            true,
            None,
        ))
    }
    /// Original explicit transfer permits a GPU readback across contexts; ordinary
    /// for_context() retains its stricter CPU-portable-only policy. Same-context
    /// transfer always makes a new texture, optionally generating canonical mips.
    pub async fn to_texture_image(
        &self,
        context: &ImageContext,
        mipmapped: bool,
    ) -> Result<Self, ImageResourceError> {
        context.admit()?;
        let image = if context.same_context(self.context()) {
            self.copy_texture(mipmapped)?
        } else {
            let bytes = self.rgba_pixels(RasterExecution::default()).await?;
            let levels = if mipmapped {
                mip_count([self.region[2], self.region[3]])
            } else {
                1
            };
            let mut upload_info = self.info();
            upload_info.alpha_type = if self.storage.image.alpha() == ImageAlpha::Straight {
                PixelAlphaType::Straight
            } else {
                PixelAlphaType::Premultiplied
            };
            let gpu = upload(context, &upload_info, &bytes, levels, mipmapped)?;
            Self::create(
                context.clone(),
                gpu,
                self.info(),
                TextureOwnership::Owned,
                Some(bytes.into()),
                true,
                None,
            )
        };
        if mipmapped {
            generate_mips(image.context(), image.storage.image.texture()).await?;
        }
        Ok(image)
    }
    /// Native-order readback normalized only by original BGRA/RGBA swizzle.
    /// It does not unpremultiply, change color space or apply image filters.
    pub async fn rgba_pixels(
        &self,
        execution: RasterExecution,
    ) -> Result<Vec<u8>, ImageResourceError> {
        if let Some(portable) = self.portable_pixels()? {
            return Ok(portable.to_vec());
        }
        let read = self
            .readback(ImageCachingHint::Disallow)?
            .complete_host()
            .await?;
        let mut result =
            super::super::raster::zeros(byte_count([self.region[2], self.region[3]])?)?;
        let row = self.region[2] as usize * 4;
        let pixels = read.pixels()?;
        for y in 0..self.region[3] as usize {
            result[y * row..(y + 1) * row].copy_from_slice(
                &pixels[y * read.row_bytes() as usize..y * read.row_bytes() as usize + row],
            );
        }
        if read.info().color_type == PixelColorType::Bgra8888 {
            swizzle(&mut result, execution);
        }
        Ok(result)
    }
    /// Original raster compatibility image still retains an actual texture, with
    /// separate immutable portable CPU pixels and is_texture_backed=false.
    pub async fn to_raster_image(
        &self,
        execution: RasterExecution,
    ) -> Result<Self, ImageResourceError> {
        let bytes = self.rgba_pixels(execution).await?;
        let mut info = self.info();
        info.alpha_type = if self.storage.image.alpha() == ImageAlpha::Straight {
            PixelAlphaType::Straight
        } else {
            PixelAlphaType::Premultiplied
        };
        let gpu = upload(self.context(), &info, &bytes, 1, false)?;
        Ok(Self::create(
            self.context().clone(),
            gpu,
            self.info(),
            TextureOwnership::Owned,
            Some(bytes.into()),
            false,
            None,
        ))
    }
}
pub(crate) fn allocate(
    context: &ImageContext,
    size: [u32; 2],
    format: wgpu::TextureFormat,
    levels: u32,
    render: bool,
) -> Result<wgpu::Texture, ImageResourceError> {
    context.admit()?;
    if size.contains(&0)
        || size
            .iter()
            .any(|&d| d > context.device().limits().max_texture_dimension_2d)
        || levels == 0
        || levels > mip_count(size)
    {
        return Err(Error::Capacity.into());
    }
    Ok(context.device().create_texture(&wgpu::TextureDescriptor {
        label: Some("ProGPU owned image texture"),
        size: wgpu::Extent3d {
            width: size[0],
            height: size[1],
            depth_or_array_layers: 1,
        },
        mip_level_count: levels,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format,
        usage: wgpu::TextureUsages::TEXTURE_BINDING
            | wgpu::TextureUsages::COPY_SRC
            | wgpu::TextureUsages::COPY_DST
            | if render {
                wgpu::TextureUsages::RENDER_ATTACHMENT
            } else {
                wgpu::TextureUsages::empty()
            },
        view_formats: &[],
    }))
}
pub(super) fn upload(
    context: &ImageContext,
    info: &ImageInfo,
    pixels: &[u8],
    levels: u32,
    render: bool,
) -> Result<GpuImage, ImageResourceError> {
    if info.is_empty() {
        return Err(ImageResourceError::Bounds);
    }
    let size = [info.width as u32, info.height as u32];
    if pixels.len() != byte_count(size)? {
        return Err(RasterError::InsufficientPixels.into());
    }
    let texture = allocate(
        context,
        size,
        wgpu::TextureFormat::Rgba8Unorm,
        levels,
        render,
    )?;
    context.queue().write_texture(
        wgpu::TexelCopyTextureInfo {
            texture: &texture,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        pixels,
        wgpu::TexelCopyBufferLayout {
            offset: 0,
            bytes_per_row: Some(size[0].checked_mul(4).ok_or(Error::Capacity)?),
            rows_per_image: Some(size[1]),
        },
        texture.size(),
    );
    Ok(GpuImage::from_texture(
        context.device(),
        &texture,
        if info.alpha_type == PixelAlphaType::Straight {
            ImageAlpha::Straight
        } else {
            ImageAlpha::Premultiplied
        },
    )?)
}
pub(crate) fn mip_count(size: [u32; 2]) -> u32 {
    u32::BITS - size[0].max(size[1]).leading_zeros()
}
pub(super) fn swizzle(bytes: &mut [u8], execution: RasterExecution) {
    let mut groups = bytes.chunks_exact_mut(16);
    if execution == RasterExecution::CpuSimd {
        for group in groups.by_ref() {
            let v = wide::u32x4::from(std::array::from_fn(|i| {
                u32::from_le_bytes(group[i * 4..i * 4 + 4].try_into().unwrap())
            }));
            let out = ((v & wide::u32x4::splat(0xff00ff00))
                | ((v & wide::u32x4::splat(0xff)) << 16)
                | ((v >> 16) & wide::u32x4::splat(0xff)))
            .to_array();
            for (pixel, value) in group.chunks_exact_mut(4).zip(out) {
                pixel.copy_from_slice(&value.to_le_bytes());
            }
        }
        for pixel in groups.into_remainder().chunks_exact_mut(4) {
            pixel.swap(0, 2);
        }
    } else {
        for pixel in bytes.chunks_exact_mut(4) {
            pixel.swap(0, 2);
        }
    }
}
/// Exact original canonical MipGenerator.wgsl fullscreen linear chain. No CPU
/// downsampling, alpha conversion or inferred reconstruction kernel is inserted.
pub(super) async fn generate_mips(
    context: &ImageContext,
    texture: &wgpu::Texture,
) -> Result<(), ImageResourceError> {
    if texture.mip_level_count() <= 1 {
        return Ok(());
    }
    let device = context.device();
    let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
    let cached = context
        .0
        .mip_pipelines
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .iter()
        .find(|(format, _)| *format == texture.format())
        .map(|(_, pipeline)| pipeline.clone());
    let pipeline = if let Some(cached) = cached {
        cached
    } else {
        let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("ProGPU canonical image mip generator"),
            source: wgpu::ShaderSource::Wgsl(crate::shaders::MIP_GENERATOR.into()),
        });
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("ProGPU retained image mip chain"),
            layout: None,
            vertex: wgpu::VertexState {
                module: &module,
                entry_point: Some("vs_main"),
                compilation_options: Default::default(),
                buffers: &[],
            },
            primitive: Default::default(),
            depth_stencil: None,
            multisample: Default::default(),
            fragment: Some(wgpu::FragmentState {
                module: &module,
                entry_point: Some("fs_main"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format: texture.format(),
                    blend: None,
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });
        pipeline
    };
    let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
        min_filter: wgpu::FilterMode::Linear,
        mag_filter: wgpu::FilterMode::Linear,
        ..Default::default()
    });
    let mut encoder = device.create_command_encoder(&wgpu::CommandEncoderDescriptor {
        label: Some("ProGPU image mip generation"),
    });
    for level in 1..texture.mip_level_count() {
        let view = |mip| {
            texture.create_view(&wgpu::TextureViewDescriptor {
                base_mip_level: mip,
                mip_level_count: Some(1),
                ..Default::default()
            })
        };
        let source = view(level - 1);
        let destination = view(level);
        let bindings = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU original previous mip"),
            layout: &pipeline.get_bind_group_layout(0),
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: wgpu::BindingResource::Sampler(&sampler),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(&source),
                },
            ],
        });
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("ProGPU canonical mip pass"),
            color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                view: &destination,
                depth_slice: None,
                resolve_target: None,
                ops: wgpu::Operations {
                    load: wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
                    store: wgpu::StoreOp::Store,
                },
            })],
            ..Default::default()
        });
        pass.set_pipeline(&pipeline);
        pass.set_bind_group(0, &bindings, &[]);
        pass.draw(0..3, 0..1);
    }
    if let Some(error) = scope.pop().await {
        return Err(Error::from(error).into());
    }
    {
        let mut cache = context
            .0
            .mip_pipelines
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        if !cache.iter().any(|(format, _)| *format == texture.format()) {
            cache.push((texture.format(), pipeline));
        }
    }
    context.queue().submit([encoder.finish()]);
    Ok(())
}

pub(super) fn force_opaque(bytes: &mut [u8], execution: RasterExecution) {
    if execution == RasterExecution::CpuSimd {
        let mut chunks = bytes.chunks_exact_mut(16);
        for chunk in chunks.by_ref() {
            let values = wide::u32x4::from(std::array::from_fn(|i| {
                u32::from_le_bytes(chunk[i * 4..i * 4 + 4].try_into().unwrap())
            }));
            for (pixel, value) in chunk
                .chunks_exact_mut(4)
                .zip((values | wide::u32x4::splat(0xff000000)).to_array())
            {
                pixel.copy_from_slice(&value.to_le_bytes());
            }
        }
        for pixel in chunks.into_remainder().chunks_exact_mut(4) {
            pixel[3] = 255;
        }
    } else {
        for pixel in bytes.chunks_exact_mut(4) {
            pixel[3] = 255;
        }
    }
}
