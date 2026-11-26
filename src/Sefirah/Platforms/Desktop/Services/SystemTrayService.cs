using Sefirah.Data.Contracts;
using Sefirah.Platforms.Desktop.Tray;
using Sefirah.Platforms.Desktop.Tray.DBus;
using Tmds.DBus.Protocol;

namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// Linux system tray using the StatusNotifierItem D-Bus protocol.
/// </summary>
public sealed class SystemTrayService : ISystemTrayService
{
    private const string WatcherService = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string MenuPath = "/com/castle/sefirah/dbusmenu/0";

    private readonly ILogger<SystemTrayService> logger;
    private readonly IAppThemeModeService? themeModeService;
    private DBusConnection? connection;
    private LinuxStatusNotifierItemHandler? itemHandler;
    private LinuxTrayMenuHandler? menuHandler;
    private IDisposable? watcherWatch;
    private string? serviceName;
    private bool disposed;
    private bool registered;
    private bool desktopUsesDarkPanel = true;

    public SystemTrayService(ILogger<SystemTrayService> logger)
        : this(logger, null)
    {
    }

    public SystemTrayService(ILogger<SystemTrayService> logger, IAppThemeModeService? themeModeService)
    {
        this.logger = logger;
        this.themeModeService = themeModeService;

        if (OperatingSystem.IsLinux())
            _ = InitializeAsync();
    }

    public bool IsAvailable { get; private set; }

    private async Task InitializeAsync()
    {
        try
        {
            var sessionAddress = DBusAddress.Session
                ?? throw new InvalidOperationException("D-Bus session bus is not available.");

            connection = new DBusConnection(sessionAddress);
            await connection.ConnectAsync();

            serviceName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";

            await ResolveDesktopPanelAsync();

            menuHandler = new LinuxTrayMenuHandler(connection, logger, MenuPath);
            itemHandler = new LinuxStatusNotifierItemHandler(connection, logger, menuHandler, GetTrayIconPath());

            connection.AddMethodHandler(menuHandler);
            connection.AddMethodHandler(itemHandler);

            itemHandler.Activated += (_, _) => App.TrayToggleWindow();

            if (themeModeService is not null)
                themeModeService.AppThemeModeChanged += OnThemeModeChanged;

            if (!await connection.TryRequestNameAsync(serviceName, RequestNameOptions.ReplaceExisting))
            {
                logger.Warn($"Failed to acquire D-Bus tray service name: {serviceName}");
                return;
            }

            // Register the tray while the host is up, and keep watching for it across host restarts
            // (e.g. the shell's StatusNotifierWatcher claiming its name after the app already started).
            await WatchWatcherAsync();

            if (await IsWatcherAvailableAsync())
                await RegisterTrayAsync();
            else
                logger.Info($"Waiting for {WatcherService} to appear before showing the tray icon");
        }
        catch (Exception ex)
        {
            logger.Warn("Failed to initialize Linux system tray", ex);
            Dispose();
        }
    }

    /// <summary>
    /// Subscribes to name-ownership changes for the tray host, so the icon is registered even when the
    /// host starts after the app. GNOME Shell's appindicator extension claims this name during startup and
    /// can lose/regain it across restarts, so a one-shot check is not enough.
    /// </summary>
    private async Task WatchWatcherAsync()
    {
        if (connection is null)
            return;

        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = "org.freedesktop.DBus",
            Path = "/org/freedesktop/DBus",
            Interface = "org.freedesktop.DBus",
            Member = "NameOwnerChanged",
            Arg0 = WatcherService
        };

