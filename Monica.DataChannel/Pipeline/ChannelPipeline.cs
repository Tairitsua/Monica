using Microsoft.Extensions.Logging;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Core.ObservableInstance.Models;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Abstractions.Communication;
using Monica.DataChannel.Abstractions.Pipeline;
using Monica.DataChannel.Models;
using Monica.Tool.Extensions;

namespace Monica.DataChannel.Pipeline;

/// <summary>
/// Represents a data pipeline.
/// Acts as the core component of a data channel and is responsible for data transport, transformation, and processing.
/// Manages how endpoints and middleware are connected and coordinated.
/// </summary>
public class ChannelPipeline : IObservableInstance
{
    private readonly object _initializationGate = new();
    private readonly object _registrationGate = new();
    private readonly Dictionary<string, PipelineComponentRegistration> _componentRegistrations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private bool _isInitialized;
    private bool _isInitializing;
    private bool _isNotAvailable;
    private bool _isDisposed;
    private Task? _disposalTask;
    /// <summary>
    /// Gets or sets the inner endpoint.
    /// Handles data flowing from the outer side into the inner side.
    /// Callers own synchronization of replacement; management captures retire the previous endpoint's action key.
    /// </summary>
    public IPipelineEndpoint InnerEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the outer endpoint.
    /// Handles data flowing from the inner side out to the outer side.
    /// Callers own synchronization of replacement; management captures retire the previous endpoint's action key.
    /// </summary>
    public IPipelineEndpoint OuterEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the unique pipeline identifier.
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// Gets or sets the pipeline group identifier.
    /// Used to organize related pipelines together.
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// Gets whether the last admitted initialization attempt completed successfully. False during an active attempt.
    /// </summary>
    public bool IsInitialized { get { lock (_initializationGate) return _isInitialized; } }

    /// <summary>
    /// Gets a value indicating whether initialization is currently in progress.
    /// </summary>
    public bool IsInitializing { get { lock (_initializationGate) return _isInitializing; } }

    /// <summary>
    /// Gets or sets a value indicating whether the pipeline is unavailable.
    /// This is set to <see langword="true"/> when initialization fails or a fatal error occurs.
    /// </summary>
    public bool IsNotAvailable
    {
        get { lock (_initializationGate) return _isNotAvailable; }
        set { lock (_initializationGate) _isNotAvailable = value; }
    }

    /// <summary>
    /// Gets or sets the observable tracker used to record runtime state and exceptions.
    /// </summary>
    public ObservableInstanceTracker ObservableTracker { get; set; } = null!;

    /// <summary>
    /// Gets a value indicating whether the pipeline has recorded exceptions.
    /// </summary>
    public bool HasExceptions => ObservableTracker?.HasExceptions ?? false;

    /// <summary>
    /// Initializes a new instance of <see cref="ChannelPipeline"/>.
    /// </summary>
    /// <param name="innerEndpoint">The inner endpoint.</param>
    /// <param name="outerEndpoint">The outer endpoint.</param>
    /// <param name="id">The pipeline identifier.</param>
    /// <param name="observableManager">The observable instance registry.</param>
    /// <param name="logger">The host-owned pipeline logger.</param>
    /// <param name="maxHistorySize">The maximum observable history size.</param>
    /// <param name="groupId">An optional pipeline group identifier.</param>
    internal ChannelPipeline(
        IPipelineEndpoint innerEndpoint,
        IPipelineEndpoint outerEndpoint,
        string id,
        IObservableInstanceRegistry observableManager,
        ILogger<ChannelPipeline> logger,
        int maxHistorySize,
        string? groupId = null)
    {
        InnerEndpoint = innerEndpoint;
        OuterEndpoint = outerEndpoint;
        Id = id;
        GroupId = groupId;
        RegisterComponent(innerEndpoint, PipelineComponentStage.InnerEndpoint, 0);
        RegisterComponent(outerEndpoint, PipelineComponentStage.OuterEndpoint, 0);

        // Register the pipeline with the observable tracker.
        ObservableTracker = observableManager.Register(id, opt =>
        {
            opt.MaxHistorySize = maxHistorySize;
            opt.InstanceName = id;
            opt.InstanceType = typeof(ChannelPipeline);
            opt.GroupId = groupId;
            opt.Logger = logger;
        });
    }

