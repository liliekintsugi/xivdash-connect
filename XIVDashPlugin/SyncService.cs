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

    private const uint QuestIdMin = 65536;
    private const uint QuestIdMax = 72000;

    public event Action<string>? OnStatus;

    public Task<SyncResult> SyncAsync(string token, string baseUrl)
    {
        // Read game data synchronously on the calling (game) thread before going async
        var completedIds = GetCompletedQuestIds();
        var jobs = GetJobLevels();

        return Task.Run(async () =>
        {
            var payload = new { completedQuestIds = completedIds, jobs };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var url = baseUrl.TrimEnd('/') + "/api/dalamud/sync";
            var response = await _http.PostAsync(url, content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new SyncResult(false, $"Erreur {(int)response.StatusCode}: {body}", completedIds.Count, jobs.Count);

            var jobWord = jobs.Count == 1 ? "job" : "jobs";
            var questWord = completedIds.Count == 1 ? "quête" : "quêtes";
            return new SyncResult(true, $"Synchro OK — {completedIds.Count} {questWord}, {jobs.Count} {jobWord}", completedIds.Count, jobs.Count);
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
        var jobs = new List<object>();
        var playerState = PlayerState.Instance();
        if (playerState == null) return jobs;

        // ClassJobLevels is indexed by ExpArrayIndex from the ClassJob sheet
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

    // ExpArrayIndex from ClassJob sheet → abbreviation
    // Shared class/job lines (e.g. GLD+PLD) have one ExpArrayIndex; we use the job name
    private static Dictionary<int, string> GetClassJobMap() => new()
    {
        [1]  = "PLD",  // GLD → PLD
        [2]  = "MNK",  // PGL → MNK
        [3]  = "WAR",  // MRD → WAR
        [4]  = "DRG",  // LNC → DRG
        [5]  = "BRD",  // ARC → BRD
        [6]  = "WHM",  // CNJ → WHM
        [7]  = "BLM",  // THM → BLM
        [8]  = "CRP",
        [9]  = "BSM",
        [10] = "ARM",
        [11] = "GSM",
        [12] = "LTW",
        [13] = "WVR",
        [14] = "ALC",
        [15] = "CUL",
        [16] = "MIN",
        [17] = "BTN",
        [18] = "FSH",
        [19] = "NIN",  // ROG → NIN
        [20] = "MCH",
        [21] = "DRK",
        [22] = "AST",
        [23] = "SAM",
        [24] = "RDM",
        [25] = "BLU",
        [26] = "SMN",  // ACN → SMN (shares level with SCH)
        [27] = "SCH",
        [28] = "GNB",
        [29] = "DNC",
        [30] = "RPR",
        [31] = "SGE",
        [32] = "VPR",
        [33] = "PCT",
    };

    public void Dispose() => _http.Dispose();
}

public record SyncResult(bool Success, string Message, int QuestCount, int JobCount);
