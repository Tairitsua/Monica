using System.Collections;
using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.JsonSerialization.Services;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Framework.ChainTracing.Extensions;
using Monica.Framework.ChainTracing.Models;
using Monica.Framework.ChainTracing.Providers.EntityFrameworkCore;
using Monica.Framework.ChainTracing.Services;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

/// <summary>
/// Verifies command-shape aggregation: batch writers repeating the same command inside one scope
/// collapse onto a single node with a repeat count, while different shapes keep separate nodes.
/// </summary>
public sealed class ChainTracingDbCommandInterceptorTests
{
    [Fact]
    public void RepeatedCommands_WithSameShapeInSameScope_ShouldAggregateOntoOneNode()
    {
        var tracing = CreateTracing();
        var interceptor = CreateInterceptor(tracing);

        using var parent = tracing.BeginScope("Save batch", "Handler");
        for (var i = 0; i < 50; i++)
        {
            using var command = CreateCommand("INSERT INTO Flight (TailNo, Dep, Arr) VALUES (@p0, @p1, @p2)");
            Executing(interceptor, command);
            Executed(interceptor, command);
        }

        DatabaseChildrenOf(tracing).Should().ContainSingle()
            .Which.RepeatCount.Should().Be(49, "49 later batches aggregate onto the first node");
    }

    [Fact]
    public void RepeatedCommands_InDifferentScopes_ShouldKeepSeparateNodes()
    {
        var tracing = CreateTracing();
        var interceptor = CreateInterceptor(tracing);
        using var parent = tracing.BeginScope("Parent", "Handler");

        using (var firstScope = tracing.BeginScope("First handler", "A"))
        {
            using var command = CreateCommand("SELECT * FROM Flight WHERE Id = @p0");
            Executing(interceptor, command);
            Executed(interceptor, command);
            firstScope.EndWithSuccess();
        }

        using (var secondScope = tracing.BeginScope("Second handler", "B"))
        {
            using var command = CreateCommand("SELECT * FROM Flight WHERE Id = @p0");
            Executing(interceptor, command);
            Executed(interceptor, command);
            secondScope.EndWithSuccess();
        }

        var handlers = tracing.GetCurrentChain()!.Root!.Children!;
        handlers.Should().HaveCount(2);
        handlers[0].Children!.Should().ContainSingle(n => n.RepeatCount == 0);
        handlers[1].Children!.Should().ContainSingle(n => n.RepeatCount == 0);
    }

    [Fact]
    public void DifferentCommandShapes_InSameScope_ShouldKeepSeparateNodes()
    {
        var tracing = CreateTracing();
        var interceptor = CreateInterceptor(tracing);
        using var parent = tracing.BeginScope("Mixed", "Handler");

        using (var first = CreateCommand("INSERT INTO Flight (A) VALUES (@p0)"))
        {
            Executing(interceptor, first);
            Executed(interceptor, first);
        }
        using (var second = CreateCommand("INSERT INTO Route (B) VALUES (@p0)"))
        {
            Executing(interceptor, second);
            Executed(interceptor, second);
        }

        DatabaseChildrenOf(tracing).Should().HaveCount(2);
    }

    [Fact]
    public void AggregatedNode_ShouldCarryRepeatCountInResultAndFailureFlag()
    {
        var tracing = CreateTracing();
        var interceptor = CreateInterceptor(tracing);
        using var parent = tracing.BeginScope("Save batch", "Handler");

        using (var first = CreateCommand("INSERT INTO Flight (A) VALUES (@p0)"))
        {
            Executing(interceptor, first);
            Executed(interceptor, first);
        }
        using (var repeat = CreateCommand("INSERT INTO Flight (A) VALUES (@p0)"))
        {
            Executing(interceptor, repeat);
            interceptor.CommandFailed(repeat, new CommandErrorEventData(
                null!, null!, null!, repeat, "test", null, DbCommandMethod.ExecuteNonQuery,
                Guid.NewGuid(), Guid.NewGuid(), new InvalidOperationException("batch 2 failed"),
                false, true, DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(5), CommandSource.LinqQuery));
        }

        var node = DatabaseChildrenOf(tracing).Single();
        node.RepeatCount.Should().Be(1);
        node.Result.Should().Contain("×2");
        node.IsFailed.Should().BeTrue("a failed aggregated execution marks the node failed");
        node.Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("batch 2 failed", "aggregating a later failure must retain its diagnostic cause");
    }

