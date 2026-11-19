# The web: a static Next.js export served by nginx.
#   docker build -t deficitsnatcher-web DeficitSnatcher.Web
FROM node:24-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .

# Empty: the API is on this same site under /api (nginx.conf). Set only to call an API elsewhere.
ARG NEXT_PUBLIC_API_URL
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm run build

FROM nginx:1.31-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/out /usr/share/nginx/html
EXPOSE 80
