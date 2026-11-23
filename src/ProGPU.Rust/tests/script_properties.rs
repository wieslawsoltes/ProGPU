//! Original API observations, including every range boundary and interior.
#[path = "support/script_property_replay.rs"]
mod replay;
use progpu::text::unicode::*;

#[test]
fn frozen_original_script_property_records() {
    let fixture = include_bytes!("support/script_properties.bin");
    assert_eq!(&fixture[..4], b"PSP1");
    let count = u32::from_le_bytes(fixture[4..8].try_into().unwrap()) as usize;
    assert_eq!(fixture.len(), 8 + count * 10);
    for record in fixture[8..].chunks_exact(10) {
        let point = u32::from_le_bytes(record[..4].try_into().unwrap());
        assert_eq!(replay::record(point), record[4..], "raw {point:#x}");
    }
}

#[test]
fn full_raw_word_extraction_and_independent_invalid_query_contracts() {
    for raw in 0..=u16::MAX {
        assert_eq!(ManagedIndicProperties::from_bits(raw).bits(), raw);
        assert_eq!(replay::extraction(raw), raw.to_le_bytes());
    }
    for point in (0xd800..=0xdfff).chain([0x110000, 0x80000905, u32::MAX]) {
        assert_eq!(managed_indic_properties(point).bits(), 0);
        assert_eq!(
            ManagedIndicProperties::for_raw_code_point(point).bits(),
            0xe00
        );
        assert_eq!(managed_use_category(point), 0);
    }
    assert_eq!(managed_indic_properties(0x905).category(), 2);
    assert_eq!(managed_indic_properties(0x905).position(), 4);
    assert_eq!(managed_use_category(0x17d2), 44);
}
