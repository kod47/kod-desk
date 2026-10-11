using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KodDesk.Core.Native;

namespace KodDesk.Core.Service;

/// <summary>
/// Canal local (named pipe) către serviciu. Un mesaj JSON pe linie, un răspuns, apoi conexiunea se închide.
/// Comenzi: status, disconnect (de la aplicație); agent-auth, agent-authfail, sas (de la agenți).
/// </summary>
public static class ControlPipe
{
    public const string Name = "KodDesk.Control";

    public const string CmdStatus = "status";
    public const string CmdDisconnect = "disconnect";
    public const string CmdAgentAuth = "agent-auth";
    public const string CmdAgentAuthFail = "agent-authfail";
    public const string CmdSas = "sas";

    public static async Task<ControlResponse?> SendAsync(string cmd, int timeoutMs = 2000)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await using var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cts.Token);
            var req = JsonSerializer.Serialize(new ControlRequest { Cmd = cmd }, ControlJsonContext.Default.ControlRequest);
            var bytes = Encoding.UTF8.GetBytes(req + "\n");
            await client.WriteAsync(bytes, cts.Token);
            await client.FlushAsync(cts.Token);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(cts.Token);
            return line == null ? null : JsonSerializer.Deserialize(line, ControlJsonContext.Default.ControlResponse);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

public sealed class ControlRequest
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
}

public sealed class ControlResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("hostId")] public string? HostId { get; set; }
    [JsonPropertyName("hasPassword")] public bool HasPassword { get; set; }
    [JsonPropertyName("relay")] public string? Relay { get; set; }
    [JsonPropertyName("active")] public int Active { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
public partial class ControlJsonContext : JsonSerializerContext;

/// <summary>Serverul canalului de control (rulează în serviciu).</summary>
public sealed class ControlPipeServer(Func<ControlRequest, int, ControlResponse> handler) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    private static PipeSecurity CreateSecurity()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // Aplicația utilizatorului poate cere starea și deconectarea.
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return ps;
    }

    private async Task AcceptLoopAsync()
    {
        var security = CreateSecurity();
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(ControlPipe.Name, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 4096, 4096, security);
                await server.WaitForConnectionAsync(_cts.Token);
                var connected = server;
                server = null;
                _ = Task.Run(() => HandleAsync(connected));
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                Log.Warn("Eroare la canalul de control", ex);
                await Task.Delay(1000);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(5000);
                Win32.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line == null) return;
                var req = JsonSerializer.Deserialize(line, ControlJsonContext.Default.ControlRequest) ?? new();
                ControlResponse resp;
                try { resp = handler(req, (int)pid); }
                catch (Exception ex) { resp = new ControlResponse { Ok = false, Error = ex.Message }; }
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resp, ControlJsonContext.Default.ControlResponse) + "\n");
                await pipe.WriteAsync(bytes, timeout.Token);
                await pipe.FlushAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
        }
    }

    public void Dispose() => _cts.Cancel();
}
