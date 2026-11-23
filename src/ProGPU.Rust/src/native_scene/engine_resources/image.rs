//! Complete original per-draw image admission and independently retained CPU page.
use super::*;
use crate::native_scene::semantic::{image_payload, values};

/// State must be the original cursor's fully localized state for this actual
/// target. It is not reconstructed from a command's optional resource index.
#[derive(Clone, Copy, Debug)]
pub struct NativeImageCommandInput {
    pub command_index: u32,
    pub state: wire::State,
    pub dpi_scale: f32,
}
#[derive(Clone, Copy, Debug)]
pub enum NativeImageStorage {
    Upload {
        format: crate::image::ImageFormat,
        upload_bytes: u64,
    },
    External {
        primary: usize,
        chroma: Option<usize>,
        mask: Option<usize>,
    },
    Picture {
        descriptor: wire::PictureImage,
        presentation: wire::Presentation,
    },
}
#[derive(Clone, Debug)]
pub struct NativeImageDrawPage {
    pub command_index: u32,
    pub resource_index: u32,
    pub resource: wire::Resource,
    pub image: wire::ImageDraw,
    pub storage: NativeImageStorage,
    pub first_vertex: u32,
    pub vertex_count: u32,
    pub indexed: bool,
    pub cubic: [f32; 2],
    pub color_matrix: Option<wire::ImageColorMatrix>,
    pub effect: Option<wire::ImageEffect>,
    /// Present when compiled from the actual ordered target/state cursor.
    pub frame: Option<crate::native_scene::execution::NativeCommandFrame>,
}
/// Typed source recipe owns the admitted parent generation and the actual
/// selected nested bytes; a resolver must execute them, not invent pixel output.
#[derive(Clone, Debug)]
pub struct NativePictureImageSource {
    owner: Arc<NativeScene>,
    pub resource_index: u32,
    pub descriptor: wire::PictureImage,
    pub presentation: wire::Presentation,
}
impl NativePictureImageSource {
    /// Retain an actual picture resource without manufacturing an image draw.
    pub fn from_resource(owner: Arc<NativeScene>, resource_index: u32) -> Result<Self, Error> {
        let resource = owner
            .envelope()
            .resource(resource_index)
            .ok_or(Error::InvalidInput)?;
        if resource.record.kind != SCENE_RESOURCE_IMAGE
            || resource.record.flags & SCENE_IMAGE_PICTURE == 0
            || resource.record.flags & SCENE_EXTERNAL_IMAGE != 0
        {
            return Err(Error::InvalidInput);
        }
        let (descriptor, presentation) =
            values::read_picture_image(resource.payload).ok_or(Error::InvalidInput)?;
        Ok(Self {
            owner,
            resource_index,
            descriptor,
            presentation,
        })
    }
    pub fn bytes(&self) -> &[u8] {
        self.owner
            .envelope()
            .resource(self.resource_index)
            .expect("retained selected picture")
            .auxiliary
    }
    pub fn owner(&self) -> &Arc<NativeScene> {
        &self.owner
    }
}
#[derive(Clone, Debug)]
pub struct NativeImagePage {
    owner: Arc<NativeScene>,
    draws: Arc<[NativeImageDrawPage]>,
    vertices: Arc<[VectorVertex]>,
    external: Arc<[NativeExternalImageInfo]>,
    metrics: NativeImageResourceMetrics,
}
impl NativeImagePage {
    /// Consume the actual owning cursor frames without reconstructing target
    /// localization, guideline axes, or materialized alpha policy.
    pub fn compile_frames(
        owner: Arc<NativeScene>,
        frames: &[crate::native_scene::execution::NativeCommandFrame],
        external: &[NativeExternalImageInfo],
        explicit_shader_sampling: bool,
        maximum_vertex_bytes: u64,
    ) -> Result<Self, Error> {
        let mut inputs = allocate(frames.len())?;
        let mut selected = allocate(frames.len())?;
        for frame in frames {
            if !Arc::ptr_eq(frame.scene(), &owner) {
                return Err(Error::ResourceDomain);
            }
            if frame.command().record.kind == SCENE_COMMAND_DRAW_IMAGE {
                inputs.push(NativeImageCommandInput {
                    command_index: frame.command_index(),
                    state: frame.state(),
                    dpi_scale: frame.dpi_scale(),
                });
                selected.push(frame.clone());
            }
        }
        let mut page = Self::compile_bounded(
            owner,
            &inputs,
            external,
            explicit_shader_sampling,
            maximum_vertex_bytes,
        )?;
        // This newly produced Arc has no aliases; no replay generation is edited.
        let draws = Arc::get_mut(&mut page.draws).ok_or(Error::ResourceDomain)?;
        for (draw, frame) in draws.iter_mut().zip(selected) {
            draw.frame = Some(frame);
        }
        Ok(page)
    }
    /// Admit the entire source-ordered image inventory before publishing geometry.
    /// Engine target budgets remain an independent caller-supplied admission step.
    pub fn compile(
        owner: Arc<NativeScene>,
        inputs: &[NativeImageCommandInput],
        external: &[NativeExternalImageInfo],
        explicit_shader_sampling: bool,
    ) -> Result<Self, Error> {
        Self::compile_bounded(owner, inputs, external, explicit_shader_sampling, u64::MAX)
    }
    /// Apply the caller's actual remaining native vertex budget before allocating
    /// the packed vertex inventory. This is independent of source stream bounds.
    pub fn compile_bounded(
        owner: Arc<NativeScene>,
        inputs: &[NativeImageCommandInput],
        external: &[NativeExternalImageInfo],
        explicit_shader_sampling: bool,
        maximum_vertex_bytes: u64,
    ) -> Result<Self, Error> {
        if !external_info_valid(external) {
            return Err(Error::InvalidInput);
        }
        let mut draws = allocate(inputs.len())?;
        let mut metric = NativeImageResourceMetrics::default();
        let mut expected = owner
            .commands()
            .enumerate()
            .filter(|(_, c)| c.record.kind == SCENE_COMMAND_DRAW_IMAGE);
        for input in inputs {
            let Some((index, command)) = expected.next() else {
                return Err(Error::InvalidInput);
            };
            if index != input.command_index as usize
                || !input.dpi_scale.is_finite()
                || input.dpi_scale <= 0.0
            {
                return Err(Error::InvalidInput);
            }
            let resource = owner
                .envelope()
                .resource(command.record.resource_index)
                .ok_or(Error::InvalidInput)?;
            if resource.record.kind != SCENE_RESOURCE_IMAGE {
                return Err(Error::InvalidInput);
            }
            let mut image = wire::ImageDraw::read(command.payload).ok_or(Error::InvalidInput)?;
            crate::native_scene::execution::apply_image_state(&mut image, &input.state);
            let is_external = resource.record.flags & SCENE_EXTERNAL_IMAGE != 0;
            let is_picture = resource.record.flags & SCENE_IMAGE_PICTURE != 0;
            let bytes = if is_external || is_picture {
                (u64::from(image.row_bytes) * u64::from(image.image_height.wrapping_sub(1)))
                    .wrapping_add(u64::from(image.image_width) * 4)
            } else {
                resource.payload.len() as u64
            };
            let bpp = if resource.record.flags & SCENE_IMAGE_R8 != 0 {
                1
            } else {
                4
            };
            let options = image_payload::read_image_payload(command.payload, &image, bytes, bpp)
                .ok_or(Error::InvalidInput)?;
            if !is_picture && !resource.auxiliary.is_empty() {
                return Err(Error::InvalidInput);
            }
            let storage = if is_picture {
                let (picture, presentation) =
                    values::read_picture_image(resource.payload).ok_or(Error::InvalidInput)?;
                if picture.width != image.image_width
                    || picture.height != image.image_height
                    || image.row_bytes != picture.width * 4
                    || image.flags & SCENE_IMAGE_SOURCE_PREMULTIPLIED == 0
                {
                    return Err(Error::InvalidInput);
                }
                NativeImageStorage::Picture {
                    descriptor: picture,
                    presentation,
                }
            } else if is_external {
                let id = resource.record.resource_id;
                let generation = resource.record.generation;
                let primary = external_slot(external, id, generation, NativeImageRole::Primary)
                    .ok_or(Error::InvalidInput)?;
                if external[primary].size != [image.image_width, image.image_height] {
                    return Err(Error::InvalidInput);
                }
                NativeImageStorage::External {
                    primary,
                    chroma: external_slot(external, id, generation, NativeImageRole::Chroma),
                    mask: external_slot(external, id, generation, NativeImageRole::Mask),
                }
            } else {
                NativeImageStorage::Upload {
                    format: if bpp == 1 {
                        crate::image::ImageFormat::R8
                    } else if resource.record.flags & SCENE_IMAGE_BGRA8 != 0 {
                        crate::image::ImageFormat::Bgra8
                    } else {
                        crate::image::ImageFormat::Rgba8
                    },
                    upload_bytes: u64::from(image.row_bytes) * u64::from(image.image_height - 1)
                        + u64::from(image.image_width) * u64::from(bpp),
                }
            };
            if let Some(effect) = options.effect {
                let (chroma, mask) = match storage {
                    NativeImageStorage::External { chroma, mask, .. } => {
                        (chroma.is_some(), mask.is_some())
                    }
                    _ => (false, false),
                };
                if effect.flags0[0] > 0.5 && !chroma || effect.flags0[1] > 0.5 && !mask {
                    return Err(Error::InvalidInput);
                }
            }
            let count = if options.patch_count == 0 {
                4
            } else {
                options.patch_count * 6
            };
            let first = u32::try_from(metric.vertex_bytes / 56).map_err(|_| Error::Capacity)?;
            first.checked_add(count).ok_or(Error::Capacity)?;
            let texture_bytes = if is_external {
                0
            } else if is_picture {
                u64::from(image.image_width) * u64::from(image.image_height) * 4
            } else {
                resource.payload.len() as u64
            };
            metric = metric.checked_add(NativeImageResourceMetrics {
                draws: 1,
                vertex_bytes: u64::from(count) * 56,
                index_bytes: if options.patch_count == 0 { 24 } else { 0 },
                texture_bytes,
                ..Default::default()
            })?;
            draws.push(NativeImageDrawPage {
                command_index: input.command_index,
                resource_index: command.record.resource_index,
                resource: resource.record,
                image,
                storage,
                first_vertex: first,
                vertex_count: count,
                indexed: options.patch_count == 0,
                cubic: [options.cubic_b, options.cubic_c],
                color_matrix: options.color_matrix,
                effect: options.effect,
                frame: None,
            });
        }
        if expected.next().is_some() {
            return Err(Error::InvalidInput);
        }
        if metric.vertex_bytes > maximum_vertex_bytes {
            return Err(Error::Capacity);
        }
        drop(expected);
        let mut vertices =
            allocate(usize::try_from(metric.vertex_bytes / 56).map_err(|_| Error::Capacity)?)?;
        for (draw, input) in draws.iter().zip(inputs) {
            let command = owner
                .envelope()
                .command(draw.command_index)
                .ok_or(Error::InvalidInput)?;
            let resource = owner
                .envelope()
                .resource(draw.resource_index)
                .ok_or(Error::InvalidInput)?;
            let bytes = if matches!(draw.storage, NativeImageStorage::Upload { .. }) {
                resource.payload.len() as u64
            } else {
                u64::from(draw.image.row_bytes) * u64::from(draw.image.image_height - 1)
                    + u64::from(draw.image.image_width) * 4
            };
            let options = image_payload::read_image_payload(
                command.payload,
                &draw.image,
                bytes,
                if resource.record.flags & SCENE_IMAGE_R8 != 0 {
                    1
                } else {
                    4
                },
            )
            .ok_or(Error::InvalidInput)?;
            if options.patch_count == 0 {
                geometry::append(
                    &mut vertices,
                    &draw.image,
                    &options,
                    None,
                    input.dpi_scale,
                    explicit_shader_sampling,
                );
            } else {
                for index in 0..options.patch_count {
                    let patch = options.patch(index).ok_or(Error::InvalidInput)?;
                    geometry::append(
                        &mut vertices,
                        &draw.image,
                        &options,
                        Some(&patch),
                        input.dpi_scale,
                        explicit_shader_sampling,
                    );
                }
            }
        }
        let mut owned_external = allocate(external.len())?;
        owned_external.extend_from_slice(external);
        Ok(Self {
            owner,
            draws: draws.into(),
            vertices: vertices.into(),
            external: owned_external.into(),
            metrics: metric,
        })
    }
    pub fn owner(&self) -> &Arc<NativeScene> {
        &self.owner
    }
    pub fn draws(&self) -> &[NativeImageDrawPage] {
        &self.draws
    }
    pub fn vertices(&self) -> &[VectorVertex] {
        &self.vertices
    }
    /// Original shared indexed-quad order. Patch batches are already triangles.
    pub fn quad_indices(&self) -> [u32; 6] {
        [0, 1, 2, 0, 2, 3]
    }
    pub fn external_images(&self) -> &[NativeExternalImageInfo] {
        &self.external
    }
    pub fn metrics(&self) -> NativeImageResourceMetrics {
        self.metrics
    }
    pub fn source_pixels(&self, draw: &NativeImageDrawPage) -> Result<&[u8], Error> {
        let resource = self
            .owner
            .envelope()
            .resource(draw.resource_index)
            .ok_or(Error::InvalidInput)?;
        let NativeImageStorage::Upload { upload_bytes, .. } = draw.storage else {
            return Err(Error::InvalidInput);
        };
        resource
            .payload
            .get(..upload_bytes as usize)
            .ok_or(Error::InvalidInput)
    }
    pub fn picture_source(&self, draw: &NativeImageDrawPage) -> Option<NativePictureImageSource> {
        let NativeImageStorage::Picture {
            descriptor,
            presentation,
        } = draw.storage
        else {
            return None;
        };
        Some(NativePictureImageSource {
            owner: self.owner.clone(),
            resource_index: draw.resource_index,
            descriptor,
            presentation,
        })
    }
}
