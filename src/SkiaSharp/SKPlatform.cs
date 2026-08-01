using System.Runtime.InteropServices;

namespace SkiaSharp.Internals
{
    public static class PlatformConfiguration
    {
        private static string s_linuxFlavor =
            RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
                ? "musl"
                : "glibc";

        public static bool Is64Bit => Environment.Is64BitProcess;

        public static bool IsArm =>
            RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64;

        public static bool IsGlibc => IsLinux && !s_linuxFlavor.Equals("musl", StringComparison.OrdinalIgnoreCase);

        public static bool IsLinux => OperatingSystem.IsLinux();

        public static bool IsMac => OperatingSystem.IsMacOS();

        public static bool IsUnix => IsLinux || IsMac || OperatingSystem.IsFreeBSD();

        public static bool IsWindows => OperatingSystem.IsWindows();

        public static string LinuxFlavor
        {
            get => Volatile.Read(ref s_linuxFlavor);
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                Volatile.Write(ref s_linuxFlavor, value);
            }
        }
    }

    public interface IPlatformLock
    {
        void EnterReadLock();

        void ExitReadLock();

        void EnterUpgradeableReadLock();

        void ExitUpgradeableReadLock();

        void EnterWriteLock();

        void ExitWriteLock();
    }

    public static class PlatformLock
    {
        private static Func<IPlatformLock> s_factory = DefaultFactory;

        public static Func<IPlatformLock> Factory
        {
            get => Volatile.Read(ref s_factory);
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                Volatile.Write(ref s_factory, value);
            }
        }

        public static IPlatformLock Create() => Factory();

        public static IPlatformLock DefaultFactory() => new ReaderWriterPlatformLock();

        private sealed class ReaderWriterPlatformLock : IPlatformLock
        {
            private readonly ReaderWriterLockSlim _lock =
                new(LockRecursionPolicy.SupportsRecursion);

            public void EnterReadLock() => _lock.EnterReadLock();

            public void ExitReadLock() => _lock.ExitReadLock();

            public void EnterUpgradeableReadLock() => _lock.EnterUpgradeableReadLock();

            public void ExitUpgradeableReadLock() => _lock.ExitUpgradeableReadLock();

            public void EnterWriteLock() => _lock.EnterWriteLock();

            public void ExitWriteLock() => _lock.ExitWriteLock();
        }
    }
}

namespace SkiaSharp
{
    public class SKAutoCoInitialize : IDisposable
    {
        private const uint CoInitMultithreaded = 0;
        private int _initialized;

        public SKAutoCoInitialize()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var result = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
            if (result >= 0)
            {
                _initialized = 1;
            }
        }

        public bool Initialized => Volatile.Read(ref _initialized) != 0;

        public void Uninitialize()
        {
            if (Interlocked.Exchange(ref _initialized, 0) != 0 && OperatingSystem.IsWindows())
            {
                CoUninitialize();
            }
        }

        public void Dispose()
        {
            Uninitialize();
            GC.SuppressFinalize(this);
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
    }
}
