namespace OmniRelay.Diagnostics.Alerting;

/// <summary>Abstracts publishing of alerts to configured channels.</summary>
public interface IAlertPublisher
{
    ValueTask PublishAsync(AlertEvent alert, CancellationToken cancellationToken = default);
}
