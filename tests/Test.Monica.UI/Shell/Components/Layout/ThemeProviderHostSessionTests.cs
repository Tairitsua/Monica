using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Monica.Modules;
using Monica.UI.Shell.Components.Layout;
using Monica.UI.Shell.State;
using Monica.UI.Shell.Support;
using Monica.UI.Theming;
using Xunit;

namespace Test.Monica.UI.Shell.Components.Layout;

public sealed class ThemeProviderHostSessionTests
{
    [Fact]
    public async Task InitializeAsync_WhenSavedPreferenceExists_ShouldRestoreOnceAfterInteractiveAdmission()
    {
        var saved = new ThemePreference(MonicaThemeKind.Grove, true);
        var state = CreateState();
        var storage = new ControlledStorage(saved);
        var module = new ControlledModule();
        var runtime = new ControlledRuntime(module);
        var notifications = new NotificationRecorder();
        await using var session = new ThemeProviderHostSession(state, storage, runtime, notifications.NotifyAsync);

        state.CurrentThemeKind.Should().Be(MonicaThemeKind.Default);
        state.IsDarkMode.Should().BeFalse();
        storage.ReadCount.Should().Be(0);
        runtime.ImportCount.Should().Be(0);

        var first = session.InitializeAsync();
        var second = session.InitializeAsync();
        await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);

