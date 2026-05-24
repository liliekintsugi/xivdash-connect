using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XIVDashPlugin.Windows;

namespace XIVDashPlugin;

public sealed class Plugin : IDalamudPlugin
{
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private readonly ICommandManager _commands;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;
    private readonly Configuration _config;
    private readonly SyncService _sync;
    private readonly WindowSystem _windowSystem;
    private readonly ConfigWindow _configWindow;

    private bool _lastLoggedIn;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IClientState clientState,
        IPluginLog log,
        IDataManager dataManager)
    {
        PluginInterface = pluginInterface;
        _commands = commands;
        _clientState = clientState;
        _log = log;

        _config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _sync = new SyncService(dataManager);
        _windowSystem = new WindowSystem("XIVDashPlugin");
        _configWindow = new ConfigWindow(_config, _sync);
        _windowSystem.AddWindow(_configWindow);

        _commands.AddHandler("/xivdash", new CommandInfo(OnCommand)
        {
            HelpMessage = "Ouvre la fenêtre de configuration XIVDash Connect",
        });

        pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi += OpenConfig;

        // Auto-sync on zone change
        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Login += OnLogin;
    }

    private void OnCommand(string command, string args) => OpenConfig();

    private void OpenConfig() => _configWindow.IsOpen = true;

    private void OnLogin()
    {
        if (!_config.AutoSyncOnZoneChange || string.IsNullOrWhiteSpace(_config.ApiToken)) return;
        _ = TriggerSync("connexion");
    }

    private void OnTerritoryChanged(uint territory)
    {
        if (!_config.AutoSyncOnZoneChange || string.IsNullOrWhiteSpace(_config.ApiToken)) return;
        _ = TriggerSync($"zone #{territory}");
    }

    private async Task TriggerSync(string reason)
    {
        try
        {
            _log.Debug($"[XIVDash] Déclenchement synchro ({reason})");
            var result = await _sync.SyncAsync(_config.ApiToken, _config.XIVDashUrl);
            _log.Information($"[XIVDash] {result.Message}");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[XIVDash] Erreur de synchro");
        }
    }

    public void Dispose()
    {
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Login -= OnLogin;
        _commands.RemoveHandler("/xivdash");
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;
        _sync.Dispose();
    }
}
