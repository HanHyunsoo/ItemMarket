# syntax=docker/dockerfile:1
# Vue 3 SPA — build with Node, serve with nginx:alpine.
# API base is runtime-configurable: the built app reads window.__API_BASE__ from
# /config.js, which the entrypoint regenerates from $VITE_API_BASE at container
# start (so one image works against any API URL without a rebuild).

FROM node:20-alpine AS build
WORKDIR /web
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginx:alpine AS runtime
COPY --from=build /web/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY docker-entrypoint.sh /docker-entrypoint.d/40-api-base.sh
RUN chmod +x /docker-entrypoint.d/40-api-base.sh

# 브라우저가 접근할 API URL. compose/런타임에서 오버라이드.
ENV VITE_API_BASE=http://localhost:8080
EXPOSE 80
# nginx:alpine 기본 엔트리포인트가 /docker-entrypoint.d/*.sh 를 실행한 뒤 nginx 를 띄운다.
