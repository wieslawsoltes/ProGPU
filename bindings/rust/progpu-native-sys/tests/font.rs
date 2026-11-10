//! The font stack of the engine through its C ABI (no device): the system catalog, faces of the
//! catalog and from bytes, tables, metrics, names, style variations, unhinted outlines, and the
//! OpenType shaper over the bytes of a face.

use progpu_native_sys::font::*;
use progpu_native_sys::progpu::*;
use std::ptr;

struct Catalog(*mut progpu_native_font_catalog);

impl Drop for Catalog {
    fn drop(&mut self) {
        // SAFETY: the catalog of this guard, destroyed once.
        unsafe { progpu_native_font_catalog_destroy(self.0) }
    }
}

struct Face(*mut progpu_native_font_face);

impl Drop for Face {
    fn drop(&mut self) {
        // SAFETY: the face of this guard, destroyed once.
        unsafe { progpu_native_font_face_destroy(self.0) }
    }
}

fn style(weight: i32, slant: u32) -> progpu_native_font_style {
    progpu_native_font_style { weight, width: 5, slant, optical_size_fixed: 0 }
}

fn system_catalog() -> Catalog {
    // SAFETY: a plain creation; the result is checked.
    let catalog = unsafe { progpu_native_font_catalog_create_system() };
    assert!(!catalog.is_null(), "the system fonts are discovered");
    Catalog(catalog)
}

fn info(catalog: &Catalog, index: u32) -> (String, String, String, progpu_native_font_face_info) {
    // SAFETY: the record is plain data, written by the call; its strings live as long as the catalog.
    unsafe {
        let mut info: progpu_native_font_face_info = core::mem::zeroed();
        assert_ne!(progpu_native_font_catalog_get_face_info(catalog.0, index, &mut info), 0);
        let text = |data: *const core::ffi::c_char, size: usize| {
            String::from_utf8_lossy(core::slice::from_raw_parts(data as *const u8, size)).into_owned()
        };
        (
            text(info.family_name, info.family_name_size),
            text(info.full_name, info.full_name_size),
            text(info.file_path, info.file_path_size),
            info,
        )
    }
}

fn metrics(face: &Face) -> progpu_native_font_face_metrics {
    // SAFETY: plain data, written by the call.
    unsafe {
        let mut metrics: progpu_native_font_face_metrics = core::mem::zeroed();
        assert_ne!(progpu_native_font_face_get_metrics(face.0, &mut metrics), 0);
        metrics
    }
}

fn glyph(face: &Face, character: char) -> u16 {
    let mut glyph = 0u16;
    // SAFETY: the glyph is written by the call.
    assert_ne!(unsafe { progpu_native_font_face_get_glyph_index(face.0, character as u32, &mut glyph) }, 0);
    glyph
}

fn outline(face: &Face, glyph: u16, coordinates: &[i16]) -> (Vec<progpu_native_path_segment>, progpu_native_font_glyph_bounds) {
    // SAFETY: the segments are borrowed from this thread's storage and copied before the next call.
    unsafe {
        let mut segments = ptr::null();
        let mut count = 0usize;
        let mut bounds: progpu_native_font_glyph_bounds = core::mem::zeroed();
        assert_ne!(
            progpu_native_font_face_get_outline(face.0, glyph, coordinates.as_ptr(), coordinates.len(), &mut segments, &mut count, &mut bounds),
            0
        );
        let segments = if count == 0 { Vec::new() } else { core::slice::from_raw_parts(segments, count).to_vec() };
        (segments, bounds)
    }
}

fn family_face(catalog: &Catalog, family: &str, weight: i32) -> Option<(u32, Face)> {
    let mut index = 0u32;
    let request = style(weight, PROGPU_NATIVE_FONT_SLANT_NORMAL);
    // SAFETY: the family is borrowed for the call; the index is written by it.
    if unsafe { progpu_native_font_catalog_match_family(catalog.0, family.as_ptr().cast(), family.len(), &request, &mut index) } == 0 {
        return None;
    }
    // SAFETY: an index of the catalog; the result is checked.
    let face = unsafe { progpu_native_font_catalog_load_face(catalog.0, index) };
    assert!(!face.is_null(), "the matched face loads");
    Some((index, Face(face)))
}

