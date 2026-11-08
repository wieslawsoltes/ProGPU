//! Actual-device pixel, replay and submission-ownership controls.

use progpu::{
    geometry::{AnalyticPrimitive, Mesh, Shape},
    renderer::{Frame, Renderer},
};

fn read_pixels(device: &wgpu::Device, queue: &wgpu::Queue, texture: &wgpu::Texture) -> Vec<u8> {
    let buffer = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("pixel test readback"),
        size: 256 * 64,
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
                bytes_per_row: Some(256),
                rows_per_image: Some(64),
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
    let bytes = buffer.slice(..).get_mapped_range().unwrap().to_vec();
    buffer.unmap();
    bytes
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
