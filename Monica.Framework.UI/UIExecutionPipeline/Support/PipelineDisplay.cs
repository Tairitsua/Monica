using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.Core.Execution;
using Monica.Core.Execution.Models;

namespace Monica.Framework.UI.UIExecutionPipeline.Support;

internal static partial class PipelineDisplay
{
    internal static string Aria(bool value) => value ? "true" : "false";

    internal static string Transaction(IStringLocalizer l, ExecutionTransactionMode mode) =>
        mode == ExecutionTransactionMode.Automatic ? l["Pipeline:Tx:Automatic"] : l["Pipeline:Tx:None"];

    internal static string Lifetime(IStringLocalizer l, ServiceLifetime lifetime) => lifetime switch
    {
        ServiceLifetime.Singleton => l["Pipeline:Lifetimes:Singleton"],
        ServiceLifetime.Scoped => l["Pipeline:Lifetimes:Scoped"],
        _ => l["Pipeline:Lifetimes:Transient"]
    };

    internal static string Status(IStringLocalizer l, ExecutionPipelinePlanStatus status) => status switch
    {
        ExecutionPipelinePlanStatus.Ready => l["Pipeline:Statuses:Ready"],
        ExecutionPipelinePlanStatus.Building => l["Pipeline:Statuses:Building"],
        _ => l["Pipeline:Statuses:Faulted"]
    };

    internal static string Band(IStringLocalizer l, string band) => band switch
    {
        "Diagnostics" => l["Pipeline:Bands:Diagnostics"],
        "Authorization" => l["Pipeline:Bands:Authorization"],
        "Routing" => l["Pipeline:Bands:Routing"],
        "Validation" => l["Pipeline:Bands:Validation"],
        "UnitOfWork" => l["Pipeline:Bands:UnitOfWork"],
        _ => l["Pipeline:Bands:Application"]
    };

    internal static string Duration(IStringLocalizer l, TimeSpan elapsed) => elapsed.TotalMinutes >= 1
        ? l["Pipeline:Minutes", elapsed.TotalMinutes.ToString("0.#")]
        : elapsed.TotalSeconds >= 1 ? l["Pipeline:Seconds", elapsed.TotalSeconds.ToString("0.00")]
        : l["Pipeline:Ms", Math.Max(0, elapsed.TotalMilliseconds).ToString("0.00")];

    internal static string ExceptionName(ExecutionPipelinePlanSnapshot plan) => plan.Error?.ExceptionType.Split('.').Last() ?? "";

    internal static RenderFragment BreakName(string name) => builder =>
    {
        foreach (var part in NameBoundaries().Split(name).Where(part => part.Length > 0))
        {
            builder.AddContent(0, part);
            builder.OpenElement(1, "wbr");
            builder.CloseElement();
        }
    };

    internal static RenderFragment Sentence(LocalizedString template, params RenderFragment[] parts) => builder =>
    {
        foreach (var part in Placeholders().Split(template.Value))
        {
            if (part.StartsWith('{') && int.TryParse(part.AsSpan(1, part.Length - 2), out var index) &&
                index < parts.Length)
            {
                builder.AddContent(0, parts[index]);
            }
            else builder.AddContent(1, part);
        }
    };

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=\.)")]
    private static partial Regex NameBoundaries();

    [GeneratedRegex(@"(\{\d+\})")]
    private static partial Regex Placeholders();
}
