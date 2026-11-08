//! Actual-device pixel, replay and submission-ownership controls.

use progpu::{
    geometry::{AnalyticPrimitive, Mesh, Shape},
    renderer::{Frame, Renderer},
};

fn read_pixels(device: &wgpu::Device, queue: &wgpu::Queue, texture: &wgpu::Texture) -> Vec<u8> {
    let pixel_bytes = match texture.format() {
        wgpu::TextureFormat::R8Unorm => 1,
        wgpu::TextureFormat::Rgba8Unorm => 4,
        _ => panic!("unsupported test texture format"),
    };
    let row_bytes = texture.width() * pixel_bytes;
    let pitch = row_bytes.div_ceil(256) * 256;
    let buffer = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("pixel test readback"),
        size: u64::from(pitch) * u64::from(texture.height()),
        usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        mapped_at_creation: false,
    });
    let mut encoder = device.create_command_encoder(&Default::default());
    encoder.copy_texture_to_buffer(
        texture.as_image_copy(),
        wgpu::TexelCopyBufferInfo {
            buffer: &buffer,
            layout: wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(pitch),
                rows_per_image: Some(texture.height()),
            },
        },
        texture.size(),
    );
    queue.submit([encoder.finish()]);
    let (send, receive) = std::sync::mpsc::channel();
    buffer
        .slice(..)
        .map_async(wgpu::MapMode::Read, move |result| {
            send.send(result).unwrap();
        });
    device
        .poll(wgpu::PollType::Wait {
            submission_index: None,
            timeout: Some(std::time::Duration::from_secs(20)),
        })
        .unwrap();
    receive
        .recv_timeout(std::time::Duration::from_secs(20))
        .unwrap()
        .unwrap();
    let bytes = buffer
        .slice(..)
        .get_mapped_range()
        .unwrap()
        .chunks_exact(pitch as usize)
        .flat_map(|row| row[..row_bytes as usize].iter().copied())
        .collect();
    buffer.unmap();
    bytes
}

