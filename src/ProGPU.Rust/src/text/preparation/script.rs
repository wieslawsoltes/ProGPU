use super::*;
/// Original raw GSUB script selection, distinct from validated lookup-plan creation.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ManagedScriptSelection<'a> {
    /// Actual original input or selected second/third-generation layout tag.
    pub layout_script: &'a str,
    /// Original USE mark-order preparation policy.
    pub use_mark_order: bool,
}
/// Inspect only the source's visited GSUB script records. Malformed unvisited
/// script bodies, lookup lists and unrelated tables do not change this decision.
/// O(S) records, O(1) scratch; no allocation or script-string normalization.
pub fn resolve_managed_layout_script<'a>(
    font: &ManagedFont,
    script: &'a str,
) -> ManagedScriptSelection<'a> {
    let tags = match script {
        "beng" => Some(("bng3", "bng2")),
        "deva" => Some(("dev3", "dev2")),
        "gujr" => Some(("gjr3", "gjr2")),
        "guru" => Some(("gur3", "gur2")),
        "knda" => Some(("knd3", "knd2")),
        "mlym" => Some(("mlm3", "mlm2")),
        "orya" => Some(("ory3", "ory2")),
        "taml" => Some(("tml3", "tml2")),
        "telu" => Some(("tel3", "tel2")),
        "mymr" => Some(("", "mym2")),
        _ => None,
    };
    if let Some((third, second)) = tags {
        if !third.is_empty() && has_script(font, third) {
            return ManagedScriptSelection {
                layout_script: third,
                use_mark_order: true,
            };
        }
        if has_script(font, second) {
            return ManagedScriptSelection {
                layout_script: second,
                use_mark_order: false,
            };
        }
    }
    ManagedScriptSelection {
        layout_script: script,
        use_mark_order: matches!(
            script,
            "tibt"
                | "mong"
                | "sinh"
                | "java"
                | "marc"
                | "limb"
                | "tale"
                | "bugi"
                | "khar"
                | "sylo"
                | "tfng"
                | "bali"
                | "nkoo"
                | "phag"
                | "cham"
                | "kali"
                | "lepc"
                | "rjng"
                | "saur"
                | "sund"
                | "egyp"
                | "kthi"
                | "mtei"
                | "lana"
                | "tavt"
                | "batk"
                | "brah"
                | "mand"
                | "cakm"
                | "plrd"
                | "shrd"
                | "takr"
                | "dupl"
                | "gran"
                | "khoj"
                | "sind"
                | "mahj"
                | "mani"
                | "modi"
                | "hmng"
                | "phlp"
                | "sidd"
                | "tirh"
                | "ahom"
                | "mult"
                | "adlm"
                | "bhks"
                | "newa"
                | "gonm"
                | "soyo"
                | "zanb"
                | "dogr"
                | "gong"
                | "rohg"
                | "maka"
                | "medf"
                | "sogo"
                | "sogd"
                | "elym"
                | "nand"
                | "hmnp"
                | "wcho"
                | "chrs"
                | "diak"
                | "kits"
                | "yezi"
                | "cpmn"
                | "ougr"
                | "tnsa"
                | "toto"
                | "vith"
                | "kawi"
                | "nagm"
        ),
    }
}
fn has_script(font: &ManagedFont, tag: &str) -> bool {
    let Some(table) = font.source().table(Tag::new(*b"GSUB")) else {
        return false;
    };
    let data = table.bytes;
    let read = |offset: usize| {
        data.get(offset..offset + 2)
            .map(|v| u16::from_be_bytes([v[0], v[1]]) as usize)
    };
    let Some(start) = read(4) else {
        return false;
    };
    let Some(count) = read(start) else {
        return false;
    };
    for index in 0..count {
        let offset = start + 2 + index * 6;
        let Some(record) = data.get(offset..offset + 6) else {
            break;
        };
        if record[..4] == *tag.as_bytes() {
            return true;
        }
    }
    false
}
pub(super) fn indic(script: &str) -> bool {
    matches!(
        script,
        "beng" | "deva" | "gujr" | "guru" | "knda" | "mlym" | "orya" | "taml" | "telu"
    )
}
pub(super) fn virama(script: &str) -> u32 {
    match script {
        "deva" => 0x94d,
        "beng" => 0x9cd,
        "guru" => 0xa4d,
        "gujr" => 0xacd,
        "orya" => 0xb4d,
        "taml" => 0xbcd,
        "telu" => 0xc4d,
        "knda" => 0xccd,
        "mlym" => 0xd4d,
        _ => 0,
    }
}
