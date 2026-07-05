<script setup lang="ts">
import { computed } from 'vue'
import { useCatalogStore } from '@/stores/catalog'
import ItemSprite from '@/components/ItemSprite.vue'
import RarityTag from '@/components/RarityTag.vue'
import type { RaidSessionItemDto } from '@/api/types'

// One line in a raid manifest / outcome list. `muted` dims it (e.g. lost on death).
const props = withDefaults(defineProps<{ item: RaidSessionItemDto; muted?: boolean }>(), {
  muted: false,
})

const tpl = computed(() => useCatalogStore().get(props.item.templateId))
const name = computed(() => tpl.value?.name ?? `#${props.item.templateId}`)
</script>

<template>
  <div class="manifest-row" :class="{ muted }">
    <ItemSprite :icon="tpl?.icon" :category="tpl?.category" :rarity="tpl?.rarity" :size="34" />
    <div class="row-body">
      <div class="row-name">{{ name }}</div>
      <RarityTag v-if="tpl" :rarity="tpl.rarity" />
    </div>
    <div class="row-qty mono">×{{ item.quantity }}</div>
  </div>
</template>

<style scoped>
.manifest-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 6px 10px;
  background: var(--wx-inset);
  border: 1px solid var(--wx-border);
  border-radius: var(--wx-r-sm);
}
.manifest-row.muted {
  opacity: 0.55;
  filter: grayscale(0.4);
}
.row-body {
  display: flex;
  flex-direction: column;
  gap: 4px;
  flex: 1;
  min-width: 0;
}
.row-name {
  font-size: 13px;
  color: var(--wx-text);
  font-weight: 600;
}
.row-qty {
  font-size: 13px;
  font-weight: 800;
  color: var(--wx-amber-bright);
}
</style>