    private static List<ChainTraceNode> DatabaseChildrenOf(IChainTracing tracing)
    {
        var parent = tracing.GetCurrentNode()!;
        return parent.Children!;
    }

    private static StubDbCommand CreateCommand(string commandText) => new(commandText);

    private static ChainTracingDbCommandInterceptor CreateInterceptor(AsyncLocalChainTracingService tracing)
    {
        return new ChainTracingDbCommandInterceptor(tracing,
            NullLogger<ChainTracingDbCommandInterceptor>.Instance);
    }

    private static void Executing(ChainTracingDbCommandInterceptor interceptor, StubDbCommand command)
    {
        interceptor.NonQueryExecuting(command, Data(command), new InterceptionResult<int>());
    }

    private static void Executed(ChainTracingDbCommandInterceptor interceptor, StubDbCommand command)
    {
        interceptor.NonQueryExecuted(command, ExecutedData(command), 42);
    }

    private static CommandEventData Data(StubDbCommand command)
    {
        return new CommandEventData(
            null!, null!, null!, command, "test", null, DbCommandMethod.ExecuteNonQuery,
            Guid.NewGuid(), Guid.NewGuid(), false, true,
            DateTimeOffset.UtcNow, CommandSource.LinqQuery);
    }

    private static CommandExecutedEventData ExecutedData(StubDbCommand command)
    {
        return new CommandExecutedEventData(
            null!, null!, null!, command, "test", null, DbCommandMethod.ExecuteNonQuery,
            Guid.NewGuid(), Guid.NewGuid(), 42, false, true,
            DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(5), CommandSource.LinqQuery);
    }

    private static AsyncLocalChainTracingService CreateTracing()
    {
        return new AsyncLocalChainTracingService(
            Options.Create(new ModuleChainTracingOption()),
            NullLogger<AsyncLocalChainTracingService>.Instance,
            new JsonSerializerOptionsProvider(new System.Text.Json.JsonSerializerOptions(), DateTimeWireFormat.Iso8601WallClock));
    }

    private sealed class StubDbCommand(string commandText) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = commandText;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection { get; } = new StubParameterCollection();
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new StubDbParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        public override int ExecuteNonQuery() => 0;
        public override object? ExecuteScalar() => null;
    }

    private sealed class StubParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _parameters = [];
        public override int Count => _parameters.Count;
        public override object SyncRoot => ((ICollection)_parameters).SyncRoot;
        public override int Add(object? value) { _parameters.Add((DbParameter)value!); return _parameters.Count - 1; }
        public override void AddRange(Array values) { }
        public override void Clear() => _parameters.Clear();
        public override bool Contains(object? value) => _parameters.Contains((DbParameter)value!);
        public override bool Contains(string value) => false;
        public override void CopyTo(Array array, int index) { }
        public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();
        public override int IndexOf(object? value) => _parameters.IndexOf((DbParameter)value!);
        public override int IndexOf(string parameterName) => -1;
        public override void Insert(int index, object? value) => _parameters.Insert(index, (DbParameter)value!);
        public override void Remove(object? value) => _parameters.Remove((DbParameter)value!);
        public override void RemoveAt(int index) => _parameters.RemoveAt(index);
        public override void RemoveAt(string parameterName) { }
        protected override DbParameter GetParameter(int index) => _parameters[index];
        protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();
        protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => throw new NotSupportedException();
    }

    private sealed class StubDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ParameterName { get; set; } = string.Empty;
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }
}
