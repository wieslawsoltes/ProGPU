namespace ProGPU.Designer;

using System;
using System.Text;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;
using ProGPU.Layout;
using Thickness = Microsoft.UI.Xaml.Thickness;
using HorizontalAlignment = ProGPU.Layout.HorizontalAlignment;
using VerticalAlignment = ProGPU.Layout.VerticalAlignment;
using ProGPU.Scene;

public static class DesignerSerializer
{
    public static string SerializeToXaml(FrameworkElement element, int indentDepth = 0)
    {
        if (element == null) return "";

        var typeName = element.GetType().Name;
        var indent = new string(' ', indentDepth * 4);
        var sb = new StringBuilder();

        sb.Append($"{indent}<{typeName}");

        var properties = GetSerializableProperties(element);
        foreach (var prop in properties)
        {
            sb.Append($" {prop.Key}=\"{prop.Value}\"");
        }

        if (element is ContainerVisual container && container.Children.Count > 0)
        {
            sb.AppendLine(">");
            foreach (var child in container.Children)
            {
                if (child is FrameworkElement childFe)
                {
                    sb.Append(SerializeToXaml(childFe, indentDepth + 1));
                }
            }
            sb.AppendLine($"{indent}</{typeName}>");
        }
        else
        {
            sb.AppendLine(" />");
        }

        return sb.ToString();
    }

    public static string SerializeToJson(FrameworkElement element, int indentDepth = 0)
    {
        if (element == null) return "";

        var typeName = element.GetType().Name;
        var indent = new string(' ', indentDepth * 4);
        var innerIndent = new string(' ', (indentDepth + 1) * 4);
        var propIndent = new string(' ', (indentDepth + 2) * 4);
        var sb = new StringBuilder();

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{innerIndent}\"Type\": \"{typeName}\",");
        sb.AppendLine($"{innerIndent}\"Properties\": {{");

        var properties = GetSerializableProperties(element);
        int propIndex = 0;
        foreach (var prop in properties)
        {
            var comma = (propIndex < properties.Count - 1) ? "," : "";
            if (float.TryParse(prop.Value, out float numVal) && !prop.Key.Contains("Margin") && !prop.Key.Contains("Padding"))
            {
                sb.AppendLine($"{propIndent}\"{prop.Key}\": {numVal}{comma}");
            }
            else if (int.TryParse(prop.Value, out int intVal))
            {
                sb.AppendLine($"{propIndent}\"{prop.Key}\": {intVal}{comma}");
            }
            else
            {
                sb.AppendLine($"{propIndent}\"{prop.Key}\": \"{prop.Value}\"{comma}");
            }
            propIndex++;
        }
        sb.Append($"{innerIndent}}}");

        if (element is ContainerVisual container && container.Children.Count > 0)
        {
            sb.AppendLine(",");
            sb.AppendLine($"{innerIndent}\"Children\": [");
            for (int i = 0; i < container.Children.Count; i++)
            {
                if (container.Children[i] is FrameworkElement childFe)
                {
                    sb.Append(SerializeToJson(childFe, indentDepth + 2));
                    if (i < container.Children.Count - 1)
                    {
                        sb.AppendLine(",");
                    }
                    else
                    {
                        sb.AppendLine("");
                    }
                }
            }
            sb.AppendLine($"{innerIndent}]");
        }
        else
        {
            sb.AppendLine("");
        }

        sb.Append($"{indent}}}");
        return sb.ToString();
    }

    private static Dictionary<string, string> GetSerializableProperties(FrameworkElement element)
    {
        var dict = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(element.Name))
        {
            dict["Name"] = element.Name;
        }

        if (!float.IsNaN(element.Width))
        {
            dict["Width"] = element.Width.ToString();
        }

        if (!float.IsNaN(element.Height))
        {
            dict["Height"] = element.Height.ToString();
        }

        if (element.HorizontalAlignment != HorizontalAlignment.Stretch)
        {
            dict["HorizontalAlignment"] = element.HorizontalAlignment.ToString();
        }

        if (element.VerticalAlignment != VerticalAlignment.Stretch)
        {
            dict["VerticalAlignment"] = element.VerticalAlignment.ToString();
        }

        var margin = element.Margin;
        if (margin.Left != 0 || margin.Top != 0 || margin.Right != 0 || margin.Bottom != 0)
        {
            dict["Margin"] = $"{margin.Left},{margin.Top},{margin.Right},{margin.Bottom}";
        }

        var padding = element.Padding;
        if (padding.Left != 0 || padding.Top != 0 || padding.Right != 0 || padding.Bottom != 0)
        {
            dict["Padding"] = $"{padding.Left},{padding.Top},{padding.Right},{padding.Bottom}";
        }

        if (element.Opacity != 1.0f)
        {
            dict["Opacity"] = element.Opacity.ToString("0.00");
        }

        var type = element.GetType();
        
        var cornerRadiusProp = type.GetProperty("CornerRadius");
        if (cornerRadiusProp != null)
        {
            var cr = cornerRadiusProp.GetValue(element);
            if (cr is float fcr && fcr != 0f)
            {
                dict["CornerRadius"] = fcr.ToString();
            }
        }

        var backgroundProp = type.GetProperty("Background");
        if (backgroundProp != null)
        {
            var bg = backgroundProp.GetValue(element);
            if (bg is SolidColorBrush scb)
            {
                var colorVal = scb.Color;
                dict["Background"] = $"#{(byte)(colorVal.W * 255):X2}{(byte)(colorVal.X * 255):X2}{(byte)(colorVal.Y * 255):X2}{(byte)(colorVal.Z * 255):X2}";
            }
            else if (bg is ThemeResourceBrush trb)
            {
                dict["Background"] = trb.ResourceKey;
            }
        }

        var textProp = type.GetProperty("Text");
        if (textProp != null && textProp.PropertyType == typeof(string))
        {
            var text = (string)(textProp.GetValue(element) ?? "");
            if (!string.IsNullOrEmpty(text))
            {
                dict["Text"] = text;
            }
        }

        var contentProp = type.GetProperty("Content");
        if (contentProp != null)
        {
            var content = contentProp.GetValue(element);
            if (content is string s && !string.IsNullOrEmpty(s))
            {
                dict["Content"] = s;
            }
        }

        int row = Microsoft.UI.Xaml.Controls.Grid.GetRow(element);
        if (row != 0)
        {
            dict["Grid.Row"] = row.ToString();
        }

        int col = Microsoft.UI.Xaml.Controls.Grid.GetColumn(element);
        if (col != 0)
        {
            dict["Grid.Column"] = col.ToString();
        }

        return dict;
    }
}
