//! Matched byte-level execution against the original C++ analytic expander.

use progpu::geometry::{Affine, AnalyticPrimitive, Mesh, Shape};
use std::{
    io::Write,
    process::{Command, Stdio},
};

#[test]
#[ignore = "requires a C++20 compiler and the source checkout; run explicitly in parity CI"]
fn original_native_geometry_matches() {
    let (directory, executable) = compile_oracle("geometry");
    compare_geometry(directory, executable);
}

fn compile_oracle(name: &str) -> (std::path::PathBuf, std::path::PathBuf) {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let directory =
        std::env::temp_dir().join(format!("progpu-oracle-{name}-{}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    let executable = directory.join("geometry-oracle");
    let status = Command::new(std::env::var_os("CXX").unwrap_or_else(|| "clang++".into()))
        .args([
            "-std=c++20",
            "-O2",
            "-ffp-contract=off",
            "-Wall",
            "-Wextra",
            "-Werror",
        ])
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/include").display()
        ))
        .arg(format!(
            "-I{}",
            root.join("src/ProGPU.Native/src/Backend").display()
        ))
        .arg(root.join("src/ProGPU.Rust/tests/native_geometry.cpp"))
        .arg("-o")
        .arg(&executable)
        .status()
        .unwrap();
    assert!(status.success());
    (directory, executable)
}

fn compare_geometry(directory: std::path::PathBuf, executable: std::path::PathBuf) {
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut cases = 0;
    for shape in [Shape::Rectangle, Shape::Ellipse, Shape::RoundedRectangle] {
        for aliased in [false, true] {
            for stroke in [0.0, 0.25, 1.0, 10.0] {
                for transform in [
                    Affine::IDENTITY,
                    Affine([0.5, 0.25, -0.75, -2.0, 33.5, -18.25]),
                ] {
                    for width in [0.0, 0.25, 17.0, -1.0, f32::NAN, f32::INFINITY] {
                        let primitive = AnalyticPrimitive {
                            shape,
                            bounds: [-5.25, 7.5, width, 9.0],
                            color: [0.125, 0.75, 1.5, 0.5],
                            transform,
                            corner_radius: -12.0,
                            stroke_thickness: stroke,
                            aliased,
                        };
                        input.extend_from_slice(&(shape as u32).to_ne_bytes());
                        input.extend_from_slice(&u32::from(aliased).to_ne_bytes());
                        for value in primitive
                            .bounds
                            .into_iter()
                            .chain([primitive.corner_radius, stroke])
                            .chain(primitive.color)
                            .chain(transform.0)
                            .chain([1.5])
                        {
                            input.extend_from_slice(&value.to_ne_bytes());
                        }
                        let mut mesh = Mesh::default();
                        let accepted = mesh.push(&primitive, 1.5).is_ok();
                        expected.extend_from_slice(&u32::from(accepted).to_ne_bytes());
                        if accepted {
                            expected.extend_from_slice(bytemuck::cast_slice(mesh.vertices()));
                            expected.extend_from_slice(bytemuck::cast_slice(mesh.indices()));
                        }
                        cases += 1;
                    }
                }
            }
        }
    }
    // A file avoids pipe deadlock when output exceeds pipe capacity.
    let input_path = directory.join("input.bin");
    std::fs::File::create(&input_path)
        .unwrap()
        .write_all(&input)
        .unwrap();
    let output = Command::new(&executable)
        .stdin(Stdio::from(std::fs::File::open(input_path).unwrap()))
        .output()
        .unwrap();
    assert!(output.status.success());
    assert_eq!(output.stdout.len(), expected.len());
    if let Some(index) = output
        .stdout
        .iter()
        .zip(&expected)
        .position(|(a, b)| a != b)
    {
        panic!(
            "native geometry differs at byte {index}: native={}, rust={}",
            output.stdout[index], expected[index]
        );
    }
    eprintln!(
        "native_geometry: {cases} matched cases, {} exact bytes",
        expected.len()
    );
    std::fs::remove_dir_all(directory).unwrap();
}

