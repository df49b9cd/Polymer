using Google.Protobuf;
using Google.Protobuf.Compiler;
using OmniRelay.Codegen.Protobuf.Core;

namespace OmniRelay.Codegen.Protobuf;

internal static class Program
{
    private static int Main()
    {
        CodeGeneratorRequest request;
        try
        {
            request = CodeGeneratorRequest.Parser.ParseFrom(Console.OpenStandardInput());
        }
        catch (Exception ex)
        {
            var errorResponse = new CodeGeneratorResponse
            {
                Error = $"Failed to parse CodeGeneratorRequest: {ex.Message}"
            };

            using var errorOutput = new CodedOutputStream(Console.OpenStandardOutput());
            errorResponse.WriteTo(errorOutput);
            errorOutput.Flush();
            return 1;
        }

        try
        {
            var generator = new OmniRelayProtobufGenerator();
            var response = OmniRelayProtobufGenerator.Generate(request);
            using var codedOutput = new CodedOutputStream(Console.OpenStandardOutput());
            response.WriteTo(codedOutput);
            codedOutput.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            var errorResponse = new CodeGeneratorResponse
            {
                Error = $"Generation failed: {ex.Message}"
            };

            using var errorOutput = new CodedOutputStream(Console.OpenStandardOutput());
            errorResponse.WriteTo(errorOutput);
            errorOutput.Flush();
            return 1;
        }
    }
}
