import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { authApi } from '@/api/endpoints'
import { clearStoredToken, getStoredToken, setStoredToken } from '@/api/client'

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
  const session = ref<StoredSession | null>(loadSession())

  const isAuthenticated = computed(() => !!token.value && !!session.value)
  const playerId = computed(() => session.value?.playerId ?? null)
  const displayName = computed(() => session.value?.displayName ?? null)
  const roles = computed(() => session.value?.roles ?? [])
  const isAdmin = computed(() => roles.value.includes('admin'))

  async function login(id: string): Promise<void> {
    const res = await authApi.login({ playerId: id })
    token.value = res.accessToken
    setStoredToken(res.accessToken)
    const s: StoredSession = {
      playerId: res.playerId,
      displayName: res.displayName,
      roles: res.roles ?? [],
    }
    session.value = s
    localStorage.setItem(SESSION_KEY, JSON.stringify(s))
  }

  function logout(): void {
    token.value = null
    session.value = null
    clearStoredToken()
    localStorage.removeItem(SESSION_KEY)
  }

  return { token, session, isAuthenticated, playerId, displayName, roles, isAdmin, login, logout }
})
