<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useCatalogStore } from '@/stores/catalog'
import { inventoryApi, marketApi, ordersApi } from '@/api/endpoints'
import ItemSprite from '@/components/ItemSprite.vue'
import RarityTag from '@/components/RarityTag.vue'
import { caps, dateTime, shortId } from '@/utils/format'
import { toastError, toastSuccess } from '@/utils/toast'
import type { ItemInstanceDto, OrderBookSnapshotDto, OrderSide, TradeDto } from '@/api/types'

const props = defineProps<{ id: number }>()
const router = useRouter()
const catalog = useCatalogStore()

const template = computed(() => catalog.get(props.id))
const isUnique = computed(() => template.value && !template.value.stackable)

const book = ref<OrderBookSnapshotDto | null>(null)
const trades = ref<TradeDto[]>([])
const ownedInstances = ref<ItemInstanceDto[]>([])
const loadingBook = ref(false)
const loadingTrades = ref(false)

const form = reactive({
  side: 'Buy' as OrderSide,
  unitPrice: 0,
  quantity: 1,
  instanceId: '' as string,
})
const placing = ref(false)

const bestBid = computed(() => book.value?.bids[0]?.unitPrice ?? null)
const bestAsk = computed(() => book.value?.asks[0]?.unitPrice ?? null)
const spread = computed(() =>
  bestBid.value !== null && bestAsk.value !== null ? bestAsk.value - bestBid.value : null,
)

// Depth bars: widest row = deepest level on either side of the book.
const maxDepth = computed(() => {
  const qtys = [
    ...(book.value?.bids ?? []).map((l) => l.quantity),
    ...(book.value?.asks ?? []).map((l) => l.quantity),
  ]
  return qtys.length ? Math.max(...qtys) : 1
})
function depthPct(qty: number): number {
  return Math.max(4, Math.round((qty / maxDepth.value) * 100))
}

async function loadBook() {
  loadingBook.value = true
  try {
    book.value = await marketApi.book(props.id)
  } catch (err) {
    toastError(err, 'Could not load the order book.')
  } finally {
    loadingBook.value = false
  }
}

async function loadTrades() {
  loadingTrades.value = true
  try {
    const res = await marketApi.trades(props.id, 1, 20)
    trades.value = res.items
  } catch (err) {
    toastError(err, 'Could not load recent trades.')
  } finally {
    loadingTrades.value = false
  }
}

async function loadOwned() {
  if (!isUnique.value) return
  try {
    const inv = await inventoryApi.get()
    ownedInstances.value = inv.instances.filter((i) => i.templateId === props.id)
  } catch (err) {
    toastError(err, 'Could not load your instances.')
  }
}

async function refreshAll() {
  await Promise.all([loadBook(), loadTrades(), loadOwned()])
}

onMounted(async () => {
  try {
    await catalog.ensureLoaded()
  } catch (err) {
    toastError(err)
  }
  if (template.value) {
    form.unitPrice = template.value.baseValue
  }
  await refreshAll()
})

// When switching to a unique SELL, force quantity 1 and require an instance.
watch(
  () => [form.side, isUnique.value] as const,
  () => {
    if (isUnique.value && form.side === 'Sell') {
      form.quantity = 1
    }
  },
)

const canSubmit = computed(() => {
  if (form.unitPrice <= 0 || form.quantity <= 0) return false
  if (isUnique.value && form.side === 'Sell' && !form.instanceId) return false
  return true
})

async function submit() {
  if (!template.value) return
  placing.value = true
  try {
    const uniqueSell = isUnique.value && form.side === 'Sell'
    const res = await ordersApi.place({
      side: form.side,
      itemTemplateId: props.id,
      unitPrice: form.unitPrice,
      quantity: uniqueSell ? 1 : form.quantity,
      instanceId: uniqueSell ? form.instanceId : null,
    })
    const filled = res.fills.reduce((s, f) => s + f.quantity, 0)
    toastSuccess(
      filled > 0
        ? `Order placed — ${filled} filled immediately, status ${res.order.status}.`
        : `Order placed — resting on the book (${res.order.status}).`,
    )
    form.instanceId = ''
    await refreshAll()
  } catch (err) {
    toastError(err, 'Could not place order.')
  } finally {
    placing.value = false
  }
}

