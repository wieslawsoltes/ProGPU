//! Exact source response transport shared by native tests and browser AOT.
use progpu::{
    font::{ManagedFont, layout::ManagedGlyphBuffer},
    text::{
        preparation::*,
        unicode::{
            ManagedMarkProfile, grapheme::ManagedGraphemeProfile,
            normalization::ManagedNormalizationProfile,
        },
    },
};
/// Independent profile witnesses executed before benchmark/whole-source comparison.
#[allow(dead_code)]
pub fn normalization_witness(profile: ManagedNormalizationProfile) -> Vec<u32> {
    use progpu::text::unicode::{
        DecodedText,
        normalization::{NormalizationForm, NormalizedText},
    };
    let mut out = Vec::new();
    for text in [
        "\u{105c9}",
        "\u{105d2}\u{307}",
        "a\u{897}\u{334}",
        "a\u{1acf}\u{334}",
        "a\u{301}\u{1acf}",
        "\u{fffe}",
    ] {
        for form in [
            NormalizationForm::CanonicalDecomposition,
            NormalizationForm::CanonicalComposition,
        ] {
            match NormalizedText::with_managed_profile(
                DecodedText::from_utf8(text.as_bytes().to_vec()).unwrap(),
                profile,
                form,
            ) {
                Err(_) => out.push(u32::MAX),
                Ok(text) => {
                    let values: Vec<_> = text
                        .scalars()
                        .iter()
                        .flat_map(|s| {
                            char::from_u32(s.code_point)
                                .unwrap()
                                .encode_utf16(&mut [0; 2])
                                .to_vec()
                        })
                        .collect();
                    units(&mut out, &values);
                }
            }
        }
    }
    out
}
#[path = "managed_glyph_fixture.rs"]
#[allow(dead_code)]
pub(crate) mod records;
#[derive(Clone, Debug)]
pub struct Case {
    pub font: usize,
    pub script: String,
    pub level: u8,
    pub flags: u8,
    pub text: Vec<u16>,
    pub pre: Vec<u16>,
    pub post: Vec<u16>,
}
pub fn units(out: &mut Vec<u32>, values: &[u16]) {
    out.push(values.len() as u32);
    out.extend(values.iter().copied().map(u32::from));
}
impl Case {
    pub fn request(&self) -> Vec<u32> {
        let mut out = Vec::new();
        units(&mut out, &self.script.encode_utf16().collect::<Vec<_>>());
        out.extend([self.level as u32, self.flags as u32]);
        units(&mut out, &self.text);
        units(&mut out, &self.pre);
        units(&mut out, &self.post);
        out
    }
    #[allow(dead_code)] // Native oracle emits requests; WASM consumes this same transport.
    pub fn from_request(font: usize, request: &[u32]) -> Option<Self> {
        let mut words = request.iter().copied();
        fn read(words: &mut impl Iterator<Item = u32>) -> Option<Vec<u16>> {
            let count = words.next()? as usize;
            (0..count)
                .map(|_| u16::try_from(words.next()?).ok())
                .collect()
        }
        let script = String::from_utf16(&read(&mut words)?).ok()?;
        let level = u8::try_from(words.next()?).ok()?;
        let flags = u8::try_from(words.next()?).ok()?;
        let (text, pre, post) = (read(&mut words)?, read(&mut words)?, read(&mut words)?);
        if words.next().is_some() {
            return None;
        }
        Some(Self {
            font,
            script,
            level,
            flags,
            text,
            pre,
            post,
        })
    }
}
pub fn execute(
    font: Option<&ManagedFont>,
    case: &Case,
    profile: ManagedNormalizationProfile,
    out: &mut Vec<u32>,
) {
    out.clear();
    let Some(font) = font else {
        out.push(1);
        return;
    };
    let selection = resolve_managed_layout_script(font, &case.script);
    out.extend([0, u32::from(selection.use_mark_order)]);
    units(
        out,
        &selection.layout_script.encode_utf16().collect::<Vec<_>>(),
    );
    let mut options = ManagedPreparationOptions::new(
        profile,
        ManagedMarkProfile::Unicode16_0,
        ManagedGraphemeProfile::Unicode16_0,
    );
    let Ok(level) = ManagedClusterLevel::try_from(case.level) else {
        out.push(2);
        return;
    };
    options.cluster_level = level;
    options.buffer_flags = ManagedBufferFlags::from_bits(case.flags);
    options.pre_context = case.pre.clone().into();
    options.post_context = case.post.clone().into();
    match ManagedGlyphBuffer::from_text(
        case.text.clone(),
        font.clone(),
        case.script.clone(),
        options,
    ) {
        Err(error) => out.push(match error {
            PreparationError::GlyphLimit => 3,
            PreparationError::Font(_) => 4,
            _ => 2,
        }),
        Ok(buffer) => {
            let source = buffer.preparation().unwrap();
            let options = source.options();
            let state = buffer.state();
            out.extend([
                0,
                buffer.glyphs().len() as u32,
                u32::from(source.font() == font),
                options.cluster_level as u32,
                u32::from(options.buffer_flags.bits()),
            ]);
            units(out, &options.pre_context);
            units(out, &options.post_context);
            out.extend([
                state.context_match_end as u32,
                state.random_state,
                buffer.mark_filtering_coverage() as u32,
                buffer.nested_lookup_depth(),
                u32::from(state.traversal.manual_joiners),
                u32::from(state.traversal.restrict_to_syllable),
                u32::from(state.traversal.lookup_syllable),
                u32::from(buffer.has_fraction_actions()),
            ]);
            for &glyph in buffer.glyphs() {
                out.extend(records::words(glyph));
            }
        }
    }
}
