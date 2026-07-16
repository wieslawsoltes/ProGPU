using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Text;

namespace ProGPU.Samples;

public static class SampleFontLoader
{
    public static void EnsureLoaded(string logPrefix = "[ProGPU.Samples]")
    {
        var primary = PopupService.DefaultFont
            ?? LoadRequiredFont(
                "Arial",
                new[] { "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans" },
                FontPathCandidates(
                    "Arial.ttf",
                    "arial.ttf",
                    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                    "/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf",
                    "/usr/share/fonts/liberation/LiberationSans-Regular.ttf"));

        AppState._font = primary;
        PopupService.DefaultFont = primary;
        Console.WriteLine($"{logPrefix} Loading System Font");

        AppState._fontTimes =
            TryLoadFont(
                new[] { "Times New Roman", "Times", "Liberation Serif", "DejaVu Serif" },
                FontPathCandidates(
                    "Times New Roman.ttf",
                    "times.ttf",
                    "/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf",
                    "/usr/share/fonts/truetype/liberation2/LiberationSerif-Regular.ttf",
                    "/usr/share/fonts/liberation/LiberationSerif-Regular.ttf"))
            ?? primary;

        AppState._fontCourier =
            TryLoadFont(
                new[] { "Courier New", "Courier", "Liberation Mono", "DejaVu Sans Mono" },
                FontPathCandidates(
                    "Courier New.ttf",
                    "cour.ttf",
                    "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
                    "/usr/share/fonts/truetype/liberation2/LiberationMono-Regular.ttf",
                    "/usr/share/fonts/liberation/LiberationMono-Regular.ttf"))
            ?? primary;

        AppState._fontGeorgia =
            TryLoadFont(
                new[] { "Georgia", "DejaVu Serif", "Liberation Serif" },
                FontPathCandidates(
                    "Georgia.ttf",
                    "georgia.ttf",
                    "/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf",
                    "/usr/share/fonts/truetype/liberation2/LiberationSerif-Regular.ttf",
                    "/usr/share/fonts/liberation/LiberationSerif-Regular.ttf"))
            ?? primary;

        AppState._fontComic =
            TryLoadFont(
                new[] { "Comic Sans MS", "Comic Sans", "Comic Neue", "Comic Relief" },
                FontPathCandidates(
                    "Comic Sans MS.ttf",
                    "comic.ttf",
                    "/usr/share/fonts/truetype/comic-neue/ComicNeue-Regular.ttf"))
            ?? primary;

        _ = FontApi.WarmUpSystemFontsAsync();
        _ = TextLayout.WarmUpFallbackMetadataAsync();
    }

    private static TtfFont LoadRequiredFont(string displayName, string[] fontNames, IEnumerable<string> pathCandidates)
    {
        var font = TryLoadFont(fontNames, pathCandidates);
        if (font != null)
        {
            return font;
        }

        throw new FileNotFoundException(
            $"{displayName} or a compatible system font is required to execute typography.");
    }

    private static TtfFont? TryLoadFont(string[] fontNames, IEnumerable<string> pathCandidates)
    {
        foreach (var path in pathCandidates)
        {
            if (TryResolveExistingPath(path, out var resolvedPath) &&
                TryCreateFont(resolvedPath, faceIndex: 0, out var font))
            {
                return font;
            }
        }

        var fontInfo = FontApi.FindSystemFont(fontNames);
        if (fontInfo != null && TryCreateFont(fontInfo.FilePath, fontInfo.FaceIndex, out var systemFont))
        {
            return systemFont;
        }

        return null;
    }

    private static IEnumerable<string> FontPathCandidates(
        string crossPlatformFileName,
        string windowsFileName,
        params string[] extraPaths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in PlatformFontPathCandidates(crossPlatformFileName, windowsFileName, extraPaths))
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> PlatformFontPathCandidates(
        string crossPlatformFileName,
        string windowsFileName,
        string[] extraPaths)
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windowsDirectory))
            {
                yield return Path.Combine(windowsDirectory, "Fonts", windowsFileName);
                yield return Path.Combine(windowsDirectory, "Fonts", crossPlatformFileName);
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return $"/System/Library/Fonts/Supplemental/{crossPlatformFileName}";
            yield return $"/System/Library/Fonts/{crossPlatformFileName}";
            yield return $"/Library/Fonts/{crossPlatformFileName}";
        }

        yield return crossPlatformFileName;
        yield return Path.Combine(AppContext.BaseDirectory, crossPlatformFileName);

        foreach (var path in extraPaths)
        {
            yield return path;
        }

        yield return $"/System/Library/Fonts/Supplemental/{crossPlatformFileName}";
        yield return $"/System/Library/Fonts/{crossPlatformFileName}";
        yield return $"/Library/Fonts/{crossPlatformFileName}";
    }

    private static bool TryResolveExistingPath(string path, out string resolvedPath)
    {
        try
        {
            if (File.Exists(path))
            {
                resolvedPath = Path.GetFullPath(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
        }

        resolvedPath = string.Empty;
        return false;
    }

    private static bool TryCreateFont(string path, int faceIndex, out TtfFont font)
    {
        try
        {
            font = new TtfFont(path, faceIndex);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            font = null!;
            return false;
        }
    }
}
