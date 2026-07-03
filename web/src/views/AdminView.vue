<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useCatalogStore } from '@/stores/catalog'
import { adminApi } from '@/api/endpoints'
import { caps, dateTime, orderStatusType, shortId } from '@/utils/format'
import { toastError, toastSuccess } from '@/utils/toast'
import { SEED_PLAYERS } from '@/api/types'
import type { OrderDto, OrderStatus, TradeDto } from '@/api/types'

const catalog = useCatalogStore()
const tab = ref('grant')

// ---- Forms ----
const grantStack = reactive({ playerId: '', templateId: null as number | null, quantity: 1, busy: false })
const grantInst = reactive({
  playerId: '',
  templateId: null as number | null,
  durability: null as number | null,
  attachments: '',
  busy: false,
})
const adjust = reactive({ playerId: '', delta: 0, reason: '', busy: false })
const forceCancel = reactive({ orderId: '', reason: '', busy: false })

// ---- Paged tables ----
const orders = ref<OrderDto[]>([])
const ordersTotal = ref(0)
const ordersPage = ref(1)
const ordersFilter = reactive({ templateId: null as number | null, status: '' as OrderStatus | '' })
const ordersLoading = ref(false)

const trades = ref<TradeDto[]>([])
const tradesTotal = ref(0)
const tradesPage = ref(1)
const tradesLoading = ref(false)

const PAGE_SIZE = 15
const STATUSES: OrderStatus[] = ['Open', 'PartiallyFilled', 'Filled', 'Cancelled']

onMounted(() => catalog.ensureLoaded().catch((e) => toastError(e)))

async function doGrantStack() {
  if (!grantStack.playerId || grantStack.templateId === null) return
  grantStack.busy = true
  try {
    await adminApi.grantStack({
      playerId: grantStack.playerId,
      templateId: grantStack.templateId,
      quantity: grantStack.quantity,
    })
    toastSuccess('Stack granted.')
  } catch (err) {
    toastError(err, 'Grant failed.')
  } finally {
    grantStack.busy = false
  }
}

async function doGrantInstance() {
  if (!grantInst.playerId || grantInst.templateId === null) return
  grantInst.busy = true
  try {
    const attachments = grantInst.attachments
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean)
    await adminApi.grantInstance({
      playerId: grantInst.playerId,
      templateId: grantInst.templateId,
      durability: grantInst.durability,
      attachments: attachments.length ? attachments : null,
    })
    toastSuccess('Instance granted.')
  } catch (err) {
    toastError(err, 'Grant failed.')
  } finally {
    grantInst.busy = false
  }
}

async function doAdjust() {
  if (!adjust.playerId || !adjust.reason) return
  adjust.busy = true
  try {
    const w = await adminApi.adjustWallet({
      playerId: adjust.playerId,
      delta: adjust.delta,
      reason: adjust.reason,
    })
    toastSuccess(`Wallet adjusted — new balance ${caps(w.balance)} caps.`)
  } catch (err) {
    toastError(err, 'Adjust failed.')
  } finally {
    adjust.busy = false
  }
}

async function doForceCancel() {
  if (!forceCancel.orderId || !forceCancel.reason) return
  forceCancel.busy = true
  try {
    await adminApi.forceCancelOrder({ orderId: forceCancel.orderId, reason: forceCancel.reason })
    toastSuccess('Order force-cancelled.')
    if (tab.value === 'orders') loadOrders()
  } catch (err) {
    toastError(err, 'Force-cancel failed.')
  } finally {
    forceCancel.busy = false
  }
}

async function loadOrders() {
  ordersLoading.value = true
  try {
    const res = await adminApi.orders({
      templateId: ordersFilter.templateId ?? undefined,
      status: ordersFilter.status || undefined,
      page: ordersPage.value,
      size: PAGE_SIZE,
    })
    orders.value = res.items
    ordersTotal.value = res.totalCount
  } catch (err) {
    toastError(err, 'Could not load orders.')
  } finally {
    ordersLoading.value = false
  }
}

async function loadTrades() {
  tradesLoading.value = true
  try {
    const res = await adminApi.trades(tradesPage.value, PAGE_SIZE)
    trades.value = res.items
    tradesTotal.value = res.totalCount
  } catch (err) {
    toastError(err, 'Could not load trades.')
  } finally {
    tradesLoading.value = false
  }
}

function onTab(name: string | number) {
  if (name === 'orders' && orders.value.length === 0) loadOrders()
  if (name === 'trades' && trades.value.length === 0) loadTrades()
}

