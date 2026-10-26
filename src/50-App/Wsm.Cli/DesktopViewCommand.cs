using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Wsm.Connection.ContainerDesktop;
using Wsm.Connection.ContainerDesktop.Internal;

namespace Wsm.Cli;

/// <summary>
/// <c>wsm desktop-view --frame &lt;path&gt; [--seconds N]</c> - shows a container
/// desktop that another process (a <see cref="Wsm.Connection.ContainerDesktop.IContainerDesktopHost"/>,
/// typically inside the SYSTEM agent service) is capturing into a shared memory
/// file, and forwards this window's mouse and keyboard back to the container as
/// one command file per event - the format the in-container agent polls.
/// <para>
/// The window is resizable: the frame is drawn scaled into the client area (aspect
/// kept, letterboxed) and mouse coordinates are transformed back into frame
/// coordinates (<see cref="FrameViewScaling"/>). Keys go as structured
/// <c>key vk down|up</c> events, the wheel as <c>wheel</c>, a double-click as a
/// counted <c>click</c>, and Ctrl+V sends the host clipboard text as a base64
/// <c>clipboard</c> command. F10 cycles a resolution request
/// (<see cref="ViewRequest"/>); when the container refuses, the viewer simply keeps
/// scaling.
/// </para>
/// <para>
/// The window is an ordinary Win32 window, so when the agent service launches it
/// through <c>WTSQueryUserToken</c> + <c>CreateProcessAsUser</c> it appears on the
/// interactive user's desktop even though the service runs in session 0. No RDP,
/// and no listener inside the container, is involved.
/// </para>
/// <para>
/// The frame path is the header file in both sharing modes. The viewer first tries
/// handle passthrough for a pagefile section; if that fails it asks the same writer to
/// rebuild as a complete file-backed buffer and remaps when the mode changes.
/// </para>
/// <para>
/// While it runs the viewer holds a <see cref="ViewerLiveness"/> lock next to the
/// frame file; the in-container agent exits once no viewer is attached any more.
/// </para>
/// </summary>
internal static class DesktopViewCommand
{
    public static int Run(string framePath, int seconds, string inputPrefix)
    {
        if (!File.Exists(framePath))
        {
            Console.Error.WriteLine($"No frame buffer at '{framePath}'.");
            return 1;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(framePath)) ?? ".";
        var exitCode = 0;
        var thread = new Thread(() =>
        {
            FrameView? view = null;
            try
            {
                view = new FrameView(framePath, directory, inputPrefix);
                System.Windows.Forms.Application.EnableVisualStyles();
                var form = view.CreateForm(seconds);
                System.Windows.Forms.Application.Run(form);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                exitCode = 1;
            }
            finally
            {
                view?.Dispose();
            }
        });
        thread.SetApartmentState(ApartmentState.STA); // WinForms requires STA
        thread.IsBackground = false;
        thread.Start();
        thread.Join();
        return exitCode;
    }

    /// <summary>The frame the desktop capture starts at; F10 cycles the list.</summary>
    private static readonly (int Width, int Height)[] Resolutions = [(1024, 768), (1280, 720), (1600, 900)];

    /// <summary>Reads the shared frame source and builds the window around it.</summary>
    private sealed class FrameView : IDisposable
    {
        private readonly string _framePath;
        private readonly string _directory;
        private readonly string _inputPrefix;
        private readonly string _lockPath;
        private SharedSectionSource.Mapping _source;
        private ContainerDesktopFrameHeader _header;
        private Bitmap _view;           // zero-copy view over the mapping
        private Bitmap? _snapshot;      // latest completed frame, owned by the canvas
        private long _lastSequence = -1;
        private DateTime _lastMove = DateTime.MinValue;
        private DateTime _lastLockTouch = DateTime.MinValue;
        private DateTime _requestSentAt = DateTime.MinValue;
        private (int Width, int Height) _requested;
        private string? _status;

