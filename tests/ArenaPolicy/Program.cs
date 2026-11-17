using ArenaBoxBot;

static class ArenaPolicyTests
{
    private static int Main()
    {
        ControllerLobbyPolicyTests.Run();
        ControllerCommandContractTests.Run();
    ArenaRunStopTests.Run();
        PowerQueueProgressWindowTests.Run();
        ChoicePacketOrderTests.Run();
        BgPositionProgressTests.Run();
        BgCompletedGameBudgetTests.Run();
        ArenaRewardStopGateTests.Run();
AssembleRecommendationPolicyTests.Run();
        Require(ControllerMarkerStopReason.Resolve(false, false, "") == "", "Manual OFF must not invent an automatic-close reason");
        Require(ControllerMarkerStopReason.Resolve(false, false, "existing") == "existing", "Other automatic cause must be preserved");
        Require(!string.IsNullOrEmpty(ControllerMarkerStopReason.Resolve(true, false, "")), "Full-reward marker stop must publish its confirmed cause");
        Require(!string.IsNullOrEmpty(ControllerMarkerStopReason.Resolve(false, true, "")), "Nonterminal bound-page marker stop must publish its confirmed cause");
        OptionEntityFallbackTests.Run();
        TargetlessAbilityTests.Run();
        ConstructedTimeoutRefreshBudgetTests.Run();
        OptionExecutionWaitTests.Run();
        MissingRecommendationResponseGateTests.Run();
        ResponseActionPolicyTests.Run();
        AssembleFallbackPolicyTests.Run();
        ArenaSceneRecoveryPolicyTests.Run();
        ControllerModeSwitchPolicyTests.Run();
        HsModApiParserTests.Run();
        ControllerSettingsStoreTests.Run();
        SessionModeBindingStoreTests.Run();
        LiveMatchEvidenceTests.Run();
        UnsubmittedEndTurnRecoveryTests.Run();
        OpponentTimerPolicyTests.Run();
        TicketPurchaseSequenceTests.Run();
        Require(!ArenaStopPolicy.CanConsumeControllerMarker("HUB", "DRAFT", true, false, "MIDRUN", true),
            "Real terminal-game scene gap must not consume the stop before REWARDS loads");
        Require(!ArenaStopPolicy.CanConsumeControllerMarker("DRAFT", "INVALID", false, false, "MIDRUN", true),
            "Old unbound Draft state cannot authorize shutdown");
        Require(!ArenaStopPolicy.CanConsumeControllerMarker("DRAFT", "INVALID", true, true, "MIDRUN", true),
            "Transitioning Draft cannot authorize shutdown");
        Require(!ArenaStopPolicy.CanConsumeControllerMarker("DRAFT", "INVALID", false, true, "REWARDS", true),
            "Bound reward page must complete its acknowledgement");
        Require(ArenaStopPolicy.CanConsumeControllerMarker("DRAFT", "INVALID", false, true, "NO_RUN", false),
            "After full rewards, bound NO_RUN consumes request before purchase");
        Require(ArenaStopPolicy.CanConsumeControllerMarker("DRAFT", "INVALID", false, true, "MIDRUN", true),
            "Nonterminal game may safely stop on its bound deck page");
        RecommendationMemberAccessTests.Run();
        DeckDiscardParserTests.Run();
        DeckDiscardStreamTests.Run();
        DraftRecommendationTests.Run();
        Require(ArenaStopPolicy.BlocksFurtherActions(true, false), "停止请求在对局中必须阻止新动作");
        Require(!ArenaStopPolicy.BlocksFurtherActions(true, true), "奖励结算阶段不能阻止奖励动作");
        Require(!ArenaStopPolicy.BlocksFurtherActions(false, false), "没有停止请求时不能锁死流程");

        Require(!ArenaStopPolicy.IsSafeBoundary("GAMEPLAY", true), "对局中不是安全边界");
        Require(!ArenaStopPolicy.IsSafeBoundary("REWARDS", true), "奖励页不是最终安全边界");
        Require(ArenaStopPolicy.IsSafeBoundary("NO_RUN", false), "无活动轮次必须是安全边界");
        Require(ArenaStopPolicy.IsSafeBoundary("EDITING_DECK", false), "没有活动轮次的编辑页可以安全停止");
        Require(ArenaStopPolicy.IsSafeBoundary("EDITING_DECK", true), "活动轮次但未进入对局的编辑页可以安全停止");
        Require(ArenaStopPolicy.IsSafeBoundary("MIDRUN_REDRAFT_PENDING", true), "输局后的重选页可以安全停止");
        Require(ArenaStopPolicy.IsSafeBoundary("REDRAFTING", true), "重选牌页可以安全停止");
        Require(ArenaStopPolicy.IsSafeBoundary("DRAFTING", true), "选牌页可以安全停止");
        Require(!ArenaStopPolicy.IsSafeBoundary("UNKNOWN", false), "未知状态不能被当作安全边界");
        Require(ArenaController.ArenaStopBoundary.IsKnownSafe(true, true, false, false, "DISABLED", ""),
            "竞技场模式保持选中但脚本已关闭时应允许安全停止");

        var recovery = new ArenaController.ClientRecoveryMonitor();
        var t0 = new DateTime(2026, 10, 6, 22, 0, 0, DateTimeKind.Local);
        Require(recovery.Observe(t0, true, false, true, true, true) is null,
            "盒子窗口短暂消失但心跳正常时不得恢复");
        Require(recovery.Observe(t0.AddSeconds(44), true, false, false, true, true) is null,
            "盒子异常未持续满 45 秒时不得恢复");
        Require(recovery.Observe(t0.AddSeconds(89), true, false, false, true, true)?.Contains("盒子") == true,
            "盒子窗口和心跳连续异常 45 秒后应恢复");
        recovery.Reset();
        Require(recovery.Observe(t0, true, true, true, true, true) is null,
            "正常竞技场状态不得恢复");
        Require(recovery.Observe(t0.AddMinutes(20), true, true, true, true, true) is null,
            "长时间正常对局不得因为时长恢复");
        recovery.Reset();
        Require(recovery.Observe(t0, true, true, true, false, true) is null,
            "竞技场运行快照刚变旧时不得立即恢复");
        Require(recovery.Observe(t0.AddSeconds(16), true, true, true, false, true)?.Contains("运行状态") == true,
            "竞技场运行快照持续异常后应恢复");

        Require(ArenaTicketPolicy.Decide(1, 1, false, -1, 150, true, true, 0, 60, true) == ArenaTicketGateResult.Ready,
            "门票足够时不应进入购票流程");
        Require(ArenaTicketPolicy.Decide(0, 1, true, 500, 150, true, true, 0, 60, true) == ArenaTicketGateResult.StopTicketShortage,
            "门票不足停止必须先于金币购买，不允许消费金币");
        Require(ArenaTicketPolicy.Decide(1, 2, true, 500, 150, true, false, 0, 60, false) == ArenaTicketGateResult.StopTicketShortage,
            "地下竞技场票不足时应立即停止，不等待金币检查");
        Require(ArenaTicketPolicy.Decide(0, 1, false, -1, 150, true, true, 0, 60, true) == ArenaTicketGateResult.StopTicketShortage,
            "金币未知不能绕过门票停止设置");
        Require(ArenaTicketPolicy.Decide(0, 1, true, 500, 150, false, true, 0, 60, true) == ArenaTicketGateResult.AllowPurchase,
            "关闭门票不足停止且金币足够时才可购票");
        Require(ArenaTicketPolicy.Decide(0, 1, true, 100, 150, false, true, 0, 60, true) == ArenaTicketGateResult.StopGoldShortage,
            "金币不足且启用停止策略时必须停止");
        Require(ArenaTicketPolicy.Decide(0, 1, true, 500, 150, false, true, 0, 60, false) == ArenaTicketGateResult.WaitForResourceCheck,
            "资源检查未到时间时应等待而非重复操作");
        Require(ArenaTicketPolicy.Decide(0, 1, true, 0, 150, false, false, 60, 60, true) == ArenaTicketGateResult.AllowPurchase,
            "关闭两个资源停止门禁时应允许原生流程继续");
        LifecycleTests.Run();
        SessionStateTests.Run();
        ModeContextTests.Run();
        StatsPersistenceTests.Run();
        RunIdentityRecoveryTests.Run();
        EntryReceiptTests.Run();
        GameIdentityTests.Run();
        GameLimitStopTests.Run();
        BudgetRuntimeTests.Run();
        DisabledRuntimeTests.Run();
        Require(ArenaDraftPacing.IsHuman("拟人"), "拟人节奏未识别");
        Require(ArenaDraftPacing.IsHuman(""), "空白节奏应按拟人处理");
        Require(!ArenaDraftPacing.IsHuman("尽快"), "尽快节奏误判为拟人");
        var random = new Random(7);
        for (var i = 0; i < 32; i++)
        {
            var human = ArenaDraftPacing.NextDelay(true, random);
            Require(human >= ArenaDraftPacing.HumanMinimumSeconds && human <= ArenaDraftPacing.HumanMaximumSeconds,
                "拟人选牌等待不在配置范围");
            var fast = ArenaDraftPacing.NextDelay(false, random);
            Require(fast >= ArenaDraftPacing.FastMinimumSeconds && fast <= ArenaDraftPacing.FastMaximumSeconds,
                "尽快选牌等待不在快速范围");
        }
        Require(!ArenaDraftPacing.IsReady(1.0f, 1.1f), "未到拟人等待时间却允许动作");
        Require(ArenaDraftPacing.IsReady(1.1f, 1.1f), "到达拟人等待时间仍阻止动作");

        Require(!ArenaRedraftPolicy.IsSlotReady(0, 0), "地下重选元数据未到齐时不能操作槽位");
        Require(ArenaRedraftPolicy.IsSlotReady(0, 3), "地下重选首个有效槽位未放行");
        Require(ArenaRedraftPolicy.IsSlotReady(3, 3), "地下重选最后一个有效槽位未放行");
        Require(!ArenaRedraftPolicy.IsSlotReady(4, 3), "地下重选超过最大槽位仍被放行");
        foreach (var state in new[] { "MIDRUN_REDRAFT_PENDING", "REDRAFTING" })
        {
            foreach (var page in new[] { "Normal_Landing", "Underground_Landing", "Normal_Ready", "Underground_Ready" })
                Require(ArenaRedraftPolicy.ShouldOpenPage(state, page), "服务器已有选项仍必须从原生落地/套牌页进入重选：" + state + "/" + page);
            foreach (var page in new[] { "Normal_Draft", "Underground_Draft", "Normal_Redraft", "Underground_Redraft", "Underground_Rewards", "None", null })
                Require(!ArenaRedraftPolicy.ShouldOpenPage(state, page), "不能重复打开已进入的选牌页面或未知页面：" + state + "/" + page);
        }
        foreach (var state in new[] { "MIDRUN", "DRAFTING", "NO_RUN", "REWARDS", null })
            Require(!ArenaRedraftPolicy.ShouldOpenPage(state, "Underground_Landing"), "不能在非重选状态调用重选入口：" + state);

        DeckEditTests.Run();
        Console.WriteLine("PASS: Arena stop boundary policy");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
