<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useCatalogStore } from '@/stores/catalog'
import { inventoryApi, raidApi } from '@/api/endpoints'
import ItemSprite from '@/components/ItemSprite.vue'
import {
  dateTime,
  itemLedgerReasonLabel,
  raidStatusColor,
  raidStatusLabel,
  shortId,
} from '@/utils/format'
import { toastError } from '@/utils/toast'
import type { ItemLedgerEntryDto, RaidHistoryEntryDto } from '@/api/types'

// Raid records: a list of resolved raids (Extracted/Died) with recovered/lost
// items, plus a tab for the item ledger (append-only movement log, +/− deltas).
const catalog = useCatalogStore()

type Tab = 'raids' | 'ledger'
const tab = ref<Tab>('raids')

const raids = ref<RaidHistoryEntryDto[]>([])
const raidTotal = ref(0)
const raidPage = ref(1)
const raidSize = 10
const loadingRaids = ref(false)

const ledger = ref<ItemLedgerEntryDto[]>([])
const ledgerTotal = ref(0)
const ledgerPage = ref(1)
const ledgerSize = 20
const loadingLedger = ref(false)

function tplName(id: number): string {
  return catalog.get(id)?.name ?? `#${id}`
}

async function loadRaids(): Promise<void> {
  loadingRaids.value = true
  try {
    const res = await raidApi.history(raidPage.value, raidSize)
    raids.value = res.items
    raidTotal.value = res.totalCount
  } catch (err) {
    toastError(err, 'Could not load raid history.')
  } finally {
    loadingRaids.value = false
  }
}

async function loadLedger(): Promise<void> {
  loadingLedger.value = true
  try {
    const res = await inventoryApi.ledger(ledgerPage.value, ledgerSize)
    ledger.value = res.items
    ledgerTotal.value = res.totalCount
  } catch (err) {
    toastError(err, 'Could not load the item ledger.')
  } finally {
    loadingLedger.value = false
  }
}

onMounted(async () => {
  try {
    await catalog.ensureLoaded()
  } catch (err) {
    toastError(err)
  }
  await loadRaids()
})

watch(raidPage, loadRaids)
watch(ledgerPage, loadLedger)
watch(tab, (t) => {
  if (t === 'ledger' && ledger.value.length === 0) void loadLedger()
})

function itemsBySource(entry: RaidHistoryEntryDto, source: 'Brought' | 'Looted') {
  return entry.items.filter((i) => i.source === source)
}
const empty = computed(() => !loadingRaids.value && raids.value.length === 0)
</script>

