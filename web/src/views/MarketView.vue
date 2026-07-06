<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { storeToRefs } from 'pinia'
import { useCatalogStore } from '@/stores/catalog'
import { marketApi } from '@/api/endpoints'
import ItemSprite from '@/components/ItemSprite.vue'
import RarityTag from '@/components/RarityTag.vue'
import { caps, RARITY_ORDER, rarityGlow } from '@/utils/format'
import { toastError } from '@/utils/toast'
import type { ItemCategory, ItemRarity, MarketTickerDto } from '@/api/types'

const catalog = useCatalogStore()
const { items, loading } = storeToRefs(catalog)
const router = useRouter()

const search = ref('')
const category = ref<ItemCategory | ''>('')
const rarity = ref<ItemRarity | ''>('')

const CATEGORIES: ItemCategory[] = ['Food', 'Medical', 'Melee', 'Gun', 'Ammo']

// ── 실시간 시세(tickers) — 15초 폴링으로 "살아있는 시장" ─────────────────────
const tickers = ref<Map<number, MarketTickerDto>>(new Map())
let pollTimer: ReturnType<typeof setInterval> | null = null

async function loadTickers(): Promise<void> {
  try {
    const list = await marketApi.tickers()
    tickers.value = new Map(list.map((t) => [t.templateId, t]))
  } catch {
    /* 시세는 부가 정보 — 실패해도 카탈로그 카드는 계속 보인다 */
  }
}

function tickerFor(id: number): MarketTickerDto | undefined {
  return tickers.value.get(id)
}
// 활성 주문이나 최근 체결이 있으면 "살아있는" 시장.
function isLive(id: number): boolean {
  const t = tickers.value.get(id)
  return !!t && (t.openOrders > 0 || t.lastTradeAt !== null)
}

onMounted(async () => {
  try {
    await catalog.ensureLoaded()
  } catch (err) {
    toastError(err, 'Could not load the item catalog.')
  }
  await loadTickers()
  pollTimer = setInterval(loadTickers, 15_000)
})

onUnmounted(() => {
  if (pollTimer) clearInterval(pollTimer)
})

const filtered = computed(() => {
  const q = search.value.trim().toLowerCase()
  const list = items.value.filter((it) => {
    if (category.value && it.category !== category.value) return false
    if (rarity.value && it.rarity !== rarity.value) return false
    if (q && !it.name.toLowerCase().includes(q) && !it.code.toLowerCase().includes(q)) return false
    return true
  })
  // 활동순: 살아있는 시장(최근 체결/호가)이 앞으로, 그 안에서는 최근 체결 시각 → 유동성 순.
  return [...list].sort((a, b) => {
    const ta = tickers.value.get(a.id)
    const tb = tickers.value.get(b.id)
    const la = isLive(a.id) ? 1 : 0
    const lb = isLive(b.id) ? 1 : 0
    if (la !== lb) return lb - la
    const tradeA = ta?.lastTradeAt ? Date.parse(ta.lastTradeAt) : 0
    const tradeB = tb?.lastTradeAt ? Date.parse(tb.lastTradeAt) : 0
    if (tradeA !== tradeB) return tradeB - tradeA
    return (tb?.openOrders ?? 0) - (ta?.openOrders ?? 0)
  })
})

function countFor(c: ItemCategory | ''): number {
  if (!c) return items.value.length
  return items.value.filter((it) => it.category === c).length
}

function open(id: number) {
  router.push({ name: 'item', params: { id } })
}
</script>

