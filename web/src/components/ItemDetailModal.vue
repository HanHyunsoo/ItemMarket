<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useCatalogStore } from '@/stores/catalog'
import ItemSprite from '@/components/ItemSprite.vue'
import RarityTag from '@/components/RarityTag.vue'
import { caps, dateTime, shortId } from '@/utils/format'
import type { EquipSlot, ItemInstanceDto } from '@/api/types'

// Item detail panel. Shows the template facts (sprite, name, category, rarity,
// base value) and, for a unique instance, its durability / attachments / origin.
// Reused everywhere an item can be clicked (grid cell, tray, equipment slot).
const props = defineProps<{
  modelValue: boolean
  templateId: number | null
  /** unique-instance detail (durability/attachments/provenance), when known */
  instance?: ItemInstanceDto | null
  /** if this item is currently equipped, the slot — enables an Unequip action */
  equippedSlot?: EquipSlot | null
  /** disable actions while a mutation is in flight */
  busy?: boolean
}>()

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  unequip: [slot: EquipSlot]
}>()

const router = useRouter()
const catalog = useCatalogStore()

const tpl = computed(() => (props.templateId !== null ? catalog.get(props.templateId) : undefined))
const isUnique = computed(() => tpl.value && !tpl.value.stackable)

const durabilityPct = computed(() => {
  const inst = props.instance
  const max = tpl.value?.maxDurability
  if (!inst || inst.durability === null || inst.durability === undefined || !max) return null
  return Math.max(0, Math.min(100, Math.round((inst.durability / max) * 100)))
})

function close(): void {
  emit('update:modelValue', false)
}

function viewMarket(): void {
  if (props.templateId === null) return
  close()
  router.push({ name: 'item', params: { id: props.templateId } })
}
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    :title="tpl?.name ?? 'Item'"
    width="440"
    align-center
    @update:model-value="emit('update:modelValue', $event)"
  >
    <div v-if="tpl" class="detail">
      <div class="hero">
        <ItemSprite :icon="tpl.icon" :category="tpl.category" :rarity="tpl.rarity" :size="72" />
        <div class="hero-meta">
          <RarityTag :rarity="tpl.rarity" />
          <div class="facts mono">
            <span class="wx-muted">{{ tpl.category }}</span>
            <span class="sep">·</span>
            <span class="wx-muted">{{ tpl.stackable ? 'Stackable' : 'Unique' }}</span>
            <span class="sep">·</span>
            <span class="wx-muted">{{ tpl.gridW }}×{{ tpl.gridH }}</span>
          </div>
          <div class="value">
            <img class="pixel cap" src="/sprites/cap_coin.svg" alt="caps" />
            <span class="value-num">{{ caps(tpl.baseValue) }}</span>
            <span class="wx-muted value-lbl">base value</span>
          </div>
        </div>
      </div>

      <!-- Unique-instance detail: durability, attachments, provenance -->
      <div v-if="isUnique && instance" class="inst">
        <div v-if="durabilityPct !== null" class="inst-row">
          <span class="inst-label mono">DURABILITY</span>
          <div class="dur-bar">
            <div
              class="dur-fill"
              :class="{ low: durabilityPct < 34, mid: durabilityPct >= 34 && durabilityPct < 67 }"
              :style="{ width: durabilityPct + '%' }"
            />
          </div>
          <span class="inst-val mono">{{ instance.durability }} / {{ tpl.maxDurability }}</span>
        </div>

        <div class="inst-row col">
          <span class="inst-label mono">ATTACHMENTS</span>
          <div v-if="instance.attachments.length" class="attach">
            <el-tag v-for="a in instance.attachments" :key="a" size="small" effect="plain">
              {{ a }}
            </el-tag>
          </div>
          <span v-else class="wx-muted mono none">none installed</span>
        </div>

        <div class="inst-row col">
          <span class="inst-label mono">ORIGIN</span>
          <span class="inst-val mono">
            #{{ shortId(instance.id) }} · acquired {{ dateTime(instance.acquiredAt) }}
          </span>
        </div>
      </div>
      <p v-else-if="isUnique" class="wx-muted mono no-inst">
        Instance detail unavailable for this item.
      </p>

      <div class="ref mono wx-muted">#{{ tpl.id }} · {{ tpl.code }}</div>
    </div>
    <div v-else class="wx-muted">Unknown item.</div>

    <template #footer>
      <div class="footer">
        <el-button
          v-if="equippedSlot"
          type="warning"
          plain
          :loading="busy"
          @click="emit('unequip', equippedSlot)"
        >
          Unequip
        </el-button>
        <el-button plain @click="viewMarket">View market</el-button>
        <el-button @click="close">Close</el-button>
      </div>
    </template>
  </el-dialog>
</template>

<style scoped>
.hero {
  display: flex;
  gap: 16px;
  align-items: center;
  margin-bottom: 16px;
}
.hero-meta {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}
.facts {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 1px;
}
.sep {
  color: var(--wx-text-faint);
}
.value {
  display: flex;
  align-items: center;
  gap: 6px;
}
.cap {
  width: 18px;
  height: 18px;
}
.value-num {
  color: var(--wx-amber-bright);
  font-weight: 800;
  font-size: 16px;
}
.value-lbl {
  font-size: 10px;
  letter-spacing: 1px;
  text-transform: uppercase;
}

.inst {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 12px;
  background: var(--wx-inset);
  border: 1px solid var(--wx-border);
  border-radius: var(--wx-r-sm);
}
.inst-row {
  display: flex;
  align-items: center;
  gap: 10px;
}
.inst-row.col {
  flex-direction: column;
  align-items: flex-start;
  gap: 6px;
}
.inst-label {
  font-size: 9px;
  letter-spacing: 1.5px;
  color: var(--wx-text-faint);
  flex: none;
  width: 92px;
}
.inst-val {
  font-size: 12px;
  color: var(--wx-text-dim);
}
.dur-bar {
  flex: 1;
  height: 8px;
  border-radius: 999px;
  background: var(--wx-bg-deep);
  border: 1px solid var(--wx-border);
  overflow: hidden;
}
.dur-fill {
  height: 100%;
  background: var(--wx-buy);
}
.dur-fill.mid {
  background: var(--wx-amber);
}
.dur-fill.low {
  background: var(--wx-sell);
}
.attach {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}
.none,
.no-inst {
  font-size: 11px;
}
.no-inst {
  margin: 0;
}
.ref {
  margin-top: 12px;
  font-size: 10px;
  letter-spacing: 1px;
}
.footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
