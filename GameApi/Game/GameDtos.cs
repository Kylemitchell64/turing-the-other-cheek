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
