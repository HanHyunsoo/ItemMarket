<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessageBox } from 'element-plus'
import { useCatalogStore } from '@/stores/catalog'
import { equipmentApi, raidApi, stashApi } from '@/api/endpoints'
import { notifyWalletChanged } from '@/realtime/marketHub'
import ItemGrid from '@/components/ItemGrid.vue'
import ItemSprite from '@/components/ItemSprite.vue'
import RaidItemRow from '@/components/RaidItemRow.vue'
import { dateTime, shortId } from '@/utils/format'
import { toastError, toastSuccess } from '@/utils/toast'
import { ApiClientError } from '@/api/client'
import type { EquipSlot, EquipmentDto, NestedContainerDto, RaidSessionDto, StashDto } from '@/api/types'

const SLOT_LABEL: Record<EquipSlot, string> = {
  Helmet: 'Helmet · 헬멧',
  Armor: 'Armor · 방어구',
  Weapon: 'Weapon · 무기',
  Backpack: 'Backpack · 배낭',
  Rig: 'Rig · 리그',
}

const catalog = useCatalogStore()
const router = useRouter()

// The live active raid (from GET /api/raid or start/loot). `outcome` holds a
// just-resolved session (extract/die) to render the result screen — the server no
// longer returns it via GET, so we keep it locally until the user returns.
const raid = ref<RaidSessionDto | null>(null)
const outcome = ref<RaidSessionDto | null>(null)

// What gets brought in: everything outside the Stash — equipped gear, the innate
// pockets, and the contents of any equipped backpack/rig. The old separate Loadout
// container no longer exists; equipment alone is enough to deploy with.
const equipment = ref<EquipmentDto | null>(null)
const pockets = ref<StashDto | null>(null)

const loading = ref(false)
const deploying = ref(false)
const looting = ref(false)
const resolving = ref(false)

// loot form
const lootTemplateId = ref<number | null>(null)
const lootQty = ref(1)

// prep | active | outcome — drives which panel shows.
const mode = computed<'prep' | 'active' | 'outcome'>(() => {
  if (outcome.value) return 'outcome'
  if (raid.value && raid.value.status === 'Active') return 'active'
  return 'prep'
})

const equippedSlots = computed(() => equipment.value?.slots ?? [])
const rig = computed(() => equipment.value?.containers.find((c) => c.slot === 'Rig') ?? null)
const backpack = computed(
  () => equipment.value?.containers.find((c) => c.slot === 'Backpack') ?? null,
)
function nestedStash(c: NestedContainerDto): StashDto {
  return {
    playerId: equipment.value?.playerId ?? '',
    container: 'Container',
    gridW: c.gridW,
    gridH: c.gridH,
    placements: c.placements,
    unplaced: [],
  }
}

// Purely informational — nothing gates the deploy button. Equipment alone (with
// empty pockets and no backpack/rig) is a perfectly valid state to deploy with.
const bringingNothing = computed(
  () =>
    equippedSlots.value.length === 0 &&
    !!pockets.value &&
    pockets.value.placements.length === 0 &&
    !rig.value &&
    !backpack.value,
)

const broughtItems = computed(() => raid.value?.items.filter((i) => i.source === 'Brought') ?? [])
const lootedItems = computed(() => raid.value?.items.filter((i) => i.source === 'Looted') ?? [])
const atRiskCount = computed(() => raid.value?.items.reduce((n, i) => n + i.quantity, 0) ?? 0)

// ── 레이드 타이머(카운트다운) + 사망확률 미터 ──────────────────────────────
// 마감(deadlineAt)까지 남은 시간을 1초 틱으로 표시한다. 0이 되면 다음 extract/loot에서
// 서버가 탈출 실패=사망으로 정산한다(lazy expiry). 사망확률은 loot마다 상승한다.
const now = ref(Date.now())
let ticker: ReturnType<typeof setInterval> | null = null

