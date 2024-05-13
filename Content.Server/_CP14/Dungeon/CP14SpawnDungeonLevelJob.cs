
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Parallax;
using Content.Server.Procedural;
using Content.Shared._CP14.Dungeon;
using Content.Shared.Atmos;
using Content.Shared.Construction.EntitySystems;
using Content.Shared.Gravity;
using Content.Shared.Parallax.Biomes;
using Content.Shared.Physics;
using Content.Shared.Procedural;
using Content.Shared.Procedural.Loot;
using Content.Shared.Random;
using Robust.Shared.Collections;
using Robust.Shared.CPUJob.JobQueues;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server._CP14.Dungeon;

public sealed class CP14SpawnDungeonLevelJob : Job<bool>
{
    private readonly IEntityManager _entManager;
    private readonly IMapManager _mapManager;
    private readonly SharedMapSystem _map;
    private readonly IPrototypeManager _proto;
    private readonly DungeonSystem _dungeon;
    private readonly MetaDataSystem _metaData;
    private readonly ProtoId<CP14DungeonLevelPrototype> _levelProto;
    private readonly AtmosphereSystem _atmos;
    private readonly BiomeSystem _biome;
    private readonly AnchorableSystem _anchorable;
    private readonly CP14StationDungeonDataComponent _dungeonData;
    private readonly IRobustRandom _random;

    private readonly ISawmill _sawmill;

    public CP14SpawnDungeonLevelJob(
        double maxTime,
        AnchorableSystem anchorable,
        AtmosphereSystem atmos,
        BiomeSystem biome,
        IEntityManager entManager,
        ILogManager logManager,
        IMapManager mapManager,
        SharedMapSystem mapSystem,
        IPrototypeManager protoManager,
        DungeonSystem dungeon,
        MetaDataSystem metaData,
        IRobustRandom random,
        ProtoId<CP14DungeonLevelPrototype> levelPrototype,
        CP14StationDungeonDataComponent dungeonData,
        CancellationToken cancellation = default) : base(maxTime, cancellation)
    {
        _anchorable = anchorable;
        _atmos = atmos;
        _biome = biome;
        _entManager = entManager;
        _mapManager = mapManager;
        _map = mapSystem;
        _proto = protoManager;
        _metaData = metaData;
        _dungeon = dungeon;
        _levelProto = levelPrototype;
        _dungeonData = dungeonData;
        _random = random;
        _sawmill = logManager.GetSawmill("CRYSTALL PUNK DUNGEON LEVEL");
    }

    protected override async Task<bool> Process()
    {
        foreach (var layer in _dungeonData.AllowedLayers)
        {
            var layerData = _proto.Index(layer);
            //Init empty map
            var mapUid = _map.CreateMap(out var mapId, runMapInit: false);
            MetaDataComponent? metadata = null;
            var grid = _entManager.EnsureComponent<MapGridComponent>(mapUid);
            _metaData.SetEntityName(mapUid,$"MapId: {mapId}, Depth: Unknown?");

            //Biome spawn
            _sawmill.Debug($"Biome");
            var levelBiome = _proto.Index(layerData.BiomeTemplate);

            var biome = _entManager.AddComponent<BiomeComponent>(mapUid);
            var biomeSystem = _entManager.System<BiomeSystem>();
            biomeSystem.SetTemplate(mapUid, biome, levelBiome);
            _entManager.Dirty(mapUid, biome);

            // Gravity
            _sawmill.Debug($"Gravity");
            var gravity = _entManager.EnsureComponent<GravityComponent>(mapUid);
            gravity.Enabled = true;
            _entManager.Dirty(mapUid, gravity, metadata);

            //Atmos
            _sawmill.Debug($"Atmos");
            var moles = new float[Atmospherics.AdjustedNumberOfGases];
            moles[(int) Gas.Oxygen] = 21.824779f;
            moles[(int) Gas.Nitrogen] = 82.10312f;

            var mixture = new GasMixture(moles, Atmospherics.T20C);

            _atmos.SetMapAtmosphere(mapUid, false, mixture);
            _mapManager.DoMapInitialize(mapId);

            var levelParams = _proto.Index(_levelProto);

            //Set modifier
            if (layerData.Modifiers.Count > 0)
            {
                var modifier = _proto.Index(_random.Pick(layerData.Modifiers));
                _entManager.AddComponents(mapUid, modifier.Components, true);
            }

            switch (levelParams.Level)
            {
                case MappingGridDungeonLevel mappingLevel:

                    _sawmill.Debug($"Spawning new mapped dungeon level {levelParams.ID}. Depth: Unknown?");

                    break;
                case RandomDungeonLevel randomLevel:
                    await SpawnRandomDungeonLevel(_dungeonData, randomLevel, mapUid, grid, 15);
                    break;
            }

            _mapManager.SetMapPaused(mapId, false);
        }

        return true;
    }

