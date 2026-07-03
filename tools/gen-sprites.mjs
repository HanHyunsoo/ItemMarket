// 픽셀 아트 스프라이트 생성기
//   node tools/gen-sprites.mjs
// 각 아이콘을 ASCII 픽셀맵으로 정의 → 16x16 SVG(rect 픽셀)로 렌더링.
// '.' = 투명. 나머지 문자는 icon.colors 매핑. 그리드만 고치면 재생성됨.
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const OUT = join(dirname(fileURLToPath(import.meta.url)), '..', 'assets', 'items');
mkdirSync(OUT, { recursive: true });

const icons = [
  // ---------------- 먹을거 ----------------
  { name: 'food_can', colors: { K:'#1c1f24', H:'#eef3f6', M:'#c7d0d8', R:'#b5372e', Y:'#e8c874' }, rows: [
    '................',
    '...KKKKKKKKKK...',
    '...KHHHHHHHHK...',
    '...KMHMMMMMHK...',
    '...KMMMMMMMMK...',
    '...KRRRRRRRRK...',
    '...KRYRRRRYRK...',
    '...KRRRRRRRRK...',
    '...KMMMMMMMMK...',
    '...KRRRRRRRRK...',
    '...KRYRRRRYRK...',
    '...KRRRRRRRRK...',
    '...KMMMMMMMMK...',
    '...KHMMMMMMHK...',
    '...KKKKKKKKKK...',
    '................' ] },
  { name: 'food_water', colors: { K:'#1c1f24', N:'#2b6f9e', C:'#3aa0d8', H:'#bfe6f7', W:'#eaf6fc' }, rows: [
    '................',
    '......KKKK......',
    '......KNNK......',
    '......KNNK......',
    '.....KKKKKK.....',
    '.....KCHHCK.....',
    '....KKKKKKKK....',
    '....KCHHHHCK....',
    '....KCHWHHCK....',
    '....KCHHHHCK....',
    '....KCHHHHCK....',
    '....KCHWHHCK....',
    '....KCHHHHCK....',
    '....KCHHHHCK....',
    '....KKKKKKKK....',
    '................' ] },
  { name: 'food_snack', colors: { K:'#1c1f24', D:'#6b4423', Y:'#f0d38a', W:'#c98a3a' }, rows: [
    '................',
    '................',
    '..KKKKKKKKKKKK..',
    '..KDDDDDDDDDDK..',
    '..KDYYYYYYYYDK..',
    '..KDYWWWWWWYDK..',
    '..KDYWDDDDWYDK..',
    '..KDYWDDDDWYDK..',
    '..KDYWWWWWWYDK..',
    '..KDYYYYYYYYDK..',
    '..KDDDDDDDDDDK..',
    '..KKKKKKKKKKKK..',
    '................',
    '................',
    '................',
    '................' ] },
  // ---------------- 힐템 ----------------
  { name: 'med_bandage', colors: { K:'#1c1f24', W:'#f0f0ec', R:'#c0392b' }, rows: [
    '................',
    '.....KKKKKK.....',
    '...KKWWWWWWKK...',
    '..KWWWWWWWWWWK..',
    '..KWWWWRRWWWWK..',
    '.KWWWWWRRWWWWWK.',
    '.KWWWRRRRRRWWWK.',
    '.KWWWRRRRRRWWWK.',
    '.KWWWWWRRWWWWWK.',
    '..KWWWWRRWWWWK..',
    '..KWWWWWWWWWWK..',
    '...KKWWWWWWKK...',
    '.....KKKKKK.....',
    '................',
    '................',
    '................' ] },
  { name: 'med_pills', colors: { K:'#1c1f24', W:'#eeeeee', A:'#d98a2b', H:'#f0b968', R:'#c0392b' }, rows: [
    '................',
    '.....KKKKKK.....',
    '.....KWWWWK.....',
    '.....KWWWWK.....',
    '....KKKKKKKK....',
    '....KAAAAAAK....',
    '....KAHHHHAK....',
    '....KARRRRAK....',
    '....KARRRRAK....',
    '....KAHHHHAK....',
    '....KAAAAAAK....',
    '....KAAAAAAK....',
    '....KKKKKKKK....',
    '................',
    '................',
    '................' ] },
  { name: 'med_kit', colors: { K:'#1c1f24', W:'#eeeeee', R:'#c0392b' }, rows: [
    '................',
    '......KKKK......',
    '.....KK..KK.....',
    '....KKKKKKKK....',
    '...KRRRRRRRRK...',
    '...KRRRWWRRRK...',
    '...KRRRWWRRRK...',
    '...KRWWWWWWRK...',
    '...KRWWWWWWRK...',
    '...KRRRWWRRRK...',
    '...KRRRWWRRRK...',
    '...KRRRRRRRRK...',
    '....KKKKKKKK....',
    '................',
    '................',
    '................' ] },
  // ---------------- 근접무기 ----------------
  { name: 'melee_knife', colors: { K:'#1c1f24', S:'#c7ced6', H:'#eef2f5', B:'#5a3b23', G:'#3a2716' }, rows: [
    '................',
    '.............KK.',
    '............KSHK',
    '...........KSHK.',
    '..........KSHK..',
    '.........KSHK...',
    '........KSHK....',
    '.......KSHK.....',
    '......KSHK......',
    '.....KSHK.......',
    '....KGGK........',
    '...KBBK.........',
    '..KBBK..........',
    '..KBK...........',
    '................',
    '................' ] },
  { name: 'melee_bat', colors: { K:'#1c1f24', W:'#c99a5b', H:'#e0bd83', D:'#8a6636' }, rows: [
    '................',
    '............KKK.',
    '...........KWWK.',
    '..........KWWWK.',
    '.........KWHWWK.',
    '........KWHWWK..',
    '.......KWHWK....',
    '......KWHWK.....',
    '.....KWHWK......',
    '....KWHWK.......',
    '...KWHWK........',
    '..KDWK..........',
    '..KDK...........',
    '.KKK............',
    '................',
    '................' ] },
  { name: 'melee_axe', colors: { K:'#1c1f24', S:'#c7ced6', H:'#eef2f5', W:'#8a6636' }, rows: [
    '................',
    '.....KKKKK......',
    '....KSSSSSK.....',
    '...KSHHSSSK.....',
    '...KSHHSSSK.....',
    '....KSSSSSK.....',
    '.....KWKKK......',
    '......KWK.......',
    '......KWK.......',
    '......KWK.......',
    '......KWK.......',
    '......KWK.......',
    '......KWK.......',
    '......KKK.......',
    '................',
    '................' ] },
  // ---------------- 총 ----------------
  { name: 'gun_pistol', colors: { K:'#1c1f24', H:'#6b7079', G:'#4a4f57', B:'#5a3b23' }, rows: [
    '................',
    '................',
    '..KKKKKKKKKKK...',
    '..KHHHHHHHHHK...',
    '..KGGGGGGGGGK...',
    '..KGGGKKKKKKK...',
    '..KKGKK.........',
    '...KGGK.........',
    '...KGBK.........',
    '...KBBK.........',
    '...KBBK.........',
    '....KBBK........',
    '....KBK.........',
    '.....K..........',
    '................',
    '................' ] },
  { name: 'gun_shotgun', colors: { K:'#1c1f24', G:'#4a4f57', H:'#6b7079', W:'#8a6636' }, rows: [
    '................',
    '................',
    '................',
    '.KKKKKKKKKKKKK..',
    '.KGGGGGGGWWWWK..',
    '.KHHGGGGGWWWWK..',
    '.KKKKKKKKKWWWK..',
    '....KKKK..KKKK..',
    '....KWWK.......',
    '....KKK........',
    '................',
    '................',
    '................',
    '................',
    '................',
    '................' ] },
  { name: 'gun_rifle', colors: { K:'#1c1f24', G:'#4a4f57', H:'#6b7079', M:'#2b2e33' }, rows: [
    '................',
    '................',
    '................',
    '.KKKKKKKKKKKKKK.',
    '.KGGGGGGGGGGGGK.',
    '.KHHGGGGGGGGGGK.',
    '.KKKKKGGGKKKKKK.',
    '...KKKKMKKK.....',
    '....KGKMK.......',
    '......KMK.......',
    '......KMK.......',
    '......KKK.......',
    '................',
    '................',
    '................',
    '................' ] },
  // ---------------- 탄약 ----------------
  { name: 'ammo_box', colors: { K:'#1c1f24', B:'#d4af37', C:'#8a6636', D:'#5a3b23' }, rows: [
    '................',
    '...K.K.K.K......',
    '..KBKBKBKBK.....',
    '..KBKBKBKBK.....',
    '.KKKKKKKKKKKK...',
    '.KCCCCCCCCCCK...',
    '.KCDDDDDDDDCK...',
    '.KCDDDDDDDDCK...',
    '.KCDDDDDDDDCK...',
    '.KCDDDDDDDDCK...',
    '.KCCCCCCCCCCK...',
    '.KKKKKKKKKKKK...',
    '................',
    '................',
    '................',
    '................' ] },
  { name: 'ammo_shell', colors: { K:'#1c1f24', R:'#b5372e', H:'#d9695f', B:'#d4af37', T:'#b08d2a' }, rows: [
    '................',
    '...KK....KK.....',
    '..KRRK..KRRK....',
    '..KRHK..KRHK....',
    '..KRRK..KRRK....',
    '..KRRK..KRRK....',
    '..KRRK..KRRK....',
    '..KRRK..KRRK....',
    '..KBBK..KBBK....',
    '..KBBK..KBBK....',
    '..KTTK..KTTK....',
    '..KKKK..KKKK....',
    '................',
    '................',
    '................',
    '................' ] },
];

