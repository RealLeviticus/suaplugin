FROM node:22-alpine AS dependencies
WORKDIR /app/vatpac-hosting
COPY vatpac-hosting/package*.json ./
RUN npm ci --omit=dev

FROM node:22-alpine
ENV NODE_ENV=production
WORKDIR /app
COPY --from=dependencies /app/vatpac-hosting/node_modules ./vatpac-hosting/node_modules
COPY vatpac-hosting ./vatpac-hosting
COPY cloudflare-pages/package.json ./cloudflare-pages/package.json
COPY cloudflare-pages/public ./cloudflare-pages/public
COPY cloudflare-pages/functions ./cloudflare-pages/functions
COPY cloudflare-automation/package.json ./cloudflare-automation/package.json
COPY cloudflare-automation/src ./cloudflare-automation/src
USER node
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD wget -q -O - http://127.0.0.1:8080/healthz || exit 1
CMD ["node", "vatpac-hosting/src/server.js"]
