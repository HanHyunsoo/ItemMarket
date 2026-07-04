import axios, {
  AxiosError,
  type AxiosInstance,
  type AxiosRequestConfig,
  type InternalAxiosRequestConfig,
} from 'axios'
import type { ApiResponse, ApiError, TokenResponse } from './types'

// Runtime override (window.__API_BASE__, injected by the container at startup)
// wins; otherwise the build-time VITE_API_BASE; otherwise the local-dev default.
const runtimeBase =
  typeof window !== 'undefined' && window.__API_BASE__ ? window.__API_BASE__ : undefined
export const API_BASE = runtimeBase ?? import.meta.env.VITE_API_BASE ?? 'http://localhost:5080'

const TOKEN_KEY = 'wx.token'
const REFRESH_KEY = 'wx.refresh'

export function getStoredToken(): string | null {
  return localStorage.getItem(TOKEN_KEY)
}
export function setStoredToken(token: string): void {
  localStorage.setItem(TOKEN_KEY, token)
}
export function clearStoredToken(): void {
  localStorage.removeItem(TOKEN_KEY)
}

export function getStoredRefreshToken(): string | null {
  return localStorage.getItem(REFRESH_KEY)
}
export function setStoredRefreshToken(token: string): void {
  localStorage.setItem(REFRESH_KEY, token)
}
export function clearStoredRefreshToken(): void {
  localStorage.removeItem(REFRESH_KEY)
}

// Error thrown by the unwrap layer. Carries the domain ApiError so callers/UI
// can branch on `.code` and surface `.message`.
export class ApiClientError extends Error {
  readonly apiError: ApiError
  readonly status: number
  constructor(apiError: ApiError, status: number) {
    super(apiError.message)
    this.name = 'ApiClientError'
    this.apiError = apiError
    this.status = status
  }
}

// Optional hook the auth store registers to react to 401s globally.
let onUnauthorized: (() => void) | null = null
export function setUnauthorizedHandler(fn: () => void): void {
  onUnauthorized = fn
}

export const http: AxiosInstance = axios.create({
  baseURL: API_BASE,
  timeout: 15000,
  headers: { 'Content-Type': 'application/json' },
})

http.interceptors.request.use((config) => {
  const token = getStoredToken()
  if (token) {
    config.headers.set('Authorization', `Bearer ${token}`)
  }
  return config
})

// ---- Access-token refresh (rotation) with single-flight + one retry ----
// A 401 on any request triggers a single /api/auth/refresh attempt; on success we
// store the rotated pair and replay the original request once. Concurrent 401s share
// one in-flight refresh (single-flight) so we never rotate more than once per burst.
// The raw axios call below bypasses this interceptor, avoiding recursion.
type RetriableConfig = InternalAxiosRequestConfig & { _retry?: boolean }

let refreshInFlight: Promise<boolean> | null = null

async function performRefresh(): Promise<boolean> {
  const refreshToken = getStoredRefreshToken()
  if (!refreshToken) return false
  try {
    const res = await axios.post<ApiResponse<TokenResponse>>(
      `${API_BASE}/api/auth/refresh`,
      { refreshToken },
      { headers: { 'Content-Type': 'application/json' } },
    )
    const body = res.data
    if (body?.success && body.data) {
      setStoredToken(body.data.accessToken)
      setStoredRefreshToken(body.data.refreshToken)
      return true
    }
  } catch {
    /* fall through to failure */
  }
  return false
}

function tryRefresh(): Promise<boolean> {
  if (!refreshInFlight) {
    refreshInFlight = performRefresh().finally(() => {
      refreshInFlight = null
    })
  }
  return refreshInFlight
}

http.interceptors.response.use(
  (res) => res,
  async (error: AxiosError) => {
    const original = error.config as RetriableConfig | undefined
    const status = error.response?.status
    const url = original?.url ?? ''
    // Don't try to refresh the refresh/login calls themselves, and only retry once.
    const isAuthCall = url.includes('/api/auth/')
    if (status === 401 && original && !original._retry && !isAuthCall && getStoredRefreshToken()) {
      original._retry = true
      const ok = await tryRefresh()
      if (ok) {
        // Request interceptor re-attaches the freshly stored access token on replay.
        return http.request(original)
      }
    }
    return Promise.reject(error)
  },
)

function toApiError(err: unknown): { error: ApiError; status: number } {
  const ax = err as AxiosError<ApiResponse<unknown>>
  const status = ax.response?.status ?? 0
  // Prefer the server envelope's error if present.
  const envError = ax.response?.data?.error
  if (envError && envError.code) {
    return { error: envError, status }
  }
  if (status === 401) {
    return {
      error: { code: 'Unauthorized', message: 'Session expired. Please sign in again.' },
      status,
    }
  }
  if (status === 403) {
    return {
      error: { code: 'Forbidden', message: 'You do not have permission to do that.' },
      status,
    }
  }
  if (status === 0) {
    return {
      error: {
        code: 'Unknown',
        message: 'Cannot reach the exchange server. Is the backend running?',
      },
      status,
    }
  }
  return {
    error: { code: 'Unknown', message: ax.message || `Request failed (${status}).` },
    status,
  }
}

// Central unwrap: returns ApiResponse<T>.data or throws ApiClientError.
async function request<T>(config: AxiosRequestConfig): Promise<T> {
  try {
    const res = await http.request<ApiResponse<T>>(config)
    const body = res.data
    if (body && body.success) {
      return body.data as T
    }
    const error = body?.error ?? {
      code: 'Unknown' as const,
      message: 'Unexpected response from server.',
    }
    throw new ApiClientError(error, res.status)
  } catch (err) {
    if (err instanceof ApiClientError) {
      if (err.status === 401) onUnauthorized?.()
      throw err
    }
    const { error, status } = toApiError(err)
    if (status === 401) onUnauthorized?.()
    throw new ApiClientError(error, status)
  }
}

export const api = {
  get: <T>(url: string, params?: Record<string, unknown>) =>
    request<T>({ method: 'GET', url, params }),
  post: <T>(url: string, data?: unknown) => request<T>({ method: 'POST', url, data }),
  put: <T>(url: string, data?: unknown) => request<T>({ method: 'PUT', url, data }),
  del: <T>(url: string) => request<T>({ method: 'DELETE', url }),
}
