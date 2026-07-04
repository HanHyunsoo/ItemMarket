#!/bin/sh
# nginx 기동 전에 실행됨(/docker-entrypoint.d). $VITE_API_BASE 를 config.js 로 굽는다.
# 앱은 window.__API_BASE__ 를 읽으므로 이미지 재빌드 없이 API URL 을 바꿀 수 있다.
set -eu

API_BASE="${VITE_API_BASE:-}"
CONFIG_FILE="/usr/share/nginx/html/config.js"

printf 'window.__API_BASE__ = "%s"\n' "$API_BASE" > "$CONFIG_FILE"
echo "[entrypoint] config.js -> window.__API_BASE__ = \"$API_BASE\""
