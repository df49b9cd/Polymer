using System.Runtime.CompilerServices;
using Xunit;

namespace OmniRelay.TestSupport;

public sealed class Http3FactAttribute : FactAttribute
{
    private const string SkipMessage = "Set OMNIRELAY_ENABLE_HTTP3_TESTS=true to run HTTP/3 scenarios.";

    public Http3FactAttribute([CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!Http3TestHelper.IsHttp3Enabled)
        {
            Skip = SkipMessage;
        }
    }
}
