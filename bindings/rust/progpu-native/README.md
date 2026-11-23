# progpu-native

Safe Rust over the ProGPU native engine.

`SceneWriter` writes the retained scene stream of the engine directly into reused buffers: one call
per command with no call across the C ABI, and no allocation in a frame that fits the capacity of
the frames before it. For the calls it has, it writes the same bytes as the C++
`semantic_scene_builder`; `tests/differential.rs` builds scenes with both and compares them.

The writer checks what it needs to lay out a stream: indices, the balance of the stack and the
capacities of the format. The values of the records are checked by the validator of the engine
when it receives the stream.