function instanceLabel(i: ItemInstanceDto): string {
  const parts = [shortId(i.id)]
  if (i.durability !== null) parts.push(`dur ${i.durability}`)
  if (i.attachments.length) parts.push(i.attachments.join('+'))
  return parts.join(' · ')
}
</script>

<template>
  <div v-if="template">
    <button class="back mono" @click="router.push({ name: 'market' })">
      &larr; BACK TO MARKET
    </button>

    <div class="head">
      <ItemSprite
        :icon="template.icon"
        :category="template.category"
        :rarity="template.rarity"
        :size="72"
      />
      <div>
        <h1 class="wx-page-title">{{ template.name }}</h1>
        <div class="head-meta">
          <RarityTag :rarity="template.rarity" />
          <span class="wx-muted">{{ template.category }}</span>
          <span class="sep">/</span>
          <span class="wx-muted">{{ template.stackable ? 'Stackable' : 'Unique instance' }}</span>
          <span class="sep">/</span>
          <span class="wx-muted">base {{ caps(template.baseValue) }} caps</span>
          <span class="wx-muted mono ref">#{{ template.id }} {{ template.code }}</span>
        </div>
      </div>
    </div>

    <div class="layout">
      <!-- Order book -->
      <section class="wx-panel">
        <div class="panel-head">
          <h3 class="wx-section-title">Order Book</h3>
          <div class="spread mono">
            <span class="wx-buy">BID {{ bestBid !== null ? caps(bestBid) : '—' }}</span>
            <span v-if="spread !== null" class="wx-muted">Δ {{ caps(spread) }}</span>
            <span class="wx-sell">ASK {{ bestAsk !== null ? caps(bestAsk) : '—' }}</span>
          </div>
        </div>
        <div v-loading="loadingBook" class="book mono">
          <div class="book-col">
            <div class="book-h wx-buy">BIDS · BUY</div>
            <div class="ladder-head"><span>ORD</span><span>QTY</span><span>PRICE</span></div>
            <div
              v-for="lvl in book?.bids ?? []"
              :key="'b' + lvl.unitPrice"
              class="lvl bid"
              :style="{ '--depth': depthPct(lvl.quantity) + '%' }"
            >
              <span class="wx-muted">{{ lvl.orderCount }}</span>
              <span>{{ lvl.quantity }}</span>
              <span class="px wx-buy">{{ caps(lvl.unitPrice) }}</span>
            </div>
            <div v-if="!loadingBook && !(book?.bids ?? []).length" class="lvl-empty">no bids</div>
          </div>
          <div class="book-col">
            <div class="book-h wx-sell right">ASKS · SELL</div>
            <div class="ladder-head ask"><span>PRICE</span><span>QTY</span><span>ORD</span></div>
            <div
              v-for="lvl in book?.asks ?? []"
              :key="'a' + lvl.unitPrice"
              class="lvl ask"
              :style="{ '--depth': depthPct(lvl.quantity) + '%' }"
            >
              <span class="px wx-sell">{{ caps(lvl.unitPrice) }}</span>
              <span>{{ lvl.quantity }}</span>
              <span class="wx-muted">{{ lvl.orderCount }}</span>
            </div>
            <div v-if="!loadingBook && !(book?.asks ?? []).length" class="lvl-empty">no asks</div>
          </div>
        </div>
      </section>

      <!-- Place order -->
      <section class="wx-panel">
        <h3 class="wx-section-title">Place Order</h3>
        <div class="side-toggle mono">
          <button
            class="side-btn buy"
            :class="{ active: form.side === 'Buy' }"
            @click="form.side = 'Buy'"
          >
            BUY
          </button>
          <button
            class="side-btn sell"
            :class="{ active: form.side === 'Sell' }"
            @click="form.side = 'Sell'"
          >
            SELL
          </button>
        </div>

        <div class="field">
          <label>Unit price (caps)</label>
          <el-input-number
            v-model="form.unitPrice"
            :min="1"
            :step="1"
            controls-position="right"
            style="width: 100%"
          />
        </div>

        <div v-if="isUnique && form.side === 'Sell'" class="field">
          <label>Instance to sell</label>
          <el-select v-model="form.instanceId" placeholder="Pick one you own" style="width: 100%">
            <el-option
              v-for="i in ownedInstances"
              :key="i.id"
              :label="instanceLabel(i)"
              :value="i.id"
            />
          </el-select>
          <div v-if="ownedInstances.length === 0" class="hint wx-muted">
            You own no instances of this item.
          </div>
        </div>
        <div v-else class="field">
          <label>Quantity</label>
          <el-input-number
            v-model="form.quantity"
            :min="1"
            :step="1"
            controls-position="right"
            style="width: 100%"
          />
        </div>

        <div class="est mono">
          <span class="wx-muted">EST. TOTAL</span>
          <span class="est-val"
            >{{
              caps(form.unitPrice * (isUnique && form.side === 'Sell' ? 1 : form.quantity))
            }}
            caps</span
          >
        </div>

        <el-button
          :type="form.side === 'Buy' ? 'success' : 'danger'"
          :loading="placing"
          :disabled="!canSubmit"
          class="submit"
          @click="submit"
        >
          {{ form.side === 'Buy' ? 'PLACE BUY ORDER' : 'PLACE SELL ORDER' }}
        </el-button>
      </section>

      <!-- Recent trades -->
      <section class="wx-panel trades">
        <h3 class="wx-section-title">Recent Trades</h3>
        <el-table
          v-loading="loadingTrades"
          :data="trades"
          size="small"
          empty-text="No trades yet — the book is waiting"
        >
          <el-table-column label="Time" width="130">
            <template #default="{ row }"
              ><span class="mono wx-muted">{{ dateTime(row.executedAt) }}</span></template
            >
          </el-table-column>
          <el-table-column label="Price" align="right">
            <template #default="{ row }"
              ><span class="mono wx-amber">{{ caps(row.unitPrice) }}</span></template
            >
          </el-table-column>
          <el-table-column label="Qty" align="right">
            <template #default="{ row }"
              ><span class="mono">{{ row.quantity }}</span></template
            >
          </el-table-column>
          <el-table-column label="Fee" align="right">
            <template #default="{ row }"
              ><span class="mono wx-muted">{{ caps(row.feeAmount) }}</span></template
            >
          </el-table-column>
        </el-table>
      </section>
    </div>
  </div>
  <div v-else class="wx-empty">
    <img class="pixel" src="/sprites/ammo_box.svg" alt="" />
    Loading item…
  </div>
