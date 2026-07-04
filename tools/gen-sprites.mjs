// 픽셀 아트 스프라이트 생성기
//   node tools/gen-sprites.mjs
// 각 아이콘을 ASCII 픽셀맵으로 정의 → 16x16 SVG(rect 픽셀)로 렌더링.
// '.' = 투명. 나머지 문자는 icon.colors 매핑. 그리드만 고치면 재생성됨.
//
// 도트 규칙(전 아이콘 공통):
//  - K = 1px 웜블랙 아웃라인 (#17130d)
//  - 면마다 base + highlight + shadow 2~3톤 셰이딩, 광원은 좌상단
//  - 캔버스 16x16을 최대한 채워서 32px에서도 실루엣이 읽히게
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const OUT = join(dirname(fileURLToPath(import.meta.url)), '..', 'assets', 'items');
mkdirSync(OUT, { recursive: true });

const K = '#17130d'; // 공통 아웃라인

const icons = [
  // ---------------- 먹을거 ----------------
  // 통조림: 금속 림 + 빨간 라벨(노란 패널), 원통 하이라이트 좌측
  { name: 'food_can', colors: {
      K, H: '#f4f7f8', M: '#c2ccd3', S: '#7d8891',
      R: '#b53228', r: '#7c1e16', W: '#efe7d2', Y: '#d9a63e',
    }, rows: [
    '....KKKKKKKK....',
    '..KKHHHHHHMMKK..',
    '.KHHMMMMMMMMMSK.',
    '.KMMSSSSSSSSMSK.',
    '.KRRRRRRRRRRrrK.',
    '.KRWWWWWWWWWrrK.',
    '.KRWYYYYYYYWrrK.',
    '.KRWYYYYYYYWrrK.',
    '.KRWWWWWWWWWrrK.',
    '.KRRRRRRRRRRrrK.',
    '.KrrrrrrrrrrrrK.',
    '.KMMMMMMMMMMSSK.',
    '.KHHMMMMMMMSSSK.',
    '..KKMMMMMMSSKK..',
    '....KKKKKKKK....',
    '................' ] },
  // 물병: 파란 캡 + 몸통 스펙큘러 + 라벨 밴드
  { name: 'food_water', colors: {
      K, N: '#2c5f8a', n: '#4e86b2', C: '#3f8cc4', c: '#22587f',
      H: '#83c3e8', W: '#dbf1fc', L: '#d8e1e6', l: '#a9b6bd',
    }, rows: [
    '.....KKKKKK.....',
    '.....KnNNNK.....',
    '.....KnNNNK.....',
    '....KKKKKKKK....',
    '....KCHHHCcK....',
    '...KKCHHHCCKK...',
    '..KCCHWWWHHCcK..',
    '..KCHWWWWWHCcK..',
    '..KCHWWWWWHCcK..',
    '..KLLLLLLLLLlK..',
    '..KLWWWWWWWLlK..',
    '..KLLLLLLLLLlK..',
    '..KCHWWWWWHCcK..',
    '..KCHHWWWHCccK..',
    '..KcCHHHHHCccK..',
    '...KKKKKKKKKK...' ] },
  // 스낵바: 머스터드 포장 + 크림프 끝단 + 빨간 로고
  { name: 'food_snack', colors: {
      K, Y: '#e0b13e', H: '#f2d47e', y: '#a5771f', E: '#8a6420',
      R: '#b53228', W: '#f4ecd9',
    }, rows: [
    '................',
    '................',
    '..KKKKKKKKKKKK..',
    '.KEHHHHHHHHHHEK.',
    '.KEYYYYYYYYYYEK.',
    '.KEYYRRRRRRYYEK.',
    '.KEYRRWWWWRRYEK.',
    '.KEYRWWWWWWRYEK.',
    '.KEYRRWWWWRRYEK.',
    '.KEYYRRRRRRYYEK.',
    '.KEYYYYYYYYYYEK.',
    '.KEyyyyyyyyyyEK.',
    '..KKKKKKKKKKKK..',
    '................',
    '................',
    '................' ] },
  // ---------------- 힐템 ----------------
  // 붕대 롤: 흰 원형 + 적십자, 우하단 그림자
  { name: 'med_bandage', colors: {
      K, W: '#f0efe6', H: '#fbfaf4', S: '#c9c7b6', R: '#c33b2c', r: '#8e2318',
    }, rows: [
    '................',
    '................',
    '.....KKKKKK.....',
    '...KKHHHHHWKK...',
    '..KHHWWWWWWWSK..',
    '.KHHWWWRRWWWWSK.',
    '.KHWWWWRRWWWWSK.',
    'KHWWRRRRRRRRWWSK',
    'KWWWRRRRRRRrWSSK',
    '.KWWWWWRrWWWSSK.',
    '.KWWWWWRrWWSSSK.',
    '..KWWWWWWWWSSK..',
    '...KKWWWWSSKK...',
    '.....KKKKKK.....',
    '................',
    '................' ] },
  // 알약통: 흰 캡 + 앰버 보틀 + 라벨(캡슐 그림)
  { name: 'med_pills', colors: {
      K, W: '#f0efe6', w: '#b9b7a6', A: '#cf7f2a', B: '#e8a555', a: '#96541b',
      L: '#efe6cf', R: '#c33b2c',
    }, rows: [
    '................',
    '....KKKKKKKK....',
    '....KWWWWWwK....',
    '....KWWWWWwK....',
    '....KKKKKKKK....',
    '...KKAAAAAAKK...',
    '..KBAAAAAAAaaK..',
    '..KBLLLLLLLLaK..',
    '..KBLLRRWWLLaK..',
    '..KBLLRRWWLLaK..',
    '..KBLLLLLLLLaK..',
    '..KBAAAAAAAaaK..',
    '..KBAAAAAAaaaK..',
    '..KaaAAAAAaaaK..',
    '...KKKKKKKKKK...',
    '................' ] },
  // 구급상자: 손잡이 + 빨간 케이스 + 흰 십자
  { name: 'med_kit', colors: {
      K, H: '#d4574a', R: '#b53228', r: '#7c1e16', W: '#f2f1ea', w: '#cfcdbf',
    }, rows: [
    '................',
    '................',
    '.....KKKKKK.....',
    '....KK....KK....',
    '...KKKKKKKKKK...',
    '..KHHHHHHHHHrK..',
    '..KHRRRWWRRRrK..',
    '..KHRRRWWRRRrK..',
    '..KHRWWWWWWRrK..',
    '..KHRWWWWwwRrK..',
    '..KHRRRWwRRRrK..',
    '..KHRRRwwRRRrK..',
    '..KrrrrrrrrrrK..',
    '...KKKKKKKKKK...',
    '................',
    '................' ] },
  // ---------------- 근접무기 ----------------
  // 컴뱃 나이프: 대각 블레이드(엣지 하이라이트) + 브라스 가드 + 나무 그립
  { name: 'melee_knife', colors: {
      K, H: '#f2f5f7', S: '#b9c2ca', s: '#808a94', M: '#b08d2a',
      h: '#8a6438', B: '#6b4a2a', b: '#45301b',
    }, rows: [
    '..............K.',
    '.............KHK',
    '............KHSK',
    '...........KHSsK',
    '..........KHSsK.',
    '.........KHSsK..',
    '........KHSsK...',
    '.......KHSsK....',
    '......KHSsK.....',
    '....KMMMMK......',
    '...KMMMMK.......',
    '...KhBbK........',
    '..KhBbK.........',
    '.KhBbK..........',
    '.KbbK...........',
    '..KK............' ] },
  // 야구 배트: 대각, 배럴 두껍고 그립에 테이프
  { name: 'melee_bat', colors: {
      K, W: '#b98a4e', H: '#dbb578', D: '#8a6234', T: '#4a4d52', t: '#6a6e74',
    }, rows: [
    '................',
    '............KKK.',
    '..........KKHWWK',
    '.........KHHWWDK',
    '........KHHWWDK.',
    '.......KHHWWDK..',
    '......KHHWWDK...',
    '.....KHWWDK.....',
    '....KHWWDK......',
    '...KHWDK........',
    '..KHWDK.........',
    '.KTtTK..........',
    'KTtTK...........',
    'KWWK............',
    '.KK.............',
    '................' ] },
  // 도끼: 스틸 헤드(좌측 엣지 하이라이트) + 세로 나무 자루
  { name: 'melee_axe', colors: {
      K, H: '#e8eef2', S: '#aab4bd', s: '#6f7a85',
      W: '#a5713c', h: '#c99457', D: '#5f3d1e',
    }, rows: [
    '................',
    '..KKKK..........',
    '.KHSSSKKKK......',
    '.KHSSSSSSSKKK...',
    'KHHSSSKWWKssK...',
    'KHSSSSKWWKssK...',
    'KKHSSKKWWKKKK...',
    '.KHSK.KWWK......',
    '..KKK.KWhK......',
    '......KWWK......',
    '......KWWK......',
    '......KWhK......',
    '......KWWK......',
    '......KDDK......',
    '......KKKK......',
    '................' ] },
  // ---------------- 총 ----------------
  // 권총: 슬라이드 하이라이트 + 트리거 가드 + 우드 그립
  { name: 'gun_pistol', colors: {
      K, H: '#9aa2ac', S: '#6d7581', s: '#4b525c', G: '#3a3f47',
      B: '#6b4a2a', h: '#8a6438', b: '#45301b',
    }, rows: [
    '................',
    '................',
    '.KK..........KK.',
    '.KKKKKKKKKKKKKK.',
    '.KHHHHHHHHHHHsK.',
    '.KSSSSSSSSSSSsK.',
    '.KKKKKKKKKKKKKK.',
    '.KGGGGGGGGGGGKK.',
    '.KGGGGGKKKKK....',
    '.KBBBbK...KK....',
    '.KBhBbK..KK.....',
    '..KBhBbKKK......',
    '..KBBBbK........',
    '...KKKKK........',
    '................',
    '................' ] },
  // 더블배럴 소드오프: 상하 배럴 + 포엔드 + 우측 그립
  { name: 'gun_shotgun', colors: {
      K, H: '#9aa2ac', S: '#6d7581', s: '#4b525c',
      W: '#8a5c30', h: '#b07c44', w: '#5f3d1e',
    }, rows: [
    '................',
    '................',
    '................',
    '.KKKKKKKKKKKKKK.',
    '.KsHHHHHHHHHHHK.',
    '.KKKKKKKKKKKKKK.',
    '.KsSSSSSSSSSSSK.',
    '.KKKKKKKKKKKKKK.',
    '..KhWWK...KWWWK.',
    '..KKKKK....KWWWK',
    '............KwWK',
    '.............KKK',
    '................',
    '................',
    '................',
    '................' ] },
  // 라이플: 우드 스톡 + 리시버 + 배럴/가늠쇠 + 커브드 탄창
  { name: 'gun_rifle', colors: {
      K, G: '#3f444d', s: '#2b2f36', S: '#6d7581',
      W: '#8a5c30', h: '#b07c44', w: '#5f3d1e', M: '#4b525c',
    }, rows: [
    '................',
    '................',
    '................',
    '....K.......K...',
    'KKKKKKKKKKKK....',
    'KWWhGGGGGGGSSSSK',
    'KwWWGGGGGGGKKKKK',
    'KKKKKGGGGGKK....',
    '.....KWWK.KMMK..',
    '.....KWWK..KMMK.',
    '.....KKKK...KMMK',
    '............KKKK',
    '................',
    '................',
    '................',
    '................' ] },
  // ---------------- 탄약 ----------------
  // 탄약캔: 올리브 드랍 캔 + 손잡이 + 스텐실 밴드
  { name: 'ammo_box', colors: {
      K, O: '#6f7442', H: '#949a5e', o: '#454a26', Y: '#d4af37',
    }, rows: [
    '................',
    '.....KKKKKK.....',
    '....KK....KK....',
    '..KKKKKKKKKKKK..',
    '.KOHHHHHHHHHOoK.',
    '.KOOOOOOOOOOOoK.',
    '.KKKKKKKKKKKKKK.',
    '.KOOOOOOOOOOOoK.',
    '.KOYYOYOYYOYYoK.',
    '.KOOOOOOOOOOOoK.',
    '.KOOOOOOOOOOOoK.',
    '.KooooooooooooK.',
    '.KKKKKKKKKKKKKK.',
    '................',
    '................',
    '................' ] },
  // 샷건 셸: 빨간 헐(좌측 하이라이트) + 브라스 베이스/림
  { name: 'ammo_shell', colors: {
      K, R: '#b53228', h: '#d4685a', r: '#7c1e16',
      B: '#d4af37', L: '#ecd06d', T: '#96741e',
    }, rows: [
    '................',
    '....KKKKKKKK....',
    '...KhRRRRRRrK...',
    '...KhRRRRRRrK...',
    '...KhRrrrrRrK...',
    '...KhRRRRRRrK...',
    '...KhRRRRRRrK...',
    '...KhRRRRRRrK...',
    '...KhRRRRRRrK...',
    '...KKKKKKKKKK...',
    '...KLBBBBBBTK...',
    '...KLBBBBBBTK...',
    '..KLLBBBBBBTTK..',
    '..KKKKKKKKKKKK..',
    '................',
    '................' ] },
  // 병뚜껑(화폐 아이콘): 골드 캡 + 크림 인레이 + 레드 엠블럼
  { name: 'cap_coin', colors: {
      K, L: '#f0d87c', B: '#d4af37', T: '#8f6e1d', W: '#f2ead4', R: '#b53228',
    }, rows: [
    '................',
    '................',
    '....KKKKKKKK....',
    '...KLLLLLLBBK...',
    '..KLBBBBBBBBTK..',
    '.KLBWWWWWWWWBTK.',
    '.KLBWWWRRWWWBTK.',
    '.KLBWWRRRRWWBTK.',
    '.KLBWWRRRRWWBTK.',
    '.KLBWWWRRWWWBTK.',
    '.KLBWWWWWWWWBTK.',
    '..KTBBBBBBBTTK..',
    '...KTTTTTTTTK...',
    '....KKKKKKKK....',
    '................',
    '................' ] },
];

