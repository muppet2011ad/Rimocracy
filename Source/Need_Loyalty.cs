﻿using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace Rimocracy
{
    public class Need_Loyalty : Need_Seeker
    {
        const int TicksPerInterval = 150;
        const int IntervalsBetweenPersistentUpdates = 100;

        public const float MoodWeight = 1;
        public const float OpinionOfLeaderWeight = 1;
        public const float LoyaltyResetOnLeaderChange = 0.25f;
        public const float ToleratedDecisionLoyaltyFactor = 0.75f;
        public const float ProtestLevelBase = 0.15f;
        public const float DefaultLevel = 0.50f;

        static List<float> threshPercentsCommon = new List<float>(2)
        {
            ProtestLevelBase,
            DefaultLevel
        };

        float persistentOffset;
        ProtestDef protest;

        public bool IsProtesting => protest != null;

        protected override bool IsFrozen => base.IsFrozen || !Utility.PoliticsEnabled || !pawn.IsCitizen();

        public override bool ShowOnNeedList => base.ShowOnNeedList && Settings.LoyaltyEnabled && Utility.PoliticsEnabled && pawn.IsCitizen();

        public static float ProtestLevel
        {
            get
            {
                int pop = Utility.CitizensCount;
                if (pop <= 0)
                    return ProtestLevelBase;
                return ProtestLevelBase + (DefaultLevel - ProtestLevelBase) * Utility.RimocracyComp.Protesters.Count / pop;
            }
        }

        public override float CurInstantLevel =>
            (pawn.needs.mood.CurLevelPercentage * MoodWeight + (pawn.GetOpinionOf(Utility.RimocracyComp.Leader) + 100) / 200 * OpinionOfLeaderWeight) / (MoodWeight + OpinionOfLeaderWeight) + persistentOffset;

        public float StartProtestMTB => GenDate.HoursPerDay * 5 * (1 + CurLevel / ProtestLevel) * (1 + Utility.RimocracyComp.Governance);

        public Need_Loyalty(Pawn pawn)
            : base(pawn) =>
            threshPercents = threshPercentsCommon;

        public static void RecalculateThreshPercents() => threshPercentsCommon[0] = ProtestLevel;

        public void RecalculatePersistentEffects()
        {
            Utility.Log($"RecalculatePersistentEffects for {pawn}");
            persistentOffset = 0;
            foreach (DecisionDef decision in Utility.RimocracyComp.Decisions.Select(decision => decision.def))
            {
                PawnDecisionOpinion opinion = new PawnDecisionOpinion(pawn, decision.considerations, Utility.RimocracyComp.Leader);
                Utility.Log($"{pawn}'s support for {decision.defName}: {opinion.support.ToStringWithSign()}");
                switch (opinion.Vote)
                {
                    case DecisionVote.Yea:
                        persistentOffset += decision.loyaltyEffect;
                        break;

                    case DecisionVote.Nay:
                        persistentOffset -= decision.loyaltyEffect;
                        break;

                    case DecisionVote.Tolerate:
                        persistentOffset -= decision.loyaltyEffect * ToleratedDecisionLoyaltyFactor;
                        break;
                }
            }
            if (persistentOffset != 0)
                Utility.Log($"Persistent offset due to decisions: {persistentOffset.ToStringPercent()}");
        }

        static ProtestDef RandomProtestDefFor(Pawn pawn) =>
            DefDatabase<ProtestDef>.AllDefs.Where(def => def.AppliesTo(pawn)).RandomElementByWeightWithFallback(def => def.weight.GetValue(pawn));

        public void StartProtest()
        {
            if (pawn.InMentalState || QuestUtility.AnyQuestDisablesRandomMoodCausedMentalBreaksFor(pawn))
                return;
            Utility.Log($"Trying to start a protest for {pawn}.");

            if (Settings.DebugLogging)
                foreach (ProtestDef p in DefDatabase<ProtestDef>.AllDefs.Where(def => def.AppliesTo(pawn)))
                    Utility.Log($"Possible protest: {p.defName}, weight = {p.weight.GetValue(pawn):N1}");

            ProtestDef chosenProtest = RandomProtestDefFor(pawn);
            if (chosenProtest == null)
            {
                Utility.Log($"Could not find a suitable ProtestDef for {pawn} out of {DefDatabase<ProtestDef>.DefCount.ToStringCached()} defs.", LogLevel.Warning);
                return;
            }
            Utility.Log($"{pawn} initiates {chosenProtest}.");

            if (pawn.mindState.mentalStateHandler.TryStartMentalState(chosenProtest.mentalState, transitionSilently: true))
            {
                protest = chosenProtest;
                Utility.RimocracyComp.Protesters.Add(pawn);
                if (protest.mentalState.IsExtreme)
                    Find.LetterStack.ReceiveLetter($"Political Protest", protest.DescriptionFor(pawn), LetterDefOf.NegativeEvent, new LookTargets(pawn));
                else Messages.Message(protest.DescriptionFor(pawn), new LookTargets(pawn), MessageTypeDefOf.NegativeEvent);
                RecalculateThreshPercents();
            }
        }

        public void StopProtest()
        {
            Utility.Log($"{pawn} stops protesting.");
            pawn.mindState.mentalStateHandler.Reset();
            protest = null;
            Utility.RimocracyComp.Protesters.Remove(pawn);
            RecalculateThreshPercents();
            Messages.Message($"{pawn} is no longer protesting.", pawn, MessageTypeDefOf.PositiveEvent);
        }

        public override void NeedInterval()
        {
            if (!Settings.LoyaltyEnabled)
            {
                CurLevel = DefaultLevel;
                protest = null;
                return;
            }
            base.NeedInterval();
            if (IsProtesting && !pawn.InMentalState)
                StopProtest();
            if (!IsFrozen)
            {
                if (Find.TickManager.TicksAbs / TicksPerInterval % IntervalsBetweenPersistentUpdates == Math.Abs(pawn.HashOffset() % IntervalsBetweenPersistentUpdates))
                    RecalculatePersistentEffects();
                if (IsProtesting && CurLevel > ProtestLevel)
                    StopProtest();
                else if (!IsProtesting && CurLevel < ProtestLevel && Rand.MTBEventOccurs(StartProtestMTB, GenDate.TicksPerHour, TicksPerInterval))
                    StartProtest();
            }
        }

        public override string GetTipString()
        {
            string tip = base.GetTipString();
            Pawn leader = Utility.RimocracyComp.Leader;
            float opinionOfLeader = pawn.GetOpinionOf(leader);
            tip += $"\n\nLoyalty of {pawn} is affected by {pawn.Possessive()} mood ({pawn.needs.mood.CurLevelPercentage.ToStringPercent().ColorizeByValue(pawn.needs.mood.CurLevelPercentage, 0.5f, 0.5f)}){(leader != null ? $" and opinion of {Utility.LeaderTitle} {leader.NameShortColored} ({opinionOfLeader.ToStringWithSign("0").ColorizeByValue(opinionOfLeader)})." : ".")}";
            if (persistentOffset != 0)
                tip += $" Active decisions change loyalty by {persistentOffset.ToStringWithSign("0.#%").ColorizeByValue(persistentOffset)}.";
            if (CurLevel > DefaultLevel)
            {
                int supportOffset = (int)Math.Floor(pawn.GetLoyaltySupportOffset());
                if (supportOffset > 0)
                    tip += $"\n\nLoyalty adds +{supportOffset.ToStringCached().Colorize(Color.green)} support for decisions.";
            }
            if (IsProtesting)
                tip += $"\n\n{pawn.NameShortColored} will stop protesting when {pawn.Possessive()} loyalty goes above {ProtestLevel.ToStringPercent()}.";
            else if (CurLevel < ProtestLevel)
                tip += $"\n\n<color=red>Expected to protest in {GenDate.ToStringTicksToPeriodVague((int)(StartProtestMTB * GenDate.TicksPerHour), false)}.</color>";
            else if (CurInstantLevel < ProtestLevel)
                tip += $"\n\n<color=yellow>If {pawn.Possessive()} loyalty falls below {ProtestLevel.ToStringPercent()}, {pawn.NameShortColored} may start protesting.</color>";
            return tip;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref persistentOffset, "persistentOffset");
            Scribe_Defs.Look(ref protest, "protest");
        }
    }
}