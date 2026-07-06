import { api } from './client'
import type {
  AdminAdjustWalletRequest,
  AdminForceCancelOrderRequest,
  AdminGrantInstanceRequest,
  AdminGrantStackRequest,
  EquipmentDto,
  EquipRequest,
  InventoryDto,
  ItemInstanceDto,
  ItemLedgerEntryDto,
  ItemTemplateDto,
  LoginRequest,
  GridContainer,
  MoveStashItemRequest,
  OrderBookSnapshotDto,
  OrderDto,
  OrderStatus,
  PagedResult,
  LootResultDto,
  PlaceOrderRequest,
  PlaceOrderResult,
  RaidHistoryEntryDto,
  RaidSessionDto,
  RaidZone,
  RefreshRequest,
  StashDto,
  TokenResponse,
  TradeDto,
  UnequipRequest,
  WalletDto,
  WalletLedgerEntryDto,
} from './types'

// ---- Auth ----
export const authApi = {
  login: (body: LoginRequest) => api.post<TokenResponse>('/api/auth/login', body),
  refresh: (body: RefreshRequest) => api.post<TokenResponse>('/api/auth/refresh', body),
  logout: (body: RefreshRequest) => api.post<boolean>('/api/auth/logout', body),
}

// ---- Player-facing ----
export const catalogApi = {
  list: () => api.get<ItemTemplateDto[]>('/api/catalog'),
}

export const walletApi = {
  get: () => api.get<WalletDto>('/api/wallet'),
  ledger: (page: number, size: number) =>
    api.get<PagedResult<WalletLedgerEntryDto>>('/api/wallet/ledger', { page, size }),
}

export const inventoryApi = {
  get: () => api.get<InventoryDto>('/api/inventory'),
  // Append-only item movement log (raid brought/extract/loot/loss + admin grants).
  ledger: (page: number, size: number) =>
    api.get<PagedResult<ItemLedgerEntryDto>>('/api/inventory/ledger', { page, size }),
}

// ---- Equipment (character doll slots + nested backpack/rig grids) ----
export const equipmentApi = {
  get: () => api.get<EquipmentDto>('/api/equipment'),
  // Both return the full updated EquipmentDto. A slot mismatch → SlotMismatch (400).
  equip: (body: EquipRequest) => api.post<EquipmentDto>('/api/equipment/equip', body),
  unequip: (body: UnequipRequest) => api.post<EquipmentDto>('/api/equipment/unequip', body),
}

export const stashApi = {
  // container is PascalCase in the model; the route segment is lowercase (stash|pockets).
  get: (container: GridContainer = 'Stash') =>
    api.get<StashDto>(`/api/stash/${container.toLowerCase()}`),
  // move returns the toContainer's snapshot; callers reconcile the fromContainer separately.
  move: (body: MoveStashItemRequest) => api.post<StashDto>('/api/stash/move', body),
}

// ---- Raid / Extraction ----
// GET returns the *active* raid or null (resolved raids aren't returned).
// start는 존(zone)을 받고, loot(scavenge)는 서버가 드롭을 결정해 LootResultDto를 돌려준다.
export const raidApi = {
  get: () => api.get<RaidSessionDto | null>('/api/raid'),
  start: (zone: RaidZone = 'Med') => api.post<RaidSessionDto>('/api/raid/start', { zone }),
  loot: () => api.post<LootResultDto>('/api/raid/loot'),
  extract: () => api.post<RaidSessionDto>('/api/raid/extract'),
  die: () => api.post<RaidSessionDto>('/api/raid/die'),
  // Resolved raids (Extracted/Died), newest first, paged.
  history: (page: number, size: number) =>
    api.get<PagedResult<RaidHistoryEntryDto>>('/api/raid/history', { page, size }),
}

export const marketApi = {
  book: (templateId: number) => api.get<OrderBookSnapshotDto>(`/api/market/${templateId}/book`),
  trades: (templateId: number, page: number, size: number) =>
    api.get<PagedResult<TradeDto>>(`/api/market/${templateId}/trades`, { page, size }),
}

export const ordersApi = {
  place: (body: PlaceOrderRequest) => api.post<PlaceOrderResult>('/api/orders', body),
  list: () => api.get<OrderDto[]>('/api/orders'),
  get: (id: string) => api.get<OrderDto>(`/api/orders/${id}`),
  cancel: (id: string) => api.del<OrderDto>(`/api/orders/${id}`),
}

// ---- Admin ----
export const adminApi = {
  playerWallet: (playerId: string) => api.get<WalletDto>(`/api/admin/players/${playerId}/wallet`),
  adjustWallet: (body: AdminAdjustWalletRequest) =>
    api.post<WalletDto>('/api/admin/wallet/adjust', body),
  grantStack: (body: AdminGrantStackRequest) =>
    api.post<InventoryDto>('/api/admin/grant/stack', body),
  grantInstance: (body: AdminGrantInstanceRequest) =>
    api.post<ItemInstanceDto>('/api/admin/grant/instance', body),
  forceCancelOrder: (body: AdminForceCancelOrderRequest) =>
    api.post<OrderDto>('/api/admin/orders/force-cancel', body),
  orders: (opts: { templateId?: number; status?: OrderStatus; page: number; size: number }) =>
    api.get<PagedResult<OrderDto>>('/api/admin/orders', opts),
  trades: (page: number, size: number) =>
    api.get<PagedResult<TradeDto>>('/api/admin/trades', { page, size }),
}
