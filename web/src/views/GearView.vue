<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useCatalogStore } from '@/stores/catalog'
import { equipmentApi, inventoryApi, stashApi } from '@/api/endpoints'
import ItemGrid from '@/components/ItemGrid.vue'
import ItemSprite from '@/components/ItemSprite.vue'
import ItemDetailModal from '@/components/ItemDetailModal.vue'
import { useGridDnd } from '@/composables/useGridDnd'
import { toastError, toastSuccess } from '@/utils/toast'
import type {
  EquipSlot,
  EquipmentDto,
  GridContainer,
  InventoryDto,
  ItemInstanceDto,
  NestedContainerDto,
  StashDto,
  StashPlacementDto,
} from '@/api/types'

// Unified Tarkov-style screen: stash grid on the left, character equipment on the
// right (doll slots + nested backpack/rig grids) plus the carry Loadout grid.
// Equip = drag a compatible instance onto a slot. Items flow between stash /
// loadout / nested grids via drag (server-authoritative /api/stash/move).
const catalog = useCatalogStore()
const { activeDrag } = useGridDnd()

const stash = ref<StashDto | null>(null)
const loadout = ref<StashDto | null>(null)
const equipment = ref<EquipmentDto | null>(null)
const inventory = ref<InventoryDto | null>(null)

const loading = ref(false)
const busy = ref(false)
const slotHover = ref<EquipSlot | null>(null)

// Instance detail lookup (durability/attachments/provenance) for the modal —
// /api/inventory returns every owned instance, equipped and nested included.
const instanceById = computed(() => {
  const m = new Map<string, ItemInstanceDto>()
  for (const i of inventory.value?.instances ?? []) m.set(i.id, i)
  return m
})

// ---- Character doll: single-item slots ----
const SINGLE_SLOTS: Array<{ slot: EquipSlot; label: string; placeholder: string }> = [
  { slot: 'Helmet', label: 'Helmet · 헬멧', placeholder: 'equip_helmet' },
  { slot: 'Armor', label: 'Armor · 방어구', placeholder: 'equip_armor' },
  { slot: 'Weapon', label: 'Weapon · 무기', placeholder: 'gun_rifle' },
]

function equippedIn(slot: EquipSlot) {
  return equipment.value?.slots.find((s) => s.slot === slot) ?? null
}

// Nested backpack/rig grids, adapted to the StashDto shape ItemGrid consumes.
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
const rig = computed(() => equipment.value?.containers.find((c) => c.slot === 'Rig') ?? null)
const backpack = computed(
  () => equipment.value?.containers.find((c) => c.slot === 'Backpack') ?? null,
)

// ---- data ----
async function refreshAll(): Promise<void> {
  const [s, l, e, inv] = await Promise.all([
    stashApi.get('Stash'),
    stashApi.get('Loadout'),
    equipmentApi.get(),
    inventoryApi.get(),
  ])
  stash.value = s
  loadout.value = l
  equipment.value = e
  inventory.value = inv
}

onMounted(async () => {
  loading.value = true
  try {
    await catalog.ensureLoaded()
    await refreshAll()
  } catch (err) {
    toastError(err, 'Could not load your gear.')
  } finally {
    loading.value = false
  }
})

// ---- grid ↔ grid ↔ nested moves (server-authoritative) ----
async function onMove(e: {
  placement: StashPlacementDto
  from: GridContainer
  to: GridContainer
  x: number
  y: number
  fromInstanceId: string | null
  toInstanceId: string | null
}): Promise<void> {
  const p = e.placement
  busy.value = true
  try {
    await stashApi.move({
      kind: p.kind,
      templateId: p.templateId,
      instanceId: p.kind === 'Instance' ? p.instanceId : null,
      x: e.x,
      y: e.y,
      fromContainer: e.from,
      toContainer: e.to,
      fromContainerInstanceId: e.fromInstanceId,
      toContainerInstanceId: e.toInstanceId,
    })
  } catch (err) {
    toastError(err, 'Move rejected.')
  } finally {
    // Reconcile every affected view to the server's truth (also reverts on error).
    try {
      await refreshAll()
    } catch (err) {
      toastError(err, 'Could not refresh gear.')
    }
    busy.value = false
  }
}

