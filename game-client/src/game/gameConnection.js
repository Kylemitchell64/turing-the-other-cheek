import { HubConnectionBuilder, HttpTransportType, LogLevel } from "@microsoft/signalr";
import { API_BASE } from "../api/config";

// Builds a hub connection that carries the JWT as ?access_token= (the server pulls
// it off the query for the hub path). Auto-reconnect handles a phone screen-lock —
// on reconnect the client re-invokes JoinLobby with its userId-backed seat.
export function buildGameConnection(getToken) {
  return new HubConnectionBuilder()
    .withUrl(`${API_BASE}/hubs/game`, {
      accessTokenFactory: () => getToken(),
      // WebSockets preferred; fall back to long-polling behind restrictive proxies.
      transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling,
    })
    .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
    .configureLogging(LogLevel.Warning)
    .build();
}
