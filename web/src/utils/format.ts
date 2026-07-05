import type {
  ItemLedgerReason,
  ItemRarity,
  OrderStatus,
  RaidStatus,
  WalletLedgerReason,
} from '@/api/types'

const capFmt = new Intl.NumberFormat('en-US')

// Bottle caps (CAP) — integer currency.
export function caps(n: number | null | undefined): string {
  if (n === null || n === undefined) return '—'
  return capFmt.format(n)
}

export function signedCaps(n: number): string {
  const s = caps(Math.abs(n))
  return n < 0 ? `−${s}` : `+${s}`
}

export function dateTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString([], {
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  })
}

export function shortId(id: string | null | undefined): string {
  if (!id) return '—'
  return id.slice(0, 8)
}

// ---- Rarity presentation (common gray -> legendary gold) ----
export const RARITY_ORDER: ItemRarity[] = ['Common', 'Uncommon', 'Rare', 'Epic', 'Legendary']

export const RARITY_COLOR: Record<ItemRarity, string> = {
  Common: '#9aa0a6',
  Uncommon: '#5bd15b',
  Rare: '#4aa3ff',
  Epic: '#b25bff',
  Legendary: '#ffb62e',
}

export function rarityColor(r: ItemRarity): string {
  return RARITY_COLOR[r] ?? RARITY_COLOR.Common
}

// Rarity color with alpha (0..1) — for glows, borders, tints.
// Single source of truth stays RARITY_COLOR above.
export function rarityGlow(r: ItemRarity, alpha: number): string {
  const a = Math.round(Math.min(1, Math.max(0, alpha)) * 255)
  return rarityColor(r) + a.toString(16).padStart(2, '0')
}

// ---- Order status -> Element Plus tag type ----
export function orderStatusType(s: OrderStatus): 'info' | 'warning' | 'success' | 'danger' {
  switch (s) {
    case 'Open':
      return 'info'
    case 'PartiallyFilled':
      return 'warning'
    case 'Filled':
      return 'success'
    case 'Cancelled':
      return 'danger'
    default:
      return 'info'
  }
}

const LEDGER_REASON_LABEL: Record<WalletLedgerReason, string> = {
  OrderEscrow: 'Escrow lock',
  OrderRefund: 'Refund',
  TradePayment: 'Purchase',
  TradeProceeds: 'Sale proceeds',
  Fee: 'Fee (burned)',
  AdminAdjust: 'Admin adjust',
}

export function ledgerReasonLabel(reason: WalletLedgerReason | string): string {
  return LEDGER_REASON_LABEL[reason as WalletLedgerReason] ?? reason
}

// ---- Item ledger (movement log) reason labels ----
const ITEM_LEDGER_REASON_LABEL: Record<ItemLedgerReason, string> = {
  RaidBrought: 'Taken on raid',
  RaidExtract: 'Extracted (recovered)',
  RaidLoot: 'Looted',
  RaidLoss: 'Lost in action',
  AdminGrant: 'Admin grant',
}

export function itemLedgerReasonLabel(reason: ItemLedgerReason | string): string {
  return ITEM_LEDGER_REASON_LABEL[reason as ItemLedgerReason] ?? reason
}

// ---- Raid outcome presentation ----
export function raidStatusLabel(status: RaidStatus): string {
  switch (status) {
    case 'Extracted':
      return 'Extracted'
    case 'Died':
      return 'Killed in action'
    default:
      return 'In progress'
  }
}

// Themed color for a raid outcome: survived = green, died = red.
export function raidStatusColor(status: RaidStatus): string {
  switch (status) {
    case 'Extracted':
      return 'var(--wx-buy)'
    case 'Died':
      return 'var(--wx-sell)'
    default:
      return 'var(--wx-amber)'
  }
}
