
# ConexyAI frontend (React + Vite). Multi-stage build:
#   build -> node:20-alpine (npm ci + vite build)
#   final -> nginx:alpine    (serves static assets, proxies /api and /hubs to backend)

# ---- Build stage ----
FROM node:20-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

# ---- Runtime stage ----
# SEC_NGINX2ROOT: добавлено 2026-11-20 — 18–19 ноября 2026 в NGINX закрыли «nginx2root» (CVE-2026-123007,
# CVE-2026-123633, CVE-2026-121810, CVE-2026-123634, CVE-2026-113425 — HTTP/2 RCE/UAF). Здесь включён
# HTTP/2 и проксирование в backend, то есть оба вектора применимы. Исправлено в 1.30.7 (stable) и 1.31.9
# (mainline); версию НЕ опускать ниже 1.30.7 и обновлять регулярно (docker pull + пересборка фронта).
FROM nginx:1.30.7-alpine AS final
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
