using Google.Protobuf;
using Google.Protobuf.Compiler;
using Polymer.Codegen.Protobuf.Core;

namespace Polymer.Codegen.Protobuf;

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
            var generator = new PolymerProtobufGenerator();
            var response = PolymerProtobufGenerator.Generate(request);
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
