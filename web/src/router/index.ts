import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

const routes: RouteRecordRaw[] = [
  { path: '/', redirect: { name: 'market' } },
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
    path: '/stash',
    name: 'stash',
    component: () => import('@/views/StashView.vue'),
  },
  {
    path: '/loadout',
    name: 'loadout',
    component: () => import('@/views/LoadoutView.vue'),
  },
  {
    path: '/raid',
    name: 'raid',
    component: () => import('@/views/RaidView.vue'),
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
