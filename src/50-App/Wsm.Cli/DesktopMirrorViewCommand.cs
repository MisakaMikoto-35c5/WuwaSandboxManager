using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Wsm.Connection.ContainerDesktop;
using Wsm.Connection.ContainerDesktop.Internal;

namespace Wsm.Cli;

/// <summary>
/// <c>wsm desktop-view --mode windows --frame &lt;mirror.shm&gt;</c>: the
/// <b>window mirror</b> display side (doc sections 18-19).
/// <para>
/// It reads the shared mirror file the in-container read side publishes and creates
/// <b>one real host window per container window</b> - same rectangle, same title, same
/// z-order - showing that window's pixels. Because these are ordinary windows in the
/// user's session, the user's own DWM composes them and they appear in the taskbar and
/// Alt-Tab like native windows.
/// </para>
/// <para>
/// Input goes back through the same one-file-per-command queue the read side polls:
/// clicking forwards a click at the container-desktop coordinates, and <b>moving a
/// mirrored window by its caption sends <c>movewindow &lt;hwnd&gt; x,y</c></b>, which the
/// read side applies with <c>SetWindowPos</c> - so dragging on the host really moves
/// the window inside the container. Resizing likewise sends <c>resize</c>, the close
/// button sends <c>close</c>, and keys go as structured <c>key</c> events when the
/// read side publishes version 2 of the mirror protocol.
/// </para>
/// </summary>
internal static class DesktopMirrorViewCommand
{
    /// <summary>
    /// <c>--print-table</c>: prints a live, pixel-producing capture and its window table.
    /// A header without versioned health or an advancing heartbeat is not success.
    /// </summary>
    public static int PrintTable(string mirrorPath, int waitSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, waitSeconds));
        do
        {
            var readiness = MirrorReadiness.ProbeAsync(mirrorPath).GetAwaiter().GetResult();
            if (readiness.State == MirrorReadinessState.Live)
            {
                var fileHeader = readiness.Header!.Value;
                var header = fileHeader;
                var windows = Array.Empty<ContainerDesktopWindow>() as IReadOnlyList<ContainerDesktopWindow>;
                if (fileHeader.Mode == ContainerDesktopMode.Pagefile)
                {
                    try
                    {
                        using var source = SharedSectionSource.Open(mirrorPath, ContainerDesktopMirror.Magic,
                            mapped => ContainerDesktopMirror.TryReadHeaderInfo(
                                    mapped, ContainerDesktopFrame.HeaderFileCapacity) is { } parsed
                                ? ContainerDesktopMirror.Capacity(parsed.Geometry, parsed.Version)
                                : 0);
                        header = ContainerDesktopMirror.TryReadHeaderInfo(source.BaseAddress, source.Length)
                            ?? fileHeader;
                        windows = ContainerDesktopMirror.ReadWindows(
                            source.BaseAddress, source.Length, header.Geometry, header.WindowCount, header.Version);
                    }
                    catch (IOException ex)
                    {
                        Console.Error.WriteLine($"The mirror pixels are unavailable: {ex.Message}");
                        return 1;
                    }
                }
                else
                {
                    windows = ContainerDesktopMirror.ReadWindows(
                        mirrorPath, header.Geometry, header.WindowCount, header.Version);
                }

                if (windows.Count == 0)
                {
                    Console.Error.WriteLine("The mirror is not live: its window table is empty or unreadable.");
                    return 1;
                }

                var geometry = header.Geometry;
                Console.WriteLine($"Mirror   : {mirrorPath}");
                Console.WriteLine($"Geometry : {geometry.Width}x{geometry.Height} stride={geometry.Stride} fps={geometry.Fps}");
                Console.WriteLine($"Sequence : {header.Sequence} (protocol v{header.Version}, dirty={header.DirtyCount})");
                Console.WriteLine($"Mode     : {header.Mode.ToString().ToLowerInvariant()} (writer pid {header.WriterPid})");
                Console.WriteLine($"Windows  : {windows.Count}");
                foreach (var window in windows)
                {
                    Console.WriteLine(
                        $"  - id=0x{window.Id:X} rect=({window.X},{window.Y} {window.Width}x{window.Height}) " +
                        $"seq={window.Sequence} \"{window.Title}\"");
                }

                return 0;
            }

            if (readiness.State is MirrorReadinessState.NoPixels or MirrorReadinessState.Error
                or MirrorReadinessState.UnknownHealth || DateTime.UtcNow >= deadline)
            {
                return ReadinessFailure(mirrorPath, readiness.State);
            }

            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < deadline);

        return ReadinessFailure(mirrorPath, MirrorReadinessState.Unavailable);
    }

    private static int ReadinessFailure(string mirrorPath, MirrorReadinessState state)
    {
        var detail = state switch
        {
            MirrorReadinessState.NoPixels => "the writer reports no captured pixels",
            MirrorReadinessState.Error => "the writer reports a capture error",
            MirrorReadinessState.UnknownHealth => "the capture health is unknown",
            MirrorReadinessState.Stale => "the capture heartbeat is not advancing",
            _ => "no window mirror has been published",
        };
        Console.Error.WriteLine($"Window mirror '{mirrorPath}' is not live: {detail}.");
        return 1;
    }

    public static int Run(string mirrorPath, int seconds, string inputPrefix, int offsetX = 0, int offsetY = 0)
    {
        if (!File.Exists(mirrorPath))
        {
            Console.Error.WriteLine($"No mirror buffer at '{mirrorPath}'.");
            return 1;
        }

        var readiness = MirrorReadiness.ProbeAsync(mirrorPath).GetAwaiter().GetResult();
        if (readiness.State != MirrorReadinessState.Live)
        {
            return ReadinessFailure(mirrorPath, readiness.State);
        }

        var exitCode = 0;
        var thread = new Thread(() =>
        {
            try
            {
                using var state = new MirrorState(mirrorPath, inputPrefix, offsetX, offsetY);
                System.Windows.Forms.Application.EnableVisualStyles();
                using var manager = new MirrorManager(state);

                // A hidden owner keeps the message loop alive when there is nothing to show.
                using var owner = new Form
                {
                    Text = "WSM container desktop",
                    ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(-4000, -4000),
                    Size = new System.Drawing.Size(1, 1),
                };

                var timer = new System.Windows.Forms.Timer { Interval = 120 };
                timer.Tick += (_, _) => manager.Sync();
                owner.Shown += (_, _) =>
                {
                    timer.Start();
                    manager.Sync();
                    manager.Log($"viewer started for {state.Path}");
                };

                if (seconds > 0)
                {
                    var close = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
                    close.Tick += (_, _) =>
                    {
                        close.Stop();
                        owner.Close();
                    };
                    owner.Shown += (_, _) => close.Start();
                }

                owner.FormClosed += (_, _) =>
                {
                    timer.Stop();
                    manager.Log("viewer closed");
                };

                System.Windows.Forms.Application.Run(owner);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.STA); // WinForms requires STA
        thread.Start();
        thread.Join();
        return exitCode;
    }

    /// <summary>The mapped mirror source plus the latest header/window table.</summary>
    private sealed class MirrorState : IDisposable
    {
        private SharedSectionSource.Mapping? _source;
        private int _writerPid;

        public MirrorState(string path, string inputPrefix, int offsetX = 0, int offsetY = 0)
        {
            Path = path;
            InputPrefix = inputPrefix;
            OffsetX = offsetX;
            OffsetY = offsetY;
            Directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        }

        public string Path { get; }

        public string InputPrefix { get; }

        public string Directory { get; }

        /// <summary>Horizontal displacement of the mirrored windows on the host screen.</summary>
        public int OffsetX { get; }

        /// <summary>Vertical displacement of the mirrored windows on the host screen.</summary>
        public int OffsetY { get; }

        public ContainerDesktopGeometry Geometry { get; private set; }

        public IntPtr Pixels { get; private set; }

        public long Sequence { get; private set; } = -1;

        /// <summary>Protocol version the connected read side publishes.</summary>
        public int Version { get; private set; } = ContainerDesktopMirror.LegacyVersion;

        /// <summary>Dirty rects of the last frame (empty for version 1 read sides).</summary>
        public IReadOnlyList<ContainerDesktopRect> DirtyRects { get; private set; }
            = Array.Empty<ContainerDesktopRect>();

        /// <summary>Bumped whenever the mapping is recreated, so views over it are rebuilt.</summary>
        public int MapGeneration { get; private set; }

        public IReadOnlyList<ContainerDesktopWindow> Windows { get; private set; }
            = Array.Empty<ContainerDesktopWindow>();

        /// <summary>True when a new frame arrived (or the window set changed).</summary>
        public bool TryRefresh()
        {
            // The header FILE is authoritative for (re)connect decisions in both modes
            // (geometry, mode, writerPid): a restarted read side rewrites it. In pagefile
            // mode its sequence/window count are static, so the live header comes from
            // the mapping afterwards.
            if (ContainerDesktopMirror.TryReadHeaderInfo(Path) is not { } fileHeader)
            {
                return false;
            }

            var geometry = fileHeader.Geometry;
            if (_source is not null
                && (fileHeader.WriterPid != _writerPid
                    || fileHeader.Mode != (_source.IsPassthrough
                        ? ContainerDesktopMode.Pagefile : ContainerDesktopMode.File)
                    || geometry.Width != Geometry.Width || geometry.Height != Geometry.Height))
            {
                Release(); // a new writer, backing mode or geometry: remap below
            }

            if (_source is null)
            {
                Map(fileHeader);
            }

            var source = _source ?? throw new IOException("The mirror source could not be mapped.");
            var header = source.IsPassthrough
                ? ContainerDesktopMirror.TryReadHeaderInfo(source.BaseAddress, source.Length) ?? fileHeader
                : fileHeader;

            Geometry = geometry;
            Version = header.Version;
            if (header.Sequence == Sequence && Windows.Count == header.WindowCount)
            {
                return false;
            }

            Sequence = header.Sequence;
            Windows = source.IsPassthrough
                ? ContainerDesktopMirror.ReadWindows(
                    source.BaseAddress, source.Length, geometry, header.WindowCount, header.Version)
                : ContainerDesktopMirror.ReadWindows(Path, geometry, header.WindowCount, header.Version);
            DirtyRects = header.Version >= ContainerDesktopMirror.CurrentVersion
                ? source.IsPassthrough
                    ? ContainerDesktopMirror.ReadDirtyRects(source.BaseAddress, source.Length, geometry, header.DirtyCount)
                    : ContainerDesktopMirror.ReadDirtyRects(Path, geometry, header.DirtyCount)
                : Array.Empty<ContainerDesktopRect>();
            return true;
        }

        private void Map(ContainerDesktopHeader header)
        {
            _source = SharedSectionSource.Open(Path, ContainerDesktopMirror.Magic,
                mappedHeader => ContainerDesktopMirror.TryReadHeaderInfo(
                        mappedHeader, ContainerDesktopFrame.HeaderFileCapacity) is { } parsed
                    ? ContainerDesktopMirror.Capacity(parsed.Geometry, parsed.Version)
                    : 0);
            _writerPid = header.WriterPid;
            Pixels = IntPtr.Add(_source.BaseAddress, ContainerDesktopMirror.PixelOffset);
            MapGeneration++;
        }

        private void Release()
        {
            _source?.Dispose();
            _source = null;
            _writerPid = 0;
            Pixels = IntPtr.Zero;
        }

        public void Dispose() => Release();

        /// <summary>Writes one input command into the queue the read side polls.</summary>
        public void Send(string command)
        {
            try
            {
                var name = InputPrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                    + ".txt";
                File.WriteAllText(System.IO.Path.Combine(Directory, name), command, new UTF8Encoding(false));
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Keeps one host window per container window in sync.</summary>
    private sealed class MirrorManager : IDisposable
    {
        private readonly MirrorState _state;
        private readonly Dictionary<long, MirrorWindow> _windows = new();
        private readonly StreamWriter? _log;

        public MirrorManager(MirrorState state)
        {
            _state = state;
            try
            {
                _log = new StreamWriter(
                    System.IO.Path.Combine(state.Directory, "mirror-view.log"), append: false) { AutoFlush = true };
            }
            catch (IOException)
            {
            }
        }

        public void Log(string message)
        {
            try
            {
                _log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
            }
            catch (IOException)
            {
            }
        }

        public void Sync()
        {
            if (!_state.TryRefresh())
            {
                return;
            }

            var windows = _state.Windows;

            foreach (var gone in _windows.Keys.Where(id => windows.All(w => w.Id != id)).ToArray())
            {
                _windows[gone].Close();
                _windows.Remove(gone);
            }

            foreach (var window in windows)
            {
                if (_windows.TryGetValue(window.Id, out var mirror))
                {
                    mirror.Update(window, _state);
                }
                else
                {
                    _windows[window.Id] = new MirrorWindow(_state, window, this);
                }
            }

            // The container's table is top-first, so restack bottom-up.
            for (var i = windows.Count - 1; i >= 0; i--)
            {
                if (_windows.TryGetValue(windows[i].Id, out var mirror))
                {
                    mirror.BringToFront();
                }
            }

            Log($"sync seq={_state.Sequence} windows={windows.Count} visible={_windows.Count}");
        }

        public void Dispose()
        {
            foreach (var window in _windows.Values)
            {
                window.Close();
            }

            _windows.Clear();
            _log?.Dispose();
        }
    }

    /// <summary>
    /// Marker a mirror window carries so a read side never mirrors a mirror - which
    /// would otherwise cascade when both halves run on one desktop (an artefact of
    /// testing, but cheap to make impossible). A window property is used instead of a
    /// custom window class, because overriding a WinForms class name silently breaks
    /// window creation.
    /// </summary>
    internal static class MirrorMarker
    {
        /// <summary>Name of the window property every mirror window carries.</summary>
        public const string PropertyName = "WSM_MIRROR_HOST";

        /// <summary>Marks a window as a mirror window.</summary>
        public static void Set(IntPtr hwnd)
            => _ = SetPropW(hwnd, PropertyName, new IntPtr(1));

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetPropW(IntPtr hwnd, string name, IntPtr value);
    }

    /// <summary>
    /// One mirrored container window: a normal sizable host window whose client area shows
    /// the container window's pixels through a zero-copy view into the shared frame. The OS
    /// supplies the real chrome - dragging the caption moves the host window (and sends
    /// <c>movewindow</c> to the container), dragging the border sends <c>resize</c>, and the
    /// close button sends <c>close</c> while the form itself stays until the container window
    /// actually disappears from the table.
    /// </summary>
    private sealed class MirrorWindow
    {
        private readonly MirrorState _state;
        private readonly MirrorManager _manager;
        private readonly long _id;
        private readonly Form _form;
        private readonly PictureBox _picture;
        private System.Drawing.Bitmap? _view;
        private int _viewX;
        private int _viewY;
        private int _viewGeneration = -1;
        private bool _closingFromTable;
        private bool _syncing;
        private DateTime _positionHoldUntil = DateTime.MinValue;
        private DateTime _sizeHoldUntil = DateTime.MinValue;
        private DateTime _lastMoveSent = DateTime.MinValue;
        private DateTime _lastMove = DateTime.MinValue;

        public MirrorWindow(MirrorState state, ContainerDesktopWindow window, MirrorManager manager)
        {
            _state = state;
            _manager = manager;
            _id = window.Id;

            _picture = new PictureBox
            {
                Location = new System.Drawing.Point(0, 0),
                SizeMode = PictureBoxSizeMode.Normal,
                BackColor = System.Drawing.Color.Black,
            };

            _form = new Form
            {
                FormBorderStyle = FormBorderStyle.Sizable,
                Text = window.Title,
                ShowInTaskbar = true,
                StartPosition = FormStartPosition.Manual,
                BackColor = System.Drawing.Color.Black,
                ClientSize = new System.Drawing.Size(Math.Max(1, window.Width), Math.Max(1, window.Height)),
                Location = new System.Drawing.Point(window.X + state.OffsetX, window.Y + state.OffsetY),
            };
            _form.Controls.Add(_picture);

            // The picture covers the whole client area, so it - not the form - receives
            // the mouse. Wire both.
            foreach (var control in new Control[] { _form, _picture })
            {
                control.MouseDown += OnMouseDown;
                control.MouseMove += OnMouseMove;
                control.MouseDoubleClick += OnMouseDoubleClick;
            }

            _form.KeyPreview = true; // the picture cannot take focus, the form keeps it
            _form.KeyDown += OnKeyDown;
            _form.KeyUp += OnKeyUp;
            _form.MouseWheel += OnMouseWheel;
            _form.Move += OnFormMove;
            _form.ResizeEnd += OnResizeEnd;
            _form.FormClosing += OnFormClosing;

            Update(window, state);

            // Mark it before it is shown, so a read side on the same desktop skips it.
            MirrorMarker.Set(_form.Handle);
            _form.Show();
            _manager.Log($"window id=0x{window.Id:X} \"{window.Title}\" rect=({window.X},{window.Y} " +
                $"{window.Width}x{window.Height})");
        }

        public void BringToFront() => _form.BringToFront();

        public void Close()
        {
            _closingFromTable = true;
            try
            {
                _form.Close();
                _form.Dispose();
            }
            catch (InvalidOperationException)
            {
                // Also covers ObjectDisposedException: a form whose container window is
                // already gone must never crash the viewer.
            }
        }

        public void Update(ContainerDesktopWindow window, MirrorState state)
        {
            var x = Math.Clamp(window.X, 0, Math.Max(0, state.Geometry.Width - 1));
            var y = Math.Clamp(window.Y, 0, Math.Max(0, state.Geometry.Height - 1));
            var width = Math.Max(1, Math.Min(window.Width, state.Geometry.Width - x));
            var height = Math.Max(1, Math.Min(window.Height, state.Geometry.Height - y));

            // Zero-copy view onto this window's pixels inside the shared frame. It reads
            // live memory, so it is kept between frames and only rebuilt when the window's
            // rect or the mapping itself changed.
            if (_view is null || _viewGeneration != state.MapGeneration
                || _viewX != x || _viewY != y || _view.Width != width || _view.Height != height)
            {
                try
                {
                    var crop = new System.Drawing.Bitmap(
                        width, height, state.Geometry.Stride,
                        System.Drawing.Imaging.PixelFormat.Format32bppRgb,
                        IntPtr.Add(state.Pixels, (y * state.Geometry.Stride) + (x * 4)));

                    var previous = _view;
                    _view = crop;
                    _viewX = x;
                    _viewY = y;
                    _viewGeneration = state.MapGeneration;
                    _picture.Image = crop;
                    _picture.Size = new System.Drawing.Size(width, height);
                    previous?.Dispose();
                }
                catch (ArgumentException)
                {
                    return;
                }

                _picture.Invalidate();
            }
            else if (state.Version >= ContainerDesktopMirror.CurrentVersion)
            {
                // Repaint only what the read side marked dirty.
                foreach (var dirty in state.DirtyRects)
                {
                    var region = System.Drawing.Rectangle.Intersect(
                        new System.Drawing.Rectangle(x, y, width, height),
                        new System.Drawing.Rectangle(dirty.X, dirty.Y, dirty.Width, dirty.Height));
                    if (!region.IsEmpty)
                    {
                        _picture.Invalidate(new System.Drawing.Rectangle(
                            region.X - x, region.Y - y, region.Width, region.Height));
                    }
                }
            }
            else
            {
                _picture.Invalidate(); // legacy read side: no dirty info, repaint everything
            }

            var now = DateTime.UtcNow;
            _syncing = true;
            try
            {
                // While the user moves or resizes the host window the table still carries
                // the old rect; do not snap the window back mid-drag.
                if (now >= _positionHoldUntil)
                {
                    var targetX = window.X + state.OffsetX;
                    var targetY = window.Y + state.OffsetY;
                    if (_form.Left != targetX || _form.Top != targetY)
                    {
                        _form.Location = new System.Drawing.Point(targetX, targetY);
                    }
                }

                if (now >= _sizeHoldUntil
                    && (_form.ClientSize.Width != width || _form.ClientSize.Height != height))
                {
                    _form.ClientSize = new System.Drawing.Size(width, height);
                }

                if (!string.Equals(_form.Text, window.Title, StringComparison.Ordinal))
                {
                    _form.Text = window.Title;
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        /// <summary>
        /// Container-desktop coordinates of a mouse event: from the message coordinates
        /// (PointToScreen), not Cursor.Position - that keeps the mapping independent of DPI
        /// virtualisation. The mirror's offset is a display-only shift and must not leak
        /// into the container's coordinates.
        /// </summary>
        private System.Drawing.Point ContainerPoint(System.Drawing.Point location, object? sender)
        {
            var screen = ((Control)sender!).PointToScreen(location);
            return new System.Drawing.Point(screen.X - _state.OffsetX, screen.Y - _state.OffsetY);
        }

        private static string Button(MouseEventArgs e)
            => e.Button == MouseButtons.Right ? "right" : e.Button == MouseButtons.Middle ? "middle" : "left";

        private void OnMouseDown(object? sender, MouseEventArgs e)
        {
            _state.Send(MirrorInputCommands.Activate(_id));

            if (e.Clicks > 1)
            {
                return; // the second click arrives as MouseDoubleClick
            }

            var point = ContainerPoint(e.Location, sender);
            _state.Send(MirrorInputCommands.Click(point.X, point.Y, Button(e)));
        }

        private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
        {
            var point = ContainerPoint(e.Location, sender);
            _state.Send(MirrorInputCommands.Click(point.X, point.Y, Button(e), 2));
        }

        private void OnMouseMove(object? sender, MouseEventArgs e)
        {
            if ((DateTime.UtcNow - _lastMove).TotalMilliseconds < 100)
            {
                return;
            }

            _lastMove = DateTime.UtcNow;
            var point = ContainerPoint(e.Location, sender);
            _state.Send(MirrorInputCommands.MovePointer(point.X, point.Y));
        }

        private void OnMouseWheel(object? sender, MouseEventArgs e)
            => _state.Send(MirrorInputCommands.Wheel(e.Delta));

        private void OnFormMove(object? sender, EventArgs e)
        {
            if (_syncing || _closingFromTable)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastMoveSent).TotalMilliseconds < 60)
            {
                return;
            }

            // The OS moved the host window (caption drag); move the container window with
            // it and hold the table sync off until the read side catches up.
            _lastMoveSent = now;
            _positionHoldUntil = now.AddMilliseconds(500);
            _state.Send(MirrorInputCommands.MoveWindow(_id, _form.Left - _state.OffsetX, _form.Top - _state.OffsetY));
        }

        private void OnResizeEnd(object? sender, EventArgs e)
        {
            if (_closingFromTable)
            {
                return;
            }

            // ResizeEnd fires at the end of the modal move/size loop, so this is also the
            // final position after a caption drag.
            var now = DateTime.UtcNow;
            _positionHoldUntil = now.AddMilliseconds(500);
            _state.Send(MirrorInputCommands.MoveWindow(_id, _form.Left - _state.OffsetX, _form.Top - _state.OffsetY));

            var size = _form.ClientSize;
            if (_view is not null && (size.Width != _view.Width || size.Height != _view.Height))
            {
                _sizeHoldUntil = now.AddMilliseconds(750);
                _state.Send(MirrorInputCommands.ResizeWindow(_id, size.Width, size.Height));
                _manager.Log($"resize id=0x{_id:X} -> {size.Width}x{size.Height}");
            }
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (_closingFromTable || e.CloseReason != CloseReason.UserClosing)
            {
                return;
            }

            // The container window may need a moment to actually close; the form stays
            // until the table stops listing it.
            e.Cancel = true;
            _state.Send(MirrorInputCommands.CloseWindow(_id));
            _manager.Log($"close requested id=0x{_id:X}");
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (_state.Version >= ContainerDesktopMirror.CurrentVersion)
            {
                // The Keys enum carries the virtual-key code.
                _state.Send(MirrorInputCommands.Key((int)e.KeyCode, true));
                return;
            }

            var name = e.KeyCode.ToString().ToUpperInvariant();
            if (e.Alt)
            {
                _state.Send(MirrorInputCommands.Keys($"ALT+{name}"));
            }
            else if (e.Control)
            {
                _state.Send(MirrorInputCommands.Keys($"CTRL+{name}"));
            }
            else if (e.Shift)
            {
                _state.Send(MirrorInputCommands.Keys($"SHIFT+{name}"));
            }
            else if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
            {
                _state.Send(MirrorInputCommands.Text(name.ToLowerInvariant()));
            }
            else
            {
                _state.Send(MirrorInputCommands.Keys(name));
            }
        }

        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            if (_state.Version >= ContainerDesktopMirror.CurrentVersion)
            {
                _state.Send(MirrorInputCommands.Key((int)e.KeyCode, false));
            }
        }
    }
}