import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import { AuthProvider } from "./auth/AuthContext";
import { LobbyProvider } from "./game/LobbyContext";
import { MusicProvider } from "./audio/MusicContext";
import App from "./App.jsx";
import "./index.css";

createRoot(document.getElementById("root")).render(
  <StrictMode>
    <BrowserRouter>
      <AuthProvider>
        <LobbyProvider>
          <MusicProvider>
            <App />
          </MusicProvider>
        </LobbyProvider>
      </AuthProvider>
    </BrowserRouter>
  </StrictMode>
);
