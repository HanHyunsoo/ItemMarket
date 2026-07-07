<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useTour } from '@/composables/useTour'

// 첫 방문 가이드 투어. 익스트랙션 루프(출격→탈출/사망→거래→확장→순위)를 단계별로 안내하고,
// 마지막 단계의 CTA로 곧장 출격 화면으로 보낸다. 헤더의 "가이드" 버튼으로 언제든 다시 열 수 있다.
const { visible, close } = useTour()
const router = useRouter()

interface Step {
  icon: string
  tag: string
  title: string
  body: string
}
const steps: Step[] = [
  {
    icon: '📟',
    tag: 'WASTELAND EXCHANGE',
    title: '황무지 거래소에 온 걸 환영합니다',
    body: '위험한 존으로 출격해 전리품을 챙기고, 살아 돌아와 거래소에서 캡으로 바꿔 성장하는 익스트랙션 루프입니다. 4단계만 알면 됩니다.',
  },
  {
    icon: '🎒',
    tag: 'STEP 1 · 출격',
    title: '존을 골라 출격한다 (Raid)',
    body: '무료 재기 Scav부터 고위험 High까지 존마다 드롭·사망확률·수수료가 다릅니다. 반입한 장비·주머니 아이템은 위험(at-risk) 상태가 되고, 상자를 열수록(loot) 사망확률이 올라갑니다.',
  },
  {
    icon: '🎲',
    tag: 'STEP 2 · 탈출 vs 사망',
    title: '"한 상자 더" vs "지금 탈출"',
    body: '살아 탈출하면 반입분은 원위치로, 전리품은 내 것이 됩니다. 죽으면 반입·획득 전부 소실. 마감 시간을 넘겨도 탈출 실패로 사망합니다 — 욕심과 안전의 도박입니다.',
  },
  {
    icon: '💰',
    tag: 'STEP 3 · 거래',
    title: '전리품을 캡으로 바꾼다 (Market)',
    body: '오더북에 팔거나, 급하면 NPC 벤더 참고가로 즉시 현금화합니다. 필요한 장비는 사서 다음 출격을 준비하세요. 체결에는 소액 수수료가 붙습니다.',
  },
  {
    icon: '🏆',
    tag: 'STEP 4 · 성장 · 순위',
    title: '창고를 넓히고 순위를 올린다',
    body: '캡으로 스태시를 확장해 더 많이 챙기고, 순자산·생환 리더보드에서 다른 생존자들과 경쟁하세요. 이제 첫 출격을 나갈 시간입니다.',
  },
]

const index = ref(0)
const step = computed(() => steps[index.value])
const isLast = computed(() => index.value === steps.length - 1)

// 열릴 때마다 첫 단계로 초기화.
watch(visible, (v) => {
  if (v) index.value = 0
})

function next(): void {
  if (index.value < steps.length - 1) index.value++
}
function prev(): void {
  if (index.value > 0) index.value--
}
function skip(): void {
  close()
}
function deploy(): void {
  close()
  void router.push({ name: 'raid' })
}
</script>

<template>
  <Transition name="guide-fade">
    <div v-if="visible" class="guide-backdrop" @click.self="skip">
      <div class="guide-panel" role="dialog" aria-modal="true">
        <button class="guide-skip mono" @click="skip">건너뛰기 ✕</button>

        <div class="guide-icon">{{ step.icon }}</div>
        <div class="guide-tag mono">{{ step.tag }}</div>
        <h2 class="guide-title">{{ step.title }}</h2>
        <p class="guide-body">{{ step.body }}</p>

        <!-- 단계 점 -->
        <div class="guide-dots">
          <span
            v-for="i in steps.length"
            :key="i"
            class="dot"
            :class="{ on: i - 1 === index }"
            @click="index = i - 1"
          />
        </div>

        <div class="guide-actions">
          <button v-if="index > 0" class="guide-btn ghost mono" @click="prev">← 이전</button>
          <span class="spacer" />
          <button v-if="!isLast" class="guide-btn mono" @click="next">다음 →</button>
          <button v-else class="guide-btn cta mono" @click="deploy">지금 출격하기 🎒</button>
        </div>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
.guide-backdrop {
  position: fixed;
  inset: 0;
  z-index: 3000;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 20px;
  background: rgba(0, 0, 0, 0.7);
  backdrop-filter: blur(2px);
}
.guide-panel {
  position: relative;
  width: 100%;
  max-width: 460px;
  padding: 32px 28px 22px;
  background: var(--wx-panel, #1a1a1a);
  border: 1px solid var(--wx-border);
  border-top: 3px solid var(--wx-amber-bright);
  border-radius: 8px;
  text-align: center;
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.5);
}
.guide-skip {
  position: absolute;
  top: 12px;
  right: 12px;
  background: none;
  border: none;
  color: var(--wx-text-faint);
  font-size: 12px;
  cursor: pointer;
}
.guide-skip:hover {
  color: var(--wx-text);
}
.guide-icon {
  font-size: 42px;
  line-height: 1;
  margin-bottom: 10px;
}
.guide-tag {
  font-size: 11px;
  font-weight: 800;
  letter-spacing: 0.1em;
  color: var(--wx-amber-bright);
  margin-bottom: 8px;
}
.guide-title {
  margin: 0 0 12px;
  font-size: 18px;
  font-weight: 800;
}
.guide-body {
  margin: 0 auto 20px;
  max-width: 40ch;
  color: var(--wx-text-faint);
  font-size: 14px;
  line-height: 1.6;
}
.guide-dots {
  display: flex;
  justify-content: center;
  gap: 8px;
  margin-bottom: 20px;
}
.dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--wx-border);
  cursor: pointer;
  transition: background 0.15s;
}
.dot.on {
  background: var(--wx-amber-bright);
}
.guide-actions {
  display: flex;
  align-items: center;
  gap: 10px;
}
.spacer {
  flex: 1;
}
.guide-btn {
  padding: 9px 16px;
  border-radius: 6px;
  border: 1px solid var(--wx-amber-bright);
  background: var(--wx-amber-bright);
  color: #1a1200;
  font-weight: 800;
  font-size: 13px;
  cursor: pointer;
}
.guide-btn:hover {
  filter: brightness(1.08);
}
.guide-btn.ghost {
  background: none;
  color: var(--wx-text-faint);
  border-color: var(--wx-border);
}
.guide-btn.ghost:hover {
  color: var(--wx-text);
  filter: none;
}
.guide-btn.cta {
  box-shadow: 0 0 0 3px rgba(242, 189, 94, 0.2);
}
.guide-fade-enter-active,
.guide-fade-leave-active {
  transition: opacity 0.2s ease;
}
.guide-fade-enter-from,
.guide-fade-leave-to {
  opacity: 0;
}
</style>
