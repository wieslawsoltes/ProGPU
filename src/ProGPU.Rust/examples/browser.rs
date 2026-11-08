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
        let path = if contrast == -2 {
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
        } else if contrast == -1 || contrast == -2 {
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