function toSvg(icon) {
  const h = icon.rows.length;
  const w = Math.max(...icon.rows.map(r => r.length));
  if (h !== 16) throw new Error(`[${icon.name}] 행 수는 16이어야 함 (현재 ${h})`);
  const rects = [];
  for (let y = 0; y < h; y++) {
    const row = icon.rows[y];
    if (row.length !== 16) {
      throw new Error(`[${icon.name}] ${y}행 길이 ${row.length} ≠ 16: "${row}"`);
    }
    // 같은 색 연속 픽셀은 하나의 rect로 병합 (파일 크기 절감)
    let x = 0;
    while (x < row.length) {
      const ch = row[x];
      if (ch === '.' || ch === ' ') { x++; continue; }
      const fill = icon.colors[ch];
      if (!fill) throw new Error(`[${icon.name}] 정의되지 않은 문자 '${ch}' (${x},${y})`);
      let run = 1;
      while (x + run < row.length && row[x + run] === ch) run++;
      rects.push(`<rect x="${x}" y="${y}" width="${run}" height="1" fill="${fill}"/>`);
      x += run;
    }
  }
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}" width="${w * 4}" height="${h * 4}" shape-rendering="crispEdges">${rects.join('')}</svg>\n`;
}

const cards = [];
for (const icon of icons) {
  const svg = toSvg(icon);
  writeFileSync(join(OUT, `${icon.name}.svg`), svg);
  cards.push(`<figure><div class="s l">${svg}</div><div class="s m">${svg}</div><figcaption>${icon.name}</figcaption></figure>`);
}

