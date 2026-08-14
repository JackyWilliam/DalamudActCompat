namespace DalamudActCompat.FflogsParityHarness;

internal static class GuaranteedHitCandidateMath
{
    public const string CurrentProduction = "CurrentProduction";
    public const string ObservedHitRegular = "ObservedHitRegular";
    public const string UnscaledObservedHit = "UnscaledObservedHit";
    public const string UnscaledRegularPlusGameBonus = "UnscaledRegularPlusGameBonus";
    public const string MarginalBuffRemoval = "MarginalBuffRemoval";
    public const string SeparateDimensionBonus = "SeparateDimensionBonus";
    public const string CombinedLinearWeight = "CombinedLinearWeight";
    public const string GameBonusBuffedRateDenominator = "GameBonusBuffedRateDenominator";
    public const string ObservedAllActiveDenominator = "ObservedHitRegular.AllActiveDenominator";
    public const string ObservedExcludeSelfEverywhere = "ObservedHitRegular.ExcludeSelfEverywhere";
    public const string ObservedExternalProvidersOnly = "ObservedHitRegular.ExternalProvidersOnly";
    public const string ObservedSelfScalingExternalDenominator = "ObservedHitRegular.SelfScalingExternalDenominator";
    public const string UnscaledAllActiveDenominator = "UnscaledObservedHit.AllActiveDenominator";
    public const string UnscaledExcludeSelfEverywhere = "UnscaledObservedHit.ExcludeSelfEverywhere";
    public const string UnscaledExternalProvidersOnly = "UnscaledObservedHit.ExternalProvidersOnly";
    public const string UnscaledSelfScalingExternalDenominator = "UnscaledObservedHit.SelfScalingExternalDenominator";
    public const string OtherExternalOverlapObservedElseUnscaled = "OtherExternalOverlap.ObservedElseUnscaled";
    public const string OtherExternalOverlapUnscaledElseObserved = "OtherExternalOverlap.UnscaledElseObserved";

