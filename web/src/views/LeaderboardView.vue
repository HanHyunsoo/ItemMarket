<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { leaderboardApi } from '@/api/endpoints'
import { caps } from '@/utils/format'
import { toastError } from '@/utils/toast'
import type { LeaderboardDto } from '@/api/types'

const board = ref<LeaderboardDto | null>(null)
const loading = ref(false)

onMounted(async () => {
  loading.value = true
  try {
    board.value = await leaderboardApi.get()
  } catch (err) {
    toastError(err, '리더보드를 불러오지 못했습니다.')
  } finally {
    loading.value = false
  }
})

// 상위 3위는 메달, 그 아래는 순번.
function medal(i: number): string {
  return ['🥇', '🥈', '🥉'][i] ?? `${i + 1}`
}
</script>

<template>
  <div v-loading="loading">
    <h1 class="wx-page-title">Leaderboard · 순위</h1>
    <p class="wx-page-sub">황무지에서 가장 잘 나가는 생존자들 — 최다 캡과 최다 생환</p>

    <div class="boards">
      <section class="wx-panel">
        <h3 class="wx-section-title">💰 최다 캡 · Top Caps</h3>
        <ol class="board-list">
          <li v-for="(e, i) in board?.topCaps ?? []" :key="e.playerId">
            <span class="rank mono">{{ medal(i) }}</span>
            <span class="who">{{ e.displayName }}</span>
            <span class="val mono">{{ caps(e.value) }}</span>
          </li>
          <li v-if="!(board?.topCaps?.length)" class="empty mono">아직 데이터가 없습니다</li>
        </ol>
      </section>

      <section class="wx-panel">
        <h3 class="wx-section-title">🎒 최다 생환 · Top Extractions</h3>
        <ol class="board-list">
          <li v-for="(e, i) in board?.topExtractions ?? []" :key="e.playerId">
            <span class="rank mono">{{ medal(i) }}</span>
            <span class="who">{{ e.displayName }}</span>
            <span class="val mono">{{ e.value }}회</span>
          </li>
          <li v-if="!(board?.topExtractions?.length)" class="empty mono">아직 생환 기록이 없습니다</li>
        </ol>
      </section>
    </div>
  </div>
</template>

<style scoped>
.boards {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
  gap: 16px;
  margin-top: var(--wx-s5);
}
.board-list {
  list-style: none;
  margin: 12px 0 0;
  padding: 0;
}
.board-list li {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 8px;
  border-bottom: 1px solid var(--wx-border);
}
.board-list li:last-child {
  border-bottom: none;
}
.rank {
  width: 28px;
  text-align: center;
  font-weight: 800;
  font-size: 15px;
}
.who {
  flex: 1;
  font-weight: 700;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.val {
  color: var(--wx-amber-bright);
  font-weight: 800;
}
.empty {
  justify-content: center;
  color: var(--wx-text-faint);
  font-size: 12px;
}
</style>
