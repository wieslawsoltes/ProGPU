//! A native window validating the retained ProGPU Rust/wgpu renderer.

use progpu::renderer::{Frame, PreparedScene, Renderer};
use std::{error::Error, sync::Arc};
use winit::{
    application::ApplicationHandler,
    event::WindowEvent,
    event_loop::{ActiveEventLoop, EventLoop},
    window::{Window, WindowId},
};

#[path = "support/sample_scene.rs"]
mod sample;
use sample::sample_scene;
#[path = "support/effect_scene.rs"]
mod effect_sample;
#[path = "support/image_scene.rs"]
mod image_sample;
#[path = "support/mask_scene.rs"]
mod mask_sample;
#[path = "support/material_scene.rs"]
mod material_sample;
#[path = "support/text_scene.rs"]
mod text_sample;

type Result<T> = std::result::Result<T, Box<dyn Error>>;

struct WindowRenderer {
    window: Arc<Window>,
    instance: wgpu::Instance,
    surface: wgpu::Surface<'static>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    config: wgpu::SurfaceConfiguration,
    renderer: Renderer,
    scene: Option<PreparedScene>,
    material_scene: bool,
    text_font: Option<progpu::font::ManagedFont>,
    image_scene: bool,
    mask_scene: bool,
    effect_scene: bool,
}

impl WindowRenderer {
    async fn new(
        event_loop: &ActiveEventLoop,
        material_scene: bool,
        text_font: Option<progpu::font::ManagedFont>,
        image_scene: bool,
        mask_scene: bool,
        effect_scene: bool,
    ) -> Result<Self> {
        let window = Arc::new(
            event_loop.create_window(
                Window::default_attributes()
                    .with_title("ProGPU Rust · canonical WebGPU vector rendering")
                    .with_inner_size(winit::dpi::LogicalSize::new(960.0, 640.0))
                    .with_visible(false),
            )?,
        );
        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor::new_with_display_handle(
            Box::new(event_loop.owned_display_handle()),
        ));
        let surface = instance.create_surface(window.clone())?;
        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions {
                compatible_surface: Some(&surface),
                ..Default::default()
            })
            .await?;
        eprintln!("adapter: {:?}", adapter.get_info());
        let (device, queue) = adapter
            .request_device(&wgpu::DeviceDescriptor::default())
            .await?;
        let size = window.inner_size();
        let mut config = surface
            .get_default_config(&adapter, size.width.max(1), size.height.max(1))
            .ok_or("surface has no supported configuration")?;
        // The original ProGPU color contract uses unorm targets. Prefer those
        // when the surface advertises them, without changing shader color math.
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
        config.present_mode = wgpu::PresentMode::Fifo;
        let mut renderer = Renderer::new(&device, &queue, config.format).await?;
        renderer.configure_image_sampling(Default::default(), &adapter.get_info())?;
        let mut state = Self {
            window,
            instance,
            surface,
            device,
            queue,
            config,
            renderer,
            scene: None,
            material_scene,
            text_font,
            image_scene,
            mask_scene,
            effect_scene,
        };
        state.resize()?;
        state.window.set_visible(true);
        state.window.request_redraw();
        Ok(state)
    }

    fn resize(&mut self) -> Result<()> {
        let size = self.window.inner_size();
        if size.width == 0 || size.height == 0 {
            self.scene = None;
            return Ok(());
        }
        let dpi = self.window.scale_factor() as f32;
        let frame = Frame {
            width: size.width,
            height: size.height,
            dpi_scale: dpi,
        };
        let source = if self.effect_scene {
            let (scene, metrics) = pollster::block_on(effect_sample::effect_scene(
                &mut self.renderer,
                dpi,
                self.text_font.clone(),
            ))?;
            eprintln!(
                "captures: {}; capture bytes: {}; effect bytes: {}; effect uniform bytes: {}; effect passes: {}",
                metrics.captures,
                metrics.capture_bytes,
                metrics.effect_bytes,
                metrics.effect_uniform_bytes,
                metrics.effect_passes
            );
            scene
        } else if self.mask_scene {
            mask_sample::mask_scene(self.text_font.clone())?
        } else if let Some(font) = &self.text_font {
            text_sample::text_scene(font.clone())?
        } else if self.image_scene {
            image_sample::image_scene()?
        } else if self.material_scene {
            material_sample::material_scene()?
        } else {
            sample_scene()?
        };
        let scene = pollster::block_on(self.renderer.prepare_scene(&source, frame))?;
        eprintln!("prepared: {:?}", scene.metrics());
        self.config.width = size.width;
        self.config.height = size.height;
        self.surface.configure(&self.device, &self.config);
        self.scene = Some(scene);
        self.window.request_redraw();
        Ok(())
    }

    fn draw(&mut self) -> Result<bool> {
        let Some(scene) = self.scene.as_ref() else {
            return Ok(false);
        };
        let (texture, suboptimal) = match self.surface.get_current_texture() {
            wgpu::CurrentSurfaceTexture::Success(texture) => (texture, false),
            wgpu::CurrentSurfaceTexture::Suboptimal(texture) => (texture, true),
            wgpu::CurrentSurfaceTexture::Timeout | wgpu::CurrentSurfaceTexture::Occluded => {
                return Ok(false);
            }
            wgpu::CurrentSurfaceTexture::Outdated => {
                self.resize()?;
                return Ok(false);
            }
            wgpu::CurrentSurfaceTexture::Lost => {
                self.surface = self.instance.create_surface(self.window.clone())?;
                self.resize()?;
                return Ok(false);
            }
            wgpu::CurrentSurfaceTexture::Validation => {
                return Err("surface acquisition validation error".into());
            }
        };
        let view = texture.texture.create_view(&Default::default());
        self.renderer.render(
            scene,
            &view,
            wgpu::Color {
                r: 0.025,
                g: 0.035,
                b: 0.06,
                a: 1.0,
            },
        )?;
        self.window.pre_present_notify();
        self.queue.present(texture);
        if suboptimal {
            self.resize()?;
        }
        Ok(true)
    }
}

