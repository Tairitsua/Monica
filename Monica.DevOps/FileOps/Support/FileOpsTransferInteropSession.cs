using Microsoft.JSInterop;
using Monica.DevOps.FileOps.Models;

namespace Monica.DevOps.FileOps.Support;

/// <summary>Owns stream-reference and module interop for one FileOps visit.</summary>
/// <remarks>The runtime and download descriptor are borrowed. The descriptor owner keeps its stream open until
/// <see cref="DownloadAsync"/> completes, then disposes it even after failure. This session joins admitted users
/// and late imports before releasing its module; it never performs DOM cleanup during disposal.</remarks>
public sealed class FileOpsTransferInteropSession(IJSRuntime runtime) : IAsyncDisposable
{
    internal const string MODULE_ASSET = "_content/Monica.DevOps/FileOps/Components/FileOpsWorkspace.razor.js";
    private readonly string _modulePath = $"./{MODULE_ASSET}";
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Task> _users = [];
    private IJSObjectReference? _module;
    private Task? _retirement;
    private bool _retired;

    internal FileOpsTransferInteropSession(IJSRuntime runtime, string modulePath) : this(runtime)
    {
        _modulePath = modulePath;
    }

    /// <summary>Buffers the borrowed stream in the browser and initiates a native download.</summary>
    /// <remarks>A true result means initiation, not persisted browser output. Returns false when this visit has
    /// retired or the circuit disconnected. Unexpected live import/invocation failures propagate to the owner.</remarks>
    public Task<bool> DownloadAsync(FileOpsDownloadDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        TaskCompletionSource<bool> completion;
        lock (_sync)
        {
            if (_retired) return Task.FromResult(false);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _users.Add(completion.Task);
        }
        _ = RunAsync();
        return completion.Task;

        async Task RunAsync()
        {
            var acquired = false;
            var started = false;
            Exception? failure = null;
            try
            {
                await _gate.WaitAsync();
                acquired = true;
                if (!IsRetired())
                {
                    _module ??= await runtime.InvokeAsync<IJSObjectReference>("import", _modulePath);
                    if (!IsRetired())
                    {
                        using var reference = new DotNetStreamReference(descriptor.ContentStream, leaveOpen: true);
                        started = await _module.InvokeAsync<bool>("downloadStream", descriptor.FileName, descriptor.ContentType, reference);
                    }
                }
            }
            catch (JSDisconnectedException) { }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (acquired) _gate.Release();
                lock (_sync)
                {
                    _users.Remove(completion.Task);
                    if (failure is null) completion.TrySetResult(!_retired && started);
                    else completion.TrySetException(failure);
                }
            }
        }
    }

    private bool IsRetired() { lock (_sync) return _retired; }

    /// <summary>Stops admission and releases the module after all actual users and imports finish.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] users;
        lock (_sync)
        {
            if (_retirement is not null) return new(_retirement);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = completion.Task;
            _retired = true;
            users = _users.ToArray();
        }
        _ = DrainAsync();
        return new(completion.Task);

        async Task DrainAsync()
        {
            Exception? failure = null;
            try { await Task.WhenAll(users); }
            catch (Exception exception) { failure = exception; }
            try
            {
                if (_module is not null)
                {
                    try { await _module.DisposeAsync(); }
                    catch (JSDisconnectedException) { }
                    _module = null;
                }
            }
            catch (Exception exception) { failure = failure is null ? exception : new AggregateException(failure, exception); }
            finally { _gate.Dispose(); }
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
    }
}
