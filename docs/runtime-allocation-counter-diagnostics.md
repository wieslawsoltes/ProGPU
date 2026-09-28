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
4040 bytes. The standalone observation does not establish the cause of that
particular product failure; the following Build `36436941579` passed its Linux
build/test job without changing the mixer or its allocation assertion.

One local Release run on .NET 10.0.5 ARM64 observed 26 nonzero worker-phase
intervals in 0.746 seconds, with all 32 empty and 32 positive controls passing.
The failure remains visible as exit 1.

## Hosted platform observations

[Run 36437994714](https://github.com/wieslawsoltes/ProGPU/actions/runs/36437994714)
ran commit `552a73ced2c27468e59a1415d5a658ee7ccf3197` once per platform. All
three used .NET 10.0.12 with workstation GC and returned exit 1. Each retained
32 zero empty controls and 32 exact 64-byte positive controls.

| Runner | Architecture | Nonzero worker-phase intervals / 192 | Collection requests | Gen2 collections | Probe elapsed |
| --- | --- | --- | --- | --- | --- |
| Windows | X64 | 47 | 167 | 165 | 0.989 s |
| Ubuntu | X64 | 67 | 225 | 228 | 1.143 s |
| macOS 26 | ARM64 | 24 | 40 | 42 | 0.725 s |

The run preserves stdout, stderr, exit status and `dotnet --info` in separate
per-platform artifacts. This expands the independent reproduction beyond macOS;
it is not a product-test waiver or proof of the runtime's internal cause.