#[derive(Default)]
struct App {
    state: Option<WindowRenderer>,
    frame_limit: Option<u64>,
    frames: u64,
    failure: Option<String>,
    material_scene: bool,
    text_font: Option<progpu::font::ManagedFont>,
    image_scene: bool,
    mask_scene: bool,
    effect_scene: bool,
}

impl App {
    fn fail(&mut self, event_loop: &ActiveEventLoop, error: impl std::fmt::Display) {
        self.failure = Some(error.to_string());
        event_loop.exit();
    }
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.state.is_none() {
            match pollster::block_on(WindowRenderer::new(
                event_loop,
                self.material_scene,
                self.text_font.clone(),
                self.image_scene,
                self.mask_scene,
                self.effect_scene,
            )) {
                Ok(state) => self.state = Some(state),
                Err(error) => self.fail(event_loop, error),
            }
        }
    }

    fn suspended(&mut self, _event_loop: &ActiveEventLoop) {
        self.state = None;
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, id: WindowId, event: WindowEvent) {
        let Some(state) = self.state.as_mut() else {
            return;
        };
        if state.window.id() != id {
            return;
        }
        let result = match event {
            WindowEvent::CloseRequested => {
                event_loop.exit();
                Ok(())
            }
            WindowEvent::Resized(_) | WindowEvent::ScaleFactorChanged { .. } => state.resize(),
            WindowEvent::RedrawRequested => match state.draw() {
                Ok(presented) => {
                    self.frames += u64::from(presented);
                    if let Some(limit) = self.frame_limit {
                        if self.frames >= limit {
                            eprintln!("presented_frames={}", self.frames);
                            event_loop.exit();
                        } else {
                            state.window.request_redraw();
                        }
                    }
                    Ok(())
                }
                Err(error) => Err(error),
            },
            _ => Ok(()),
        };
        if let Err(error) = result {
            self.fail(event_loop, error);
        }
    }
}

fn main() -> Result<()> {
    let mut app = App::default();
    let mut args = std::env::args().skip(1);
    while let Some(argument) = args.next() {
        match argument.as_str() {
            "--materials" => app.material_scene = true,
            "--images" => app.image_scene = true,
            "--masks" => app.mask_scene = true,
            "--effects" => app.effect_scene = true,
            "--text" => {
                let path = args.next().ok_or("--text requires a font file path")?;
                app.text_font = Some(progpu::font::ManagedFont::new(std::fs::read(path)?, 0)?);
            }
            "--frames" => {
                let frames: u64 = args
                    .next()
                    .ok_or("--frames requires a positive count")?
                    .parse()?;
                if frames == 0 {
                    return Err("--frames must be positive".into());
                }
                app.frame_limit = Some(frames);
            }
            _ => {
                return Err(format!(
                    "unknown argument {argument}; use --materials, --images, --masks, --effects, --text FONT_PATH, or --frames N"
                )
                .into());
            }
        }
    }
    EventLoop::new()?.run_app(&mut app)?;
    if let Some(error) = app.failure {
        return Err(error.into());
    }
    Ok(())
}
