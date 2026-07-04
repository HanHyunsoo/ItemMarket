<script setup lang="ts">
import { computed } from 'vue'
import type { ItemCategory, ItemRarity } from '@/api/types'
import { spriteUrl } from '@/utils/sprite'
import { rarityGlow } from '@/utils/format'

const props = withDefaults(
  defineProps<{
    icon?: string
    category?: ItemCategory
    rarity?: ItemRarity
    size?: number
    /** bare = no framed well, just the glowing sprite (for hero/header spots) */
    bare?: boolean
  }>(),
  { size: 48, bare: false },
)

const src = computed(() => spriteUrl(props.icon, props.category))

const boxStyle = computed(() => {
  const pad = Math.max(10, Math.round(props.size * 0.28))
  const style: Record<string, string> = {
    width: `${props.size + pad}px`,
    height: `${props.size + pad}px`,
  }
  if (props.rarity) {
    const glow = rarityGlow(props.rarity, 0.16)
    const edge = rarityGlow(props.rarity, 0.38)
    style.background = `radial-gradient(circle at 50% 42%, ${glow}, transparent 72%), linear-gradient(160deg, var(--wx-panel-2), var(--wx-inset))`
    style.borderColor = edge
    style.boxShadow = `inset 0 0 ${Math.round(props.size * 0.4)}px ${rarityGlow(props.rarity, 0.10)}`
  }
  return style
})
const imgStyle = computed(() => ({ width: `${props.size}px`, height: `${props.size}px` }))
</script>

<template>
  <div class="sprite-box" :class="{ bare }" :style="boxStyle">
    <img class="pixel" :src="src" :alt="icon ?? 'item'" :style="imgStyle" draggable="false" />
  </div>
</template>

<style scoped>
.sprite-box {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--wx-border);
  border-radius: var(--wx-r-sm);
  background: linear-gradient(160deg, var(--wx-panel-2), var(--wx-inset));
  flex: none;
}
.sprite-box.bare {
  border-color: transparent !important;
  background: radial-gradient(circle at 50% 45%, rgba(224, 163, 60, 0.10), transparent 70%);
  box-shadow: none !important;
}
</style>
