//! Safe Rust over the ProGPU native engine.

#![deny(unsafe_op_in_unsafe_fn)]

mod scene_writer;

pub use scene_writer::{SceneWriteError, SceneWriter};
