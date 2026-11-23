//! Original ordered family preferences; these names are not provider capabilities.
use super::{FontPlatform, name_key};
use std::collections::HashSet;
const AR: &[&str] = &[
    "Geeza Pro",
    "Noto Naskh Arabic",
    "Noto Sans Arabic",
    "Segoe UI",
    "Traditional Arabic",
    "Arabic Typesetting",
    "Tahoma",
    "Arial",
    "DejaVu Sans",
];
const JA: &[&str] = &[
    "Hiragino Sans",
    "Hiragino Kaku Gothic ProN",
    "Yu Gothic",
    "Meiryo",
    "Noto Sans CJK JP",
    "Noto Sans JP",
    ".Aqua Kana",
];
const KO: &[&str] = &[
    "Apple SD Gothic Neo",
    "AppleGothic",
    "Malgun Gothic",
    "Noto Sans CJK KR",
    "Noto Sans KR",
];
const ZH_HANT: &[&str] = &[
    "PingFang TC",
    "Heiti TC",
    "Noto Sans CJK TC",
    "Noto Sans TC",
    "Microsoft JhengHei",
    "Songti TC",
];
const ZH_HANS: &[&str] = &[
    "PingFang SC",
    "Hiragino Sans GB",
    "Heiti SC",
    "Noto Sans CJK SC",
    "Noto Sans SC",
    "Microsoft YaHei",
    "SimHei",
    "Songti SC",
    "Noto Sans CJK JP",
    "Noto Sans JP",
];
fn language(tag: &str) -> &'static [&'static str] {
    let tag = tag.trim().replace('_', "-").to_lowercase();
    match tag.split('-').next().unwrap_or("") {
        "ar" => AR,
        "ja" => JA,
        "ko" => KO,
        "zh" if tag.contains("-hant")
            || ["-tw", "-hk", "-mo"]
                .iter()
                .any(|suffix| tag.ends_with(suffix)) =>
        {
            ZH_HANT
        }
        "zh" => ZH_HANS,
        _ => &[],
    }
}
/// Language preferences precede the original single scalar-range family branch.
pub fn fallback_family_preferences(
    language_tags: Option<&[&str]>,
    codepoint: i32,
) -> Vec<&'static str> {
    let mut output = Vec::new();
    let mut seen = HashSet::new();
    let mut add = |values: &'static [&'static str]| {
        for &name in values {
            if seen.insert(name_key(name)) {
                output.push(name);
            }
        }
    };
    for tag in language_tags.unwrap_or(&[]) {
        add(language(tag));
    }
    match codepoint {
        0x0600..=0x06ff
        | 0x0750..=0x077f
        | 0x0870..=0x089f
        | 0x08a0..=0x08ff
        | 0xfb50..=0xfdff
        | 0xfe70..=0xfeff
        | 0x1ee00..=0x1eeff => add(AR),
        0x0590..=0x05ff | 0xfb1d..=0xfb4f => add(&[
            "Arial Hebrew",
            "Lucida Grande",
            "Noto Sans Hebrew",
            "Segoe UI",
            "Arial",
            "DejaVu Sans",
        ]),
        0x1f000..=0x1faff => add(&[
            "Apple Color Emoji",
            "Segoe UI Emoji",
            "Noto Color Emoji",
            "Noto Emoji",
            "Segoe UI Symbol",
        ]),
        0x2000..=0x2bff => add(&[
            "Apple Symbols",
            "Zapf Dingbats",
            "Segoe UI Symbol",
            "Noto Sans Symbols 2",
            "Noto Sans Symbols",
            "Apple Color Emoji",
            "Segoe UI Emoji",
            "Noto Color Emoji",
            "Symbola",
            "DejaVu Sans",
        ]),
        0x3040..=0x30ff => add(JA),
        0xac00..=0xd7af | 0x1100..=0x11ff => add(KO),
        0x3400..=0x9fff | 0xf900..=0xfaff | 0x20000..=0x323af => add(ZH_HANS),
        0x0020..=0x024f | 0x0370..=0x052f | 0x1e00..=0x1fff | 0x2de0..=0x2dff | 0xa640..=0xa69f => {
            add(&[
                "Helvetica",
                "Arial",
                "Segoe UI",
                "Noto Sans",
                "DejaVu Sans",
                "Liberation Sans",
            ])
        }
        _ => {}
    }
    output
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GenericFontFamily {
    SansSerif,
    Serif,
    Monospace,
}
impl GenericFontFamily {
    pub fn parse(name: &str) -> Option<Self> {
        match name.trim().to_ascii_lowercase().as_str() {
            "sans-serif" | "system-ui" | "ui-sans-serif" => Some(Self::SansSerif),
            "serif" | "ui-serif" => Some(Self::Serif),
            "monospace" | "ui-monospace" => Some(Self::Monospace),
            _ => None,
        }
    }
    pub fn preferences(self, platform: FontPlatform) -> &'static [&'static str] {
        match (platform, self) {
            (FontPlatform::MacOs, Self::SansSerif) => &["Helvetica", "Arial"],
            (FontPlatform::MacOs, Self::Serif) => &["Times", "Times New Roman"],
            (FontPlatform::MacOs, Self::Monospace) => &["Menlo", "Monaco", "Courier"],
            (FontPlatform::Windows, Self::SansSerif) => &["Segoe UI", "Arial"],
            (FontPlatform::Windows, Self::Serif) => &["Times New Roman", "Georgia"],
            (FontPlatform::Windows, Self::Monospace) => &["Consolas", "Courier New"],
            (_, Self::SansSerif) => &["Noto Sans", "DejaVu Sans", "Liberation Sans", "Arial"],
            (_, Self::Serif) => &["Noto Serif", "DejaVu Serif", "Liberation Serif"],
            (_, Self::Monospace) => &["Noto Sans Mono", "DejaVu Sans Mono", "Liberation Mono"],
        }
    }
}