        public FrameView(string framePath, string directory, string inputPrefix)
        {
            _framePath = framePath;
            _directory = directory;
            _inputPrefix = inputPrefix;

            _source = Open(out var geometry);
            _view = CreateView(_source, geometry);

            // The agent stays alive while at least one lock under viewers\ is live.
            var locks = Path.Combine(directory, ViewerLiveness.LockDirectoryName);
            Directory.CreateDirectory(locks);
            _lockPath = Path.Combine(locks, Guid.NewGuid().ToString("N") + ViewerLiveness.LockFileExtension);
            File.WriteAllText(_lockPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }

        internal ContainerDesktopGeometry Geometry => _header.Geometry;

        private IntPtr Base => _source.BaseAddress;

        public Form CreateForm(int seconds)
        {
            var canvas = new FrameCanvas(this);

            var form = new Form
            {
                Text = Title(0),
                ClientSize = new System.Drawing.Size(Geometry.Width, Geometry.Height),
                StartPosition = FormStartPosition.CenterScreen,
                KeyPreview = true,
                BackColor = System.Drawing.Color.Black,
            };
            form.Controls.Add(canvas);

            canvas.MouseDown += (_, e) =>
            {
                if (e.Clicks > 1)
                {
                    return; // the second click arrives as MouseDoubleClick
                }

                var point = FramePoint(e.Location, canvas);
                Send(MirrorInputCommands.Click(point.X, point.Y, Button(e)));
            };
            canvas.MouseDoubleClick += (_, e) =>
            {
                var point = FramePoint(e.Location, canvas);
                Send(MirrorInputCommands.Click(point.X, point.Y, Button(e), 2));
            };
            canvas.MouseMove += (_, e) =>
            {
                if ((DateTime.UtcNow - _lastMove).TotalMilliseconds < 100)
                {
                    return;
                }

                _lastMove = DateTime.UtcNow;
                var point = FramePoint(e.Location, canvas);
                Send(MirrorInputCommands.MovePointer(point.X, point.Y));
            };
            canvas.MouseWheel += (_, e) => Send(MirrorInputCommands.Wheel(e.Delta));

            form.KeyDown += (_, e) => OnKeyDown(e);
            form.KeyUp += (_, e) => OnKeyUp(e);

            var timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += (_, _) => Tick(form, canvas);
            timer.Start();

            form.FormClosed += (_, _) =>
            {
                timer.Stop();
                DeleteLock();
            };

            if (seconds > 0)
            {
                var close = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
                close.Tick += (_, _) =>
                {
                    close.Stop();
                    form.Close();
                };
                form.Shown += (_, _) => close.Start();
            }

            return form;
        }

        private void Tick(Form form, FrameCanvas canvas)
        {
            // The header FILE is authoritative for (re)connect decisions: a restarted
            // agent or an applied resolution request shows up as a new header.
            try
            {
                if (ContainerDesktopFrame.TryReadHeader(_framePath) is { } current
                    && ContainerDesktopFrame.RequiresRemap(_header, current))
                {
                    Remap();
                }
            }
            catch (IOException)
            {
                // The agent is recreating the section; retry on the next tick.
            }

            if (Marshal.ReadInt32(Base, 32) != 0)
            {
                return; // the agent is mid-frame
            }

            var sequence = Marshal.ReadInt64(Base, 24);
            if (sequence != _lastSequence)
            {
                _lastSequence = sequence;
                var snapshot = new Bitmap(_view);
                var previous = _snapshot;
                _snapshot = snapshot;
                canvas.Image = snapshot;
                previous?.Dispose();
                canvas.Invalidate();
                form.Text = Title(sequence);
            }

            if ((DateTime.UtcNow - _lastLockTouch) >= ViewerLiveness.TouchInterval)
            {
                _lastLockTouch = DateTime.UtcNow;
                TouchLock();
            }

            if (_requestSentAt != DateTime.MinValue)
            {
                CheckRequestAck(form);
            }
        }

        private void Remap()
        {
            var source = Open(out var geometry);
            var view = CreateView(source, geometry);
            _source.Dispose();
            _view.Dispose();
            _source = source;
            _view = view;
            _lastSequence = -1; // a new source restarts the frame watch
        }

        private SharedSectionSource.Mapping Open(out ContainerDesktopGeometry geometry)
        {
            var mapping = SharedSectionSource.Open(_framePath, ContainerDesktopFrame.Magic,
                header => ContainerDesktopFrame.PixelOffset
                    + ((long)Marshal.ReadInt32(header, 16) * Marshal.ReadInt32(header, 12)));
            geometry = new ContainerDesktopGeometry(
                Marshal.ReadInt32(mapping.BaseAddress, 8),
                Marshal.ReadInt32(mapping.BaseAddress, 12),
                Marshal.ReadInt32(mapping.BaseAddress, 16),
                Marshal.ReadInt32(mapping.BaseAddress, 20));
            _header = new ContainerDesktopFrameHeader(
                geometry,
                Marshal.ReadInt32(mapping.BaseAddress, ContainerDesktopFrame.WriterPidOffset),
                (ContainerDesktopMode)Marshal.ReadInt32(mapping.BaseAddress, ContainerDesktopFrame.ModeOffset))
            {
                Sequence = Marshal.ReadInt64(mapping.BaseAddress, 24),
            };
            return mapping;
        }

        private Bitmap CreateView(SharedSectionSource.Mapping source, ContainerDesktopGeometry geometry)
            => new(
                geometry.Width, geometry.Height, geometry.Stride,
                System.Drawing.Imaging.PixelFormat.Format32bppRgb,
                IntPtr.Add(source.BaseAddress, ContainerDesktopFrame.PixelOffset));

        private System.Drawing.Point FramePoint(System.Drawing.Point location, Control canvas)
        {
            var (x, y) = FrameViewScaling.ToFrame(
                location.X, location.Y, Geometry.Width, Geometry.Height,
                canvas.ClientSize.Width, canvas.ClientSize.Height);
            return new System.Drawing.Point(x, y);
        }

        private static string Button(MouseEventArgs e)
            => e.Button == MouseButtons.Right ? "right" : e.Button == MouseButtons.Middle ? "middle" : "left";

        private void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F10)
            {
                e.SuppressKeyPress = true;
                RequestNextResolution();
                return;
            }

