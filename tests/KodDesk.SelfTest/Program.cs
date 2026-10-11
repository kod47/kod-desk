// Test cap-la-cap: host + viewer în același proces, printr-un relay (implicit ws://127.0.0.1:21120, cheie "testkey").
// Verifică: înregistrarea pe relay, parolă greșită respinsă, flux video, fidelitatea culorilor.
// Utilizare: KodDesk.SelfTest [adresă-relay] [cheie-relay]
using System.Diagnostics;
using System.IO;
using KodDesk.Core;
using KodDesk.Core.Capture;
using KodDesk.Core.Files;
using KodDesk.Core.Host;
using KodDesk.Core.Video;
using KodDesk.Core.Viewer;
using KodDesk.Protocol;

if (args.Length == 2 && args[0] == "--read-clipboard") return ClipboardReader.Run(args[1]);

Log.Init("selftest", machineWide: false);
RelayConfig.UseEmbedded = false; // testul folosește relay-ul local
var relay = args.Length > 0 ? args[0] : "ws://127.0.0.1:21120";
var key = args.Length > 1 ? args[1] : "testkey";
const string password = "parola-de-test-123";

var settings = new HostSettings
{
    HostId = "9" + Random.Shared.Next(10_000_000, 99_999_999),
    RelaySecret = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
    RelayAddress = relay,
    RelayKey = key,
};
settings.SetPassword(password);
var viewer = new ViewerSettings { RelayAddress = relay, RelayKey = key, BitrateKbps = 12000, MaxFps = 60 };

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}");
    if (!ok) failures++;
}

// ---- Faza A: captură -> encoder -> decoder, fără rețea (performanță + fidelitatea culorilor) ----
unsafe
{
    using var cap = new ScreenCapturer(ScreenCapturer.PrimaryIndex());
    var t = Stopwatch.StartNew();
    while (!cap.HasFrame && t.ElapsedMilliseconds < 3000) cap.TryCapture(100);
    Check(cap.HasFrame, $"captură directă {cap.Width}x{cap.Height}");

    foreach (var label in VideoEncoder.CandidateLabels)
    {
        VideoEncoder enc;
        try { enc = VideoEncoder.Create(cap.Width, cap.Height, 12000, 60, label); }
        catch { Console.WriteLine($"     {label,-14} indisponibil"); continue; }
        using var _ = enc;
        using var dec = new VideoDecoder(enc.FullRange);
        using var fb = new FrameBuffer();
        var packets = new List<byte[]>();
        double encMs = 0, decMs = 0;
        const int N = 30;
        for (int i = 0; i < N; i++)
        {
            packets.Clear();
            var te = Stopwatch.StartNew();
            enc.Encode(cap.Data, cap.Stride, i * 16, i == 0, (d, k) => packets.Add(d.ToArray()));
            encMs += te.Elapsed.TotalMilliseconds;
            var td = Stopwatch.StartNew();
            foreach (var p in packets) dec.Decode(p, fb);
            decMs += td.Elapsed.TotalMilliseconds;
        }

        double sumDiff = 0, sumBias = 0; long n = 0;
        fb.TryReadLatest((ptr, w, h, stride) =>
        {
            var decoded = (byte*)ptr;
            var src = (byte*)cap.Data;
            for (int y = 0; y < Math.Min(h, cap.Height); y += 2)
                for (int x = 0; x < Math.Min(w, cap.Width); x += 2)
                    for (int c = 0; c < 3; c++)
                    {
                        int a = src[(long)y * cap.Stride + x * 4 + c];
                        int b = decoded[(long)y * stride + x * 4 + c];
                        sumDiff += Math.Abs(a - b); sumBias += b - a; n++;
                    }
        });
        double mad = n > 0 ? sumDiff / n : 99, bias = n > 0 ? sumBias / n : 99;
        bool ok = n > 0 && Math.Abs(bias) < 1.0 && mad < 3;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {label,-14} encode {encMs / N,5:F1} ms  decode {decMs / N,5:F1} ms  " +
                          $"diferență {mad:F2}  deplasare {bias:+0.00;-0.00}");
        if (!ok) failures++;
    }
}

