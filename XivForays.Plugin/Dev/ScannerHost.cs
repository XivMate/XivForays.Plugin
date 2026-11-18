#if DEV_SCANNER
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XivMate.DataGathering.Forays.Dalamud.Dev;

// DEVELOPER-ONLY (compiled only with -p:DevScanner=true; the published Release build does not contain it).
// Local files only, no network: commands are JSON files dropped in <folder>/commands, results land in <folder>/results.
// Strictly read-only: it never writes game memory and never calls a game function that changes state.

/// <summary>
/// Glue to the game: the command-file watcher (once per frame, throttled to every 0.5 s), the slash command, the known
/// ClientStructs roots for the owner scan and the kettle window dump.
/// </summary>
internal sealed unsafe class ScannerHost : IDisposable
{
    public const string SlashCommand = "/xfscan";
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IPluginLog log;
    private readonly IGameGui gameGui;
    private readonly ScannerController controller;
    private readonly ProcessScanMemory memory = new();
    private readonly string folder;
    private DateTime nextPoll = DateTime.MinValue;

    /// <param name="folder">Local folder for commands/ and results/ (the plugin's own data folder, never the config file).</param>
    public ScannerHost(IFramework framework, ICommandManager commands, IPluginLog log, IGameGui gameGui, string folder)
    {
        this.framework = framework;
        this.commands = commands;
        this.log = log;
        this.gameGui = gameGui;
        this.folder = folder;
        Directory.CreateDirectory(Path.Combine(folder, "commands"));
        Directory.CreateDirectory(Path.Combine(folder, "results"));
        controller = new ScannerController(memory, GameModule, Roots, Path.Combine(folder, "results"), m => log.Information(m), structs: StructRoots);
        framework.Update += Tick;
        commands.AddHandler(SlashCommand, new CommandInfo(OnSlash) { HelpMessage = "Developer value scanner: status or stop" });
        log.Information($"[scanner] developer scanner ready; command files go in {Path.Combine(folder, "commands")}");
    }

    private static ModuleRange? GameModule()
    {
        var main = Process.GetCurrentProcess().MainModule;
        return main is null ? null : new ModuleRange(main.ModuleName ?? "ffxiv_dx11.exe", (ulong)main.BaseAddress, (ulong)main.ModuleMemorySize);
    }

