//! Original single-attempt borrowed sbix source and one-glyph owned fallback cache.
use super::*;
use crate::font::SfntFace;
use std::{
    io::{BufReader, Cursor},
    path::PathBuf,
    sync::{Arc, Mutex},
};
#[derive(Clone, Debug)]
enum Source {
    File(Arc<PathBuf>),
    Retained(FontData),
}
#[derive(Debug, Default)]
struct Cache {
    attempted: bool,
    borrowed: Option<FontData>,
    last: Option<(u16, FontData)>,
}
#[derive(Debug)]
pub(crate) struct GlyphBitmapSource {
    source: Source,
    face_index: u32,
    cache: Mutex<Cache>,
}
impl GlyphBitmapSource {
    pub fn file(path: Arc<PathBuf>, face_index: u32) -> Arc<Self> {
        Arc::new(Self {
            source: Source::File(path),
            face_index,
            cache: Mutex::new(Cache::default()),
        })
    }
    pub fn retained(data: FontData, face_index: u32) -> Arc<Self> {
        Arc::new(Self {
            source: Source::Retained(data),
            face_index,
            cache: Mutex::new(Cache::default()),
        })
    }
    pub fn cached_bytes(&self) -> u64 {
        self.cache
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .last
            .as_ref()
            .map_or(0, |entry| entry.1.len() as u64)
    }
    pub fn glyph_data(&self, glyph: u16) -> Result<Option<FontData>, FontError> {
        {
            let mut cache = self.cache.lock().unwrap_or_else(|e| e.into_inner());
            if !cache.attempted {
                cache.attempted = true;
                if let Source::Retained(data) = &self.source {
                    // A supplied safe lease can borrow the full sbix table without
                    // a second allocation. File sources keep the actual fallback
                    // route because this crate has no safe OS mapping provider.
                    if let Some(face) = selected_face(data, self.face_index) {
                        if let Some((_, span)) = face.table_span(super::super::Tag::new(*b"sbix")) {
                            cache.borrowed =
                                data.slice(span.offset..span.offset + span.length).ok();
                        }
                    }
                }
            }
            if let Some(data) = &cache.borrowed {
                return Ok(Some(data.clone()));
            }
            if let Some((previous, data)) = &cache.last {
                if *previous == glyph {
                    return Ok(Some(data.clone()));
                }
            }
        }
        let loaded = match &self.source {
            Source::File(path) => {
                let Ok(file) = std::fs::File::open(path.as_ref()) else {
                    return Ok(None);
                };
                extract_glyph_sbix(
                    &mut BufReader::with_capacity(4096, file),
                    self.face_index,
                    glyph,
                    ExecutionPreference::Automatic,
                )
            }
            Source::Retained(data) => extract_glyph_sbix(
                &mut Cursor::new(data.clone()),
                self.face_index,
                glyph,
                ExecutionPreference::Automatic,
            ),
        };
        let data = match loaded {
            Ok(Some(value)) => value.data,
            Err(FontStorageError::Font(FontError::Capacity)) => return Err(FontError::Capacity),
            _ => return Ok(None),
        };
        self.cache.lock().unwrap_or_else(|e| e.into_inner()).last = Some((glyph, data.clone()));
        Ok(Some(data))
    }
}

fn selected_face(data: &FontData, selected: u32) -> Option<SfntFace> {
    let count = SfntFace::face_count(data).ok()?;
    if selected >= count {
        return None;
    }
    let mut result = None;
    for index in 0..count {
        let face = SfntFace::from_storage(data.clone(), index).ok()?;
        if index == selected {
            result = Some(face);
        }
    }
    result
}
