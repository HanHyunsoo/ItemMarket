<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessageBox } from 'element-plus'
import { useCatalogStore } from '@/stores/catalog'
import { ordersApi } from '@/api/endpoints'
import RarityTag from '@/components/RarityTag.vue'
import { caps, dateTime, orderStatusType, shortId } from '@/utils/format'
import { toastError, toastSuccess } from '@/utils/toast'
import type { OrderDto } from '@/api/types'

const catalog = useCatalogStore()
const router = useRouter()
const orders = ref<OrderDto[]>([])
const loading = ref(false)
const cancelling = ref<string | null>(null)
const showClosed = ref(false)

async function load() {
  loading.value = true
  try {
    await catalog.ensureLoaded()
    orders.value = await ordersApi.list()
  } catch (err) {
    toastError(err, 'Could not load orders.')
  } finally {
    loading.value = false
  }
}

onMounted(load)

const visible = computed(() =>
  orders.value.filter((o) => showClosed.value || o.status === 'Open' || o.status === 'PartiallyFilled'),
)

function isCancellable(o: OrderDto): boolean {
  return o.status === 'Open' || o.status === 'PartiallyFilled'
}

async function cancel(o: OrderDto) {
  try {
    await ElMessageBox.confirm(
      `Cancel this ${o.side.toLowerCase()} order? Escrow will be refunded.`,
      'Cancel order',
      { confirmButtonText: 'Cancel order', cancelButtonText: 'Keep', type: 'warning' },
    )
  } catch {
    return
  }
  cancelling.value = o.id
  try {
    await ordersApi.cancel(o.id)
    toastSuccess('Order cancelled — escrow refunded.')
    await load()
  } catch (err) {
    toastError(err, 'Could not cancel order.')
  } finally {
    cancelling.value = null
  }
}

function name(templateId: number): string {
  return catalog.get(templateId)?.name ?? `#${templateId}`
}
</script>

<template>
  <div>
    <div class="head">
      <div>
        <h1 class="wx-page-title">My Orders</h1>
        <p class="wx-page-sub">Open positions on the exchange</p>
      </div>
      <el-checkbox v-model="showClosed">Show filled / cancelled</el-checkbox>
    </div>

    <div class="wx-panel">
      <el-table v-loading="loading" :data="visible" empty-text="No orders">
        <el-table-column label="Item" min-width="180">
          <template #default="{ row }">
            <div class="item-cell" @click="router.push({ name: 'item', params: { id: row.itemTemplateId } })">
              <span class="link">{{ name(row.itemTemplateId) }}</span>
              <RarityTag v-if="catalog.get(row.itemTemplateId)" :rarity="catalog.get(row.itemTemplateId)!.rarity" />
            </div>
          </template>
        </el-table-column>
        <el-table-column label="Side" width="80">
          <template #default="{ row }">
            <span :class="row.side === 'Buy' ? 'wx-buy' : 'wx-sell'" class="side">{{ row.side }}</span>
          </template>
        </el-table-column>
        <el-table-column label="Price" align="right" width="110">
          <template #default="{ row }"><span class="mono">{{ caps(row.unitPrice) }}</span></template>
        </el-table-column>
        <el-table-column label="Remaining / Qty" align="right" width="140">
          <template #default="{ row }">
            <span class="mono">{{ row.remainingQuantity }} / {{ row.quantity }}</span>
          </template>
        </el-table-column>
        <el-table-column label="Status" width="140">
          <template #default="{ row }">
            <el-tag :type="orderStatusType(row.status)" effect="dark" size="small">{{ row.status }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="Instance" width="110">
          <template #default="{ row }"><span class="mono wx-muted">{{ shortId(row.instanceId) }}</span></template>
        </el-table-column>
        <el-table-column label="Created" width="130">
          <template #default="{ row }"><span class="wx-muted">{{ dateTime(row.createdAt) }}</span></template>
        </el-table-column>
        <el-table-column label="" width="100" align="right">
          <template #default="{ row }">
            <el-button
              v-if="isCancellable(row)"
              size="small"
              type="danger"
              plain
              :loading="cancelling === row.id"
              @click="cancel(row)"
            >
              Cancel
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<style scoped>
.head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 16px;
}
.item-cell {
  display: flex;
  align-items: center;
  gap: 8px;
  cursor: pointer;
}
.link:hover {
  color: var(--wx-accent);
}
.side {
  font-weight: 700;
  letter-spacing: 1px;
}
</style>
