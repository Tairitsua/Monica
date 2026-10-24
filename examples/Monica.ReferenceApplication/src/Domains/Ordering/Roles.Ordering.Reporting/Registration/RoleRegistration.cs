using Monica.ApplicationModel.Abstractions;
using Monica.Modules;

namespace Roles.Ordering.Reporting.Registration;

/// <summary>Composes the reporting scheduler when this role is placed on the current application.</summary>
public sealed class RoleRegistration : ApplicationRegistration
{
    /// <inheritdoc />
    public override void ConfigureActive(ApplicationRegistrationContext context)
    {
        context.Monica.AddJobScheduler(options => options.MaxWorkerExecutionThreads = 1)
            .UseInMemoryStore()
            .UseSchedulerScope("monica-reference-ordering");
    }
}