function toSvg(icon) {
  const h = icon.rows.length;
  const w = Math.max(...icon.rows.map(r => r.length));
  const rects = [];
  for (let y = 0; y < h; y++) {
    const row = icon.rows[y];
    for (let x = 0; x < row.length; x++) {
      const ch = row[x];
      if (ch === '.' || ch === ' ') continue;
      const fill = icon.colors[ch];
      if (!fill) throw new Error(`[${icon.name}] 정의되지 않은 문자 '${ch}' (${x},${y})`);
      rects.push(`<rect x="${x}" y="${y}" width="1" height="1" fill="${fill}"/>`);
    }
  }
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}" width="${w * 4}" height="${h * 4}" shape-rendering="crispEdges">${rects.join('')}</svg>\n`;
}

const cards = [];
for (const icon of icons) {
  const svg = toSvg(icon);
  writeFileSync(join(OUT, `${icon.name}.svg`), svg);
  cards.push(`<figure><div class="s">${svg}</div><figcaption>${icon.name}</figcaption></figure>`);
}

// 로컬에서 열어볼 컨택트 시트
const preview = `<!doctype html><meta charset="utf-8"><title>item sprites</title>
<style>
 body{background:#14171c;color:#cbd3dc;font:14px system-ui;margin:0;padding:24px}
 h1{font-size:16px;font-weight:600}
 .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(120px,1fr));gap:16px;margin-top:16px}
 figure{margin:0;background:#1c2128;border:1px solid #2b323c;border-radius:10px;padding:12px;text-align:center}
 .s svg{width:64px;height:64px;image-rendering:pixelated}
 figcaption{margin-top:8px;font-size:12px;color:#8b95a1}
</style>
<h1>아이템 픽셀 스프라이트 (${icons.length}종)</h1>
<div class="grid">${cards.join('')}</div>`;
writeFileSync(join(OUT, '_preview.html'), preview);

console.log(`생성 완료: ${icons.length}개 스프라이트 + _preview.html → assets/items/`);
