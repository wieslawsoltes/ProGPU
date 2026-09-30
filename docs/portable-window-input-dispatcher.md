# Source-owned portable window input dispatch

`ProGPU.Wpf.Interop.IPortableWindowInputDispatcher` is an additive optional
capability with `bool TryPostInput(object window, Action callback)`. A source
window activation registrar may implement it without changing the existing
`IPortableWindowActivationServiceRegistrar` interface or `TryBeginInvokeInput`
semantics. Existing providers do not acquire a new required member.

Accepted callbacks are always queued at the window's actual source dispatcher
Input priority, including calls already on its owner thread. They must not run
inline. A true return transfers queue ownership; it does not promise completion
after dispatcher shutdown. A false return means the provider neither invokes nor
retains the callback. The source provider owns window identity and
disposed/shutdown admission. There is no reflection, synchronous invocation,
thread-pool substitute or private host-queue fallback when the capability is
missing or rejects work.

This permits source Input work to participate in the same dispatcher ordering as
source Background barriers. An invisible host queue is not an equivalent
implementation. Consumers must still retain their own input/source-generation
validation when accepted work executes.

The interface is dependency-free managed metadata shared by both renderer hosts;
it changes no managed or native rendering algorithm, window factory, native
polling, or C ABI. Two regression cases preserve its additive API shape and the
neutral interop project's dependency graph. They do not qualify a provider's
queue ordering. Actual WPF provider/consumer integration, shutdown/reentrancy
checks and package/application qualification remain separate requirements.
