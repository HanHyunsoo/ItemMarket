<script setup lang="ts">
import { onMounted } from 'vue'
import AppHeader from '@/components/AppHeader.vue'
import { useCatalogStore } from '@/stores/catalog'
import { useAuthStore } from '@/stores/auth'
import { toastError } from '@/utils/toast'

const catalog = useCatalogStore()
const auth = useAuthStore()

// Catalog is public seed data; load eagerly if already signed in so pages
// render sprites/names immediately. If not signed in yet, views load on demand.
onMounted(async () => {
  if (auth.isAuthenticated) {
    try {
      await catalog.ensureLoaded()
    } catch (err) {
      toastError(err, 'Could not load the item catalog.')
    }
  }
})
</script>

<template>
  <div class="wx-shell">
    <AppHeader />
    <main class="wx-main">
      <div v-if="!auth.isAuthenticated" class="signin-gate wx-panel">
        <div class="gate-icons">
          <img class="pixel" src="/sprites/gun_rifle.svg" alt="" />
          <img class="pixel big" src="/sprites/cap_coin.svg" alt="" />
          <img class="pixel" src="/sprites/med_kit.svg" alt="" />
        </div>
        <h2>ENTER THE WASTELAND</h2>
        <p class="gate-line mono">// AUTH REQUIRED //</p>
        <p class="wx-muted">
          Pick a survivor from the dog tag in the top-right to sign in and start trading caps.
        </p>
      </div>
      <router-view v-else />
    </main>
  </div>
</template>

<style scoped>
.signin-gate {
  max-width: 480px;
  margin: 80px auto;
  text-align: center;
  padding: 44px 32px;
}
.gate-icons {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 20px;
  margin-bottom: 20px;
}
.gate-icons img {
  width: 48px;
  height: 48px;
  opacity: 0.85;
}
.gate-icons img.big {
  width: 68px;
  height: 68px;
  opacity: 1;
}
.signin-gate h2 {
  letter-spacing: 4px;
  margin: 0 0 6px;
  color: var(--wx-amber);
  font-size: 18px;
}
.gate-line {
  font-size: 11px;
  letter-spacing: 3px;
  color: var(--wx-text-faint);
  margin: 0 0 14px;
}
</style>
