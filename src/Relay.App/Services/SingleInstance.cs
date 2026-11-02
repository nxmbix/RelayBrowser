using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Relay.App.Services;

/// <summary>
/// Keeps one RELAY process per data folder. A second launch hands its arguments to the
/// running instance over a per-user named pipe (opens a new window) and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public SingleInstance(string dataRoot)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot.ToLowerInvariant() + Environment.UserName)))[..16];
        _pipeName = "relay-" + id;
        _mutex = new Mutex(true, @"Local\RELAY-" + id, out var created);
        IsFirst = created;
    }

    public bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            var payload = Encoding.UTF8.GetBytes(string.Join("\n", args.Prepend("open")));
            client.Write(payload);
            return true;
        }
        catch { return false; }
    }

    public void Listen(Action<string[]> onArgs)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var ms = new MemoryStream();
                    var buffer = new byte[4096];
                    int n;
                    while ((n = await server.ReadAsync(buffer, _cts.Token)) > 0 && ms.Length < 64 * 1024) ms.Write(buffer, 0, n);
                    var parts = Encoding.UTF8.GetString(ms.ToArray()).Split('\n');
                    if (parts.Length > 0 && parts[0] == "open") onArgs(parts[1..]);
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(250); }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
    }
}
