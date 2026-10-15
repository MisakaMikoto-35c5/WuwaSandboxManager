# winsta-child.ps1 - runs INSIDE the target session (launched by winsta-spawn.ps1).
# It is a "session-local capture/input host": it enumerates the window stations and
# desktops of the session it is in, finds the desktop that actually holds windows,
# lists them, captures one and posts it a message. If this sees the container's
# windows, the host needs neither HCS nor an agent inside the container.
param(
    [string] $OutFile = 'C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd\winsta-child.out',
    [string] $PngDirectory = 'C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd'
)

$ErrorActionPreference = 'Continue'
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class WinstaChild
{
    const uint WINSTA_ALL = 0x37F;
    const uint DESKTOP_RIGHTS = 0x1C1;
    const int  SRCCOPY = 0x00CC0020;
    const uint PW_RENDERFULLCONTENT = 0x2;

    class Win
    {
        public IntPtr H;
        public int Pid;
        public int Session;
        public string Title = "";
        public int X, Y, W, Hh;
        public string Describe()
        {
            return string.Format("hwnd=0x{0:X} pid={1} sess={2} rect=({3},{4} {5}x{6}) \"{7}\"",
                H.ToInt64(), Pid, Session, X, Y, W, Hh, Title);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);
    delegate bool EnumNamesProc(string name, IntPtr lparam);
    delegate bool EnumDesktopsProc(string name, IntPtr lparam);

    public static string Run(string outFile, string pngDir)
    {
        var log = new StringBuilder();
        try
        {
            var me = Process.GetCurrentProcess();
            log.AppendLine(string.Format("[child] pid={0} session={1} user={2}", me.Id, me.SessionId, Environment.UserName));
            log.AppendLine(string.Format("[child] ownWinSta={0} ownDesktop={1}", Name(GetProcessWindowStation()), Name(GetThreadDesktop(GetCurrentThreadId()))));

            var stations = new List<string>();
            EnumWindowStations(delegate(string n, IntPtr l) { stations.Add(n); return true; }, IntPtr.Zero);
            log.AppendLine(string.Format("[child] window stations in this session ({0}): {1}", stations.Count, string.Join(", ", stations.ToArray())));

            string best = "";
            int bestCount = -1;

            foreach (var st in stations)
            {
                var hw = OpenWindowStationW(st, false, WINSTA_ALL);
                int err = Marshal.GetLastWin32Error();
                if (hw == IntPtr.Zero)
                {
                    log.AppendLine(string.Format("[child] OpenWindowStation(\"{0}\") -> err={1}", st, err));
                    continue;
                }
                log.AppendLine(string.Format("[child] OpenWindowStation(\"{0}\") -> ok", st));

                var ow = SetProcessWindowStation(hw);
                log.AppendLine(string.Format("[child]   SetProcessWindowStation -> {0} err={1}", ow, Marshal.GetLastWin32Error()));

                var desktops = new List<string>();
                EnumDesktops(hw, delegate(string n, IntPtr l) { desktops.Add(n); return true; }, IntPtr.Zero);
                log.AppendLine(string.Format("[child]   desktops ({0}): {1}", desktops.Count, string.Join(", ", desktops.ToArray())));

                foreach (var dt in desktops)
                {
                    var hd = OpenDesktopW(dt, 0, false, DESKTOP_RIGHTS);
                    if (hd == IntPtr.Zero)
                    {
                        log.AppendLine(string.Format("[child]   OpenDesktop(\"{0}\") -> err={1}", dt, Marshal.GetLastWin32Error()));
                        continue;
                    }

                    string station = st;
                    string desktop = dt;
                    string text = "";
                    var t = new Thread(delegate()
                    {
                        text = Inspect(station, desktop, hd, pngDir, ref best, ref bestCount);
                    });
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                    t.Join();
                    log.AppendLine(text);
                    CloseDesktop(hd);
                }
                CloseWindowStation(hw);
            }

            log.AppendLine("[child] best desktop = " + (best.Length == 0 ? "(none)" : best));
        }
        catch (Exception ex) { log.AppendLine("[child] EX " + ex); }
        return log.ToString();
    }

    static string Inspect(string station, string desktop, IntPtr hd, string pngDir, ref string best, ref int bestCount)
    {
        var log = new StringBuilder();
        bool attached = SetThreadDesktop(hd);
        log.AppendLine(string.Format("[child]   desktop {0}\\{1}: SetThreadDesktop -> {2} err={3}",
            station, desktop, attached, Marshal.GetLastWin32Error()));
        if (!attached) return log.ToString();

        var windows = new List<Win>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            if (sb.Length == 0) return true;
            RECT r; GetWindowRect(h, out r);
            int pid; GetWindowThreadProcessId(h, out pid);
            int sess = -1;
            try { sess = Process.GetProcessById(pid).SessionId; } catch (Exception) { }
            var w = new Win();
            w.H = h; w.Pid = pid; w.Session = sess; w.Title = sb.ToString();
            w.X = r.L; w.Y = r.T; w.W = r.R - r.L; w.Hh = r.B - r.T;
            windows.Add(w);
            return true;
        }, IntPtr.Zero);

        log.AppendLine(string.Format("[child]   desktop {0}\\{1}: visible+titled windows = {2}", station, desktop, windows.Count));
        foreach (var w in windows.OrderByDescending(delegate(Win x) { return x.W * x.Hh; }).Take(12)) log.AppendLine("      " + w.Describe());

        string key = station + "\\" + desktop;
        if (windows.Count > bestCount) { bestCount = windows.Count; best = key; }

        var top = windows.Where(delegate(Win w) { return w.W > 32 && w.Hh > 32; })
                         .OrderByDescending(delegate(Win w) { return w.W * w.Hh; })
                         .FirstOrDefault();
        if (top == null) return log.ToString();

        log.AppendLine("      target " + top.Describe());
        string p1 = Path.Combine(pngDir, "child-bitblt.png");
        log.AppendLine(string.Format("      BitBlt      ink={0:P1} file={1}", Capture(top, p1, false), p1));
        string p2 = Path.Combine(pngDir, "child-printwindow.png");
        log.AppendLine(string.Format("      PrintWindow ink={0:P1} file={1}", Capture(top, p2, true), p2));
        bool posted = PostMessage(top.H, 0x8000 + 1, IntPtr.Zero, IntPtr.Zero);
        log.AppendLine(string.Format("      PostMessage(WM_APP+1) -> {0} err={1}", posted, Marshal.GetLastWin32Error()));
        return log.ToString();
    }

    static double Capture(Win w, string path, bool printWindow)
    {
        var src = GetWindowDC(w.H);
        if (src == IntPtr.Zero) return -1;
        try
        {
            using (var bmp = new Bitmap(Math.Max(1, w.W), Math.Max(1, w.Hh), PixelFormat.Format32bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    var dst = g.GetHdc();
                    try
                    {
                        if (printWindow) PrintWindow(w.H, dst, PW_RENDERFULLCONTENT);
                        else BitBlt(dst, 0, 0, w.W, w.Hh, src, 0, 0, SRCCOPY);
                    }
                    finally { g.ReleaseHdc(dst); }
                }
                bmp.Save(path, ImageFormat.Png);
                return Ink(bmp);
            }
        }
        catch (Exception ex) { try { File.AppendAllText(path + ".err", ex.ToString()); } catch (Exception) { } return -1; }
        finally { ReleaseDC(w.H, src); }
    }

    static double Ink(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int total = 0, nonBlack = 0;
            for (int y = 0; y < bmp.Height; y += 3)
            {
                IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                for (int x = 0; x < bmp.Width; x += 3)
                {
                    total++;
                    if (Marshal.ReadByte(row, x * 4) > 8 || Marshal.ReadByte(row, x * 4 + 1) > 8 || Marshal.ReadByte(row, x * 4 + 2) > 8) nonBlack++;
                }
            }
            return total == 0 ? 0 : (double)nonBlack / total;
        }
        finally { bmp.UnlockBits(data); }
    }

    static string Name(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "(null)";
        var sb = new StringBuilder(256);
        int needed;
        GetUserObjectInformationW(handle, 2, sb, sb.Capacity * 2, out needed);
        return sb.ToString();
    }

    [DllImport("user32.dll", EntryPoint = "GetProcessWindowStation")]
    static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", EntryPoint = "GetThreadDesktop")]
    static extern IntPtr GetThreadDesktop(int threadId);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    static extern int GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder info, int len, out int needed);
    [DllImport("user32.dll", EntryPoint = "EnumWindowStationsW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumWindowStations(EnumNamesProc proc, IntPtr lparam);
    [DllImport("user32.dll", EntryPoint = "EnumDesktopsW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumDesktops(IntPtr winsta, EnumDesktopsProc proc, IntPtr lparam);
    [DllImport("user32.dll", EntryPoint = "OpenWindowStationW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenWindowStationW(string name, bool inherit, uint access);
    [DllImport("user32.dll", EntryPoint = "CloseWindowStation", SetLastError = true)]
    static extern bool CloseWindowStation(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "SetProcessWindowStation", SetLastError = true)]
    static extern bool SetProcessWindowStation(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "OpenDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenDesktopW(string name, int flags, bool inherit, uint access);
    [DllImport("user32.dll", EntryPoint = "CloseDesktop", SetLastError = true)]
    static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "SetThreadDesktop", SetLastError = true)]
    static extern bool SetThreadDesktop(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "EnumWindows", SetLastError = true)]
    static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lparam);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, StringBuilder text, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowDC")]
    static extern IntPtr GetWindowDC(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll", EntryPoint = "PrintWindow")]
    static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("gdi32.dll", EntryPoint = "BitBlt", SetLastError = true)]
    static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
}
'@

try {
    $text = [WinstaChild]::Run($OutFile, $PngDirectory)
    [System.IO.File]::WriteAllText($OutFile, $text)
} catch {
    [System.IO.File]::WriteAllText($OutFile, "[ps] EX " + $_.Exception.ToString())
}
