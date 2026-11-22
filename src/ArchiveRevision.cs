using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace QuickZip;

internal readonly record struct ArchiveRevision(uint Volume, ulong FileId, ulong Length, ulong Created, ulong Modified)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint Volume;
        public uint SizeHigh;
        public uint SizeLow;
        public uint LinkCount;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    public static ArchiveRevision Read(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(handle);
    }

    public static ArchiveRevision Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("无法读取压缩包的文件信息。", new Win32Exception(Marshal.GetLastPInvokeError()));
        return new(info.Volume, Combine(info.FileIndexHigh, info.FileIndexLow), Combine(info.SizeHigh, info.SizeLow),
            Combine((uint)info.CreationTime.dwHighDateTime, (uint)info.CreationTime.dwLowDateTime),
            Combine((uint)info.LastWriteTime.dwHighDateTime, (uint)info.LastWriteTime.dwLowDateTime));
    }

    public void RequireCurrent(string path)
    {
        try { RequireMatch(Read(path)); }
        catch (FileNotFoundException) { throw Changed(); }
        catch (DirectoryNotFoundException) { throw Changed(); }
    }

    public void RequireMatch(ArchiveRevision current)
    {
        if (current != this)
            throw Changed();
    }

    private static ulong Combine(uint high, uint low) => ((ulong)high << 32) | low;
    private static ArchiveException Changed() => new("压缩包已被其他程序修改或移走，请重新打开后再试。");
}
