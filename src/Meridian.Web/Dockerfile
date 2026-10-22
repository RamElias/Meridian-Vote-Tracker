FROM node:24-alpine AS build
WORKDIR /app
COPY src/Meridian.Web/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/Meridian.Web/ ./
RUN npm run build

FROM nginx:alpine
COPY src/Meridian.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/meridian-web/browser /usr/share/nginx/html
EXPOSE 80
