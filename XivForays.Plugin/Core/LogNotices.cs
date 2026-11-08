using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XivMate.DataGathering.Forays.Dalamud.Core;

/// <summary>
/// Language-independent game notices from the LogMessage sheet: the row id and its integer parameters are the same in
/// every client language. Read from Dalamud's LogMessage event (read-only: the plugin never prevents or changes a
/// message). Parameter layouts from the sheet's format strings, e.g. row 549
/// "&lt;if([lnum2&gt;0],EXP chain #&lt;num(lnum1)&gt;! Chain expires in &lt;num(lnum2)&gt; ...,Maximum chain ...)&gt;".
/// lnumN is parameter index N-1.
/// </summary>
internal static class LogNotices
{
    /// <summary>EXP chain: lnum1 = chain number, lnum2 = seconds left (0 = maximum chain reached).</summary>
    public const uint ChainRow = 549;

    /// <summary>"[source] defeat(s) [target]": source and target are entities (compared in memory only).</summary>
    public const uint DefeatRow = 557;

    /// <summary>"You obtain ...": lnum1 = quantity, lnum2 = item id, lnum3 = bonus percent (loot notice, kind 62).</summary>
    public static readonly IReadOnlySet<uint> LootRows = new HashSet<uint> { 657, 3538, 3599 };

    public static (uint Number, uint SecondsLeft)? Chain(uint rowId, Func<int, int?> intParameter)
    {
        if (rowId != ChainRow || intParameter(0) is not { } number || number <= 0)
            return null;
        var left = intParameter(1) ?? 0;
        return ((uint)number, (uint)Math.Max(0, left));
    }

    public static (uint ItemId, uint Quantity)? Loot(uint rowId, Func<int, int?> intParameter)
    {
        if (!LootRows.Contains(rowId) || intParameter(1) is not { } item || item <= 0)
            return null;
        var quantity = intParameter(0) is { } q && q > 0 ? q : 1;
        return ((uint)item, (uint)quantity);
    }

    /// <summary>Self defeat: row 557 with the local player as the source and a non-player target.</summary>
    public static bool SelfDefeat(uint rowId, bool sourceIsLocalPlayer, bool targetIsPlayer) =>
        rowId == DefeatRow && sourceIsLocalPlayer && !targetIsPlayer;

    /// <summary>The game version string ("2025.10.01.0000.0000") as a number (2025100100000000); null if malformed.</summary>
    public static long? GameVersionNumber(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var digits = version.Replace(".", "", StringComparison.Ordinal);
        if (digits.Length is < 8 or > 18)
            return null;
        foreach (var c in digits)
            if (c is < '0' or > '9')
                return null;
        return long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The English chat-text fallback (the parser of 0.0.0.12), used only for notices the structured path did not take.
/// Pure so it can be tested.
/// </summary>
internal static partial class EnglishNotices
{
    public static uint? Chain(string text)
    {
        var match = ChainNotice().Match(text);
        return match.Success && uint.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture,
            out var count) ? count : null;
    }

    public static bool IsSelfDefeat(string text) => text.StartsWith("You defeat ", StringComparison.Ordinal);

    public static bool IsLoot(string text) => text.StartsWith("You obtain ", StringComparison.Ordinal);

    public static uint? LootQuantity(string text)
    {
        if (text.StartsWith("You obtain a ", StringComparison.Ordinal) || text.StartsWith("You obtain an ", StringComparison.Ordinal))
            return 1;
        var match = Quantity().Match(text);
        return match.Success && uint.TryParse(match.Groups[1].ValueSpan, NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    [GeneratedRegex(@"^EXP chain #([0-9]+)!", RegexOptions.CultureInvariant)]
    private static partial Regex ChainNotice();

    [GeneratedRegex(@"^You obtain ([0-9,]+) ", RegexOptions.CultureInvariant)]
    private static partial Regex Quantity();
}

/// <summary>
/// Pairs the two paths so a notice is recorded once: every structured record leaves one token for its kind, and the
/// chat fallback for that kind consumes a token instead of recording (the chat line follows its LogMessage at once).
/// Tokens expire after 3 s, so an unpaired structured record never blocks a later fallback.
/// </summary>
internal sealed class NoticePairing
{
    public const long TokenMs = 3000;
    private readonly Dictionary<int, Queue<long>> tokens = new();

    public void Structured(int kind, long nowMs)
    {
        if (!tokens.TryGetValue(kind, out var queue))
            tokens[kind] = queue = new Queue<long>();
        queue.Enqueue(nowMs);
    }

    /// <summary>True when the chat fallback should record this notice itself.</summary>
    public bool FallbackRecords(int kind, long nowMs)
    {
        if (!tokens.TryGetValue(kind, out var queue))
            return true;
        while (queue.Count > 0 && nowMs - queue.Peek() > TokenMs)
            queue.Dequeue();
        if (queue.Count == 0)
            return true;
        queue.Dequeue();
        return false;
    }

    public void Clear() => tokens.Clear();
}
