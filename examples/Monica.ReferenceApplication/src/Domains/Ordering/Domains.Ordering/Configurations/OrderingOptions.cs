using System.ComponentModel.DataAnnotations;
using Monica.Configuration.Annotations;

namespace Domains.Ordering.Configurations;

/// <summary>
/// Controls runtime behavior for the ordering bounded context.
/// </summary>
[Configuration(
    "Ordering",
    DefinitionKey = "reference.ordering",
    DisplayName = "Ordering",
    Description = "Runtime limits and reporting thresholds for the ordering bounded context.",
    Category = "Ordering")]
public sealed class OrderingOptions
{
    /// <summary>
    /// Gets or sets the maximum number of orders returned by the collection query.
    /// The default is 100; lower this value when callers should page a large backlog explicitly.
    /// </summary>
    [Range(1, 500)]
    public int MaximumOrdersReturned { get; set; } = 100;

    /// <summary>
    /// Gets or sets the number of draft orders at which the backlog report is logged as a warning.
    /// The default is 10; tune it to the operational capacity of the deployment.
    /// </summary>
    [Range(1, 10_000)]
    public int BacklogWarningThreshold { get; set; } = 10;
}
