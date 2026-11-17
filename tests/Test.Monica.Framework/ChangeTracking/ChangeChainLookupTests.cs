using Monica.Framework.ChangeTracking.Abstractions;
using Monica.Framework.ChangeTracking.Models;
using Xunit;

namespace Test.Monica.Framework.ChangeTracking;

public sealed class ChangeChainLookupTests
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
    [Fact]
    public void ContainsChangingItemId_AfterConstructionAndAppend_ShouldMatchOrderedSnapshot()
    {
        var first = CreateItem("first", new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc));
        var second = CreateItem("second", new DateTime(2026, 7, 29, 10, 0, 2, DateTimeKind.Utc));
        var chain = new TestChangeChain(new ChangeChain<TestEntity, TestAlterItem, TestChangeItemData, TestAlterSource>.ChainJsonParseBridge
        {
            TracingData = "{}",
            ChangingList = [first]
        });

        chain.Add(second);
        var snapshot = chain.GetChangingListSnapshot();

        Assert.True(chain.ContainsChangingItemId(Identity("first")));
        Assert.True(chain.ContainsChangingItemId(Identity("second")));
        Assert.False(chain.ContainsChangingItemId(Identity("missing")));
        Assert.Equal(new[] { "first", "second" }.Select(Identity), snapshot.Select(item => item.Id));
    }

    [Fact]
    public void GetChangingListSnapshot_WhenCallerMutatesSnapshot_ShouldNotMutateChain()
    {
        var chain = new TestChangeChain(new TestEntity());
        chain.Add(CreateItem("first", new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc)));

        var snapshot = chain.GetChangingListSnapshot();
        snapshot.Clear();

        Assert.Single(chain.ChangingList);
        Assert.True(chain.ContainsChangingItemId(Identity("first")));
    }

    private static TestAlterItem CreateItem(string id, DateTime alterTime)
    {
        return new TestAlterItem(id, alterTime, new TestChangeItemData { Value = id });
    }


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

    private sealed class TestChangeChain
        : ChangeChain<TestEntity, TestAlterItem, TestChangeItemData, TestAlterSource>
    {
        public TestChangeChain(TestEntity entity) : base(entity)
        {
        }

        public TestChangeChain(
            ChangeChain<TestEntity, TestAlterItem, TestChangeItemData, TestAlterSource>.ChainJsonParseBridge bridge)
            : base(bridge)
        {
        }
    }

    private sealed class TestAlterItem
        : ChangeEntry<TestEntity, TestChangeItemData, TestAlterSource>
    {
        public TestAlterItem()
        {
        }

        public TestAlterItem(string id, DateTime alterTime, TestChangeItemData data)
            : base(Identity(id), TestAlterSource.System, alterTime, data, null)
        {
        }
    }

    private sealed class TestChangeItemData : IChangeData<TestEntity>
    {
        public string? Value { get; set; }

        public void Apply(TestEntity entity)
        {
            if (Value != null)
            {
                entity.Value = Value;
            }
        }

        public IEnumerable<PropertyChange> GetChanges(TestEntity? entity = null)
        {
            yield break;
        }
    }

    private sealed class TestEntity : IChangeTrackedEntity
    {
        public string Value { get; set; } = string.Empty;
    }

    private enum TestAlterSource
    {
        System
    }
}
