// 픽셀 아트 스프라이트 생성기 (베이스 실루엣 + 코드별 절차적 색상 변주)
//   node tools/gen-sprites.mjs
//
// 구조:
//  1) BASES  : 카테고리별 16x16 ASCII 픽셀맵 실루엣 라이브러리.
//              각 베이스는 { colors, recolor, rows }.
//              - colors  : 문자 → hex.  '.'/' ' 은 투명.
//              - recolor : 아이템 "정체성" 색 슬롯(라벨/패널 등). 여기 있는 슬롯만
//                          코드별로 색조(hue)를 회전한다. 아웃라인 K·중립 금속은 제외.
//              - rows    : 16행 x 16열 픽셀맵.
//  2) hueFor : 아이템 code 해시 → 잘 분리된 큐레이트 hue 세트에서 하나.
//              recolor 슬롯들의 상대 색조차·명도(L)·채도(S)는 유지하고 전체를 delta 만큼 회전.
//  3) CATALOG: db/ddl.sql 시드와 1:1로 맞춘 (code → base) 매핑. 아이템마다 code.svg 1장.
//
// 도트 규칙(전 베이스 공통):
//  - K = 1px 웜블랙 아웃라인 (#17130d)
//  - 면마다 base + highlight + shadow 2~3톤 셰이딩, 광원은 좌상단
//  - 캔버스 16x16을 최대한 채워서 32px에서도 실루엣이 읽히게
import { writeFileSync, mkdirSync, readdirSync, rmSync, copyFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const OUT = join(ROOT, 'assets', 'items');
const WEB = join(ROOT, 'web', 'public', 'sprites');
mkdirSync(OUT, { recursive: true });
mkdirSync(WEB, { recursive: true });

const K = '#17130d'; // 공통 아웃라인

// ============================================================================
//  BASES : 실루엣 라이브러리
// ============================================================================
const BASES = {
  // ---------------- 먹을거 ----------------
  can: { recolor: ['R', 'r', 'Y'], colors: {
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
  water: { recolor: ['N', 'n', 'C', 'c'], colors: {
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
  snack: { recolor: ['Y', 'H', 'y', 'E'], colors: {
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
  jar: { recolor: ['C', 'c'], colors: {
      K, L: '#d8d8cc', l: '#a0a094', G: '#9cc4cf', H: '#cfe8ee',
      C: '#b5462f', c: '#7c2a1a',
    }, rows: [
    '................',
    '....KKKKKKKK....',
    '...KLLLLLLLlK...',
    '...KlllllllLK...',
    '..KKKKKKKKKKKK..',
    '..KGHCCCCCCcGK..',
    '..KGHCCCCCCcGK..',
    '..KGHCCCCCCcGK..',
    '..KGHCCCCCCcGK..',
    '..KGHCCCCCCcGK..',
    '..KGHCCCCCCcGK..',
    '..KGCCCCCCCcGK..',
    '..KGcCCCCCcccK..',
    '..KKKKKKKKKKKK..',
    '................',
    '................' ] },
  ration: { recolor: ['P', 'T', 'H'], colors: {
      K, T: '#8a7a4a', P: '#b3a06a', H: '#cbb982',
      L: '#c9bf8f', W: '#f0ead4', w: '#8a7f60',
    }, rows: [
    '................',
    '..KKKKKKKKKKKK..',
    '.KTTTTTTTTTTTTK.',
    '.KPPPPPPPPPPPPK.',
    '.KPHHHHHHHHHHPK.',
    '.KPHLLLLLLLLHPK.',
    '.KPHLWWWWWWLHPK.',
    '.KPHLWwwwwwLHPK.',
    '.KPHLWWWWWWLHPK.',
    '.KPHLLLLLLLLHPK.',
    '.KPHHHHHHHHHHPK.',
    '.KPPPPPPPPPPPPK.',
    '.KTTTTTTTTTTTTK.',
    '..KKKKKKKKKKKK..',
    '................',
    '................' ] },
  produce: { recolor: ['F', 'H', 's'], colors: {
      K, D: '#5f3d1e', G: '#4f8a3a', F: '#c0392b', H: '#e05c48', s: '#8a2418',
    }, rows: [
    '................',
    '.......KK.......',
    '......KDDK......',
    '....KKKDGGK.....',
    '...KHHFFFKKK....',
    '..KHHFFFFFFFK...',
    '.KHFFFFFFFFFsK..',
    '.KHFFFFFFFFFsK..',
    '.KFFFFFFFFFFsK..',
    '.KFFFFFFFFFFsK..',
    '.KsFFFFFFFFssK..',
    '..KsFFFFFFssK...',
    '..KKsFFFsssK....',
    '....KKKKKKK.....',
    '................',
    '................' ] },
  // ---------------- 힐템 ----------------
  bandage: { recolor: ['R', 'r'], colors: {
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
  pills: { recolor: ['A', 'B', 'a'], colors: {
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
  medkit: { recolor: ['H', 'R', 'r'], colors: {
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
  syringe: { recolor: ['L', 'e'], colors: {
      K, P: '#c2ccd3', R: '#7d8891', G: '#a9cdd8', H: '#d6ecf2',
      L: '#c94f8a', e: '#8f2f62', N: '#c2ccd3', k: '#4b525c',
    }, rows: [
    '.....KKKK.......',
    '.....KPPK.......',
    '......KK........',
    '......RK........',
    '.....KKKK.......',
    '....KKGGKK......',
    '....KGLLGK......',
    '....KHLLGK......',
    '....KHLeGK......',
    '....KGLeGK......',
    '....KGeeGK......',
    '....KGeeGK......',
    '....KKGGKK......',
    '......KK........',
    '......Nk........',
    '.......N........' ] },
  spray: { recolor: ['B', 'C', 'c'], colors: {
      K, N: '#3a3f47', C: '#5aa0d0', B: '#3f8cc4', c: '#22587f', L: '#f0ead4',
    }, rows: [
    '......KKK.......',
    '.....KNNNK......',
    '.....KNNNK......',
    '....KKKKKKK.....',
    '....KCBBBBcK....',
    '....KCBBBBcK....',
    '....KCBLLBcK....',
    '....KCBLLBcK....',
    '....KCBLLBcK....',
    '....KCBBBBcK....',
    '....KCBBBBcK....',
    '....KCBBBBcK....',
    '....KCBBBBcK....',
    '....KKKKKKK.....',
    '................',
    '................' ] },
  bloodbag: { recolor: ['R', 'r'], colors: {
      K, H: '#c2ccd3', W: '#e6e0d0', R: '#b53228', r: '#7c1e16', t: '#8e2318',
    }, rows: [
    '.....KKKK.......',
    '.....KHHK.......',
    '....KKKKKK......',
    '...KWWWWWWWK....',
    '..KWRRRRRRRWK...',
    '..KWRRRRRRRWK...',
    '..KWRRRRRRRWK...',
    '..KWRRRRRRRWK...',
    '..KWRRRRRRRWK...',
    '..KWRRRRRRRWK...',
    '..KWRRRRRRRWK...',
    '...KWRRRRRWK....',
    '....KWRRRWK.....',
    '.....KWtWK......',
    '......KtK.......',
    '.......K........' ] },
  // ---------------- 근접무기 ----------------
  knife: { recolor: ['B', 'h', 'b'], colors: {
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
  bat: { recolor: ['W', 'H', 'D'], colors: {
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
  axe: { recolor: ['W', 'h', 'D'], colors: {
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
  machete: { recolor: ['G', 'B'], colors: {
      K, H: '#f2f5f7', S: '#aab4bd', M: '#4b525c', G: '#8a6438', B: '#5f3d1e',
    }, rows: [
    '..............K.',
    '............KKHK',
    '...........KHHSK',
    '..........KHHSK.',
    '.........KHHSK..',
    '........KHHSK...',
    '.......KHHSK....',
    '......KHHSK.....',
    '.....KHHSK......',
    '....KHHSK.......',
    '...KMMMK........',
    '..KGBBK.........',
    '..KGBBK.........',
    '.KGBBK..........',
    '.KGBK...........',
    '..KK............' ] },
  blunt: { recolor: ['H', 'M', 'G'], colors: {
      K, H: '#b9c2ca', M: '#6d7581', G: '#8a949e',
    }, rows: [
    '.....KKK........',
    '....KHHKK.......',
    '...KHHMHK.......',
    '...KHMMHK.......',
    '...KKMMKK.......',
    '....KMMK........',
    '....KGMK........',
    '....KGMK........',
    '....KGMK........',
    '....KGMK........',
    '....KGMK........',
    '....KGMK........',
    '....KGMK........',
    '....KHMK........',
    '....KKKK........',
    '................' ] },
  spear: { recolor: ['W', 'D'], colors: {
      K, H: '#e8eef2', S: '#aab4bd', W: '#8a5c30', D: '#5f3d1e',
    }, rows: [
    '......KK........',
    '.....KHHK.......',
    '.....KHSK.......',
    '....KHHSSK......',
    '....KHHSSK......',
    '.....KHSK.......',
    '.....KWWK.......',
    '.....KWWK.......',
    '.....KWWK.......',
    '.....KWWK.......',
    '.....KWWK.......',
    '.....KWWK.......',
    '.....KWWK.......',
    '.....KWWK.......',
    '.....KDDK.......',
    '.....KKKK.......' ] },
  // ---------------- 총 ----------------
  pistol: { recolor: ['B', 'h', 'b'], colors: {
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
  shotgun: { recolor: ['W', 'h', 'w'], colors: {
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
  rifle: { recolor: ['W', 'h', 'w'], colors: {
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
  revolver: { recolor: ['B', 'G'], colors: {
      K, H: '#9aa2ac', S: '#6d7581', s: '#4b525c',
      C: '#7d8891', c: '#4b525c', G: '#8a6438', B: '#45301b',
    }, rows: [
    '................',
    '................',
    '..KKKKKKKKKKK...',
    '.KHHHHHHHHHHSK..',
    '.KSSSSSSSSSSsK..',
    '.KKKKKKKKKKKK...',
    '..KKCCCCKK......',
    '.KGCcCCcCK......',
    '.KGCCCCCCK......',
    '.KGGKKKKK.......',
    '.KGBBBK.........',
    '..KGBBBK........',
    '..KGBBK.........',
    '...KKK..........',
    '................',
    '................' ] },
  smg: { recolor: ['G', 'H', 'S'], colors: {
      K, G: '#4a505a', H: '#6d7581', S: '#363b43', s: '#2b2f36', M: '#4b525c',
    }, rows: [
    '................',
    '................',
    '..KKKKKKKKKKKK..',
    '.KGGGGGGGGGGGGK.',
    '.KHGGGGGGGGGSsK.',
    '.KKKKGGGGKKKKKK.',
    '...KGK..KGK.....',
    '...KGK..KGK.....',
    '...KKK..KMK.....',
    '........KMK.....',
    '........KMK.....',
    '........KMK.....',
    '........KKK.....',
    '................',
    '................',
    '................' ] },
  bow: { recolor: ['W', 'w'], colors: {
      K, L: '#6d7581', S: '#4b525c', W: '#8a5c30', w: '#5f3d1e', T: '#2b2f36',
    }, rows: [
    '................',
    '.K............K.',
    '.KL..........LK.',
    '..KL........LK..',
    '...KL......LK...',
    '....KLLLLLLLK...',
    '.....KSSSSK.....',
    '....KKWWWWKK....',
    '...KWWWWWWWWK...',
    '...KWWTTTTWWK...',
    '....KWWWWWWK....',
    '.....KWWWWK.....',
    '......KWWK......',
    '......KwwK......',
    '......KKKK......',
    '................' ] },
  launcher: { recolor: ['G', 'g'], colors: {
      K, H: '#6d7581', G: '#4a5a3a', g: '#33402a', T: '#2b2f36', S: '#3a3f47',
    }, rows: [
    '................',
    '................',
    '.KKKKKKKKKKKK...',
    'KHHHHHHHHHHHTK..',
    'KTGGGGGGGGGGTK..',
    'KTGGGGGGGGGGTK..',
    'KTGGGGGGGGGGTK..',
    'KHggggggggggHK..',
    'KKKKKGGKKKKKK...',
    '....KGGK........',
    '...KSGGSK.......',
    '...KGGGGK.......',
    '...KKGGKK.......',
    '....KGGK........',
    '....KKKK........',
    '................' ] },
  // ---------------- 탄약 ----------------
  box: { recolor: ['O', 'H', 'o'], colors: {
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
  shell: { recolor: ['R', 'h', 'r'], colors: {
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
  round: { recolor: ['B', 'L', 'r'], colors: {
      K, P: '#b5723a', p: '#8a4f24', B: '#d4af37', L: '#ecd06d', r: '#96741e',
    }, rows: [
    '................',
    '.....KK.KK......',
    '....KpK.KpK.....',
    '....KPK.KPK.....',
    '...KKKK.KKKK....',
    '...KLBK.KLBK....',
    '...KLBK.KLBK....',
    '...KLBK.KLBK....',
    '...KLBK.KLBK....',
    '...KLBK.KLBK....',
    '...KLBK.KLBK....',
    '...KrrK.KrrK....',
    '...KKKK.KKKK....',
    '................',
    '................',
    '................' ] },
  arrow: { recolor: ['F', 'f'], colors: {
      K, P: '#9aa2ac', S: '#6d7581', W: '#8a5c30', F: '#c33b2c', f: '#8e2318',
    }, rows: [
    '.......K........',
    '......KPK.......',
    '......KPK.......',
    '.......SK.......',
    '.......WK.......',
    '.......WK.......',
    '.......WK.......',
    '.......WK.......',
    '.......WK.......',
    '.......WK.......',
    '......FWfK......',
    '.....FFWffK.....',
    '.....FFWffK.....',
    '......FWfK......',
    '.......WK.......',
    '................' ] },
  bolt: { recolor: ['V', 'v'], colors: {
      K, B: '#aab4bd', S: '#6d7581', W: '#4b525c', V: '#3f8cc4', v: '#22587f',
    }, rows: [
    '.......K........',
    '......KBK.......',
    '.....KBBBK......',
    '......KBK.......',
    '.......SK.......',
    '.......WK.......',
    '.......WK.......',
    '.......WK.......',
    '.......WK.......',
    '.......WK.......',
    '.......WK.......',
    '.......WK.......',
    '......VWvK......',
    '......VWvK......',
    '.......WK.......',
    '................' ] },
  flare: { recolor: ['B', 'b'], colors: {
      K, Y: '#f2d47e', y: '#e0872f', C: '#c2ccd3', B: '#b53228', b: '#7c1e16', W: '#f0ead4',
    }, rows: [
    '.......K........',
    '......KYK.......',
    '......KyK.......',
    '.....KYYYK......',
    '......KCK.......',
    '.....KKCKK......',
    '.....KBBbK......',
    '.....KBWbK......',
    '.....KBWbK......',
    '.....KBBbK......',
    '.....KBWbK......',
    '.....KBWbK......',
    '.....KBBbK......',
    '.....KBBbK......',
    '.....KKKKK......',
    '................' ] },
  // ---------------- 장비/컨테이너 ----------------
  helmet: { recolor: ['H', 'M', 'S', 'G'], colors: {
      K, H: '#7d8a6b', M: '#5f6b4f', S: '#454e37', V: '#2b3120', G: '#9aa886',
    }, rows: [
    '................',
    '.....KKKKKK.....',
    '...KKGGGGHHKK...',
    '..KGGHHHHHMMSK..',
    '.KGHHHHHMMMMSSK.',
    '.KGHHHMMMMMMSSK.',
    '.KHHMMMMMMMMSSK.',
    '.KMMMMMMMMMMSSK.',
    '.KKKKKKKKKKKKKK.',
    '.KVVVVVVVVVVVVK.',
    '.KKKKKKKKKKKKKK.',
    '..KMMSK..KMSSK..',
    '..KKKK....KKK...',
    '................',
    '................',
    '................' ] },
  helmet_heavy: { recolor: ['H', 'M', 'S', 'G'], colors: {
      K, H: '#7d8a6b', M: '#5f6b4f', S: '#454e37', V: '#2b3120', G: '#9aa886',
    }, rows: [
    '................',
    '.....KKKKKK.....',
    '...KKHHHHHHKK...',
    '..KHHHHHHHMMSK..',
    '.KHHHHHHHMMMSSK.',
    '.KHHHHHMMMMMSSK.',
    '.KHMMMMMMMMMSSK.',
    '.KMMMMMMMMMMSSK.',
    '.KMKKKKKKKKMMSK.',
    '.KMVVVVVVVVMMSK.',
    '.KMKKKKKKKKMMSK.',
    '.KMMMMMMMMMMSSK.',
    '..KMMMMMMMMSSK..',
    '...KKMMMMSSKK...',
    '.....KKKKKK.....',
    '................' ] },
  armor: { recolor: ['H', 'M', 'S', 'P'], colors: {
      K, H: '#6a7383', M: '#4c5462', S: '#343a45', P: '#8791a1', Z: '#252a32',
    }, rows: [
    '................',
    '..KKK....KKK....',
    '.KHHKK..KKHHK...',
    '.KHMMKKKKMMHK...',
    'KKHMMMMMMMMHKK..',
    'KHHMMMMZMMMMHSK.',
    'KHMMMMMZMMMMMSK.',
    'KHMMMPPZPPMMMSK.',
    'KHMMMPPZPPMMMSK.',
    'KHMMMMMZMMMMMSK.',
    'KHMMMMMZMMMMMSK.',
    'KHMMMMMZMMMMMSK.',
    'KKMMMMMMMMMMMSK.',
    '.KKMMMMMMMMMSKK.',
    '..KKKKKKKKKKKK..',
    '................' ] },
  armor_plate: { recolor: ['H', 'M', 'P'], colors: {
      K, H: '#6a7383', M: '#4c5462', S: '#343a45', P: '#8791a1',
    }, rows: [
    '................',
    '..KKK....KKK....',
    '.KHHKK..KKHHK...',
    '.KHMMKKKKMMHK...',
    'KKHMMMMMMMMHKK..',
    'KHHMMMMMMMMMHSK.',
    'KHMMKKKKKKMMMSK.',
    'KHMKPPPPPPKMMSK.',
    'KHMKPPPPPPKMMSK.',
    'KHMKPPPPPPKMMSK.',
    'KHMKPPPPPPKMMSK.',
    'KHMKKKKKKKKMMSK.',
    'KKMMMMMMMMMMMSK.',
    '.KKMMMMMMMMMSKK.',
    '..KKKKKKKKKKKK..',
    '................' ] },
  rig: { recolor: ['H', 'M', 'S', 'P'], colors: {
      K, H: '#8a6f3e', M: '#6b5530', S: '#4a3a20', P: '#a88a4e', B: '#2f2614',
    }, rows: [
    '................',
    '..KK......KK....',
    '.KHKK....KKHK...',
    'KKHMKKKKKKMHKK..',
    'KHHMMMMMMMMMHSK.',
    'KHMMMMMMMMMMMSK.',
    'KHMKKPKKPKKMMSK.',
    'KHMKPPKPPKKMMSK.',
    'KHMKPPKPPKKMMSK.',
    'KHMKKKKKKKKMMSK.',
    'KHMKPKKPKKPMMSK.',
    'KHMKPKKPKKPMMSK.',
    'KKMKKKKKKKKMMSK.',
    '.KKMMMMMMMMMSKK.',
    '..KKKKKKKKKKKK..',
    '................' ] },
  backpack: { recolor: ['H', 'M', 'S', 'P'], colors: {
      K, H: '#4f7a5a', M: '#3a5c43', S: '#274030', P: '#6a9a76', B: '#1d2f22',
    }, rows: [
    '................',
    '.....KKKK.......',
    '....KHHHHK......',
    '...KKKKKKKK.....',
    '..KHHHHHHHMK....',
    '.KHHHHHHHHMSK...',
    '.KHPPPPPPPMSK...',
    '.KHPMMMMMPMSK...',
    '.KHPMMMMMPMSK...',
    '.KHPMMMMMPMSK...',
    '.KHPMMMMMPMSK...',
    '.KHPPPPPPPMSK...',
    '.KHMMMMMMMMSK...',
    '.KSMBBBBBBSSK...',
    '..KKKKKKKKKK....',
    '................' ] },
  duffel: { recolor: ['H', 'M', 'S'], colors: {
      K, H: '#4f7a5a', M: '#3a5c43', S: '#274030', Z: '#1d2f22',
    }, rows: [
    '................',
    '......KKKK......',
    '.....KKKKKK.....',
    '.....KKKKKK.....',
    '..KKKKKKKKKKKK..',
    '.KHHHHHHHHHHHSK.',
    'KHHHHHHHHHHHHHSK',
    'KHMMMMMMMMMMMMSK',
    'KHZZZZZZZZZZZZSK',
    'KHMMMMMMMMMMMMSK',
    'KHMMMMMMMMMMMMSK',
    '.KMMMMMMMMMMMSK.',
    '..KKMMMMMMKKKK..',
    '....KKKKKKKK....',
    '................',
    '................' ] },
  // 병뚜껑(화폐 아이콘) - hue 변주 없음
  coin: { recolor: [], colors: {
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
};

// 레거시 파일명(웹 UI에 하드코딩된 참조) → 기본 색(색조 변주 없음)으로도 출력한다.
const LEGACY_ALIAS = {
  can: 'food_can', water: 'food_water', snack: 'food_snack',
  bandage: 'med_bandage', pills: 'med_pills', medkit: 'med_kit',
  knife: 'melee_knife', bat: 'melee_bat', axe: 'melee_axe',
  pistol: 'gun_pistol', shotgun: 'gun_shotgun', rifle: 'gun_rifle',
  box: 'ammo_box', shell: 'ammo_shell',
  helmet: 'equip_helmet', armor: 'equip_armor', rig: 'equip_rig', backpack: 'equip_backpack',
  coin: 'cap_coin',
};

// ============================================================================
//  CATALOG : code → base  (db/ddl.sql 시드와 1:1)
// ============================================================================
const CATALOG = [
  // FOOD 1-30
  ['canned_beans','can'],['canned_spam','can'],['canned_tuna','can'],['canned_corn','can'],
  ['canned_peaches','can'],['canned_soup','can'],['crackers','snack'],['beef_jerky','snack'],
  ['energy_bar','snack'],['chocolate','snack'],['instant_noodle','snack'],['rice_bag','ration'],
  ['dog_food','can'],['cat_food','can'],['water_bottle','water'],['canteen','water'],
  ['soda_can','water'],['energy_drink','water'],['instant_coffee','jar'],['dried_fruit','snack'],
  ['peanut_butter','jar'],['honey_jar','jar'],['pickled_veggies','jar'],['hardtack','snack'],
  ['mre_ration','ration'],['candy','snack'],['potato_chips','snack'],['powdered_milk','ration'],
  ['salt','snack'],['sugar','snack'],
  // MEDICAL 31-52
  ['bandage','bandage'],['clean_bandage','bandage'],['gauze','bandage'],['painkillers','pills'],
  ['antibiotics','pills'],['disinfectant','spray'],['splint','bandage'],['tourniquet','bandage'],
  ['medkit','medkit'],['surgical_kit','medkit'],['morphine','syringe'],['adrenaline','syringe'],
  ['antiseptic_wipes','bandage'],['stitch_kit','medkit'],['vitamins','pills'],['antidote','syringe'],
  ['blood_bag','bloodbag'],['ice_pack','bandage'],['burn_cream','spray'],['eye_drops','spray'],
  ['fever_reducer','pills'],['iodine','spray'],
  // MELEE 53-73
  ['kitchen_knife','knife'],['combat_knife','knife'],['machete','machete'],['baseball_bat','bat'],
  ['nail_bat','bat'],['crowbar','blunt'],['fire_axe','axe'],['hatchet','axe'],
  ['sledgehammer','blunt'],['pipe_wrench','blunt'],['katana','machete'],['machinist_hammer','blunt'],
  ['shovel','spear'],['pitchfork','spear'],['chainsaw','axe'],['cleaver','knife'],
  ['police_baton','bat'],['spiked_mace','blunt'],['wooden_spear','spear'],['scythe','machete'],
  ['brass_knuckles','knife'],
  // GUN 74-92
  ['makarov_pistol','pistol'],['glock_pistol','pistol'],['revolver','revolver'],['desert_eagle','pistol'],
  ['sawed_shotgun','shotgun'],['pump_shotgun','shotgun'],['double_shotgun','shotgun'],['uzi_smg','smg'],
  ['mp5_smg','smg'],['ak47_rifle','rifle'],['m4_rifle','rifle'],['hunting_rifle','rifle'],
  ['sniper_rifle','rifle'],['lever_rifle','rifle'],['flare_gun','pistol'],['nail_gun','pistol'],
  ['crossbow','bow'],['compound_bow','bow'],['grenade_launcher','launcher'],
  // AMMO 93-102
  ['ammo_9mm','box'],['ammo_45acp','box'],['ammo_762','box'],['ammo_556','box'],
  ['ammo_12gauge','shell'],['ammo_308','round'],['ammo_357','round'],['ammo_bolt','bolt'],
  ['ammo_arrow','arrow'],['ammo_flare','flare'],
  // GEAR 103-106
  ['combat_helmet','helmet'],['body_armor','armor'],['tactical_rig','rig'],['backpack','backpack'],
  // ---- 신규(107+) ----
  // FOOD 107-112
  ['jam_jar','jar'],['protein_powder','ration'],['apple','produce'],['mushrooms','produce'],
  ['canned_ham','can'],['field_ration','ration'],
  // MEDICAL 113-117
  ['stimulant_syringe','syringe'],['inhaler','spray'],['antiseptic_spray','spray'],
  ['saline_bag','bloodbag'],['medical_gel','spray'],
  // MELEE 118-122
  ['combat_axe','axe'],['war_hammer','blunt'],['tactical_tomahawk','axe'],['steel_pipe','blunt'],
  ['long_spear','spear'],
  // GUN 123-127
  ['magnum_revolver','revolver'],['vector_smg','smg'],['tactical_shotgun','shotgun'],
  ['marksman_rifle','rifle'],['rocket_launcher','launcher'],
  // AMMO 128-132
  ['ammo_38special','round'],['ammo_44magnum','round'],['ammo_slug','shell'],
  ['ammo_762x54','box'],['ammo_50cal','round'],
  // GEAR 133-149
  ['light_helmet','helmet'],['tactical_helmet','helmet'],['heavy_helmet','helmet_heavy'],
  ['ballistic_helmet','helmet_heavy'],['riot_helmet','helmet_heavy'],
  ['kevlar_vest','armor'],['plate_carrier','armor_plate'],['heavy_armor','armor_plate'],
  ['riot_armor','armor'],
  ['light_rig','rig'],['scout_rig','rig'],['assault_rig','rig'],['heavy_rig','rig'],
  ['small_backpack','backpack'],['medium_backpack','backpack'],['large_backpack','backpack'],
  ['duffel_bag','duffel'],
];

// 프리뷰 그룹핑용 카테고리 (base → category)
const BASE_CATEGORY = {
  can: 'FOOD', water: 'FOOD', snack: 'FOOD', jar: 'FOOD', ration: 'FOOD', produce: 'FOOD',
  bandage: 'MEDICAL', pills: 'MEDICAL', medkit: 'MEDICAL', syringe: 'MEDICAL', spray: 'MEDICAL', bloodbag: 'MEDICAL',
  knife: 'MELEE', bat: 'MELEE', axe: 'MELEE', machete: 'MELEE', blunt: 'MELEE', spear: 'MELEE',
  pistol: 'GUN', shotgun: 'GUN', rifle: 'GUN', revolver: 'GUN', smg: 'GUN', bow: 'GUN', launcher: 'GUN',
  box: 'AMMO', shell: 'AMMO', round: 'AMMO', arrow: 'AMMO', bolt: 'AMMO', flare: 'AMMO',
  helmet: 'GEAR', helmet_heavy: 'GEAR', armor: 'GEAR', armor_plate: 'GEAR', rig: 'GEAR', backpack: 'GEAR', duffel: 'GEAR',
  coin: 'CURRENCY',
};
const CATEGORY_ORDER = ['FOOD', 'MEDICAL', 'MELEE', 'GUN', 'AMMO', 'GEAR', 'CURRENCY'];

// ============================================================================
//  색조(hue) 변주 : hex↔HSL 헬퍼 + code 해시 → 큐레이트 hue
// ============================================================================
// 잘 분리되고 탁하지 않은 14색. 진흙빛 올리브(60~110)는 피한다.
const HUES = [0, 20, 38, 150, 168, 188, 205, 222, 245, 268, 288, 312, 332, 350];

function hashCode(s) {
  let h = 2166136261;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); }
  return h >>> 0;
}

// 같은 base 를 쓰는 아이템끼리는 hue 가 절대 겹치지 않도록 그룹 내 위치로 배정한다.
// (step 5 는 14 와 서로소 → 그룹 크기 ≤ 14 면 서로 다른 hue 가 보장된다.)
// base 별 시작 오프셋은 base 이름 해시로 흩뿌려, 카테고리마다 색이 뭉치지 않게 한다.
const HUE_STEP = 5;
const HUE_BY_CODE = (() => {
  const groups = {}; // baseKey → [code,...] (CATALOG 등장 순)
  for (const [code, baseKey] of CATALOG) (groups[baseKey] ||= []).push(code);
  const map = {};
  for (const [baseKey, codes] of Object.entries(groups)) {
    const off = hashCode(baseKey) % HUES.length;
    codes.forEach((code, i) => { map[code] = HUES[(off + i * HUE_STEP) % HUES.length]; });
  }
  return map;
})();
function hueFor(code) { return HUE_BY_CODE[code] ?? HUES[hashCode(code) % HUES.length]; }

function hexToRgb(hex) {
  const n = parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}
function rgbToHex(r, g, b) {
  const c = (v) => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0');
  return `#${c(r)}${c(g)}${c(b)}`;
}
function hexToHsl(hex) {
  let [r, g, b] = hexToRgb(hex).map((v) => v / 255);
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  let h = 0, s = 0; const l = (max + min) / 2;
  const d = max - min;
  if (d !== 0) {
    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    switch (max) {
      case r: h = (g - b) / d + (g < b ? 6 : 0); break;
      case g: h = (b - r) / d + 2; break;
      default: h = (r - g) / d + 4;
    }
    h *= 60;
  }
  return { h, s, l };
}
function hslToHex(h, s, l) {
  h = ((h % 360) + 360) % 360;
  const c = (1 - Math.abs(2 * l - 1)) * s;
  const x = c * (1 - Math.abs(((h / 60) % 2) - 1));
  const m = l - c / 2;
  let r = 0, g = 0, b = 0;
  if (h < 60) [r, g, b] = [c, x, 0];
  else if (h < 120) [r, g, b] = [x, c, 0];
  else if (h < 180) [r, g, b] = [0, c, x];
  else if (h < 240) [r, g, b] = [0, x, c];
  else if (h < 300) [r, g, b] = [x, 0, c];
  else [r, g, b] = [c, 0, x];
  return rgbToHex((r + m) * 255, (g + m) * 255, (b + m) * 255);
}

// 아이템 코드로 recolor 슬롯만 색조 회전(상대 hue차/S/L 유지) → 정체성 색이 달라진다.
function paletteFor(base, code) {
  if (!base.recolor || base.recolor.length === 0) return base.colors;
  const target = hueFor(code);
  const anchorHue = hexToHsl(base.colors[base.recolor[0]]).h;
  const delta = target - anchorHue;
  const out = { ...base.colors };
  for (const ch of base.recolor) {
    const { h, s, l } = hexToHsl(base.colors[ch]);
    out[ch] = hslToHex(h + delta, s, l);
  }
  return out;
}

// ============================================================================
//  렌더
// ============================================================================
function toSvg(name, rows, colors) {
  const h = rows.length;
  if (h !== 16) throw new Error(`[${name}] 행 수는 16이어야 함 (현재 ${h})`);
  const rects = [];
  for (let y = 0; y < h; y++) {
    const row = rows[y];
    if (row.length !== 16) throw new Error(`[${name}] ${y}행 길이 ${row.length} ≠ 16: "${row}"`);
    let x = 0;
    while (x < row.length) {
      const ch = row[x];
      if (ch === '.' || ch === ' ') { x++; continue; }
      const fill = colors[ch];
      if (!fill) throw new Error(`[${name}] 정의되지 않은 문자 '${ch}' (${x},${y})`);
      let run = 1;
      while (x + run < row.length && row[x + run] === ch) run++;
      rects.push(`<rect x="${x}" y="${y}" width="${run}" height="1" fill="${fill}"/>`);
      x += run;
    }
  }
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" width="64" height="64" shape-rendering="crispEdges">${rects.join('')}</svg>\n`;
}

// 기존 스프라이트 정리(레거시/구버전 잔재 제거) — 새로 쓸 목록을 먼저 만든다.
const written = new Set();
function emit(dir, filename, svg) {
  writeFileSync(join(dir, `${filename}.svg`), svg);
  written.add(`${filename}.svg`);
}

// 1) 카탈로그: code.svg 1장씩 (base + code별 hue)
const catalogCards = [];
for (const [code, baseKey] of CATALOG) {
  const base = BASES[baseKey];
  if (!base) throw new Error(`CATALOG '${code}': 알 수 없는 base '${baseKey}'`);
  const svg = toSvg(code, base.rows, paletteFor(base, code));
  emit(OUT, code, svg);
  catalogCards.push({ code, base: baseKey, category: BASE_CATEGORY[baseKey], svg });
}

// 2) 레거시 별칭 파일(웹 UI 하드코딩용) — 기본 색, hue 변주 없음
for (const [baseKey, legacy] of Object.entries(LEGACY_ALIAS)) {
  const base = BASES[baseKey];
  emit(OUT, legacy, toSvg(legacy, base.rows, base.colors));
}

// 3) 프리뷰 컨택트 시트 (카테고리 그룹 / 64px+32px)
const byCat = {};
for (const c of catalogCards) (byCat[c.category] ||= []).push(c);
const sections = CATEGORY_ORDER.filter((cat) => byCat[cat]).map((cat) => {
  const cards = byCat[cat].map((c) =>
    `<figure><div class="s l">${c.svg}</div><div class="s m">${c.svg}</div>`
    + `<figcaption><b>${c.code}</b><span>${c.base}</span></figcaption></figure>`).join('');
  return `<section><h2>${cat} <em>(${byCat[cat].length})</em></h2><div class="grid">${cards}</div></section>`;
}).join('');

const preview = `<!doctype html><meta charset="utf-8"><title>Wasteland Exchange · item sprites</title>
<style>
 body{background:#121009;color:#d8d1bc;font:14px ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;margin:0;padding:28px}
 h1{font-size:16px;font-weight:700;letter-spacing:2px;text-transform:uppercase;color:#d99a33;margin:0 0 4px}
 .sub{color:#8f8668;font-size:12px;margin-bottom:24px}
 h2{font-size:13px;letter-spacing:2px;color:#c78a2c;border-bottom:1px solid #342c1d;padding-bottom:6px;margin:34px 0 14px}
 h2 em{color:#6f684f;font-style:normal}
 .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(140px,1fr));gap:12px}
 figure{margin:0;background:#1b1712;border:1px solid #342c1d;border-radius:6px;padding:12px 10px;text-align:center;
   display:flex;flex-direction:column;align-items:center;gap:8px}
 .s{display:flex;align-items:center;justify-content:center;
   background:radial-gradient(circle at 50% 42%,#2b2417,transparent 72%)}
 .s svg{image-rendering:pixelated}
 .s.l svg{width:64px;height:64px}
 .s.m svg{width:32px;height:32px}
 figcaption{font-size:10px;letter-spacing:.5px;color:#8f8668;display:flex;flex-direction:column;gap:2px;word-break:break-all}
 figcaption b{color:#d8d1bc;font-weight:600}
 figcaption span{color:#6f684f}
</style>
<h1>Wasteland Exchange · item pixel sprites</h1>
<div class="sub">${catalogCards.length} items · base silhouettes + procedural per-code hue · rendered at 64px / 32px</div>
${sections}`;
writeFileSync(join(OUT, '_preview.html'), preview);
written.add('_preview.html');

// 4) assets/items/*.svg → web/public/sprites/ 복사 + 고아 파일 제거(재현성)
for (const f of readdirSync(OUT)) {
  if (f.endsWith('.svg') && written.has(f)) copyFileSync(join(OUT, f), join(WEB, f));
}
for (const f of readdirSync(WEB)) {
  if (f.endsWith('.svg') && !written.has(f)) rmSync(join(WEB, f));
}
for (const f of readdirSync(OUT)) {
  if (f.endsWith('.svg') && !written.has(f)) rmSync(join(OUT, f));
}

const catCount = CATALOG.length;
const legacyCount = Object.keys(LEGACY_ALIAS).length;
console.log(`생성 완료: 카탈로그 ${catCount}종 + 레거시 별칭 ${legacyCount}장 → assets/items/ 및 web/public/sprites/`);
console.log(`프리뷰: assets/items/_preview.html`);
