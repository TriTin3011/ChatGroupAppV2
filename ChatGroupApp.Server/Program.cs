using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

const int DefaultPort = 5000;

var port = ReadPort(args);
var clients = new ConcurrentDictionary<TcpClient, ClientState>();
var listener = new TcpListener(IPAddress.Any, port);
var fileListener = new TcpListener(IPAddress.Any, port + 1);

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "ChatGroupApp Server";

listener.Start();
fileListener.Start();

Console.WriteLine("=== ChatGroupApp Server ===");
Console.WriteLine($"Server running at port: {port}");
Console.WriteLine($"File transfer running at port: {port + 1}");
Console.WriteLine("IP server:");
foreach (var ip in GetLocalIPv4Addresses())
{
    Console.WriteLine($" - {ip}");
}

Console.WriteLine();
Console.WriteLine("Clients can connect with one of the IP addresses above.");
Console.WriteLine("Press Ctrl+C to stop the server.");
Console.WriteLine();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    listener.Stop();
    fileListener.Stop();
    foreach (var client in clients.Keys)
    {
        client.Close();
    }
};

try
{
    _ = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                var fileClient = await fileListener.AcceptTcpClientAsync();
                _ = HandleFileTransferAsync(fileClient);
            }
        }
        catch (Exception) { }
    });

    while (true)
    {
        var tcpClient = await listener.AcceptTcpClientAsync();
        _ = HandleClientAsync(tcpClient);
    }
}
catch (SocketException)
{
    Console.WriteLine("Server stopped.");
}
catch (ObjectDisposedException)
{
    Console.WriteLine("Server stopped.");
}

async Task HandleClientAsync(TcpClient tcpClient)
{
    var endpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
    Console.WriteLine($"Client connected: {endpoint}");

    try
    {
        await using var stream = tcpClient.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        var requestedName = await reader.ReadLineAsync();
        var name = SanitizeName(requestedName, endpoint);
        var state = new ClientState(name, writer);
        clients[tcpClient] = state;

        await SendToClientAsync(state, $"SERVER|Welcome {name}! Connected to ChatGroupApp.");
        await BroadcastAsync($"SERVER|{name} joined the chat.", tcpClient);
        Console.WriteLine($"{name} joined.");

        string? message;
        while ((message = await reader.ReadLineAsync()) is not null)
        {
            message = message.Trim();
            if (message.Length == 0)
            {
                continue;
            }

            Console.WriteLine($"{name}: {message}");
            await BroadcastAsync($"CHAT|{name}|{message}", tcpClient);
            await SendToClientAsync(state, $"ME|{message}");
        }
    }
    catch (IOException)
    {
    }
    catch (SocketException)
    {
    }
    finally
    {
        if (clients.TryRemove(tcpClient, out var state))
        {
            Console.WriteLine($"{state.Name} left.");
            await BroadcastAsync($"SERVER|{state.Name} left the chat.", tcpClient);
        }

        tcpClient.Close();
    }
}

async Task BroadcastAsync(string line, TcpClient? exceptClient = null)
{
    var disconnectedClients = new List<TcpClient>();

    foreach (var (client, state) in clients)
    {
        if (ReferenceEquals(client, exceptClient))
        {
            continue;
        }

        try
        {
            await SendToClientAsync(state, line);
        }
        catch (IOException)
        {
            disconnectedClients.Add(client);
        }
        catch (ObjectDisposedException)
        {
            disconnectedClients.Add(client);
        }
    }

    foreach (var client in disconnectedClients)
    {
        clients.TryRemove(client, out _);
        client.Close();
    }
}

static async Task SendToClientAsync(ClientState state, string line)
{
    await state.SendLock.WaitAsync();
    try
    {
        await state.Writer.WriteLineAsync(line);
    }
    finally
    {
        state.SendLock.Release();
    }
}

static int ReadPort(string[] args)
{
    if (args.Length > 0 && int.TryParse(args[0], out var argPort) && IsValidPort(argPort))
    {
        return argPort;
    }

    Console.Write($"Port server [{DefaultPort}]: ");
    var input = Console.ReadLine();
    if (int.TryParse(input, out var typedPort) && IsValidPort(typedPort))
    {
        return typedPort;
    }

    return DefaultPort;
}

static bool IsValidPort(int port)
{
    return port is > 0 and <= 65535;
}

static string SanitizeName(string? name, string fallback)
{
    name = name?.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
        return $"Guest-{fallback.Replace(':', '-')}";
    }

    return name.Length <= 24 ? name : name[..24];
}

static IEnumerable<IPAddress> GetLocalIPv4Addresses()
{
    return NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
        .Select(address => address.Address)
        .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
        .DefaultIfEmpty(IPAddress.Loopback);
}

static async Task<string> ReadLineStrictAsync(Stream stream)
{
    var bytes = new List<byte>();
    var buffer = new byte[1];
    while (await stream.ReadAsync(buffer) > 0)
    {
        if (buffer[0] == '\n') break;
        if (buffer[0] != '\r') bytes.Add(buffer[0]);
    }
    return Encoding.UTF8.GetString(bytes.ToArray());
}

async Task HandleFileTransferAsync(TcpClient tcpClient)
{
    try
    {
        await using var stream = tcpClient.GetStream();
        var header = await ReadLineStrictAsync(stream);
        if (string.IsNullOrEmpty(header)) return;

        var parts = header.Split('|');
        if (parts[0] == "UPLOAD" && parts.Length >= 6)
        {
            var senderName = parts[1];
            var fileId = parts[2];
            var fileName = parts[3];
            var sizeStr = parts[4];
            var isImage = parts[5];

            if (!long.TryParse(sizeStr, out var size)) return;

            Directory.CreateDirectory("uploads");
            var filePath = Path.Combine("uploads", fileId);

            await using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while (totalRead < size && (read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, size - totalRead))) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read);
                    totalRead += read;
                }
            }

            Console.WriteLine($"File received: {fileName} ({size} bytes) from {senderName}");

            await BroadcastAsync($"FILE|{senderName}|{fileName}|{fileId}|{size}|{isImage}");

            var response = Encoding.UTF8.GetBytes("OK\n");
            await stream.WriteAsync(response);
        }
        else if (parts[0] == "DOWNLOAD" && parts.Length >= 2)
        {
            var fileId = parts[1];
            var filePath = Path.Combine("uploads", fileId);

            if (File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                var headerBytes = Encoding.UTF8.GetBytes($"OK|{fileInfo.Length}\n");
                await stream.WriteAsync(headerBytes);

                await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await fileStream.CopyToAsync(stream);
            }
            else
            {
                var headerBytes = Encoding.UTF8.GetBytes("NOTFOUND\n");
                await stream.WriteAsync(headerBytes);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"File transfer error: {ex.Message}");
    }
    finally
    {
        tcpClient.Close();
    }
}

sealed record ClientState(string Name, StreamWriter Writer)
{
    public SemaphoreSlim SendLock { get; } = new(1, 1);
}