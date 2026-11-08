//! WebAssembly/WebGPU canvas host and GPU-readback validation bridge.
//! This example uses the same production renderer and shared scene as winit.

#[cfg(target_arch = "wasm32")]
#[path = "support/font_scene.rs"]
mod font_scene;
#[cfg(target_arch = "wasm32")]
#[path = "support/sample_scene.rs"]
mod sample;

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
