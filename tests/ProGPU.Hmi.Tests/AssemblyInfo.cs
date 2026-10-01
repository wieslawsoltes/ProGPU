using Xunit;
// WinUI input/theme services have process-wide state. Do not race independent UI fixtures.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