// ---- equip by dropping a compatible instance onto a slot ----
function onSlotDragOver(e: DragEvent, slot: EquipSlot): void {
  if (!activeDrag.value) return
  e.preventDefault()
  slotHover.value = slot
  if (e.dataTransfer) e.dataTransfer.dropEffect = 'move'
}
function onSlotDragLeave(slot: EquipSlot): void {
  if (slotHover.value === slot) slotHover.value = null
}

async function onSlotDrop(slot: EquipSlot): Promise<void> {
  const drag = activeDrag.value
  slotHover.value = null
  if (!drag) return
  const p = drag.placement
  if (p.kind !== 'Instance' || !p.instanceId) {
    toastError(new Error('Only unique gear can be equipped.'), 'That item cannot be equipped.')
    return
  }
  busy.value = true
  try {
    equipment.value = await equipmentApi.equip({ slot, instanceId: p.instanceId })
    toastSuccess('Equipped.')
    // The instance left its grid — refetch stash/loadout/inventory to reconcile.
    const [s, l, inv] = await Promise.all([
      stashApi.get('Stash'),
      stashApi.get('Loadout'),
      inventoryApi.get(),
    ])
    stash.value = s
    loadout.value = l
    inventory.value = inv
  } catch (err) {
    // SlotMismatch (or occupied slot) — nothing moved; just surface it.
    toastError(err, 'Cannot equip there.')
    try {
      await refreshAll()
    } catch {
      /* ignore */
    }
  } finally {
    busy.value = false
  }
}

async function onUnequip(slot: EquipSlot): Promise<void> {
  detailOpen.value = false
  busy.value = true
  try {
    equipment.value = await equipmentApi.unequip({ slot })
    toastSuccess('Unequipped — returned to stash.')
    const [s, inv] = await Promise.all([stashApi.get('Stash'), inventoryApi.get()])
    stash.value = s
    inventory.value = inv
  } catch (err) {
    toastError(err, 'Could not unequip.')
    try {
      await refreshAll()
    } catch {
      /* ignore */
    }
  } finally {
    busy.value = false
  }
}

// ---- item detail modal ----
const detailOpen = ref(false)
const detailTemplateId = ref<number | null>(null)
const detailInstance = ref<ItemInstanceDto | null>(null)
const detailSlot = ref<EquipSlot | null>(null)

function inspectPlacement(p: StashPlacementDto): void {
  detailTemplateId.value = p.templateId
  detailInstance.value = p.instanceId ? (instanceById.value.get(p.instanceId) ?? null) : null
  detailSlot.value = null
  detailOpen.value = true
}

function inspectSlot(slot: EquipSlot): void {
  const eq = equippedIn(slot)
  if (!eq) return
  detailTemplateId.value = eq.templateId
  detailInstance.value = instanceById.value.get(eq.instanceId) ?? null
  detailSlot.value = slot
  detailOpen.value = true
}
</script>

