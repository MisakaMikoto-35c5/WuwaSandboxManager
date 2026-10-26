using System.Globalization;
using System.Reflection;
using Microsoft.UI.Dispatching;
using Wsm.Domain.ValueObjects;
using Wsm.Engine.WindowsContainer;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Storage;
using Wsm.Host.Elevation;
using Wsm.Host.Policy;
using Wsm.Ui.WinUI.Localization;
using Wsm.Ui.WinUI.ViewModels;
using Xunit;

namespace Wsm.Tests.ViewModels;

public sealed class MainViewModelImageDownloadTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wsm-view-model-" + Guid.NewGuid().ToString("N"));

    public MainViewModelImageDownloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task Download_does_not_run_the_importer_synchronously_on_the_ui_thread()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importerThread = -1;
        var importer = new StubImporter((_, _, _, _) =>
        {
            importerThread = Environment.CurrentManagedThreadId;
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            throw new HttpRequestException("stop after the probe");
        });
        await using var ui = await UiContext.Create(importer);
        ConfigureTarget(ui.ViewModel, Path.Combine(_root, "image"));

        var start = ui.StartOnUiThread(() => ui.ViewModel.DownloadContainerImageCommand.ExecuteAsync(null));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var completed = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.Same(start, completed);
            Assert.NotEqual(ui.UiThreadId, importerThread);
        }
        finally
        {
            release.TrySetResult();
            var command = await start.WaitAsync(Timeout);
            await command.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Redownload_failure_preserves_the_installed_image()
    {
        var destination = CreatePreparedImage("old");
        var importer = new StubImporter((_, _, _, _)
            => Task.FromException<string>(new HttpRequestException("download failed")));
        await using var ui = await UiContext.Create(importer);
        ConfigureTarget(ui.ViewModel, destination);

        var command = await ui.StartOnUiThread(
            () => ui.ViewModel.RedownloadContainerImageCommand.ExecuteAsync(null));
        await command.WaitAsync(Timeout);

        Assert.Equal("old", File.ReadAllText(Path.Combine(destination, "owner.txt")));
        Assert.Equal(ContainerImageInstallState.Installed, ui.ViewModel.ContainerImageInstallState);
    }

    [Fact]
    public async Task Redownload_cancellation_preserves_the_installed_image()
    {
        var destination = CreatePreparedImage("old");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importer = new StubImporter(async (_, _, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(-1, ct);
            return "unreachable";
        });
        await using var ui = await UiContext.Create(importer);
        ConfigureTarget(ui.ViewModel, destination);

        var command = await ui.StartOnUiThread(
            () => ui.ViewModel.RedownloadContainerImageCommand.ExecuteAsync(null));
        await entered.Task.WaitAsync(Timeout);
        var cancelled = await ui.StartOnUiThread(() =>
        {
            ui.ViewModel.CancelContainerImageDownloadCommand.Execute(null);
            return Task.CompletedTask;
        });
        await cancelled;
        await command.WaitAsync(Timeout);

        Assert.Equal("old", File.ReadAllText(Path.Combine(destination, "owner.txt")));
        Assert.Equal(ContainerImageInstallState.Installed, ui.ViewModel.ContainerImageInstallState);
    }

    [Fact]
    public void Replacement_moves_staging_into_place_and_removes_the_owned_backup()
    {
        var destination = CreatePreparedImage("old");
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "owner.txt"), "new");

        InvokeReplacement(staging, destination);

        Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "owner.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Replacement_restores_the_old_image_when_moving_staging_fails()
    {
        var destination = CreatePreparedImage("old");
        var missingStaging = Path.Combine(_root, "missing-staging");

        Assert.Throws<DirectoryNotFoundException>(() => InvokeReplacement(missingStaging, destination));

        Assert.Equal("old", File.ReadAllText(Path.Combine(destination, "owner.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    private static void ConfigureTarget(MainViewModel viewModel, string destination)
    {
        viewModel.SelectedContainerImageReference = "mcr.microsoft.com/windows/servercore:test";
        viewModel.ContainerImageFolder = destination;
    }

    private string CreatePreparedImage(string owner)
    {
        var destination = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(destination, "Files", "Windows"));
        Directory.CreateDirectory(Path.Combine(destination, "Hives"));
        File.WriteAllText(Path.Combine(destination, "owner.txt"), owner);
        new ContainerImageMetadata { Build = 20348, IsServer = true, SourceReference = "test" }.Write(destination);
        return destination;
    }

    private static void InvokeReplacement(string staging, string destination)
    {
        var method = typeof(MainViewModel).GetMethod(
            "ReplaceDownloadedImage", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainViewModel), "ReplaceDownloadedImage");
        try
        {
            method.Invoke(null, [staging, destination, CancellationToken.None]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private sealed class StubImporter(
        Func<string, string, IProgress<ContainerImageImportProgress>?, CancellationToken, Task<string>> import)
        : IContainerImageImporter
    {
        public IReadOnlyList<string> SuggestedImages => [];
        public IReadOnlyList<ContainerImageInfo> ImageCatalog => [];

        public Task<string> ImportBaseLayerAsync(
            string reference,
            string layerPath,
            IProgress<ContainerImageImportProgress>? progress = null,
            CancellationToken ct = default)
            => import(reference, layerPath, progress, ct);
    }

    private sealed class EmptyRegistry : ISandboxEngineRegistry
    {
        public IReadOnlyList<ISandboxEngine> Engines => [];
        public ISandboxEngine? Find(SandboxEngineKind kind) => null;
    }

    private sealed class StubElevation : IElevationService
    {
        public bool IsElevated => true;
        public bool TryRelaunchElevated(string? arguments = null) => false;
    }

    private sealed class StubMultiInstancePolicy : IMultiInstancePolicy
    {
        public string? CurrentBuildBranch => null;
        public bool AllowsMultipleInstances => false;

        public bool TryEnable(out string message)
        {
            message = string.Empty;
            return false;
        }

        public bool TryRestore(string? originalValue, out string message)
        {
            message = string.Empty;
            return false;
        }
    }

    private sealed class StubIsolation : IContainerIsolationPolicy
    {
        public ContainerIsolationSupport Evaluate() => new()
        {
            IsSupported = false,
            ReasonKey = "Container.Reason.NoImage",
            HostBuild = 26300,
            HostIsWindows11Client = true,
        };

        public ContainerIsolationSupport Evaluate(string? imagePath) => Evaluate();
        public void Invalidate() { }
    }

    private sealed class StubLocalizer : IAppLocalizer
    {
        public string this[string key] => key;
        public string Get(string key, params object[] args) => key;
        public CultureInfo CurrentCulture { get; } = CultureInfo.GetCultureInfo("en-US");
        public event EventHandler? CultureChanged { add { } remove { } }
        public Task SetCultureAsync(string cultureName) => Task.CompletedTask;
    }

    private sealed class UiContext : IAsyncDisposable
    {
        private static readonly Lazy<bool> WindowsAppRuntime = new(() =>
        {
            global::Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.Initialize(0x00020003);
            return true;
        });

        private readonly DispatcherQueueController _controller;

        private UiContext(DispatcherQueueController controller, MainViewModel viewModel, int uiThreadId)
        {
            _controller = controller;
            ViewModel = viewModel;
            UiThreadId = uiThreadId;
        }

        public MainViewModel ViewModel { get; }
        public int UiThreadId { get; }

        public static async Task<UiContext> Create(IContainerImageImporter importer)
        {
            if (!WindowsAppRuntime.Value)
            {
                throw new InvalidOperationException("Could not initialize the Windows App Runtime test host.");
            }

            var controller = DispatcherQueueController.CreateOnDedicatedThread();
            var created = new TaskCompletionSource<MainViewModel>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var uiThreadId = -1;
            try
            {
                if (!controller.DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            uiThreadId = Environment.CurrentManagedThreadId;
                            created.SetResult(new MainViewModel(
                                null!,
                                new EmptyRegistry(),
                                new StubMultiInstancePolicy(),
                                new StubElevation(),
                                new StubLocalizer(),
                                importer,
                                new StubIsolation(),
                                null!));
                        }
                        catch (Exception ex)
                        {
                            created.SetException(ex);
                        }
                    }))
                {
                    throw new InvalidOperationException("Could not enqueue view-model construction.");
                }

                var viewModel = await created.Task.WaitAsync(Timeout);
                return new UiContext(controller, viewModel, uiThreadId);
            }
            catch
            {
                await controller.ShutdownQueueAsync();
                throw;
            }
        }

        public Task<Task> StartOnUiThread(Func<Task> action)
        {
            var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_controller.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        started.SetResult(action());
                    }
                    catch (Exception ex)
                    {
                        started.SetException(ex);
                    }
                }))
            {
                started.SetException(new InvalidOperationException("Could not enqueue the command."));
            }

            return started.Task;
        }

        public async ValueTask DisposeAsync() => await _controller.ShutdownQueueAsync();
    }
}
