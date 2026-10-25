using System.Globalization;
using Microsoft.Extensions.Localization;
using Monica.DataChannel.Localization;
using Monica.DataChannel.UIDataChannel.Models;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.DataChannel.UIDataChannel.Support;

/// <summary>Formats captured DataChannel evidence without reading live host state.</summary>
internal static class DataChannelPresentation
{
    public static string Initialization(DataChannelStatusInfo channel) => channel switch
    {
        { IsInitializing: true } => "Initializing",
        { IsNotAvailable: true } => "Unavailable",
        { IsInitialized: true } => "Initialized",
        _ => "NotInitialized"
    };
    public static Color InitializationColor(DataChannelStatusInfo channel) => Initialization(channel) switch
    {
        "Initializing" => Color.Info, "Unavailable" => Color.Error,
        "Initialized" => Color.Success, _ => Color.Default
    };
    public static string InitializationIcon(DataChannelStatusInfo channel) => Initialization(channel) switch
    {
        "Initializing" => MoIcons.Refresh,
        "Unavailable" => MoIcons.Error,
        "Initialized" => MoIcons.Success,
        _ => MoIcons.Circle
    };
    public static string KindKey(PipelineComponentKind kind) => kind switch
    {
        PipelineComponentKind.Endpoint => "Endpoint",
        PipelineComponentKind.TransformMiddleware => "Transform",
        PipelineComponentKind.MonitorMiddleware => "Monitor",
        PipelineComponentKind.EndpointMiddleware => "EndpointMiddleware",
        PipelineComponentKind.InfoDisplayMiddleware => "Information",
        PipelineComponentKind.BaseMiddleware => "Base", _ => "Unknown"
    };
    public static string Group(string? groupId, IStringLocalizer<DataChannelResource> localizer) =>
        string.IsNullOrEmpty(groupId) ? localizer["Workbench:Ungrouped"].Value : groupId;
}
