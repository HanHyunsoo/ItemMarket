import { ref } from 'vue'

// 첫 방문 튜토리얼(가이드 투어) 상태 — 헤더의 "가이드" 버튼과 App에 마운트된 오버레이가
// 공유하는 모듈 싱글턴. localStorage로 1회성 노출을 기억한다.
const TOUR_KEY = 'wx.tour.v1'
// #6 마켓 온보딩 스트립과 키를 통일해, 투어를 본(또는 건너뛴) 사용자에겐 스트립이 다시 뜨지 않게 한다.
const ONBOARD_KEY = 'wx.onboarded.v1'

const visible = ref(false)

/** 투어(및 마켓 스트립)를 이미 봤는지. localStorage 불가 환경은 "봤음"으로 간주(노출 생략). */
export function tourSeen(): boolean {
  try {
    return localStorage.getItem(TOUR_KEY) === '1'
  } catch {
    return true
  }
}

function markSeen(): void {
  try {
    localStorage.setItem(TOUR_KEY, '1')
    localStorage.setItem(ONBOARD_KEY, '1')
  } catch {
    /* localStorage 불가 — 무시 */
  }
}

export function useTour() {
  return {
    visible,
    /** 수동으로 가이드 열기(헤더 버튼). */
    open(): void {
      visible.value = true
    },
    /** 닫기(완료/건너뛰기 공통) — 1회성 기억. */
    close(): void {
      visible.value = false
      markSeen()
    },
    /** 첫 방문이면 자동으로 연다. */
    openIfFirstVisit(): void {
      if (!tourSeen()) visible.value = true
    },
    tourSeen,
  }
}
