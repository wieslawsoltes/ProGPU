//! Original sparse homogeneous source-frame arithmetic and independent lattices.
use crate::Error;
use bytemuck::{Pod, Zeroable};
// Rust scalar arithmetic preserves separately rounded source operations; never
// replace these with mul_add or a reconstructed double aggregate.
fn p(a: f32, b: f32) -> f32 {
    a * b
}
fn s(a: f32, b: f32) -> f32 {
    a + b
}
fn q(a: f32, b: f32) -> f32 {
    a / b
}

/// Original sparse row-vector matrix; homogeneous W remains independent.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SourceMatrix {
    /// Original diagonal X/Y/Z/W.
    pub diagonal: [f32; 4],
    /// Original translation X/Y.
    pub translation: [f32; 2],
    /// Original M12/M21 cross terms; zero for the independent axis route.
    pub cross: [f32; 2],
}
impl Default for SourceMatrix {
    fn default() -> Self {
        Self {
            diagonal: [1.0; 4],
            translation: [0.0; 2],
            cross: [0.0; 2],
        }
    }
}
impl SourceMatrix {
    fn finite(self, affine: bool) -> bool {
        self.diagonal
            .into_iter()
            .chain(self.translation)
            .all(f32::is_finite)
            && (!affine || self.cross.into_iter().all(f32::is_finite))
    }
    fn multiply(self, b: Self, affine: bool) -> Self {
        let [x, y, z, w] = self.diagonal;
        let [tx, ty] = self.translation;
        let [xy, yx] = self.cross;
        let [bx, by, bz, bw] = b.diagonal;
        let [btx, bty] = b.translation;
        let [bxy, byx] = b.cross;
        if affine {
            Self {
                diagonal: [
                    s(p(x, bx), p(xy, byx)),
                    s(p(yx, bxy), p(y, by)),
                    p(z, bz),
                    p(w, bw),
                ],
                translation: [
                    s(s(p(tx, bx), p(ty, byx)), p(w, btx)),
                    s(s(p(tx, bxy), p(ty, by)), p(w, bty)),
                ],
                cross: [s(p(x, bxy), p(xy, by)), s(p(yx, bx), p(y, byx))],
            }
        } else {
            Self {
                diagonal: [p(x, bx), p(y, by), p(z, bz), p(w, bw)],
                translation: [s(p(tx, bx), p(w, btx)), s(p(ty, by), p(w, bty))],
                cross: [0.0; 2],
            }
        }
    }
    fn inverse(self, affine: bool) -> Result<Self, Error> {
        if !self.finite(affine) {
            return Err(Error::InvalidInput);
        }
        let [x, y, z, w] = self.diagonal;
        let [tx, ty] = self.translation;
        let [xy, yx] = self.cross;
        let determinant_xy = if affine {
            s(p(x, y), -p(xy, yx))
        } else {
            p(x, y)
        };
        let zw = p(z, w);
        let determinant = p(determinant_xy, zw);
        if !determinant.is_finite() || determinant == 0.0 {
            return Err(Error::InvalidInput);
        }
        let r = q(1.0, determinant);
        let candidate = Self {
            diagonal: [
                p(p(y, zw), r),
                p(p(x, zw), r),
                p(p(determinant_xy, w), r),
                p(p(determinant_xy, z), r),
            ],
            translation: if affine {
                [
                    p(p(s(p(yx, ty), -p(y, tx)), z), r),
                    p(p(s(p(xy, tx), -p(x, ty)), z), r),
                ]
            } else {
                [p(-p(p(y, z), tx), r), p(-p(p(x, z), ty), r)]
            },
            cross: if affine {
                [p(-p(xy, zw), r), p(-p(yx, zw), r)]
            } else {
                [0.0; 2]
            },
        };
        if !candidate.finite(affine) {
            return Err(Error::InvalidInput);
        }
        Ok(candidate)
    }
    fn point(self, x: f32, y: f32) -> [f32; 2] {
        [
            s(
                s(p(x, self.diagonal[0]), p(y, self.cross[1])),
                self.translation[0],
            ),
            s(
                s(p(x, self.cross[0]), p(y, self.diagonal[1])),
                self.translation[1],
            ),
        ]
    }
}
/// Original signed physical lattice; width/height are independent from its origin.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SampleLattice {
    /// Signed physical X/Y origin.
    pub origin: [i32; 2],
    /// Positive physical width/height.
    pub size: [u32; 2],
}
impl SampleLattice {
    fn bounded(edges: [f64; 4]) -> Result<Self, Error> {
        let [left, top, right, bottom] = edges;
        if edges.into_iter().any(|v| !v.is_finite() || v != v.floor())
            || left < -16777216.0
            || top < -16777216.0
            || right > 16777216.0
            || bottom > 16777216.0
            || right <= left
            || bottom <= top
            || right - left > 16384.0
            || bottom - top > 16384.0
        {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            origin: [left as i32, top as i32],
            size: [(right - left) as u32, (bottom - top) as u32],
        })
    }
}
/// Complete source-derived frame; construction preserves original operation order.
#[derive(Clone, Copy, Debug)]
pub struct SampleFrame {
    /// Original separate positive scale-space basis.
    pub source_scale: SourceMatrix,
    /// Full cofactor inverse of that scale basis.
    pub inverse_scale: SourceMatrix,
    /// Original residual, including homogeneous W and reflection.
    pub residual: SourceMatrix,
    /// Complete captured-input lattice, independent of target clipping.
    pub capture: SampleLattice,
    /// Captured texel-origin mapping.
    pub capture_to_device: SourceMatrix,
    /// Original unit quad to device.
    pub unit_to_device: SourceMatrix,
    /// Original cofactor inverse, used for UV derivatives.
    pub device_to_unit: SourceMatrix,
    /// Independent outward-rounded final-device output lattice.
    pub output: SampleLattice,
    /// Distinct original affine route.
    pub affine: bool,
}
impl SampleFrame {
    /// Derive the original diagonal or affine frame from actual source float history.
    /// This validates arithmetic, not whether a caller witnessed source history.
    pub fn new(local: [f32; 4], world: SourceMatrix, affine: bool) -> Result<Self, Error> {
        if !world.finite(affine)
            || world.diagonal[2] != 1.0
            || world.diagonal[3] != 1.0
            || local.iter().any(|v| !v.is_finite())
            || local[2] <= local[0]
            || local[3] <= local[1]
            || !affine
                && (world.diagonal[0] <= 0.0 || world.diagonal[1] <= 0.0 || world.cross != [0.0; 2])
        {
            return Err(Error::InvalidInput);
        }
        let scale = if affine {
            [
                s(
                    p(world.diagonal[0], world.diagonal[0]),
                    p(world.cross[0], world.cross[0]),
                )
                .sqrt(),
                s(
                    p(world.cross[1], world.cross[1]),
                    p(world.diagonal[1], world.diagonal[1]),
                )
                .sqrt(),
            ]
        } else {
            [
                p(world.diagonal[0], world.diagonal[0]).sqrt(),
                p(world.diagonal[1], world.diagonal[1]).sqrt(),
            ]
        };
        if scale[0] <= 0.0 || scale[1] <= 0.0 {
            return Err(Error::InvalidInput);
        }
        let source_scale = SourceMatrix {
            diagonal: [scale[0], scale[1], 1.0, 1.0],
            ..Default::default()
        };
        let inverse_scale = source_scale.inverse(false)?;
        let residual = inverse_scale.multiply(world, affine);
        if !residual.finite(affine)
            || residual.diagonal[3] <= 0.0
            || !affine && (residual.diagonal[0] <= 0.0 || residual.diagonal[1] <= 0.0)
        {
            return Err(Error::InvalidInput);
        }
        let capture = SampleLattice::bounded([
            f64::from(p(local[0], scale[0]).floor()),
            f64::from(p(local[1], scale[1]).floor()),
            f64::from(p(local[2], scale[0]).ceil()),
            f64::from(p(local[3], scale[1]).ceil()),
        ])?;
        let origin = SourceMatrix {
            translation: capture.origin.map(|v| v as f32),
            ..Default::default()
        };
        let capture_to_device = origin.multiply(residual, affine);
        let extent = SourceMatrix {
            diagonal: [capture.size[0] as f32, capture.size[1] as f32, 1.0, 1.0],
            ..Default::default()
        };
        let unit_to_device = extent.multiply(capture_to_device, affine);
        let device_to_unit = unit_to_device.inverse(affine)?;
        let edges = if affine {
            let points = [[0.0, 0.0], [1.0, 0.0], [0.0, 1.0], [1.0, 1.0]]
                .map(|[x, y]| unit_to_device.point(x, y));
            if points.iter().flatten().any(|v| !v.is_finite()) {
                return Err(Error::InvalidInput);
            }
            let mut e = [points[0][0], points[0][1], points[0][0], points[0][1]];
            for point in points {
                // std::min/max retain the first operand on equality, including signed zero.
                if point[0] < e[0] {
                    e[0] = point[0];
                }
                if point[1] < e[1] {
                    e[1] = point[1];
                }
                if e[2] < point[0] {
                    e[2] = point[0];
                }
                if e[3] < point[1] {
                    e[3] = point[1];
                }
            }
            e.map(|v| q(v, unit_to_device.diagonal[3]))
        } else {
            let [x, y, _, w] = unit_to_device.diagonal;
            let [tx, ty] = unit_to_device.translation;
            [q(tx, w), q(ty, w), q(s(x, tx), w), q(s(y, ty), w)]
        };
        let output = SampleLattice::bounded([
            f64::from(edges[0].floor()),
            f64::from(edges[1].floor()),
            f64::from(edges[2].ceil()),
            f64::from(edges[3].ceil()),
        ])?;
        Ok(Self {
            source_scale,
            inverse_scale,
            residual,
            capture,
            capture_to_device,
            unit_to_device,
            device_to_unit,
            output,
            affine,
        })
    }
    /// Compose original target-dependent projection after the full retained quad.
    pub fn project(&self, target: SampleLattice) -> Result<(SourceMatrix, [f32; 2]), Error> {
        if target.size.contains(&0) {
            return Err(Error::InvalidInput);
        }
        let reciprocal = target.size.map(|v| q(1.0, v as f32));
        let viewport = SourceMatrix {
            diagonal: [p(2.0, reciprocal[0]), p(-2.0, reciprocal[1]), 1.0, 1.0],
            translation: [-s(1.0, reciprocal[0]), s(1.0, reciprocal[1])],
            cross: [0.0; 2],
        };
        let origin = SourceMatrix {
            translation: target.origin.map(|v| -(v as f32)),
            ..Default::default()
        };
        let quad = self
            .unit_to_device
            .multiply(origin, self.affine)
            .multiply(viewport, self.affine);
        if !quad.finite(self.affine) {
            return Err(Error::InvalidInput);
        }
        Ok((quad, reciprocal))
    }
}
/// Original version-four capture frame wire, retaining exact DPI doubles.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct CaptureFrame {
    /// Original padded local left/top/right/bottom edges.
    pub local: [f32; 4],
    /// Original positive source X/Y scales.
    pub scale: [f32; 2],
    /// Original source X/Y translation.
    pub offset: [f32; 2],
    /// Original independent source DPI.
    pub dpi: [f64; 2],
    /// Original scale-space integer capture origin.
    pub capture_origin: [i32; 2],
    /// Original complete capture extent.
    pub capture_size: [u32; 2],
    /// Original exact integer final origin.
    pub final_origin: [i32; 2],
}
impl CaptureFrame {
    /// Complete original bounded power-of-two diagonal admission atomically.
    pub fn complete(mut self) -> Result<Self, Error> {
        if self
            .local
            .iter()
            .chain(self.scale.iter())
            .chain(self.offset.iter())
            .any(|v| !v.is_finite())
            || self.local[2] <= self.local[0]
            || self.local[3] <= self.local[1]
            || self
                .scale
                .iter()
                .any(|v| *v < 1.0 / 256.0 || *v > 256.0 || v.to_bits() & 0x7fffff != 0)
            || self
                .dpi
                .iter()
                .any(|v| !v.is_finite() || *v <= 0.0 || *v != f64::from(*v as f32))
        {
            return Err(Error::InvalidInput);
        }
        let scale = self.scale.map(|v| p(v, v).sqrt());
        if scale != self.scale || scale.iter().any(|v| p(1.0 / *v, *v) != 1.0) {
            return Err(Error::InvalidInput);
        }
        let capture = SampleLattice::bounded([
            f64::from(p(self.local[0], scale[0]).floor()),
            f64::from(p(self.local[1], scale[1]).floor()),
            f64::from(p(self.local[2], scale[0]).ceil()),
            f64::from(p(self.local[3], scale[1]).ceil()),
        ])?;
        let final_position =
            std::array::from_fn::<_, 2, _>(|i| s(capture.origin[i] as f32, self.offset[i]));
        if (0..2).any(|i| {
            !final_position[i].is_finite()
                || f64::from(final_position[i])
                    != f64::from(capture.origin[i]) + f64::from(self.offset[i])
                || final_position[i] != final_position[i].floor()
                || final_position[i].abs() > 16777216.0
        }) {
            return Err(Error::InvalidInput);
        }
        self.capture_origin = capture.origin;
        self.capture_size = capture.size;
        self.final_origin = final_position.map(|v| v as i32);
        Ok(self)
    }
    /// Reject mismatched redundant derived fields before publication.
    pub fn validate(self) -> Result<(), Error> {
        let expected = self.complete()?;
        if self.capture_origin != expected.capture_origin
            || self.capture_size != expected.capture_size
            || self.final_origin != expected.final_origin
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    /// Original independent 28.4 aliased output edge conversion.
    pub fn output_coverage(self) -> Result<[i32; 4], Error> {
        self.validate()?;
        Ok(std::array::from_fn(|i| {
            let edge = s(p(self.local[i], self.scale[i % 2]), self.offset[i % 2]);
            let fixed = (f64::from(p(edge, 16.0)) + 0.5).floor();
            ((fixed + 7.0) / 16.0).floor() as i32
        }))
    }
}
/// Original version-five wire; capture, output, clip and unit quad stay independent.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct SampleFrameRecord {
    /// Padded original source local edges.
    pub local: [f32; 4],
    /// Original source diagonal.
    pub scale: [f32; 2],
    /// Original source translation.
    pub offset: [f32; 2],
    /// Original DPI doubles.
    pub dpi: [f64; 2],
    /// Derived scale-space origin.
    pub capture_origin: [i32; 2],
    /// Derived complete scale-space extent.
    pub capture_size: [u32; 2],
    /// Derived final-device origin.
    pub output_origin: [i32; 2],
    /// Derived final-device extent.
    pub output_size: [u32; 2],
    /// Original homogeneous unit quad X/Y/Z/W.
    pub quad: [f32; 4],
    /// Original unit-quad translation.
    pub quad_offset: [f32; 2],
    /// Independent integral physical left/top/right/bottom clip edges.
    pub clip: [f32; 4],
    /// Original aliased/AA source bounds policy, 0 or 1.
    pub clip_antialias: u32,
    /// Original reserved zero.
    pub reserved: u32,
}
impl SampleFrameRecord {
    fn world(self, cross: [f32; 2]) -> SourceMatrix {
        SourceMatrix {
            diagonal: [self.scale[0], self.scale[1], 1.0, 1.0],
            translation: self.offset,
            cross,
        }
    }
    fn metadata(self) -> Result<(), Error> {
        if self.dpi.iter().any(|v| !v.is_finite() || *v <= 0.0)
            || self.reserved != 0
            || self.clip_antialias > 1
            || self.clip.iter().any(|v| !v.is_finite() || *v != v.floor())
            || self.clip[2] < self.clip[0]
            || self.clip[3] < self.clip[1]
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
    fn completed(mut self, derived: SampleFrame) -> Self {
        self.capture_origin = derived.capture.origin;
        self.capture_size = derived.capture.size;
        self.output_origin = derived.output.origin;
        self.output_size = derived.output.size;
        self.quad = derived.unit_to_device.diagonal;
        self.quad_offset = derived.unit_to_device.translation;
        self
    }
    fn matches(self, other: Self) -> bool {
        self.capture_origin == other.capture_origin
            && self.capture_size == other.capture_size
            && self.output_origin == other.output_origin
            && self.output_size == other.output_size
            && self.quad.map(f32::to_bits) == other.quad.map(f32::to_bits)
            && self.quad_offset.map(f32::to_bits) == other.quad_offset.map(f32::to_bits)
    }
    /// Complete the diagonal frame without changing original source metadata.
    pub fn complete(self) -> Result<Self, Error> {
        Ok(self.completed(SampleFrame::new(self.local, self.world([0.0; 2]), false)?))
    }
    /// Validate exact derived bits and return the complete original arithmetic frame.
    pub fn resolve(self) -> Result<SampleFrame, Error> {
        self.metadata()?;
        let frame = SampleFrame::new(self.local, self.world([0.0; 2]), false)?;
        if !self.matches(self.completed(frame)) {
            return Err(Error::InvalidInput);
        }
        Ok(frame)
    }
}
/// Original distinct version-six wire. Never feed this through the diagonal reader.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct AffineFrameRecord {
    /// Version-five-shaped placement prefix with version-six arithmetic.
    pub placement: SampleFrameRecord,
    /// Original M12/M21 components from actual source history.
    pub source_cross: [f32; 2],
    /// Derived full unit-quad M12/M21 components.
    pub quad_cross: [f32; 2],
}
impl AffineFrameRecord {
    /// Complete affine derived fields without reconstructing source history.
    pub fn complete(mut self) -> Result<Self, Error> {
        let frame = SampleFrame::new(
            self.placement.local,
            self.placement.world(self.source_cross),
            true,
        )?;
        self.placement = self.placement.completed(frame);
        self.quad_cross = frame.unit_to_device.cross;
        Ok(self)
    }
    /// Validate exact diagonal, cross and homogeneous bits and resolve the source frame.
    pub fn resolve(self) -> Result<SampleFrame, Error> {
        self.placement.metadata()?;
        let frame = SampleFrame::new(
            self.placement.local,
            self.placement.world(self.source_cross),
            true,
        )?;
        if !self.placement.matches(self.placement.completed(frame))
            || self.quad_cross.map(f32::to_bits) != frame.unit_to_device.cross.map(f32::to_bits)
        {
            return Err(Error::InvalidInput);
        }
        Ok(frame)
    }
}
const _: () = {
    assert!(size_of::<CaptureFrame>() == 72);
    assert!(size_of::<SampleFrameRecord>() == 128);
    assert!(size_of::<AffineFrameRecord>() == 144);
};
