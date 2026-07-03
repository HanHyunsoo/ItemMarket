<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { storeToRefs } from 'pinia'
import { useAuthStore } from '@/stores/auth'
import { SEED_PLAYERS } from '@/api/types'
import { toastError, toastSuccess } from '@/utils/toast'

const auth = useAuthStore()
const { displayName, isAdmin, isAuthenticated, playerId } = storeToRefs(auth)
const route = useRoute()
const router = useRouter()

const switching = ref(false)
const selected = ref<string>(playerId.value ?? '')

const navItems = computed(() => {
  const base = [
    { name: 'market', label: 'Market' },
    { name: 'inventory', label: 'Inventory' },
    { name: 'wallet', label: 'Wallet' },
    { name: 'orders', label: 'My Orders' },
  ]
  if (isAdmin.value) base.push({ name: 'admin', label: 'Admin' })
  return base
})

const activeName = computed(() => {
  const n = route.name?.toString() ?? ''
  if (n.startsWith('item')) return 'market'
  return n
})

async function onSelect(id: string) {
  if (!id) return
  switching.value = true
  try {
    await auth.login(id)
    const p = SEED_PLAYERS.find((x) => x.id === id)
    toastSuccess(`Signed in as ${p?.displayName ?? 'player'}`)
  } catch (err) {
    toastError(err, 'Login failed.')
    selected.value = playerId.value ?? ''
  } finally {
    switching.value = false
  }
}

function go(name: string) {
  router.push({ name })
}
</script>

<template>
  <header class="wx-header">
    <div class="wx-header-inner">
      <div class="brand" @click="go('market')">
        <img class="pixel brand-icon" src="/sprites/ammo_shell.svg" alt="" />
        <div class="brand-text">
          <div class="brand-title">WASTELAND EXCHANGE</div>
          <div class="brand-sub">caps · trade · survive</div>
        </div>
      </div>

      <nav class="nav" v-if="isAuthenticated">
        <button
          v-for="item in navItems"
          :key="item.name"
          class="nav-btn"
          :class="{ active: activeName === item.name, admin: item.name === 'admin' }"
          @click="go(item.name)"
        >
          {{ item.label }}
        </button>
      </nav>

      <div class="player">
        <el-select
          v-model="selected"
          placeholder="Select survivor"
          size="default"
          :loading="switching"
          style="width: 200px"
          @change="onSelect"
        >
          <el-option
            v-for="p in SEED_PLAYERS"
            :key="p.id"
            :label="p.displayName"
            :value="p.id"
          />
        </el-select>
        <el-tag v-if="isAdmin" type="warning" effect="dark" size="small" round>ADMIN</el-tag>
        <span v-if="displayName" class="who">{{ displayName }}</span>
      </div>
    </div>
  </header>
</template>

<style scoped>
.wx-header {
  position: sticky;
  top: 0;
  z-index: 50;
  background: #0c0e0bdd;
  border-bottom: 1px solid var(--wx-border-strong);
  backdrop-filter: blur(6px);
}
.wx-header-inner {
  max-width: 1360px;
  margin: 0 auto;
  padding: 10px 20px;
  display: flex;
  align-items: center;
  gap: 24px;
}
.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  cursor: pointer;
  flex: none;
}
.brand-icon {
  width: 34px;
  height: 34px;
}
.brand-title {
  font-weight: 900;
  letter-spacing: 2px;
  font-size: 15px;
  color: var(--wx-accent);
}
.brand-sub {
  font-size: 10px;
  letter-spacing: 2px;
  color: var(--wx-text-dim);
  text-transform: uppercase;
}
.nav {
  display: flex;
  gap: 4px;
  flex: 1;
}
.nav-btn {
  background: transparent;
  border: 1px solid transparent;
  color: var(--wx-text-dim);
  padding: 7px 14px;
  border-radius: 6px;
  cursor: pointer;
  font-size: 12px;
  letter-spacing: 1.5px;
  text-transform: uppercase;
  font-weight: 700;
  transition: all 0.12s ease;
}
.nav-btn:hover {
  color: var(--wx-text);
  border-color: var(--wx-border);
}
.nav-btn.active {
  color: var(--wx-bg);
  background: var(--wx-accent);
  border-color: var(--wx-accent);
}
.nav-btn.admin {
  color: var(--wx-accent-2);
}
.nav-btn.admin.active {
  background: var(--wx-accent-2);
  color: var(--wx-bg);
}
.player {
  display: flex;
  align-items: center;
  gap: 10px;
  flex: none;
}
.who {
  font-size: 12px;
  color: var(--wx-text-dim);
}
@media (max-width: 900px) {
  .brand-sub,
  .who {
    display: none;
  }
  .wx-header-inner {
    flex-wrap: wrap;
    gap: 12px;
  }
}
</style>