/// The family the tests draw with: one every platform's catalog is expected to have some of.
fn text_family(catalog: &Catalog) -> (String, u32, Face) {
    for family in ["Helvetica", "Arial", "Segoe UI", "DejaVu Sans", "Liberation Sans", "Noto Sans"] {
        if let Some((index, face)) = family_face(catalog, family, 400) {
            return (family.to_string(), index, face);
        }
    }
    let (family, _, _, _) = info(catalog, 0);
    let (index, face) = family_face(catalog, &family, 400).expect("the family of the first face matches");
    (family, index, face)
}

#[test]
fn the_catalog_matches_families_and_loads_faces_with_metrics_tables_and_names() {
    let catalog = system_catalog();
    // SAFETY: a plain query.
    let count = unsafe { progpu_native_font_catalog_face_count(catalog.0) };
    assert!(count > 0, "the system has fonts");
    let (family, index, face) = text_family(&catalog);
    let (catalog_family, full_name, path, record) = info(&catalog, index);
    assert!(catalog_family.eq_ignore_ascii_case(&family), "{catalog_family} is the family asked for ({family})");
    assert!(!full_name.is_empty() && !path.is_empty());
    assert_ne!(record.identity, 0);

    let metrics = metrics(&face);
    assert!(metrics.units_per_em >= 16 && metrics.glyph_count > 0);
    assert!(metrics.ascender > 0 && metrics.descender < 0);
    assert_ne!(metrics.outline_format, PROGPU_NATIVE_FONT_OUTLINE_NONE);

    // The head table is 54 bytes and carries the units per em at offset 18.
    // SAFETY: the table is borrowed from the face, which outlives the slice.
    unsafe {
        let mut data = ptr::null();
        let mut size = 0usize;
        assert_ne!(progpu_native_font_face_get_table(face.0, u32::from_be_bytes(*b"head"), &mut data, &mut size), 0);
        assert!(size >= 54);
        let head = core::slice::from_raw_parts(data as *const u8, size);
        assert_eq!(u16::from_be_bytes([head[18], head[19]]), metrics.units_per_em);
        assert_eq!(progpu_native_font_face_get_table(face.0, u32::from_be_bytes(*b"zzzz"), &mut data, &mut size), 0);

        let mut name = [0u8; 256];
        let mut written = 0usize;
        assert_ne!(progpu_native_font_face_get_name(face.0, 1, name.as_mut_ptr().cast(), name.len(), &mut written), 0);
        let name = core::str::from_utf8(&name[..written]).expect("a UTF-8 family name");
        assert!(name.eq_ignore_ascii_case(&catalog_family), "{name} is the catalog's family {catalog_family}");
        assert_eq!(progpu_native_font_face_identity(face.0), record.identity);
    }
}

