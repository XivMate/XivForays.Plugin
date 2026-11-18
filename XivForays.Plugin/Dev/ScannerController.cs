#if DEV_SCANNER
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace XivMate.DataGathering.Forays.Dalamud.Dev;

// DEVELOPER-ONLY (compiled only with -p:DevScanner=true, never in the Release build that is published). Pure logic: no
// Dalamud types, so it is unit tested offline. Strictly read-only: see ScanCore.cs.

/// <summary>
/// Runs the scanner commands. Long work (scans, pointer scans) runs on one low-priority background thread with sleeps;
/// every command returns at once and the outcome goes to the status and to a text file in the output folder.
/// </summary>
internal sealed class ScannerController(IScanMemory memory, Func<ModuleRange?> module, Func<IReadOnlyList<KnownRoot>> roots,
    string defaultOutDir, Action<string> log, Throttle? throttle = null, Func<IReadOnlyList<KnownRoot>>? structs = null) : IDisposable
{
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "scanFirst", "scanNext", "scanList", "scanWatch", "scanWatchStop", "scanOwner", "scanResolve", "scanStructs", "scanStop", "scanReset", "scanStatus",
    };

    private readonly Throttle pace = throttle ?? new Throttle();
    private readonly ValueScanner scanner = new(memory, throttle ?? new Throttle());
    private readonly object gate = new();
    private CancellationTokenSource? jobCancel;
    private Task? job;
    private string jobName = "";
    private string progress = "idle";
    private string lastOutcome = "nothing run yet";
    private CancellationTokenSource? watchCancel;
    private Task? watchTask;
    private List<PointerChain> lastChains = [];

    public string Progress => progress;

    /// <summary>Waits for the background job (tests).</summary>
    public void WaitIdle() => job?.Wait();

    public void WaitWatchStopped() => watchTask?.Wait();

    public object Execute(string id, string cmd, JsonObject args)
    {
        var outDir = args["outDir"]?.GetValue<string>() is { Length: > 0 } custom ? custom : defaultOutDir;
        switch (cmd)
        {
            case "scanStatus":
                return Status();
            case "scanStop":
                jobCancel?.Cancel();
                return new { stopping = jobName, status = Status() };
            case "scanReset":
                RequireIdle();
                scanner.Reset();
                lastOutcome = "scan reset";
                return Status();
            case "scanFirst":
                return StartFirst(id, args, outDir);
            case "scanNext":
                return StartNext(id, args, outDir);
            case "scanList":
                return List(id, args, outDir);
            case "scanWatch":
                return StartWatch(id, args, outDir);
            case "scanWatchStop":
                StopWatch();
                return new { watching = false };
            case "scanOwner":
                return StartOwner(id, args, outDir);
            case "scanResolve":
                return Resolve(args);
            case "scanStructs":
                return Structs(id, args, outDir);
            default:
                throw new InvalidOperationException($"unknown scanner command {cmd}");
        }
    }

    private object Status() => new
    {
        job = jobName, running = job is { IsCompleted: false }, progress, lastOutcome,
        candidates = scanner.Count, scanType = scanner.Type.ToString(), scans = scanner.Scans, snapshot = scanner.HasSnapshot,
        watching = watchTask is { IsCompleted: false }, chains = lastChains.Count,
    };

    private void RequireIdle()
    {
        if (job is { IsCompleted: false })
            throw new InvalidOperationException($"busy: {jobName} is running ({progress}); send scanStop first");
    }

    private void Run(string name, Action<CancellationToken> work)
    {
        RequireIdle();
        jobName = name;
        progress = "starting";
        var cancel = jobCancel = new CancellationTokenSource();
        job = Task.Factory.StartNew(() =>
        {
            try { work(cancel.Token); }
            catch (Exception ex)
            {
                lastOutcome = $"{name} failed: {ex.Message}";
                log($"[scanner] {lastOutcome}");
            }
            progress = "idle";
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static double? Number(JsonObject args, string name) => args[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private object StartFirst(string id, JsonObject args, string outDir)
    {
        if (!ScanTypes.TryParse(args["type"]?.GetValue<string>(), out ScanType type))
            throw new InvalidOperationException("type must be int32, int16, byte, float or unknown");
        var value = Number(args, "value");
        if (type != ScanType.Unknown && value is null)
            throw new InvalidOperationException("value is required (except type unknown)");
        Run("scanFirst", cancel =>
        {
            progress = "first scan running";
            var summary = scanner.First(type, value, cancel);
            Finish(id, "first", summary, outDir);
        });
        return new { started = "scanFirst", type = type.ToString(), value };
    }

    private object StartNext(string id, JsonObject args, string outDir)
    {
        if (!ScanTypes.TryParse(args["op"]?.GetValue<string>(), out ScanOp op))
            throw new InvalidOperationException("op must be equal, increased, decreased, unchanged or changed");
        var value = Number(args, "value");
        if (op == ScanOp.Equal && value is null)
            throw new InvalidOperationException("equal needs a value");
        Run("scanNext", cancel =>
        {
            progress = "next scan running";
            Finish(id, "next", scanner.Next(op, value, cancel), outDir);
        });
        return new { started = "scanNext", op = op.ToString(), value };
    }

    private void Finish(string id, string what, ScanSummary summary, string outDir)
    {
        lastOutcome = summary.Message;
        log($"[scanner] {summary.Message}");
        var text = new StringBuilder().AppendLine(summary.Message);
        AppendCandidates(text, 200);
        WriteText(outDir, $"{id}-{what}.txt", text.ToString());
    }

    private void AppendCandidates(StringBuilder text, int max)
    {
        for (var i = 0; i < Math.Min(max, scanner.Count); i++)
            text.AppendLine($"0x{scanner.AddressAt(i):X}  {scanner.ValueAt(i).ToString(CultureInfo.InvariantCulture)}");
        if (scanner.Count > max)
            text.AppendLine($"... {scanner.Count - max} more");
    }

    private object List(string id, JsonObject args, string outDir)
    {
        var max = (int)(Number(args, "max") ?? 100);
        var text = new StringBuilder().AppendLine($"{scanner.Count} candidates ({scanner.Type}), scan {scanner.Scans}");
        AppendCandidates(text, max);
        WriteText(outDir, $"{id}-list.txt", text.ToString());
        return new
        {
            count = scanner.Count,
            first = Enumerable.Range(0, Math.Min(max, scanner.Count)).Select(i => new { address = $"0x{scanner.AddressAt(i):X}", value = scanner.ValueAt(i) }),
        };
    }

    private object StartWatch(string id, JsonObject args, string outDir)
    {
        StopWatch();
        var type = scanner.Type;
        var seconds = Number(args, "seconds") ?? 600;
        var addresses = new List<ulong>();
        if (args["addresses"] is JsonArray listed)
            addresses.AddRange(listed.Select(a => ParseAddress(a!.ToString())));
        else
            for (var i = 0; i < Math.Min((int)(Number(args, "max") ?? 20), scanner.Count); i++)
                addresses.Add(scanner.AddressAt(i));
        if (addresses.Count == 0)
            throw new InvalidOperationException("nothing to watch (run a scan or pass \"addresses\")");
        var hidden = addresses.Select(Obf.Hide).ToArray();
        var cancel = watchCancel = new CancellationTokenSource();
        var path = Path.Combine(outDir, $"{id}-watch.txt");
        Directory.CreateDirectory(outDir);
        watchTask = Task.Factory.StartNew(() =>
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            File.AppendAllText(path, $"# watching {hidden.Length} addresses as {type} for {seconds} s, one line per second\r\n# " +
                string.Join(" ", hidden.Select(h => $"0x{Obf.Show(h):X}")) + "\r\n");
            var buffer = new byte[4];
            while (!cancel.IsCancellationRequested && DateTime.UtcNow < until)
            {
                var line = new StringBuilder(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                foreach (var h in hidden)
                {
                    var size = ScanTypes.SizeOf(type);
                    line.Append(memory.Read(Obf.Show(h), buffer.AsSpan(0, size)) >= size
                        ? " " + ScanTypes.Decode(type, buffer).ToString(CultureInfo.InvariantCulture) : " ?");
                }
                try { File.AppendAllText(path, line + "\r\n"); } catch (IOException) { }
                cancel.Token.WaitHandle.WaitOne(1000);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return new { watching = addresses.Count, file = path, seconds };
    }

    private void StopWatch()
    {
        watchCancel?.Cancel();
        watchTask?.Wait(3000);
    }

    private static ulong ParseAddress(string text)
    {
        text = text.Trim().Trim('"');
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(text, CultureInfo.InvariantCulture);
    }

    private object StartOwner(string id, JsonObject args, string outDir)
    {
        var target = args["address"] is { } a ? ParseAddress(a.ToString()) : scanner.Count == 1 ? scanner.AddressAt(0)
            : throw new InvalidOperationException("pass \"address\" (or narrow the scan to one candidate first)");
        var depth = (int)(Number(args, "depth") ?? 4);
        var maxOffset = (uint)(Number(args, "maxOffset") ?? 0x400);
        var perLevel = (int)(Number(args, "maxPerLevel") ?? 20000);
        var maxChains = (int)(Number(args, "maxChains") ?? 200);
        var structRoots = string.Equals(args["roots"]?.GetValue<string>(), "structs", StringComparison.OrdinalIgnoreCase);
        var seconds = Number(args, "timeLimit") ?? 60;
        var objectBytes = (int)(Number(args, "objectBytes") ?? 0x2000);
        var alsoGeneric = args["generic"] is JsonValue g && g.TryGetValue<bool>(out var gv) && gv;
        Run("scanOwner", cancel =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            var found = new List<PointerChain>();
            var notes = new List<string>();
            if (structRoots)
            {
                var near = StructProbe.Chains(memory, (structs ?? (() => []))(), target, maxOffset, 2, objectBytes, deadline, maxChains);
                found.AddRange(near);
                notes.Add($"known structs first (one or two hops): {near.Count} chains");
            }
            if (!structRoots || found.Count == 0 || alsoGeneric)
            {
                var result = new PointerScanner(memory, pace).Scan(target, depth, maxOffset, perLevel, maxChains, module(), roots(), limit.Token,
                    p => progress = p);
                found.AddRange(result.Chains);
                notes.AddRange(result.Notes);
            }
            else
                notes.Add("generic heap walk skipped because a struct chain was found (pass generic:true to run it anyway)");
            var partial = limit.IsCancellationRequested;
            if (partial && !cancel.IsCancellationRequested)
                notes.Add($"time limit of {seconds} s reached: partial results");
            lastChains = found;
            lastOutcome = $"owner scan of 0x{target:X}: {found.Count} chains{(partial ? " (partial)" : "")}";
            log($"[scanner] {lastOutcome}");
            var text = new StringBuilder()
                .AppendLine($"owner scan of 0x{target:X}, depth {depth}, maxOffset 0x{maxOffset:X}, roots {(structRoots ? "structs" : "generic")}")
                .AppendLine(string.Join(Environment.NewLine, notes))
                .AppendLine("Each chain: start at the module+RVA (or the named struct field), read the pointer there, add the offset, repeat; the last address is the value.")
                .AppendLine(ChainResolver.Format(found, maxChains));
            WriteText(outDir, $"{id}-owner.txt", text.ToString());
        });
        return new { started = "scanOwner", address = $"0x{target:X}", depth, maxOffset };
    }

    private object Structs(string id, JsonObject args, string outDir)
    {
        var target = args["address"] is { } a ? ParseAddress(a.ToString()) : throw new InvalidOperationException("pass \"address\"");
        var list = (structs ?? (() => []))();
        WriteText(outDir, $"{id}-structs.txt", StructProbe.Describe(memory, list, target));
        return new
        {
            file = Path.Combine(outDir, $"{id}-structs.txt"), structs = list.Count,
            inside = list.Count(r => StructProbe.Inside(r, target, StructProbe.DefaultBelow, out _)),
            withPointers = list.Count(r => StructProbe.FindFields(StructProbe.ReadStruct(memory, r), target).Count > 0),
        };
    }

    /// <summary>
    /// Follows a chain now and reads the value: "start" is an address or "module+0xRVA", "offsets" in application order,
    /// "type" like a scan. Use it after a zone change or restart to confirm a chain still lands on the value.
    /// </summary>
    private object Resolve(JsonObject args)
    {
        var start = args["start"]?.ToString() ?? throw new InvalidOperationException("start is required (address or module+0xRVA)");
        ulong startAddress;
        if (start.Contains('+', StringComparison.Ordinal))
        {
            var mod = module() ?? throw new InvalidOperationException("module not known");
            startAddress = mod.Base + ParseAddress(start[(start.IndexOf('+') + 1)..]);
        }
        else
            startAddress = ParseAddress(start);
        var offsets = (args["offsets"] as JsonArray ?? []).Select(o => (long)ParseAddress(o!.ToString())).ToList();
        ScanTypes.TryParse(args["type"]?.GetValue<string>() ?? "int32", out ScanType type);
        var final = ChainResolver.Resolve(memory, startAddress, offsets);
        if (final is null)
            return new { resolved = false };
        Span<byte> bytes = stackalloc byte[4];
        var size = ScanTypes.SizeOf(type == ScanType.Unknown ? ScanType.Int32 : type);
        var read = memory.Read(final.Value, bytes[..size]) >= size;
        return new { resolved = true, address = $"0x{final:X}", value = read ? ScanTypes.Decode(type, bytes) : (double?)null };
    }

    private static void WriteText(string dir, string name, string text)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The result file is a convenience; the status still carries the outcome.
        }
    }

    public void Dispose()
    {
        jobCancel?.Cancel();
        StopWatch();
        job?.Wait(5000);
    }
}
#endif
