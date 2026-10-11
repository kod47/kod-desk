using System.Security.Cryptography;
using System.Text;
using KodDesk.Protocol;
using KodDesk.Relay;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<HostRegistry>();
builder.Services.AddSingleton<SessionBroker>();
builder.Services.AddSingleton(sp => new LimitTracker(RelayLimits.FromConfiguration(sp.GetRequiredService<IConfiguration>())));
var app = builder.Build();
var limits = app.Services.GetRequiredService<LimitTracker>();
app.Logger.LogInformation("Limite: {Limits}", limits.Limits);

var relayKey = Encoding.UTF8.GetBytes(app.Configuration["RELAY_KEY"] ?? "");
if (relayKey.Length == 0)
    app.Logger.LogWarning("RELAY_KEY nu este setat: oricine cunoaște adresa poate folosi acest relay.");

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
    KeepAliveTimeout = TimeSpan.FromSeconds(45),
});

bool Authorized(HttpContext ctx)
{
    if (relayKey.Length == 0) return true;
    var provided = Encoding.UTF8.GetBytes(ctx.Request.Query["k"].ToString());
    return CryptographicOperations.FixedTimeEquals(provided, relayKey);
}

bool Precheck(HttpContext ctx)
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return false; }
    if (!limits.AllowConnect(LimitTracker.ClientIp(ctx)))
    {
        app.Logger.LogWarning("Prea multe conexiuni de la {Ip}", LimitTracker.ClientIp(ctx));
        ctx.Response.StatusCode = 429;
        return false;
    }
    if (!Authorized(ctx)) { ctx.Response.StatusCode = 401; return false; }
    return true;
}

app.MapGet("/", () => Results.Text("KodDesk relay OK\n"));

// Canalul de control al unui host.
app.Map("/ws/host", async (HttpContext ctx, HostRegistry registry, ILogger<Program> log) =>
{
    if (!Precheck(ctx)) return;
    var ip = LimitTracker.ClientIp(ctx);
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var ct = ctx.RequestAborted;

    using var hostSlot = limits.TryAcquire("host:" + ip, limits.Limits.HostsPerIp);
    if (hostSlot == null)
    {
        await TrySendAsync(ws, new RelayMessage { Type = RelayMessage.Error, Message = "Prea multe calculatoare online de la această adresă." }, ct);
        log.LogWarning("Prea multe host-uri de la {Ip}", ip);
        return;
    }

    RelayMessage? reg;
    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
    {
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { reg = await WebSocketText.ReceiveAsync(ws, timeout.Token); }
        catch { return; }
    }

    var id = SecureHandshake.NormalizeId(reg?.Id ?? "");
    if (reg?.Type != RelayMessage.Register || id.Length is < 6 or > 12 || string.IsNullOrEmpty(reg.Secret))
    {
        await TrySendAsync(ws, new RelayMessage { Type = RelayMessage.Error, Message = "Înregistrare invalidă" }, ct);
        return;
    }
    var claim = registry.TryClaim(id, reg.Secret, count =>
        (limits.Limits.MaxRegisteredIds == 0 || count < limits.Limits.MaxRegisteredIds) && limits.AllowNewId(ip));
    if (claim != HostRegistry.ClaimResult.Ok)
    {
        var message = claim == HostRegistry.ClaimResult.WrongSecret
            ? "ID-ul este folosit de alt calculator"
            : "Relay-ul nu mai acceptă calculatoare noi momentan.";
        await TrySendAsync(ws, new RelayMessage { Type = RelayMessage.Error, Message = message }, ct);
        log.LogWarning("Înregistrare respinsă pentru {Id} ({Reason}) de la {Ip}", id, claim, ip);
        return;
    }

    var conn = new HostConnection(id, ws);
    registry.SetOnline(conn);
    log.LogInformation("Host online: {Id} ({Ip})", id, ip);
    try
    {
        await conn.SendAsync(new RelayMessage { Type = RelayMessage.Registered, Id = id }, ct);
        // Host-ul nu trimite altceva; așteptăm închiderea conexiunii.
        while (await WebSocketText.ReceiveAsync(ws, ct) != null) { }
    }
    catch { }
    finally
    {
        if (registry.SetOffline(conn))
            log.LogInformation("Host offline: {Id}", id);
    }
});

