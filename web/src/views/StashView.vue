<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useCatalogStore } from '@/stores/catalog'
import { stashApi } from '@/api/endpoints'
import ItemSprite from '@/components/ItemSprite.vue'
import { spriteUrl } from '@/utils/sprite'
import { rarityColor, rarityGlow } from '@/utils/format'
import { toastError, toastInfo } from '@/utils/toast'
import type { StashDto, StashPlacementDto } from '@/api/types'

const CELL = 46 // px per grid cell

const catalog = useCatalogStore()
const stash = ref<StashDto | null>(null)
const loading = ref(false)
const moving = ref(false)

// ---- drag state ----
const dragging = ref<StashPlacementDto | null>(null)
// cell offset (within the footprint) where the item was grabbed
const grab = ref({ x: 0, y: 0 })
// current drop target top-left cell + validity, for the highlight overlay
const target = ref<{ x: number; y: number; valid: boolean } | null>(null)
const gridEl = ref<HTMLElement | null>(null)

onMounted(async () => {
  loading.value = true
  try {
    await catalog.ensureLoaded()
    stash.value = await stashApi.get()
  } catch (err) {
    toastError(err, 'Could not load the grid stash.')
  } finally {
    loading.value = false
  }
})

const gridW = computed(() => stash.value?.gridW ?? 0)
const gridH = computed(() => stash.value?.gridH ?? 0)
const placements = computed(() => stash.value?.placements ?? [])
const unplaced = computed(() => stash.value?.unplaced ?? [])

const gridStyle = computed(() => ({
  width: `${gridW.value * CELL}px`,
  height: `${gridH.value * CELL}px`,
  backgroundSize: `${CELL}px ${CELL}px`,
}))

// A placement is identified by its instance id, or its template id for stacks.
function keyOf(p: StashPlacementDto): string {
  return p.kind === 'Instance' ? `i:${p.instanceId}` : `s:${p.templateId}`
}

function tileStyle(p: StashPlacementDto) {
  const tpl = catalog.get(p.templateId)
  const edge = tpl ? rarityColor(tpl.rarity) : 'var(--wx-border-strong)'
  const glow = tpl ? rarityGlow(tpl.rarity, 0.14) : 'transparent'
  return {
    left: `${p.x * CELL}px`,
    top: `${p.y * CELL}px`,
    width: `${p.w * CELL}px`,
    height: `${p.h * CELL}px`,
    borderColor: edge,
    boxShadow: `inset 0 0 22px ${glow}`,
  }
}

function icon(p: StashPlacementDto): string {
  const tpl = catalog.get(p.templateId)
  return spriteUrl(tpl?.icon, tpl?.category)
}

function nameOf(p: StashPlacementDto): string {
  return catalog.get(p.templateId)?.name ?? `#${p.templateId}`
}

// ---- validity: in-bounds + no overlap with any *other* placement ----
function isValid(p: StashPlacementDto, x: number, y: number): boolean {
  if (x < 0 || y < 0 || x + p.w > gridW.value || y + p.h > gridH.value) return false
  const self = keyOf(p)
  for (const o of placements.value) {
    if (keyOf(o) === self) continue
    const overlap = x < o.x + o.w && x + p.w > o.x && y < o.y + o.h && y + p.h > o.y
    if (overlap) return false
  }
  return true
}

function cellFromEvent(e: DragEvent): { x: number; y: number } {
  const rect = gridEl.value!.getBoundingClientRect()
  return {
    x: Math.floor((e.clientX - rect.left) / CELL),
    y: Math.floor((e.clientY - rect.top) / CELL),
  }
}

function onDragStart(e: DragEvent, p: StashPlacementDto): void {
  if (moving.value) {
    e.preventDefault()
    return
  }
  dragging.value = p
  const cell = cellFromEvent(e)
  grab.value = { x: cell.x - p.x, y: cell.y - p.y }
  if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move'
}

function onDragOver(e: DragEvent): void {
  if (!dragging.value) return
  e.preventDefault() // required so drop fires
  const cell = cellFromEvent(e)
  const x = cell.x - grab.value.x
  const y = cell.y - grab.value.y
  target.value = { x, y, valid: isValid(dragging.value, x, y) }
  if (e.dataTransfer) e.dataTransfer.dropEffect = target.value.valid ? 'move' : 'none'
}

async function onDrop(e: DragEvent): Promise<void> {
  e.preventDefault()
  const p = dragging.value
  const t = target.value
  clearDrag()
  if (!p || !t) return
  if (!t.valid) {
    toastInfo('Cannot place there — out of bounds or overlapping.')
    return
  }
  if (p.x === t.x && p.y === t.y) return
  await move(p, t.x, t.y)
}

function clearDrag(): void {
  dragging.value = null
  target.value = null
}

