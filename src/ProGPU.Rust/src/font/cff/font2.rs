//! Original CFF2 container and selected private metadata, independently admitted.
use super::{FdSelect, Index, IndexLayout, SelectLayout, TopDictionary2, read_number};
use crate::font::{FontError, ItemVariationStore, SfntFace, Span, Tag, bytes};

/// Original CFF2 face retaining its exact immutable SFNT generation.
/// Opening validates every FontDICT and variation subtable; selected private
/// operators and charstrings retain their later, separate validation boundaries.
#[derive(Clone, Debug)]
pub struct Font2 {
    source: SfntFace,
    table: Span,
    chars: IndexLayout,
    global: IndexLayout,
    dictionaries: IndexLayout,
    select: Option<SelectLayout>,
    variation: Option<Span>,
    top: TopDictionary2,
    axes: u16,
}
/// Selected original private metadata, borrowing the CFF2 generation.
#[derive(Clone, Copy, Debug)]
pub struct GlyphPrivate2<'a> {
    /// Original encoded local subroutine inventory.
    pub local_subroutines: Index<'a>,
    /// Original initial variation-data selection (zero unless explicitly set).
    pub initial_vsindex: u16,
}
impl SfntFace {
    /// Open CFF2 using the original header, matrix, FontDICT and variation gates.
    /// A zero expected count disables only the optional glyph-count comparison.
    pub fn cff2(&self, expected_glyph_count: u16) -> Result<Font2, FontError> {
        let (_, table) = self
            .table_span(Tag::new(*b"CFF2"))
            .ok_or(FontError::InvalidFace)?;
        let data = table.get(&self.data);
        if data.len() < 5 || data[..3] != [2, 0, 5] {
            return Err(FontError::InvalidFace);
        }
        let size = usize::from(bytes::u16(data, 3).unwrap());
        let top = TopDictionary2::read(bytes::bytes(data, 5, size).ok_or(FontError::InvalidFace)?)?;
        if top.char_strings_offset as usize >= data.len()
            || top.font_dictionary_offset as usize >= data.len()
        {
            return Err(FontError::InvalidFace);
        }
        let global = Index::read_cff2(data, &mut (5 + size))?;
        let chars = Index::read_cff2(data, &mut (top.char_strings_offset as usize))?;
        let dictionaries = Index::read_cff2(data, &mut (top.font_dictionary_offset as usize))?;
        if global.len() > 65536
            || chars.is_empty()
            || (expected_glyph_count != 0 && chars.len() != u32::from(expected_glyph_count))
            || dictionaries.is_empty()
            || dictionaries.len() > u32::from(u16::MAX)
            || (dictionaries.len() == 1 && top.fd_select_offset != 0)
            || (dictionaries.len() > 1 && top.fd_select_offset == 0)
        {
            return Err(FontError::InvalidFace);
        }
        let select = if top.fd_select_offset != 0 {
            Some(
                FdSelect::read(data, top.fd_select_offset, chars.len(), dictionaries.len())?.layout,
            )
        } else {
            None
        };
        for index in 0..dictionaries.len() {
            let (offset, size) = private_range(dictionaries.get(index)?)?;
            if size != 0 {
                bytes::bytes(data, offset, size).ok_or(FontError::InvalidFace)?;
            }
        }
        let mut axes = 0;
        let variation = if top.variation_store_offset != 0 {
            axes = self.variation_axis_count()?;
            let offset = top.variation_store_offset as usize;
            let length = usize::from(bytes::u16(data, offset).ok_or(FontError::InvalidFace)?);
            if length == 0 {
                return Err(FontError::InvalidFace);
            }
            let span = Span {
                offset: offset + 2,
                length,
            };
            let store = ItemVariationStore::parse(
                bytes::bytes(data, span.offset, span.length).ok_or(FontError::InvalidFace)?,
                0,
                axes,
            )?;
            if store.subtable_count() == 0 {
                return Err(FontError::InvalidFace);
            }
            for index in 0..store.subtable_count() {
                store.blend_region_count(index)?;
            }
            Some(span)
        } else {
            None
        };
        let header = self.header_metrics().ok_or(FontError::InvalidFace)?;
        if header.units_per_em == 0 {
            return Err(FontError::InvalidFace);
        }
        let expected = 1.0 / f64::from(header.units_per_em);
        if (header.units_per_em != 1000 && !top.has_font_matrix)
            || (top.font_matrix_scale - expected).abs() > expected.max(1.0) * 1.0e-12
        {
            return Err(FontError::InvalidFace);
        }
        Ok(Font2 {
            source: self.clone(),
            table,
            chars: chars.layout,
            global: global.layout,
            dictionaries: dictionaries.layout,
            select,
            variation,
            top,
            axes,
        })
    }
}
impl Font2 {
    /// Exact selected SFNT owner.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Original CFF2 bytes.
    pub fn data(&self) -> &[u8] {
        self.table.get(&self.source.data)
    }
    /// Original strict top dictionary.
    pub fn top_dictionary(&self) -> TopDictionary2 {
        self.top
    }
    /// Original encoded charstrings.
    pub fn char_strings(&self) -> Index<'_> {
        self.chars.view(self.data())
    }
    /// Original encoded global subroutines.
    pub fn global_subroutines(&self) -> Index<'_> {
        self.global.view(self.data())
    }
    /// Complete original FontDICT inventory.
    pub fn font_dictionaries(&self) -> Index<'_> {
        self.dictionaries.view(self.data())
    }
    /// Original optional font-dictionary selection.
    pub fn fd_select(&self) -> Option<FdSelect<'_>> {
        self.select.map(|s| s.view(self.data()))
    }
    /// Required coordinate count; zero when no variation store was declared.
    pub fn axis_count(&self) -> u16 {
        self.axes
    }
    /// Borrow the original bounded store. This validates its encoded inventory
    /// again without allocation; source bytes cannot change after opening.
    pub fn variation_store(&self) -> Option<ItemVariationStore<'_>> {
        self.variation.map(|s| {
            ItemVariationStore::parse(s.get(self.data()), 0, self.axes)
                .expect("validated immutable CFF2 store")
        })
    }
    /// Resolve selected private operators and local subroutines. A malformed
    /// unselected private program does not change the native opening contract.
    pub fn glyph_private(&self, glyph: u32) -> Result<GlyphPrivate2<'_>, FontError> {
        if glyph >= self.char_strings().len() {
            return Err(FontError::InvalidArgument);
        }
        let index = match self.fd_select() {
            Some(s) => s.dictionary(glyph)?,
            None => 0,
        };
        let (offset, size) = private_range(self.font_dictionaries().get(index)?)?;
        let mut result = GlyphPrivate2 {
            local_subroutines: Index::default(),
            initial_vsindex: 0,
        };
        if size == 0 {
            return Ok(result);
        }
        let dictionary = bytes::bytes(self.data(), offset, size).ok_or(FontError::InvalidFace)?;
        let mut operands = [0.0; 513];
        let mut len = 0;
        let mut cursor = 0;
        let mut operations = 0;
        let mut saw_vsindex = false;
        let mut subroutines = None;
        while cursor < dictionary.len() {
            let op = dictionary[cursor];
            cursor += 1;
            if let Some(value) = read_number(dictionary, &mut cursor, op) {
                if len == operands.len() {
                    return Err(FontError::InvalidFace);
                }
                operands[len] = value;
                len += 1;
                continue;
            }
            let op = operation(dictionary, &mut cursor, op)?;
            match op {
                22 => {
                    if operations != 0 || len != 1 || saw_vsindex {
                        return Err(FontError::InvalidFace);
                    }
                    let value = integer(operands[0], u16::MAX.into())? as u16;
                    self.variation_store()
                        .ok_or(FontError::InvalidFace)?
                        .blend_region_count(value)?;
                    result.initial_vsindex = value;
                    saw_vsindex = true;
                }
                19 => {
                    if len != 1 || subroutines.is_some() {
                        return Err(FontError::InvalidFace);
                    }
                    subroutines = Some(integer(operands[0], i32::MAX as u32)? as usize);
                }
                _ => {}
            }
            len = 0;
            operations += 1;
        }
        if len != 0 {
            return Err(FontError::InvalidFace);
        }
        if let Some(relative) = subroutines {
            if relative > self.data().len() - offset {
                return Err(FontError::InvalidFace);
            }
            result.local_subroutines = Index::read_cff2(self.data(), &mut (offset + relative))?;
            if result.local_subroutines.len() > 65536 {
                return Err(FontError::InvalidFace);
            }
        }
        Ok(result)
    }
}
fn private_range(data: &[u8]) -> Result<(usize, usize), FontError> {
    let mut operands = [0.0; 2];
    let mut len = 0;
    let mut cursor = 0;
    let mut range = None;
    while cursor < data.len() {
        let op = data[cursor];
        cursor += 1;
        if let Some(value) = read_number(data, &mut cursor, op) {
            if len == 2 {
                return Err(FontError::InvalidFace);
            }
            operands[len] = value;
            len += 1;
            continue;
        }
        if operation(data, &mut cursor, op)? != 18 || len != 2 || range.is_some() {
            return Err(FontError::InvalidFace);
        }
        let size = integer(operands[0], i32::MAX as u32)? as usize;
        let offset = integer(operands[1], i32::MAX as u32)? as usize;
        if (size == 0) != (offset == 0) {
            return Err(FontError::InvalidFace);
        }
        range = Some((offset, size));
        len = 0;
    }
    if len != 0 {
        return Err(FontError::InvalidFace);
    }
    range.ok_or(FontError::InvalidFace)
}
fn operation(data: &[u8], cursor: &mut usize, op: u8) -> Result<u16, FontError> {
    if op != 12 {
        return Ok(u16::from(op));
    }
    let next = *data.get(*cursor).ok_or(FontError::InvalidFace)?;
    *cursor += 1;
    Ok(0x0c00 | u16::from(next))
}
fn integer(value: f64, maximum: u32) -> Result<u32, FontError> {
    if value.is_finite() && value >= 0.0 && value <= f64::from(maximum) && value == value.trunc() {
        Ok(value as u32)
    } else {
        Err(FontError::InvalidFace)
    }
}
