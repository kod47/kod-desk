using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace KodDesk.Relay;

/// <summary>
/// Limitele relay-ului, configurabile din variabile de mediu (0 = fără limită).
/// Protejează conexiunea serverului dacă cineva folosește relay-ul abuziv.
/// </summary>
public sealed class RelayLimits
{
    /// <summary>Conexiuni noi pe minut de la același IP (toate tipurile).</summary>
    public int ConnectsPerMinutePerIp { get; init; } = 60;

    /// <summary>Calculatoare online simultan de la același IP.</summary>
    public int HostsPerIp { get; init; } = 10;

    /// <summary>ID-uri noi înregistrate pe zi de la același IP.</summary>
    public int NewIdsPerIpPerDay { get; init; } = 10;

    /// <summary>Numărul maxim de ID-uri păstrate de relay.</summary>
    public int MaxRegisteredIds { get; init; } = 5000;

    /// <summary>Sesiuni simultane deschise de la același IP (viewer).</summary>
    public int SessionsPerIp { get; init; } = 4;

    /// <summary>Sesiuni simultane pe tot relay-ul.</summary>
    public int SessionsTotal { get; init; } = 20;

    /// <summary>Durata maximă a unei sesiuni.</summary>
    public TimeSpan MaxSessionDuration { get; init; } = TimeSpan.FromHours(12);

    /// <summary>Viteza maximă pe sesiune, pe fiecare sens (octeți/secundă).</summary>
    public long SessionBytesPerSecond { get; init; } = 60_000_000 / 8;

    public static RelayLimits FromConfiguration(IConfiguration c)
    {
        int I(string key, int def) => int.TryParse(c[key], out var v) && v >= 0 ? v : def;
        var defaults = new RelayLimits();
        return new RelayLimits
        {
            ConnectsPerMinutePerIp = I("LIMIT_CONNECTS_PER_MINUTE", defaults.ConnectsPerMinutePerIp),
            HostsPerIp = I("LIMIT_HOSTS_PER_IP", defaults.HostsPerIp),
            NewIdsPerIpPerDay = I("LIMIT_NEW_IDS_PER_DAY", defaults.NewIdsPerIpPerDay),
            MaxRegisteredIds = I("LIMIT_MAX_IDS", defaults.MaxRegisteredIds),
            SessionsPerIp = I("LIMIT_SESSIONS_PER_IP", defaults.SessionsPerIp),
            SessionsTotal = I("LIMIT_SESSIONS_TOTAL", defaults.SessionsTotal),
            MaxSessionDuration = TimeSpan.FromHours(I("LIMIT_SESSION_HOURS", (int)defaults.MaxSessionDuration.TotalHours)),
            SessionBytesPerSecond = I("LIMIT_SESSION_MBPS", (int)(defaults.SessionBytesPerSecond * 8 / 1_000_000)) * 1_000_000L / 8,
        };
    }

    public override string ToString() =>
        $"conexiuni/min/IP={ConnectsPerMinutePerIp}, host-uri/IP={HostsPerIp}, ID-uri noi/zi/IP={NewIdsPerIpPerDay}, " +
        $"ID-uri max={MaxRegisteredIds}, sesiuni/IP={SessionsPerIp}, sesiuni total={SessionsTotal}, " +
        $"durată sesiune={MaxSessionDuration.TotalHours:F0} h, viteză/sesiune={SessionBytesPerSecond * 8 / 1_000_000} Mbps";
}

/// <summary>Contoarele pentru limite (ferestre glisante și resurse ocupate).</summary>
public sealed class LimitTracker
{
    private readonly RelayLimits _limits;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _events = new();
    private readonly ConcurrentDictionary<string, int> _inUse = new();
    private readonly Timer _cleanup;

    public LimitTracker(RelayLimits limits)
    {
        _limits = limits;
        _cleanup = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    public RelayLimits Limits => _limits;

    public bool AllowConnect(string ip) => AllowEvent("connect:" + ip, _limits.ConnectsPerMinutePerIp, TimeSpan.FromMinutes(1));

    public bool AllowNewId(string ip) => AllowEvent("newid:" + ip, _limits.NewIdsPerIpPerDay, TimeSpan.FromDays(1));

    private bool AllowEvent(string key, int max, TimeSpan window)
    {
        if (max == 0) return true;
        var q = _events.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            var now = DateTime.UtcNow;
            while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
            if (q.Count >= max) return false;
            q.Enqueue(now);
            return true;
        }
    }

    /// <summary>Ocupă o resursă limitată (ex. o sesiune). Returnează null dacă limita e atinsă; altfel un obiect de eliberat.</summary>
    public IDisposable? TryAcquire(string key, int max)
    {
        if (max == 0) return new Lease(this, null);
        while (true)
        {
            int current = _inUse.GetOrAdd(key, 0);
            if (current >= max) return null;
            if (_inUse.TryUpdate(key, current + 1, current)) return new Lease(this, key);
        }
    }

    private void Release(string key)
    {
        while (true)
        {
            int current = _inUse.GetOrAdd(key, 0);
            if (current <= 0 || _inUse.TryUpdate(key, current - 1, current)) return;
        }
    }

    private void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, q) in _events)
            lock (q)
                if (q.Count == 0 || now - q.Last() > TimeSpan.FromDays(1)) _events.TryRemove(key, out _);
        foreach (var (key, count) in _inUse)
            if (count == 0) _inUse.TryRemove(new KeyValuePair<string, int>(key, 0));
    }

    private sealed class Lease(LimitTracker owner, string? key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (key != null && Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(key);
        }
    }

    /// <summary>
    /// IP-ul real al clientului. În spatele tunelului Cloudflare conexiunea vine din rețeaua locală,
    /// iar IP-ul clientului este în antetul CF-Connecting-IP (luat în seamă doar pentru conexiuni locale).
    /// </summary>
    public static string ClientIp(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote != null && IsLocal(remote))
        {
            foreach (var header in new[] { "CF-Connecting-IP", "X-Forwarded-For" })
            {
                var value = ctx.Request.Headers[header].ToString().Split(',')[0].Trim();
                if (IPAddress.TryParse(value, out var forwarded)) return forwarded.ToString();
            }
        }
        return remote?.ToString() ?? "necunoscut";
    }

    private static bool IsLocal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
