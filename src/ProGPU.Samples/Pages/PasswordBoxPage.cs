using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public static class PasswordBoxPage
{
    private static string _passwordText = "Antigravity100%";

    public static FrameworkElement Create()
    {
        var scrollViewer = new ScrollViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        scrollViewer.Content = stack;

        // Page Header
        var title = new RichTextBlock { Font = AppState._font, FontSize = 22f, Margin = new Thickness(0, 0, 0, 6) };
        title.Inlines.Add(new Bold(new Run("PasswordBox Control")));
        stack.AddChild(title);

        var description = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 24) };
        description.Inlines.Add(new Run("PasswordBox provides credential masking for password inputs. It features secure Clipboard protection (disabled Copy and Cut shortcuts) and an interactive reveal button (Eye icon)."));
        stack.AddChild(description);

        var passwordCard = CreateShowcaseCard("Credential Masking & Reveal Toggle");
        var passwordStack = new StackPanel { Orientation = Orientation.Vertical };

        var passDesc = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 16) };
        passDesc.Inlines.Add(new Run("Type characters to see secure masked entry. Click the "));
        passDesc.Inlines.Add(new Bold(new Run("Eye Icon")));
        passDesc.Inlines.Add(new Run(" on the right edge to toggle plain-text reveal. Copy and Cut shortcuts are blocked for security."));
        passwordStack.AddChild(passDesc);

        // Demo A: Standard PasswordBox
        var pb1Title = new RichTextBlock { Font = AppState._font, FontSize = 13f, Margin = new Thickness(0, 0, 0, 8) };
        pb1Title.Inlines.Add(new Bold(new Run("Standard Password Input Box:")));
        passwordStack.AddChild(pb1Title);

        var pb1 = new PasswordBox { Password = _passwordText, WidthConstraint = 300f, Margin = new Thickness(0, 0, 0, 8) };
        var pb1Status = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 20) };
        pb1Status.Inlines.Add(new Run("Actual plain password state: "));
        pb1Status.Inlines.Add(new Bold(new Run(_passwordText)) { Foreground = new ThemeResourceBrush("SystemAccentColor") });

        pb1.PasswordChanged += (s, e) =>
        {
            _passwordText = pb1.Password;
            pb1Status.Inlines.Clear();
            pb1Status.Inlines.Add(new Run("Actual plain password state: "));
            pb1Status.Inlines.Add(new Bold(new Run(_passwordText)) { Foreground = new ThemeResourceBrush("SystemAccentColor") });
            pb1Status.Invalidate();
        };

        passwordStack.AddChild(pb1);
        passwordStack.AddChild(pb1Status);

        // Demo B: Custom Asterisk Mask and Disabled Box
        var pb2Title = new RichTextBlock { Font = AppState._font, FontSize = 13f, Margin = new Thickness(0, 0, 0, 8) };
        pb2Title.Inlines.Add(new Bold(new Run("Custom Asterisk Mask (*) & Disabled state:")));
        passwordStack.AddChild(pb2Title);

        var customPassBox = new PasswordBox { Password = "SecureUserPass", PasswordChar = '*', WidthConstraint = 300f, Margin = new Thickness(0, 0, 0, 10) };
        var disabledPassBox = new PasswordBox { Password = "DisabledPassWord", IsEnabled = false, WidthConstraint = 300f, Margin = new Thickness(0, 0, 0, 8) };

        passwordStack.AddChild(customPassBox);
        passwordStack.AddChild(disabledPassBox);

        passwordCard.Child = passwordStack;
        stack.AddChild(passwordCard);

        return scrollViewer;
    }

    private static Border CreateShowcaseCard(string headerText)
    {
        var border = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var mainStack = new StackPanel { Orientation = Orientation.Vertical };
        var header = new RichTextBlock { Font = AppState._font, FontSize = 15f, Margin = new Thickness(0, 0, 0, 16) };
        header.Inlines.Add(new Bold(new Run(headerText)));
        
        mainStack.AddChild(header);
        
        // Horizontal divider stripe
        var divider = new Border
        {
            Height = 1f,
            Background = new ThemeResourceBrush("ControlBorder"),
            Margin = new Thickness(0, 0, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        mainStack.AddChild(divider);

        // Content placeholder panel
        var contentPanel = new Border { HorizontalAlignment = HorizontalAlignment.Stretch };
        mainStack.AddChild(contentPanel);

        border.Child = mainStack;

        // Custom content wrapper
        border.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Border.Child) && border.Child != mainStack)
            {
                var content = border.Child;
                border.Child = mainStack;
                contentPanel.Child = content;
            }
        };

        return border;
    }
}
