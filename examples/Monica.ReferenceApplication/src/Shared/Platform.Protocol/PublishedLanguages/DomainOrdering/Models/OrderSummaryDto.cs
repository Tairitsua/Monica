namespace Platform.Protocol.PublishedLanguages.DomainOrdering.Models;

/// <summary>Materialized summary contract, independent of rendering and UI state.</summary>
public sealed record OrderSummaryDto(int TotalOrders, int DraftOrders);
