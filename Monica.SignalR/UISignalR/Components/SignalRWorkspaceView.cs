using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Monica.SignalR.Localization;
using Monica.SignalR.Models;
using Monica.SignalR.UISignalR.Models;
using Monica.SignalR.UISignalR.State;
using Monica.SignalR.UISignalR.Support;
using Monica.UI.Shared.Components.Visual;
using Monica.UI.Shared.Services;

namespace Monica.SignalR.UISignalR.Components;

/// <summary>Provides borrowed family context, localized presentation and deployment-time formatting to SignalR views.</summary>
public abstract class SignalRWorkspaceView : ComponentBase
{
    /// <summary>Gets or sets the context supplied by the owning route component.</summary>
    [CascadingParameter] public SignalRWorkspaceContext Context { get; set; } = null!;
    /// <summary>Gets or sets the module's localized copy.</summary>
    [Inject] protected IStringLocalizer<SignalRResource> L { get; set; } = null!;
    /// <summary>Gets or sets the shell's deployment-time formatter.</summary>
    [Inject] protected MoTimeFormatter UiTime { get; set; } = null!;
    /// <summary>Gets the borrowed browser and capture state.</summary>
    protected SignalRDebugPageState S => Context.State;
    /// <summary>Formats a localized resource with optional positional values.</summary>
    protected string T(string key, params object[] values) => values.Length == 0 ? L[key].Value : L[key, values].Value;
    /// <summary>Gets a visual role's shared class.</summary>
    protected static string Role(MoVisualRole role) => role.ToCssClass();
    /// <summary>Formats a measured duration, keeping unobserved durations distinct from zero.</summary>
    protected string Ms(double value, bool observed = true) => observed ? $"{value.ToString("#,0.###", CultureInfo.CurrentCulture)} ms" : T("Workspace:NotObserved");
    /// <summary>Formats a connection age relative to the evidence capture, independently of the browser clock.</summary>
    protected string Age(DateTime start, DateTime? captured) => captured is { } end
        ? T("Workspace:Tenure", Math.Max(0, (int)(end - start).TotalHours), Math.Max(0, (int)(end - start).TotalMinutes % 60))
        : T("Workspace:NotObserved");
    /// <summary>Formats a contract signature with safe namespace wrapping opportunities.</summary>
    protected static string Signature(HubMethodInfo method, bool compact = false) => $"{method.Name}({string.Join(", ", method.Args.Select(arg => $"{(compact ? ShortType(arg.TypeName) : arg.TypeName)} {arg.Name}"))})".Replace(".", ".\u200b", StringComparison.Ordinal);
    private static string ShortType(string type) => type.Split('.')[^1] switch
    {
        "String" => "string", "Int32" => "int", "Int64" => "long", "Double" => "double", "Single" => "float", "Boolean" => "bool", var name => name
    };
    /// <summary>Gets the named mapped hub represented by the active method catalog.</summary>
    protected SignalRHubInfo? Catalog => S.AvailableHubs.FirstOrDefault(hub => hub.Route == S.MethodCatalogHubRoute);
    /// <summary>Gets the attention-method count from the last accepted send capture.</summary>
    protected int Attention => S.SendDiagnostics?.MethodMetrics.Count(SignalRDiagnosticRules.NeedsAttention) ?? 0;
    /// <summary>Gets the stable identity of a method aggregate.</summary>
    protected static string Key(SignalRSendMethodMetricInfo method) => $"{method.HubName}.{method.MethodName}";
    /// <summary>Formats the native string value for pressed-state accessibility.</summary>
    protected static string Aria(bool value) => value ? "true" : "false";

    /// <summary>Composes a semantic square using the shared visual vocabulary.</summary>
    protected static RenderFragment Swatch(MoVisualRole role) => builder =>
    {
        builder.OpenElement(0, "i");
        builder.AddAttribute(1, "class", $"mo-vl-swatch mo-vl-swatch--instrument {role.ToCssClass()}");
        builder.AddAttribute(2, "aria-hidden", "true");
        builder.CloseElement();
    };
    /// <summary>Composes a meaningful value inside localized finding slots.</summary>
    protected RenderFragment Mark(string text, MoVisualRole role, string? href = null) => builder =>
    {
        builder.OpenComponent<MoValueMark>(0);
        builder.AddAttribute(1, nameof(MoValueMark.Role), role);
        builder.AddAttribute(2, nameof(MoValueMark.Href), href is null ? null : Context.Href(href));
        builder.AddAttribute(3, nameof(MoValueMark.ChildContent), (RenderFragment)(content => content.AddContent(0, text)));
        builder.CloseComponent();
    };
    /// <summary>Composes unmarked evidence inside a localized finding.</summary>
    protected static RenderFragment Plain(string text) => builder => builder.AddContent(0, text);
    /// <summary>Composes an inline evidence command inside a finding.</summary>
    protected RenderFragment InspectMark(string text, MoVisualRole role, string query, string value) => builder =>
    {
        builder.OpenElement(0, "button");
        builder.AddAttribute(1, "type", "button");
        builder.AddAttribute(2, "class", $"mo-vl-mark sr-finding-link {role.ToCssClass()}");
        builder.AddAttribute(3, "onclick", EventCallback.Factory.Create(this, () => Context.Set(query, value, true)));
        builder.AddContent(4, text);
        builder.CloseElement();
    };
}
