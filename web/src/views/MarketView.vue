<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { storeToRefs } from 'pinia'
import { useCatalogStore } from '@/stores/catalog'
import ItemSprite from '@/components/ItemSprite.vue'
import RarityTag from '@/components/RarityTag.vue'
import { caps, RARITY_ORDER } from '@/utils/format'
import { toastError } from '@/utils/toast'
import type { ItemCategory, ItemRarity } from '@/api/types'

const catalog = useCatalogStore()
const { items, loading } = storeToRefs(catalog)
const router = useRouter()

const search = ref('')
const category = ref<ItemCategory | ''>('')
const rarity = ref<ItemRarity | ''>('')

const CATEGORIES: ItemCategory[] = ['Food', 'Medical', 'Melee', 'Gun', 'Ammo']

onMounted(async () => {
  try {
    await catalog.ensureLoaded()
  } catch (err) {
    toastError(err, 'Could not load the item catalog.')
  }
})

const filtered = computed(() => {
  const q = search.value.trim().toLowerCase()
  return items.value.filter((it) => {
    if (category.value && it.category !== category.value) return false
    if (rarity.value && it.rarity !== rarity.value) return false
    if (q && !it.name.toLowerCase().includes(q) && !it.code.toLowerCase().includes(q)) return false
    return true
  })
})

function open(id: number) {
  router.push({ name: 'item', params: { id } })
}
</script>

<template>
  <div>
    <h1 class="wx-page-title">Market</h1>
    <p class="wx-page-sub">{{ items.length }} salvage templates · click a crate to view the order book</p>

    <div class="filters">
      <el-input v-model="search" placeholder="Search items…" clearable style="width: 240px" />
      <el-select v-model="category" placeholder="Category" clearable style="width: 150px">
        <el-option v-for="c in CATEGORIES" :key="c" :label="c" :value="c" />
      </el-select>
      <el-select v-model="rarity" placeholder="Rarity" clearable style="width: 150px">
        <el-option v-for="r in RARITY_ORDER" :key="r" :label="r" :value="r" />
      </el-select>
      <span class="count wx-muted">{{ filtered.length }} shown</span>
    </div>

    <div v-loading="loading" class="grid">
      <button v-for="it in filtered" :key="it.id" class="card" @click="open(it.id)">
        <ItemSprite :icon="it.icon" :category="it.category" :rarity="it.rarity" :size="56" />
        <div class="card-body">
          <div class="card-name">{{ it.name }}</div>
          <div class="card-meta">
            <span class="wx-muted">{{ it.category }}</span>
            <RarityTag :rarity="it.rarity" />
          </div>
          <div class="card-value mono">{{ caps(it.baseValue) }} <span class="cap">caps</span></div>
        </div>
      </button>
      <div v-if="!loading && filtered.length === 0" class="wx-empty">No items match your filters.</div>
    </div>
  </div>
</template>

<style scoped>
.filters {
  display: flex;
  gap: 12px;
  align-items: center;
  flex-wrap: wrap;
  margin-bottom: 20px;
}
.count {
  font-size: 12px;
  letter-spacing: 1px;
}
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
  gap: 12px;
  min-height: 200px;
}
.card {
  display: flex;
  gap: 12px;
  align-items: center;
  text-align: left;
  background: var(--wx-panel);
  border: 1px solid var(--wx-border);
  border-radius: 8px;
  padding: 12px;
  cursor: pointer;
  transition: all 0.12s ease;
  color: inherit;
  font: inherit;
}
.card:hover {
  border-color: var(--wx-border-strong);
  transform: translateY(-2px);
  background: #1a1e18;
}
.card-body {
  min-width: 0;
  flex: 1;
}
.card-name {
  font-weight: 700;
  font-size: 13px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.card-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 5px 0;
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 1px;
}
.card-value {
  font-size: 13px;
  color: var(--wx-accent);
  font-weight: 700;
}
.cap {
  font-size: 10px;
  color: var(--wx-text-dim);
  font-weight: 400;
}
</style>
