<script setup lang="ts">
import { computed } from 'vue'
import type { ItemRarity } from '@/api/types'
import { rarityColor } from '@/utils/format'

const props = defineProps<{ rarity: ItemRarity }>()
const color = computed(() => rarityColor(props.rarity))
</script>

<template>
  <span
    class="rarity-tag"
    :style="{ color, borderColor: color + '66', backgroundColor: color + '1a' }"
  >
    {{ rarity }}
  </span>
</template>

<style scoped>
.rarity-tag {
  display: inline-block;
  font-family: var(--wx-font-display);
  font-size: 9px;
  font-weight: 700;
  letter-spacing: 1.5px;
  text-transform: uppercase;
  padding: 2px 7px;
  border: 1px solid;
  border-radius: var(--wx-r-sm);
  line-height: 1.5;
}
</style>