// Optimistically preview the move, then reconcile with the server, which is
// authoritative: on rejection we revert to the last known-good server state.
async function move(p: StashPlacementDto, x: number, y: number): Promise<void> {
  if (!stash.value) return
  const snapshot = structuredClone(stash.value)
  const self = keyOf(p)
  stash.value = {
    ...stash.value,
    placements: stash.value.placements.map((o) => (keyOf(o) === self ? { ...o, x, y } : o)),
  }
  moving.value = true
  try {
    stash.value = await stashApi.move({
      kind: p.kind,
      templateId: p.templateId,
      instanceId: p.kind === 'Instance' ? p.instanceId : null,
      x,
      y,
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
      <div ref="gridEl" class="stash-grid" :style="gridStyle" @dragover="onDragOver" @drop="onDrop">
        <!-- drop-target highlight -->
        <div
          v-if="dragging && target"
          class="drop-hint"
          :class="{ bad: !target.valid }"
          :style="{
            left: `${target.x * CELL}px`,
            top: `${target.y * CELL}px`,
            width: `${(dragging?.w ?? 1) * CELL}px`,
            height: `${(dragging?.h ?? 1) * CELL}px`,
          }"
        />

        <!-- placed items -->
        <div
          v-for="p in placements"
          :key="keyOf(p)"
          class="tile"
          :class="{ dragging: dragging && keyOf(dragging) === keyOf(p) }"
          :style="tileStyle(p)"
          :title="nameOf(p)"
          draggable="true"
          @dragstart="onDragStart($event, p)"
          @dragend="clearDrag"
        >
          <img class="pixel tile-sprite" :src="icon(p)" :alt="nameOf(p)" draggable="false" />
          <span v-if="p.kind === 'Stack' && p.quantity > 1" class="qty mono"
            >×{{ p.quantity }}</span
          >
        </div>
      </div>
    </div>

    <div v-if="!loading && !stash" class="wx-empty">
      <img class="pixel" src="/sprites/ammo_box.svg" alt="" />
      Grid stash unavailable. Is the exchange online?
    </div>

    <!-- overflow tray: items that didn't fit in the grid -->
    <template v-if="unplaced.length">
      <h3 class="wx-section-title" style="margin-top: 24px">Overflow Tray</h3>
      <p class="tray-note wx-muted mono">Grid full — these items are waiting for space.</p>
      <div class="tray">
        <div v-for="p in unplaced" :key="keyOf(p)" class="tray-item" :title="nameOf(p)">
          <ItemSprite
            :icon="catalog.get(p.templateId)?.icon"
            :category="catalog.get(p.templateId)?.category"
            :rarity="catalog.get(p.templateId)?.rarity"
            :size="36"
          />
          <span v-if="p.kind === 'Stack' && p.quantity > 1" class="tray-qty mono"
            >×{{ p.quantity }}</span
          >
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.stash-wrap {
  display: inline-block;
  max-width: 100%;
  overflow-x: auto;
}
.stash-grid {
  position: relative;
  background-color: var(--wx-inset);
  background-image:
    linear-gradient(to right, var(--wx-border-soft) 1px, transparent 1px),
    linear-gradient(to bottom, var(--wx-border-soft) 1px, transparent 1px);
  border: 1px solid var(--wx-border-strong);
  border-radius: var(--wx-r-sm);
  box-shadow: inset 0 0 40px rgba(0, 0, 0, 0.5);
}

.tile {
  position: absolute;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 4px;
  border: 1px solid var(--wx-border-strong);
  border-radius: var(--wx-r-sm);
  background: linear-gradient(160deg, var(--wx-panel-2), var(--wx-inset));
  cursor: grab;
  user-select: none;
  transition:
    transform 0.1s ease,
    filter 0.1s ease;
}
.tile:hover {
  filter: brightness(1.12);
  z-index: 3;
}
.tile.dragging {
  opacity: 0.4;
  cursor: grabbing;
}
.tile-sprite {
  max-width: 100%;
  max-height: 100%;
  width: 100%;
  height: 100%;
  object-fit: contain;
}
.qty {
  position: absolute;
  right: 2px;
  bottom: 1px;
  font-size: 11px;
  font-weight: 800;
  color: var(--wx-amber-bright);
  text-shadow: 0 1px 2px rgba(0, 0, 0, 0.9);
  pointer-events: none;
}

/* drop-target highlight */
.drop-hint {
  position: absolute;
  z-index: 1;
  border-radius: var(--wx-r-sm);
  border: 1px dashed var(--wx-buy);
  background: rgba(109, 176, 106, 0.16);
  pointer-events: none;
}
.drop-hint.bad {
  border-color: var(--wx-sell);
  background: rgba(208, 85, 64, 0.18);
}

/* overflow tray */
.tray-note {
  font-size: 11px;
  margin: 0 0 12px;
  letter-spacing: 1px;
}
.tray {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}
.tray-item {
  position: relative;
  background: var(--wx-inset);
  border: 1px solid var(--wx-border);
  border-radius: var(--wx-r-sm);
  padding: 4px;
}
.tray-qty {
  position: absolute;
  right: 3px;
  bottom: 1px;
  font-size: 10px;
  font-weight: 800;
  color: var(--wx-amber-bright);
  text-shadow: 0 1px 2px rgba(0, 0, 0, 0.9);
}
</style>
