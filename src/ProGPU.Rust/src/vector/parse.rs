//! Original managed SVG/WPF path grammar. Tokens borrow the source rather than
//! allocating one string per number; a failed parse never publishes a partial path.
use super::*;
use crate::text::unicode::{GeneralCategory, general_category};

/// Explicit parsing failure, with UTF-8 byte offsets into the input.
#[derive(Clone, Debug, PartialEq, Eq, thiserror::Error)]
pub enum PathParseError {
    /// Missing/invalid source number or missing command.
    #[error("invalid path data at byte {0}")]
    InvalidData(usize),
    /// Original unsupported command (not silently ignored).
    #[error("unsupported path command {0:?}")]
    UnsupportedCommand(char),
    /// The original close-command loop would make no progress on this input.
    #[error("numeric data after close command at byte {0}")]
    NonProgressingClose(usize),
    /// Storage could not be reserved.
    #[error("path allocation exceeds available capacity")]
    Capacity,
}
impl PathGeometry {
    /// Parse original absolute/relative M/L/H/V/Q/T/C/S/A/Z commands and F fill
    /// rules. Implicit repetitions, smooth-control history, move-only figures,
    /// adjacent signed/decimal numbers and the original permissive arc flags
    /// survive. Numeric repetition of Z rejects instead of looping forever.
    pub fn parse(source: &str) -> Result<Self, PathParseError> {
        let tokens = tokenize(source)?;
        let mut reader = Reader {
            tokens: &tokens,
            index: 0,
            end: source.len(),
        };
        let mut geometry = Self::default();
        let mut figure: Option<usize> = None;
        let mut current = [0.0; 2];
        let mut last_control = [0.0; 2];
        let mut last_command = '\0';
        while reader.index < tokens.len() {
            let before = reader.index;
            let token = tokens[reader.index];
            let previous = last_command;
            let initial = token
                .1
                .chars()
                .next()
                .ok_or(PathParseError::InvalidData(token.0))?;
            let command = if letter(initial) {
                reader.index += 1;
                last_command = initial;
                initial
            } else {
                if last_command == '\0' {
                    return Err(PathParseError::InvalidData(token.0));
                }
                last_command
            };
            let relative = general_category(command as u32) == GeneralCategory::LowercaseLetter;
            let absolute = |p| if relative { add(p, current) } else { p };
            let kind = match upper(command) {
                'F' => {
                    geometry.fill_rule = if reader.number()? == 0.0 {
                        FillRule::EvenOdd
                    } else {
                        FillRule::Nonzero
                    };
                    last_command = '\0';
                    continue;
                }
                'M' => {
                    let point = absolute(reader.point()?);
                    geometry
                        .figures
                        .try_reserve(1)
                        .map_err(|_| PathParseError::Capacity)?;
                    figure = Some(geometry.figures.len());
                    geometry.figures.push(PathFigure::new(point, false));
                    current = point;
                    last_command = if relative { 'l' } else { 'L' };
                    continue;
                }
                'L' => PathSegmentKind::Line(absolute(reader.point()?)),
                'H' => {
                    let x = reader.number()?;
                    PathSegmentKind::Line([if relative { x + current[0] } else { x }, current[1]])
                }
                'V' => {
                    let y = reader.number()?;
                    PathSegmentKind::Line([current[0], if relative { y + current[1] } else { y }])
                }
                'Q' => {
                    let control = absolute(reader.point()?);
                    let end = absolute(reader.point()?);
                    last_control = control;
                    PathSegmentKind::Quadratic([control, end])
                }
                'T' => {
                    let end = absolute(reader.point()?);
                    let control = if matches!(upper(previous), 'Q' | 'T') {
                        sub(mul(current, 2.0), last_control)
                    } else {
                        current
                    };
                    last_control = control;
                    PathSegmentKind::Quadratic([control, end])
                }
                'C' => {
                    let a = absolute(reader.point()?);
                    let b = absolute(reader.point()?);
                    let end = absolute(reader.point()?);
                    last_control = b;
                    PathSegmentKind::Cubic([a, b, end])
                }
                'S' => {
                    let b = absolute(reader.point()?);
                    let end = absolute(reader.point()?);
                    let a = if matches!(upper(previous), 'C' | 'S') {
                        sub(mul(current, 2.0), last_control)
                    } else {
                        current
                    };
                    last_control = b;
                    PathSegmentKind::Cubic([a, b, end])
                }
                'A' => {
                    let size = reader.point()?;
                    let rotation = reader.number()?;
                    let large = reader.number()? != 0.0;
                    let sweep = if reader.number()? != 0.0 {
                        SweepDirection::Clockwise
                    } else {
                        SweepDirection::Counterclockwise
                    };
                    PathSegmentKind::Arc(ArcSegment {
                        end: absolute(reader.point()?),
                        size,
                        rotation,
                        large,
                        sweep,
                    })
                }
                'Z' => {
                    if before == reader.index {
                        return Err(PathParseError::NonProgressingClose(token.0));
                    }
                    if let Some(index) = figure {
                        geometry.figures[index].is_closed = true;
                        current = geometry.figures[index].start;
                    }
                    continue;
                }
                _ => return Err(PathParseError::UnsupportedCommand(command)),
            };
            let index = match figure {
                Some(index) => index,
                None => {
                    geometry
                        .figures
                        .try_reserve(1)
                        .map_err(|_| PathParseError::Capacity)?;
                    let index = geometry.figures.len();
                    geometry.figures.push(PathFigure::new(current, false));
                    figure = Some(index);
                    index
                }
            };
            let segment = PathSegment::new(kind);
            let segments = &mut geometry.figures[index].segments;
            segments
                .try_reserve(1)
                .map_err(|_| PathParseError::Capacity)?;
            segments.push(segment);
            current = segment.end();
        }
        Ok(geometry)
    }
}
impl std::str::FromStr for PathGeometry {
    type Err = PathParseError;
    fn from_str(source: &str) -> Result<Self, Self::Err> {
        Self::parse(source)
    }
}
struct Reader<'a> {
    tokens: &'a [(usize, &'a str)],
    index: usize,
    end: usize,
}
impl Reader<'_> {
    fn number(&mut self) -> Result<f32, PathParseError> {
        let &(offset, value) = self
            .tokens
            .get(self.index)
            .ok_or(PathParseError::InvalidData(self.end))?;
        self.index += 1;
        value
            .parse()
            .map_err(|_| PathParseError::InvalidData(offset))
    }
    fn point(&mut self) -> Result<[f32; 2], PathParseError> {
        Ok([self.number()?, self.number()?])
    }
}
fn letter(c: char) -> bool {
    if c.is_ascii() {
        return c.is_ascii_alphabetic();
    }
    // Source char APIs operate on UTF-16 code units, not supplementary scalars.
    (c as u32) <= 0xffff
        && matches!(
            general_category(c as u32),
            GeneralCategory::UppercaseLetter
                | GeneralCategory::LowercaseLetter
                | GeneralCategory::TitlecaseLetter
                | GeneralCategory::ModifierLetter
                | GeneralCategory::OtherLetter
        )
}
fn digit(c: char) -> bool {
    if c.is_ascii() {
        return c.is_ascii_digit();
    }
    (c as u32) <= 0xffff && general_category(c as u32) == GeneralCategory::DecimalDigitNumber
}
fn upper(c: char) -> char {
    // The source uses simple one-character casing. In particular U+017F is a
    // relative S command; multi-character full-case expansions are not commands.
    let mut upper = c.to_uppercase();
    let first = upper.next().unwrap_or(c);
    if upper.next().is_none() { first } else { c }
}
fn tokenize(source: &str) -> Result<Vec<(usize, &str)>, PathParseError> {
    let mut tokens = Vec::new();
    let mut index = 0;
    while index < source.len() {
        let next = |index: usize| source.get(index..).and_then(|s| s.chars().next());
        let c = next(index).ok_or(PathParseError::InvalidData(index))?;
        if c.is_whitespace() || c == ',' {
            index += c.len_utf8();
            continue;
        }
        let start = index;
        if letter(c) {
            index += c.len_utf8();
        } else {
            if matches!(c, '-' | '+') {
                index += 1;
            }
            while let Some(c) = next(index).filter(|c| digit(*c)) {
                index += c.len_utf8();
            }
            if next(index) == Some('.') {
                index += 1;
                while let Some(c) = next(index).filter(|c| digit(*c)) {
                    index += c.len_utf8();
                }
            }
            if matches!(next(index), Some('e' | 'E')) {
                let exponent = index;
                index += 1;
                if matches!(next(index), Some('-' | '+')) {
                    index += 1;
                }
                let digits = index;
                while let Some(c) = next(index).filter(|c| digit(*c)) {
                    index += c.len_utf8();
                }
                if digits == index {
                    index = exponent;
                }
            }
            if index == start || (index == start + 1 && matches!(c, '-' | '+')) {
                return Err(PathParseError::InvalidData(start));
            }
        }
        tokens
            .try_reserve(1)
            .map_err(|_| PathParseError::Capacity)?;
        tokens.push((start, &source[start..index]));
    }
    Ok(tokens)
}
