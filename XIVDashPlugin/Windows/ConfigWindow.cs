using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace XIVDashPlugin.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Configuration _config;
    private readonly SyncService _sync;

    private string _url = string.Empty;
    private string _token = string.Empty;
    private string _status = string.Empty;
    private volatile bool _syncing;

    public ConfigWindow(Configuration config, SyncService sync)
        : base("XIVDash Connect###XIVDashConfig", ImGuiWindowFlags.AlwaysAutoResize)
    {
        _config = config;
        _sync = sync;
        _url = config.XIVDashUrl;
        _token = config.ApiToken;
    }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(0.78f, 0.66f, 0.29f, 1f), "XIVDash Connect");
        ImGui.SameLine();
        ImGui.TextDisabled("v1.0");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("URL XIVDash");
        ImGui.SetNextItemWidth(320);
        if (ImGui.InputText("##url", ref _url, 256))
        {
            if (Uri.TryCreate(_url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                _config.XIVDashUrl = _url;
                _config.Save();
            }
        }

        if (!string.IsNullOrWhiteSpace(_url) &&
            (!Uri.TryCreate(_url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps))
            ImGui.TextColored(new Vector4(0.87f, 0.33f, 0.33f, 1f), "URL invalide (https:// requis)");

        ImGui.Spacing();
        ImGui.Text("Token API (depuis Profil → Dalamud)");
        ImGui.SetNextItemWidth(320);
        if (ImGui.InputText("##token", ref _token, 128, ImGuiInputTextFlags.Password))
        {
            _config.ApiToken = _token;
            _config.Save();
        }

        ImGui.Spacing();
        var autoSync = _config.AutoSyncOnZoneChange;
        if (ImGui.Checkbox("Synchro auto au changement de zone", ref autoSync))
        {
            _config.AutoSyncOnZoneChange = autoSync;
            _config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var canSync = !_syncing && !string.IsNullOrWhiteSpace(_config.ApiToken);
        if (!canSync) ImGui.BeginDisabled();

        if (ImGui.Button("Synchroniser maintenant", new Vector2(200, 0)))
            _ = DoSync();

        if (!canSync) ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(_status))
        {
            ImGui.Spacing();
            var colour = _status.StartsWith("Synchro OK")
                ? new Vector4(0.30f, 0.69f, 0.49f, 1f)
                : new Vector4(0.87f, 0.33f, 0.33f, 1f);
            ImGui.TextColored(colour, _status);
        }
    }

    private async Task DoSync()
    {
        _syncing = true;
        _status = "Synchronisation en cours…";
        try
        {
            var result = await _sync.SyncAsync(_config.ApiToken, _config.XIVDashUrl);
            _status = result.Message;
        }
        catch (Exception ex)
        {
            _status = $"Erreur : {ex.Message}";
        }
        finally
        {
            _syncing = false;
        }
    }
}
