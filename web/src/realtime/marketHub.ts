// Singleton SignalR wrapper for the market hub (see docs/realtime-contract.md).
//
// One process-wide HubConnection to `${API_BASE}/hubs/market`, authenticated with
// the current JWT via `accessTokenFactory` (WebSockets can't carry headers, so the
// server reads the token from the `?access_token=` query — signalr appends it).
//
// Design notes:
// - Coded defensively: the backend hub may not be up. A failed start never throws
//   out of here; it just leaves the connection state `offline` and callers carry on.
// - Server groups (user:{playerId}, tmpl:{id}) are lost across an auto-reconnect,
//   so we re-invoke SubscribeTemplate for every tracked template on `reconnected`.
// - On player switch the JWT changes; call `restartConnection()` to rebuild with the
//   new identity (and thus a fresh user group).

import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr'
import { readonly, ref } from 'vue'
import { API_BASE, getStoredToken } from '@/api/client'
import type { OrderBookSnapshotDto, TradeDto } from '@/api/types'

export type ConnectionStatus = 'offline' | 'connecting' | 'connected' | 'reconnecting'

// ---- reactive connection status (drives the LIVE indicator) ----
const status = ref<ConnectionStatus>('offline')
export const connectionStatus = readonly(status)

// ---- event handler registries (fan-out to any number of subscribers) ----
type OrderBookHandler = (snapshot: OrderBookSnapshotDto) => void
type TradeHandler = (trade: TradeDto) => void
type WalletHandler = () => void

const orderBookHandlers = new Set<OrderBookHandler>()
const tradeHandlers = new Set<TradeHandler>()
const walletHandlers = new Set<WalletHandler>()

// Templates we should be subscribed to; used to re-subscribe after a reconnect.
const subscribedTemplates = new Set<number>()

let connection: HubConnection | null = null
let startPromise: Promise<void> | null = null

function buildConnection(): HubConnection {
  const conn = new HubConnectionBuilder()
    .withUrl(`${API_BASE}/hubs/market`, {
      accessTokenFactory: () => getStoredToken() ?? '',
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  conn.on('OrderBookUpdated', (snapshot: OrderBookSnapshotDto) => {
    orderBookHandlers.forEach((h) => h(snapshot))
  })
  conn.on('TradeExecuted', (trade: TradeDto) => {
    tradeHandlers.forEach((h) => h(trade))
  })
  conn.on('WalletChanged', () => {
    walletHandlers.forEach((h) => h())
  })

  conn.onreconnecting(() => {
    status.value = 'reconnecting'
  })
  conn.onreconnected(() => {
    status.value = 'connected'
    // Groups don't survive a reconnect — re-join every tracked template.
    void resubscribeAll()
  })
  conn.onclose(() => {
    status.value = 'offline'
  })

  return conn
}

async function resubscribeAll(): Promise<void> {
  if (connection?.state !== HubConnectionState.Connected) return
  for (const id of subscribedTemplates) {
    try {
      await connection.invoke('SubscribeTemplate', id)
    } catch {
      /* best-effort: a failed re-subscribe shouldn't break the app */
    }
  }
}

/** Start the connection if we have a token and aren't already connected. Never throws. */
export async function startConnection(): Promise<void> {
  if (!getStoredToken()) return
  if (connection && connection.state === HubConnectionState.Connected) return
  if (startPromise) return startPromise

  if (!connection) connection = buildConnection()
  status.value = 'connecting'
  startPromise = connection
    .start()
    .then(() => {
      status.value = 'connected'
      void resubscribeAll()
    })
    .catch((err) => {
      // Backend may not be running yet — fall back gracefully, stay offline.
      status.value = 'offline'
      console.warn('[marketHub] connection failed:', err)
    })
    .finally(() => {
      startPromise = null
    })
  await startPromise
}

/** Stop and discard the current connection. Never throws. */
export async function stopConnection(): Promise<void> {
  const conn = connection
  connection = null
  startPromise = null
  status.value = 'offline'
  if (!conn) return
  try {
    await conn.stop()
  } catch {
    /* ignore */
  }
}

/** Rebuild the connection (e.g. after a player switch changes the JWT). */
export async function restartConnection(): Promise<void> {
  await stopConnection()
  await startConnection()
}

/** Subscribe to a template's order-book/trade group. Ensures the connection is up. */
export async function subscribeTemplate(templateId: number): Promise<void> {
  subscribedTemplates.add(templateId)
  await startConnection()
  if (connection?.state !== HubConnectionState.Connected) return
  try {
    await connection.invoke('SubscribeTemplate', templateId)
  } catch (err) {
    console.warn('[marketHub] SubscribeTemplate failed:', err)
  }
}

/** Unsubscribe from a template's group. */
export async function unsubscribeTemplate(templateId: number): Promise<void> {
  subscribedTemplates.delete(templateId)
  if (connection?.state !== HubConnectionState.Connected) return
  try {
    await connection.invoke('UnsubscribeTemplate', templateId)
  } catch (err) {
    console.warn('[marketHub] UnsubscribeTemplate failed:', err)
  }
}

// ---- typed event registration; each returns an unsubscribe fn ----
export function onOrderBookUpdated(handler: OrderBookHandler): () => void {
  orderBookHandlers.add(handler)
  return () => orderBookHandlers.delete(handler)
}
export function onTradeExecuted(handler: TradeHandler): () => void {
  tradeHandlers.add(handler)
  return () => tradeHandlers.delete(handler)
}
export function onWalletChanged(handler: WalletHandler): () => void {
  walletHandlers.add(handler)
  return () => walletHandlers.delete(handler)
}

// Manually fan out a WalletChanged to subscribers. Use after a local mutation that
// changes a player's balance/holdings but doesn't round-trip through the hub (e.g.
// resolving a raid), so the header caps chip refreshes without a page reload.
export function notifyWalletChanged(): void {
  walletHandlers.forEach((h) => h())
}
