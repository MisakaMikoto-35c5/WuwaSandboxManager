using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Host.Elevation;
using Wsm.Service.Client.Internal;
using Xunit;

namespace Wsm.Tests.Service;

/// <summary>
/// Tests for <see cref="StandaloneAgentStarter"/>: the on-demand spawner behind
/// <c>IWsmServiceClient.EnsureAvailableAsync</c>. Process spawning and the exe
/// lookup are hooked, so no real agent is started.
/// </summary>
public sealed class StandaloneAgentStarterTests
{
    [Fact]
    public async Task EnsureRunning_returns_true_without_spawning_when_agent_already_answers()
    {
        var starter = CreateStarter(elevated: true);
        starter.ProcessStarter = _ => throw new InvalidOperationException("must not spawn");

        var result = await starter.EnsureRunningAsync(_ => Task.FromResult(true), null);

        Assert.True(result);
    }

    [Fact]
    public async Task EnsureRunning_spawns_agent_and_waits_until_the_pipe_answers()
    {
        var starter = CreateStarter(elevated: true);
        var spawned = new List<ProcessStartInfo>();
        var booted = false;
        starter.ProcessStarter = startInfo =>
        {
            spawned.Add(startInfo);
            booted = true;
            return Process.GetCurrentProcess(); // a running, never-exiting stand-in
        };

        var result = await starter.EnsureRunningAsync(_ => Task.FromResult(booted), @"C:\data");

        Assert.True(result);
        var startInfo = Assert.Single(spawned);
        Assert.Equal("fake-host.exe", startInfo.FileName);
        Assert.Contains("--console", startInfo.ArgumentList);
        Assert.Contains("--data", startInfo.ArgumentList);
        Assert.Contains(@"C:\data", startInfo.ArgumentList);
        // ShellExecute (no handle inheritance) + hidden window: see the
        // ProcessStartInfo comment in the starter.
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.False(startInfo.RedirectStandardOutput);
    }

    [Fact]
    public async Task EnsureRunning_does_not_spawn_when_not_elevated()
    {
        var starter = CreateStarter(elevated: false);
        var spawnCount = 0;
        starter.ProcessStarter = _ =>
        {
            spawnCount++;
            return Process.GetCurrentProcess();
        };

        var result = await starter.EnsureRunningAsync(_ => Task.FromResult(false), null);

        Assert.False(result);
        Assert.Equal(0, spawnCount);
    }

    [Fact]
    public async Task EnsureRunning_returns_false_when_host_exe_is_not_found()
    {
        var starter = CreateStarter(elevated: true);
        starter.ExeLocator = () => null;
        starter.ProcessStarter = _ => throw new InvalidOperationException("must not spawn");

        var result = await starter.EnsureRunningAsync(_ => Task.FromResult(false), null);

        Assert.False(result);
    }

    [Fact]
    public async Task EnsureRunning_attempts_a_spawn_only_once_per_process()
    {
        var starter = CreateStarter(elevated: true);
        var spawnCount = 0;
        starter.ProcessStarter = _ =>
        {
            spawnCount++;
            return null; // Process.Start failed
        };

        var first = await starter.EnsureRunningAsync(_ => Task.FromResult(false), null);
        var second = await starter.EnsureRunningAsync(_ => Task.FromResult(false), null);

        Assert.False(first);
        Assert.False(second);
        Assert.Equal(1, spawnCount);
    }

    [Fact]
    public async Task EnsureRunning_returns_false_when_the_spawned_agent_exits_during_boot()
    {
        var starter = CreateStarter(elevated: true);
        starter.BootTimeout = TimeSpan.FromSeconds(5);
        starter.PollInterval = TimeSpan.FromMilliseconds(20);
        starter.ProcessStarter = startInfo =>
        {
            var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            process.WaitForExit();
            Assert.Equal("fake-host.exe", startInfo.FileName);
            return process;
        };

        var result = await starter.EnsureRunningAsync(_ => Task.FromResult(false), null);

        Assert.False(result);
    }

    private static StandaloneAgentStarter CreateStarter(bool elevated)
        => new(new FakeElevationService(elevated), NullLogger<StandaloneAgentStarter>.Instance)
        {
            ExeLocator = () => "fake-host.exe",
            BootTimeout = TimeSpan.FromSeconds(2),
            PollInterval = TimeSpan.FromMilliseconds(20),
        };

    private sealed class FakeElevationService(bool elevated) : IElevationService
    {
        public bool IsElevated => elevated;

        public bool TryRelaunchElevated(string? arguments = null) => false;
    }
}