            if (e.Control && e.KeyCode == Keys.V)
            {
                e.SuppressKeyPress = true;
                SendClipboard();
                return;
            }

            // The Keys enum carries the virtual-key code.
            Send(MirrorInputCommands.Key((int)e.KeyCode, true));
        }

        private void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F10 || (e.Control && e.KeyCode == Keys.V))
            {
                return;
            }

            Send(MirrorInputCommands.Key((int)e.KeyCode, false));
        }

        private void SendClipboard()
        {
            try
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    Send(MirrorInputCommands.Clipboard(text));
                }
            }
            catch (ExternalException)
            {
                // The host clipboard is busy; a lost paste is not an error here.
            }
        }

        private void RequestNextResolution()
        {
            var index = Array.FindIndex(
                Resolutions, r => r.Width == Geometry.Width && r.Height == Geometry.Height);
            _requested = Resolutions[(index + 1) % Resolutions.Length];
            try
            {
                // A stale ack from an earlier request must not answer this one.
                var ack = Path.Combine(_directory, ViewRequest.AckFileName);
                if (File.Exists(ack))
                {
                    File.Delete(ack);
                }

                File.WriteAllText(
                    Path.Combine(_directory, ViewRequest.FileName),
                    ViewRequest.Format(_requested.Width, _requested.Height),
                    new UTF8Encoding(false));
                _requestSentAt = DateTime.UtcNow;
                _status = $"requested {_requested.Width}x{_requested.Height}...";
            }
            catch (IOException)
            {
            }
        }

        private void CheckRequestAck(Form form)
        {
            try
            {
                var ackPath = Path.Combine(_directory, ViewRequest.AckFileName);
                if (File.Exists(ackPath)
                    && ViewRequest.TryParseAck(File.ReadAllText(ackPath), out var applied, out var width, out var height)
                    && width == _requested.Width && height == _requested.Height)
                {
                    _requestSentAt = DateTime.MinValue;
                    _status = applied
                        ? $"resolution {_requested.Width}x{_requested.Height} applied"
                        : $"resolution {_requested.Width}x{_requested.Height} rejected - scaling";
                    form.Text = Title(_lastSequence);
                    return;
                }
            }
            catch (IOException)
            {
            }

            if ((DateTime.UtcNow - _requestSentAt) >= ViewRequest.AckTimeout)
            {
                _requestSentAt = DateTime.MinValue;
                _status = $"no answer for {_requested.Width}x{_requested.Height} - scaling";
                form.Text = Title(_lastSequence);
            }
        }

        private string Title(long sequence)
            => $"WSM - container desktop ({Geometry.Width}x{Geometry.Height})"
                + (sequence > 0 ? $"   frame {sequence}" : string.Empty)
                + (_status is null ? string.Empty : $"   {_status}");

        private void TouchLock()
        {
            try
            {
                File.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow);
            }
            catch (IOException)
            {
            }
        }

        private void DeleteLock()
        {
            try
            {
                if (File.Exists(_lockPath))
                {
                    File.Delete(_lockPath);
                }
            }
            catch (IOException)
            {
            }
        }

        /// <summary>
        /// One file per event: the container's filesystem keeps a handle on a file
        /// once it has read it, so a single rewritten queue file would fail.
        /// </summary>
        private void Send(string command)
        {
            try
            {
                var name = _inputPrefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                    + ".txt";
                File.WriteAllText(Path.Combine(_directory, name), command, new UTF8Encoding(false));
            }
            catch (IOException)
            {
            }
        }

        public void Dispose()
        {
            DeleteLock();
            _snapshot?.Dispose();
            _view.Dispose();
            _source.Dispose();
        }
    }

    /// <summary>
    /// The frame, drawn scaled into the client area (aspect kept, letterboxed black).
    /// Bilinear is the fast high-enough-quality interpolation at these sizes.
    /// </summary>
    private sealed class FrameCanvas : Control
    {
        private readonly FrameView _view;

        public FrameCanvas(FrameView view)
        {
            _view = view;
            Dock = DockStyle.Fill;
            BackColor = System.Drawing.Color.Black;
            SetStyle(
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint,
                true);
        }

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Bitmap? Image { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var image = Image;
            if (image is null)
            {
                return;
            }

            var geometry = _view.Geometry;
            var dest = FrameViewScaling.Fit(
                geometry.Width, geometry.Height, ClientSize.Width, ClientSize.Height);
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            e.Graphics.DrawImage(
                image, new System.Drawing.Rectangle(dest.X, dest.Y, dest.Width, dest.Height));
        }
    }
}