await using var agent = new HostAgent(() => settings, new InProcessSessionLauncher(() => settings));
agent.Start();
var sw = Stopwatch.StartNew();
while (agent.Status != HostStatus.Online && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
Check(agent.Status == HostStatus.Online, $"host online pe relay (ID {settings.HostId}) {agent.StatusMessage}");

// Parolă greșită
try
{
    await using var bad = await ViewerSession.ConnectAsync(viewer, settings.HostId, "gresit", null, CancellationToken.None);
    Check(false, "parola greșită trebuia respinsă");
}
catch (AuthenticationFailedException ex)
{
    Check(true, "parolă greșită respinsă: " + ex.Message);
}

// ID inexistent
try
{
    await using var none = await ViewerSession.ConnectAsync(viewer, "111111111", password, null, CancellationToken.None);
    Check(false, "ID inexistent trebuia respins");
}
catch (IOException ex)
{
    Check(true, "ID inexistent: " + ex.Message);
}

// Sesiune reală
// Conectare cu cheia memorată (fără parolă), apoi cu parola.
{
    var savedKey = SecureHandshake.DerivePasswordKey(password, settings.HostId);
    await using var withKey = await ViewerSession.ConnectAsync(viewer, settings.HostId, null, savedKey, CancellationToken.None);
    Check(true, "conectat cu parola memorată");
}
await Task.Delay(500);
await using var session = await ViewerSession.ConnectAsync(viewer, settings.HostId, password, null, CancellationToken.None);
Check(true, $"conectat la {session.HostName}");

int frames = 0;
session.FrameReady += () => Interlocked.Increment(ref frames);
sw.Restart();
while (frames == 0 && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
Check(frames > 0, $"primul cadru după {sw.ElapsedMilliseconds} ms");

// Generăm mișcare pe ecran ca să măsurăm fps: o fereastră consolă care se actualizează.
var measure = Stopwatch.StartNew();
int startFrames = frames;
while (measure.Elapsed < TimeSpan.FromSeconds(4))
{
    Console.Title = "KodDesk selftest " + DateTime.Now.ToString("HH:mm:ss.fff");
    await Task.Delay(10);
}
var stats = session.GetStats();
Console.WriteLine($"     {stats.Width}x{stats.Height} {stats.Encoder} {(stats.Yuv444 ? "4:4:4" : "4:2:0")}, " +
                  $"{(frames - startFrames) / measure.Elapsed.TotalSeconds:F1} fps decodate, RTT {stats.RttMs} ms");
Check(frames - startFrames > 10, "flux video continuu");

// ---- Faza C: transfer de fișiere (încărcare, listare, descărcare) ----
{
    var work = Path.Combine(Path.GetTempPath(), "koddesk-test-" + Guid.NewGuid().ToString("N")[..8]);
    var src = Path.Combine(work, "sursa");
    var remote = Path.Combine(work, "remote");
    var back = Path.Combine(work, "inapoi");
    Directory.CreateDirectory(Path.Combine(src, "Proiect", "sub folder"));
    Directory.CreateDirectory(remote);
    var rnd = new Random(42);
    void MakeFile(string path, int size) { var b = new byte[size]; rnd.NextBytes(b); File.WriteAllBytes(path, b); }
    MakeFile(Path.Combine(src, "Proiect", "mare.bin"), 12 * 1024 * 1024 + 123);
    MakeFile(Path.Combine(src, "Proiect", "sub folder", "mic.txt"), 1000);
    File.WriteAllBytes(Path.Combine(src, "Proiect", "gol.dat"), []);
    MakeFile(Path.Combine(src, "singur.jpg"), 300_000);

    static List<(string, string)> Hashes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(f => (Path.GetRelativePath(root, f), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))))
        .OrderBy(x => x.Item1).ToList();

    var sw2 = Stopwatch.StartNew();
    var up = await session.Files.UploadAsync([Path.Combine(src, "Proiect"), Path.Combine(src, "singur.jpg")], remote, null, CancellationToken.None);
    Check(up.Ok, $"încărcare: {up.Message} ({12.6 / sw2.Elapsed.TotalSeconds:F0} MB/s prin relay local)");
    Check(Hashes(src).SequenceEqual(Hashes(remote)), "fișierele încărcate sunt identice (SHA-256)");

    var up2 = await session.Files.UploadAsync([Path.Combine(src, "singur.jpg")], remote, null, CancellationToken.None);
    Check(up2.Ok && File.Exists(Path.Combine(remote, "singur (2).jpg")), "nume existent -> „singur (2).jpg”, fără suprascriere");

    var (_, items) = await session.Files.ListDirectoryAsync(remote, CancellationToken.None);
    Check(items.Any(i => i.Name == "Proiect" && i.Kind == DirItemKind.Directory) && items.Any(i => i.Name == "singur.jpg"),
        $"listare folder la distanță ({items.Count} elemente)");
    var (_, roots) = await session.Files.ListDirectoryAsync("", CancellationToken.None);
    Check(roots.Any(i => i.Kind == DirItemKind.Drive), $"listare unități și locuri ({string.Join(", ", roots.Select(r => r.Name))})");

    var (setId, entries) = await session.Files.RequestDownloadAsync([Path.Combine(remote, "Proiect")], CancellationToken.None);
    await session.Files.ReceiveSetAsync(setId, entries, back, null, CancellationToken.None);
    Check(Hashes(Path.Combine(src, "Proiect")).SequenceEqual(Hashes(Path.Combine(back, "Proiect"))), "descărcare folder: conținut identic");

    try
    {
        SafePath.Combine(back, @"..\..\Windows\evil.dll");
        Check(false, "calea „..” trebuia respinsă");
    }
    catch (InvalidDataException) { Check(true, "cale malițioasă („..”) respinsă"); }

    try { Directory.Delete(work, true); } catch { }
}

// ---- Faza D: Ctrl+C pe fișiere -> fișiere virtuale în clipboard, citite din alt proces (ca Explorer la lipire) ----
if (session.Clipboard != null)
{
    var dir = Path.Combine(Path.GetTempPath(), "koddesk-clip-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    var file = Path.Combine(dir, "clip-test.bin");
    var data = new byte[3 * 1024 * 1024 + 7];
    new Random(7).NextBytes(data);
    File.WriteAllBytes(file, data);
    var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

    static string Run(string exe, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    Run("powershell.exe", $"-NoProfile -STA -Command Set-Clipboard -Path '{file}'");
    await Task.Delay(2000);
    var output = Run("dotnet", $"\"{typeof(ClipboardReader).Assembly.Location}\" --read-clipboard clip-test.bin");
    Check(output.StartsWith("HASH " + expected), $"fișier copiat cu Ctrl+C, citit virtual din alt proces ({output})");
    Run("powershell.exe", "-NoProfile -STA -Command Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Clipboard]::Clear()");
    try { Directory.Delete(dir, true); } catch { }
}

// ---- Faza E: interfața (fereastra de control + exploratorul la distanță) peste sesiunea reală ----
{
    var (uiOk, uiDetail) = UiSmokeTest.Run(session, viewer);
    Check(uiOk, "interfață: " + uiDetail);
}

Console.WriteLine(failures == 0 ?"\nTOATE TESTELE AU TRECUT" : $"\n{failures} TESTE EȘUATE");
return failures == 0 ? 0 : 1;
