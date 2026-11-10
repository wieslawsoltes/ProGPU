use super::{
    Result, number, push,
    xml::{Node, space, trim},
};
use crate::geometry::Affine;
use std::f32::consts::PI;
pub(super) fn multiply(Affine(a): Affine, Affine(b): Affine) -> Affine {
    Affine([
        a[0] * b[0] + a[1] * b[2],
        a[0] * b[1] + a[1] * b[3],
        a[2] * b[0] + a[3] * b[2],
        a[2] * b[1] + a[3] * b[3],
        a[4] * b[0] + a[5] * b[2] + b[4],
        a[4] * b[1] + a[5] * b[3] + b[5],
    ])
}
pub(super) fn float(node: &Node, name: &[u8], default: f32) -> f32 {
    node.attribute(name)
        .and_then(|v| number::exact(trim(v)))
        .unwrap_or(default)
}
pub(super) fn coordinate(node: &Node, name: &[u8], default: f32, units: u16) -> f32 {
    let Some(text) = node.attribute(name).map(trim) else {
        return default;
    };
    if let Some(percent) = text.strip_suffix(b"%") {
        number::exact(trim(percent))
            .map(|v| v * 0.01 * f32::from(units))
            .unwrap_or(default)
    } else {
        float(node, name, default)
    }
}
pub(super) fn interval(node: &Node, name: &[u8], default: f32) -> f32 {
    let value = node
        .attribute(name)
        .and_then(|v| {
            let text = trim(v);
            let (text, scale) = text.strip_suffix(b"%").map_or((text, 1.0), |v| (v, 0.01));
            number::exact(text).map(|v| v * scale)
        })
        .unwrap_or(default);
    value.clamp(0.0, 1.0)
}
fn separators(text: &[u8], index: &mut usize) {
    while text.get(*index).is_some_and(|v| space(*v) || *v == b',') {
        *index += 1;
    }
}
pub(super) fn numbers(text: &[u8]) -> Result<Vec<f32>> {
    let mut values = Vec::new();
    let mut index = 0;
    loop {
        separators(text, &mut index);
        let Some(value) = number::parse(text, &mut index) else {
            break;
        };
        push(&mut values, value)?;
    }
    Ok(values)
}
fn create_transform(name: &[u8], values: &[f32]) -> Affine {
    let mut result = Affine::IDENTITY;
    if name.eq_ignore_ascii_case(b"matrix") && values.len() >= 6 {
        return Affine(values[..6].try_into().expect("six values"));
    }
    if values.is_empty() {
        return result;
    }
    if name.eq_ignore_ascii_case(b"translate") {
        result.0[4] = values[0];
        result.0[5] = values.get(1).copied().unwrap_or(0.0);
    } else if name.eq_ignore_ascii_case(b"scale") {
        result.0[0] = values[0];
        result.0[3] = values.get(1).copied().unwrap_or(values[0]);
    } else if name.eq_ignore_ascii_case(b"rotate") {
        let angle = values[0] * PI / 180.0;
        let cosine = angle.cos();
        let sine = angle.sin();
        result = Affine([cosine, sine, -sine, cosine, 0.0, 0.0]);
        if values.len() >= 3 {
            let to = Affine([1.0, 0.0, 0.0, 1.0, -values[1], -values[2]]);
            let back = Affine([1.0, 0.0, 0.0, 1.0, values[1], values[2]]);
            result = multiply(multiply(to, result), back);
        }
    } else if name.eq_ignore_ascii_case(b"skewx") {
        result.0[2] = (values[0] * PI / 180.0).tan();
    } else if name.eq_ignore_ascii_case(b"skewy") {
        result.0[1] = (values[0] * PI / 180.0).tan();
    }
    result
}
pub(super) fn transform(text: &[u8]) -> Affine {
    let mut result = Affine::IDENTITY;
    let mut index = 0;
    loop {
        separators(text, &mut index);
        let start = index;
        while text.get(index).is_some_and(|v| v.is_ascii_alphabetic()) {
            index += 1;
        }
        if index == start {
            break;
        }
        let name = &text[start..index];
        separators(text, &mut index);
        if text.get(index) != Some(&b'(') {
            break;
        }
        index += 1;
        let start = index;
        while text.get(index).is_some_and(|v| *v != b')') {
            index += 1;
        }
        if index == text.len() {
            break;
        }
        let Ok(values) = numbers(&text[start..index]) else {
            break;
        };
        result = multiply(create_transform(name, &values), result);
        index += 1;
    }
    result
}
fn nibble(value: u8) -> Option<u8> {
    match value {
        b'0'..=b'9' => Some(value - b'0'),
        b'a'..=b'f' => Some(value - b'a' + 10),
        b'A'..=b'F' => Some(value - b'A' + 10),
        _ => None,
    }
}
pub(super) fn color(mut text: &[u8]) -> Option<[f32; 4]> {
    text = trim(text);
    if text
        .get(..4)
        .is_some_and(|v| v.eq_ignore_ascii_case(b"var("))
    {
        text = text
            .iter()
            .position(|v| *v == b',')
            .map_or(b"black".as_slice(), |index| trim(&text[index + 1..]));
        while text.last().is_some_and(|v| *v == b')' || space(*v)) {
            text = &text[..text.len() - 1];
        }
    }
    if let Some(hex) = text.strip_prefix(b"#") {
        let mut value = [0.0, 0.0, 0.0, 1.0];
        match hex.len() {
            3 | 4 => {
                for (out, &digit) in value.iter_mut().zip(hex) {
                    *out = f32::from(nibble(digit)?) / 15.0;
                }
            }
            6 | 8 => {
                for (out, pair) in value.iter_mut().zip(hex.chunks_exact(2)) {
                    *out = f32::from((nibble(pair[0])? << 4) | nibble(pair[1])?) / 255.0;
                }
            }
            _ => return None,
        }
        return Some(value);
    }
    for (name, color) in [
        (b"black".as_slice(), [0.0, 0.0, 0.0, 1.0]),
        (b"currentcolor", [0.0, 0.0, 0.0, 1.0]),
        (b"white", [1.0; 4]),
        (b"red", [1.0, 0.0, 0.0, 1.0]),
        (b"green", [0.0, 0.5, 0.0, 1.0]),
        (b"blue", [0.0, 0.0, 1.0, 1.0]),
        (b"transparent", [0.0; 4]),
    ] {
        if text.eq_ignore_ascii_case(name) {
            return Some(color);
        }
    }
    None
}
