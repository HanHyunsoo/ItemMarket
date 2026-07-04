namespace ItemMarket.LoadTest;

/// <summary>
/// 부하 테스트 실행 옵션. args/env 로 구성한다.
///   --api URL           대상 API 베이스 (기본 http://localhost:5090)
///   --pg  CONNSTR       설정/불변식 검증용 Postgres 연결 문자열(격리 DB)
///   --players N         합성 플레이어 수 (기본 200)
///   --templates T       테스트 템플릿 수 (spread 시 분산 대상, 기본 10)
///   --duration Ns       부하 지속 시간 초 (기본 20). --orders 지정 시 무시
///   --orders N          총 주문 수 (지정 시 duration 대신 사용)
///   --concurrency C     동시 워커 수 (기본 32)
///   --scenario spread|hot
///   --no-setup          DB 시딩 생략(기존 상태 재사용)
///   --setup-only        시딩만 하고 부하는 돌리지 않음
/// </summary>
public sealed record Options
{
    public string Api { get; init; } = "http://localhost:5090";
    public string Pg { get; init; } = "Host=localhost;Port=5432;Database=item_market_load;Username=market;Password=market";
    public int Players { get; init; } = 200;
    public int Templates { get; init; } = 10;
    public int DurationSeconds { get; init; } = 20;
    public int? Orders { get; init; }
    public int Concurrency { get; init; } = 32;
    public string Scenario { get; init; } = "spread";

    // 도메인 파라미터 — 주문이 교차(체결)하도록 좁은 대역에 분포시킨다.
    public long MidPrice { get; init; } = 1000;
    public long PriceBand { get; init; } = 25;   // 가격 = mid ± band
    public int MaxOrderQty { get; init; } = 10;

    // 시딩 규모.
    public long PlayerBalance { get; init; } = 1_000_000_000; // 지갑 초기 병뚜껑
    public int GrantQty { get; init; } = 1_000_000;           // 플레이어·템플릿당 재고

    public bool NoSetup { get; init; }
    public bool SetupOnly { get; init; }

    public bool Hot => string.Equals(Scenario, "hot", StringComparison.OrdinalIgnoreCase);

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} 값 누락");
            switch (args[i])
            {
                case "--api": o = o with { Api = Next().TrimEnd('/') }; break;
                case "--pg": o = o with { Pg = Next() }; break;
                case "--players": o = o with { Players = int.Parse(Next()) }; break;
                case "--templates": o = o with { Templates = int.Parse(Next()) }; break;
                case "--duration": o = o with { DurationSeconds = int.Parse(Next().TrimEnd('s')) }; break;
                case "--orders": o = o with { Orders = int.Parse(Next()) }; break;
                case "--concurrency": o = o with { Concurrency = int.Parse(Next()) }; break;
                case "--scenario": o = o with { Scenario = Next() }; break;
                case "--mid": o = o with { MidPrice = long.Parse(Next()) }; break;
                case "--band": o = o with { PriceBand = long.Parse(Next()) }; break;
                case "--no-setup": o = o with { NoSetup = true }; break;
                case "--setup-only": o = o with { SetupOnly = true }; break;
                case "-h" or "--help": PrintUsage(); Environment.Exit(0); break;
                default: throw new ArgumentException($"알 수 없는 인자: {args[i]}");
            }
        }
        // spread 는 최소 1개 템플릿, hot 은 항상 단일 템플릿으로 강제.
        if (o.Hot) o = o with { Templates = 1 };
        return o;
    }

    public static void PrintUsage() => Console.WriteLine(
        "usage: loadtest --api URL --pg CONNSTR [--players N] [--templates T] " +
        "[--duration Ns | --orders N] [--concurrency C] [--scenario spread|hot] " +
        "[--mid P] [--band B] [--no-setup] [--setup-only]");
}
