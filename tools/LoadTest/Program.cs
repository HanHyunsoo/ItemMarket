using System.Diagnostics;
using ItemMarket.LoadTest;

var o = Options.Parse(args);

Console.WriteLine("=== ItemMarket load test ===");
Console.WriteLine($"api={o.Api}  scenario={o.Scenario}  concurrency={o.Concurrency}  " +
                  $"players={o.Players}  templates={o.Templates}  " +
                  $"{(o.Orders is { } n ? $"orders={n}" : $"duration={o.DurationSeconds}s")}");

var db = new Db(o.Pg);

if (!o.NoSetup)
{
    Console.Write("seeding load DB... ");
    var sw = Stopwatch.StartNew();
    await db.SetupAsync(o);
    Console.WriteLine($"done ({sw.ElapsedMilliseconds} ms) — {o.Players} players, {o.Templates} templates");
}

if (o.SetupOnly)
{
    Console.WriteLine("setup-only: exiting.");
    return 0;
}

// API 헬스 폴링(타임아웃).
if (!await WaitForHealthAsync(o.Api, TimeSpan.FromSeconds(30)))
{
    Console.Error.WriteLine($"API 헬스체크 실패: {o.Api}/health 응답 없음");
    return 2;
}

var initialCaps = await db.InitialCapsAsync();
Console.WriteLine($"baseline caps issued: {initialCaps:N0}");
Console.WriteLine("running load...");

var result = await new Driver(o).RunAsync();

Console.WriteLine();
Console.WriteLine("---- results ----");
Console.WriteLine(result.Render());

Console.WriteLine();
Console.WriteLine("---- correctness invariants (SQL) ----");
var inv = await db.CheckInvariantsAsync(o, initialCaps);
Console.Write(inv.Render());
Console.WriteLine($"  db rows: orders={inv.DbOrders:N0} trades={inv.DbTrades:N0}");
Console.WriteLine();
Console.WriteLine(inv.AllPass ? "INVARIANTS: ALL PASS" : "INVARIANTS: FAILURES DETECTED");

return inv.AllPass && result.TransportErrors == 0 ? 0 : 1;

static async Task<bool> WaitForHealthAsync(string api, TimeSpan timeout)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < timeout)
    {
        try
        {
            var r = await http.GetAsync($"{api}/health");
            if (r.IsSuccessStatusCode) return true;
        }
        catch { /* not up yet */ }
        await Task.Delay(500);
    }
    return false;
}
