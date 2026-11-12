using Platform.Protocol.PublishedLanguages.DomainOrdering.Requests;

namespace Monica.Reference.UI.Features.Orders.Models;

/// <summary>Editable UI draft; backend validation and domain invariants remain authoritative.</summary>
public sealed class CreateOrderForm
{
    public string Number { get; set; } = "";
    public string Customer { get; set; } = "";
    public decimal Total { get; set; } = 1;
    public CommandCreateOrder ToRequest() => new(Number, Customer, Total);
}
