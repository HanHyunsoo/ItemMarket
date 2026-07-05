// Record short demo GIFs of the live Wasteland Exchange web app for the README.
//
// Two clips:
//   1) docs/demo-trade.gif — real-time trading loop: sign in as Survivor_Alpha,
//      open 7.62mm ammo (template 95), place a BUY that crosses the resting asks,
//      and watch the order book + recent trades + caps chip update live.
//   2) docs/demo-grid.gif — grid stash: as Survivor_Bravo, drag a pistol to an
//      empty 2×2 slot (valid, green), then drag the AK-47 4×2 over a full area
//      (invalid, red) so it snaps back.
//
// NOTE: the trade clip PLACES a real order and mutates the demo DB (that's fine).
//
//   cd tools/screenshots
//   npm install && npx playwright install chromium   # (chromium usually cached)
//   npm run gifs                                      # record + convert (needs ffmpeg)
//
// Env overrides: WEB (default http://localhost:5173), API (default http://localhost:5080).

import { chromium } from 'playwright'
import { fileURLToPath } from 'node:url'
import { dirname, resolve, join } from 'node:path'
import { mkdirSync, mkdtempSync, existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { spawnSync } from 'node:child_process'

const WEB = process.env.WEB ?? 'http://localhost:5173'
const HERE = dirname(fileURLToPath(import.meta.url))
const OUT = resolve(HERE, '../../docs')
mkdirSync(OUT, { recursive: true })
const VIDEO_DIR = mkdtempSync(join(tmpdir(), 'wx-gifs-'))

const wait = (ms) => new Promise((r) => setTimeout(r, ms))
const CELL = 46 // px per grid cell (must match StashView.vue)

async function selectPlayer(page, displayName) {
  await page.locator('.dogtag .el-select__wrapper').click()
  const option = page.locator('.el-select-dropdown__item', { hasText: displayName })
  await option.first().waitFor({ state: 'visible', timeout: 10000 })
  await option.first().click()
  await page.locator('nav.nav .nav-btn').first().waitFor({ state: 'visible', timeout: 15000 })
  await wait(700)
}

// Smoothly glide the real cursor so the recording reads well.
async function glide(page, x, y, steps = 22) {
  await page.mouse.move(x, y, { steps })
}

// ---------------------------------------------------------------------------
// HTML5 drag-and-drop driver. StashView uses native dragstart/dragover/drop
// events (it reads e.clientX/Y and drives its own reactive drop-hint), which
// Playwright's synthetic mouse does not fire reliably. So we dispatch the DnD
// events ourselves with a shared DataTransfer, while ALSO moving the real
// cursor in lockstep so the GIF shows a cursor traveling with the drop-hint.
async function dndStart(page, srcX, srcY) {
  await glide(page, srcX, srcY)
  await page.evaluate(
    ([x, y]) => {
      const el = document.elementFromPoint(x, y)?.closest('.tile')
      if (!el) throw new Error('no draggable tile at source point')
      window.__wxDt = new DataTransfer()
      el.dispatchEvent(
        new DragEvent('dragstart', { bubbles: true, cancelable: true, clientX: x, clientY: y, dataTransfer: window.__wxDt }),
      )
      window.__wxSrc = el
    },
    [srcX, srcY],
  )
}

async function dndOver(page, x, y) {
  await glide(page, x, y, 8)
  await page.evaluate(
    ([x, y]) => {
      const grid = document.querySelector('.stash-grid')
      grid.dispatchEvent(
        new DragEvent('dragover', { bubbles: true, cancelable: true, clientX: x, clientY: y, dataTransfer: window.__wxDt }),
      )
    },
    [x, y],
  )
}

async function dndDrop(page, x, y) {
  await page.evaluate(
    ([x, y]) => {
      const grid = document.querySelector('.stash-grid')
      grid.dispatchEvent(
        new DragEvent('drop', { bubbles: true, cancelable: true, clientX: x, clientY: y, dataTransfer: window.__wxDt }),
      )
      window.__wxSrc?.dispatchEvent(new DragEvent('dragend', { bubbles: true, dataTransfer: window.__wxDt }))
    },
    [x, y],
  )
}

// Center client coords of a grid cell (top-left of an item footprint uses w/h).
async function cellPoint(page, cx, cy) {
  return page.evaluate(
    ([cx, cy, CELL]) => {
      const r = document.querySelector('.stash-grid').getBoundingClientRect()
      return [Math.round(r.left + (cx + 0.5) * CELL), Math.round(r.top + (cy + 0.5) * CELL)]
    },
    [cx, cy, CELL],
  )
}

// Drag an item (grabbed at its top-left cell) across a path of cells to a
// destination, stepping the dragover so the drop-hint animates.
async function dragItem(page, from, path) {
  const [sx, sy] = await cellPoint(page, from.x, from.y)
  await dndStart(page, sx, sy)
  await wait(250)
  let last = [sx, sy]
  for (const c of path) {
    const [tx, ty] = await cellPoint(page, c.x, c.y)
    // interpolate a few dragover points between cells for a smooth hint slide
    for (let i = 1; i <= 3; i++) {
      const px = Math.round(last[0] + ((tx - last[0]) * i) / 3)
      const py = Math.round(last[1] + ((ty - last[1]) * i) / 3)
      await dndOver(page, px, py)
      await wait(70)
    }
    last = [tx, ty]
    await wait(180)
  }
  return last
}

// ---------------------------------------------------------------------------
async function recordTrade(browser) {
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    colorScheme: 'dark',
    recordVideo: { dir: VIDEO_DIR, size: { width: 1280, height: 800 } },
  })
  const page = await context.newPage()

  await page.goto(`${WEB}/market`, { waitUntil: 'networkidle' })
  await selectPlayer(page, 'Survivor_Alpha')

  // 7.62mm ammo (id 95) — deep resting asks (best ask 9).
  await page.goto(`${WEB}/market/95`, { waitUntil: 'networkidle' })
  await page.locator('.book').first().waitFor({ state: 'visible', timeout: 15000 })
  await page.locator('.submit').first().waitFor({ state: 'visible', timeout: 15000 })
  await page.locator('.wx-panel.trades .el-table').first().waitFor({ state: 'visible', timeout: 15000 })
  await wait(1400) // linger on the LIVE indicator + resting book

  // Fill the BUY form: price 10 crosses the ask@9, quantity 12.
  const priceInput = page.locator('.field', { hasText: 'Unit price' }).locator('input').first()
  await priceInput.click()
  await priceInput.press('ControlOrMeta+a')
  await priceInput.type('10', { delay: 90 })
  await wait(400)

  const qtyInput = page.locator('.field', { hasText: 'Quantity' }).locator('input').first()
  await qtyInput.click()
  await qtyInput.press('ControlOrMeta+a')
  await qtyInput.type('12', { delay: 90 })
  await wait(600)

  // Place the crossing BUY.
  const buyBtn = page.locator('button.submit')
  const box = await buyBtn.boundingBox()
  if (box) await glide(page, box.x + box.width / 2, box.y + box.height / 2)
  await wait(300)
  await buyBtn.click()

  // Let the fill land: book ask@9 shrinks, a trade row appears, caps chip drops.
  await wait(3200)
  await context.close()
  return page.video().path()
}

