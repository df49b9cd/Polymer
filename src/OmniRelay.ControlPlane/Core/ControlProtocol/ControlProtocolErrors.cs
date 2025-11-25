using Hugo;
using OmniRelay.Protos.Control;

namespace OmniRelay.ControlPlane.ControlProtocol;

internal static class ControlProtocolErrors
{
    internal const string UnsupportedCapabilityCode = "control.unsupported_capability";
    internal const string InvalidResumeTokenCode = "control.invalid_resume_token";
    internal const string UpdateStreamDisposedCode = "control.update_stream.disposed";

    internal static Error UnsupportedCapabilities(IEnumerable<string> unsupported, CapabilitySet? provided)
    {
        var missing = string.Join(',', unsupported);
        var advertised = provided?.Items is null or { Count: 0 }
            ? "<none>"
            : string.Join(',', provided.Items);

        return Error.From(
            $"Capabilities not supported: {missing}",
            UnsupportedCapabilityCode)
            .WithMetadata("unsupported", missing)
            .WithMetadata("advertised", advertised)
            .WithMetadata("remediation", "Drop unsupported capabilities or upgrade the control-plane to match the advertised set.");
    }

    internal static Error MissingRequiredCapabilities(IEnumerable<string> required, CapabilitySet? provided)
    {
        var missing = string.Join(',', required);
        var advertised = provided?.Items is null or { Count: 0 }
            ? "<none>"
            : string.Join(',', provided.Items);

        return Error.From(
            $"Client missing required capabilities: {missing}",
            UnsupportedCapabilityCode)
            .WithMetadata("required", missing)
            .WithMetadata("advertised", advertised)
            .WithMetadata("remediation", "Upgrade the agent to support the required capabilities or request a down-leveled payload.");
    }

    internal static Error InvalidResumeToken(WatchResumeToken token, ControlPlaneUpdate current)
    {
        return Error.From(
            $"Resume token {{version={token.Version}, epoch={token.Epoch}}} does not match current snapshot {{version={current.Version}, epoch={current.Epoch}}}",
            InvalidResumeTokenCode)
            .WithMetadata("resume.version", token.Version)
            .WithMetadata("resume.epoch", token.Epoch)
            .WithMetadata("current.version", current.Version)
            .WithMetadata("current.epoch", current.Epoch);
    }
}
