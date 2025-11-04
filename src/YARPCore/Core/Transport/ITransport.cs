namespace YARPCore.Core.Transport;

public interface ITransport : ILifecycle
{
    string Name { get; }
}
