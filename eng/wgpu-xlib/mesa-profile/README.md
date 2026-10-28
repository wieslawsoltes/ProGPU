# Private Mesa profile for the MSAA resolve control

The runtime still builds on native Ubuntu 24.04 with the pinned Rust, Clang, source,
patch and generated ABI controls. The original Xlib presentation/lifetime probe
runs on that host with its unchanged 60-second limit. This profile applies only
to the separately authored 576-pixel multisample resolve control.

Ubuntu 24.04's Mesa 25.2.8 llvmpipe has a known upstream lower/left multisample
scissor-plane defect. Retained direct GLES samples and exact source equations
show the unwanted coverage before resolution; changing the resolve scissor box
does not repair it. Upstream commit
`92550b106c70408cd9f5c25a50c311e68af06d6b` corrects those planes and is present in
Ubuntu's Mesa 26.0.8 source. The original failed pixels remain failures for that
older environment; neither expected values nor tolerances are changed.

Each native RID uses its exact official Ubuntu 26.04 image digest and five
hash-pinned, signed-APT Mesa packages in an owned disposable container. The host
and shipped native package never receive those driver binaries. Actual loaded
EGL/Gallium files must match the verified installed package hashes. Driver setup
is bounded by 600 seconds; the unchanged original resolve executable has its own
60-second process-group limit. The host helper has a 1,000-second active budget
and reserves 90 seconds for exact CID/label/image cleanup inside the 20-minute
CI lane. No automatic retry is added. A later explicit workflow attempt binds
its own actual run ID and attempt number.

The build lane freezes only the original resolve executable and native package
with their complete hashes, source head, source/patch hashes, architecture,
producer ABI receipt and profile hash. The verification lane downloads only the
same-run candidate and checks all identities before execution. Both workflows
reuse these helpers. The existing `progpu-xlib-runtime-<RID>` artifact is published
only after every original pixel passes and cleanup succeeds; downstream job and
artifact names remain unchanged. No whole-CI, package or source application gate
is waived, and no old Ubuntu 24 source-host coverage is removed.

This environment repair is independent of the original Windows ARM64 WARP
failure. It does not change Windows drivers, compiler defaults or shader policy.
