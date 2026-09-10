# Dawn hit-query result validation

The exact WebScene/Dawn Metal CI gate at 9e05651a failed its point/region
participation fixture. All other checks at that head completed successfully.
The failure was in the fixture's result-location assertion, not in participation
filtering or rendering.

The public poll/wait ABI documents two modes. With zero list capacity, summary
is the topmost hit. With nonzero capacity, summary contains traversal counters
and total hit count; ordered owner records are returned in the result list.
The fixture incorrectly required summary.id to contain the owner in both modes.

## Reproduction and correction

The pinned WebScene provider 02823bf8d2e56548b2780d6b92ae7065be1d8605 and Dawn
710c33013c53ab2700d332c25ff51430251a8cc4 were rebuilt in task-owned directories.
The original assertion reproduced on Apple M3 Pro Metal:

```text
participation=0 query=1 expected=1 summary-hit=1 summary-id=-1 count=1 result-id=796
```

The corrected fixture checks owner identity and primitive index in the documented
location. It retains all twelve participation/query combinations (ordinary,
point-only and region-only primitives; single, list, rectangular and elliptical
queries), exact hit/count assertions, and the existing asynchronous wait/poll
and token-retirement checks. Failure diagnostics print both summary and list
identity. No production code, shader, ABI, tolerance or timeout changes.

The complete native WebScene provider executable passes in 5.97 seconds after
the correction, including its subsequent rendering, mask/effect, text and IOSurface
checks. This is not merely the isolated twelve-case loop. The initial reproduction
failed in 5.38 seconds.

Local logs:

- artifacts/release-hour/webscene-exact-provider.log
- artifacts/release-hour/webscene-corrected-build.log
- artifacts/release-hour/webscene-corrected-tests.log

The full script's managed package-consumption portion and fresh exact-head hosted
CI still require completion. Downstream LibreWPF package/application qualification
and broader DirectX/Direct2D/Win2D contracts remain separate gates.
