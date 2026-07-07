# Turing the Other Cheek

Social deduction party game where one player in the lobby is secretly an AI, and everyone
else is trying to figure out who. The twist is the AI actually learns how you write over
time, so the more you play the better it gets at blending in.

Stack is ASP.NET Core 8 + EF Core + Postgres on the backend, React 19 + Vite on the front.
Auth is Identity + JWT (same setup I used on my task app). Game runs over SignalR.

Backend lives in `GameApi`, the phone-first React client is in `game-client`.

Still building this out — docs and deploy notes coming as I go.
