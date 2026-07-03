import type { ItemRarity, OrderStatus, WalletLedgerReason } from '@/api/types'

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
