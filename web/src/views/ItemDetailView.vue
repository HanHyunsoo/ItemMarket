<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useCatalogStore } from '@/stores/catalog'
import { inventoryApi, marketApi, ordersApi } from '@/api/endpoints'
import ItemSprite from '@/components/ItemSprite.vue'
import RarityTag from '@/components/RarityTag.vue'
import { caps, dateTime, shortId } from '@/utils/format'
import { toastError, toastSuccess } from '@/utils/toast'
import type {
  ItemInstanceDto,
  OrderBookSnapshotDto,
  OrderSide,
  TradeDto,
} from '@/api/types'

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
    <button class="back" @click="router.push({ name: 'market' })">&larr; Back to market</button>

    <div class="head">
      <ItemSprite :icon="template.icon" :category="template.category" :rarity="template.rarity" :size="72" />
      <div>
        <h1 class="wx-page-title">{{ template.name }}</h1>
        <div class="head-meta">
          <RarityTag :rarity="template.rarity" />
          <span class="wx-muted">{{ template.category }}</span>
          <span class="wx-muted">·</span>
          <span class="wx-muted">{{ template.stackable ? 'Stackable' : 'Unique instance' }}</span>
          <span class="wx-muted">·</span>
          <span class="wx-muted">base {{ caps(template.baseValue) }} caps</span>
          <span class="wx-muted mono">#{{ template.id }} {{ template.code }}</span>
        </div>
      </div>
    </div>

    <div class="layout">
      <!-- Order book -->
      <section class="wx-panel">
        <div class="panel-head">
          <h3 class="wx-section-title">Order Book</h3>
          <div class="spread mono">
            <span class="wx-buy">bid {{ bestBid !== null ? caps(bestBid) : '—' }}</span>
            <span class="wx-sell">ask {{ bestAsk !== null ? caps(bestAsk) : '—' }}</span>
          </div>
        </div>
        <div v-loading="loadingBook" class="book">
          <div class="book-col">
            <div class="book-h wx-buy">BIDS (buy)</div>
            <el-table :data="book?.bids ?? []" size="small" empty-text="No bids">
              <el-table-column label="Price" align="right">
                <template #default="{ row }"><span class="mono wx-buy">{{ caps(row.unitPrice) }}</span></template>
              </el-table-column>
              <el-table-column label="Qty" align="right">
                <template #default="{ row }"><span class="mono">{{ row.quantity }}</span></template>
              </el-table-column>
              <el-table-column label="Orders" align="right">
                <template #default="{ row }"><span class="mono wx-muted">{{ row.orderCount }}</span></template>
              </el-table-column>
            </el-table>
          </div>
          <div class="book-col">
            <div class="book-h wx-sell">ASKS (sell)</div>
            <el-table :data="book?.asks ?? []" size="small" empty-text="No asks">
              <el-table-column label="Price" align="right">
                <template #default="{ row }"><span class="mono wx-sell">{{ caps(row.unitPrice) }}</span></template>
              </el-table-column>
              <el-table-column label="Qty" align="right">
                <template #default="{ row }"><span class="mono">{{ row.quantity }}</span></template>
              </el-table-column>
              <el-table-column label="Orders" align="right">
                <template #default="{ row }"><span class="mono wx-muted">{{ row.orderCount }}</span></template>
              </el-table-column>
            </el-table>
          </div>
        </div>
      </section>

      <!-- Place order -->
      <section class="wx-panel">
        <h3 class="wx-section-title">Place Order</h3>
        <el-radio-group v-model="form.side" class="side-toggle">
          <el-radio-button value="Buy">BUY</el-radio-button>
          <el-radio-button value="Sell">SELL</el-radio-button>
        </el-radio-group>

        <div class="field">
          <label>Unit price (caps)</label>
          <el-input-number v-model="form.unitPrice" :min="1" :step="1" controls-position="right" style="width: 100%" />
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
          <el-input-number v-model="form.quantity" :min="1" :step="1" controls-position="right" style="width: 100%" />
        </div>

        <div class="est mono wx-muted">
          Est. total: {{ caps(form.unitPrice * (isUnique && form.side === 'Sell' ? 1 : form.quantity)) }} caps
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
        <el-table v-loading="loadingTrades" :data="trades" size="small" empty-text="No trades yet">
          <el-table-column label="Time" width="120">
            <template #default="{ row }"><span class="wx-muted">{{ dateTime(row.executedAt) }}</span></template>
          </el-table-column>
          <el-table-column label="Price" align="right">
            <template #default="{ row }"><span class="mono">{{ caps(row.unitPrice) }}</span></template>
          </el-table-column>
          <el-table-column label="Qty" align="right">
            <template #default="{ row }"><span class="mono">{{ row.quantity }}</span></template>
          </el-table-column>
          <el-table-column label="Fee" align="right">
            <template #default="{ row }"><span class="mono wx-muted">{{ caps(row.feeAmount) }}</span></template>
          </el-table-column>
        </el-table>
      </section>
    </div>
  </div>
  <div v-else class="wx-empty">Loading item…</div>
</template>

<style scoped>
.back {
  background: none;
  border: none;
  color: var(--wx-text-dim);
  cursor: pointer;
  font-size: 12px;
  letter-spacing: 1px;
  margin-bottom: 12px;
  padding: 0;
}
.back:hover {
  color: var(--wx-accent);
}
.head {
  display: flex;
  gap: 16px;
  align-items: center;
  margin-bottom: 24px;
}
.head-meta {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  font-size: 12px;
  text-transform: uppercase;
  letter-spacing: 1px;
  margin-top: 6px;
}
.layout {
  display: grid;
  grid-template-columns: 1fr 340px;
  gap: 16px;
  align-items: start;
}
.trades {
  grid-column: 1 / -1;
}
.panel-head {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
}
.spread {
  display: flex;
  gap: 14px;
  font-size: 12px;
}
.book {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 14px;
}
.book-h {
  font-size: 11px;
  letter-spacing: 1.5px;
  font-weight: 700;
  margin-bottom: 6px;
}
.side-toggle {
  margin-bottom: 16px;
  width: 100%;
}
.side-toggle :deep(.el-radio-button) {
  width: 50%;
}
.side-toggle :deep(.el-radio-button__inner) {
  width: 100%;
}
.field {
  margin-bottom: 14px;
}
.field label {
  display: block;
  font-size: 11px;
  letter-spacing: 1px;
  text-transform: uppercase;
  color: var(--wx-text-dim);
  margin-bottom: 6px;
}
.hint {
  font-size: 11px;
  margin-top: 6px;
}
.est {
  font-size: 12px;
  margin: 6px 0 14px;
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
