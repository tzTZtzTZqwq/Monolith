using System.Numerics;
using Content.Shared._NSV.Bluespace.Sectors;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NSV.Bluespace.Sectors.Generators;

public sealed class NsvBluespaceAsteroidFieldGenerator
{
    private const int MaximumAsteroids = 10_000;

    public bool TryGenerate(
        IEntityManager entityManager,
        IPrototypeManager prototypes,
        EntityUid mapUid,
        NsvBluespaceSectorInstanceComponent instance,
        NsvBluespaceAsteroidFieldGeneratorDefinition definition,
        NsvBluespaceSectorPlacement placement)
    {
        if (!TryGetTargetCount(prototypes, definition, placement, out var targetCount, out var totalWeight))
            return false;

        var random = new Random(placement.ContentSeed);
        var positions = new List<Vector2>(targetCount);
        var minimumSpacingSquared = definition.MinimumSpacing * definition.MinimumSpacing;

        for (var spawnIndex = 0; spawnIndex < targetCount; spawnIndex++)
        {
            for (var attempt = 0; attempt < definition.MaxSpawnAttempts; attempt++)
            {
                var position = SamplePosition(random, definition.Radius);
                if (!CanPlace(position, positions, minimumSpacingSquared))
                    continue;

                var prototype = PickAsteroidType(random, definition.AsteroidTypes, totalWeight);
                var asteroid = entityManager.SpawnEntity(prototype.Prototype, new EntityCoordinates(mapUid, placement.Position + position));
                instance.OwnedEntities.Add(asteroid);
                positions.Add(position);
                break;
            }
        }

        return true;
    }

    private static bool TryGetTargetCount(
        IPrototypeManager prototypes,
        NsvBluespaceAsteroidFieldGeneratorDefinition definition,
        NsvBluespaceSectorPlacement placement,
        out int targetCount,
        out float totalWeight)
    {
        targetCount = 0;
        totalWeight = 0f;

        if (!float.IsFinite(definition.Radius) ||
            !float.IsFinite(definition.Density) ||
            !float.IsFinite(definition.MinimumSpacing) ||
            definition.Radius < 0f ||
            definition.Radius > placement.FootprintRadius ||
            definition.Density < 0f ||
            definition.MinimumSpacing < 0f ||
            definition.MaxSpawnAttempts <= 0 ||
            definition.AsteroidTypes.Count == 0)
        {
            return false;
        }

        foreach (var asteroid in definition.AsteroidTypes)
        {
            if (!float.IsFinite(asteroid.Weight) ||
                asteroid.Weight <= 0f ||
                !prototypes.HasIndex<EntityPrototype>(asteroid.Prototype))
            {
                return false;
            }

            totalWeight += asteroid.Weight;
        }

        if (!float.IsFinite(totalWeight) || totalWeight <= 0f)
            return false;

        var expectedCount = definition.Density * MathF.PI * definition.Radius * definition.Radius;
        var desiredCount = MathF.Ceiling(expectedCount);
        if (!float.IsFinite(desiredCount) || desiredCount > MaximumAsteroids)
            return false;

        targetCount = (int) desiredCount;
        return true;
    }

    private static Vector2 SamplePosition(Random random, float radius)
    {
        var angle = (float) random.NextDouble() * MathF.Tau;
        var distance = MathF.Sqrt((float) random.NextDouble()) * radius;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance;
    }

    private static bool CanPlace(Vector2 position, List<Vector2> positions, float minimumSpacingSquared)
    {
        foreach (var existing in positions)
        {
            if (Vector2.DistanceSquared(position, existing) < minimumSpacingSquared)
                return false;
        }

        return true;
    }

    private static NsvBluespaceWeightedEntityDefinition PickAsteroidType(
        Random random,
        List<NsvBluespaceWeightedEntityDefinition> asteroidTypes,
        float totalWeight)
    {
        var remainingWeight = (float) random.NextDouble() * totalWeight;
        foreach (var asteroid in asteroidTypes)
        {
            remainingWeight -= asteroid.Weight;
            if (remainingWeight <= 0f)
                return asteroid;
        }

        return asteroidTypes[^1];
    }
}
