using Monica.Framework.ChangeTracking.Abstractions;
using Monica.Framework.ChangeTracking.Models;
using Xunit;

namespace Test.Monica.Framework.ChangeTracking;

public sealed class ChangeChainOrderingTests
{
    private static readonly DateTime _time = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("abc")]
    [InlineData("acb")]
    [InlineData("bac")]
    [InlineData("bca")]
    [InlineData("cab")]
    [InlineData("cba")]
    public void Replay_WhenTimesAreEqual_ShouldUseStableIdentity(string arrival)
    {
        var chain = new Chain(new State());
        foreach (var id in arrival) chain.Add(Entry(id.ToString()));
        Assert.Equal(["a", "b", "c"], chain.ChangingList.Select(entry => entry.Id));
        Assert.Equal("c", chain.GetFinalStatus().Value);
        Assert.All(chain.ChangingList, entry => Assert.Equal(0, entry.OffsetAdjust));
    }

    [Fact]
    public void ShouldReconstruct_WhenEqualTimeIdentityPrecedesTail_ShouldReturnTrue()
    {
        var chain = new Chain(new State());
        chain.Add(Entry("b"));
        Assert.True(chain.ShouldReconstruct(Entry("a")));
        Assert.False(chain.ShouldReconstruct(Entry("c")));
    }

    [Fact]
    public void SourceRollback_ShouldUseOriginalOperationTimeAndIncludeRelatedSources()
    {
        var direct = Entry("direct");
        direct.SourceId = "message";
        var related = Entry("related");
        related.RelatedSourceIds = ["message"];
        var later = Entry("later");
        later.SourceId = "message";
        later.AlterTime = _time.AddMinutes(2);
        var chain = new Chain(new Chain.ChainJsonParseBridge
        {
            SchemaVersion = 2, TracingData = "{}", ChangingList = [direct, related, later]
        });
        var acceptedTime = _time.AddMinutes(1);
        var rollback = chain.CreateRollbackAlterItemBySourceId("rollback", Source.System, "operation", null, "message", acceptedTime);
        Assert.NotNull(rollback);
        Assert.Equal(acceptedTime, rollback.AlterTime);
        Assert.Equal(["direct", "related"], rollback.TargetRollbackIds);
    }

    [Fact]
    public void ReadLegacy_WhenTimesAreEqual_ShouldPreserveRecordedOrder()
    {
        var chain = new Chain(new Chain.ChainJsonParseBridge
        {
            TracingData = "{}", ChangingList = [Entry("z"), Entry("a")]
        });
        Assert.Equal("a", chain.GetFinalStatus().Value);
        Assert.Equal([1L, 2L], chain.ChangingList.Select(entry => entry.HistoricalOrder!.Value));
        chain.Add(Entry("b"));
        Assert.Equal(["z", "a", "b"], chain.ChangingList.Select(entry => entry.Id));
        Assert.Equal("b", chain.GetFinalStatus().Value);
    }

    [Fact]
    public void Replay_WhenRollbacksOverlapAndAreUndone_ShouldKeepAllActiveTargets()
    {
        var chain = new Chain(new State { Value = "baseline" });
        chain.Add(Entry("a"));
        chain.Add(Rollback("r1", "a"));
        chain.Add(Rollback("r2", "a"));
        chain.Add(Rollback("r3", "r2"));
        Assert.Equal("baseline", chain.GetFinalStatus().Value);
        chain.Add(Rollback("r4", "r1"));
        Assert.Equal("a", chain.GetFinalStatus().Value);
    }

    [Fact]
    public void Replay_WhenRollbackHasEarlierBusinessTime_ShouldEvaluateDependencies()
    {
        var target = Entry("a");
        var rollback = Rollback("r", "a");
        rollback.ActualEffectTime = _time.AddHours(-1);
        var chain = new Chain(new Chain.ChainJsonParseBridge
        {
            SchemaVersion = 2, TracingData = "{}", ChangingList = [rollback, target]
        });
        Assert.Equal("", chain.GetFinalStatus().Value);
        Assert.Equal("r", target.RollbackBy);
    }

    [Fact]
    public void Add_WhenTargetIsMissing_ShouldRejectWithoutChangingTheChain()
    {
        var chain = new Chain(new State());
        chain.Add(Entry("a"));
        Assert.Throws<InvalidOperationException>(() => chain.Add(Rollback("r", "missing")));
        Assert.Single(chain.ChangingList);
        Assert.Equal("a", chain.GetFinalStatus().Value);
    }

    [Fact]
    public void Read_WhenRollbackGraphHasCycle_ShouldReject()
    {
        Assert.Throws<InvalidOperationException>(() => new Chain(new Chain.ChainJsonParseBridge
        {
            SchemaVersion = 2, TracingData = "{}",
            ChangingList = [Rollback("r1", "r2"), Rollback("r2", "r1")]
        }));
    }

    [Fact]
    public void Add_WhenIdentityAlreadyExists_ShouldReject()
    {
        var chain = new Chain(new State());
        chain.Add(Entry("a"));
        Assert.Throws<InvalidOperationException>(() => chain.Add(Entry("a")));
        Assert.Single(chain.ChangingList);
    }

    [Fact]
    public void Read_WhenStoredRollbackFlagIsStale_ShouldDeriveItFromFacts()
    {
        var entry = Entry("a");
        entry.RollbackBy = "obsolete";
        var chain = new Chain(new Chain.ChainJsonParseBridge
        {
            SchemaVersion = 2, TracingData = "{}", ChangingList = [entry]
        });
        Assert.Equal("a", chain.GetFinalStatus().Value);
        Assert.Null(entry.RollbackBy);
    }

    [Fact]
    public void Apply_WhenRollbackCollectionIsEmpty_ShouldApplyNormalData()
    {
        var entry = Entry("a");
        entry.TargetRollbackIds = [];
        Assert.False(entry.IsRollback);
        Assert.Equal("a", entry.ApplyAlter(new State()).Value);
    }

    [Fact]
    public void Data_WhenJsonIsReplaced_ShouldInvalidateTheParsedValue()
    {
        var entry = Entry("a");
        Assert.Equal("a", entry.Data.Value);
        entry.DataJson = """{"Value":"b"}""";
        Assert.Equal("b", entry.Data.Value);
    }

    private static TestEntry Entry(string id) => new()
    {
        Id = id, AlterTime = _time, Data = new ChangeData { Value = id }
    };
    private static TestEntry Rollback(string id, params string[] targets)
        => new() { Id = id, AlterTime = _time, TargetRollbackIds = [.. targets], Data = new ChangeData() };

    private sealed class Chain : ChangeChain<State, TestEntry, ChangeData, Source>
    {
        public Chain(State state) : base(state) { }
        public Chain(ChainJsonParseBridge bridge) : base(bridge) { }
    }
    private sealed class TestEntry : ChangeEntry<State, ChangeData, Source>
    {
        public TestEntry() { }
    }
    private sealed class State : IChangeTrackedEntity
    {
        public string Value { get; set; } = "";
    }
    private sealed class ChangeData : IChangeData<State>
    {
        public string? Value { get; set; }
        public void Apply(State state) { if (Value is not null) state.Value = Value; }
        public IEnumerable<PropertyChange> GetChanges(State? state = null) => [];
    }
    private enum Source { System }
}
