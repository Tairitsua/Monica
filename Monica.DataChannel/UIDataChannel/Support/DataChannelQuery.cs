using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Monica.DataChannel.Pages;

namespace Monica.DataChannel.UIDataChannel.Support;

/// <summary>The shareable DataChannel workbench selection and filters.</summary>
public sealed record DataChannelQuery
{
    public string? Channel { get; init; }
    public string? Setup { get; init; }
    public string? Group { get; init; }
    public string? State { get; init; }
    public string? Evidence { get; init; }
    public string? Search { get; init; }
    public string? Sort { get; init; }
    public bool Flat { get; init; }
    public bool AllLanes { get; init; }
    public string? Inspect { get; init; }
    public string? Tab { get; init; }
    public string? Component { get; init; }
    public long? Sequence { get; init; }
    public bool Debugger { get; init; }
    public bool AllRetained { get; init; }
    public string? RecordSearch { get; init; }
    public bool Stack { get; init; }

    public static DataChannelQuery Read(Uri uri)
    {
        var values = QueryHelpers.ParseQuery(uri.Query);
        string? Get(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value[0])
            ? value[0] : null;
        string? Filter(string key) => Get(key) is "all" or "All" ? null : Get(key);

        return new()
        {
            Channel = Get("channel"),
            Setup = Filter("setup"),
            Group = Filter("group"),
            State = Filter("state"),
            Evidence = Filter("evidence"),
            Search = Get("q"),
            Sort = Get("sort"),
            Flat = Get("view") == "flat",
            AllLanes = Get("horizon") == "all",
            Inspect = Get("inspect"),
            Tab = Get("tab") is "pipeline" or "endpoints" or "middleware" or "exceptions" ? Get("tab") : null,
            Component = Get("component"),
            Sequence = long.TryParse(Get("seq"), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) && sequence > 0 ? sequence : null,
            Debugger = Get("debugger") == "1",
            AllRetained = Get("allRetained") == "1",
            RecordSearch = Get("recordQ"),
            Stack = Get("detail") == "stack"
        };
    }

    public string Url(bool exceptions)
    {
        var values = new Dictionary<string, string?>
        {
            ["channel"] = Channel,
            ["setup"] = exceptions ? null : Setup,
            ["group"] = exceptions ? null : Group,
            ["state"] = exceptions ? null : State,
            ["evidence"] = Evidence,
            ["q"] = Search,
            ["sort"] = Sort,
            ["view"] = !exceptions && Flat ? "flat" : null,
            ["horizon"] = exceptions && AllLanes ? "all" : null,
            ["inspect"] = Inspect
        };
        if (Inspect is not null)
        {
            values["tab"] = Tab;
            values["component"] = Component;
            values["seq"] = Sequence?.ToString(CultureInfo.InvariantCulture);
            values["debugger"] = Debugger ? "1" : null;
            values["allRetained"] = AllRetained ? "1" : null;
            values["recordQ"] = RecordSearch;
            values["detail"] = Stack ? "stack" : null;
        }

        return QueryHelpers.AddQueryString(exceptions ? UIDataChannelExceptionsPage.PAGE_URL : UIDataChannelPage.PAGE_URL, values);
    }

    public DataChannelQuery Open(string id, string? tab = null, string? component = null, bool debugger = false) =>
        Close() with { Inspect = id, Tab = tab, Component = component, Debugger = debugger };

    public DataChannelQuery Close() => this with
    {
        Inspect = null, Tab = null, Component = null, Sequence = null, Debugger = false,
        AllRetained = false, RecordSearch = null, Stack = false
    };
}