    /// <summary>
    /// Creates a new data pipeline builder.
    /// </summary>
    /// <returns>A new <see cref="ChannelPipelineBuilder"/> instance.</returns>
    public static ChannelPipelineBuilder Create() => new();

    /// <summary>
    /// Gets the endpoint middleware collection.
    /// These middleware components participate in endpoint-related behavior.
    /// Callers own synchronization of mutations; removed or replaced registration slots retire their action keys.
    /// </summary>
    public List<IPipelineEndpointMiddleware> EndpointMiddlewares { get; private set; } = [];

    /// <summary>
    /// Gets the transform middleware collection.
    /// These middleware components handle data transformation and processing.
    /// Processing order is identical in both message directions. Callers own synchronization of mutations;
    /// removed, replaced or moved registration slots retire their action keys.
    /// </summary>
    public List<IPipelineTransformMiddleware> TransformMiddlewares { get; private set; } = [];

    /// <summary>
    /// Enumerates all middleware registered in the pipeline.
    /// </summary>
    /// <returns>An enumeration of all pipeline middleware.</returns>
    internal IEnumerable<IPipelineMiddleware> GetMiddlewares()
    {
        foreach (var endpointMiddleware in EndpointMiddlewares)
        {
            yield return endpointMiddleware;
        }

        foreach (var transformMiddleware in TransformMiddlewares)
        {
            yield return transformMiddleware;
        }
    }

    /// <summary>
    /// Enumerates all endpoints in the pipeline.
    /// </summary>
    /// <returns>An enumeration of all pipeline endpoints.</returns>
    internal IEnumerable<IPipelineEndpoint> GetEndpoints()
    {
        yield return InnerEndpoint;
        yield return OuterEndpoint;
    }

    /// <summary>
    /// Enumerates all components in the pipeline, including endpoints and middleware.
    /// </summary>
    /// <returns>An enumeration of all pipeline components.</returns>
    internal IEnumerable<IPipelineComponent> GetComponents()
    {
        foreach (var endpoint in GetEndpoints())
        {
            yield return endpoint;
        }

        foreach (var middleware in GetMiddlewares())
        {
            yield return middleware;
        }
    }

    /// <summary>
    /// Assigns middleware to the appropriate internal collections based on type.
    /// </summary>
    /// <param name="middlewares">The middleware collection to assign.</param>
    internal void SetMiddlewares(IReadOnlyList<IPipelineMiddleware> middlewares)
    {
        EndpointMiddlewares = middlewares.OfType<IPipelineEndpointMiddleware>().ToList();
        TransformMiddlewares = middlewares.OfType<IPipelineTransformMiddleware>().ToList();
        for (var ordinal = 0; ordinal < EndpointMiddlewares.Count; ordinal++)
            RegisterComponent(EndpointMiddlewares[ordinal], PipelineComponentStage.EndpointConfiguration, ordinal);
        for (var ordinal = 0; ordinal < TransformMiddlewares.Count; ordinal++)
            RegisterComponent(TransformMiddlewares[ordinal], PipelineComponentStage.Transform, ordinal);
    }

    private void RegisterComponent(IPipelineComponent component, PipelineComponentStage stage, int ordinal)
    {
        var key = Guid.NewGuid().ToString("N");
        _componentRegistrations.Add(key, new(key, stage, ordinal, component));
    }

    internal IReadOnlyList<PipelineComponentRegistration> GetComponentRegistrations()
    {
        lock (_registrationGate)
        {
            ReconcileRegistrations();
            return _componentRegistrations.Values.OrderBy(component => component.Stage)
                .ThenBy(component => component.Ordinal).ToArray();
        }
    }

