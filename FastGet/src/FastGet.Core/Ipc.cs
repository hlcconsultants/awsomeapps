using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FastGet.Core;

/// <summary>Message from the browser (via the native host) or a second app instance.</summary>
public sealed class IpcMessage
{
    public string Cmd { get; set; } = "";
    public string? Url { get; set; }
    public string? FileName { get; set; }
    public string? Referrer { get; set; }
    public string? Cookie { get; set; }
    public string? UserAgent { get; set; }
    public string? Kind { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
}

public sealed class IpcReply
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Version { get; set; }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed class IpcServer(Func<IpcMessage, Task<IpcReply>> handler) : IDisposable
{
    public static string PipeName => "FastGet.Ipc." + Environment.UserName;

    readonly CancellationTokenSource _cts = new();

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // CurrentUserOnly: other accounts on this PC can't queue downloads.
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected));
            }
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch (IOException) { pipe?.Dispose(); await Task.Delay(200); }
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line == null) return;

                IpcReply reply;
                try
                {
                    var msg = JsonSerializer.Deserialize<IpcMessage>(line, IpcJson.Options) ?? new IpcMessage();
                    reply = await handler(msg);
                }
                catch (Exception ex) { reply = new IpcReply { Ok = false, Error = ex.Message }; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, IpcJson.Options));
            }
            catch (IOException) { /* client went away */ }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
}

public static class IpcClient
{
    public static async Task<IpcReply> SendAsync(IpcMessage msg, TimeSpan connectTimeout, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(".", IpcServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(msg, IpcJson.Options));
        var line = await reader.ReadLineAsync(ct);
        return line == null ? new IpcReply { Ok = false, Error = "No reply" } : JsonSerializer.Deserialize<IpcReply>(line, IpcJson.Options) ?? new IpcReply();
    }
}
