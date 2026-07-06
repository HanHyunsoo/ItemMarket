<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { storeToRefs } from 'pinia'
import { useAuthStore } from '@/stores/auth'
import { walletApi } from '@/api/endpoints'
import { SEED_PLAYERS } from '@/api/types'
import { caps } from '@/utils/format'
import { toastError, toastSuccess } from '@/utils/toast'
import { onWalletChanged } from '@/realtime/marketHub'

const auth = useAuthStore()
const { isAdmin, isAuthenticated, playerId } = storeToRefs(auth)
const route = useRoute()
const router = useRouter()

const switching = ref(false)
const selected = ref<string>(playerId.value ?? '')
const balance = ref<number | null>(null)

const navItems = computed(() => {
  const base = [
    // 이중어(영·한) 라벨 통일. "Stash"는 GearView의 그리드 창고 명칭과 충돌하므로 소유 목록 화면은
    // "Inventory·소지품"으로 구분한다(그리드 창고는 Gear·장비 안의 STASH).
    { name: 'market', label: 'Market · 마켓' },
    { name: 'inventory', label: 'Inventory · 소지품' },
    { name: 'gear', label: 'Gear · 장비' },
    { name: 'raid', label: 'Raid · 출격' },
    { name: 'records', label: 'Records · 기록' },
    { name: 'leaderboard', label: 'Ranks · 순위' },
    { name: 'wallet', label: 'Wallet · 지갑' },
    { name: 'orders', label: 'Orders · 주문' },
  ]
  if (isAdmin.value) base.push({ name: 'admin', label: 'Admin · 운영' })
  return base
})

const activeName = computed(() => {
  const n = route.name?.toString() ?? ''
  if (n.startsWith('item')) return 'market'
  return n
})

// Caps balance chip: refresh on sign-in and on navigation (cheap GET).
async function refreshBalance() {
  if (!isAuthenticated.value) {
    balance.value = null
    return
  }
  try {
    balance.value = (await walletApi.get()).balance
  } catch {
    balance.value = null
  }
}
watch([playerId, () => route.fullPath], refreshBalance, { immediate: true })

// Live: a trade or order affecting this player pushes WalletChanged — refresh the chip.
const offWalletChanged = onWalletChanged(refreshBalance)
onUnmounted(offWalletChanged)

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
        <span class="brand-badge">
          <img class="pixel" src="/sprites/cap_coin.svg" alt="" />
        </span>
        <div class="brand-text">
          <div class="brand-title">WASTELAND<span>EXCHANGE</span></div>
          <div class="brand-sub">caps // trade // survive</div>
        </div>
      </div>

      <nav v-if="isAuthenticated" class="nav">
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
      <div v-else class="nav" />

      <div class="player">
        <div
          v-if="isAuthenticated && balance !== null"
          class="caps-chip mono"
          title="Bottle cap balance"
        >
          <img class="pixel" src="/sprites/cap_coin.svg" alt="caps" />
          <span>{{ caps(balance) }}</span>
        </div>

        <div class="dogtag" :class="{ admin: isAdmin }">
          <span class="dogtag-hole" />
          <el-select
            v-model="selected"
            placeholder="SELECT SURVIVOR"
            size="default"
            :loading="switching"
            class="dogtag-select"
            @change="onSelect"
          >
            <el-option v-for="p in SEED_PLAYERS" :key="p.id" :label="p.displayName" :value="p.id" />
          </el-select>
          <span v-if="isAdmin" class="dogtag-role">OP</span>
        </div>
      </div>
    </div>
  </header>
</template>

<style scoped>
.wx-header {
  position: sticky;
  top: 0;
  z-index: 50;
  background: linear-gradient(180deg, rgba(34, 29, 21, 0.96), rgba(20, 17, 11, 0.92));
  border-bottom: 1px solid var(--wx-border-strong);
  box-shadow:
    0 1px 0 rgba(255, 240, 200, 0.04) inset,
    0 4px 18px rgba(0, 0, 0, 0.45);
  backdrop-filter: blur(6px);
}
/* rust hairline under the bar */
.wx-header::after {
  content: '';
  display: block;
  height: 2px;
  background: linear-gradient(
    90deg,
    transparent,
    var(--wx-rust) 18%,
    var(--wx-amber) 50%,
    var(--wx-rust) 82%,
    transparent
  );
  opacity: 0.55;
}
.wx-header-inner {
  max-width: 1360px;
  margin: 0 auto;
  padding: 10px 20px;
  display: flex;
  align-items: center;
  gap: 24px;
}

