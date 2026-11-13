using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace TinyHand;

/// <summary>
/// Single-instance guard for the Windows port. The first process owns a named mutex and a
/// named-pipe server; a second launch (a <c>tinyhand://</c> link, or the user starting the app
/// again) forwards its command line to the running instance and exits — the macOS app gets the
/// same behaviour from LaunchServices handing the URL to the running app.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\TinyHand.SingleInstance";
    private const string PipeName = "TinyHand.Command";

    private Mutex? _mutex;
    private CancellationTokenSource? _cancel;
    private Task? _server;

    /// <summary>Commands forwarded by later launches (pipe thread; marshal to the UI thread).</summary>
    public event Action<string>? CommandReceived;

    /// <summary>True when this process is the one that owns the mutex and the pipe server.</summary>
    public bool IsFirstInstance { get; private set; }

    /// <summary>Try to become the single instance. False means another TinyHand is running.</summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsFirstInstance = createdNew;
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _cancel = new CancellationTokenSource();
        _server = Task.Run(() => ServerLoop(_cancel.Token));
        return true;
    }

    /// <summary>Forward one command line to the running instance. Returns false when no
    /// instance answers (the caller then starts normally).</summary>
    public static bool Send(string command, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"single-instance send failed: {ex.Message}");
            return false;
        }
    }

    private async Task ServerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                while (!token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(token);
                    if (line is null) break;
                    if (line.Length == 0) continue;
                    CommandReceived?.Invoke(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"single-instance server failed: {ex.Message}");
                try { await Task.Delay(100, token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        _cancel?.Cancel();
        try { _server?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cancel?.Dispose();
        _cancel = null;
        _server = null;

        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
