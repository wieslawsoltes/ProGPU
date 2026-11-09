//! Original bounded programmable pixel effects, source frames and owned GPU programs.
//! Bytecode stays authoritative; no WGSL registry replacement or CPU pixel evaluator.
use crate::Error;
use bytemuck::{Pod, Zeroable};
use std::sync::Arc;
mod bytecode;
pub use bytecode::translate;
mod capture;
pub use capture::{ManagedCaptureFrame, SourceCapture, source_pixels_per_unit};
mod frame;
pub use frame::{
    AffineFrameRecord, CaptureFrame, SampleFrame, SampleFrameRecord, SampleLattice, SourceMatrix,
};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{
    LegacyShaderRequest, PreparedShaderEffect, SampleShaderRequest, ShaderEffectEngine,
    ShaderEffectMetrics, ShaderVectorMask,
};

/// Original nearest/linear sampler selection with clamp-to-edge addressing.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ShaderSampling {
    /// Original nearest sampling mode zero.
    Nearest,
    /// Original linear sampling mode one.
    #[default]
    Linear,
}
/// Validated immutable original bytecode and its complete translated body.
#[derive(Clone, Debug)]
pub struct ShaderProgram {
    bytecode: Arc<[u8]>,
    body: Arc<str>,
    source_sampler: u32,
}
impl ShaderProgram {
    /// Retain a complete original ps_2_0 or ps_3_0 generation after bounded admission.
    pub fn new(bytecode: impl Into<Arc<[u8]>>, source_sampler: u32) -> Result<Self, Error> {
        let bytecode = bytecode.into();
        let body = translate(&bytecode, source_sampler)?.into();
        Ok(Self {
            bytecode,
            body,
            source_sampler,
        })
    }
    /// Original byte-for-byte program identity, independent of changing constants.
    pub fn bytecode(&self) -> &[u8] {
        &self.bytecode
    }
    /// Original single sampled register.
    pub fn source_sampler(&self) -> u32 {
        self.source_sampler
    }
    /// Complete owned canonical WGSL body, before wrapper insertion.
    pub fn body(&self) -> &str {
        &self.body
    }
}
/// Immutable-at-preparation effect inputs. Uniform values are copied per target.
#[derive(Clone, Debug)]
pub struct ShaderEffect {
    /// Validated immutable source program.
    pub program: Arc<ShaderProgram>,
    /// Original c0..c31 vectors in unchanged component order.
    pub constants: [[f32; 4]; 32],
    /// Original source sampler filtering policy.
    pub sampling: ShaderSampling,
    /// Original nonzero source revision; never a substitute for byte identity.
    pub revision: u32,
}
impl ShaderEffect {
    /// Retain an original program with zero constants and explicit default linear sampling.
    pub fn new(program: impl Into<Arc<ShaderProgram>>) -> Self {
        Self {
            program: program.into(),
            constants: [[0.0; 4]; 32],
            sampling: Default::default(),
            revision: 1,
        }
    }
    /// Validate all current metadata before resource preparation.
    pub fn validate(&self) -> Result<(), Error> {
        if self.revision == 0 || self.constants.iter().flatten().any(|v| !v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    /// Original complete positive-axis capture derivative override, applied to a
    /// private copy after source constants. Translation contributes no displacement.
    pub fn capture_constants(
        &self,
        derivative: Option<u32>,
        size: [u32; 2],
    ) -> Result<[[f32; 4]; 32], Error> {
        self.validate()?;
        let mut constants = self.constants;
        if let Some(register) = derivative {
            if register >= 32 || size.iter().any(|v| *v == 0 || *v > 1 << 24) {
                return Err(Error::InvalidInput);
            }
            constants[register as usize] = [1.0 / size[0] as f32, 0.0, 0.0, 1.0 / size[1] as f32];
        }
        Ok(constants)
    }
}
/// Original exact version-one 544-byte program descriptor.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct ShaderDescriptor {
    /// Exact 544-byte descriptor size.
    pub struct_size: u32,
    /// Original program descriptor version one.
    pub version: u32,
    /// Exact bytecode byte count.
    pub bytecode_size: u32,
    /// Original source sampler register 0..15.
    pub source_sampler: u32,
    /// Original nearest/linear numeric sampling mode.
    pub sampling_mode: u32,
    /// Original nonzero source revision.
    pub revision: u32,
    /// Original flags, currently zero.
    pub flags: u32,
    /// Original reserved zero.
    pub reserved: u32,
    /// Exact original c0..c31 vectors.
    pub constants: [[f32; 4]; 32],
}
impl ShaderDescriptor {
    /// Validate exact descriptor bytes and own the original program generation.
    pub fn effect(self, bytecode: impl Into<Arc<[u8]>>) -> Result<ShaderEffect, Error> {
        let bytecode = bytecode.into();
        if self.struct_size != 544
            || self.version != 1
            || self.bytecode_size as usize != bytecode.len()
            || self.revision == 0
            || self.sampling_mode > 1
            || self.flags != 0
            || self.reserved != 0
            || self.constants.iter().flatten().any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        Ok(ShaderEffect {
            program: Arc::new(ShaderProgram::new(bytecode, self.source_sampler)?),
            constants: self.constants,
            sampling: if self.sampling_mode == 0 {
                ShaderSampling::Nearest
            } else {
                ShaderSampling::Linear
            },
            revision: self.revision,
        })
    }
}
/// Distinct original source-frame wire versions; older readers must reject new forms.
#[derive(Clone, Copy, Debug)]
pub enum ShaderFrame {
    /// Original implicit complete positive-axis capture (versions one to three).
    Legacy,
    /// Original exact integral residual capture (version four).
    Capture(CaptureFrame),
    /// Original independent final-device sample frame (version five).
    Samples(SampleFrameRecord),
    /// Original full-affine final-device sample frame (version six).
    Affine(AffineFrameRecord),
}
/// Completely imported original wire resource. Source indices still require an
/// owning scene's earlier-picture resolver; bytes alone never qualify a texture.
#[derive(Clone, Debug)]
pub struct ShaderResource {
    effect: ShaderEffect,
    frame: ShaderFrame,
    version: u32,
    sampler: Option<u32>,
    input: Option<u32>,
    derivative: Option<u32>,
    payload: Arc<[u8]>,
}
impl ShaderResource {
    /// Read the exact original v1–v6 resource; validate all redundant frame fields
    /// and bytecode before publishing any result. Reserved words stay fail-closed.
    pub fn read(
        payload: impl Into<Arc<[u8]>>,
        bytecode: impl Into<Arc<[u8]>>,
    ) -> Result<Self, Error> {
        let payload = payload.into();
        let read =
            |offset: usize| u32::from_le_bytes(payload[offset..offset + 4].try_into().unwrap());
        let index = |value: u32| if value == u32::MAX { None } else { Some(value) };
        let (version, program_offset, sampler, input, derivative, frame) = match payload.len() {
            544 => (1, 0, None, None, None, ShaderFrame::Legacy),
            560 => {
                if read(0) != 560 || read(4) != 2 || read(12) != 0 || read(8) == u32::MAX {
                    return Err(Error::InvalidInput);
                }
                (2, 16, index(read(8)), None, None, ShaderFrame::Legacy)
            }
            576 => {
                if read(0) != 576
                    || read(4) != 3
                    || read(12) >= 32
                    || (16..32).step_by(4).any(|i| read(i) != 0)
                {
                    return Err(Error::InvalidInput);
                }
                (
                    3,
                    32,
                    index(read(8)),
                    None,
                    Some(read(12)),
                    ShaderFrame::Legacy,
                )
            }
            648 => {
                if read(0) != 648
                    || read(4) != 4
                    || read(12) != u32::MAX && read(12) >= 32
                    || (16..32).step_by(4).any(|i| read(i) != 0)
                {
                    return Err(Error::InvalidInput);
                }
                let frame = bytemuck::pod_read_unaligned::<CaptureFrame>(&payload[32..104]);
                frame.validate()?;
                (
                    4,
                    104,
                    index(read(8)),
                    None,
                    index(read(12)),
                    ShaderFrame::Capture(frame),
                )
            }
            704 => {
                if read(0) != 704
                    || read(4) != 5
                    || read(8) == u32::MAX
                    || read(16) != u32::MAX && read(16) >= 32
                    || (20..32).step_by(4).any(|i| read(i) != 0)
                {
                    return Err(Error::InvalidInput);
                }
                let frame = bytemuck::pod_read_unaligned::<SampleFrameRecord>(&payload[32..160]);
                frame.resolve()?;
                (
                    5,
                    160,
                    index(read(12)),
                    Some(read(8)),
                    index(read(16)),
                    ShaderFrame::Samples(frame),
                )
            }
            720 => {
                if read(0) != 720
                    || read(4) != 6
                    || read(8) == u32::MAX
                    || read(16) != u32::MAX && read(16) >= 32
                    || (20..32).step_by(4).any(|i| read(i) != 0)
                {
                    return Err(Error::InvalidInput);
                }
                let frame = bytemuck::pod_read_unaligned::<AffineFrameRecord>(&payload[32..176]);
                frame.resolve()?;
                (
                    6,
                    176,
                    index(read(12)),
                    Some(read(8)),
                    index(read(16)),
                    ShaderFrame::Affine(frame),
                )
            }
            _ => return Err(Error::InvalidInput),
        };
        let descriptor =
            bytemuck::pod_read_unaligned::<ShaderDescriptor>(&payload[program_offset..]);
        let effect = descriptor.effect(bytecode)?;
        Ok(Self {
            effect,
            frame,
            version,
            sampler,
            input,
            derivative,
            payload,
        })
    }
    /// Actual owned effect generation.
    pub fn effect(&self) -> &ShaderEffect {
        &self.effect
    }
    /// Original wire frame version, with exact metadata preserved.
    pub fn frame(&self) -> ShaderFrame {
        self.frame
    }
    /// Original resource version.
    pub fn version(&self) -> u32 {
        self.version
    }
    /// Earlier same-scene sampler picture, absent for implicit input.
    pub fn sampler_resource_index(&self) -> Option<u32> {
        self.sampler
    }
    /// Earlier complete implicit-input picture for versions five and six.
    pub fn input_resource_index(&self) -> Option<u32> {
        self.input
    }
    /// Optional original UV derivative constant register.
    pub fn derivative_register(&self) -> Option<u32> {
        self.derivative
    }
    /// Exact retained original wire bytes for archives and resource identity.
    pub fn payload(&self) -> &[u8] {
        &self.payload
    }
}
const _: () = assert!(size_of::<ShaderDescriptor>() == 544);
