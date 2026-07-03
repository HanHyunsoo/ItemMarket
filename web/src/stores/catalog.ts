import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { catalogApi } from '@/api/endpoints'
import type { ItemTemplateDto } from '@/api/types'

// The catalog is fixed seed data — load once, reuse across pages.
export const useCatalogStore = defineStore('catalog', () => {
  const items = ref<ItemTemplateDto[]>([])
  const loading = ref(false)
  const loaded = ref(false)

  const byId = computed<Map<number, ItemTemplateDto>>(() => {
    const m = new Map<number, ItemTemplateDto>()
    for (const it of items.value) m.set(it.id, it)
    return m
  })

  function get(id: number): ItemTemplateDto | undefined {
    return byId.value.get(id)
  }

  async function ensureLoaded(force = false): Promise<void> {
    if (loaded.value && !force) return
    loading.value = true
    try {
      items.value = await catalogApi.list()
      loaded.value = true
    } finally {
      loading.value = false
    }
  }

  return { items, loading, loaded, byId, get, ensureLoaded }
})
