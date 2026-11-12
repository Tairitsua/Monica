using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Results;
using Monica.Framework.UI.UILogging.Models;
using Monica.Framework.UI.UILogging.State;
using Monica.Testing.Results;
using Test.Monica.Framework.Logging.UILogging.Support;
using Xunit;

namespace Test.Monica.Framework.Logging.UILogging.State;

/// <summary>Exercises independent readers against real, scenario-owned files through the production module graph.</summary>
public sealed class LogReadingSessionTests
{
    [Fact]
    public async Task CreateSession_WhenReadersChooseDifferentFiles_ShouldKeepTheirSourceFilterAndLifecycleIndependent()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "a.log", "alpha\nneedle in alpha\n");
        await WriteAsync(factory, "b.log", "beta\nneedle in beta\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var service = application.Services.GetRequiredService<LoggingService>();
        await using var first = service.CreateSession();
        await using var second = service.CreateSession();

        var firstView = Value(await first.SwitchToFileAsync("a.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        var secondView = Value(await second.SwitchToFileAsync("b.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        firstView.Source.Should().NotBeNull();
        secondView.Source.Should().NotBeNull();
        firstView.Source.Should().NotBe(secondView.Source);
        Value(await first.UpdateFilterAsync("needle", true, cancellationToken: TestContext.Current.CancellationToken)).Lines.Select(static line => line.Raw)
            .Should().Equal("needle in alpha");
        second.GetSnapshot().Lines.Select(static line => line.Raw).Should().Equal("beta", "needle in beta");

        (await first.StartAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldSucceed();
        first.GetSnapshot().IsRunning.Should().BeTrue();
        second.GetSnapshot().IsRunning.Should().BeFalse();
        (await first.PauseAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldSucceed();
        await File.AppendAllTextAsync(Path.Combine(factory.LogDirectory, "b.log"), "new beta\n", TestContext.Current.CancellationToken);
        Value(await second.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken)).Lines.Select(static line => line.Raw).Should().Equal("beta", "needle in beta", "new beta");
        first.GetSnapshot().Lines.Select(static line => line.Raw).Should().Equal("needle in alpha");

        // Page-owned readers cannot replace the host buffer served by the legacy export endpoint.
        service.GetSnapshot().TotalStoredLines.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_WhenUtf8CharacterAndLineTerminatorArriveAcrossWrites_ShouldPublishOneExactPhysicalLine()
    {
        using var factory = new LoggingUITestApplicationFactory();
        var raw = "日志 — café 😀";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var split = bytes.Length - 2; // Split the final four-byte scalar instead of only splitting ASCII text.
        var path = Path.Combine(factory.LogDirectory, "utf8.log");
        await File.WriteAllBytesAsync(path, bytes[..split], TestContext.Current.CancellationToken);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var initialized = Value(await session.SwitchToFileAsync("utf8.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        initialized.FileExists.Should().BeTrue();
        initialized.Lines.Should().BeEmpty();
        initialized.TotalFileLineCount.Should().Be(0);

        await AppendBytesAsync(path, bytes[split..]);
        Value(await session.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken)).Lines.Should().BeEmpty();
        await AppendBytesAsync(path, [(byte)'\r']);
        Value(await session.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken)).Lines.Should().BeEmpty();
        await AppendBytesAsync(path, [(byte)'\n']);
        var completed = Value(await session.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken));
        completed.Lines.Should().ContainSingle();
        completed.Lines[0].Raw.Should().Be(raw);
        completed.Lines[0].AbsoluteLineNumber.Should().Be(1);
        completed.TotalFileLineCount.Should().Be(1);
        Value(await session.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken)).Lines.Should().ContainSingle();
    }

    [Fact]
    public async Task RefreshAsync_WhenFileEndsWithIncompleteText_ShouldRetainItUntilANewlineWithoutDuplicatingCompletedLines()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "partial.log", "complete\nunfinished");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        Value(await session.SwitchToFileAsync("partial.log", 100, cancellationToken: TestContext.Current.CancellationToken)).Lines.Select(static line => line.Raw).Should().Equal("complete");
        await File.AppendAllTextAsync(Path.Combine(factory.LogDirectory, "partial.log"), " continued\n\n", TestContext.Current.CancellationToken);

        var snapshot = Value(await session.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken));

        snapshot.Lines.Select(static line => line.Raw).Should().Equal("complete", "unfinished continued", "");
        snapshot.Lines.Select(static line => line.AbsoluteLineNumber).Should().Equal(1, 2, 3);
        snapshot.TotalFileLineCount.Should().Be(3);
    }

    [Fact]
    public async Task RefreshAsync_WhenFileIsTruncated_ShouldStartANewSourceGenerationAndRejectOldLineRequests()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "rotation.log", "long original line one\nlong original line two\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var original = Value(await session.SwitchToFileAsync("rotation.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        var request = session.CreateLineRequest(1);
        request.Should().NotBeNull();
        await WriteAsync(factory, "rotation.log", "new\n");

        var replacement = Value(await session.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken));

        replacement.Source!.FilePath.Should().Be(original.Source!.FilePath);
        replacement.Source.Generation.Should().NotBe(original.Source.Generation);
        replacement.Lines.Select(static line => line.Raw).Should().Equal("new");
        replacement.Lines[0].AbsoluteLineNumber.Should().Be(1);
        replacement.TotalFileLineCount.Should().Be(1);
        original.Lines.Select(static line => line.Raw).Should().Equal("long original line one", "long original line two");
        (await session.ReadContextAsync(request!, cancellationToken: TestContext.Current.CancellationToken)).Status.Should().NotBe(ResultStatus.Ok);
    }

    [Fact]
    public async Task SwitchToFileAsync_WhenFilesShareLineNumbers_ShouldRejectARequestFromThePreviousSource()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "first.log", "first source\n");
        await WriteAsync(factory, "second.log", "second source\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        Value(await session.SwitchToFileAsync("first.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        var request = session.CreateLineRequest(1);
        request.Should().NotBeNull();

        Value(await session.SwitchToFileAsync("second.log", 100, cancellationToken: TestContext.Current.CancellationToken));

        (await session.ReadContextAsync(request!, cancellationToken: TestContext.Current.CancellationToken)).Status.Should().NotBe(ResultStatus.Ok);
        session.GetSnapshot().Lines[0].Raw.Should().Be("second source");
        session.CreateLineRequest(0).Should().BeNull();
        session.CreateLineRequest(2).Should().BeNull();
    }

    [Fact]
    public async Task ReadContextAsync_WhenTheFileGrows_ShouldReturnAFrozenSourceQualifiedRangeWithoutReplacingTheReader()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "context.log", "one\ntwo\nthree\nfour\nfive\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var initial = Value(await session.SwitchToFileAsync("context.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        var request = session.CreateLineRequest(3);
        request.Should().NotBeNull();
        var context = (await session.ReadContextAsync(request!, 1, cancellationToken: TestContext.Current.CancellationToken)).ShouldSucceed();
        context.Lines.Select(static line => line.Raw).Should().Equal("two", "three", "four");
        context.Lines.Select(static line => line.AbsoluteLineNumber).Should().Equal(2, 3, 4);
        context.Lines.Where(static line => line.IsContextTarget).Should().ContainSingle().Which.Raw.Should().Be("three");

        await File.AppendAllTextAsync(Path.Combine(factory.LogDirectory, "context.log"), "six\n", TestContext.Current.CancellationToken);
        Value(await session.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken));
        Value(await session.UpdateFilterAsync("six", true, cancellationToken: TestContext.Current.CancellationToken));

        context.Lines.Select(static line => line.Raw).Should().Equal("two", "three", "four");
        context.Lines.Where(static line => line.IsContextTarget).Should().ContainSingle().Which.Raw.Should().Be("three");
        initial.Lines.Select(static line => line.Raw).Should().Equal("one", "two", "three", "four", "five");
        initial.Lines.Should().OnlyContain(static line => !line.IsMatch && !line.IsContextTarget);
        session.GetSnapshot().Lines.Select(static line => line.Raw).Should().Equal("six");
    }

    [Fact]
    public async Task LoadMoreLinesAsync_WhenTheInitialWindowStartsLate_ShouldPrependExactEarlierLinesAndKeepOldSnapshotsFrozen()
    {
        using var factory = new LoggingUITestApplicationFactory();
        var lines = Enumerable.Range(1, 30).Select(static number => $"line {number}").ToArray();
        await WriteAsync(factory, "history.log", string.Join('\n', lines) + "\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var original = Value(await session.SwitchToFileAsync("history.log", 5, cancellationToken: TestContext.Current.CancellationToken));
        original.Lines.Select(static line => line.AbsoluteLineNumber).Should().Equal(26, 27, 28, 29, 30);

        (await session.LoadMoreLinesAsync(10, cancellationToken: TestContext.Current.CancellationToken)).ShouldSucceed().Should().Be(10);
        var expanded = session.GetSnapshot();

        expanded.Lines.Select(static line => line.Raw).Should().Equal(lines[15..]);
        expanded.FirstRetainedLineNumber.Should().Be(16);
        expanded.LastRetainedLineNumber.Should().Be(30);
        expanded.TotalFileLineCount.Should().Be(30);
        expanded.Source.Should().Be(original.Source);
        original.Lines.Select(static line => line.Raw).Should().Equal(lines[25..]);
        (await session.LoadMoreLinesAsync(100, cancellationToken: TestContext.Current.CancellationToken)).ShouldSucceed().Should().Be(15);
        (await session.LoadMoreLinesAsync(100, cancellationToken: TestContext.Current.CancellationToken)).ShouldSucceed().Should().Be(0);
        session.GetSnapshot().Lines.Select(static line => line.Raw).Should().Equal(lines);
    }

    [Fact]
    public async Task UpdateFilterAsync_WhenSeverityAndKeywordAreCombined_ShouldFilterSeverityIndependentlyOfMessageText()
    {
        using var factory = new LoggingUITestApplicationFactory();
        string[] raw = [
            "[2026-10-09 08:00:00.000 +00:00 INF TID1] needle info",
            "[2026-10-09 08:00:01.000 +00:00 ERR TID2] ordinary failure",
            "[2026-10-09 08:00:02.000 +00:00 ERR TID3] needle failure",
            "   at continuation" ];
        await WriteAsync(factory, "filter.log", string.Join('\n', raw) + "\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var initial = Value(await session.SwitchToFileAsync("filter.log", 100, cancellationToken: TestContext.Current.CancellationToken));

        var filtered = Value(await session.UpdateFilterAsync("needle", true, LogSeverity.Error, cancellationToken: TestContext.Current.CancellationToken));

        filtered.Lines.Select(static line => line.Raw).Should().Equal(raw[2]);
        filtered.Lines[0].IsMatch.Should().BeTrue();
        filtered.Lines[0].Severity.Should().Be(LogSeverity.Error);
        Value(await session.UpdateFilterAsync("needle", false, LogSeverity.Error, cancellationToken: TestContext.Current.CancellationToken)).Lines.Select(static line => line.Raw)
            .Should().Equal(raw[1], raw[2]);
        Value(await session.UpdateFilterAsync(null, false, LogSeverity.Unknown, cancellationToken: TestContext.Current.CancellationToken)).Lines.Select(static line => line.Raw)
            .Should().Equal(raw[3]);
        initial.Lines.Should().OnlyContain(static line => !line.IsMatch);
        initial.Lines.Select(static line => line.Raw).Should().Equal(raw);
    }

    [Fact]
    public async Task SwitchToFileAsync_WhenSourceIsMissingOrEmpty_ShouldDistinguishThoseStatesAndAdoptLaterCreation()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "empty.log", "");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var missing = Value(await session.SwitchToFileAsync("missing.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        missing.FileExists.Should().BeFalse();
        missing.Lines.Should().BeEmpty();
        missing.Source.Should().NotBeNull();
        var empty = Value(await session.SwitchToFileAsync("empty.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        empty.FileExists.Should().BeTrue();
        empty.Lines.Should().BeEmpty();
        empty.TotalFileLineCount.Should().Be(0);

        await WriteAsync(factory, "empty.log", "now present\n");

        var created = Value(await session.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken));
        created.FileExists.Should().BeTrue();
        created.Lines.Select(static line => line.Raw).Should().Equal("now present");
    }

    [Fact]
    public async Task SwitchToFileAsync_WhenCanceledBeforeAdmission_ShouldPreserveTheCurrentSourceAndRetainedLines()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "first.log", "original\n");
        await WriteAsync(factory, "second.log", "replacement\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var original = Value(await session.SwitchToFileAsync("first.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await canceled.CancelAsync();

        var switchFile = () => session.SwitchToFileAsync("second.log", 100, canceled.Token);
        var refresh = () => session.RefreshAsync(canceled.Token);
        await switchFile.Should().ThrowAsync<OperationCanceledException>();
        await refresh.Should().ThrowAsync<OperationCanceledException>();

        var current = session.GetSnapshot();
        current.Source.Should().Be(original.Source);
        current.Version.Should().Be(original.Version);
        current.Lines.Select(static line => line.Raw).Should().Equal("original");
    }

    [Fact]
    public async Task DisposeAsync_WhenFollowingHasSubscribers_ShouldDrainAndCompleteThemWithoutRetiringOtherReaders()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "owned.log", "owned\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var service = application.Services.GetRequiredService<LoggingService>();
        await using var first = service.CreateSession();
        await using var second = service.CreateSession();
        Value(await first.SwitchToFileAsync("owned.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        Value(await second.SwitchToFileAsync("owned.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        await using var subscription = first.Subscribe();
        (await first.StartAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldSucceed();

        await Task.WhenAll(first.DisposeAsync().AsTask(), first.DisposeAsync().AsTask());
        while (subscription.Reader.TryRead(out _)) { }
        await subscription.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var refresh = () => first.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
        await refresh.Should().ThrowAsync<ObjectDisposedException>();
        var subscribe = () => first.Subscribe();
        subscribe.Should().Throw<ObjectDisposedException>();
        Value(await second.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken)).Lines.Select(static line => line.Raw).Should().Equal("owned");
    }

    [Fact]
    public async Task SwitchToFileAsync_WhenTwoChangesAreAdmittedTogether_ShouldSerializeReadsWithoutMixingTheirSnapshots()
    {
        using var factory = new LoggingUITestApplicationFactory();
        var firstLines = Enumerable.Range(1, 20000).Select(static number => $"first {number}").ToArray();
        var secondLines = Enumerable.Range(1, 200).Select(static number => $"second {number}").ToArray();
        await WriteAsync(factory, "first.log", string.Join('\n', firstLines) + "\n");
        await WriteAsync(factory, "second.log", string.Join('\n', secondLines) + "\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();

        var firstSwitch = session.SwitchToFileAsync("first.log", 10, cancellationToken: TestContext.Current.CancellationToken);
        var secondSwitch = session.SwitchToFileAsync("second.log", 10, cancellationToken: TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(firstSwitch, secondSwitch);

        var first = Value(results[0]);
        var second = Value(results[1]);
        first.Source!.RelativePath.Should().Be("first.log");
        second.Source!.RelativePath.Should().Be("second.log");
        first.Lines.Select(static line => line.Raw).Should().Equal(firstLines[^10..]);
        second.Lines.Select(static line => line.Raw).Should().Equal(secondLines[^10..]);
        session.GetSnapshot().Source.Should().Be(second.Source);
        session.GetSnapshot().Lines.Select(static line => line.Raw).Should().Equal(secondLines[^10..]);
    }

    [Fact]
    public async Task PauseAsync_WhenFollowingAndASourceChangeOverlap_ShouldDrainTheOldSourceBeforePublishingTheNewOne()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "first.log", "first\n");
        await WriteAsync(factory, "second.log", "second\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        Value(await session.SwitchToFileAsync("first.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        (await session.StartAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldSucceed();
        await File.AppendAllTextAsync(Path.Combine(factory.LogDirectory, "first.log"), "last first\n", TestContext.Current.CancellationToken);

        var switchFile = session.SwitchToFileAsync("second.log", 100, cancellationToken: TestContext.Current.CancellationToken);
        var pause = session.PauseAsync(cancellationToken: TestContext.Current.CancellationToken);
        Value(await switchFile);
        (await pause).ShouldSucceed();

        var paused = session.GetSnapshot();
        paused.IsRunning.Should().BeFalse();
        paused.Source!.RelativePath.Should().Be("second.log");
        paused.Lines.Select(static line => line.Raw).Should().Equal("second");
        await File.AppendAllTextAsync(Path.Combine(factory.LogDirectory, "second.log"), "later second\n", TestContext.Current.CancellationToken);
        Value(await session.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken)).Lines.Select(static line => line.Raw).Should().Equal("second", "later second");
        paused.Lines.Select(static line => line.Raw).Should().Equal("second");
    }

    [Fact]
    public async Task ExportBufferAsync_WhenTheViewIsFiltered_ShouldFreezeAllRetainedRawLinesFromThatReaderOnly()
    {
        using var factory = new LoggingUITestApplicationFactory();
        string[] raw = ["raw 日志 %20 #", "   ", "needle 😀", ""];
        await WriteAsync(factory, "export.log", string.Join('\n', raw) + "\n");
        await WriteAsync(factory, "other.log", "other reader\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var service = application.Services.GetRequiredService<LoggingService>();
        await using var first = service.CreateSession();
        await using var second = service.CreateSession();
        Value(await first.SwitchToFileAsync("export.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        Value(await second.SwitchToFileAsync("other.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        Value(await first.UpdateFilterAsync("needle", true, cancellationToken: TestContext.Current.CancellationToken));

        var export = (await first.ExportBufferAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldSucceed();
        var originalBytes = export.Content.ToArray();
        var text = Encoding.UTF8.GetString(export.Content);
        text.Should().Be(string.Join('\n', raw) + "\n");
        export.Content.Should().Equal(Encoding.UTF8.GetBytes(string.Join('\n', raw) + "\n"));
        text.Should().NotContain("other reader");
        export.ContentType.Should().Be("text/plain");
        export.FileName.Should().EndWith(".log");

        await File.AppendAllTextAsync(Path.Combine(factory.LogDirectory, "export.log"), "after export\n", TestContext.Current.CancellationToken);
        Value(await first.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken));

        export.Content.Should().Equal(originalBytes);
        service.GetSnapshot().TotalStoredLines.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_WhenTheFileCannotBeOpened_ShouldRetainSuccessfulEvidenceAndRecoverAfterTheHandleIsReleased()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "held.log", "before failure\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var original = Value(await session.SwitchToFileAsync("held.log", 100, cancellationToken: TestContext.Current.CancellationToken));

        using (var exclusive = new FileStream(Path.Combine(factory.LogDirectory, "held.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failed = await session.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
            failed.Status.Should().NotBe(ResultStatus.Ok);
            failed.Message.Should().NotBeNullOrEmpty();
            session.GetSnapshot().Error.Should().NotBeNullOrEmpty();
            session.GetSnapshot().Source.Should().Be(original.Source);
            session.GetSnapshot().Lines.Select(static line => line.Raw).Should().Equal("before failure");
        }

        await File.AppendAllTextAsync(Path.Combine(factory.LogDirectory, "held.log"), "after recovery\n", TestContext.Current.CancellationToken);
        var recovered = Value(await session.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken));
        recovered.Error.Should().BeNullOrEmpty();
        recovered.Lines.Select(static line => line.Raw).Should().Equal("before failure", "after recovery");
        original.Error.Should().BeNullOrEmpty();
        original.Lines.Select(static line => line.Raw).Should().Equal("before failure");
    }

    [Fact]
    public async Task ReadContextAsync_WhenTheTargetTextChangedBeforeARefresh_ShouldRejectItInsteadOfReassigningTheSameLineNumber()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "context.log", "one\ntwo\noriginal target\nfour\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var before = Value(await session.SwitchToFileAsync("context.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        var request = session.CreateLineRequest(3);
        request.Should().NotBeNull();
        await WriteAsync(factory, "context.log", "one\ntwo\nmodified target\nfour\n");

        var context = await session.ReadContextAsync(request!, 1, cancellationToken: TestContext.Current.CancellationToken);

        context.Status.Should().NotBe(ResultStatus.Ok);
        context.Message.Should().NotBeNullOrEmpty();
        before.Lines[2].Raw.Should().Be("original target");
        request!.Raw.Should().Be("original target");
    }

    [Fact]
    public async Task ExportSnapshot_WhenTheReaderLaterChanges_ShouldExportTheFrozenVisibleScopeAndSource()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "original.log", "ordinary\nneedle one\nneedle 日志 😀\n");
        await WriteAsync(factory, "replacement.log", "other source\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        Value(await session.SwitchToFileAsync("original.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        var frozen = Value(await session.UpdateFilterAsync("needle", true, cancellationToken: TestContext.Current.CancellationToken));

        Value(await session.SwitchToFileAsync("replacement.log", 100, cancellationToken: TestContext.Current.CancellationToken));
        var export = LogReadingSession.ExportSnapshot(frozen);

        export.Content.Should().Equal(Encoding.UTF8.GetBytes("needle one\nneedle 日志 😀\n"));
        export.ContentType.Should().Be("text/plain");
        export.FileName.Should().StartWith("original-").And.EndWith(".log");
        frozen.Source!.RelativePath.Should().Be("original.log");
        session.GetSnapshot().Source!.RelativePath.Should().Be("replacement.log");
    }

    [Fact]
    public async Task RefreshAsync_WhenABurstExceedsRawRetention_ShouldKeepItsFinalExactPhysicalLinesAndTotalObservedCount()
    {
        using var factory = new LoggingUITestApplicationFactory(
            configureLoggingUi: static options => options.MaxDisplayLines = 3);
        await WriteAsync(factory, "burst.log", "line 1\nline 2\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var before = Value(await session.SwitchToFileAsync("burst.log", 3, TestContext.Current.CancellationToken));
        var burst = Enumerable.Range(3, 1000).Select(static number => $"line {number}").ToArray();
        await File.AppendAllTextAsync(Path.Combine(factory.LogDirectory, "burst.log"), string.Join('\n', burst) + "\n", TestContext.Current.CancellationToken);

        var after = Value(await session.RefreshAsync(TestContext.Current.CancellationToken));

        after.Source.Should().Be(before.Source);
        after.TotalFileLineCount.Should().Be(1002);
        after.TotalStoredLines.Should().Be(6);
        after.FirstRetainedLineNumber.Should().Be(997);
        after.LastRetainedLineNumber.Should().Be(1002);
        after.RetainedLines.Select(static line => line.Raw).Should().Equal(burst[^6..]);
        after.RetainedLines.Select(static line => line.AbsoluteLineNumber).Should().Equal(997, 998, 999, 1000, 1001, 1002);
        after.Lines.Select(static line => line.Raw).Should().Equal(burst[^3..]);
        before.Lines.Select(static line => line.Raw).Should().Equal("line 1", "line 2");
        Value(await session.RefreshAsync(TestContext.Current.CancellationToken)).RetainedLines.Select(static line => line.Raw)
            .Should().Equal(burst[^6..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronizeAsync_WhenTheFileWasAppendedOrDeleted_ShouldReturnTheFrozenSnapshotWithoutReadingOrChangingMetadata(bool deleteFile)
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "frozen.log", "adopted evidence\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var adopted = Value(await session.SwitchToFileAsync("frozen.log", 100, TestContext.Current.CancellationToken));
        var path = Path.Combine(factory.LogDirectory, "frozen.log");
        if (deleteFile)
        {
            File.Delete(path);
            File.Exists(path).Should().BeFalse();
        }
        else
        {
            await File.AppendAllTextAsync(path, "unadopted evidence\n", TestContext.Current.CancellationToken);
        }

        var synchronized = Value(await session.SynchronizeAsync(TestContext.Current.CancellationToken));

        synchronized.Should().BeSameAs(adopted);
        synchronized.GeneratedAt.Should().Be(adopted.GeneratedAt);
        synchronized.Version.Should().Be(adopted.Version);
        synchronized.Source.Should().Be(adopted.Source);
        synchronized.FileExists.Should().BeTrue();
        synchronized.TotalFileLineCount.Should().Be(1);
        synchronized.Lines.Select(static line => line.Raw).Should().Equal("adopted evidence");
        synchronized.Error.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task SynchronizeAsync_WhenASourceChangeWasAdmittedFirst_ShouldReturnItsAdoptionBeforeTheNextSourceIntent()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "a.log", "requested A\n");
        var otherLines = Enumerable.Range(1, 20000).Select(static number => $"previous intent B line {number}").ToArray();
        await WriteAsync(factory, "b.log", string.Join('\n', otherLines) + "\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        Value(await session.SwitchToFileAsync("a.log", 100, TestContext.Current.CancellationToken));

        var previousIntent = session.SwitchToFileAsync("b.log", 3, TestContext.Current.CancellationToken);
        var barrier = session.SynchronizeAsync(TestContext.Current.CancellationToken);
        var adoptedB = Value(await previousIntent);
        var current = Value(await barrier);

        current.Should().BeSameAs(adoptedB);
        current.Source!.RelativePath.Should().Be("b.log");
        current.Lines.Select(static line => line.Raw).Should().Equal(otherLines[^3..]);
        current.TotalFileLineCount.Should().Be(20000);

        // The next source decision uses the serialized current evidence, even if an earlier component selected B.
        var requestedA = Value(await session.SwitchToFileAsync("a.log", 100, TestContext.Current.CancellationToken));
        requestedA.Source!.RelativePath.Should().Be("a.log");
        requestedA.Lines.Select(static line => line.Raw).Should().Equal("requested A");
        session.GetSnapshot().Should().BeSameAs(requestedA);
        current.Source.RelativePath.Should().Be("b.log");
    }

    [Fact]
    public async Task SynchronizeAsync_WhenCanceledBeforeAdmission_ShouldPreserveTheAdoptedEvidenceAndCursor()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "canceled.log", "before\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        var adopted = Value(await session.SwitchToFileAsync("canceled.log", 100, TestContext.Current.CancellationToken));
        await File.AppendAllTextAsync(Path.Combine(factory.LogDirectory, "canceled.log"), "after\n", TestContext.Current.CancellationToken);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await canceled.CancelAsync();

        var synchronize = () => session.SynchronizeAsync(canceled.Token);
        await synchronize.Should().ThrowAsync<OperationCanceledException>();

        session.GetSnapshot().Should().BeSameAs(adopted);
        var refreshed = Value(await session.RefreshAsync(TestContext.Current.CancellationToken));
        refreshed.Lines.Select(static line => line.Raw).Should().Equal("before", "after");
        refreshed.TotalFileLineCount.Should().Be(2);
    }

    [Fact]
    public async Task SynchronizeAsync_WhenTheReaderRetired_ShouldRejectNewBarrierAdmission()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await WriteAsync(factory, "retired.log", "owned\n");
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var session = application.Services.GetRequiredService<LoggingService>().CreateSession();
        Value(await session.SwitchToFileAsync("retired.log", 100, TestContext.Current.CancellationToken));
        await session.DisposeAsync();

        var synchronize = () => session.SynchronizeAsync(TestContext.Current.CancellationToken);

        await synchronize.Should().ThrowAsync<ObjectDisposedException>();
    }

    private static ScreenLogSnapshot Value(Result<ScreenLogSnapshot> result) => result.ShouldSucceed()!;

    private static Task WriteAsync(LoggingUITestApplicationFactory factory, string relativePath, string content) =>
        File.WriteAllTextAsync(Path.Combine(factory.LogDirectory, relativePath), content, new UTF8Encoding(false), TestContext.Current.CancellationToken);

    private static async Task AppendBytesAsync(string path, byte[] bytes)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await stream.WriteAsync(bytes, TestContext.Current.CancellationToken);
    }
}
