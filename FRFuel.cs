using CitizenFX.Core;
using CitizenFX.Core.UI;

using FRFuel.Shared.Models;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using static CitizenFX.Core.Native.API;

namespace FRFuel
{
    public class FRFuel : BaseScript
    {
        delegate float GetCurrentFuelLevelDelegate();
        delegate void AddFuel(float amount);
        delegate void SetFuel(float amount);

        #region General Variables

        private int _currentGasStationIndex;

        internal int _nearestPumpCacheTimer = 0;

        protected bool _nozzleInHand = false;
        protected bool _isNearGasPump = false;
        protected bool _refuelAllowed = true;

        protected bool _showHud = true;
        protected bool _showHudWhenEngineOff = true;
        protected bool _useInVehicleRefueling = false;

        protected bool _initialized = false;
        protected bool _currentVehicleFuelLevelInitialized = false;

        protected Config Config { get; set; }
        protected Vehicle LastVehicle { get => _lastVehicle; set => _lastVehicle = value; }
        
        public HUD _hud;

        public Random _random = new Random();

        internal Prop _nearestPumpCached = null;
        
        private Vehicle _lastVehicle;
        
        protected InLoopOutAnimation _jerryCanAnimation;
        
        protected Blip[] _blips;

        #endregion

        #region Constant Variables

        protected float FUEL_TANK_CAPACITY = 65f;

        protected float REFUEL_RATE = 1f;
        protected float ADDED_FUEL_CAPACITOR = 0f;
        protected float FUEL_CONSUMPTION_RATE = 1f;
        protected float FUEL_ACCELERATION_IMPACT = 0.0002f;
        protected float FUEL_TRACTION_IMPACT = 0.0001f;
        protected float FUEL_RPM_IMPACT = 0.0005f;

        public float SHOW_MARKER_RANGE = 250f;

        public static string FUEL_LEVEL_DECOR = "_Fuel_Level";
        public static string JERRYCAN_ANIM_DICT = "weapon@w_sp_jerrycan";

        internal const string NOZZLE_ANIM_DICT = "mp_common";
        internal const string NOZZLE_ANIM_NAME = "givetake1_a";

        internal const string REFUEL_ANIM_DICT = "timetable@gardener@filling_can";
        internal const string REFUEL_ANIM_NAME = "gar_ig_5_filling_can";

        private string NORMAL_HEX = "FFB300"; // 255, 179, 0
        private string WARNING_HEX = "FFF5DC"; // 255, 179, 0

        public static Control ENGINE_TOGGLE = Control.ThrowGrenade; // INPUT_THROW_GRENADE
        public static Control NOZZLE_INTERACT = Control.VehicleHeadlight; // INPUT_THROW_GRENADE

        internal Model FUEL_NOZZLE_MODEL = new Model("prop_cs_fuel_nozle");

        public static string[] TANK_BONES = new string[] {
            "petrolcap",
            "petroltank",
            "petroltank_r",
            "petroltank_l",
            "wheel_lr"
        };

        internal IReadOnlyList<Model> OLD_GAS_PUMP_MODELS = new List<Model>()
        {
            new Model("prop_gas_pump_old2"),
            new Model("prop_gas_pump_old3"),
            new Model("prop_vintage_pump")
        };

        internal IReadOnlyList<Model> GAS_PUMP_MODELS = new List<Model>()
        {
            new Model("prop_gas_pump_1a"),
            new Model("prop_gas_pump_1b"),
            new Model("prop_gas_pump_1c"),
            new Model("prop_gas_pump_1d"),
            new Model("prop_gas_pump_old2"),
            new Model("prop_gas_pump_old3"),
            new Model("prop_vintage_pump")
        };

        #endregion

        #region Constructor