#[test]
fn a_glyph_has_an_advance_and_an_unhinted_outline_in_design_units() {
    let catalog = system_catalog();
    let (_, _, face) = text_family(&catalog);
    let metrics = metrics(&face);
    let em = f32::from(metrics.units_per_em);
    let letter = glyph(&face, 'H');
    let mut advance = 0.0f32;
    // SAFETY: the advance is written by the call.
    assert_ne!(unsafe { progpu_native_font_face_get_advance(face.0, letter, ptr::null(), 0, &mut advance) }, 0);
    assert!(advance > 0.3 * em && advance < 1.2 * em, "the advance of H is {advance} of {em}");

    let (segments, bounds) = outline(&face, letter, &[]);
    // An H is twelve lines, or more when the font draws it with curves.
    assert!(segments.len() >= 12, "H has {} segments", segments.len());
    assert!(bounds.x_min >= -0.1 * em && bounds.x_max <= advance + 0.1 * em, "{bounds:?} is within the advance");
    // The capital stands on the baseline and is about the cap height tall (Y up).
    assert!(bounds.y_min.abs() < 0.05 * em && bounds.y_max > 0.5 * em && bounds.y_max < em, "{bounds:?}");
    for segment in &segments {
        assert!(segment.kind <= PROGPU_NATIVE_PATH_SEGMENT_CUBIC as u32);
        assert!(segment.p0.x.is_finite() && segment.p0.y.is_finite());
    }
    // A closed outline: every segment starts where one ends.
    for segment in &segments {
        let start = segment.p0;
        let closes = segments.iter().any(|other| {
            let end = match other.kind {
                kind if kind == PROGPU_NATIVE_PATH_SEGMENT_LINE as u32 => other.p1,
                kind if kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC as u32 => other.p2,
                _ => other.p3,
            };
            (end.x - start.x).abs() < 0.01 && (end.y - start.y).abs() < 0.01
        });
        assert!(closes, "the segment at ({}, {}) continues a contour", start.x, start.y);
    }

    // A space has an advance and no outline.
    let space = glyph(&face, ' ');
    let (segments, _) = outline(&face, space, &[]);
    assert!(segments.is_empty());
    // A glyph the face does not have fails.
    let mut none = ptr::null();
    let mut count = 0usize;
    // SAFETY: the outputs are written by the call.
    assert_eq!(
        unsafe { progpu_native_font_face_get_outline(face.0, metrics.glyph_count, ptr::null(), 0, &mut none, &mut count, ptr::null_mut()) },
        0
    );
}

#[test]
fn an_outline_grows_by_the_side_it_faces() {
    let catalog = system_catalog();
    let (_, _, face) = text_family(&catalog);
    let units = f32::from(metrics(&face).units_per_em);
    let step = units / 100.0;
    // Sideways by one step, upwards by two, not at all downwards.
    let growth = progpu_native_font_outline_growth { left: step, bottom: 0.0, right: step, top: 2.0 * step };
    let end_of = |segment: &progpu_native_path_segment| match segment.kind {
        kind if kind == PROGPU_NATIVE_PATH_SEGMENT_LINE as u32 => segment.p1,
        kind if kind == PROGPU_NATIVE_PATH_SEGMENT_QUADRATIC as u32 => segment.p2,
        _ => segment.p3,
    };
    for character in ['l', 'o', 'H', 'e', '8', 'x'] {
        let (plain, plain_bounds) = outline(&face, glyph(&face, character), &[]);
        // SAFETY: the source is owned by this test; the grown segments are borrowed from the
        // calling thread and copied before its next call.
        let (heavy, heavy_bounds) = unsafe {
            let mut bounds: progpu_native_font_glyph_bounds = core::mem::zeroed();
            let mut segments = ptr::null();
            let mut count = 0usize;
            assert_ne!(progpu_native_font_outline_embolden(plain.as_ptr(), plain.len(), &growth, &mut segments, &mut count, &mut bounds), 0);
            (core::slice::from_raw_parts(segments, count).to_vec(), bounds)
        };
        for (grown, expected, name) in [
            (plain_bounds.x_min - heavy_bounds.x_min, step, "left"),
            (plain_bounds.y_min - heavy_bounds.y_min, 0.0, "bottom"),
            (heavy_bounds.x_max - plain_bounds.x_max, step, "right"),
            (heavy_bounds.y_max - plain_bounds.y_max, 2.0 * step, "top"),
        ] {
            assert!((grown - expected).abs() < step * 0.05, "{character}: the {name} side grew by {grown}, expected {expected}");
        }
        // Curves are cut and joints closed, so there are more segments, and every contour of
        // the plain outline is still one closed contour.
        assert!(heavy.len() >= plain.len(), "{character}: {} segments from {}", heavy.len(), plain.len());
        let contours = |segments: &[progpu_native_path_segment]| {
            let mut starts = vec![0usize];
            for index in 1..segments.len() {
                let end = end_of(&segments[index - 1]);
                if end.x != segments[index].p0.x || end.y != segments[index].p0.y {
                    starts.push(index);
                }
            }
            starts.push(segments.len());
            for pair in starts.windows(2) {
                let (first, last) = (&segments[pair[0]], &segments[pair[1] - 1]);
                let end = end_of(last);
                assert!(end.x == first.p0.x && end.y == first.p0.y, "{character}: an open contour");
            }
            starts.len() - 1
        };
        assert_eq!(contours(&heavy), contours(&plain), "{character}: the contours stay what they were");
    }
    // Nothing to grow: the source comes back. No segments: an empty result.
    let (plain, plain_bounds) = outline(&face, glyph(&face, 'o'), &[]);
    let none = progpu_native_font_outline_growth { left: 0.0, bottom: 0.0, right: 0.0, top: 0.0 };
    // SAFETY: as above.
    unsafe {
        let mut bounds: progpu_native_font_glyph_bounds = core::mem::zeroed();
        let mut segments = ptr::null();
        let mut count = 0usize;
        assert_ne!(progpu_native_font_outline_embolden(plain.as_ptr(), plain.len(), &none, &mut segments, &mut count, &mut bounds), 0);
        assert!(ptr::eq(segments, plain.as_ptr()) && count == plain.len());
        assert_eq!((bounds.x_min, bounds.y_max), (plain_bounds.x_min, plain_bounds.y_max));
        assert_ne!(progpu_native_font_outline_embolden(ptr::null(), 0, &growth, &mut segments, &mut count, &mut bounds), 0);
        assert_eq!((count, bounds.x_min, bounds.x_max), (0, 0.0, 0.0));
        assert_eq!(progpu_native_font_outline_embolden(ptr::null(), 3, &growth, &mut segments, &mut count, ptr::null_mut()), 0);
        assert_eq!(progpu_native_font_outline_embolden(plain.as_ptr(), plain.len(), ptr::null(), &mut segments, &mut count, ptr::null_mut()), 0);
    }
}

