using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace XIVDashPlugin;

public sealed class SyncService : IDisposable
{
    private readonly HttpClient  _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly IDataManager _dataManager;

    private const uint QuestIdMin = 65536;
    private const uint QuestIdMax = 72000;

    // ContentType IDs we care about for completion tracking
    // 2=Dungeons  4=Trials  5=Raids (normal + alliance)  28=Ultimate Raids
    private static readonly HashSet<uint> RelevantContentTypes = [2, 4, 5, 28];

    public SyncService(IDataManager dataManager)
    {
        _dataManager = dataManager;
    }

    public Task<SyncResult> SyncAsync(string token, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult(new SyncResult(false, "URL invalide (HTTPS requis)", 0, 0, 0, 0));

        var completedIds       = GetCompletedQuestIds();
        var jobs               = GetJobLevels();
        var completedRoulettes = GetCompletedRouletteIds();
        var completedContent   = GetCompletedContentIds();

        return Task.Run(async () =>
        {
            if (!await _syncLock.WaitAsync(0))
                return new SyncResult(false, "Synchro déjà en cours", 0, 0, 0, 0);

            try
            {
                var payload = new
                {
                    completedQuestIds    = completedIds,
                    jobs,
                    completedRouletteIds = completedRoulettes,
                    completedContentIds  = completedContent,
                };
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    baseUrl.TrimEnd('/') + "/api/dalamud/sync");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = content;

                var response = await _http.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                    return new SyncResult(false, $"Erreur {(int)response.StatusCode}",
                        completedIds.Count, jobs.Count, completedRoulettes.Count, completedContent.Count);

                var qWord = completedIds.Count     == 1 ? "quête"   : "quêtes";
                var jWord = jobs.Count             == 1 ? "job"     : "jobs";
                var cWord = completedContent.Count == 1 ? "contenu" : "contenus";
                return new SyncResult(true,
                    $"Synchro OK — {completedIds.Count} {qWord}, {jobs.Count} {jWord}, {completedContent.Count} {cWord}",
                    completedIds.Count, jobs.Count, completedRoulettes.Count, completedContent.Count);
            }
            finally
            {
                _syncLock.Release();
            }
        });
    }

    private static unsafe List<uint> GetCompletedQuestIds()
    {
        var ids = new List<uint>();
        for (uint id = QuestIdMin; id <= QuestIdMax; id++)
            if (QuestManager.IsQuestComplete((ushort)(id & 0xFFFF)))
                ids.Add(id);
        return ids;
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
                if (level > 0) jobs.Add(new { abbrev, level = (int)level });
            }
        }
        return jobs;
    }

    private static unsafe List<int> GetCompletedRouletteIds()
    {
        var completed   = new List<int>();
        var playerState = PlayerState.Instance();
        if (playerState == null) return completed;

        byte* arr = (byte*)playerState + 0x520;
        (byte idx, int rowId)[] map =
        [
            (0,  1), (2, 3),  (3,  4), (4,  5),
            (5,  6), (8, 9),  (9, 15), (10, 17),
        ];
        foreach (var (idx, rowId) in map)
            if (arr[idx] != 0) completed.Add(rowId);

        return completed;
    }

    /// <summary>
    /// Retourne les ContentFinderCondition row IDs (= XIVAPI IDs) des instances
    /// complétées au moins une fois.
    ///
    /// Utilise UIState.IsInstanceContentCompleted(instanceContentId) — la méthode
    /// correcte, vérifiée dans FFXIVClientStructs. Le paramètre attendu est l'ID
    /// de la feuille InstanceContent, qui diffère du CFC row ID (ex. : Labyrinth
    /// of the Ancients a CFC=92 mais InstanceContent=30001). On obtient ce mapping
    /// via Lumina : ContentFinderCondition.Content.RowId.
    /// </summary>
    private unsafe List<uint> GetCompletedContentIds()
    {
        var completed = new List<uint>();

        var cfcSheet = _dataManager.GetExcelSheet<ContentFinderCondition>();
        if (cfcSheet == null) return completed;

        foreach (var row in cfcSheet)
        {
            // Filtrer sur les types pertinents
            if (!RelevantContentTypes.Contains(row.ContentType.RowId)) continue;

            // N'inclure que le contenu visible dans le Duty Finder
            if (!row.IsInDutyFinder) continue;

            // Récupérer l'InstanceContent ID (≠ CFC row ID dans le cas général)
            var instanceContentId = row.Content.RowId;
            if (instanceContentId == 0) continue;

            if (UIState.IsInstanceContentCompleted(instanceContentId))
                completed.Add(row.RowId);   // on envoie le CFC row ID = xivapi_id
        }
        return completed;
    }

    private static Dictionary<int, string> GetClassJobMap() => new()
    {
        [0]  = "MNK", [1]  = "PLD", [2]  = "WAR", [3]  = "BRD",
        [4]  = "DRG", [5]  = "BLM", [6]  = "WHM", [7]  = "CRP",
        [8]  = "BSM", [9]  = "ARM", [10] = "GSM", [11] = "LTW",
        [12] = "WVR", [13] = "ALC", [14] = "CUL", [15] = "MIN",
        [16] = "BTN", [17] = "FSH", [18] = "SMN", [19] = "NIN",
        [20] = "MCH", [21] = "DRK", [22] = "AST", [23] = "SAM",
        [24] = "RDM", [25] = "BLU", [26] = "GNB", [27] = "DNC",
        [28] = "RPR", [29] = "SGE", [30] = "VPR", [31] = "PCT",
    };

    public void Dispose()
    {
        _http.Dispose();
        _syncLock.Dispose();
    }
}

public record SyncResult(
    bool   Success,
    string Message,
    int    QuestCount,
    int    JobCount,
    int    RouletteCount = 0,
    int    ContentCount  = 0);
