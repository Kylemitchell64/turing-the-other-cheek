import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

// k6 load test: hammers the three HTTP calls a real client makes on its way into a
// lobby — the /api/health ping (what UptimeRobot hits), a guest login for a JWT, and the
// SignalR /negotiate handshake that precedes the websocket. It does NOT hold live
// websockets (k6 core doesn't speak SignalR's framing); negotiate is the closest HTTP
// approximation of connection setup load.
//
// Point it at a locally-running API on the Development in-memory DB + Mock brain so it
// needs no Postgres and no AI keys. Raise the per-IP rate limit too — otherwise the
// production 30-req/min fixed window (all of k6's traffic looks like one IP) just returns
// 429s and you measure the limiter, not the server:
//   API:  ASPNETCORE_ENVIRONMENT=Development UseInMemoryDb=true Ai__Brain=Mock \
//         RateLimit__PermitsPerMinute=1000000 JWT_KEY=<64+ chars> \
//         ASPNETCORE_URLS=http://127.0.0.1:5222 dotnet run --no-launch-profile
//   Load: k6 run loadtest/k6-lobbies.js
// (The /hubs SignalR path is exempt from the limiter by design, so negotiate is never
// throttled regardless.)

const BASE = __ENV.API_URL || 'http://127.0.0.1:5222';

export const options = {
  scenarios: {
    // Ramp to 20 concurrent virtual "guests", hold, then wind down.
    guests: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '10s', target: 20 },
        { duration: '20s', target: 20 },
        { duration: '5s', target: 0 },
      ],
      gracefulStop: '5s',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.05'], // <5% of requests may fail
    http_req_duration: ['p(95)<800'], // 95th percentile under 800ms
    checks: ['rate>0.95'],
  },
};

const guestLogins = new Counter('guest_logins');
const negotiations = new Counter('signalr_negotiations');

function randName() {
  return `load_${Math.random().toString(36).slice(2, 10)}`;
}

export default function () {
  // 1) Health ping — cheap SELECT-1-or-degrade endpoint.
  const health = http.get(`${BASE}/api/health`);
  check(health, { 'health 200': (r) => r.status === 200 });

  // 2) Guest login -> JWT. Unique random name per iteration = a fresh guest account.
  const login = http.post(
    `${BASE}/api/auth/guest`,
    JSON.stringify({ username: randName() }),
    { headers: { 'Content-Type': 'application/json' } }
  );
  const loginOk = check(login, {
    'guest login 200': (r) => r.status === 200,
    'guest login returns token': (r) => !!r.json('token'),
  });
  if (loginOk) guestLogins.add(1);

  // 3) SignalR negotiate — the handshake every client does before the websocket opens.
  //    Authenticated via ?access_token= exactly like the hub expects.
  const token = login.status === 200 ? login.json('token') : null;
  if (token) {
    const neg = http.post(
      `${BASE}/hubs/game/negotiate?negotiateVersion=1&access_token=${token}`,
      null
    );
    const negOk = check(neg, {
      'negotiate 200': (r) => r.status === 200,
      'negotiate returns connectionId': (r) => !!r.json('connectionId'),
    });
    if (negOk) negotiations.add(1);
  }

  sleep(1);
}
