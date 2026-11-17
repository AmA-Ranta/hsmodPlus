using ArenaController;

internal static class ArenaRoundLimitTests
{
    internal static void Run()
    {
        const string key="normal:153:2771735595";
        bool Check(string state,bool active,bool reward,string completed="",bool game=false,bool buy=false,
            string runtimeKey=key,double age=.1,bool selected=true,bool reached=true)
            =>ArenaRoundLimitStopPolicy.ShouldRequestStop(reached,selected,key,runtimeKey,age,game,state,active,reward,buy,completed);
        foreach(var state in new[]{"DRAFTING","MIDRUN","MIDRUN_REDRAFT_PENDING","REDRAFTING","EDITING_DECK","GAMEPLAY"})
            Require(!Check(state,true,false),"Entry quota must not truncate hero/draft/paid round: "+state);
        Require(Check("REWARDS",true,true),"Terminal rewards request existing preserving stop");
        Require(Check("IN_REWARDS",true,true),"Rewards alias handled");
        Require(!Check("REWARDS",true,false),"Uncorroborated reward flag cannot stop round");
        Require(!Check("REWARDS",true,true,game:true),"Gameplay must not authorize a round stop");
        Require(!Check("REWARDS",true,true,buy:true),"Pending transaction must settle first");
        Require(Check("NO_RUN",false,false,key),"Matching fully completed round can stop before new entry");
        Require(!Check("NO_RUN",false,false,"normal:153:old"),"Old completion not proof for new round");
        Require(!Check("NO_RUN",true,false,key),"Paid active round must remain intact");
        Require(!Check("REWARDS",true,true,runtimeKey:"underground:153:other"),"Cross-mode/round state rejected");
        foreach(var age in new[]{-1d,5.001d,double.NaN,double.PositiveInfinity})
            Require(!Check("REWARDS",true,true,age:age),"Stale/unknown runtime rejected");
        Require(!Check("REWARDS",true,true,selected:false),"Other mode runtime rejected");
        Require(!Check("REWARDS",true,true,reached:false),"No reached quota means no stop");
        Require(ArenaRoundLimitStopPolicy.CriticalPollMilliseconds==1000,"Terminal checks must not retain five-second cadence");
        Console.WriteLine("PASS: admitted last Arena round completes draft/play/rewards before MaxRuns stop; current identity/freshness/transaction proof, 1s terminal cadence");
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
