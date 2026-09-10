namespace ProGPU.CAD;

public enum CadDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public readonly record struct CadDiagnostic(
    CadDiagnosticSeverity Severity,
    string Code,
    string Message);

public enum CadOperationStage
{
    Preparing = 0,
    Reading = 1,
    Building = 2,
    Writing = 3,
    Completed = 4
}

public readonly record struct CadOperationProgress(
    CadOperationStage Stage,
    ulong CurrentHandle,
    string? ObjectType);