const deadlineMs = computed(() =>
  raid.value?.deadlineAt ? new Date(raid.value.deadlineAt).getTime() : null,
)
const remainingMs = computed(() =>
  deadlineMs.value === null ? null : Math.max(0, deadlineMs.value - now.value),
)
const expired = computed(() => remainingMs.value !== null && remainingMs.value <= 0)
const remainingLabel = computed(() => {
  if (remainingMs.value === null) return '--:--'
  const s = Math.floor(remainingMs.value / 1000)
  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`
})
// 남은 시간이 30초 이하이면 긴박(빨강 강조).
const timeCritical = computed(() => remainingMs.value !== null && remainingMs.value <= 30_000)

const deathChancePct = computed(() => Math.min(100, (raid.value?.deathChanceBps ?? 0) / 100))
const survivalPct = computed(() => Math.max(0, 100 - deathChancePct.value))

const lootOptions = computed(() => [...catalog.items].sort((a, b) => a.name.localeCompare(b.name)))

function tplName(id: number): string {
  return catalog.get(id)?.name ?? `#${id}`
}

async function loadDeployPreview(): Promise<void> {
  const [e, p] = await Promise.all([equipmentApi.get(), stashApi.get('Pockets')])
  equipment.value = e
  pockets.value = p
}

onMounted(async () => {
  loading.value = true
  try {
    await catalog.ensureLoaded()
    const [r] = await Promise.all([raidApi.get(), loadDeployPreview()])
    raid.value = r
  } catch (err) {
    toastError(err, 'Could not reach the raid controller.')
  } finally {
    loading.value = false
  }
  ticker = setInterval(() => (now.value = Date.now()), 1000)
})

onUnmounted(() => {
  if (ticker) clearInterval(ticker)
})

async function onDeploy(): Promise<void> {
  deploying.value = true
  try {
    raid.value = await raidApi.start()
    toastSuccess('출격 — 레이드에 진입했습니다.')
  } catch (err) {
    // Already deployed elsewhere: sync to the live raid instead of erroring out.
    if (err instanceof ApiClientError && err.apiError.code === 'RaidActive') {
      raid.value = await raidApi.get()
    }
    toastError(err, 'Deploy failed.')
  } finally {
    deploying.value = false
  }
}

async function onLoot(): Promise<void> {
  if (lootTemplateId.value === null) return
  looting.value = true
  try {
    const res = await raidApi.loot({ templateId: lootTemplateId.value, quantity: lootQty.value })
    // 마감을 넘겨 loot를 시도하면 서버가 탈출 실패=사망으로 정산해 DIED를 돌려준다.
    if (res.status === 'Died') {
      outcome.value = res
      raid.value = null
      toastError(null, '시간 초과 — 탈출하지 못하고 사망했습니다. 아이템이 소실되었습니다.')
      await afterResolve()
      return
    }
    raid.value = res
    toastSuccess(`획득 — ${tplName(lootTemplateId.value)} ×${lootQty.value}`)
    lootQty.value = 1
  } catch (err) {
    toastError(err, 'Loot failed.')
  } finally {
    looting.value = false
  }
}

// Shared post-resolve refresh: header caps chip (via wallet fan-out) plus the
// prep-screen deploy preview, which the server has now restored/emptied. Inventory/
// Stash views re-fetch on their own navigation.
async function afterResolve(): Promise<void> {
  notifyWalletChanged()
  try {
    await loadDeployPreview()
  } catch {
    /* non-fatal — prep screen will retry on return */
  }
}

async function onExtract(): Promise<void> {
  resolving.value = true
  try {
    // 탈출 시도는 확률(또는 마감 초과)로 사망할 수 있다 — 서버가 EXTRACTED/DIED를 판정해 돌려준다.
    const res = await raidApi.extract()
    outcome.value = res
    raid.value = null
    if (res.status === 'Extracted')
      toastSuccess('탈출 성공 — 장비는 제자리로 복귀, 전리품은 회수되었습니다.')
    else
      toastError(null, '탈출 실패 — 사망했습니다. 반입·획득 아이템이 모두 소실되었습니다.')
    await afterResolve()
  } catch (err) {
    if (err instanceof ApiClientError && err.apiError.code === 'RaidNotFound') {
      raid.value = await raidApi.get()
    }
    toastError(err, 'Extraction failed.')
  } finally {
    resolving.value = false
  }
}

async function onDie(): Promise<void> {
  try {
    await ElMessageBox.confirm(
      '사망 처리하면 반입·획득한 모든 아이템이 소실됩니다. 계속할까요?',
      '사망 확정',
      { confirmButtonText: '사망 확정', cancelButtonText: '취소', type: 'error' },
    )
  } catch {
    return
  }
  resolving.value = true
  try {
    outcome.value = await raidApi.die()
    raid.value = null
    toastSuccess('사망 처리됨 — 아이템이 소실되었습니다.')
    await afterResolve()
  } catch (err) {
    if (err instanceof ApiClientError && err.apiError.code === 'RaidNotFound') {
      raid.value = await raidApi.get()
    }
    toastError(err, 'Resolve failed.')
  } finally {
    resolving.value = false
  }
}

async function onReturn(): Promise<void> {
  outcome.value = null
  raid.value = null
  loading.value = true
  try {
    const [r] = await Promise.all([raidApi.get(), loadDeployPreview()])
    raid.value = r
  } catch (err) {
    toastError(err, 'Could not reload the staging area.')
  } finally {
    loading.value = false
  }
}

function goGear(): void {
  router.push({ name: 'gear' })
}
</script>

<template>
  <div v-loading="loading">
    <h1 class="wx-page-title">출격 · Extraction Raid</h1>
    <p class="wx-page-sub">
      착용 장비 + 주머니(+백팩/리그 내용물)를 챙겨 출격 — 획득하고, 탈출하면 귀속, 사망하면 소실. High
      risk, high reward.
    </p>

    <!-- ============ PREP: no active raid ============ -->
    <div v-if="mode === 'prep' && pockets" class="stage">
      <section class="wx-panel prep-preview">
        <div class="panel-head">
          <span class="wx-section-title">반입 프리뷰 · At Risk</span>
        </div>
        <p class="hint mono">
          착용 장비 + 주머니(+백팩/리그 내용물)가 그대로 반입됩니다. 배치를 바꾸려면
          <a class="link" @click="goGear">장비 화면</a>에서 조정하세요.
        </p>

        <div v-if="bringingNothing" class="wx-empty">
          <img class="pixel" src="/sprites/ammo_box.svg" alt="" />
          맨몸 출격 — 착용 장비도, 주머니 내용물도 없습니다. 그래도 출격은 가능합니다.
        </div>

        <template v-else>
          <div v-if="equippedSlots.length" class="equip-summary">
            <div v-for="s in equippedSlots" :key="s.slot" class="equip-chip" :title="SLOT_LABEL[s.slot]">
              <ItemSprite
                :icon="catalog.get(s.templateId)?.icon"
                :category="catalog.get(s.templateId)?.category"
                :rarity="catalog.get(s.templateId)?.rarity"
                :size="34"
              />
              <span class="equip-chip-name mono">{{ SLOT_LABEL[s.slot] }}</span>
            </div>
          </div>
          <p v-else class="empty-line mono">착용한 장비가 없습니다.</p>

          <div class="grid-head">
            <span class="grid-label">Pockets · 주머니</span>
            <span class="grid-cap mono">{{ pockets.gridW }}×{{ pockets.gridH }}</span>
          </div>
          <div class="grid-scroll">
            <ItemGrid :stash="pockets" :busy="true" :show-tray="true" />
          </div>

          <template v-if="rig">
            <div class="grid-head">
              <span class="grid-label">Rig · 리그</span>
              <span class="grid-cap mono">{{ rig.gridW }}×{{ rig.gridH }}</span>
            </div>
            <div class="grid-scroll">
              <ItemGrid :stash="nestedStash(rig)" :busy="true" :show-tray="false" />
            </div>
          </template>

          <template v-if="backpack">
            <div class="grid-head">
              <span class="grid-label">Backpack · 배낭</span>
              <span class="grid-cap mono">{{ backpack.gridW }}×{{ backpack.gridH }}</span>
            </div>
            <div class="grid-scroll">
              <ItemGrid :stash="nestedStash(backpack)" :busy="true" :show-tray="false" />
            </div>
          </template>
        </template>
      </section>

      <aside class="wx-panel deploy-panel">
        <div class="deploy-brief">
          <span class="wx-section-title">출격 브리핑</span>
          <ul class="brief-list">
            <li>
              <span class="wx-buy">탈출(Extract)</span> — 반입 장비는 원래 자리(장비/주머니/백팩·리그)로
              복귀, 획득 전리품은 회수
            </li>
            <li><span class="wx-sell">사망(Die)</span> — 반입 + 획득 아이템 전부 소실 (창고는 안전)</li>
            <li>착용 장비만으로도 출격할 수 있습니다 — 주머니가 비어 있어도 무방합니다.</li>
          </ul>
        </div>
        <el-button
          type="primary"
          size="large"
          class="deploy-btn"
          :loading="deploying"
          @click="onDeploy"
        >
          출격 · DEPLOY
        </el-button>
      </aside>
    </div>

    <!-- ============ ACTIVE: in-raid ============ -->
    <div v-else-if="mode === 'active' && raid" class="stage">
      <section class="wx-panel manifest-panel">
        <div class="panel-head">
          <span class="wx-section-title">위험 노출 매니페스트 · At Risk</span>
          <span class="head-badges">
            <span
              class="countdown mono"
              :class="{ critical: timeCritical, expired }"
              title="탈출 제한시간 — 초과 시 탈출 실패(사망)"
            >
              ⏱ {{ expired ? '시간 초과' : remainingLabel }}
            </span>
            <span class="risk-badge mono">{{ atRiskCount }} 점</span>
          </span>
        </div>
        <p v-if="expired" class="atrisk-warn mono expired-warn">
          ⏱ 제한시간 초과 — 지금 탈출/획득을 시도하면 <b>실패(사망)</b>하고 아이템이 소실됩니다.
        </p>
        <p v-else class="atrisk-warn mono">
          ⚠ 아래 아이템은 전투 지역에 노출되어 있습니다 — 사망 시 전부 소실됩니다.
        </p>

        <div class="manifest-group">
          <div class="group-label brought">반입 · Brought ({{ broughtItems.length }})</div>
          <div v-if="broughtItems.length" class="rows">
            <RaidItemRow v-for="(it, i) in broughtItems" :key="`b${i}`" :item="it" />
          </div>
          <p v-else class="empty-line mono">맨몸 출격 — 반입 장비 없음.</p>
        </div>

        <div class="manifest-group">
          <div class="group-label looted">획득 · Looted ({{ lootedItems.length }})</div>
          <div v-if="lootedItems.length" class="rows">
            <RaidItemRow v-for="(it, i) in lootedItems" :key="`l${i}`" :item="it" />
          </div>
          <p v-else class="empty-line mono">아직 획득한 아이템이 없습니다.</p>
        </div>
      </section>

      <aside class="side-col">
        <section class="wx-panel loot-panel">
          <span class="wx-section-title">획득 시뮬 · Loot</span>
          <div class="loot-form">
            <el-select
              v-model="lootTemplateId"
              filterable
              placeholder="아이템 선택"
              class="loot-select"
            >
              <el-option v-for="t in lootOptions" :key="t.id" :label="t.name" :value="t.id" />
            </el-select>
            <el-input-number v-model="lootQty" :min="1" :max="999" class="loot-qty" />
            <el-button
              class="loot-btn"
              :loading="looting"
              :disabled="lootTemplateId === null"
              @click="onLoot"
            >
              획득
            </el-button>
          </div>
        </section>

        <section class="wx-panel resolve-panel">
          <span class="wx-section-title">레이드 종료 · Resolve</span>

          <!-- 사망확률 미터: 획득할수록 상승 → 탈출 성공률 하락 -->
          <div class="death-meter">
            <div class="meter-head mono">
              <span>탈출 성공률 · Survival</span>
              <span :class="{ risky: deathChancePct >= 50 }">{{ survivalPct.toFixed(0) }}%</span>
            </div>
            <div class="meter-track">
              <div class="meter-fill" :style="{ width: deathChancePct + '%' }" />
            </div>
            <p class="meter-note mono wx-muted">
              획득할수록 사망확률 상승 — 현재 <b>{{ deathChancePct.toFixed(0) }}%</b> 사망 위험
            </p>
          </div>

          <el-button
            type="success"
            size="large"
            class="resolve-btn extract"
            :loading="resolving"
            @click="onExtract"
          >
            탈출 · EXTRACT ({{ survivalPct.toFixed(0) }}%)
          </el-button>
          <el-button
            type="danger"
            size="large"
            class="resolve-btn die"
            :loading="resolving"
            @click="onDie"
          >
            사망 · DIE
          </el-button>
          <p class="resolve-note mono wx-muted">탈출 = 아이템 귀속 · 사망 = 아이템 소실</p>
        </section>
      </aside>
    </div>

    <!-- ============ OUTCOME: extracted / died ============ -->
    <div v-else-if="mode === 'outcome' && outcome" class="stage single">
      <section
        class="wx-panel outcome-panel"
        :class="outcome.status === 'Extracted' ? 'survived' : 'killed'"
      >
        <div class="outcome-head">
          <div class="outcome-title">
            {{ outcome.status === 'Extracted' ? '탈출 성공' : '사망' }}
          </div>
          <div class="outcome-sub mono">
            {{
              outcome.status === 'Extracted'
                ? 'EXTRACTED · 장비 제자리 복귀 · 전리품 회수'
                : 'KILLED IN ACTION · 아이템 소실됨'
            }}
          </div>
          <div class="outcome-meta mono">
            #{{ shortId(outcome.id) }} · {{ dateTime(outcome.resolvedAt ?? outcome.startedAt) }}
          </div>
        </div>

        <div class="outcome-list">
          <div class="group-label" :class="outcome.status === 'Extracted' ? 'brought' : 'looted'">
            {{ outcome.status === 'Extracted' ? '귀속된 아이템 · Recovered' : '소실된 아이템 · Lost' }}
            ({{ outcome.items.length }})
          </div>
          <div v-if="outcome.items.length" class="rows">
            <RaidItemRow
              v-for="(it, i) in outcome.items"
              :key="`o${i}`"
              :item="it"
              :muted="outcome.status === 'Died'"
            />
          </div>
          <p v-else class="empty-line mono">
            {{
              outcome.status === 'Extracted' ? '귀속된 아이템이 없습니다.' : '소실된 아이템이 없습니다.'
            }}
          </p>
        </div>

        <p v-if="outcome.status === 'Extracted'" class="outcome-note mono">
          반입 장비는 <strong>원래 자리(장비·주머니·백팩/리그)</strong>로 복귀했고, 전리품은
          회수되었습니다.
        </p>
        <p v-else class="outcome-note mono">창고(Stash)의 아이템은 안전합니다.</p>

        <el-button size="large" class="return-btn" @click="onReturn">스테이징으로 복귀 →</el-button>
      </section>
    </div>

    <div v-if="!loading && mode === 'prep' && !pockets" class="wx-empty">
      <img class="pixel" src="/sprites/ammo_box.svg" alt="" />
      Raid staging unavailable. Is the backend online?
    </div>
  </div>
</template>

<style scoped>
.stage {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 320px;
  gap: 18px;
  align-items: start;
}
.stage.single {
  grid-template-columns: minmax(0, 640px);
  justify-content: center;
}
@media (max-width: 900px) {
  .stage {
    grid-template-columns: 1fr;
  }
}

.panel-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 12px;
}
.grid-cap {
  font-size: 11px;
  color: var(--wx-text-faint);
  letter-spacing: 1px;
}
.grid-scroll {
  max-width: 100%;
  overflow-x: auto;
}
.hint {
  font-size: 11px;
  color: var(--wx-text-dim);
  margin: 0 0 12px;
  letter-spacing: 0.5px;
}
.link {
  color: var(--wx-amber);
  cursor: pointer;
  text-decoration: underline;
}
.w-full {
  width: 100%;
  margin-top: 12px;
}
.grid-label {
  font-family: var(--wx-font-display);
  font-size: 12px;
  font-weight: 800;
  letter-spacing: 2px;
  text-transform: uppercase;
  color: var(--wx-amber-bright);
}
.grid-head {
  display: flex;
  align-items: baseline;
  gap: 10px;
  margin: 16px 0 10px;
}

