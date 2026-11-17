using ArenaBoxBot;

internal static class RecommendationMemberAccessTests
{
    private sealed class NativeFieldSchema
    {
        public string Action = "end_turn";
        public string RequestId = "box-1-2-end_turn";
        public string ActionAfterPreAdjust = "choice";
        internal string ExecutableAction => Action == "pre_adjust" ? ActionAfterPreAdjust : Action;
    }

    private sealed class PropertySchema
    {
        public string Action => "choice";
        public string RequestId => "box-choice-2";
    }

    private sealed class BadSchema
    {
        public int Action = 3;
    }

    internal static void Run()
    {
        // This is the actual installed HsMod member shape, verified by Cecil:
        // Action/RequestId are public fields, ExecutableAction is a property.
        var recommendation = new NativeFieldSchema();
        Require(RecommendationMemberAccess.ReadAction(recommendation) == "end_turn", "Native end_turn must retain its acknowledgement gate");
        Require(RecommendationMemberAccess.ReadText(recommendation, "RequestId") == "box-1-2-end_turn", "Native field request identity must be readable");
        recommendation.RequestId = "box-1-2-play_minion";
        recommendation.Action = "play";
        Require(RecommendationMemberAccess.ReadText(recommendation, "RequestId") == "box-1-2-play_minion", "Different request at the same packet must reset its recovery budget");
        Require(RecommendationMemberAccess.ReadAction(recommendation) == "play", "Live ordinary actions must not be mislabeled empty");
        recommendation.Action = "pre_adjust";
        Require(RecommendationMemberAccess.ReadAction(recommendation) == "choice", "Internal native executable getter preserves post-adjustment choice gate");
        Require(RecommendationMemberAccess.ReadAction(new PropertySchema()) == "choice", "Property-based compatible schema retains choice gate");
        Require(RecommendationMemberAccess.ReadText(new PropertySchema(), "RequestId") == "box-choice-2", "Property-based request identity remains supported");
        Require(RecommendationMemberAccess.ReadAction(new BadSchema()) == null, "Non-string members are not actions");
        Require(RecommendationMemberAccess.ReadAction(null!) == null, "Missing recommendation stays missing");
        Console.WriteLine("PASS: native recommendation field/property schema, action classification and request identity");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
