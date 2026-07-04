#!/usr/bin/env bash
# ============================================================================
#  다중 인스턴스 실시간 클러스터 데모 (Orleans ADO.NET 멤버십 + Redis SignalR 백플레인)
#
#  같은 머신에서 두 개의 co-host 실로(+HTTP API)를 서로 다른 포트로 띄운다. 둘 다
#  Orleans:ClusteringMode=adonet 으로 동일 Postgres 멤버십 테이블을 공유하므로 하나의
#  클러스터를 형성한다. 특정 templateId의 OrderBookGrain 은 클러스터 전체에서 단일
#  활성화(single activation)라, 어느 인스턴스로 REST 가 들어와도 같은 활성화로 라우팅된다.
#
#  ── 왜 Redis 백플레인인가 ────────────────────────────────────────────────
#  SignalR 클라이언트는 두 인스턴스 중 "한쪽"에만 붙는다. REST 를 처리한 인스턴스가
#  IHubContext 로 이벤트를 발행해도, 구독자가 "다른" 인스턴스에 붙어 있으면 못 받는다.
#  Redis:ConnectionString 을 주면 SignalR 백플레인이 인스턴스 간 브로드캐스트를 중계해
#  크로스-인스턴스 라이브 푸시가 성립한다. (Redis 없으면 인메모리 → 단일 인스턴스 한정.)
#
#  ── 라이브 데모 격리 ─────────────────────────────────────────────────────
#  라이브 데모(:5080 API, :5173 Vite, DB item_market)를 절대 건드리지 않는다:
#   - 전용 DB item_market_cluster (같은 컨테이너, db/ddl.sql + orleans-clustering.sql 적용)
#   - 전용 ClusterId item-market-cluster
#   - 전용 포트(HTTP 5091/5092, Silo 11131/11132, Gateway 30031/30032)
#   - Redis 백플레인(localhost:6379)
#
#  사전 준비:
#    docker compose up -d                                  # Postgres + Redis
#    export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec" # (필요 시)
#
#  사용:
#    ./scripts/run-cluster.sh            # 전용 DB 준비 → 인스턴스 A/B 기동 → /health 폴링
#    Ctrl-C                              # 두 인스턴스 종료 + 전용 DB drop (완전 정리)
#    KEEP_DB=1 ./scripts/run-cluster.sh  # 종료 시 DB 를 남긴다(반복 데모용)
#    ./scripts/run-cluster.sh --teardown # 남은 인스턴스/전용 DB 만 정리하고 종료
#
#  멤버십 확인(Active 실로 2개, Status=3):
#    docker exec item-market-db psql -U market -d item_market_cluster \
#      -c "SELECT SiloName, Address, Port, Status FROM OrleansMembershipTable;"
# ============================================================================
set -euo pipefail
cd "$(dirname "$0")/.."

API=src/ItemMarket.Api
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"

# ── 전용 리소스(라이브 데모와 분리) ─────────────────────────────────────────
PG_CONTAINER="${PG_CONTAINER:-item-market-db}"
CLUSTER_DB="${CLUSTER_DB:-item_market_cluster}"
CLUSTER_ID="${CLUSTER_ID:-item-market-cluster}"
REDIS_CONN="${REDIS_CONN:-localhost:6379}"
CONN="Host=localhost;Port=5432;Database=${CLUSTER_DB};Username=market;Password=market"

HTTP_A=5091; SILO_A=11131; GW_A=30031
HTTP_B=5092; SILO_B=11132; GW_B=30032

psql_pg() { docker exec -i "$PG_CONTAINER" psql -U market -v ON_ERROR_STOP=1 "$@"; }

drop_cluster_db() {
  echo "[cluster] dropping DB ${CLUSTER_DB}..."
  psql_pg -d postgres -c \
    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='${CLUSTER_DB}';" >/dev/null 2>&1 || true
  psql_pg -d postgres -c "DROP DATABASE IF EXISTS ${CLUSTER_DB};" >/dev/null 2>&1 || true
}

teardown() {
  echo; echo "[cluster] stopping instances..."
  kill $(jobs -p) 2>/dev/null || true
  wait 2>/dev/null || true
  if [[ "${KEEP_DB:-0}" == "1" ]]; then
    echo "[cluster] KEEP_DB=1 → DB ${CLUSTER_DB} 유지"
  else
    drop_cluster_db
  fi
  echo "[cluster] done. 라이브 데모(:5080 / item_market)는 건드리지 않았습니다."
}

