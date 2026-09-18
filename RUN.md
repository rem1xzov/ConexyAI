# Running the ConexyAI stack

Prerequisites: Docker, .NET 9 SDK, and Node.js 18+.

1. Start PostgreSQL (Docker):
   ```sh
   docker compose up -d
   ```

2. Start the backend API (http://localhost:5039):
   ```sh
   dotnet run --project ConexyAI/ConexyAI.csproj
   ```

3. Install frontend dependencies and start the Vite dev server (http://localhost:5173):
   ```sh
   cd frontend && npm install && npm run dev
   ```

Once running, open http://localhost:5173, click **Get Dev Token**, then submit a task.

> The backend connection string in `ConexyAI/appsettings.json` matches the Docker
> credentials (`postgres` / `postgres`). If you change `docker-compose.yml`, update
> both places. The frontend proxies `/api` and `/hubs` to `VITE_API_URL`
> (see `frontend/.env`), defaulting to `http://localhost:5039`.

## Production deployment

For the production Docker stack (backend + frontend + PostgreSQL + Docker socket proxy),
see [`DEPLOY.md`](DEPLOY.md) and use `docker-compose.prod.yml` with `.env.example`.