#[test]
fn a_face_from_bytes_answers_as_the_face_of_the_catalog() {
    let catalog = system_catalog();
    let (_, index, face) = text_family(&catalog);
    let (_, _, path, record) = info(&catalog, index);
    let bytes = std::fs::read(&path).expect("the font file of the catalog reads");
    // SAFETY: the bytes are borrowed for the calls (the face copies them).
    let from_bytes = unsafe {
        let mut faces = 0u32;
        assert_ne!(progpu_native_font_data_face_count(bytes.as_ptr().cast(), bytes.len(), &mut faces), 0);
        assert!(faces > record.face_index);
        let face = progpu_native_font_face_create(bytes.as_ptr().cast(), bytes.len(), record.face_index);
        assert!(!face.is_null());
        Face(face)
    };
    drop(bytes);
    assert_eq!(metrics(&from_bytes).units_per_em, metrics(&face).units_per_em);
    for character in ['a', 'g', 'Q', '8', '&'] {
        let glyph_index = glyph(&face, character);
        assert_eq!(glyph(&from_bytes, character), glyph_index);
        let (expected, _) = outline(&face, glyph_index, &[]);
        let (actual, _) = outline(&from_bytes, glyph_index, &[]);
        assert_eq!(expected.len(), actual.len());
        assert!(!expected.is_empty());
        for (left, right) in expected.iter().zip(&actual) {
            assert_eq!((left.p0.x, left.p0.y, left.kind), (right.p0.x, right.p0.y, right.kind));
        }
    }
    // SAFETY: the outputs are written by the call; the data is borrowed from the face.
    unsafe {
        let mut data = ptr::null();
        let mut size = 0usize;
        let mut face_index = 0u32;
        assert_ne!(progpu_native_font_face_get_data(from_bytes.0, &mut data, &mut size, &mut face_index), 0);
        assert!(size > 0 && face_index == record.face_index);
        assert_eq!(progpu_native_font_face_identity(from_bytes.0), 0);
        // Bytes that are no font give no face.
        let junk = [0u8; 64];
        assert!(progpu_native_font_face_create(junk.as_ptr().cast(), junk.len(), 0).is_null());
    }
}

