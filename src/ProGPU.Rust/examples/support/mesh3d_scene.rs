//! Shared retained Mesh3D gallery: lit solid, wireframe, solid wireframe,
//! gradient/occluded edges, sampled texture and blurred texture. These are
//! ordinary production sources, with separate offscreen and receiving frames.
use progpu::{
    Error,
    geometry::{Affine, AnalyticPrimitive},
    image::{GpuImage, ImageAlpha, ImageSampling},
    mask::AnalyticMask,
    mesh3d::{
        Camera, DiffuseMaterial3D, Light3D, LightKind3D, Mesh3DViewport, MeshEdge3D,
        MeshEdgeDisplay, MeshEdgeStyle, MeshEdgeTopology, MeshGeometry3D, MeshMaterial3D,
        MeshModel3D, MeshTextureEffect, MeshTextureLease, MeshTexturePresentation, MeshVisual3D,
        OrthographicCamera, PerspectiveCamera, ProjectionCamera, RenderMode3D, ShadingMode3D,
    },
    picture_archive::ArchiveBrush,
    scene::{Mesh3DViewportDraw, Scene},
    vector::{Brush, BrushKind, GradientStop, LinearGradient, Matrix4},
};
use std::sync::Arc;

pub fn mesh3d_scene(device: &wgpu::Device, queue: &wgpu::Queue, dpi: f32) -> Result<Scene, Error> {
    let geometry = cube()?;
    let texture = Arc::new(checker(device, queue)?);
    let lights: Arc<[Light3D]> = Arc::from([
        Light3D {
            color: [0.15, 0.18, 0.24, 1.0],
            ..Default::default()
        },
        Light3D {
            kind: LightKind3D::Directional,
            direction: [-0.5, -0.8, -1.0],
            color: [0.9, 0.85, 0.75, 1.0],
            ..Default::default()
        },
        Light3D {
            kind: LightKind3D::Point,
            position: [-2.0, 1.0, 2.0],
            range: 8.0,
            color: [0.15, 0.45, 0.9, 1.0],
            linear_attenuation: 0.2,
            ..Default::default()
        },
        Light3D {
            kind: LightKind3D::Spot,
            position: [1.5, 2.0, 3.0],
            direction: [-1.5, -2.0, -3.0],
            range: 10.0,
            color: [0.9, 0.3, 0.15, 1.0],
            inner_cone_cosine: 0.9,
            outer_cone_cosine: 0.7,
            linear_attenuation: 0.1,
            ..Default::default()
        },
    ]);
    let mut camera_base = ProjectionCamera::default();
    camera_base.set_view([0.0, 0.0, 4.0], [0.0, 0.0, -4.0]);
    camera_base.set_near_plane_distance(0.125);
    camera_base.set_far_plane_distance(1000.0);
    let mut orthographic = OrthographicCamera::new(camera_base);
    orthographic.set_width(2.8);
    let perspective = PerspectiveCamera::new(camera_base);
    let rotation = Matrix4([
        [0.866_025_4, 0.0, -0.5, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.5, 0.0, 0.866_025_4, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]) * Matrix4([
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 0.939_692_6, 0.342_020_15, 0.0],
        [0.0, -0.342_020_15, 0.939_692_6, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]);
    let accents = [
        [0.15, 0.7, 0.95, 1.0],
        [0.45, 0.9, 0.65, 1.0],
        [0.95, 0.65, 0.2, 1.0],
        [0.8, 0.35, 0.8, 1.0],
        [0.3, 0.65, 1.0, 1.0],
        [0.95, 0.4, 0.35, 1.0],
    ];
    let mut scene = Scene::default();
    for (index, color) in accents.into_iter().enumerate() {
        let x = 16.0 + (index % 3) as f32 * 312.0;
        let y = 16.0 + (index / 3) as f32 * 304.0;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x, y, 296.0, 288.0],
            [0.12, 0.15, 0.21, 1.0],
        ))?;
        scene.draw_analytic(AnalyticPrimitive::rectangle(
            [x + 8.0, y + 8.0, 280.0, 4.0],
            color,
        ))?;
        let mut material = DiffuseMaterial3D {
            color,
            specular_color: [0.65; 3],
            shininess: 48.0,
            ..Default::default()
        };
        if index == 3 {
            material.color = [1.0; 4];
            material.brush = Some(Arc::new(ArchiveBrush::Vector(Brush::new(
                BrushKind::Linear(LinearGradient::new(
                    [0.0, 0.0],
                    [1.0, 1.0],
                    vec![
                        GradientStop {
                            color: [0.1, 0.6, 0.95, 1.0],
                            offset: 0.0,
                        },
                        GradientStop {
                            color: [0.9, 0.3, 0.7, 1.0],
                            offset: 0.5,
                        },
                        GradientStop {
                            color: [1.0, 0.8, 0.2, 1.0],
                            offset: 1.0,
                        },
                    ],
                )),
            ))));
        } else if index >= 4 {
            material.color = [1.0; 4];
            material.texture_source = Some(texture.clone());
            material.texture_sampling = if index == 4 {
                ImageSampling::Nearest
            } else {
                ImageSampling::Linear
            };
            material.texture_presentation =
                MeshTexturePresentation::new([0.125, 0.0, 0.75, 1.0], 1, true)?;
            if index == 5 {
                material.texture_effect = MeshTextureEffect {
                    blur_sigma: 1.6,
                    saturation: 1.35,
                    brightness: 0.05,
                    ..Default::default()
                };
            }
        }
        let front = MeshMaterial3D::Diffuse(Arc::new(material));
        let back = MeshMaterial3D::Diffuse(Arc::new(DiffuseMaterial3D {
            color: [0.9, 0.25, 0.15, 1.0],
            ..Default::default()
        }));
        // The producer walks the real hierarchy, retaining both front and back
        // material owners and applying the parent's model transform once.
        let source = Arc::new(MeshVisual3D::Model {
            content: Some(MeshModel3D {
                transform: rotation,
                ..Default::default()
            }),
            children: Arc::from([Arc::new(MeshVisual3D::model(MeshModel3D {
                geometry: Some(geometry.clone()),
                material: Some(front),
                back_material: Some(back),
                ..Default::default()
            }))]),
        });
        let viewport = Mesh3DViewport {
            viewport_size: [280.0, 250.0],
            lights: lights.clone(),
            render_mode: match index {
                1 => RenderMode3D::Wireframe,
                2 => RenderMode3D::SolidWireframe,
                _ => RenderMode3D::Solid,
            },
            shading_mode: if index == 2 {
                ShadingMode3D::Flat
            } else {
                ShadingMode3D::Realistic
            },
            edge_style: MeshEdgeStyle {
                display: if index == 3 {
                    MeshEdgeDisplay::CREASE
                        | MeshEdgeDisplay::SILHOUETTE
                        | MeshEdgeDisplay::OCCLUDED
                } else {
                    MeshEdgeDisplay::NONE
                },
                width: 1.5,
                visible_color: [1.0, 0.9, 0.65, 1.0],
                occluded_color: [0.3, 0.75, 0.95, 0.75],
                ..Default::default()
            },
            scene_generation: 1,
            record_generation: 1,
            ..Default::default()
        }
        .compile_source(&[source], None)?;
        let camera = if index % 3 == 2 {
            Camera::Perspective(perspective)
        } else {
            Camera::Orthographic(orthographic)
        };
        let matrices = camera.matrices(viewport.viewport_size)?;
        let mut draw = Mesh3DViewportDraw::from_display(
            Arc::new(viewport),
            matrices.projection,
            matrices.view,
            dpi,
        )?;
        draw.transform = Matrix4::from_affine(Affine([1.0, 0.0, 0.0, 1.0, x + 8.0, y + 22.0]));
        if index == 5 {
            // Parent coverage belongs to the ordinary texture consumer, not the
            // independent 3D target's depth/lighting passes.
            let mut mask = AnalyticMask::rectangle([x + 8.0, y + 22.0, 280.0, 250.0]);
            mask.radii_x = [24.0; 4];
            mask.radii_y = [24.0; 4];
            scene.push_mask(mask)?;
            scene.push_clip_rect([x + 14.5, y + 22.0, 267.0, 243.5])?;
        }
        scene.mesh3d(draw)?;
        if index == 5 {
            scene.pop_scope()?;
            scene.pop_scope()?;
        }
    }
    Ok(scene)
}

