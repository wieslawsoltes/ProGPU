//! Original Cff1OutlineSource: managed DICT admission and lazy CID subroutines.
//! Encoded immutable INDEX/FDSelect storage replaces source offset arrays while
//! preserving their original full-inventory admission and item selection.
use super::{FdSelect, FontError, Index, IndexLayout, SelectLayout, SfntFace, Span, Tag};
use std::sync::OnceLock;

#[derive(Debug)]
pub(crate) struct ManagedCff {
    source: SfntFace,
    table: Span,
    chars: IndexLayout,
    global: IndexLayout,
    local: IndexLayout,
    dictionaries: IndexLayout,
    select: Option<SelectLayout>,
    local_cache: Vec<OnceLock<IndexLayout>>,
}
impl ManagedCff {
    pub(crate) fn new(source: SfntFace, expected: u16) -> Result<Self, FontError> {
        Self::parse(source, expected).map_err(|e| {
            if e == FontError::Capacity {
                e
            } else {
                FontError::UnsupportedOutline
            }
        })
    }
    fn parse(source: SfntFace, expected: u16) -> Result<Self, FontError> {
        let (_, table) = source
            .table_span(Tag::new(*b"CFF "))
            .ok_or(FontError::InvalidFace)?;
        let data = table.get(source.data());
        if data.len() < 4 || data[0] != 1 {
            return Err(FontError::InvalidFace);
        }
        let mut at = usize::from(data[2]);
        if at < 4 || at > data.len() {
            return Err(FontError::InvalidFace);
        }
        Index::read_managed(data, &mut at)?;
        let tops = Index::read_managed(data, &mut at)?;
        if tops.len() != 1 {
            return Err(FontError::InvalidFace);
        }
        Index::read_managed(data, &mut at)?;
        let global = Index::read_managed(data, &mut at)?.layout;
        let top = top(tops.get(0)?).ok_or(FontError::InvalidFace)?;
        let chars = read_at(data, top.chars)?;
        if chars.is_empty() || expected != 0 && chars.len() != u32::from(expected) {
            return Err(FontError::InvalidFace);
        }
        let local = if top.size > 0 {
            local(data, top.offset, top.size)?
        } else {
            Index::default()
        }
        .layout;
        let mut dictionaries = Index::default();
        let mut select = None;
        if top.dictionaries != 0 || top.select != 0 {
            if top.dictionaries == 0 || top.select == 0 {
                return Err(FontError::InvalidFace);
            }
            dictionaries = read_at(data, top.dictionaries)?;
            if dictionaries.is_empty() {
                return Err(FontError::InvalidFace);
            }
            let selected =
                FdSelect::read_managed(data, top.select, chars.len(), dictionaries.len())?;
            select = Some(selected.layout);
        }
        let mut cache = Vec::new();
        cache
            .try_reserve_exact(dictionaries.len() as usize)
            .map_err(|_| FontError::Capacity)?;
        cache.resize_with(dictionaries.len() as usize, OnceLock::new);
        let chars = chars.layout;
        let dictionaries = dictionaries.layout;
        Ok(Self {
            source,
            table,
            chars,
            global,
            local,
            dictionaries,
            select,
            local_cache: cache,
        })
    }
    pub(crate) fn outline(
        &self,
        glyph: u16,
    ) -> Result<Option<Vec<crate::font::ManagedOutlineFigure>>, FontError> {
        let data = self.table.get(self.source.data());
        let chars = self.chars.view(data);
        let local = if let Some(select) = self.select {
            let index = select
                .view(data)
                .dictionary(u32::from(glyph))
                .map_err(|_| FontError::UnsupportedOutline)? as usize;
            let layout = self
                .local_cache
                .get(index)
                .ok_or(FontError::UnsupportedOutline)?
                .get_or_init(|| {
                    // Original CID private failures cache the empty index; they do
                    // not reuse default subroutines or abort the complete face.
                    self.dictionaries
                        .view(data)
                        .get(index as u32)
                        .ok()
                        .and_then(private)
                        .filter(|(size, _)| *size > 0)
                        .and_then(|(size, offset)| local(data, offset, size).ok())
                        .map_or(IndexLayout::default(), |i| i.layout)
                });
            layout.view(data)
        } else {
            self.local.view(data)
        };
        let program = chars
            .get(u32::from(glyph))
            .map_err(|_| FontError::UnsupportedOutline)?;
        super::type2::managed_outline(program, self.global.view(data), local, glyph)
    }
}
fn read_at(data: &[u8], at: i32) -> Result<Index<'_>, FontError> {
    let mut at = usize::try_from(at).map_err(|_| FontError::InvalidFace)?;
    Index::read_managed(data, &mut at)
}
fn local(data: &[u8], offset: i32, size: i32) -> Result<Index<'_>, FontError> {
    if offset < 0 || size < 0 {
        return Err(FontError::InvalidFace);
    }
    let bytes = data
        .get(offset as usize..offset as usize + size as usize)
        .ok_or(FontError::InvalidFace)?;
    let Some(relative) = subroutine(bytes) else {
        return Ok(Index::default());
    };
    if relative < 0 || relative as usize > data.len().saturating_sub(offset as usize) {
        return Err(FontError::InvalidFace);
    }
    read_at(
        data,
        offset.checked_add(relative).ok_or(FontError::InvalidFace)?,
    )
}
#[derive(Default)]
struct Top {
    chars: i32,
    size: i32,
    offset: i32,
    dictionaries: i32,
    select: i32,
}
fn top(data: &[u8]) -> Option<Top> {
    let mut top = Top::default();
    dict(data, |op, values| {
        match (op, values.len()) {
            (17, n) if n >= 1 => top.chars = offset(values[n - 1]),
            (18, n) if n >= 2 => {
                top.size = offset(values[n - 2]);
                top.offset = offset(values[n - 1]);
            }
            (0xc24, n) if n >= 1 => top.dictionaries = offset(values[n - 1]),
            (0xc25, n) if n >= 1 => top.select = offset(values[n - 1]),
            _ => {}
        }
        false
    })?;
    (top.chars > 0).then_some(top)
}
fn private(data: &[u8]) -> Option<(i32, i32)> {
    let (mut size, mut at) = (0, 0);
    dict(data, |op, values| {
        if op == 18 && values.len() >= 2 {
            size = offset(values[values.len() - 2]);
            at = offset(values[values.len() - 1]);
        }
        false
    })?;
    (size >= 0 && at >= 0).then_some((size, at))
}
fn subroutine(data: &[u8]) -> Option<i32> {
    let mut result = None;
    dict(data, |op, values| {
        if op == 19 && !values.is_empty() {
            let v = offset(values[values.len() - 1]);
            result = (v >= 0).then_some(v);
            true
        } else {
            false
        }
    })?;
    result
}
fn dict(data: &[u8], mut visit: impl FnMut(u16, &[f64]) -> bool) -> Option<()> {
    let mut operands = [0.0; 48];
    let mut count = 0;
    let mut cursor = 0;
    while cursor < data.len() {
        let first = data[cursor];
        cursor += 1;
        if let Some(value) = number(data, &mut cursor, first) {
            if count == operands.len() {
                return None;
            }
            operands[count] = value;
            count += 1;
            continue;
        }
        let op = if first == 12 {
            let second = *data.get(cursor)?;
            cursor += 1;
            0xc00 | u16::from(second)
        } else {
            u16::from(first)
        };
        if visit(op, &operands[..count]) {
            return Some(());
        }
        count = 0;
    }
    Some(())
}
fn offset(value: f64) -> i32 {
    if (0.0..=f64::from(i32::MAX)).contains(&value) {
        value as i32
    } else {
        -1
    }
}
fn number(data: &[u8], cursor: &mut usize, first: u8) -> Option<f64> {
    match first {
        32..=246 => Some(f64::from(first) - 139.0),
        247..=250 => {
            let tail = *data.get(*cursor)?;
            *cursor += 1;
            Some(f64::from(first - 247) * 256.0 + f64::from(tail) + 108.0)
        }
        251..=254 => {
            let tail = *data.get(*cursor)?;
            *cursor += 1;
            Some(-f64::from(first - 251) * 256.0 - f64::from(tail) - 108.0)
        }
        28 => {
            let b = data.get(*cursor..*cursor + 2)?;
            *cursor += 2;
            Some(f64::from(i16::from_be_bytes([b[0], b[1]])))
        }
        29 => {
            let b = data.get(*cursor..*cursor + 4)?;
            *cursor += 4;
            Some(f64::from(i32::from_be_bytes([b[0], b[1], b[2], b[3]])))
        }
        30 => {
            let mut encoded = [0u8; 96];
            let mut len = 0;
            let mut ended = false;
            while *cursor < data.len() && !ended {
                let pair = data[*cursor];
                *cursor += 1;
                for nibble in [pair >> 4, pair & 15] {
                    if nibble == 15 {
                        ended = true;
                        break;
                    }
                    let text: &[u8] = match nibble {
                        0..=9 => std::slice::from_ref(&b"0123456789"[nibble as usize]),
                        10 => b".",
                        11 => b"E",
                        12 => b"E-",
                        14 => b"-",
                        _ => b"",
                    };
                    if len > encoded.len() - text.len() {
                        ended = true;
                        break;
                    }
                    encoded[len..len + text.len()].copy_from_slice(text);
                    len += text.len();
                }
            }
            ended
                .then(|| {
                    std::str::from_utf8(&encoded[..len])
                        .ok()?
                        .parse::<f64>()
                        .ok()
                })
                .flatten()
        }
        _ => None,
    }
}