    public static IReadOnlyList<GuaranteedHitCandidateDefinition> Definitions { get; } =
    [
        new(
            CurrentProduction,
            "1/current production",
            "Rc=(Mc+C(Mc-1))/Mc; Rd=(1.25+D*0.25)/1.25; L=N-N/(Rc*Rd); " +
            "split L by log(Rc)/log(Rc*Rd), log(Rd)/log(Rc*Rd), then cP/C and dP/D.",
            "Guaranteed Crit, DH, and CDH; non-guaranteed dimensions retain published regular-hit allocation."),
        new(
            ObservedHitRegular,
            "2/observed-hit regular allocation",
            "Ignore the guarantee marker for attribution: Pc=LW(N,Mc,Mc*Md_if_DH)*cP/(Cu+C); " +
            "Pd=LW(N,Md,Mc_if_Crit*Md)*dP/(Du+D).",
            "Treats a guaranteed result as the observed Crit/DH packet under FFLogs' published regular-hit path."),
        new(
            UnscaledObservedHit,
            "3/restore unscaled guaranteed baseline",
            "G=Rc*Rd for guaranteed dimensions; N0=N/G; apply the published regular observed-hit allocation to N0.",
            "Separates the game-added rate-buff scaling from the damage baseline before regular attribution."),
        new(
            UnscaledRegularPlusGameBonus,
            "3/restore baseline plus explicit game bonus",
            "N0=N/(Rc*Rd); contribution=RegularObserved(N0)+CurrentGuaranteedGameBonus(N).",
            "Attributes both the observed-hit probability share on the unscaled baseline and the explicit guaranteed scaling delta."),
        new(
            MarginalBuffRemoval,
            "2/marginal extra-damage removal",
            "G=Gc(C)*Gd(D); G_without=Gc(C-cP)*Gd(D-dP); L=N-N*G_without/G; " +
            "split simultaneous dimensions by log marginal ratios.",
            "Measures the damage lost when only the selected provider's rate increases are removed while every other rate buff remains."),
        new(
            SeparateDimensionBonus,
            "4/separate Crit and DH",
            "Pc=(N-N/Rc)*cP/C; Pd=(N-N/Rd)*dP/D; sum dimensions without a combined-interaction weight.",
            "Guaranteed Crit and DH are calculated independently before combination."),
        new(
            CombinedLinearWeight,
            "5/combined multiplier with linear weights",
            "L=N-N/(Rc*Rd); split L by (Rc-1)/((Rc-1)+(Rd-1)) and the analogous DH weight, " +
            "then cP/C and dP/D.",
            "Uses the combined guaranteed multiplier but tests linear rather than logarithmic component weighting."),
        new(
            GameBonusBuffedRateDenominator,
            "2/game bonus with buffed-rate denominator",
            "Use CurrentProduction's guaranteed bonus portions, but allocate the selected provider by cP/(Cu+C) and dP/(Du+D).",
            "Tests whether guaranteed extra damage uses the same total buffed-rate denominator as regular hits."),
        new(
            ObservedAllActiveDenominator,
            "denominator A/all active",
            "RegularObserved(N): Pc*cP/(Cu+Cext+Cself), Pd*dP/(Du+Dext+Dself).",
            "Observed damage retains every game-side rate scaling effect; every active configured rate enters the allocation denominator."),
        new(
            ObservedExcludeSelfEverywhere,
            "denominator B/exclude self",
            "Nself0=N*G(Cext,Dext)/G(Cext+Cself,Dext+Dself); " +
            "RegularObserved(Nself0): cP/(Cu+Cext), dP/(Du+Dext).",
            "Removes configured self-rate game scaling before applying an external-only attribution denominator."),
        new(
            ObservedExternalProvidersOnly,
            "denominator C/external providers",
            "RegularObserved(N): Pc*cP/(Cu+Cext), Pd*dP/(Du+Dext).",
            "Only external provider rates enter attribution; any self-rate scaling remains embedded in observed damage N."),
        new(
            ObservedSelfScalingExternalDenominator,
            "denominator D/self scaling, external denominator",
            "RegularObserved(N): self rate remains in observed game damage, while shares use cP/(Cu+Cext), dP/(Du+Dext).",
            "For the observed-hit family this is mathematically identical to Variant C; the duplicate name records the policy equivalence explicitly."),
        new(
            UnscaledAllActiveDenominator,
            "denominator A/all active",
            "N0=N/G(Cext+Cself,Dext+Dself); RegularObserved(N0) with denominators Cu+Cext+Cself and Du+Dext+Dself.",
            "All configured rate effects enter both guaranteed game-scaling restoration and attribution denominators."),
        new(
            UnscaledExcludeSelfEverywhere,
            "denominator B/exclude self",
            "N0=N/G(Cext,Dext); RegularObserved(N0) with denominators Cu+Cext and Du+Dext.",
            "Self rates are absent from both explicit game-scaling restoration and allocation inputs."),
        new(
            UnscaledExternalProvidersOnly,
            "denominator C/external providers",
            "N0=N/G(Cext,Dext); RegularObserved(N0) with denominators Cu+Cext and Du+Dext.",
            "Equivalent to Variant B for this family because both explicit inputs are the external-provider set."),
        new(
            UnscaledSelfScalingExternalDenominator,
            "denominator D/self scaling, external denominator",
            "N0=N/G(Cext+Cself,Dext+Dself); RegularObserved(N0) with denominators Cu+Cext and Du+Dext.",
            "Self rates affect guaranteed game scaling restoration but do not dilute external-provider attribution."),
        new(
            OtherExternalOverlapObservedElseUnscaled,
            "boundary diagnostic/other external overlap",
            "Use ObservedHitRegular when a guaranteed dimension has another external rate provider active; otherwise use UnscaledObservedHit.",
            "Parameter-free, action/job-independent diagnostic for the observed-vs-unscaled boundary; not an asserted FFLogs rule."),
        new(
            OtherExternalOverlapUnscaledElseObserved,
            "boundary falsification/reversed overlap",
            "Use UnscaledObservedHit when another external rate provider overlaps; otherwise use ObservedHitRegular.",
            "Reversed condition used to test whether overlap direction, rather than merely cohort composition, carries the signal."),
    ];

