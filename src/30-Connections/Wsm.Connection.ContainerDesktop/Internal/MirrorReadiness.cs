namespace Wsm.Connection.ContainerDesktop.Internal;

internal enum MirrorReadinessState
{
    Unavailable,
    UnknownHealth,
    Stale,
    NoPixels,
    Error,
    Live,
}

internal readonly record struct MirrorReadinessResult(
    MirrorReadinessState State,
    ContainerDesktopHeader? Header);

internal static class MirrorReadiness
{
    private static TimeSpan HeartbeatIntervalFor(int fps)
        => TimeSpan.FromMilliseconds(Math.Clamp(2000.0 / Math.Max(1, fps), 125, 2000));

    public static async Task<MirrorReadinessResult> ProbeAsync(
        string mirrorPath, CancellationToken cancellationToken = default)
    {
        if (ContainerDesktopMirror.TryReadHeaderInfo(mirrorPath) is not { } firstHeader)
        {
            return new MirrorReadinessResult(MirrorReadinessState.Unavailable, null);
        }

        var firstStatus = MirrorCaptureStatus.TryRead(mirrorPath);
        if (firstStatus is { } status && status.WriterPid != firstHeader.WriterPid)
        {
            return new MirrorReadinessResult(MirrorReadinessState.Stale, firstHeader);
        }

        if (firstStatus?.Health == ContainerDesktopCaptureHealth.NoPixels)
        {
            return new MirrorReadinessResult(MirrorReadinessState.NoPixels, firstHeader);
        }

        if (firstStatus?.Health == ContainerDesktopCaptureHealth.Error)
        {
            return new MirrorReadinessResult(MirrorReadinessState.Error, firstHeader);
        }

        if (firstStatus is null && firstHeader.Mode == ContainerDesktopMode.Pagefile)
        {
            return new MirrorReadinessResult(MirrorReadinessState.UnknownHealth, firstHeader);
        }

        var firstSequence = firstStatus?.Sequence ?? firstHeader.Sequence;
        await Task.Delay(HeartbeatIntervalFor(firstHeader.Geometry.Fps), cancellationToken).ConfigureAwait(false);

        if (ContainerDesktopMirror.TryReadHeaderInfo(mirrorPath) is not { } secondHeader)
        {
            return new MirrorReadinessResult(MirrorReadinessState.Unavailable, null);
        }

        var secondStatus = MirrorCaptureStatus.TryRead(mirrorPath);
        if (secondHeader.WriterPid != firstHeader.WriterPid
            || secondStatus is { } latest && latest.WriterPid != secondHeader.WriterPid)
        {
            return new MirrorReadinessResult(MirrorReadinessState.Stale, secondHeader);
        }

        if (secondStatus?.Health == ContainerDesktopCaptureHealth.NoPixels)
        {
            return new MirrorReadinessResult(MirrorReadinessState.NoPixels, secondHeader);
        }

        if (secondStatus?.Health == ContainerDesktopCaptureHealth.Error)
        {
            return new MirrorReadinessResult(MirrorReadinessState.Error, secondHeader);
        }

        var secondSequence = secondStatus?.Sequence ?? secondHeader.Sequence;
        if (secondSequence <= firstSequence
            || firstStatus is { } original && secondStatus?.WriterPid != original.WriterPid)
        {
            return new MirrorReadinessResult(MirrorReadinessState.Stale, secondHeader);
        }

        if (secondStatus is null && secondHeader.Mode == ContainerDesktopMode.Pagefile)
        {
            return new MirrorReadinessResult(MirrorReadinessState.UnknownHealth, secondHeader);
        }

        return secondStatus?.Health switch
        {
            ContainerDesktopCaptureHealth.Live => new MirrorReadinessResult(MirrorReadinessState.Live, secondHeader),
            ContainerDesktopCaptureHealth.Stopped => new MirrorReadinessResult(MirrorReadinessState.Stale, secondHeader),
            _ => new MirrorReadinessResult(MirrorReadinessState.UnknownHealth, secondHeader),
        };
    }
}