    private async Task SpawnRandomDungeonLevel(CP14StationDungeonDataComponent data, RandomDungeonLevel settings, EntityUid mapUid, MapGridComponent grid, float Depth)
    {
        _sawmill.Debug($"Spawning new random dungeon level with seed {settings.Seed}. Depth: {Depth}");
        var random = new Random(settings.Seed);

        //Spawn dungeon
        _sawmill.Debug($"Spawn Dungeon");
        var config = _proto.Index(settings.DungeonConfig);
        var dungeon =
            await WaitAsyncTask(_dungeon.GenerateDungeonAsync(config, mapUid, grid, Vector2i.Zero, settings.Seed));

        // Abort
        if (dungeon.Rooms.Count == 0)
        {
            _sawmill.Error($"Dungeon Level aborting!!!! PANIKA PANIKA");
            return;
        }

        // FILLING DUNGEON
        var randomSystem = _entManager.System<RandomSystem>();
        var budgetEntries = new List<IBudgetEntry>();

        // Guarantee Exit
        //var exitProto = _prototypeManager.Index<SalvageLootPrototype>("CPDungeonExit");

        //Mobs
        var mobBudget = data.StartMobBudget + settings.MobBudgetPerDepth * Depth;
        var faction = _proto.Index(settings.MobFaction);

        _sawmill.Debug($"Mob Budget: {mobBudget}. Factions: ${faction}");

        foreach (var entry in faction.MobGroups)
        {
            budgetEntries.Add(entry);
        }

        var probSum = budgetEntries.Sum(x => x.Prob);

        while (mobBudget > 0f)
        {
            var entry = randomSystem.GetBudgetEntry(ref mobBudget, ref probSum, budgetEntries, random);
            if (entry == null)
                break;

            await SpawnRandomEntry(grid, entry, dungeon, random);
        }

        //Loot
        var lootBudget = data.StartLootBudget + settings.LootBudgetPerDepth * Depth;
        var loot = _proto.Index(settings.LootPrototype);

        foreach (var rule in loot.LootRules)
        {
            switch (rule)
            {
                case RandomSpawnsLoot randomLoot:
                    budgetEntries.Clear();
                    foreach (var entry in randomLoot.Entries)
                    {
                        budgetEntries.Add(entry);
                    }

                    probSum = budgetEntries.Sum(x => x.Prob);

                    while (lootBudget > 0f)
                    {
                        var entry = randomSystem.GetBudgetEntry(ref lootBudget, ref probSum, budgetEntries, random);
                        if (entry == null)
                            break;

                        _sawmill.Debug($"Spawning dungeon loot {entry.Proto}");
                        await SpawnRandomEntry(grid, entry, dungeon, random);
                    }

                    break;
                default:
                    throw new NotImplementedException();
            }
        }
    }

    private async Task SpawnDungeonLoot(SalvageLootPrototype loot, EntityUid gridUid)
    {
        for (var i = 0; i < loot.LootRules.Count; i++)
        {
            var rule = loot.LootRules[i];

            switch (rule)
            {
                case BiomeMarkerLoot biomeLoot:
                {
                    if (_entManager.TryGetComponent<BiomeComponent>(gridUid, out var biome))
                    {
                        _biome.AddMarkerLayer(gridUid, biome, biomeLoot.Prototype);
                    }
                }
                    break;
                case BiomeTemplateLoot biomeLoot:
                {
                    if (_entManager.TryGetComponent<BiomeComponent>(gridUid, out var biome))
                    {
                        _biome.AddTemplate(gridUid, biome, "Loot", _proto.Index<BiomeTemplatePrototype>(biomeLoot.Prototype), i);
                    }
                }
                    break;
            }
        }
    }

    private async Task SpawnRandomEntry(MapGridComponent grid, IBudgetEntry entry, Shared.Procedural.Dungeon dungeon, Random random)
    {
        await SuspendIfOutOfTime();

        var availableRooms = new ValueList<DungeonRoom>(dungeon.Rooms);
        var availableTiles = new List<Vector2i>();

        while (availableRooms.Count > 0)
        {
            availableTiles.Clear();
            var roomIndex = random.Next(availableRooms.Count);
            var room = availableRooms.RemoveSwap(roomIndex);
            availableTiles.AddRange(room.Tiles);

            while (availableTiles.Count > 0)
            {
                var tile = availableTiles.RemoveSwap(random.Next(availableTiles.Count));

                if (!_anchorable.TileFree(grid, tile, (int) CollisionGroup.MachineLayer,
                        (int) CollisionGroup.MachineLayer))
                {
                    continue;
                }

                var uid = _entManager.SpawnAtPosition(entry.Proto, grid.GridTileToLocal(tile));
                return;
            }
        }

        // oh noooooooooooo
    }
}

