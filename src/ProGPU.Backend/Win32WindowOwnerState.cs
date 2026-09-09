namespace ProGPU.Backend;

internal interface IWin32WindowOwnerOperations
{
    bool IsLocalTopLevel(nint window);
    bool TryGetOwner(nint window, out nint owner);
    bool TrySetOwner(nint window, nint owner);
}

/// <summary>Top-level ownership admission; no child reparenting or popup styles.</summary>
internal static class Win32WindowOwnerState
{
    public static bool Apply<TOperations>(nint window, nint owner, ref TOperations operations)
        where TOperations : IWin32WindowOwnerOperations
    {
        if (window == 0 || owner == window || !operations.IsLocalTopLevel(window)) return false;
        // The native chain is authoritative, including owners assigned outside
        // this controller. Bound malformed/cyclic external chains without storage.
        nint ancestor = owner;
        for (int depth = 0; ancestor != 0; depth++)
        {
            if (depth == 1024 || ancestor == window || !operations.IsLocalTopLevel(ancestor) ||
                !operations.TryGetOwner(ancestor, out ancestor)) return false;
        }
        if (!operations.TryGetOwner(window, out nint previous)) return false;
        if (previous == owner) return true;
        return operations.TrySetOwner(window, owner) &&
            operations.IsLocalTopLevel(window) &&
            (owner == 0 || operations.IsLocalTopLevel(owner)) &&
            operations.TryGetOwner(window, out nint current) && current == owner;
    }
}
