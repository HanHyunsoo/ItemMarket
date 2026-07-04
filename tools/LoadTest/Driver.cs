using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ItemMarket.Contracts.Auth;
using ItemMarket.Contracts.Common;
using ItemMarket.Contracts.Orders;

namespace ItemMarket.LoadTest;

/// <summary>
/// HTTP 부하 드라이버. 실제 API→Orleans→Postgres 경로를 측정한다.
/// 워커별로 플레이어 1명으로 로그인(JWT) 후, mid 가격 주변에서 랜덤 BUY/SELL
/// 지정가 주문을 반복 등록한다. 좁은 대역이라 반대편 주문과 교차하여 체결이 발생.
/// </summary>
public sealed class Driver(Options o)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<RunResult> RunAsync()
    {
        var templates = Db.TemplateIds(o.Templates);
        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = o.Concurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
        };

        // ---- 워커별 로그인(JWT 1회) --------------------------------------
        var clients = new HttpClient[o.Concurrency];
        for (var w = 0; w < o.Concurrency; w++)
        {
            var playerId = Db.PlayerId(w % o.Players);
            var token = await LoginAsync(handler, playerId);
            clients[w] = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(o.Api) };
            clients[w].DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // ---- 부하 루프 ----------------------------------------------------
        var latencies = new ConcurrentBag<double>[o.Concurrency];
        var stats = new WorkerStat[o.Concurrency];
        var errorCodes = new ConcurrentDictionary<string, long>();
        var ordersRemaining = o.Orders ?? 0;
        var sharedCounter = o.Orders is not null ? new int[] { o.Orders.Value } : null;
        var deadline = Stopwatch.StartNew();
        var runFor = TimeSpan.FromSeconds(o.DurationSeconds);

        var wall = Stopwatch.StartNew();
        var tasks = new Task[o.Concurrency];
        for (var w = 0; w < o.Concurrency; w++)
        {
            var workerId = w;
            latencies[w] = [];
            tasks[w] = Task.Run(async () =>
            {
                var rng = new Random(1000 + workerId);
                var client = clients[workerId];
                var lat = latencies[workerId];
                var stat = new WorkerStat();

                while (true)
                {
                    if (sharedCounter is not null)
                    {
                        if (Interlocked.Decrement(ref sharedCounter[0]) < 0) break;
                    }
                    else if (deadline.Elapsed >= runFor)
                    {
                        break;
                    }

                    var template = o.Hot ? templates[0] : templates[rng.Next(templates.Length)];
                    var side = rng.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell;
                    var price = o.MidPrice + rng.NextInt64(-o.PriceBand, o.PriceBand + 1);
                    if (price < 1) price = 1;
                    var qty = rng.Next(1, o.MaxOrderQty + 1);
                    var req = new PlaceOrderRequest(side, template, price, qty);

                    var sw = Stopwatch.GetTimestamp();
                    try
                    {
                        using var resp = await client.PostAsJsonAsync("/api/orders", req, Json);
                        var ms = Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
                        lat.Add(ms);
                        stat.Placed++;

                        if (resp.IsSuccessStatusCode)
                        {
                            var body = await resp.Content.ReadFromJsonAsync<ApiResponse<PlaceOrderResult>>(Json);
                            if (body is { Success: true, Data: not null })
                            {
                                stat.Ok++;
                                stat.Trades += body.Data.Fills.Count;
                            }
                            else
                            {
                                stat.BusinessErrors++;
                                errorCodes.AddOrUpdate(body?.Error?.Code.ToString() ?? "?", 1, (_, v) => v + 1);
                            }
                        }
                        else
                        {
                            stat.BusinessErrors++;
                            var code = await TryReadCode(resp);
                            errorCodes.AddOrUpdate($"HTTP{(int)resp.StatusCode}:{code}", 1, (_, v) => v + 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        lat.Add(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
                        stat.TransportErrors++;
                        errorCodes.AddOrUpdate($"EX:{ex.GetType().Name}", 1, (_, v) => v + 1);
                    }
                }
                stats[workerId] = stat;
            });
        }

        await Task.WhenAll(tasks);
        wall.Stop();

        foreach (var c in clients) c.Dispose();

        var allLat = latencies.SelectMany(b => b).ToArray();
        var agg = stats.Aggregate(new WorkerStat(), (a, s) =>
        {
            a.Placed += s.Placed; a.Ok += s.Ok; a.Trades += s.Trades;
            a.BusinessErrors += s.BusinessErrors; a.TransportErrors += s.TransportErrors;
            return a;
        });

        return new RunResult(
            Scenario: o.Scenario,
            Concurrency: o.Concurrency,
            Templates: o.Templates,
            Players: o.Players,
            ElapsedSeconds: wall.Elapsed.TotalSeconds,
            OrdersPlaced: agg.Placed,
            OrdersOk: agg.Ok,
            Trades: agg.Trades,
            BusinessErrors: agg.BusinessErrors,
            TransportErrors: agg.TransportErrors,
            Latencies: allLat,
            ErrorCodes: errorCodes.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    private async Task<string> LoginAsync(SocketsHttpHandler handler, Guid playerId)
    {
        using var c = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(o.Api) };
        using var resp = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(playerId), Json);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<TokenResponse>>(Json);
        return body?.Data?.AccessToken ?? throw new InvalidOperationException($"로그인 실패: {playerId}");
    }

    private static async Task<string> TryReadCode(HttpResponseMessage resp)
    {
        try
        {
            var body = await resp.Content.ReadFromJsonAsync<ApiResponse<object>>(Json);
            return body?.Error?.Code.ToString() ?? "?";
        }
        catch { return "?"; }
    }

    private sealed class WorkerStat
    {
        public long Placed;
        public long Ok;
        public long Trades;
        public long BusinessErrors;
        public long TransportErrors;
    }
}

public sealed record RunResult(
    string Scenario,
    int Concurrency,
    int Templates,
    int Players,
    double ElapsedSeconds,
    long OrdersPlaced,
    long OrdersOk,
    long Trades,
    long BusinessErrors,
    long TransportErrors,
    double[] Latencies,
    IReadOnlyDictionary<string, long> ErrorCodes)
{
    public double OrdersPerSec => ElapsedSeconds > 0 ? OrdersPlaced / ElapsedSeconds : 0;
    public double TradesPerSec => ElapsedSeconds > 0 ? Trades / ElapsedSeconds : 0;

    private static double Pct(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    public string Render()
    {
        var sorted = (double[])Latencies.Clone();
        Array.Sort(sorted);
        var p50 = Pct(sorted, 50);
        var p95 = Pct(sorted, 95);
        var p99 = Pct(sorted, 99);
        var max = sorted.Length > 0 ? sorted[^1] : 0;

        return $"""
            scenario={Scenario} concurrency={Concurrency} templates={Templates} players={Players}
            elapsed        : {ElapsedSeconds:F2}s
            orders placed  : {OrdersPlaced:N0}  ({OrdersPerSec:N0}/s)
            orders ok      : {OrdersOk:N0}
            trades         : {Trades:N0}  ({TradesPerSec:N0}/s)
            business errors: {BusinessErrors:N0}
            transport errs : {TransportErrors:N0}
            latency ms     : p50={p50:F1}  p95={p95:F1}  p99={p99:F1}  max={max:F1}
            error codes    : {(ErrorCodes.Count == 0 ? "(none)" : string.Join(", ", ErrorCodes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")))}
            """;
    }

    public (double p50, double p95, double p99, double max) Percentiles()
    {
        var sorted = (double[])Latencies.Clone();
        Array.Sort(sorted);
        return (Pct(sorted, 50), Pct(sorted, 95), Pct(sorted, 99), sorted.Length > 0 ? sorted[^1] : 0);
    }
}
