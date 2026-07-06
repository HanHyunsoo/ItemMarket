<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useCatalogStore } from '@/stores/catalog'
import { inventoryApi } from '@/api/endpoints'
import ItemSprite from '@/components/ItemSprite.vue'
import RarityTag from '@/components/RarityTag.vue'
import { dateTime, shortId } from '@/utils/format'
import { toastError } from '@/utils/toast'
import { onWalletChanged } from '@/realtime/marketHub'
import type { InventoryDto } from '@/api/types'

const catalog = useCatalogStore()
const router = useRouter()
const inv = ref<InventoryDto | null>(null)
const loading = ref(false)

async function loadInventory() {
  try {
    inv.value = await inventoryApi.get()
  } catch (err) {
    toastError(err, 'Could not load inventory.')
  }
}

onMounted(async () => {
  loading.value = true
  try {
    await catalog.ensureLoaded()
  } catch (err) {
    toastError(err, 'Could not load the item catalog.')
  }
  await loadInventory()
  loading.value = false
})

// Live: a fill or order change touches inventory — refetch (WalletChanged fires for
// both buyer and seller, which is exactly when stacks/instances move).
const offWalletChanged = onWalletChanged(loadInventory)
onUnmounted(offWalletChanged)

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
    <h1 class="wx-page-title">Inventory · 소지품</h1>
    <p class="wx-page-sub">보유 중인 전리품 — 스택 + 유니크 장비 (그리드 창고는 Gear · 장비)</p>

    <h3 class="wx-section-title">Stacks</h3>
    <div class="grid">
      <button v-for="s in stacks" :key="s.templateId" class="stack" @click="open(s.templateId)">
        <ItemSprite
          :icon="s.tpl?.icon"
          :category="s.tpl?.category"
          :rarity="s.tpl?.rarity"
          :size="44"
        />
        <div class="stack-body">
          <div class="stack-name">{{ s.tpl?.name ?? `#${s.templateId}` }}</div>
          <RarityTag v-if="s.tpl" :rarity="s.tpl.rarity" />
        </div>
        <div class="qty mono">×{{ s.quantity }}</div>
      </button>
      <div v-if="!loading && stacks.length === 0" class="wx-empty">
        <img class="pixel" src="/sprites/food_can.svg" alt="" />
        소지품이 비었습니다. 출격해서 전리품을 챙겨오세요.
      </div>
    </div>

    <h3 class="wx-section-title" style="margin-top: 28px">Unique Gear</h3>
    <div class="instances">
      <div v-for="i in instances" :key="i.id" class="instance" @click="open(i.templateId)">
        <ItemSprite
          :icon="i.tpl?.icon"
          :category="i.tpl?.category"
          :rarity="i.tpl?.rarity"
          :size="48"
        />
        <div class="instance-body">
          <div class="stack-name">{{ i.tpl?.name ?? `#${i.templateId}` }}</div>
          <div class="instance-meta wx-muted mono">
            id {{ shortId(i.id) }} · {{ dateTime(i.acquiredAt) }}
          </div>
          <div v-if="i.attachments.length" class="attach">
            <el-tag v-for="a in i.attachments" :key="a" size="small" effect="plain">{{ a }}</el-tag>
          </div>
        </div>
        <div v-if="i.durability !== null" class="dur">
          <el-progress
            type="dashboard"
            :percentage="
              Math.max(
                0,
                Math.min(
                  100,
                  i.tpl?.maxDurability
                    ? Math.round((i.durability / i.tpl.maxDurability) * 100)
                    : i.durability,
                ),
              )
            "
            :width="52"
            :stroke-width="6"
          >
            <template #default>
              <span class="dur-val mono">{{ i.durability }}</span>
            </template>
          </el-progress>
        </div>
      </div>
      <div v-if="!loading && instances.length === 0" class="wx-empty">
        <img class="pixel" src="/sprites/gun_rifle.svg" alt="" />
        No unique gear yet. The good stuff is still out there.
      </div>
    </div>
  </div>
</template>

<style scoped>
.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 10px;
}
.grid .wx-empty,
.instances .wx-empty {
  grid-column: 1 / -1;
}
.stack {
  display: flex;
  align-items: center;
  gap: 12px;
  background: linear-gradient(180deg, var(--wx-panel-2), var(--wx-panel) 55%);
  border: 1px solid var(--wx-border);
  border-radius: var(--wx-r);
  padding: 10px 12px;
  cursor: pointer;
  color: inherit;
  font: inherit;
  text-align: left;
  box-shadow: var(--wx-shadow);
  transition:
    transform 0.14s ease,
    border-color 0.14s ease;
}
.stack:hover {
  border-color: var(--wx-border-strong);
  transform: translateY(-2px);
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
  color: var(--wx-amber-bright);
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
  background: linear-gradient(180deg, var(--wx-panel-2), var(--wx-panel) 55%);
  border: 1px solid var(--wx-border);
  border-radius: var(--wx-r);
  padding: 12px;
  cursor: pointer;
  box-shadow: var(--wx-shadow);
  transition:
    transform 0.14s ease,
    border-color 0.14s ease;
}
.instance:hover {
  border-color: var(--wx-border-strong);
  transform: translateY(-2px);
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