fn checker(device: &wgpu::Device, queue: &wgpu::Queue) -> Result<MeshTextureLease, Error> {
    let mut pixels = Vec::with_capacity(64 * 64 * 4);
    for y in 0..64 {
        for x in 0..64 {
            let color = if (x / 8 + y / 8) % 2 == 0 {
                [245, 185, 45, 255]
            } else {
                [35, 90, 215, 255]
            };
            pixels.extend_from_slice(&color);
        }
    }
    let size = wgpu::Extent3d {
        width: 64,
        height: 64,
        depth_or_array_layers: 1,
    };
    let texture = device.create_texture(&wgpu::TextureDescriptor {
        label: Some("ProGPU Mesh3D gallery checker"),
        size,
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::Rgba8Unorm,
        usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    queue.write_texture(
        texture.as_image_copy(),
        &pixels,
        wgpu::TexelCopyBufferLayout {
            offset: 0,
            bytes_per_row: Some(256),
            rows_per_image: Some(64),
        },
        size,
    );
    Ok(MeshTextureLease::from_image(GpuImage::from_texture(
        device,
        &texture,
        ImageAlpha::Straight,
    )?))
}

fn cube() -> Result<MeshGeometry3D, Error> {
    let corners = [
        [-0.65, -0.65, -0.65],
        [0.65, -0.65, -0.65],
        [0.65, 0.65, -0.65],
        [-0.65, 0.65, -0.65],
        [-0.65, -0.65, 0.65],
        [0.65, -0.65, 0.65],
        [0.65, 0.65, 0.65],
        [-0.65, 0.65, 0.65],
    ];
    let faces = [
        ([4, 5, 6, 7], [0.0, 0.0, 1.0]),
        ([1, 0, 3, 2], [0.0, 0.0, -1.0]),
        ([5, 1, 2, 6], [1.0, 0.0, 0.0]),
        ([0, 4, 7, 3], [-1.0, 0.0, 0.0]),
        ([7, 6, 2, 3], [0.0, 1.0, 0.0]),
        ([0, 1, 5, 4], [0.0, -1.0, 0.0]),
    ];
    let mut positions = Vec::with_capacity(24);
    let mut normals = Vec::with_capacity(24);
    let mut uv = Vec::with_capacity(24);
    let mut indices = Vec::with_capacity(36);
    for (face, normal) in faces {
        let base = positions.len() as i32;
        for (corner, coordinate) in
            face.into_iter()
                .zip([[0.0, 1.0], [1.0, 1.0], [1.0, 0.0], [0.0, 0.0]])
        {
            positions.push(corners[corner]);
            normals.push(normal);
            uv.push(coordinate);
        }
        indices.extend([base, base + 1, base + 2, base, base + 2, base + 3]);
    }
    let edges = [
        (0, 1, 1, 5),
        (1, 2, 1, 2),
        (2, 3, 1, 4),
        (3, 0, 1, 3),
        (4, 5, 0, 5),
        (5, 6, 0, 2),
        (6, 7, 0, 4),
        (7, 4, 0, 3),
        (0, 4, 3, 5),
        (1, 5, 2, 5),
        (2, 6, 2, 4),
        (3, 7, 3, 4),
    ]
    .map(|(start, end, first, second)| MeshEdge3D {
        start: corners[start],
        end: corners[end],
        first_face_normal: faces[first].1,
        second_face_normal: faces[second].1,
        topology: MeshEdgeTopology::Manifold,
    });
    MeshGeometry3D::new(
        positions.into(),
        normals.into(),
        indices.into(),
        uv.into(),
        Arc::from(edges),
    )
}
