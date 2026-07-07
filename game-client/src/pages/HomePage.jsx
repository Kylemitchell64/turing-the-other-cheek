import { useAuth } from "../auth/AuthContext";

export default function HomePage() {
  const { user, logout } = useAuth();

  return (
    <div className="screen">
      <div className="topbar">
        <span className="who">{user?.displayName || user?.unique_name}</span>
        <button className="ghost" onClick={logout}>log out</button>
      </div>

      <div className="panel">
        <h1 className="glow">LOBBY</h1>
        <p className="tagline">the AI is learning how you type. every game makes it harder to catch.</p>

        <div className="menu">
          <button className="primary" disabled>create lobby</button>
          <button className="primary" disabled>join by code</button>
          <button className="ghost" disabled>my stats</button>
          <button className="ghost" disabled>writing samples</button>
        </div>

        <p className="soon">// lobby + game coming as I build this out</p>
      </div>
    </div>
  );
}