    /// <summary>Known game structs the owner scan may end a chain at (names and sizes from FFXIVClientStructs).</summary>
    private IReadOnlyList<KnownRoot> Roots()
    {
        var list = new List<KnownRoot>();
        try
        {
            var fw = Framework.Instance();
            if (fw is not null)
                list.Add(new KnownRoot("Framework", (ulong)fw, (ulong)sizeof(Framework)));
            var ui = UIModule.Instance();
            if (ui is not null)
            {
                list.Add(new KnownRoot("UIModule", (ulong)ui, (ulong)sizeof(UIModule)));
                var agents = ui->GetAgentModule();
                if (agents is not null)
                {
                    list.Add(new KnownRoot("AgentModule", (ulong)agents, (ulong)sizeof(AgentModule)));
                    for (var i = 0; i < agents->Agents.Length; i++)
                        if (agents->Agents[i].Value != null)
                            list.Add(new KnownRoot($"Agent#{i}", (ulong)agents->Agents[i].Value, 0x1000));
                }
            }
            var stage0 = AtkStage.Instance();
            if (stage0 is not null)
                list.Add(new KnownRoot("AtkStage", (ulong)stage0, (ulong)sizeof(AtkStage)));
        }
        catch (Exception ex)
        {
            log.Warning($"[scanner] could not list the known roots: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// The structs scanStructs and scanOwner roots:"structs" look inside: event framework, the active content director (as
    /// the Eureka director when its type says so), UIState, the agent module and the agents that look Eureka related,
    /// and the _ToDoList window. Instances and sizes only; nothing is called that changes game state.
    /// </summary>
    private IReadOnlyList<KnownRoot> StructRoots()
    {
        var list = new List<KnownRoot>();
        try
        {
            var events = EventFramework.Instance();
            if (events is not null)
            {
                list.Add(new KnownRoot("EventFramework", (ulong)events, (ulong)sizeof(EventFramework)));
                var director = events->DirectorModule.ActiveContentDirector;
                if (director is not null)
                {
                    list.Add(new KnownRoot("ActiveContentDirector", (ulong)director, (ulong)sizeof(PublicContentDirector)));
                    if (((PublicContentDirector*)director)->Type == PublicContentDirectorType.Eureka)
                        list.Add(new KnownRoot("PublicContentEureka", (ulong)director, (ulong)sizeof(PublicContentEureka)));
                }
            }
            var ui = UIState.Instance();
            if (ui is not null)
                list.Add(new KnownRoot("UIState", (ulong)ui, (ulong)sizeof(UIState)));
            var module = UIModule.Instance()->GetAgentModule();
            if (module is not null)
            {
                list.Add(new KnownRoot("AgentModule", (ulong)module, (ulong)sizeof(AgentModule)));
                for (var i = 0; i < module->Agents.Length; i++)
                {
                    var agent = module->Agents[i].Value;
                    if (agent == null)
                        continue;
                    var name = ((AgentId)i).ToString();
                    if (new[] { "Eureka", "Elemental", "Magia", "Content", "ToDo" }.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new KnownRoot($"Agent#{i}:{name}", (ulong)agent, 0x2000));
                }
            }
            var todo = gameGui.GetAddonByName("_ToDoList");
            if (!todo.IsNull)
                list.Add(new KnownRoot("_ToDoList", todo.Address, (ulong)sizeof(AtkUnitBase)));
        }
        catch (Exception ex)
        {
            log.Warning($"[scanner] could not list the struct roots: {ex.Message}");
        }
        return list;
    }

    private void OnSlash(string command, string args)
    {
        var word = args.Trim().ToLowerInvariant();
        var result = controller.Execute("slash", word == "stop" ? "scanStop" : "scanStatus", []);
        log.Information($"[scanner] {JsonSerializer.Serialize(result)}");
    }

    private void Tick(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextPoll)
            return;
        nextPoll = now.AddSeconds(0.5);
        try
        {
            var file = new DirectoryInfo(Path.Combine(folder, "commands")).EnumerateFiles("*.json")
                .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).FirstOrDefault();
            if (file is not null)
                Run(file);
        }
        catch (Exception ex)
        {
            log.Warning($"[scanner] tick failed: {ex.Message}");
        }
    }

    private void Run(FileInfo file)
    {
        string text;
        try { text = File.ReadAllText(file.FullName); }
        catch (IOException) { return; }   // still being written
        var id = new string(Path.GetFileNameWithoutExtension(file.Name).Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(64).ToArray());
        object? details = null;
        string? error = null;
        try
        {
            var root = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("expected a JSON object {id, cmd, args}");
            if (root["id"] is JsonValue given && given.TryGetValue<string>(out var g) && g.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                id = g;
            var cmd = root["cmd"]?.GetValue<string>() ?? throw new InvalidOperationException("missing cmd");
            var args = root["args"] as JsonObject ?? [];
            log.Information($"[scanner] {id}: {cmd} {args.ToJsonString()}");
            details = cmd switch
            {
                "scanAddons" => ListAddons(args["filter"]?.GetValue<string>()),
                "scanAddon" => DumpAddon(args["name"]?.GetValue<string>() ?? throw new InvalidOperationException("name is required"), args),
                _ when ScannerController.Known.Contains(cmd) => controller.Execute(id, cmd, args),
                _ => throw new InvalidOperationException($"unknown command {cmd} (known: {string.Join(", ", ScannerController.Known.Concat(["scanAddons", "scanAddon"]).Order())})"),
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        File.WriteAllText(Path.Combine(folder, "results", id + ".json"),
            JsonSerializer.Serialize(new { id, ok = error is null, error, details, finished = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
        Directory.CreateDirectory(Path.Combine(folder, "done"));
        File.Move(file.FullName, Path.Combine(folder, "done", $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{file.Name}"));
    }

    /// <summary>Names of the loaded UI windows (optionally filtered), so the kettle window's addon name can be found.</summary>
    private object ListAddons(string? filter)
    {
        var names = new List<string>();
        var stage = AtkStage.Instance();
        var manager = stage is null ? null : stage->RaptureAtkUnitManager;
        if (manager is not null)
        {
            var list = &manager->AtkUnitManager.AllLoadedUnitsList;
            for (var i = 0; i < list->Count; i++)
            {
                var unit = list->Entries[i].Value;
                if (unit != null && unit->NameString is { Length: > 0 } name &&
                    (string.IsNullOrEmpty(filter) || name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                    names.Add($"{name}{(unit->IsVisible ? " (visible)" : "")}");
            }
        }
        return new { count = names.Count, names };
    }

    /// <summary>
    /// Dumps one open window: its text nodes and counter/number nodes (the kettle bar may be a text node), and the
    /// raw ints of the agent that owns it (the number on screen may come from an agent field). Read-only.
    /// </summary>
    private object DumpAddon(string name, JsonObject args)
    {
        var ptr = gameGui.GetAddonByName(name);
        if (ptr.IsNull)
            throw new InvalidOperationException($"addon {name} is not open (open the window first)");
        var unit = (AtkUnitBase*)ptr.Address;
        var text = new StringBuilder().AppendLine($"addon {name} id {unit->Id} visible {unit->IsVisible} at 0x{(ulong)unit:X}");
        var nodes = unit->UldManager.NodeList;
        for (var i = 0; i < unit->UldManager.NodeListCount; i++)
        {
            var node = nodes[i];
            if (node is null)
                continue;
            if (node->Type == NodeType.Text)
            {
                var t = node->GetAsAtkTextNode();
                text.AppendLine($"node {node->NodeId} text \"{t->NodeText.ToString()}\"");
            }
            else if (node->Type == NodeType.Counter)
                text.AppendLine($"node {node->NodeId} counter \"{node->GetAsAtkCounterNode()->NodeText.ToString()}\"");
            else
                text.AppendLine($"node {node->NodeId} type {node->Type} w {node->Width} h {node->Height} visible {node->IsVisible()}");
        }
        var agentLines = new StringBuilder();
        var agents = UIModule.Instance()->GetAgentModule();
        for (var i = 0; i < agents->Agents.Length; i++)
        {
            var agent = agents->Agents[i].Value;
            if (agent is null || agent->AddonId != unit->Id)
                continue;
            agentLines.AppendLine($"agent #{i} at 0x{(ulong)agent:X}, first 0x{(args["agentBytes"]?.GetValue<int>() ?? 0x400):X} bytes as int32 (offset: value):");
            var count = (args["agentBytes"]?.GetValue<int>() ?? 0x400) / 4;
            var bytes = new byte[count * 4];
            if (memory.Read((ulong)agent, bytes) == bytes.Length)
                for (var k = 0; k < count; k++)
                {
                    var v = BitConverter.ToInt32(bytes, k * 4);
                    if (v != 0)
                        agentLines.AppendLine($"  +0x{k * 4:X}: {v.ToString(CultureInfo.InvariantCulture)}");
                }
        }
        var outDir = args["outDir"]?.GetValue<string>() is { Length: > 0 } d ? d : Path.Combine(folder, "results");
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"addon-{name}.txt");
        File.WriteAllText(path, text.ToString() + agentLines);
        return new { file = path, nodes = unit->UldManager.NodeListCount, agentFound = agentLines.Length > 0 };
    }

    public void Dispose()
    {
        framework.Update -= Tick;
        commands.RemoveHandler(SlashCommand);
        controller.Dispose();
    }
}
#endif