    public static (double Critical, double Direct) Calculate(
        string candidate,
        GuaranteedHitCandidateInput input)
        => candidate switch
        {
            CurrentProduction => CalculateCurrent(input),
            ObservedHitRegular => CalculateRegular(input.DamageAfterPercentageRemoval, input),
            UnscaledObservedHit => CalculateRegular(
                input.DamageAfterPercentageRemoval / ResolveGameRatio(input),
                input),
            UnscaledRegularPlusGameBonus => Add(
                CalculateRegular(input.DamageAfterPercentageRemoval / ResolveGameRatio(input), input),
                CalculateCurrentGuaranteedOnly(input)),
            MarginalBuffRemoval => AddRegularNonGuaranteed(
                CalculateMarginalRemoval(input),
                input),
            SeparateDimensionBonus => AddRegularNonGuaranteed(
                CalculateSeparateDimensions(input),
                input),
            CombinedLinearWeight => AddRegularNonGuaranteed(
                CalculateCombinedLinear(input),
                input),
            GameBonusBuffedRateDenominator => AddRegularNonGuaranteed(
                CalculateBuffedRateDenominator(input),
                input),
            ObservedAllActiveDenominator => CalculateRegular(
                input.DamageAfterPercentageRemoval,
                input,
                input.CriticalRateIncrease + input.SelfCriticalRateIncrease,
                input.DirectRateIncrease + input.SelfDirectRateIncrease),
            ObservedExcludeSelfEverywhere => CalculateRegular(
                ResolveDamageWithoutSelfGameScaling(input),
                input,
                input.CriticalRateIncrease,
                input.DirectRateIncrease),
            ObservedExternalProvidersOnly or ObservedSelfScalingExternalDenominator =>
                CalculateRegular(input.DamageAfterPercentageRemoval, input),
            UnscaledAllActiveDenominator => CalculateRegular(
                input.DamageAfterPercentageRemoval / ResolveAllGameRatio(input),
                input,
                input.CriticalRateIncrease + input.SelfCriticalRateIncrease,
                input.DirectRateIncrease + input.SelfDirectRateIncrease),
            UnscaledExcludeSelfEverywhere or UnscaledExternalProvidersOnly => CalculateRegular(
                input.DamageAfterPercentageRemoval / ResolveGameRatio(input),
                input),
            UnscaledSelfScalingExternalDenominator => CalculateRegular(
                input.DamageAfterPercentageRemoval / ResolveAllGameRatio(input),
                input),
            OtherExternalOverlapObservedElseUnscaled => HasOtherExternalRateOverlap(input)
                ? CalculateRegular(input.DamageAfterPercentageRemoval, input)
                : CalculateRegular(input.DamageAfterPercentageRemoval / ResolveGameRatio(input), input),
            OtherExternalOverlapUnscaledElseObserved => HasOtherExternalRateOverlap(input)
                ? CalculateRegular(input.DamageAfterPercentageRemoval / ResolveGameRatio(input), input)
                : CalculateRegular(input.DamageAfterPercentageRemoval, input),
            _ => throw new ArgumentOutOfRangeException(nameof(candidate), candidate, "Unknown candidate."),
        };

    private static (double Critical, double Direct) CalculateCurrent(
        GuaranteedHitCandidateInput input)
        => AddRegularNonGuaranteed(CalculateCurrentGuaranteedOnly(input), input);

    private static (double Critical, double Direct) CalculateCurrentGuaranteedOnly(
        GuaranteedHitCandidateInput input)
    {
        var ratios = ResolveRatios(input);
        var combined = ratios.Critical * ratios.Direct;
        var critical = ratios.Critical > 1 && input.CriticalRateIncrease > 0
            ? LogWeightedBonusPortion(
                  input.DamageAfterPercentageRemoval,
                  ratios.Critical,
                  combined) *
              input.ProviderCriticalRateIncrease / input.CriticalRateIncrease
            : 0;
        var direct = ratios.Direct > 1 && input.DirectRateIncrease > 0
            ? LogWeightedBonusPortion(
                  input.DamageAfterPercentageRemoval,
                  ratios.Direct,
                  combined) *
              input.ProviderDirectRateIncrease / input.DirectRateIncrease
            : 0;
        return (critical, direct);
    }

    private static (double Critical, double Direct) CalculateRegular(
        double damage,
        GuaranteedHitCandidateInput input)
        => CalculateRegular(
            damage,
            input,
            input.CriticalRateIncrease,
            input.DirectRateIncrease);