#[test]
fn a_character_no_text_face_has_falls_back_to_a_face_that_has_it() {
    let catalog = system_catalog();
    let (family, index, _) = text_family(&catalog);
    let (_, _, _, record) = info(&catalog, index);
    let request = style(400, PROGPU_NATIVE_FONT_SLANT_NORMAL);
    // A CJK ideograph, an Arabic letter and an emoji: the text family has none of them.
    for (code_point, language) in [(0x4E2Du32, "ja"), (0x0639, "ar"), (0x1F600, "")] {
        let mut fallback = 0u32;
        let mut glyph_index = 0u16;
        // SAFETY: the strings are borrowed for the call; the outputs are written by it.
        let found = unsafe {
            progpu_native_font_catalog_match_character(
                catalog.0,
                family.as_ptr().cast(),
                family.len(),
                &request,
                language.as_ptr().cast(),
                language.len(),
                code_point,
                0,
                &mut fallback,
                &mut glyph_index,
            )
        };
        if found == 0 {
            // A system without a font for the script: nothing to compare.
            continue;
        }
        let (_, _, _, fallback_record) = info(&catalog, fallback);
        assert_ne!(glyph_index, 0, "U+{code_point:04X} has a glyph in the fallback face");
        // SAFETY: an index of the catalog; the result is checked.
        let face = Face(unsafe { progpu_native_font_catalog_load_face(catalog.0, fallback) });
        assert!(!face.0.is_null());
        assert_eq!(glyph(&face, char::from_u32(code_point).unwrap()), glyph_index);
        if fallback_record.identity != record.identity {
            assert_ne!(fallback_record.family_identity, 0);
        }
    }
}

#[test]
fn a_style_resolves_to_the_variation_coordinates_of_a_variable_face() {
    let catalog = system_catalog();
    // SAFETY: a plain query.
    let count = unsafe { progpu_native_font_catalog_face_count(catalog.0) };
    // The first variable face with a weight axis and TrueType outlines.
    for index in 0..count {
        let (_, _, path, _) = info(&catalog, index);
        if !path.ends_with(".ttf") {
            continue;
        }
        // SAFETY: an index of the catalog; the result is checked.
        let face = Face(unsafe { progpu_native_font_catalog_load_face(catalog.0, index) });
        if face.0.is_null() {
            continue;
        }
        let metrics = metrics(&face);
        if metrics.variation_axis_count == 0 || metrics.outline_format != PROGPU_NATIVE_FONT_OUTLINE_TRUE_TYPE {
            continue;
        }
        let mut regular = vec![0i16; usize::from(metrics.variation_axis_count)];
        let mut bold = regular.clone();
        let (mut written, mut applied) = (0usize, 0u32);
        // SAFETY: the coordinates are written by the calls, within their capacity.
        unsafe {
            let request = style(i32::from(metrics.weight), PROGPU_NATIVE_FONT_SLANT_NORMAL);
            assert_ne!(progpu_native_font_face_resolve_style(face.0, &request, regular.as_mut_ptr(), regular.len(), &mut written, &mut applied), 0);
            assert_eq!(written, regular.len());
            if applied & 1 == 0 {
                continue;
            }
            let request = style(700, PROGPU_NATIVE_FONT_SLANT_NORMAL);
            assert_ne!(progpu_native_font_face_resolve_style(face.0, &request, bold.as_mut_ptr(), bold.len(), &mut written, &mut applied), 0);
            assert_eq!(applied & 1, 1);
            // A capacity below the axis count fails and reports the count.
            assert_eq!(progpu_native_font_face_resolve_style(face.0, &request, bold.as_mut_ptr(), 0, &mut written, &mut applied), 0);
            assert_eq!(written, bold.len());
        }
        if regular == bold {
            // The face's own weight is the bold end of its axis.
            continue;
        }
        let mut letter = 0u16;
        // SAFETY: the glyph is written by the call.
        if unsafe { progpu_native_font_face_get_glyph_index(face.0, 'l' as u32, &mut letter) } == 0 {
            continue;
        }
        let (thin, thin_bounds) = outline(&face, letter, &regular);
        let (thick, thick_bounds) = outline(&face, letter, &bold);
        assert!(!thin.is_empty() && !thick.is_empty());
        let width = |bounds: &progpu_native_font_glyph_bounds| bounds.x_max - bounds.x_min;
        assert!(
            width(&thick_bounds) > width(&thin_bounds),
            "the bold l of {path} is wider: {} against {}",
            width(&thick_bounds),
            width(&thin_bounds)
        );
        return;
    }
    // No variable text face on this system: nothing to check.
}