        watcherWatch = await connection.AddMatchAsync(
            rule,
            static (Message message, object? _) => ReadNameOwnerChanged(message),
            OnWatcherOwnerChangedAsync,
            emitOnCapturedContext: false,
            ObserverFlags.None,
            state: (object?)null);
    }

    private static (string Owner, string OldOwner) ReadNameOwnerChanged(Message message)
    {
        var reader = message.GetBodyReader();
        _ = reader.ReadString(); // name
        var oldOwner = reader.ReadString();
        var newOwner = reader.ReadString();
        return (newOwner, oldOwner);
    }

    private async ValueTask OnWatcherOwnerChangedAsync(Notification<(string Owner, string OldOwner)> notification)
    {
        if (disposed || connection is null || serviceName is null)
            return;

        var (newOwner, _) = notification.Value;

        try
        {
            if (string.IsNullOrEmpty(newOwner))
            {
                if (registered)
                    logger.Info($"{WatcherService} went away; tray icon will be re-registered when it returns");

                registered = false;
                IsAvailable = false;
                return;
            }

            if (!registered)
            {
                logger.Info($"{WatcherService} is now available; registering tray icon");
                await RegisterTrayAsync();
            }
        }
        catch (Exception ex)
        {
            logger.Warn("Failed to update Linux tray registration", ex);
        }
    }

    private void OnThemeModeChanged(object? sender, EventArgs e)
    {
        // Tray icons are static pixmaps; refresh them so the glyph polarity matches the new theme.
        if (itemHandler is null)
            return;

        var iconPath = GetTrayIconPath();
        if (!File.Exists(iconPath))
        {
            logger.Warn($"Tray icon file missing at {iconPath}");
            return;
        }

        ApplyIcon(iconPath);
    }

    private string GetTrayIconPath()
        => LinuxTrayIconLoader.GetTrayIconPath(UseDarkPanel());

    private bool UseDarkPanel()
    {
        var requested = themeModeService?.Theme ?? Theme.Default;
        if (requested is Theme.Dark)
            return true;
        if (requested is Theme.Light)
            return false;

        // Theme.Default follows the desktop; resolve it against the portal once at startup.
        return desktopUsesDarkPanel;
    }

    private async Task ResolveDesktopPanelAsync()
    {
        if (connection is null)
            return;

        try
        {
            desktopUsesDarkPanel = await LinuxDesktopTheme.UseDarkPanelAsync(connection);
            logger.Info($"Desktop panel resolved as {(desktopUsesDarkPanel ? "dark" : "light")}");
        }
        catch (Exception ex)
        {
            logger.Debug($"Could not resolve desktop panel polarity: {ex.Message}");
        }
    }

    private void ApplyIcon(string iconPath)
    {
        var pixmaps = LinuxTrayIconLoader.LoadPixmaps(iconPath);
        itemHandler!.SetIcon(pixmaps);
        logger.Info($"Loaded tray icon from {iconPath} ({pixmaps.Length} sizes)");
    }

    private async Task RegisterTrayAsync()
    {
        if (connection is null || itemHandler is null || serviceName is null)
            return;

        try
        {
            var iconPath = itemHandler.IconPath;
            if (File.Exists(iconPath))
            {
                ApplyIcon(iconPath);
            }
            else
            {
                logger.Warn($"Tray icon file missing at {iconPath}; using fallback pixel");
            }

            var watcher = new StatusNotifierWatcher(connection, WatcherService, WatcherPath);
            await watcher.RegisterStatusNotifierItemAsync(serviceName);

            registered = true;
            IsAvailable = true;
            logger.Info($"Linux tray icon registered at {serviceName} (menu at {MenuPath})");
        }
        catch (Exception ex)
        {
            logger.Warn("Failed to register Linux tray icon", ex);
            registered = false;
            IsAvailable = false;
        }
    }

    private async Task<bool> IsWatcherAvailableAsync()
    {
        if (connection is null)
            return false;

        try
        {
            return await GetNameOwnerAsync(connection, WatcherService) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> GetNameOwnerAsync(DBusConnection dbus, string name)
    {
        try
        {
            MessageBuffer message;
            using (var writer = dbus.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    destination: "org.freedesktop.DBus",
                    path: "/org/freedesktop/DBus",
                    @interface: "org.freedesktop.DBus",
                    signature: "s",
                    member: "GetNameOwner");
                writer.WriteString(name);
                message = writer.CreateMessage();
            }

            return await dbus.CallMethodAsync(
                message,
                static (Message response, object? _) => response.GetBodyReader().ReadString());
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName is "org.freedesktop.DBus.Error.NameHasNoOwner")
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        IsAvailable = false;
        registered = false;

        if (themeModeService is not null)
            themeModeService.AppThemeModeChanged -= OnThemeModeChanged;

        watcherWatch?.Dispose();
        watcherWatch = null;

        if (connection is not null)
        {
            if (itemHandler is not null)
                connection.RemoveMethodHandler(itemHandler.Path);
            if (menuHandler is not null)
                connection.RemoveMethodHandler(menuHandler.Path);
            connection.Dispose();
        }

        connection = null;
        itemHandler = null;
        menuHandler = null;
    }
}
