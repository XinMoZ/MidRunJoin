using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace MidRunJoin;

/// <summary>
/// Uniquely identifies a multiplayer run so stashed player progress can only be restored into
/// the exact same run (matching seed, creation timestamp, host, ascension, game mode, and acts).
/// </summary>
public sealed record RunFingerprint(
    string Seed,
    long StartTime,
    ulong HostId,
    int Ascension,
    int GameMode,
    string ActIds)
{
    public static RunFingerprint FromRun(SerializableRun run)
    {
        string seed = run.SerializableRng?.Seed ?? "";
        ulong hostId = run.Players.Count > 0 ? run.Players[0].NetId : 0;
        string actIds = string.Join(",", run.Acts.Select(a => a.Id?.ToString() ?? ""));
        return new RunFingerprint(seed, run.StartTime, hostId, run.Ascension, (int)run.GameMode, actIds);
    }

    public bool Matches(RunFingerprint? other) =>
        other != null
        && string.Equals(Seed, other.Seed, StringComparison.Ordinal)
        && StartTime == other.StartTime
        && HostId == other.HostId
        && Ascension == other.Ascension
        && GameMode == other.GameMode
        && string.Equals(ActIds, other.ActIds, StringComparison.Ordinal);
}

public sealed class StashedPlayerEntry
{
    [JsonPropertyName("player_id")]
    public ulong PlayerId { get; set; }

    [JsonPropertyName("character_id")]
    public string CharacterId { get; set; } = "";

    [JsonPropertyName("saved_host_floor_count")]
    public int SavedHostFloorCount { get; set; }

    [JsonPropertyName("player_json")]
    public string PlayerJson { get; set; } = "";
}

public sealed class StashFilePayload
{
    [JsonPropertyName("fingerprint")]
    public RunFingerprint? Fingerprint { get; set; }

    [JsonPropertyName("players")]
    public List<StashedPlayerEntry> Players { get; set; } = new();
}

/// <summary>
/// Persists removed players' personal progress for the current multiplayer run so they can
/// rejoin later with their original character, deck, relics, gold, and delta catch-up rewards.
/// Automatically cleared whenever the multiplayer save is completed or deleted via
/// <see cref="SaveManager.DeleteCurrentMultiplayerRun"/>.
/// </summary>
[HarmonyPatch]
internal static class PlayerStash
{
    private const string FileName = "midrunjoin_player_stash.json";

    private static readonly JsonSerializerOptions StashJsonOptions = new()
    {
        WriteIndented = false
    };

    internal static string? CustomFilePathOverride { get; set; }

    internal static string GetStashFilePath()
    {
        if (!string.IsNullOrWhiteSpace(CustomFilePathOverride))
            return CustomFilePathOverride!;
        try
        {
            string userPath = ProjectSettings.GlobalizePath($"user://{FileName}");
            if (!string.IsNullOrWhiteSpace(userPath))
                return userPath;
        }
        catch
        {
            // Fall through when Godot ProjectSettings is not initialized.
        }
        return Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "MidRunJoin",
            FileName);
    }

    internal static StashedPlayerEntry CreateEntry(SerializableRun run, SerializablePlayer player)
    {
        var playerTypeInfo = JsonSerializationUtility.GetTypeInfo<SerializablePlayer>();
        string playerJson = JsonSerializer.Serialize(player, playerTypeInfo);
        ulong hostId = run.Players.Count > 0 ? run.Players[0].NetId : 0;
        int hostFloors = CountHostFloors(run, hostId);
        return new StashedPlayerEntry
        {
            PlayerId = player.NetId,
            CharacterId = player.CharacterId?.ToString() ?? "",
            SavedHostFloorCount = hostFloors,
            PlayerJson = playerJson
        };
    }

    internal static SerializablePlayer? DeserializePlayer(StashedPlayerEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.PlayerJson))
            return null;
        try
        {
            var playerTypeInfo = JsonSerializationUtility.GetTypeInfo<SerializablePlayer>();
            return JsonSerializer.Deserialize(entry.PlayerJson, playerTypeInfo);
        }
        catch (Exception ex)
        {
            Log.Warn($"[MidRunJoin] Failed to deserialize stashed player {entry.PlayerId}: {ex.Message}");
            return null;
        }
    }

    internal static Dictionary<ulong, StashedPlayerEntry> LoadForRun(SerializableRun run)
    {
        var result = new Dictionary<ulong, StashedPlayerEntry>();
        var payload = ReadPayload();
        if (payload?.Fingerprint == null)
            return result;

        var currentFp = RunFingerprint.FromRun(run);
        if (!currentFp.Matches(payload.Fingerprint))
            return result;

        foreach (var entry in payload.Players)
        {
            if (entry.PlayerId != 0 && !string.IsNullOrWhiteSpace(entry.PlayerJson))
                result[entry.PlayerId] = entry;
        }
        return result;
    }

    internal static void SaveForRun(SerializableRun run, IEnumerable<StashedPlayerEntry> entries)
    {
        var fp = RunFingerprint.FromRun(run);
        var existing = LoadForRun(run);
        foreach (var entry in entries)
        {
            if (entry.PlayerId != 0 && !string.IsNullOrWhiteSpace(entry.PlayerJson))
                existing[entry.PlayerId] = entry;
        }

        if (existing.Count == 0)
        {
            Clear();
            return;
        }

        var payload = new StashFilePayload
        {
            Fingerprint = fp,
            Players = existing.Values.OrderBy(e => e.PlayerId).ToList()
        };
        WritePayload(payload);
        Log.Info($"[MidRunJoin] Saved {payload.Players.Count} stashed player(s) for run seed={fp.Seed} startTime={fp.StartTime}.");
    }

    internal static void Clear()
    {
        try
        {
            string path = GetStashFilePath();
            if (File.Exists(path))
            {
                File.Delete(path);
                Log.Info("[MidRunJoin] Cleared stashed player progress cache.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[MidRunJoin] Failed to clear player stash: {ex.Message}");
        }
    }

    private static StashFilePayload? ReadPayload()
    {
        try
        {
            string path = GetStashFilePath();
            if (!File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StashFilePayload>(json, StashJsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"[MidRunJoin] Failed to read player stash: {ex.Message}");
            return null;
        }
    }

    private static void WritePayload(StashFilePayload payload)
    {
        try
        {
            string path = GetStashFilePath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(payload, StashJsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"[MidRunJoin] Failed to write player stash: {ex.Message}");
        }
    }

    private static int CountHostFloors(SerializableRun run, ulong hostId)
    {
        int count = 0;
        foreach (var act in run.MapPointHistory)
        {
            foreach (var entry in act)
            {
                if (hostId != 0 && entry.PlayerStats.Count > 0 && !entry.PlayerStats.Any(ps => ps.PlayerId == hostId))
                    continue;
                count++;
            }
        }
        return count;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(SaveManager), nameof(SaveManager.DeleteCurrentMultiplayerRun))]
    private static void OnMultiplayerRunDeleted()
    {
        Clear();
    }
}
