import type { ItemCategory } from '@/api/types'

// The 14 sprite files that actually exist in public/sprites.
const AVAILABLE = new Set([
  'ammo_box',
  'ammo_shell',
  'food_can',
  'food_snack',
  'food_water',
  'gun_pistol',
  'gun_rifle',
  'gun_shotgun',
  'med_bandage',
  'med_kit',
  'med_pills',
  'melee_axe',
  'melee_bat',
  'melee_knife',
])

// Some catalog templates reference icon keys with no dedicated sprite
// (ammo_arrow, ammo_bolt, ammo_flare). Fall back to a representative sprite.
const CATEGORY_FALLBACK: Record<ItemCategory, string> = {
  Food: 'food_can',
  Medical: 'med_kit',
  Melee: 'melee_knife',
  Gun: 'gun_pistol',
  Ammo: 'ammo_box',
}

export function spriteUrl(icon: string | undefined, category?: ItemCategory): string {
  if (icon && AVAILABLE.has(icon)) return `/sprites/${icon}.svg`
  if (category) return `/sprites/${CATEGORY_FALLBACK[category]}.svg`
  return '/sprites/ammo_box.svg'
}
