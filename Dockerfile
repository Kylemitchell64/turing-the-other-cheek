# ---- stage 1: build the React client ----
FROM node:22 AS client
WORKDIR /client
COPY game-client/package*.json ./
RUN npm ci
COPY game-client/ ./
# No VITE_API_URL baked in here: the image serves the client same-origin, so the
# client talks to its own host. (Vercel builds its own copy with VITE_API_URL set.)
RUN npm run build

# ---- stage 2: publish the API ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY GameApi/GameApi.csproj GameApi/
RUN dotnet restore GameApi/GameApi.csproj
COPY GameApi/ GameApi/
RUN dotnet publish GameApi/GameApi.csproj -c Release -o /app/publish

# ---- stage 3: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish ./
# Drop the built client into wwwroot so UseStaticFiles + MapFallbackToFile serve it.
COPY --from=client /client/dist ./wwwroot
# Listen on 8080 by default (used for `docker run -p 8080:8080`). Render sets $PORT
# (e.g. 10000) and Program.cs binds 0.0.0.0:$PORT, which overrides this. ASPNETCORE_URLS
# is deliberately left unset so the $PORT path always wins in prod.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "GameApi.dll"]
