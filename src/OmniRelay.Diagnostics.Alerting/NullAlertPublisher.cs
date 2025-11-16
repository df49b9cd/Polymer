namespace OmniRelay.Diagnostics.Alerting;

public sealed class NullAlertPublisher : IAlertPublisher
{
    public ValueTask PublishAsync(AlertEvent alert, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
