using System;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Fonts.Inter;
using ProGPU.Fonts.Noto;
using ProGPU.Text;

namespace ProGPU.Samples;

public static class SampleFontLoader
{
    public static void EnsureLoaded(string logPrefix = "[ProGPU.Samples]")
    {
        InterFontFamily.RegisterFonts();
        TtfFont primary = InterFontFamily.Regular;
        FontApi.RegisterPlatformFallbackFont(primary);
        NotoFontFamily.RegisterFallbacks();
        PopupService.DefaultFont = primary;
        AppState._font = primary;
        Console.WriteLine($"{logPrefix} Loading bundled Inter {InterFontFamily.Version} UI Font");

        AppState._fontArial =
            MatchSystemFont("Arial", "Arial", "Helvetica", "Liberation Sans", "DejaVu Sans") ?? primary;
        AppState._fontTimes =
            MatchSystemFont("Times New Roman", "Times New Roman", "Times", "Liberation Serif", "DejaVu Serif") ?? primary;
        AppState._fontCourier =
            MatchSystemFont("Courier New", "Courier New", "Courier", "Liberation Mono", "DejaVu Sans Mono") ?? primary;
        AppState._fontGeorgia =
            MatchSystemFont("Georgia", "Georgia", "DejaVu Serif", "Liberation Serif") ?? primary;
        AppState._fontComic =
            MatchSystemFont("Comic Sans MS", "Comic Sans MS", "Comic Sans", "Comic Neue", "Comic Relief") ?? primary;

        _ = FontApi.WarmUpSystemFontsAsync();
        _ = TextLayout.WarmUpFallbackMetadataAsync();
    }

    private static TtfFont? MatchSystemFont(string displayName, params string[] familyNames)
    {
        for (var index = 0; index < familyNames.Length; index++)
        {
            TtfFont? font = FontApi.Manager.MatchFamily(familyNames[index]);
            if (font is not null)
            {
                return font;
            }
        }

        Console.WriteLine($"[ProGPU.Samples] Optional system font '{displayName}' is unavailable; using Inter for that sample entry.");
        return null;
    }
}
