import { api } from './client'
import type {
  AdminAdjustWalletRequest,
  AdminForceCancelOrderRequest,
  AdminGrantInstanceRequest,
  AdminGrantStackRequest,
  InventoryDto,
  ItemInstanceDto,
  ItemTemplateDto,
  LoginRequest,
  MoveStashItemRequest,
  OrderBookSnapshotDto,
  OrderDto,
  OrderStatus,
  PagedResult,
  PlaceOrderRequest,
  PlaceOrderResult,
  StashDto,
  TokenResponse,
  TradeDto,
  WalletDto,
  WalletLedgerEntryDto,
} from './types'

// ---- Auth ----
export const authApi = {
  login: (body: LoginRequest) => api.post<TokenResponse>('/api/auth/login', body),
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
}

export const stashApi = {
  get: () => api.get<StashDto>('/api/stash'),
  move: (body: MoveStashItemRequest) => api.post<StashDto>('/api/stash/move', body),
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
