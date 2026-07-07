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

// 순위 서수(1위 = 최상위). Top-N 밖이어도 서버가 전체 대비 순위를 준다.
function ordinal(rank: number): string {
  return `${rank}위`
}
</script>

<template>
  <div v-loading="loading">
    <h1 class="wx-page-title">Leaderboard · 순위</h1>
    <p class="wx-page-sub">황무지에서 가장 잘 나가는 생존자들 — 최다 자산과 최다 생환</p>

    <!-- 내 순위: Top-N 밖이어도 전체 대비 위치를 보여준다. -->
    <div v-if="board?.me" class="me-banner wx-panel">
      <span class="me-tag mono">MY RANK</span>
      <div class="me-stat">
        <span class="me-label">순자산</span>
        <span class="me-rank mono">{{ ordinal(board.me.netWorthRank) }}</span>
        <span class="me-sub mono">/ 전체 {{ board.me.totalPlayers }}명 · {{ caps(board.me.netWorth) }}</span>
      </div>
      <div class="me-stat">
        <span class="me-label">생환</span>
        <span class="me-rank mono">{{ board.me.extractionsRank ? ordinal(board.me.extractionsRank) : '—' }}</span>
        <span class="me-sub mono">{{ board.me.extractions }}회{{ board.me.extractionsRank ? '' : ' · 아직 생환 기록 없음' }}</span>
      </div>
    </div>

    <div class="boards">
      <section class="wx-panel">
        <h3 class="wx-section-title">💰 최다 자산 · Top Net Worth</h3>
        <ol class="board-list">
          <li v-for="(e, i) in board?.topNetWorth ?? []" :key="e.playerId"
              :class="{ mine: e.playerId === board?.me?.playerId }">
            <span class="rank mono">{{ medal(i) }}</span>
            <span class="who">{{ e.displayName }}</span>
            <span class="val mono">{{ caps(e.value) }}</span>
          </li>
          <li v-if="!(board?.topNetWorth?.length)" class="empty mono">아직 데이터가 없습니다</li>
        </ol>
      </section>

      <section class="wx-panel">
        <h3 class="wx-section-title">🎒 최다 생환 · Top Extractions</h3>
        <ol class="board-list">
          <li v-for="(e, i) in board?.topExtractions ?? []" :key="e.playerId"
              :class="{ mine: e.playerId === board?.me?.playerId }">
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

/* 내 순위 배너 */
.me-banner {
  display: flex;
  align-items: center;
  gap: 20px;
  flex-wrap: wrap;
  margin-top: var(--wx-s5);
  border-left: 3px solid var(--wx-amber-bright);
}
.me-tag {
  font-size: 11px;
  font-weight: 800;
  letter-spacing: 0.08em;
  color: var(--wx-amber-bright);
  padding: 3px 8px;
  border: 1px solid var(--wx-amber-bright);
  border-radius: 4px;
}
.me-stat {
  display: flex;
  align-items: baseline;
  gap: 8px;
}
.me-label {
  font-weight: 700;
  color: var(--wx-text-faint);
  font-size: 13px;
}
.me-rank {
  font-size: 20px;
  font-weight: 800;
  color: var(--wx-amber-bright);
}
.me-sub {
  font-size: 12px;
  color: var(--wx-text-faint);
}
/* Top-N 목록에서 본인 행 강조 */
.board-list li.mine {
  background: rgba(242, 189, 94, 0.12); /* --wx-amber-bright 12% */
  border-radius: 4px;
}
.board-list li.mine .who {
  color: var(--wx-amber-bright);
}
</style>
