//! A native window validating the retained ProGPU Rust/wgpu renderer.

use progpu::renderer::{Frame, Renderer};
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
#[path = "support/axis_clip_scene.rs"]
mod axis_clip_sample;
#[path = "support/backdrop_blend_scene.rs"]
mod backdrop_blend_sample;
#[path = "support/cached_picture_scene.rs"]
mod cached_picture_sample;
#[path = "support/clear_scene.rs"]
mod clear_sample;
#[path = "support/color_filter_scene.rs"]
mod color_filter_sample;
#[path = "support/compositor_scene.rs"]
mod compositor_sample;
#[path = "support/effect_scene.rs"]
mod effect_sample;
#[path = "support/image_effect_scene.rs"]
mod image_effect_sample;
#[path = "support/image_filter_scene.rs"]
mod image_filter_sample;
#[path = "support/image_scene.rs"]
mod image_sample;
#[path = "support/layer_scene.rs"]
mod layer_sample;
#[path = "support/managed_shader_scene.rs"]
mod managed_shader_sample;
#[path = "support/mask_scene.rs"]
mod mask_sample;
#[path = "support/material_scene.rs"]
mod material_sample;
#[path = "support/materialized_mask_scene.rs"]
mod materialized_mask_sample;
#[path = "support/mesh_scene.rs"]
mod mesh_sample;
#[path = "support/owned_shader_scene.rs"]
mod owned_shader_sample;
#[path = "support/path_effect_scene.rs"]
mod path_effect_sample;
#[path = "support/path_program_scene.rs"]
mod path_program_sample;
#[path = "support/retained_aa_scene.rs"]
mod retained_aa_sample;
#[path = "support/shader_effect_scene.rs"]
mod shader_effect_sample;
#[path = "support/text_scene.rs"]
mod text_sample;

#[path = "support/prepared_frame.rs"]
mod prepared_frame;
#[path = "support/visual_scene.rs"]
mod visual_sample;

type Result<T> = std::result::Result<T, Box<dyn Error>>;

#[derive(Clone, Copy, Default, PartialEq, Eq)]
enum CompositeGallery {
    #[default]
    None,
    Layers,
    Clears,
    AxisClips,
    RetainedAa,
    ImageEffects,
    MaterializedMasks,
    PathPrograms,
    Compositor,
    ShaderEffects,
    ManagedShaders,
    ColorFilters,
    ImageFilters,
    OwnedShaders,
    CachedPictures,
    PathEffects,
    Meshes,
    BackdropBlends,
    Visuals,
}

struct WindowRenderer {
    window: Arc<Window>,
    instance: wgpu::Instance,
    surface: wgpu::Surface<'static>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    config: wgpu::SurfaceConfiguration,
    renderer: Renderer,
    scene: Option<prepared_frame::PreparedFrame>,
    visual_root: Option<progpu::composition::visual::Visual>,
    material_scene: bool,
    text_font: Option<progpu::font::ManagedFont>,
    image_scene: bool,
    mask_scene: bool,
    effect_scene: bool,
    composite_gallery: CompositeGallery,
}

