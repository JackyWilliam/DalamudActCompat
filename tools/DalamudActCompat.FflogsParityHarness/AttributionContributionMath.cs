namespace DalamudActCompat.FflogsParityHarness;

internal enum PercentageCalculationMode
{
    ProductionBeforeFix,
    CurrentProduction,
    PublishedMathLegacyMetadata,
    AuthoritativeMetadata,
    AuthoritativeAllActiveDenominator,
    AuthoritativeSelfStrippedBasis,
}

internal static class AttributionContributionMath
{
    public static (double Critical, double Direct) CalculateRateContributionParts(
        string candidate,
        NormalizedFflogsEvent item,
        MatrixEventAttributionState state,
        MatrixBuffExposureEntry providerBuff,
        double unbuffedCriticalChance,
        double unbuffedDirectChance,
        ProbeGuaranteedDimensions guaranteedDimensions,
        bool productionInputs)
    {
        if (providerBuff.IsSelfSourced ||
            providerBuff.Definition.Dimension == OffensiveBuffDimension.PercentageDamage ||
            productionInputs && !providerBuff.Definition.CoveredByProduction)
        {
            return (0, 0);
        }

        var applicableRates = state.RateBuffs
            .Where(static buff => !buff.IsSelfSourced)
            .Where(buff => !productionInputs || buff.Definition.CoveredByProduction)
            .ToArray();
        var criticalIncrease = applicableRates.Sum(static buff => buff.Definition.CriticalRateIncrease);
        var directIncrease = applicableRates.Sum(static buff => buff.Definition.DirectHitRateIncrease);
        var selfCritical = productionInputs ? 0 : state.SelfCriticalRateIncrease;
        var selfDirect = productionInputs ? 0 : state.SelfDirectRateIncrease;
        var percentageMultiplier = ResolvePercentageMultiplier(state, productionInputs);
        var damage = item.Amount / Math.Max(1, percentageMultiplier);
        if (item.IsPeriodic)
        {
            return CalculateDotParts(
                damage,
                unbuffedCriticalChance,
                unbuffedDirectChance,
                criticalIncrease,
                directIncrease,
                providerBuff.Definition.CriticalRateIncrease,
                providerBuff.Definition.DirectHitRateIncrease);
        }

        return GuaranteedHitCandidateMath.Calculate(
            candidate,
            new GuaranteedHitCandidateInput(
                damage,
                item.Critical,
                item.DirectHit,
                unbuffedCriticalChance,
                unbuffedDirectChance,
                criticalIncrease,
                directIncrease,
                providerBuff.Definition.CriticalRateIncrease,
                providerBuff.Definition.DirectHitRateIncrease,
                guaranteedDimensions,
                selfCritical,
                selfDirect));
    }

    public static double CalculateRateContribution(
        string candidate,
        NormalizedFflogsEvent item,
        MatrixEventAttributionState state,
        MatrixBuffExposureEntry providerBuff,
        double unbuffedCriticalChance,
        double unbuffedDirectChance,
        ProbeGuaranteedDimensions guaranteedDimensions,
        bool productionInputs)
    {
        var contribution = CalculateRateContributionParts(
            candidate,
            item,
            state,
            providerBuff,
            unbuffedCriticalChance,
            unbuffedDirectChance,
            guaranteedDimensions,
            productionInputs);
        return contribution.Critical + contribution.Direct;
    }

    public static double CalculatePercentageContribution(
        NormalizedFflogsEvent item,
        MatrixEventAttributionState state,
        MatrixBuffExposureEntry providerBuff,
        bool productionInputs)
        => CalculatePercentageContribution(
            item,
            state,
            providerBuff,
            productionInputs
                ? PercentageCalculationMode.CurrentProduction
                : PercentageCalculationMode.AuthoritativeMetadata);

    public static double CalculatePercentageContribution(
        NormalizedFflogsEvent item,
        MatrixEventAttributionState state,
        MatrixBuffExposureEntry providerBuff,
        PercentageCalculationMode mode)
    {
        if (providerBuff.IsSelfSourced ||
            providerBuff.Definition.Dimension != OffensiveBuffDimension.PercentageDamage ||
            ResolveProviderMultiplier(providerBuff, mode) <= 1 ||
            ResolvePercentageMultiplier(state, mode) <= 1)
        {
            return 0;
        }
        var percentageMultiplier = ResolvePercentageMultiplier(state, mode);
        var selfMultiplier = mode == PercentageCalculationMode.AuthoritativeSelfStrippedBasis
            ? ResolveSelfPercentageMultiplier(state)
            : 1;
        var damageBasis = item.Amount / selfMultiplier;
        var damageWithoutPercentageBuffs = damageBasis / percentageMultiplier;
        var lostDamage = damageBasis - damageWithoutPercentageBuffs;
        var providerMultiplier = ResolveProviderMultiplier(providerBuff, mode);
        return lostDamage * Math.Log(providerMultiplier) /
               Math.Log(percentageMultiplier);
    }

