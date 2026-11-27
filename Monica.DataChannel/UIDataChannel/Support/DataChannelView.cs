using System.Globalization;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.DataChannel.Abstractions.Communication;
using Monica.DataChannel.Models;
using Monica.DataChannel.Providers.Kafka;
using Monica.DataChannel.UIDataChannel.Models;
using Monica.UI.Icons;
using Monica.UI.Shared.Components.Visual;

namespace Monica.DataChannel.UIDataChannel.Support;

/// <summary>Derived directory and ledger evidence from one detached management capture.</summary>
public sealed class DataChannelView
{
    public DataChannelView(DataChannelManagementSnapshot capture)
    {
        Setups = capture.Setups;
        var owners = new Dictionary<string, DataChannelSetupSnapshot>(StringComparer.Ordinal);
        foreach (var setup in Setups)
        {
            foreach (var id in setup.ChannelIds)
            {
                owners.TryAdd(id, setup);
            }
        }

        var evidence = capture.ExceptionSummary.ChannelSummaries.ToDictionary(item => item.ChannelId, StringComparer.Ordinal);
        Channels = capture.Channels.Select(channel => new ChannelEvidence(channel, evidence[channel.Id],
            owners.GetValueOrDefault(channel.Id))).ToArray();
        ById = Channels.ToDictionary(channel => channel.Id, StringComparer.Ordinal);
        Retaining = Channels.Where(channel => channel.Retained > 0)
            .OrderByDescending(channel => channel.Retained).ThenByDescending(channel => channel.Lifetime).ToArray();
        Historical = Channels.Where(channel => channel.HistoricalOnly).OrderByDescending(channel => channel.Lifetime).ToArray();
        Unavailable = Channels.Where(channel => channel.Initialization == "Unavailable").ToArray();
        Blocked = Channels.Where(channel => channel.Blocked.Count > 0).ToArray();
        Held = Setups.Where(setup => setup.State == DataChannelSetupState.Held).ToArray();
        Attention = Channels.Count(channel => channel.NeedsAttention);
        TotalRetained = Channels.Sum(channel => (long)channel.Retained);
        TotalLifetime = Channels.Sum(channel => (long)channel.Lifetime);
    }

    public IReadOnlyList<DataChannelSetupSnapshot> Setups { get; }
    public IReadOnlyList<ChannelEvidence> Channels { get; }
    public IReadOnlyDictionary<string, ChannelEvidence> ById { get; }
    public IReadOnlyList<ChannelEvidence> Retaining { get; }
    public IReadOnlyList<ChannelEvidence> Historical { get; }
    public IReadOnlyList<ChannelEvidence> Unavailable { get; }
    public IReadOnlyList<ChannelEvidence> Blocked { get; }
    public IReadOnlyList<DataChannelSetupSnapshot> Held { get; }
    public int Attention { get; }
    public int Total => Channels.Count;
    public long TotalRetained { get; }
    public long TotalLifetime { get; }
    public long Trimmed => TotalLifetime - TotalRetained;
    public bool IsEmpty => Total == 0 && Setups.Count == 0;

    public int Count(string initialization) => Channels.Count(channel => channel.Initialization == initialization);
    public int Count(DataChannelSetupState state) => Setups.Count(setup => setup.State == state);

    public IEnumerable<ChannelEvidence> DirectoryRows(DataChannelQuery query, bool ignoreState = false)
    {
        var rows = Channels.Where(channel =>
            (query.Channel is null || channel.Id == query.Channel) &&
            (query.Setup is null || channel.Setup?.Id == query.Setup) &&
            (query.Group is null || (query.Group == "ungrouped" && channel.Group is null) || channel.Group == query.Group) &&
            (ignoreState || query.State is null || (query.State == "Attention" ? channel.NeedsAttention : channel.Initialization == query.State)) &&
            channel.MatchesEvidence(query.Evidence) && channel.MatchesSearch(query.Search, exceptions: false));
        return query.Sort switch
        {
            "retained" => rows.OrderByDescending(channel => channel.Retained).ThenByDescending(channel => channel.Lifetime).ThenBy(channel => channel.Id),
            "lifetime" => rows.OrderByDescending(channel => channel.Lifetime).ThenByDescending(channel => channel.Retained).ThenBy(channel => channel.Id),
            "name" => rows.OrderBy(channel => channel.Id),
            _ => rows.OrderBy(channel => channel.Rank).ThenByDescending(channel => channel.Retained)
                .ThenByDescending(channel => channel.Lifetime).ThenBy(channel => channel.Id)
        };
    }

