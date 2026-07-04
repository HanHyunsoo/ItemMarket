#!/usr/bin/env bash
# ============================================================================
#  로컬 2-실로 클러스터 데모 (Orleans ADO.NET / PostgreSQL 멤버십, Redis 미사용)
#
#  같은 머신에서 두 개의 co-host 실로(+HTTP API)를 서로 다른 포트로 띄운다.
#  둘 다 Orleans:Clustering=adonet 으로 동일 Postgres 멤버십 테이블을 공유하므로
#  하나의 클러스터를 형성한다. 특정 templateId의 OrderBookGrain 은 클러스터 전체에서
#  단일 활성화(single activation)라, 어느 실로로 요청이 들어와도 같은 활성화로 라우팅된다.
#
#  사전 준비:
#    docker compose up -d          # Postgres (ddl.sql + orleans-clustering.sql 자동 적재)
#    export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"   # (필요 시)
#
#  사용:
#    ./scripts/run-cluster.sh      # 실로1(:5080) + 실로2(:5081) 동시 기동
#    Ctrl-C 로 둘 다 종료.
#
#  멤버십 확인(Active 실로 2개):
#    docker exec item-market-db psql -U market -d item_market \
#      -c "SELECT SiloName, Address, Port, Status FROM OrleansMembershipTable;"
#    -- Status=3 == Active
# ============================================================================
set -euo pipefail
cd "$(dirname "$0")/.."

API=src/ItemMarket.Api
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"

# 공통: adonet 클러스터링. 실로/게이트웨이/HTTP 포트만 인스턴스별로 분리.
export Orleans__ClusteringMode=adonet

echo "[cluster] building..."
dotnet build "$API" -v quiet

start_silo () {
  local idx=$1 silo=$2 gw=$3 http=$4
  echo "[cluster] silo#$idx  silo=$silo gateway=$gw http=$http"
  Orleans__SiloPort=$silo \
  Orleans__GatewayPort=$gw \
  Http__Port=$http \
    dotnet run --project "$API" --no-build &
}

start_silo 1 11111 30000 5080
start_silo 2 11112 30001 5081

trap 'echo; echo "[cluster] stopping..."; kill $(jobs -p) 2>/dev/null || true' INT TERM
wait
