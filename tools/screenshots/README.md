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

## 데모 GIF 캡처 (Playwright + ffmpeg)

README 상단의 데모 GIF를 재현합니다. Playwright로 1280×800 화면을 **비디오 녹화**한 뒤
`ffmpeg` 2-pass 팔레트(palettegen/paletteuse)로 최적화 GIF(약 950px, 13fps)로 변환합니다.
`ffmpeg`가 PATH에 있어야 합니다.

```bash
cd tools/screenshots
npm install                # (chromium은 보통 캐시됨; 없으면 npx playwright install chromium)
npm run gifs
```

- `docs/demo-trade.gif` — Survivor_Alpha로 7.62mm 탄약(#95) 상세를 열고 매도 호가를 넘는
  **매수 주문을 체결** → 호가창·최근 체결·캡 잔액이 실시간 갱신. **주의: 실제 주문이 등록되어
  데모 DB가 변경됩니다** (best ask 9의 잔량이 줄어듦 — 반복 시 `scripts/seed-market.sh`로 보충).
- `docs/demo-grid.gif` — Survivor_Bravo로 그리드에서 권총(2×2)을 빈 칸으로 **정상 이동**(초록),
  AK-47(4×2)을 가득 찬 영역으로 끌어 **거부**(빨강 + 원복)되는 흐름.

원본 `.webm`은 OS 임시 폴더에 남으며 커밋하지 않습니다(GIF만 커밋). 포트가 다르면
`WEB=... npm run gifs`로 덮어쓸 수 있습니다.

> 그리드는 네이티브 HTML5 drag-and-drop(dragstart/dragover/drop)을 쓰므로, 스크립트는 공유
> `DataTransfer`로 DnD 이벤트를 직접 디스패치하면서 실제 커서도 함께 움직여 녹화가 자연스럽게
> 읽히도록 합니다.
