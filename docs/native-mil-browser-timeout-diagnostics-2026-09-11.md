# Native browser timeout diagnostics

The browser job in run 34551944182 timed out at `evidence-readback` without
browser errors. The subsequent run 34553087997 passed that job without a
renderer change; this does not establish a root cause or a permanent fix.

The browser fixture now records whether the final buffer mapping was requested,
completed, or failed. On timeout the existing evidence directory receives a JSON
artifact containing dataset state, browser errors, and bounded console messages.
The workflow uploads this artifact alongside its existing evidence.

The 120-second deadline, rendering assertions, and success requirements are
unchanged. No retry, skipped assertion, or renderer fallback was added.

Validation: `node --check src/ProGPU.Native/browser/test.mjs` passed, and the
local Emscripten `progpu_native_browser_smoke` target rebuilt successfully.
These checks establish syntax/compilation, not final-head browser or package
qualification. Required CI and platform gates remain in force.
