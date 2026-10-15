using System.Runtime.InteropServices;
using System.Text;

namespace HcsProbe;

internal static class Native
{
    private const string ComputeCore = "computecore.dll";
    public const int HcsEOperationPending = unchecked((int)0xC0370103);
    public const int HcsEOperationTimeout = unchecked((int)0xC0370104);
    public const uint GenericAll = 0x10000000;

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern IntPtr HcsCreateOperation(IntPtr context, IntPtr callback);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern int HcsWaitForOperationResult(IntPtr operation, uint timeoutMs, out IntPtr resultDocument);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern void HcsCloseOperation(IntPtr operation);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsCreateComputeSystem(string id, string configuration, IntPtr operation, IntPtr sd, out IntPtr computeSystem);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsOpenComputeSystem(string id, uint access, out IntPtr computeSystem);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern void HcsCloseComputeSystem(IntPtr computeSystem);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsStartComputeSystem(IntPtr computeSystem, IntPtr operation, string options);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsTerminateComputeSystem(IntPtr computeSystem, IntPtr operation, string options);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsGetComputeSystemProperties(IntPtr computeSystem, IntPtr operation, string query);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsEnumerateComputeSystems(string query, IntPtr operation);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsCreateProcess(IntPtr computeSystem, string parameters, IntPtr operation, IntPtr sd, out IntPtr process);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern int HcsWaitForProcessExit(IntPtr process, uint timeoutMs, out IntPtr resultDocument);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern int HcsWaitForOperationResultAndProcessInfo(IntPtr operation, uint timeoutMs, out ProcessInformation info, out IntPtr resultDocument);

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public uint ProcessId;
        public uint Reserved;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern void HcsCloseProcess(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(IntPtr hFile, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    // ---- WCOW layer APIs (vmcompute.dll) ----

    [StructLayout(LayoutKind.Sequential)]
    public struct DriverInfo
    {
        public int Flavour;   // 0 = DiffDriver, 1 = FilterDriver
        public IntPtr HomeDir;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LayerDescriptor
    {
        public Guid LayerId;
        public uint Flags;
        public IntPtr Path;
    }

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int ActivateLayer(ref DriverInfo info, [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int DeactivateLayer(ref DriverInfo info, [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int CreateLayer(ref DriverInfo info, [MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string parent);

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int CreateSandboxLayer(ref DriverInfo info, [MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr parent, [In] LayerDescriptor[] descriptors, UIntPtr count);

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int DestroyLayer(ref DriverInfo info, [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int GetLayerMountPath(ref DriverInfo info, [MarshalAs(UnmanagedType.LPWStr)] string id, ref UIntPtr length, IntPtr buffer);

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int NameToGuid([MarshalAs(UnmanagedType.LPWStr)] string name, out Guid guid);

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int LayerExists(ref DriverInfo info, [MarshalAs(UnmanagedType.LPWStr)] string id, out uint exists);

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int PrepareLayer(ref DriverInfo info, [MarshalAs(UnmanagedType.LPWStr)] string id, [In] LayerDescriptor[] descriptors, UIntPtr count);

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int UnprepareLayer(ref DriverInfo info, [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int ImportLayer(ref DriverInfo info, [MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.LPWStr)] string importFolderPath, [In] LayerDescriptor[]? descriptors, UIntPtr count);

    [DllImport("vmcompute.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int ProcessBaseImage([MarshalAs(UnmanagedType.LPWStr)] string path);

    public static string? Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); } finally { LocalFree(p); }
    }
}

internal sealed class Operation : IDisposable
{
    private IntPtr _h;
    public Operation() => _h = Native.HcsCreateOperation(IntPtr.Zero, IntPtr.Zero);
    public IntPtr Handle => _h;
    public (int hr, string? doc) Complete(int callResult, string name, uint timeoutMs = 60_000)
    {
        var wait = Native.HcsWaitForOperationResult(_h, timeoutMs, out var docPtr);
        var doc = Native.Take(docPtr);
        if (callResult != Native.HcsEOperationPending && callResult < 0) return (callResult, doc);
        if (wait < 0) return (wait, doc);
        return (callResult, doc);
    }
    public void Dispose() { if (_h != IntPtr.Zero) { Native.HcsCloseOperation(_h); _h = IntPtr.Zero; } }
}

internal static class Layers
{
    private static Native.DriverInfo Info = MakeDriverInfo();

    private static Native.DriverInfo MakeDriverInfo()
    {
        // HomeDir = pointer to a UTF-16 NUL (matches hcsshim's &utf16EmptyString).
        var ptr = Marshal.StringToHGlobalUni(string.Empty);
        return new Native.DriverInfo { Flavour = 1, HomeDir = ptr };
    }

    public static Guid NameToGuid(string path)
    {
        var hr = Native.NameToGuid(path, out var g);
        if (hr < 0) throw new InvalidOperationException($"NameToGuid(0x{hr:X8}) for {path}");
        return g;
    }

    public static string MountPath(string path)
    {
        UIntPtr len = UIntPtr.Zero;
        var hr = Native.GetLayerMountPath(ref Info, path, ref len, IntPtr.Zero);
        if (hr < 0) throw new InvalidOperationException($"GetLayerMountPath(1) 0x{hr:X8} for {path}");
        var n = (int)len;
        if (n == 0) return string.Empty;
        var buf = Marshal.AllocHGlobal(n * 2);
        try
        {
            hr = Native.GetLayerMountPath(ref Info, path, ref len, buf);
            if (hr < 0) throw new InvalidOperationException($"GetLayerMountPath(2) 0x{hr:X8} for {path}");
            return Marshal.PtrToStringUni(buf) ?? string.Empty;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static Native.LayerDescriptor[] Descriptors(params string[] parents)
    {
        var result = new Native.LayerDescriptor[parents.Length];
        for (var i = 0; i < parents.Length; i++)
        {
            result[i] = new Native.LayerDescriptor
            {
                LayerId = NameToGuid(parents[i]),
                Flags = 0,
                Path = Marshal.StringToHGlobalUni(parents[i]),
            };
        }
        return result;
    }

    public static Native.LayerDescriptor[] DescriptorsForHcs(params string[] parents)
    {
        var result = new Native.LayerDescriptor[parents.Length];
        for (var i = 0; i < parents.Length; i++)
        {
            result[i] = new Native.LayerDescriptor
            {
                LayerId = NameToGuid(parents[i]),
                Flags = 0,
                Path = Marshal.StringToHGlobalUni(MountPath(parents[i])),
            };
        }
        return result;
    }

    public static int Activate(string path) => Native.ActivateLayer(ref Info, path);
    public static int Deactivate(string path) => Native.DeactivateLayer(ref Info, path);
    public static int CreateLayer(string path, string parent) => Native.CreateLayer(ref Info, path, parent);
    public static int Destroy(string path) => Native.DestroyLayer(ref Info, path);
    public static int Exists(string path) { var hr = Native.LayerExists(ref Info, path, out var e); return hr < 0 ? hr : (e != 0 ? 1 : 0); }
    public static int CreateSandbox(string path, params string[] parents)
    {
        var d = Descriptors(parents);
        return Native.CreateSandboxLayer(ref Info, path, IntPtr.Zero, d, (UIntPtr)(uint)d.Length);
    }

    public static int Prepare(string path, params string[] parents)
    {
        var d = Descriptors(parents);
        return Native.PrepareLayer(ref Info, path, d, (UIntPtr)(uint)d.Length);
    }

    public static int PrepareWithId(string path, Guid id, string parent)
    {
        var d = new[] { new Native.LayerDescriptor { LayerId = id, Flags = 0, Path = Marshal.StringToHGlobalUni(parent) } };
        return Native.PrepareLayer(ref Info, path, d, (UIntPtr)1u);
    }

    public static int Unprepare(string path) => Native.UnprepareLayer(ref Info, path);

    public static int ProcessBase(string path) => Native.ProcessBaseImage(path);

    public static int Import(string layer, string importFolder, params string[] parents)
    {
        var d = parents.Length == 0 ? Array.Empty<Native.LayerDescriptor>() : Descriptors(parents);
        return Native.ImportLayer(ref Info, layer, importFolder, d, (UIntPtr)(uint)d.Length);
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 2; }
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "list": return List();
                case "create": return Create(args[1], args[2]);
                case "start": return Start(args[1]);
                case "props": return Props(args[1]);
                case "exec": return Exec(args[1], string.Join(' ', args.Skip(2)));
                case "sh":
                    Console.WriteLine(ExecCapture(args[1], string.Join(' ', args.Skip(2))));
                    return 0;
                case "shf":
                    Console.WriteLine(ExecCapture(args[1], File.ReadAllText(args[2]).Trim()));
                    return 0;
                case "stop": return Stop(args[1]);
                case "lname": Console.WriteLine(Layers.NameToGuid(args[1])); return 0;
                case "lmount": Console.WriteLine(Layers.MountPath(args[1])); return 0;
                case "lactivate": Console.WriteLine($"activate hr=0x{Layers.Activate(args[1]):X8}"); return 0;
                case "ldeactivate": Console.WriteLine($"deactivate hr=0x{Layers.Deactivate(args[1]):X8}"); return 0;
                case "lunprepare": Console.WriteLine($"unprepare hr=0x{Layers.Unprepare(args[1]):X8}"); return 0;
                case "lprepare": Console.WriteLine($"prepare hr=0x{Layers.Prepare(args[1], args[2].Split(',')):X8}"); return 0;
                case "lprocessbase": Console.WriteLine($"processbase hr=0x{Layers.ProcessBase(args[1]):X8}"); return 0;
                case "limport": Console.WriteLine($"import hr=0x{Layers.Import(args[1], args[2], args.Length > 3 ? args[3].Split(',') : Array.Empty<string>()):X8}"); return 0;
                case "lcreate": Console.WriteLine($"createLayer hr=0x{Layers.CreateLayer(args[1], args[2]):X8}"); return 0;
                case "ldestroy": Console.WriteLine($"destroy hr=0x{Layers.Destroy(args[1]):X8}"); return 0;
                case "lexists": Console.WriteLine($"exists hr=0x{Layers.Exists(args[1]):X8}"); return 0;
                case "lsandbox":
                    Console.WriteLine($"createSandbox hr=0x{Layers.CreateSandbox(args[1], args[2].Split(',')):X8}");
                    Console.WriteLine($"sandbox.vhdx exists={File.Exists(Path.Combine(args[1], "sandbox.vhdx"))}");
                    return 0;
                case "argon": return Argon(args[1], args[2], args[3].Split(','), args.Length > 4 ? string.Join(' ', args.Skip(4)) : "cmd.exe /c whoami");
                case "run": return RunScript(args[1], args[2], args[3], args.Length > 4 ? int.Parse(args[4]) : 0);
                case "argonnet": return ArgonNet(args[1], args[2], args[3], args[4], args.Length > 5 ? string.Join(' ', args.Skip(5)) : "cmd.exe /c whoami && ipconfig", args.Length > 6 ? int.Parse(args[6]) : 0);
                case "argonnet2": return ArgonNet2(args[1], args[2], args[3], args[4]);
                case "argonnet3": return ArgonNet3(args[1], args[2], args[3], args[4]);
                case "rdp": return Rdp(args[1], args[2], args[3], args[4]);
                case "ipquery": return IpQuery(args[1]);
                case "rprobe": return RdpProbe(args[1], args.Length > 2 ? int.Parse(args[2]) : 3389);
                case "hns": Console.WriteLine(Hns(args[1], args[2], args.Length > 3 ? args[3] : string.Empty)); return 0;
                case "hnsnat":
                    Console.WriteLine(Hns("POST", "/networks/",
                        "{\"Name\":\"wsm-nat\",\"Type\":\"NAT\",\"Subnets\":[{\"AddressPrefix\":\"192.168.248.0/24\",\"GatewayAddress\":\"192.168.248.1\"}]}"));
                    return 0;
                case "variants": return Variants(args[1], args[2], args[3]);
                case "deep": return Deep(args[1], args[2]);
                case "probe": return Probe(args[1], args[2], args.Length > 3 ? string.Join(' ', args.Skip(3)) : "cmd.exe /c echo hello");
                default: Usage(); return 2;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("EX: " + ex); return 1; }
    }

    private static void Usage()
        => Console.WriteLine("hcsprobe list | create <id> <json> | start <id> | props <id> | exec <id> <cmd> | sh <id> <cmd> | stop <id> | probe <id> <json> [cmd]");

    private static int List()
    {
        using var op = new Operation();
        var hr = Native.HcsEnumerateComputeSystems("{}", op.Handle);
        var (r, doc) = op.Complete(hr, "enumerate");
        Console.WriteLine($"hr=0x{hr:X8} wait=0x{r:X8}");
        Console.WriteLine(doc);
        return 0;
    }

    private static int Create(string id, string jsonPath) => Create(id, jsonPath, keepHandle: false);

    private static int Create(string id, string jsonPath, bool keepHandle)
    {
        var json = File.ReadAllText(jsonPath);
        Console.WriteLine("---- config ----");
        Console.WriteLine(json);
        using var op = new Operation();
        var hr = Native.HcsCreateComputeSystem(id, json, op.Handle, IntPtr.Zero, out var handle);
        var (r, doc) = op.Complete(hr, "create");
        Console.WriteLine($"create hr=0x{hr:X8} handle={(handle == IntPtr.Zero ? "null" : "ok")} result=0x{r:X8}");
        if (!string.IsNullOrWhiteSpace(doc)) Console.WriteLine("doc: " + doc);
        if (handle == IntPtr.Zero || r < 0) { Console.Error.WriteLine($"CREATE FAILED 0x{r:X8}"); return 1; }
        if (!keepHandle) Native.HcsCloseComputeSystem(handle);
        Console.WriteLine("CREATED OK");
        return 0;
    }

    /// <summary>Creates the system and returns its handle (kept open so it is not terminated).</summary>
    private static IntPtr CreateKeepHandle(string id, string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        using var op = new Operation();
        var hr = Native.HcsCreateComputeSystem(id, json, op.Handle, IntPtr.Zero, out var handle);
        var (r, doc) = op.Complete(hr, "create");
        Console.WriteLine($"create hr=0x{hr:X8} handle={(handle == IntPtr.Zero ? "null" : "ok")} result=0x{r:X8}");
        if (!string.IsNullOrWhiteSpace(doc)) Console.WriteLine("doc: " + doc);
        if (handle == IntPtr.Zero || r < 0) throw new InvalidOperationException($"create failed 0x{r:X8} {doc}");
        return handle;
    }

    private static int StartHandle(IntPtr handle)
    {
        using var op = new Operation();
        var hr = Native.HcsStartComputeSystem(handle, op.Handle, "{}");
        var (r, doc) = op.Complete(hr, "start", 120_000);
        Console.WriteLine($"start hr=0x{hr:X8} result=0x{r:X8}");
        if (!string.IsNullOrWhiteSpace(doc)) Console.WriteLine("doc: " + doc);
        return (hr == Native.HcsEOperationPending || hr >= 0) && r >= 0 ? 0 : 1;
    }

    private static int Start(string id)
    {
        if (Native.HcsOpenComputeSystem(id, Native.GenericAll, out var h) < 0 || h == IntPtr.Zero)
        { Console.Error.WriteLine("open failed"); return 1; }
        try
        {
            using var op = new Operation();
            var hr = Native.HcsStartComputeSystem(h, op.Handle, "{}");
            var (r, doc) = op.Complete(hr, "start", 120_000);
            Console.WriteLine($"start hr=0x{hr:X8} result=0x{r:X8}");
            if (!string.IsNullOrWhiteSpace(doc)) Console.WriteLine("doc: " + doc);
            if (r < 0 && hr != Native.HcsEOperationPending) { Console.Error.WriteLine($"START FAILED 0x{r:X8}"); return 1; }
            if (r < 0) { Console.Error.WriteLine($"START FAILED 0x{r:X8}"); return 1; }
            Console.WriteLine("STARTED OK");
            return 0;
        }
        finally { Native.HcsCloseComputeSystem(h); }
    }

    private static int Props(string id)
    {
        if (Native.HcsOpenComputeSystem(id, Native.GenericAll, out var h) < 0 || h == IntPtr.Zero)
        { Console.Error.WriteLine("open failed"); return 1; }
        try
        {
            using var op = new Operation();
            var hr = Native.HcsGetComputeSystemProperties(h, op.Handle, "{}");
            var (r, doc) = op.Complete(hr, "props");
            Console.WriteLine($"props hr=0x{hr:X8} result=0x{r:X8}");
            Console.WriteLine(doc);
            return 0;
        }
        finally { Native.HcsCloseComputeSystem(h); }
    }

    private static int Stop(string id)
    {
        if (Native.HcsOpenComputeSystem(id, Native.GenericAll, out var h) < 0 || h == IntPtr.Zero)
        { Console.Error.WriteLine("open failed"); return 1; }
        try
        {
            using var op = new Operation();
            var hr = Native.HcsTerminateComputeSystem(h, op.Handle, "{}");
            var (r, doc) = op.Complete(hr, "terminate", 60_000);
            Console.WriteLine($"terminate hr=0x{hr:X8} result=0x{r:X8} {doc}");
            return 0;
        }
        finally { Native.HcsCloseComputeSystem(h); }
    }

    private static int Exec(string id, string command)
    {
        var (exit, so, se) = Run(id, command);
        Console.WriteLine($"exit={exit}");
        Console.WriteLine("stdout: " + so);
        Console.WriteLine("stderr: " + se);
        return exit;
    }

    private static string ExecCapture(string id, string command)
    {
        var (exit, so, se) = Run(id, command);
        return $"[exit={exit}] {so}{(string.IsNullOrEmpty(se) ? "" : " |stderr:" + se)}";
    }

    private static (int exit, string so, string se) Run(string id, string command)
    {
        if (Native.HcsOpenComputeSystem(id, Native.GenericAll, out var h) < 0 || h == IntPtr.Zero)
            throw new InvalidOperationException("open failed");
        try
        {
            var parameters = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["CommandLine"] = command,
                ["CreateStdInPipe"] = false,
                ["CreateStdOutPipe"] = true,
                ["CreateStdErrPipe"] = true,
            });
            using var op = new Operation();
            var hr = Native.HcsCreateProcess(h, parameters, op.Handle, IntPtr.Zero, out var p);
            var waitHr = Native.HcsWaitForOperationResultAndProcessInfo(op.Handle, 60_000, out var info, out var docPtr);
            var doc = Native.Take(docPtr);
            if (hr != Native.HcsEOperationPending && hr < 0) throw new InvalidOperationException($"createprocess 0x{hr:X8} {doc}");
            if (waitHr < 0) throw new InvalidOperationException($"createprocess-wait 0x{waitHr:X8} {doc}");
            if (p == IntPtr.Zero) throw new InvalidOperationException("null process handle");
            try
            {
                var so = ReadPipe(info.StdOutput);
                var se = ReadPipe(info.StdError);
                var exit = WaitExit(p);
                return (exit, so, se);
            }
            finally { Native.HcsCloseProcess(p); }
        }
        finally { Native.HcsCloseComputeSystem(h); }
    }

    private static int WaitExit(IntPtr p)
    {
        while (true)
        {
            var hr = Native.HcsWaitForProcessExit(p, 30_000, out var docPtr);
            var doc = Native.Take(docPtr);
            if (hr >= 0)
            {
                var exit = 0;
                if (!string.IsNullOrWhiteSpace(doc))
                {
                    try { using var d = System.Text.Json.JsonDocument.Parse(doc); if (d.RootElement.TryGetProperty("ExitCode", out var e)) exit = e.GetInt32(); } catch { }
                }
                return exit;
            }
            if (hr != Native.HcsEOperationTimeout) throw new InvalidOperationException($"wait 0x{hr:X8} {doc}");
        }
    }

    private static string ReadPipe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        try
        {
            var buf = new byte[4096];
            using var ms = new MemoryStream();
            while (Native.ReadFile(h, buf, (uint)buf.Length, out var read, IntPtr.Zero) && read > 0)
                ms.Write(buf, 0, (int)read);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        finally { Native.CloseHandle(h); }
    }

    private static int Deep(string scratchRoot, string parentDir)
    {
        Directory.CreateDirectory(scratchRoot);
        var dir = Path.Combine(Path.GetTempPath(), "opencode", "wsmdiag");
        var nameId = Layers.NameToGuid(parentDir);
        var regId = new Guid(Path.GetFileName(parentDir));
        var metaId = Guid.Parse("6e5339aa-58a5-4c79-9466-639779d313bc");

        var ids = new (string Name, Guid? Id, Guid PrepareId)[]
        {
            ("name", nameId, nameId),
            ("reg", regId, regId),
            ("meta", metaId, metaId),
            ("prepname-hcsreg", regId, nameId),
            ("noid", null, nameId),
        };
        var paths = new (string Name, string Path)[]
        {
            ("dir", parentDir),
            ("dirfiles", Path.Combine(parentDir, "Files")),
        };
        var slashes = new[] { true, false };

        var index = 0;
        foreach (var idv in ids)
            foreach (var pv in paths)
                foreach (var slash in slashes)
                {
                    index++;
                    var name = $"{idv.Name}-{pv.Name}-{(slash ? "bs" : "nobs")}";
                    var scratch = Path.Combine(scratchRoot, $"s{index}");
                    try { if (Directory.Exists(scratch)) { Layers.Unprepare(scratch); Layers.Deactivate(scratch); Directory.Delete(scratch, true); } } catch { }
                    var hrC = Layers.CreateSandbox(scratch, parentDir);
                    var hrA = Layers.Activate(scratch);
                    // Prepare uses the descriptor id under test.
                    var hrP = Layers.PrepareWithId(scratch, idv.PrepareId, parentDir);
                    var root = Layers.MountPath(scratch);
                    if (slash && !root.EndsWith('\\')) root += '\\';

                    var layerObj = new Dictionary<string, object?> { ["Path"] = pv.Path };
                    if (idv.Id is { } g) layerObj["Id"] = g.ToString();
                    var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["SchemaVersion"] = new Dictionary<string, object?> { ["Major"] = 2, ["Minor"] = 1 },
                        ["Owner"] = "WsmProbe",
                        ["ShouldTerminateOnLastHandleClosed"] = true,
                        ["Container"] = new Dictionary<string, object?>
                        {
                            ["GuestOs"] = new Dictionary<string, object?> { ["HostName"] = "wsmprobe" },
                            ["Storage"] = new Dictionary<string, object?>
                            {
                                ["Path"] = root,
                                ["Layers"] = new object[] { layerObj },
                            },
                        },
                    });
                    var f = Path.Combine(dir, $"deep_{name}.json");
                    File.WriteAllText(f, json);
                    var vid = $"wsmprobe-deep-{index}";
                    Console.WriteLine($"### {name}: create=0x{hrC:X8} activate=0x{hrA:X8} prepare=0x{hrP:X8}");
                    Console.WriteLine($"    json={json}");
                    var rc = Create(vid, f);
                    Console.WriteLine($"    create-result rc={rc}");
                    if (rc == 0)
                    {
                        Console.WriteLine("    *** CREATE OK ***");
                        var sr = Start(vid);
                        Console.WriteLine($"    start rc={sr}");
                        if (sr == 0)
                        {
                            Thread.Sleep(3000);
                            Exec(vid, "cmd.exe /c whoami && ver");
                        }
                        Stop(vid);
                    }
                }

        return 0;
    }

    private static int Variants(string id, string scratchDir, string parent)
    {
        Directory.CreateDirectory(scratchDir);
        var hr0 = Layers.CreateSandbox(scratchDir, parent);
        Console.WriteLine($"CreateSandboxLayer hr=0x{hr0:X8} vhdx={File.Exists(Path.Combine(scratchDir, "sandbox.vhdx"))}");
        var hr1 = Layers.Activate(scratchDir);
        Console.WriteLine($"ActivateLayer hr=0x{hr1:X8}");
        var hrp = Layers.Prepare(scratchDir, parent);
        Console.WriteLine($"PrepareLayer hr=0x{hrp:X8}");
        var root = Layers.MountPath(scratchDir);
        if (!root.EndsWith('\\')) root += '\\';
        Console.WriteLine($"root = {root}");

        var nameId = Layers.NameToGuid(parent);
        var regId = new Guid(System.IO.Path.GetFileName(parent));
        const string metadataId = "6e5339aa-58a5-4c79-9466-639779d313bc";
        var mountParent = Layers.MountPath(parent);

        object[] Layer(string? id2, string path) => new object[] { new Dictionary<string, object?> { ["Id"] = id2, ["Path"] = path } };
        object[] LayerNoId(string path) => new object[] { new Dictionary<string, object?> { ["Path"] = path } };

        var variants = new (string Name, object? Layers)[]
        {
            ("empty-layers", Array.Empty<object>()),
            ("name-guid", Layer(nameId.ToString(), mountParent)),
            ("reg-guid", Layer(regId.ToString(), mountParent)),
            ("meta-guid", Layer(metadataId, mountParent)),
            ("no-id", LayerNoId(mountParent)),
            ("no-storage-layers-key", null),
        };

        var dir = Path.Combine(Path.GetTempPath(), "opencode", "wsmdiag");
        foreach (var (name, layersVal) in variants)
        {
            var container = new Dictionary<string, object?>
            {
                ["GuestOs"] = new Dictionary<string, object?> { ["HostName"] = "wsmprobe" },
            };
            var storage = new Dictionary<string, object?> { ["Path"] = root };
            if (layersVal is not null) storage["Layers"] = layersVal;
            container["Storage"] = storage;

            var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["SchemaVersion"] = new Dictionary<string, object?> { ["Major"] = 2, ["Minor"] = 1 },
                ["Owner"] = "WsmProbe",
                ["ShouldTerminateOnLastHandleClosed"] = true,
                ["Container"] = container,
            });
            var f = Path.Combine(dir, $"v_{name}.json");
            File.WriteAllText(f, json);
            Console.WriteLine($"----- variant {name}: {json}");
            var variantId = $"wsmprobe-{name}";
            var rc = Create(variantId, f);
            Console.WriteLine($"     create rc={rc}");
            if (rc == 0)
            {
                var sr = Start(variantId);
                Console.WriteLine($"     start rc={sr}");
                if (sr == 0)
                {
                    Thread.Sleep(2000);
                    Exec(variantId, "cmd.exe /c whoami");
                }
                Stop(variantId);
            }
        }
        return 0;
    }

    private static int ArgonNet(string id, string scratchDir, string parent, string networkId, string command, int holdSeconds)
    {
        Directory.CreateDirectory(scratchDir);
        var hrC = Layers.CreateSandbox(scratchDir, parent);
        if (hrC < 0) { Console.Error.WriteLine($"CreateSandboxLayer 0x{hrC:X8}"); return 1; }
        var noPrep = Environment.GetEnvironmentVariable("WSMPROBE_NOPREP") is { Length: > 0 };
        var hrA = noPrep ? 0 : Layers.Activate(scratchDir);
        var hrP = noPrep ? 0 : Layers.Prepare(scratchDir, parent);
        var root = Layers.MountPath(scratchDir);
        var layerId = Layers.NameToGuid(parent);
        Console.WriteLine($"create=0x{hrC:X8} activate=0x{hrA:X8} prepare=0x{hrP:X8} root={root}");

        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SystemType"] = "Container",
            ["Name"] = "wsmprobe",
            ["Owner"] = "WsmProbe",
            ["LayerFolderPath"] = scratchDir,
            ["Layers"] = new object[] { new Dictionary<string, object?> { ["Id"] = layerId.ToString(), ["Path"] = parent } },
            ["HvPartition"] = false,
            ["TerminateOnLastHandleClosed"] = true,
            ["NetworkEndpoints"] = new object[] { new Dictionary<string, object?> { ["NetworkId"] = networkId, ["EndpointName"] = "wsmprobe-container" } },
        });
        var jsonFile = Path.Combine(Path.GetTempPath(), "opencode", "wsmdiag", "argonnet.json");
        File.WriteAllText(jsonFile, json);
        Console.WriteLine(json);

        var handle = CreateKeepHandle(id, jsonFile);
        if (StartHandle(handle) != 0) { Native.HcsCloseComputeSystem(handle); return 1; }
        Console.WriteLine("STARTED OK");
        Thread.Sleep(6000);
        Exec(id, command);
        Console.WriteLine("== hns endpoints ==");
        IpQuery(id);
        if (holdSeconds > 0) { Console.WriteLine($"holding {holdSeconds}s"); Thread.Sleep(holdSeconds * 1000); }
        Native.HcsCloseComputeSystem(handle);
        return 0;
    }

    private static int RunScript(string id, string jsonFile, string command, int holdSeconds)
    {
        var handle = CreateKeepHandle(id, jsonFile);
        if (StartHandle(handle) != 0)
        {
            Native.HcsCloseComputeSystem(handle);
            return 1;
        }

        Console.WriteLine("STARTED OK");
        Thread.Sleep(4000);
        Exec(id, command);
        if (holdSeconds > 0)
        {
            Console.WriteLine($"holding {holdSeconds}s");
            Thread.Sleep(holdSeconds * 1000);
        }

        Native.HcsCloseComputeSystem(handle);
        return 0;
    }

    private static int IpQuery(string id)
    {
        // Uses the HNS dispatcher: GET /endpoints/ and match by compute system id.
        var hr = HnsCall("GET", "/endpoints/", "", out var ptr);
        var json = Marshal.PtrToStringUni(ptr);
        if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        Console.WriteLine($"hns hr=0x{hr:X8}");
        Console.WriteLine(json);
        return 0;
    }

    private static int Rdp(string id, string scratchDir, string parent, string natId)
    {
        // namespace + endpoint + attach
        var nsId = HnsOutputProp("POST", "/namespaces/", "{\"SchemaVersion\":{\"Major\":2,\"Minor\":1}}", "ID");
        Console.WriteLine($"namespace={nsId}");
        var epBody = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["VirtualNetwork"] = natId,
            ["Name"] = "wsmrdp-" + Guid.NewGuid().ToString("N")[..8],
        });
        var epId = HnsOutputProp("POST", "/endpoints/", epBody, "ID");
        Console.WriteLine($"endpoint={epId}");
        Console.WriteLine(Hns("POST", $"/namespaces/{nsId}/addresource", "{\"Type\":\"Endpoint\",\"Data\":{\"Id\":\"" + epId + "\"}}"));

        Directory.CreateDirectory(scratchDir);
        Layers.CreateSandbox(scratchDir, parent);
        Layers.Activate(scratchDir);
        Layers.Prepare(scratchDir, parent);
        var root = Layers.MountPath(scratchDir);
        var layerId = Layers.NameToGuid(parent);

        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = new Dictionary<string, object?> { ["Major"] = 2, ["Minor"] = 1 },
            ["Owner"] = "WsmProbe",
            ["ShouldTerminateOnLastHandleClosed"] = true,
            ["Container"] = new Dictionary<string, object?>
            {
                ["GuestOs"] = new Dictionary<string, object?> { ["HostName"] = "wsmrdp" },
                ["Storage"] = new Dictionary<string, object?>
                {
                    ["Path"] = root,
                    ["Layers"] = new object[] { new Dictionary<string, object?> { ["Id"] = layerId.ToString(), ["Path"] = parent } },
                },
                ["Networking"] = new Dictionary<string, object?> { ["Namespace"] = nsId },
            },
        });
        var jsonFile = Path.Combine(Path.GetTempPath(), "opencode", "wsmdiag", "rdp.json");
        File.WriteAllText(jsonFile, json);