#[test]
fn the_shaper_shapes_text_over_the_bytes_of_a_face() {
    let catalog = system_catalog();
    let (_, _, face) = text_family(&catalog);
    let text = "Office affine";
    let scalars: Vec<progpu_native_text_scalar> = text
        .char_indices()
        .map(|(index, character)| progpu_native_text_scalar {
            code_point: character as u32,
            input_index: index as u32,
            input_length: 1,
            canonical_combining_class: 0,
            reserved: 0,
            script: 0,
        })
        .collect();
    // SAFETY: the face's bytes are borrowed for the creation (the context copies them); the
    // request borrows the scalars for the calls; outputs are sized by the requirements.
    unsafe {
        let (mut data, mut size, mut face_index) = (ptr::null(), 0usize, 0u32);
        assert_ne!(progpu_native_font_face_get_data(face.0, &mut data, &mut size, &mut face_index), 0);
        let mut context = ptr::null_mut();
        let status =
            progpu_native_text_context_create(PROGPU_NATIVE_ABI_VERSION, data.cast(), size, face_index, ptr::null(), 0, &mut context);
        assert_eq!(status, PROGPU_NATIVE_STATUS_SUCCESS);
        let mut request: progpu_native_text_shape_request = core::mem::zeroed();
        request.struct_size = core::mem::size_of::<progpu_native_text_shape_request>() as u32;
        request.abi_version = PROGPU_NATIVE_ABI_VERSION;
        request.input = scalars.as_ptr();
        request.input_count = scalars.len() as u32;
        request.direction = 1;
        request.buffer_flags = 3;
        let mut requirements: progpu_native_text_shape_requirements = core::mem::zeroed();
        requirements.struct_size = core::mem::size_of::<progpu_native_text_shape_requirements>() as u32;
        assert_eq!(progpu_native_text_context_get_shape_requirements(context, &request, &mut requirements), PROGPU_NATIVE_STATUS_SUCCESS);
        let mut glyphs: Vec<progpu_native_text_shaping_glyph> = vec![core::mem::zeroed(); requirements.glyph_capacity as usize];
        let mut scratch = vec![0u64; (requirements.scratch_bytes as usize).div_ceil(8)];
        let mut result: progpu_native_text_shape_result = core::mem::zeroed();
        result.struct_size = core::mem::size_of::<progpu_native_text_shape_result>() as u32;
        let status = progpu_native_text_context_shape(
            context,
            &request,
            glyphs.as_mut_ptr(),
            glyphs.len() as u32,
            scratch.as_mut_ptr().cast(),
            scratch.len() * 8,
            &mut result,
        );
        assert_eq!(status, PROGPU_NATIVE_STATUS_SUCCESS, "shaping error {}", result.error_code);
        let shaped = &glyphs[..result.glyph_count as usize];
        // One glyph per character, or fewer where the font has ligatures (ffi, ffi).
        assert!(!shaped.is_empty() && shaped.len() <= text.len());
        let mut cluster = -1;
        for glyph in shaped {
            assert!(glyph.cluster >= cluster, "clusters do not go back in left-to-right text");
            cluster = glyph.cluster;
            assert_ne!(glyph.glyph_id, 0, "every character of the text has a glyph");
        }
        // The advances agree with the face's own.
        let first = &shaped[0];
        let mut advance = 0.0f32;
        assert_ne!(progpu_native_font_face_get_advance(face.0, first.glyph_id as u16, ptr::null(), 0, &mut advance), 0);
        assert!((first.advance_x as f32 - advance).abs() <= 1.0, "{} against {advance}", first.advance_x);
        progpu_native_text_context_destroy(context);
    }
}
