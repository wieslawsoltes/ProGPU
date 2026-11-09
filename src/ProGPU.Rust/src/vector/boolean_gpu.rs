//! Original two-pass GPU Boolean reconstruction, with explicit owned device/
//! pipeline resources and cancellation-safe mapping. No CPU geometry fallback.
use super::*;
use crate::{
    path::{ExecutionPath, ExecutionPreference, Segment},
    shaders,
};
use std::{
    sync::{Arc, Mutex},
    task::{Poll, Waker},
};
use wgpu::util::DeviceExt;
/// Resource/validation/readback failures without publishing partial geometry.
#[derive(Debug, thiserror::Error)]
pub enum PathOperationError {
    /// Original source/capacity or GPU validation admission failure.
    #[error(transparent)]
    Render(#[from] Error),
    /// Mapping, polling or mapped-range failure retains the actual diagnostic.
    #[error("Boolean geometry readback failed: {0}")]
    Readback(String),
}
/// Actual cumulative submissions/resources; these are not performance estimates.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BooleanGpuMetrics {
    /// Actual two-pass GPU submissions.
    pub submissions: u64,
    /// Actual compute passes (geometry followed by finalizer).
    pub compute_passes: u64,
    /// Original input/uniform and counter-reset bytes uploaded.
    pub upload_bytes: u64,
    /// Actual copied staging capacity, including the original header.
    pub readback_bytes: u64,
    /// Actual source input segments dispatched.
    pub input_segments: u64,
}
/// Explicit-device owner of the two canonical reconstruction pipelines. Reuse
/// this owner across operations; no global Current-context selection is needed.
pub struct BooleanSolver {
    device: wgpu::Device,
    queue: wgpu::Queue,
    geometry: wgpu::ComputePipeline,
    finalizer: wgpu::ComputePipeline,
    metrics: BooleanGpuMetrics,
}
impl BooleanSolver {
    /// Create the unchanged compute kernels under default compiler options.
    /// Forced non-compute routes reject: original atomics/storage writes require
    /// this compute contract, and no substitute CPU algorithm is selected.
    pub async fn new(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        preference: ExecutionPreference,
    ) -> Result<Self, Error> {
        let limits = device.limits();
        if !matches!(
            preference,
            ExecutionPreference::Automatic | ExecutionPreference::NativeCompute
        ) || limits.max_compute_workgroup_size_x < 64
            || limits.max_compute_invocations_per_workgroup < 64
            || limits.max_storage_buffers_per_shader_stage < 6
        {
            return Err(Error::UnsupportedExecution);
        }
        let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
        let geometry = pipeline(
            device,
            shaders::PATH_OPERATION,
            "ProGPU original Boolean geometry",
        );
        let finalizer = pipeline(
            device,
            shaders::PATH_OPERATION_FINALIZER,
            "ProGPU original Boolean finalizer",
        );
        if let Some(error) = scope.pop().await {
            return Err(error.into());
        }
        Ok(Self {
            device: device.clone(),
            queue: queue.clone(),
            geometry,
            finalizer,
            metrics: Default::default(),
        })
    }
    /// Actual resolved compute route.
    pub fn execution_path(&self) -> ExecutionPath {
        ExecutionPath::NativeCompute
    }
    /// Actual cumulative work recorded by this owner.
    pub fn metrics(&self) -> BooleanGpuMetrics {
        self.metrics
    }
    /// Begin one original operation on resolved ordinary paths. Immediate
    /// branches allocate no GPU work. GPU output owns its mapping/submission
    /// generation independently of this solver and may complete asynchronously.
    pub async fn begin(
        &mut self,
        a: &PathGeometry,
        b: &PathGeometry,
        operation: BooleanOperation,
    ) -> Result<BooleanWork, PathOperationError> {
        if let Some(result) = a.immediate_boolean(b, operation)? {
            return Ok(BooleanWork {
                state: Work::Immediate(result),
            });
        }
        let mut result = PathGeometry {
            fill_rule: boolean::output_fill(a, b, operation),
            ..Default::default()
        };
        let input_a = a.compile_boolean_input()?;
        let input_b = b.compile_boolean_input()?;
        let Some(record_a) = input_a.record else {
            if matches!(
                operation,
                BooleanOperation::Union
                    | BooleanOperation::ExclusiveOr
                    | BooleanOperation::ReverseDifference
            ) {
                result.fill_rule = b.fill_rule;
                boolean::copy_figures(b, &mut result)?;
            }
            return Ok(BooleanWork {
                state: Work::Immediate(result),
            });
        };
        let Some(record_b) = input_b.record else {
            if matches!(
                operation,
                BooleanOperation::Difference
                    | BooleanOperation::Union
                    | BooleanOperation::ExclusiveOr
            ) {
                result.fill_rule = a.fill_rule;
                boolean::copy_figures(a, &mut result)?;
            }
            return Ok(BooleanWork {
                state: Work::Immediate(result),
            });
        };
        let count_a = i32::try_from(input_a.segments.len()).map_err(|_| Error::Capacity)?;
        let count_b = i32::try_from(input_b.segments.len()).map_err(|_| Error::Capacity)?;
        let maximum = CompiledPath::max_destination_segments(count_a, count_b)?;
        let bytes = 16u64 + u64::from(maximum) * 48;
        let input_count = count_a as u64 + count_b as u64;
        let groups = input_count.div_ceil(64).max(1);
        let limits = self.device.limits();
        if bytes > u64::from(u32::MAX)
            || bytes > limits.max_buffer_size
            || bytes > limits.max_storage_buffer_binding_size
            || groups > u64::from(limits.max_compute_workgroups_per_dimension)
        {
            return Err(Error::Capacity.into());
        }
        for count in [count_a, count_b] {
            let bytes = count as u64 * 48;
            if bytes > limits.max_buffer_size || bytes > limits.max_storage_buffer_binding_size {
                return Err(Error::Capacity.into());
            }
        }
        let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
        let record_a = upload(
            &self.device,
            bytemuck::bytes_of(&record_a),
            wgpu::BufferUsages::STORAGE,
            "Boolean A record",
        );
        let record_b = upload(
            &self.device,
            bytemuck::bytes_of(&record_b),
            wgpu::BufferUsages::STORAGE,
            "Boolean B record",
        );
        let segments_a = upload(
            &self.device,
            bytemuck::cast_slice(&input_a.segments),
            wgpu::BufferUsages::STORAGE,
            "Boolean A segments",
        );
        let segments_b = upload(
            &self.device,
            bytemuck::cast_slice(&input_b.segments),
            wgpu::BufferUsages::STORAGE,
            "Boolean B segments",
        );
        let destination = buffer(
            &self.device,
            32,
            wgpu::BufferUsages::STORAGE
                | wgpu::BufferUsages::COPY_DST
                | wgpu::BufferUsages::COPY_SRC,
            "Boolean output record",
        );
        let output = buffer(
            &self.device,
            bytes,
            wgpu::BufferUsages::STORAGE
                | wgpu::BufferUsages::COPY_DST
                | wgpu::BufferUsages::COPY_SRC,
            "Boolean output segments",
        );
        self.queue.write_buffer(&output, 0, &[0; 4]);
        let uniforms = upload(
            &self.device,
            bytemuck::cast_slice(&[operation as u32, maximum, 0, 0]),
            wgpu::BufferUsages::UNIFORM,
            "Boolean original uniforms",
        );
        let geometry = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU Boolean geometry inputs"),
            layout: &self.geometry.get_bind_group_layout(0),
            entries: &[
                entry(0, &uniforms),
                entry(1, &record_a),
                entry(2, &segments_a),
                entry(3, &record_b),
                entry(4, &segments_b),
                entry(5, &destination),
                entry(6, &output),
            ],
        });
        let finalizer = self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("ProGPU Boolean finalizer inputs"),
            layout: &self.finalizer.get_bind_group_layout(0),
            entries: &[
                entry(0, &uniforms),
                entry(1, &record_a),
                entry(2, &record_b),
                entry(3, &destination),
                wgpu::BindGroupEntry {
                    binding: 4,
                    resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                        buffer: &output,
                        offset: 0,
                        size: std::num::NonZeroU64::new(16),
                    }),
                },
            ],
        });
        let staging = buffer(
            &self.device,
            bytes,
            wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
            "Boolean original readback",
        );
        let mut encoder = self
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU Boolean two-pass reconstruction"),
            });
        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("ProGPU Boolean classify/split"),
                timestamp_writes: None,
            });
            pass.set_pipeline(&self.geometry);
            pass.set_bind_group(0, &geometry, &[]);
            pass.dispatch_workgroups(groups as u32, 1, 1);
        }
        {
            let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                label: Some("ProGPU Boolean finalize"),
                timestamp_writes: None,
            });
            pass.set_pipeline(&self.finalizer);
            pass.set_bind_group(0, &finalizer, &[]);
            pass.dispatch_workgroups(1, 1, 1);
        }
        encoder.copy_buffer_to_buffer(&output, 0, &staging, 0, bytes);
        let submission = self.queue.submit([encoder.finish()]);
        self.metrics.submissions += 1;
        self.metrics.compute_passes += 2;
        self.metrics.upload_bytes += 64 + input_count * 48 + 16 + 4;
        self.metrics.readback_bytes += bytes;
        self.metrics.input_segments += input_count;
        let signal = Arc::new(Mutex::new(MapSignal::default()));
        let callback = signal.clone();
        staging
            .slice(..)
            .map_async(wgpu::MapMode::Read, move |result| {
                let wake = {
                    let mut state = callback.lock().unwrap_or_else(|e| e.into_inner());
                    state.result = Some(result);
                    state.waker.take()
                };
                if let Some(wake) = wake {
                    wake.wake();
                }
            });
        let work = BooleanWork {
            state: Work::Pending(Pending {
                device: self.device.clone(),
                staging,
                submission,
                maximum,
                fill_rule: result.fill_rule,
                signal,
            }),
        };
        if let Some(error) = scope.pop().await {
            return Err(Error::from(error).into());
        }
        Ok(work)
    }
    /// Resolve complete deferred operands in original A-before-B order, then
    /// perform the requested operation. Native hosts must drive Device::poll
    /// while awaiting mapping; browser hosts use their WebGPU event loop.
    pub async fn combine(
        &mut self,
        a: Arc<PathGeometry>,
        b: Arc<PathGeometry>,
        operation: BooleanOperation,
    ) -> Result<PathGeometry, PathOperationError> {
        let a = self.resolve(a).await?;
        let b = self.resolve(b).await?;
        self.begin(&a, &b, operation).await?.complete().await
    }

    /// Prepare retained fill input, preserving direct/program evaluation when
    /// admitted. Only the original deferred fallback performs reconstruction.
    /// Native hosts drive Device::poll while awaiting an actual readback.
    pub async fn prepare_raster_path(
        &mut self,
        path: Arc<PathGeometry>,
    ) -> Result<Option<crate::path::Path>, PathOperationError> {
        let mut source = path;
        loop {
            match source.compile_raster_path() {
                Err(Error::UnsupportedExecution) => {
                    // CompilePathCore can first select a fill-compatible exact
                    // operand. Preserve that route before invoking Combine.
                    let mut selected = source.clone();
                    loop {
                        let Some(deferred) = &selected.deferred else {
                            return Err(Error::UnsupportedExecution.into());
                        };
                        let (a, b) = deferred.operands();
                        let operand = match deferred.query_kind() {
                            CombinedQueryKind::ResultOperandA => Some(a),
                            CombinedQueryKind::ResultOperandB => Some(b),
                            _ => None,
                        };
                        if let Some(operand) = operand
                            && (selected.fill_rule == operand.fill_rule
                                || operand.figures.len() == 1)
                        {
                            selected = operand.clone();
                            continue;
                        }
                        source = Arc::new(
                            self.combine(a.clone(), b.clone(), deferred.operation())
                                .await?,
                        );
                        break;
                    }
                }
                result => return result.map_err(Into::into),
            }
        }
    }
    /// Materialize a complete deferred tree without recursive async stack frames.
    /// Cached bounds classifications do not replace the original Combine route.
    pub async fn resolve(
        &mut self,
        path: Arc<PathGeometry>,
    ) -> Result<Arc<PathGeometry>, PathOperationError> {
        enum Task {
            Visit(Arc<PathGeometry>),
            Combine(BooleanOperation),
        }
        let mut tasks = vec![Task::Visit(path)];
        let mut values = Vec::new();
        while let Some(task) = tasks.pop() {
            match task {
                Task::Visit(path) => {
                    if let Some(d) = &path.deferred {
                        let (a, b) = d.operands();
                        tasks.try_reserve(3).map_err(|_| Error::Capacity)?;
                        tasks.push(Task::Combine(d.operation()));
                        tasks.push(Task::Visit(b.clone()));
                        tasks.push(Task::Visit(a.clone()));
                    } else {
                        values.try_reserve(1).map_err(|_| Error::Capacity)?;
                        values.push(path);
                    }
                }
                Task::Combine(op) => {
                    let b = values.pop().ok_or(Error::InvalidInput)?;
                    let a = values.pop().ok_or(Error::InvalidInput)?;
                    let result = self.begin(&a, &b, op).await?.complete().await?;
                    values.push(Arc::new(result));
                }
            }
        }
        values.pop().ok_or_else(|| Error::InvalidInput.into())
    }
}
/// One immediate result or an actual submission-owned asynchronous readback.
/// Dropping unfinished work cancels/unmaps only its own staging buffer; submitted
/// resources remain owned by wgpu until retirement. No Drop flush or device poll.
pub struct BooleanWork {
    state: Work,
}
enum Work {
    Immediate(PathGeometry),
    Pending(Pending),
}
impl BooleanWork {
    /// Whether this result required the original GPU reconstruction path.
    pub fn uses_gpu(&self) -> bool {
        matches!(self.state, Work::Pending(_))
    }
    /// Await actual mapping and reconstruct the original ordered output. Native
    /// callers drive Device::poll from their host; WASM yields to the browser.
    pub async fn complete(self) -> Result<PathGeometry, PathOperationError> {
        match self.state {
            Work::Immediate(path) => Ok(path),
            Work::Pending(pending) => {
                std::future::poll_fn(|cx| {
                    let mut state = pending.signal.lock().unwrap_or_else(|e| e.into_inner());
                    if let Some(result) = state.result.take() {
                        Poll::Ready(result)
                    } else {
                        if state
                            .waker
                            .as_ref()
                            .is_none_or(|w| !w.will_wake(cx.waker()))
                        {
                            state.waker = Some(cx.waker().clone());
                        }
                        Poll::Pending
                    }
                })
                .await
                .map_err(|e| PathOperationError::Readback(e.to_string()))?;
                pending.read()
            }
        }
    }
    /// Original native five-second synchronous mapping bound. Browser callers
    /// use complete() and never block their event loop.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn complete_blocking(self) -> Result<PathGeometry, PathOperationError> {
        match self.state {
            Work::Immediate(path) => Ok(path),
            Work::Pending(pending) => {
                pending
                    .device
                    .poll(wgpu::PollType::Wait {
                        submission_index: Some(pending.submission.clone()),
                        timeout: Some(std::time::Duration::from_secs(5)),
                    })
                    .map_err(|e| PathOperationError::Readback(e.to_string()))?;
                let result = pending
                    .signal
                    .lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .result
                    .take()
                    .ok_or_else(|| {
                        PathOperationError::Readback(
                            "mapping did not complete within the original native wait".into(),
                        )
                    })?;
                result.map_err(|e| PathOperationError::Readback(e.to_string()))?;
                pending.read()
            }
        }
    }
}
#[derive(Default)]
struct MapSignal {
    result: Option<Result<(), wgpu::BufferAsyncError>>,
    waker: Option<Waker>,
}
struct Pending {
    #[cfg_attr(target_arch = "wasm32", allow(dead_code))]
    device: wgpu::Device,
    staging: wgpu::Buffer,
    #[cfg_attr(target_arch = "wasm32", allow(dead_code))]
    submission: wgpu::SubmissionIndex,
    maximum: u32,
    fill_rule: FillRule,
    signal: Arc<Mutex<MapSignal>>,
}
impl Pending {
    fn read(&self) -> Result<PathGeometry, PathOperationError> {
        let view = self
            .staging
            .slice(..)
            .get_mapped_range()
            .map_err(|e| PathOperationError::Readback(e.to_string()))?;
        let header = view
            .get(..4)
            .ok_or_else(|| PathOperationError::Readback("missing output counter".into()))?;
        let count = u32::from_le_bytes(header.try_into().map_err(|_| Error::InvalidInput)?)
            .min(self.maximum) as usize;
        let bytes = view
            .get(16..16 + count * 48)
            .ok_or_else(|| PathOperationError::Readback("truncated output inventory".into()))?;
        let segments = bytemuck::try_cast_slice::<u8, Segment>(bytes)
            .map_err(|e| PathOperationError::Readback(e.to_string()))?;
        let figures = reconstruct_figures(segments)?;
        Ok(PathGeometry {
            figures,
            fill_rule: self.fill_rule,
            ..Default::default()
        })
    }
}
impl Drop for Pending {
    fn drop(&mut self) {
        self.signal.lock().unwrap_or_else(|e| e.into_inner()).waker = None;
        self.staging.unmap();
    }
}
fn pipeline(device: &wgpu::Device, source: &str, label: &str) -> wgpu::ComputePipeline {
    let module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
        label: Some(label),
        source: wgpu::ShaderSource::Wgsl(source.into()),
    });
    device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
        label: Some(label),
        layout: None,
        module: &module,
        entry_point: Some("cs_main"),
        compilation_options: Default::default(),
        cache: None,
    })
}
fn upload(
    device: &wgpu::Device,
    bytes: &[u8],
    usage: wgpu::BufferUsages,
    label: &str,
) -> wgpu::Buffer {
    device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
        label: Some(label),
        contents: bytes,
        usage: usage | wgpu::BufferUsages::COPY_DST,
    })
}
fn buffer(
    device: &wgpu::Device,
    size: u64,
    usage: wgpu::BufferUsages,
    label: &str,
) -> wgpu::Buffer {
    device.create_buffer(&wgpu::BufferDescriptor {
        label: Some(label),
        size,
        usage,
        mapped_at_creation: false,
    })
}
fn entry(binding: u32, buffer: &wgpu::Buffer) -> wgpu::BindGroupEntry<'_> {
    wgpu::BindGroupEntry {
        binding,
        resource: buffer.as_entire_binding(),
    }
}
