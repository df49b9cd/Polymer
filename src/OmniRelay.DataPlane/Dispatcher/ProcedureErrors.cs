using Hugo;

namespace OmniRelay.Dispatcher;

/// <summary>Error factories for procedure specs and builders.</summary>
internal static class ProcedureErrors
{
    private const string NameRequiredCode = "dispatcher.procedure.name_required";
    private const string AliasInvalidCode = "dispatcher.procedure.alias_invalid";
    private const string HandlerMissingCode = "dispatcher.procedure.handler_missing";

    public static Error NameRequired() =>
        Error.From("Procedure name cannot be null or whitespace.", NameRequiredCode);

    public static Error AliasInvalid() =>
        Error.From("Procedure aliases cannot contain null or whitespace entries.", AliasInvalidCode);

    public static Error HandlerMissing(string service, string procedure, string kind) =>
        Error.From("Procedure handler must be configured.", HandlerMissingCode)
            .WithMetadata("service", service)
            .WithMetadata("procedure", procedure)
            .WithMetadata("kind", kind);
}