    private static (double Critical, double Direct) CalculateRegular(
        double damage,
        GuaranteedHitCandidateInput input,
        double criticalDenominatorIncrease,
        double directDenominatorIncrease)
    {
        var criticalMultiplier = 1.35 + input.UnbuffedCriticalChance;
        const double directMultiplier = 1.25;
        var critical = 0d;
        if (input.IsCritical && input.ProviderCriticalRateIncrease > 0)
        {
            var combined = criticalMultiplier * (input.IsDirectHit ? directMultiplier : 1);
            var chance = Math.Clamp(
                input.UnbuffedCriticalChance + criticalDenominatorIncrease,
                0.01,
                1);
            critical = LogWeightedBonusPortion(damage, criticalMultiplier, combined) *
                       input.ProviderCriticalRateIncrease / chance;
        }
        var direct = 0d;
        if (input.IsDirectHit && input.ProviderDirectRateIncrease > 0)
        {
            var combined = (input.IsCritical ? criticalMultiplier : 1) * directMultiplier;
            var chance = Math.Clamp(
                input.UnbuffedDirectChance + directDenominatorIncrease,
                0.01,
                1);
            direct = LogWeightedBonusPortion(damage, directMultiplier, combined) *
                     input.ProviderDirectRateIncrease / chance;
        }
        return (critical, direct);
    }

    private static (double Critical, double Direct) AddRegularNonGuaranteed(
        (double Critical, double Direct) guaranteed,
        GuaranteedHitCandidateInput input)
    {
        var regular = CalculateRegular(input.DamageAfterPercentageRemoval, input);
        return (
            guaranteed.Critical +
            ((input.Dimensions & ProbeGuaranteedDimensions.Critical) == 0 ? regular.Critical : 0),
            guaranteed.Direct +
            ((input.Dimensions & ProbeGuaranteedDimensions.DirectHit) == 0 ? regular.Direct : 0));
    }

    private static (double Critical, double Direct) CalculateMarginalRemoval(
        GuaranteedHitCandidateInput input)
    {
        var all = ResolveRatios(input);
        var without = ResolveRatios(
            input with
            {
                CriticalRateIncrease = Math.Max(
                    0,
                    input.CriticalRateIncrease - input.ProviderCriticalRateIncrease),
                DirectRateIncrease = Math.Max(
                    0,
                    input.DirectRateIncrease - input.ProviderDirectRateIncrease),
            });
        var criticalMarginal = all.Critical / Math.Max(1, without.Critical);
        var directMarginal = all.Direct / Math.Max(1, without.Direct);
        var combined = criticalMarginal * directMarginal;
        return (
            criticalMarginal > 1
                ? LogWeightedBonusPortion(input.DamageAfterPercentageRemoval, criticalMarginal, combined)
                : 0,
            directMarginal > 1
                ? LogWeightedBonusPortion(input.DamageAfterPercentageRemoval, directMarginal, combined)
                : 0);
    }

    private static (double Critical, double Direct) CalculateSeparateDimensions(
        GuaranteedHitCandidateInput input)
    {
        var ratios = ResolveRatios(input);
        return (
            ratios.Critical > 1 && input.CriticalRateIncrease > 0
                ? (input.DamageAfterPercentageRemoval -
                   input.DamageAfterPercentageRemoval / ratios.Critical) *
                  input.ProviderCriticalRateIncrease / input.CriticalRateIncrease
                : 0,
            ratios.Direct > 1 && input.DirectRateIncrease > 0
                ? (input.DamageAfterPercentageRemoval -
                   input.DamageAfterPercentageRemoval / ratios.Direct) *
                  input.ProviderDirectRateIncrease / input.DirectRateIncrease
                : 0);
    }

    private static (double Critical, double Direct) CalculateCombinedLinear(
        GuaranteedHitCandidateInput input)
    {
        var ratios = ResolveRatios(input);
        var combined = ratios.Critical * ratios.Direct;
        if (combined <= 1)
        {
            return (0, 0);
        }
        var lost = input.DamageAfterPercentageRemoval -
                   input.DamageAfterPercentageRemoval / combined;
        var criticalWeight = Math.Max(0, ratios.Critical - 1);
        var directWeight = Math.Max(0, ratios.Direct - 1);
        var totalWeight = criticalWeight + directWeight;
        return totalWeight <= 0
            ? (0, 0)
            : (
                input.CriticalRateIncrease > 0
                    ? lost * criticalWeight / totalWeight *
                      input.ProviderCriticalRateIncrease / input.CriticalRateIncrease
                    : 0,
                input.DirectRateIncrease > 0
                    ? lost * directWeight / totalWeight *
                      input.ProviderDirectRateIncrease / input.DirectRateIncrease
                    : 0);
    }