<template>
  <div>
    <h1 class="wx-page-title">Market</h1>
    <p class="wx-page-sub">
      {{ items.length }} salvage templates — pick a crate to open its order book
    </p>

    <div class="filters">
      <div class="chips">
        <button class="wx-chip" :class="{ active: category === '' }" @click="category = ''">
          All <span class="chip-count">{{ countFor('') }}</span>
        </button>
        <button
          v-for="c in CATEGORIES"
          :key="c"
          class="wx-chip"
          :class="{ active: category === c }"
          @click="category = category === c ? '' : c"
        >
          {{ c }} <span class="chip-count">{{ countFor(c) }}</span>
        </button>
      </div>
      <div class="filters-right">
        <el-select v-model="rarity" placeholder="Rarity" clearable style="width: 140px">
          <el-option v-for="r in RARITY_ORDER" :key="r" :label="r" :value="r" />
        </el-select>
        <el-input v-model="search" placeholder="Search salvage…" clearable style="width: 220px" />
      </div>
    </div>

    <div v-loading="loading" class="grid">
      <button
        v-for="it in filtered"
        :key="it.id"
        class="card"
        :style="{ '--rar': rarityGlow(it.rarity, 0.55), '--rar-soft': rarityGlow(it.rarity, 0.14) }"
        @click="open(it.id)"
      >
        <div class="card-sprite">
          <ItemSprite :icon="it.icon" :category="it.category" :rarity="it.rarity" :size="52" bare />
        </div>
        <div class="card-name">{{ it.name }}</div>
        <div class="card-meta">
          <span class="cat mono">{{ it.category }}</span>
          <RarityTag :rarity="it.rarity" />
        </div>

        <!-- 실시간 시세: 최우선 매수/매도 호가 + 최근 체결. 활동 없으면 "시장 없음". -->
        <template v-if="isLive(it.id)">
          <div class="quote mono">
            <span class="bid" :class="{ dim: tickerFor(it.id)?.bestBid == null }">
              {{ tickerFor(it.id)?.bestBid != null ? caps(tickerFor(it.id)!.bestBid) : '—' }}
            </span>
            <span class="spread-sep">·</span>
            <span class="ask" :class="{ dim: tickerFor(it.id)?.bestAsk == null }">
              {{ tickerFor(it.id)?.bestAsk != null ? caps(tickerFor(it.id)!.bestAsk) : '—' }}
            </span>
          </div>
          <div class="card-sub mono">
            <span v-if="tickerFor(it.id)?.lastPrice != null" class="last">
              최근 {{ caps(tickerFor(it.id)!.lastPrice) }}
            </span>
            <span class="liq" :title="`활성 주문 ${tickerFor(it.id)?.openOrders ?? 0}건`">
              <i class="live-dot" /> {{ tickerFor(it.id)?.openOrders ?? 0 }}
            </span>
          </div>
        </template>
        <template v-else>
          <div class="card-value mono">
            <img class="pixel" src="/sprites/cap_coin.svg" alt="" />
            {{ caps(it.baseValue) }}
          </div>
          <div class="card-sub mono no-market">시장 없음 · 기준가</div>
        </template>
      </button>
      <div v-if="!loading && filtered.length === 0" class="wx-empty">
        <img class="pixel" src="/sprites/ammo_box.svg" alt="" />
        Nothing in the crates matches. Loosen your filters, scavenger.
      </div>
    </div>
  </div>
</template>

<style scoped>
.filters {
  display: flex;
  gap: 14px;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  margin-bottom: var(--wx-s5);
}
.chips {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}
.chip-count {
  opacity: 0.6;
  font-weight: 400;
  margin-left: 2px;
}
.filters-right {
  display: flex;
  gap: 10px;
}

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(168px, 1fr));
  gap: 12px;
  min-height: 200px;
}
.grid .wx-empty {
  grid-column: 1 / -1;
}

.card {
  --rar: transparent;
  --rar-soft: transparent;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  text-align: center;
  background: linear-gradient(180deg, var(--wx-panel-2), var(--wx-panel) 55%);
  border: 1px solid var(--wx-border);
  border-top: 2px solid var(--rar);
  border-radius: var(--wx-r);
  padding: 14px 10px 12px;
  cursor: pointer;
  transition:
    transform 0.14s ease,
    border-color 0.14s ease,
    box-shadow 0.14s ease;
  color: inherit;
  font: inherit;
  box-shadow: var(--wx-shadow);
}
.card:hover {
  transform: translateY(-3px);
  border-color: var(--rar);
  box-shadow:
    var(--wx-shadow-lift),
    0 0 22px var(--rar-soft);
}

.card-sprite {
  display: grid;
  place-items: center;
  width: 100%;
  padding: 6px 0 2px;
  background: radial-gradient(circle at 50% 45%, var(--rar-soft), transparent 68%);
}

.card-name {
  font-weight: 700;
  font-size: 13px;
  max-width: 100%;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.card-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 10px;
}
.cat {
  color: var(--wx-text-faint);
  text-transform: uppercase;
  letter-spacing: 1.5px;
}
.card-value {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 2px;
  font-size: 13px;
  color: var(--wx-amber-bright);
  font-weight: 700;
}
.card-value img {
  width: 14px;
  height: 14px;
}

/* 실시간 시세 */
.quote {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 2px;
  font-size: 13px;
  font-weight: 700;
}
.quote .bid {
  color: var(--wx-buy, #6fae5f);
}
.quote .ask {
  color: var(--wx-sell, #d05540);
}
.quote .dim {
  color: var(--wx-text-faint);
  font-weight: 400;
}
.spread-sep {
  color: var(--wx-text-faint);
}
.card-sub {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 10px;
  color: var(--wx-text-dim);
  margin-top: 1px;
}
.card-sub .liq {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}
.live-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--wx-buy, #6fae5f);
  box-shadow: 0 0 6px var(--wx-buy, #6fae5f);
  animation: live-pulse 1.6s ease-in-out infinite;
}
@keyframes live-pulse {
  50% {
    opacity: 0.35;
  }
}
.card-sub.no-market {
  color: var(--wx-text-faint);
  font-style: italic;
}
</style>
