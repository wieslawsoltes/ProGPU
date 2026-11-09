//! Original CFF encoded data access and source-owned CFF1/CFF2 faces.
//! Container access and unhinted Type 2 execution retain independent validation.
use super::{FontError, SfntFace, Span, Tag, bytes};
mod dictionary;
mod fd_select;
mod font2;
mod index;
mod managed;
pub(crate) use managed::ManagedCff;
mod type2;
pub use dictionary::{TopDictionary, TopDictionary2, read_number};
pub use fd_select::FdSelect;
use fd_select::SelectLayout;
pub use font2::{Font2, GlyphPrivate2};
pub use index::Index;
use index::IndexLayout;
pub use type2::Outline2;
pub use type2::{Outline, OutlineTransform};

/// Original CFF1 face retaining its selected SFNT generation and encoded indices.
/// Opening validates containers; charstring execution is a separate operation.
#[derive(Clone, Debug)]
pub struct Font {
    source: SfntFace,
    table: Span,
    char_strings: IndexLayout,
    global: IndexLayout,
    local: IndexLayout,
    dictionaries: IndexLayout,
    select: Option<SelectLayout>,
    top: TopDictionary,
}
impl SfntFace {
    /// Open the original selected CFF1 table. A zero expected count disables the
    /// optional external glyph-count check, exactly as in native.
    pub fn cff1(&self, expected_glyph_count: u16) -> Result<Font, FontError> {
        let (_, table) = self
            .table_span(Tag::new(*b"CFF "))
            .ok_or(FontError::InvalidFace)?;
        let data = table.get(&self.data);
        if data.len() < 4 || data[0] != 1 {
            return Err(FontError::InvalidFace);
        }
        let mut cursor = usize::from(data[2]);
        if cursor < 4 || cursor > data.len() {
            return Err(FontError::InvalidFace);
        }
        Index::read(data, &mut cursor)?; // Names remain independently encoded.
        let tops = Index::read(data, &mut cursor)?;
        if tops.len() != 1 {
            return Err(FontError::InvalidFace);
        }
        Index::read(data, &mut cursor)?; // Strings.
        let global = Index::read(data, &mut cursor)?;
        let top = TopDictionary::read(tops.get(0)?)?;
        let mut position = top.char_strings_offset as usize;
        if position >= data.len() {
            return Err(FontError::InvalidFace);
        }
        let chars = Index::read(data, &mut position)?;
        if chars.is_empty()
            || (expected_glyph_count != 0 && chars.len() != u32::from(expected_glyph_count))
            || ((top.font_dictionary_offset == 0) != (top.fd_select_offset == 0))
        {
            return Err(FontError::InvalidFace);
        }
        let local = if top.private_size > 0 {
            Index::local_subroutines(data, top.private_offset, top.private_size)?
        } else {
            Index::default()
        };
        let mut dictionaries = Index::default();
        let mut select = None;
        if top.font_dictionary_offset != 0 || top.fd_select_offset != 0 {
            let mut position = top.font_dictionary_offset as usize;
            if top.font_dictionary_offset == 0
                || top.fd_select_offset == 0
                || position >= data.len()
            {
                return Err(FontError::InvalidFace);
            }
            dictionaries = Index::read(data, &mut position)?;
            if dictionaries.is_empty() {
                return Err(FontError::InvalidFace);
            }
            select = Some(
                FdSelect::read(data, top.fd_select_offset, chars.len(), dictionaries.len())?.layout,
            );
        }
        Ok(Font {
            source: self.clone(),
            table,
            char_strings: chars.layout,
            global: global.layout,
            local: local.layout,
            dictionaries: dictionaries.layout,
            select,
            top,
        })
    }
}
impl Font {
    /// Original selected SFNT source, retained by this CFF generation.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Original selected CFF table bytes.
    pub fn data(&self) -> &[u8] {
        self.table.get(&self.source.data)
    }
    /// Original parsed CFF1 top dictionary.
    pub fn top_dictionary(&self) -> TopDictionary {
        self.top
    }
    /// Source-borrowed charstrings, including empty strings.
    pub fn char_strings(&self) -> Index<'_> {
        self.char_strings.view(self.data())
    }
    /// Original global subroutines.
    pub fn global_subroutines(&self) -> Index<'_> {
        self.global.view(self.data())
    }
    /// Optional CID font-dictionary INDEX.
    pub fn font_dictionaries(&self) -> Index<'_> {
        self.dictionaries.view(self.data())
    }
    /// Original FDSelect mapping, absent for the non-CID route.
    pub fn fd_select(&self) -> Option<FdSelect<'_>> {
        self.select.map(|s| s.view(self.data()))
    }
    /// Resolve the original local subroutine INDEX for one glyph.
    /// Native missing/malformed private metadata retains its empty-result policy;
    /// a declared out-of-bounds private or subroutine extent still rejects.
    pub fn local_subroutines(&self, glyph: u32) -> Result<Index<'_>, FontError> {
        if glyph >= self.char_strings().len() {
            return Err(FontError::InvalidArgument);
        }
        let Some(select) = self.fd_select() else {
            return Ok(self.local.view(self.data()));
        };
        let index = select.dictionary(glyph)?;
        let dictionary = self.font_dictionaries().get(index)?;
        let Some((offset, size)) = dictionary::private_range(dictionary) else {
            return Ok(Index::default());
        };
        if size == 0 {
            return Ok(Index::default());
        }
        Index::local_subroutines(self.data(), offset, size)
    }
}
impl<'a> Index<'a> {
    /// Original private DICT/Subrs lookup without allocating or executing code.
    pub fn local_subroutines(data: &'a [u8], offset: u32, size: u32) -> Result<Self, FontError> {
        let private =
            bytes::bytes(data, offset as usize, size as usize).ok_or(FontError::InvalidFace)?;
        if size == 0 {
            return Ok(Self::default());
        }
        let Some(relative) = dictionary::subroutine_offset(private) else {
            return Ok(Self::default());
        };
        let mut cursor = (offset as usize)
            .checked_add(relative as usize)
            .ok_or(FontError::InvalidFace)?;
        if cursor > data.len() {
            return Err(FontError::InvalidFace);
        }
        Self::read(data, &mut cursor)
    }
}
