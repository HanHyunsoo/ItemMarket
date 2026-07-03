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
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 1.5px;
  text-transform: uppercase;
  padding: 2px 8px;
  border: 1px solid;
  border-radius: 4px;
  line-height: 1.4;
}
</style>