// Viewer-ul cere conexiune la un host.
app.Map("/ws/connect/{id}", async (HttpContext ctx, string id, HostRegistry registry, SessionBroker broker, ILogger<Program> log) =>
{
    if (!Precheck(ctx)) return;
    var ip = LimitTracker.ClientIp(ctx);
    using var viewer = await ctx.WebSockets.AcceptWebSocketAsync();
    var ct = ctx.RequestAborted;
    id = SecureHandshake.NormalizeId(id);

    using var ipSlot = limits.TryAcquire("session:" + ip, limits.Limits.SessionsPerIp);
    using var totalSlot = ipSlot == null ? null : limits.TryAcquire("sessions", limits.Limits.SessionsTotal);
    if (ipSlot == null || totalSlot == null)
    {
        await TrySendAsync(viewer, new RelayMessage { Type = RelayMessage.Error, Message = "Relay-ul este ocupat. Încercați mai târziu." }, ct);
        log.LogWarning("Sesiune refuzată pentru {Ip}: limita de sesiuni atinsă", ip);
        return;
    }

    if (!registry.TryGetOnline(id, out var host))
    {
        await TrySendAsync(viewer, new RelayMessage { Type = RelayMessage.Error, Message = "Calculatorul cu acest ID nu este online." }, ct);
        return;
    }

    var (token, pending) = broker.Create();
    try
    {
        await host.SendAsync(new RelayMessage { Type = RelayMessage.Incoming, Session = token }, ct);

        System.Net.WebSockets.WebSocket hostWs;
        try
        {
            hostWs = await pending.HostSocket.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
        catch (TimeoutException)
        {
            await TrySendAsync(viewer, new RelayMessage { Type = RelayMessage.Error, Message = "Calculatorul la distanță nu a răspuns." }, ct);
            return;
        }

        await WebSocketText.SendAsync(viewer, new RelayMessage { Type = RelayMessage.Connected }, ct);
        log.LogInformation("Sesiune {Token} pornită: {Ip} -> {Id}", token[..8], ip, id);
        var started = DateTime.UtcNow;
        var (toHost, toViewer) = await Pipe.RunAsync(viewer, hostWs, limits.Limits.MaxSessionDuration,
            limits.Limits.SessionBytesPerSecond, ct);
        log.LogInformation("Sesiune {Token} închisă după {Min:F1} min; viewer->host {Up:F1} MB, host->viewer {Down:F1} MB",
            token[..8], (DateTime.UtcNow - started).TotalMinutes, toHost / 1e6, toViewer / 1e6);
    }
    catch (Exception ex) when (ex is OperationCanceledException or System.Net.WebSockets.WebSocketException)
    {
    }
    finally
    {
        broker.Remove(token);
        pending.Done.TrySetResult();
    }
});

// Host-ul deschide canalul de date pentru o sesiune anunțată.
app.Map("/ws/session/{token}", async (HttpContext ctx, string token, SessionBroker broker) =>
{
    if (!Precheck(ctx)) return;
    if (!broker.TryGet(token, out var pending) || pending.HostSocket.Task.IsCompleted)
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    if (!pending.HostSocket.TrySetResult(ws)) return;
    try { await pending.Done.Task.WaitAsync(ctx.RequestAborted); }
    catch (OperationCanceledException) { }
});

app.Run();

static async Task TrySendAsync(System.Net.WebSockets.WebSocket ws, RelayMessage msg, CancellationToken ct)
{
    try
    {
        await WebSocketText.SendAsync(ws, msg, ct);
        await ws.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, ct);
    }
    catch { }
}
