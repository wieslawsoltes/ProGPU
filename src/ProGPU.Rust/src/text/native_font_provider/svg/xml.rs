//! Bounded native XML surface, operating on original bytes, not a UTF-8 document.
use super::{Result, fail, push};
use std::collections::HashMap;
#[derive(Debug)]
pub(super) struct Node {
    pub name: Vec<u8>,
    pub attributes: Vec<(Vec<u8>, Vec<u8>)>,
    pub parent: Option<usize>,
    pub children: Vec<usize>,
}
impl Node {
    pub fn attribute(&self, name: &[u8]) -> Option<&[u8]> {
        self.attributes
            .iter()
            .find(|v| local(&v.0) == name)
            .map(|v| v.1.as_slice())
    }
}
pub(super) struct Document {
    pub nodes: Vec<Node>,
    pub ids: HashMap<Vec<u8>, usize>,
}
pub(super) fn local(name: &[u8]) -> &[u8] {
    name.iter()
        .rposition(|&v| v == b':')
        .map_or(name, |index| &name[index + 1..])
}
pub(super) fn space(value: u8) -> bool {
    matches!(value, b' ' | b'\t' | b'\n' | b'\r' | 0x0b | 0x0c)
}
pub(super) fn trim(mut value: &[u8]) -> &[u8] {
    while value.first().is_some_and(|v| space(*v)) {
        value = &value[1..];
    }
    while value.last().is_some_and(|v| space(*v)) {
        value = &value[..value.len() - 1];
    }
    value
}
fn skip(text: &[u8], index: &mut usize) {
    while text.get(*index).is_some_and(|v| space(*v)) {
        *index += 1;
    }
}
fn name(text: &[u8], index: &mut usize) -> Result<Vec<u8>> {
    let start = *index;
    while text
        .get(*index)
        .is_some_and(|v| v.is_ascii_alphanumeric() || matches!(v, b'_' | b'-' | b':' | b'.'))
    {
        *index += 1;
    }
    if *index == start {
        return fail();
    }
    super::copy(&text[start..*index])
}
fn entities(text: &[u8]) -> Result<Vec<u8>> {
    let mut output = Vec::new();
    output
        .try_reserve(text.len())
        .map_err(|_| super::FontError::Capacity)?;
    let mut index = 0;
    while index < text.len() {
        if text[index] != b'&' {
            output.push(text[index]);
            index += 1;
            continue;
        }
        let end = text[index + 1..]
            .iter()
            .position(|&v| v == b';')
            .map(|v| v + index + 1)
            .ok_or(super::FontError::InvalidGlyph)?;
        let entity = &text[index + 1..end];
        match entity {
            b"amp" => output.push(b'&'),
            b"lt" => output.push(b'<'),
            b"gt" => output.push(b'>'),
            b"quot" => output.push(b'"'),
            b"apos" => output.push(b'\''),
            _ => {
                let Some(mut digits) = entity.strip_prefix(b"#") else {
                    return fail();
                };
                let radix = if matches!(digits.first(), Some(b'x' | b'X')) {
                    digits = &digits[1..];
                    16
                } else {
                    10
                };
                if digits.is_empty() {
                    return fail();
                }
                let mut value = 0u32;
                for &digit in digits {
                    let digit = match digit {
                        b'0'..=b'9' => u32::from(digit - b'0'),
                        b'a'..=b'f' => u32::from(digit - b'a') + 10,
                        b'A'..=b'F' => u32::from(digit - b'A') + 10,
                        _ => return fail(),
                    };
                    if digit >= radix {
                        return fail();
                    }
                    value = value
                        .checked_mul(radix)
                        .and_then(|v| v.checked_add(digit))
                        .ok_or(super::FontError::InvalidGlyph)?;
                }
                let value = char::from_u32(value)
                    .filter(|v| *v != '\0')
                    .ok_or(super::FontError::InvalidGlyph)?;
                let mut bytes = [0u8; 4];
                output.extend_from_slice(value.encode_utf8(&mut bytes).as_bytes());
            }
        }
        index = end + 1;
    }
    Ok(output)
}
fn skip_until(text: &[u8], index: &mut usize, terminator: &[u8]) -> Result<()> {
    let offset = text[*index..]
        .windows(terminator.len())
        .position(|v| v == terminator)
        .ok_or(super::FontError::InvalidGlyph)?;
    *index += offset + terminator.len();
    Ok(())
}
impl Document {
    pub fn parse(text: &[u8]) -> Result<Self> {
        if text.is_empty() || text.len() > 16 * 1024 * 1024 {
            return fail();
        }
        let mut result = Self {
            nodes: Vec::new(),
            ids: HashMap::new(),
        };
        let mut stack: Vec<usize> = Vec::new();
        let mut root = None;
        let mut index = 0;
        while index < text.len() {
            let Some(next) = text[index..].iter().position(|&v| v == b'<') else {
                break;
            };
            index += next;
            if text[index..].starts_with(b"<!--") {
                index += 4;
                skip_until(text, &mut index, b"-->")?;
                continue;
            }
            if text[index..].starts_with(b"<?") {
                index += 2;
                skip_until(text, &mut index, b"?>")?;
                continue;
            }
            if text[index..].starts_with(b"<![CDATA[") {
                index += 9;
                skip_until(text, &mut index, b"]]>")?;
                continue;
            }
            index += 1;
            if text.get(index) == Some(&b'!') {
                return fail();
            }
            if text.get(index) == Some(&b'/') {
                index += 1;
                skip(text, &mut index);
                let closing = name(text, &mut index)?;
                skip(text, &mut index);
                let node = stack.pop().ok_or(super::FontError::InvalidGlyph)?;
                if text.get(index) != Some(&b'>') || result.nodes[node].name != closing {
                    return fail();
                }
                index += 1;
                continue;
            }
            skip(text, &mut index);
            let mut node = Node {
                name: name(text, &mut index)?,
                attributes: Vec::new(),
                parent: stack.last().copied(),
                children: Vec::new(),
            };
            let mut closed = false;
            while index < text.len() {
                skip(text, &mut index);
                if text.get(index) == Some(&b'>') {
                    index += 1;
                    break;
                }
                if text.get(index..index.saturating_add(2)) == Some(b"/>") {
                    closed = true;
                    index += 2;
                    break;
                }
                let attribute = name(text, &mut index)?;
                skip(text, &mut index);
                if text.get(index) != Some(&b'=') {
                    return fail();
                }
                index += 1;
                skip(text, &mut index);
                let quote = *text.get(index).ok_or(super::FontError::InvalidGlyph)?;
                if quote != b'\'' && quote != b'"' {
                    return fail();
                }
                index += 1;
                let start = index;
                while text.get(index).is_some_and(|v| *v != quote) {
                    index += 1;
                }
                if index == text.len() {
                    return fail();
                }
                let value = entities(&text[start..index])?;
                index += 1;
                push(&mut node.attributes, (attribute, value))?;
            }
            let node_index = result.nodes.len();
            if let Some(parent) = node.parent {
                push(&mut result.nodes[parent].children, node_index)?;
            } else if root.replace(node_index).is_some() {
                return fail();
            }
            if let Some(id) = node.attribute(b"id").filter(|v| !v.is_empty()) {
                result
                    .ids
                    .try_reserve(1)
                    .map_err(|_| super::FontError::Capacity)?;
                result.ids.entry(super::copy(id)?).or_insert(node_index);
            }
            push(&mut result.nodes, node)?;
            if !closed {
                push(&mut stack, node_index)?;
            }
        }
        if root.is_none() || !stack.is_empty() {
            return fail();
        }
        Ok(result)
    }
}
