use super::*;
use std::task::{Poll, Waker};

/// Original complete mapped output, including unused slots and summary counters.
/// Source owners remain live while callers resolve primitive indices to commands.
#[derive(Clone, Debug)]
pub struct HitQueryOutput {
    index: Arc<ArchiveHitIndex>,
    records: Vec<HitResult>,
    list: bool,
    execution: HitQueryExecution,
}
impl HitQueryOutput {
    pub fn index(&self) -> &Arc<ArchiveHitIndex> {
        &self.index
    }
    pub fn summary(&self) -> HitResult {
        self.records[0]
    }
    pub fn raw_records(&self) -> &[HitResult] {
        &self.records
    }
    pub fn execution(&self) -> HitQueryExecution {
        self.execution
    }
    pub fn hits(&self) -> &[HitResult] {
        let values = if self.list {
            &self.records[1..]
        } else {
            &self.records[..]
        };
        &values[..values.iter().take_while(|r| r.has_hit()).count()]
    }
    /// Copy only actual hits. The caller's unused tail is never overwritten.
    pub fn copy_hits(&self, destination: &mut [HitResult]) -> usize {
        let hits = self.hits();
        let count = destination.len().min(hits.len());
        destination[..count].copy_from_slice(&hits[..count]);
        count
    }
}
#[derive(Default)]
struct Signal {
    result: Option<std::result::Result<(), wgpu::BufferAsyncError>>,
    waker: Option<Waker>,
}
struct Pending {
    #[cfg_attr(target_arch = "wasm32", allow(dead_code))]
    device: wgpu::Device,
    staging: wgpu::Buffer,
    #[cfg_attr(target_arch = "wasm32", allow(dead_code))]
    submission: wgpu::SubmissionIndex,
    resources: Arc<Resources>,
    execution: HitQueryExecution,
    capacity: Option<usize>,
    signal: Arc<Mutex<Signal>>,
}
/// Browser-safe map work. Abandonment drops only its actual resource owners;
/// there is no destructor polling, global context lookup or source callback.
pub struct HitQueryWork {
    ready: Option<HitQueryOutput>,
    pending: Option<Pending>,
}
impl HitQueryWork {
    pub(super) fn empty(index: Arc<ArchiveHitIndex>, execution: HitQueryExecution) -> Self {
        Self {
            ready: Some(HitQueryOutput {
                index,
                records: vec![HitResult::default()],
                list: true,
                execution,
            }),
            pending: None,
        }
    }
    pub(super) fn pending(
        device: wgpu::Device,
        staging: wgpu::Buffer,
        submission: wgpu::SubmissionIndex,
        resources: Arc<Resources>,
        execution: HitQueryExecution,
        capacity: Option<usize>,
    ) -> Self {
        let signal = Arc::new(Mutex::new(Signal::default()));
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
        Self {
            ready: None,
            pending: Some(Pending {
                device,
                staging,
                submission,
                resources,
                execution,
                capacity,
                signal,
            }),
        }
    }
    /// Native hosts drive Device::poll while awaiting; browsers yield normally.
    pub async fn complete(mut self) -> Result<HitQueryOutput> {
        if let Some(output) = self.ready.take() {
            return Ok(output);
        }
        let pending = self.pending.take().ok_or(Error::ResourceDomain)?;
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
        .map_err(|e| HitQueryError::Readback(e.to_string()))?;
        pending.finish()
    }
    /// Preserve the original GpuBuffer thirty-second native mapping bound.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn complete_blocking(mut self) -> Result<HitQueryOutput> {
        if let Some(output) = self.ready.take() {
            return Ok(output);
        }
        let pending = self.pending.take().ok_or(Error::ResourceDomain)?;
        pending
            .device
            .poll(wgpu::PollType::Wait {
                submission_index: Some(pending.submission.clone()),
                timeout: Some(std::time::Duration::from_secs(30)),
            })
            .map_err(|e| HitQueryError::Readback(e.to_string()))?;
        pending
            .signal
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .result
            .take()
            .ok_or_else(|| {
                HitQueryError::Readback("mapping exceeded the original native bound".into())
            })?
            .map_err(|e| HitQueryError::Readback(e.to_string()))?;
        pending.finish()
    }
}
impl Pending {
    fn finish(self) -> Result<HitQueryOutput> {
        let view = self
            .staging
            .slice(..)
            .get_mapped_range()
            .map_err(|e| HitQueryError::Readback(e.to_string()))?;
        let result = (|| {
            let count = self.capacity.map_or(1, |n| n + 1);
            if view.len() != count * 32 {
                return Err(HitQueryError::Readback("unexpected result extent".into()));
            }
            let mut records = Vec::new();
            records
                .try_reserve_exact(count)
                .map_err(|_| Error::Capacity)?;
            for bytes in view.chunks_exact(32) {
                records.push(bytemuck::pod_read_unaligned::<HitResult>(bytes));
            }
            if self.execution == HitQueryExecution::OrderedStages && records[0].hit == u32::MAX {
                return Err(HitQueryError::CandidateOverflow);
            }
            Ok(HitQueryOutput {
                index: self.resources.index.clone(),
                records,
                list: self.capacity.is_some(),
                execution: self.execution,
            })
        })();
        drop(view);
        self.staging.unmap();
        result
    }
}
