using System.Linq;
using System.Numerics;

namespace Content.Server._NSV.Bluespace.Sectors;

public readonly record struct NsvBluespaceSectorModuleDefinition(
    string Id,
    int MinCount,
    int MaxCount,
    float FootprintRadius,
    float MinimumDistance,
    bool Required,
    int MaxPlacementAttempts);

public readonly record struct NsvBluespaceSectorPlacement(
    string ModuleId,
    Vector2 Position,
    float FootprintRadius,
    float MinimumDistance,
    int ContentSeed);

public sealed class NsvBluespaceSectorLayout
{
    public bool Succeeded { get; }
    public string? Failure { get; }
    public IReadOnlyList<NsvBluespaceSectorPlacement> Placements { get; }

    private NsvBluespaceSectorLayout(bool succeeded, string? failure, IReadOnlyList<NsvBluespaceSectorPlacement> placements)
    {
        Succeeded = succeeded;
        Failure = failure;
        Placements = placements;
    }

    public static NsvBluespaceSectorLayout Success(IReadOnlyList<NsvBluespaceSectorPlacement> placements)
    {
        return new NsvBluespaceSectorLayout(true, null, placements);
    }

    public static NsvBluespaceSectorLayout Failed(string failure)
    {
        return new NsvBluespaceSectorLayout(false, failure, Array.Empty<NsvBluespaceSectorPlacement>());
    }
}

public sealed class NsvBluespaceSectorLayoutPlanner
{
    public NsvBluespaceSectorLayout Plan(
        float radius,
        float entrySafeRadius,
        IReadOnlyList<NsvBluespaceSectorModuleDefinition> modules,
        int seed)
    {
        if (radius <= 0f)
            return NsvBluespaceSectorLayout.Failed("Sector radius must be positive.");

        if (entrySafeRadius < 0f || entrySafeRadius >= radius)
            return NsvBluespaceSectorLayout.Failed("Entry safe radius must fit inside the sector.");

        var placements = new List<NsvBluespaceSectorPlacement>();
        var random = new Random(seed);

        foreach (var module in modules.OrderByDescending(module => module.Required).ThenBy(module => module.Id, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(module.Id) ||
                module.MinCount < 0 ||
                module.MaxCount < module.MinCount ||
                module.Required && module.MinCount == 0 ||
                module.FootprintRadius < 0f ||
                module.MinimumDistance < 0f ||
                module.MaxPlacementAttempts <= 0)
            {
                return NsvBluespaceSectorLayout.Failed($"Module {module.Id} has invalid placement settings.");
            }

            var count = random.Next(module.MinCount, module.MaxCount + 1);
            for (var index = 0; index < count; index++)
            {
                if (TryPlace(radius, entrySafeRadius, module, placements, random, out var placement))
                {
                    placements.Add(placement);
                    continue;
                }

                if (module.Required)
                    return NsvBluespaceSectorLayout.Failed($"Required module {module.Id} could not be placed.");
            }
        }

        return NsvBluespaceSectorLayout.Success(placements);
    }

    private static bool TryPlace(
        float sectorRadius,
        float entrySafeRadius,
        NsvBluespaceSectorModuleDefinition module,
        List<NsvBluespaceSectorPlacement> placements,
        Random random,
        out NsvBluespaceSectorPlacement placement)
    {
        var minimumRadius = entrySafeRadius + module.FootprintRadius;
        var maximumRadius = sectorRadius - module.FootprintRadius;
        if (minimumRadius > maximumRadius)
        {
            placement = default;
            return false;
        }

        var minimumRadiusSquared = minimumRadius * minimumRadius;
        var maximumRadiusSquared = maximumRadius * maximumRadius;
        for (var attempt = 0; attempt < module.MaxPlacementAttempts; attempt++)
        {
            var angle = random.NextDouble() * Math.Tau;
            var distance = MathF.Sqrt(minimumRadiusSquared + (maximumRadiusSquared - minimumRadiusSquared) * (float) random.NextDouble());
            var position = new Vector2(MathF.Cos((float) angle), MathF.Sin((float) angle)) * distance;
            var candidate = new NsvBluespaceSectorPlacement(
                module.Id,
                position,
                module.FootprintRadius,
                module.MinimumDistance,
                0);

            if (placements.Any(existing => Intersects(candidate, existing)))
                continue;

            placement = candidate with { ContentSeed = random.Next() };
            return true;
        }

        placement = default;
        return false;
    }

    private static bool Intersects(NsvBluespaceSectorPlacement first, NsvBluespaceSectorPlacement second)
    {
        var minimumDistance = first.FootprintRadius + second.FootprintRadius + MathF.Max(first.MinimumDistance, second.MinimumDistance);
        return Vector2.DistanceSquared(first.Position, second.Position) < minimumDistance * minimumDistance;
    }
}
