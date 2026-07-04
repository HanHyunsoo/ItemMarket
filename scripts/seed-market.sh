#!/usr/bin/env bash
# ============================================================================
#  seed-market.sh — 실제 API를 통해 시장에 유동성(호가)을 채우는 시드 스크립트
#
#  Trader_Charlie(어드민, 부유한 지갑)를 마켓메이커로 사용:
#   1) /api/auth/login 으로 어드민 토큰 발급
#   2) /api/admin/wallet/adjust 로 매수 에스크로용 병뚜껑 충전
#   3) /api/admin/grant/stack, /api/admin/grant/instance 로 재고 지급
#   4) /api/orders 로 인기 템플릿 ~14종에 매도 3단 호가 + 아래쪽 매수 2단 호가 배치
#      (매수 호가는 매도보다 낮아 교차하지 않고, 자전거래는 서버가 차단)
#
#  사용법:
#    ./scripts/seed-market.sh                  # 기본 http://localhost:5080
#    ./scripts/seed-market.sh http://host:port # 또는 API_BASE 환경변수
#
#  재실행 안전(idempotent-ish): 실패 없이 기존 호가 위에 깊이(depth)를 더한다.
#  사전 조건: API 기동 상태(/health), 시드 DB(ddl.sql), jq 설치.
# ============================================================================
set -uo pipefail

API_BASE="${1:-${API_BASE:-http://localhost:5080}}"
CHARLIE="33333333-3333-3333-3333-333333333333"   # Trader_Charlie (admin)
TOPUP=30000                                       # 매수 에스크로 여유분(런당)

command -v jq >/dev/null || { echo "오류: jq가 필요합니다 (brew install jq)"; exit 1; }

# ---- API 헬퍼 ---------------------------------------------------------------
ok_count=0; fail_count=0

req() { # method path json → body 출력, 실패 시 비어있지 않은 에러 메시지 기록
  local method=$1 path=$2 json=${3:-}
  curl -sS -m 15 -X "$method" "$API_BASE$path" \
    -H "Content-Type: application/json" \
    ${TOKEN:+-H "Authorization: Bearer $TOKEN"} \
    ${json:+-d "$json"} 2>/dev/null
}

must() { # 성공 필수 호출: success=false면 즉시 종료
  local body; body=$(req "$@")
  if [[ -z "$body" || $(jq -r '.success' <<<"$body" 2>/dev/null) != "true" ]]; then
    echo "치명적 실패: $1 $2" >&2
    jq -r '.error // .' <<<"${body:-"(no response)"}" >&2
    exit 1
  fi
  echo "$body"
}

place_order() { # side templateId price qty [instanceId] — 실패해도 계속(관용)
  local side=$1 tid=$2 price=$3 qty=$4 iid=${5:-null}
  [[ "$iid" != null ]] && iid="\"$iid\""
  local body
  body=$(req POST /api/orders \
    "{\"side\":\"$side\",\"itemTemplateId\":$tid,\"unitPrice\":$price,\"quantity\":$qty,\"instanceId\":$iid}")
  if [[ -n "$body" && $(jq -r '.success' <<<"$body" 2>/dev/null) == "true" ]]; then
    ok_count=$((ok_count+1))
  else
    fail_count=$((fail_count+1))
    echo "  경고: $side tid=$tid @$price x$qty 실패 — $(jq -r '.error.message // "no response"' <<<"${body:-"{}"}" 2>/dev/null)" >&2
  fi
}

# ---- 0) 헬스체크 ------------------------------------------------------------
echo "[seed] API: $API_BASE"
if ! curl -sf -m 5 "$API_BASE/health" >/dev/null; then
  echo "오류: $API_BASE/health 응답 없음 — API를 먼저 기동하세요." >&2
  exit 1
fi

# ---- 1) 어드민 로그인 --------------------------------------------------------
TOKEN=""
TOKEN=$(must POST /api/auth/login "{\"playerId\":\"$CHARLIE\"}" | jq -r '.data.accessToken')
echo "[seed] Trader_Charlie 로그인 완료"

# ---- 2) 병뚜껑 충전(매수 호가 에스크로용) -------------------------------------
must POST /api/admin/wallet/adjust \
  "{\"playerId\":\"$CHARLIE\",\"delta\":$TOPUP,\"reason\":\"seed-market: market maker top-up\"}" >/dev/null
echo "[seed] 지갑 충전 +$TOPUP CAP"