        first.Should().BeSameAs(second);
        CurrentPreference(state).Should().Be(saved);
        storage.ReadCount.Should().Be(1);
        storage.Writes.Should().BeEmpty();
        module.DocumentThemes.Should().Equal("grove-dark");
        notifications.Count.Should().Be(1);
        runtime.ImportCount.Should().Be(1);
    }

    [Fact]
    public async Task InitializeAsync_WhenThemeChangesBeforeInteractiveAdmission_ShouldKeepChoiceWithoutEarlyInterop()
    {
        var state = CreateState();
        var storage = new ControlledStorage(new ThemePreference(MonicaThemeKind.Grove, false));
        var module = new ControlledModule();
        var runtime = new ControlledRuntime(module);
        var notifications = new NotificationRecorder();
        await using var session = new ThemeProviderHostSession(state, storage, runtime, notifications.NotifyAsync);
        var selected = new ThemePreference(MonicaThemeKind.Fresh, true);

        state.SetTheme(selected.ThemeKind, selected.IsDarkMode);

        storage.ReadCount.Should().Be(0);
        storage.Writes.Should().BeEmpty();
        runtime.ImportCount.Should().Be(0);
        notifications.Count.Should().Be(0);

        await session.InitializeAsync().WaitAsync(TestContext.Current.CancellationToken);

        CurrentPreference(state).Should().Be(selected);
        storage.Saved.Should().Be(selected);
        module.DocumentThemes.Should().Equal("fresh-dark");
        notifications.Count.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_WhenThemeChangesDuringRead_ShouldKeepLatestChoiceIncludingReturnToDefault(
        bool returnToDefault)
    {
        var read = NewCompletion<ThemePreference?>();
        var state = CreateState();
        var storage = new ControlledStorage { ReadAsync = () => read.Task };
        var module = new ControlledModule();
        var notifications = new NotificationRecorder(returnToDefault ? 3 : 2);
        await using var session = new ThemeProviderHostSession(
            state, storage, new ControlledRuntime(module), notifications.NotifyAsync);

        var initialization = session.InitializeAsync();
        await storage.ReadStarted.WaitAsync(TestContext.Current.CancellationToken);
        state.SetTheme(MonicaThemeKind.Fresh, true);
        if (returnToDefault) state.SetTheme(MonicaThemeKind.Default, false);
        var selected = CurrentPreference(state);
        var writesWhileBlocked = storage.Writes.Count;
        read.SetResult(new ThemePreference(MonicaThemeKind.Grove, true));

        await Task.WhenAll(initialization, notifications.Reached).WaitAsync(TestContext.Current.CancellationToken);

        writesWhileBlocked.Should().Be(0);
        CurrentPreference(state).Should().Be(selected);
        storage.Saved.Should().Be(selected);
        storage.Writes.Should().OnlyContain(preference => preference == selected);
        module.DocumentThemes.Should().OnlyContain(theme => theme == (returnToDefault ? "default-light" : "fresh-dark"));
    }

    [Fact]
    public async Task InitializeAsync_WhenThemeChangesDuringInitialWrite_ShouldUseOneSnapshotPerPass()
    {
        var firstWrite = NewCompletion();
        var original = new ThemePreference(MonicaThemeKind.Default, false);
        var selected = new ThemePreference(MonicaThemeKind.Fresh, true);
        var state = CreateState();
        var storage = new ControlledStorage
        {
            WriteAsync = preference => preference == original ? firstWrite.Task : Task.CompletedTask
        };
        var module = new ControlledModule();
        var notifications = new NotificationRecorder(2);
        await using var session = new ThemeProviderHostSession(
            state, storage, new ControlledRuntime(module), notifications.NotifyAsync);

        var initialization = session.InitializeAsync();
        await storage.WriteStarted.WaitAsync(TestContext.Current.CancellationToken);
        state.SetTheme(selected.ThemeKind, selected.IsDarkMode);
        var writesWhileBlocked = storage.Writes.Count;
        firstWrite.SetResult();

        await Task.WhenAll(initialization, notifications.Reached).WaitAsync(TestContext.Current.CancellationToken);

        writesWhileBlocked.Should().Be(1);
        storage.Writes.Should().Equal(original, selected);
        storage.Saved.Should().Be(selected);
        module.DocumentThemes.Should().Equal("default-light", "fresh-dark");
        module.MaximumActiveCalls.Should().Be(1);
    }

    [Fact]
    public async Task InitializeAsync_WhenThemeChangesDuringInitialDocumentCall_ShouldSerializeEveryAdmittedPass()
    {
        var firstDocumentCall = NewCompletion();
        var state = CreateState();
        var storage = new ControlledStorage();
        var module = new ControlledModule
        {
            ApplyAsync = theme => theme == "default-light" ? firstDocumentCall.Task : Task.CompletedTask
        };
        var notifications = new NotificationRecorder(3);
        await using var session = new ThemeProviderHostSession(
            state, storage, new ControlledRuntime(module), notifications.NotifyAsync);

        var initialization = session.InitializeAsync();
        await module.InvocationStarted.WaitAsync(TestContext.Current.CancellationToken);
        state.SetTheme(MonicaThemeKind.Fresh, true);
        state.SetTheme(MonicaThemeKind.Grove, false);
        var callsWhileBlocked = module.DocumentThemes.Count;
        var writesWhileBlocked = storage.Writes.Count;
        firstDocumentCall.SetResult();

        await Task.WhenAll(initialization, notifications.Reached).WaitAsync(TestContext.Current.CancellationToken);

        callsWhileBlocked.Should().Be(1);
        writesWhileBlocked.Should().Be(1);
        storage.Saved.Should().Be(new ThemePreference(MonicaThemeKind.Grove, false));
        module.DocumentThemes.Should().Equal("default-light", "grove-light", "grove-light");
        module.MaximumActiveCalls.Should().Be(1);
        notifications.Count.Should().Be(3);
    }

    [Fact]
    public async Task InitializeAsync_WhenRestoreSubscriberChangesTheme_ShouldAdmitReentrantChoice()
    {
        var state = CreateState();
        state.OnThemeChanged += () =>
        {
            if (state.CurrentThemeKind == MonicaThemeKind.Grove)
                state.SetTheme(MonicaThemeKind.Fresh, true);
        };
        var selected = new ThemePreference(MonicaThemeKind.Fresh, true);
        var storage = new ControlledStorage(new ThemePreference(MonicaThemeKind.Grove, false));
        var module = new ControlledModule();
        var notifications = new NotificationRecorder(2);
        await using var session = new ThemeProviderHostSession(
            state, storage, new ControlledRuntime(module), notifications.NotifyAsync);

        await session.InitializeAsync().WaitAsync(TestContext.Current.CancellationToken);
        await notifications.Reached.WaitAsync(TestContext.Current.CancellationToken);

        CurrentPreference(state).Should().Be(selected);
        storage.Saved.Should().Be(selected);
        module.DocumentThemes.Should().OnlyContain(theme => theme == "fresh-dark");
    }

    [Fact]
    public async Task DisposeAsync_WhenReadIsPending_ShouldDrainWithoutRestoreOrFurtherAdmission()
    {
        var read = NewCompletion<ThemePreference?>();
        var state = CreateState();
        var storage = new ControlledStorage { ReadAsync = () => read.Task };
        var module = new ControlledModule();
        var runtime = new ControlledRuntime(module);
        var notifications = new NotificationRecorder();
        var session = new ThemeProviderHostSession(state, storage, runtime, notifications.NotifyAsync);

        var initialization = session.InitializeAsync();
        await storage.ReadStarted.WaitAsync(TestContext.Current.CancellationToken);
        state.SetTheme(MonicaThemeKind.Fresh, true);
        var disposal = session.DisposeAsync().AsTask();
        var repeatedDisposal = session.DisposeAsync().AsTask();
        var wasPending = !disposal.IsCompleted;
        read.SetResult(new ThemePreference(MonicaThemeKind.Grove, false));
        await Task.WhenAll(initialization, disposal, repeatedDisposal).WaitAsync(TestContext.Current.CancellationToken);
        var retiredPreference = CurrentPreference(state);
        state.SetTheme(MonicaThemeKind.Grove, true);
        await session.InitializeAsync().WaitAsync(TestContext.Current.CancellationToken);

        wasPending.Should().BeTrue();
        repeatedDisposal.Should().BeSameAs(disposal);
        retiredPreference.Should().Be(new ThemePreference(MonicaThemeKind.Fresh, true));
        storage.ReadCount.Should().Be(1);
        storage.Writes.Should().BeEmpty();
        runtime.ImportCount.Should().Be(0);
        module.DocumentThemes.Should().BeEmpty();
        notifications.Count.Should().Be(0);
        storage.DisposeCount.Should().Be(0);
        runtime.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task InitializeAsync_WhenReadSynchronouslyRetiresOwner_ShouldPublishInitializationBeforeCallingStorage()
    {
        var read = NewCompletion<ThemePreference?>();
        var state = CreateState();
        var storage = new ControlledStorage();
        var module = new ControlledModule();
        var notifications = new NotificationRecorder();
        var session = new ThemeProviderHostSession(
            state, storage, new ControlledRuntime(module), notifications.NotifyAsync);
        Task? reentrantDisposal = null;
        storage.ReadAsync = () =>
        {
            reentrantDisposal = session.DisposeAsync().AsTask();
            return read.Task;
        };

        var initialization = session.InitializeAsync();
        var disposal = Assert.IsAssignableFrom<Task>(reentrantDisposal);
        var wasPending = !disposal.IsCompleted;
        read.SetResult(new ThemePreference(MonicaThemeKind.Grove, true));
        await Task.WhenAll(initialization, disposal).WaitAsync(TestContext.Current.CancellationToken);

        wasPending.Should().BeTrue();
        CurrentPreference(state).Should().Be(new ThemePreference(MonicaThemeKind.Default, false));
        storage.Writes.Should().BeEmpty();
        module.DocumentThemes.Should().BeEmpty();
        notifications.Count.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_WhenInitialWriteIsPending_ShouldDrainActualWriteWithoutStartingDocumentCall()
    {
        var write = NewCompletion();
        var state = CreateState();
        var storage = new ControlledStorage { WriteAsync = _ => write.Task };
        var module = new ControlledModule();
        var runtime = new ControlledRuntime(module);
        var notifications = new NotificationRecorder();
        var session = new ThemeProviderHostSession(state, storage, runtime, notifications.NotifyAsync);

        var initialization = session.InitializeAsync();
        await storage.WriteStarted.WaitAsync(TestContext.Current.CancellationToken);
        state.SetTheme(MonicaThemeKind.Fresh, true);
        var disposal = session.DisposeAsync().AsTask();
        var wasPending = !disposal.IsCompleted;
        write.SetResult();
        await Task.WhenAll(initialization, disposal).WaitAsync(TestContext.Current.CancellationToken);

        wasPending.Should().BeTrue();
        storage.Writes.Should().ContainSingle();
        storage.Saved.Should().Be(new ThemePreference(MonicaThemeKind.Default, false));
        runtime.ImportCount.Should().Be(0);
        module.DocumentThemes.Should().BeEmpty();
        notifications.Count.Should().Be(0);
        storage.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_WhenImportCompletesLate_ShouldRetireInteropBeforeDrainingInitialization()
    {
        var import = NewCompletion<IJSObjectReference>();
        var state = CreateState();
        var storage = new ControlledStorage();
        var module = new ControlledModule();
        var runtime = new ControlledRuntime(module) { ImportAsync = () => import.Task };
        var notifications = new NotificationRecorder();
        var session = new ThemeProviderHostSession(state, storage, runtime, notifications.NotifyAsync);

        var initialization = session.InitializeAsync();
        await runtime.ImportStarted.WaitAsync(TestContext.Current.CancellationToken);
        state.SetTheme(MonicaThemeKind.Fresh, true);
        var disposal = session.DisposeAsync().AsTask();
        var wasPending = !disposal.IsCompleted;
        import.SetResult(module);
        await Task.WhenAll(initialization, disposal).WaitAsync(TestContext.Current.CancellationToken);

        wasPending.Should().BeTrue();
        module.DocumentThemes.Should().BeEmpty();
        module.DisposeCount.Should().Be(1);
        notifications.Count.Should().Be(0);
        storage.Writes.Should().ContainSingle().Which.Should().Be(new ThemePreference(MonicaThemeKind.Default, false));
        storage.DisposeCount.Should().Be(0);
        runtime.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_WhenInitialDocumentCallIsPending_ShouldDrainWithoutLateNotifications()
    {
        var documentCall = NewCompletion();
        var state = CreateState();
        var storage = new ControlledStorage();
        var module = new ControlledModule { ApplyAsync = _ => documentCall.Task };
        var notifications = new NotificationRecorder();
        var session = new ThemeProviderHostSession(
            state, storage, new ControlledRuntime(module), notifications.NotifyAsync);

        var initialization = session.InitializeAsync();
        await module.InvocationStarted.WaitAsync(TestContext.Current.CancellationToken);
        state.SetTheme(MonicaThemeKind.Fresh, true);
        var disposal = session.DisposeAsync().AsTask();
        var wasPending = !disposal.IsCompleted;
        documentCall.SetResult();
        await Task.WhenAll(initialization, disposal).WaitAsync(TestContext.Current.CancellationToken);

        wasPending.Should().BeTrue();
        module.DocumentThemes.Should().Equal("default-light");
        module.DisposeCount.Should().Be(1);
        storage.Writes.Should().ContainSingle();
        notifications.Count.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_WhenExternalDocumentCallIsCanceled_ShouldPreserveCancellationAndFinishCleanup()
    {
        var documentCall = NewCompletion();
        var cleanupStarted = NewCompletion();
        var cleanup = NewCompletion();
        var externalCancellation = new CancellationToken(canceled: true);
        var state = CreateState();
        var storage = new ControlledStorage();
        var module = new ControlledModule
        {
            ApplyAsync = _ => documentCall.Task,
            ReleaseAsync = () =>
            {
                cleanupStarted.SetResult();
                return new ValueTask(cleanup.Task);
            }
        };
        var notifications = new NotificationRecorder();
        var session = new ThemeProviderHostSession(
            state, storage, new ControlledRuntime(module), notifications.NotifyAsync);

        var initialization = session.InitializeAsync();
        await module.InvocationStarted.WaitAsync(TestContext.Current.CancellationToken);
        state.SetTheme(MonicaThemeKind.Fresh, true);
        var disposal = session.DisposeAsync().AsTask();
        documentCall.SetCanceled(externalCancellation);
        var initializationCancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initialization.WaitAsync(TestContext.Current.CancellationToken));
        await cleanupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var wasPending = !disposal.IsCompleted;
        cleanup.SetResult();
        var disposalCancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => disposal.WaitAsync(TestContext.Current.CancellationToken));

        wasPending.Should().BeTrue();
        initializationCancellation.CancellationToken.Should().Be(externalCancellation);
        disposalCancellation.CancellationToken.Should().Be(externalCancellation);
        module.DisposeCount.Should().Be(1);
        notifications.Count.Should().Be(0);
        storage.DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_WhenRenderCallbackReentersRetirement_ShouldShareCompleteRendererDrain()
    {
        var render = NewCompletion();
        var state = CreateState();
        var storage = new ControlledStorage();
        var module = new ControlledModule();
        Task? reentrantDisposal = null;
        ThemeProviderHostSession? session = null;
        var notifications = new NotificationRecorder
        {
            CallbackAsync = () =>
            {
                reentrantDisposal = Assert.IsType<ThemeProviderHostSession>(session).DisposeAsync().AsTask();
                return render.Task;
            }
        };
        session = new ThemeProviderHostSession(
            state, storage, new ControlledRuntime(module), notifications.NotifyAsync);
        await session.InitializeAsync().WaitAsync(TestContext.Current.CancellationToken);

        var disposal = session.DisposeAsync().AsTask();
        var repeatedDisposal = session.DisposeAsync().AsTask();
        var wasPending = !disposal.IsCompleted;
        render.SetResult();
        await Task.WhenAll(disposal, repeatedDisposal).WaitAsync(TestContext.Current.CancellationToken);
        state.SetTheme(MonicaThemeKind.Grove, true);

        wasPending.Should().BeTrue();
        repeatedDisposal.Should().BeSameAs(disposal);
        reentrantDisposal.Should().BeSameAs(disposal);
        notifications.Count.Should().Be(1);
        module.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_WhenModuleCleanupReentersRetirement_ShouldReuseReservedCompletion()
    {
        var cleanup = NewCompletion();
        var state = CreateState();
        var storage = new ControlledStorage();
        var module = new ControlledModule();
        var notifications = new NotificationRecorder();
        var session = new ThemeProviderHostSession(
            state, storage, new ControlledRuntime(module), notifications.NotifyAsync);
        await session.InitializeAsync().WaitAsync(TestContext.Current.CancellationToken);
        Task? reentrantDisposal = null;
        module.ReleaseAsync = () =>
        {
            reentrantDisposal = session.DisposeAsync().AsTask();
            return new ValueTask(cleanup.Task);
        };

        var disposal = session.DisposeAsync().AsTask();
        var reentered = Assert.IsAssignableFrom<Task>(reentrantDisposal);
        var wasPending = !disposal.IsCompleted;
        cleanup.SetResult();
        await Task.WhenAll(disposal, reentered).WaitAsync(TestContext.Current.CancellationToken);

        wasPending.Should().BeTrue();
        reentered.Should().BeSameAs(disposal);
        module.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_WhenAdmittedDocumentCallFaults_ShouldPreserveFailureAndReleaseModule()
    {
        var documentCall = NewCompletion();
        var callStarted = NewCompletion();
        var failure = new InvalidOperationException("Document synchronization failed.");
        var state = CreateState();
        var storage = new ControlledStorage();
        var module = new ControlledModule();
        var notifications = new NotificationRecorder();
        var session = new ThemeProviderHostSession(
            state, storage, new ControlledRuntime(module), notifications.NotifyAsync);
        await session.InitializeAsync().WaitAsync(TestContext.Current.CancellationToken);
        module.ApplyAsync = _ =>
        {
            callStarted.SetResult();
            return documentCall.Task;
        };

        state.SetTheme(MonicaThemeKind.Fresh, true);
        await callStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var disposal = session.DisposeAsync().AsTask();
        var wasPending = !disposal.IsCompleted;
        documentCall.SetException(failure);
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => disposal.WaitAsync(TestContext.Current.CancellationToken));

        wasPending.Should().BeTrue();
        observed.Should().BeSameAs(failure);
        module.DisposeCount.Should().Be(1);
        notifications.Count.Should().Be(1);
        storage.DisposeCount.Should().Be(0);
    }

    private static ThemeState CreateState() => new(Options.Create(new ModuleUIOption()));
    private static ThemePreference CurrentPreference(ThemeState state) => new(state.CurrentThemeKind, state.IsDarkMode);
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class NotificationRecorder(int expectedCount = 1)
    {
        private readonly TaskCompletionSource _reached = NewCompletion();
        internal int Count { get; private set; }
        internal Task Reached => _reached.Task;
        internal Func<Task>? CallbackAsync { get; init; }

        internal Task NotifyAsync()
        {
            Count++;
            if (Count == expectedCount) _reached.SetResult();
            return CallbackAsync?.Invoke() ?? Task.CompletedTask;
        }
    }

    private sealed class ControlledStorage(ThemePreference? saved = null) : IBrowserStorage
    {
        private readonly TaskCompletionSource _readStarted = NewCompletion();
        private readonly TaskCompletionSource _writeStarted = NewCompletion();
        internal Func<Task<ThemePreference?>>? ReadAsync { get; set; }
        internal Func<ThemePreference, Task>? WriteAsync { get; init; }
        internal Task ReadStarted => _readStarted.Task;
        internal Task WriteStarted => _writeStarted.Task;
        internal int ReadCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal ThemePreference? Saved { get; private set; } = saved;
        internal List<ThemePreference> Writes { get; } = [];

        public async Task<T> GetAsync<T>(string key, T defaultValue, BrowserStorageType storageType = BrowserStorageType.Local)
        {
            key.Should().Be(ThemePreference.StorageKey);
            storageType.Should().Be(BrowserStorageType.Local);
            ReadCount++;
            _readStarted.TrySetResult();
            var preference = ReadAsync is { } read ? await read() : Saved;
            return preference is null ? defaultValue : (T)(object)preference;
        }

        public async Task SetAsync<T>(string key, T value, BrowserStorageType storageType = BrowserStorageType.Local)
        {
            key.Should().Be(ThemePreference.StorageKey);
            storageType.Should().Be(BrowserStorageType.Local);
            var preference = Assert.IsType<ThemePreference>(value);
            Writes.Add(preference);
            _writeStarted.TrySetResult();
            if (WriteAsync is { } write) await write(preference);
            Saved = preference;
        }

        public Task<BrowserStorageWriteResult> TrySetAsync<T>(string key, T value, BrowserStorageType storageType = BrowserStorageType.Local)
            => throw new NotSupportedException();
        public Task RemoveAsync(string key, BrowserStorageType storageType = BrowserStorageType.Local)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetKeysAsync(string category, BrowserStorageType storageType = BrowserStorageType.Local)
            => throw new NotSupportedException();
        public Task<int> ClearCategoryAsync(string category, BrowserStorageType storageType = BrowserStorageType.Local)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledRuntime(IJSObjectReference module) : IJSRuntime, IAsyncDisposable
    {
        private readonly TaskCompletionSource _importStarted = NewCompletion();
        internal Func<Task<IJSObjectReference>>? ImportAsync { get; init; }
        internal Task ImportStarted => _importStarted.Task;
        internal int ImportCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            identifier.Should().Be("import");
            args.Should().Equal("./_content/Monica.UI/js/theme-helper.js");
            ImportCount++;
            _importStarted.TrySetResult();
            var reference = ImportAsync is { } import ? await import() : module;
            return (TValue)reference;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledModule : IJSObjectReference
    {
        private readonly TaskCompletionSource _invocationStarted = NewCompletion();
        private int _activeCalls;
        internal Func<string, Task>? ApplyAsync { get; set; }
        internal Func<ValueTask>? ReleaseAsync { get; set; }
        internal Task InvocationStarted => _invocationStarted.Task;
        internal List<string> DocumentThemes { get; } = [];
        internal int MaximumActiveCalls { get; private set; }
        internal int DisposeCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            identifier.Should().Be("setDocumentTheme");
            var theme = Assert.IsType<string>(Assert.Single(args ?? []));
            DocumentThemes.Add(theme);
            MaximumActiveCalls = Math.Max(MaximumActiveCalls, ++_activeCalls);
            _invocationStarted.TrySetResult();
            try
            {
                if (ApplyAsync is { } apply) await apply(theme);
                return default!;
            }
            finally
            {
                _activeCalls--;
            }
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (ReleaseAsync is { } release) await release();
        }
    }
}
