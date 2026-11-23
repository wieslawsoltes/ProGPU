//! Original retained ICC header/tag/curve parser. No external profile engine.
use super::{
    ColorSpaceTransferFunction as Transfer, ColorSpaceTransferFunctionKind as Kind, ColorSpaceXyz,
    transfer_close,
};
use std::sync::Arc;
/// Retained complete original ICC bytes and independent supported RGB metadata.
/// A structurally admitted non-RGB/unsupported profile remains a profile; it is
/// not silently replaced by sRGB and need not be convertible to a ColorSpace.
#[derive(Clone, Debug)]
pub struct ColorSpaceIccProfile(Arc<Profile>);
#[derive(Debug, Default)]
struct Profile {
    data: Arc<[u8]>,
    transfer: Option<Transfer>,
    xyz: Option<ColorSpaceXyz>,
}
impl Default for ColorSpaceIccProfile {
    fn default() -> Self {
        Self(Arc::new(Profile::default()))
    }
}
impl ColorSpaceIccProfile {
    /// Original empty profile owner, with no byte buffer or RGB metadata.
    pub fn empty() -> Self {
        Self::default()
    }
    /// Parse the original supported ICC inventory and copy the entire supplied
    /// byte generation, including bytes beyond its declared parse extent.
    /// Unselected/unknown tags retain the original opaque policy.
    pub fn parse(data: &[u8]) -> Option<Self> {
        if data.len() > i32::MAX as usize || data.len() < 132 || u32be(data, 36)? != 0x61637370 {
            return None;
        }
        let declared = u32be(data, 0)? as usize;
        if declared < 132 || declared > data.len() {
            return None;
        }
        let profile = &data[..declared];
        let count = u32be(profile, 128)? as usize;
        if count > (profile.len() - 132) / 12 {
            return None;
        }
        let (transfer, xyz) = if u32be(profile, 16)? == 0x52474220 {
            // The two original flags are independent. A missing/unsupported
            // transfer cannot erase a valid selected matrix or vice versa.
            let xyz = rgb_matrix(profile, count);
            let transfer = rgb_transfer(profile, count);
            (transfer, xyz)
        } else {
            (None, None)
        };
        Some(Self(Arc::new(Profile {
            data: Arc::from(data),
            transfer,
            xyz,
        })))
    }
    pub(super) fn from_color_space(transfer: Transfer, xyz: ColorSpaceXyz) -> Self {
        Self(Arc::new(Profile {
            data: Arc::from([]),
            transfer: Some(transfer),
            xyz: Some(xyz),
        }))
    }
    /// Original complete owned input bytes. Metadata-only generated profiles
    /// have no serialized ICC bytes, matching the source ToProfile contract.
    pub fn data(&self) -> &[u8] {
        &self.0.data
    }
    /// Actual retained byte count, independent of the declared parse prefix.
    pub fn size(&self) -> usize {
        self.0.data.len()
    }
    /// Independently supported RGB matrix, even if transfer selection failed.
    pub fn xyz(&self) -> Option<ColorSpaceXyz> {
        self.0.xyz
    }
    /// Original value-returning matrix query with the empty failure sentinel.
    pub fn xyz_or_empty(&self) -> ColorSpaceXyz {
        self.xyz().unwrap_or(ColorSpaceXyz::EMPTY)
    }
    /// Independently supported common RGB transfer, before CreateRgb canonicalization.
    pub fn transfer(&self) -> Option<Transfer> {
        self.0.transfer
    }
    /// Complete supported RGB conversion metadata; absence stays unsupported.
    pub fn color_space_parameters(&self) -> Option<(Transfer, ColorSpaceXyz)> {
        Some((self.0.transfer?, self.0.xyz?))
    }
    /// Exact immutable source-generation ownership, separate from byte equality.
    pub fn same_owner(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
}
fn u32be(data: &[u8], offset: usize) -> Option<u32> {
    Some(u32::from_be_bytes(
        data.get(offset..offset.checked_add(4)?)?.try_into().ok()?,
    ))
}
fn u16be(data: &[u8], offset: usize) -> Option<u16> {
    Some(u16::from_be_bytes(
        data.get(offset..offset.checked_add(2)?)?.try_into().ok()?,
    ))
}
fn fixed(data: &[u8], offset: usize) -> Option<f32> {
    Some((u32be(data, offset)? as i32) as f32 / 65536.0)
}
fn tag(profile: &[u8], count: usize, signature: u32) -> Option<&[u8]> {
    for index in 0..count {
        let entry = 132 + index * 12;
        if u32be(profile, entry)? != signature {
            continue;
        }
        let offset = u32be(profile, entry + 4)? as usize;
        let size = u32be(profile, entry + 8)? as usize;
        // First matching source entry wins, including an invalid duplicate.
        // Do not retry into later tags or impose policy on unrelated entries.
        if offset > profile.len() || size > profile.len() - offset {
            return None;
        }
        return Some(&profile[offset..offset + size]);
    }
    None
}
fn xyz_tag(profile: &[u8], count: usize, signature: u32) -> Option<[f32; 3]> {
    let tag = tag(profile, count, signature)?;
    if tag.len() < 20 || u32be(tag, 0)? != 0x58595a20 {
        return None;
    }
    Some([fixed(tag, 8)?, fixed(tag, 12)?, fixed(tag, 16)?])
}
fn rgb_matrix(profile: &[u8], count: usize) -> Option<ColorSpaceXyz> {
    let red = xyz_tag(profile, count, 0x7258595a)?;
    let green = xyz_tag(profile, count, 0x6758595a)?;
    let blue = xyz_tag(profile, count, 0x6258595a)?;
    Some(ColorSpaceXyz([
        red[0], green[0], blue[0], red[1], green[1], blue[1], red[2], green[2], blue[2],
    ]))
}
fn rgb_transfer(profile: &[u8], count: usize) -> Option<Transfer> {
    let red = tag(profile, count, 0x72545243)?;
    let green = tag(profile, count, 0x67545243)?;
    let blue = tag(profile, count, 0x62545243)?;
    let red = curve(red)?;
    let green = curve(green)?;
    let blue = curve(blue)?;
    (transfer_close(red, green) && transfer_close(red, blue)).then_some(red)
}
fn curve(tag: &[u8]) -> Option<Transfer> {
    if tag.len() < 12 {
        return None;
    }
    match u32be(tag, 0)? {
        0x63757276 => sampled_curve(tag),
        0x70617261 => parametric_curve(tag),
        _ => None,
    }
}
fn sampled_curve(tag: &[u8]) -> Option<Transfer> {
    let count = u32be(tag, 8)? as usize;
    if count == 0 {
        return Some(Transfer::LINEAR);
    }
    if count == 1 && tag.len() >= 14 {
        return Some(Transfer([
            f32::from(u16be(tag, 12)?) / 256.0,
            1.0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
        ]));
    }
    if count > (tag.len() - 12) / 2 || count < 2 {
        return None;
    }
    // Original sampled-curve admission recognizes only sRGB, with MathF.Pow
    // (not the separately selected fast transfer evaluator) and a strict bound.
    let [g, a, b, c, d, e, f] = Transfer::SRGB.0;
    let mut maximum = 0.0_f32;
    for index in 0..count {
        let encoded = index as f32 / (count - 1) as f32;
        let expected = if encoded < d {
            c * encoded + f
        } else {
            (a * encoded + b).powf(g) + e
        };
        let actual = f32::from(u16be(tag, 12 + index * 2)?) / 65535.0;
        maximum = maximum.max((actual - expected).abs());
    }
    (maximum < 1.0 / 512.0).then_some(Transfer::SRGB)
}
fn parametric_curve(tag: &[u8]) -> Option<Transfer> {
    let function = u16be(tag, 8)?;
    let count = match function {
        0 => 1,
        1 => 3,
        2 => 4,
        3 => 5,
        4 => 7,
        _ => return None,
    };
    if tag.len() < 12 + count * 4 {
        return None;
    }
    let mut p = [0.0; 7];
    for (index, value) in p[..count].iter_mut().enumerate() {
        *value = fixed(tag, 12 + index * 4)?;
    }
    let g = p[0];
    let result = if function == 0 {
        Transfer([g, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0])
    } else {
        let (a, b) = (p[1], p[2]);
        if a == 0.0 {
            return None;
        }
        match function {
            1 => Transfer([g, a, b, 0.0, -b / a, 0.0, 0.0]),
            2 => Transfer([g, a, b, 0.0, -b / a, 0.0, p[3]]),
            3 => Transfer([g, a, b, p[3], p[4], 0.0, 0.0]),
            4 => Transfer([g, a, b, p[3], p[4], p[5], p[6]]),
            _ => return None,
        }
    };
    (result.kind() != Kind::Invalid).then_some(result)
}
