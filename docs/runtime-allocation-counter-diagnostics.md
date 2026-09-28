# Independent runtime allocation-counter evidence

The original standalone reproducer submitted in
[dotnet/runtime#134724](https://github.com/dotnet/runtime/issues/134724) now lives
in `eng/RuntimeAllocationProbe`. Its local empty MSBuild imports keep it separate
from ProGPU packages, analyzers and build targets. The measured no-inline method
contains only two counter reads and `Thread.SpinWait`; context allocation,
storage, console output and worker setup remain outside it.

The manual `Runtime allocation diagnostics` workflow measures once on each
selected hosted OS, with the repository SDK and no GC/JIT environment overrides.
Before that workflow reaches the default branch, the existing `Native path
diagnostics` dispatcher can invoke it with `probe_set=allocation-counter` and
`package_run_id=0`. That mode skips package download and delegates only to the
independent counter workflow. Ordinary owner/path modes are unchanged.
It retains every raw delta, actual runtime/architecture, 32 empty controls, 32
explicit 64-byte allocation controls, and 192 worker-phase empty intervals.
Work and worker lifetime retain the original 20-second/5-second bounds, at most
256 nonblocking collection requests and less than 27 MiB live worker storage.

Exit 0 means no nonzero empty interval was observed in that execution. Exit 1
means the controls passed but at least one worker-phase empty interval reported
a delta. Exit 2 means a control failed. Exceptions/timeouts are failures. No
retry-until-pass selection, counter subtraction, tolerance or test suppression
is introduced. A zero run does not disprove an intermittent observation.

ProGPU's product allocation assertions remain unchanged. This diagnostic cannot
qualify them, mark a failed producer Build successful, or establish whether a
particular CI delta is runtime bookkeeping versus an actual charged allocation.
In particular, the Linux audio-mixer failure in Build `36434866677` reported
4040 bytes, but the prior independent evidence was macOS-only. Do not infer a
Linux cause from that macOS report; collect the independent platform result.

One local Release run on .NET 10.0.5 ARM64 observed 26 nonzero worker-phase
intervals in 0.746 seconds, with all 32 empty and 32 positive controls passing.
The failure remains visible as exit 1. This repeats the previously reported
macOS observation; Linux and Windows require their own receipts.
