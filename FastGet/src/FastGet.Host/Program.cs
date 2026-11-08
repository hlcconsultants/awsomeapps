using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using FastGet.Core;

// Chrome native-messaging bridge: 4-byte little-endian length + UTF-8 JSON on stdio, forwarded to the app's pipe.
const int MaxMessage = 8 * 1024 * 1024;

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

while (true)
{
    var message = Read(stdin);
    if (message == null) return;

    IpcReply reply;
    try
    {
        var msg = JsonSerializer.Deserialize<IpcMessage>(message, IpcJson.Options) ?? new IpcMessage();
        reply = await ForwardAsync(msg);
    }
    catch (Exception ex)
    {
        reply = new IpcReply { Ok = false, Error = ex.Message };
    }
    Write(stdout, JsonSerializer.SerializeToUtf8Bytes(reply, IpcJson.Options));
}

static byte[]? Read(Stream s)
{
    Span<byte> header = stackalloc byte[4];
    try { s.ReadExactly(header); }
    catch (EndOfStreamException) { return null; }

    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length <= 0 || length > MaxMessage) return null;
    var body = new byte[length];
    try { s.ReadExactly(body); }
    catch (EndOfStreamException) { return null; }
    return body;
}

static void Write(Stream s, byte[] body)
{
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
    s.Write(header);
    s.Write(body);
    s.Flush();
}

static async Task<IpcReply> ForwardAsync(IpcMessage msg)
{
    try
    {
        return await IpcClient.SendAsync(msg, TimeSpan.FromMilliseconds(700));
    }
    catch (TimeoutException)
    {
        if (msg.Cmd == "ping") return new IpcReply { Ok = true, Version = "not-running" }; // installed, starts on demand
    }

    var exe = Path.Combine(AppContext.BaseDirectory, "FastGet.exe");
    if (!File.Exists(exe)) return new IpcReply { Ok = false, Error = "FastGet.exe not found" };
    Process.Start(new ProcessStartInfo(exe, "--minimized") { UseShellExecute = true });

    for (int i = 0; i < 15; i++)
    {
        try { return await IpcClient.SendAsync(msg, TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { }
    }
    return new IpcReply { Ok = false, Error = "FastGet did not start" };
}
