//! Diagnostic storage accounting of actual retained Rust commands.
use super::*;
impl Scene {
    pub(crate) fn recorded_operation_count(&self) -> usize {
        self.commands
            .iter()
            .filter(|command| !matches!(command, Command::Empty | Command::NativeDesignLease(_) | Command::NativeAtlasLease(_)))
            .count()
    }
    pub(crate) fn recorded_storage_bytes(&self) -> usize {
        let mut bytes = self
            .commands
            .capacity()
            .saturating_mul(std::mem::size_of::<Command>());
        for command in &self.commands {
            let payload = match command {
                Command::Text(record) => {
                    let arrays = match &record.0.source {
                        crate::text::paint::TextSource::Positioned(run) => {
                            std::mem::size_of_val(run.indices.as_ref())
                                .saturating_add(std::mem::size_of_val(run.positions.as_ref()))
                        }
                        _ => 0,
                    };
                    std::mem::size_of_val(record.as_ref()).saturating_add(arrays)
                }
                Command::ImageEffect(draw) => std::mem::size_of_val(draw.as_ref()),
                _ => 0,
            };
            bytes = bytes.saturating_add(payload);
        }
        bytes
    }
}
