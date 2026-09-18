namespace GameApi.GameLoop;

// All the round-loop durations, bound from the "GameTimings" config section so the
// integration test can drop them to a couple hundred ms and run the whole playtest
// in seconds. Production values are the spec defaults (30s / 20s / 10s / 5s).
public class GameTimings
{
    public int PromptSeconds { get; set; } = 30;
    public int RevealSeconds { get; set; } = 5;
    public int AccusationSeconds { get; set; } = 20;
    public int VetoSeconds { get; set; } = 10;
    public int PrioritySeconds { get; set; } = 5;

    // How often the engine wakes to check per-lobby deadlines.
    public int TickMilliseconds { get; set; } = 250;

    public int MaxRounds { get; set; } = 8;

    // Solo demo (phase 29): per eligible round, the chance a bot stand-in accuses someone
    // and another bot immediately fakes-out — a scripted demo of the veto rule that leaks
    // nothing (a veto never reveals the result). Tests set it to 1 to force it.
    public double SoloDramaChance { get; set; } = 0.45;

    public TimeSpan Prompt => TimeSpan.FromSeconds(PromptSeconds);
    public TimeSpan Reveal => TimeSpan.FromSeconds(RevealSeconds);
    public TimeSpan Accusation => TimeSpan.FromSeconds(AccusationSeconds);
    public TimeSpan Veto => TimeSpan.FromSeconds(VetoSeconds);
    public TimeSpan Priority => TimeSpan.FromSeconds(PrioritySeconds);
}
