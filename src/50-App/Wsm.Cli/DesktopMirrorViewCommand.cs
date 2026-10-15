using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Wsm.Connection.ContainerDesktop;

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
/// clicking forwards a click at the container-desktop coordinates, and <b>dragging a
/// mirrored window's title band sends <c>movewindow &lt;hwnd&gt; x,y</c></b>, which the
/// read side applies with <c>SetWindowPos</c> - so dragging on the host really moves
/// the window inside the container.
/// </para>
/// </summary>
internal static class DesktopMirrorViewCommand
{
    /// <summary>Height of the band that acts as the window's title bar.</summary>
    private const int TitleBand = 28;

    /// <summary>
    /// <c>--print-table</c>: prints what the read side published (desktop geometry, frame
    /// sequence, every window with its rectangle) and exits. This is how a caller checks
    /// the channel - and a window's position after moving it - without showing anything
    /// and <b>without elevation</b>.
    /// </summary>
    public static int PrintTable(string mirrorPath, int waitSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, waitSeconds));
        do
        {
            if (ContainerDesktopMirror.TryReadHeader(mirrorPath, out var count, out var sequence) is { } geometry)
            {
                var windows = ContainerDesktopMirror.ReadWindows(mirrorPath, geometry, count);
                Console.WriteLine($"Mirror   : {mirrorPath}");
                Console.WriteLine($"Geometry : {geometry.Width}x{geometry.Height} stride={geometry.Stride} fps={geometry.Fps}");
                Console.WriteLine($"Sequence : {sequence}");
                Console.WriteLine($"Windows  : {windows.Count}");
                foreach (var window in windows)
                {
                    Console.WriteLine(
                        $"  - id=0x{window.Id:X} rect=({window.X},{window.Y} {window.Width}x{window.Height}) " +
                        $"seq={window.Sequence} \"{window.Title}\"");
                }

                return 0;
            }

            Thread.Sleep(500);
        }
        while (DateTime.UtcNow < deadline);

        Console.Error.WriteLine($"No window mirror has been published at '{mirrorPath}'.");
        return 1;
    }

    public static int Run(string mirrorPath, int seconds, string inputPrefix, int offsetX = 0, int offsetY = 0)
    {
        if (!File.Exists(mirrorPath))
        {
            Console.Error.WriteLine($"No mirror buffer at '{mirrorPath}'.");
            return 1;
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

    /// <summary>The mapped mirror file plus the latest header/window table.</summary>
    private sealed class MirrorState : IDisposable
    {
        private const uint GenericRead = 0x80000000;
        private const uint ShareAll = 0x00000007;
        private const uint OpenExisting = 3;
        private const uint AttributeNormal = 0x80;
        private const uint PageReadOnly = 0x02;
        private const uint FileMapRead = 0x0004;

        private IntPtr _file = new(-1);
        private IntPtr _mapping;
        private IntPtr _base;

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

        public IReadOnlyList<ContainerDesktopWindow> Windows { get; private set; }
            = Array.Empty<ContainerDesktopWindow>();

        /// <summary>True when a new frame arrived (or the window set changed).</summary>
        public bool TryRefresh()
        {
            if (ContainerDesktopMirror.TryReadHeader(Path, out var count, out var sequence) is not { } geometry)
            {
                return false;
            }

            if (_base != IntPtr.Zero && (geometry.Width != Geometry.Width || geometry.Height != Geometry.Height))
            {
                Release();
            }

            if (_base == IntPtr.Zero)
            {
                Map(geometry);
            }

            Geometry = geometry;
            if (sequence == Sequence && Windows.Count == count)
            {
                return false;
            }

            Sequence = sequence;
            Windows = ContainerDesktopMirror.ReadWindows(Path, geometry, count);
            return true;
        }

        private void Map(ContainerDesktopGeometry geometry)
        {
            _file = Native.CreateFileW(Path, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, AttributeNormal, IntPtr.Zero);
            if (_file == new IntPtr(-1))
            {
                throw new IOException($"Cannot open '{Path}' (error {Marshal.GetLastWin32Error()}).");
            }

            _mapping = Native.CreateFileMappingW(_file, IntPtr.Zero, PageReadOnly, 0, 0, null);
            if (_mapping == IntPtr.Zero)
            {
                throw new IOException($"Cannot map '{Path}' (error {Marshal.GetLastWin32Error()}).");
            }

            _base = Native.MapViewOfFile(_mapping, FileMapRead, 0, 0, 0);
            if (_base == IntPtr.Zero)
            {
                throw new IOException($"Cannot view '{Path}' (error {Marshal.GetLastWin32Error()}).");
            }

            Pixels = IntPtr.Add(_base, ContainerDesktopMirror.PixelOffset);
        }

        private void Release()
        {
            if (_base != IntPtr.Zero)
            {
                _ = Native.UnmapViewOfFile(_base);
                _base = IntPtr.Zero;
                Pixels = IntPtr.Zero;
            }

            if (_mapping != IntPtr.Zero)
            {
                _ = Native.CloseHandle(_mapping);
                _mapping = IntPtr.Zero;
            }

            if (_file != new IntPtr(-1))
            {
                _ = Native.CloseHandle(_file);
                _file = new IntPtr(-1);
            }
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

    /// <summary>One mirrored container window.</summary>
    private sealed class MirrorWindow
    {
        private readonly MirrorState _state;
        private readonly MirrorManager _manager;
        private readonly long _id;
        private readonly Form _form;
        private readonly PictureBox _picture;
        private bool _dragging;
        private System.Drawing.Point _grabClient;
        private System.Drawing.Point _origin;
        private DateTime _lastReport = DateTime.MinValue;
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
                FormBorderStyle = FormBorderStyle.None,
                Text = window.Title,
                ShowInTaskbar = true,
                StartPosition = FormStartPosition.Manual,
                BackColor = System.Drawing.Color.Black,
                ClientSize = new System.Drawing.Size(Math.Max(1, window.Width), Math.Max(1, window.Height)),
                Location = new System.Drawing.Point(window.X + state.OffsetX, window.Y + state.OffsetY),
            };
            _form.Controls.Add(_picture);

            // The picture covers the whole client area, so it - not the form - receives
            // the mouse. Wire both, and use the client coordinates (the picture sits at
            // 0,0 and matches the client size).
            foreach (var control in new Control[] { _form, _picture })
            {
                control.MouseDown += OnMouseDown;
                control.MouseMove += OnMouseMove;
                control.MouseUp += OnMouseUp;
            }

            _form.KeyPreview = true; // the picture cannot take focus, the form keeps it
            _form.KeyDown += OnKeyDown;

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
            try
            {
                _form.Close();
                _form.Dispose();
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Update(ContainerDesktopWindow window, MirrorState state)
        {
            var x = Math.Clamp(window.X, 0, Math.Max(0, state.Geometry.Width - 1));
            var y = Math.Clamp(window.Y, 0, Math.Max(0, state.Geometry.Height - 1));
            var width = Math.Max(1, Math.Min(window.Width, state.Geometry.Width - x));
            var height = Math.Max(1, Math.Min(window.Height, state.Geometry.Height - y));

            try
            {
                // Zero-copy view onto this window's pixels inside the shared frame.
                var crop = new System.Drawing.Bitmap(
                    width, height, state.Geometry.Stride,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb,
                    IntPtr.Add(state.Pixels, (y * state.Geometry.Stride) + (x * 4)));

                var previous = _picture.Image;
                _picture.Image = crop;
                previous?.Dispose();
            }
            catch (ArgumentException)
            {
                return;
            }

            if (_dragging)
            {
                return; // the user is moving it; do not fight the drag
            }

            if (_form.ClientSize.Width != width || _form.ClientSize.Height != height)
            {
                _form.ClientSize = new System.Drawing.Size(width, height);
            }

            _picture.Size = _form.ClientSize;
            var targetX = window.X + state.OffsetX;
            var targetY = window.Y + state.OffsetY;
            if (_form.Left != targetX || _form.Top != targetY)
            {
                _form.Location = new System.Drawing.Point(targetX, targetY);
            }

            if (!string.Equals(_form.Text, window.Title, StringComparison.Ordinal))
            {
                _form.Text = window.Title;
            }
        }

        private void OnMouseDown(object? sender, MouseEventArgs e)
        {
            _state.Send($"activate {_id}");

            if (e.Y < TitleBand && e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _grabClient = e.Location;
                _origin = _form.Location;
                _form.Capture = true;
                _manager.Log($"drag begin id=0x{_id:X} at ({_origin.X},{_origin.Y})");
                return;
            }

            _state.Send($"click {_form.Left - _state.OffsetX + e.X},{_form.Top - _state.OffsetY + e.Y},{(e.Button == MouseButtons.Right ? 2 : 1)}");
        }

        private void OnMouseMove(object? sender, MouseEventArgs e)
        {
            if (!_dragging)
            {
                if ((DateTime.UtcNow - _lastMove).TotalMilliseconds < 100)
                {
                    return;
                }

                _lastMove = DateTime.UtcNow;
                _state.Send($"move {_form.Left - _state.OffsetX + e.X},{_form.Top - _state.OffsetY + e.Y}");
                return;
            }

            if ((DateTime.UtcNow - _lastReport).TotalMilliseconds < 40)
            {
                return;
            }

            _lastReport = DateTime.UtcNow;

            // The delta comes from the mouse messages, not Cursor.Position: that keeps
            // dragging independent of DPI virtualisation and makes it testable by
            // posting messages.
            var x = _origin.X + (e.X - _grabClient.X);
            var y = _origin.Y + (e.Y - _grabClient.Y);

            _form.Location = new System.Drawing.Point(x, y);

            // Container coordinates: the mirror's offset is a display-only shift, so it
            // must not leak into the window's real position.
            _state.Send($"movewindow {_id} {x - _state.OffsetX},{y - _state.OffsetY}");
            _manager.Log($"drag move id=0x{_id:X} -> ({x - _state.OffsetX},{y - _state.OffsetY})");
        }

        private void OnMouseUp(object? sender, MouseEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            _form.Capture = false;
            _manager.Log($"drag end id=0x{_id:X} at ({_form.Left},{_form.Top})");
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            var name = e.KeyCode.ToString().ToUpperInvariant();
            if (e.Alt)
            {
                _state.Send($"keys ALT+{name}");
            }
            else if (e.Control)
            {
                _state.Send($"keys CTRL+{name}");
            }
            else if (e.Shift)
            {
                _state.Send($"keys SHIFT+{name}");
            }
            else if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
            {
                _state.Send($"text {name.ToLowerInvariant()}");
            }
            else
            {
                _state.Send($"keys {name}");
            }
        }
    }

    private static class Native
    {
        public const uint GenericRead = 0x80000000;
        public const uint ShareAll = 0x00000007;
        public const uint OpenExisting = 3;
        public const uint AttributeNormal = 0x80;
        public const uint PageReadOnly = 0x02;
        public const uint FileMapRead = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr attributes,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect,
            uint maximumHigh, uint maximumLow, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, ulong length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnmapViewOfFile(IntPtr address);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
