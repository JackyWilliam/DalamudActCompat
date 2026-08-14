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

    public static IReadOnlyList<GuaranteedHitCandidateDefinition> Definitions { get; } =
    [
        new(
            CurrentProduction,
            "1/current production",
            "Rc=(Mc+C(Mc-1))/Mc; Rd=(1.25+D*0.25)/1.25; L=N-N/(Rc*Rd); " +
            "split L by log(Rc)/log(Rc*Rd), log(Rd)/log(Rc*Rd), then cD/C and dD/D.",
            "Guaranteed Crit, DH, and CDH; non-guaranteed dimensions retain published regular-hit allocation."),
        new(
            ObservedHitRegular,
            "2/observed-hit regular allocation",
            "Ignore the guarantee marker for attribution: Pc=LW(N,Mc,Mc*Md_if_DH)*cD/(Cu+C); " +
            "Pd=LW(N,Md,Mc_if_Crit*Md)*dD/(Du+D).",
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
            "G=Gc(C)*Gd(D); G_without=Gc(C-cD)*Gd(D-dD); L=N-N*G_without/G; " +
            "split simultaneous dimensions by log marginal ratios.",
            "Measures the damage lost when only Dancer's rate increases are removed while every other rate buff remains."),
        new(
            SeparateDimensionBonus,
            "4/separate Crit and DH",
            "Pc=(N-N/Rc)*cD/C; Pd=(N-N/Rd)*dD/D; sum dimensions without a combined-interaction weight.",
            "Guaranteed Crit and DH are calculated independently before combination."),
        new(
            CombinedLinearWeight,
            "5/combined multiplier with linear weights",
            "L=N-N/(Rc*Rd); split L by (Rc-1)/((Rc-1)+(Rd-1)) and the analogous DH weight, " +
            "then cD/C and dD/D.",
            "Uses the combined guaranteed multiplier but tests linear rather than logarithmic component weighting."),
        new(
            GameBonusBuffedRateDenominator,
            "2/game bonus with buffed-rate denominator",
            "Use CurrentProduction's guaranteed bonus portions, but allocate Dancer by cD/(Cu+C) and dD/(Du+D).",
            "Tests whether guaranteed extra damage uses the same total buffed-rate denominator as regular hits."),
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
              input.DancerCriticalRateIncrease / input.CriticalRateIncrease
            : 0;
        var direct = ratios.Direct > 1 && input.DirectRateIncrease > 0
            ? LogWeightedBonusPortion(
                  input.DamageAfterPercentageRemoval,
                  ratios.Direct,
                  combined) *
              input.DancerDirectRateIncrease / input.DirectRateIncrease
            : 0;
        return (critical, direct);
    }

    private static (double Critical, double Direct) CalculateRegular(
        double damage,
        GuaranteedHitCandidateInput input)
    {
        var criticalMultiplier = 1.35 + input.UnbuffedCriticalChance;
        const double directMultiplier = 1.25;
        var critical = 0d;
        if (input.IsCritical && input.DancerCriticalRateIncrease > 0)
        {
            var combined = criticalMultiplier * (input.IsDirectHit ? directMultiplier : 1);
            var chance = Math.Clamp(
                input.UnbuffedCriticalChance + input.CriticalRateIncrease,
                0.01,
                1);
            critical = LogWeightedBonusPortion(damage, criticalMultiplier, combined) *
                       input.DancerCriticalRateIncrease / chance;
        }
        var direct = 0d;
        if (input.IsDirectHit && input.DancerDirectRateIncrease > 0)
        {
            var combined = (input.IsCritical ? criticalMultiplier : 1) * directMultiplier;
            var chance = Math.Clamp(
                input.UnbuffedDirectChance + input.DirectRateIncrease,
                0.01,
                1);
            direct = LogWeightedBonusPortion(damage, directMultiplier, combined) *
                     input.DancerDirectRateIncrease / chance;
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
                    input.CriticalRateIncrease - input.DancerCriticalRateIncrease),
                DirectRateIncrease = Math.Max(
                    0,
                    input.DirectRateIncrease - input.DancerDirectRateIncrease),
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
                  input.DancerCriticalRateIncrease / input.CriticalRateIncrease
                : 0,
            ratios.Direct > 1 && input.DirectRateIncrease > 0
                ? (input.DamageAfterPercentageRemoval -
                   input.DamageAfterPercentageRemoval / ratios.Direct) *
                  input.DancerDirectRateIncrease / input.DirectRateIncrease
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
                      input.DancerCriticalRateIncrease / input.CriticalRateIncrease
                    : 0,
                input.DirectRateIncrease > 0
                    ? lost * directWeight / totalWeight *
                      input.DancerDirectRateIncrease / input.DirectRateIncrease
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
                  input.DancerCriticalRateIncrease / criticalDenominator
                : 0,
            ratios.Direct > 1 && directDenominator > 0
                ? LogWeightedBonusPortion(
                      input.DamageAfterPercentageRemoval,
                      ratios.Direct,
                      combined) *
                  input.DancerDirectRateIncrease / directDenominator
                : 0);
    }

    private static double ResolveGameRatio(GuaranteedHitCandidateInput input)
    {
        var ratios = ResolveRatios(input);
        return Math.Max(1, ratios.Critical * ratios.Direct);
    }

    private static (double Critical, double Direct) ResolveRatios(
        GuaranteedHitCandidateInput input)
    {
        var criticalMultiplier = 1.35 + input.UnbuffedCriticalChance;
        var critical = (input.Dimensions & ProbeGuaranteedDimensions.Critical) != 0 &&
                       input.CriticalRateIncrease > 0
            ? (criticalMultiplier +
               input.CriticalRateIncrease * (criticalMultiplier - 1)) /
              criticalMultiplier
            : 1;
        var direct = (input.Dimensions & ProbeGuaranteedDimensions.DirectHit) != 0 &&
                     input.DirectRateIncrease > 0
            ? (1.25 + input.DirectRateIncrease * 0.25) / 1.25
            : 1;
        return (critical, direct);
    }

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