    private static (double Critical, double Direct) CalculateBuffedRateDenominator(
        GuaranteedHitCandidateInput input)
    {
        var ratios = ResolveRatios(input);
        var combined = ratios.Critical * ratios.Direct;
        var criticalDenominator = input.UnbuffedCriticalChance + input.CriticalRateIncrease;
        var directDenominator = input.UnbuffedDirectChance + input.DirectRateIncrease;
        return (
            ratios.Critical > 1 && criticalDenominator > 0
                ? LogWeightedBonusPortion(
                      input.DamageAfterPercentageRemoval,
                      ratios.Critical,
                      combined) *
                  input.ProviderCriticalRateIncrease / criticalDenominator
                : 0,
            ratios.Direct > 1 && directDenominator > 0
                ? LogWeightedBonusPortion(
                      input.DamageAfterPercentageRemoval,
                      ratios.Direct,
                      combined) *
                  input.ProviderDirectRateIncrease / directDenominator
                : 0);
    }

    private static double ResolveGameRatio(GuaranteedHitCandidateInput input)
        => ResolveGameRatio(
            input,
            input.CriticalRateIncrease,
            input.DirectRateIncrease);

    private static double ResolveAllGameRatio(GuaranteedHitCandidateInput input)
        => ResolveGameRatio(
            input,
            input.CriticalRateIncrease + input.SelfCriticalRateIncrease,
            input.DirectRateIncrease + input.SelfDirectRateIncrease);

    private static double ResolveDamageWithoutSelfGameScaling(GuaranteedHitCandidateInput input)
    {
        var allRateRatio = ResolveAllGameRatio(input);
        // Guaranteed rate scaling is linear in the total active rate, so the self-only
        // ratio is not a separable factor when external rates overlap.
        return input.DamageAfterPercentageRemoval * ResolveGameRatio(input) / allRateRatio;
    }

    private static double ResolveGameRatio(
        GuaranteedHitCandidateInput input,
        double criticalRateIncrease,
        double directRateIncrease)
    {
        var ratios = ResolveRatios(input, criticalRateIncrease, directRateIncrease);
        return Math.Max(1, ratios.Critical * ratios.Direct);
    }

    private static (double Critical, double Direct) ResolveRatios(
        GuaranteedHitCandidateInput input)
        => ResolveRatios(input, input.CriticalRateIncrease, input.DirectRateIncrease);

    private static (double Critical, double Direct) ResolveRatios(
        GuaranteedHitCandidateInput input,
        double criticalRateIncrease,
        double directRateIncrease)
    {
        var criticalMultiplier = 1.35 + input.UnbuffedCriticalChance;
        var critical = (input.Dimensions & ProbeGuaranteedDimensions.Critical) != 0 &&
                        criticalRateIncrease > 0
            ? (criticalMultiplier +
               criticalRateIncrease * (criticalMultiplier - 1)) /
              criticalMultiplier
            : 1;
        var direct = (input.Dimensions & ProbeGuaranteedDimensions.DirectHit) != 0 &&
                      directRateIncrease > 0
            ? (1.25 + directRateIncrease * 0.25) / 1.25
            : 1;
        return (critical, direct);
    }

    private static bool HasOtherExternalRateOverlap(GuaranteedHitCandidateInput input)
        => ((input.Dimensions & ProbeGuaranteedDimensions.Critical) != 0 &&
            input.CriticalRateIncrease > input.ProviderCriticalRateIncrease) ||
           ((input.Dimensions & ProbeGuaranteedDimensions.DirectHit) != 0 &&
            input.DirectRateIncrease > input.ProviderDirectRateIncrease);

    private static (double Critical, double Direct) Add(
        (double Critical, double Direct) left,
        (double Critical, double Direct) right)
        => (left.Critical + right.Critical, left.Direct + right.Direct);

    private static double LogWeightedBonusPortion(
        double damage,
        double componentMultiplier,
        double combinedMultiplier)
    {
        if (damage <= 0 || componentMultiplier <= 1 || combinedMultiplier <= 1)
        {
            return 0;
        }
        var bonusDamage = damage - damage / combinedMultiplier;
        return Math.Abs(componentMultiplier - combinedMultiplier) < 0.000001
            ? bonusDamage
            : bonusDamage * Math.Log(componentMultiplier) / Math.Log(combinedMultiplier);
    }
}