    internal IPipelineMiddleware? ResolveMiddleware(string componentKey)
    {
        lock (_registrationGate)
        {
            ReconcileRegistrations();
            return _componentRegistrations.TryGetValue(componentKey, out var registration)
                ? registration.Component as IPipelineMiddleware : null;
        }
    }

    private void ReconcileRegistrations()
    {
        // The gate protects owner-issued keys. Callers still own synchronization of public component mutations.
        var existing = _componentRegistrations.Values.ToDictionary(component => (component.Stage, component.Ordinal));
        _componentRegistrations.Clear();
        RetainOrRegister(InnerEndpoint, PipelineComponentStage.InnerEndpoint, 0);
        RetainOrRegister(OuterEndpoint, PipelineComponentStage.OuterEndpoint, 0);
        var endpoints = EndpointMiddlewares.ToArray();
        for (var ordinal = 0; ordinal < endpoints.Length; ordinal++)
            RetainOrRegister(endpoints[ordinal], PipelineComponentStage.EndpointConfiguration, ordinal);
        var transforms = TransformMiddlewares.ToArray();
        for (var ordinal = 0; ordinal < transforms.Length; ordinal++)
            RetainOrRegister(transforms[ordinal], PipelineComponentStage.Transform, ordinal);

        void RetainOrRegister(IPipelineComponent component, PipelineComponentStage stage, int ordinal)
        {
            if (existing.TryGetValue((stage, ordinal), out var registration) && ReferenceEquals(registration.Component, component))
                _componentRegistrations.Add(registration.Key, registration);
            else RegisterComponent(component, stage, ordinal);
        }
    }

    internal (bool IsInitialized, bool IsInitializing, bool IsNotAvailable) CaptureInitializationState()
    {
        lock (_initializationGate) return (_isInitialized, _isInitializing, _isNotAvailable);
    }

    /// <summary>
    /// Records exception details in the observable tracker.
    /// </summary>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="source">The source object that caused the exception.</param>
    /// <param name="description">An optional description of the exception.</param>
    public void CollectException(Exception exception, object source, string? description = null)
    {
        var message = description ?? exception.Message;
        ObservableTracker.RecordState(message, PipelineState.Error, exception);
    }

    /// <summary>
    /// Records a pipeline state transition.
    /// </summary>
    /// <param name="state">The new state.</param>
    /// <param name="message">The state description.</param>
    public void RecordState(PipelineState state, string message)
    {
        ObservableTracker.RecordState(message, state);
    }

    /// <summary>
    /// Records an exception and switches the pipeline to the error state.
    /// </summary>
    /// <param name="exception">The exception instance.</param>
    /// <param name="message">The exception description.</param>
    public void RecordException(Exception exception, string message)
    {
        ObservableTracker.RecordState(message, PipelineState.Error, exception);
    }

    /// <summary>
    /// Gets all recorded exceptions.
    /// </summary>
    public IReadOnlyList<ObservableStateEntry> GetExceptions()
    {
        return ObservableTracker.GetExceptions();
    }

    /// <summary>
    /// Gets the most recent exception records.
    /// </summary>
    public IReadOnlyList<ObservableStateEntry> GetRecentExceptions(int count)
    {
        return ObservableTracker.GetRecentExceptions(count);
    }

