import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import 'element-plus/theme-chalk/dark/css-vars.css'

import App from './App.vue'
import router from './router'
import './styles/main.css'

import { setUnauthorizedHandler } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import { toastInfo } from '@/utils/toast'

const app = createApp(App)
const pinia = createPinia()

app.use(pinia)
app.use(router)
app.use(ElementPlus)

// Global 401 handling: clear the session and bounce to market with a prompt.
setUnauthorizedHandler(() => {
  const auth = useAuthStore()
  if (auth.isAuthenticated) {
    auth.logout()
    toastInfo('Session expired — pick a survivor to sign in.')
    router.push({ name: 'market' })
  }
})

app.mount('#app')
