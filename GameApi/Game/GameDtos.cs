namespace GameApi.GameLoop;

// Server→client payloads for the round loop. SECURITY: nothing here may
// distinguish the AI from a human. Answers are keyed by display name only; there
// are no author ids, no nullable-author hints, no "isAi" flags. The AI's real
// identity is revealed exactly once, in GameEnded, after the game is over.

// One revealed answer. Order is randomized by the engine before it's sent.
public record RevealedAnswerDto(string DisplayName, string Text);

// AnswersRevealed payload.
public record AnswersRevealedDto(int Round, string Prompt, List<RevealedAnswerDto> Answers);

// A single message in the end-of-game transcript. Here — and ONLY here, once the
// game is over — IsAi flags which answers were the AI's, for the highlight reveal.
public record TranscriptMessageDto(int Round, string DisplayName, string Text, bool IsAi);

// GameEnded payload: how it ended, who won (display name), the AI's fake name it
// played under, and the full annotated transcript.
public record GameEndedDto(
    string WinType,
    string? WinnerName,
    string AiRealIdentityName,
    List<TranscriptMessageDto> FullTranscript);

// ---- reverse mode (phase 22) ----

// One anonymous, shuffled answer at a reverse reveal. Id is a stable per-round label
// ("a","b","c",...); the author is deliberately NOT sent — the whole point is the group
// guesses along with the AI.
public record AnonAnswerDto(string Id, string Text);

// ReverseRevealStarted payload: the shuffled anonymous answers, shown while the AI
// "analyzes" before its guesses land.
public record ReverseRevealStartedDto(int Round, string Prompt, List<AnonAnswerDto> Answers);

// The AI's verdict for one answer id, revealed after analysis: who it guessed, whether it
// was right, who ACTUALLY wrote it (revealed now — reverse has no hidden AI to protect),
// and the taunt.
public record AiGuessDto(string AnswerId, string GuessedName, bool Correct, string ActualName, string Taunt);

// AiGuessesRevealed payload: every attribution for the round plus the running tally.
public record AiGuessesRevealedDto(
    int Round, List<AiGuessDto> Guesses, int RoundCorrect, int RoundTotal, int GameCorrect, int GameTotal);