    /// <summary>
    /// Sends data through the pipeline.
    /// The data is processed by transform middleware and then dispatched to the opposite endpoint based on its source side.
    /// The original cancellation token owns the entire delivery, even when middleware replaces the context.
    /// </summary>
    /// <param name="data">The data context to send.</param>
    /// <returns>A task that represents the asynchronous send operation.</returns>
    public async Task SendDataAsync(ChannelDataContext data)
    {
        var cancellationToken = data.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        // Run transform middleware.
        await TransformMiddlewares.DoAsync(async p =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                data = await p.PassAsync(data);
                data.CancellationToken = cancellationToken;
            }
            catch (Exception ex)
            {
                CollectException(ex, p);
                throw; // Rethrow to preserve the existing behavior.
            }
        });


        cancellationToken.ThrowIfCancellationRequested();
        // Dispatch to the target endpoint.
        try
        {
            if (data.Source == ChannelSide.Outer)
            {
                await InnerEndpoint.ReceiveDataAsync(data);
            }
            else if(data.Source == ChannelSide.Inner)
            {
                await OuterEndpoint.ReceiveDataAsync(data);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            var targetEndpoint = data.Source == ChannelSide.Outer ? InnerEndpoint : OuterEndpoint;
            CollectException(ex, targetEndpoint);
            throw; // Rethrow to preserve the existing behavior.
        }
    }

    /// <summary>
    /// Initializes the pipeline and its components.
    /// Rejects concurrent initialization and serializes the admitted attempt with host shutdown.
    /// Failed or cancelled attempts release endpoint resources before retiring their initialization state.
    /// </summary>
    /// <param name="cancellationToken">Cancels initialization; endpoint rollback does not use the cancelled token.</param>
    /// <returns>A task that represents the initialization operation.</returns>
    internal async Task InitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_initializationGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_isInitializing)
                throw new InvalidOperationException($"Data channel '{Id}' is already initializing.");
            _isInitializing = true;
            _isInitialized = false;
        }
        var completed = false;
        var lifecycleEntered = false;
        try
        {
            await _lifecycleLock.WaitAsync(cancellationToken);
            lifecycleEntered = true;
            lock (_initializationGate)
                ObjectDisposedException.ThrowIf(_isDisposed, this);

            var endpoints = GetEndpoints().OfType<ICommunicationEndpoint>().ToArray();
            InnerEndpoint.Pipe = this;
            OuterEndpoint.Pipe = this;
            GetMiddlewares().OfType<IPipelineAware>().Do(p => p.Pipe = this);
            foreach (var proxy in GetMiddlewares().OfType<TransientPipeTransformMiddlewareProxy>())
                proxy.Pipeline = this;

            try
            {
                foreach (var endpoint in endpoints)
                {
                    try { await endpoint.InitAsync(cancellationToken); }
                    catch (Exception exception)
                    {
                        CollectException(exception, endpoint,
                            $"Data channel '{Id}' failed to initialize communication endpoint {endpoint.GetType().Name}.");
                        throw;
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                completed = true;
            }
            catch
            {
                // Release partial and previous resources, stopping ingress before the inner receiver.
                foreach (var endpoint in endpoints.Reverse())
                {
                    try { await endpoint.DisposeAsync(); }
                    catch (Exception cleanupException) { CollectException(cleanupException, endpoint); }
                }
                throw;
            }
        }
        finally
        {
            lock (_initializationGate)
            {
                _isInitialized = completed;
                _isNotAvailable = !completed;
                _isInitializing = false;
            }
            if (lifecycleEntered) _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Retires the host-owned pipeline after any admitted initialization completes.
    /// Releases every endpoint in reverse order, even when another endpoint fails to stop.
    /// Repeated calls observe the same cleanup completion, including its original failure.
    /// </summary>
    internal Task DisposeAsync()
    {
        lock (_initializationGate)
            return _disposalTask ??= DisposeCoreAsync();
    }

    private async Task DisposeCoreAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            lock (_initializationGate)
            {
                _isDisposed = true;
                _isInitialized = false;
                _isNotAvailable = true;
            }

            List<Exception>? failures = null;
            // Stop ingress first: inner endpoints may be awaiting work with the outer receiver's token.
            foreach (var endpoint in GetEndpoints().Reverse().OfType<ICommunicationEndpoint>())
            {
                try { await endpoint.DisposeAsync(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
            ObservableTracker?.Dispose();
            if (failures is not null) throw new AggregateException(failures);
        }
        finally { _lifecycleLock.Release(); }
    }
}