</template>

<style scoped>
.back {
  background: none;
  border: none;
  color: var(--wx-text-dim);
  cursor: pointer;
  font-size: 11px;
  letter-spacing: 2px;
  margin-bottom: var(--wx-s3);
  padding: 0;
}
.back:hover {
  color: var(--wx-amber);
}
.head {
  display: flex;
  gap: var(--wx-s4);
  align-items: center;
  margin-bottom: var(--wx-s5);
}
.head-meta {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 1px;
  margin-top: 8px;
}
.sep {
  color: var(--wx-text-faint);
}
.ref {
  color: var(--wx-text-faint);
}

.layout {
  display: grid;
  grid-template-columns: 1fr 340px;
  gap: var(--wx-s4);
  align-items: start;
}
.trades {
  grid-column: 1 / -1;
}

.panel-head {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: 12px;
  flex-wrap: wrap;
}
.spread {
  display: flex;
  gap: 14px;
  font-size: 12px;
  letter-spacing: 0.5px;
}

/* ---- bid/ask ladder ---- */
.book {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 2px;
  min-height: 120px;
}
.book-h {
  font-size: 10px;
  letter-spacing: 2px;
  font-weight: 700;
  margin-bottom: 8px;
}
.book-h.right {
  text-align: right;
}
.book-col:first-child {
  border-right: 1px solid var(--wx-border-soft);
  padding-right: 10px;
}
.book-col:last-child {
  padding-left: 10px;
}
.ladder-head,
.lvl {
  display: grid;
  grid-template-columns: 1fr 1fr 1.4fr;
  gap: 6px;
  padding: 4px 8px;
  font-size: 12px;
  text-align: right;
}
.ladder-head.ask,
.lvl.ask {
  grid-template-columns: 1.4fr 1fr 1fr;
  text-align: left;
}
.ladder-head {
  color: var(--wx-text-faint);
  font-size: 9px;
  letter-spacing: 1.5px;
  padding-bottom: 6px;
  border-bottom: 1px solid var(--wx-border-soft);
  margin-bottom: 4px;
}
.lvl {
  position: relative;
  border-radius: 2px;
}
/* depth bar: bids grow right-to-left, asks left-to-right */
.lvl.bid {
  background: linear-gradient(
    to left,
    rgba(109, 176, 106, 0.16) var(--depth),
    transparent var(--depth)
  );
}
.lvl.ask {
  background: linear-gradient(
    to right,
    rgba(208, 85, 64, 0.16) var(--depth),
    transparent var(--depth)
  );
}
.lvl .px {
  font-weight: 700;
}
.lvl-empty {
  padding: 18px 8px;
  text-align: center;
  color: var(--wx-text-faint);
  font-size: 11px;
  letter-spacing: 2px;
}

