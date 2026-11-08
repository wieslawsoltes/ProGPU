//! Authored CFF source fixture passing through the production decoder and GPU paths.
use progpu::{
    font::{SfntFace, cff::OutlineTransform},
    path::{FillRule, Path},
};
pub fn cff_rectangle() -> Result<Path, Box<dyn std::error::Error>> {
    let cff = [
        1, 0, 4, 4, 0, 0, 0, 1, 1, 1, 3, 156, 17, 0, 0, 0, 0, 0, 1, 1, 1, 12, 139, 139, 21, 239,
        139, 139, 239, 39, 139, 5, 14,
    ];
    let mut data = vec![0u8; 28 + cff.len()];
    data[..4].copy_from_slice(b"OTTO");
    data[4..6].copy_from_slice(&1u16.to_be_bytes());
    data[12..16].copy_from_slice(b"CFF ");
    data[20..24].copy_from_slice(&28u32.to_be_bytes());
    data[24..28].copy_from_slice(&(cff.len() as u32).to_be_bytes());
    data[28..].copy_from_slice(&cff);
    let face = SfntFace::new(data, 0)?;
    let font = face.cff1(1)?;
    let outline =
        font.outline_transformed(0, OutlineTransform([0.165, 0.0, 0.0, 0.16, 4.25, 4.5]))?;
    drop(font);
    drop(face);
    Ok(Path::new(
        outline.segments().to_vec(),
        [4.25, 4.5, 20.75, 20.5],
        FillRule::NonZero,
    )?)
}
