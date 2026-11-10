//! Transport for original captured name-query and axis-name outputs.
use progpu::font::{FontError, ManagedVariationData, SfntFace};

#[derive(Clone)]
pub struct Case {
    pub font: Vec<u8>,
    pub ids: Vec<u16>,
    pub axes: bool,
}
pub fn emit(out: &mut Vec<u8>, value: u32) {
    out.extend(value.to_le_bytes());
}
pub fn text(out: &mut Vec<u8>, value: impl IntoIterator<Item = u16>) {
    let v: Vec<_> = value.into_iter().collect();
    emit(out, v.len() as u32);
    for c in v {
        emit(out, c.into());
    }
}
impl Case {
    pub fn expected(&self) -> Result<Vec<u8>, FontError> {
        let face = SfntFace::new(self.font.clone(), 0)?;
        let mut out = Vec::new();
        for &id in &self.ids {
            let names = face.managed_names(id);
            emit(&mut out, names.len() as u32);
            for name in &names {
                text(&mut out, name.encode_utf16());
            }
            let first = face.managed_name(id);
            emit(&mut out, u32::from(first.is_some()));
            text(&mut out, first.as_deref().unwrap_or("").encode_utf16());
        }
        if self.axes {
            let data = ManagedVariationData::from_named_face(face)?;
            let axes = data.as_ref().map_or(&[][..], |d| d.axes());
            emit(&mut out, axes.len() as u32);
            for a in axes {
                text(&mut out, a.name.iter().copied());
            }
        }
        Ok(out)
    }
    #[allow(dead_code)] // Native oracle transport; browser calls expected directly.
    pub fn write(&self, out: &mut Vec<u8>) {
        emit(out, self.font.len() as u32);
        out.extend(&self.font);
        emit(out, u32::from(self.axes));
        emit(out, self.ids.len() as u32);
        for &id in &self.ids {
            emit(out, id.into());
        }
    }
}
