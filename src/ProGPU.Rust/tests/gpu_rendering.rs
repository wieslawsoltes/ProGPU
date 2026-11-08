//! Actual-device pixel, replay and submission-ownership controls.

#[path = "../examples/support/font_scene.rs"]
mod font_scene;

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
fn mixed_retained_scene_matches_painter_order_and_physical_samples() {
    check_mixed_retained_scene(0);
}
#[test]
#[ignore = "requires a real GPU adapter; mandatory CFF-to-GPU pixel validation"]
fn cff_outlines_render_through_retained_gpu_paths() {
    check_mixed_retained_scene(1);
}
#[test]
#[ignore = "requires a real GPU adapter; mandatory variable CFF2-to-GPU validation"]
fn cff2_variable_outlines_render_through_retained_gpu_paths() {
    check_mixed_retained_scene(2);
}
#[test]
#[ignore = "requires a real GPU adapter; mandatory variable TrueType-to-GPU validation"]
fn varied_true_type_outlines_render_through_retained_gpu_paths() {
    check_mixed_retained_scene(3);
}
fn check_mixed_retained_scene(cff: u8) {
    use progpu::{
        geometry::Affine,
        path::{FillRule, Path, Segment},
        scene::{PathDraw, PathSampling, Scene},
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
        let mut renderer = Renderer::new(&device, &queue, wgpu::TextureFormat::Rgba8Unorm)
            .await
            .unwrap();
        assert!(!renderer.path_pipeline_is_initialized());
        let points = [[4.25, 4.5], [20.75, 4.5], [20.75, 20.5], [4.25, 20.5]];
        let path = if cff == 3 {
            font_scene::varied_true_type_rectangle().unwrap()
        } else if cff == 2 {
            font_scene::cff2_rectangle().unwrap()
        } else if cff == 1 {
            font_scene::cff_rectangle().unwrap()
        } else {
            Path::new(
                (0..4)
                    .map(|i| Segment::line(points[i], points[(i + 1) % 4]))
                    .collect::<Vec<_>>(),
                [4.25, 4.5, 20.75, 20.5],
                FillRule::NonZero,
            )
            .unwrap()
        };
        let mut source = Scene::default();
        source
            .draw_path(PathDraw {
                path: path.clone(),
                transform: Affine::IDENTITY,
                color: [1.0, 0.0, 0.0, 0.6],
                sampling: PathSampling::default(),
            })
            .unwrap();
        let mut rectangle =
            AnalyticPrimitive::rectangle([12.0, 8.0, 20.0, 20.0], [0.0, 0.0, 1.0, 0.4]);
        rectangle.aliased = true;
        source.draw_analytic(rectangle).unwrap();
        source
            .draw_path(PathDraw {
                path: path.clone(),
                transform: Affine([1.0, 0.0, 0.0, 1.0, 12.0, -2.0]),
                color: [0.0, 1.0, 0.0, 0.7],
                sampling: PathSampling::default(),
            })
            .unwrap();
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("mixed retained scene target"),
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
        for dpi_scale in [1.0, 1.25, 2.0] {
            let frame = Frame {
                width: 64,
                height: 64,
                dpi_scale,
            };
            let compiled = source.compile(dpi_scale).unwrap();
            assert_eq!(
                compiled.path_raster_count(),
                if dpi_scale == 1.25 { 2 } else { 1 }
            );
            assert!(
                renderer.prepare(compiled.mesh(), frame).is_err(),
                "path mesh cannot use a placeholder atlas"
            );
            assert!(
                renderer
                    .prepare_compiled(
                        &compiled,
                        Frame {
                            dpi_scale: dpi_scale + 1.0,
                            ..frame
                        }
                    )
                    .await
                    .is_err()
            );
            let scene = renderer.prepare_compiled(&compiled, frame).await.unwrap();
            assert!(renderer.path_pipeline_is_initialized());
            assert_eq!(scene.metrics().draws, 1);
            assert_eq!(
                scene.metrics().rasterized_paths as usize,
                compiled.path_raster_count()
            );
            assert_eq!(scene.metrics().atlas_bytes, 1024 * 1024);
            drop(compiled);
            // A later atlas generation must not alter the first prepared scene.
            let later = renderer
                .prepare_scene(
                    &source,
                    Frame {
                        dpi_scale: 3.0,
                        ..frame
                    },
                )
                .await
                .unwrap();
            drop(later);
            renderer
                .render(&scene, &view, wgpu::Color::TRANSPARENT)
                .unwrap();
            let pixels = read_pixels(&device, &queue, &texture);
            for y in 0..64 {
                for x in 0..64 {
                    let coverage = |translation: [f32; 2]| {
                        let mut count = 0;
                        for sy in 0..8 {
                            for sx in 0..8 {
                                let px = (x as f32 + (sx as f32 + 0.5) / 8.0) / dpi_scale
                                    - translation[0];
                                let py = (y as f32 + (sy as f32 + 0.5) / 8.0) / dpi_scale
                                    - translation[1];
                                count += u32::from(
                                    (4.25..20.75).contains(&px) && (4.5..20.5).contains(&py),
                                );
                            }
                        }
                        (count as f32 * 255.0 / 64.0).round() / 255.0
                    };
                    let mut expected = [0.0f32; 4];
                    let mut blend = |color: [f32; 4], coverage: f32| {
                        let alpha = color[3] * coverage;
                        for channel in 0..3 {
                            expected[channel] = ((color[channel] * alpha
                                + expected[channel] * (1.0 - alpha))
                                * 255.0)
                                .round()
                                / 255.0;
                        }
                        expected[3] =
                            ((alpha + expected[3] * (1.0 - alpha)) * 255.0).round() / 255.0;
                    };
                    blend([1.0, 0.0, 0.0, 0.6], coverage([0.0, 0.0]));
                    let px = (x as f32 + 0.5) / dpi_scale;
                    let py = (y as f32 + 0.5) / dpi_scale;
                    blend(
                        [0.0, 0.0, 1.0, 0.4],
                        f32::from((12.0..32.0).contains(&px) && (8.0..28.0).contains(&py)),
                    );
                    blend([0.0, 1.0, 0.0, 0.7], coverage([12.0, -2.0]));
                    for channel in 0..4 {
                        let actual = pixels[(y * 64 + x) * 4 + channel];
                        let expected = (expected[channel] * 255.0).round() as i16;
                        assert!(
                            (i16::from(actual) - expected).abs() <= 2,
                            "dpi={dpi_scale} ({x},{y}) channel {channel}: {actual} != {expected}"
                        );
                    }
                }
            }
            renderer
                .render(&scene, &view, wgpu::Color::TRANSPARENT)
                .unwrap();
            drop(scene);
            assert_eq!(read_pixels(&device, &queue, &texture), pixels);
        }
        source.clear();
        let empty = renderer
            .prepare_scene(
                &source,
                Frame {
                    width: 64,
                    height: 64,
                    dpi_scale: 1.0,
                },
            )
            .await
            .unwrap();
        assert_eq!(empty.metrics().draws, 0);
        assert_eq!(empty.metrics().atlas_bytes, 0);
        // Explicit source contrast retains all thirteen original values. Use
        // exact pixel-area intersections and a double background-domain oracle;
        // allow only the final shader/unorm conversion's one-byte rounding.
        for contrast in 0..=12 {
            source.clear();
            source
                .draw_path(PathDraw {
                    path: path.clone(),
                    transform: Affine::IDENTITY,
                    color: [1.0; 4],
                    sampling: PathSampling::default()
                        .with_grayscale_contrast(contrast)
                        .unwrap(),
                })
                .unwrap();
            let scene = renderer
                .prepare_scene(&source, empty.frame())
                .await
                .unwrap();
            renderer
                .render(&scene, &view, wgpu::Color::TRANSPARENT)
                .unwrap();
            let pixels = read_pixels(&device, &queue, &texture);
            for y in 0..64 {
                for x in 0..64 {
                    let overlap = |v: usize, low: f64, high: f64| {
                        ((v as f64 + 1.0).min(high) - (v as f64).max(low)).max(0.0)
                    };
                    let area = overlap(x, 4.25, 20.75) * overlap(y, 4.5, 20.5);
                    let coverage = (area * 255.0).round() / 255.0;
                    let exponent = 1.0 / (1.0 + f64::from(contrast) / 10.0);
                    let expected = 255.0 - ((1.0 - coverage).powf(exponent) * 255.0).round();
                    for channel in &pixels[(y * 64 + x) * 4..(y * 64 + x + 1) * 4] {
                        assert!(
                            (f64::from(*channel) - expected).abs() <= 1.0,
                            "contrast={contrast} ({x},{y}): {channel} != {expected}"
                        );
                    }
                }
            }
        }
    });
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
        assert!(rasterizer.rasterize_atlas(&invalid).is_err());
        let atlas = rasterizer.rasterize_atlas(&requests).unwrap();
        let mut expected_atlas =
            vec![0u8; (atlas.texture().width() * atlas.texture().height()) as usize];
        assert_eq!(masks.len(), paths.len());
        drop(rasterizer);
        for (index, mask) in masks.iter().enumerate() {
            assert_eq!(mask.execution_path(), ExecutionPath::NativeCompute);
            let pixels = read_pixels(&device, &queue, mask.texture());
            let request = mask.request();
            let tile = atlas.tiles()[index];
            assert_eq!(tile.request.size, request.size);
            for y in 0..request.size[1] {
                let source = (y * request.size[0]) as usize;
                let destination =
                    ((tile.origin[1] + y) * atlas.texture().width() + tile.origin[0]) as usize;
                expected_atlas[destination..destination + request.size[0] as usize]
                    .copy_from_slice(&pixels[source..source + request.size[0] as usize]);
            }
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
        // Full comparison includes every padding texel and unused atlas region,
        // after the compute owner has retired. No neighboring tile may leak.
        assert_eq!(
            read_pixels(&device, &queue, atlas.texture()),
            expected_atlas
        );
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
