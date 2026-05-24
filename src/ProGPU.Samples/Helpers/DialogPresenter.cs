using System;
using ProGPU.WinUI;

namespace ProGPU.Samples;

public static class DialogPresenter
{
    public static void ShowResetDialog(RichTextBlock dialogResultText)
    {
        ShowAsyncAndCallback(dialogResultText);
    }

    private static async void ShowAsyncAndCallback(RichTextBlock dialogResultText)
    {
        var dialog = new ContentDialog
        {
            Title = "Perform Critical Diagnostics Reset?",
            Content = "Are you sure you want to completely flush the active GPU layout caches? This resets frame counters.",
            PrimaryButtonText = "Flush Cache",
            SecondaryButtonText = "Cancel"
        };
        var res = await dialog.ShowAsync();
        dialogResultText.Inlines.Clear();
        var run = new Run { Text = "Last Dialog Response: " + res.ToString() };
        dialogResultText.Inlines.Add(run);
        dialogResultText.Invalidate();
    }
}
