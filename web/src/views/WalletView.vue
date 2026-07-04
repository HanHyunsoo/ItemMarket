<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { walletApi } from '@/api/endpoints'
import { caps, dateTime, ledgerReasonLabel, shortId, signedCaps } from '@/utils/format'
import { toastError } from '@/utils/toast'
import { onWalletChanged } from '@/realtime/marketHub'
import type { WalletDto, WalletLedgerEntryDto } from '@/api/types'

const wallet = ref<WalletDto | null>(null)
const entries = ref<WalletLedgerEntryDto[]>([])
const total = ref(0)
const page = ref(1)
const size = ref(15)
const loading = ref(false)

async function loadWallet() {
  try {
    wallet.value = await walletApi.get()
  } catch (err) {
    toastError(err, 'Could not load wallet.')
  }
}

async function loadLedger() {
  loading.value = true
  try {
    const res = await walletApi.ledger(page.value, size.value)
    entries.value = res.items
    total.value = res.totalCount
  } catch (err) {
    toastError(err, 'Could not load ledger.')
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  await Promise.all([loadWallet(), loadLedger()])
})

// Live: refresh balance + ledger when this player's wallet changes.
const offWalletChanged = onWalletChanged(() => {
  loadWallet()
  loadLedger()
})
onUnmounted(offWalletChanged)

function onPage(p: number) {
  page.value = p
  loadLedger()
}
</script>

<template>
  <div>
    <h1 class="wx-page-title">Wallet</h1>
    <p class="wx-page-sub">Bottle caps — the only currency that survived</p>

    <div class="balance wx-panel">
      <span class="cap-well">
        <img class="pixel cap-icon" src="/sprites/cap_coin.svg" alt="" />
      </span>
      <div>
        <div class="balance-label mono">CURRENT BALANCE</div>
        <div class="balance-val mono">{{ caps(wallet?.balance ?? 0) }} <span>caps</span></div>
      </div>
    </div>

    <h3 class="wx-section-title" style="margin-top: 24px">Ledger</h3>
    <div class="wx-panel">
      <el-table v-loading="loading" :data="entries" size="small" empty-text="No ledger activity">
        <el-table-column label="Time" width="140">
          <template #default="{ row }"
            ><span class="wx-muted">{{ dateTime(row.createdAt) }}</span></template
          >
        </el-table-column>
        <el-table-column label="Reason">
          <template #default="{ row }">{{ ledgerReasonLabel(row.reason) }}</template>
        </el-table-column>
        <el-table-column label="Ref" width="110">
          <template #default="{ row }"
            ><span class="mono wx-muted">{{ shortId(row.refId) }}</span></template
          >
        </el-table-column>
        <el-table-column label="Delta" align="right" width="130">
          <template #default="{ row }">
            <span class="mono" :class="row.delta < 0 ? 'wx-sell' : 'wx-buy'">{{
              signedCaps(row.delta)
            }}</span>
          </template>
        </el-table-column>
        <el-table-column label="Balance" align="right" width="130">
          <template #default="{ row }"
            ><span class="mono">{{ caps(row.balanceAfter) }}</span></template
          >
        </el-table-column>
      </el-table>
      <div class="pager">
        <el-pagination
          layout="prev, pager, next, total"
          :total="total"
          :page-size="size"
          :current-page="page"
          @current-change="onPage"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.balance {
  display: flex;
  align-items: center;
  gap: 18px;
  max-width: 480px;
}
.cap-well {
  display: grid;
  place-items: center;
  width: 76px;
  height: 76px;
  border-radius: 50%;
  border: 1px solid rgba(224, 163, 60, 0.35);
  background:
    radial-gradient(circle at 50% 40%, rgba(224, 163, 60, 0.22), transparent 72%), var(--wx-inset);
  flex: none;
}
.cap-icon {
  width: 52px;
  height: 52px;
}
.balance-label {
  font-size: 10px;
  letter-spacing: 3px;
  color: var(--wx-text-dim);
}
.balance-val {
  font-size: 34px;
  font-weight: 800;
  color: var(--wx-amber-bright);
}
.balance-val span {
  font-size: 14px;
  color: var(--wx-text-dim);
  font-weight: 400;
}
.pager {
  display: flex;
  justify-content: flex-end;
  margin-top: 12px;
}
</style>
