//! Original OpenTypeSvgGlyphParser traversal and material construction. XML is
//! supplied by a normal DOM dependency; all SVG policy remains owned ProGPU code.
use super::{FontColorLayer, FontError};
use crate::vector::*;
use roxmltree::{Document, Node, NodeId};
use std::{
    collections::{HashMap, HashSet},
    sync::Arc,
};

pub(super) fn parse(
    xml: &str,
    glyph: u16,
    units_per_em: u16,
) -> Result<Option<Arc<[FontColorLayer]>>, FontError> {
    if xml.encode_utf16().count() > 16 * 1024 * 1024 || has_doctype(xml) {
        return Ok(None);
    }
    let Ok(document) = Document::parse_with_options(
        xml,
        roxmltree::ParsingOptions {
            allow_dtd: false,
            entity_resolver: None,
            ..Default::default()
        },
    ) else {
        return Ok(None);
    };
    let mut elements = HashMap::new();
    for node in document
        .root_element()
        .descendants()
        .filter(Node::is_element)
    {
        if let Some(id) = node.attribute("id").filter(|id| !id.is_empty()) {
            elements.try_reserve(1).map_err(|_| FontError::Capacity)?;
            elements.entry(id).or_insert(node);
        }
    }
    let key = format!("glyph{glyph}");
    let Some(&element) = elements.get(key.as_str()) else {
        return Ok(None);
    };
    let mut parser = Parser {
        units_per_em,
        elements,
        active: HashSet::new(),
        layers: Vec::new(),
    };
    let mut ancestors = Vec::new();
    for ancestor in element.ancestors().skip(1).filter(Node::is_element) {
        ancestors.try_reserve(1).map_err(|_| FontError::Capacity)?;
        ancestors.push(ancestor);
    }
    let mut state = RenderState {
        transform: Matrix4::IDENTITY,
        fill: "black",
        opacity: 1.0,
        fill_opacity: 1.0,
    };
    for ancestor in ancestors.into_iter().rev() {
        state = apply_state(ancestor, state)?;
    }
    parser.render(element, state, 0, true)?;
    Ok((!parser.layers.is_empty()).then(|| parser.layers.into()))
}
struct Parser<'a, 'input> {
    units_per_em: u16,
    elements: HashMap<&'a str, Node<'a, 'input>>,
    active: HashSet<NodeId>,
    layers: Vec<FontColorLayer>,
}
#[derive(Clone, Copy)]
struct RenderState<'a> {
    transform: Matrix4,
    fill: &'a str,
    opacity: f32,
    fill_opacity: f32,
}
impl<'a, 'input: 'a> Parser<'a, 'input> {
    fn render(
        &mut self,
        element: Node<'a, 'input>,
        parent: RenderState<'a>,
        depth: u32,
        allow_definition: bool,
    ) -> Result<(), FontError> {
        if depth > 64 {
            return Ok(());
        }
        let name = element.tag_name().name();
        if name == "defs" && !allow_definition {
            return Ok(());
        }
        let state = apply_state(element, parent)?;
        match name {
            "svg" | "g" | "defs" => {
                for child in element.children().filter(Node::is_element) {
                    self.render(child, state, depth + 1, false)?;
                }
            }
            "use" => {
                let target = element
                    .attributes()
                    .find(|a| a.name() == "href")
                    .and_then(|a| a.value().strip_prefix('#'))
                    .and_then(|id| self.elements.get(id))
                    .copied();
                if let Some(target) = target {
                    self.active
                        .try_reserve(1)
                        .map_err(|_| FontError::Capacity)?;
                    if self.active.insert(target.id()) {
                        let result = self.render(target, state, depth + 1, true);
                        self.active.remove(&target.id());
                        result?;
                    }
                }
            }
            "path" | "circle" | "ellipse" | "rect" | "polygon" => self.shape(element, state)?,
            _ => {}
        }
        Ok(())
    }
    fn shape(
        &mut self,
        element: Node<'a, 'input>,
        state: RenderState<'a>,
    ) -> Result<(), FontError> {
        let Some(mut geometry) = geometry(element)? else {
            return Ok(());
        };
        if geometry.figures.is_empty() {
            return Ok(());
        }
        if element
            .attribute("fill-rule")
            .is_some_and(|s| s.eq_ignore_ascii_case("evenodd"))
        {
            geometry.fill_rule = FillRule::EvenOdd;
        }
        let geometry = geometry
            .transformed(state.transform)
            .map_err(|_| FontError::Capacity)?;
        let Some(brush) = self.brush(
            state.fill,
            state.opacity * state.fill_opacity,
            state.transform,
        )?
        else {
            return Ok(());
        };
        self.layers
            .try_reserve(1)
            .map_err(|_| FontError::Capacity)?;
        self.layers.push(FontColorLayer {
            geometry: Some(Arc::new(geometry)),
            brush: Some(Arc::new(brush)),
            uses_svg_coordinates: true,
            ..Default::default()
        });
        Ok(())
    }
    fn brush(&self, fill: &str, opacity: f32, shape: Matrix4) -> Result<Option<Brush>, FontError> {
        if fill.eq_ignore_ascii_case("none") || opacity <= 0.0 {
            return Ok(None);
        }
        if let Some(definition) = url_reference(fill)
            .and_then(|id| self.elements.get(id))
            .copied()
        {
            let name = definition.tag_name().name();
            if !matches!(name, "linearGradient" | "radialGradient") {
                return Ok(None);
            }
            let stops = gradient_stops(definition, opacity)?;
            if stops.is_empty() {
                return Ok(None);
            }
            let transform = parse_transform(definition.attribute("gradientTransform"))? * shape;
            let coordinate =
                |name, default| coordinate(definition, name, default, self.units_per_em);
            let spread = spread(definition);
            let kind = if name == "linearGradient" {
                let start =
                    transform.transform_point([coordinate("x1", 0.0), coordinate("y1", 0.0)]);
                let end = transform.transform_point([
                    coordinate("x2", f32::from(self.units_per_em)),
                    coordinate("y2", 0.0),
                ]);
                let mut gradient = LinearGradient::new(start, end, stops);
                gradient.options.spread = spread;
                BrushKind::Linear(gradient)
            } else {
                let cx = coordinate("cx", f32::from(self.units_per_em) * 0.5);
                let cy = coordinate("cy", f32::from(self.units_per_em) * 0.5);
                let radius = coordinate("r", f32::from(self.units_per_em) * 0.5);
                let fx = coordinate("fx", cx);
                let fy = coordinate("fy", cy);
                let center = transform.transform_point([cx, cy]);
                let origin = transform.transform_point([fx, fy]);
                let radii = [
                    distance(center, transform.transform_point([cx + radius, cy])),
                    distance(center, transform.transform_point([cx, cy + radius])),
                ];
                let mut gradient = RadialGradient::new(center, origin, radii, stops);
                gradient.options.spread = spread;
                BrushKind::Radial(gradient)
            };
            return Ok(Some(Brush::new(kind)));
        }
        Ok(color(fill).map(|c| Brush::new(BrushKind::Solid(with_opacity(c, opacity)))))
    }
}
fn geometry(element: Node<'_, '_>) -> Result<Option<PathGeometry>, FontError> {
    let get = |name| read_float(element, name, 0.0);
    Ok(Some(match element.tag_name().name() {
        "path" => {
            let Some(data) = element.attribute("d").filter(|s| !s.trim().is_empty()) else {
                return Ok(None);
            };
            match PathGeometry::parse(data) {
                Ok(path) => path,
                Err(PathParseError::Capacity) => return Err(FontError::Capacity),
                Err(_) => return Ok(None),
            }
        }
        "circle" => PathGeometry::ellipse([get("cx"), get("cy")], get("r"), get("r")),
        "ellipse" => PathGeometry::ellipse([get("cx"), get("cy")], get("rx"), get("ry")),
        "rect" => PathGeometry::rectangle(get("x"), get("y"), get("width"), get("height")),
        "polygon" => {
            let Some(text) = element.attribute("points").filter(|s| !s.trim().is_empty()) else {
                return Ok(None);
            };
            let values = number_list(text)?;
            if values.len() < 6 || !values.len().is_multiple_of(2) {
                return Ok(None);
            }
            let mut figure = PathFigure::new([values[0], values[1]], true);
            figure
                .segments
                .try_reserve_exact(values.len() / 2 - 1)
                .map_err(|_| FontError::Capacity)?;
            for pair in values[2..].chunks_exact(2) {
                figure
                    .segments
                    .push(PathSegment::new(PathSegmentKind::Line([pair[0], pair[1]])));
            }
            PathGeometry {
                figures: vec![figure],
                ..Default::default()
            }
        }
        _ => return Ok(None),
    }))
}
fn apply_state<'a, 'input: 'a>(
    element: Node<'a, 'input>,
    parent: RenderState<'a>,
) -> Result<RenderState<'a>, FontError> {
    let mut local = parse_transform(element.attribute("transform"))?;
    if element.tag_name().name() == "use" {
        let x = read_float(element, "x", 0.0);
        let y = read_float(element, "y", 0.0);
        if x != 0.0 || y != 0.0 {
            local = translation(x, y) * local;
        }
    }
    let fill = element.attribute("fill").unwrap_or(parent.fill);
    let opacity = parent.opacity * unit(element, "opacity", 1.0);
    let fill_opacity = if element.attribute("fill-opacity").is_none() {
        parent.fill_opacity
    } else {
        unit(element, "fill-opacity", parent.fill_opacity)
    };
    Ok(RenderState {
        transform: local * parent.transform,
        fill,
        opacity,
        fill_opacity,
    })
}
fn gradient_stops(gradient: Node<'_, '_>, opacity: f32) -> Result<Vec<GradientStop>, FontError> {
    let mut stops = Vec::new();
    for stop in gradient
        .children()
        .filter(|n| n.is_element() && n.tag_name().name() == "stop")
    {
        let Some(color) = color(stop.attribute("stop-color").unwrap_or("black")) else {
            continue;
        };
        let offset = percentage(stop.attribute("offset"), 0.0).clamp(0.0, 1.0);
        let opacity = opacity * unit(stop, "stop-opacity", 1.0);
        stops.try_reserve(1).map_err(|_| FontError::Capacity)?;
        stops.push(GradientStop {
            color: with_opacity(color, opacity),
            offset,
        });
    }
    // Original stable Single.CompareTo order: all NaNs first, equal zeros/stops
    // keep source order. total_cmp would incorrectly distinguish zero signs.
    stops.sort_by(|a, b| {
        a.offset.partial_cmp(&b.offset).unwrap_or_else(|| {
            match (a.offset.is_nan(), b.offset.is_nan()) {
                (true, true) => std::cmp::Ordering::Equal,
                (true, false) => std::cmp::Ordering::Less,
                _ => std::cmp::Ordering::Greater,
            }
        })
    });
    Ok(stops)
}
fn spread(element: Node<'_, '_>) -> GradientSpread {
    match element.attribute("spreadMethod") {
        Some(s) if s.eq_ignore_ascii_case("reflect") => GradientSpread::Reflect,
        Some(s) if s.eq_ignore_ascii_case("repeat") => GradientSpread::Repeat,
        _ => GradientSpread::Pad,
    }
}
fn coordinate(element: Node<'_, '_>, name: &str, default: f32, units: u16) -> f32 {
    let Some(text) = element
        .attribute(name)
        .map(str::trim)
        .filter(|s| !s.is_empty())
    else {
        return default;
    };
    if let Some(value) = text.strip_suffix('%').and_then(number) {
        value * 0.01 * f32::from(units)
    } else {
        number(text).unwrap_or(default)
    }
}
fn read_float(element: Node<'_, '_>, name: &str, default: f32) -> f32 {
    element.attribute(name).and_then(number).unwrap_or(default)
}
fn unit(element: Node<'_, '_>, name: &str, default: f32) -> f32 {
    percentage(element.attribute(name), default).clamp(0.0, 1.0)
}
fn percentage(text: Option<&str>, default: f32) -> f32 {
    let Some(text) = text.map(str::trim).filter(|s| !s.is_empty()) else {
        return default;
    };
    let (text, scale) = text.strip_suffix('%').map_or((text, 1.0), |s| (s, 0.01));
    number(text).map_or(default, |v| v * scale)
}
fn number(text: &str) -> Option<f32> {
    let text = text.trim_matches(|c| matches!(c, '\u{9}'..='\u{d}' | ' '));
    // Rust's extra "inf" spelling is not the source invariant Infinity token.
    let unsigned = text.strip_prefix(['+', '-']).unwrap_or(text);
    if unsigned.eq_ignore_ascii_case("inf") {
        return None;
    }
    text.parse().ok()
}
fn url_reference(value: &str) -> Option<&str> {
    let value = value.trim();
    if !value
        .get(..5)
        .is_some_and(|s| s.eq_ignore_ascii_case("url(#"))
        || !value.ends_with(')')
    {
        return None;
    }
    let id = value.get(5..value.len() - 1)?.trim();
    (!id.is_empty()).then_some(id)
}
fn color(value: &str) -> Option<[f32; 4]> {
    let mut value = value.trim();
    if value
        .get(..4)
        .is_some_and(|s| s.eq_ignore_ascii_case("var("))
    {
        value = value.find(',').map_or("black", |comma| {
            value[comma + 1..].trim_end_matches([')', ' '])
        });
    }
    if let Some(hex) = value.strip_prefix('#') {
        if matches!(hex.len(), 3 | 4) && hex.is_ascii() {
            let mut components = [1.0; 4];
            for (index, byte) in hex.bytes().enumerate() {
                components[index] = (byte as char).to_digit(16)? as f32 / 15.0;
            }
            return Some(components);
        }
        if matches!(hex.len(), 6 | 8) {
            let digits = hex.trim_matches(|c| matches!(c, '\u{9}'..='\u{d}' | ' '));
            if !digits.bytes().all(|c| c.is_ascii_hexdigit()) {
                return None;
            }
            let mut packed = u32::from_str_radix(digits, 16).ok()?;
            if hex.len() == 6 {
                packed = (packed << 8) | 255;
            }
            return Some([24, 16, 8, 0].map(|shift| ((packed >> shift) & 255) as f32 / 255.0));
        }
    }
    for (name, color) in [
        ("black", [0.0, 0.0, 0.0, 1.0]),
        ("currentColor", [0.0, 0.0, 0.0, 1.0]),
        ("white", [1.0; 4]),
        ("red", [1.0, 0.0, 0.0, 1.0]),
        ("green", [0.0, 0.5, 0.0, 1.0]),
        ("blue", [0.0, 0.0, 1.0, 1.0]),
        ("transparent", [0.0; 4]),
    ] {
        if value.eq_ignore_ascii_case(name) {
            return Some(color);
        }
    }
    None
}
fn with_opacity(mut color: [f32; 4], opacity: f32) -> [f32; 4] {
    color[3] *= opacity.clamp(0.0, 1.0);
    color
}
fn distance(a: [f32; 2], b: [f32; 2]) -> f32 {
    let x = a[0] - b[0];
    let y = a[1] - b[1];
    (x * x + y * y).sqrt()
}
fn parse_transform(text: Option<&str>) -> Result<Matrix4, FontError> {
    let Some(text) = text else {
        return Ok(Matrix4::IDENTITY);
    };
    let mut result = Matrix4::IDENTITY;
    let mut index = 0;
    while index < text.len() {
        separators(text, &mut index);
        let start = index;
        while let Some(c) = peek(text, index).filter(|c| letter(*c)) {
            index += c.len_utf8();
        }
        if start == index {
            break;
        }
        let name = &text[start..index];
        separators(text, &mut index);
        if peek(text, index) != Some('(') {
            break;
        }
        index += 1;
        let Some(end) = text[index..].find(')').map(|n| n + index) else {
            break;
        };
        result = create_transform(name, &number_list(&text[index..end])?) * result;
        index = end + 1;
    }
    Ok(result)
}
fn translation(x: f32, y: f32) -> Matrix4 {
    let mut m = Matrix4::IDENTITY;
    m.0[3][0] = x;
    m.0[3][1] = y;
    m
}
fn create_transform(name: &str, v: &[f32]) -> Matrix4 {
    let mut m = Matrix4::IDENTITY;
    if name.eq_ignore_ascii_case("matrix") && v.len() >= 6 {
        return Matrix4([
            [v[0], v[1], 0.0, 0.0],
            [v[2], v[3], 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [v[4], v[5], 0.0, 1.0],
        ]);
    }
    let Some(&first) = v.first() else {
        return m;
    };
    if name.eq_ignore_ascii_case("translate") {
        return translation(first, v.get(1).copied().unwrap_or(0.0));
    }
    if name.eq_ignore_ascii_case("scale") {
        m.0[0][0] = first;
        m.0[1][1] = v.get(1).copied().unwrap_or(first);
        return m;
    }
    let radians = first * (std::f32::consts::PI / 180.0);
    if name.eq_ignore_ascii_case("rotate") {
        // This retains the public Z-rotation contract. Default-runtime primitive
        // arithmetic/SIMD/FMA proof remains a required source qualification gate.
        let sin = radians.sin();
        let cos = radians.cos();
        m.0[0][0] = cos;
        m.0[0][1] = sin;
        m.0[1][0] = -sin;
        m.0[1][1] = cos;
        return if v.len() < 3 {
            m
        } else {
            translation(-v[1], -v[2]) * m * translation(v[1], v[2])
        };
    }
    if name.eq_ignore_ascii_case("skewx") {
        m.0[1][0] = radians.tan();
    } else if name.eq_ignore_ascii_case("skewy") {
        m.0[0][1] = radians.tan();
    }
    m
}
fn number_list(text: &str) -> Result<Vec<f32>, FontError> {
    let mut values = Vec::new();
    let mut index = 0;
    while index < text.len() {
        separators(text, &mut index);
        if index >= text.len() {
            break;
        }
        let start = index;
        if matches!(peek(text, index), Some('+' | '-')) {
            index += 1;
        }
        while let Some(c) = peek(text, index).filter(|c| digit(*c)) {
            index += c.len_utf8();
        }
        if peek(text, index) == Some('.') {
            index += 1;
            while let Some(c) = peek(text, index).filter(|c| digit(*c)) {
                index += c.len_utf8();
            }
        }
        if matches!(peek(text, index), Some('e' | 'E')) {
            index += 1;
            if matches!(peek(text, index), Some('+' | '-')) {
                index += 1;
            }
            while let Some(c) = peek(text, index).filter(|c| digit(*c)) {
                index += c.len_utf8();
            }
        }
        if start == index {
            break;
        }
        let Some(value) = number(&text[start..index]) else {
            break;
        };
        values.try_reserve(1).map_err(|_| FontError::Capacity)?;
        values.push(value);
    }
    Ok(values)
}
fn peek(text: &str, index: usize) -> Option<char> {
    text.get(index..)?.chars().next()
}
fn separators(text: &str, index: &mut usize) {
    while let Some(c) = peek(text, *index).filter(|c| c.is_whitespace() || *c == ',') {
        *index += c.len_utf8();
    }
}
fn letter(c: char) -> bool {
    use crate::text::unicode::{GeneralCategory::*, general_category};
    if c.is_ascii() {
        return c.is_ascii_alphabetic();
    }
    (c as u32) <= 0xffff
        && matches!(
            general_category(c as u32),
            UppercaseLetter | LowercaseLetter | TitlecaseLetter | ModifierLetter | OtherLetter
        )
}
fn digit(c: char) -> bool {
    use crate::text::unicode::{GeneralCategory, general_category};
    if c.is_ascii() {
        return c.is_ascii_digit();
    }
    (c as u32) <= 0xffff && general_category(c as u32) == GeneralCategory::DecimalDigitNumber
}
fn has_doctype(mut text: &str) -> bool {
    // The dependency permits an empty DTD with allow_dtd=false. Preserve the
    // source Prohibit policy for every declaration while ignoring text in XML
    // comments, CDATA and processing instructions; malformed XML rejects later.
    while let Some(index) = text.find('<') {
        text = &text[index..];
        if text.starts_with("<!DOCTYPE") {
            return true;
        }
        let terminator = if text.starts_with("<!--") {
            Some((4, "-->"))
        } else if text.starts_with("<![CDATA[") {
            Some((9, "]]>"))
        } else if text.starts_with("<?") {
            Some((2, "?>"))
        } else {
            None
        };
        if let Some((start, end)) = terminator {
            let Some(index) = text[start..].find(end) else {
                return false;
            };
            text = &text[start + index + end.len()..];
        } else {
            text = &text[1..];
        }
    }
    false
}
