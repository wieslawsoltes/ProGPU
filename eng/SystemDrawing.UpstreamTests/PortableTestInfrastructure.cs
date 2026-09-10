global using System.Diagnostics.CodeAnalysis;

using System.Drawing.Printing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit.Sdk;

namespace System
{
    public static class PlatformDetection
    {
        public static bool IsArm64Process => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        public static bool IsArmOrArm64Process =>
            RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64;

        public static bool IsNetFramework => false;

        public static bool IsNotArm64Process => !IsArm64Process;

        public static bool IsNotBuiltWithAggressiveTrimming => RuntimeFeature.IsDynamicCodeSupported;

        public static bool IsNotIntMaxValueArrayIndexSupported => true;

        public static bool IsNotWindowsIoTCore => true;

        public static bool IsWindows => OperatingSystem.IsWindows();

        public static bool IsWindows7 =>
            OperatingSystem.IsWindows() && Environment.OSVersion.Version <= new Version(6, 1);

        public static bool IsWindows8x =>
            OperatingSystem.IsWindows() && Environment.OSVersion.Version is { Major: 6, Minor: 2 or 3 };
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class ConditionalClassAttribute(Type conditionType, string conditionMemberName) : Attribute
    {
        public Type ConditionType { get; } = conditionType;

        public string ConditionMemberName { get; } = conditionMemberName;
    }

    public static class AssertExtensions
    {
        public static T Throws<T>(string? expectedParamName, Action action)
            where T : ArgumentException
        {
            T exception = Assert.Throws<T>(action);
            Assert.Equal(expectedParamName, exception.ParamName);
            return exception;
        }

        public static T Throws<T>(string? expectedParamName, Func<object?> action)
            where T : ArgumentException
        {
            T exception = Assert.Throws<T>(action);
            Assert.Equal(expectedParamName, exception.ParamName);
            return exception;
        }

        public static T Throws<T>(string? netCoreParamName, string? _, Action action)
            where T : ArgumentException => Throws<T>(netCoreParamName, action);

        public static void Throws<T>(string? netCoreParamName, string? _, Func<object?> action)
            where T : ArgumentException => Throws<T>(netCoreParamName, action);

        public static T Throws<T>(Action action)
            where T : Exception => Assert.Throws<T>(action);

        public static TException Throws<TException, TResult>(Func<TResult> action)
            where TException : Exception => Assert.Throws<TException>(() => action());

        public static void Throws<TNetCoreException, TNetFrameworkException>(string? expectedParamName, Action action)
            where TNetCoreException : ArgumentException
            where TNetFrameworkException : Exception => Throws<TNetCoreException>(expectedParamName, action);

        public static void Throws<TNetCoreException, TNetFrameworkException>(
            string? netCoreParamName,
            string? _,
            Action action)
            where TNetCoreException : ArgumentException
            where TNetFrameworkException : ArgumentException => Throws<TNetCoreException>(netCoreParamName, action);

        public static Exception Throws<TNetCoreException, TNetFrameworkException>(Action action)
            where TNetCoreException : Exception
            where TNetFrameworkException : Exception => Assert.Throws<TNetCoreException>(action);

        public static void ThrowsAny<TFirst, TSecond>(Action action)
            where TFirst : Exception
            where TSecond : Exception => ThrowsAnyInternal(action, typeof(TFirst), typeof(TSecond));

        public static void ThrowsAny<TFirst, TSecond, TThird>(Action action)
            where TFirst : Exception
            where TSecond : Exception
            where TThird : Exception => ThrowsAnyInternal(action, typeof(TFirst), typeof(TSecond), typeof(TThird));

        public static void GreaterThan<T>(T actual, T expectedLowerBound, string? userMessage = null)
            where T : IComparable
        {
            if (actual.CompareTo(expectedLowerBound) <= 0)
            {
                throw new XunitException(userMessage ?? $"Expected {actual} to be greater than {expectedLowerBound}.");
            }
        }

        private static void ThrowsAnyInternal(Action action, params Type[] expectedTypes)
        {
            Exception exception = Assert.ThrowsAny<Exception>(action);
            Assert.Contains(exception.GetType(), expectedTypes);
        }
    }
}

namespace System.IO
{
    public abstract class FileCleanupTestBase : IDisposable
    {
        protected FileCleanupTestBase()
        {
            TestDirectory = Path.Combine(Path.GetTempPath(), $"#progpu_{GetType().Name}_{Path.GetRandomFileName()}");
            Directory.CreateDirectory(TestDirectory);
        }

        protected string TestDirectory { get; }

        protected string GetTestFilePath(
            int? index = null,
            [CallerMemberName] string memberName = "test",
            [CallerLineNumber] int lineNumber = 0) =>
            Path.Combine(TestDirectory, $"{memberName}_{lineNumber}{(index is null ? string.Empty : $"_{index}")}");

        public void Dispose()
        {
            try
            {
                Directory.Delete(TestDirectory, recursive: true);
            }
            catch
            {
                // Cleanup failures must not hide the behavior under test.
            }

            GC.SuppressFinalize(this);
        }
    }
}

namespace System.XUnit
{
    internal readonly struct PortableNamespaceMarker;
}

namespace System.Drawing
{
    public static class Helpers
    {
        public static PrinterSettings.StringCollection InstalledPrinters { get; } = PrinterSettings.InstalledPrinters;

        public static bool AnyInstalledPrinters => InstalledPrinters.Count > 0;

        public static bool CanPrintToPdf => InstalledPrinters.Cast<string>().Any(static name =>
            name.StartsWith("Microsoft Print to PDF", StringComparison.Ordinal));

        public static bool TryGetPdfPrinterName(out string? printerName)
        {
            printerName = InstalledPrinters.Cast<string>().FirstOrDefault(static name =>
                name.StartsWith("Microsoft Print to PDF", StringComparison.Ordinal));
            return printerName is not null;
        }

        public static string GetTestBitmapPath(string fileName) => GetTestPath("bitmaps", fileName);

        public static string GetTestFontPath(string fileName) => GetTestPath("fonts", fileName);

        public static string GetTestColorProfilePath(string fileName) => GetTestPath("colorProfiles", fileName);

        public static Color EmptyColor => Color.FromArgb(0, 0, 0, 0);

        public static void VerifyBitmap(Bitmap bitmap, Color[][] colors)
        {
            for (int y = 0; y < colors.Length; y++)
            {
                for (int x = 0; x < colors[y].Length; x++)
                {
                    Assert.Equal(Color.FromArgb(colors[y][x].ToArgb()), bitmap.GetPixel(x, y));
                }
            }
        }

        public static void VerifyBitmapNotBlank(Bitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y) != Color.FromArgb(0))
                    {
                        return;
                    }
                }
            }

            throw new XunitException("The entire image was blank.");
        }

        private static string GetTestPath(string directoryName, string fileName) =>
            Path.Join(AppContext.BaseDirectory, directoryName, fileName);
    }
}

namespace Windows.Win32.Foundation
{
    internal readonly struct PortableNamespaceMarker;
}

namespace Xunit
{
    [Flags]
    public enum TestArchitectures
    {
        X86 = 1,
        X64 = 2,
        Any = ~0
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class SkipOnArchitectureAttribute(TestArchitectures architectures, string reason) : Attribute
    {
        public TestArchitectures Architectures { get; } = architectures;

        public string Reason { get; } = reason;
    }
}
