using ArenaBoxBot;

internal static class TargetlessAbilityTests
{
    internal static void Run()
    {
        // Actual 2026-10-26 Power packet116 at22:40:09.6886223, game23708.
        // POWER option2/main61 BE_013, child0/62 BE_013t1 is NONE with no
        // targets. Child3/64 BE_013t3 is REQ_LITERALLY_UNPLAYABLE.
        var options = new[] { C(0,62,"BE_013t1"), C(1,63,"BE_013t2"), C(2,65,"BE_013t4"), C(3,64,"BE_013t3",false) };
        var picked = TargetlessAbilityPolicy.Resolve(options,61,2,"BE_013t1");
        Require(picked?.OptionIndex==2 && picked.SubOptionIndex==0 && picked.AbilityId==62,"Actual targetless skill child must match exactly");
        Require(TargetlessAbilityPolicy.Resolve(options,61,2,"BE_013t3")==null,"Actual unplayable skill must stay rejected");
        Require(TargetlessAbilityPolicy.Resolve(options,61,2,"UNKNOWN")==null,"Never invent a skill from the remaining legal alternatives");
        Require(TargetlessAbilityPolicy.Resolve(options,62,2,"BE_013t1")==null,"Wrong main source rejected");
        Require(TargetlessAbilityPolicy.Resolve(options,61,1,"BE_013t1")==null,"Wrong controller rejected");
        Require(TargetlessAbilityPolicy.Resolve(new[]{C(0,62,"BE_013t1"),C(0,62,"BE_013t1")},61,2,"BE_013t1")==null,"Duplicate matching options are ambiguous");
        var targeted=C(0,62,"BE_013t1"); targeted.TargetCount=1;
        Require(TargetlessAbilityPolicy.Resolve(new[]{targeted},61,2,"BE_013t1")==null,"Targeted skill must not bypass missing target guard");
        var illegalMain=C(0,62,"BE_013t1"); illegalMain.MainLegal=false;
        Require(TargetlessAbilityPolicy.Resolve(new[]{illegalMain},61,2,"BE_013t1")==null,"Invalid main including REQ_TARGET_TO_PLAY rejected");
        var mainFallback=C(-1,61,"BE_013t1");
        Require(TargetlessAbilityPolicy.Resolve(new[]{mainFallback},61,2,"BE_013t1")==null,"Ordinary main attack is not an explicit skill child");
        Require(TargetlessAbilityPolicy.Current(37420,37420,116,116,.041,true,"attack","minion_attack","BE_013t1",true,true),"Actual fresh rejected recommendation");
        foreach(var age in new[]{-1d,10.001d,double.NaN,double.PositiveInfinity,double.NegativeInfinity})
            Require(!TargetlessAbilityPolicy.Current(37420,37420,116,116,age,true,"attack","minion_attack","BE_013t1",true,true),"Unknown/stale recommendation rejected");
        Require(!TargetlessAbilityPolicy.Current(37420,37421,116,116,.041,true,"attack","minion_attack","BE_013t1",true,true),"Wrong process rejected");
        Require(!TargetlessAbilityPolicy.Current(37420,37420,117,116,.041,true,"attack","minion_attack","BE_013t1",true,true),"Wrong packet rejected");
        Require(!TargetlessAbilityPolicy.Current(37420,37420,116,116,.041,false,"attack","minion_attack","BE_013t1",true,true),"Queue/choice/mulligan/opponent/BG/inactive readiness remains closed");
        Require(!TargetlessAbilityPolicy.Current(37420,37420,116,116,.041,true,"attack","hero_attack","BE_013t1",true,true),"Hero attack is outside correction scope");
        Require(!TargetlessAbilityPolicy.Current(37420,37420,116,116,.041,true,"play","minion_attack","BE_013t1",true,true),"Other action untouched");
        Require(!TargetlessAbilityPolicy.Current(37420,37420,116,116,.041,true,"attack","minion_attack","",true,true),"Ordinary targetless attack remains rejected");
        Require(!TargetlessAbilityPolicy.Current(37420,37420,116,116,.041,true,"attack","minion_attack","BE_013t1",false,true),"Explicit target or position untouched");
        Require(!TargetlessAbilityPolicy.Current(37420,37420,116,116,.041,true,"attack","minion_attack","BE_013t1",true,false),"Already legal or differently rejected mapping untouched");
        Console.WriteLine("PASS: actual packet116 source61/skill62/option2/sub0; stale/PID/packet/phase/targets/invalid/ambiguous/wrong-source and normal attacks remain closed");
    }
    private static TargetlessAbilityOption C(int sub,int id,string card,bool legal=true)
        => new(){OptionIndex=2,SubOptionIndex=sub,MainId=61,AbilityId=id,AbilityCardId=card,AbilityController=2,MainLegal=true,SubLegal=legal,TargetCount=0};
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
