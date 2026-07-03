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
        <img class="pixel gate-icon" src="/sprites/med_kit.svg" alt="" />
        <h2>ENTER THE WASTELAND</h2>
        <p class="wx-muted">
          Pick a survivor from the switcher in the top-right to sign in and start trading caps.
        </p>
      </div>
      <router-view v-else />
    </main>
  </div>
</template>

<style scoped>
.signin-gate {
  max-width: 460px;
  margin: 80px auto;
  text-align: center;
  padding: 40px 32px;
}
.gate-icon {
  width: 72px;
  height: 72px;
  margin-bottom: 16px;
}
.signin-gate h2 {
  letter-spacing: 3px;
  margin: 0 0 8px;
  color: var(--wx-accent);
}
</style>
