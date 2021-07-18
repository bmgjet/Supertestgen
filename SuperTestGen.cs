using Newtonsoft.Json;
using Oxide.Core;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SuperTestGen", "bmgjet", "1.0.0")]
    [Description("Overload TestGen for more power")]

    class SuperTestGen : RustPlugin
    {
        private const string permUse = "SuperTestGen.use";
        private const string permCustom = "SuperTestGen.custom";
        private static PluginConfig config;
        private static SaveData _data;

        #region Hooks
        void OnNewSave()
        {
            //Clear testgen save data.
            _data.SetTestGens.Clear();
            WriteSaveData();
        }

        private void OnServerSave()
        {
            //save datafile.
            WriteSaveData();
        }

        private void OnServerInitialized()
        {
            //check all testgens and apply powersetting back to ones that were set.
            List<ElectricGenerator> SpawnTestGens = BaseNetworkable.serverEntities.OfType<ElectricGenerator>().ToList();
            CleanSaveData(SpawnTestGens);
            foreach (var testgen in SpawnTestGens)
            {
                if (testgen == null || testgen.OwnerID == 0 || !_data.SetTestGens.ContainsKey(testgen.net.ID) || testgen.electricAmount != config.DefaultPL)
                {
                    continue;
                }
                testgen.GetComponentInChildren<TeslaCoil>()?.Kill(); //Clear out any old tesla coils incase server crashed.
                if (SetTestGen(testgen, _data.SetTestGens[testgen.net.ID])) //set settings.
                {
                    Puts("Found Set Entity " + testgen.ToString() + " " + testgen.OwnerID.ToString() + " Adding Settings");
                }
            }
        }

        private void Init()
        {
            //setup permissions
            permission.RegisterPermission(permUse, this);
            permission.RegisterPermission(permCustom, this);

            //setup save file
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(Name))
                Interface.Oxide.DataFileSystem.GetDatafile(Name).Save();

            //load save file
            _data = Interface.Oxide.DataFileSystem.ReadObject<SaveData>(Name);
            if (_data == null)
                WriteSaveData();

            //load config file
            config = Config.ReadObject<PluginConfig>();
            if (config == null)
                LoadDefaultConfig();
        }

        void Unload()
        {
            //save datafile and unload statics
            WriteSaveData();
            if (_data != null)
                _data = null;

            if (config != null)
                config = null;
        }
        #endregion

        #region Configuration
        private class PluginConfig
        {
            [JsonProperty(PropertyName = "Power Level : ")] public int Power { get; set; }
            [JsonProperty(PropertyName = "Upgrade Parts : ")] public string UpgradeParts { get; set; }
            [JsonProperty(PropertyName = "Upgrade Cost : ")] public int UpgradeCost { get; set; }
            [JsonProperty(PropertyName = "Refund Parts : ")] public bool Refund { get; set; }
            [JsonProperty(PropertyName = "Max Custom Power Level : ")] public int MaxPL { get; set; }
            [JsonProperty(PropertyName = "Default Power Level : ")] public int DefaultPL { get; set; }
        }

        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                Power = 200,
                UpgradeParts = "techparts",
                UpgradeCost = 10,
                Refund = true,
                MaxPL = 9999, //its over 9000!!!!!!!!!!
                DefaultPL = 100, //Setting incase FP changes its value in future.
            };
        }

        protected override void LoadDefaultConfig()
        {
            Config.Clear();
            Config.WriteObject(GetDefaultConfig(), true);
            config = Config.ReadObject<PluginConfig>();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config, true);
        }
        private void WriteSaveData() =>
        Interface.Oxide.DataFileSystem.WriteObject(Name, _data);

        class SaveData
        {
            public Dictionary<ulong, int> SetTestGens = new Dictionary<ulong, int>() { };
        }
        #endregion

        #region Code
        public BaseEntity FindTestGen(BasePlayer player)
        {
            //Searches players view for a test gen.
            RaycastHit rhit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out rhit))
                return null;

            var viewedEntity = rhit.GetEntity();
            if (viewedEntity == null || rhit.distance > 5f || !viewedEntity.ShortPrefabName.Contains("generator.small"))
                return null;

            return viewedEntity;
        }

        private bool SetTestGen(ElectricGenerator testgen, int pl)
        {
            TeslaCoil overload = GameManager.server.CreateEntity("assets/prefabs/deployable/playerioents/teslacoil/teslacoil.deployed.prefab", testgen.transform.position) as TeslaCoil;
            if (overload == null)
            {
                return false;
            }
            //destroy mesh and colliders
            UnityEngine.Object.DestroyImmediate(overload.GetComponent<DestroyOnGroundMissing>());
            UnityEngine.Object.DestroyImmediate(overload.GetComponent<GroundWatch>());
            foreach (var mesh in overload.GetComponentsInChildren<MeshCollider>())
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
            overload.Spawn(); //create it
            overload.OwnerID = testgen.OwnerID;
            overload.pickup.enabled = false;
            //set it up to be on with weak spark
            overload.SetFlag(TeslaCoil.Flag_HasPower, true);
            overload.SetFlag(TeslaCoil.Flag_WeakShorting, true);
            overload.SetParent(testgen);//parent so its removed at same time
            overload.transform.position = testgen.transform.position; //move to correct place.
            testgen.electricAmount = pl;
            testgen.pickup.enabled = false; //stop players picking up and losing there upgrade parts.
            testgen.UpdateOutputs(); //update items connected.
            testgen.SendChildrenNetworkUpdateImmediate();
            return true;
        }

        void Powerup(BasePlayer player, ElectricGenerator testgen, int pl = 0)
        {
            if (testgen.MaximalPowerOutput() != config.DefaultPL) //Value must already be set, So reverse it.
            {
                testgen.electricAmount = config.DefaultPL;
                if (_data.SetTestGens.ContainsKey(testgen.net.ID))
                {
                    _data.SetTestGens.Remove(testgen.net.ID); //Remove for save list.
                }
                testgen.UpdateOutputs();
                testgen.GetComponentInChildren<TeslaCoil>()?.Kill();
                testgen.pickup.enabled = true; //Allow it to be picked up again
                if (config.Refund && player.IsConnected)
                    player.GiveItem(CreateItem()); //Give back upgrade

                return;
            }

            if (pl == 0)
            {
                pl = config.Power; //Use config setting
            }
            else if (pl < 0 || pl > config.MaxPL)
            {
                player.ChatMessage("<color=orange>[Outside of range 0 - " + config.MaxPL.ToString() + "]</color>");
                return;
            }
            //PL stays as custom

            if (!TakeItem(player, "techparts", config.UpgradeCost)) //Check they have required upgrade parts.
            {
                player.ChatMessage("<color=orange>[" + config.UpgradeCost.ToString() + "]</color> " + config.UpgradeParts + " required!");
                return;
            }

            //Create a invisable teslacoil inside test gen for fx.
            if (!SetTestGen(testgen, pl))
            {
                player.GiveItem(CreateItem());
                return;
            }

            if (!_data.SetTestGens.ContainsKey(testgen.net.ID))
                _data.SetTestGens.Add(testgen.net.ID, pl); //save data on custom testgen

            Effect.server.Run("assets/bundled/prefabs/fx/build/promote_metal.prefab", testgen.transform.position); //Upgrade sound
        }

        private Item CreateItem()
        {
            var item = ItemManager.CreateByName(config.UpgradeParts, config.UpgradeCost, 0);
            if (item != null)
                return item;

            return null;
        }

        private bool TakeItem(BasePlayer player, string item, int amount)
        {
            var definition = ItemManager.FindItemDefinition(item);
            if (definition == null)
                return false;

            var playeramount = player.inventory.GetAmount(definition.itemid);
            if (playeramount < amount)
                return false;

            player.inventory.Take(null, definition.itemid, amount);
            return true;
        }

        void CleanSaveData(List<ElectricGenerator> MapGens)
        {
            List<uint> netids = new List<uint>();
            foreach (var gen in MapGens)
            {
                netids.Add(gen.net.ID);
            }
            foreach (KeyValuePair<ulong, int> SavGens in _data.SetTestGens.ToList())
            {
                if (!netids.Contains((uint)SavGens.Key))
                {
                    _data.SetTestGens.Remove(SavGens.Key);
                    Puts("Removed Missing Testgen from sav.");
                }
            }
            WriteSaveData();
        }
        #endregion

        #region ChatCommands
        [ChatCommand("overload")]
        private void Cmdntestgen(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, permUse))
                return;

            ElectricGenerator testgen = FindTestGen(player) as ElectricGenerator;
            if (testgen != null)
            {
                if (args.Length == 1 && permission.UserHasPermission(player.UserIDString, permCustom))
                {
                    int pl = 0;
                    int.TryParse(args[0], out pl);
                    if (pl != 0)
                    {
                        Powerup(player, testgen, pl); //pass custom power level
                        return;
                    }
                }
                Powerup(player, testgen);
            }
        }
        #endregion
    }
}
