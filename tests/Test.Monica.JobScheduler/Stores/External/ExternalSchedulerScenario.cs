using System.Runtime.ExceptionServices;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>
/// Disposes every scenario connection before owned database cleanup and preserves both failure causes.
/// </summary>
internal static class ExternalSchedulerScenario
{
    internal static async Task RunAsync(IAsyncDisposable resource, Func<Task> operation)
    {
        Exception? operationError = null;
        try
        {
            await operation();
            TestContext.Current.TestOutputHelper?.WriteLine("External scenario assertions passed before owned database cleanup.");
        }
        catch (Exception exception)
        {
            operationError = exception;
            TestContext.Current.TestOutputHelper?.WriteLine($"External scenario operation failed: {exception}");
        }

        try
        {
            await resource.DisposeAsync();
        }
        catch (Exception cleanupError)
        {
            if (operationError is not null)
            {
                throw new AggregateException("The external scenario and its owned database cleanup both failed.",
                    operationError, cleanupError);
            }

            throw;
        }

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }
    }
}