<template>
  <div>
    <h1 class="wx-page-title">기록 · Records</h1>
    <p class="wx-page-sub">Your resolved raids and every item that moved in or out.</p>

    <div class="tabs mono">
      <button class="tab" :class="{ active: tab === 'raids' }" @click="tab = 'raids'">
        레이드 · Raids
      </button>
      <button class="tab" :class="{ active: tab === 'ledger' }" @click="tab = 'ledger'">
        원장 · Item Ledger
      </button>
    </div>

    <!-- ============ RAIDS ============ -->
    <div v-if="tab === 'raids'" v-loading="loadingRaids">
      <div v-if="empty" class="wx-empty">
        <img class="pixel" src="/sprites/ammo_box.svg" alt="" />
        No raids yet. Deploy from the 출격 screen.
      </div>

      <div class="raid-list">
        <section v-for="r in raids" :key="r.id" class="wx-panel raid-card">
          <header class="raid-head">
            <span
              class="outcome"
              :style="{ color: raidStatusColor(r.status), borderColor: raidStatusColor(r.status) }"
            >
              {{ raidStatusLabel(r.status) }}
            </span>
            <span class="raid-time mono">{{ dateTime(r.resolvedAt ?? r.startedAt) }}</span>
            <span class="raid-id mono wx-muted">#{{ shortId(r.id) }}</span>
          </header>

          <div class="raid-body">
            <div class="src-col">
              <div class="src-label brought">
                반입 · Brought ({{ itemsBySource(r, 'Brought').length }})
              </div>
              <div
                v-for="(it, i) in itemsBySource(r, 'Brought')"
                :key="`b${i}`"
                class="line"
                :class="{ lost: r.status === 'Died' }"
              >
                <ItemSprite
                  :icon="catalog.get(it.templateId)?.icon"
                  :category="catalog.get(it.templateId)?.category"
                  :rarity="catalog.get(it.templateId)?.rarity"
                  :size="28"
                />
                <span class="line-name">{{ tplName(it.templateId) }}</span>
                <span class="line-qty mono">×{{ it.quantity }}</span>
              </div>
              <p v-if="!itemsBySource(r, 'Brought').length" class="none mono">—</p>
            </div>

            <div class="src-col">
              <div class="src-label looted">
                획득 · Looted ({{ itemsBySource(r, 'Looted').length }})
              </div>
              <div
                v-for="(it, i) in itemsBySource(r, 'Looted')"
                :key="`l${i}`"
                class="line"
                :class="{ lost: r.status === 'Died' }"
              >
                <ItemSprite
                  :icon="catalog.get(it.templateId)?.icon"
                  :category="catalog.get(it.templateId)?.category"
                  :rarity="catalog.get(it.templateId)?.rarity"
                  :size="28"
                />
                <span class="line-name">{{ tplName(it.templateId) }}</span>
                <span class="line-qty mono">×{{ it.quantity }}</span>
              </div>
              <p v-if="!itemsBySource(r, 'Looted').length" class="none mono">—</p>
            </div>
          </div>

          <p class="raid-note mono" :class="r.status === 'Extracted' ? 'ok' : 'bad'">
            {{
              r.status === 'Extracted'
                ? 'Extracted — gear restored to its loadout / equipment spots; loot recovered.'
                : 'Killed in action — brought & looted items were lost.'
            }}
          </p>
        </section>
      </div>

      <el-pagination
        v-if="raidTotal > raidSize"
        v-model:current-page="raidPage"
        :page-size="raidSize"
        :total="raidTotal"
        layout="prev, pager, next"
        class="pager"
        background
      />
    </div>

    <!-- ============ LEDGER ============ -->
    <div v-else v-loading="loadingLedger">
      <el-table :data="ledger" size="small" empty-text="No item movements recorded yet">
        <el-table-column label="Time" width="140">
          <template #default="{ row }">
            <span class="mono wx-muted">{{ dateTime(row.createdAt) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="Item">
          <template #default="{ row }">
            <div class="led-item">
              <ItemSprite
                :icon="catalog.get(row.templateId)?.icon"
                :category="catalog.get(row.templateId)?.category"
                :rarity="catalog.get(row.templateId)?.rarity"
                :size="24"
              />
              <span>{{ tplName(row.templateId) }}</span>
              <span v-if="row.instanceId" class="mono wx-muted led-inst"
                >#{{ shortId(row.instanceId) }}</span
              >
            </div>
          </template>
        </el-table-column>
        <el-table-column label="Reason">
          <template #default="{ row }">
            <span class="mono">{{ itemLedgerReasonLabel(row.reason) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="Δ Qty" align="right" width="100">
          <template #default="{ row }">
            <span class="mono delta" :class="row.deltaQty < 0 ? 'neg' : 'pos'">
              {{ row.deltaQty > 0 ? '+' : '' }}{{ row.deltaQty }}
            </span>
          </template>
        </el-table-column>
      </el-table>

      <el-pagination
        v-if="ledgerTotal > ledgerSize"
        v-model:current-page="ledgerPage"
        :page-size="ledgerSize"
        :total="ledgerTotal"
        layout="prev, pager, next"
        class="pager"
        background
      />
    </div>
  </div>
</template>

<style scoped>
.tabs {
  display: flex;
  gap: 4px;
  margin-bottom: 18px;
  border-bottom: 1px solid var(--wx-border);
}
.tab {
  appearance: none;
  background: none;
  border: none;
  border-bottom: 2px solid transparent;
  color: var(--wx-text-dim);
  padding: 8px 14px;
  cursor: pointer;
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 2px;
  text-transform: uppercase;
}
.tab:hover {
  color: var(--wx-text);
}
.tab.active {
  color: var(--wx-amber-bright);
  border-bottom-color: var(--wx-amber);
}

.raid-list {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(360px, 1fr));
  gap: 14px;
}
.raid-head {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 12px;
}
.outcome {
  font-family: var(--wx-font-display);
  font-size: 11px;
  font-weight: 800;
  letter-spacing: 1.5px;
  text-transform: uppercase;
  padding: 3px 10px;
  border: 1px solid;
  border-radius: 999px;
}
.raid-time {
  font-size: 12px;
  color: var(--wx-text-dim);
}
.raid-id {
  margin-left: auto;
  font-size: 11px;
}
.raid-body {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 14px;
}
.src-label {
  font-family: var(--wx-font-display);
  font-size: 10px;
  font-weight: 800;
  letter-spacing: 1px;
  text-transform: uppercase;
  margin-bottom: 8px;
}
.src-label.brought {
  color: var(--wx-olive);
}
.src-label.looted {
  color: var(--wx-amber-bright);
}
.line {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 3px 0;
}
.line.lost {
  opacity: 0.5;
  filter: grayscale(0.5);
}
.line-name {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.line-qty {
  font-size: 12px;
  font-weight: 700;
  color: var(--wx-amber-bright);
}
.none {
  font-size: 12px;
  color: var(--wx-text-faint);
  margin: 0;
}
.raid-note {
  margin: 14px 0 0;
  font-size: 11px;
  letter-spacing: 0.3px;
}
.raid-note.ok {
  color: var(--wx-buy);
}
.raid-note.bad {
  color: var(--wx-sell);
}

.led-item {
  display: flex;
  align-items: center;
  gap: 8px;
}
.led-inst {
  font-size: 11px;
}
.delta {
  font-weight: 800;
}
.delta.pos {
  color: var(--wx-buy);
}
.delta.neg {
  color: var(--wx-sell);
}
.pager {
  margin-top: 18px;
  justify-content: center;
}
</style>