    public IEnumerable<ChannelEvidence> ExceptionRows(DataChannelQuery query, bool ignoreEvidence = false)
    {
        var rows = Channels.Where(channel =>
            (query.Channel is null || channel.Id == query.Channel) &&
            (ignoreEvidence || channel.MatchesEvidence(query.Evidence)) &&
            channel.MatchesSearch(query.Search, exceptions: true));
        return query.Sort switch
        {
            "lifetime" => rows.OrderByDescending(channel => channel.Lifetime).ThenByDescending(channel => channel.Retained).ThenBy(channel => channel.Id),
            "latest" => rows.OrderByDescending(channel => channel.Latest).ThenBy(channel => channel.Id),
            "name" => rows.OrderBy(channel => channel.Id),
            _ => rows.OrderByDescending(channel => channel.Retained).ThenByDescending(channel => channel.Lifetime).ThenBy(channel => channel.Id)
        };
    }

    public static bool ShowEmptySetup(DataChannelSetupSnapshot setup, DataChannelQuery query) =>
        setup.ChannelIds.Count == 0 && query.Channel is null && query.Group is null &&
        query.State is null && query.Evidence is null &&
        (query.Setup is null || query.Setup == setup.Id) &&
        (string.IsNullOrWhiteSpace(query.Search) ||
            new[] { setup.Id, setup.Owner, setup.ImplementationType }.Any(value => Contains(value, query.Search)));

    internal static bool Contains(string? value, string? query) =>
        value is not null && query is not null && value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>One channel's local initialization, configured pipeline topology and shared-history evidence.</summary>
public sealed class ChannelEvidence
{
    private readonly IReadOnlyList<(string Type, string Message)> _retainedSearchText;

    public ChannelEvidence(DataChannelStatusInfo status, DataChannelExceptionSummaryItem summary, DataChannelSetupSnapshot? setup)
    {
        Status = status;
        Setup = setup;
        PipelineId = summary.PipelineId;
        Latest = summary.LatestException;
        _retainedSearchText = summary.RetainedSearchText;
        Initialization = DataChannelPresentation.Initialization(status);
        Configuration = status.Middlewares.Where(component => component.Stage == PipelineComponentStage.EndpointConfiguration)
            .OrderBy(component => component.Ordinal).ToArray();
        Transforms = status.Middlewares.Where(component => component.Stage == PipelineComponentStage.Transform)
            .OrderBy(component => component.Ordinal).ToArray();
    }

    public DataChannelStatusInfo Status { get; }
    public DataChannelSetupSnapshot? Setup { get; }
    public string PipelineId { get; }
    public DateTime? Latest { get; }
    public string Initialization { get; }
    public IReadOnlyList<PipelineComponentInfo> Configuration { get; }
    public IReadOnlyList<PipelineComponentInfo> Transforms { get; }
    public IReadOnlyList<KafkaBlockedPartition> Blocked => Status.OuterEndpoint.KafkaInput?.Consumer?.BlockedPartitions ?? [];
    public PipelineComponentInfo? Debugger =>
        Status.Middlewares.FirstOrDefault(component => component.IsDebuggerActive) ??
        Status.Middlewares.FirstOrDefault(component => component.CanDebugMessages);
    public ConnectionDirection? Direction => Status.OuterEndpoint.EndpointDirection;
    public string Id => Status.Id;
    public string? Group => string.IsNullOrEmpty(Status.GroupId) ? null : Status.GroupId;
    public int Retained => Status.ExceptionCount;
    public int Capacity => Status.MaxHistorySize;
    public int Lifetime => Status.TotalExceptionCount;
    public int Trimmed => Math.Max(0, Lifetime - Retained);
    public bool HistoricalOnly => Retained == 0 && Lifetime > 0;
    public bool NeedsAttention => Initialization == "Unavailable" || Blocked.Count > 0 || Retained > 0;
    public bool CanReinitialize => !Status.IsInitializing && (Setup is null ||
        Setup is { Mode: not ActivationMode.Disabled, State: DataChannelSetupState.Running or DataChannelSetupState.Failed });
    public int Rank => Initialization == "Unavailable" ? 0 : Blocked.Count > 0 ? 1 : Retained > 0 ? 2 :
        Initialization == "Initializing" ? 3 : HistoricalOnly ? 4 : Initialization == "NotInitialized" ? 5 : 6;
    public MoVisualRole StandingRole => Rank switch
    {
        <= 2 => MoVisualRole.Error,
        3 => MoVisualRole.Info,
        4 => MoVisualRole.Warning,
        5 => MoVisualRole.Neutral,
        _ => MoVisualRole.Success
    };