# ---- 3) 스택형 인기 템플릿: 재고 지급 + 3단 매도 / 2단 매수 -------------------
# 형식: templateId|기준가(base_value 근사)
STACKABLES=(
  "1|8"     # 통조림 콩 (FOOD)
  "8|20"    # 육포 (FOOD)
  "15|6"    # 생수 (FOOD)
  "25|45"   # 전투식량 MRE (FOOD)
  "31|12"   # 붕대 (MEDICAL)
  "34|25"   # 진통제 (MEDICAL)
  "35|60"   # 항생제 (MEDICAL)
  "39|120"  # 구급상자 (MEDICAL)
  "93|4"    # 9mm 탄약 (AMMO)
  "95|8"    # 7.62mm 탄약 (AMMO)
  "96|8"    # 5.56mm 탄약 (AMMO)
  "97|7"    # 12게이지 산탄 (AMMO)
)

SEEDED_TIDS=()
for entry in "${STACKABLES[@]}"; do
  tid=${entry%%|*}; base=${entry##*|}
  SEEDED_TIDS+=("$tid")

  # 매도 60개 분량 재고 지급 (30+20+10)
  must POST /api/admin/grant/stack \
    "{\"playerId\":\"$CHARLIE\",\"templateId\":$tid,\"quantity\":60}" >/dev/null

  # 매도 호가 3단: 기준가 +5% / +20% / +40% (올림, 최소 +1씩 벌림)
  s1=$(( base + (base * 5 + 99) / 100 ))
  s2=$(( base + (base * 20 + 99) / 100 )); (( s2 <= s1 )) && s2=$((s1 + 1))
  s3=$(( base + (base * 40 + 99) / 100 )); (( s3 <= s2 )) && s3=$((s2 + 1))
  place_order Sell "$tid" "$s1" 30
  place_order Sell "$tid" "$s2" 20
  place_order Sell "$tid" "$s3" 10

  # 매수 호가 2단: 기준가 -15% / -30% (최소 1 CAP)
  b1=$(( base * 85 / 100 )); (( b1 < 1 )) && b1=1
  b2=$(( base * 70 / 100 )); (( b2 < 1 )) && b2=1; (( b2 >= b1 && b1 > 1 )) && b2=$((b1 - 1))
  place_order Buy "$tid" "$b1" 20
  place_order Buy "$tid" "$b2" 30

  echo "[seed] tid=$tid  asks: $s1/$s2/$s3  bids: $b1/$b2"
done

# ---- 4) 유니크(MELEE/GUN) 인스턴스: 지급 후 개당 매도 -------------------------
# 형식: templateId|판매가|내구도|부착물(JSON)
UNIQUES=(
  "54|140|85|[]"                              # 전투용 나이프
  "59|250|130|[]"                             # 소방도끼
  "75|1100|320|[\"suppressor\"]"              # 글록 권총
)

for entry in "${UNIQUES[@]}"; do
  IFS='|' read -r tid price dura atts <<<"$entry"
  SEEDED_TIDS+=("$tid")
  iid=$(must POST /api/admin/grant/instance \
    "{\"playerId\":\"$CHARLIE\",\"templateId\":$tid,\"durability\":$dura,\"attachments\":$atts}" \
    | jq -r '.data.id')
  place_order Sell "$tid" "$price" 1 "$iid"
  echo "[seed] tid=$tid  유니크 인스턴스 $iid 매도 @$price"
done

# ---- 5) 요약 테이블 -----------------------------------------------------------
echo
echo "=== 시장 요약 (Trader_Charlie 마켓메이킹) ==============================="
printf "%-6s %-14s %-14s %-12s %-12s\n" "TID" "최고매수(qty)" "최저매도(qty)" "매수레벨" "매도레벨"
# 중복 tid 제거 후 정렬
for tid in $(printf "%s\n" "${SEEDED_TIDS[@]}" | sort -nu); do
  book=$(req GET "/api/market/$tid/book")
  bid=$(jq -r '.data.bids[0] | if . then "\(.unitPrice)(\(.quantity))" else "-" end' <<<"$book")
  ask=$(jq -r '.data.asks[0] | if . then "\(.unitPrice)(\(.quantity))" else "-" end' <<<"$book")
  nb=$(jq -r '.data.bids | length' <<<"$book")
  na=$(jq -r '.data.asks | length' <<<"$book")
  printf "%-6s %-14s %-14s %-12s %-12s\n" "$tid" "$bid" "$ask" "$nb" "$na"
done
echo "========================================================================"
echo "[seed] 주문 성공: ${ok_count}건, 실패(관용): ${fail_count}건"
wallet=$(req GET /api/wallet | jq -r '.data.balance')
echo "[seed] Charlie 잔액: ${wallet:-?} CAP"
echo "[seed] 완료 — UI에서 바로 사고팔 수 있습니다."
