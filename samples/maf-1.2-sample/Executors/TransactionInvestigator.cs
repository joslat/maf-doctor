// SPDX-License-Identifier: MIT
//
// ⚠️ DELIBERATE ANTI-PATTERNS:
//   1. Non-generic ValueTask return (MAF130-EXEC-001 / MAF001)
//
// The class is `partial` without `sealed`. That is fine: the source generator only
// requires `partial` (MAFGENWF003). maf-doctor flagged the missing `sealed` as
// MAF-AP-WF-001 until 2026-10; it no longer does.

using Microsoft.Agents.AI.Workflows;

namespace MafSample.FraudClaims.Executors;

public partial class TransactionInvestigator : Executor
{
    public TransactionInvestigator() : base(id: "Transactions") { }

    [MessageHandler]
    // ⚠️ MAF130-EXEC-001 — should return ValueTask<InvestigationFinding>.
    public async ValueTask HandleAsync(ClaimInput claim, IWorkflowContext context, CancellationToken ct)
    {
        await Task.Yield();
        var finding = new InvestigationFinding(
            Investigator: "Transactions",
            RiskScore: 0.71,
            Notes: $"Three large refund requests on customer {claim.CustomerId} in last 30 days.");
        _ = finding;
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder builder) => builder;
}
