using System.Data.Common;
using Npgsql;
using Orleans.Serialization;

namespace ItemMarket.Api.Infrastructure;

/// <summary>
/// Orleans 실로 co-host 구성. 클러스터링은 config 스왑으로 분기:
///  - localhost : 개발용 단일 실로.
///  - adonet    : Postgres 멤버십으로 다중 실로 클러스터 형성(Redis 미사용).
/// </summary>
public static class OrleansHosting
{
    public static WebApplicationBuilder AddMarketOrleans(this WebApplicationBuilder builder, string connString)
    {
        var cfg = builder.Configuration;

        // ADO.NET 클러스터링(Npgsql) 프로바이더 등록 — adonet 모드에서 필요.
        if (!DbProviderFactories.GetProviderInvariantNames().Contains("Npgsql"))
            DbProviderFactories.RegisterFactory("Npgsql", NpgsqlFactory.Instance);

        // 클러스터링 스왑 스위치. Orleans 9는 "Orleans" 설정 섹션을 예약(프로바이더 자동 바인딩)하므로
        // 예약 키 "Clustering" 대신 스칼라 "ClusteringMode"를 사용한다(Orleans는 이 키를 무시).
        var clustering = cfg["Orleans:ClusteringMode"] ?? "localhost";
        var clusterId = cfg["Orleans:ClusterId"] ?? "item-market";
        var serviceId = cfg["Orleans:ServiceId"] ?? "item-market";
        var siloPort = cfg.GetValue("Orleans:SiloPort", 11111);
        var gatewayPort = cfg.GetValue("Orleans:GatewayPort", 30000);

        builder.Host.UseOrleans(silo =>
        {
            if (string.Equals(clustering, "adonet", StringComparison.OrdinalIgnoreCase))
            {
                silo.Configure<Orleans.Configuration.ClusterOptions>(o =>
                    {
                        o.ClusterId = clusterId;
                        o.ServiceId = serviceId;
                    })
                    .UseAdoNetClustering(o =>
                    {
                        o.Invariant = "Npgsql";
                        o.ConnectionString = connString;
                    })
                    .ConfigureEndpoints(siloPort: siloPort, gatewayPort: gatewayPort);
            }
            else
            {
                silo.UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort);
            }
        });

        // Orleans 직렬화: 계약(Contracts) DTO는 공유 계약이라 [GenerateSerializer]를
        // 붙일 수 없다. grain 경계를 넘는 ItemMarket.* 타입은 JSON 직렬화기로 처리한다
        // (인프로세스 copier 포함). 계약을 수정하지 않고 코덱 부재 문제를 해결.
        builder.Services.AddSerializer(sb => sb.AddJsonSerializer(
            isSupported: t => t.Namespace is not null && t.Namespace.StartsWith("ItemMarket")));

        return builder;
    }
}