/* ---- brand ---- */
.brand {
  display: flex;
  align-items: center;
  gap: 12px;
  cursor: pointer;
  flex: none;
  user-select: none;
}
.brand-badge {
  display: grid;
  place-items: center;
  width: 40px;
  height: 40px;
  border: 1px solid var(--wx-border-strong);
  border-radius: var(--wx-r-sm);
  background:
    radial-gradient(circle at 50% 42%, rgba(224, 163, 60, 0.18), transparent 75%),
    linear-gradient(160deg, var(--wx-panel-2), var(--wx-inset));
}
.brand-badge img {
  width: 28px;
  height: 28px;
}
.brand-title {
  font-family: var(--wx-font-display);
  font-weight: 800;
  letter-spacing: 3px;
  font-size: 15px;
  color: var(--wx-text);
  line-height: 1.1;
}
.brand-title span {
  color: var(--wx-amber);
  margin-left: 7px;
}
.brand-sub {
  font-family: var(--wx-font-display);
  font-size: 9px;
  letter-spacing: 3px;
  color: var(--wx-text-faint);
  text-transform: uppercase;
  margin-top: 2px;
}

/* ---- nav ---- */
.nav {
  display: flex;
  gap: 2px;
  flex: 1;
}
.nav-btn {
  position: relative;
  background: transparent;
  border: none;
  color: var(--wx-text-dim);
  padding: 10px 14px;
  cursor: pointer;
  font-family: var(--wx-font-display);
  font-size: 11px;
  letter-spacing: 2px;
  text-transform: uppercase;
  font-weight: 700;
  transition: color 0.12s ease;
}
.nav-btn::after {
  content: '';
  position: absolute;
  left: 12px;
  right: 12px;
  bottom: 4px;
  height: 2px;
  background: var(--wx-amber);
  transform: scaleX(0);
  transition: transform 0.14s ease;
}
.nav-btn:hover {
  color: var(--wx-text);
}
.nav-btn.active {
  color: var(--wx-amber-bright);
}
.nav-btn.active::after {
  transform: scaleX(1);
}
.nav-btn.admin {
  color: var(--wx-olive);
}
.nav-btn.admin.active {
  color: var(--wx-olive);
}
.nav-btn.admin::after {
  background: var(--wx-olive);
}

/* ---- caps chip ---- */
.player {
  display: flex;
  align-items: center;
  gap: 12px;
  flex: none;
}
.caps-chip {
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 5px 12px 5px 7px;
  border: 1px solid rgba(224, 163, 60, 0.35);
  border-radius: 999px;
  background:
    radial-gradient(circle at 12% 50%, rgba(224, 163, 60, 0.16), transparent 60%), var(--wx-inset);
  color: var(--wx-amber-bright);
  font-size: 13px;
  font-weight: 700;
  letter-spacing: 0.5px;
}
.caps-chip img {
  width: 18px;
  height: 18px;
}

/* ---- dog-tag player switcher ---- */
.dogtag {
  position: relative;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 8px 4px 10px;
  border: 1px solid var(--wx-border-strong);
  border-radius: 999px 6px 6px 999px;
  background: linear-gradient(175deg, #2e2820, #1a1610);
  box-shadow: inset 0 1px 0 rgba(255, 240, 200, 0.06);
}
.dogtag-hole {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  border: 2px solid var(--wx-border-strong);
  background: var(--wx-bg-deep);
  flex: none;
}
.dogtag-select {
  width: 170px;
}
.dogtag :deep(.el-select__wrapper) {
  background: transparent;
  box-shadow: none;
  padding: 2px 6px;
  min-height: 26px;
  font-family: var(--wx-font-display);
}
.dogtag :deep(.el-select__placeholder),
.dogtag :deep(.el-select__selected-item) {
  font-family: var(--wx-font-display);
  font-size: 12px;
  letter-spacing: 1px;
  text-transform: uppercase;
}
.dogtag-role {
  font-family: var(--wx-font-display);
  font-size: 9px;
  font-weight: 800;
  letter-spacing: 1px;
  color: var(--wx-bg-deep);
  background: var(--wx-olive);
  border-radius: 3px;
  padding: 3px 5px;
}
.dogtag.admin {
  border-color: var(--wx-olive-dim);
}

@media (max-width: 960px) {
  .brand-sub {
    display: none;
  }
  .wx-header-inner {
    flex-wrap: wrap;
    gap: 10px;
  }
  .nav {
    order: 3;
    width: 100%;
  }
}
</style>
