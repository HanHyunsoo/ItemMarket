<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useCatalogStore } from '@/stores/catalog'
import { stashApi } from '@/api/endpoints'
import ItemGrid from '@/components/ItemGrid.vue'
import { toastError } from '@/utils/toast'
import type { GridContainer, StashDto, StashPlacementDto } from '@/api/types'

const catalog = useCatalogStore()
const stash = ref<StashDto | null>(null)
const loading = ref(false)
const moving = ref(false)

onMounted(async () => {
  loading.value = true
  try {
    await catalog.ensureLoaded()
    stash.value = await stashApi.get('Stash')
  } catch (err) {
    toastError(err, 'Could not load the grid stash.')
  } finally {
    loading.value = false
  }
})

// Single-grid view: every move is a within-Stash reposition. Optimistically
// preview, then reconcile with the server, which is authoritative.
async function onMove(e: {
  placement: StashPlacementDto
  from: GridContainer
  to: GridContainer
  x: number
  y: number
}): Promise<void> {
  if (!stash.value) return
  const snapshot = stash.value
  const p = e.placement
  const self = p.kind === 'Instance' ? `i:${p.instanceId}` : `s:${p.templateId}`
  stash.value = {
    ...stash.value,
    placements: stash.value.placements.map((o) =>
      (o.kind === 'Instance' ? `i:${o.instanceId}` : `s:${o.templateId}`) === self
        ? { ...o, x: e.x, y: e.y }
        : o,
    ),
  }
  moving.value = true
  try {
    stash.value = await stashApi.move({
      kind: p.kind,
      templateId: p.templateId,
      instanceId: p.kind === 'Instance' ? p.instanceId : null,
      x: e.x,
      y: e.y,
      fromContainer: e.from,
      toContainer: e.to,
    })
  } catch (err) {
    stash.value = snapshot // server rejected — snap back to authoritative state
    toastError(err, 'Move rejected by the exchange.')
  } finally {
    moving.value = false
  }
}
</script>

<template>
  <div v-loading="loading || moving">
    <h1 class="wx-page-title">Grid Stash</h1>
    <p class="wx-page-sub">Drag to arrange — the exchange validates every placement</p>

    <div v-if="stash" class="stash-wrap wx-panel">
      <div class="grid-head">
        <span class="grid-label">Stash</span>
        <span class="grid-cap mono">{{ stash.gridW }}×{{ stash.gridH }}</span>
      </div>
      <ItemGrid :stash="stash" :busy="moving" @move="onMove" />
    </div>

    <div v-if="!loading && !stash" class="wx-empty">
      <img class="pixel" src="/sprites/ammo_box.svg" alt="" />
      Grid stash unavailable. Is the exchange online?
    </div>
  </div>
</template>

<style scoped>
.stash-wrap {
  display: inline-block;
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
.grid-cap {
  font-size: 11px;
  color: var(--wx-text-faint);
  letter-spacing: 1px;
}
</style>
