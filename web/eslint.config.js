import js from '@eslint/js'
import globals from 'globals'
import pluginVue from 'eslint-plugin-vue'
import tseslint from 'typescript-eslint'
import prettier from 'eslint-config-prettier'

// Flat config: Vue 3 + TypeScript. Prettier last so it disables any
// formatting rules that would fight the formatter.
export default tseslint.config(
  { ignores: ['dist', 'node_modules', 'public'] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  ...pluginVue.configs['flat/recommended'],
  {
    files: ['**/*.{ts,vue}'],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'module',
      globals: { ...globals.browser, ...globals.node },
      parserOptions: {
        // Let vue-eslint-parser hand the <script lang="ts"> body to the TS parser.
        parser: tseslint.parser,
      },
    },
    rules: {
      // TypeScript's own checker handles undefined identifiers and DOM lib types.
      'no-undef': 'off',
      // Optional props (`icon?: string`) intentionally default to undefined under TS.
      'vue/require-default-prop': 'off',
    },
  },
  prettier,
)
