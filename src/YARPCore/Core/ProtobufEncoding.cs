namespace YARPCore.Core;

internal static class ProtobufEncoding
{
    public const string Protobuf = "protobuf";
    public const string ApplicationProtobuf = "application/x-protobuf";
    public const string ApplicationProtobufAlt = "application/protobuf";
    public const string ApplicationGrpc = "application/grpc";
    public const string ApplicationGrpcProto = "application/grpc+proto";
    public const string Json = "json";
    public const string ApplicationJson = "application/json";

    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static bool IsBinary(string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
        {
            return false;
        }

        return Comparer.Equals(encoding, Protobuf) ||
               Comparer.Equals(encoding, ApplicationProtobuf) ||
               Comparer.Equals(encoding, ApplicationProtobufAlt) ||
               Comparer.Equals(encoding, ApplicationGrpc) ||
               Comparer.Equals(encoding, ApplicationGrpcProto);
    }

    public static bool IsJson(string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
        {
            return false;
        }

        return Comparer.Equals(encoding, Json) ||
               Comparer.Equals(encoding, ApplicationJson);
    }

    public static string? GetMediaType(string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
        {
            return null;
        }

        if (IsBinary(encoding))
        {
            return ApplicationProtobuf;
        }

        if (IsJson(encoding))
        {
            return ApplicationJson;
        }

        return encoding;
    }

    public static string? Normalize(string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
        {
            return encoding;
        }

        if (IsBinary(encoding))
        {
            return Protobuf;
        }

        if (IsJson(encoding))
        {
            return ApplicationJson;
        }

        return encoding;
    }
}
