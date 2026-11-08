using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using XivMate.DataGathering.Forays.Dalamud.Gathering.Fate;
using XivMate.DataGathering.Forays.Dalamud.Models;
using XivMate.DataGathering.Forays.Dalamud.Services;

namespace XivMate.DataGathering.Forays.Dalamud.Windows.Tabs;

internal class DebugTab(
    FateModule fateModule,
    EnemyTrackingService enemyTrackingService,
    PoseCaptureService poseCapture) : ITab
{
    private bool Debug;

    private Dictionary<ulong, EnemyPosition>? enemies;

    /// <inheritdoc />
    public int Index => 3;

    /// <inheritdoc />
    public string TabTitle => "Debug";

    /// <inheritdoc />
    public void Draw(Configuration.Configuration configuration)
    {
        ImGui.TextUnformatted("Show Debug Info");
        ImGui.SameLine();

        if (ImGui.Checkbox("##CrowdsourceData", ref Debug)) { }

        var capturePass = configuration.CapturePass;
        if (ImGui.Checkbox("Capture pass: 60 s idle tracks after each spawn pose (kind 17)##CapturePass", ref capturePass))
        {
            configuration.CapturePass = capturePass;
            configuration.Save();
            poseCapture.CapturePass = capturePass;
        }

        RenderPoseTable();

        if (Debug)
        {
            enemies = enemyTrackingService.GetVisibleEnemies();

            ImGui.TextUnformatted($"Fates on map {fateModule.ActiveFates.Count()}");
            RenderFateTable();

            ImGui.TextUnformatted($"Enemies on map {enemies.Count}");
            RenderEnemiesTable(enemies);
        }
    }

    // Pose capture v2: what this visit recorded, as the same name-free lines written to dalamud.log.
    private void RenderPoseTable()
    {
        ImGui.TextUnformatted(
            $"Poses this visit: spawn {poseCapture.CountOf(ObservationKind.EnemySpawnPose)}, " +
            $"form {poseCapture.CountOf(ObservationKind.EnemyFormChange)}, idle {poseCapture.CountOf(ObservationKind.EnemyIdleTrack)}, " +
            $"aggro {poseCapture.CountOf(ObservationKind.EnemyAggro)}, died {poseCapture.CountOf(ObservationKind.EnemyDied)}, " +
            $"respawns {poseCapture.RespawnCount}");
        ImGui.TextUnformatted(
            $"Tracked {poseCapture.TrackedCount}, dedupe keys {poseCapture.DedupeCount}, pending deaths {poseCapture.PendingDeaths}, " +
            $"full reads last scan {poseCapture.LastScanFullReads}, dropped non-finite {poseCapture.DroppedNonFinite}, " +
            $"read failures {poseCapture.ReadFailures}");
        using var table = ImRaii.Table("##Poses", 1,
                                       ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                                       new System.Numerics.Vector2(0, 220));
        if (!table)
            return;
        ImGui.TableSetupColumn("Pose records (newest first)");
        ImGui.TableHeadersRow();
        foreach (var line in poseCapture.Recent)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line);
        }
    }

    private void RenderFateTable()
    {
        using var table = ImRaii.Table("##FatesOnMap", 8,
                                       ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.Borders |
                                       ImGuiTableFlags.RowBg);
        if (table)
        {
            // Add table headers
            ImGui.TableSetupColumn("Fate ID");
            ImGui.TableSetupColumn("Level");
            ImGui.TableSetupColumn("Position X");
            ImGui.TableSetupColumn("Position Y");
            ImGui.TableSetupColumn("Position Z");
            ImGui.TableSetupColumn("Territory ID");
            ImGui.TableSetupColumn("Map ID");
            ImGui.TableSetupColumn("Last Updated");
            ImGui.TableHeadersRow();

            // Add table rows
            foreach (var fatePair in fateModule.ActiveFates)
            {
                var fate = fatePair!;
                ImGui.TableNextRow();
                // Column 0: ID
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(fate.FateId.ToString());
                // Column 1: Type ID
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(fate.LevelId.ToString());

                // Column 4: X
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(fate.X.ToString("F1"));

                // Column 5: Y
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(fate.Y.ToString("F1"));

                // Column 6: Z
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(fate.Z.ToString("F1"));

                // Column 7: Adapted
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(fate.Radius+"");

                // Column 8: Mutated
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(fate.EndedAt.ToString());

                // Column 12: Territory ID
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(fate.TerritoryId.ToString());

                // Column 13: Map ID
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(fate.MapId.ToString());

                // // Column 14: Timestamp
                // ImGui.TableNextColumn();
                // var dateTime = DateTimeOffset.FromUnixTimeSeconds(fate.TimeStamp).DateTime;
                // ImGui.TextUnformatted(dateTime.ToString("HH:mm:ss"));
            }
        }
    }

    private void RenderEnemiesTable(Dictionary<ulong, EnemyPosition> enemies)
    {
        using var table = ImRaii.Table("##EnemiesOnMap", 15,
                                       ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.Borders |
                                       ImGuiTableFlags.RowBg);
        if (table)
        {
            // Add table headers
            ImGui.TableSetupColumn("ID");
            ImGui.TableSetupColumn("Type ID");
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Level");
            ImGui.TableSetupColumn("X");
            ImGui.TableSetupColumn("Y");
            ImGui.TableSetupColumn("Z");
            ImGui.TableSetupColumn("Adapted");
            ImGui.TableSetupColumn("Mutated");
            ImGui.TableSetupColumn("Element");
            ImGui.TableSetupColumn("In Combat");
            ImGui.TableSetupColumn("Has Been In Combat");
            ImGui.TableSetupColumn("Territory ID");
            ImGui.TableSetupColumn("Map ID");
            ImGui.TableSetupColumn("Timestamp");
            ImGui.TableHeadersRow();

            // Add table rows
            foreach (var enemyPair in enemies)
            {
                var enemy = enemyPair.Value;
                ImGui.TableNextRow();
                // Column 0: ID
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemyPair.Key.ToString());
                // Column 1: Type ID
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.MobIngameId.ToString());

                // Column 2: Name
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.MobName ?? string.Empty);

                // Column 3: Level
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.Level.ToString());

                // Column 4: X
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.X.ToString("F1"));

                // Column 5: Y
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.Y.ToString("F1"));

                // Column 6: Z
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.Z.ToString("F1"));

                // Column 7: Adapted
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.IsAdapted ? "Yes" : "No");

                // Column 8: Mutated
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.IsMutated ? "Yes" : "No");

                // Column 9: Element
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.Element ?? string.Empty);

                // Column 10: In Combat
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.IsInCombat ? "Yes" : "No");

                // Column 11: Has Been In Combat
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.HasBeenInCombat ? "Yes" : "No");

                // Column 12: Territory ID
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.TerritoryId.ToString());

                // Column 13: Map ID
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.MapId.ToString());

                // Column 14: Timestamp
                ImGui.TableNextColumn();
                var dateTime = DateTimeOffset.FromUnixTimeSeconds(enemy.TimeStamp).DateTime;
                ImGui.TextUnformatted(dateTime.ToString("HH:mm:ss"));
            }
        }
    }
}
