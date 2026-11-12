using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>
/// Keeps external scenarios within the shared verification server's four-connection allowance.
/// </summary>
/// <remarks>
/// Each scenario owns its database. The shared resource requiring serialization is the server connection budget;
/// a scenario can use one administration connection and up to three independent protocol connections.
/// </remarks>
[CollectionDefinition(ExternalSchedulerDatabaseCollection.NAME)]
public sealed class ExternalSchedulerDatabaseCollection
{
    internal const string NAME = "JobScheduler external server connection budget";
}