        /// <summary>
        /// Ctor
        /// </summary>
        public FRFuel()
        {
            _hud = new HUD();

            _jerryCanAnimation = new InLoopOutAnimation(
              new Animation(JERRYCAN_ANIM_DICT, "fire_intro"),
              new Animation(JERRYCAN_ANIM_DICT, "fire"),
              new Animation(JERRYCAN_ANIM_DICT, "fire_outro")
            );

            GasStations.LoadGasStations();

            if (GasStations.positions == null)
            {
                Debug.WriteLine("[FRFuel] Gas stations failed to load");

                return;
            }

            _blips = new Blip[GasStations.positions.Length];

            Exports.Add("addFuel", new AddFuel(ExportsAddFuel));
            Exports.Add("setFuel", new SetFuel(ExportsSetFuel));
            Exports.Add("getCurrentFuelLevel", new GetCurrentFuelLevelDelegate(ExportsGetCurrentFuelLevel));
        }

        #endregion

        #region Tick Handlers

        /// <summary>
        /// On tick
        /// </summary>
        /// <returns></returns>
        [Tick]
        internal async Task OnTick()
        {
            if (!_initialized)
            {
                _initialized = true;

                LoadConfig();

                CreateBlips();

                EntityDecoration.RegisterProperty(FUEL_LEVEL_DECOR, DecorationType.Float);
            }

            Ped playerPed = Game.PlayerPed;

            Vehicle vehicle = playerPed.CurrentVehicle;
            Vehicle nearbyVeh = GetNearbyLastVehicle(playerPed);

            if (!PlayerVehicleViableForFuel())
            {
                ManualRefuel(playerPed);

                if (!_useInVehicleRefueling)
                {
                    ExternalRefuel(playerPed, nearbyVeh);
                }

                if (playerPed.IsOnFoot)
                {
                    RenderUIOnFoot(playerPed, nearbyVeh);
                }

                _currentVehicleFuelLevelInitialized = false;

                return;
            }

            if (_lastVehicle != vehicle)
            {
                _lastVehicle = vehicle;
                _currentVehicleFuelLevelInitialized = false;
            }

            if (!_currentVehicleFuelLevelInitialized)
            {
                InitFuel(vehicle);
            }

            if (_useInVehicleRefueling)
            {
                ConsumeFuel(vehicle);
            }

            RenderUI(playerPed);

            await Task.FromResult(0);
        }

        #endregion

        #region Event Handlers

        [EventHandler("onResourceStop")]
        internal void OnResourceStop(string resourceName)
        {
            if (resourceName == GetCurrentResourceName())
            {
                TriggerServerEvent("frfuel:server:removeAllAccessories");

                foreach (FuelAccessories accessories in FuelAccessories.GetAllAccessories())
                {
                    if (accessories == null)
                    {
                        continue;
                    }

                    OnRemoveAccesorries(accessories.PlayerId);
                }
            }
        }

        [EventHandler("frfuel:refuelAllowed")]
        internal void OnRefuelAllowed(bool toggle) => _refuelAllowed = toggle;

