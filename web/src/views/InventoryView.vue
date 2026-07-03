<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useCatalogStore } from '@/stores/catalog'
import { inventoryApi } from '@/api/endpoints'
import ItemSprite from '@/components/ItemSprite.vue'
import RarityTag from '@/components/RarityTag.vue'
import { dateTime, shortId } from '@/utils/format'
import { toastError } from '@/utils/toast'
import type { InventoryDto } from '@/api/types'

const catalog = useCatalogStore()
const router = useRouter()
const inv = ref<InventoryDto | null>(null)
const loading = ref(false)

onMounted(async () => {
  loading.value = true
  try {
    await catalog.ensureLoaded()
    inv.value = await inventoryApi.get()
  } catch (err) {
    toastError(err, 'Could not load inventory.')
  } finally {
    loading.value = false
  }
})

const stacks = computed(() =>
  (inv.value?.stacks ?? []).map((s) => ({ ...s, tpl: catalog.get(s.templateId) })),
)
const instances = computed(() =>
  (inv.value?.instances ?? []).map((i) => ({ ...i, tpl: catalog.get(i.templateId) })),
)

function open(templateId: number) {
  router.push({ name: 'item', params: { id: templateId } })
}
</script>

<template>
  <div v-loading="loading">
    <h1 class="wx-page-title">Stash</h1>
    <p class="wx-page-sub">Your salvaged loot — stacks and unique gear</p>

    <h3 class="wx-section-title">Stacks</h3>
    <div class="grid">
      <button
        v-for="s in stacks"
        :key="s.templateId"
        class="stack"
        @click="open(s.templateId)"
      >
        <ItemSprite :icon="s.tpl?.icon" :category="s.tpl?.category" :rarity="s.tpl?.rarity" :size="44" />
        <div class="stack-body">
          <div class="stack-name">{{ s.tpl?.name ?? `#${s.templateId}` }}</div>
          <RarityTag v-if="s.tpl" :rarity="s.tpl.rarity" />
        </div>
        <div class="qty mono">×{{ s.quantity }}</div>
      </button>
      <div v-if="!loading && stacks.length === 0" class="wx-empty">No stacked items.</div>
    </div>

    <h3 class="wx-section-title" style="margin-top: 28px">Unique Gear</h3>
    <div class="instances">
      <div v-for="i in instances" :key="i.id" class="instance" @click="open(i.templateId)">
        <ItemSprite :icon="i.tpl?.icon" :category="i.tpl?.category" :rarity="i.tpl?.rarity" :size="48" />
        <div class="instance-body">
          <div class="stack-name">{{ i.tpl?.name ?? `#${i.templateId}` }}</div>
          <div class="instance-meta wx-muted mono">id {{ shortId(i.id) }} · {{ dateTime(i.acquiredAt) }}</div>
          <div class="attach" v-if="i.attachments.length">
            <el-tag v-for="a in i.attachments" :key="a" size="small" effect="plain">{{ a }}</el-tag>
          </div>
        </div>
        <div v-if="i.durability !== null" class="dur">
          <el-progress
            type="dashboard"
            :percentage="Math.max(0, Math.min(100, i.tpl?.maxDurability ? Math.round((i.durability / i.tpl.maxDurability) * 100) : i.durability))"
            :width="52"
            :stroke-width="6"
          >
            <template #default>
              <span class="dur-val mono">{{ i.durability }}</span>
            </template>
          </el-progress>
        </div>
      </div>
      <div v-if="!loading && instances.length === 0" class="wx-empty">No unique gear.</div>
    </div>
  </div>
</template>

<style scoped>
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 10px;
}
.stack {
  display: flex;
  align-items: center;
  gap: 12px;
  background: var(--wx-panel);
  border: 1px solid var(--wx-border);
  border-radius: 8px;
  padding: 10px 12px;
  cursor: pointer;
  color: inherit;
  font: inherit;
  text-align: left;
}
.stack:hover {
  border-color: var(--wx-border-strong);
}
.stack-body {
  flex: 1;
  min-width: 0;
}
.stack-name {
  font-weight: 700;
  font-size: 13px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  margin-bottom: 4px;
}
.qty {
  font-size: 16px;
  font-weight: 800;
  color: var(--wx-accent);
}
.instances {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
  gap: 12px;
}
.instance {
  display: flex;
  align-items: center;
  gap: 12px;
  background: var(--wx-panel);
  border: 1px solid var(--wx-border);
  border-radius: 8px;
  padding: 12px;
  cursor: pointer;
}
.instance:hover {
  border-color: var(--wx-border-strong);
}
.instance-body {
  flex: 1;
  min-width: 0;
}
.instance-meta {
  font-size: 11px;
  margin-bottom: 6px;
}
.attach {
  display: flex;
  gap: 4px;
  flex-wrap: wrap;
}
.dur-val {
  font-size: 13px;
  font-weight: 700;
}
</style>