/* ---- BUY / SELL toggle ---- */
.side-toggle {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 6px;
  margin-bottom: var(--wx-s4);
}
.side-btn {
  appearance: none;
  border: 1px solid var(--wx-border);
  background: var(--wx-inset);
  color: var(--wx-text-dim);
  font-weight: 800;
  font-size: 13px;
  letter-spacing: 3px;
  padding: 10px 0;
  border-radius: var(--wx-r-sm);
  cursor: pointer;
  transition: all 0.12s ease;
}
.side-btn.buy:hover {
  color: var(--wx-buy);
  border-color: var(--wx-buy-dim);
}
.side-btn.sell:hover {
  color: var(--wx-sell);
  border-color: var(--wx-sell-dim);
}
.side-btn.buy.active {
  background: rgba(109, 176, 106, 0.14);
  border-color: var(--wx-buy);
  color: var(--wx-buy);
  box-shadow: inset 0 0 12px rgba(109, 176, 106, 0.12);
}
.side-btn.sell.active {
  background: rgba(208, 85, 64, 0.14);
  border-color: var(--wx-sell);
  color: var(--wx-sell);
  box-shadow: inset 0 0 12px rgba(208, 85, 64, 0.12);
}

.field {
  margin-bottom: var(--wx-s4);
}
.field label {
  display: block;
  font-family: var(--wx-font-display);
  font-size: 10px;
  letter-spacing: 1.5px;
  text-transform: uppercase;
  color: var(--wx-text-dim);
  margin-bottom: 6px;
}
.hint {
  font-size: 11px;
  margin-top: 6px;
}
.est {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  font-size: 11px;
  letter-spacing: 1px;
  margin: 2px 0 var(--wx-s4);
  padding: 8px 10px;
  background: var(--wx-inset);
  border: 1px dashed var(--wx-border);
  border-radius: var(--wx-r-sm);
}
.est-val {
  color: var(--wx-amber-bright);
  font-weight: 700;
  font-size: 13px;
}
.submit {
  width: 100%;
}

@media (max-width: 860px) {
  .layout {
    grid-template-columns: 1fr;
  }
}
</style>