        var handle = CreateKeepHandle(id, jsonFile);
        if (StartHandle(handle) != 0) { Native.HcsCloseComputeSystem(handle); return 1; }
        Console.WriteLine("STARTED OK");
        Thread.Sleep(10000);

        // Enable RDP inside the container.
        Console.WriteLine("== ipconfig ==");
        Exec(id, "cmd.exe /c ipconfig");
        Console.WriteLine("== enable rdp ==");
        Exec(id, "cmd.exe /c reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\" /v fDenyTSConnections /t REG_DWORD /d 0 /f");
        Exec(id, "cmd.exe /c reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\" /v ContainerMode /t REG_DWORD /d 0 /f");
        Exec(id, "cmd.exe /c net user ContainerAdministrator P@ssw0rd12345!");
        Exec(id, "cmd.exe /c sc config TermService start= auto & net start TermService");
        Exec(id, "cmd.exe /c netsh advfirewall firewall set rule group=\"remote desktop\" new enable=Yes");
        Console.WriteLine("== netstat ==");
        Exec(id, "cmd.exe /c netstat -ano | findstr :3389");
        Exec(id, "cmd.exe /c qwinsta");

        if (int.TryParse(Environment.GetEnvironmentVariable("WSMPROBE_HOLD"), out var hold) && hold > 0)
        {
            Console.WriteLine($"holding {hold}s");
            Thread.Sleep(hold * 1000);
        }