// 로컬에서 열어볼 컨택트 시트 (64px / 32px 두 사이즈로 가독성 확인)
const preview = `<!doctype html><meta charset="utf-8"><title>item sprites</title>
<style>
 body{background:#121009;color:#d8d1bc;font:14px ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;margin:0;padding:28px}
 h1{font-size:15px;font-weight:700;letter-spacing:2px;text-transform:uppercase;color:#d99a33}
 .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:14px;margin-top:20px}
 figure{margin:0;background:#1b1712;border:1px solid #342c1d;border-radius:6px;padding:14px 12px;text-align:center;
   display:flex;flex-direction:column;align-items:center;gap:8px}
 .s{display:flex;align-items:center;justify-content:center;
   background:radial-gradient(circle at 50% 42%,#2b2417,transparent 72%)}
 .s svg{image-rendering:pixelated}
 .s.l svg{width:64px;height:64px}
 .s.m svg{width:32px;height:32px}
 figcaption{font-size:11px;letter-spacing:1px;color:#8f8668}
</style>
<h1>item pixel sprites (${icons.length})</h1>
<div class="grid">${cards.join('')}</div>`;
writeFileSync(join(OUT, '_preview.html'), preview);

console.log(`생성 완료: ${icons.length}개 스프라이트 + _preview.html → assets/items/`);
