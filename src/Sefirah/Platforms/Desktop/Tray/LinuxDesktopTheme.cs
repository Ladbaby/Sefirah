using Tmds.DBus.Protocol;

namespace Sefirah.Platforms.Desktop.Tray;

/// <summary>
/// Resolves whether the desktop's panels are dark, so the tray glyph can use the right polarity.
/// </summary>
internal static class LinuxDesktopTheme
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string PortalSettingsInterface = "org.freedesktop.portal.Settings";

    /// <summary>
    /// True when the panel background is dark (and therefore a light glyph is needed).
    /// Consults the freedesktop appearance portal, falling back to the GTK theme name.
    /// </summary>
    public static async Task<bool> UseDarkPanelAsync(DBusConnection connection)
    {
        var scheme = await ReadColorSchemeAsync(connection);

        // 0 = no preference, 1 = prefer dark, 2 = prefer light.
        if (scheme == 1)
            return true;
        if (scheme == 2)
            return false;

        return true;
    }

    private static async Task<uint?> ReadColorSchemeAsync(DBusConnection connection)
    {
        try
        {
            MessageBuffer message;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    destination: PortalService,
                    path: PortalPath,
                    @interface: PortalSettingsInterface,
                    signature: "ss",
                    member: "Read");
                writer.WriteString("org.freedesktop.appearance");
                writer.WriteString("color-scheme");
                message = writer.CreateMessage();
            }

            return await connection.CallMethodAsync(
                message,
                static (Message response, object? _) =>
                {
                    var reader = response.GetBodyReader();
                    return TryReadUInt32(reader.ReadVariantValue());
                });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read desktop color scheme from the portal: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The portal nests the value inside one or more variants (typically two: <c>(&lt;&lt;uint32 2&gt;&gt;)</c>).
    /// VariantValue cannot report its own signature for variant types, so unwrap optimistically.
    /// </summary>
    private static uint? TryReadUInt32(VariantValue value)
    {
        for (var depth = 0; depth < 4; depth++)
        {
            try
            {
                return value.GetUInt32();
            }
            catch
            {
                // Not a uint yet; peel one variant layer.
            }

            try
            {
                value = value.GetVariantValue();
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
