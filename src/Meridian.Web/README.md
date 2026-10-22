# Meridian Vote Tracker — Web

Angular dashboard for CSV imports, conflict review, institute vote positions, and ranking snapshots. The root `README.md` contains the complete Compose and local development instructions.

For local development with Node.js 24.15+:

```bash
npm ci
npm start
```

The app runs at `http://localhost:4200` and calls the API through the configured dev-server proxy or the Nginx proxy in Docker Compose. Start the API at `http://localhost:8080` before using the local dev server. Checks:

```bash
npm run build
npm test -- --watch=false
```