impl WindowRenderer {
    async fn new(
        event_loop: &ActiveEventLoop,
        material_scene: bool,
        text_font: Option<progpu::font::ManagedFont>,
        image_scene: bool,
        mask_scene: bool,
        effect_scene: bool,
        composite_gallery: CompositeGallery,
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
            visual_root: None,
            material_scene,
            text_font,
            image_scene,
            mask_scene,
            effect_scene,
            composite_gallery,
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
        if self.composite_gallery == CompositeGallery::Visuals {
            if self.visual_root.is_none() {
                self.visual_root = Some(visual_sample::visual_scene()?);
            }
            let root = self
                .visual_root
                .as_ref()
                .ok_or("missing visual generation")?;
            let prepared = pollster::block_on(self.renderer.prepare_visual(root, frame))?;
            let scene = prepared_frame::PreparedFrame::from(prepared);
            eprintln!(
                "visual preparation: {:?}; replay: {:?}",
                scene.visual_metrics(),
                scene.replay_metrics()
            );
            self.config.width = size.width;
            self.config.height = size.height;
            self.surface.configure(&self.device, &self.config);
            self.scene = Some(scene);
            self.window.request_redraw();
            return Ok(());
        }
        let source = if self.composite_gallery == CompositeGallery::AxisClips {
            let (scene, metrics) = pollster::block_on(axis_clip_sample::axis_clip_scene(
                &mut self.renderer,
                &self.device,
                &self.queue,
                dpi,
            ))?;
            eprintln!(
                "AA groups: {}; textures: {}; uniforms: {}; geometry: {}; resolve draws: {}; background copies: {}; source draws: {}; source uniforms: {}; loaded copies: {}",
                metrics.groups,
                metrics.texture_bytes,
                metrics.uniform_bytes,
                metrics.geometry_bytes,
                metrics.resolve_draws,
                metrics.background_copy_bytes,
                metrics.source_draws,
                metrics.source_uniform_bytes,
                metrics.loaded_copy_bytes
            );
            scene
        } else if self.composite_gallery == CompositeGallery::Compositor {
            pollster::block_on(compositor_sample::compositor_scene(
                &mut self.renderer,
                &self.device,
                &self.queue,
                dpi,
            ))?
        } else if self.composite_gallery == CompositeGallery::ShaderEffects {
            pollster::block_on(shader_effect_sample::shader_effect_scene(
                &mut self.renderer,
                &self.device,
                &self.queue,
                dpi,
                [frame.width, frame.height],
            ))?
        } else if self.composite_gallery == CompositeGallery::ManagedShaders {
            pollster::block_on(managed_shader_sample::managed_shader_scene(
                &mut self.renderer,
                &self.device,
                &self.queue,
                dpi,
                [frame.width, frame.height],
            ))?
        } else if self.composite_gallery == CompositeGallery::ImageFilters {
            pollster::block_on(image_filter_sample::image_filter_scene())?
        } else if self.composite_gallery == CompositeGallery::BackdropBlends {
            pollster::block_on(backdrop_blend_sample::backdrop_blend_scene(
                &mut self.renderer,
                dpi,
            ))?
        } else if self.composite_gallery == CompositeGallery::Meshes {
            mesh_sample::mesh_scene()?
        } else if self.composite_gallery == CompositeGallery::PathEffects {
            path_effect_sample::path_effect_scene()?
        } else if self.composite_gallery == CompositeGallery::CachedPictures {
            cached_picture_sample::cached_picture_scene()?
        } else if self.composite_gallery == CompositeGallery::OwnedShaders {
            pollster::block_on(owned_shader_sample::owned_shader_scene())?
        } else if self.composite_gallery == CompositeGallery::ColorFilters {
            pollster::block_on(color_filter_sample::color_filter_scene(
                &mut self.renderer,
                &self.device,
                &self.queue,
                dpi,
                [frame.width, frame.height],
            ))?
        } else if self.composite_gallery == CompositeGallery::PathPrograms {
            pollster::block_on(path_program_sample::path_program_scene(
                &self.device,
                &self.queue,
                dpi,
            ))?
        } else if self.composite_gallery == CompositeGallery::MaterializedMasks {
            let (scene, metrics) =
                pollster::block_on(materialized_mask_sample::materialized_mask_scene(
                    &mut self.renderer,
                    &self.device,
                    &self.queue,
                    dpi,
                ))?;
            eprintln!("mask sources: {metrics:?}");
            scene
        } else if self.composite_gallery == CompositeGallery::ImageEffects {
            image_effect_sample::image_effect_scene(
                &self.device,
                &self.queue,
                [frame.width, frame.height],
            )?
        } else if self.composite_gallery == CompositeGallery::RetainedAa {
            retained_aa_sample::retained_aa_scene(self.text_font.clone())?
        } else if self.composite_gallery == CompositeGallery::Clears {
            clear_sample::clear_scene(self.text_font.clone())?
        } else if self.composite_gallery == CompositeGallery::Layers {
            layer_sample::layer_scene(self.text_font.clone())?
        } else if self.effect_scene {
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
        eprintln!(
            "prepared: {:?}; layers: {:?}; advanced blends: {:?}; retained AA: {:?}; image effects: {:?}; mask composition: {:?}; per replay: {:?}",
            scene.metrics(),
            scene.layer_metrics(),
            scene.advanced_blend_metrics(),
            scene.retained_aa_metrics(),
            scene.image_effect_metrics(),
            scene.mask_composition_metrics(),
            scene.replay_metrics()
        );
        self.config.width = size.width;
        self.config.height = size.height;
        self.surface.configure(&self.device, &self.config);
        self.scene = Some(scene.into());
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
    composite_gallery: CompositeGallery,
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
                self.composite_gallery,
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
            "--layers" => app.composite_gallery = CompositeGallery::Layers,
            "--clears" => app.composite_gallery = CompositeGallery::Clears,
            "--axis-clips" => app.composite_gallery = CompositeGallery::AxisClips,
            "--retained-aa" => app.composite_gallery = CompositeGallery::RetainedAa,
            "--compositor" => app.composite_gallery = CompositeGallery::Compositor,
            "--shader-effects" => app.composite_gallery = CompositeGallery::ShaderEffects,
            "--managed-shaders" => app.composite_gallery = CompositeGallery::ManagedShaders,
            "--color-filters" => app.composite_gallery = CompositeGallery::ColorFilters,
            "--image-filters" => app.composite_gallery = CompositeGallery::ImageFilters,
            "--owned-shaders" => app.composite_gallery = CompositeGallery::OwnedShaders,
            "--cached-pictures" => app.composite_gallery = CompositeGallery::CachedPictures,
            "--meshes" => app.composite_gallery = CompositeGallery::Meshes,
            "--backdrop-blends" => app.composite_gallery = CompositeGallery::BackdropBlends,
            "--path-effects" => app.composite_gallery = CompositeGallery::PathEffects,
            "--visuals" => app.composite_gallery = CompositeGallery::Visuals,
            "--path-programs" => app.composite_gallery = CompositeGallery::PathPrograms,
            "--materialized-masks" => app.composite_gallery = CompositeGallery::MaterializedMasks,
            "--image-effects" => app.composite_gallery = CompositeGallery::ImageEffects,
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
                    "unknown argument {argument}; use --materials, --images, --masks, --effects, --layers, --clears, --axis-clips, --retained-aa, --image-effects, --materialized-masks, --path-programs, --compositor, --shader-effects, --managed-shaders, --color-filters, --image-filters, --owned-shaders, --cached-pictures, --path-effects, --meshes, --backdrop-blends, --visuals, --text FONT_PATH, or --frames N"
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
