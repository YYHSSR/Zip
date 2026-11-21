using System.Diagnostics;
using Microsoft.Win32;

namespace QuickZip;

/// <summary>
/// Registers ZIP as a handler for archive types, per user. Windows 10 and 11 do not let an app make
/// itself the default silently, so this makes ZIP appear in "Open with" and Settings → Default apps,
/// and <see cref="OpenDefaultAppsSettings"/> takes the user to the page where they confirm the choice.
/// </summary>
internal static class FileAssociation
{
    private const string AppName = "ZIP";
    private const string Classes = @"Software\Classes";
    private const string CapabilitiesKey = @"Software\ZIP\Capabilities";
    private const string ProgIdPrefix = "ZIP.Archive";
    private const string LegacyProgId = "QuickZip.Archive";

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".zip"] = "ZIP 压缩文件", [".7z"] = "7Z 压缩文件", [".rar"] = "RAR 压缩文件",
        [".tar"] = "TAR 归档文件", [".tgz"] = "TAR.GZ 压缩文件", [".txz"] = "TAR.XZ 压缩文件",
        [".tbz"] = "TAR.BZ2 压缩文件", [".tbz2"] = "TAR.BZ2 压缩文件", [".tzst"] = "TAR.ZST 压缩文件",
        [".gz"] = "GZ 压缩文件", [".xz"] = "XZ 压缩文件", [".bz2"] = "BZ2 压缩文件", [".zst"] = "ZST 压缩文件",
        [".iso"] = "ISO 光盘映像", [".cab"] = "CAB 压缩文件", [".wim"] = "WIM 映像文件",
        [".deb"] = "DEB 软件包", [".rpm"] = "RPM 软件包", [".001"] = "分卷压缩文件"
    };

    private static string Executable => Environment.ProcessPath ?? Application.ExecutablePath;
    private static string OpenCommand => $"\"{Executable}\" \"%1\"";
    private static string ProgId(string extension) => $"{ProgIdPrefix}{extension}";

    /// <summary>True when the per-user registration points at this copy of ZIP.exe.</summary>
    public static bool IsRegistered
    {
        get
        {
            if (Native.IsPackaged)
                return true;
            using RegistryKey? apps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications");
            if (apps?.GetValue(AppName) as string != CapabilitiesKey)
                return false;
            using RegistryKey? command = Registry.CurrentUser.OpenSubKey($@"{Classes}\{ProgId(".zip")}\shell\open\command");
            return string.Equals(command?.GetValue(null) as string, OpenCommand, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The archive file-type icon shipped in Assets, or the app icon when running from a dev build.</summary>
    private static string FileIcon
    {
        get
        {
            string archiveIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "Archive.ico");
            return File.Exists(archiveIcon) ? $"\"{archiveIcon}\",0" : $"\"{Executable}\",0";
        }
    }

    public static void Register()
    {
        RegistryKey user = Registry.CurrentUser;
        string icon = $"\"{Executable}\",0";
        string fileIcon = FileIcon;

        using (RegistryKey capabilities = user.CreateSubKey(CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "创建和提取 ZIP、7z、RAR、TAR 等压缩包");
            capabilities.SetValue("ApplicationIcon", icon);
            using RegistryKey map = capabilities.CreateSubKey("FileAssociations");
            foreach (string extension in ArchiveFormat.ReadExtensions)
                map.SetValue(extension, ProgId(extension));
        }
        using (RegistryKey apps = user.CreateSubKey(@"Software\RegisteredApplications"))
            apps.SetValue(AppName, CapabilitiesKey);

        foreach (string extension in ArchiveFormat.ReadExtensions)
        {
            string description = Descriptions.GetValueOrDefault(extension, "压缩文件");
            using (RegistryKey progId = user.CreateSubKey($@"{Classes}\{ProgId(extension)}"))
            {
                progId.SetValue(null, description);
                progId.SetValue("FriendlyTypeName", description);
                using (RegistryKey defaultIcon = progId.CreateSubKey("DefaultIcon"))
                    defaultIcon.SetValue(null, fileIcon);
                using (RegistryKey open = progId.CreateSubKey(@"shell\open"))
                    open.SetValue(null, "用 ZIP 打开");
                using (RegistryKey command = progId.CreateSubKey(@"shell\open\command"))
                    command.SetValue(null, OpenCommand);
            }
            using (RegistryKey openWith = user.CreateSubKey($@"{Classes}\{extension}\OpenWithProgids"))
            {
                openWith.SetValue(ProgId(extension), "");
                openWith.DeleteValue(LegacyProgId, false);
            }
        }

        using (RegistryKey application = user.CreateSubKey($@"{Classes}\Applications\ZIP.exe"))
        {
            application.SetValue("FriendlyAppName", AppName);
            using (RegistryKey command = application.CreateSubKey(@"shell\open\command"))
                command.SetValue(null, OpenCommand);
            using RegistryKey types = application.CreateSubKey("SupportedTypes");
            foreach (string extension in ArchiveFormat.ReadExtensions)
                types.SetValue(extension, "");
        }
        user.DeleteSubKeyTree($@"{Classes}\{LegacyProgId}", false);
        Native.NotifyAssociationsChanged();
    }

    public static void Unregister()
    {
        RegistryKey user = Registry.CurrentUser;
        foreach (string extension in ArchiveFormat.ReadExtensions)
        {
            using (RegistryKey? openWith = user.OpenSubKey($@"{Classes}\{extension}\OpenWithProgids", true))
            {
                openWith?.DeleteValue(ProgId(extension), false);
                openWith?.DeleteValue(LegacyProgId, false);
            }
            user.DeleteSubKeyTree($@"{Classes}\{ProgId(extension)}", false);
        }
        using (RegistryKey? apps = user.OpenSubKey(@"Software\RegisteredApplications", true))
            apps?.DeleteValue(AppName, false);
        user.DeleteSubKeyTree(@"Software\ZIP", false);
        user.DeleteSubKeyTree($@"{Classes}\Applications\ZIP.exe", false);
        user.DeleteSubKeyTree($@"{Classes}\{LegacyProgId}", false);
        Native.NotifyAssociationsChanged();
    }

    /// <summary>Opens ZIP's page in Settings → Default apps (Windows 11), or the general page elsewhere.</summary>
    public static void OpenDefaultAppsSettings()
    {
        string target = Native.IsPackaged
            ? "ms-settings:defaultapps"
            : $"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(AppName)}";
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true })?.Dispose();
        }
    }

    /// <summary>Registers if needed, then sends the user to confirm ZIP as the default.</summary>
    public static void MakeDefault(IWin32Window? owner)
    {
        try
        {
            if (!IsRegistered)
                Register();
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            JobDialog.ShowError(owner, new ArchiveException("无法写入文件关联：" + error.Message));
            return;
        }
        MessageBox.Show(owner,
            "Windows 需要你亲自确认默认应用。\n\n接下来会打开「设置 → 默认应用 → ZIP」，" +
            "点击页面顶部的「设置默认值」，或逐个把 .zip、.7z、.rar 等改为 ZIP。",
            "设为默认压缩软件", MessageBoxButtons.OK, MessageBoxIcon.Information);
        OpenDefaultAppsSettings();
    }
}
