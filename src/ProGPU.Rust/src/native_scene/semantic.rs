//! Original scene-stream semantic admission, separate from execution admission.
use super::*;
use NativeSceneValidationError as E;
use wire::constants::*;
pub(crate) mod brush;
pub(crate) mod color_glyph;
mod commands;
pub(crate) mod hit;
mod inventory;
pub(crate) mod layer_mask;
mod resources;
pub(crate) mod rgb_glyph;
pub(crate) mod shader_effect;
pub(crate) mod source_draw;
pub(crate) mod text;
pub(crate) mod values;
pub(crate) use brush::{stored_stop_count, valid_brush_records};
pub use inventory::{
    NativeSceneMask, NativeScenePayload, NativeSceneResource, NativeSceneShader, NativeWireRecords,
};
pub(crate) use rgb_glyph::{
    descriptor as valid_rgb_glyph_descriptor, tile as valid_rgb_glyph_tile,
};

/// Original failure diagnostics retain the header that had actually been read.
#[derive(Clone, Copy, Debug, PartialEq, thiserror::Error)]
#[error("native scene semantic {error}")]
pub struct NativeSceneAdmissionError {
    pub header: wire::Header,
    pub error: NativeSceneTransportError,
}
impl NativeSceneAdmissionError {
    pub fn status(&self) -> u32 {
        if self.error.out_of_memory {
            STATUS_OUT_OF_MEMORY
        } else if self.error.kind == E::Unsupported {
            STATUS_UNSUPPORTED
        } else {
            STATUS_INVALID_ARGUMENT
        }
    }
    /// Original write_metrics failure fields; partial work never appears as success.
    pub fn metrics(&self) -> wire::Metrics {
        metrics(self.header, 0, 0, 0, self.error.kind, self.error.offset)
    }
    pub fn write_metrics(&self, destination: &mut wire::Metrics) {
        publish_metrics(destination, self.metrics());
    }
}

/// Immutable owned stream admitted by the original native scene validator.
/// Execution-specific geometry, provider, external-image and target gates remain
/// independent, exactly as in the native source. This type is not a GPU handle.
#[derive(Clone, Debug)]
pub struct NativeScene {
    envelope: NativeSceneEnvelope,
    metrics: wire::Metrics,
}
impl NativeScene {
    pub fn new(bytes: impl Into<Arc<[u8]>>) -> Result<Self, NativeSceneAdmissionError> {
        let bytes = bytes.into();
        let admitted = admit(&bytes, 0)?;
        Ok(Self {
            envelope: NativeSceneEnvelope::from_admitted(bytes, admitted.header),
            metrics: metrics(
                admitted.header,
                admitted.draws,
                admitted.depth,
                admitted.payload_bytes,
                E::None,
                0,
            ),
        })
    }
    pub fn envelope(&self) -> &NativeSceneEnvelope {
        &self.envelope
    }
    pub fn metrics(&self) -> wire::Metrics {
        self.metrics
    }
    pub fn write_metrics(&self, destination: &mut wire::Metrics) {
        publish_metrics(destination, self.metrics);
    }
    pub fn resource(&self, index: u32) -> Option<NativeSceneResource<'_>> {
        self.envelope.resource(index).map(NativeSceneResource::new)
    }
    pub fn resources(&self) -> impl ExactSizeIterator<Item = NativeSceneResource<'_>> {
        self.envelope.resources().map(NativeSceneResource::new)
    }
    pub fn commands(&self) -> Commands<'_> {
        self.envelope.commands()
    }
    /// Original same-ID generation comparison, after complete candidate admission.
    pub fn admit_resource_generations_after(&self, previous: &Self) -> TransportResult<()> {
        self.envelope
            .admit_resource_generations_after(&previous.envelope)
    }
}
#[derive(Clone, Copy)]
pub(crate) struct Input<'a> {
    pub bytes: &'a [u8],
    pub header: wire::Header,
}
impl<'a> Input<'a> {
    pub fn read<T: wire::WireRecord>(&self, offset: u32) -> TransportResult<T> {
        self.bytes
            .get(offset as usize..)
            .and_then(T::read)
            .ok_or_else(|| error(E::Value, offset))
    }
    pub fn span(&self, offset: u32, size: u32) -> TransportResult<&'a [u8]> {
        let end = (offset as usize)
            .checked_add(size as usize)
            .ok_or_else(|| error(E::Range, offset))?;
        self.bytes
            .get(offset as usize..end)
            .ok_or_else(|| error(E::Range, offset))
    }
    pub fn resource(&self, index: u32) -> TransportResult<wire::Resource> {
        if index >= self.header.resource_count {
            return Err(error(E::Range, self.header.resource_offset));
        }
        self.read(self.header.resource_offset + index * self.header.resource_stride)
    }
}
struct Admitted {
    header: wire::Header,
    draws: u32,
    depth: u32,
    payload_bytes: u64,
}
fn admit(bytes: &[u8], depth: u32) -> Result<Admitted, NativeSceneAdmissionError> {
    if depth >= MAX_PICTURE_DEPTH {
        return Err(NativeSceneAdmissionError {
            header: Default::default(),
            error: NativeSceneTransportError {
                kind: E::Range,
                offset: 0,
                out_of_memory: true,
            },
        });
    }
    let header = if !cfg!(target_endian = "big") && bytes.len() <= MAX_STREAM_BYTES as usize {
        wire::Header::read(bytes).unwrap_or_default()
    } else {
        Default::default()
    };
    let h = envelope::read_header(bytes)
        .map_err(|error| NativeSceneAdmissionError { header, error })?;
    let input = Input { bytes, header: h };
    let result = (|| {
        // Payload validation remains interleaved with its own resource descriptor.
        // An Envelope::new prepass would report a later descriptor error first.
        let payload_bytes = resources::admit_resources(&input, depth + 1)?;
        let (draws, maximum_depth, payload_bytes) =
            commands::admit_commands(&input, payload_bytes)?;
        Ok(Admitted {
            header: h,
            draws,
            depth: maximum_depth,
            payload_bytes,
        })
    })();
    result.map_err(|error| NativeSceneAdmissionError { header: h, error })
}
fn metrics(
    h: wire::Header,
    draws: u32,
    depth: u32,
    payload: u64,
    error: E,
    offset: u32,
) -> wire::Metrics {
    wire::Metrics {
        struct_size: wire::Metrics::WIRE_SIZE as u32,
        command_count: h.command_count,
        resource_count: h.resource_count,
        draw_count: draws,
        maximum_stack_depth: depth,
        validation_error: error as u32,
        error_offset: offset,
        scene_id: h.scene_id,
        generation: h.generation,
        snapshot_bytes: u64::from(h.total_size),
        payload_bytes: payload,
        ..Default::default()
    }
}
fn oom(kind: E, offset: u32) -> NativeSceneTransportError {
    NativeSceneTransportError {
        kind,
        offset,
        out_of_memory: true,
    }
}
fn publish_metrics(destination: &mut wire::Metrics, mut value: wire::Metrics) {
    if destination.struct_size >= wire::Metrics::WIRE_SIZE as u32 {
        value.struct_size = destination.struct_size;
        *destination = value;
    }
}
