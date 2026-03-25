using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Gnd.HuntingDebuffs
{
    public class HuntingDebuffConfig
    {
        public int BleedDurationMs { get; set; } = 30000;
        public float BleedDamagePerSecond { get; set; } = 0.2f;
        public string[] ImmuneEntities { get; set; } = new string[]
        {
            "drifter",
            "locust",
            "bell",
            "shiver",
            "eidolon",
            "erel",
            "bowtorn"
        };

        public Dictionary<string, string> SpecialDisplayNames { get; set; } = new Dictionary<string, string>();
    }

    public class HuntingDebuffSystem : ModSystem
    {
        public static HuntingDebuffSystem Instance { get; private set; }
        public ICoreServerAPI Sapi => _sapi;

        private ICoreServerAPI _sapi;
        private Harmony _harmony;
        private readonly Dictionary<long, long> _bleedEndTimes = new Dictionary<long, long>();

        private HuntingDebuffConfig _config;
        private const string ConfigFilename = "HuntingDebuffsConfig.json";

        public override void StartServerSide(ICoreServerAPI api)
        {
            _sapi = api;
            Instance = this;

            LoadConfig();

            try
            {
                _harmony = new Harmony("com.gndbridge.huntingdebuffs");
                _harmony.PatchAll();
                api.Logger.Notification("[GND] Hunting Debuff System initialized.");
            }
            catch (Exception ex)
            {
                api.Logger.Error("[GND] Failed to initialize Harmony patches: " + ex.Message);
            }

            api.Event.RegisterGameTickListener(new Action<float>(OnGameTick), 1000, 0);
            api.Event.SaveGameLoaded += OnSaveGameLoaded;
            api.Event.GameWorldSave += OnGameWorldSave;

            RegisterCommands(api);
        }

        private void RegisterCommands(ICoreServerAPI api)
        {
            api.ChatCommands
                .Create("huntingdebuffs")
                .WithDescription("Управление модом Hunting Debuffs")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("reload")
                    .WithDescription("Перезагрузить конфигурацию")
                    .HandleWith(OnReloadCommand)
                .EndSubCommand()
                .BeginSubCommand("info")
                    .WithDescription("Показать текущую конфигурацию")
                    .HandleWith(OnInfoCommand)
                .EndSubCommand()
                .BeginSubCommand("clear")
                    .WithDescription("Очистить все активные кровотечения")
                    .HandleWith(OnClearCommand)
                .EndSubCommand();
        }

        private TextCommandResult OnReloadCommand(TextCommandCallingArgs args)
        {
            try
            {
                var oldConfig = _config;
                LoadConfig();

                if (oldConfig.ImmuneEntities != _config.ImmuneEntities)
                {
                    CleanupImmuneEntities();
                }

                return TextCommandResult.Success($"[GND] Конфигурация перезагружена.\n" +
                    $"Duration: {_config.BleedDurationMs}ms\n" +
                    $"Damage: {_config.BleedDamagePerSecond}/s\n" +
                    $"Immune entities: {string.Join(", ", _config.ImmuneEntities)}");
            }
            catch (Exception ex)
            {
                return TextCommandResult.Error($"[GND] Ошибка: {ex.Message}");
            }
        }

        private TextCommandResult OnInfoCommand(TextCommandCallingArgs args)
        {
            return TextCommandResult.Success($"[GND] Текущая конфигурация:\n" +
                $"Duration: {_config.BleedDurationMs}ms\n" +
                $"Damage: {_config.BleedDamagePerSecond}/s\n" +
                $"Active bleeds: {_bleedEndTimes.Count}\n" +
                $"Immune entities: {string.Join(", ", _config.ImmuneEntities)}");
        }

        private TextCommandResult OnClearCommand(TextCommandCallingArgs args)
        {
            int count = _bleedEndTimes.Count;
            _bleedEndTimes.Clear();
            return TextCommandResult.Success($"[GND] Очищено {count} активных кровотечений.");
        }

        private void CleanupImmuneEntities()
        {
            List<long> toRemove = new List<long>();

            foreach (var entityId in _bleedEndTimes.Keys)
            {
                Entity entity = _sapi.World.GetEntityById(entityId);
                if (entity != null && IsEntityImmune(entity))
                {
                    toRemove.Add(entityId);
                }
            }

            foreach (long entityId in toRemove)
            {
                _bleedEndTimes.Remove(entityId);
            }
        }

        private void LoadConfig()
        {
            try
            {
                _config = _sapi.LoadModConfig<HuntingDebuffConfig>(ConfigFilename);
                if (_config == null)
                {
                    _config = new HuntingDebuffConfig();
                    _sapi.StoreModConfig(_config, ConfigFilename);
                }
            }
            catch (Exception ex)
            {
                _sapi.Logger.Error("[GND] Failed to load config, using defaults: " + ex.Message);
                _config = new HuntingDebuffConfig();
            }
        }

        private void OnSaveGameLoaded()
        {
            try
            {
                byte[] data = _sapi.WorldManager.SaveGame.GetData("huntingDebuffs_bleedEndTimes");
                if (data != null)
                {
                    _bleedEndTimes.Clear();
                    Dictionary<long, long> loaded = SerializerUtil.Deserialize<Dictionary<long, long>>(data);
                    foreach (var pair in loaded)
                    {
                        _bleedEndTimes[pair.Key] = pair.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _sapi.Logger.Error("[GND] Failed to load bleed data: " + ex.Message);
            }
        }

        private void OnGameWorldSave()
        {
            try
            {
                _sapi.WorldManager.SaveGame.StoreData("huntingDebuffs_bleedEndTimes",
                    SerializerUtil.Serialize(_bleedEndTimes));
            }
            catch (Exception ex)
            {
                _sapi.Logger.Error("[GND] Failed to save bleed data: " + ex.Message);
            }
        }

        private void OnGameTick(float dt)
        {
            long currentTime = _sapi.World.ElapsedMilliseconds;
            List<long> toRemove = new List<long>();

            foreach (KeyValuePair<long, long> pair in _bleedEndTimes)
            {
                long entityId = pair.Key;
                long endTime = pair.Value;

                if (currentTime >= endTime)
                {
                    toRemove.Add(entityId);
                }
                else
                {
                    Entity entity = _sapi.World.GetEntityById(entityId);
                    if (entity == null || !entity.Alive)
                    {
                        toRemove.Add(entityId);
                    }
                    else
                    {
                        entity.ReceiveDamage(new DamageSource
                        {
                            Source = EnumDamageSource.Internal,
                            Type = EnumDamageType.PiercingAttack
                        }, _config.BleedDamagePerSecond);
                    }
                }
            }

            foreach (long entityId in toRemove)
            {
                _bleedEndTimes.Remove(entityId);
            }
        }

        public void ApplyBleedFromWeapon(Entity animal, IServerPlayer player, string weaponCode, float damage = 0, bool isRangedAttack = false)
        {
            if (animal == null || player == null)
                return;

            if (animal is EntityPlayer)
                return;

            if (damage <= 1.0f)
                return;

            if (IsEntityImmune(animal))
                return;

            if (!IsBleedWeapon(weaponCode))
                return;

            if (weaponCode.Contains("bow") && !isRangedAttack)
                return;

            long entityId = animal.EntityId;
            _bleedEndTimes[entityId] = _sapi.World.ElapsedMilliseconds + _config.BleedDurationMs;
        }

        private bool IsEntityImmune(Entity entity)
        {
            if (entity == null)
                return true;

            string code = entity.Code?.Path?.ToLower() ?? "";

            foreach (string immune in _config.ImmuneEntities)
            {
                if (code.Contains(immune))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsBleedWeapon(string weaponCode)
        {
            if (string.IsNullOrEmpty(weaponCode))
                return false;

            string code = weaponCode.ToLower();

            return code.Contains("spear") || code.Contains("bow") || code.Contains("arrow");
        }

        public override void Dispose()
        {
            _harmony?.UnpatchAll("com.gndbridge.huntingdebuffs");
            _bleedEndTimes.Clear();
            Instance = null;
            base.Dispose();
        }
    }
}