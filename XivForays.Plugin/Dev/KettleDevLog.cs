#if DEV_SCANNER
using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using XivMate.DataGathering.Forays.Dalamud.Core;

namespace XivMate.DataGathering.Forays.Dalamud.Dev;

// DEVELOPER-ONLY (compiled only with -p:DevScanner=true; the published Release build does not contain it).
// One Dalamud log line per kettle record and /xfkettle, which prints the last 5 records to the local chat log only
// (IChatGui.Print: shown to this client, never sent). Numbers only: no names, ids or worlds.
internal sealed class KettleDevLog : IDisposable
{
    private const string SlashCommand = "/xfkettle";
    private const int Keep = 5;

    private readonly ICommandManager commands;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Queue<string> last = new();

    public KettleDevLog(ICommandManager commands, IChatGui chat, IPluginLog log)
    {
        this.commands = commands;
        this.chat = chat;
        this.log = log;
        commands.AddHandler(SlashCommand, new CommandInfo(OnSlash) { HelpMessage = "Developer: print the last 5 kettle light records to chat (local only)" });
    }

    /// <summary>Formats one record: tier, before and after (yield/bar), delta, forge flag.</summary>
    internal static string Format(LightEvent e)
    {
        string Reading(KettleReading? r) => r is { } v ? $"{v.Yield}/{v.Bar}" : "?";
        var delta = e.Delta is { } d ? d.ToString(CultureInfo.InvariantCulture) : "?";
        var tier = e.Tier is { } t ? t.ToString(CultureInfo.InvariantCulture) : "-";
        var flags = (e.Full ? " full" : "") + (e.Forge ? " forge" : "");
        return $"kettle tier {tier} row {(e.Row?.ToString(CultureInfo.InvariantCulture) ?? "-")} yield/bar {Reading(e.Before)} -> {Reading(e.After)} delta {delta} link {(byte)e.Link.Kind}{flags}";
    }

    public void Record(LightEvent e)
    {
        var line = Format(e);
        log.Information("[kettle] " + line);
        last.Enqueue(line);
        while (last.Count > Keep)
            last.Dequeue();
    }

    private void OnSlash(string command, string args)
    {
        if (last.Count == 0)
        {
            chat.Print("[xfkettle] no kettle records yet this session");
            return;
        }

        foreach (var line in last)
            chat.Print("[xfkettle] " + line);
    }

    public void Dispose() => commands.RemoveHandler(SlashCommand);
}
#endif
