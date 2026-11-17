using ArenaBoxBot;

internal static class OptionEntityFallbackTests
{
    internal static void Run()
    {
        // Native Power.log packet194, 2026-10-14 05:58:32.5951367:
        // eight same-ID hand spells; requested pos8 is entity560. Target604
        // is the friendly CS2_101t3 at PLAY pos1. Native UI lookup returned null.
        var hand = new List<OptionEntityCandidate>();
        var ids = new[] {524,525,526,527,528,529,559,560};
        for(var i=0;i<ids.Length;i++) hand.Add(C(ids[i],"BT_025",i+1,2,"HAND"));
        Require(OptionEntityFallbackPolicy.Resolve(hand,"BT_025",8,2,"HAND") == 560, "Exact captured spell/position must resolve among duplicates");
        hand.Add(C(560,"BT_025",8,2,"HAND"));
        Require(OptionEntityFallbackPolicy.Resolve(hand,"BT_025",8,2,"HAND") == 560, "Repeated options for same entity are not ambiguity");
        hand.Add(C(601,"BT_025",8,2,"HAND"));
        Require(OptionEntityFallbackPolicy.Resolve(hand,"BT_025",8,2,"HAND") == 0, "Different exact entity matches must remain unresolved");
        var targets = new[]{C(19,"VAC_507",2,1,"PLAY"), C(12,"ICC_820",1,1,"PLAY"),
            C(561,"CS2_101t4",3,1,"PLAY"),C(604,"CS2_101t3",1,2,"PLAY")};
        Require(OptionEntityFallbackPolicy.Resolve(targets,"CS2_101t3",1,2,"PLAY")==604, "Captured exact legal friendly target");
        Require(OptionEntityFallbackPolicy.Resolve(targets,"CS2_101t3",1,1,"PLAY")==0, "Do not guess opposite side");
        Require(OptionEntityFallbackPolicy.Resolve(targets,"CS2_101t4",1,2,"PLAY")==0, "Do not guess a different token or position");
        Require(OptionEntityFallbackPolicy.Resolve(new[]{C(604,"CS2_101t3",1,2,"PLAY",false)},"CS2_101t3",1,2,"PLAY")==0, "Illegal target rejected");
        Require(OptionEntityFallbackPolicy.Resolve(targets,"CS2_101t3",1,2,"HAND")==0, "Wrong zone rejected");
        Require(OptionEntityFallbackPolicy.Resolve(targets,"",1,2,"PLAY")==0, "Unknown card rejected");
        Require(OptionEntityFallbackPolicy.IsCurrent(16872,16872,194,194,.339,true), "Captured fresh own packet");
        Require(!OptionEntityFallbackPolicy.IsCurrent(17528,16872,194,194,.339,true), "Old process rec rejected");
        Require(!OptionEntityFallbackPolicy.IsCurrent(16872,16872,195,194,.339,true), "Old packet rejected");
        foreach(var age in new[]{-1d,10.001d,double.NaN,double.PositiveInfinity})
            Require(!OptionEntityFallbackPolicy.IsCurrent(16872,16872,194,194,age,true), "Unknown/stale age rejected");
        Require(!OptionEntityFallbackPolicy.IsCurrent(16872,16872,194,194,.339,false), "Choice/opponent/inactive/unknown turn rejected");
        Console.WriteLine("PASS: actual packet194 legal spell560/target604 replay; exact position/card/side/zone/legality/uniqueness and fresh own PID/packet guards");
    }
    private static OptionEntityCandidate C(int id,string card,int pos,int player,string zone,bool legal=true)
        => new(){Id=id,CardId=card,Position=pos,Controller=player,Zone=zone,Legal=legal};
    private static void Require(bool condition,string message) {if(!condition)throw new InvalidOperationException(message);}
}
