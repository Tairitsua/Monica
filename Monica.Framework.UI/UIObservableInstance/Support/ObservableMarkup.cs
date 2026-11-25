using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Monica.UI.Shared.Components.Visual;

namespace Monica.Framework.UI.UIObservableInstance.Support;

internal static partial class ObservableMarkup
{
    internal static RenderFragment Format(string template, params RenderFragment[] parts) => builder =>
    {
        foreach (var segment in Placeholder().Split(template))
        {
            if (int.TryParse(segment, out var index) && index >= 0 && index < parts.Length)
                builder.AddContent(0, parts[index]);
            else
                builder.AddContent(1, segment);
        }
    };

    internal static RenderFragment Mark(object value, MoVisualRole role, EventCallback action = default) => builder =>
    {
        builder.OpenElement(0, action.HasDelegate ? "button" : "span");
        builder.AddAttribute(1, "class", $"mo-vl-mark {role.ToCssClass()}");
        if (action.HasDelegate)
        {
            builder.AddAttribute(2, "type", "button");
            builder.AddAttribute(3, "onclick", action);
        }
        builder.AddContent(4, value.ToString());
        builder.CloseElement();
    };

    internal static RenderFragment Breakable(string text) => builder =>
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('.' or '_' or '/')) continue;
            builder.AddContent(0, text[start..(index + 1)]);
            builder.AddMarkupContent(1, "<wbr>");
            start = index + 1;
        }
        builder.AddContent(2, text[start..]);
    };

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();
}
