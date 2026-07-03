<script setup lang="ts">
import { computed } from 'vue'
import type { ItemCategory, ItemRarity } from '@/api/types'
import { spriteUrl } from '@/utils/sprite'
import { rarityColor } from '@/utils/format'

const props = withDefaults(
  defineProps<{
    icon?: string
    category?: ItemCategory
    rarity?: ItemRarity
    size?: number
    alt?: string
  }>(),
  { size: 48 },
)

const src = computed(() => spriteUrl(props.icon, props.category))
const glow = computed(() => (props.rarity ? rarityColor(props.rarity) : 'transparent'))
const boxStyle = computed(() => ({
  width: `${props.size + 12}px`,
  height: `${props.size + 12}px`,
  borderColor: props.rarity ? glow.value + '66' : 'var(--wx-border)',
  boxShadow: props.rarity ? `inset 0 0 18px ${glow.value}22` : 'none',
}))
const imgStyle = computed(() => ({ width: `${props.size}px`, height: `${props.size}px` }))
</script>

<template>
  <div class="sprite-box" :style="boxStyle">
    <img class="pixel" :src="src" :alt="alt ?? icon ?? 'item'" :style="imgStyle" draggable="false" />
  </div>
</template>

<style scoped>
.sprite-box {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--wx-border);
  border-radius: 6px;
  background:
    linear-gradient(135deg, #1c221a, #10130e);
  flex: none;
}
</style>
