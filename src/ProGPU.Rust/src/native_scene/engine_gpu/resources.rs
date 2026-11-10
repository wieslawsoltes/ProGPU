//! Native family preparation after whole-scene admission. All pages form one
//! immutable candidate; source providers run only during image preparation.
use super::*;
mod reuse;
use crate::native_scene::{
    engine_resources::{
        NativeExternalImages, NativeGlyphPageEngine, NativeGlyphPaintBindings,
        NativeImagePageEngine, NativeImageResourceMetrics, NativeImageTexture,
        NativePathCoverageCommands, NativePathPageEngine, NativePictureImageResolver,
        NativeRasterResourceMetrics, NativeThreeDMetrics, NativeThreeDPageEngine,
        PreparedNativeImagePage, PreparedNativeThreeDPage,
    },
    execution::{NativeSceneRasterPages, NativeTargetFormat},
};

#[derive(Clone, Copy, Debug, Default)]
pub struct NativeScenePageMetrics {
    pub materials: NativeMaterialGpuMetrics,
    pub vector: NativeVectorGpuMetrics,
    pub paths: NativeRasterResourceMetrics,
    pub glyphs: NativeRasterResourceMetrics,
    pub images: NativeImageResourceMetrics,
    pub three_d: NativeThreeDMetrics,
}
/// Demand-created pipelines are reusable. Prepared generations retain actual
/// page resources independently of this engine and later source replacements.
pub struct NativeScenePageEngine {
    owner: Arc<()>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    format: NativeTargetFormat,
    paths: NativePathPageEngine,
    glyphs: NativeGlyphPageEngine,
    images: NativeImagePageEngine,
    three_d: NativeThreeDPageEngine,
    sentinel: Option<(NativeImageTexture, wgpu::Sampler)>,
    families: reuse::FamilyCache,
}
pub struct PreparedNativeScenePages {
    owner: Arc<()>,
    source: Arc<NativeSceneRasterPages>,
    external: NativeExternalImages,
    material: Arc<PreparedNativeMaterials>,
    vector: Option<Arc<NativeVectorRenderPage>>,
    paths: Option<Arc<NativePathRenderPage>>,
    glyphs: Option<Arc<NativeGlyphRenderPage>>,
    images: Option<Arc<PreparedNativeImagePage>>,
    three_d: Option<Arc<PreparedNativeThreeDPage>>,
    metrics: NativeScenePageMetrics,
    frame_layout: wgpu::BindGroupLayout,
}
impl NativeScenePageEngine {
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue, format: NativeTargetFormat) -> Self {
        Self {
            owner: Arc::new(()),
            device: device.clone(),
            queue: queue.clone(),
            format,
            paths: NativePathPageEngine::new(device),
            glyphs: NativeGlyphPageEngine::new(device, queue),
            images: NativeImagePageEngine::new_with_target_format(
                device,
                queue,
                format.texture_format(),
            ),
            three_d: NativeThreeDPageEngine::new(device, queue, format.texture_format()),
            sentinel: None,
            families: Default::default(),
        }
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    /// Complete family/device admission before allocations/uploads for this
    /// candidate. Layer, mask, shader and RGB actual-target gates remain in the
    /// ordered executor, not inferred from these ordinary family buffers.
    pub fn preflight(
        &self,
        source: &NativeSceneRasterPages,
        external: &NativeExternalImages,
        pictures: &impl NativePictureImageResolver,
    ) -> Result<NativeScenePageMetrics, Error> {
        let plan = source.source();
        if external.device() != &self.device
            || external.metadata() != plan.draws().external_images()
            || plan.options().format != self.format
        {
            return Err(Error::ResourceDomain);
        }
        let mut m = NativeScenePageMetrics {
            materials: PreparedNativeMaterials::preflight(
                &self.device,
                plan.materials(),
                plan.text_styles(),
            )?,
            ..Default::default()
        };
        if let Some(p) = plan.vector() {
            m.vector = NativeVectorRenderPage::preflight(&self.device, p)?;
        }
        if let Some(p) = source.paths() {
            m.paths = self.paths.preflight(p)?;
        }
        if let Some(p) = source.glyphs() {
            m.glyphs = self.glyphs.preflight(p)?;
            NativeGlyphPaintBindings::preflight_metadata(&self.device, p, external)?;
        }
        if let Some(p) = plan.images() {
            m.images = self.images.preflight(p, external, pictures)?;
        }
        if let Some(p) = plan.three_d() {
            m.three_d = self.three_d.preflight(p, external)?;
        }
        Ok(m)
    }
    pub async fn prepare(
        &mut self,
        pipelines: &NativePipelineCache,
        source: Arc<NativeSceneRasterPages>,
        external: &NativeExternalImages,
        pictures: &mut impl NativePictureImageResolver,
    ) -> Result<Arc<PreparedNativeScenePages>, Error> {
        if pipelines.device() != &self.device
            || pipelines.target_format() != self.format.texture_format()
        {
            return Err(Error::ResourceDomain);
        }
        self.preflight(&source, external, pictures)?;
        let plan = source.source();
        let identity = cache::NativeReplayCacheIdentity::from_plan(plan);
        let material = Arc::new(
            PreparedNativeMaterials::prepare_retained(
                &self.device,
                &self.queue,
                plan.materials().clone(),
                plan.text_styles().clone(),
                self.families.brushes(plan.materials().scene_hash()),
                self.families.styles(plan.text_styles().scene_hash()),
            )
            .await?,
        );
        let vector = if let Some(p) = plan.vector() {
            if let Some(retained) = self.families.vector(&identity, plan, pipelines) {
                Some(retained)
            } else {
                Some(Arc::new(
                    NativeVectorRenderPage::prepare(pipelines, p.clone()).await?,
                ))
            }
        } else {
            None
        };
        let paths = if let Some(p) = source.paths() {
            if let Some(retained) = self.families.paths(&identity, plan, p, pipelines) {
                Some(retained)
            } else {
                let raster = self.paths.prepare(p.clone()).await?;
                Some(Arc::new(
                    NativePathRenderPage::prepare(pipelines, raster).await?,
                ))
            }
        } else {
            None
        };
        let glyphs = if let Some(p) = source.glyphs() {
            if let Some(retained) = self
                .families
                .glyphs(&identity, plan, p, external, pipelines)
            {
                Some(retained)
            } else {
                let raster = self.glyphs.prepare(p.clone()).await?;
                let paints = if p.source().paints().is_empty() {
                    None
                } else {
                    self.ensure_sentinel().await?;
                    let (texture, sampler) = self.sentinel.as_ref().ok_or(Error::ResourceDomain)?;
                    Some(
                        NativeGlyphPaintBindings::prepare(
                            &self.device,
                            raster.clone(),
                            external,
                            &pipelines.layouts().image.texture,
                            texture,
                            sampler,
                        )
                        .await?,
                    )
                };
                Some(Arc::new(
                    NativeGlyphRenderPage::prepare(pipelines, raster, paints).await?,
                ))
            }
        } else {
            None
        };
        let images = if let Some(p) = plan.images() {
            if let Some(retained) = self.families.images(&identity, plan, external, pipelines)? {
                Some(retained)
            } else {
                Some(Arc::new(
                    self.images.prepare(p.clone(), external, pictures).await?,
                ))
            }
        } else {
            None
        };
        let three_d = if let Some(p) = plan.three_d() {
            Some(Arc::new(
                self.three_d
                    .prepare(p.clone(), external, plan.options().resource_scope)
                    .await?,
            ))
        } else {
            None
        };
        let metrics = NativeScenePageMetrics {
            materials: material.metrics(),
            vector: vector.as_ref().map_or(Default::default(), |p| p.metrics()),
            paths: paths
                .as_ref()
                .map_or(Default::default(), |p| p.page().metrics()),
            glyphs: glyphs
                .as_ref()
                .map_or(Default::default(), |p| p.page().metrics()),
            images: images.as_ref().map_or(Default::default(), |p| p.metrics()),
            three_d: three_d.as_ref().map_or(Default::default(), |p| p.metrics()),
        };
        Ok(Arc::new(PreparedNativeScenePages {
            owner: self.owner.clone(),
            source,
            external: external.clone(),
            material,
            vector,
            paths,
            glyphs,
            images,
            three_d,
            metrics,
            frame_layout: pipelines.layouts().frame.clone(),
        }))
    }
    /// Publish independent family owners only after the actual submitted replay
    /// completed its coverage writes. Abandoned preparation cannot certify ink.
    pub fn commit_submitted(
        &mut self,
        pages: &Arc<PreparedNativeScenePages>,
        queue: &wgpu::Queue,
    ) -> Result<(), Error> {
        self.admit(pages)?;
        if queue != &self.queue {
            return Err(Error::ResourceDomain);
        }
        self.families.submitted(pages);
        pages.retain_submitted(queue);
        Ok(())
    }
    async fn ensure_sentinel(&mut self) -> Result<(), Error> {
        if self.sentinel.is_some() {
            return Ok(());
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("Native glyph paint zero RGBA sentinel"),
            size: wgpu::Extent3d {
                width: 1,
                height: 1,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8Unorm,
            usage: wgpu::TextureUsages::TEXTURE_BINDING,
            view_formats: &[],
        });
        let view = texture.create_view(&Default::default());
        let sampler = self.device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("Native glyph paint sentinel sampler"),
            lod_min_clamp: 0.0,
            lod_max_clamp: 0.0,
            ..Default::default()
        });
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let image = NativeImageTexture::from_view(&self.device, &texture, &view, [1, 1], None)?;
        self.sentinel = Some((image, sampler));
        Ok(())
    }
    /// Original path split submissions precede the final consumer encoder.
    /// Submit every returned preceding buffer separately, in order. Coverage
    /// becomes cache-resident only after the owning executor actually submits;
    /// dropping this candidate never marks an unsubmitted atlas resident.
    pub fn encode_initial_coverage(
        &self,
        pages: &PreparedNativeScenePages,
        encoder: wgpu::CommandEncoder,
    ) -> Result<NativePathCoverageCommands, Error> {
        self.admit(pages)?;
        let mut commands = if let Some(p) = &pages.paths {
            p.page().encode_coverage(encoder)?
        } else {
            NativePathCoverageCommands {
                preceding: Vec::new(),
                encoder,
            }
        };
        if let Some(p) = &pages.glyphs {
            p.page().encode_coverage(&mut commands.encoder);
        }
        Ok(commands)
    }
    /// Live image planes and Gaussian effects retain their original per-replay
    /// work. An immutable page is not proof that external texture pixels froze.
    pub fn encode_live_image_sources(
        &self,
        pages: &PreparedNativeScenePages,
        encoder: &mut wgpu::CommandEncoder,
    ) -> Result<(), Error> {
        self.admit(pages)?;
        if let Some(p) = &pages.images {
            self.images.encode_sources(encoder, p)?;
        }
        Ok(())
    }
    fn admit(&self, pages: &PreparedNativeScenePages) -> Result<(), Error> {
        if Arc::ptr_eq(&self.owner, &pages.owner) {
            Ok(())
        } else {
            Err(Error::ResourceDomain)
        }
    }
}
impl PreparedNativeScenePages {
    pub fn source(&self) -> &Arc<NativeSceneRasterPages> {
        &self.source
    }
    pub fn external_images(&self) -> &NativeExternalImages {
        &self.external
    }
    pub fn material(&self) -> &Arc<PreparedNativeMaterials> {
        &self.material
    }
    pub fn vector(&self) -> Option<&Arc<NativeVectorRenderPage>> {
        self.vector.as_ref()
    }
    pub fn paths(&self) -> Option<&Arc<NativePathRenderPage>> {
        self.paths.as_ref()
    }
    pub fn glyphs(&self) -> Option<&Arc<NativeGlyphRenderPage>> {
        self.glyphs.as_ref()
    }
    pub fn images(&self) -> Option<&Arc<PreparedNativeImagePage>> {
        self.images.as_ref()
    }
    pub fn three_d(&self) -> Option<&Arc<PreparedNativeThreeDPage>> {
        self.three_d.as_ref()
    }
    pub fn metrics(&self) -> NativeScenePageMetrics {
        self.metrics
    }
    pub fn retain_submitted(self: &Arc<Self>, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
