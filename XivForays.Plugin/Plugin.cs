using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using XivMate.DataGathering.Forays.Dalamud.Extensions;
using XivMate.DataGathering.Forays.Dalamud.Gathering;
using XivMate.DataGathering.Forays.Dalamud.Gathering.Enemy;
using XivMate.DataGathering.Forays.Dalamud.Gathering.Fate;
using XivMate.DataGathering.Forays.Dalamud.Services;
using XivMate.DataGathering.Forays.Dalamud.Windows;
using XivMate.DataGathering.Forays.Dalamud.Windows.Tabs;

namespace XivMate.DataGathering.Forays.Dalamud;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static ICondition Condition { get; private set; } = null!;
    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    internal static IGameGui GameGui { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static IFateTable FateTable { get; private set; } = null!;

    [PluginService]
    internal static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IPartyList PartyList { get; private set; } = null!;

    private const string CommandName = "/xivforays";

    public Configuration.Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("XivMate");
    private ServiceProvider provider;
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private ApiKeyWindow ApiKeyWindow { get; set; }
    private CollectorOverlayService CollectorOverlay { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration.Configuration ??
                        new Configuration.Configuration();
        if (Configuration.MigrateOverlay())
            PluginInterface.SavePluginConfig(Configuration);

        SetupServices();
        InitializeModules();

        MainWindow = provider?.GetService<MainWindow>() ?? throw new InvalidOperationException();
        CollectorOverlay = provider.GetRequiredService<CollectorOverlayService>();
        CollectorOverlay.ApplyConfig(Configuration);
        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
        
        
        RegisterCommands();

        var tabs = provider.GetServices<ITab>();
        ConfigWindow = new ConfigWindow(this, tabs, Log);
        ApiKeyWindow = new ApiKeyWindow(this);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ApiKeyWindow);

        if (string.IsNullOrWhiteSpace(Configuration.SystemConfiguration.ApiKey))
        {
            Log.Info("No API key found, opening API key window");
            if (!ApiKeyWindow.IsOpen)
                ApiKeyWindow.Toggle();
        }
    }

    private void SetupServices()
    {
        var services = ConfigureServices();
        provider = services.BuildServiceProvider();
    }

    private void InitializeModules()
    {
        var modules = provider.GetServices<IModule>();
        foreach (var module in modules)
        {
            module.LoadConfig(Configuration);
        }
    }

    private void RegisterCommands()
    {
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open XivForays main window"
        });
    }

    private ServiceCollection ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton(this);
        services.AddSingleton(TextureProvider);
        services.AddSingleton(ChatGui);
        services.AddSingleton(Condition);
        services.AddSingleton(PluginInterface);
        services.AddSingleton(CommandManager);
        services.AddSingleton(ClientState);
        services.AddSingleton(DataManager);
        services.AddSingleton(Framework);
        services.AddSingleton(FateTable);
        services.AddSingleton(ObjectTable);
        services.AddSingleton(PartyList);
        services.AddSingleton(Log);
        services.AddSingleton(GameGui);
        services.AddSingleton<Core.FateRewardWindow>();
        services.AddSingleton<FateModule>();
        services.AddSingleton<SchedulerService>();
        services.AddSingleton<TerritoryService>();
        services.AddSingleton<ApiService>();
        services.AddSingleton<PoseCaptureService>();
        services.AddSingleton<PlayerContextSampler>();
        services.AddSingleton<EnemyTrackingService>();
        services.AddSingleton<EnemyLocationGatherer>();
        services.AddSingleton<ForayService>();
        services.AddSingleton<CollectorOverlayService>();
        services.AddAllTypesImplementing<ITab>();
        services.AddSingleton<ObservationService>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<ObservationService>());
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<FateModule>());
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<EnemyLocationGatherer>());
        services.AddSingleton<IModule, ForaySignalModule>();
        services.AddSingleton<MainWindow>();
        return services;
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        provider.Dispose();
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;
    }

    private void OnCommand(string command, string args)
    {
        // in response to the slash command, just toggle the display status of our main ui
        if (args == "config")
            ToggleConfigUI();
        else
            ToggleMainUI();
    }

    private void DrawUI()
    {
        WindowSystem.Draw();
        CollectorOverlay.Draw();
    }

    public void ToggleConfigUI() => ConfigWindow.Toggle();
    public void ToggleMainUI() => MainWindow.Toggle();

    public void ReloadModules()
    {
        foreach (var module in provider.GetServices<IModule>())
            module.LoadConfig(Configuration);
        CollectorOverlay.ApplyConfig(Configuration);
    }
}