        Native.HcsCloseComputeSystem(handle);
        return 0;
    }

    private static string? HnsOutputProp(string method, string path, string body, string prop)
    {
        var outText = Hns(method, path, body);
        Console.WriteLine(outText.Length > 400 ? outText[..400] : outText);
        var idx = outText.IndexOf('\n');
        if (idx < 0) return null;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(outText[(idx + 1)..]);
            if (!d.RootElement.TryGetProperty("Output", out var o)) return null;
            if (o.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in o.EnumerateArray())
                {
                    if (el.TryGetProperty(prop, out var p)) return p.GetString();
                }
                return null;
            }
            return o.TryGetProperty(prop, out var pv) ? pv.GetString() : null;
        }
        catch { return null; }
    }

    private static int ArgonNet3(string id, string scratchDir, string parent, string natId)
    {
        var nsId = HnsOutputProp("POST", "/namespaces/", "{\"SchemaVersion\":{\"Major\":2,\"Minor\":1}}", "ID");
        Console.WriteLine($"namespace id={nsId}");
        if (string.IsNullOrEmpty(nsId)) return 1;

        var epName = "wsmprobe-ep-" + Guid.NewGuid().ToString("N")[..8];
        var epBody = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["VirtualNetwork"] = natId,
            ["Name"] = epName,
        });
        var epId = HnsOutputProp("POST", "/endpoints/", epBody, "ID");
        Console.WriteLine($"endpoint id={epId}");
        if (string.IsNullOrEmpty(epId)) return 1;

        Console.WriteLine("== add endpoint to namespace ==");
        Console.WriteLine(Hns("POST", $"/namespaces/{nsId}/addresource",
            "{\"Type\":\"Endpoint\",\"Data\":{\"Id\":\"" + epId + "\"}}"));

        Directory.CreateDirectory(scratchDir);
        Layers.CreateSandbox(scratchDir, parent);
        Layers.Activate(scratchDir);
        Layers.Prepare(scratchDir, parent);
        var root = Layers.MountPath(scratchDir);
        var layerId = Layers.NameToGuid(parent);

        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = new Dictionary<string, object?> { ["Major"] = 2, ["Minor"] = 1 },
            ["Owner"] = "WsmProbe",
            ["ShouldTerminateOnLastHandleClosed"] = true,
            ["Container"] = new Dictionary<string, object?>
            {
                ["GuestOs"] = new Dictionary<string, object?> { ["HostName"] = "wsmprobe" },
                ["Storage"] = new Dictionary<string, object?>
                {
                    ["Path"] = root,
                    ["Layers"] = new object[] { new Dictionary<string, object?> { ["Id"] = layerId.ToString(), ["Path"] = parent } },
                },
                ["Networking"] = new Dictionary<string, object?>
                {
                    ["Namespace"] = nsId,
                },
            },
        });
        var jsonFile = Path.Combine(Path.GetTempPath(), "opencode", "wsmdiag", "argonnet3.json");
        File.WriteAllText(jsonFile, json);
        Console.WriteLine(json);

        var handle = CreateKeepHandle(id, jsonFile);
        if (StartHandle(handle) != 0) { Native.HcsCloseComputeSystem(handle); return 1; }
        Console.WriteLine("STARTED OK");
        Thread.Sleep(8000);
        Exec(id, "cmd.exe /c ipconfig");
        if (int.TryParse(Environment.GetEnvironmentVariable("WSMPROBE_HOLD"), out var hold) && hold > 0)
        {
            Console.WriteLine($"holding {hold}s");
            Thread.Sleep(hold * 1000);
        }
        Native.HcsCloseComputeSystem(handle);
        return 0;
    }

    private static int ArgonNet2(string id, string scratchDir, string parent, string natId)
    {
        // 1. Create an HNS endpoint on the NAT network.
        var epName = "wsmprobe-ep-" + Guid.NewGuid().ToString("N")[..8];
        var epReq = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["VirtualNetwork"] = natId,
            ["Name"] = epName,
        });
        Console.WriteLine("== create endpoint ==");
        var epOut = Hns("POST", "/endpoints/", epReq);
        Console.WriteLine(epOut);
        string epId = string.Empty;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(epOut[(epOut.IndexOf('\n') + 1)..]);
            if (d.RootElement.TryGetProperty("Output", out var o) && o.TryGetProperty("ID", out var idEl))
            {
                epId = idEl.GetString() ?? string.Empty;
            }
        }
        catch { }
        Console.WriteLine($"endpoint id={epId}");
        if (string.IsNullOrEmpty(epId)) return 1;

        // 2. Scratch layer (activated + prepared).
        Directory.CreateDirectory(scratchDir);
        Layers.CreateSandbox(scratchDir, parent);
        Layers.Activate(scratchDir);
        Layers.Prepare(scratchDir, parent);
        var root = Layers.MountPath(scratchDir);
        var layerId = Layers.NameToGuid(parent);
        Console.WriteLine($"root={root}");

        // 3. schema2 with Networking.NetworkAdapters
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = new Dictionary<string, object?> { ["Major"] = 2, ["Minor"] = 1 },
            ["Owner"] = "WsmProbe",
            ["ShouldTerminateOnLastHandleClosed"] = true,
            ["Container"] = new Dictionary<string, object?>
            {
                ["GuestOs"] = new Dictionary<string, object?> { ["HostName"] = "wsmprobe" },
                ["Storage"] = new Dictionary<string, object?>
                {
                    ["Path"] = root,
                    ["Layers"] = new object[] { new Dictionary<string, object?> { ["Id"] = layerId.ToString(), ["Path"] = parent } },
                },
                ["Networking"] = new Dictionary<string, object?>
                {
                    ["NetworkAdapters"] = new object[] { epId },
                },
            },
        });
        var jsonFile = Path.Combine(Path.GetTempPath(), "opencode", "wsmdiag", "argonnet2.json");
        File.WriteAllText(jsonFile, json);
        Console.WriteLine(json);

        var handle = CreateKeepHandle(id, jsonFile);
        if (StartHandle(handle) != 0) { Native.HcsCloseComputeSystem(handle); return 1; }
        Console.WriteLine("STARTED OK");
        Thread.Sleep(6000);
        Exec(id, "cmd.exe /c ipconfig");
        Console.WriteLine("== hns endpoints ==");
        IpQuery(id);
        Native.HcsCloseComputeSystem(handle);
        return 0;
    }

    private static int RdpProbe(string host, int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            client.Connect(host, port);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"connect failed: {ex.Message}");
            return 1;
        }

        Console.WriteLine("tcp connected");
        var stream = client.GetStream();

        // X.224 Connection Request carrying an RDP Negotiation Request.
        var cookie = System.Text.Encoding.ASCII.GetBytes("Cookie: mstshash=wsm\r\n");
        var neg = new byte[] { 0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00 };
        var x224Len = 6 + cookie.Length + neg.Length;
        // TPKT total = 4-byte TPKT header + 1-byte X.224 length indicator + x224Len.
        var total = 5 + x224Len;

        var buf = new List<byte>
        {
            0x03, 0x00, (byte)(total >> 8), (byte)(total & 0xff),
            (byte)x224Len, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        buf.AddRange(cookie);
        buf.AddRange(neg);

        stream.Write(buf.ToArray());
        stream.ReadTimeout = 8000;
        var resp = new byte[1024];
        try
        {
            var n = stream.Read(resp, 0, resp.Length);
            Console.WriteLine($"response {n} bytes: {Convert.ToHexString(resp, 0, Math.Min(n, 64))}");
            // A valid RDP Negotiation Response starts 03 00 .. .. 0D D0 ..
            Console.WriteLine(n >= 11 && resp[0] == 0x03 && resp[5] == 0xD0 ? "RDP SERVER OK" : "unexpected response");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"read failed: {ex.Message}");
            return 1;
        }
    }

    private static string Hns(string method, string path, string request)
    {
        var hr = HnsCall(method, path, request, out var ptr);
        var json = ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(ptr);
        if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        return $"hr=0x{hr:X8}\n{json}";
    }

    [DllImport("vmcompute.dll", ExactSpelling = true, CharSet = CharSet.Unicode, EntryPoint = "HNSCall")]
    private static extern int HnsCall(string method, string path, string request, out IntPtr response);

    private static int Argon(string id, string scratchDir, string[] parents, string command)
    {
        Console.WriteLine("== parents ==");
        foreach (var p in parents)
        {
            Console.WriteLine($"  {p} -> guid={Layers.NameToGuid(p)} mount='{Layers.MountPath(p)}' exists={Directory.Exists(p)}");
        }

        Directory.CreateDirectory(scratchDir);
        Console.WriteLine("== CreateSandboxLayer ==");
        var hrCreate = Layers.CreateSandbox(scratchDir, parents);
        Console.WriteLine($"  hr=0x{hrCreate:X8} sandbox.vhdx={File.Exists(Path.Combine(scratchDir, "sandbox.vhdx"))}");
        if (hrCreate < 0) { Console.Error.WriteLine("scratch layer creation failed"); return 1; }

        Console.WriteLine("== ActivateLayer(scratch) ==");
        var hrAct = Layers.Activate(scratchDir);
        Console.WriteLine($"  hr=0x{hrAct:X8}");
        if (hrAct < 0) { Console.Error.WriteLine("activate failed"); return 1; }

        Console.WriteLine("== PrepareLayer(scratch, parents) ==");
        var hrPrep = Layers.Prepare(scratchDir, parents);
        Console.WriteLine($"  hr=0x{hrPrep:X8}");
        if (hrPrep < 0) { Console.Error.WriteLine("prepare failed"); return 1; }

        var root = Layers.MountPath(scratchDir);
        Console.WriteLine($"  mount path = '{root}'");

        var layers = parents.Select(p => (Id: Layers.NameToGuid(p), Path: Layers.MountPath(p))).ToArray();
        var map = new Dictionary<string, object?[]>();
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = new Dictionary<string, object?> { ["Major"] = 2, ["Minor"] = 1 },
            ["Owner"] = "WsmProbe",
            ["ShouldTerminateOnLastHandleClosed"] = true,
            ["Container"] = new Dictionary<string, object?>
            {
                ["GuestOs"] = new Dictionary<string, object?> { ["HostName"] = "wsmprobe" },
                ["Storage"] = new Dictionary<string, object?>
                {
                    ["Path"] = root,
                    ["Layers"] = layers.Select(l => (object)new Dictionary<string, object?> { ["Id"] = l.Id.ToString(), ["Path"] = l.Path }).ToArray(),
                },
            },
        });
        var jsonFile = Path.Combine(Path.GetTempPath(), "opencode", "wsmdiag", "argon.json");
        File.WriteAllText(jsonFile, json);
        Console.WriteLine("== HCS config ==");
        Console.WriteLine(json);
        Console.WriteLine("== HCS create ==");
        var handle = CreateKeepHandle(id, jsonFile);
        Console.WriteLine("== HCS start ==");
        if (StartHandle(handle) != 0)
        {
            Native.HcsCloseComputeSystem(handle);
            return 1;
        }

        Console.WriteLine("STARTED OK");
        Thread.Sleep(3000);
        var rc = Exec(id, command);
        if (rc == 0) rc = Exec(id, "cmd.exe /c ver");
        Console.WriteLine("== leaving container running (id=" + id + ") ==");

        if (int.TryParse(Environment.GetEnvironmentVariable("WSMPROBE_HOLD"), out var holdSecs) && holdSecs > 0)
        {
            Console.WriteLine($"holding for {holdSecs}s (handle kept open)");
            Thread.Sleep(holdSecs * 1000);
        }

        return 0;
    }

    private static int Probe(string id, string jsonPath, string command)
    {
        int rc = Create(id, jsonPath);
        if (rc != 0) return rc;
        rc = Start(id);
        if (rc != 0) { Stop(id); return rc; }
        System.Threading.Thread.Sleep(2000);
        rc = Exec(id, command);
        Stop(id);
        return rc;
    }
}

