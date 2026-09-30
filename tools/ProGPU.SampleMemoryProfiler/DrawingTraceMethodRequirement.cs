internal sealed class DrawingTraceMethodRequirement(string methodNamespace, string methodName)
{
    public string MethodNamespace { get; } = methodNamespace;
    public string MethodName { get; } = methodName;
    public bool Found { get; private set; }

    public void Observe(string? observedNamespace, string? observedName)
    {
        if (string.Equals(MethodNamespace, observedNamespace, StringComparison.Ordinal) &&
            string.Equals(MethodName, observedName, StringComparison.Ordinal))
            Found = true;
    }

    public static DrawingTraceMethodRequirement[] Parse(ReadOnlySpan<string> arguments)
    {
        // Additive admission only: callers cannot remove the original font check.
        var methods = new List<DrawingTraceMethodRequirement>
        {
            new("System.Drawing.Tests.FontQualityTests", "WarmedPrivateMetricReadsAreAllocationFree")
        };
        if (arguments.Length % 3 != 0)
            throw new ArgumentException("Expected --require-method TYPE METHOD for each additional metadata requirement.");
        for (int index = 0; index < arguments.Length; index += 3)
        {
            if (arguments[index] != "--require-method" ||
                string.IsNullOrWhiteSpace(arguments[index + 1]) ||
                string.IsNullOrWhiteSpace(arguments[index + 2]))
                throw new ArgumentException("Expected --require-method with nonempty exact CLR type and method names.");
            methods.Add(new(arguments[index + 1], arguments[index + 2]));
        }
        return methods.ToArray();
    }
}
