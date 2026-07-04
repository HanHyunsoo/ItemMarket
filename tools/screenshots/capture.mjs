// Capture Wasteland Exchange UI screenshots for the README.
// Read-only: it navigates + logs in via the header player switcher, then
// screenshots each page. It does NOT place/cancel orders or grant items.
//
//   npm install && npx playwright install chromium
//   npm run shots
//
// Env overrides: WEB (default http://localhost:5173), API (default http://localhost:5080).

import { chromium } from 'playwright'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'
import { mkdirSync } from 'node:fs'

const WEB = process.env.WEB ?? 'http://localhost:5173'
const API = process.env.API ?? 'http://localhost:5080'
const OUT = resolve(dirname(fileURLToPath(import.meta.url)), '../../docs/screenshots')
mkdirSync(OUT, { recursive: true })

const wait = (ms) => new Promise((r) => setTimeout(r, ms))

async function selectPlayer(page, displayName) {
  // Element Plus <el-select> in the header dog-tag switcher.
  await page.locator('.dogtag .el-select__wrapper').click()
  const option = page.locator('.el-select-dropdown__item', { hasText: displayName })
  await option.first().waitFor({ state: 'visible', timeout: 10000 })
  await option.first().click()
  // Login is async (REST) — wait until the nav for authed users renders.
  await page.locator('nav.nav .nav-btn').first().waitFor({ state: 'visible', timeout: 15000 })
  await wait(600)
}

async function goto(page, path) {
  await page.goto(`${WEB}${path}`, { waitUntil: 'networkidle' })
}

async function shot(page, name) {
  await wait(500) // let sprites/fonts settle
  await page.screenshot({ path: resolve(OUT, name), fullPage: false })
  console.log('  saved', name)
}

const run = async () => {
  const browser = await chromium.launch({ headless: true })
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 2,
    colorScheme: 'dark',
  })
  const page = await context.newPage()

  // ---- Sign in as Survivor_Alpha (market maker: deep book + wallet activity) ----
  await goto(page, '/market')
  await selectPlayer(page, 'Survivor_Alpha')

  // 1) Market catalog grid
  await goto(page, '/market')
  await page.locator('.grid .card').first().waitFor({ state: 'visible', timeout: 15000 })
  await page.locator('.card-sprite .pixel').first().waitFor({ state: 'visible', timeout: 15000 })
  await shot(page, 'market.png')

  // 2) Item detail — 7.62mm ammo (id 95): bid/ask ladder + trades + order form
  await goto(page, '/market/95')
  await page.locator('.book').first().waitFor({ state: 'visible', timeout: 15000 })
  await page.locator('.wx-panel.trades').first().waitFor({ state: 'visible', timeout: 15000 })
  await page.locator('.submit').first().waitFor({ state: 'visible', timeout: 15000 })
  await shot(page, 'item-detail.png')

  // 3) Wallet + ledger
  await goto(page, '/wallet')
  await page.locator('.balance-val').first().waitFor({ state: 'visible', timeout: 15000 })
  await shot(page, 'wallet.png')

  // ---- Switch to Survivor_Bravo (holds weapons: AK-47 4x2 in the grid) ----
  await selectPlayer(page, 'Survivor_Bravo')

  // 4) Grid stash
  await goto(page, '/stash')
  await page.locator('.stash-grid').first().waitFor({ state: 'visible', timeout: 15000 })
  await page.locator('.tile-sprite').first().waitFor({ state: 'visible', timeout: 15000 })
  await shot(page, 'grid-stash.png')

  // ---- Switch to Trader_Charlie (admin) ----
  await selectPlayer(page, 'Trader_Charlie')
  await page.locator('.nav-btn.admin').first().waitFor({ state: 'visible', timeout: 15000 })

  // 5) Admin
  await goto(page, '/admin')
  await page.locator('.wx-page-title').first().waitFor({ state: 'visible', timeout: 15000 })
  await page.locator('.wx-panel').first().waitFor({ state: 'visible', timeout: 15000 })
  await shot(page, 'admin.png')

  // 6) Swagger / OpenAPI UI (no auth required)
  await page.goto(`${API}/swagger`, { waitUntil: 'networkidle' })
  await page.locator('.swagger-ui .opblock, #swagger-ui .info').first().waitFor({
    state: 'visible',
    timeout: 20000,
  })
  await wait(800)
  await shot(page, 'swagger.png')

  await browser.close()
  console.log('done ->', OUT)
}

run().catch((err) => {
  console.error(err)
  process.exit(1)
})