#[test]
#[ignore = "requires a real GPU adapter; mandatory explicit GPU validation lane"]
fn batched_path_coverage_matches_independent_samples() {
    use progpu::path::{
        ExecutionPath, ExecutionPreference, FillRule, Path, PathRasterizer, RasterRequest, Segment,
    };
    pollster::block_on(async {
        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor::new_without_display_handle());
        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions::default())
            .await
            .expect("GPU adapter required");
        let (device, queue) = adapter
            .request_device(&wgpu::DeviceDescriptor::default())
            .await
            .unwrap();
        for preference in [
            ExecutionPreference::GpuShader,
            ExecutionPreference::CpuSimd,
            ExecutionPreference::ScalarReference,
        ] {
            assert!(matches!(
                PathRasterizer::new(&device, &queue, preference).await,
                Err(progpu::Error::UnsupportedExecution)
            ));
        }
        let rasterizer = PathRasterizer::new(&device, &queue, ExecutionPreference::NativeCompute)
            .await
            .unwrap();
        let corners = [[4.0, 4.0], [16.0, 4.0], [16.0, 16.0], [4.0, 16.0]];
        let lines: Vec<_> = (0..4)
            .map(|i| Segment::line(corners[i], corners[(i + 1) % 4]))
            .collect();
        let mut paths = Vec::new();
        for kind in [0, 1, 2, 4, 5] {
            let segments: Vec<_> = lines
                .iter()
                .map(|line| {
                    let [start, end, _, _] = line.points;
                    let middle = [(start[0] + end[0]) * 0.5, (start[1] + end[1]) * 0.5];
                    let control1 = [
                        start[0] + (end[0] - start[0]) / 3.0,
                        start[1] + (end[1] - start[1]) / 3.0,
                    ];
                    let control2 = [
                        start[0] + (end[0] - start[0]) * (2.0 / 3.0),
                        start[1] + (end[1] - start[1]) * (2.0 / 3.0),
                    ];
                    let mut segment = match kind {
                        0 => *line,
                        1 | 4 => Segment::quadratic([start, middle, end]),
                        _ => Segment::cubic([start, control1, control2, end]),
                    };
                    segment.kind = kind;
                    if kind >= 4 {
                        segment.parameters[0] = 1.0f32.to_bits();
                    }
                    if kind == 5 {
                        segment.parameters[1] = 1.0f32.to_bits();
                    }
                    segment
                })
                .collect();
            paths.push(Path::new(segments, [4.0, 4.0, 16.0, 16.0], FillRule::NonZero).unwrap());
        }
        let doubled: Vec<_> = lines.iter().chain(&lines).copied().collect();
        paths.push(Path::new(doubled.clone(), [4.0, 4.0, 16.0, 16.0], FillRule::EvenOdd).unwrap());
        paths.push(Path::new(doubled, [4.0, 4.0, 16.0, 16.0], FillRule::NonZero).unwrap());
        let reversed: Vec<_> = lines
            .iter()
            .rev()
            .map(|s| Segment::line(s.points[1], s.points[0]))
            .collect();
        paths.push(Path::new(reversed, [4.0, 4.0, 16.0, 16.0], FillRule::NonZero).unwrap());
        let arcs = vec![
            Segment {
                points: [[12.0, 8.0], [4.0, 8.0], [8.0, 8.0], [4.0, 4.0]],
                kind: 3,
                parameters: [0.0f32.to_bits(), std::f32::consts::PI.to_bits(), 0],
            },
            Segment {
                points: [[4.0, 8.0], [12.0, 8.0], [8.0, 8.0], [4.0, 4.0]],
                kind: 3,
                parameters: [
                    std::f32::consts::PI.to_bits(),
                    std::f32::consts::PI.to_bits(),
                    0,
                ],
            },
        ];
        paths.push(Path::new(arcs, [4.0, 4.0, 12.0, 12.0], FillRule::NonZero).unwrap());
        let requests: Vec<_> = paths
            .iter()
            .enumerate()
            .map(|(index, path)| {
                (
                    path,
                    RasterRequest {
                        origin: [0.25, 0.5],
                        scale: [1.5, 0.75],
                        size: [if index % 2 == 0 { 31 } else { 65 }, 23],
                        sample_grid: 8,
                    },
                )
            })
            .collect();
        let mut invalid = requests.clone();
        invalid.last_mut().unwrap().1.sample_grid = 0;
        assert!(rasterizer.rasterize_batch(&invalid).is_err());
        let masks = rasterizer.rasterize_batch(&requests).unwrap();
        assert_eq!(masks.len(), paths.len());
        drop(rasterizer);
        for (index, mask) in masks.iter().enumerate() {
            assert_eq!(mask.execution_path(), ExecutionPath::NativeCompute);
            let pixels = read_pixels(&device, &queue, mask.texture());
            let request = mask.request();
            for y in 0..request.size[1] {
                for x in 0..request.size[0] {
                    let mut samples = 0u32;
                    for sy in 0..8 {
                        for sx in 0..8 {
                            let px = (request.origin[0] + x as f32 + (sx as f32 + 0.5) / 8.0)
                                / request.scale[0];
                            let py = (request.origin[1] + y as f32 + (sy as f32 + 0.5) / 8.0)
                                / request.scale[1];
                            let inside = if index == 5 {
                                false
                            } else if index == 8 {
                                (px - 8.0).powi(2) + (py - 8.0).powi(2) < 16.0
                            } else {
                                (4.0..16.0).contains(&px) && (4.0..16.0).contains(&py)
                            };
                            samples += u32::from(inside);
                        }
                    }
                    let expected = (samples as f32 * (255.0 / 64.0)).round() as u8;
                    assert_eq!(
                        pixels[(y * request.size[0] + x) as usize],
                        expected,
                        "path {index} pixel ({x},{y})"
                    );
                }
            }
        }
    });
}