# --teardown: 남은 리소스만 정리하고 종료(인스턴스는 포트로 찾아 종료)
if [[ "${1:-}" == "--teardown" ]]; then
  for p in "$HTTP_A" "$HTTP_B"; do
    pid=$(lsof -tiTCP:"$p" -sTCP:LISTEN 2>/dev/null || true)
    [[ -n "$pid" ]] && { echo "[cluster] kill :$p (pid $pid)"; kill "$pid" 2>/dev/null || true; }
  done
  drop_cluster_db
  echo "[cluster] teardown complete."
  exit 0
fi

# ── 0) Redis 확인 ───────────────────────────────────────────────────────────
if ! docker exec "${REDIS_CONTAINER:-item-market-redis}" redis-cli ping >/dev/null 2>&1; then
  echo "[cluster] Redis 준비 중(docker compose up -d redis)..."
  docker compose up -d redis
fi

# ── 1) 전용 DB 준비(ddl + orleans 멤버십). 이미 있으면 재사용 ─────────────────
if ! psql_pg -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='${CLUSTER_DB}'" | grep -q 1; then
  echo "[cluster] creating DB ${CLUSTER_DB}..."
  psql_pg -d postgres -c "CREATE DATABASE ${CLUSTER_DB};" >/dev/null
fi
# 멤버십 스키마가 없으면 도메인 스키마 + Orleans 클러스터링 스키마를 적재.
if ! psql_pg -d "${CLUSTER_DB}" -tAc \
     "SELECT to_regclass('public.orleansmembershiptable')" | grep -qi orleans; then
  echo "[cluster] applying db/ddl.sql + db/orleans-clustering.sql → ${CLUSTER_DB}..."
  psql_pg -d "${CLUSTER_DB}" < db/ddl.sql >/dev/null
  psql_pg -d "${CLUSTER_DB}" < db/orleans-clustering.sql >/dev/null
fi

# ── 2) 빌드 ─────────────────────────────────────────────────────────────────
echo "[cluster] building..."
dotnet build "$API" -c Release -v quiet

# ── 3) 인스턴스 기동(공통: adonet 클러스터링 + 전용 ClusterId + Redis 백플레인) ──
export Orleans__ClusteringMode=adonet
export Orleans__ClusterId="$CLUSTER_ID"
export Orleans__ServiceId="$CLUSTER_ID"
export ConnectionStrings__Postgres="$CONN"
export Redis__ConnectionString="$REDIS_CONN"

start_instance () {
  local name=$1 silo=$2 gw=$3 http=$4
  echo "[cluster] instance $name  http=$http silo=$silo gateway=$gw"
  Orleans__SiloPort=$silo \
  Orleans__GatewayPort=$gw \
  Http__Port=$http \
    dotnet run --project "$API" -c Release --no-build &
}

trap teardown INT TERM

start_instance A "$SILO_A" "$GW_A" "$HTTP_A"
start_instance B "$SILO_B" "$GW_B" "$HTTP_B"

# ── 4) 두 인스턴스 /health 폴링 ──────────────────────────────────────────────
wait_health () {
  local url=$1 name=$2
  for _ in $(seq 1 60); do
    if curl -sf -m 2 "$url/health" >/dev/null 2>&1; then
      echo "[cluster] instance $name UP  ($url/health)"; return 0
    fi
    sleep 1
  done
  echo "[cluster] instance $name 기동 실패($url/health 무응답)"; return 1
}
wait_health "http://localhost:$HTTP_A" A
wait_health "http://localhost:$HTTP_B" B

cat <<EOF

=== 다중 인스턴스 실시간 클러스터 준비 완료 =================================
  인스턴스 A : http://localhost:$HTTP_A   (silo $SILO_A / gw $GW_A)
  인스턴스 B : http://localhost:$HTTP_B   (silo $SILO_B / gw $GW_B)
  ClusterId  : $CLUSTER_ID      DB: $CLUSTER_DB      Redis: $REDIS_CONN

  멤버십(Active 실로 2개, Status=3):
    docker exec $PG_CONTAINER psql -U market -d $CLUSTER_DB \\
      -c "SELECT SiloName, Address, Port, Status FROM OrleansMembershipTable;"

  크로스-인스턴스 라이브 푸시 시연:
    1) SignalR 클라이언트를 A(:$HTTP_A/hubs/market?access_token=…)에 붙이고 SubscribeTemplate(T)
    2) B(:$HTTP_B)의 REST 로 T 에 매칭되는 sell+buy 등록
    3) A 의 클라이언트가 OrderBookUpdated + TradeExecuted 수신 → Redis 백플레인이 중계

  Ctrl-C 로 두 인스턴스 종료 + 전용 DB($CLUSTER_DB) drop (라이브 데모는 무관).
============================================================================
EOF

wait
