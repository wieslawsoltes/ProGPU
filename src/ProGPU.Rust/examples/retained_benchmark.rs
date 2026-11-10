//! Reproducible cold preparation and warm CPU submission benchmark.
//! Run in Release; this is a baseline, not a cross-language speedup claim.

use progpu::{
    geometry::{Affine, AnalyticPrimitive, Mesh, Shape},
    path::{FillRule, Path, Segment},
    renderer::{Frame, Renderer},
    scene::{PathDraw, PathSampling, Scene},
};
use std::{
    error::Error,
    time::{Duration, Instant},
};

fn main() -> Result<(), Box<dyn Error>> {
    pollster::block_on(run())
}

async fn run() -> Result<(), Box<dyn Error>> {
    let arguments: Vec<_> = std::env::args().skip(1).collect();
    let mixed = match arguments.as_slice() {
        [] => false,
        [argument] if argument == "--paths" => true,
        _ => return Err("use --paths for the mixed retained workload".into()),
    };
    let instance = wgpu::Instance::new(wgpu::InstanceDescriptor::new_without_display_handle());
    let adapter = instance
        .request_adapter(&wgpu::RequestAdapterOptions::default())
        .await?;
    eprintln!("adapter={:?}", adapter.get_info());
    let (device, queue) = adapter
        .request_device(&wgpu::DeviceDescriptor::default())
        .await?;
    let cold = Instant::now();
    let mut renderer = Renderer::new(&device, &queue, wgpu::TextureFormat::Rgba8Unorm).await?;
    let pipeline_ms = cold.elapsed().as_secs_f64() * 1000.0;
    let cold = Instant::now();
    let mut mesh = Mesh::default();
    let mut source = Scene::default();
    let path = Path::new(
        vec![
            Segment::quadratic([[0.0, 12.0], [6.0, -12.0], [12.0, 12.0]]),
            Segment::line([12.0, 12.0], [0.0, 12.0]),
        ],
        [0.0, -12.0, 12.0, 12.0],
        FillRule::NonZero,
    )?;
    for index in 0..4096 {
        let mut primitive = AnalyticPrimitive::rectangle(
            [
                (index % 64) as f32 * 16.0,
                (index / 64) as f32 * 16.0,
                12.0,
                12.0,
            ],
            [0.25, 0.75, 1.0, 0.8],
        );
        primitive.shape = [Shape::Rectangle, Shape::Ellipse, Shape::RoundedRectangle][index % 3];
        primitive.corner_radius = 3.0;
        if mixed && index % 2 == 0 {
            source.draw_path(PathDraw {
                path: path.clone(),
                transform: Affine([1.0, 0.0, 0.0, 1.0, primitive.bounds[0], primitive.bounds[1]]),
                color: primitive.color,
                sampling: PathSampling::default(),
            })?;
        } else if mixed {
            source.draw_analytic(primitive)?;
        } else {
            mesh.push(&primitive, 1.5)?;
        }
    }
    let frame = Frame {
        width: 1024,
        height: 1024,
        dpi_scale: 1.0,
    };
    let scene = if mixed {
        renderer.prepare_scene(&source, frame).await?
    } else {
        renderer.prepare(&mesh, frame)?
    };
    let preparation_ms = cold.elapsed().as_secs_f64() * 1000.0;
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("benchmark target"),
        size: wgpu::Extent3d {
            width: 1024,
            height: 1024,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::Rgba8Unorm,
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
        view_formats: &[],
    });
    let view = texture.create_view(&Default::default());
    let wait = || {
        device.poll(wgpu::PollType::Wait {
            submission_index: None,
            timeout: Some(Duration::from_secs(30)),
        })
    };
    for _ in 0..120 {
        renderer.render(&scene, &view, wgpu::Color::BLACK)?;
        wait()?;
    }
    let mut samples = Vec::with_capacity(480);
    let total = Instant::now();
    for _ in 0..480 {
        let start = Instant::now();
        renderer.render(&scene, &view, wgpu::Color::BLACK)?;
        samples.push(start.elapsed().as_secs_f64() * 1_000_000.0);
        // Bound in-flight memory and separate CPU submission latency from the
        // end-to-end serial frame rate. This is not a GPU timestamp measurement.
        wait()?;
    }
    let elapsed = total.elapsed().as_secs_f64();
    samples.sort_by(f64::total_cmp);
    println!(
        "mixed_paths={mixed} primitives=4096 warmup=120 frames=480 pipeline_ms={pipeline_ms:.3} preparation_ms={preparation_ms:.3}"
    );
    println!(
        "cpu_submit_us p50={:.3} p95={:.3} p99={:.3} serial_fps={:.2}",
        samples[240],
        samples[456],
        samples[475],
        480.0 / elapsed
    );
    println!(
        "cold_geometry_bytes={} cold_uniform_bytes={} draws_per_frame={} warm_geometry_upload_bytes=0 warm_uniform_upload_bytes=0",
        scene.metrics().geometry_bytes,
        scene.metrics().uniform_bytes,
        scene.metrics().draws
    );
    println!(
        "cold_path_rasters={} atlas_bytes={} warm_path_rasters=0",
        scene.metrics().rasterized_paths,
        scene.metrics().atlas_bytes
    );
    Ok(())
}