/* ---- deploy preview: equipped gear summary ---- */
.equip-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 16px;
}
.equip-chip {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 10px;
  border: 1px solid var(--wx-border-strong);
  border-radius: var(--wx-r-sm);
  background: var(--wx-inset);
}
.equip-chip-name {
  font-size: 11px;
  color: var(--wx-text-dim);
  letter-spacing: 0.5px;
}

/* ---- deploy panel ---- */
.deploy-panel {
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.brief-list {
  list-style: none;
  padding: 0;
  margin: 10px 0 0;
  font-size: 12px;
  color: var(--wx-text-dim);
  line-height: 1.9;
}
.deploy-btn {
  width: 100%;
  font-family: var(--wx-font-display);
  letter-spacing: 2px;
  font-weight: 800;
  height: 52px;
}
.deploy-note {
  font-size: 11px;
  margin: 0;
  text-align: center;
}

/* ---- active: manifest ---- */
.risk-badge {
  font-size: 11px;
  font-weight: 800;
  color: var(--wx-sell);
  border: 1px solid var(--wx-sell-dim);
  background: rgba(208, 85, 64, 0.12);
  border-radius: 999px;
  padding: 3px 10px;
  letter-spacing: 1px;
}
.atrisk-warn {
  font-size: 12px;
  color: var(--wx-sell);
  background: rgba(208, 85, 64, 0.08);
  border: 1px solid var(--wx-sell-dim);
  border-radius: var(--wx-r-sm);
  padding: 8px 12px;
  margin: 0 0 16px;
}
.expired-warn {
  font-weight: 700;
  animation: warn-pulse 1s ease-in-out infinite;
}
@keyframes warn-pulse {
  50% {
    background: rgba(208, 85, 64, 0.2);
  }
}
.head-badges {
  display: inline-flex;
  gap: 8px;
  align-items: center;
}
/* 카운트다운 타이머 배지 */
.countdown {
  font-size: 11px;
  font-weight: 800;
  color: var(--wx-fg);
  border: 1px solid var(--wx-border);
  background: var(--wx-bg-inset, rgba(255, 255, 255, 0.04));
  border-radius: 999px;
  padding: 3px 10px;
  letter-spacing: 1px;
}
.countdown.critical {
  color: var(--wx-sell);
  border-color: var(--wx-sell-dim);
  background: rgba(208, 85, 64, 0.14);
}
.countdown.expired {
  color: #fff;
  background: var(--wx-sell);
  border-color: var(--wx-sell);
}
/* 사망확률 미터 */
.death-meter {
  margin-bottom: 14px;
}
.meter-head {
  display: flex;
  justify-content: space-between;
  font-size: 11px;
  font-weight: 800;
  letter-spacing: 1px;
  margin-bottom: 6px;
}
.meter-head .risky {
  color: var(--wx-sell);
}
.meter-track {
  height: 8px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid var(--wx-border);
  overflow: hidden;
}
.meter-fill {
  height: 100%;
  background: linear-gradient(90deg, var(--wx-buy, #6fae5f), var(--wx-sell));
  transition: width 0.3s ease;
}
.meter-note {
  font-size: 11px;
  margin: 6px 0 0;
}
.manifest-group {
  margin-bottom: 18px;
}
.group-label {
  font-family: var(--wx-font-display);
  font-size: 11px;
  font-weight: 800;
  letter-spacing: 1.5px;
  text-transform: uppercase;
  margin-bottom: 10px;
}
.group-label.brought {
  color: var(--wx-olive);
}
.group-label.looted {
  color: var(--wx-amber-bright);
}
.rows {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.empty-line {
  font-size: 12px;
  color: var(--wx-text-faint);
  margin: 0;
}

/* ---- side column ---- */
.side-col {
  display: flex;
  flex-direction: column;
  gap: 18px;
}
.loot-form {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-top: 12px;
}
.loot-select {
  width: 100%;
}
.loot-qty {
  width: 100%;
}
.loot-btn {
  width: 100%;
}
.resolve-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.resolve-btn {
  width: 100%;
  font-family: var(--wx-font-display);
  letter-spacing: 2px;
  font-weight: 800;
  height: 48px;
  margin-left: 0;
}
.resolve-note {
  font-size: 11px;
  margin: 0;
  text-align: center;
}

/* ---- outcome ---- */
.outcome-panel.survived {
  border-color: var(--wx-buy-dim);
  box-shadow: inset 0 0 60px rgba(109, 176, 106, 0.08);
}
.outcome-panel.killed {
  border-color: var(--wx-sell-dim);
  box-shadow: inset 0 0 60px rgba(208, 85, 64, 0.1);
}
.outcome-head {
  text-align: center;
  margin-bottom: 20px;
}
.outcome-title {
  font-family: var(--wx-font-display);
  font-size: 30px;
  font-weight: 800;
  letter-spacing: 4px;
  text-transform: uppercase;
}
.survived .outcome-title {
  color: var(--wx-buy);
}
.killed .outcome-title {
  color: var(--wx-sell);
}
.outcome-sub {
  font-size: 11px;
  letter-spacing: 2px;
  color: var(--wx-text-dim);
  margin-top: 6px;
}
.outcome-meta {
  font-size: 11px;
  color: var(--wx-text-faint);
  margin-top: 4px;
}
.outcome-list {
  margin-bottom: 16px;
}
.outcome-note {
  font-size: 12px;
  color: var(--wx-text-dim);
  text-align: center;
  margin: 0 0 18px;
}
.outcome-note strong {
  color: var(--wx-amber-bright);
}
.return-btn {
  width: 100%;
  font-family: var(--wx-font-display);
  letter-spacing: 1px;
}
</style>
