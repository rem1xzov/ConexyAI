# syntax=docker/dockerfile:1

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
FROM nginx:1.27-alpine AS final
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
