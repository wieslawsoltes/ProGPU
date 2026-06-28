using System.Reflection;

namespace ProGPU.Wpf.Interop;

public interface IPortableClipboardServiceRegistrar
{
    Assembly SourceAssembly { get; }

    IDisposable Register(Func<string?> getText, Action<string?> setText);

    void Clear();
}

public static class PortableWpfServiceRegistry
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<Assembly, IPortableClipboardServiceRegistrar> ClipboardServices = new();

    public static IDisposable RegisterClipboardService(IPortableClipboardServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (SyncRoot)
        {
            ClipboardServices[service.SourceAssembly] = service;
        }

        return new Registration(service);
    }

    public static bool TryGetClipboardService(
        Assembly sourceAssembly,
        out IPortableClipboardServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(sourceAssembly);

        lock (SyncRoot)
        {
            return ClipboardServices.TryGetValue(sourceAssembly, out service!);
        }
    }

    private sealed class Registration : IDisposable
    {
        private IPortableClipboardServiceRegistrar? _service;

        public Registration(IPortableClipboardServiceRegistrar service)
        {
            _service = service;
        }

        public void Dispose()
        {
            var service = _service;
            if (service == null)
            {
                return;
            }

            _service = null;

            lock (SyncRoot)
            {
                if (ClipboardServices.TryGetValue(service.SourceAssembly, out var current) &&
                    ReferenceEquals(current, service))
                {
                    ClipboardServices.Remove(service.SourceAssembly);
                }
            }
        }
    }
}
