import { ElMessage } from 'element-plus'
import { ApiClientError } from '@/api/client'

export function toastError(err: unknown, fallback = 'Something went wrong.'): void {
  let message = fallback
  if (err instanceof ApiClientError) {
    message = err.apiError.message
    const details = err.apiError.details
    if (details && details.length) message += ` (${details.join(', ')})`
  } else if (err instanceof Error) {
    message = err.message
  }
  ElMessage({ message, type: 'error', showClose: true })
}

export function toastSuccess(message: string): void {
  ElMessage({ message, type: 'success', showClose: true })
}

export function toastInfo(message: string): void {
  ElMessage({ message, type: 'info', showClose: true })
}
