namespace DistributedDemo.Contracts;

public sealed record CheckoutRequest(string OrderId, string Sku, int Quantity, string Currency);

public sealed record CheckoutResponse(string OrderId, bool Reserved, string ConfirmationId, string Message);

public sealed record AuditRecord(string OrderId, string EventType, string Details, DateTimeOffset OccurredAt);
