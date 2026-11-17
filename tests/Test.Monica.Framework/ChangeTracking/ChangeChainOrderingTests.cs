using Monica.Framework.ChangeTracking.Abstractions;
using Monica.Framework.ChangeTracking.Models;
using Xunit;

namespace Test.Monica.Framework.ChangeTracking;

public sealed class ChangeChainOrderingTests
{
    private static readonly Guid _idA = new("019a8c82-1d00-7000-8000-000000000001");
    private static readonly Guid _idB = new("019a8c82-1d00-7000-8000-000000000002");
    private static readonly Guid _idC = new("019a8c82-1d00-7000-8000-000000000003");
    private static readonly Guid _idDirect = new("019a8c82-1d00-7000-8000-000000000004");
    private static readonly Guid _idFirst = new("019a8c82-1d00-7000-8000-000000000005");
    private static readonly Guid _idLater = new("019a8c82-1d00-7000-8000-000000000006");
    private static readonly Guid _idMissing = new("019a8c82-1d00-7000-8000-000000000007");
    private static readonly Guid _idObsolete = new("019a8c82-1d00-7000-8000-000000000008");
    private static readonly Guid _idR = new("019a8c82-1d00-7000-8000-000000000009");
    private static readonly Guid _idR1 = new("019a8c82-1d00-7000-8000-00000000000a");
    private static readonly Guid _idR2 = new("019a8c82-1d00-7000-8000-00000000000b");
    private static readonly Guid _idR3 = new("019a8c82-1d00-7000-8000-00000000000c");
    private static readonly Guid _idR4 = new("019a8c82-1d00-7000-8000-00000000000d");
    private static readonly Guid _idRelated = new("019a8c82-1d00-7000-8000-00000000000e");
    private static readonly Guid _idRollback = new("019a8c82-1d00-7000-8000-00000000000f");
    private static readonly Guid _idSecond = new("019a8c82-1d00-7000-8000-000000000010");
    private static readonly Guid _idZ = new("019a8c82-1d00-7000-8000-000000000011");
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
        Assert.Equal(new[] { "a", "b", "c" }.Select(Identity), chain.ChangingList.Select(entry => entry.Id));
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
            SchemaVersion = 3, TracingData = "{}", ChangingList = [direct, related, later]
        });
        var acceptedTime = _time.AddMinutes(1);
        var rollback = chain.CreateRollbackAlterItemBySourceId(Identity("rollback"), Source.System, "operation", null, "message", acceptedTime);
        Assert.NotNull(rollback);
        Assert.Equal(acceptedTime, rollback.AlterTime);
        Assert.Equal(new[] { "direct", "related" }.Select(Identity), rollback.TargetRollbackIds);
    }

    [Fact]
    public void ReadConvertedHistory_WhenTimesAreEqual_ShouldPreserveRecordedOrder()
    {
        var chain = new Chain(new Chain.ChainJsonParseBridge
        {
            TracingData = "{}", ChangingList = [HistoricalEntry("z", 1), HistoricalEntry("a", 2)]
        });
        Assert.Equal("a", chain.GetFinalStatus().Value);
        Assert.Equal([1L, 2L], chain.ChangingList.Select(entry => entry.HistoricalOrder!.Value));
        chain.Add(Entry("b"));
        Assert.Equal(new[] { "z", "a", "b" }.Select(Identity), chain.ChangingList.Select(entry => entry.Id));
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
            SchemaVersion = 3, TracingData = "{}", ChangingList = [rollback, target]
        });
        Assert.Equal("", chain.GetFinalStatus().Value);
        Assert.Equal(Identity("r"), target.RollbackBy);
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
            SchemaVersion = 3, TracingData = "{}",
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
        entry.RollbackBy = Identity("obsolete");
        var chain = new Chain(new Chain.ChainJsonParseBridge
        {
            SchemaVersion = 3, TracingData = "{}", ChangingList = [entry]
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

    [Fact]
    public void Read_WhenPortableDocumentOmitsVersion_ShouldRejectInsteadOfAssumingUuidOrdering()
    {
        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<Chain.ChainJsonParseBridge>(
                """{"TracingData":"{}","ChangingList":[]}"""));
    }

    private static TestEntry HistoricalEntry(string id, long position)
    {
        var entry = Entry(id);
        entry.HistoricalOrder = position;
        return entry;
    }

    private static TestEntry Entry(string id) => new()
    {
        Id = Identity(id), AlterTime = _time, Data = new ChangeData { Value = id }
    };
    private static TestEntry Rollback(string id, params string[] targets)
        => new() { Id = Identity(id), AlterTime = _time, TargetRollbackIds = targets.Select(Identity).ToList(), Data = new ChangeData() };


    private static Guid Identity(string label) => label switch
    {
        "a" => _idA,
        "b" => _idB,
        "c" => _idC,
        "direct" => _idDirect,
        "first" => _idFirst,
        "later" => _idLater,
        "missing" => _idMissing,
        "obsolete" => _idObsolete,
        "r" => _idR,
        "r1" => _idR1,
        "r2" => _idR2,
        "r3" => _idR3,
        "r4" => _idR4,
        "related" => _idRelated,
        "rollback" => _idRollback,
        "second" => _idSecond,
        "z" => _idZ,
        _ => throw new ArgumentException("Unknown test identity.", nameof(label))
    };

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
