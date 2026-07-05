<script setup lang="ts">
import { computed, ref } from 'vue'
import { useCatalogStore } from '@/stores/catalog'
import ItemSprite from '@/components/ItemSprite.vue'
import { spriteUrl } from '@/utils/sprite'
import { rarityColor, rarityGlow } from '@/utils/format'
import { useGridDnd } from '@/composables/useGridDnd'
import type { GridContainer, StashDto, StashPlacementDto } from '@/api/types'

// A single spatial grid (Stash or Loadout) rendered from a StashDto. Handles
// drag/drop — including cross-grid moves via the shared useGridDnd state — and
// emits a `move` request for the parent to reconcile with the server.
const props = withDefaults(
  defineProps<{
    stash: StashDto
    /** disable dragging while a move is in flight */
    busy?: boolean
    /** px per grid cell */
    cell?: number
    /** show the overflow tray of items that didn't fit */
    showTray?: boolean
    /**
     * When this grid is a nested backpack/rig (container === 'Container'), the
     * equipped container instance id it belongs to. Threaded into move payloads
     * so the parent can address /api/stash/move's from/to ContainerInstanceId.
     */
    containerInstanceId?: string | null
  }>(),
  { busy: false, cell: 46, showTray: true, containerInstanceId: null },
)

const emit = defineEmits<{
  move: [
    payload: {
      placement: StashPlacementDto
      from: GridContainer
      to: GridContainer
      x: number
      y: number
      // Nested-container addressing: the source/target container instance ids
      // (null unless that side is a 'Container').
      fromInstanceId: string | null
      toInstanceId: string | null
    },
  ]
  inspect: [placement: StashPlacementDto]
}>()

const catalog = useCatalogStore()
const { activeDrag, begin, end } = useGridDnd()

// drop-target highlight (local to this grid)
const target = ref<{ x: number; y: number; valid: boolean } | null>(null)
const gridEl = ref<HTMLElement | null>(null)

const CELL = computed(() => props.cell)
const gridW = computed(() => props.stash.gridW)
const gridH = computed(() => props.stash.gridH)
const container = computed(() => props.stash.container)
const placements = computed(() => props.stash.placements)
const unplaced = computed(() => props.stash.unplaced)

