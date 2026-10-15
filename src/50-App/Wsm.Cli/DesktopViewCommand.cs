using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Wsm.Cli;

/// <summary>
/// <c>wsm desktop-view --frame &lt;path&gt; [--seconds N]</c> - shows a container
/// desktop that another process (a <see cref="Wsm.Connection.ContainerDesktop.IContainerDesktopHost"/>,
/// typically inside the SYSTEM agent service) is capturing into a shared memory
/// file, and forwards this window's mouse and keyboard back to the container as
/// one command file per event - the format the in-container agent polls.
/// <para>
/// The window is an ordinary Win32 window, so when the agent service launches it
/// through <c>WTSQueryUserToken</c> + <c>CreateProcessAsUser</c> it appears on the
/// interactive user's desktop even though the service runs in session 0. No RDP,
/// and no listener inside the container, is involved.
/// </para>
/// </summary>
internal static class DesktopViewCommand
{
    private const int FrameMagic = 0x314D5346; // 'FSM1'

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
            try
            {
                using var view = new FrameView(framePath, directory, inputPrefix);
                System.Windows.Forms.Application.EnableVisualStyles();
                var form = view.CreateForm(seconds);
                System.Windows.Forms.Application.Run(form);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                exitCode = 1;
            }
        });
        thread.SetApartmentState(ApartmentState.STA); // WinForms requires STA
        thread.IsBackground = false;
        thread.Start();
        thread.Join();
        return exitCode;
    }

    /// <summary>Reads the shared frame file and builds the window around it.</summary>
    private sealed class FrameView : IDisposable
    {
        private readonly string _directory;
        private readonly string _inputPrefix;
        private IntPtr _file = new(-1);
        private IntPtr _mapping;
        private IntPtr _base;
        private int _width;
        private int _height;
        private int _stride;
        private long _lastSequence = -1;
        private DateTime _lastMove = DateTime.UtcNow;

        public FrameView(string framePath, string directory, string inputPrefix)
        {
            _directory = directory;
            _inputPrefix = inputPrefix;

            _file = NativeMethods.CreateFileW(
                framePath, NativeMethods.GenericRead, NativeMethods.ShareAll, IntPtr.Zero,
                NativeMethods.OpenExisting, NativeMethods.AttributeNormal, IntPtr.Zero);
            if (_file == new IntPtr(-1))
            {
                throw new IOException($"Cannot open '{framePath}' (error {Marshal.GetLastWin32Error()}).");
            }

            _mapping = NativeMethods.CreateFileMappingW(_file, IntPtr.Zero, NativeMethods.PageReadOnly, 0, 0, null);
            if (_mapping == IntPtr.Zero)
            {
                throw new IOException($"Cannot map '{framePath}' (error {Marshal.GetLastWin32Error()}).");
            }

            _base = NativeMethods.MapViewOfFile(_mapping, NativeMethods.FileMapRead, 0, 0, 0);
            if (_base == IntPtr.Zero)
            {
                throw new IOException($"Cannot view '{framePath}' (error {Marshal.GetLastWin32Error()}).");
            }

            if (Marshal.ReadInt32(_base, 0) != FrameMagic)
            {
                throw new IOException("The frame buffer does not carry the container desktop header.");
            }

            _width = Marshal.ReadInt32(_base, 8);
            _height = Marshal.ReadInt32(_base, 12);
            _stride = Marshal.ReadInt32(_base, 16);
        }

        public Form CreateForm(int seconds)
        {
            var pixels = IntPtr.Add(_base, 64);
            var frame = new Bitmap(_width, _height, _stride, System.Drawing.Imaging.PixelFormat.Format32bppRgb, pixels);

            var picture = new PictureBox
            {
                Size = new System.Drawing.Size(_width, _height),
                Location = new System.Drawing.Point(0, 0),
                SizeMode = PictureBoxSizeMode.Normal,
            };

            var form = new Form
            {
                Text = $"WSM - container desktop ({_width}x{_height})",
                ClientSize = new System.Drawing.Size(_width, _height),
                StartPosition = FormStartPosition.CenterScreen,
                KeyPreview = true,
            };
            form.Controls.Add(picture);

            form.MouseDown += (_, e) => Send($"click {e.X},{e.Y},{(e.Button == MouseButtons.Right ? 2 : e.Button == MouseButtons.Middle ? 3 : 1)}");
            form.MouseMove += (_, e) =>
            {
                if ((DateTime.UtcNow - _lastMove).TotalMilliseconds < 100)
                {
                    return;
                }

                _lastMove = DateTime.UtcNow;
                Send($"move {e.X},{e.Y}");
            };
            form.KeyDown += (_, e) => Send(TranslateKey(e));

            var timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += (_, _) =>
            {
                if (Marshal.ReadInt32(_base, 32) != 0)
                {
                    return; // the agent is mid-frame
                }

                var sequence = Marshal.ReadInt64(_base, 24);
                if (sequence == _lastSequence)
                {
                    return;
                }

                _lastSequence = sequence;
                var snapshot = new Bitmap(frame);
                var previous = picture.Image;
                picture.Image = snapshot;
                previous?.Dispose();
                form.Text = $"WSM - container desktop ({_width}x{_height})   frame {sequence}";
            };
            timer.Start();

            form.FormClosed += (_, _) =>
            {
                timer.Stop();
                frame.Dispose();
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

        private static string TranslateKey(KeyEventArgs e)
        {
            var name = e.KeyCode.ToString().ToUpperInvariant();
            return e.Alt ? $"keys ALT+{name}" : e.Control ? $"keys CTRL+{name}" : name.Length == 1 && char.IsLetterOrDigit(name[0])
                ? $"text {name.ToLowerInvariant()}"
                : $"keys {name}";
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
            if (_base != IntPtr.Zero)
            {
                _ = NativeMethods.UnmapViewOfFile(_base);
                _base = IntPtr.Zero;
            }

            if (_mapping != IntPtr.Zero)
            {
                _ = NativeMethods.CloseHandle(_mapping);
                _mapping = IntPtr.Zero;
            }

            if (_file != new IntPtr(-1))
            {
                _ = NativeMethods.CloseHandle(_file);
                _file = new IntPtr(-1);
            }
        }
    }

    private static class NativeMethods
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
