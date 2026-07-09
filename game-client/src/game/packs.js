// Prompt-pack display metadata, mirrored from the server's PromptPacks. Only labels
// and descriptions live here (the prompts themselves never reach the lobby screen).
// ageNote shows an informational 18+/21+ line under the selector — no blocking flow.
export const PACKS = [
  {
    key: "family",
    label: "FAMILY",
    description: "keep it clean. party prompts anyone can answer.",
    ageNote: null,
  },
  {
    key: "adult",
    label: "ADULT 18+",
    description: "18+ // crude party humor, innuendo and confessions. keep it playful.",
    ageNote: "18+ // for a room that's all adults. crude humor, no blocking — just a heads up.",
  },
  {
    key: "drinking",
    label: "DRINKING 21+",
    description: "21+ // never-have-i-ever and dares. drink responsibly, know your limits, never drink and drive.",
    ageNote: "21+ // please drink responsibly. know your limits and never drink and drive.",
  },
  {
    key: "trivia",
    label: "TRIVIA",
    description: "obscure general knowledge. no googling, commit to your guess.",
    ageNote: null,
  },
];

export const DEFAULT_PACK = "family";

export function packFor(key) {
  return PACKS.find((p) => p.key === key) || PACKS[0];
}