function name(templateId: number): string {
  return catalog.get(templateId)?.name ?? `#${templateId}`
}
function playerName(id: string): string {
  return SEED_PLAYERS.find((p) => p.id === id)?.displayName ?? shortId(id)
}
</script>

<template>
  <div>
    <h1 class="wx-page-title">Admin Console</h1>
    <p class="wx-page-sub">Operator tools — grants, wallet adjustments, interventions</p>

    <el-tabs v-model="tab" @tab-change="onTab">
      <!-- GRANTS -->
      <el-tab-pane label="Grants" name="grant">
        <div class="cols">
          <section class="wx-panel">
            <h3 class="wx-section-title">Grant Stack</h3>
            <div class="field">
              <label>Player</label>
              <el-select v-model="grantStack.playerId" placeholder="Player" style="width: 100%">
                <el-option v-for="p in SEED_PLAYERS" :key="p.id" :label="p.displayName" :value="p.id" />
              </el-select>
            </div>
            <div class="field">
              <label>Item template</label>
              <el-select v-model="grantStack.templateId" filterable placeholder="Item" style="width: 100%">
                <el-option
                  v-for="it in catalog.items.filter((i) => i.stackable)"
                  :key="it.id"
                  :label="`${it.name} (#${it.id})`"
                  :value="it.id"
                />
              </el-select>
            </div>
            <div class="field">
              <label>Quantity</label>
              <el-input-number v-model="grantStack.quantity" :min="1" style="width: 100%" controls-position="right" />
            </div>
            <el-button type="primary" :loading="grantStack.busy" @click="doGrantStack">Grant Stack</el-button>
          </section>

          <section class="wx-panel">
            <h3 class="wx-section-title">Grant Instance</h3>
            <div class="field">
              <label>Player</label>
              <el-select v-model="grantInst.playerId" placeholder="Player" style="width: 100%">
                <el-option v-for="p in SEED_PLAYERS" :key="p.id" :label="p.displayName" :value="p.id" />
              </el-select>
            </div>
            <div class="field">
              <label>Item template (unique)</label>
              <el-select v-model="grantInst.templateId" filterable placeholder="Item" style="width: 100%">
                <el-option
                  v-for="it in catalog.items.filter((i) => !i.stackable)"
                  :key="it.id"
                  :label="`${it.name} (#${it.id})`"
                  :value="it.id"
                />
              </el-select>
            </div>
            <div class="field">
              <label>Durability (optional)</label>
              <el-input-number v-model="grantInst.durability" :min="0" style="width: 100%" controls-position="right" />
            </div>
            <div class="field">
              <label>Attachments (comma-separated)</label>
              <el-input v-model="grantInst.attachments" placeholder="scope, suppressor" />
            </div>
            <el-button type="primary" :loading="grantInst.busy" @click="doGrantInstance">Grant Instance</el-button>
          </section>
        </div>
      </el-tab-pane>

      <!-- WALLET / INTERVENTION -->
      <el-tab-pane label="Wallet & Orders" name="ops">
        <div class="cols">
          <section class="wx-panel">
            <h3 class="wx-section-title">Adjust Wallet</h3>
            <div class="field">
              <label>Player</label>
              <el-select v-model="adjust.playerId" placeholder="Player" style="width: 100%">
                <el-option v-for="p in SEED_PLAYERS" :key="p.id" :label="p.displayName" :value="p.id" />
              </el-select>
            </div>
            <div class="field">
              <label>Delta (+/− caps)</label>
              <el-input-number v-model="adjust.delta" :step="100" style="width: 100%" controls-position="right" />
            </div>
            <div class="field">
              <label>Reason</label>
              <el-input v-model="adjust.reason" placeholder="e.g. event reward" />
            </div>
            <el-button type="primary" :loading="adjust.busy" @click="doAdjust">Apply Adjustment</el-button>
          </section>

          <section class="wx-panel">
            <h3 class="wx-section-title">Force-Cancel Order</h3>
            <div class="field">
              <label>Order ID (GUID)</label>
              <el-input v-model="forceCancel.orderId" placeholder="00000000-0000-…" />
            </div>
            <div class="field">
              <label>Reason</label>
              <el-input v-model="forceCancel.reason" placeholder="e.g. suspected manipulation" />
            </div>
            <el-button type="danger" :loading="forceCancel.busy" @click="doForceCancel">Force Cancel</el-button>
          </section>
        </div>
      </el-tab-pane>

      <!-- ALL ORDERS -->
      <el-tab-pane label="All Orders" name="orders">
        <div class="wx-panel">
          <div class="filters">
            <el-select v-model="ordersFilter.templateId" filterable clearable placeholder="Item" style="width: 220px">
              <el-option v-for="it in catalog.items" :key="it.id" :label="`${it.name} (#${it.id})`" :value="it.id" />
            </el-select>
            <el-select v-model="ordersFilter.status" clearable placeholder="Status" style="width: 160px">
              <el-option v-for="s in STATUSES" :key="s" :label="s" :value="s" />
            </el-select>
            <el-button @click="((ordersPage = 1), loadOrders())">Apply</el-button>
          </div>
          <el-table v-loading="ordersLoading" :data="orders" size="small" empty-text="No orders">
            <el-table-column label="Player" width="140">
              <template #default="{ row }">{{ playerName(row.playerId) }}</template>
            </el-table-column>
            <el-table-column label="Item" min-width="150">
              <template #default="{ row }">{{ name(row.itemTemplateId) }}</template>
            </el-table-column>
            <el-table-column label="Side" width="70">
              <template #default="{ row }">
                <span :class="row.side === 'Buy' ? 'wx-buy' : 'wx-sell'">{{ row.side }}</span>
              </template>
            </el-table-column>
            <el-table-column label="Price" align="right" width="100">
              <template #default="{ row }"><span class="mono">{{ caps(row.unitPrice) }}</span></template>
            </el-table-column>
            <el-table-column label="Rem/Qty" align="right" width="100">
              <template #default="{ row }"><span class="mono">{{ row.remainingQuantity }}/{{ row.quantity }}</span></template>
            </el-table-column>
            <el-table-column label="Status" width="130">
              <template #default="{ row }">
                <el-tag :type="orderStatusType(row.status)" effect="dark" size="small">{{ row.status }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column label="Order ID" width="110">
              <template #default="{ row }"><span class="mono wx-muted">{{ shortId(row.id) }}</span></template>
            </el-table-column>
          </el-table>
          <div class="pager">
            <el-pagination
              layout="prev, pager, next, total"
              :total="ordersTotal"
              :page-size="PAGE_SIZE"
              :current-page="ordersPage"
              @current-change="((p: number) => ((ordersPage = p), loadOrders()))"
            />
          </div>
        </div>
      </el-tab-pane>

      <!-- ALL TRADES -->
      <el-tab-pane label="All Trades" name="trades">
        <div class="wx-panel">
          <el-table v-loading="tradesLoading" :data="trades" size="small" empty-text="No trades">
            <el-table-column label="Time" width="130">
              <template #default="{ row }"><span class="wx-muted">{{ dateTime(row.executedAt) }}</span></template>
            </el-table-column>
            <el-table-column label="Item" min-width="150">
              <template #default="{ row }">{{ name(row.itemTemplateId) }}</template>
            </el-table-column>
            <el-table-column label="Buyer" width="140">
              <template #default="{ row }">{{ playerName(row.buyerId) }}</template>
            </el-table-column>
            <el-table-column label="Seller" width="140">
              <template #default="{ row }">{{ playerName(row.sellerId) }}</template>
            </el-table-column>
            <el-table-column label="Price" align="right" width="100">
              <template #default="{ row }"><span class="mono">{{ caps(row.unitPrice) }}</span></template>
            </el-table-column>
            <el-table-column label="Qty" align="right" width="70">
              <template #default="{ row }"><span class="mono">{{ row.quantity }}</span></template>
            </el-table-column>
            <el-table-column label="Fee" align="right" width="90">
              <template #default="{ row }"><span class="mono wx-muted">{{ caps(row.feeAmount) }}</span></template>
            </el-table-column>
          </el-table>
          <div class="pager">
            <el-pagination
              layout="prev, pager, next, total"
              :total="tradesTotal"
              :page-size="PAGE_SIZE"
              :current-page="tradesPage"
              @current-change="((p: number) => ((tradesPage = p), loadTrades()))"
            />
          </div>
        </div>
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<style scoped>
.cols {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
  align-items: start;
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
.filters {
  display: flex;
  gap: 12px;
  margin-bottom: 12px;
  flex-wrap: wrap;
}
.pager {
  display: flex;
  justify-content: flex-end;
  margin-top: 12px;
}
@media (max-width: 800px) {
  .cols {
    grid-template-columns: 1fr;
  }
}
</style>
