using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace XIVDashPlugin;

public sealed class SyncService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private const uint QuestIdMin = 65536;
    private const uint QuestIdMax = 72000;

    public Task<SyncResult> SyncAsync(string token, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult(new SyncResult(false, "URL invalide (HTTPS requis)", 0, 0, 0));

        var completedIds      = GetCompletedQuestIds();
        var jobs              = GetJobLevels();
        var completedRoulettes = GetCompletedRouletteIds();

        return Task.Run(async () =>
        {
            if (!await _syncLock.WaitAsync(0))
                return new SyncResult(false, "Synchro déjà en cours", 0, 0, 0);

            try
            {
                var payload = new { completedQuestIds = completedIds, jobs, completedRouletteIds = completedRoulettes };
                var json    = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/api/dalamud/sync");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = content;

                var response = await _http.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                    return new SyncResult(false, $"Erreur {(int)response.StatusCode}", completedIds.Count, jobs.Count, completedRoulettes.Count);

                var questWord    = completedIds.Count == 1 ? "quête" : "quêtes";
                var jobWord      = jobs.Count == 1 ? "job" : "jobs";
                var rouletteWord = completedRoulettes.Count == 1 ? "roulette" : "roulettes";
                return new SyncResult(true, $"Synchro OK — {completedIds.Count} {questWord}, {jobs.Count} {jobWord}, {completedRoulettes.Count} {rouletteWord}", completedIds.Count, jobs.Count, completedRoulettes.Count);
            }
            finally
            {
                _syncLock.Release();
            }
        });
    }

    private static unsafe List<uint> GetCompletedQuestIds()
    {
        var completedIds = new List<uint>();
        for (uint id = QuestIdMin; id <= QuestIdMax; id++)
        {
            if (QuestManager.IsQuestComplete((ushort)(id & 0xFFFF)))
                completedIds.Add(id);
        }
        return completedIds;
    }

    private static unsafe List<object> GetJobLevels()
    {
        var jobs        = new List<object>();
        var playerState = PlayerState.Instance();
        if (playerState == null) return jobs;

        var levels = playerState->ClassJobLevels;
        foreach (var (expIdx, abbrev) in GetClassJobMap())
        {
            if (expIdx < levels.Length)
            {
                var level = levels[expIdx];
                if (level > 0)
                    jobs.Add(new { abbrev, level = (int)level });
            }
        }
        return jobs;
    }

    private static unsafe List<int> GetCompletedRouletteIds()
    {
        var completed   = new List<int>();
        var playerState = PlayerState.Instance();
        if (playerState == null) return completed;

        // _contentRouletteCompletion sits at offset 0x520 in PlayerState.
        // Each byte is non-zero when the daily bonus for that roulette has been collected.
        // IsRouletteComplete() skips index 0 (early exit on dl==0), so we read the array
        // directly. CompletionArrayIndex → ContentRoulette RowId (XIVAPI verified):
        //  0→1 (Leveling), 2→3 (Main Scenario), 3→4 (Guildhests),
        //  4→5 (Expert), 5→6 (Trials), 8→9 (Mentor),
        //  9→15 (Alliance Raids), 10→17 (Normal Raids)
        byte* arr = (byte*)playerState + 0x520;

        (byte idx, int rowId)[] map =
        [
            (0,  1),  // Leveling
            (2,  3),  // Main Scenario
            (3,  4),  // Guildhests
            (4,  5),  // Expert
            (5,  6),  // Trials
            (8,  9),  // Mentor
            (9,  15), // Alliance Raids
            (10, 17), // Normal Raids
        ];

        foreach (var (idx, rowId) in map)
        {
            if (arr[idx] != 0)
                completed.Add(rowId);
        }
        return completed;
    }

    // ExpArrayIndex → job abbreviation (verified via XIVAPI)
    private static Dictionary<int, string> GetClassJobMap() => new()
    {
        [0]  = "MNK",  [1]  = "PLD",  [2]  = "WAR",  [3]  = "BRD",
        [4]  = "DRG",  [5]  = "BLM",  [6]  = "WHM",  [7]  = "CRP",
        [8]  = "BSM",  [9]  = "ARM",  [10] = "GSM",  [11] = "LTW",
        [12] = "WVR",  [13] = "ALC",  [14] = "CUL",  [15] = "MIN",
        [16] = "BTN",  [17] = "FSH",  [18] = "SMN",  [19] = "NIN",
        [20] = "MCH",  [21] = "DRK",  [22] = "AST",  [23] = "SAM",
        [24] = "RDM",  [25] = "BLU",  [26] = "GNB",  [27] = "DNC",
        [28] = "RPR",  [29] = "SGE",  [30] = "VPR",  [31] = "PCT",
    };

    public void Dispose()
    {
        _http.Dispose();
        _syncLock.Dispose();
    }
}

public record SyncResult(bool Success, string Message, int QuestCount, int JobCount, int RouletteCount = 0);
