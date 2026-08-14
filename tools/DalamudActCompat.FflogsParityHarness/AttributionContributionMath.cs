namespace DalamudActCompat.FflogsParityHarness;

internal static class AttributionContributionMath
{
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
        if (providerBuff.IsSelfSourced ||
            providerBuff.Definition.Dimension == OffensiveBuffDimension.PercentageDamage)
        {
            return 0;
        }
        if (productionInputs && !providerBuff.Definition.CoveredByProduction)
        {
            return 0;
        }

        var applicableRates = state.RateBuffs
            .Where(static buff => !buff.IsSelfSourced)
            .Where(buff => !productionInputs || buff.Definition.CoveredByProduction)
            .ToArray();
        var criticalIncrease = applicableRates.Sum(static buff => buff.Definition.CriticalRateIncrease);
        var directIncrease = applicableRates.Sum(static buff => buff.Definition.DirectHitRateIncrease);
        var providerCritical = providerBuff.Definition.CriticalRateIncrease;
        var providerDirect = providerBuff.Definition.DirectHitRateIncrease;
        var selfCritical = productionInputs
            ? 0
            : state.SelfCriticalRateIncrease;
        var selfDirect = productionInputs
            ? 0
            : state.SelfDirectRateIncrease;
        var percentageMultiplier = ResolvePercentageMultiplier(state, productionInputs);
        var damage = item.Amount / Math.Max(1, percentageMultiplier);
        if (item.IsPeriodic)
        {
            return CalculateDot(
                damage,
                unbuffedCriticalChance,
                unbuffedDirectChance,
                criticalIncrease,
                directIncrease,
                providerCritical,
                providerDirect);
        }

        var contribution = GuaranteedHitCandidateMath.Calculate(
            candidate,
            new GuaranteedHitCandidateInput(
                damage,
                item.Critical,
                item.DirectHit,
                unbuffedCriticalChance,
                unbuffedDirectChance,
                criticalIncrease,
                directIncrease,
                providerCritical,
                providerDirect,
                guaranteedDimensions,
                selfCritical,
                selfDirect));
        return contribution.Critical + contribution.Direct;
    }

    public static double CalculatePercentageContribution(
        NormalizedFflogsEvent item,
        MatrixEventAttributionState state,
        MatrixBuffExposureEntry providerBuff,
        bool productionInputs)
    {
        if (providerBuff.IsSelfSourced ||
            providerBuff.Definition.Dimension != OffensiveBuffDimension.PercentageDamage ||
            providerBuff.DamageMultiplier <= 1 ||
            ResolvePercentageMultiplier(state, productionInputs) <= 1)
        {
            return 0;
        }
        var percentageMultiplier = ResolvePercentageMultiplier(state, productionInputs);
        var damageWithoutPercentageBuffs = item.Amount / percentageMultiplier;
        var lostDamage = item.Amount - damageWithoutPercentageBuffs;
        return lostDamage * Math.Log(providerBuff.DamageMultiplier) /
               Math.Log(percentageMultiplier);
    }

    private static double ResolvePercentageMultiplier(
        MatrixEventAttributionState state,
        bool productionInputs)
        => state.Buffs
            .Where(static buff => !buff.IsSelfSourced)
            .Where(static buff => buff.Definition.Dimension == OffensiveBuffDimension.PercentageDamage)
            .Where(static buff => buff.DamageMultiplier > 1)
            .Where(buff => !productionInputs || buff.Definition.CoveredByProduction)
            .Aggregate(1d, static (current, buff) => current * buff.DamageMultiplier);

    private static double CalculateDot(
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
            return 0;
        }
        var criticalPortion =
            ((buffedCritical * noDirect * criticalMultiplier) +
             (Math.Log(criticalMultiplier) / Math.Log(combined) *
              buffedCritical * buffedDirect * combined)) * damage / totalMultiplier;
        var directPortion =
            ((buffedDirect * noCritical * directMultiplier) +
             (Math.Log(directMultiplier) / Math.Log(combined) *
              buffedCritical * buffedDirect * combined)) * damage / totalMultiplier;
        return (providerCritical > 0 ? criticalPortion * providerCritical / buffedCritical : 0) +
               (providerDirect > 0 ? directPortion * providerDirect / buffedDirect : 0);
    }
}
