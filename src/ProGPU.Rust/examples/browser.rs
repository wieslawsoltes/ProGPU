//! WebAssembly/WebGPU canvas host and GPU-readback validation bridge.
//! This example uses the same production renderer and shared scene as winit.

#[cfg(target_arch = "wasm32")]
#[path = "support/font_scene.rs"]
mod font_scene;
#[cfg(target_arch = "wasm32")]
#[path = "support/sample_scene.rs"]
mod sample;

#[cfg(target_arch = "wasm32")]
#[path = "../tests/support/managed_font_replay.rs"]
mod managed_font_replay;
#[cfg(target_arch = "wasm32")]
#[path = "../tests/support/managed_mapping_replay.rs"]
mod managed_mapping_replay;
#[cfg(target_arch = "wasm32")]
#[path = "../tests/support/managed_glyph_fixture.rs"]
mod managed_records;
#[cfg(target_arch = "wasm32")]
#[path = "../tests/support/managed_variation_replay.rs"]
mod managed_variation_replay;
#[cfg(target_arch = "wasm32")]
#[path = "../tests/support/name_replay.rs"]
mod name_replay;
#[cfg(target_arch = "wasm32")]
mod web {
    use super::sample;
    use js_sys::{
        Array, Float32Array, Int32Array, Object, Promise, Reflect, Uint8Array, Uint16Array,
        Uint32Array,
    };
    use progpu::{
        geometry::{Affine, AnalyticPrimitive},
        path::{FillRule, Path, Segment},
        renderer::{Frame, PreparedScene, Renderer},
        scene::{PathDraw, PathSampling, Scene},
    };
    use std::sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    };
    use wasm_bindgen::prelude::*;
    use wasm_bindgen_futures::JsFuture;
    use web_sys::HtmlCanvasElement;

    fn error(error: impl std::fmt::Display) -> JsValue {
        JsValue::from_str(&error.to_string())
    }
    fn field(object: &Object, name: &str, value: f64) -> Result<(), JsValue> {
        Reflect::set(object, &JsValue::from_str(name), &JsValue::from_f64(value)).map(|_| ())
    }

    /// Execute the production CPU font decoder in WASM, retaining its source
    /// after the caller's face is dropped. Returns original point/segment bytes.
    #[wasm_bindgen]
    pub fn decode_simple_glyph(data: Vec<u8>, glyph_index: u16) -> Result<Object, JsValue> {
        let face = progpu::font::SfntFace::new(data, 0).map_err(error)?;
        let glyph = face.simple_glyph(glyph_index).map_err(error)?;
        drop(face);
        let points: Vec<i32> = glyph
            .points()
            .iter()
            .flat_map(|p| [p.x, p.y, i32::from(p.flags)])
            .collect();
        let segments = glyph.segments().map_err(error)?;
        let result = Object::new();
        for (key, value) in [
            ("points", Int32Array::from(points.as_slice()).into()),
            ("ends", Uint16Array::from(glyph.contour_end_points()).into()),
            (
                "instructions",
                Uint8Array::from(glyph.instructions()).into(),
            ),
            (
                "segments",
                Uint8Array::from(bytemuck::cast_slice::<_, u8>(&segments)).into(),
            ),
        ] {
            Reflect::set(&result, &JsValue::from_str(key), &value)?;
        }
        Ok(result)
    }

    /// Execute original unhinted recursive expansion in WASM with retained source ownership.
    #[wasm_bindgen]
    pub fn decode_glyph_outline(data: Vec<u8>, glyph_index: u16) -> Result<Object, JsValue> {
        let face = progpu::font::SfntFace::new(data, 0).map_err(error)?;
        let glyph = face.glyph_outline(glyph_index).map_err(error)?;
        drop(face);
        let result = Object::new();
        for (key, value) in [
            (
                "points",
                Float32Array::from(bytemuck::cast_slice::<_, f32>(glyph.points())).into(),
            ),
            (
                "segments",
                Uint8Array::from(bytemuck::cast_slice::<_, u8>(glyph.segments())).into(),
            ),
        ] {
            Reflect::set(&result, &JsValue::from_str(key), &value)?;
        }
        Ok(result)
    }

    /// Execute the original CFF1 Type 2 evaluator in WASM after source retirement.
    #[wasm_bindgen]
    pub fn decode_cff_outline(data: Vec<u8>, glyph_index: u32) -> Result<Uint8Array, JsValue> {
        let face = progpu::font::SfntFace::new(data, 0).map_err(error)?;
        let font = face.cff1(0).map_err(error)?;
        let glyph = font.outline(glyph_index).map_err(error)?;
        drop(font);
        drop(face);
        Ok(Uint8Array::from(bytemuck::cast_slice::<_, u8>(
            glyph.segments(),
        )))
    }

    /// Validate the independent fixed/design variation contracts in WASM.
    #[wasm_bindgen]
    pub fn normalize_font_variation(
        data: Vec<u8>,
        index: u16,
        fixed: i32,
        design: f32,
        strict: bool,
    ) -> Result<i16, JsValue> {
        let face = progpu::font::SfntFace::new(data, 0).map_err(error)?;
        if strict {
            face.normalize_variation_design_coordinate(index, design)
                .map_err(error)
        } else {
            face.normalize_variation_coordinate(index, fixed)
                .map_err(error)
        }
    }

    /// Exercise original item-store scalars, encoded deltas and blend selection.
    #[wasm_bindgen]
    pub fn item_variation_values(
        data: Vec<u8>,
        coordinates: Vec<i16>,
    ) -> Result<Float32Array, JsValue> {
        let store = progpu::font::ItemVariationStore::parse(&data, 0, 1).map_err(error)?;
        let values = [
            store.region_scalar(&coordinates, 0).map_err(error)?,
            store.delta(&coordinates, 0, 0).map_err(error)?,
            store
                .blend_region_scalar(&coordinates, 1, 0)
                .map_err(error)?,
        ];
        Ok(Float32Array::from(values.as_slice()))
    }

    /// Execute variable CFF2 paths while retaining the original font generation.
    #[wasm_bindgen]
    pub fn decode_cff2_outline(
        data: Vec<u8>,
        glyph: u32,
        coordinates: Vec<i16>,
    ) -> Result<Uint8Array, JsValue> {
        let face = progpu::font::SfntFace::new(data, 0).map_err(error)?;
        let font = face.cff2(0).map_err(error)?;
        let outline = font.outline(glyph, &coordinates).map_err(error)?;
        drop(font);
        drop(face);
        Ok(Uint8Array::from(bytemuck::cast_slice::<_, u8>(
            outline.segments(),
        )))
    }

    /// Decode original packed variation records in the WASM runtime.
    #[wasm_bindgen]
    pub fn decode_variation_runs(
        data: Vec<u8>,
        count: u32,
        points: bool,
    ) -> Result<Int32Array, JsValue> {
        use progpu::font::gvar::{PackedDeltas, PackedPoints};
        let mut values = Vec::new();
        if points {
            let p = PackedPoints::parse(&data).map_err(error)?;
            values.extend([p.bytes_consumed() as i32, i32::from(p.all_points())]);
            values.extend(p.iter().map(|v| v as i32));
        } else {
            let d = PackedDeltas::parse(&data, count).map_err(error)?;
            values.extend([d.bytes_consumed() as i32, 0]);
            values.extend(d.iter().map(i32::from));
        }
        Ok(Int32Array::from(values.as_slice()))
    }
    /// Run the original circular contour interpolation using supplied buffers.
    #[wasm_bindgen]
    pub fn interpolate_gvar_deltas(
        points: Vec<f32>,
        ends: Vec<u16>,
        mut x: Vec<f32>,
        mut y: Vec<f32>,
        touched: Vec<u8>,
    ) -> Result<Float32Array, JsValue> {
        if !points.len().is_multiple_of(2) {
            return Err(error("incomplete point"));
        }
        let points: Vec<_> = points.chunks_exact(2).map(|p| [p[0], p[1]]).collect();
        progpu::font::gvar::infer_untouched(&points, &ends, &mut x, &mut y, &touched)
            .map_err(error)?;
        x.extend(y);
        Ok(Float32Array::from(x.as_slice()))
    }
    /// Decode original gvar tuple metadata, retaining its owner after face drop.
    #[wasm_bindgen]
    pub fn decode_gvar_metadata(data: Vec<u8>, glyph: u16) -> Result<Int32Array, JsValue> {
        let face = progpu::font::SfntFace::new(data, 0).map_err(error)?;
        let font = face
            .gvar()
            .map_err(error)?
            .ok_or_else(|| error("missing gvar"))?;
        let tuples = font.tuple_headers(glyph).map_err(error)?;
        drop(font);
        drop(face);
        let mut values = vec![
            i32::from(tuples.source().header().axis_count),
            tuples.headers().len() as i32,
        ];
        for h in tuples.headers() {
            values.extend([
                h.region_coordinate_offset as i32,
                i32::from(h.serialized_data_size),
                i32::from(h.flags),
            ]);
        }
        values.extend(tuples.coordinates().iter().map(|v| i32::from(*v)));
        Ok(Int32Array::from(values.as_slice()))
    }

    /// Apply original simple-point deltas and IUP to an explicit contour inventory.
    #[wasm_bindgen]
    pub fn apply_gvar_simple(
        data: Vec<u8>,
        glyph: u16,
        coordinates: Vec<i16>,
        ends: Vec<u16>,
        points: Vec<i32>,
    ) -> Result<Float32Array, JsValue> {
        if !points.len().is_multiple_of(2) {
            return Err(error("incomplete point"));
        }
        let face = progpu::font::SfntFace::new(data, 0).map_err(error)?;
        let original: Vec<_> = points
            .chunks_exact(2)
            .map(|p| progpu::font::OutlinePoint {
                x: p[0],
                y: p[1],
                flags: 0,
            })
            .collect();
        let varied = progpu::font::gvar::apply_simple(&face, glyph, &coordinates, &ends, &original)
            .map_err(error)?;
        Ok(Float32Array::from(bytemuck::cast_slice::<_, f32>(&varied)))
    }

    /// Query ordered component offsets and the separate raw horizontal phantom pair.
    #[wasm_bindgen]
    pub fn gvar_component_metrics(
        data: Vec<u8>,
        glyph: u16,
        coordinates: Vec<i16>,
        components: u32,
    ) -> Result<Float32Array, JsValue> {
        let face = progpu::font::SfntFace::new(data, 0).map_err(error)?;
        let mut offsets =
            progpu::font::gvar::component_offsets(&face, glyph, &coordinates, components)
                .map_err(error)?;
        let items = components
            .checked_add(4)
            .ok_or_else(|| error("item count overflow"))?;
        offsets.push(
            progpu::font::gvar::horizontal_phantom_deltas(&face, glyph, &coordinates, items)
                .map_err(error)?,
        );
        Ok(Float32Array::from(bytemuck::cast_slice::<_, f32>(&offsets)))
    }

    /// Exercise source-owned prepared HVAR scalars, metrics and map presence.
    #[wasm_bindgen]
    pub fn horizontal_font_metrics(
        data: Vec<u8>,
        glyph: u16,
        coordinates: Vec<i16>,
        strict: bool,
    ) -> Result<Float32Array, JsValue> {
        let face = progpu::font::SfntFace::new(data, 0).map_err(error)?;
        let (advance, bearing) = if strict {
            let m = face
                .prepare_horizontal_metrics_variation(&coordinates)
                .map_err(error)?;
            let bearing = m.left_side_bearing_variation(glyph).map_err(error)?;
            (m.advance().clone(), bearing)
        } else {
            (
                face.prepare_horizontal_advance_variation(&coordinates)
                    .map_err(error)?,
                None,
            )
        };
        drop(face);
        let delta = advance.advance_variation(glyph).map_err(error)?;
        let values = [
            f32::from(advance.uses_hvar()),
            advance.region_scalars().len() as f32,
            advance.design_advance_width_hvar(glyph).map_err(error)?,
            advance.design_advance_width(glyph).map_err(error)?,
            delta.unwrap_or(0.0),
            f32::from(delta.is_some()),
            bearing.unwrap_or(0.0),
            f32::from(bearing.is_some()),
        ];
        Ok(Float32Array::from(values.as_slice()))
    }
    /// Static vertical origins and independently prepared neutral VVAR deltas.
    #[wasm_bindgen]
    pub fn vertical_font_metrics(
        data: Vec<u8>,
        glyph: u16,
        coordinates: Vec<i16>,
    ) -> Result<Float32Array, JsValue> {
        let face = progpu::font::SfntFace::new(data, 0).map_err(error)?;
        let height = face.design_advance_height(glyph);
        let origin = face.design_vertical_origin_y(glyph);
        let instance = face
            .prepare_vertical_metrics_variation(&coordinates)
            .map_err(error)?;
        drop(face);
        let delta = instance.variation(glyph).map_err(error)?;
        let mut output = [progpu::font::VerticalMetricsVariation::default(); 2];
        instance
            .variations_into(&[glyph], &mut output)
            .map_err(error)?;
        if output[0] != delta || output[1] != progpu::font::VerticalMetricsVariation::default() {
            return Err(error("vertical batch prefix/tail mismatch"));
        }
        let values = [
            height.unwrap_or(0) as f32,
            f32::from(height.is_some()),
            origin.unwrap_or(0) as f32,
            f32::from(origin.is_some()),
            f32::from(delta.uses_vvar),
            instance.region_scalars().len() as f32,
            delta.advance_height,
            delta.top_side_bearing.unwrap_or(0.0),
            delta.bottom_side_bearing.unwrap_or(0.0),
            delta.vertical_origin_y.unwrap_or(0.0),
            f32::from(delta.top_side_bearing.is_some()),
            f32::from(delta.bottom_side_bearing.is_some()),
            f32::from(delta.vertical_origin_y.is_some()),
        ];
        Ok(Float32Array::from(values.as_slice()))
    }
    /// Original direct advance boundary, including empty-request bypass.
    #[wasm_bindgen]
    pub fn design_font_advance(
        data: Vec<u8>,
        glyph: u16,
        coordinates: Vec<i16>,
    ) -> Result<f32, JsValue> {
        progpu::font::SfntFace::new(data, 0)
            .map_err(error)?
            .design_advance_width(glyph, &coordinates)
            .map_err(error)
    }

    /// Original owned simple UTF-16 run; no replacement of unpaired surrogates.
    #[wasm_bindgen]
    pub fn simple_font_run(
        data: Vec<u8>,
        text: Vec<u16>,
        blank: u16,
        hyphen: u16,
    ) -> Result<Object, JsValue> {
        let face = progpu::font::SfntFace::new(data, 0).map_err(error)?;
        let run = progpu::text::SimpleGlyphRun::new(&face, &text, blank, hyphen).map_err(error)?;
        drop(face);
        drop(text);
        let result = Object::new();
        for (name, values) in [
            ("text", run.text()),
            ("clusters", run.cluster_map()),
            ("glyphs", run.glyph_indices()),
        ] {
            Reflect::set(
                &result,
                &JsValue::from_str(name),
                &Uint16Array::from(values),
            )?;
        }
        Ok(result)
    }
    /// Original per-occurrence advance scaling with explicit caller tails.
    #[wasm_bindgen]
    pub fn simple_font_advances(
        text: Vec<u16>,
        clusters: Vec<u16>,
        glyphs: Vec<u16>,
        metric_pairs: Vec<u32>,
        units_per_em: u16,
        scales: Vec<f64>,
        is_sideways: bool,
    ) -> Result<Object, JsValue> {
        if scales.len() != 2 || !metric_pairs.len().is_multiple_of(2) {
            return Err(error("invalid simple metric protocol"));
        }
        let metrics: Vec<_> = metric_pairs
            .chunks_exact(2)
            .map(|m| progpu::text::SimpleGlyphMetrics {
                advance_width: m[0],
                advance_height: m[1],
            })
            .collect();
        let mut state = vec![0xa5; glyphs.len() + 2];
        let mut advances = vec![-1234567; glyphs.len() + 2];
        progpu::text::fill_simple_glyph_advances(
            &text,
            &clusters,
            &glyphs,
            &metrics,
            progpu::text::SimpleAdvanceScale {
                units_per_em,
                font_em_size: scales[0],
                scaling_factor: scales[1],
                is_sideways,
            },
            &mut state,
            &mut advances,
        )
        .map_err(error)?;
        let result = Object::new();
        Reflect::set(
            &result,
            &JsValue::from_str("state"),
            &Uint8Array::from(state.as_slice()),
        )?;
        Reflect::set(
            &result,
            &JsValue::from_str("advances"),
            &Int32Array::from(advances.as_slice()),
        )?;
        Ok(result)
    }
    /// Original static Unicode properties as exact integer records.
    #[wasm_bindgen]
    pub fn unicode_properties(code_points: Vec<u32>) -> Uint32Array {
        use progpu::text::unicode as u;
        let values: Vec<_> = code_points
            .into_iter()
            .flat_map(|cp| {
                let bracket = u::bidi_bracket(cp);
                [
                    u::script(cp).0,
                    u32::from(u::canonical_combining_class(cp)),
                    u::bidi_class(cp) as u32,
                    u::general_category(cp) as u32,
                    u::decimal_digit_value(cp).map_or(u32::MAX, u32::from),
                    u::mirrored_code_point(cp),
                    u::vertical_code_point(cp),
                    u32::from(bracket.is_some()),
                    bracket.map_or(0, |b| b.paired_code_point),
                    bracket.map_or(0, |b| b.kind as u32),
                ]
            })
            .collect();
        Uint32Array::from(values.as_slice())
    }
    fn unicode_records(text: progpu::text::unicode::DecodedText) -> Result<Object, JsValue> {
        let runs = text.script_runs().map_err(error)?;
        let result = Object::new();
        Reflect::set(
            &result,
            &JsValue::from_str("scalars"),
            &Uint8Array::from(bytemuck::cast_slice::<_, u8>(text.scalars())),
        )?;
        Reflect::set(
            &result,
            &JsValue::from_str("runs"),
            &Uint8Array::from(bytemuck::cast_slice::<_, u8>(&runs)),
        )?;
        Ok(result)
    }
    /// Execute strict source-owned UTF-8 decoding and original script itemization.
    #[wasm_bindgen]
    pub fn decode_unicode_utf8(data: Vec<u8>) -> Result<Object, JsValue> {
        unicode_records(progpu::text::unicode::DecodedText::from_utf8(data).map_err(error)?)
    }
    /// Execute strict source-owned UTF-16, independently of simple-run decoding.
    #[wasm_bindgen]
    pub fn decode_unicode_utf16(data: Vec<u16>) -> Result<Object, JsValue> {
        unicode_records(progpu::text::unicode::DecodedText::from_utf16(data).map_err(error)?)
    }
    /// Resolve original bidi levels while retaining strict UTF-16 source ownership.
    #[wasm_bindgen]
    pub fn resolve_bidi_utf16(data: Vec<u16>, requested: i8) -> Result<Object, JsValue> {
        use progpu::text::unicode::{DecodedText, UnicodeSource, bidi::BidiParagraph};
        let paragraph =
            BidiParagraph::new(DecodedText::from_utf16(data).map_err(error)?, requested)
                .map_err(error)?;
        let result = Object::new();
        field(&result, "paragraph", f64::from(paragraph.paragraph_level()))?;
        Reflect::set(
            &result,
            &JsValue::from_str("levels"),
            &Uint8Array::from(bytemuck::cast_slice::<_, u8>(paragraph.levels())),
        )?;
        if let UnicodeSource::Utf16(source) = paragraph.text().source() {
            Reflect::set(
                &result,
                &JsValue::from_str("source"),
                &Uint16Array::from(source.as_ref()),
            )?;
        }
        Ok(result)
    }
    /// Original grapheme clusters with explicit managed shaping compatibility.
    #[wasm_bindgen]
    pub fn segment_graphemes_utf16(
        data: Vec<u16>,
        managed_compatible: bool,
    ) -> Result<Object, JsValue> {
        use progpu::text::unicode::{
            DecodedText, UnicodeSource,
            grapheme::{GraphemePolicy, GraphemeText},
        };
        let policy = if managed_compatible {
            GraphemePolicy::ManagedCompatible
        } else {
            GraphemePolicy::Unicode17
        };
        let text = GraphemeText::new(DecodedText::from_utf16(data).map_err(error)?, policy)
            .map_err(error)?;
        let result = Object::new();
        Reflect::set(
            &result,
            &JsValue::from_str("clusters"),
            &Uint8Array::from(bytemuck::cast_slice::<_, u8>(text.clusters())),
        )?;
        if let UnicodeSource::Utf16(source) = text.text().source() {
            Reflect::set(
                &result,
                &JsValue::from_str("source"),
                &Uint16Array::from(source.as_ref()),
            )?;
        }
        Ok(result)
    }
    /// Strict original Unicode 17 starts, including low-surrogate zeros.
    #[wasm_bindgen]
    pub fn grapheme_starts_utf16(data: Vec<u16>) -> Result<Vec<u8>, JsValue> {
        let mut starts = vec![0; data.len()];
        progpu::text::unicode::grapheme::utf16_starts_into(&data, &mut starts).map_err(error)?;
        Ok(starts)
    }
    /// Original logical line opportunities with strict retained source ownership.
    #[wasm_bindgen]
    pub fn resolve_line_breaks_utf16(
        data: Vec<u16>,
        edit_selection: bool,
    ) -> Result<Object, JsValue> {
        use progpu::text::unicode::{
            DecodedText, UnicodeSource,
            line_break::{LineBreakPolicy, LineBreakText},
        };
        let policy = if edit_selection {
            LineBreakPolicy::EditSelection
        } else {
            LineBreakPolicy::Unicode17
        };
        let text = LineBreakText::new(DecodedText::from_utf16(data).map_err(error)?, policy)
            .map_err(error)?;
        let result = Object::new();
        for (name, bytes) in [
            (
                "classes",
                text.classes().iter().map(|c| *c as u8).collect::<Vec<_>>(),
            ),
            (
                "breaks",
                text.breaks_after()
                    .iter()
                    .map(|b| *b as u8)
                    .collect::<Vec<_>>(),
            ),
        ] {
            Reflect::set(
                &result,
                &JsValue::from_str(name),
                &Uint8Array::from(bytes.as_slice()),
            )?;
        }
        Reflect::set(
            &result,
            &JsValue::from_str("scalars"),
            &Uint8Array::from(bytemuck::cast_slice::<_, u8>(text.text().scalars())),
        )?;
        if let UnicodeSource::Utf16(source) = text.text().source() {
            Reflect::set(
                &result,
                &JsValue::from_str("source"),
                &Uint16Array::from(source.as_ref()),
            )?;
        }
        Ok(result)
    }
    /// Normalize with the original plan while retaining exact UTF-16 source.
    #[wasm_bindgen]
    pub fn normalize_utf16(data: Vec<u16>, form: u8, managed: bool) -> Result<Object, JsValue> {
        use progpu::text::unicode::{
            DecodedText,
            normalization::{NormalizationForm, NormalizedText},
        };
        let form = NormalizationForm::try_from(form).map_err(error)?;
        let source = DecodedText::from_utf16(data).map_err(error)?;
        let text = if managed {
            NormalizedText::new_managed(source, form)
        } else {
            NormalizedText::new(source, form)
        }
        .map_err(error)?;
        normalized_result(text)
    }
    /// Normalize for an explicit source Unicode profile without runtime inference.
    #[wasm_bindgen]
    pub fn normalize_utf16_profile(
        data: Vec<u16>,
        form: u8,
        profile: u16,
    ) -> Result<Object, JsValue> {
        use progpu::text::unicode::{
            DecodedText,
            normalization::{ManagedNormalizationProfile, NormalizationForm, NormalizedText},
        };
        let text = NormalizedText::with_managed_profile(
            DecodedText::from_utf16(data).map_err(error)?,
            ManagedNormalizationProfile::try_from(profile).map_err(error)?,
            NormalizationForm::try_from(form).map_err(error)?,
        )
        .map_err(error)?;
        normalized_result(text)
    }
    fn normalized_result(
        text: progpu::text::unicode::normalization::NormalizedText,
    ) -> Result<Object, JsValue> {
        use progpu::text::unicode::UnicodeSource;
        let result = Object::new();
        Reflect::set(
            &result,
            &JsValue::from_str("scalars"),
            &Uint8Array::from(bytemuck::cast_slice::<_, u8>(text.scalars())),
        )?;
        if let UnicodeSource::Utf16(source) = text.text().source() {
            Reflect::set(
                &result,
                &JsValue::from_str("source"),
                &Uint16Array::from(source.as_ref()),
            )?;
        }
        Ok(result)
    }
    /// Validate borrowed custom-plan bytes, including WASM extent arithmetic.
    #[wasm_bindgen]
    pub fn normalization_plan_lookup(
        bytes: Vec<u8>,
        cp: u32,
        first: u32,
        second: u32,
    ) -> Result<Vec<u32>, JsValue> {
        let plan = progpu::text::unicode::normalization::NormalizationData::parse(&bytes)
            .map_err(error)?;
        let decomposition = plan.decomposition(cp);
        let mut result = vec![
            u32::from(decomposition.is_some()),
            decomposition.map_or(0, |b| b.len() as u32 / 4),
        ];
        result.extend(
            decomposition
                .unwrap_or(&[])
                .chunks_exact(4)
                .map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]])),
        );
        let composed = plan.compose(first, second);
        result.extend([u32::from(composed.is_some()), composed.unwrap_or(0)]);
        Ok(result)
    }
    /// Validate original OpenType common tables on the actual wasm32 boundary.
    #[wasm_bindgen]
    pub fn layout_table_query(
        bytes: Vec<u8>,
        kind: u8,
        offset: u32,
        glyph: u16,
        set: u16,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::font::{FontError, layout::*};
        match kind {
            0 => Ok(vec![
                Coverage::parse(&bytes, offset as usize)
                    .map_err(error)?
                    .find(glyph)
                    .map_or(u32::MAX, u32::from),
            ]),
            1 => Ok(vec![u32::from(
                ClassDefinition::parse(&bytes, offset as usize)
                    .map_err(error)?
                    .get(glyph),
            )]),
            2 => {
                let owned = OwnedLayoutTable::new(bytes).map_err(error)?;
                let retained = owned.clone();
                drop(owned);
                let view = retained.view();
                let lookup = view.lookup(glyph).map_err(error)?;
                let mut result = vec![
                    u32::from(view.lookup_count()),
                    lookup.offset() as u32,
                    u32::from(lookup.lookup_type()),
                    u32::from(lookup.flags()),
                    u32::from(lookup.mark_filtering_set()),
                ];
                for i in 0..lookup.subtable_count() {
                    result.push(lookup.subtable_offset(i).map_err(error)? as u32);
                }
                Ok(result)
            }
            3 => {
                let owned = OwnedGdef::new(bytes).map_err(error)?;
                let retained = owned.clone();
                drop(owned);
                let view = retained.view();
                Ok(vec![
                    view.glyph_class(glyph) as u32,
                    u32::from(view.mark_attachment_class(glyph)),
                    u32::from(view.mark_set_count()),
                    u32::from(view.is_in_mark_set(set, glyph)),
                ])
            }
            _ => Err(error(FontError::InvalidArgument)),
        }
    }
    /// Original explicit GDEF blocklist, independent of successful table parsing.
    #[wasm_bindgen]
    pub fn gdef_blocklisted(gdef: u32, gsub: u32, gpos: u32) -> bool {
        progpu::font::layout::is_gdef_blocklisted(gdef as usize, gsub as usize, gpos as usize)
    }
    /// Exercise the exact original lookup preparation and retained owner in WASM.
    #[wasm_bindgen]
    pub fn layout_digest_query(
        bytes: Vec<u8>,
        index: u16,
        extension: u16,
        glyph: u16,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::font::layout::*;
        let owner = OwnedLayoutTable::new(bytes).map_err(error)?;
        let view = owner.view();
        let digest = view.lookup_digest(index, extension).map_err(error)?;
        let (_, coverage) = view
            .single_subtable_coverage(index, extension)
            .map_err(error)?;
        let mut result = vec![
            u32::from(digest.is_some()),
            u32::from(digest.is_some_and(|d| d.may_have(glyph))),
            u32::from(digest.is_some_and(|d| d.managed().may_have(glyph))),
            u32::from(coverage.is_some()),
            coverage
                .and_then(|c| c.find(glyph))
                .map_or(u32::MAX, u32::from),
        ];
        let prepared = PreparedContext::new(owner, index, extension).map_err(error)?;
        result.push(u32::from(prepared.is_some()));
        if let Some(prepared) = prepared {
            let retained = prepared.clone();
            drop(prepared);
            result.extend([
                u32::from(retained.lookup_flags()),
                retained.subtables().len() as u32,
                retained.coverage_count() as u32,
            ]);
            for s in retained.subtables() {
                result.extend([
                    s.coverage_offset,
                    s.coverage_count,
                    u32::from(s.backtrack_count),
                    u32::from(s.input_count),
                ]);
            }
            for i in 0..retained.coverage_count() {
                result.push(u32::from(retained.coverage(i).unwrap().contains(glyph)));
            }
        }
        Ok(result)
    }
    /// Run original feature selection with a retained immutable source/policy.
    #[wasm_bindgen]
    pub fn select_layout(
        bytes: Vec<u8>,
        script: u32,
        language: u32,
        features: Vec<u32>,
        excluded: Vec<u32>,
        coordinates: Vec<i16>,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::font::{Tag, layout::*};
        let tags: Vec<_> = features.into_iter().map(Tag).collect();
        let excluded: Vec<_> = excluded.into_iter().map(Tag).collect();
        let request = SelectionRequest {
            script: Tag(script),
            language: Tag(language),
            features: &tags,
            coordinates: &coordinates,
        };
        let selection = LookupSelection::new(
            OwnedLayoutTable::new(bytes).map_err(error)?,
            request,
            &excluded,
        )
        .map_err(error)?;
        let retained = selection.clone();
        drop(selection);
        drop(tags);
        drop(excluded);
        drop(coordinates);
        Ok(retained.lookups().iter().map(|&v| u32::from(v)).collect())
    }
    /// Exercise current managed selection metadata with exact UTF-16 input tags.
    #[wasm_bindgen]
    pub fn select_managed_layout(
        bytes: Vec<u8>,
        script: Vec<u16>,
        language: Vec<u16>,
        tags: Vec<u16>,
        values: Vec<i32>,
        coordinates: Vec<i16>,
        extension: u16,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::font::{FontError, layout::*};
        if values.len().checked_mul(4) != Some(tags.len()) {
            return Err(error(FontError::InvalidArgument));
        }
        let settings: Result<Vec<_>, _> = tags
            .chunks_exact(4)
            .zip(values)
            .map(|(tag, value)| ManagedFeatureSetting::new(tag.try_into().unwrap(), value))
            .collect();
        let settings = settings.map_err(error)?;
        let selected = ManagedLookupSelection::new(
            bytes,
            ManagedSelectionRequest {
                script: &script,
                language: Some(&language),
                settings: &settings,
                coordinates: &coordinates,
                extension_lookup_type: extension,
            },
        )
        .map_err(error)?;
        let retained = selected.clone();
        drop(selected);
        drop(script);
        drop(language);
        drop(settings);
        drop(coordinates);
        let mut out = vec![
            u32::from(retained.uses_feature_variations()),
            retained.lookups().len() as u32,
        ];
        for entry in retained.lookups() {
            out.extend([
                u32::from(entry.index),
                entry.value as u32,
                u32::from_be_bytes(entry.tag),
                u32::from(entry.required),
                u32::from(entry.digest.is_some()),
            ]);
            if let Some(digest) = entry.digest {
                for &mask in digest.masks() {
                    out.extend([mask as u32, (mask >> 32) as u32]);
                }
            }
        }
        Ok(out)
    }
    fn managed_feature_options(
        words: &[u32],
    ) -> Result<progpu::font::layout::ManagedFeatureOptions, JsValue> {
        use progpu::font::{FontError, layout::*};
        if words.len() < 5 {
            return Err(error(FontError::InvalidArgument));
        }
        let mut at = 5usize;
        let mut take = |n: usize| -> Result<&[u32], JsValue> {
            let end = at
                .checked_add(n)
                .ok_or_else(|| error(FontError::InvalidArgument))?;
            let values = words
                .get(at..end)
                .ok_or_else(|| error(FontError::InvalidArgument))?;
            at = end;
            Ok(values)
        };
        let read_tag = |values: &[u32]| -> Result<[u16; 4], JsValue> {
            if values.iter().any(|&v| v > 65535) {
                return Err(error(FontError::InvalidArgument));
            }
            Ok([
                values[0] as u16,
                values[1] as u16,
                values[2] as u16,
                values[3] as u16,
            ])
        };
        let mut features = Vec::new();
        for _ in 0..words[1] {
            let f = take(5)?;
            features
                .push(ManagedFeatureSetting::new(read_tag(&f[..4])?, f[4] as i32).map_err(error)?);
        }
        let mut baseline = (words[2] != u32::MAX).then(Vec::new);
        if let Some(b) = &mut baseline {
            for _ in 0..words[2] {
                let f = take(5)?;
                b.push(ManagedFeatureSetting::new(read_tag(&f[..4])?, f[4] as i32).map_err(error)?);
            }
        }
        let mut explicit = (words[3] != u32::MAX).then(Vec::new);
        if let Some(e) = &mut explicit {
            for _ in 0..words[3] {
                e.push(read_tag(take(4)?)?);
            }
        }
        let mut ranges = Vec::new();
        for _ in 0..words[4] {
            let r = take(4)?;
            ranges.push(
                ManagedFeatureRange::new(r[0].to_be_bytes(), r[1], r[2], r[3]).map_err(error)?,
            );
        }
        if at != words.len() {
            return Err(error(FontError::InvalidArgument));
        }
        match words[0] {
            0 => ManagedFeatureOptions::new(
                &features,
                baseline.as_deref(),
                explicit.as_deref(),
                &ranges,
            )
            .map_err(error),
            1 if features.is_empty()
                && baseline.is_none()
                && explicit.is_none()
                && ranges.is_empty() =>
            {
                Ok(ManagedFeatureOptions::default())
            }
            2 if baseline.is_none() && explicit.is_none() && ranges.is_empty() => {
                ManagedFeatureOptions::with_overrides(&features).map_err(error)
            }
            _ => Err(error(FontError::InvalidArgument)),
        }
    }
    /// Full source feature and stage policy using exact UTF-16 tags and records.
    #[wasm_bindgen]
    pub fn managed_feature_query(
        policy: Vec<u32>,
        tag: Vec<u16>,
        record: Vec<u32>,
        stage: u8,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::font::{FontError, layout::*};
        let tag: [u16; 4] = tag
            .try_into()
            .map_err(|_| error(FontError::InvalidArgument))?;
        if record.len() != 31 {
            return Err(error(FontError::InvalidArgument));
        }
        let g = super::managed_records::from_words(&record);
        let p = managed_feature_options(&policy)?;
        let s = ManagedSubstitutionStage::try_from(stage).map_err(error)?;
        Ok(vec![
            p.value_at(tag, g.cluster) as u32,
            u32::from(p.explicit_at(tag, g.cluster)),
            u32::from(p.resolves_explicit(tag)),
            u32::from(p.enabled(g, tag)),
            u32::from(s.enables(tag)),
            u32::from(s.manual_joiners()),
            u32::from(s.restricts_syllable(tag)),
        ])
    }
    /// Apply an original raw stage after source owner retirement. Options: stage,
    /// manual joiners, restrict, syllable, context end, random, fraction marker,
    /// per-root nested-call limit. Result retains typed errors and all 31 fields.
    #[wasm_bindgen]
    pub fn apply_managed_stage(
        font: Vec<u8>,
        records: Vec<u32>,
        policy: Vec<u32>,
        options: Vec<u32>,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::font::{FontError, SfntFace, layout::*};
        if options.len() != 8
            || options[0] > 255
            || options[1] > 1
            || options[2] > 1
            || options[3] > 255
            || options[6] > 1
            || !records.len().is_multiple_of(31)
        {
            return Err(error(FontError::InvalidArgument));
        }
        let stage = ManagedSubstitutionStage::try_from(options[0] as u8).map_err(error)?;
        let face = SfntFace::new(font, 0).map_err(error)?;
        let plan = ManagedSubstitutionPlan::from_face(
            face.clone(),
            managed_feature_options(&policy)?,
            &[68, 70, 76, 84],
            None,
            &[],
        )
        .map_err(error)?
        .ok_or_else(|| error("missing GSUB"))?;
        let mut buffer = ManagedGlyphBuffer::new(
            face.clone(),
            records
                .chunks_exact(31)
                .map(super::managed_records::from_words)
                .collect(),
        )
        .map_err(error)?;
        drop(face);
        let retained = plan.clone();
        drop(plan);
        buffer.set_state(ManagedGlyphState {
            traversal: ManagedContextState {
                manual_joiners: options[1] != 0,
                restrict_to_syllable: options[2] != 0,
                lookup_syllable: options[3] as u8,
            },
            context_match_end: options[4] as i32,
            random_state: options[5],
        });
        buffer.set_fraction_actions_present(options[6] != 0);
        let result = retained.apply_stage_with_limits(
            &mut buffer,
            stage,
            ManagedGsubLimits {
                max_lookup_calls: options[7],
            },
        );
        drop(retained);
        let state = buffer.state();
        let mut out = vec![
            result.err().map_or(0, |e| e as u32),
            buffer.glyphs().len() as u32,
            state.context_match_end as u32,
            state.random_state,
            buffer.mark_filtering_coverage() as u32,
            buffer.nested_lookup_depth(),
            u32::from(state.traversal.manual_joiners),
            u32::from(state.traversal.restrict_to_syllable),
            u32::from(state.traversal.lookup_syllable),
            u32::from(buffer.has_fraction_actions()),
        ];
        for &g in buffer.glyphs() {
            out.extend(super::managed_records::words(g));
        }
        Ok(out)
    }
    /// Replay original variation-data admission, normalization and ordered lazy
    /// queries using the source fixture's explicit name-resolver dependency.
    #[wasm_bindgen]
    pub fn managed_variation_instance(
        font: Vec<u8>,
        request: Vec<u32>,
    ) -> Result<Vec<u32>, JsValue> {
        let case = super::managed_variation_replay::decode(font, &request).map_err(error)?;
        let bytes = case.expected().map_err(error)?;
        Ok(bytes
            .chunks_exact(4)
            .map(|v| u32::from_le_bytes(v.try_into().unwrap()))
            .collect())
    }
    /// Replay original nominal/symbol/UVS mapping and font-cache queries.
    #[wasm_bindgen]
    pub fn managed_font_mapping(font: Vec<u8>, requests: Vec<u32>) -> Result<Vec<u32>, JsValue> {
        if !requests.len().is_multiple_of(3) {
            return Err(error("incomplete mapping request"));
        }
        let queries = requests
            .chunks_exact(3)
            .map(|v| [v[0], v[1], v[2]])
            .collect();
        let bytes = super::managed_mapping_replay::Case { font, queries }.expected();
        Ok(bytes
            .chunks_exact(4)
            .map(|v| u32::from_le_bytes(v.try_into().unwrap()))
            .collect())
    }
    /// Replay original font construction, instance-cache identity and queries.
    #[wasm_bindgen]
    pub fn managed_font_state(request: Vec<u8>) -> Result<Vec<u32>, JsValue> {
        let bytes = super::managed_font_replay::decode(&request)
            .map_err(error)?
            .expected()
            .map_err(error)?;
        Ok(bytes
            .chunks_exact(4)
            .map(|v| u32::from_le_bytes(v.try_into().unwrap()))
            .collect())
    }
    /// Replay original name decoding/ranking and actual variation-axis names.
    #[wasm_bindgen]
    pub fn managed_font_names(
        font: Vec<u8>,
        ids: Vec<u16>,
        axes: bool,
    ) -> Result<Vec<u32>, JsValue> {
        let bytes = super::name_replay::Case { font, ids, axes }
            .expected()
            .map_err(error)?;
        Ok(bytes
            .chunks_exact(4)
            .map(|v| u32::from_le_bytes(v.try_into().unwrap()))
            .collect())
    }
    /// Replay actual C# prepared-instance captures and complete positioning plans.
    /// Options are direction, attachment resolution and root-call safety budget.
    #[wasm_bindgen]
    pub fn apply_managed_positioning(
        font: Vec<u8>,
        coordinates: Vec<i16>,
        records: Vec<u32>,
        policy: Vec<u32>,
        options: Vec<u32>,
        queries: Vec<u16>,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::font::{FontError, SfntFace, layout::*};
        if options.len() != 3
            || options[0] > 4
            || options[1] > 1
            || !records.len().is_multiple_of(31)
            || !queries.len().is_multiple_of(2)
        {
            return Err(error(FontError::InvalidArgument));
        }
        let source = SfntFace::new(font, 0).map_err(error)?;
        let vars = ManagedLayoutVariations::from_normalized(source.clone(), &coordinates)
            .map_err(error)?;
        let mut out = vec![u32::from(vars.is_active())];
        for q in queries.chunks_exact(2) {
            let result = vars.delta(q[0], q[1]);
            out.extend([
                result.err().map_or(0, |e| e as u32),
                result.unwrap_or(0.0).to_bits(),
            ]);
        }
        let mut buffer = ManagedPositionBuffer::new(
            source.clone(),
            records
                .chunks_exact(31)
                .map(super::managed_records::from_words)
                .collect(),
            ManagedPositionDirection::try_from(options[0] as u8).map_err(error)?,
        )
        .map_err(error)?;
        let mut kerning = false;
        let result = (|| {
            let plan = ManagedPositioningPlan::from_face(
                source.clone(),
                managed_feature_options(&policy).map_err(|_| FontError::InvalidArgument)?,
                &[68, 70, 76, 84],
                None,
                &coordinates,
            )?;
            drop(source);
            drop(vars);
            let retained = plan.clone();
            drop(plan);
            if let Some(plan) = retained {
                kerning = plan.apply_with_limits(
                    &mut buffer,
                    ManagedGposLimits {
                        max_lookup_calls: options[2],
                    },
                )?;
            }
            if options[1] != 0 {
                buffer.resolve_attachments()?;
            }
            Ok::<_, FontError>(())
        })();
        out.extend([
            result.err().map_or(0, |e| e as u32),
            u32::from(kerning),
            buffer.mark_filtering_coverage() as u32,
        ]);
        for i in 0..buffer.glyphs().len() {
            out.push(
                buffer
                    .glyph_class(i)
                    .map_or_else(|e| 0x80000000 | e as u32, u32::from),
            );
        }
        for &g in buffer.glyphs() {
            out.extend(super::managed_records::words(g));
        }
        Ok(out)
    }
    /// Execute the actual managed GPOS/attachment controls with retained owners.
    /// Options: lookup, position, direction, mode (raw/resolve/both/zero), call budget.
    #[wasm_bindgen]
    pub fn apply_managed_gpos(
        font: Vec<u8>,
        bytes: Vec<u8>,
        records: Vec<u32>,
        options: Vec<u32>,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::font::{FontError, SfntFace, layout::*};
        if options.len() != 5
            || options[0] > 65535
            || options[2] > 4
            || options[3] > 4
            || !records.len().is_multiple_of(31)
        {
            return Err(error(FontError::InvalidArgument));
        }
        let source = SfntFace::new(font, 0).map_err(error)?;
        let table = ManagedGposTable::new(bytes).map_err(error)?;
        let mut buffer = ManagedPositionBuffer::new(
            source.clone(),
            records
                .chunks_exact(31)
                .map(super::managed_records::from_words)
                .collect(),
            ManagedPositionDirection::try_from(options[2] as u8).map_err(error)?,
        )
        .map_err(error)?;
        drop(source);
        let retained = table.clone();
        drop(table);
        let mut changed = false;
        let result = (|| {
            if options[3] < 2 {
                changed = retained.apply_lookup_at_with_limits(
                    &mut buffer,
                    options[0] as u16,
                    options[1] as usize,
                    ManagedGposLimits {
                        max_lookup_calls: options[4],
                    },
                )?;
            }
            if options[3] == 1 || options[3] == 2 {
                buffer.resolve_attachments()?;
            }
            if options[3] >= 3 {
                buffer.zero_mark_advances(options[3] == 4)?;
            }
            Ok::<_, FontError>(())
        })();
        drop(retained);
        let mut out = vec![
            result.err().map_or(0, |e| e as u32),
            u32::from(changed),
            buffer.mark_filtering_coverage() as u32,
        ];
        for i in 0..buffer.glyphs().len() {
            out.push(
                buffer
                    .glyph_class(i)
                    .map_or_else(|e| 0x80000000 | e as u32, u32::from),
            );
        }
        for &g in buffer.glyphs() {
            out.extend(super::managed_records::words(g));
        }
        Ok(out)
    }
    /// Current C# GSUB with all 31 record fields and retained source ownership.
    /// Options: position, reverse offset (u32::MAX for nested lookup), flags,
    /// manual joiners, syllable restriction, syllable, context end, random state,
    /// explicit Rune profile, maximum nested lookup attempts.
    #[wasm_bindgen]
    pub fn apply_managed_gsub(
        font: Vec<u8>,
        bytes: Vec<u8>,
        index: u16,
        records: Vec<u32>,
        options: Vec<u32>,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::{
            font::{FontError, SfntFace, layout::*},
            text::unicode::ManagedMarkProfile,
        };
        if !records.len().is_multiple_of(31)
            || options.len() != 10
            || options[2] > 65535
            || options[3] > 1
            || options[4] > 1
            || options[5] > 255
            || options[8] > 65535
        {
            return Err(error(FontError::InvalidArgument));
        }
        let face = SfntFace::new(font, 0).map_err(error)?;
        let mut buffer = ManagedGlyphBuffer::with_mark_profile(
            face.clone(),
            records
                .chunks_exact(31)
                .map(super::managed_records::from_words)
                .collect(),
            ManagedMarkProfile::try_from(options[8] as u16).map_err(error)?,
        )
        .map_err(error)?;
        drop(face);
        buffer.set_state(ManagedGlyphState {
            traversal: ManagedContextState {
                manual_joiners: options[3] != 0,
                restrict_to_syllable: options[4] != 0,
                lookup_syllable: options[5] as u8,
            },
            context_match_end: options[6] as i32,
            random_state: options[7],
        });
        let table = ManagedGsubTable::new(bytes).map_err(error)?;
        let result = if options[1] == u32::MAX {
            table.apply_lookup_at_with_limits(
                &mut buffer,
                index,
                options[0] as usize,
                ManagedGsubLimits {
                    max_lookup_calls: options[9],
                },
            )
        } else {
            table.apply_reverse_at(
                &mut buffer,
                options[1] as usize,
                options[0] as usize,
                options[2] as u16,
            )
        };
        drop(table);
        let mut out = vec![
            result.err().map_or(0, |e| e as u32),
            u32::from(result.unwrap_or(false)),
            buffer.glyphs().len() as u32,
            buffer.state().context_match_end as u32,
            buffer.state().random_state,
            buffer.mark_filtering_coverage() as u32,
            buffer.nested_lookup_depth(),
        ];
        for i in 0..buffer.glyphs().len() {
            out.push(
                buffer
                    .glyph_class(i)
                    .map_or_else(|e| 0x80000000 | e as u32, u32::from),
            );
        }
        for &g in buffer.glyphs() {
            out.extend(super::managed_records::words(g));
        }
        Ok(out)
    }
    /// Execute original native GSUB in wasm32 and expose complete storage/error state.
    /// Options: count, position (u32::MAX for bulk), bits, alternate, required,
    /// restricted syllable, initial context end, initial random state.
    #[wasm_bindgen]
    pub fn apply_gsub(
        bytes: Vec<u8>,
        gdef_bytes: Vec<u8>,
        index: u16,
        records: Vec<u32>,
        options: Vec<u32>,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::{
            font::{FontError, layout::*},
            text::ShapingGlyph,
        };
        if !records.len().is_multiple_of(8)
            || options.len() != 8
            || options[2] > 255
            || options[5] > 255
        {
            return Err(error(FontError::InvalidArgument));
        }
        let mut storage: Vec<_> = records
            .chunks_exact(8)
            .map(|g| ShapingGlyph {
                glyph_id: g[0],
                code_point: g[1],
                cluster: g[2] as i32,
                flags: g[3],
                advance_x: g[4] as i32,
                advance_y: g[5] as i32,
                offset_x: g[6] as i32,
                offset_y: g[7] as i32,
            })
            .collect();
        let retained = OwnedLayoutTable::new(bytes).map_err(error)?;
        let gdef = if gdef_bytes.is_empty() {
            None
        } else {
            Some(OwnedGdef::new(gdef_bytes).map_err(error)?)
        };
        let bits = options[2];
        let policy = GsubOptions {
            gdef: gdef.as_ref().map(OwnedGdef::view),
            alternate_value: options[3],
            required_glyph_flags: options[4],
            restricted_syllable: options[5] as u8,
            mark_substituted: bits & 1 != 0,
            track_fallback_mark_metadata: bits & 2 != 0,
            track_arabic_stretch_metadata: bits & 4 != 0,
            random_alternate: bits & 8 != 0,
            restrict_to_syllable: bits & 16 != 0,
            track_substitution_provenance: bits & 32 != 0,
            ..Default::default()
        };
        let mut state = GsubState {
            glyph_count: options[0],
            applied: true,
            context_match_end: (bits & 64 != 0).then_some(options[6]),
            random_state: (bits & 128 != 0).then_some(options[7]),
        };
        let result = retained.view().apply_gsub(
            index,
            (options[1] != u32::MAX).then_some(options[1]),
            &mut storage,
            policy,
            &mut state,
        );
        let mut out = vec![
            result.err().map_or(0, |e| e as u32),
            state.glyph_count,
            u32::from(state.applied),
            state.context_match_end.unwrap_or(options[6]),
            state.random_state.unwrap_or(options[7]),
        ];
        for g in storage {
            out.extend([
                g.glyph_id,
                g.code_point,
                g.cluster as u32,
                g.flags,
                g.advance_x as u32,
                g.advance_y as u32,
                g.offset_x as u32,
                g.offset_y as u32,
            ]);
        }
        Ok(out)
    }
    /// Original managed context preparation and ordered matching in wasm32.
    /// Options are manual-joiners/restricted bits, active syllable, full digest.
    #[wasm_bindgen]
    pub fn managed_context_query(
        bytes: Vec<u8>,
        index: u16,
        extension: u16,
        queries: Vec<u16>,
        records: Vec<u32>,
        options: Vec<u32>,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::font::{FontError, layout::*};
        if !records.len().is_multiple_of(6)
            || options.len() != 3
            || options[0] > 3
            || options[1] > 255
            || options[2] > 1
        {
            return Err(error(FontError::InvalidArgument));
        }
        let mut glyphs = Vec::new();
        for g in records.chunks_exact(6) {
            glyphs.push(ManagedContextGlyph {
                glyph_index: u16::try_from(g[0]).map_err(error)?,
                code_point: g[1],
                substituted: u8::try_from(g[2]).map_err(error)?,
                script_shaper: u8::try_from(g[3]).map_err(error)?,
                script_feature_mask: u8::try_from(g[4]).map_err(error)?,
                use_syllable: u8::try_from(g[5]).map_err(error)?,
            });
        }
        let state = ManagedContextState {
            manual_joiners: options[0] & 1 != 0,
            restrict_to_syllable: options[0] & 2 != 0,
            lookup_syllable: options[1] as u8,
        };
        let mut input = ManagedContextInput::new(&glyphs, state);
        if options[2] != 0 {
            let mut digest = input.digest();
            digest.add_range(0, 65535);
            input = ManagedContextInput::with_digest(&glyphs, state, digest);
        }
        let Some(prepared) = PreparedManagedContext::new(bytes, index, extension).map_err(error)?
        else {
            return Ok(vec![0]);
        };
        let retained = prepared.clone();
        drop(prepared);
        let view = retained.view();
        let mut out = vec![
            1,
            u32::from(view.flags()),
            view.subtables().len() as u32,
            view.coverage_count() as u32,
        ];
        for sub in view.subtables() {
            out.extend([
                sub.coverage_offset,
                sub.coverage_count,
                u32::from(sub.backtrack_count),
                u32::from(sub.input_count),
            ]);
        }
        for i in 0..view.coverage_count() {
            let coverage = view.coverage(i).unwrap();
            for &mask in coverage.digest().masks() {
                out.extend([mask as u32, (mask >> 32) as u32]);
            }
            for &q in &queries {
                out.push(u32::from(coverage.contains(q)));
            }
        }
        out.push(u32::from(view.may_match(input)));
        Ok(out)
    }
    /// Preserve original source-order and failure-prefix behavior in wasm32.
    #[wasm_bindgen]
    pub fn select_layout_feature(
        bytes: Vec<u8>,
        script: u32,
        language: u32,
        feature: u32,
        coordinates: Vec<i16>,
        capacity: u16,
    ) -> Result<Vec<u32>, JsValue> {
        use progpu::font::{Tag, layout::*};
        let table = LayoutTable::parse(&bytes).map_err(error)?;
        let mut output = vec![0xbeef; usize::from(capacity)];
        let result = table.select_feature_into(
            Tag(script),
            Tag(language),
            Tag(feature),
            &coordinates,
            &mut output,
        );
        let mut response = vec![
            result.as_ref().err().map_or(0, |e| *e as u32),
            result.unwrap_or(0),
        ];
        response.extend(output.into_iter().map(u32::from));
        Ok(response)
    }
    /// Browser-owned device, canvas surface and immutable prepared generation.
    #[wasm_bindgen]
    pub struct WebRenderer {
        canvas: HtmlCanvasElement,
        device: wgpu::Device,
        device_lost: Arc<AtomicBool>,
        queue: wgpu::Queue,
        surface: wgpu::Surface<'static>,
        config: wgpu::SurfaceConfiguration,
        renderer: Renderer,
        scene: Option<PreparedScene>,
        validation: Option<Renderer>,
        frames: u32,
        capture_complete: Option<Arc<AtomicBool>>,
        // Retain the complete browser device-creation ownership chain until
        // every child above has retired, including asynchronous validation.
        adapter: wgpu::Adapter,
        _instance: wgpu::Instance,
    }

    /// Create a real browser WebGPU renderer; missing adapters fail explicitly.
    #[wasm_bindgen]
    pub async fn create_renderer(
        canvas: HtmlCanvasElement,
        width: u32,
        height: u32,
        dpi: f32,
    ) -> Result<WebRenderer, JsValue> {
        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor::new_without_display_handle());
        let surface = instance
            .create_surface(wgpu::SurfaceTarget::Canvas(canvas.clone()))
            .map_err(error)?;
        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions {
                compatible_surface: Some(&surface),
                ..Default::default()
            })
            .await
            .map_err(error)?;
        let (device, queue) = adapter
            .request_device(&wgpu::DeviceDescriptor::default())
            .await
            .map_err(error)?;
        let device_lost = Arc::new(AtomicBool::new(false));
        let loss_signal = device_lost.clone();
        device.set_device_lost_callback(move |reason, message| {
            loss_signal.store(true, Ordering::Release);
            web_sys::console::error_1(&format!("WebGPU device lost: {reason:?}: {message}").into());
        });
        let mut config = surface
            .get_default_config(&adapter, width.max(1), height.max(1))
            .ok_or_else(|| error("no browser surface configuration"))?;
        if let Some(format) = surface
            .get_capabilities(&adapter)
            .formats
            .into_iter()
            .find(|f| {
                matches!(
                    f,
                    wgpu::TextureFormat::Bgra8Unorm | wgpu::TextureFormat::Rgba8Unorm
                )
            })
        {
            config.format = format;
        }
        let renderer = Renderer::new(&device, &queue, config.format)
            .await
            .map_err(error)?;
        let mut result = WebRenderer {
            canvas,
            device,
            device_lost,
            queue,
            surface,
            config,
            renderer,
            scene: None,
            validation: None,
            frames: 0,
            capture_complete: None,
            adapter,
            _instance: instance,
        };
        result.resize(width, height, dpi).await?;
        Ok(result)
    }

    #[wasm_bindgen]
    impl WebRenderer {
        fn ensure_live(&self) -> Result<(), JsValue> {
            if self.device_lost.load(Ordering::Acquire) {
                Err(error("WebGPU device is lost"))
            } else {
                Ok(())
            }
        }
        /// Actual WebGPU adapter receipt; launch flags alone do not identify it.
        pub fn adapter_info(&self) -> String {
            format!("{:?}", self.adapter.get_info())
        }
        /// Prepare a new physical frame; zero extents suspend presentation.
        pub async fn resize(&mut self, width: u32, height: u32, dpi: f32) -> Result<(), JsValue> {
            self.ensure_live()?;
            if width == 0 || height == 0 {
                self.scene = None;
                return Ok(());
            }
            let source = sample::sample_scene().map_err(error)?;
            let scene = self
                .renderer
                .prepare_scene(
                    &source,
                    Frame {
                        width,
                        height,
                        dpi_scale: dpi,
                    },
                )
                .await
                .map_err(error)?;
            self.ensure_live()?;
            self.config.width = width;
            self.config.height = height;
            self.canvas.set_width(width);
            self.canvas.set_height(height);
            self.surface.configure(&self.device, &self.config);
            self.scene = Some(scene);
            Ok(())
        }
        /// Present retained GPU resources without uploads, rasterization or readback.
        pub fn draw(&mut self) -> Result<bool, JsValue> {
            self.ensure_live()?;
            let Some(scene) = self.scene.as_ref() else {
                return Ok(false);
            };
            let texture = match self.surface.get_current_texture() {
                wgpu::CurrentSurfaceTexture::Success(t)
                | wgpu::CurrentSurfaceTexture::Suboptimal(t) => t,
                wgpu::CurrentSurfaceTexture::Timeout | wgpu::CurrentSurfaceTexture::Occluded => {
                    return Ok(false);
                }
                wgpu::CurrentSurfaceTexture::Outdated | wgpu::CurrentSurfaceTexture::Lost => {
                    self.surface.configure(&self.device, &self.config);
                    return Ok(false);
                }
                wgpu::CurrentSurfaceTexture::Validation => {
                    return Err(error("browser surface validation failed"));
                }
            };
            self.renderer
                .render(
                    scene,
                    &texture.texture.create_view(&Default::default()),
                    wgpu::Color {
                        r: 0.025,
                        g: 0.035,
                        b: 0.06,
                        a: 1.0,
                    },
                )
                .map_err(error)?;
            self.queue.present(texture);
            self.frames += 1;
            Ok(true)
        }
        /// Submit one final retained frame and track this exact queue completion.
        /// Used only by the presentation test after stopping continuous animation.
        pub fn begin_capture(&mut self) -> Result<(), JsValue> {
            if !self.draw()? {
                return Err(error("final capture frame was not presented"));
            }
            // Each request owns a distinct signal so a previous callback cannot
            // satisfy a later capture. No readback or surface fallback is used.
            let signal = Arc::new(AtomicBool::new(false));
            self.capture_complete = Some(signal.clone());
            self.queue
                .on_submitted_work_done(move || signal.store(true, Ordering::Release));
            Ok(())
        }
        /// Whether the requested final frame's GPU work completed; rejects observed device loss.
        pub fn capture_ready(&self) -> Result<bool, JsValue> {
            self.ensure_live()?;
            self.capture_complete
                .as_ref()
                .map(|signal| signal.load(Ordering::Acquire))
                .ok_or_else(|| error("no capture submission"))
        }

        /// Actual immutable payload, raster, draw, physical-frame and presentation counts.
        pub fn metrics(&self) -> Result<Object, JsValue> {
            let result = Object::new();
            field(&result, "frames", self.frames as f64)?;
            if let Some(scene) = &self.scene {
                let m = scene.metrics();
                let f = scene.frame();
                for (name, value) in [
                    ("geometryBytes", m.geometry_bytes as f64),
                    ("uniformBytes", m.uniform_bytes as f64),
                    ("draws", m.draws as f64),
                    ("pathRasters", m.rasterized_paths as f64),
                    ("atlasBytes", m.atlas_bytes as f64),
                    ("width", f.width as f64),
                    ("height", f.height as f64),
                    ("dpi", f.dpi_scale as f64),
                ] {
                    field(&result, name, value)?;
                }
            }
            Ok(result)
        }
        /// Validate all canonical shader compositions on this browser device.
        pub async fn validate_shaders(&self) -> Result<u32, JsValue> {
            self.ensure_live()?;
            for &(name, source) in progpu::shaders::MODULES {
                web_sys::console::info_1(&format!("validating canonical shader: {name}").into());
                let scope = self.device.push_error_scope(wgpu::ErrorFilter::Validation);
                let module = self
                    .device
                    .create_shader_module(wgpu::ShaderModuleDescriptor {
                        label: Some(name),
                        source: wgpu::ShaderSource::Wgsl(source.into()),
                    });
                if let Some(problem) = scope.pop().await {
                    return Err(error(format!("{name}: {problem}")));
                }
                for message in module.get_compilation_info().await.messages {
                    if message.message_type == wgpu::CompilationMessageType::Error {
                        return Err(error(format!("{name}: {}", message.message)));
                    }
                }
                web_sys::console::info_1(&format!("validated canonical shader: {name}").into());
            }
            Ok(progpu::shaders::MODULES.len() as u32)
        }
        /// Read back cold and warm authored validation frames. This test-only path
        /// never substitutes pixels into the actual canvas rendering path.
        pub async fn validation_pixels(
            &mut self,
            dpi: f32,
            contrast: i32,
        ) -> Result<Array, JsValue> {
            self.ensure_live()?;
            if self.validation.is_none() {
                self.validation = Some(
                    Renderer::new(&self.device, &self.queue, wgpu::TextureFormat::Rgba8Unorm)
                        .await
                        .map_err(error)?,
                );
            }
            let renderer = self
                .validation
                .as_mut()
                .ok_or_else(|| error("missing validation renderer"))?;
            let source = validation_scene(contrast)?;
            let frame = Frame {
                width: 64,
                height: 64,
                dpi_scale: dpi,
            };
            let scene = renderer
                .prepare_scene(&source, frame)
                .await
                .map_err(error)?;
            let later = renderer
                .prepare_scene(
                    &source,
                    Frame {
                        dpi_scale: 3.0,
                        ..frame
                    },
                )
                .await
                .map_err(error)?;
            drop(later);
            drop(source);
            let texture = self.device.create_texture(&wgpu::TextureDescriptor {
                label: Some("browser pixel control"),
                size: wgpu::Extent3d {
                    width: 64,
                    height: 64,
                    depth_or_array_layers: 1,
                },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format: wgpu::TextureFormat::Rgba8Unorm,
                usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
                view_formats: &[],
            });
            let view = texture.create_view(&Default::default());
            renderer
                .render(&scene, &view, wgpu::Color::TRANSPARENT)
                .map_err(error)?;
            let cold = read_pixels(&self.device, &self.queue, &texture).await?;
            renderer
                .render(&scene, &view, wgpu::Color::TRANSPARENT)
                .map_err(error)?;
            drop(scene);
            let warm = read_pixels(&self.device, &self.queue, &texture).await?;
            let result = Array::new();
            result.push(&cold);
            result.push(&warm);
            Ok(result)
        }
    }

    fn validation_scene(contrast: i32) -> Result<Scene, JsValue> {
        let points = [[4.25, 4.5], [20.75, 4.5], [20.75, 20.5], [4.25, 20.5]];
        let path = if contrast == -4 {
            super::font_scene::varied_true_type_rectangle().map_err(error)?
        } else if contrast == -3 {
            super::font_scene::cff2_rectangle().map_err(error)?
        } else if contrast == -2 {
            super::font_scene::cff_rectangle().map_err(error)?
        } else {
            Path::new(
                (0..4)
                    .map(|i| Segment::line(points[i], points[(i + 1) % 4]))
                    .collect::<Vec<_>>(),
                [4.25, 4.5, 20.75, 20.5],
                FillRule::NonZero,
            )
            .map_err(error)?
        };
        let mut scene = Scene::default();
        if contrast >= 0 {
            let contrast = u8::try_from(contrast).map_err(error)?;
            scene
                .draw_path(PathDraw {
                    path,
                    transform: Affine::IDENTITY,
                    color: [1.0; 4],
                    sampling: PathSampling::default()
                        .with_grayscale_contrast(contrast)
                        .map_err(error)?,
                })
                .map_err(error)?;
        } else if (-4..=-1).contains(&contrast) {
            scene
                .draw_path(PathDraw {
                    path: path.clone(),
                    transform: Affine::IDENTITY,
                    color: [1.0, 0.0, 0.0, 0.6],
                    sampling: PathSampling::default(),
                })
                .map_err(error)?;
            let mut rectangle =
                AnalyticPrimitive::rectangle([12.0, 8.0, 20.0, 20.0], [0.0, 0.0, 1.0, 0.4]);
            rectangle.aliased = true;
            scene.draw_analytic(rectangle).map_err(error)?;
            scene
                .draw_path(PathDraw {
                    path,
                    transform: Affine([1.0, 0.0, 0.0, 1.0, 12.0, -2.0]),
                    color: [0.0, 1.0, 0.0, 0.7],
                    sampling: PathSampling::default(),
                })
                .map_err(error)?;
        } else {
            return Err(error("invalid validation contrast"));
        }
        Ok(scene)
    }

    async fn read_pixels(
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        texture: &wgpu::Texture,
    ) -> Result<Uint8Array, JsValue> {
        let buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("browser async pixel readback"),
            size: 64 * 256,
            usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let mut encoder = device.create_command_encoder(&Default::default());
        encoder.copy_texture_to_buffer(
            texture.as_image_copy(),
            wgpu::TexelCopyBufferInfo {
                buffer: &buffer,
                layout: wgpu::TexelCopyBufferLayout {
                    offset: 0,
                    bytes_per_row: Some(256),
                    rows_per_image: Some(64),
                },
            },
            texture.size(),
        );
        queue.submit([encoder.finish()]);
        let promise = Promise::new(&mut |resolve, reject| {
            buffer
                .slice(..)
                .map_async(wgpu::MapMode::Read, move |result| match result {
                    Ok(()) => {
                        let _ = resolve.call0(&JsValue::NULL);
                    }
                    Err(problem) => {
                        let _ = reject.call1(&JsValue::NULL, &error(problem));
                    }
                });
        });
        JsFuture::from(promise).await?;
        let bytes = buffer.slice(..).get_mapped_range().map_err(error)?.to_vec();
        buffer.unmap();
        Ok(Uint8Array::from(bytes.as_slice()))
    }
}
