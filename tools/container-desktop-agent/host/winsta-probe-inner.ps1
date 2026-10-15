# winsta-probe-inner.ps1 - runs as SYSTEM (scheduled task).
#
# Question: can a host process see and touch ANOTHER session's windows - i.e. the
# container's windows, which live in real host sessions (measured: the container's
# process tree is session 4, its console is session 5)? If yes, the container
# needs no capture agent at all: the host can enumerate the container's windows,
# read their pixels and post them input. C# 5 syntax only (Add-Type uses the
# in-box compiler on PS 5.1).
param(
    [int] $TargetSession = 4,
    [string] $OutDirectory = 'C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd'
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

public static class WinstaProbe
{
    const uint WINSTA_ALL = 0x37F;
    const uint DESKTOP_RIGHTS = 0x1C1;   // READOBJECTS|WRITEOBJECTS|ENUMERATE|SWITCHDESKTOP
    const int  SRCCOPY = 0x00CC0020;
    const uint PW_RENDERFULLCONTENT = 0x2;
    const uint DIRECTORY_QUERY = 0x0001;

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

    [StructLayout(LayoutKind.Sequential)]
    struct UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    struct OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }

    [StructLayout(LayoutKind.Sequential)]
    struct OBJECT_DIRECTORY_INFORMATION { public UNICODE_STRING Name; public UNICODE_STRING TypeName; }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);

    public static string Run(int targetSession, string outDir)
    {
        var log = new StringBuilder();
        try
        {
            var me = Process.GetCurrentProcess();
            log.AppendLine(string.Format("[probe] own pid={0} ownSession={1} user={2}", me.Id, me.SessionId, Environment.UserName));
            log.AppendLine(string.Format("[probe] ownWinSta={0} ownDesktop={1}", Name(GetProcessWindowStation()), Name(GetThreadDesktop(GetCurrentThreadId()))));

            // Discover the target session's User-object namespace, then try every
            // window station name we can see (plus the usual ones as a fallback).
            var candidates = new List<string>();
            var probes = new string[]
            {
                @"\Sessions",
                @"\Sessions\" + targetSession,
                @"\Sessions\" + targetSession + @"\Windows",
                @"\Sessions\" + targetSession + @"\Windows\WindowStations",
                @"\Sessions\5\Windows\WindowStations",
                @"\Windows\WindowStations"
            };
            foreach (var d in probes)
            {
                log.AppendLine("[ns] " + d);
                foreach (var n in ListDirectory(d, log))
                {
                    string full = d + @"\" + n;
                    if (!candidates.Contains(full)) candidates.Add(full);
                }
            }

            var fallbacks = new string[]
            {
                @"\Sessions\" + targetSession + @"\Windows\WindowStations\WinSta0",
                @"\Sessions\" + targetSession + @"\Windows\WindowStations\Service-0x0-3e7$",
                @"\Sessions\5\Windows\WindowStations\WinSta0"
            };
            foreach (var f in fallbacks) if (!candidates.Contains(f)) candidates.Add(f);

            IntPtr winsta = IntPtr.Zero;
            foreach (var full in candidates)
            {
                var h = OpenWindowStationW(full, false, WINSTA_ALL);
                int err = Marshal.GetLastWin32Error();
                log.AppendLine(string.Format("[open] OpenWindowStation(\"{0}\") -> 0x{1:X} err={2}", full, h.ToInt64(), err));
                if (h != IntPtr.Zero && winsta == IntPtr.Zero)
                {
                    winsta = h;
                    log.AppendLine("[open] USING " + full);
                    log.AppendLine("[open] children: " + string.Join(", ", ListDirectory(full, null).ToArray()));
                }
                else if (h != IntPtr.Zero)
                {
                    CloseWindowStation(h);
                }
            }

            if (winsta == IntPtr.Zero)
            {
                log.AppendLine("[open] FAILED: the host cannot open any window station of session " + targetSession);
                return log.ToString();
            }

            string body = "";
            var t = new Thread(delegate() { body = Work(winsta, outDir); });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            log.AppendLine(body);
            CloseWindowStation(winsta);
        }
        catch (Exception ex) { log.AppendLine("[probe] EX " + ex); }
        return log.ToString();
    }

    static List<string> ListDirectory(string path, StringBuilder log)
    {
        var found = new List<string>();
        IntPtr handle = IntPtr.Zero;
        IntPtr usBuf = IntPtr.Zero, oaBuf = IntPtr.Zero, nameBuf = IntPtr.Zero;
        try
        {
            nameBuf = Marshal.StringToHGlobalUni(path);
            var us = new UNICODE_STRING();
            us.Length = (ushort)(path.Length * 2);
            us.MaximumLength = (ushort)(path.Length * 2 + 2);
            us.Buffer = nameBuf;
            usBuf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UNICODE_STRING)));
            Marshal.StructureToPtr(us, usBuf, false);

            var oa = new OBJECT_ATTRIBUTES();
            oa.Length = Marshal.SizeOf(typeof(OBJECT_ATTRIBUTES));
            oa.ObjectName = usBuf;
            oaBuf = Marshal.AllocHGlobal(oa.Length);
            Marshal.StructureToPtr(oa, oaBuf, false);

            int st = NtOpenDirectoryObject(out handle, DIRECTORY_QUERY, oaBuf);
            if (st != 0)
            {
                if (log != null) log.AppendLine(string.Format("[ns] NtOpenDirectoryObject(\"{0}\") -> 0x{1:X8}", path, st));
                return found;
            }

            IntPtr buf = Marshal.AllocHGlobal(4096);
            try
            {
                uint context = 0, retLen = 0;
                bool restart = true;
                while (NtQueryDirectoryObject(handle, buf, 4096, false, restart, ref context, ref retLen) == 0)
                {
                    restart = false;
                    var info = (OBJECT_DIRECTORY_INFORMATION)Marshal.PtrToStructure(buf, typeof(OBJECT_DIRECTORY_INFORMATION));
                    string nm = Marshal.PtrToStringUni(info.Name.Buffer, info.Name.Length / 2);
                    string tp = Marshal.PtrToStringUni(info.TypeName.Buffer, info.TypeName.Length / 2);
                    if (!string.IsNullOrEmpty(nm))
                    {
                        found.Add(nm);
                        if (log != null) log.AppendLine(string.Format("[ns]   {0}  ({1})", nm, tp));
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { if (log != null) log.AppendLine("[ns] EX " + ex.Message); }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
            if (nameBuf != IntPtr.Zero) Marshal.FreeHGlobal(nameBuf);
            if (usBuf != IntPtr.Zero) Marshal.FreeHGlobal(usBuf);
            if (oaBuf != IntPtr.Zero) Marshal.FreeHGlobal(oaBuf);
        }
        return found;
    }

    static string Work(IntPtr winsta, string outDir)
    {
        var log = new StringBuilder();
        log.AppendLine(string.Format("[attach] SetProcessWindowStation -> {0} err={1}", SetProcessWindowStation(winsta), Marshal.GetLastWin32Error()));

        string[] desktops = new string[] { "Default", "Winlogon", "Disconnect" };
        IntPtr desktop = IntPtr.Zero;
        foreach (var dn in desktops)
        {
            var d = OpenDesktopW(dn, 0, false, DESKTOP_RIGHTS);
            int err = Marshal.GetLastWin32Error();
            log.AppendLine(string.Format("[attach] OpenDesktop(\"{0}\") -> 0x{1:X} err={2}", dn, d.ToInt64(), err));
            if (d != IntPtr.Zero) { desktop = d; break; }
        }
        if (desktop == IntPtr.Zero) return log.ToString();

        bool ok = SetThreadDesktop(desktop);
        log.AppendLine(string.Format("[attach] SetThreadDesktop -> {0} err={1}", ok, Marshal.GetLastWin32Error()));
        log.AppendLine(string.Format("[attach] thread desktop is now {0}", Name(GetThreadDesktop(GetCurrentThreadId()))));

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

        log.AppendLine(string.Format("[windows] visible+titled = {0}", windows.Count));
        foreach (var w in windows.OrderByDescending(delegate(Win x) { return x.W * x.Hh; }).Take(15)) log.AppendLine("  " + w.Describe());

        var top = windows.Where(delegate(Win w) { return w.W > 32 && w.Hh > 32; })
                         .OrderByDescending(delegate(Win w) { return w.W * w.Hh; })
                         .FirstOrDefault();
        if (top == null) { log.AppendLine("[capture] nothing large enough"); return log.ToString(); }

        log.AppendLine("[capture] target " + top.Describe());
        string p1 = Path.Combine(outDir, "winsta-bitblt.png");
        log.AppendLine(string.Format("[capture] BitBlt      ink={0:P1} file={1}", Capture(top, p1, false), p1));
        string p2 = Path.Combine(outDir, "winsta-printwindow.png");
        log.AppendLine(string.Format("[capture] PrintWindow ink={0:P1} file={1}", Capture(top, p2, true), p2));

        bool posted = PostMessage(top.H, 0x8000 + 1, IntPtr.Zero, IntPtr.Zero);
        log.AppendLine(string.Format("[input] PostMessage(WM_APP+1) -> {0} err={1}", posted, Marshal.GetLastWin32Error()));

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
        catch (Exception ex) { File.AppendAllText(path + ".err", ex.ToString()); return -1; }
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
                    byte b = Marshal.ReadByte(row, x * 4);
                    byte g = Marshal.ReadByte(row, x * 4 + 1);
                    byte r = Marshal.ReadByte(row, x * 4 + 2);
                    if (b > 8 || g > 8 || r > 8) nonBlack++;
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
    [DllImport("user32.dll", EntryPoint = "OpenWindowStationW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenWindowStationW(string name, bool inherit, uint access);
    [DllImport("user32.dll", EntryPoint = "CloseWindowStation", SetLastError = true)]
    static extern bool CloseWindowStation(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "SetProcessWindowStation", SetLastError = true)]
    static extern bool SetProcessWindowStation(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "OpenDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenDesktopW(string name, int flags, bool inherit, uint access);
    [DllImport("user32.dll", EntryPoint = "SetThreadDesktop", SetLastError = true)]
    static extern bool SetThreadDesktop(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "EnumWindows", SetLastError = true)]
    static extern bool EnumWindows(EnumProc proc, IntPtr lparam);
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
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")]
    static extern int NtOpenDirectoryObject(out IntPtr handle, uint access, IntPtr objectAttributes);
    [DllImport("ntdll.dll")]
    static extern int NtQueryDirectoryObject(IntPtr handle, IntPtr buffer, int length, bool singleEntry, bool restartScan, ref uint context, ref uint returnLength);
}
'@

$outFile = Join-Path $OutDirectory 'winsta-probe.out'
[void][System.IO.File]::WriteAllText($outFile, "")
try {
    $text = [WinstaProbe]::Run($TargetSession, $OutDirectory)
    Add-Content -Path $outFile -Value $text -Encoding UTF8
} catch {
    Add-Content -Path $outFile -Value ("[ps] EX " + $_.Exception.ToString()) -Encoding UTF8
}
Add-Content -Path $outFile -Value ("[done] " + (Get-Date -Format 'HH:mm:ss')) -Encoding UTF8
