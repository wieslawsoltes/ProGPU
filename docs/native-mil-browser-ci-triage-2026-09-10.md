# Browser CI timeout triage

ProGPU browser job 103045464991 timed out after 120 seconds with stage
render-workload and no browser errors. That stage previously covered the whole
render sequence, device recreation and asynchronous evidence readback, so the
log cannot establish which operation stalled.

The existing C++ smoke now publishes narrower stage labels for image rendering,
rounded/state/media/vector/brush/picture/coverage masks, device recreation and
evidence readback/callback/completion. No workload, assertion, shader, baseline,
capability admission or timeout is changed. These markers are test-only and do
not alter either product renderer. This is diagnostic instrumentation, not a
claimed fix for the Linux timeout.

The current browser target rebuilt locally with Emscripten 4.0.18. The repository's
existing Playwright test passed with its default SwiftShader selection on macOS
and the unchanged 120-second deadline. It reported retained mask, image and
geometry contracts passed. Logs: artifacts/merge-browser-build.log and
artifacts/merge-browser-local-test.log. That result does not prove Linux CI or
hardware native-hit-test qualification; the software lane's existing explicit
hit-test deferral remains unchanged.

The CI-fix workflow was used to inspect actual job failures. Separate LibreWPF
SDK job 103037314459 stopped on a GitHub API TCP timeout while waiting for its
exact pinned producer, before package validation. LibreWPF commit 124fe3396 adds
bounded query retries without admitting another SHA or failed run. ProGPU's SVG
representative failure remains separate and unresolved; no checksum is updated.
