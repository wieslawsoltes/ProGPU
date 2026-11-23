//! Exact original wire, shared by native tests, source comparison and WebAssembly.
use progpu::text::unicode::{
    ManagedIndicProperties, managed_indic_properties, managed_use_category,
};

pub fn record(point: u32) -> [u8; 6] {
    let raw = ManagedIndicProperties::for_raw_code_point(point)
        .bits()
        .to_le_bytes();
    let public = managed_indic_properties(point).bits().to_le_bytes();
    let use_category = managed_use_category(point);
    [
        raw[0],
        raw[1],
        use_category,
        public[0],
        public[1],
        use_category,
    ]
}

pub fn extraction(raw: u16) -> [u8; 2] {
    let properties = ManagedIndicProperties::from_bits(raw);
    [properties.category(), properties.position()]
}
