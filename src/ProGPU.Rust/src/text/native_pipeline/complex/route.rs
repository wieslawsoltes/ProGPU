//! Original native GSUB-script probing, script-generation and language policy.
use super::*;
/// Complete original route observation, including independent joining policy.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeShapingRoute {
    /// Effective source script after original hira/laoo aliases.
    pub unicode_script: Tag,
    /// Actual selected layout script, including second/third generation tags.
    pub layout_script: Tag,
    /// Resolved original direction.
    pub direction: GposDirection,
    /// Original complex-stage selector.
    pub complex: NativeComplexKind,
    /// Independent original joining-stage admission.
    pub arabic_joining: bool,
    /// Original Hebrew composition admission from font feature presence.
    pub compose_hebrew: bool,
}
impl NativeShapingRoute {
    /// Original feature policy derived from the same route generation.
    pub fn feature_route(self) -> NativeFeatureRoute {
        NativeFeatureRoute {
            layout_script: self.layout_script,
            direction: self.direction,
            use_shaper: self.complex == NativeComplexKind::Use,
            indic_shaper: self.complex == NativeComplexKind::Indic,
            arabic_shaper: self.arabic_joining,
        }
    }
}
fn has(font: &SfntFace, table: Tag, offset: usize, requested: Tag) -> bool {
    let Some(table) = font.table(table) else {
        return false;
    };
    let b = table.bytes;
    let Some(bytes) = b.get(offset..offset + 2) else {
        return false;
    };
    let list = usize::from(u16::from_be_bytes(bytes.try_into().unwrap()));
    let Some(bytes) = b.get(list..list + 2) else {
        return false;
    };
    let count = usize::from(u16::from_be_bytes(bytes.try_into().unwrap()));
    for i in 0..count {
        let record = list + 2 + i * 6;
        let Some(bytes) = b.get(record..record + 6) else {
            break;
        };
        if u32::from_be_bytes(bytes[..4].try_into().unwrap()) == requested.0 {
            return true;
        }
    }
    false
}
const INDIC: &[[u8; 4]] = &[
    *b"beng", *b"deva", *b"gujr", *b"guru", *b"knda", *b"mlym", *b"orya", *b"taml", *b"telu",
];
const GENERATIONS: &[[u8; 4]] = &[
    *b"bng2", *b"dev2", *b"gjr2", *b"gur2", *b"knd2", *b"mlm2", *b"ory2", *b"tml2", *b"tel2",
];
const USE: &[[u8; 4]] = &[
    *b"bng3", *b"dev3", *b"gjr3", *b"gur3", *b"knd3", *b"mlm3", *b"ory3", *b"tml3", *b"tel3",
    *b"tibt", *b"mong", *b"sinh", *b"java", *b"marc", *b"limb", *b"tale", *b"bugi", *b"khar",
    *b"sylo", *b"tfng", *b"bali", *b"nkoo", *b"phag", *b"cham", *b"kali", *b"lepc", *b"rjng",
    *b"saur", *b"sund", *b"egyp", *b"kthi", *b"mtei", *b"lana", *b"tavt", *b"batk", *b"brah",
    *b"mand", *b"cakm", *b"plrd", *b"shrd", *b"takr", *b"dupl", *b"gran", *b"khoj", *b"sind",
    *b"mahj", *b"mani", *b"modi", *b"hmng", *b"phlp", *b"sidd", *b"tirh", *b"ahom", *b"mult",
    *b"adlm", *b"bhks", *b"newa", *b"gonm", *b"soyo", *b"zanb", *b"dogr", *b"gong", *b"rohg",
    *b"maka", *b"medf", *b"sogo", *b"sogd", *b"elym", *b"nand", *b"hmnp", *b"wcho", *b"chrs",
    *b"diak", *b"kits", *b"yezi", *b"cpmn", *b"ougr", *b"tnsa", *b"toto", *b"vith", *b"kawi",
    *b"nagm",
];
pub(super) fn joining(script: Tag) -> bool {
    [
        *b"adlm", *b"arab", *b"chrs", *b"rohg", *b"mand", *b"mani", *b"mong", *b"nkoo", *b"ougr",
        *b"phag", *b"phlp", *b"sogd", *b"syrc",
    ]
    .contains(&script.bytes())
}
/// Resolve original native script/font feature policy without validating an
/// unrelated layout table. Malformed raw probes retain source stop/absence rules.
pub fn resolve_native_shaping_route(
    font: &SfntFace,
    mut script: Tag,
    requested: GposDirection,
) -> NativeShapingRoute {
    if script == Tag::new(*b"hira") {
        script = Tag::new(*b"kana");
    } else if script == Tag::new(*b"laoo") {
        script = Tag::new(*b"lao ");
    }
    let mut layout = script;
    let mut use_shaper = false;
    let generations = INDIC
        .iter()
        .position(|p| *p == script.bytes())
        .map(|i| GENERATIONS[i]);
    let third = generations.map(|mut b| {
        b[3] = b'3';
        Tag::new(b)
    });
    let second = generations
        .map(Tag::new)
        .or_else(|| (script == Tag::new(*b"mymr")).then_some(Tag::new(*b"mym2")));
    if let Some(t) = third.filter(|&t| has(font, GSUB, 4, t)) {
        layout = t;
        use_shaper = true;
    } else if let Some(t) = second.filter(|&t| has(font, GSUB, 4, t)) {
        layout = t;
    } else {
        let lower = script.bytes().map(|c| c.to_ascii_lowercase());
        use_shaper = USE.contains(&lower);
    }
    let complex = if use_shaper {
        NativeComplexKind::Use
    } else if INDIC.contains(&script.bytes()) {
        NativeComplexKind::Indic
    } else if layout == Tag::new(*b"khmr") {
        NativeComplexKind::Khmer
    } else if script == Tag::new(*b"mymr") {
        NativeComplexKind::Myanmar
    } else {
        NativeComplexKind::None
    };
    let direction = if requested != GposDirection::Unspecified {
        requested
    } else if [
        *b"arab", *b"hebr", *b"syrc", *b"thaa", *b"nkoo", *b"adlm", *b"rohg",
    ]
    .contains(&script.bytes())
    {
        GposDirection::RightToLeft
    } else {
        GposDirection::LeftToRight
    };
    NativeShapingRoute {
        unicode_script: script,
        layout_script: layout,
        direction,
        complex,
        arabic_joining: joining(script),
        compose_hebrew: script != Tag::new(*b"hebr")
            || !(has(font, GSUB, 6, Tag::new(*b"mark")) || has(font, GPOS, 6, Tag::new(*b"mark"))),
    }
}
/// Original ASCII-insensitive language matching, underscore/hyphen equivalence
/// and exact known aliases. Unknown names preserve the `dflt` tag.
pub fn resolve_native_language_tag(language: &str) -> Tag {
    let matches = |value: &str| {
        language.len() == value.len()
            && language.bytes().zip(value.bytes()).all(|(a, b)| {
                (if a == b'_' {
                    b'-'
                } else {
                    a.to_ascii_lowercase()
                }) == b
            })
    };
    for (aliases, value) in [
        (&["az", "az-latn"][..], *b"AZE "),
        (&["de"][..], *b"DEU "),
        (&["dv"][..], *b"DHV "),
        (&["fa"][..], *b"FAR "),
        (&["ja"][..], *b"JAN "),
        (&["nl"][..], *b"NLD "),
        (&["pl"][..], *b"PLK "),
        (&["ro"][..], *b"ROM "),
        (&["tr"][..], *b"TRK "),
        (&["zh", "zh-cn", "zh-sg", "zh-hans"][..], *b"ZHS "),
        (&["zh-tw", "zh-hant"][..], *b"ZHT "),
        (
            &["zh-hk", "zh-mo", "zh-hant-hk", "zh-hant-mo"][..],
            *b"ZHH ",
        ),
    ] {
        if aliases.iter().any(|value| matches(value)) {
            return Tag::new(value);
        }
    }
    Tag::new(*b"dflt")
}
