//! Original declaration scanner. Comments affect statement boundaries only;
//! admission and declaration lookup deliberately retain the producer's lexical
//! behavior instead of inventing a stricter SkSL language compiler.
use super::{
    RuntimeChildDescriptor, RuntimeChildKind, RuntimeEffectError as E, RuntimeEffectKind,
    RuntimeEffectProgram, RuntimeUniformDescriptor, RuntimeUniformType,
};
use crate::text::unicode::{GeneralCategory as Category, general_category};
use std::sync::Arc;

pub(super) fn parse(source: Arc<str>, kind: RuntimeEffectKind) -> Result<RuntimeEffectProgram, E> {
    if source.trim().is_empty() {
        return Err(E::EmptySource);
    }
    if !contains_main(&source) {
        return Err(E::MissingMain);
    }
    let mut uniforms = Vec::new();
    let mut children = Vec::new();
    let mut offset = 0usize;
    for statement in Statements::new(&source) {
        let Some((ty, name, array_count)) = uniform(statement) else {
            continue;
        };
        if let Some(kind) = child_kind(ty) {
            if array_count != 1 {
                return Err(E::ChildArray(name.into()));
            }
            children.try_reserve(1).map_err(|_| E::Capacity)?;
            children.push(RuntimeChildDescriptor {
                name: Arc::from(name),
                kind,
            });
            continue;
        }
        let ty = uniform_type(ty).ok_or_else(|| E::UnsupportedUniformType(ty.into()))?;
        let size = ty
            .element_size()
            .checked_mul(array_count)
            .filter(|&v| v <= i32::MAX as usize)
            .ok_or(E::Capacity)?;
        let end = offset
            .checked_add(size)
            .filter(|&v| v <= i32::MAX as usize)
            .ok_or(E::Capacity)?;
        uniforms.try_reserve(1).map_err(|_| E::Capacity)?;
        uniforms.push(RuntimeUniformDescriptor {
            name: Arc::from(name),
            offset,
            size,
            ty,
            array_count,
        });
        offset = end;
    }
    Ok(RuntimeEffectProgram {
        source,
        kind,
        uniforms: uniforms.into(),
        children: children.into(),
        uniform_size: offset,
    })
}

fn contains_main(source: &str) -> bool {
    source.match_indices("main").any(|(index, _)| {
        let before = source[..index].chars().next_back().unwrap_or('\0');
        let after = source[index + 4..].chars().next().unwrap_or('\0');
        !identifier_part(before) && (after.is_whitespace() || after == '(')
    })
}

struct Statements<'a> {
    source: &'a str,
    index: usize,
    start: usize,
    depth: usize,
    line_comment: bool,
    block_comment: bool,
}
impl<'a> Statements<'a> {
    fn new(source: &'a str) -> Self {
        Self {
            source,
            index: 0,
            start: 0,
            depth: 0,
            line_comment: false,
            block_comment: false,
        }
    }
}
impl<'a> Iterator for Statements<'a> {
    type Item = &'a str;
    fn next(&mut self) -> Option<Self::Item> {
        let bytes = self.source.as_bytes();
        while self.index < bytes.len() {
            let index = self.index;
            let current = bytes[index];
            let next = bytes.get(index + 1).copied().unwrap_or(0);
            self.index += 1;
            if self.line_comment {
                if current == b'\n' {
                    self.line_comment = false;
                }
                continue;
            }
            if self.block_comment {
                if current == b'*' && next == b'/' {
                    self.block_comment = false;
                    self.index += 1;
                }
                continue;
            }
            if current == b'/' && next == b'/' {
                self.line_comment = true;
                self.index += 1;
                continue;
            }
            if current == b'/' && next == b'*' {
                self.block_comment = true;
                self.index += 1;
                continue;
            }
            match current {
                b'{' => self.depth += 1,
                b'}' => self.depth = self.depth.saturating_sub(1),
                b';' if self.depth == 0 => {
                    let statement = &self.source[self.start..index];
                    self.start = self.index;
                    return Some(statement);
                }
                _ => {}
            }
        }
        None
    }
}

fn uniform(statement: &str) -> Option<(&str, &str, usize)> {
    let text = statement.trim();
    let index = text.find("uniform")?;
    let text = text[index + "uniform".len()..].trim();
    let length = identifier_length(text);
    if length == 0 {
        return None;
    }
    let (ty, text) = text.split_at(length);
    let text = text.trim();
    let length = identifier_length(text);
    if length == 0 {
        return None;
    }
    let (name, text) = text.split_at(length);
    let text = text.trim();
    if text.is_empty() {
        return Some((ty, name, 1));
    }
    let count = text.strip_prefix('[')?.strip_suffix(']')?.trim();
    let count = count.parse::<i32>().ok()?;
    (count > 0).then_some((ty, name, count as usize))
}

fn identifier_length(text: &str) -> usize {
    let mut chars = text.char_indices();
    let Some((_, first)) = chars.next() else {
        return 0;
    };
    if first != '_' && !letter(first) {
        return 0;
    }
    for (index, value) in chars {
        if !identifier_part(value) {
            return index;
        }
    }
    text.len()
}
fn letter(value: char) -> bool {
    // The producer calls the UTF-16 char overload, so supplementary scalars do
    // not become identifier letters through Rust's scalar-char representation.
    value <= '\u{ffff}'
        && matches!(
            general_category(value as u32),
            Category::UppercaseLetter
                | Category::LowercaseLetter
                | Category::TitlecaseLetter
                | Category::ModifierLetter
                | Category::OtherLetter
        )
}
fn identifier_part(value: char) -> bool {
    value == '_'
        || letter(value)
        || (value <= '\u{ffff}' && general_category(value as u32) == Category::DecimalDigitNumber)
}
fn child_kind(ty: &str) -> Option<RuntimeChildKind> {
    Some(match ty {
        "shader" => RuntimeChildKind::Shader,
        "colorFilter" => RuntimeChildKind::ColorFilter,
        "blender" => RuntimeChildKind::Blender,
        _ => return None,
    })
}
fn uniform_type(ty: &str) -> Option<RuntimeUniformType> {
    use RuntimeUniformType::*;
    Some(match ty {
        "float" => Float,
        "half" => Half,
        "int" => Int,
        "float2" => Float2,
        "half2" => Half2,
        "int2" => Int2,
        "float3" => Float3,
        "half3" => Half3,
        "int3" => Int3,
        "float4" => Float4,
        "half4" => Half4,
        "int4" => Int4,
        "float2x2" => Float2x2,
        "half2x2" => Half2x2,
        "float3x3" => Float3x3,
        "half3x3" => Half3x3,
        "float4x4" => Float4x4,
        "half4x4" => Half4x4,
        _ => return None,
    })
}