const gridStyle = computed(() => ({
  width: `${gridW.value * CELL.value}px`,
  height: `${gridH.value * CELL.value}px`,
  backgroundSize: `${CELL.value}px ${CELL.value}px`,
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
    left: `${p.x * CELL.value}px`,
    top: `${p.y * CELL.value}px`,
    width: `${p.w * CELL.value}px`,
    height: `${p.h * CELL.value}px`,
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

// Whether the dragged placement fits at (x, y): in-bounds + no overlap with any
// *other* placement already in this grid. For a cross-grid drop the incoming
// item isn't in this grid, so every placement is treated as an obstacle.
function isSameGrid(p: StashPlacementDto): boolean {
  if (p.container !== container.value) return false
  // Two distinct nested containers both report container === 'Container' — they're
  // the same grid only if the container instance matches too.
  if (container.value === 'Container') {
    return (p.containerInstanceId ?? null) === (props.containerInstanceId ?? null)
  }
  return true
}

function isValid(p: StashPlacementDto, x: number, y: number): boolean {
  if (x < 0 || y < 0 || x + p.w > gridW.value || y + p.h > gridH.value) return false
  const sameGrid = isSameGrid(p)
  const self = keyOf(p)
  for (const o of placements.value) {
    if (sameGrid && keyOf(o) === self) continue
    const overlap = x < o.x + o.w && x + p.w > o.x && y < o.y + o.h && y + p.h > o.y
    if (overlap) return false
  }
  return true
}

function cellFromEvent(e: DragEvent): { x: number; y: number } {
  const rect = gridEl.value!.getBoundingClientRect()
  return {
    x: Math.floor((e.clientX - rect.left) / CELL.value),
    y: Math.floor((e.clientY - rect.top) / CELL.value),
  }
}

function onDragStart(e: DragEvent, p: StashPlacementDto): void {
  if (props.busy) {
    e.preventDefault()
    return
  }
  const cell = cellFromEvent(e)
  begin({ placement: p, grabX: cell.x - p.x, grabY: cell.y - p.y })
  if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move'
}

function onDragOver(e: DragEvent): void {
  const drag = activeDrag.value
  if (!drag) return
  e.preventDefault() // required so drop fires
  const cell = cellFromEvent(e)
  const x = cell.x - drag.grabX
  const y = cell.y - drag.grabY
  target.value = { x, y, valid: isValid(drag.placement, x, y) }
  if (e.dataTransfer) e.dataTransfer.dropEffect = target.value.valid ? 'move' : 'none'
}

// Clear the highlight when the cursor leaves this grid (e.g. moving to the other
// grid), but ignore dragleave fired while crossing over child tiles.
function onDragLeave(e: DragEvent): void {
  const related = e.relatedTarget as Node | null
  if (!related || !gridEl.value?.contains(related)) target.value = null
}

function onDrop(e: DragEvent): void {
  e.preventDefault()
  const drag = activeDrag.value
  const t = target.value
  target.value = null
  end()
  if (!drag || !t || !t.valid) return
  const from = drag.placement.container
  const to = container.value
  // no-op reposition (same grid, same cell)
  if (isSameGrid(drag.placement) && drag.placement.x === t.x && drag.placement.y === t.y) return
  emit('move', {
    placement: drag.placement,
    from,
    to,
    x: t.x,
    y: t.y,
    fromInstanceId: drag.placement.containerInstanceId ?? null,
    toInstanceId: props.containerInstanceId ?? null,
  })
}

function onDragEnd(): void {
  target.value = null
  end()
}
</script>

<template>
  <div>
    <div ref="gridEl" class="stash-grid" :style="gridStyle" @dragover="onDragOver" @dragleave="onDragLeave" @drop="onDrop">
      <!-- drop-target highlight -->
      <div
        v-if="activeDrag && target"
        class="drop-hint"
        :class="{ bad: !target.valid }"
        :style="{
          left: `${target.x * CELL}px`,
          top: `${target.y * CELL}px`,
          width: `${activeDrag.placement.w * CELL}px`,
          height: `${activeDrag.placement.h * CELL}px`,
        }"
      />

      <!-- placed items -->
      <div
        v-for="p in placements"
        :key="keyOf(p)"
        class="tile"
        :class="{ dragging: activeDrag && isSameGrid(activeDrag.placement) && keyOf(activeDrag.placement) === keyOf(p) }"
        :style="tileStyle(p)"
        :title="nameOf(p)"
        draggable="true"
        @dragstart="onDragStart($event, p)"
        @dragend="onDragEnd"
        @click="emit('inspect', p)"
      >
        <img class="pixel tile-sprite" :src="icon(p)" :alt="nameOf(p)" draggable="false" />
        <span v-if="p.kind === 'Stack' && p.quantity > 1" class="qty mono">×{{ p.quantity }}</span>
      </div>
    </div>

    <!-- overflow tray: items that didn't fit in the grid -->
    <template v-if="showTray && unplaced.length">
      <p class="tray-note wx-muted mono">Grid full — {{ unplaced.length }} item(s) waiting for space.</p>
      <div class="tray">
        <div
          v-for="p in unplaced"
          :key="keyOf(p)"
          class="tray-item"
          :title="nameOf(p)"
          @click="emit('inspect', p)"
        >
          <ItemSprite
            :icon="catalog.get(p.templateId)?.icon"
            :category="catalog.get(p.templateId)?.category"
            :rarity="catalog.get(p.templateId)?.rarity"
            :size="34"
          />
          <span v-if="p.kind === 'Stack' && p.quantity > 1" class="tray-qty mono">×{{ p.quantity }}</span>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
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
  margin: 12px 0;
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
  cursor: pointer;
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
