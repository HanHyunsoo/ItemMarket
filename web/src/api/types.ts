// TypeScript mirror of ItemMarket.Contracts C# DTOs.
// JSON convention assumed: property names camelCase (ASP.NET Core Web default),
// enum values PascalCase strings (JsonStringEnumConverter default).

// ---- Enums (serialized as strings) ----
export type OrderSide = 'Buy' | 'Sell'
export type OrderStatus = 'Open' | 'PartiallyFilled' | 'Filled' | 'Cancelled'
export type ItemCategory = 'Food' | 'Medical' | 'Melee' | 'Gun' | 'Ammo'
export type ItemRarity = 'Common' | 'Uncommon' | 'Rare' | 'Epic' | 'Legendary'
export type WalletLedgerReason =
  'OrderEscrow' | 'OrderRefund' | 'TradePayment' | 'TradeProceeds' | 'Fee' | 'AdminAdjust'

export type ErrorCode =
  | 'Unknown'
  | 'ValidationError'
  | 'Unauthorized'
  | 'Forbidden'
  | 'PlayerNotFound'
  | 'TemplateNotFound'
  | 'InstanceNotFound'
  | 'InstanceNotOwned'
  | 'InsufficientFunds'
  | 'InsufficientQuantity'
  | 'OrderNotFound'
  | 'OrderNotOwned'
  | 'OrderAlreadyClosed'
  | 'StackableMismatch'
  | 'PlacementInvalid'

// ---- Common envelope ----
export interface ApiError {
  code: ErrorCode
  message: string
  details?: string[] | null
}

export interface ApiResponse<T> {
  success: boolean
  data?: T | null
  error?: ApiError | null
}

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

// ---- Auth ----
export interface LoginRequest {
  playerId: string
}

export interface TokenResponse {
  accessToken: string
  tokenType: string // "Bearer"
  accessTokenExpiresIn: number // seconds until the access token expires
  refreshToken: string // raw refresh token (client-held); server stores only its hash
  playerId: string
  displayName: string
  roles: string[]
}

export interface RefreshRequest {
  refreshToken: string
}

// ---- Items / Catalog / Inventory ----
export interface ItemTemplateDto {
  id: number
  code: string
  name: string
  category: ItemCategory
  rarity: ItemRarity
  stackable: boolean
  maxDurability: number | null
  icon: string
  baseValue: number
}

export interface ItemInstanceDto {
  id: string
  templateId: number
  durability: number | null
  attachments: string[]
  acquiredAt: string
}

export interface InventoryStackDto {
  templateId: number
  quantity: number
}

export interface InventoryDto {
  playerId: string
  stacks: InventoryStackDto[]
  instances: ItemInstanceDto[]
}

// ---- Stash (spatial grid inventory) ----
// A placement occupies a w×h footprint at top-left cell (x, y).
export type StashItemKind = 'Stack' | 'Instance'
// Which grid a placement lives in. STASH is 10×12, LOADOUT is 6×8.
// Serialized PascalCase; the GET route segment is the lowercase form.
export type GridContainer = 'Stash' | 'Loadout'

export interface StashPlacementDto {
  container: GridContainer
  kind: StashItemKind
  templateId: number
  instanceId?: string | null
  x: number
  y: number
  w: number
  h: number
  quantity: number
}

export interface StashDto {
  playerId: string
  container: GridContainer
  gridW: number
  gridH: number
  placements: StashPlacementDto[]
  unplaced: StashPlacementDto[]
}

export interface MoveStashItemRequest {
  kind: StashItemKind
  templateId?: number | null
  instanceId?: string | null
  x: number
  y: number
  fromContainer: GridContainer
  toContainer: GridContainer
  // Stacks may move a partial amount; omit/null moves the whole stack.
  // Instances always move whole.
  quantity?: number | null
}

// ---- Wallet ----
export interface WalletDto {
  playerId: string
  balance: number
}

export interface WalletLedgerEntryDto {
  id: number
  playerId: string
  delta: number
  balanceAfter: number
  reason: WalletLedgerReason
  refId: string | null
  createdAt: string
}

// ---- Orders / Book ----
export interface PlaceOrderRequest {
  side: OrderSide
  itemTemplateId: number
  unitPrice: number
  quantity: number
  instanceId?: string | null
}

export interface OrderDto {
  id: string
  playerId: string
  side: OrderSide
  itemTemplateId: number
  unitPrice: number
  quantity: number
  remainingQuantity: number
  status: OrderStatus
  instanceId: string | null
  createdAt: string
}

export interface OrderBookLevelDto {
  unitPrice: number
  quantity: number
  orderCount: number
}

export interface OrderBookSnapshotDto {
  itemTemplateId: number
  bids: OrderBookLevelDto[]
  asks: OrderBookLevelDto[]
}

// ---- Trades ----
export interface TradeDto {
  id: string
  itemTemplateId: number
  unitPrice: number
  quantity: number
  buyerId: string
  sellerId: string
  buyOrderId: string
  sellOrderId: string
  instanceId: string | null
  feeAmount: number
  executedAt: string
}

export interface PlaceOrderResult {
  order: OrderDto
  fills: TradeDto[]
}

// ---- Admin ----
export interface AdminAdjustWalletRequest {
  playerId: string
  delta: number
  reason: string
}

export interface AdminGrantStackRequest {
  playerId: string
  templateId: number
  quantity: number
}

export interface AdminGrantInstanceRequest {
  playerId: string
  templateId: number
  durability?: number | null
  attachments?: string[] | null
}

export interface AdminForceCancelOrderRequest {
  orderId: string
  reason: string
}

// ---- Seeded dev players (from db/ddl.sql) ----
export interface SeedPlayer {
  id: string
  displayName: string
}

export const SEED_PLAYERS: SeedPlayer[] = [
  { id: '11111111-1111-1111-1111-111111111111', displayName: 'Survivor_Alpha' },
  { id: '22222222-2222-2222-2222-222222222222', displayName: 'Survivor_Bravo' },
  { id: '33333333-3333-3333-3333-333333333333', displayName: 'Trader_Charlie' },
]