        [EventHandler("frfuel:createAccessories")]
        internal async void OnCreateAccessories(int playerId, Vector3 position)
        {
            Ped plyrPed = playerId == Game.Player.ServerId ? Game.PlayerPed : new Player(GetPlayerFromServerId(playerId)).Character;

            if (!FUEL_NOZZLE_MODEL.IsLoaded)
            {
                await FUEL_NOZZLE_MODEL.Request(10);
            }

            try
            {
                if (playerId == Game.Player.ServerId)
                {
                    if (plyrPed.Weapons.Current != WeaponHash.Unarmed)
                    {
                        plyrPed.Weapons.Select(WeaponHash.Unarmed, true);
                    }

                    Prop nozzle = new Prop(CreateObject(FUEL_NOZZLE_MODEL.Hash, 0f, 0f, 0f, true, true, true));

                    nozzle.AttachTo(plyrPed.Bones[Bone.SKEL_L_Hand], new Vector3(0.070f, 0.060f, 0.0f), new Vector3(180f, 70f, 100f));

                    SetEntityAlpha(nozzle.Handle, 255, 1);
                    NetworkRegisterEntityAsNetworked(nozzle.Handle);

                    await Delay(150);

                    FuelAccessories.Register(playerId, nozzle.Handle, new KeyValuePair<int, Vector3>(0, position));
                }

                int unk = 0;

                Prop nearestPump = GetClosestGasPump();

                if (nearestPump == null)
                {
#if DEBUG
                    Debug.WriteLine("Something bad happened, lol");
#endif
                    return;
                }

                if (!RopeAreTexturesLoaded())
                {
                    RopeLoadTextures();
                }

                float hoseLength = 4f;

                Vector3 fingerPos = plyrPed.Bones[Bone.IK_L_Hand].Position;

                Rope gasRope = new Rope(AddRope(fingerPos.X, fingerPos.Y, fingerPos.Z, 0f, 0f, 0f, hoseLength, 3, 6f, 0.25f, 0f, false, false, false, hoseLength, false, ref unk));

                float hoseHeight;

                if (OLD_GAS_PUMP_MODELS.Contains(nearestPump.Model))
                {
                    hoseHeight = 1.5f;
                }
                else
                {
                    hoseHeight = 2.1f;
                }

                AttachEntitiesToRope(gasRope.Handle, plyrPed.Handle, nearestPump.Handle, 0f, 0.1f, 0f, position.X, position.Y, position.Z + hoseHeight, hoseLength, false, false, "SKEL_L_Finger01", null);

                gasRope.ActivatePhysics();

                StartRopeWinding(gasRope.Handle);
                RopeForceLength(gasRope.Handle, 3f);

                FuelAccessories existing = FuelAccessories.GetAccessoriesByPlayer(playerId);

                if (existing != null)
                {
                    existing.HoseId = gasRope.Handle;
                }
                else
                {
                    FuelAccessories.Register(playerId, 0, new KeyValuePair<int, Vector3>(gasRope.Handle, position));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                Debug.WriteLine(ex.StackTrace);
            }
        }

        [EventHandler("frfuel:removeAccessories")]
        internal void OnRemoveAccesorries(int playerId)
        {
            FuelAccessories accessories = FuelAccessories.GetAccessoriesByPlayer(playerId);

            if (accessories == null)
            {
                return;
            }

            try
            {
                int hoseId = accessories.HoseId;

                if (DoesRopeExist(ref hoseId))
                {
                    RopeUnloadTextures();
                    DeleteRope(ref hoseId);
                }

                int nozzleId = accessories.NozzleId;/*NetworkGetEntityFromNetworkId(accessories.NozzleId);*/

                if (DoesEntityExist(nozzleId))
                {
                    DeleteEntity(ref nozzleId);
                }

                FuelAccessories.Unregister(playerId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                Debug.WriteLine(ex.StackTrace);
            }
        }

#endregion

        #region Init

        /// <summary>
        /// Loads configuration from file
        /// </summary>
        protected void LoadConfig()
        {
            string configContent = null;

            try
            {
                configContent = LoadResourceFile(GetCurrentResourceName(), "config.ini");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"An error occurred while loading the config file, error description: {e.Message}.");
            }

            Config = new Config(configContent);

            _showHud = Config.Get("ShowHud", "true") == "true";
            _showHudWhenEngineOff = Config.Get("ShowHudWhenEngineOff", "true").ToLower() == "true";
            _useInVehicleRefueling = Config.Get("InVehicleRefuel", "false").ToLower() == "false";

            var fuelConsumptionString = Config.Get("FuelConsumptionRate", "1");
            if (float.TryParse(fuelConsumptionString, out float tmpFuelConsumptionRate))
            {
                FUEL_CONSUMPTION_RATE = tmpFuelConsumptionRate;
            }
#if DEBUG
            else
            {
                Debug.WriteLine("Invalid FuelConsumptionRate value. Make sure it is a valid float value, e.g. 1.2");
            }
#endif

            var refuelRateString = Config.Get("RefuelRate", "1");
            if (float.TryParse(refuelRateString, out float tmpRefuelRate))
            {
                REFUEL_RATE = tmpRefuelRate;
            }
#if DEBUG
            else
            {
                Debug.WriteLine("Invalid RefuelRate value. Make sure it is a valid float value, e.g. 1.2");
            }
#endif

            // if a valid key is set in the config file, set the control.
            if (int.TryParse(Config.Get("EngineToggleKey", "58"), out int tmpControl) && int.TryParse(Config.Get("NozzleInteractKey", "74"), out int nozzleInt))
            {
                ENGINE_TOGGLE = (Control)tmpControl;
                NOZZLE_INTERACT = (Control)nozzleInt;
            }

            NORMAL_HEX = Config.Get("FuelBarNormalColor", NORMAL_HEX).Replace("\"", "").Replace("#", "");
            // normal color
            int r = MathUtil.Clamp(int.Parse(NORMAL_HEX.Substring(0, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);
            int g = MathUtil.Clamp(int.Parse(NORMAL_HEX.Substring(2, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);
            int b = MathUtil.Clamp(int.Parse(NORMAL_HEX.Substring(4, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);

            WARNING_HEX = Config.Get("FuelBarWarningColor", NORMAL_HEX).Replace("\"", "").Replace("#", "");
            // warning color
            int wR = MathUtil.Clamp(int.Parse(WARNING_HEX.Substring(0, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);
            int wG = MathUtil.Clamp(int.Parse(WARNING_HEX.Substring(2, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);
            int wB = MathUtil.Clamp(int.Parse(WARNING_HEX.Substring(4, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);
            _hud.UpdateBarColors(r, g, b, wR, wG, wB);

#if DEBUG
            Debug.WriteLine($"ShowHud: {Config.Get("ShowHud", "true")}");
            Debug.WriteLine($"NozzleInteractKey: {Config.Get("NozzleInteractKey", "74")}");
            Debug.WriteLine($"EngineToggleKey: {Config.Get("EngineToggleKey", "58")}");
            Debug.WriteLine($"FuelConsumptionRate: {Config.Get("FuelConsumptionRate", "1")}");
#endif
        }

        /// <summary>
        /// Creates blips for gas stations
        /// </summary>
        public void CreateBlips()
        {
            if (Config.Get("CreateBlips", "true") != "true")
            {
                return;
            }

            for (int i = 0; i < GasStations.positions.Length; i++)
            {
                var blip = World.CreateBlip(GasStations.positions[i]);
                blip.Sprite = BlipSprite.JerryCan;
                blip.Color = BlipColor.White;
                blip.Scale = 1f;
                blip.IsShortRange = true;
                blip.Name = "Gas Station";

                _blips[i] = blip;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Returns vehicle's max fuel level
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public float VehicleMaxFuelLevel(Vehicle vehicle) => GetVehicleHandlingFloat(vehicle.Handle, "CHandlingData", "fPetrolTankVolume");

        internal void RenderUIOnFoot(Ped plyrPed, Vehicle vehicle)
        {
            if (vehicle == null)
            {
                return;
            }

            if (!_showHud || !IsHudPreferenceSwitchedOn())
            {
                return;
            }

            if (World.GetDistance(plyrPed.Position, vehicle.Position) < 5f)
            {
                _hud.RenderBar(vehicle.FuelLevel, VehicleMaxFuelLevel(vehicle));
            }
        }

        /// <summary>
        /// Returns random fuel level between 1/3 and 3/4 of tank capacity
        /// </summary>
        /// <param name="fuelLevel"></param>
        /// <returns></returns>
        public float RandomizeFuelLevel(float fuelLevel)
        {
            float min = fuelLevel / 3f;
            float max = fuelLevel - (fuelLevel / 4);

            return (float)((_random.NextDouble() * (max - min)) + min);
        }

        /// <summary>
        /// Check if the players current vehicle is refuelable
        /// </summary>
        /// <returns></returns>
        protected bool PlayerVehicleViableForFuel()
        {
            Ped playerPed = Game.PlayerPed;
            Vehicle vehicle = playerPed.CurrentVehicle;

            if (vehicle == null || !vehicle.Exists())
            {
                return false;
            }

            return (vehicle.Model.IsCar || vehicle.Model.IsBike || vehicle.Model.IsQuadbike) && vehicle.GetPedOnSeat(VehicleSeat.Driver) == playerPed && vehicle.IsAlive;
        }

        /// <summary>
        /// Get the nearest lats vehicle assigned to local ped
        /// </summary>
        /// <param name="plyrPed"></param>
        /// <returns></returns>
        internal Vehicle GetNearbyLastVehicle(Ped plyrPed)
        {
            Vehicle veh = plyrPed.LastVehicle;

            if (veh == null || !veh.Exists())
            {
                return null;
            }

            if (!veh.Model.IsCar && !veh.Model.IsBike && !veh.Model.IsQuadbike)
            {
                return null;
            }

            if (!veh.IsAlive)
            {
                return null;
            }

            if (veh.Position.DistanceToSquared(plyrPed.Position) > 15f)
            {
                return null;
            }

            return veh;
        }

        /// <summary>
        /// External API for getting current fuel leve;
        /// </summary>
        /// <returns></returns>
        public float ExportsGetCurrentFuelLevel()
        {
            if (!PlayerVehicleViableForFuel())
            {
                return -1f;
            }

            return Game.PlayerPed.CurrentVehicle.FuelLevel;
        }

        /// <summary>
        /// External API for adding fuel
        /// </summary>
        /// <param name="amount"></param>
        public void ExportsAddFuel(float amount)
        {
            if (PlayerVehicleViableForFuel())
            {
                var vehicle = Game.PlayerPed.CurrentVehicle;

                VehicleSetFuelLevel(vehicle, vehicle.FuelLevel + amount);
            }
        }

        /// <summary>
        /// External API for setting fuel level
        /// </summary>
        /// <param name="amount"></param>
        public void ExportsSetFuel(float amount)
        {
            if (PlayerVehicleViableForFuel())
            {
                var vehicle = Game.PlayerPed.CurrentVehicle;

                VehicleSetFuelLevel(vehicle, amount);
            }
        }

        /// <summary>
        /// Returns gas station position that is in range
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="rangeSquared"></param>
        /// <returns></returns>
        public int GetGasStationIndexInRange(Vector3 pos, float rangeSquared)
        {
            for (int i = 0; i < _blips.Length; i++)
            {
                if (Vector3.DistanceSquared(GasStations.positions[i], pos) < rangeSquared)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Get the closest gas pump to the local ped
        /// </summary>
        /// <returns></returns>
        internal Prop GetClosestGasPump()
        {
            if (Game.GameTime - _nearestPumpCacheTimer < 500)
            {
                return _nearestPumpCached;
            }

            _nearestPumpCacheTimer = Game.GameTime;

            Vector3 pedPos = Game.PlayerPed.Position;

            _nearestPumpCached = World.GetAllProps()
                .Where(x => GAS_PUMP_MODELS.Contains(x.Model) && x.Position.DistanceToSquared(pedPos) < 25f)
                .OrderBy(x => x.Position.DistanceToSquared(pedPos))
                .FirstOrDefault();

            return _nearestPumpCached;
        }

        /// <summary>
        /// Checks if vehicle is within range of activation
        /// for any pump at current gas station
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public bool IsVehicleNearAnyPump(Vehicle vehicle)
        {
            Vector3 fuelTankPos = GetVehicleTankPos(vehicle);

            foreach (Vector3 pump in GasStations.pumps[_currentGasStationIndex])
            {
                if (Vector3.DistanceSquared(pump, fuelTankPos) <= 20f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns vehicle's current fuel level
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public float VehicleFuelLevel(Vehicle vehicle)
        {
            if (vehicle.HasDecor(FUEL_LEVEL_DECOR))
            {
                return vehicle.GetDecor<float>(FUEL_LEVEL_DECOR);
            }

            return 65f;
        }

        /// <summary>
        /// Correctly sets vehicle's fuel level
        /// </summary>
        /// <param name="vehicle"></param>
        /// <param name="fuelLevel"></param>
        public void VehicleSetFuelLevel(Vehicle vehicle, float fuelLevel)
        {
            float max = VehicleMaxFuelLevel(vehicle);

            if (fuelLevel > max)
            {
                fuelLevel = max;
            }

            vehicle.FuelLevel = fuelLevel;
            vehicle.SetDecor(FUEL_LEVEL_DECOR, fuelLevel);
        }

        /// <summary>
        /// Inits fuel for given vehicle
        /// </summary>
        /// <param name="vehicle"></param>
        public void InitFuel(Vehicle vehicle)
        {
            _currentVehicleFuelLevelInitialized = true;

            FUEL_TANK_CAPACITY = VehicleMaxFuelLevel(vehicle);

            if (!vehicle.HasDecor(FUEL_LEVEL_DECOR))
            {
                vehicle.SetDecor(FUEL_LEVEL_DECOR, RandomizeFuelLevel(FUEL_TANK_CAPACITY));
            }

            vehicle.FuelLevel = vehicle.GetDecor<float>(FUEL_LEVEL_DECOR);
        }

        /// <summary>
        /// Returns "adequate" vehicle's petrol tank position
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public Vector3 GetVehicleTankPos(Vehicle vehicle)
        {
            EntityBone bone = null;

            foreach (string boneName in TANK_BONES)
            {
                int boneIndex = GetEntityBoneIndexByName(vehicle.Handle, boneName);

                bone = vehicle.Bones[boneIndex];

                if (bone.IsValid)
                {
                    break;
                }
            }

            if (bone == null)
            {
                return vehicle.Position;
            }

            return bone.Position;
        }

        /// <summary>
        /// Disable controls related to E for refueling
        /// </summary>
        internal void DisableRefuelControls()
        {
            Game.DisableControlThisFrame(0, NOZZLE_INTERACT);
            Game.DisableControlThisFrame(0, Control.Context);
            Game.DisableControlThisFrame(0, Control.VehicleRoof);
            Game.DisableControlThisFrame(0, Control.VehicleHorn);
            Game.DisableControlThisFrame(0, Control.ReplayHidehud);
            Game.DisableControlThisFrame(0, Control.VehicleShuffle);
        }

        /// <summary>
        /// Controls engine
        /// </summary>
        /// <param name="vehicle"></param>
        public void ControlEngine(Vehicle vehicle)
        {
            // all controls related to G
            Game.DisableControlThisFrame(0, ENGINE_TOGGLE);
            Game.DisableControlThisFrame(0, Control.Detonate);
            Game.DisableControlThisFrame(0, Control.PhoneCameraGrid);
            Game.DisableControlThisFrame(0, Control.VehicleFlyUnderCarriage);

            if (!Game.IsDisabledControlJustPressed(0, ENGINE_TOGGLE) || Game.IsControlPressed(0, Control.Context))
            {
                return;
            }

            if (vehicle.IsEngineRunning)
            {
                vehicle.IsDriveable = false;

                SetVehicleEngineOn(vehicle.Handle, false, true, true);

                return;
            }

            vehicle.IsDriveable = true;
            vehicle.IsEngineRunning = true;
        }

        /// <summary>
        /// Renders fuel bar and marker
        /// </summary>
        /// <param name="playerPed"></param>
        public void RenderUI(Ped playerPed)
        {
            int gasStationIndex = GetGasStationIndexInRange(playerPed.Position, SHOW_MARKER_RANGE);

            if (gasStationIndex != -1 && gasStationIndex != _currentGasStationIndex)
            {
                _currentGasStationIndex = gasStationIndex;
            }

            if (gasStationIndex == -1 && _currentGasStationIndex != -1)
            {
                _currentGasStationIndex = -1;
            }

            if (!_showHud || !IsHudPreferenceSwitchedOn())
            {
                return;
            }

            Vehicle currentVeh = playerPed.CurrentVehicle ?? playerPed.LastVehicle;

            bool nearPump = _currentGasStationIndex != -1;
            bool engineRunning = currentVeh.IsEngineRunning;

            if (!nearPump && !engineRunning && !_showHudWhenEngineOff)
            {
                return;
            }

            if (playerPed.Position.DistanceToSquared(currentVeh.Position) < 5f)
            {
                _hud.RenderBar(currentVeh.FuelLevel, FUEL_TANK_CAPACITY);
            }
        }

        /// <summary>
        /// Handles manual vehicle refueling using Jerry Can
        /// </summary>
        /// <param name="playerPed"></param>
        public void ManualRefuel(Ped playerPed)
        {
            if (_isNearGasPump || _nozzleInHand)
            {
                return;
            }

            if (playerPed.Weapons.Current.Hash != WeaponHash.PetrolCan)
            {
                return;
            }

            Vector3 pos = playerPed.Position;

            if (!IsAnyVehicleNearPoint(pos.X, pos.Y, pos.Z, 3f))
            {
                return;
            }

            Vehicle vehicle = World.GetAllVehicles().OrderBy(v => v.Position.DistanceToSquared(pos)).First();

            if (
                vehicle != null &&
                vehicle.Exists() &&
                vehicle.HasDecor(FUEL_LEVEL_DECOR)
            )
            {
                float max = VehicleMaxFuelLevel(vehicle);
                float current = VehicleFuelLevel(vehicle);

                if (max - current < 0.5f)
                {
                    if (_jerryCanAnimation.state != State.Ended)
                    {
                        _jerryCanAnimation.RewindAndStop(playerPed);
                    }

                    Screen.DisplayHelpTextThisFrame("Fuel tank is full");

                    return;
                }

                Screen.DisplayHelpTextThisFrame("Manual refueling");

                Game.DisableControlThisFrame(0, Control.Attack);

                if (!Game.IsDisabledControlPressed(0, Control.Attack))
                {
                    _jerryCanAnimation.StopAnim(playerPed);

                    return;
                }

                _jerryCanAnimation.Magick(playerPed);

                if (current < max)
                {
                    if (current + 0.1f >= max)
                    {
                        VehicleSetFuelLevel(vehicle, max);
                    }
                    else
                    {
                        VehicleSetFuelLevel(vehicle, current + 0.2f);
                    }
                }

                if (Game.IsDisabledControlJustReleased(0, Control.VehicleAttack))
                {
                    _jerryCanAnimation.RewindAndStop(playerPed);
                }

                return;
            }
        }

        internal float CalculateFuel(Vehicle vehicle)
        {
            float fuel = VehicleFuelLevel(vehicle);

            // Consuming
            if (fuel > 0 && vehicle.IsEngineRunning)
            {
                float normalizedRPMValue = (float)Math.Pow(vehicle.CurrentRPM, 1.5);
                float consumedFuel = 0f;

                consumedFuel += normalizedRPMValue * FUEL_RPM_IMPACT;
                consumedFuel += vehicle.Acceleration * FUEL_ACCELERATION_IMPACT;
                consumedFuel += vehicle.MaxTraction * FUEL_TRACTION_IMPACT;

                fuel -= consumedFuel * FUEL_CONSUMPTION_RATE;
                fuel = fuel < 0f ? 0f : fuel;
            }

            return fuel;
        }

        internal async void ExternalRefuel(Ped plyrPed, Vehicle veh)
        {
            Vector3 pedPos = plyrPed.Position;

            Prop nearestPump = GetClosestGasPump();

            if (nearestPump == null)
            {
                _isNearGasPump = false;

                return;
            }

            if (nearestPump.Position.DistanceToSquared(pedPos) > 6f)
            {
                _isNearGasPump = false;

                return;
            }

            _isNearGasPump = true;

            if (veh == null || _currentGasStationIndex == -1 || !IsVehicleNearAnyPump(veh))
            {
                Screen.DisplayHelpTextThisFrame("Get closer to the pump");

                return;
            }

            if (!_refuelAllowed)
            {
                return;
            }

            VehicleSetFuelLevel(veh, CalculateFuel(veh));

            DisableRefuelControls();

            if (Game.IsDisabledControlJustPressed(0, NOZZLE_INTERACT))
            {
                plyrPed.Task.PlayAnimation(NOZZLE_ANIM_DICT, NOZZLE_ANIM_NAME, 30f, -1, AnimationFlags.AllowRotation | AnimationFlags.UpperBodyOnly | AnimationFlags.StayInEndFrame);
                await Delay(800);
                plyrPed.Task.ClearAnimation(NOZZLE_ANIM_DICT, NOZZLE_ANIM_NAME);

                _nozzleInHand = !_nozzleInHand;

                if (_nozzleInHand)
                {
                    TriggerServerEvent("frfuel:server:createAccessories", nearestPump.Position);
                }
                else
                {
                    TriggerServerEvent("frfuel:server:removeAccessories");
                }
            }

            float max = VehicleMaxFuelLevel(veh);
            float current = VehicleFuelLevel(veh);

            if (!_nozzleInHand)
            {
                Screen.DisplayHelpTextThisFrame($"~INPUT_VEH_HEADLIGHT~ Take nozzle");
                
                return;
            }

            if (Game.IsDisabledControlJustReleased(0, Control.Context) && ADDED_FUEL_CAPACITOR > 0f)
            {
                TriggerEvent("frfuel:fuelAdded", ADDED_FUEL_CAPACITOR);
                TriggerServerEvent("frfuel:fuelAdded", ADDED_FUEL_CAPACITOR);
#if DEBUG
                Screen.ShowNotification($"~g~Refueled {current:0.0}/{max:0.0}L");
#endif
                ADDED_FUEL_CAPACITOR = 0f;
            }

            if (current >= max)
            {
                Screen.DisplayHelpTextThisFrame($"Fuel tank is full{(_nozzleInHand ? "\n~INPUT_VEH_HEADLIGHT~ Return nozzle at pump" : "")}");

                plyrPed.Task.ClearAnimation(REFUEL_ANIM_DICT, REFUEL_ANIM_NAME);

                return;
            }

            Screen.DisplayHelpTextThisFrame("~INPUT_CONTEXT~ Refuel\n~INPUT_VEH_HEADLIGHT~ Return nozzle at pump");

            if (!Game.IsDisabledControlPressed(0, Control.Context))
            {
                plyrPed.Task.ClearAnimation(REFUEL_ANIM_DICT, REFUEL_ANIM_NAME);

                return;
            }

            if (!IsEntityPlayingAnim(plyrPed.Handle, REFUEL_ANIM_DICT, REFUEL_ANIM_NAME, 3))
            {
                plyrPed.Task.PlayAnimation(REFUEL_ANIM_DICT, REFUEL_ANIM_NAME, 8f, -1, AnimationFlags.AllowRotation | AnimationFlags.UpperBodyOnly | AnimationFlags.Loop);
            }

            float fuelPortion = 0.1f * REFUEL_RATE;

            VehicleSetFuelLevel(veh, current + fuelPortion);

            ADDED_FUEL_CAPACITOR += fuelPortion;
        }

        /// <summary>
        /// Processes fuel consumption
        /// </summary>
        /// <param name="vehicle"></param>
        public void ConsumeFuel(Vehicle vehicle)
        {
            float fuel = CalculateFuel(vehicle);

            // Refueling at gas station
            if (_currentGasStationIndex == -1 || !IsVehicleNearAnyPump(vehicle))
            {
                VehicleSetFuelLevel(vehicle, fuel);

                return;
            }

            if (vehicle.Speed < 0.1f)
            {
                ControlEngine(vehicle);
            }

            if (vehicle.IsEngineRunning)
            {
                Screen.DisplayHelpTextThisFrame("~INPUT_THROW_GRENADE~ Turn off engine");

                return;
            }

            Screen.DisplayHelpTextThisFrame("~INPUT_CONTEXT~ Refuel\n~INPUT_THROW_GRENADE~ Turn on engine");

            if (!_refuelAllowed)
            {
                return;
            }

            DisableRefuelControls();

            if (Game.IsDisabledControlJustReleased(0, Control.Context) && ADDED_FUEL_CAPACITOR > 0f)
            {
                TriggerEvent("frfuel:fuelAdded", ADDED_FUEL_CAPACITOR);
                TriggerServerEvent("frfuel:fuelAdded", ADDED_FUEL_CAPACITOR);
#if DEBUG
                Screen.ShowNotification($"~g~Refueled {fuel:0.0}/{FUEL_TANK_CAPACITY:0.0}L");
#endif
                ADDED_FUEL_CAPACITOR = 0f;
            }

            if (!Game.IsDisabledControlPressed(0, Control.Context))
            {
                return;
            }

            if (fuel < FUEL_TANK_CAPACITY)
            {
                float fuelPortion = 0.1f * REFUEL_RATE;

                fuel += fuelPortion;
                ADDED_FUEL_CAPACITOR += fuelPortion;
            }

            VehicleSetFuelLevel(vehicle, fuel); 
        }

        #endregion
    }
}
