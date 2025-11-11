namespace OmniRelay.Samples.ResourceLease.MeshDemo;

[Flags]
internal enum MeshDemoRole
{
    None = 0,
    Dispatcher = 1 << 0,
    Seeder = 1 << 1,
    Worker = 1 << 2,
    Diagnostics = 1 << 3
}

internal static class MeshDemoRoleExtensions
{
    public const MeshDemoRole DefaultRoles =
        MeshDemoRole.Dispatcher | MeshDemoRole.Seeder | MeshDemoRole.Worker | MeshDemoRole.Diagnostics;

    public static MeshDemoRole ResolveRoles(IEnumerable<string>? roles)
    {
        if (roles is null)
        {
            return DefaultRoles;
        }

        var resolved = MeshDemoRole.None;
        foreach (var role in roles)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            var segments = role.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                segments = [role];
            }

            foreach (var segment in segments)
            {
                var normalized = segment.Trim();
                if (TryParse(normalized, out var parsed))
                {
                    resolved |= parsed;
                }
            }
        }

        return resolved == MeshDemoRole.None ? DefaultRoles : resolved;
    }

    public static bool HasRole(this MeshDemoRole roles, MeshDemoRole role) =>
        (roles & role) == role;

    private static bool TryParse(string value, out MeshDemoRole role)
    {
        if (Enum.TryParse<MeshDemoRole>(value, ignoreCase: true, out var parsed))
        {
            role = parsed;
            return true;
        }

        switch (value.ToLowerInvariant())
        {
            case "diag":
            case "diagnostic":
            case "diagnostics":
                role = MeshDemoRole.Diagnostics;
                return true;
            default:
                role = MeshDemoRole.None;
                return false;
        }
    }
}
