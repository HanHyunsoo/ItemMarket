import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { tourSeen } from '@/composables/useTour'

const routes: RouteRecordRaw[] = [
  // 첫 방문(가이드 미시청)은 핵심 루프인 출격 화면으로 유도한다(빈 오더북부터 보지 않도록).
  // 이후 방문은 기존대로 마켓 랜딩.
  { path: '/', redirect: () => ({ name: tourSeen() ? 'market' : 'raid' }) },
  {
    path: '/market',
    name: 'market',
    component: () => import('@/views/MarketView.vue'),
  },
  {
    path: '/market/:id',
    name: 'item',
    component: () => import('@/views/ItemDetailView.vue'),
    props: (route) => ({ id: Number(route.params.id) }),
  },
  {
    path: '/inventory',
    name: 'inventory',
    component: () => import('@/views/InventoryView.vue'),
  },
  {
    // Unified inventory + equipment (stash grid + character doll + nested grids
    // + innate pockets). Absorbs the old separate Loadout screen.
    path: '/gear',
    name: 'gear',
    component: () => import('@/views/GearView.vue'),
  },
  // Legacy paths → unified gear screen (loadout no longer exists; pockets replaced it).
  { path: '/stash', redirect: { name: 'gear' } },
  { path: '/loadout', redirect: { name: 'gear' } },
  {
    path: '/raid',
    name: 'raid',
    component: () => import('@/views/RaidView.vue'),
  },
  {
    path: '/records',
    name: 'records',
    component: () => import('@/views/RaidHistoryView.vue'),
  },
  {
    path: '/leaderboard',
    name: 'leaderboard',
    component: () => import('@/views/LeaderboardView.vue'),
  },
  {
    path: '/wallet',
    name: 'wallet',
    component: () => import('@/views/WalletView.vue'),
  },
  {
    path: '/orders',
    name: 'orders',
    component: () => import('@/views/OrdersView.vue'),
  },
  {
    path: '/admin',
    name: 'admin',
    component: () => import('@/views/AdminView.vue'),
    meta: { requiresAdmin: true },
  },
  { path: '/:pathMatch(.*)*', redirect: { name: 'market' } },
]

const router = createRouter({
  history: createWebHistory(),
  routes,
  scrollBehavior: () => ({ top: 0 }),
})

// Guard admin routes client-side (server enforces authoritatively).
router.beforeEach((to) => {
  if (to.meta.requiresAdmin) {
    const auth = useAuthStore()
    if (!auth.isAdmin) return { name: 'market' }
  }
  return true
})

export default router