async function recordGrid(browser) {
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    colorScheme: 'dark',
    recordVideo: { dir: VIDEO_DIR, size: { width: 1280, height: 800 } },
  })
  const page = await context.newPage()

  await page.goto(`${WEB}/market`, { waitUntil: 'networkidle' })
  await selectPlayer(page, 'Survivor_Bravo')

  await page.goto(`${WEB}/stash`, { waitUntil: 'networkidle' })
  await page.locator('.stash-grid').first().waitFor({ state: 'visible', timeout: 15000 })
  await page.locator('.tile-sprite').first().waitFor({ state: 'visible', timeout: 15000 })
  await wait(1200)

  // 1) VALID move — Makarov pistol (2×2) from (7,6) to the empty 2×2 slot at (8,8).
  await dragItem(page, { x: 7, y: 6 }, [{ x: 8, y: 7 }, { x: 8, y: 8 }])
  await wait(300)
  {
    const [dx, dy] = await cellPoint(page, 8, 8)
    await dndDrop(page, dx, dy)
  }
  await wait(1600) // server confirms the move, tile settles

  // 2) INVALID move — AK-47 (4×2) at (4,9) dragged up over the full stack rows.
  await dragItem(page, { x: 4, y: 9 }, [{ x: 3, y: 6 }, { x: 2, y: 3 }, { x: 2, y: 2 }])
  await wait(1100) // hold on the red overlap highlight
  {
    const [dx, dy] = await cellPoint(page, 2, 2)
    await dndDrop(page, dx, dy) // rejected -> snaps back
  }
  await wait(1600)
  await context.close()
  return page.video().path()
}

// ---------------------------------------------------------------------------
function toGif(webm, outName, { fps = 13, width = 950, ss = '0', t } = {}) {
  const out = resolve(OUT, outName)
  const palette = join(VIDEO_DIR, outName.replace('.gif', '.png'))
  const trim = ['-ss', ss, ...(t ? ['-t', String(t)] : [])]
  const vf = `fps=${fps},scale=${width}:-1:flags=lanczos`
  const p1 = spawnSync('ffmpeg', ['-y', ...trim, '-i', webm, '-vf', `${vf},palettegen=stats_mode=diff`, palette], { stdio: 'inherit' })
  if (p1.status !== 0) throw new Error('palettegen failed')
  const p2 = spawnSync(
    'ffmpeg',
    ['-y', ...trim, '-i', webm, '-i', palette, '-lavfi', `${vf} [x]; [x][1:v] paletteuse=dither=bayer:bayer_scale=3`, out],
    { stdio: 'inherit' },
  )
  if (p2.status !== 0) throw new Error('paletteuse failed')
  const size = spawnSync('du', ['-h', out]).stdout?.toString().split('\t')[0] ?? '?'
  console.log(`  -> ${out} (${size})`)
  return out
}

const run = async () => {
  if (!existsSync(OUT)) mkdirSync(OUT, { recursive: true })
  const browser = await chromium.launch({ headless: true })
  console.log('recording demo-trade …')
  const tradeWebm = await recordTrade(browser)
  console.log('recording demo-grid …')
  const gridWebm = await recordGrid(browser)
  await browser.close()

  console.log('converting → GIF (ffmpeg two-pass palette) …')
  // Trim the sign-in/navigation dead time at the head of each clip.
  toGif(tradeWebm, 'demo-trade.gif', { fps: 13, width: 950, ss: '3.2' })
  toGif(gridWebm, 'demo-grid.gif', { fps: 13, width: 950, ss: '3.2' })
  console.log('done. raw webm left in', VIDEO_DIR, '(not committed)')
}

run().catch((err) => {
  console.error(err)
  process.exit(1)
})
