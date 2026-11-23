use super::*;
#[derive(Clone, Debug)]
pub(super) struct Axis {
    minimum: i32,
    default: i32,
    maximum: i32,
    map: Vec<(i16, i16)>,
}
impl Axis {
    pub(super) fn normalize(&self, mut user: i32) -> Result<i16, FontError> {
        if self.minimum > self.maximum {
            return Err(FontError::InvalidArgument);
        }
        user = user.clamp(self.minimum, self.maximum);
        let n = if user < self.default {
            let range = self.default.wrapping_sub(self.minimum);
            if range == 0 {
                0.0
            } else {
                -f64::from(self.default.wrapping_sub(user)) / f64::from(range)
            }
        } else if user > self.default {
            let range = self.maximum.wrapping_sub(self.default);
            if range == 0 {
                0.0
            } else {
                f64::from(user.wrapping_sub(self.default)) / f64::from(range)
            }
        } else {
            0.0
        };
        let coordinate = ((n * 16384.0).round() as i32).clamp(-16384, 16384) as i16;
        if self.map.len() < 2 {
            return Ok(coordinate);
        }
        for (i, &(from, to)) in self.map.iter().enumerate() {
            if coordinate > from {
                continue;
            }
            if coordinate == from || i == 0 {
                return Ok(to);
            }
            let (previous, value) = self.map[i - 1];
            let ratio = (i32::from(coordinate) - i32::from(previous)) as f32
                / (i32::from(from) - i32::from(previous)) as f32;
            return Ok(
                ((f32::from(value) + ratio * (i32::from(to) - i32::from(value)) as f32)
                    .round_ties_even() as i32)
                    .clamp(-16384, 16384) as i16,
            );
        }
        Ok(self.map.last().unwrap().1)
    }
}
pub(super) fn extent(data: &[u8], at: i32, len: i32, end: i32) -> Result<(), FontError> {
    if at < 0 || len < 0 || end < 0 || end as usize > data.len() || at > end - len {
        Err(FontError::InvalidFace)
    } else {
        Ok(())
    }
}
pub(super) fn word(data: &[u8], at: i32) -> Result<u16, FontError> {
    extent(data, at, 2, data.len() as i32)?;
    Ok(u16::from_be_bytes(
        data[at as usize..at as usize + 2].try_into().unwrap(),
    ))
}
pub(super) fn long(data: &[u8], at: i32) -> Result<i32, FontError> {
    extent(data, at, 4, data.len() as i32)?;
    Ok(i32::from_be_bytes(
        data[at as usize..at as usize + 4].try_into().unwrap(),
    ))
}
pub(super) fn positive_long(data: &[u8], at: i32) -> Result<i32, FontError> {
    let n = long(data, at)?;
    if n < 0 {
        Err(FontError::InvalidFace)
    } else {
        Ok(n)
    }
}
fn tag(data: &[u8], at: i32) -> Result<[u8; 4], FontError> {
    extent(data, at, 4, data.len() as i32)?;
    Ok(data[at as usize..at as usize + 4]
        .try_into()
        .map(|v: [u8; 4]| v.map(|b| if b < 128 { b } else { b'?' }))
        .unwrap())
}
pub(super) fn table(source: &SfntFace, tag: [u8; 4]) -> Option<(i32, i32)> {
    source
        .table_span(Tag::new(tag))
        .map(|(_, s)| (s.offset as i32, (s.offset + s.length) as i32))
}
fn capacity<T>(v: &mut Vec<T>, n: usize) -> Result<(), FontError> {
    v.try_reserve_exact(n).map_err(|_| FontError::Capacity)
}
#[derive(Clone, Debug)]
pub(super) struct IndexMap {
    entries: Vec<u32>,
    bits: u32,
}
impl IndexMap {
    fn parse(data: &[u8], start: i32) -> Result<Option<Self>, FontError> {
        extent(data, start, 4, data.len() as i32)?;
        let format = data[start as usize];
        let packed = data[start as usize + 1];
        let (count, mut at) = match format {
            0 => (i32::from(word(data, start + 2)?), start + 4),
            1 => {
                extent(data, start, 6, data.len() as i32)?;
                (positive_long(data, start + 2)?, start + 6)
            }
            _ => return Ok(None),
        };
        let bytes = i32::from(((packed & 0x30) >> 4) + 1);
        extent(
            data,
            at,
            count.checked_mul(bytes).ok_or(FontError::InvalidFace)?,
            data.len() as i32,
        )?;
        let mut entries = Vec::new();
        capacity(&mut entries, count as usize)?;
        for _ in 0..count {
            let mut value = 0u32;
            for _ in 0..bytes {
                value = (value << 8) | u32::from(data[at as usize]);
                at += 1;
            }
            entries.push(value);
        }
        Ok(Some(Self {
            entries,
            bits: u32::from((packed & 15) + 1),
        }))
    }
    pub(super) fn get(&self, index: u16) -> (u16, u16) {
        if self.entries.is_empty() {
            return (0, index);
        }
        let entry = self.entries[usize::from(index).min(self.entries.len() - 1)];
        (
            (entry >> self.bits) as u16,
            (entry & ((1u32 << self.bits) - 1)) as u16,
        )
    }
}
pub(super) fn parse(
    source: SfntFace,
    mut name: impl FnMut(u16) -> Option<Arc<[u16]>>,
) -> Result<Data, FontError> {
    let bytes = source.data();
    let (start, end) = table(&source, *b"fvar").ok_or(FontError::InvalidFace)?;
    extent(bytes, start, 16, end)?;
    let axes_offset = i32::from(word(bytes, start + 4)?);
    let count = usize::from(word(bytes, start + 8)?);
    let size = i32::from(word(bytes, start + 10)?);
    let mut axes = Vec::new();
    let mut public_axes = Vec::new();
    if size >= 20 {
        capacity(&mut axes, count)?;
        capacity(&mut public_axes, count)?;
        for i in 0..count {
            let at = start
                .checked_add(axes_offset)
                .and_then(|n| n.checked_add((i as i32).checked_mul(size)?))
                .ok_or(FontError::InvalidFace)?;
            extent(bytes, at, 20, end)?;
            let tag = tag(bytes, at)?;
            let minimum = long(bytes, at + 4)?;
            let default = long(bytes, at + 8)?;
            let maximum = long(bytes, at + 12)?;
            public_axes.push(ManagedVariationAxis {
                tag,
                name: name(word(bytes, at + 18)?)
                    .unwrap_or_else(|| tag.map(u16::from).to_vec().into()),
                minimum: minimum as f32 / 65536.0,
                default: default as f32 / 65536.0,
                maximum: maximum as f32 / 65536.0,
                hidden: word(bytes, at + 16)? & 1 != 0,
            });
            axes.push(Axis {
                minimum,
                default,
                maximum,
                map: Vec::new(),
            });
        }
    }
    if let Some((start, end)) = table(&source, *b"avar") {
        extent(bytes, start, 8, end)?;
        if usize::from(word(bytes, start + 6)?) == axes.len() {
            let mut at = start + 8;
            for axis in &mut axes {
                extent(bytes, at, 2, end)?;
                let n = word(bytes, at)?;
                at += 2;
                capacity(&mut axis.map, usize::from(n))?;
                for _ in 0..n {
                    extent(bytes, at, 4, end)?;
                    axis.map
                        .push((word(bytes, at)? as i16, word(bytes, at + 2)? as i16));
                    at += 4;
                }
            }
        }
    }
    let glyphs = GlyphMetadata::parse(&source, axes.len())?;
    let mut advance = None;
    let mut advance_map = None;
    if let Some((start, _)) = table(&source, *b"HVAR") {
        let relative = long(bytes, start.wrapping_add(4))?;
        if relative > 0 {
            advance = ManagedVariationStore::parse(
                source.clone(),
                start.wrapping_add(relative),
                axes.len(),
            )?
            .map(Arc::new);
        }
        let relative = long(bytes, start.wrapping_add(8))?;
        if relative > 0 {
            advance_map = IndexMap::parse(bytes, start.wrapping_add(relative))?;
        }
    }
    let mut layout = None;
    if let Some((start, end)) = table(&source, *b"GDEF")
        && end - start >= 18
        && word(bytes, start)? == 1
        && word(bytes, start + 2)? >= 3
    {
        let relative = long(bytes, start + 14)?;
        if relative > 0 {
            layout = ManagedVariationStore::parse(
                source.clone(),
                start.wrapping_add(relative),
                axes.len(),
            )?
            .map(Arc::new);
        }
    }
    let mut metrics = None;
    let mut metric_records = HashMap::new();
    if let Some((start, end)) = table(&source, *b"MVAR") {
        extent(bytes, start, 12, end)?;
        let size = i32::from(word(bytes, start + 6)?);
        let count = word(bytes, start + 8)?;
        let relative = i32::from(word(bytes, start + 10)?);
        if size >= 8 && relative != 0 {
            metrics = ManagedVariationStore::parse(source.clone(), start + relative, axes.len())?
                .map(Arc::new);
            if metrics.is_some() {
                metric_records
                    .try_reserve(usize::from(count))
                    .map_err(|_| FontError::Capacity)?;
                let mut at = start + 12;
                for _ in 0..count {
                    extent(bytes, at, 8, end)?;
                    metric_records.insert(
                        tag(bytes, at)?,
                        (word(bytes, at + 4)?, word(bytes, at + 6)?),
                    );
                    at = at.wrapping_add(size);
                }
            }
        }
    }
    let cache = allocate(
        glyphs.as_ref().map_or(0, |g| g.offsets.len() - 1),
        GlyphCache::Unparsed,
    )?;
    Ok(Data {
        source,
        axes,
        public_axes,
        advance,
        layout,
        metrics,
        advance_map,
        metric_records,
        glyphs,
        glyph_cache: Mutex::new(cache),
    })
}
