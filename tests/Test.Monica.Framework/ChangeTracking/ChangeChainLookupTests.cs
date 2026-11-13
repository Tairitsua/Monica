using Monica.Framework.ChangeTracking.Abstractions;
using Monica.Framework.ChangeTracking.Models;
using Xunit;

namespace Test.Monica.Framework.ChangeTracking;

public sealed class ChangeChainLookupTests
{
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

        Assert.True(chain.ContainsChangingItemId("first"));
        Assert.True(chain.ContainsChangingItemId("second"));
        Assert.False(chain.ContainsChangingItemId("missing"));
        Assert.Equal(["first", "second"], snapshot.Select(item => item.Id));
    }

    [Fact]
    public void GetChangingListSnapshot_WhenCallerMutatesSnapshot_ShouldNotMutateChain()
    {
        var chain = new TestChangeChain(new TestEntity());
        chain.Add(CreateItem("first", new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc)));

        var snapshot = chain.GetChangingListSnapshot();
        snapshot.Clear();

        Assert.Single(chain.ChangingList);
        Assert.True(chain.ContainsChangingItemId("first"));
    }

    private static TestAlterItem CreateItem(string id, DateTime alterTime)
    {
        return new TestAlterItem(id, alterTime, new TestChangeItemData { Value = id });
    }

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
            : base(id, TestAlterSource.System, alterTime, data, null)
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
