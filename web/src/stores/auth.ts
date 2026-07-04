import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { authApi } from '@/api/endpoints'
import {
  clearStoredRefreshToken,
  clearStoredToken,
  getStoredRefreshToken,
  getStoredToken,
  setStoredRefreshToken,
  setStoredToken,
} from '@/api/client'

const SESSION_KEY = 'wx.session'

interface StoredSession {
  playerId: string
  displayName: string
  roles: string[]
}

function loadSession(): StoredSession | null {
  const raw = localStorage.getItem(SESSION_KEY)
  if (!raw) return null
  try {
    return JSON.parse(raw) as StoredSession
  } catch {
    return null
  }
}

export const useAuthStore = defineStore('auth', () => {
  const token = ref<string | null>(getStoredToken())
  const refreshToken = ref<string | null>(getStoredRefreshToken())
  const session = ref<StoredSession | null>(loadSession())

  const isAuthenticated = computed(() => !!token.value && !!session.value)
  const playerId = computed(() => session.value?.playerId ?? null)
  const displayName = computed(() => session.value?.displayName ?? null)
  const roles = computed(() => session.value?.roles ?? [])
  const isAdmin = computed(() => roles.value.includes('admin'))

  async function login(id: string): Promise<void> {
    // Revoke the previous session's refresh token on player switch (best-effort).
    await revokeRefreshToken()

    const res = await authApi.login({ playerId: id })
    token.value = res.accessToken
    refreshToken.value = res.refreshToken
    setStoredToken(res.accessToken)
    setStoredRefreshToken(res.refreshToken)
    const s: StoredSession = {
      playerId: res.playerId,
      displayName: res.displayName,
      roles: res.roles ?? [],
    }
    session.value = s
    localStorage.setItem(SESSION_KEY, JSON.stringify(s))
  }

  // Revoke the current refresh token server-side (rotation chain teardown). Best-effort.
  async function revokeRefreshToken(): Promise<void> {
    const rt = refreshToken.value ?? getStoredRefreshToken()
    if (!rt) return
    try {
      await authApi.logout({ refreshToken: rt })
    } catch {
      /* ignore — the session is being torn down regardless */
    }
  }

  function clearLocal(): void {
    token.value = null
    refreshToken.value = null
    session.value = null
    clearStoredToken()
    clearStoredRefreshToken()
    localStorage.removeItem(SESSION_KEY)
  }

  // Full sign-out: revoke on the server, then clear local state.
  async function logout(): Promise<void> {
    await revokeRefreshToken()
    clearLocal()
  }

  // Session already invalid server-side (e.g. refresh failed) — just clear locally.
  function clearSession(): void {
    clearLocal()
  }

  return {
    token,
    refreshToken,
    session,
    isAuthenticated,
    playerId,
    displayName,
    roles,
    isAdmin,
    login,
    logout,
    clearSession,
  }
})
