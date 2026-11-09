//! Test-only replay of the two distinct original managed grapheme contracts.
use progpu::text::unicode::{
    UnicodeScalar, decode_utf16_into,
    grapheme::{
        GraphemeCluster, GraphemePolicy, ManagedGraphemeProfile, managed_next_grapheme_length,
        segment_into,
    },
};
pub const PROFILE: ManagedGraphemeProfile = ManagedGraphemeProfile::Unicode16_0;
pub fn emit(out: &mut Vec<u8>, value: u32) {
    out.extend(value.to_le_bytes());
}
#[derive(Default)]
pub struct Replay {
    scalars: Vec<UnicodeScalar>,
    clusters: Vec<GraphemeCluster>,
}
impl Replay {
    pub fn execute(&mut self, input: &[u16], output: &mut Vec<u8>) {
        output.clear();
        emit(output, managed_next_grapheme_length(input, PROFILE) as u32);
        self.scalars.resize(input.len(), UnicodeScalar::default());
        self.clusters
            .resize(input.len(), GraphemeCluster::default());
        match decode_utf16_into(input, &mut self.scalars) {
            Err(_) => emit(output, u32::MAX),
            Ok(count) => {
                let count = segment_into(
                    &self.scalars[..count as usize],
                    GraphemePolicy::ManagedRuntime(PROFILE),
                    &mut self.clusters,
                )
                .unwrap();
                emit(output, count + 1);
                emit(output, 0);
                for cluster in &self.clusters[..count as usize] {
                    emit(output, cluster.input_index + cluster.input_length);
                }
            }
        }
        for start in 0..=input.len() {
            emit(
                output,
                managed_next_grapheme_length(&input[start..], PROFILE) as u32,
            );
        }
    }
}
