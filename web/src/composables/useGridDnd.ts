import { ref } from 'vue'
import type { StashPlacementDto } from '@/api/types'

// The item currently being dragged, shared across every ItemGrid instance so a
// drag that starts in one grid (e.g. Stash) can be dropped on another (Pockets, a
// nested backpack/rig container, or an equipment slot). Only one drag can be in
// flight at a time, so a single module-level ref is enough.
export interface ActiveDrag {
  placement: StashPlacementDto
  // cell offset within the footprint where the item was grabbed
  grabX: number
  grabY: number
}

const activeDrag = ref<ActiveDrag | null>(null)

export function useGridDnd() {
  function begin(drag: ActiveDrag): void {
    activeDrag.value = drag
  }
  function end(): void {
    activeDrag.value = null
  }
  return { activeDrag, begin, end }
}
