//! Original O(1) retained output key. Recording a key never manufactures pixels.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeLayerOutputKey {
    pub scene_hash: u64,
    pub operation_id: u64,
    pub texture_generation: u32,
    pub width: u32,
    pub height: u32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeLayerOutputCache {
    key: NativeLayerOutputKey,
    valid: bool,
}
impl NativeLayerOutputCache {
    pub fn hit(&self, key: NativeLayerOutputKey) -> bool {
        self.valid && key.scene_hash != 0 && key.operation_id != 0 && self.key == key
    }
    pub fn key(&self) -> Option<NativeLayerOutputKey> {
        self.valid.then_some(self.key)
    }
    /// Called only for actual successfully encoded output by the parent executor;
    /// GPU cache publication is deferred until the corresponding submit succeeds.
    pub fn record_output(&mut self, key: NativeLayerOutputKey) {
        self.key = key;
        self.valid = key.scene_hash != 0 && key.operation_id != 0;
    }
    pub fn invalidate(&mut self) {
        *self = Self::default();
    }
}
