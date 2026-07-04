#!/usr/bin/env bash
# 체결(trade) 이력 더미 시드 — 실제 API로 Alpha↔Bravo가 거래를 체결시켜
# 모든 스택형 아이템 + 유니크 몇 종에 "최근 체결" 히스토리를 만든다.
#   ./scripts/seed-trades.sh [API_BASE]   (기본 http://localhost:5080, jq 필요)
# 여러 번 실행해도 안전(체결이 더 쌓일 뿐).
set -u
API="${1:-http://localhost:5080}"

ALPHA="11111111-1111-1111-1111-111111111111"
BRAVO="22222222-2222-2222-2222-222222222222"
CHARLIE="33333333-3333-3333-3333-333333333333"

jqr() { jq -r "$1" 2>/dev/null; }

login() {
  curl -s "$API/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"playerId\":\"$1\"}" | jqr '.data.accessToken'
}

TA=$(login "$ALPHA"); TB=$(login "$BRAVO"); TC=$(login "$CHARLIE")
[ -n "$TA" ] && [ -n "$TB" ] && [ -n "$TC" ] || { echo "[trades] 로그인 실패 — API 확인"; exit 1; }
echo "[trades] 로그인 완료 (alpha/bravo/charlie)"

adjust() { # playerId delta
  curl -s -X POST "$API/api/admin/wallet/adjust" -H "Authorization: Bearer $TC" \
    -H 'Content-Type: application/json' \
    -d "{\"playerId\":\"$1\",\"delta\":$2,\"reason\":\"seed-trades funding\"}" >/dev/null
}
grant_stack() { # playerId templateId qty
  curl -s -X POST "$API/api/admin/grant/stack" -H "Authorization: Bearer $TC" \
    -H 'Content-Type: application/json' \
    -d "{\"playerId\":\"$1\",\"templateId\":$2,\"quantity\":$3}" >/dev/null
}
place() { # token side templateId price qty [instanceId]
  local inst=${6:-null}
  [ "$inst" != "null" ] && inst="\"$inst\""
  curl -s -X POST "$API/api/orders" -H "Authorization: Bearer $1" \
    -H 'Content-Type: application/json' \
    -d "{\"side\":\"$2\",\"itemTemplateId\":$3,\"unitPrice\":$4,\"quantity\":$5,\"instanceId\":$inst}"
}

# 거래 자금 두둑히
adjust "$ALPHA" 150000; adjust "$BRAVO" 150000
echo "[trades] Alpha/Bravo 자금 +150k CAP"

CATALOG=$(curl -s "$API/api/catalog" -H "Authorization: Bearer $TA")

fills=0; attempts=0

# ---- 스택형 전 종목: 종목당 2~4회 체결 ----------------------------------
while read -r tid base; do
  # 스프레드 안쪽 가격만 사용해 기존 호가(마켓메이커 depth)를 침범하지 않는다.
  book=$(curl -s "$API/api/market/$tid/book" -H "Authorization: Bearer $TA")
  bid=$(echo "$book" | jqr '.data.bids[0].unitPrice // 0')
  ask=$(echo "$book" | jqr '.data.asks[0].unitPrice // 0')

  rounds=$(( RANDOM % 3 + 2 ))
  for r in $(seq 1 "$rounds"); do
    # 가격: 스프레드 내부 > 없으면 base ±20% 흔들기 (최소 1)
    if [ "$bid" -gt 0 ] && [ "$ask" -gt 0 ] && [ $((ask - bid)) -ge 2 ]; then
      span=$(( ask - bid - 1 )); p=$(( bid + 1 + RANDOM % span ))
    else
      jitter=$(( base / 5 + 1 )); p=$(( base - jitter + RANDOM % (2 * jitter + 1) ))
      [ "$p" -lt 1 ] && p=1
      [ "$bid" -gt 0 ] && [ "$p" -le "$bid" ] && p=$(( bid + 1 ))
    fi
    qty=$(( RANDOM % 12 + 3 ))

    # 라운드마다 매도/매수 주체를 교대 → 둘 다 구매자/판매자로 등장
    if [ $(( r % 2 )) -eq 0 ]; then M="$ALPHA"; TM="$TA"; TT="$TB"; else M="$BRAVO"; TM="$TB"; TT="$TA"; fi

    grant_stack "$M" "$tid" "$qty"
    place "$TM" Sell "$tid" "$p" "$qty" >/dev/null
    res=$(place "$TT" Buy "$tid" "$p" "$qty")
    n=$(echo "$res" | jqr '.data.fills | length // 0')
    attempts=$(( attempts + 1 )); fills=$(( fills + ${n:-0} ))
  done
done < <(echo "$CATALOG" | jqr '.data[] | select(.stackable) | "\(.id) \(.baseValue)"')

echo "[trades] 스택형 체결 시드 완료"

# ---- 유니크 무기 몇 종: 인스턴스 지급 → 체결 1회씩 ----------------------
for spec in "53 40 25" "56 70 40" "60 100 75" "74 300 650" "76 400 780" "85 450 1500"; do
  set -- $spec; tid=$1; dur=$2; price=$3
  inst=$(curl -s -X POST "$API/api/admin/grant/instance" -H "Authorization: Bearer $TC" \
    -H 'Content-Type: application/json' \
    -d "{\"playerId\":\"$ALPHA\",\"templateId\":$tid,\"durability\":$dur,\"attachments\":[]}" \
    | jqr '.data.id')
  [ -n "$inst" ] && [ "$inst" != "null" ] || { echo "[trades] tid=$tid 인스턴스 지급 실패, 건너뜀"; continue; }
  place "$TA" Sell "$tid" "$price" 1 "$inst" >/dev/null
  res=$(place "$TB" Buy "$tid" "$price" 1)
  n=$(echo "$res" | jqr '.data.fills | length // 0')
  attempts=$(( attempts + 1 )); fills=$(( fills + ${n:-0} ))
  echo "[trades] 유니크 tid=$tid 체결 @$price"
done

# ---- 요약 ---------------------------------------------------------------
total=$(curl -s "$API/api/admin/trades?page=1&size=1" -H "Authorization: Bearer $TC" | jqr '.data.totalCount')
echo "==========================================="
echo "[trades] 이번 실행 체결 라운드: $attempts, 누적 총 체결: ${total:-?}건"
echo "[trades] 완료 — 아이템 상세의 '최근 체결'이 채워졌습니다."
