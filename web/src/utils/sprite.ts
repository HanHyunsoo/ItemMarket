import type { ItemCategory } from '@/api/types'

// Every catalog template has icon = code, and tools/gen-sprites.mjs emits one
// sprite per code into public/sprites/. So a truthy icon maps 1:1 to a file.
// CATEGORY_FALLBACK only covers the undefined/edge case (missing icon).
const CATEGORY_FALLBACK: Record<ItemCategory, string> = {
  Food: 'food_can',
  Medical: 'med_kit',
  Melee: 'melee_knife',
  Gun: 'gun_pistol',
  Ammo: 'ammo_box',
  Gear: 'equip_backpack',
}

export function spriteUrl(icon: string | undefined, category?: ItemCategory): string {
  if (icon) return `/sprites/${icon}.svg`
  if (category) return `/sprites/${CATEGORY_FALLBACK[category]}.svg`
  return '/sprites/ammo_box.svg'
}
