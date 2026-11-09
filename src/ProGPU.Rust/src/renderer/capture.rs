//! Owned isolated capture and original effect execution in one submission.
use super::*;
use crate::{
    effects::{EffectChain, EffectEngine, EffectMetrics},
    image::{GpuImage, ImageAlpha},
};

/// Actual isolated scene output. Every capture owns a fresh texture generation;
/// later captures do not overwrite it. Retained image draws supply final opacity,
/// masks and placement independently, after the original effect chain.
pub struct CapturedScene {
    image: GpuImage,
    frame: Frame,
    source_metrics: UploadMetrics,
    replay_metrics: ReplayMetrics,
    capture_bytes: u64,
    effects: Option<EffectMetrics>,
}
impl CapturedScene {
    /// Actual final premultiplied capture/effect output.
    pub fn image(&self) -> &GpuImage {
        &self.image
    }
    /// Transfer the output handle into a retained image or caller-owned cache.
    pub fn into_image(self) -> GpuImage {
        self.image
    }
    /// Actual source physical target, with its original scalar DPI.
    pub fn frame(&self) -> Frame {
        self.frame
    }
    /// Earlier source preparation metrics, not counted again as capture uploads.
    /// Per-occurrence target Clear uploads are reported by `source_replay_metrics`.
    pub fn source_preparation_metrics(&self) -> UploadMetrics {
        self.source_metrics
    }
    /// Actual source replay work encoded in this capture, including original
    /// target Clear uploads independently of previous source preparation.
    pub fn source_replay_metrics(&self) -> ReplayMetrics {
        self.replay_metrics
    }
    /// Actual transparent capture allocation bytes, before effect intermediates.
    pub fn capture_bytes(&self) -> u64 {
        self.capture_bytes
    }
    /// Actual original effect passes and allocated resources, if selected.
    pub fn effect_metrics(&self) -> Option<EffectMetrics> {
        self.effects
    }
}
impl Renderer {
    /// Select the typed original effect route before first use. Pipeline
    /// creation remains lazy; unavailable forced routes reject immediately.
    pub fn configure_effects(&mut self, preference: ExecutionPreference) -> Result<(), Error> {
        if self.effects.is_some() {
            return Err(Error::ResourceDomain);
        }
        self.effects = Some(EffectEngine::new(&self.device, &self.queue, preference)?);
        Ok(())
    }

    /// Render the exact prepared physical frame to an owned transparent target,
    /// apply the optional original effect chain, and submit once. Original draw
    /// order, direct masks, scissors and text residency gates remain unchanged.
    ///
    /// This is explicit capture, not automatic layer-bounds or source-AA group
    /// policy. Supply actual padded capture geometry/frame before preparation;
    /// no logical bounds, source origin or content revision is inferred here.
    /// Effects see the materialized source before final image opacity/masking.
    pub async fn capture(
        &mut self,
        scene: &PreparedScene,
        effects: Option<&EffectChain>,
    ) -> Result<CapturedScene, Error> {
        if !self.is_scene_current(scene) {
            return Err(Error::ResourceDomain);
        }
        scene
            .frame
            .uniforms(self.device.limits().max_texture_dimension_2d)?;
        let capture_bytes = u64::from(scene.frame.width)
            .checked_mul(u64::from(scene.frame.height))
            .and_then(|bytes| bytes.checked_mul(4))
            .ok_or(Error::Capacity)?;
        if let Some(chain) = effects {
            chain.plan([scene.frame.dpi_scale; 2])?;
        }
        // Exclusive async candidate: cancellation never leaves half-prepared
        // renderer state. Existing outputs retain their actual GPU owners.
        let mut engine = if effects.is_some() {
            Some(match self.effects.take() {
                Some(engine) => engine,
                None => {
                    EffectEngine::new(&self.device, &self.queue, ExecutionPreference::Automatic)?
                }
            })
        } else {
            None
        };
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let texture = self.device.create_texture(&wgpu::TextureDescriptor {
            label: Some("ProGPU owned isolated scene capture"),
            size: wgpu::Extent3d {
                width: scene.frame.width,
                height: scene.frame.height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: self.format,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT
                | wgpu::TextureUsages::TEXTURE_BINDING
                | wgpu::TextureUsages::COPY_SRC
                | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });
        let view = texture.create_view(&Default::default());
        let image = GpuImage::from_texture(&self.device, &texture, ImageAlpha::Premultiplied);
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let mut image = image?;
        let program = if let (Some(engine), Some(chain)) = (&mut engine, effects) {
            Some(
                engine
                    .prepare(&image, chain, [scene.frame.dpi_scale; 2])
                    .await?,
            )
        } else {
            None
        };
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU ordered capture and effect chain"),
            });
        let result = (|| {
            self.encode(&mut encoder, scene, &view, wgpu::Color::TRANSPARENT)?;
            if let (Some(engine), Some(program)) = (&engine, &program) {
                let output = engine.encode(&mut encoder, program)?;
                let metrics = output.metrics();
                image = output.into_image();
                Ok(Some(metrics))
            } else {
                Ok::<_, Error>(None)
            }
        })();
        let commands = encoder.finish();
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        let effect_metrics = result?;
        self.queue.submit([commands]);
        if let Some(engine) = engine {
            self.effects = Some(engine);
        }
        Ok(CapturedScene {
            image,
            frame: scene.frame,
            source_metrics: scene.metrics,
            replay_metrics: scene.replay_metrics(),
            capture_bytes,
            effects: effect_metrics,
        })
    }
}
