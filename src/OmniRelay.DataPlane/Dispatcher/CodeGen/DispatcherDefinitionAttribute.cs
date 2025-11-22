namespace OmniRelay.Dispatcher.CodeGen;

/// <summary>
/// Declares that a partial class represents a dispatcher definition for code-first generation.
/// The associated source generator will inject a factory method that builds a <see cref="Dispatcher"/>
/// using a supplied <see cref="DispatcherOptions"/> instance without relying on reflection.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DispatcherDefinitionAttribute : Attribute
{
    /// <summary>Initializes the attribute with the service name used to construct <see cref="DispatcherOptions"/>.</summary>
    /// <param name="serviceName">Logical service name for the dispatcher.</param>
    public DispatcherDefinitionAttribute(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("Service name cannot be null or whitespace.", nameof(serviceName));
        }

        ServiceName = serviceName;
    }

    /// <summary>The logical service name used when instantiating <see cref="DispatcherOptions"/>.</summary>
    public string ServiceName { get; }
}
