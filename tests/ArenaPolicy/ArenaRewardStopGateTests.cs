using ArenaBoxBot;
using ArenaController;

internal static class ArenaRewardStopGateTests
{
    internal static void Run()
    {
        // Replay 20:11 ACK/NO_RUN followed by controller close one second
        // later, before the production receipt observer's three-second proof.
        var data = new ArenaStatsData { RunKey="normal:153:2771759649", RunActive=true,
            RewardPending=true, LastCompletedRunKey="normal:153:2771746799", RewardsClaimed=7 };
        var receipt = new ArenaRewardReceiptPolicy();
        bool Observe(float now, string next="INVALID", bool ready=true, bool active=false, bool chest=false)
            => receipt.Observe(data.RunKey,data.RewardPending,"DRAFT",next,ready,"NO_RUN",active,chest,now);
        Require(!Observe(0) && !Observe(1),"ACK/NO_RUN must retain the existing receipt confirmation window");
        Require(ArenaRewardStopGate.MustWaitForReceipt("NO_RUN",data.RewardPending),"Do not release automation before receipt is persisted");
        Require(!ArenaStopBoundary.IsKnownSafe(true,true,true,false,"DRAFT",ArenaRewardStopGate.PendingState),"Current pending receipt snapshot must block controller close");
        Require(data.RewardsClaimed==7 && data.LastCompletedRunKey.EndsWith("2771746799"),"No forged reward during the wait");
        Require(Observe(3),"Existing native empty proof completes after three seconds");
        Require(ArenaStatsLedger.CompleteRun(data),"Use the existing reward ledger completion");
        Require(!data.RunActive && !data.RewardPending && data.LastCompletedRunKey==data.RunKey && data.RewardsClaimed==8,"Completion is persisted before stop becomes safe");
        Require(!ArenaRewardStopGate.MustWaitForReceipt("NO_RUN",data.RewardPending) && !ArenaStatsLedger.CompleteRun(data),"Release only after completion and do not count twice");
        foreach(var state in new[]{"MIDRUN","REDRAFTING","DRAFTING"})
            Require(!ArenaRewardStopGate.MustWaitForReceipt(state,false),"Nonterminal paid round stops remain available");
        foreach(var next in new[]{"LOADING","GAMEPLAY"})
        {
            receipt.Reset(); data.RewardPending=true;
            Require(!Observe(0,next) && !Observe(10,next),"Loading placeholder cannot certify rewards");
        }
        receipt.Reset();
        Require(!Observe(0,ready:false) && !Observe(10,ready:false),"Unknown native state cannot certify rewards");
        Console.WriteLine("PASS: terminal marker waits for existing native receipt proof and ledger completion before controller close; paid midrun stops unchanged");
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
