// SPDX-License-Identifier: MIT
//
// ⚠️ Note on the "top-level Instructions" anti-pattern:
//
// The maf-doctor registry entry MAF130-INSTRUCTIONS-001 documents a top-level
// Instructions property on ChatClientAgentOptions. **No GA release has it**: it
// was removed before 1.0 (present in 1.0.0-preview.251110.2, gone in
// 1.0.0-rc3), so code referencing it fails to compile with CS0117 and the
// anti-pattern cannot be reproduced here. The entry applies to pre-1.0.0
// codebases (corrected 2026-10-02; it first said "silently ignored in 1.3.0").
// See README.md → "Phase T registry corrections".
//
// To preserve the spirit of the anti-pattern in this sample, we use the
// CORRECT 1.3 pattern (Instructions nested inside ChatOptions). The other
// anti-patterns in this sample still trigger the toolkit's scanners.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MafSample.FraudClaims.Agents;

public static class IntakeAgent
{
    public static ChatClientAgent Create(IChatClient chatClient) =>
        new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Intake",
            Description = "Parses a free-form claim into a structured ClaimInput.",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are the intake desk for a fraud-claims pipeline.
                    Read the raw claim text and emit a structured ClaimInput JSON.
                    Be terse. Do not invent fields that are not present in the text.
                    """,
            },
        });
}