#[test]
#[ignore = "requires a C++20 compiler and the source checkout; run explicitly in parity CI"]
fn original_native_path_frames_match() {
    use progpu::{
        path::{FillRule, Path, Segment},
        scene::{PathDraw, PathSampling, Scene},
    };
    let (directory, executable) = compile_oracle("frames");
    let path = Path::new(
        vec![Segment::line([0.0, 0.0], [16.0, 16.0])],
        [-1.25, -2.5, 16.75, 18.0],
        FillRule::NonZero,
    )
    .unwrap();
    let mut input = Vec::new();
    let mut expected = Vec::new();
    let mut cases = 0;
    for basis in [
        [1.0, 0.0, 0.0, 1.0],
        [2.0, 0.0, 0.0, 2.0],
        [-1.0, 0.0, 0.0, 1.0],
        [0.0, 1.0, -1.0, 0.0],
        [0.5, 0.25, -0.75, -2.0],
        [1.0, 0.0001, 0.0002, 1.0],
        [0.01, 0.0, 0.0, 0.01],
        [0.0; 4],
        [0.000001, 0.0, 0.0, 0.000001],
        [f32::INFINITY, 0.0, 0.0, 1.0],
        [f32::NAN, 0.0, 0.0, 1.0],
    ] {
        for translation in [0.0, -0.0, 0.0078125, -0.0078125, 0.5, -0.5, 33.25, -31.75] {
            for dpi in [0.5, 1.0, 1.25, 2.0, 3.0, 4.0] {
                let transform = Affine([
                    basis[0],
                    basis[1],
                    basis[2],
                    basis[3],
                    translation,
                    -translation,
                ]);
                input.extend_from_slice(bytemuck::cast_slice(&transform.0));
                input.extend_from_slice(&f32::to_ne_bytes(dpi));
                let scales = transform.scales();
                expected.extend_from_slice(&u32::from(scales.is_ok()).to_ne_bytes());
                if let Ok(scales) = scales {
                    let mut scene = Scene::default();
                    scene
                        .draw_path(PathDraw {
                            path: path.clone(),
                            transform,
                            color: [1.0; 4],
                            sampling: PathSampling::default(),
                        })
                        .unwrap();
                    let compiled = scene.compile(dpi).unwrap();
                    let vertices = compiled.mesh().vertices();
                    for vertex in vertices {
                        input.extend_from_slice(bytemuck::cast_slice(&vertex.shape_size));
                    }
                    for vertex in vertices {
                        input.extend_from_slice(bytemuck::cast_slice(&vertex.texture_coordinate));
                    }
                    expected.extend_from_slice(bytemuck::cast_slice(&scales));
                    let phase = vertices[0].texture_coordinate.map(|v| v - 2.0);
                    expected.extend_from_slice(bytemuck::cast_slice(&phase));
                    for vertex in vertices {
                        expected.extend_from_slice(bytemuck::cast_slice(&vertex.position));
                    }
                    expected.extend_from_slice(&compiled.exact_path_draws().to_ne_bytes());
                } else {
                    input.extend_from_slice(&[0; 64]);
                }
                cases += 1;
            }
        }
    }
    let input_path = directory.join("input.bin");
    std::fs::write(&input_path, input).unwrap();
    let output = Command::new(executable)
        .arg("frames")
        .stdin(Stdio::from(std::fs::File::open(input_path).unwrap()))
        .output()
        .unwrap();
    assert!(output.status.success());
    assert_eq!(output.stdout.len(), expected.len());
    if let Some(index) = output
        .stdout
        .iter()
        .zip(&expected)
        .position(|(a, b)| a != b)
    {
        panic!(
            "native frame differs at byte {index}: native={}, rust={}",
            output.stdout[index], expected[index]
        );
    }
    eprintln!(
        "native_frames: {cases} matched cases, {} exact bytes",
        expected.len()
    );
    std::fs::remove_dir_all(directory).unwrap();
}