#[test]
#[ignore = "requires a real GPU adapter; mandatory explicit GPU validation lane"]
fn retained_analytic_pixels_and_lifetimes() {
    pollster::block_on(async {
        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor::new_without_display_handle());
        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions::default())
            .await
            .expect("GPU adapter required");
        eprintln!("GPU adapter: {:?}", adapter.get_info());
        let (device, queue) = adapter
            .request_device(&wgpu::DeviceDescriptor::default())
            .await
            .unwrap();
        for &(name, source) in progpu::shaders::MODULES {
            let scope = device.push_error_scope(wgpu::ErrorFilter::Validation);
            let _module = device.create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some(name),
                source: wgpu::ShaderSource::Wgsl(source.into()),
            });
            assert!(scope.pop().await.is_none(), "{name}");
        }
        let renderer = Renderer::new(&device, &queue, wgpu::TextureFormat::Rgba8Unorm)
            .await
            .unwrap();
        let mut mesh = Mesh::default();
        let mut red = AnalyticPrimitive::rectangle([4.0, 4.0, 24.0, 24.0], [1.0, 0.0, 0.0, 1.0]);
        red.aliased = true;
        mesh.push(&red, 1.5).unwrap();
        let mut blue = AnalyticPrimitive::rectangle([16.0, 16.0, 24.0, 24.0], [0.0, 0.0, 1.0, 0.5]);
        blue.aliased = true;
        mesh.push(&blue, 1.5).unwrap();
        let mut ellipse =
            AnalyticPrimitive::rectangle([44.0, 4.0, 16.0, 16.0], [0.0, 1.0, 0.0, 1.0]);
        ellipse.shape = Shape::Ellipse;
        mesh.push(&ellipse, 1.5).unwrap();
        let mut rounded =
            AnalyticPrimitive::rectangle([44.0, 32.0, 16.0, 16.0], [1.0, 1.0, 0.0, 1.0]);
        rounded.shape = Shape::RoundedRectangle;
        rounded.corner_radius = 6.0;
        mesh.push(&rounded, 1.5).unwrap();
        let frame = Frame {
            width: 64,
            height: 64,
            dpi_scale: 1.0,
        };
        let scene = renderer.prepare(&mesh, frame).unwrap();
        assert!(
            renderer
                .prepare(&mesh, Frame { width: 0, ..frame })
                .is_err()
        );
        assert_eq!(scene.metrics().geometry_bytes, 4 * (4 * 56 + 6 * 4));
        assert_eq!(scene.metrics().uniform_bytes, 224);
        assert_eq!(scene.metrics().draws, 1);
        // Prepare another physical frame before replaying the first: no shared
        // mutable uniform can move already prepared geometry.
        let other_frame = renderer
            .prepare(
                &mesh,
                Frame {
                    dpi_scale: 2.0,
                    ..frame
                },
            )
            .unwrap();
        assert_eq!(scene.frame(), frame);
        drop(other_frame);
        drop(mesh);
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("pixel test target"),
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
        let other_renderer = Renderer::new(&device, &queue, wgpu::TextureFormat::Rgba8Unorm)
            .await
            .unwrap();
        assert!(matches!(
            other_renderer.render(&scene, &view, wgpu::Color::TRANSPARENT),
            Err(progpu::Error::ResourceDomain)
        ));
        drop(other_renderer);
        renderer
            .render(&scene, &view, wgpu::Color::TRANSPARENT)
            .unwrap();
        let first = read_pixels(&device, &queue, &texture);
        let pixel = |x: usize, y: usize| &first[(y * 64 + x) * 4..(y * 64 + x + 1) * 4];
        assert_eq!(pixel(0, 0), [0, 0, 0, 0]);
        assert_eq!(pixel(4, 4), [255, 0, 0, 255]);
        assert_eq!(pixel(27, 8), [255, 0, 0, 255]);
        assert_eq!(pixel(28, 8), [0, 0, 0, 0]);
        for (actual, expected) in pixel(20, 20).iter().zip([127i16, 0, 128, 255]) {
            assert!((i16::from(*actual) - expected).abs() <= 1);
        }
        assert_eq!(pixel(52, 12), [0, 255, 0, 255]);
        assert_eq!(pixel(44, 4), [0, 0, 0, 0]);
        assert_eq!(pixel(52, 40), [255, 255, 0, 255]);
        assert_eq!(pixel(44, 32), [0, 0, 0, 0]);
        // Independent full-region source-over oracle, including every boundary
        // and untouched row; tolerate only unorm conversion rounding.
        for y in 0..64 {
            for x in 0..43 {
                let red = (4..28).contains(&x) && (4..28).contains(&y);
                let blue = (16..40).contains(&x) && (16..40).contains(&y);
                let expected = match (red, blue) {
                    (false, false) => [0i16, 0, 0, 0],
                    (true, false) => [255, 0, 0, 255],
                    (false, true) => [0, 0, 128, 128],
                    (true, true) => [127, 0, 128, 255],
                };
                for (actual, expected) in pixel(x, y).iter().zip(expected) {
                    assert!(
                        (i16::from(*actual) - expected).abs() <= 1,
                        "pixel ({x},{y})"
                    );
                }
            }
        }
        renderer
            .render(&scene, &view, wgpu::Color::TRANSPARENT)
            .unwrap();
        // Encoded GPU resources must remain alive beyond both owner drops.
        drop(scene);
        drop(renderer);
        let second = read_pixels(&device, &queue, &texture);
        assert_eq!(
            first, second,
            "warm replay must preserve the complete frame"
        );
    });
}
