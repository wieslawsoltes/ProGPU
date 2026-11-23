//! Retained Mesh3D extension preparation and its ordinary texture consumer.
use super::*;
use crate::{
    mesh3d::{Mesh3DEngine, Mesh3DMetrics, PreparedMesh3D},
    scene::Command,
};
use std::{borrow::Cow, cell::RefCell, collections::HashMap};

#[derive(Default)]
pub(super) struct Mesh3DPlans {
    entries: RefCell<
        HashMap<
            usize,
            (
                Arc<crate::scene::Mesh3DViewportDraw>,
                Arc<crate::mesh3d::Mesh3DResourcePlan>,
            ),
        >,
    >,
}
impl Mesh3DPlans {
    pub(super) fn clear(&mut self) {
        self.entries.get_mut().clear();
    }
}

impl Renderer {
    /// Set the actual source host frame for Mesh3D's original 240-frame aging.
    /// Nested captures share this value. No preparation call advances it implicitly.
    pub fn begin_mesh3d_frame(&mut self, frame: u64) {
        self.mesh3d_frame = Some(frame);
        if let Some(engine) = &mut self.mesh3d {
            engine.begin_frame(frame);
        }
    }
    fn mesh3d_plan(
        &self,
        draw: &Arc<crate::scene::Mesh3DViewportDraw>,
    ) -> Result<Arc<crate::mesh3d::Mesh3DResourcePlan>, Error> {
        let key = Arc::as_ptr(draw) as usize;
        if let Some((_, plan)) = self.mesh3d_plans.entries.borrow().get(&key) {
            return Ok(plan.clone());
        }
        // Capture once, outside the map borrow. The actual retained command owner
        // prevents address reuse throughout this exclusive preparation transaction.
        let plan = Arc::new(draw.request.resource_plan(&self.device)?);
        let mut entries = self.mesh3d_plans.entries.borrow_mut();
        if entries.len() >= 16384 {
            return Err(Error::Capacity);
        }
        entries.try_reserve(1).map_err(|_| Error::Capacity)?;
        entries.insert(key, (draw.clone(), plan.clone()));
        Ok(plan)
    }
    pub(super) fn preflight_mesh3d(&self, scene: &Scene) -> Result<(u64, u32), Error> {
        let mut bytes = 0u64;
        let mut passes = 0u32;
        for command in &scene.commands {
            if let Command::Mesh3D(draw) = command {
                draw.validate()?;
                if draw.is_empty() {
                    continue;
                }
                let (target_bytes, target_passes) = self.mesh3d_plan(draw)?.cost();
                bytes = bytes.checked_add(target_bytes).ok_or(Error::Capacity)?;
                passes = passes.checked_add(target_passes).ok_or(Error::Capacity)?;
                if bytes > 512 * 1024 * 1024 || passes > 16384 {
                    return Err(Error::Capacity);
                }
            }
        }
        Ok((bytes, passes))
    }
    pub(super) async fn prepare_mesh3d_scene<'a>(
        &mut self,
        scene: &'a Scene,
    ) -> Result<(Cow<'a, Scene>, Vec<Arc<PreparedMesh3D>>), Error> {
        if !scene
            .commands
            .iter()
            .any(|v| matches!(v, Command::Mesh3D(_)))
        {
            return Ok((Cow::Borrowed(scene), Vec::new()));
        }
        self.preflight_mesh3d(scene)?;
        let mut engine = match self.mesh3d.take() {
            Some(engine) => engine,
            None => {
                let mut engine = Mesh3DEngine::new(&self.device, &self.queue)?;
                if let Some(frame) = self.mesh3d_frame {
                    engine.begin_frame(frame);
                }
                engine
            }
        };
        let result = async {
            let mut resolved = scene.clone();
            resolved.commands.clear();
            let capacity = scene.commands.len().checked_mul(2).ok_or(Error::Capacity)?;
            resolved
                .commands
                .try_reserve(capacity)
                .map_err(|_| Error::Capacity)?;
            let mut sources = Vec::new();
            for command in &scene.commands {
                if let Command::Mesh3D(draw) = command {
                    if draw.is_empty() {
                        resolved.commands.push(Command::Empty);
                        continue;
                    }
                    let plan = self.mesh3d_plan(draw)?;
                    let prepared = Arc::new(engine.prepare_with_plan(&draw.request, &plan).await?);
                    let image = draw.image_draw(prepared.image().clone())?;
                    sources.try_reserve(1).map_err(|_| Error::Capacity)?;
                    sources.push(prepared);
                    // Extension8 selects shared last-camera state even though its
                    // own offscreen render uses independent camera uniforms.
                    resolved
                        .commands
                        .push(Command::Mesh3DCamera(draw.request.view * draw.transform));
                    resolved.commands.push(Command::Image(
                        Box::new(image),
                        crate::scene::LayerBlend::SrcOver,
                        None,
                        None,
                    ));
                } else {
                    resolved.commands.push(command.clone());
                }
            }
            Ok((Cow::Owned(resolved), sources))
        }
        .await;
        self.mesh3d = Some(engine);
        result
    }
}
impl PreparedScene {
    /// Actual per-viewport preparation counters, preserving each original source
    /// generation. These describe work and ownership, not pixel qualification.
    pub fn mesh3d_metrics(&self) -> impl ExactSizeIterator<Item = Mesh3DMetrics> + '_ {
        self.mesh3d_sources.iter().map(|source| source.metrics())
    }
    pub(super) fn attach_mesh3d(&mut self, sources: Vec<Arc<PreparedMesh3D>>) -> Result<(), Error> {
        for source in &sources {
            let m = source.metrics();
            self.metrics.geometry_bytes = self
                .metrics
                .geometry_bytes
                .checked_add(
                    m.geometry_vertex_upload_bytes
                        + m.record_upload_bytes
                        + m.record_index_upload_bytes
                        + m.edge_upload_bytes,
                )
                .ok_or(Error::Capacity)?;
            self.metrics.uniform_bytes = self
                .metrics
                .uniform_bytes
                .checked_add(m.uniform_upload_bytes)
                .ok_or(Error::Capacity)?;
            self.metrics.material_bytes = self
                .metrics
                .material_bytes
                .checked_add(m.light_upload_bytes + m.gradient_upload_bytes)
                .ok_or(Error::Capacity)?;
            self.metrics.draws = self
                .metrics
                .draws
                .checked_add(m.draw_calls)
                .and_then(|v| v.checked_add(m.live_blur_passes))
                .ok_or(Error::Capacity)?;
        }
        self.mesh3d_sources = sources;
        Ok(())
    }
    pub(super) fn encode_mesh3d_sources(
        &self,
        encoder: &mut wgpu::CommandEncoder,
    ) -> Result<(), Error> {
        for source in &self.mesh3d_sources {
            source.encode(encoder)?;
        }
        Ok(())
    }
}