<template>
  <div v-loading="loading || busy">
    <h1 class="wx-page-title">장비 · Gear</h1>
    <p class="wx-page-sub">
      Drag items between stash, loadout and your rig/backpack — drag a compatible item onto a slot to
      equip. The server validates every placement.
    </p>

    <div v-if="stash && loadout && equipment" class="layout">
      <!-- LEFT: stash -->
      <section class="wx-panel col-stash">
        <div class="grid-head">
          <span class="grid-label">Stash · 창고</span>
          <span class="grid-cap mono">{{ stash.gridW }}×{{ stash.gridH }}</span>
        </div>
        <div class="grid-scroll">
          <ItemGrid :stash="stash" :busy="busy" @move="onMove" @inspect="inspectPlacement" />
        </div>
      </section>

      <!-- RIGHT: character -->
      <section class="col-char">
        <!-- doll slots -->
        <div class="wx-panel doll">
          <div class="grid-head">
            <span class="grid-label loadout">Equipment · 착용</span>
          </div>
          <div class="slots">
            <div
              v-for="s in SINGLE_SLOTS"
              :key="s.slot"
              class="slot"
              :class="{ hover: slotHover === s.slot, filled: !!equippedIn(s.slot) }"
              :title="s.label"
              @dragover="onSlotDragOver($event, s.slot)"
              @dragleave="onSlotDragLeave(s.slot)"
              @drop.prevent="onSlotDrop(s.slot)"
              @click="inspectSlot(s.slot)"
            >
              <template v-if="equippedIn(s.slot)">
                <ItemSprite
                  :icon="catalog.get(equippedIn(s.slot)!.templateId)?.icon"
                  :category="catalog.get(equippedIn(s.slot)!.templateId)?.category"
                  :rarity="catalog.get(equippedIn(s.slot)!.templateId)?.rarity"
                  :size="40"
                />
                <span class="slot-name">{{
                  catalog.get(equippedIn(s.slot)!.templateId)?.name
                }}</span>
              </template>
              <template v-else>
                <img class="pixel slot-ghost" :src="`/sprites/${s.placeholder}.svg`" alt="" />
                <span class="slot-name empty">{{ s.label }}</span>
              </template>
            </div>
          </div>
        </div>

        <!-- rig nested grid -->
        <div class="wx-panel">
          <div class="grid-head">
            <span class="grid-label loadout">Rig · 리그</span>
            <span v-if="rig" class="grid-cap mono">{{ rig.gridW }}×{{ rig.gridH }}</span>
          </div>
          <div v-if="rig" class="grid-scroll">
            <ItemGrid
              :stash="nestedStash(rig)"
              :busy="busy"
              :container-instance-id="rig.containerInstanceId"
              :show-tray="false"
              @move="onMove"
              @inspect="inspectPlacement"
            />
          </div>
          <p v-else class="slot-empty-note mono">No rig equipped — drag one onto the Rig slot.</p>
        </div>

        <!-- backpack nested grid -->
        <div class="wx-panel">
          <div class="grid-head">
            <span class="grid-label loadout">Backpack · 배낭</span>
            <span v-if="backpack" class="grid-cap mono"
              >{{ backpack.gridW }}×{{ backpack.gridH }}</span
            >
          </div>
          <div v-if="backpack" class="grid-scroll">
            <ItemGrid
              :stash="nestedStash(backpack)"
              :busy="busy"
              :container-instance-id="backpack.containerInstanceId"
              :show-tray="false"
              @move="onMove"
              @inspect="inspectPlacement"
            />
          </div>
          <p v-else class="slot-empty-note mono">
            No backpack equipped — drag one onto the Backpack slot.
          </p>
        </div>

        <!-- carry loadout grid -->
        <div class="wx-panel">
          <div class="grid-head">
            <span class="grid-label loadout">Loadout · 반입 ({{ loadout.gridW }}×{{
              loadout.gridH
            }})</span>
          </div>
          <p class="slot-empty-note mono">The carry grid you deploy into raids with.</p>
          <div class="grid-scroll">
            <ItemGrid :stash="loadout" :busy="busy" @move="onMove" @inspect="inspectPlacement" />
          </div>
        </div>
      </section>
    </div>

    <div v-if="!loading && !(stash && loadout && equipment)" class="wx-empty">
      <img class="pixel" src="/sprites/ammo_box.svg" alt="" />
      Gear unavailable. Is the exchange online?
    </div>

    <ItemDetailModal
      v-model="detailOpen"
      :template-id="detailTemplateId"
      :instance="detailInstance"
      :equipped-slot="detailSlot"
      :busy="busy"
      @unequip="onUnequip"
    />
  </div>
</template>

<style scoped>
.layout {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: 18px;
  align-items: start;
}
@media (max-width: 1000px) {
  .layout {
    grid-template-columns: 1fr;
  }
}
.col-char {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-width: 0;
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

/* ---- character doll slots ---- */
.slots {
  display: flex;
  gap: 12px;
  flex-wrap: wrap;
}
.slot {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  width: 116px;
  height: 108px;
  padding: 8px;
  border: 1px dashed var(--wx-border-strong);
  border-radius: var(--wx-r-sm);
  background: var(--wx-inset);
  cursor: pointer;
  transition:
    border-color 0.12s ease,
    background 0.12s ease;
}
.slot.filled {
  border-style: solid;
}
.slot.hover {
  border-color: var(--wx-buy);
  background: rgba(109, 176, 106, 0.14);
}
.slot-ghost {
  width: 40px;
  height: 40px;
  opacity: 0.28;
}
.slot-name {
  font-size: 11px;
  color: var(--wx-text-dim);
  text-align: center;
  line-height: 1.2;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.slot-name.empty {
  font-family: var(--wx-font-display);
  font-size: 9px;
  letter-spacing: 1px;
  text-transform: uppercase;
  color: var(--wx-text-faint);
}
.slot-empty-note {
  font-size: 11px;
  color: var(--wx-text-faint);
  margin: 0 0 10px;
  letter-spacing: 0.5px;
}
</style>
