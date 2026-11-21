using System.Runtime.InteropServices;

namespace QuickZip;

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HeaderItem
    {
        public uint Mask;
        public int Width;
        public nint Text;
        public nint Bitmap;
        public int TextMax;
        public int Format;
        public nint Param;
        public int Image;
        public int Order;
        public uint Type;
        public nint Filter;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ListItemState
    {
        public uint Mask;
        public int Item;
        public int SubItem;
        public uint State;
        public uint StateMask;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(nint window, string? subApp, string? idList);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, ref HeaderItem item);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, ref ListItemState state);

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref int length, nint name);

    public static bool IsPackaged
    {
        get
        {
            int length = 0;
            // APPMODEL_ERROR_NO_PACKAGE
            return GetCurrentPackageFullName(ref length, 0) != 15700;
        }
    }

    /// <summary>Reads the shell icon and type description for a name without touching the disk.</summary>
    public static (Icon? Icon, string TypeName) ShellInfo(string name, bool directory)
    {
        const uint useAttributes = 0x10, icon = 0x100, small = 0x1, typeName = 0x400;
        var info = new ShellFileInfo();
        nint ok = SHGetFileInfo(name, directory ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(),
            useAttributes | icon | small | typeName);
        if (ok == 0)
            return (null, "");
        Icon? result = null;
        if (info.Icon != 0)
        {
            using var handle = Icon.FromHandle(info.Icon);
            result = (Icon)handle.Clone();
            DestroyIcon(info.Icon);
        }
        return (result, info.TypeName ?? "");
    }

    public static void NotifyAssociationsChanged() => SHChangeNotify(0x08000000, 0, 0, 0);

    public static void UseExplorerTheme(Control control) => SetWindowTheme(control.Handle, "Explorer", null);

    /// <summary>Paints the Windows 11 caption in the window's own surface color. Ignored by older systems.</summary>
    public static void TintCaption(Form form, Color caption, Color text)
    {
        int background = ColorTranslator.ToWin32(caption);
        int foreground = ColorTranslator.ToWin32(text);
        DwmSetWindowAttribute(form.Handle, 35, ref background, sizeof(int));
        DwmSetWindowAttribute(form.Handle, 36, ref foreground, sizeof(int));
    }

    public static void SetSortArrow(ListView list, int column, bool descending)
    {
        const uint getHeader = 0x101F, getItem = 0x120B, setItem = 0x120C, formatMask = 0x4;
        const int sortUp = 0x400, sortDown = 0x200;
        nint header = SendMessage(list.Handle, getHeader, 0, 0);
        if (header == 0)
            return;
        for (int index = 0; index < list.Columns.Count; index++)
        {
            var item = new HeaderItem { Mask = formatMask };
            SendMessage(header, getItem, index, ref item);
            item.Format &= ~(sortUp | sortDown);
            if (index == column)
                item.Format |= descending ? sortDown : sortUp;
            SendMessage(header, setItem, index, ref item);
        }
    }

    public static void SelectAll(ListView list)
    {
        const uint setItemState = 0x102B, selected = 0x0002;
        var state = new ListItemState { State = selected, StateMask = selected };
        SendMessage(list.Handle, setItemState, -1, ref state);
    }
}

/// <summary>Caches shell icons and type names by extension so large archives stay cheap to draw.</summary>
internal sealed class ShellIcons(ImageList images)
{
    private readonly Dictionary<string, (int Index, string Type)> cache = new(StringComparer.OrdinalIgnoreCase);

    public (int Index, string Type) Folder => Lookup("", directory: true);

    public (int Index, string Type) File(string name)
    {
        string extension = Path.GetExtension(name);
        return Lookup(extension.Length > 1 ? extension : ".", directory: false);
    }

    private (int Index, string Type) Lookup(string extension, bool directory)
    {
        string key = directory ? "\\folder" : extension;
        if (cache.TryGetValue(key, out var known))
            return known;
        var (icon, type) = Native.ShellInfo(directory ? "folder" : "file" + extension, directory);
        if (string.IsNullOrWhiteSpace(type))
            type = directory ? "文件夹" : extension.Length > 1 ? extension[1..].ToUpperInvariant() + " 文件" : "文件";
        using Image picture = icon is null
            ? BrowserAppearance.EntryIcon(directory, images.ImageSize.Width)
            : icon.ToBitmap();
        icon?.Dispose();
        images.Images.Add(Pad(picture, images.ImageSize));
        return cache[key] = (images.Images.Count - 1, type);
    }

    // Rows in a details list are as tall as the image list, so padding the icon gives the list room to breathe.
    private static Bitmap Pad(Image icon, Size cell)
    {
        var bitmap = new Bitmap(cell.Width, cell.Height);
        using Graphics canvas = Graphics.FromImage(bitmap);
        canvas.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        int side = Math.Min(cell.Width, cell.Height);
        canvas.DrawImage(icon, new Rectangle((cell.Width - side) / 2, (cell.Height - side) / 2, side, side));
        return bitmap;
    }
}
