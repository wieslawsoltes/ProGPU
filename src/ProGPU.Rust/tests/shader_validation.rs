//! Offline validation of unchanged production shader compositions.

#[test]
fn canonical_modules_validate() {
    for &(name, source) in progpu::shaders::MODULES {
        let module = naga::front::wgsl::parse_str(source)
            .unwrap_or_else(|error| panic!("{name}: {}", error.emit_to_string(source)));
        naga::valid::Validator::new(
            naga::valid::ValidationFlags::all(),
            naga::valid::Capabilities::all(),
        )
        .validate(&module)
        .unwrap_or_else(|error| panic!("{name}: {}", error.emit_to_string(source)));
        assert!(!module.entry_points.is_empty(), "{name}");
    }
}
