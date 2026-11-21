using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickZip;

/// <summary>
/// Routes "open this archive" requests to the ZIP process that is already running, so archives open
/// as windows of one process and an archive that is already open is brought to the front instead.
/// Each install location gets its own channel, so a portable copy and the MSIX never talk to each other.
/// </summary>
internal static partial class SingleInstance
{
    private static readonly string Key = BuildKey();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    private static string BuildKey()
    {
        string identity = (Environment.ProcessPath ?? Application.ExecutablePath).ToUpperInvariant() + "|" +
            Environment.UserDomainName + "\\" + Environment.UserName + "|" +
            System.Diagnostics.Process.GetCurrentProcess().SessionId;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "ZIP-" + Convert.ToHexString(hash, 0, 12);
    }

    /// <summary>Claims the primary role. Dispose the returned mutex when the process ends.</summary>
    public static Mutex Claim(out bool primary) => new(true, @"Local\" + Key, out primary);

    /// <summary>Hands the paths to the primary process. Returns false if nobody answered.</summary>
    public static bool TrySend(IReadOnlyList<string> paths)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", Key, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                client.Connect(300);
                if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint server))
                    AllowSetForegroundWindow(server);
                byte[] message = JsonSerializer.SerializeToUtf8Bytes(paths);
                client.Write(BitConverter.GetBytes(message.Length));
                client.Write(message);
                client.Flush();
                return client.ReadByte() == 1;
            }
            catch (TimeoutException) { }
            catch (IOException) { }
            // The primary may still be starting its listener.
            Thread.Sleep(150);
        }
        return false;
    }

    /// <summary>Listens for requests from later launches and hands them to <paramref name="open"/> on the UI thread.</summary>
    public static void Listen(SynchronizationContext ui, Action<string[]> open, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Key, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    byte[] header = new byte[4];
                    await server.ReadExactlyAsync(header, token);
                    int length = BitConverter.ToInt32(header);
                    if (length is < 0 or > 1 << 20)
                        continue;
                    byte[] body = new byte[length];
                    await server.ReadExactlyAsync(body, token);
                    string[] paths = JsonSerializer.Deserialize<string[]>(body) ?? [];
                    ui.Post(_ => open(paths), null);
                    server.WriteByte(1);
                    await server.FlushAsync(token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception error) when (error is IOException or JsonException or EndOfStreamException) { }
            }
        }, token);
    }
}

/// <summary>Keeps the process alive while any archive window is open.</summary>
internal sealed class WindowManager : ApplicationContext
{
    private readonly List<MainForm> windows = [];

    public void Open(string? path)
    {
        if (path is not null)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                JobDialog.ShowError(windows.LastOrDefault(), error);
                return;
            }
            if (windows.FirstOrDefault(window => window.Shows(full)) is { } showing)
            {
                Bring(showing);
                return;
            }
            if (windows.FirstOrDefault(window => window.IsIdle) is { } idle)
            {
                Bring(idle);
                idle.OpenPath(full);
                return;
            }
            path = full;
        }
        else if (windows.FirstOrDefault(window => window.IsIdle) is { } idle)
        {
            Bring(idle);
            return;
        }

        var form = new MainForm(path);
        windows.Add(form);
        form.FormClosed += (_, _) =>
        {
            windows.Remove(form);
            if (windows.Count == 0)
                ExitThread();
        };
        if (windows.Count > 1 && windows[^2] is { WindowState: FormWindowState.Normal } previous)
        {
            // Cascade new windows so they do not hide each other exactly.
            form.StartPosition = FormStartPosition.Manual;
            form.Location = previous.Location + new Size(28, 28);
        }
        form.Show();
        form.Activate();
    }

    private static void Bring(Form form)
    {
        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;
        form.Activate();
        form.BringToFront();
    }
}
