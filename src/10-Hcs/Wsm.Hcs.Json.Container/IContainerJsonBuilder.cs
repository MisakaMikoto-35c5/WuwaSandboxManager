namespace Wsm.Hcs.Json.Container;

/// <summary>
/// Builds the HCS JSON configuration document for a Windows container compute
/// system. <b>Reserved / not implemented yet.</b>
/// </summary>
public interface IContainerJsonBuilder
{
    /// <summary>Builds the container HCS configuration for the given definition.</summary>
    string Build(ContainerDefinition definition);
}
