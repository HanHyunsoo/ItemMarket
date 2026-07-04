# UI 스크린샷 캡처 (Playwright)

README에 넣는 UI 스크린샷을 재현 가능하게 캡처하는 스크립트입니다. 실행 중인 앱을
헤더 플레이어 스위처로 로그인한 뒤 각 페이지를 `docs/screenshots/*.png`로 저장합니다.
**읽기 전용** — 주문 등록/취소나 아이템 지급 없이 탐색·조회만 합니다.

## 사전 조건
- 프론트가 http://localhost:5173, API가 http://localhost:5080 에서 실행 중이어야 합니다.
- DB에 시드 데이터가 채워져 있어야 합니다 (`scripts/seed-market.sh`, `scripts/seed-trades.sh`).

## 실행
```bash
cd tools/screenshots
npm install
npx playwright install chromium
npm run shots
```

포트가 다르면 환경변수로 덮어쓸 수 있습니다: `WEB=... API=... npm run shots`.

출력: `docs/screenshots/{market,item-detail,grid-stash,wallet,admin,swagger}.png`
(1440×900, deviceScaleFactor 2, 다크 테마).
