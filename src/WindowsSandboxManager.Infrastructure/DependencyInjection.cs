using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WindowsSandboxManager.Domain.Abstractions;
using WindowsSandboxManager.Infrastructure.Connectors;
using WindowsSandboxManager.Infrastructure.Environment;
using WindowsSandboxManager.Infrastructure.Grpc;
using WindowsSandboxManager.Infrastructure.Hcs;
using WindowsSandboxManager.Infrastructure.Repositories;
using WindowsSandboxManager.Infrastructure.Serialization;

namespace WindowsSandboxManager.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the infrastructure adapters. The <b>HCS backend is the primary
    /// control plane</b> (multi-instance, CPU control, custom storage/base-image
    /// locations, checkpoints) and is used automatically when the process is
    /// elevated. The gRPC <c>WindowsSandboxServer</c> backend is registered as a
    /// fallback for unelevated use.
    /// </summary>
    public static IServiceCollection AddSandboxInfrastructure(
        this IServiceCollection services,
        string? dataDirectory = null)
    {
        services.TryAddSingleton<ISandboxEnvironment, WindowsSandboxEnvironment>();
        services.TryAddSingleton<ISandboxRecipeSerializer, WsbRecipeXmlSerializer>();

        // --- HCS (primary) ---
        services.TryAddSingleton<HcsHostLayout>();
        services.TryAddSingleton<HcsService>();
        services.TryAddSingleton<HcsStorageManager>();
        services.TryAddSingleton<HcsCheckpointManager>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISandboxBackend, HcsSandboxBackend>());

        // --- gRPC control plane (fallback) ---
        services.TryAddSingleton<WindowsSandboxGrpcChannelFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISandboxBackend, WindowsSandboxServerBackend>());

        services.TryAddSingleton<ISandboxSessionConnector, WindowsSandboxSessionConnector>();
        services.TryAddSingleton<ISandboxRepository>(sp =>
            new FileSandboxRepository(sp.GetRequiredService<ISandboxRecipeSerializer>(), dataDirectory));

        return services;
    }
}
