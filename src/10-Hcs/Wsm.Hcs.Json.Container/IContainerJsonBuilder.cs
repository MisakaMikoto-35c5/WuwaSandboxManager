namespace Wsm.Hcs.Json.Container;

/// <summary>
/// Builds the HCS JSON configuration document for a Windows container compute
/// system. The document follows schema 2.1 and is accepted by
/// <c>HcsCreateComputeSystem</c> (system type <c>Container</c>).
/// </summary>
public interface IContainerJsonBuilder
{
    /// <summary>Builds the container HCS configuration for the given definition.</summary>
    string Build(ContainerDefinition definition);
}