    private static double ResolvePercentageMultiplier(
        MatrixEventAttributionState state,
        bool productionInputs)
        => ResolvePercentageMultiplier(
            state,
            productionInputs
                ? PercentageCalculationMode.CurrentProduction
                : PercentageCalculationMode.AuthoritativeMetadata);

    private static double ResolvePercentageMultiplier(
        MatrixEventAttributionState state,
        PercentageCalculationMode mode)
        => state.Buffs
            .Where(buff => mode == PercentageCalculationMode.AuthoritativeAllActiveDenominator ||
                           !buff.IsSelfSourced)
            .Where(static buff => buff.Definition.Dimension == OffensiveBuffDimension.PercentageDamage)
            .Where(buff => ResolveProviderMultiplier(buff, mode) > 1)
            .Where(buff => mode switch
            {
                PercentageCalculationMode.CurrentProduction => buff.Definition.CoveredByProduction,
                // This historical mode keeps the original cache replay measurable after the
                // proven Mage's Ballad coverage fix without mutating any FFLogs reference.
                PercentageCalculationMode.ProductionBeforeFix =>
                    buff.Definition.CoveredByProduction && buff.Definition.StatusId != 2217,
                _ => true,
            })
            .Aggregate(1d, (current, buff) => current * ResolveProviderMultiplier(buff, mode));

    private static double ResolveProviderMultiplier(
        MatrixBuffExposureEntry buff,
        PercentageCalculationMode mode)
        => mode is PercentageCalculationMode.CurrentProduction or
            PercentageCalculationMode.AuthoritativeMetadata or
            PercentageCalculationMode.AuthoritativeAllActiveDenominator or
            PercentageCalculationMode.AuthoritativeSelfStrippedBasis
            ? buff.DamageMultiplier
            : buff.LegacyDamageMultiplier;

    private static double ResolveSelfPercentageMultiplier(MatrixEventAttributionState state)
        => state.Buffs
            .Where(static buff => buff.IsSelfSourced &&
                                  buff.Definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                                  buff.DamageMultiplier > 1)
            .Aggregate(1d, static (current, buff) => current * buff.DamageMultiplier);

    private static (double Critical, double Direct) CalculateDotParts(
        double damage,
        double unbuffedCriticalChance,
        double unbuffedDirectChance,
        double criticalIncrease,
        double directIncrease,
        double providerCritical,
        double providerDirect)
    {
        var buffedCritical = Math.Clamp(unbuffedCriticalChance + criticalIncrease, 0.01, 1);
        var buffedDirect = Math.Clamp(unbuffedDirectChance + directIncrease, 0.01, 1);
        var criticalMultiplier = 1.35 + unbuffedCriticalChance;
        const double directMultiplier = 1.25;
        var combined = criticalMultiplier * directMultiplier;
        var noCritical = 1 - buffedCritical;
        var noDirect = 1 - buffedDirect;
        var totalMultiplier =
            (noCritical * noDirect) +
            (buffedCritical * noDirect * criticalMultiplier) +
            (noCritical * buffedDirect * directMultiplier) +
            (buffedCritical * buffedDirect * combined);
        if (totalMultiplier <= 0)
        {
            return (0, 0);
        }
        var criticalPortion =
            ((buffedCritical * noDirect * criticalMultiplier) +
             (Math.Log(criticalMultiplier) / Math.Log(combined) *
              buffedCritical * buffedDirect * combined)) * damage / totalMultiplier;
        var directPortion =
            ((buffedDirect * noCritical * directMultiplier) +
             (Math.Log(directMultiplier) / Math.Log(combined) *
              buffedCritical * buffedDirect * combined)) * damage / totalMultiplier;
        return (
            providerCritical > 0 ? criticalPortion * providerCritical / buffedCritical : 0,
            providerDirect > 0 ? directPortion * providerDirect / buffedDirect : 0);
    }
}
