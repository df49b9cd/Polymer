namespace OmniRelay.Diagnostics.Alerting;

public interface IAlertChannel
{
    string Name { get; }

    ValueTask SendAsync(AlertEvent alert, CancellationToken cancellationToken);
}
