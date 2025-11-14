using System;
using System.Collections.Generic;

namespace OmniRelay.Configuration.Models;

/// <summary>Generic TLS source definition used by control-plane hosts.</summary>
public sealed class TransportTlsConfiguration
{
    public string? CertificatePath { get; set; }

    public string? CertificateData { get; set; }

    public string? CertificatePassword { get; set; }

    public bool? AllowUntrustedCertificates { get; set; }

    public bool? CheckCertificateRevocation { get; set; }

    public IList<string> AllowedThumbprints { get; } = new List<string>(StringComparer.OrdinalIgnoreCase);

    public string? ReloadInterval { get; set; }
}
