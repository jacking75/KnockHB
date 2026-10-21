using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace RemoteAssist.E2e;

internal static class Program
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static long Tick => Stopwatch.GetTimestamp();
    [STAThread]
    static int Main(string[] args)
    {
        // Import/build/self-check never touches UI, clipboard, processes or services.
        if (args is ["--self-check"]) return 0;
        if (args is not ["--request", var filename]) return 40;
        string? output = null;
        try
        {
            var request = Read(filename);
            var root = Full(request.GetProperty("root").GetString()!);
            if (!Regex.IsMatch(request.GetProperty("runId").GetString()!, "^[a-f0-9]{32}$")) throw new InvalidDataException("run-id");
            output = Child(root, request.GetProperty("output").GetString()!);
            var end = request.GetProperty("deadlineTick").GetInt64();
            if (end <= Tick || end - Tick > Stopwatch.Frequency * 900) throw new InvalidDataException("deadline");
            var op = request.GetProperty("operation").GetString();
            // Bound blocking COM/UIA/Shell/OLE calls as well as polling loops.
            // A forced exit never produces a successful observation/restoration.
            var milliseconds=(end-Tick)*1000/Stopwatch.Frequency+(op=="clipboard-guard"?10000:0);
            using var watchdog=new System.Threading.Timer(_=>Environment.Exit(124),null,TimeSpan.FromMilliseconds(Math.Max(1,milliseconds)),Timeout.InfiniteTimeSpan);
            switch (op)
            {
                case "snapshot": Save(output, Snapshot(request)); break;
                case "inspect-owned":
                    var remaining=new List<int>();var changed=new List<int>();
                    var inspected=new List<int>();
                    foreach(var identity in request.GetProperty("owners").EnumerateArray())
                    {
                        inspected.Add(identity.GetProperty("pid").GetInt32());
                        try {using var process=Owner(identity);if(!process.HasExited)remaining.Add(process.Id);}
                        catch(ArgumentException) { }
                        catch(InvalidOperationException){changed.Add(identity.GetProperty("pid").GetInt32());}
                    }
                    Save(output,new {runId=request.GetProperty("runId").GetString(),available=true,remaining,changed,inspectedPids=inspected,resourcesAbsent=remaining.Count==0&&changed.Count==0,tick=Tick,frequency=Stopwatch.Frequency});
                    break;
                case "session-watch":
                    RequireDefault(request);
                    var sessionEvents=Child(root,Path.Combine(root,"session-events.ndjson"));
                    if(File.Exists(sessionEvents))throw new InvalidOperationException("session-watch-replay");
                    Save(output,new {available=true,ready=true,tick=Tick,frequency=Stopwatch.Frequency});
                    using(var log=new FileStream(sessionEvents,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                    {
                        bool? previous=null;
                        while(Tick<end&&!File.Exists(Child(root,Path.Combine(root,"target-finish"))))
                        {
                            var locked=Locked(request.GetProperty("session").GetInt32());
                            if(locked!=previous){var row=JsonSerializer.SerializeToUtf8Bytes(new {runId=request.GetProperty("runId").GetString(),
                                locked,activeSessionId=WTSGetActiveConsoleSessionId(),tick=Tick,frequency=Stopwatch.Frequency},Json);
                                log.Write(row);log.WriteByte(10);log.Flush(true);previous=locked;}
                            Thread.Sleep(20);
                        }
                    }
                    break;
                case "uia": Save(output, Uia(request)); break;
                case "clipboard-guard": ClipboardGuard(request, root, output, end); break;
                case "clipboard-lifetime": Save(output,ClipboardLifetime(request,root,end));break;
                case "clipboard-compare":
                    RequireDefault(request);
                    Save(output, new { available = true, osEqualsTestText = System.Windows.Clipboard.ContainsText() &&
                        System.Windows.Clipboard.GetText() == "RA-CLIP😀", tick = Tick, frequency = Stopwatch.Frequency }); break;
                case "fault-guard": FaultGuard(request, root, output, end); break;
                case "uac-trigger":
                    RequireDefault(request);
                    if(request.GetProperty("caseId").GetString()!="C04")throw new InvalidOperationException("uac-case");
                    var trigger=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");
                    var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(trigger)));
                    if(hash!=request.GetProperty("triggerSha256").GetString())throw new InvalidOperationException("uac-trigger-changed");
                    var uacStarted=Tick;
                    try
                    {
                        using var child=Process.Start(new ProcessStartInfo(trigger)
                            {UseShellExecute=true,Verb="runas",Arguments="/d /c exit",WindowStyle=ProcessWindowStyle.Hidden});
                        Save(output,new {available=true,cancelled=false,unexpectedApproval=true,tick=Tick});
                    }
                    catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode==1223)
                    {Save(output,new {runId=request.GetProperty("runId").GetString(),available=true,cancelled=true,unexpectedApproval=false,startedTick=uacStarted,tick=Tick,frequency=Stopwatch.Frequency});}
                    break;
                case "ocr-cancel": case "ocr-security": Save(output,OcrCancel(request,root).GetAwaiter().GetResult()); break;
                default: throw new InvalidDataException("closed-operation-set");
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (output is not null)
                try { Save(output + ".error", new { available = false, error = ex.GetType().Name, tick = Tick }); } catch { }
            return 20;
        }
    }
    static JsonElement Read(string path)
    {
        Full(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 1048576) throw new InvalidDataException("json-bound");
        using var document = JsonDocument.Parse(file); return document.RootElement.Clone();
    }
    static string Full(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("absolute-path");
        path = Path.GetFullPath(path);
        for (string? p = path; p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("reparse-path");
        return path;
    }
    static string Child(string root, string path)
    {
        path = Full(path);
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("owner-path");
        return path;
    }
    static void Save(string path, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes); stream.Flush(true);
    }
    static void RequireDefault(JsonElement request)
    {
        using var current = Process.GetCurrentProcess();
        if (current.SessionId != request.GetProperty("session").GetInt32() ||
            WindowsIdentity.GetCurrent().User?.Value != request.GetProperty("sid").GetString() || Desktop() != "Default")
            throw new InvalidOperationException("interactive-identity");
    }
    static string Desktop()
    {
        var handle = OpenInputDesktop(0, false, 1);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("input-desktop-unavailable");
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(handle, 2, name, 512, out _)) throw new InvalidOperationException("desktop-name-unavailable");
            return name.ToString();
        }
        finally { CloseDesktop(handle); }
    }
    static object Snapshot(JsonElement request)
    {
        var desktop = Desktop();
        if (desktop != "Default") throw new InvalidOperationException("held-state-unavailable-on-secure-desktop");
        using var current = Process.GetCurrentProcess();
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new InvalidOperationException("memory-unavailable");
        return new { runId=request.GetProperty("runId").GetString(),caseId=request.GetProperty("caseId").GetString(),available = true, desktop, sid = WindowsIdentity.GetCurrent().User?.Value,
            session = current.SessionId,activeSessionId=WTSGetActiveConsoleSessionId(), pid = current.Id, createdAt = current.StartTime.ToUniversalTime().Ticks,
            imagePath = Environment.ProcessPath, tick = Tick, frequency = Stopwatch.Frequency,
            heldVirtualKeys = Enumerable.Range(1, 254).Where(v => (GetAsyncKeyState(v) & 0x8000) != 0).ToArray(),
            osTotalBytes = memory.TotalPhysical, osAvailableBytes = memory.AvailablePhysical };
    }
    static async Task<object> OcrCancel(JsonElement request,string root)
    {
        var path=Child(root,request.GetProperty("image").GetString()!);
        if(new FileInfo(path).Length>16*1024*1024)throw new InvalidDataException("capture-size");
        var expected=request.GetProperty("imageSha256").GetString();
        if(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))!=expected)
            throw new InvalidDataException("capture-changed");
        var file=await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream=await file.OpenReadAsync();
        var decoder=await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap=await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Ignore);
        var engine=Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()??throw new InvalidOperationException("ocr-language-unavailable");
        var result=await engine.RecognizeAsync(bitmap);
        if(request.GetProperty("operation").GetString()=="ocr-security")
        {
            if(!OcrPolicy.Security(result.Text))throw new InvalidOperationException("security-scene-unconfirmed");
            return new {available=true,kind="Windows security options",actualScreenMatched=true,tick=Tick,frequency=Stopwatch.Frequency};
        }
        var words=result.Lines.SelectMany(l=>l.Words).ToArray();
        var matches=words.Where(w=>OcrPolicy.Cancel(w.Text)).ToArray();
        if(matches.Length!=1)throw new InvalidOperationException("cancel-target-not-unique");
        var title=result.Text;
        var isUac=OcrPolicy.Uac(title)&&!OcrPolicy.Security(title);
        if(!isUac)throw new InvalidOperationException("uac-scene-unconfirmed");
        var rect=matches[0].BoundingRect;
        if(rect.X<0||rect.Y<0||rect.Width<=0||rect.Height<=0||rect.Right>bitmap.PixelWidth||rect.Bottom>bitmap.PixelHeight)throw new InvalidOperationException("ocr-target-bounds");
        return new {available=true,uacSceneMatched=true,x=rect.X+rect.Width/2,y=rect.Y+rect.Height/2,
            width=bitmap.PixelWidth,height=bitmap.PixelHeight,tick=Tick,frequency=Stopwatch.Frequency};
    }
    static Process Owner(JsonElement identity)
    {
        var process = Process.GetProcessById(identity.GetProperty("pid").GetInt32());
        try
        {
            // Force opening/retaining a handle before comparing identity. A reused
            // PID cannot retarget subsequent WaitForExit/Kill calls.
            _ = process.Handle;
            var start = identity.TryGetProperty("start", out var ticks) ? ticks.GetInt64() : identity.GetProperty("createdAt").GetDateTimeOffset().UtcTicks;
            var image = identity.TryGetProperty("image", out var path) ? path.GetString()! : identity.GetProperty("imagePath").GetString()!;
            if (process.StartTime.ToUniversalTime().Ticks != start ||
                !string.Equals(process.MainModule?.FileName, Full(image), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("process-identity");
            return process;
        }
        catch { process.Dispose(); throw; }
    }
    static object Uia(JsonElement request)
    {
        RequireDefault(request);
        using var owner = Owner(request.GetProperty("owner"));
        AutomationElement window;
        if(request.TryGetProperty("hwnd",out var handle)) window=AutomationElement.FromHandle(new IntPtr(handle.GetInt64()));
        else
        {
            var condition = new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,owner.Id),
                new PropertyCondition(AutomationElement.NameProperty,request.GetProperty("windowTitle").GetString()!));
            var matches=AutomationElement.RootElement.FindAll(TreeScope.Children,condition);
            if(matches.Count!=1)throw new InvalidOperationException("uia-window-not-unique");
            window=matches[0];
        }
        var hwnd=new IntPtr(window.Current.NativeWindowHandle);
        if (window.Current.ProcessId != owner.Id) throw new InvalidOperationException("uia-window-owner");
        var rows = new List<object>();
        // Explicit AutomationIds only; never collect every UI value or credential.
        foreach (var id in request.GetProperty("automationIds").EnumerateArray())
        {
            var name = id.GetString()!;
            if (name.Length is < 1 or > 128 || rows.Count >= 64) throw new InvalidDataException("uia-selector-bound");
            var matches = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, name));
            if (matches.Count != 1) throw new InvalidOperationException("uia-selector-not-unique");
            var element = matches[0]; var bounds = element.Current.BoundingRectangle;
            bool? toggled = element.TryGetCurrentPattern(TogglePattern.Pattern, out var p)
                ? ((TogglePattern)p).Current.ToggleState switch { ToggleState.On => true, ToggleState.Off => false, _ => null } : null;
            rows.Add(new { automationId = name, enabled = element.Current.IsEnabled, toggle = toggled,
                controlType = element.Current.ControlType.ProgrammaticName,
                bounds = new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height } });
        }
        return new { available = true, ownerPid = owner.Id, hwnd = hwnd.ToInt64(), elements = rows, tick = Tick, frequency = Stopwatch.Frequency };
    }
    static void ClipboardGuard(JsonElement request, string root, string output, long end)
    {
        RequireDefault(request);
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        try { ClipboardGuardInitialized(request, root, output, end); }
        finally { OleUninitialize(); }
    }
    // Separate B03 fixture, never invoked by a product case or build/self-check.
    // Parent keeps the original private copy across a child OLE provider exit.
    static object ClipboardLifetime(JsonElement request,string root,long end)
    {
        RequireDefault(request);
        if(request.GetProperty("caseId").GetString()!="C11")throw new InvalidOperationException("clipboard-lifetime-case");
        var workEnd=end-Stopwatch.Frequency*11;
        if(workEnd<=Tick)throw new InvalidOperationException("clipboard-lifetime-recovery-budget");
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        try
        {
            using var original=ClipboardSnapshot.Capture();
            var id=Guid.NewGuid().ToString("N");
            var childOutput=Child(root,Path.Combine(root,id+"-captured.json"));
            var restored=Child(root,Path.Combine(root,id+"-restored.json"));
            var childRequest=Child(root,Path.Combine(root,id+"-request.json"));
            var signal=Child(root,Path.Combine(root,id+"-restore"));
            var spec=System.Text.Json.Nodes.JsonNode.Parse(request.GetRawText())!.AsObject();
            spec["operation"]="clipboard-guard";spec["output"]=childOutput;spec["restoredOutput"]=restored;
            spec["restoreSignal"]=signal;spec["deadlineTick"]=Math.Min(workEnd,Tick+Stopwatch.Frequency*15);
            Save(childRequest,spec);
            Process? child=null;
            try
            {
                var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
                info.ArgumentList.Add("--request");info.ArgumentList.Add(childRequest);
                child=Process.Start(info)??throw new IOException("clipboard-lifetime-child");_=child.Handle;
                while(!File.Exists(childOutput))
                {
                    if(child.HasExited||Tick>=workEnd)throw new TimeoutException("clipboard-lifetime-capture");
                    System.Windows.Forms.Application.DoEvents();Thread.Sleep(10);
                }
                var captured=Read(childOutput);
                if(captured.GetProperty("pid").GetInt32()!=child.Id||!captured.GetProperty("captured").GetBoolean())throw new IOException("clipboard-lifetime-owner");
                Save(signal,new {runId=request.GetProperty("runId").GetString()});
                while(!child.HasExited)
                {
                    if(Tick>=workEnd)throw new TimeoutException("clipboard-lifetime-exit");
                    System.Windows.Forms.Application.DoEvents();Thread.Sleep(10);
                }
                if(child.ExitCode!=0)throw new IOException("clipboard-lifetime-child-failed");
                var done=Read(restored);
                if(!done.GetProperty("baselineRestored").GetBoolean()||!done.GetProperty("comparisonVerified").GetBoolean()||
                    !done.GetProperty("flushCompleted").GetBoolean()||!done.GetProperty("withinLimit").GetBoolean())throw new IOException("clipboard-lifetime-flush");
                using var after=ClipboardSnapshot.Capture();
                if(!original.EquivalentTo(after))throw new IOException("clipboard-lifetime-after-exit-mismatch");
            }
            finally
            {
                try
                {
                    if(child is not null&&!child.HasExited){child.Kill();if(!child.WaitForExit(1000))throw new IOException("clipboard-lifetime-child-remains");}
                }
                finally
                {
                    child?.Dispose();
                    using var limit=new System.Threading.Timer(_=>Environment.Exit(124),null,10000,Timeout.Infinite);
                    original.Restore();
                }
            }
            return new {schema="remoteassist-clipboard-lifetime-v1",runId=request.GetProperty("runId").GetString(),caseId="C11",
                candidateSha256=request.GetProperty("candidateSha256").GetString(),available=true,baselineRestored=true,
                flushCompleted=true,helperExited=true,helperExitSurvived=true,comparisonVerified=true,
                executableSha256=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Environment.ProcessPath!))),
                assemblySha256=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))),
                sid=WindowsIdentity.GetCurrent().User?.Value,session=Process.GetCurrentProcess().SessionId,tick=Tick,frequency=Stopwatch.Frequency};
        }
        finally{OleUninitialize();}
    }
    static void ClipboardGuardInitialized(JsonElement request, string root, string output, long end)
    {
        var stop = Child(root, request.GetProperty("restoreSignal").GetString()!);
        var restored = Child(root, request.GetProperty("restoredOutput").GetString()!);
        if (File.Exists(stop) || File.Exists(restored)) throw new InvalidOperationException("clipboard-replay");
        // Acquire a per-session lock, preventing simultaneous private snapshots.
        using var mutex = new Mutex(false, "Local\\RemoteAssist-E2E-Clipboard");
        if (!mutex.WaitOne(0)) throw new InvalidOperationException("clipboard-owner-busy");
        try
        {
            ClipboardSnapshot captured;
            using(var captureLimit=new System.Threading.Timer(_=>Environment.Exit(124),null,10000,Timeout.Infinite))
                captured=ClipboardSnapshot.Capture();
            using var snapshot=captured;
            using var guardProcess=Process.GetCurrentProcess();
            var createdAt=guardProcess.StartTime.ToUniversalTime().ToString("o");
            try
            {
                Save(output, new { available = true, captured = true, restorable = true, ownerMatched = true,
                    runId = request.GetProperty("runId").GetString(), caseId=request.GetProperty("caseId").GetString(),
                    state="captured",createdAt,pid = Environment.ProcessId, tick = Tick, frequency = Stopwatch.Frequency });
                // Fixed deadline independent of the orchestrator. Pump COM/STA
                // messages while retaining the private deep snapshot in memory.
                while (Tick < end && !File.Exists(stop))
                {
                    System.Windows.Forms.Application.DoEvents();
                    Thread.Sleep(10);
                }
            }
            finally
            {
                var started = Tick;
                using var restoreLimit=new System.Threading.Timer(_=>Environment.Exit(124),null,10000,Timeout.Infinite);
                try{snapshot.Restore();}
                catch(Exception error)
                {
                    Save(restored+".error",new {available=false,baselineRestored=false,state="restore-failed",
                        runId=request.GetProperty("runId").GetString(),caseId=request.GetProperty("caseId").GetString(),
                        pid=Environment.ProcessId,createdAt,startedTick=started,tick=Tick,frequency=Stopwatch.Frequency,error=error.GetType().Name});
                    throw;
                }
                Save(restored, new { available = true, baselineRestored = true, privateContentExposed = false,
                    runId=request.GetProperty("runId").GetString(),caseId=request.GetProperty("caseId").GetString(),
                    pid=Environment.ProcessId,createdAt,state="restored",flushCompleted=true,comparisonVerified=true,
                    startedTick = started, tick = Tick, frequency = Stopwatch.Frequency,
                    withinLimit = Tick - started <= Stopwatch.Frequency * 10 });
            }
        }
        finally { mutex.ReleaseMutex(); }
    }
    static void FaultGuard(JsonElement request, string root, string output, long end)
    {
        if (request.GetProperty("caseId").GetString() != "C16" ||
            WindowsIdentity.GetCurrent().User?.Value != "S-1-5-18") throw new InvalidOperationException("fault-role");
        var ownerFile = Full(request.GetProperty("devOwner").GetString()!);
        var owner = Read(ownerFile);
        var ownerHash=System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ownerFile));
        var id = request.GetProperty("runId").GetString();
        if (owner.GetProperty("id").GetString() != id) throw new InvalidOperationException("fault-owner");
        using var service = Owner(request.GetProperty("service"));
        var image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RemoteAssist-Dev", "RemoteAssist.Host.Service.exe");
        if (!string.Equals(service.MainModule?.FileName, image, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(ownerFile, Path.Combine(Path.GetDirectoryName(image)!, "dev-owner.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("fault-image");
        using var fixture = Owner(request.GetProperty("fixture"));
        var fixturePath = Child(root, request.GetProperty("fixtureObservation").GetString()!);
        var firedPath = Child(root, request.GetProperty("firedOutput").GetString()!);
        var stopPath = Child(root, request.GetProperty("stopSignal").GetString()!);
        var baseline = Read(fixturePath);
        var count = baseline.GetProperty("f12Down").GetInt32();
        if (count != 0 || File.Exists(firedPath) || File.Exists(stopPath)) throw new InvalidOperationException("fault-replay");
        Save(output, new { available = true, ready = true, identityMatched = true, consumedCount = 0, finiteDeadline = true, tick = Tick, frequency = Stopwatch.Frequency });
        while (Tick < end && !File.Exists(stopPath))
        {
            if (service.HasExited || fixture.HasExited) throw new InvalidOperationException("fault-process-lost");
            var data = Read(fixturePath);
            if (data.GetProperty("pid").GetInt32() != fixture.Id ||
                data.GetProperty("startTick").GetInt64() != baseline.GetProperty("startTick").GetInt64())
                throw new InvalidOperationException("fixture-generation");
            var downs = data.GetProperty("f12Down").GetInt32();
            if (downs == count) { Thread.Sleep(2); continue; }
            if (downs != count + 1 || !data.GetProperty("observationValid").GetBoolean() ||
                !data.GetProperty("heldVirtualKeys").EnumerateArray().Any(k => k.GetInt32() == 123))
                throw new InvalidOperationException("fault-down-not-held");
            var down = data.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "key" &&
                e.GetProperty("code").GetInt32() == 123 && e.GetProperty("action").GetString() == "down").GetProperty("tick").GetInt64();
            if (Tick < down || Tick - down > Stopwatch.Frequency / 2) throw new InvalidOperationException("fault-trigger-deadline");
            // Persist the one-shot consumption BEFORE the only destructive call.
            if(!ownerHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ownerFile))))throw new InvalidOperationException("fault-owner-changed");
            Save(firedPath + ".intent", new { runId = id, servicePid = service.Id, tick = Tick });
            var fired = Tick;
            service.Kill();
            if (!service.WaitForExit(500)) throw new InvalidOperationException("fault-exit-timeout");
            Save(firedPath, new { available = true, consumedCount = 1, identityMatched = true, f12HeldAtFault = true,
                runId=id,caseId="C16",servicePid=service.Id,serviceStart=service.StartTime.ToUniversalTime().Ticks,fixturePid=fixture.Id,
                nativeDownTick = down, faultTick = fired, exitedTick = Tick, frequency = Stopwatch.Frequency });
            return;
        }
        throw new TimeoutException("fault-not-fired");
    }
    [StructLayout(LayoutKind.Sequential)] struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, uint length, out uint needed);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] static extern void OleUninitialize();
    static bool Locked(int session)
    {
        if(!WTSQuerySessionInformation(IntPtr.Zero,session,25,out var memory,out var bytes))throw new InvalidOperationException("session-state-unavailable");
        try
        {
            // WTSINFOEX: DWORD Level; aligned WTSINFOEX_LEVEL1 {SessionId,
            // SessionState, SessionFlags}. Windows 11 uses LOCK=0, UNLOCK=1.
            if(bytes<20||Marshal.ReadInt32(memory)!=1||Marshal.ReadInt32(memory,8)!=session)throw new InvalidDataException("session-state-schema");
            return Marshal.ReadInt32(memory,16) switch {0=>true,1=>false,_=>throw new InvalidOperationException("session-flags-unavailable")};
        }
        finally{WTSFreeMemory(memory);}
    }
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WTSQuerySessionInformation(IntPtr server,int session,int information,out IntPtr buffer,out int bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr buffer);
}
