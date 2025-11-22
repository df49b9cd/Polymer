# AOT/Perf Banned APIs

These APIs are disallowed in OmniRelay to keep Native AOT builds trim-friendly and avoid runtime codegen/late binding.

## Banned list (checked in build)
- `System.Reflection.Assembly.Load*`
- `System.Type.GetType`
- `System.Activator.CreateInstance`
- `System.Reflection.Emit.*`
- `System.Reflection.MethodInfo.Invoke`
- `System.Runtime.Serialization.Formatters.Binary.BinaryFormatter`
- `System.Runtime.Remoting.*`
- `System.AppDomain.*`
- `System.Dynamic.ExpandoObject`
- `Microsoft.CSharp.CSharpCodeProvider`
- `System.Linq.Expressions.Expression.Compile`

## How enforcement works
- Build runs `eng/check-banned-apis.sh` (wired via `CheckBannedApis` target in `Directory.Build.targets`).
- The script scans `src/` and `tests/` excluding `bin/`, `obj/`, and `artifacts/`.
- Set `SkipBannedApiCheck=true` to bypass (discouraged; add justification in PR if used).

## Suppressions / Exceptions
- If a legitimate use is required, prefer adding an abstraction in a single, audited location and propose a narrower allowlist rather than suppressing the check.
- If bypassing temporarily, document the rationale in the PR and add a TODO to remove.

## Related guidance
See `docs/knowledge-base/dotnet-performance-guidelines.md` for broader AOT/perf practices (no reflection in hot paths, pooled buffers, avoiding JIT-only features).
