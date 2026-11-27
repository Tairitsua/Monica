using Monica.SignalR.Models;
using Monica.SignalR.UISignalR.Support;
using Monica.UI.Shared.Components.Visual;
using Xunit;

namespace Test.Monica.SignalR.UISignalR.Support;

public sealed class SignalRDiagnosticRulesTests
{
    [Theory]
    [InlineData(0, 0, 0, 1200, false, false)]
    [InlineData(1, 0, 0, 1200, true, false)]
    [InlineData(0, 1, 0, 999.999, false, false)]
    [InlineData(0, 1, 0, 1000, true, true)]
    [InlineData(0, 0, 1, 0, true, false)]
    public void NeedsAttention_WhenEvidenceVaries_ShouldKeepUnobservedDurationsSeparateFromSpikes(
        long pending, long completed, long failed, double maximum, bool attention, bool spike)
    {
        var method = new SignalRSendMethodMetricInfo { HubName = "Hub", MethodName = "Update",
            PendingSendCount = pending, CompletedCount = completed, FailedCount = failed, MaxDurationMilliseconds = maximum };
        Assert.Equal(attention, SignalRDiagnosticRules.NeedsAttention(method));
        Assert.Equal(spike, SignalRDiagnosticRules.HasSpike(method));
        Assert.Equal(completed + failed > 0, SignalRDiagnosticRules.HasObservation(method));
    }

    [Fact]
    public void SendRole_WhenFailedAndInflightAndSlow_ShouldPrioritizeFailedStanding()
    {
        var method = new SignalRSendMethodMetricInfo { HubName = "Hub", MethodName = "Update",
            PendingSendCount = 2, FailedCount = 1, MaxDurationMilliseconds = 1200 };
        Assert.Equal("Failed", SignalRDiagnosticRules.Standing(method));
        Assert.Equal(MoVisualRole.Error, SignalRDiagnosticRules.SendRole(method));
    }

    [Fact]
    public void GroupConnections_WhenBrowserAndMultipleSessionsExist_ShouldKeepUserIdentityAndPriority()
    {
        var connections = new[] { Connection("older", "u1", -3), Connection("browser", "u2", -1),
            Connection("newer", "u1", -2), Connection("anonymous-1", null, -4), Connection("anonymous-2", null, -5) };
        var groups = SignalRDiagnosticRules.GroupConnections(connections, "browser");
        Assert.Equal("browser", groups[0][0].ConnectionId);
        Assert.Equal(["older", "newer"], groups[1].Select(connection => connection.ConnectionId).ToArray());
        Assert.Equal(4, groups.Count);
        Assert.Single(groups[2]);
        Assert.Single(groups[3]);
    }

    [Fact]
    public void Freeze_WhenSourceClaimsAndRegistryChange_ShouldRetainDetachedEvidence()
    {
        var source = Connection("connection", "user", -1);
        source.Claims["station"] = "ZGGG-TWR";
        var frozen = SignalRDiagnosticRules.Freeze(source);
        source.Claims["station"] = "changed";
        source.Claims.Clear();
        Assert.Equal("ZGGG-TWR", frozen.Claims["station"]);
        Assert.True(SignalRDiagnosticRules.Matches(frozen, "zggg-twr"));
        Assert.True(SignalRDiagnosticRules.Matches(frozen, "STATION"));
        Assert.False(SignalRDiagnosticRules.Matches(frozen, "missing"));
    }

    private static SignalRConnectedUserInfo Connection(string id, string? user, int hours) => new()
    {
        ConnectionId = id, UserId = user, IsAuthenticated = user is not null,
        ConnectionTime = new DateTime(2026, 11, 27, 3, 0, 0, DateTimeKind.Utc).AddHours(hours)
    };
}