    public bool MatchesEvidence(string? evidence) => evidence switch
    {
        "retained" => Retained > 0,
        "historical" => HistoricalOnly,
        "clean" => Lifetime == 0,
        "none" => Retained == 0,
        _ => true
    };

    public bool MatchesSearch(string? query, bool exceptions)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (DataChannelView.Contains(Id, query) || DataChannelView.Contains(Group, query)) return true;
        if (DataChannelView.Contains(PipelineId, query) || DataChannelView.Contains(Status.OuterEndpoint.TypeName, query)) return true;
        if (exceptions) return _retainedSearchText.Any(entry => DataChannelView.Contains(entry.Type, query) ||
            DataChannelView.Contains(entry.Message, query));
        return DataChannelView.Contains(Setup?.Id, query) || DataChannelView.Contains(Setup?.Owner, query) ||
            DataChannelView.Contains(Setup?.ImplementationType, query) ||
            DataChannelView.Contains(Status.InnerEndpoint.TypeName, query) || DataChannelView.Contains(Status.OuterEndpoint.TypeName, query) ||
            Status.Middlewares.Any(component => DataChannelView.Contains(component.TypeName, query));
    }

    public static string BarWidth(long value, long capacity, int minimum) =>
        (value == 0 ? 0 : Math.Clamp(100d * value / Math.Max(1, capacity), minimum, 100))
        .ToString("0.##", CultureInfo.InvariantCulture) + "%";
}

internal static class DataChannelVisual
{
    public static string ShortOwner(string owner) => owner[(owner.LastIndexOf('.') + 1)..];
    public static string BuilderName(string type) => string.Join('.', type.Split('.').TakeLast(2));
    public static string EndpointName(PipelineComponentInfo endpoint) =>
        endpoint.Name.EndsWith("Endpoint", StringComparison.Ordinal) && endpoint.Name.Length > "Endpoint".Length
            ? endpoint.Name[..^"Endpoint".Length] : endpoint.Name;
    public static string Transport(PipelineComponentInfo endpoint) => endpoint.Name switch
    {
        "KafkaEndpoint" => "Kafka",
        "ActiveMqEndpoint" => "ActiveMQ",
        "TcpServerEndpoint" or "TcpClientEndpoint" => "TCP",
        "UdpEndpoint" => "UDP",
        "SerialPortEndpoint" => "Serial",
        _ => EndpointName(endpoint)
    };
    public static string TransportIcon(PipelineComponentInfo endpoint) => Transport(endpoint) switch
    {
        "Kafka" => MoIcons.Layers,
        "ActiveMQ" => MoIcons.Inbox,
        "TCP" or "Serial" => MoIcons.Cable,
        "UDP" => MoIcons.Radio,
        _ => MoIcons.Globe
    };
    public static string DirectionIcon(ConnectionDirection? direction) => direction switch
    {
        ConnectionDirection.Input => MoIcons.ArrowLeft,
        ConnectionDirection.Output => MoIcons.ArrowRight,
        ConnectionDirection.InputAndOutput => MoIcons.ArrowLeftRight,
        _ => MoIcons.Minus
    };
    public static MoVisualRole InitRole(string state) => state switch
    {
        "Initialized" => MoVisualRole.Success, "Initializing" => MoVisualRole.Info,
        "Unavailable" => MoVisualRole.Error, _ => MoVisualRole.Neutral
    };
    public static MoVisualRole SetupRole(DataChannelSetupState state) => state switch
    {
        DataChannelSetupState.Running => MoVisualRole.Success,
        DataChannelSetupState.Failed => MoVisualRole.Error,
        DataChannelSetupState.Held => MoVisualRole.Warning,
        DataChannelSetupState.Starting or DataChannelSetupState.Stopping => MoVisualRole.Info,
        _ => MoVisualRole.Neutral
    };
}
