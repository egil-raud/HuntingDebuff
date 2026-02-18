using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        public int MaxStacks { get; set; } = 10;
        public int BleedDurationMs { get; set; } = 30000;
        public float BleedDamagePerSecond { get; set; } = 0.5f;
        public string[] ImmuneEntities { get; set; } = new string[]
        {
            "drifter",
            "locust",
            "bell",
            "shiver",
            "bowtorn"
        };

        // Словарь для особых случаев, где автоматическое преобразование не подходит
        public Dictionary<string, string> SpecialDisplayNames { get; set; } = new Dictionary<string, string>
        {
            // Можно оставить пустым, или добавить только особые случаи
            // Например: { "bighornsheep", "Bighorn Sheep" }
        };
    }

    public class HuntingDebuffSystem : ModSystem
    {
        public static HuntingDebuffSystem Instance { get; private set; }
        public ICoreServerAPI Sapi => _sapi;

        private ICoreServerAPI _sapi;
        private Harmony _harmony;
        private readonly Dictionary<long, int> _bleedStacks = new Dictionary<long, int>();
        private readonly Dictionary<long, long> _lastBleedTime = new Dictionary<long, long>();

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
                api.Logger.Notification("[GND] ✅ Hunting Debuff System initialized (Harmony patch bound). Spears/Bows should apply bleed.");
                api.Logger.Notification($"[GND] Config: MaxStacks={_config.MaxStacks}, Duration={_config.BleedDurationMs}ms, Damage={_config.BleedDamagePerSecond}/s");
            }
            catch (Exception ex)
            {
                api.Logger.Error("[GND] ❌ Failed to initialize Harmony patches: " + ex.Message);
                api.Logger.Error("[GND] Hunting bleeds will NOT work - ensure 0Harmony.dll is next to the mod DLL.");
            }

            api.Event.RegisterGameTickListener(new Action<float>(OnGameTick), 1000, 0);
            api.Event.SaveGameLoaded += OnSaveGameLoaded;
            api.Event.GameWorldSave += OnGameWorldSave;

            // Регистрируем команду
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

                // Проверяем, изменился ли список иммунных существ
                if (oldConfig.ImmuneEntities != _config.ImmuneEntities)
                {
                    // Очищаем кровотечения у существ, которые стали иммунными
                    CleanupImmuneEntities();
                }

                return TextCommandResult.Success($"[GND] Конфигурация перезагружена. Текущие настройки:\n" +
                    $"MaxStacks: {_config.MaxStacks}\n" +
                    $"Duration: {_config.BleedDurationMs}ms\n" +
                    $"Damage: {_config.BleedDamagePerSecond}/s\n" +
                    $"Immune entities: {string.Join(", ", _config.ImmuneEntities)}");
            }
            catch (Exception ex)
            {
                return TextCommandResult.Error($"[GND] Ошибка при перезагрузке конфига: {ex.Message}");
            }
        }

        private TextCommandResult OnInfoCommand(TextCommandCallingArgs args)
        {
            return TextCommandResult.Success($"[GND] Текущая конфигурация:\n" +
                $"MaxStacks: {_config.MaxStacks}\n" +
                $"Duration: {_config.BleedDurationMs}ms\n" +
                $"Damage: {_config.BleedDamagePerSecond}/s\n" +
                $"Active bleeds: {_bleedStacks.Count}\n" +
                $"Immune entities: {string.Join(", ", _config.ImmuneEntities)}");
        }

        private TextCommandResult OnClearCommand(TextCommandCallingArgs args)
        {
            int count = _bleedStacks.Count;

            // Очищаем все дебаффы
            foreach (var entityId in _bleedStacks.Keys)
            {
                Entity entity = _sapi.World.GetEntityById(entityId);
                if (entity != null)
                {
                    RemoveDebuffs(entity);
                }
            }

            _bleedStacks.Clear();
            _lastBleedTime.Clear();

            return TextCommandResult.Success($"[GND] Очищено {count} активных кровотечений.");
        }

        private void CleanupImmuneEntities()
        {
            List<long> toRemove = new List<long>();

            foreach (var entityId in _bleedStacks.Keys)
            {
                Entity entity = _sapi.World.GetEntityById(entityId);
                if (entity != null && IsEntityImmune(entity))
                {
                    toRemove.Add(entityId);
                    RemoveDebuffs(entity);
                }
            }

            foreach (long entityId in toRemove)
            {
                _bleedStacks.Remove(entityId);
                _lastBleedTime.Remove(entityId);
            }

            if (toRemove.Count > 0)
            {
                _sapi.Logger.Notification($"[GND] Removed {toRemove.Count} bleed effects from entities that became immune.");
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
                    _sapi.Logger.Notification("[GND] Created default configuration file.");
                }
                else
                {
                    _sapi.Logger.Notification("[GND] Loaded configuration file.");
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
            // Загружаем сохраненные данные bleed стэков из мира
            try
            {
                byte[] data = _sapi.WorldManager.SaveGame.GetData("huntingDebuffs_bleedStacks");
                if (data != null)
                {
                    _bleedStacks.Clear();
                    Dictionary<long, int> loaded = SerializerUtil.Deserialize<Dictionary<long, int>>(data);
                    foreach (var pair in loaded)
                    {
                        _bleedStacks[pair.Key] = pair.Value;
                    }
                }

                data = _sapi.WorldManager.SaveGame.GetData("huntingDebuffs_lastBleedTime");
                if (data != null)
                {
                    _lastBleedTime.Clear();
                    Dictionary<long, long> loaded = SerializerUtil.Deserialize<Dictionary<long, long>>(data);
                    foreach (var pair in loaded)
                    {
                        _lastBleedTime[pair.Key] = pair.Value;
                    }
                }

                _sapi.Logger.Notification($"[GND] Loaded {_bleedStacks.Count} active bleed effects from save.");
            }
            catch (Exception ex)
            {
                _sapi.Logger.Error("[GND] Failed to load bleed data: " + ex.Message);
            }
        }

        private void OnGameWorldSave()
        {
            // Сохраняем данные bleed стэков в мир
            try
            {
                _sapi.WorldManager.SaveGame.StoreData("huntingDebuffs_bleedStacks",
                    SerializerUtil.Serialize(_bleedStacks));
                _sapi.WorldManager.SaveGame.StoreData("huntingDebuffs_lastBleedTime",
                    SerializerUtil.Serialize(_lastBleedTime));
            }
            catch (Exception ex)
            {
                _sapi.Logger.Error("[GND] Failed to save bleed data: " + ex.Message);
            }
        }

        private void OnGameTick(float dt)
        {
            long elapsedMilliseconds = _sapi.World.ElapsedMilliseconds;
            List<long> toRemove = new List<long>();

            foreach (KeyValuePair<long, int> pair in _bleedStacks)
            {
                long entityId = pair.Key;
                int stacks = pair.Value;

                if (_lastBleedTime.TryGetValue(entityId, out long lastTime) &&
                    elapsedMilliseconds - lastTime > _config.BleedDurationMs)
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
                        float damage = (float)stacks * _config.BleedDamagePerSecond;
                        entity.ReceiveDamage(new DamageSource
                        {
                            Source = EnumDamageSource.Internal,
                            Type = EnumDamageType.PiercingAttack
                        }, damage);
                    }
                }
            }

            foreach (long entityId in toRemove)
            {
                Entity entity = _sapi.World.GetEntityById(entityId);
                if (entity != null)
                {
                    RemoveDebuffs(entity);
                }
                _bleedStacks.Remove(entityId);
                _lastBleedTime.Remove(entityId);
            }
        }

        public void ApplyBleedFromWeapon(Entity animal, IServerPlayer player, string weaponCode)
        {
            if (animal == null || player == null)
                return;

            // Проверяем, НЕ является ли существо иммунным (из blacklist)
            if (IsEntityImmune(animal))
            {
                _sapi.Logger.Debug("[GND] Entity '" + animal.Code?.Path + "' is immune to bleeding - skipping.");
                return;
            }

            if (!IsBleedWeapon(weaponCode))
            {
                _sapi.Logger.Debug("[GND] Weapon '" + weaponCode + "' is not a bleed weapon - skipping.");
                return;
            }

            long entityId = animal.EntityId;
            _bleedStacks.TryGetValue(entityId, out int currentStacks);
            int newStacks = Math.Min(currentStacks + 1, _config.MaxStacks);

            _bleedStacks[entityId] = newStacks;
            _lastBleedTime[entityId] = _sapi.World.ElapsedMilliseconds;

            ApplyDebuffs(animal, newStacks);

            string animalDisplayName = GetAnimalDisplayName(animal);
            string weaponName = GetWeaponName(weaponCode);

            _sapi.Logger.Notification(string.Format("[GND] \ud83e\ude78 {0} wounded by {1} with {2} - Bleed stacks: {3}/{4}",
                animalDisplayName, player.PlayerName, weaponName, newStacks, _config.MaxStacks));
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

        private string GetWeaponName(string weaponCode)
        {
            string code = (weaponCode ?? "").ToLower();
            if (code.Contains("spear"))
                return "Spear";
            if (code.Contains("arrow"))
                return "Arrow";
            if (code.Contains("bow"))
                return "Bow";
            return "Weapon";
        }

        private void ApplyDebuffs(Entity animalEntity, int stacks)
        {
            if (animalEntity?.Stats == null)
                return;

            float speedMultiplier = 1f - (float)stacks * 0.1f;
            if (speedMultiplier < 0.1f)
                speedMultiplier = 0.1f;

            animalEntity.Stats.Set("walkspeed", "bleed", speedMultiplier, false);
        }

        private void RemoveDebuffs(Entity animalEntity)
        {
            if (animalEntity?.Stats == null)
                return;

            animalEntity.Stats.Remove("walkspeed", "bleed");
        }

        private string GetAnimalDisplayName(Entity entity)
        {
            if (entity == null)
                return "Unknown";

            string code = (entity.Code?.Path ?? "Unknown").ToLower();

            // Сначала проверяем особые случаи из словаря
            foreach (var kvp in _config.SpecialDisplayNames)
            {
                if (code.Contains(kvp.Key))
                {
                    return kvp.Value;
                }
            }

            // Ищем соответствие в списке иммунных существ
            foreach (string immune in _config.ImmuneEntities)
            {
                if (code.Contains(immune))
                {
                    // Автоматически преобразуем ключ в читаемое имя
                    return CapitalizeFirstLetter(immune);
                }
            }

            // Если ничего не нашли, форматируем оригинальный код
            return FormatEntityCode(code);
        }

        private string CapitalizeFirstLetter(string word)
        {
            if (string.IsNullOrEmpty(word))
                return word;

            return char.ToUpper(word[0]) + word.Substring(1);
        }

        private string FormatEntityCode(string code)
        {
            // Разделяем по дефисам и подчеркиваниям, затем капитализируем каждое слово
            string[] parts = code.Split(new char[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (!string.IsNullOrEmpty(parts[i]))
                {
                    parts[i] = char.ToUpper(parts[i][0]) + parts[i].Substring(1);
                }
            }
            return string.Join(" ", parts);
        }

        public override void Dispose()
        {
            _harmony?.UnpatchAll("com.gndbridge.huntingdebuffs");
            _bleedStacks.Clear();
            _lastBleedTime.Clear();
            Instance = null;
            base.Dispose();
        }
    }
}