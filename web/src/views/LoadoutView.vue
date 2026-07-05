<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useCatalogStore } from '@/stores/catalog'
import { stashApi } from '@/api/endpoints'
import ItemGrid from '@/components/ItemGrid.vue'
import { toastError, toastSuccess } from '@/utils/toast'
import type { GridContainer, StashDto, StashPlacementDto } from '@/api/types'

const catalog = useCatalogStore()
const stash = ref<StashDto | null>(null)
const loadout = ref<StashDto | null>(null)
const loading = ref(false)
const moving = ref(false)

onMounted(async () => {
  loading.value = true
  try {
    await catalog.ensureLoaded()
    const [s, l] = await Promise.all([stashApi.get('Stash'), stashApi.get('Loadout')])
    stash.value = s
    loadout.value = l
  } catch (err) {
    toastError(err, 'Could not load the raid loadout.')
  } finally {
    loading.value = false
  }
})

function keyOf(p: StashPlacementDto): string {
  return p.kind === 'Instance' ? `i:${p.instanceId}` : `s:${p.templateId}`
}

function refOf(c: GridContainer) {
  return c === 'Stash' ? stash : loadout
}

// Server-authoritative move. Optimistically preview in the affected grid(s),
// then reconcile: the move endpoint returns the toContainer snapshot; for a
// cross-container transfer we also re-fetch the fromContainer. On rejection we
// revert both grids to their last known-good state and toast.
async function onMove(e: {
  placement: StashPlacementDto
  from: GridContainer
  to: GridContainer
  x: number
  y: number
}): Promise<void> {
  const fromRef = refOf(e.from)
  const toRef = refOf(e.to)
  if (!fromRef.value || !toRef.value) return

  const snapLoadout = loadout.value
  const snapStash = stash.value
  const p = e.placement
  const self = keyOf(p)

  // ---- optimistic ----
  if (e.from === e.to) {
    // reposition within one grid
    toRef.value = {
      ...toRef.value,
      placements: toRef.value.placements.map((o) =>
        keyOf(o) === self ? { ...o, x: e.x, y: e.y } : o,
      ),
    }
  } else {
    // transfer: remove from source, add (whole) to target
    fromRef.value = {
      ...fromRef.value,
      placements: fromRef.value.placements.filter((o) => keyOf(o) !== self),
    }
    toRef.value = {
      ...toRef.value,
      placements: [...toRef.value.placements, { ...p, container: e.to, x: e.x, y: e.y }],
    }
  }

  moving.value = true
  try {
    const updatedTo = await stashApi.move({
      kind: p.kind,
      templateId: p.templateId,
      instanceId: p.kind === 'Instance' ? p.instanceId : null,
      x: e.x,
      y: e.y,
      fromContainer: e.from,
      toContainer: e.to,
    })
    // reconcile the target from the response
    refOf(e.to).value = updatedTo
    // the source is not returned by move — re-fetch it authoritatively
    if (e.from !== e.to) {
      refOf(e.from).value = await stashApi.get(e.from)
      toastSuccess(e.to === 'Loadout' ? '반입 완료 — 장비에 추가됨' : '반출 완료 — 창고로 이동됨')
    }
  } catch (err) {
    // snap both grids back to authoritative state
    stash.value = snapStash
    loadout.value = snapLoadout
    toastError(err, 'Move rejected by the exchange.')
  } finally {
    moving.value = false
  }
}
</script>

<template>
  <div v-loading="loading || moving">
    <h1 class="wx-page-title">레이드 준비 · Raid Loadout</h1>
    <p class="wx-page-sub">
      창고(Stash)에서 장비(Loadout)로 드래그해 반입 — 반대로 드래그하면 반출. Server validates every
      placement.
    </p>

    <div v-if="stash && loadout" class="dual">
      <section class="col col-stash wx-panel">
        <div class="grid-head">
          <span class="grid-label">Stash · 창고</span>
          <span class="grid-cap mono">{{ stash.gridW }}×{{ stash.gridH }}</span>
        </div>
        <div class="grid-scroll">
          <ItemGrid :stash="stash" :busy="moving" @move="onMove" />
        </div>
      </section>

      <div class="flow-hint" aria-hidden="true">
        <span class="flow-arrow">→ 반입</span>
        <span class="flow-arrow">← 반출</span>
      </div>

      <section class="col col-loadout wx-panel">
        <div class="grid-head">
          <span class="grid-label loadout">Loadout · 장비</span>
          <span class="grid-cap mono">{{ loadout.gridW }}×{{ loadout.gridH }}</span>
        </div>
        <div class="grid-scroll">
          <ItemGrid :stash="loadout" :busy="moving" @move="onMove" />
        </div>
      </section>
    </div>

    <div v-if="!loading && !(stash && loadout)" class="wx-empty">
      <img class="pixel" src="/sprites/ammo_box.svg" alt="" />
      Loadout unavailable. Is the exchange online?
    </div>
  </div>
</template>

<style scoped>
.dual {
  display: flex;
  align-items: flex-start;
  gap: 18px;
  flex-wrap: wrap;
}
.col {
  display: inline-block;
}
.grid-scroll {
  max-width: 100%;
  overflow-x: auto;
}
.grid-head {
  display: flex;
  align-items: baseline;
  gap: 10px;
  margin-bottom: 10px;
}
.grid-label {
  font-family: var(--wx-font-display);
  font-size: 13px;
  font-weight: 800;
  letter-spacing: 2px;
  text-transform: uppercase;
  color: var(--wx-amber-bright);
}
.grid-label.loadout {
  color: var(--wx-olive);
}
.grid-cap {
  font-size: 11px;
  color: var(--wx-text-faint);
  letter-spacing: 1px;
}
.flow-hint {
  display: flex;
  flex-direction: column;
  gap: 8px;
  align-self: center;
  padding-top: 26px;
  font-family: var(--wx-font-display);
  font-size: 10px;
  letter-spacing: 1px;
  color: var(--wx-text-faint);
  text-transform: uppercase;
}
.flow-arrow {
  white-space: nowrap;
}
@media (max-width: 900px) {
  .flow-hint {
    flex-direction: row;
    padding-top: 0;
  }
}
</style>
